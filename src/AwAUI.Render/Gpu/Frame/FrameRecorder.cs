using System;
using System.Collections.Generic;
using System.Numerics;
using AwAUI.Graphics;
using AwAUI.Graphics.Commands;
using AwAUI.Render.Gpu.Atlas;
using AwAUI.Render.Raster;
using AwAUI.Render.Rhi;

namespace AwAUI.Render.Gpu.Frame;

/// <summary>
/// GPU 后端的 DisplayList 解释器（design/03 管线的「解释 → 资源准备 → 批处理」三步）：把命令变成 <see cref="GpuQuadInstance"/>，
/// 准备好图集与查找表，按（纹理、混合）合批而不改变绘制顺序。不接触 GPU：输出 <see cref="FrameGraph"/> 交给 <see cref="FrameEncoder"/>。
/// <para>
/// 语义以软件后端（<see cref="RasterContext"/>）为准，逐条对照：
/// 轴对齐矩形、圆角矩形、矩形描边与无虚线的圆角描边走解析覆盖率（着色器）；任意路径、描边与旋转/错切下的形状在 CPU 上用软件后端的同一份
/// 光栅器代码（Raster/：展平、描边、光栅化、阴影、字形、图层隔离判定、渐变查找表）生成 A8 掩码；
/// 字形掩码与软件后端逐字节相同、放进图集；裁剪矩形对齐像素时只收缩范围，否则生成浮点蒙版（与软件后端相同的算法）后上传；
/// 图层渲染到离屏纹理再合成；非 SrcOver 的混合模式先拷贝目标区域，在着色器里混合。
/// </para>
/// </summary>
internal sealed partial class FrameRecorder
{
    /// <summary>嵌套 DisplayList 的最大深度（与软件后端相同）；更深的部分忽略并上报降级（防止恶意构造的深嵌套耗尽栈）。</summary>
    public const int MaxNestingDepth = RasterContext.MaxNestingDepth;

    /// <summary>一帧最多重放的命令数（含嵌套展开），与软件后端相同，见 <see cref="RasterContext.MaxCommandsPerFrame"/>。</summary>
    public const long MaxCommandsPerFrame = RasterContext.MaxCommandsPerFrame;

    /// <summary>
    /// 离屏图层的最大嵌套深度（与软件后端相同）。更深的图层不再分配离屏纹理，内容直接画进父目标（组透明度与混合模式被忽略，并上报降级），
    /// 防止恶意构造的深嵌套占用无界的显存。
    /// </summary>
    public const int MaxLayerDepth = RasterContext.MaxLayerDepth;

    /// <summary>
    /// 一帧在 CPU 上光栅化掩码（路径、描边、裁剪、字形、阴影回退）的工作量上限，与软件后端相同（<see cref="RasterContext.MaxWorkPerFrame"/>）。
    /// 与下面两个上限一起挡住「少量命令、每条都很贵」的输入（嵌套 DisplayList 的扇出）；超出任一上限时停止本帧其余的绘制并上报降级。
    /// </summary>
    public const long MaxWorkPerFrame = RasterContext.MaxWorkPerFrame;

    /// <summary>一帧所有实例覆盖的像素总数上限（GPU 的填充量；2³⁴ 约为 4K 全屏重画 2000 次）。</summary>
    public const long MaxInstancePixelsPerFrame = 1L << 34;

    /// <summary>一帧的实例数上限（实例缓冲约 180 MB）。正常界面帧在 10⁴ 量级。</summary>
    public const int MaxInstancesPerFrame = 1 << 20;

    /// <summary>一个读目标的组最多容纳的实例数（组内两两不重叠的检查是 O(n²)）。</summary>
    const int MaxDstGroup = 64;

    readonly MaskAtlas _atlas;
    readonly GradientLuts _luts;
    readonly GlyphCache _glyphCache;
    readonly PathMaskCache _pathCache;
    readonly Action<string>? _degraded;
    readonly Action<string>? _degradedSink;
    readonly HashSet<string> _reportedThisFrame = new();

    readonly Rasterizer _rasterizer = new();
    readonly PolyList _poly = new();
    readonly PolyList _scratch = new();
    readonly ByteMaskSink _byteSink = new();
    readonly GlyphRasterizer _glyphRasterizer;
    readonly ShadowRasterizer _shadows;
    readonly Stack<State> _stack = new();
    readonly Stack<LayerFrame> _layers = new();
    readonly List<PixelRect> _dstRects = new();
    readonly List<FilterImage> _filterImages = new();
    readonly List<FilterPass> _filterPasses = new();

    FrameGraph _graph = null!;
    FrameTarget _target = null!;
    /// <summary>整个目标（阴影 CPU 回退的光栅窗口，与软件后端的 RasterContext._window 相同）。</summary>
    PixelRect _window;
    State _state;
    long _frame;
    int _depth;
    int _layerDepth;
    long _commandBudget;
    long _workBudget;
    long _instancePixels;
    bool _budgetReported;
    FrameOp? _dstOp;

    public FrameRecorder(MaskAtlas atlas, GradientLuts luts, GlyphCache glyphCache, PathMaskCache pathCache, Action<string>? degraded)
    {
        _atlas = atlas;
        _luts = luts;
        _glyphCache = glyphCache;
        _pathCache = pathCache;
        _degradedSink = degraded;
        // 同一帧内相同的降级信息只上报一次（与软件后端相同）
        _degraded = degraded is null ? null : ReportOnce;
        _glyphRasterizer = new GlyphRasterizer(_rasterizer);
        _shadows = new ShadowRasterizer(_rasterizer);
    }

    /// <summary>本帧新光栅化的路径掩码数（诊断）。</summary>
    public int PathMasksRasterized { get; private set; }

    /// <summary>本帧新光栅化的字形数（诊断）。</summary>
    public int GlyphsRasterized { get; private set; }

    readonly struct State
    {
        public State(Matrix3x2 ctm, ClipInfo clip) { Ctm = ctm; Clip = clip; }
        public Matrix3x2 Ctm { get; }
        public ClipInfo Clip { get; }
        public State With(Matrix3x2 ctm) => new(ctm, Clip);
        public State With(ClipInfo clip) => new(Ctm, clip);
    }

    /// <summary>
    /// 裁剪：像素矩形 + 可选的浮点蒙版（与软件后端的 ClipState 相同），或一个解析的圆角矩形（design/03：「圆角矩形裁剪在着色器内用 SDF 相乘」）。
    /// 解析裁剪只用于正缩放 + 平移下、外层没有蒙版或解析形状的情形；再嵌套一层非矩形裁剪时把它光栅化为蒙版（<see cref="FrameRecorder.Materialize"/>）。
    /// 蒙版在第一次被绘制用到时上传进图集。
    /// </summary>
    sealed class ClipInfo
    {
        public ClipInfo(PixelRect bounds, float[]? mask = null, PixelRect maskRect = default)
        {
            Bounds = bounds;
            Mask = mask;
            MaskRect = maskRect;
        }

        public PixelRect Bounds { get; }
        public float[]? Mask { get; }
        public PixelRect MaskRect { get; }
        public AtlasSlot Slot;
        public long SlotFrame = -1;

        /// <summary>解析裁剪形状在 <see cref="FrameGraph.ClipShapes"/> 中的下标；−1 表示没有。</summary>
        public int ShapeIndex = -1;
        /// <summary>解析形状对应的路径与变换（需要转成蒙版时光栅化）。</summary>
        public VectorPath? ShapePath;
        public Matrix3x2 ShapeCtm;
        public bool ShapeAntiAlias;

        public bool IsEmpty => Bounds.IsEmpty;

        public ClipInfo WithBounds(PixelRect bounds) => new(Bounds.Intersect(bounds), Mask, MaskRect)
        {
            Slot = Slot, SlotFrame = SlotFrame,
            ShapeIndex = ShapeIndex, ShapePath = ShapePath, ShapeCtm = ShapeCtm, ShapeAntiAlias = ShapeAntiAlias,
        };
    }

    sealed class LayerFrame
    {
        public FrameTarget Parent = null!;
        public FrameTarget? Layer;
        public float Opacity;
        public BlendMode BlendMode;
        public bool Offscreen;

        /// <summary>滤镜图层：滤镜、录制时的变换与滤镜的范围上限（与软件后端相同，见 <see cref="FilterPlan"/>）。</summary>
        public Filter? Filter;
        public Matrix3x2 Ctm;
        public PixelRect FilterLimit;
    }

    /// <summary>
    /// 录制一帧。<paramref name="area"/> 是要绘制的设备像素区域（整个目标或受损区域），<paramref name="clear"/> 非空时
    /// 先在区域内用 Replace 写入清屏色（局部重画；整帧时由渲染通道的清屏完成）。
    /// </summary>
    public void Record(DisplayList list, FrameGraph graph, PixelRect target, PixelRect area, double scale, long frame, Color? clear)
    {
        _graph = graph;
        _frame = frame;
        _window = target;
        graph.Reset(target);
        _target = graph.Main;
        _stack.Clear();
        _layers.Clear();
        _depth = 0;
        _layerDepth = 0;
        _commandBudget = MaxCommandsPerFrame;
        _workBudget = RasterContext.WorkBudgetOverride ?? MaxWorkPerFrame;
        _rasterizer.Work = 0;
        _instancePixels = 0;
        _budgetReported = false;
        _reportedThisFrame.Clear();
        _dstOp = null;
        PathMasksRasterized = 0;
        GlyphsRasterized = 0;
        _state = new State(Matrix3x2.CreateScale((float)scale), new ClipInfo(area));

        if (clear is { } c && !area.IsEmpty)
        {
            var q = new GpuQuadInstance
            {
                Bounds = Box(area),
                Shape = Box(area),
                Color = GradientLuts.Premultiply(c),
                Kinds = new QuadKinds { Shape = QuadShape.Rect },
            };
            int index = graph.Add(q);
            _target.Ops.Add(new FrameOp { Kind = FrameOpKind.Draw, First = index, Count = 1, Blend = GpuBlendState.Replace });
        }
        if (!area.IsEmpty) Replay(list);
        while (_layers.Count > 0) PopLayer();
        _graph = null!;
        _target = null!;
    }

    static Vector4 Box(PixelRect r) => new(r.X, r.Y, r.Right, r.Bottom);
    static Vector4 Box(Rect r) => new(r.Left, r.Top, r.Right, r.Bottom);

    void Degrade(string message) => _degraded?.Invoke(message);

    void ReportOnce(string message)
    {
        if (_reportedThisFrame.Count < 1024 && _reportedThisFrame.Add(message)) _degradedSink!(message);
    }

    /// <summary>当前帧（或最近一帧）在 CPU 上光栅化的工作量（诊断与测试）。</summary>
    internal long FrameWork => _rasterizer.Work;

    /// <summary>当前帧（或最近一帧）实例覆盖的像素总数（诊断与测试）。</summary>
    internal long InstancePixels => _instancePixels;

    bool OverBudget(out string reason)
    {
        reason = _rasterizer.Work > _workBudget ? $"Frame exceeded the work limit of {_workBudget} rasterized pixel operations"
            : _instancePixels > (RasterContext.WorkBudgetOverride is { } o ? o << 4 : MaxInstancePixelsPerFrame) ? "Frame exceeded the work limit for GPU fill"
            : _graph.InstanceCount >= MaxInstancesPerFrame ? $"Frame exceeded the work limit of {MaxInstancesPerFrame} instances"
            : "";
        return reason.Length > 0;
    }

    void Replay(DisplayList list)
    {
        var reader = list.GetReader();
        LayerIsolation.Summary? isolation = null;
        int layerIndex = 0;
        while (reader.MoveNext())
        {
            if (--_commandBudget < 0)
            {
                if (!_budgetReported) Degrade($"More than {MaxCommandsPerFrame} draw commands in one frame (including nested display lists); the rest were skipped.");
                _budgetReported = true;
                return;
            }
            if (OverBudget(out var reason))
            {
                if (!_budgetReported) Degrade(reason + "; the rest of the frame was skipped.");
                _budgetReported = true;
                _commandBudget = 0;
                return;
            }
            bool aa = (reader.Flags & DrawFlags.AntiAlias) != 0;
            switch (reader.Op)
            {
                case DrawOp.Save:
                    _stack.Push(_state);
                    break;
                case DrawOp.Restore:
                    if (_stack.Count > 0) _state = _stack.Pop();
                    break;
                case DrawOp.Translate:
                {
                    var c = reader.Read<TranslateCmd>();
                    _state = _state.With(Matrix3x2.CreateTranslation(c.Dx, c.Dy) * _state.Ctm);
                    break;
                }
                case DrawOp.Transform:
                    _state = _state.With(reader.Read<TransformCmd>().Matrix * _state.Ctm);
                    break;
                case DrawOp.ClipRect:
                    ClipRect(reader.Read<ClipRectCmd>().Rect, aa);
                    break;
                case DrawOp.ClipRoundRect:
                {
                    var c = reader.Read<ClipRoundRectCmd>();
                    if (c.Shape.IsRect) ClipRect(c.Shape.Rect, aa);
                    else if (!TryClipShape(c.Shape, aa)) ClipPath(VectorPath.FromRoundRect(c.Shape), aa);
                    break;
                }
                case DrawOp.ClipPath:
                    ClipPath(reader.GetResource<VectorPath>(reader.Read<ClipPathCmd>().PathRef), aa);
                    break;
                case DrawOp.FillRect:
                {
                    var c = reader.Read<FillRectCmd>();
                    FillRect(c.Rect, c.Paint, reader.GetBrush(c.Paint), aa);
                    break;
                }
                case DrawOp.FillRoundRect:
                {
                    var c = reader.Read<FillRoundRectCmd>();
                    FillRoundRect(c.Shape, c.Paint, reader.GetBrush(c.Paint), aa);
                    break;
                }
                case DrawOp.StrokeRect:
                {
                    var c = reader.Read<StrokeRectCmd>();
                    StrokeRect(c.Rect, c.Paint, reader.GetBrush(c.Paint), reader.GetResource<Stroke>(c.StrokeRef), aa);
                    break;
                }
                case DrawOp.StrokeRoundRect:
                {
                    var c = reader.Read<StrokeRoundRectCmd>();
                    StrokeRoundRect(c.Shape, c.Paint, reader.GetBrush(c.Paint), reader.GetResource<Stroke>(c.StrokeRef), aa);
                    break;
                }
                case DrawOp.DrawLine:
                {
                    var c = reader.Read<DrawLineCmd>();
                    var path = new PathBuilder().MoveTo(c.P0).LineTo(c.P1).ToPath();
                    StrokePath(path, c.Paint, reader.GetBrush(c.Paint), reader.GetResource<Stroke>(c.StrokeRef), aa);
                    break;
                }
                case DrawOp.FillPath:
                {
                    var c = reader.Read<FillPathCmd>();
                    FillPath(reader.GetResource<VectorPath>(c.PathRef), c.Paint, reader.GetBrush(c.Paint), aa);
                    break;
                }
                case DrawOp.StrokePath:
                {
                    var c = reader.Read<StrokePathCmd>();
                    StrokePath(reader.GetResource<VectorPath>(c.PathRef), c.Paint, reader.GetBrush(c.Paint), reader.GetResource<Stroke>(c.StrokeRef), aa);
                    break;
                }
                case DrawOp.DrawGlyphRun:
                {
                    var c = reader.Read<DrawGlyphRunCmd>();
                    DrawGlyphRun(reader.GetGlyphRun(c), c.Origin, c.Paint, reader.GetBrush(c.Paint), aa);
                    break;
                }
                case DrawOp.DrawBoxShadow:
                    BoxShadow(reader.Read<BoxShadowCmd>());
                    break;
                case DrawOp.DrawImage:
                {
                    var c = reader.Read<DrawImageCmd>();
                    DrawImage(c, reader.GetImage(c), aa);
                    break;
                }
                case DrawOp.PushLayer:
                {
                    var c = reader.Read<PushLayerCmd>();
                    int index = layerIndex++;
                    var filter = c.HasFilter != 0 ? reader.GetResource<Filter>(c.FilterRef) : null;
                    // 与软件后端相同：不透明度为 1 的 SrcOver 组只在组内有读目标的绘制时才需要离屏（见 LayerIsolation）；滤镜图层总是离屏
                    bool isolate = filter is not null || c.Opacity < 1f || c.BlendMode != BlendMode.SrcOver
                        || (isolation ??= LayerIsolation.Analyze(list)).Needs(index);
                    PushLayer(c, isolate, filter);
                    break;
                }
                case DrawOp.PopLayer:
                    PopLayer();
                    break;
                case DrawOp.DrawBackdrop:
                {
                    var c = reader.Read<DrawBackdropCmd>();
                    DrawBackdrop(c, reader.GetResource<Filter>(c.FilterRef), aa);
                    break;
                }
                case DrawOp.DrawDisplayList:
                {
                    var child = reader.GetResource<DisplayList>(reader.Read<DrawDisplayListCmd>().DisplayListRef);
                    if (_depth >= MaxNestingDepth)
                    {
                        Degrade($"DisplayList nesting deeper than {MaxNestingDepth} ignored.");
                        break;
                    }
                    var saved = _state;
                    int depth = _stack.Count;
                    int layers = _layers.Count;
                    _depth++;
                    Replay(child);
                    _depth--;
                    // 子列表里未配对的图层与 Save 在这里关闭（录制器保证配对，这里只防御损坏的输入）
                    while (_layers.Count > layers) PopLayer();
                    while (_stack.Count > depth) _stack.Pop();
                    _state = saved;
                    break;
                }
                case DrawOp.Annotation:
                    break;
                default:
                    Degrade($"Unsupported draw op {reader.Op} ignored.");
                    break;
            }
        }
    }

    // ---------------------------------------------------------------- 裁剪（与软件后端相同）

    void ClipRect(Rect rect, bool aa)
    {
        if (_state.Ctm.IsScaleTranslate())
        {
            var dev = rect.TransformBounds(_state.Ctm);
            if (!aa || IsPixelAligned(dev))
            {
                var px = PixelRect.FromLTRB(Round(dev.Left), Round(dev.Top), Round(dev.Right), Round(dev.Bottom));
                _state = _state.With(_state.Clip.WithBounds(px));
                return;
            }
        }
        if (!TryClipShape(new RoundRect(rect, 0), aa)) ClipPath(VectorPath.FromRect(rect), aa);
    }

    /// <summary>解析裁剪（圆角）矩形：正缩放 + 平移、外层只有矩形范围时可用。</summary>
    bool TryClipShape(in RoundRect shape, bool aa)
    {
        var clip = _state.Clip;
        var ctm = _state.Ctm;
        if (clip.Mask is not null || clip.ShapeIndex >= 0 || !IsPositiveScaleTranslate(ctm)) return false;
        if (clip.IsEmpty) return true;
        var dev = shape.Rect.TransformBounds(ctm);
        var bounds = PixelRect.RoundOut(dev).Intersect(clip.Bounds);
        if (bounds.IsEmpty)
        {
            _state = _state.With(new ClipInfo(default));
            return true;
        }
        var q = new GpuQuadInstance();
        SetRadii(ref q, shape, ctm.M11, ctm.M22);
        int index = _graph.AddClipShape(new GpuClipShape
        {
            Rect = Box(dev), RadiiX = q.RadiiX, RadiiY = q.RadiiY, Flags = new Vector4(aa ? 0 : 1, 0, 0, 0),
        });
        _state = _state.With(new ClipInfo(bounds)
        {
            ShapeIndex = index,
            ShapePath = shape.IsRect ? VectorPath.FromRect(shape.Rect) : VectorPath.FromRoundRect(shape),
            ShapeCtm = ctm,
            ShapeAntiAlias = aa,
        });
        return true;
    }

    /// <summary>把解析裁剪转换为等价的浮点蒙版（在它之内再嵌套非矩形裁剪时需要）。</summary>
    ClipInfo Materialize(ClipInfo clip)
    {
        if (clip.ShapeIndex < 0 || clip.IsEmpty) return clip;
        var rect = clip.Bounds;
        _scratch.Clear();
        Flattener.FlattenToPolygons(clip.ShapePath!, clip.ShapeCtm, Flattener.DefaultTolerance, _scratch, CullRect.FromPixels(rect, 1));
        var mask = new float[rect.Width * rect.Height];
        _rasterizer.Fill(_scratch, FillRule.NonZero, clip.ShapeAntiAlias, rect, new MaskSink(mask, rect, null));
        return new ClipInfo(rect, mask, rect);
    }

    void ClipPath(VectorPath path, bool aa)
    {
        var clip = Materialize(_state.Clip);
        if (clip.IsEmpty) return;
        _poly.Clear();
        Flattener.FlattenToPolygons(path, _state.Ctm, Flattener.DefaultTolerance, _poly, CullRect.FromPixels(clip.Bounds, 1));
        if (!_poly.TryGetBounds(out var l, out var t, out var r, out var b))
        {
            _state = _state.With(new ClipInfo(default));
            return;
        }
        var rect = PixelRect.RoundOut(Rect.FromLTRB(l, t, r, b)).Intersect(clip.Bounds);
        if (rect.IsEmpty)
        {
            _state = _state.With(new ClipInfo(default));
            return;
        }
        var mask = new float[rect.Width * rect.Height];
        var outer = clip.Mask is null ? null : new ClipState(clip.Bounds, clip.Mask, clip.MaskRect);
        _rasterizer.Fill(_poly, path.FillRule, aa, rect, new MaskSink(mask, rect, outer));
        _state = _state.With(new ClipInfo(rect, mask, rect));
    }

    static bool IsPixelAligned(Rect r) => IsInt(r.Left) && IsInt(r.Top) && IsInt(r.Right) && IsInt(r.Bottom);
    static bool IsInt(float v) => MathF.Abs(v - MathF.Round(v)) < 1e-3f;
    static int Round(float v) => (int)MathF.Round(v);

    /// <summary>图集分配；超出本帧预算时上报降级并返回 false（调用方跳过该绘制）。</summary>
    bool Allocate(int width, int height, out AtlasSlot slot)
    {
        if (_atlas.TryAllocate(width, height, out slot)) return true;
        Degrade($"Mask atlas budget exceeded; a {width}x{height} mask was dropped.");
        return false;
    }

    /// <summary>裁剪蒙版在图集中的位置（本帧第一次用到时量化为 8 位并上传）。</summary>
    bool ClipSlot(ClipInfo clip, out AtlasSlot slot)
    {
        if (clip.SlotFrame == _frame && clip.Slot.IsValid)
        {
            clip.Slot.Touch(_frame);
            slot = clip.Slot;
            return true;
        }
        var r = clip.MaskRect;
        if (!Allocate(r.Width, r.Height, out slot)) return false;
        var data = slot.Page.Shadow;
        var mask = clip.Mask!;
        int stride = slot.Page.Width;
        for (int y = 0; y < r.Height; y++)
        {
            int o = slot.Offset + y * stride, m = y * r.Width;
            for (int x = 0; x < r.Width; x++) data[o + x] = ScalarKernels.ToByte(mask[m + x]);
        }
        slot.Commit();
        clip.Slot = slot;
        clip.SlotFrame = _frame;
        return true;
    }

    // ---------------------------------------------------------------- 画刷与混合

    static bool IsSupported(BlendMode mode) => mode is not (BlendMode.Hue or BlendMode.Saturation or BlendMode.Color or BlendMode.Luminosity);

    BlendMode ResolveBlend(BlendMode mode)
    {
        if (IsSupported(mode)) return mode;
        Degrade($"Blend mode {mode} is not supported by the GPU renderer; using SrcOver.");
        return BlendMode.SrcOver;
    }

    /// <summary>
    /// 填写实例的画刷部分（与软件后端的 Shader.Create 相同的规则）。返回 false 表示不可见（透明纯色 + SrcOver），调用方跳过绘制。
    /// </summary>
    bool SetPaint(ref GpuQuadInstance q, in PaintData paint, object? brush, out object? lut, out BlendMode mode)
    {
        mode = ResolveBlend(paint.BlendMode);
        lut = null;
        Vector4 solid;
        switch (brush)
        {
            case GradientBrush g:
                if (!Matrix3x2.Invert(g.Transform * _state.Ctm, out var inv))
                {
                    solid = GradientLuts.Premultiply(g.Stops[g.Stops.Count - 1].Color);
                    break;
                }
                if (!_luts.TryGet(g, out var page, out int row))
                {
                    Degrade($"More than {GradientLuts.MaxPages * GradientLuts.RowsPerPage} distinct gradients in one frame; drawing the rest with their average colour.");
                    solid = GradientLuts.Sample(g.Stops, 0.5f);
                    break;
                }
                SetGradient(ref q, g, inv, row);
                lut = page;
                return true;
            case SolidColorBrush s:
                solid = GradientLuts.Premultiply(s.Color);
                break;
            default:
                solid = GradientLuts.Premultiply(paint.Color);
                break;
        }
        if (solid.W <= 0 && mode == BlendMode.SrcOver) return false;
        q.Color = solid;
        q.Kinds.Paint = QuadPaint.Solid;
        return true;
    }

    /// <summary>把「设备像素 → 渐变空间」的逆变换与渐变参数合并成着色器的两行仿射式。</summary>
    static void SetGradient(ref GpuQuadInstance q, GradientBrush g, in Matrix3x2 inv, int row)
    {
        uint spread = (uint)g.Spread << QuadPaint.SpreadShift;
        switch (g)
        {
            case LinearGradientBrush lin:
            {
                float ax = lin.End.X - lin.Start.X, ay = lin.End.Y - lin.Start.Y;
                float lenSq = ax * ax + ay * ay;
                float k = lenSq > 1e-12f ? 1 / lenSq : 0;
                q.Paint0 = new Vector4(
                    (inv.M11 * ax + inv.M12 * ay) * k,
                    (inv.M21 * ax + inv.M22 * ay) * k,
                    ((inv.M31 - lin.Start.X) * ax + (inv.M32 - lin.Start.Y) * ay) * k,
                    row);
                q.Kinds.Paint = QuadPaint.Linear | spread;
                break;
            }
            case RadialGradientBrush { HasFocus: true } focal:
            {
                // 双点锥形：以焦点为原点、按 norm 归一化的坐标（与软件后端 GradientShader 相同的参数），着色器解二次方程
                GradientShader.ConicalParameters(focal, out double fx, out double fy, out double norm,
                    out double cdx, out double cdy, out double r0, out double dr, out double a);
                float k = (float)(1 / norm);
                q.Paint0 = new Vector4(inv.M11 * k, inv.M21 * k, (float)((inv.M31 - fx) / norm), row);
                q.Paint1 = new Vector4(inv.M12 * k, inv.M22 * k, (float)((inv.M32 - fy) / norm), (float)a);
                q.Extra = new Vector4((float)cdx, (float)cdy, (float)r0, (float)dr);
                q.Kinds.Paint = QuadPaint.TwoPointConical | spread;
                break;
            }
            case RadialGradientBrush rad:
            {
                float k = rad.Radius > 1e-6f ? 1 / rad.Radius : 0;
                if (k == 0)
                {
                    // 软件后端：半径为 0 时 t 恒为 1（再按扩展模式处理）
                    q.Paint0 = new Vector4(0, 0, 1, row);
                    q.Paint1 = default;
                }
                else
                {
                    q.Paint0 = new Vector4(inv.M11 * k, inv.M21 * k, (inv.M31 - rad.Center.X) * k, row);
                    q.Paint1 = new Vector4(inv.M12 * k, inv.M22 * k, (inv.M32 - rad.Center.Y) * k, 0);
                }
                q.Kinds.Paint = QuadPaint.Radial | spread;
                break;
            }
            case ConicGradientBrush con:
                q.Paint0 = new Vector4(inv.M11, inv.M21, inv.M31 - con.Center.X, row);
                q.Paint1 = new Vector4(inv.M12, inv.M22, inv.M32 - con.Center.Y, con.StartAngle);
                q.Kinds.Paint = QuadPaint.Conic;
                break;
            default:
                q.Paint0 = new Vector4(0, 0, 1, row);
                q.Kinds.Paint = QuadPaint.Linear;
                break;
        }
    }

    // ---------------------------------------------------------------- 合批

    /// <summary>
    /// 追加一个实例。与上一批的纹理（不用的槽位不算）、混合相同且紧接其后时并入该批；读目标的混合模式按「组内两两不重叠」分组，
    /// 每组前插入一次目标拷贝（Replace 写回：组内每个像素只被一个实例写，覆盖率为 0 处写回拷贝的原值）。
    /// </summary>
    void Emit(ref GpuQuadInstance q, object? coverage, object? lut, BlendMode mode, ClipInfo clip)
    {
        object? clipKey = null;
        if (clip.Mask is not null)
        {
            if (!ClipSlot(clip, out var slot)) return;
            q.Clip = new Vector4(slot.X - clip.MaskRect.X, slot.Y - clip.MaskRect.Y, 1, 0);
            clipKey = slot.Page;
        }
        if (clip.ShapeIndex >= 0) q.Clip.W = clip.ShapeIndex + 1;
        bool readsDst = mode != BlendMode.SrcOver;
        q.Kinds.Blend = readsDst ? (uint)mode + 1 : 0;
        var ops = _target.Ops;
        int index = _graph.Add(q);
        var last = ops.Count > 0 ? ops[ops.Count - 1] : null;
        var bounds = PixelRect.FromLTRB((int)q.Bounds.X, (int)q.Bounds.Y, (int)q.Bounds.Z, (int)q.Bounds.W);
        _instancePixels += (long)bounds.Width * bounds.Height;

        if (!readsDst)
        {
            if (last is { Kind: FrameOpKind.Draw, CopyOp: null, Blend: GpuBlendState.PremultipliedOver }
                && last.First + last.Count == index
                && Compatible(last.Coverage, coverage) && Compatible(last.Lut, lut) && Compatible(last.Clip, clipKey))
            {
                last.Count++;
                last.Coverage ??= coverage;
                last.Lut ??= lut;
                last.Clip ??= clipKey;
                return;
            }
            ops.Add(new FrameOp
            {
                Kind = FrameOpKind.Draw, First = index, Count = 1, Coverage = coverage, Lut = lut, Clip = clipKey,
                Blend = GpuBlendState.PremultipliedOver,
            });
            return;
        }

        if (last is not null && last == _dstOp && last.First + last.Count == index
            && Compatible(last.Coverage, coverage) && Compatible(last.Lut, lut) && Compatible(last.Clip, clipKey)
            && _dstRects.Count < MaxDstGroup && !OverlapsGroup(bounds))
        {
            last.Count++;
            last.Coverage ??= coverage;
            last.Lut ??= lut;
            last.Clip ??= clipKey;
            last.CopyOp!.CopyRect = last.CopyOp.CopyRect.Union(bounds);
            _dstRects.Add(bounds);
            return;
        }
        var copy = new FrameOp { Kind = FrameOpKind.DstCopy, CopyRect = bounds };
        ops.Add(copy);
        _dstOp = new FrameOp
        {
            Kind = FrameOpKind.Draw, First = index, Count = 1, Coverage = coverage, Lut = lut, Clip = clipKey,
            Blend = GpuBlendState.Replace, CopyOp = copy,
        };
        ops.Add(_dstOp);
        _dstRects.Clear();
        _dstRects.Add(bounds);
    }

    /// <summary>不读某个槽位的实例（键为 null）与任何绑定兼容：合批后该槽位绑定另一方的纹理。</summary>
    static bool Compatible(object? a, object? b) => a is null || b is null || ReferenceEquals(a, b);

    bool OverlapsGroup(PixelRect r)
    {
        foreach (var g in _dstRects)
            if (!g.Intersect(r).IsEmpty) return true;
        return false;
    }

    /// <summary>A8 掩码实例：覆盖率取自图集 <paramref name="slot"/>，掩码左上角对应设备像素 (<paramref name="left"/>, <paramref name="top"/>)。</summary>
    void EmitMask(GpuQuadInstance q, AtlasSlot slot, int left, int top, PixelRect bounds, object? lut, BlendMode mode)
    {
        slot.Touch(_frame);
        q.Bounds = Box(bounds);
        q.Kinds.Shape = QuadShape.Mask;
        q.Texture = new Vector4(slot.X - left, slot.Y - top, 0, 0);
        Emit(ref q, slot.Page, lut, mode, _state.Clip);
    }

    // ---------------------------------------------------------------- 填充与描边

    void FillRect(Rect rect, in PaintData paint, object? brush, bool aa)
    {
        var ctm = _state.Ctm;
        if (!ctm.IsScaleTranslate())
        {
            FillPath(VectorPath.FromRect(rect), paint, brush, aa);
            return;
        }
        var clip = _state.Clip;
        if (clip.IsEmpty) return;
        var dev = rect.TransformBounds(ctm);
        if (!aa) dev = Rect.FromLTRB(MathF.Round(dev.Left), MathF.Round(dev.Top), MathF.Round(dev.Right), MathF.Round(dev.Bottom));
        var area = PixelRect.RoundOut(dev).Intersect(clip.Bounds);
        if (area.IsEmpty) return;
        var q = new GpuQuadInstance();
        if (!SetPaint(ref q, paint, brush, out var lut, out var mode)) return;
        q.Bounds = Box(area);
        q.Shape = Box(dev);
        q.Kinds.Shape = QuadShape.Rect;
        Emit(ref q, null, lut, mode, clip);
    }

    /// <summary>变换是否为正缩放 + 平移（解析形状快速路径的前提：角的顺序不变）。</summary>
    static bool IsPositiveScaleTranslate(in Matrix3x2 m) => m.M12 == 0 && m.M21 == 0 && m.M11 > 0 && m.M22 > 0;

    void FillRoundRect(in RoundRect shape, in PaintData paint, object? brush, bool aa)
    {
        var ctm = _state.Ctm;
        if (!IsPositiveScaleTranslate(ctm))
        {
            FillPath(VectorPath.FromRoundRect(shape), paint, brush, aa);
            return;
        }
        var clip = _state.Clip;
        if (clip.IsEmpty) return;
        var dev = shape.Rect.TransformBounds(ctm);
        var area = PixelRect.RoundOut(dev).Intersect(clip.Bounds);
        if (area.IsEmpty) return;
        var q = new GpuQuadInstance();
        if (!SetPaint(ref q, paint, brush, out var lut, out var mode)) return;
        q.Bounds = Box(area);
        q.Shape = Box(dev);
        SetRadii(ref q, shape, ctm.M11, ctm.M22);
        q.Kinds.Shape = QuadShape.RoundRect;
        if (!aa) q.Kinds.Paint |= QuadPaint.Aliased;
        Emit(ref q, null, lut, mode, clip);
    }

    static void SetRadii(ref GpuQuadInstance q, in RoundRect s, float sx, float sy)
    {
        q.RadiiX = new Vector4(s.TopLeft.Width, s.TopRight.Width, s.BottomRight.Width, s.BottomLeft.Width) * sx;
        q.RadiiY = new Vector4(s.TopLeft.Height, s.TopRight.Height, s.BottomRight.Height, s.BottomLeft.Height) * sy;
    }

    /// <summary>
    /// 矩形描边：正缩放 + 平移、无虚线、斜接（且斜接上限允许直角）、线宽小于矩形边长时是精确的「外矩形 − 内矩形」；
    /// 其余情况与软件后端一样展开为路径。
    /// </summary>
    void StrokeRect(Rect rect, in PaintData paint, object? brush, Stroke stroke, bool aa)
    {
        var ctm = _state.Ctm;
        if (IsPositiveScaleTranslate(ctm) && stroke.Dashes is null && stroke.Join == StrokeJoin.Miter && stroke.MiterLimit >= 1.5f
            && stroke.Width > 0 && stroke.Width < rect.Width && stroke.Width < rect.Height)
        {
            float hw = stroke.Width * 0.5f;
            EmitRing(rect.Inflate(hw), rect.Inflate(-hw), default, default, paint, brush, aa);
            return;
        }
        StrokePath(VectorPath.FromRect(rect), paint, brush, stroke, aa);
    }

    /// <summary>
    /// 圆角矩形描边：均匀缩放、无虚线、圆角（各角水平与垂直半径相等）、线宽小于边长时是「外圈 − 内圈」，外圈半径 r + w/2、
    /// 内圈 max(r − w/2, 0)；直角处要求斜接。其余情况展开为路径。
    /// </summary>
    void StrokeRoundRect(in RoundRect shape, in PaintData paint, object? brush, Stroke stroke, bool aa)
    {
        var ctm = _state.Ctm;
        var r = shape.Rect;
        if (IsPositiveScaleTranslate(ctm) && ctm.M11 == ctm.M22 && stroke.Dashes is null
            && stroke.Width > 0 && stroke.Width < r.Width && stroke.Width < r.Height
            && IsCircular(shape, out bool hasSharpCorner)
            && (!hasSharpCorner || stroke.Join == StrokeJoin.Miter && stroke.MiterLimit >= 1.5f))
        {
            float hw = stroke.Width * 0.5f;
            static float Grow(float v, float d) => v > 0 ? v + d : 0;
            var outerR = new Vector4(Grow(shape.TopLeft.Width, hw), Grow(shape.TopRight.Width, hw), Grow(shape.BottomRight.Width, hw), Grow(shape.BottomLeft.Width, hw));
            EmitRing(r.Inflate(hw), r.Inflate(-hw), outerR, outerR, paint, brush, aa);
            return;
        }
        StrokePath(VectorPath.FromRoundRect(shape), paint, brush, stroke, aa);
    }

    static bool IsCircular(in RoundRect s, out bool hasSharpCorner)
    {
        hasSharpCorner = s.TopLeft.Width == 0 || s.TopRight.Width == 0 || s.BottomRight.Width == 0 || s.BottomLeft.Width == 0;
        return s.TopLeft.Width == s.TopLeft.Height && s.TopRight.Width == s.TopRight.Height
            && s.BottomRight.Width == s.BottomRight.Height && s.BottomLeft.Width == s.BottomLeft.Height;
    }

    /// <summary>外圈（局部坐标，半径同样是局部坐标）减内圈；内圈半径由着色器按线宽推出。</summary>
    void EmitRing(Rect outer, Rect inner, Vector4 radiiX, Vector4 radiiY, in PaintData paint, object? brush, bool aa)
    {
        var clip = _state.Clip;
        if (clip.IsEmpty) return;
        var ctm = _state.Ctm;
        var devOuter = outer.TransformBounds(ctm);
        var devInner = inner.TransformBounds(ctm);
        var area = PixelRect.RoundOut(devOuter).Intersect(clip.Bounds);
        if (area.IsEmpty) return;
        var q = new GpuQuadInstance();
        if (!SetPaint(ref q, paint, brush, out var lut, out var mode)) return;
        q.Bounds = Box(area);
        q.Shape = Box(devOuter);
        q.Texture = Box(devInner);
        q.RadiiX = radiiX * ctm.M11;
        q.RadiiY = radiiY * ctm.M22;
        q.Kinds.Shape = QuadShape.Ring;
        if (!aa) q.Kinds.Paint |= QuadPaint.Aliased;
        Emit(ref q, null, lut, mode, clip);
    }

    void FillPath(VectorPath path, in PaintData paint, object? brush, bool aa)
    {
        var clip = _state.Clip;
        if (clip.IsEmpty || path.IsEmpty) return;
        var ctm = _state.Ctm;
        // 控制点包围盒是曲线的保守范围：掩码按它分配，命中缓存时无需展平
        var area = PixelRect.RoundOut(path.Bounds.TransformBounds(ctm)).Intersect(clip.Bounds);
        if (area.IsEmpty) return;
        var q = new GpuQuadInstance();
        if (!SetPaint(ref q, paint, brush, out var lut, out var mode)) return;

        if (PathMask(path, null, ctm, area, aa, out var slot)) EmitMask(q, slot, area.X, area.Y, area, lut, mode);
    }

    void StrokePath(VectorPath path, in PaintData paint, object? brush, Stroke stroke, bool aa)
    {
        var clip = _state.Clip;
        if (clip.IsEmpty || path.IsEmpty) return;
        var ctm = _state.Ctm;
        if (ctm.MaxScale() <= 0) return;
        var area = PixelRect.RoundOut(path.Bounds.Inflate(stroke.Outset).TransformBounds(ctm)).Intersect(clip.Bounds);
        if (area.IsEmpty) return;
        var q = new GpuQuadInstance();
        if (!SetPaint(ref q, paint, brush, out var lut, out var mode)) return;

        if (PathMask(path, stroke, ctm, area, aa, out var slot)) EmitMask(q, slot, area.X, area.Y, area, lut, mode);
    }

    /// <summary>路径（或其描边）在 <paramref name="area"/> 内的 A8 覆盖率：先查跨帧缓存，未命中时用与软件后端相同的光栅器生成。</summary>
    bool PathMask(VectorPath path, Stroke? stroke, in Matrix3x2 ctm, PixelRect area, bool aa, out AtlasSlot slot)
    {
        bool cacheable = PathMaskKey.IsCacheable(path);
        var key = cacheable ? new PathMaskKey(path, stroke, ctm, area, aa) : default;
        if (cacheable && _pathCache.TryGet(key, _frame, out slot)) return true;
        if (!Allocate(area.Width, area.Height, out slot)) return false;

        _poly.Clear();
        if (stroke is null)
        {
            Flattener.FlattenToPolygons(path, ctm, Flattener.DefaultTolerance, _poly, CullRect.FromPixels(area, 1));
        }
        else
        {
            // 与软件后端相同：在局部空间展平与描边（非均匀缩放下线宽随之变形），再变换到设备空间；范围外的曲线以弦代替（虚线除外）、
            // 虚线只在范围内生成
            float tol = Flattener.DefaultTolerance / ctm.MaxScale();
            var cull = RasterContext.LocalCull(area, ctm, stroke);
            var lines = Flattener.FlattenToPolylines(path, tol, stroke.Dashes is null ? cull : null);
            if (!Stroker.Stroke(lines, stroke, tol, _poly, stroke.Dashes is null ? null : cull))
                Degrade($"Dash pattern produces more than {Stroker.MaxDashPieces} visible dashes; stroked as a solid line.");
            _poly.Transform(ctm);
        }
        slot.Clear();
        _rasterizer.Fill(_poly, stroke is null ? path.FillRule : FillRule.NonZero, aa, area,
            _byteSink.Reset(slot.Page.Shadow, slot.Offset, slot.Page.Width, area));
        slot.Commit();
        PathMasksRasterized++;
        if (cacheable) _pathCache.Add(key, slot, _frame);
        return true;
    }

    // ---------------------------------------------------------------- 盒阴影

    /// <summary>阴影外扩量 ⌈3σ⌉ + 1，钳制到 2^24 以免整数溢出（远大于任何渲染目标）。</summary>
    static int ShadowMargin(float sigma) => (int)Math.Min(MathF.Ceiling(sigma * 3) + 1, 1 << 24);

    void BoxShadow(in BoxShadowCmd c)
    {
        var clip = _state.Clip;
        if (clip.IsEmpty) return;
        if (!ShadowRasterizer.HasFiniteParameters(c))
        {
            Degrade("Box shadow with non-finite parameters ignored.");
            return;
        }
        var ctm = _state.Ctm;
        if (!IsPositiveScaleTranslate(ctm))
        {
            BoxShadowOnCpu(c);
            return;
        }
        float sigma = ShadowRasterizer.Sigma(c, ctm, out bool clamped);
        if (clamped) Degrade(ShadowRasterizer.SigmaClampedMessage);
        int margin = ShadowMargin(sigma);
        var offsetShape = new RoundRect(c.Shape.Rect.Offset(c.OffsetX, c.OffsetY),
            c.Shape.TopLeft, c.Shape.TopRight, c.Shape.BottomRight, c.Shape.BottomLeft);
        var color = GradientLuts.Premultiply(c.Color);
        var q = new GpuQuadInstance { Color = color };
        // 原始形状（外阴影挖掉 / 内阴影所在）放在 Paint0 / Paint1 / Texture
        var box = c.Shape;
        q.Paint0 = Box(box.Rect.TransformBounds(ctm));
        q.Paint1 = new Vector4(box.TopLeft.Width, box.TopRight.Width, box.BottomRight.Width, box.BottomLeft.Width) * ctm.M11;
        q.Texture = new Vector4(box.TopLeft.Height, box.TopRight.Height, box.BottomRight.Height, box.BottomLeft.Height) * ctm.M22;
        q.Extra = new Vector4(sigma, 0, 0, 0);

        PixelRect outRect;
        RoundRect blurred;
        if (c.Inset == 0)
        {
            blurred = c.Spread != 0 ? offsetShape.Inflate(c.Spread) : offsetShape;
            var bounds = PixelRect.RoundOut(blurred.Rect.TransformBounds(ctm));
            if (bounds.IsEmpty) return;   // 负扩展把形状缩没了
            outRect = Inflate(bounds, margin).Intersect(clip.Bounds);
            q.Kinds.Shape = QuadShape.OuterShadow;
        }
        else
        {
            blurred = offsetShape.Inflate(-c.Spread);
            outRect = PixelRect.RoundOut(box.Rect.TransformBounds(ctm)).Intersect(clip.Bounds);
            q.Kinds.Shape = QuadShape.InsetShadow;
        }
        if (outRect.IsEmpty) return;
        q.Bounds = Box(outRect);
        q.Shape = Box(blurred.Rect.TransformBounds(ctm));
        SetRadii(ref q, blurred, ctm.M11, ctm.M22);
        Emit(ref q, null, null, BlendMode.SrcOver, clip);
    }

    static PixelRect Inflate(PixelRect r, int d) => new(r.X - d, r.Y - d, r.Width + 2 * d, r.Height + 2 * d);

    /// <summary>旋转、错切或翻转下的盒阴影：用软件后端的同一实现（<see cref="ShadowRasterizer"/>）在 CPU 上生成覆盖率，再作为 A8 掩码绘制。</summary>
    void BoxShadowOnCpu(in BoxShadowCmd c)
    {
        if (!_shadows.Compute(c, _state.Ctm, _state.Clip.Bounds, _window, out var mask, out var maskRect, out var outRect, _degraded)) return;
        if (!Allocate(outRect.Width, outRect.Height, out var slot)) return;
        var data = slot.Page.Shadow;
        for (int y = 0; y < outRect.Height; y++)
        {
            int o = slot.Offset + y * slot.Page.Width;
            int m = (outRect.Y - maskRect.Y + y) * maskRect.Width + (outRect.X - maskRect.X);
            for (int x = 0; x < outRect.Width; x++) data[o + x] = ScalarKernels.ToByte(mask[m + x]);
        }
        slot.Commit();
        var q = new GpuQuadInstance { Color = GradientLuts.Premultiply(c.Color) };
        EmitMask(q, slot, outRect.X, outRect.Y, outRect, null, BlendMode.SrcOver);
    }

    // ---------------------------------------------------------------- 图层

    void PushLayer(in PushLayerCmd c, bool isolate, Filter? filter)
    {
        _stack.Push(_state);
        var frame = new LayerFrame { Parent = _target, Opacity = c.Opacity, BlendMode = c.BlendMode };
        _layers.Push(frame);
        if (filter is not null && PushFilterLayer(c, frame, filter)) return;

        var clip = _state.Clip;
        if (c.HasBounds != 0) clip = clip.WithBounds(PixelRect.RoundOut(c.Bounds.TransformBounds(_state.Ctm)));
        _state = _state.With(clip);

        // 不需要隔离的组（不透明度 1、SrcOver、组内全是 SrcOver）等价于直接绘制，不分配离屏
        if (!isolate) return;
        if (clip.IsEmpty || c.Opacity <= 0f && c.BlendMode == BlendMode.SrcOver)
        {
            _state = _state.With(new ClipInfo(default));
            return;
        }
        if (_layerDepth >= MaxLayerDepth)
        {
            Degrade($"Layers nested deeper than {MaxLayerDepth} are drawn without an offscreen group (opacity and blend mode ignored).");
            return;
        }
        _layerDepth++;
        frame.Offscreen = true;
        var layer = new FrameTarget(clip.Bounds, null);
        _graph.Layers.Add(layer);
        frame.Layer = layer;
        _target = layer;
        _dstOp = null;
    }

    void PopLayer()
    {
        if (_layers.Count == 0) return;
        var frame = _layers.Pop();
        var inner = _state;
        _state = _stack.Count > 0 ? _stack.Pop() : _state;
        if (frame.Offscreen) _layerDepth--;
        if (frame.Layer is null) return;
        if (frame.Filter is not null)
        {
            PopFilterLayer(frame);
            return;
        }

        _target = frame.Parent;
        _dstOp = null;
        var layer = frame.Layer;
        var mode = ResolveBlend(frame.BlendMode);
        var bounds = layer.Rect.Intersect(inner.Clip.Bounds);
        if (bounds.IsEmpty) return;
        _target.Ops.Add(new FrameOp { Kind = FrameOpKind.RenderLayer, Layer = layer });
        var q = new GpuQuadInstance
        {
            Bounds = Box(bounds),
            Color = new Vector4(frame.Opacity, 0, 0, 0),
            Texture = new Vector4(-layer.Rect.X, -layer.Rect.Y, 0, 0),
            Kinds = new QuadKinds { Shape = QuadShape.Layer },
        };
        // 图层内部已经应用过裁剪，合成时只按矩形范围写回
        Emit(ref q, layer, null, mode, new ClipInfo(inner.Clip.Bounds));
    }

    // ---------------------------------------------------------------- 滤镜（与软件后端相同的 FilterPlan）

    /// <summary>离屏滤镜图像的最大边长（设备像素）；超出时不应用滤镜并上报降级。</summary>
    const int MaxFilterTextureSize = 8192;

    /// <summary>
    /// 滤镜图层（见软件后端的 RasterContext.PushFilterLayer）：内容画在不受外层裁剪限制的离屏目标上，弹出时执行滤镜，再以外层的完整裁剪合成。
    /// 返回 false 表示按普通图层处理。
    /// </summary>
    bool PushFilterLayer(in PushLayerCmd c, LayerFrame frame, Filter filter)
    {
        var ctm = _state.Ctm;
        var parentClip = _state.Clip;
        FilterPlan.DeviceOutset(filter, ctm, out int l, out int t, out int r, out int b);
        var limit = PixelRect.FromLTRB(
            _window.X - Math.Min(l, FilterPlan.MaxOutsideReach), _window.Y - Math.Min(t, FilterPlan.MaxOutsideReach),
            _window.Right + Math.Min(r, FilterPlan.MaxOutsideReach), _window.Bottom + Math.Min(b, FilterPlan.MaxOutsideReach));
        var rect = c.HasBounds != 0 ? PixelRect.RoundOut(c.Bounds.TransformBounds(ctm)).Intersect(limit) : limit;
        var reach = PixelRect.FromLTRB(rect.X - l, rect.Y - t, rect.Right + r, rect.Bottom + b);
        if (rect.IsEmpty || parentClip.IsEmpty || reach.Intersect(parentClip.Bounds).IsEmpty || c.Opacity <= 0f && c.BlendMode == BlendMode.SrcOver)
        {
            _state = _state.With(new ClipInfo(default));
            return true;
        }
        if ((long)rect.Width * rect.Height > RasterContext.MaxFilterLayerPixels || !FitsTexture(limit))
        {
            Degrade($"Filter layer larger than {RasterContext.MaxFilterLayerPixels} device pixels drawn without its filter.");
            return false;
        }
        if (_layerDepth >= MaxLayerDepth)
        {
            Degrade($"Layers nested deeper than {MaxLayerDepth} are drawn without an offscreen group (opacity, blend mode and filter ignored).");
            return false;
        }
        _layerDepth++;
        frame.Offscreen = true;
        frame.Filter = filter;
        frame.Ctm = ctm;
        frame.FilterLimit = limit;
        var layer = new FrameTarget(rect, null);
        _graph.Layers.Add(layer);
        frame.Layer = layer;
        _target = layer;
        _dstOp = null;
        _state = _state.With(new ClipInfo(rect));
        return true;
    }

    /// <summary>滤镜的所有中间图像都在 limit 之内：limit 的边长不超过纹理上限时它们都能放进一张纹理。</summary>
    static bool FitsTexture(PixelRect limit) => limit.Width <= MaxFilterTextureSize && limit.Height <= MaxFilterTextureSize;

    void PopFilterLayer(LayerFrame frame)
    {
        _target = frame.Parent;
        _dstOp = null;
        var result = RecordFilter(frame.Filter!, frame.Ctm, frame.Layer!, frame.FilterLimit);
        if (result is null) return;
        var mode = ResolveBlend(frame.BlendMode);
        // 滤镜的结果受外层的完整裁剪（含解析形状与蒙版）
        var clip = _state.Clip;
        var bounds = result.Rect.Intersect(clip.Bounds);
        if (bounds.IsEmpty) return;
        _target.Ops.Add(new FrameOp { Kind = FrameOpKind.RenderLayer, Layer = result });
        var q = new GpuQuadInstance
        {
            Bounds = Box(bounds),
            Color = new Vector4(frame.Opacity, 0, 0, 0),
            Texture = new Vector4(-result.Rect.X, -result.Rect.Y, 0, 0),
            Kinds = new QuadKinds { Shape = QuadShape.Layer },
        };
        Emit(ref q, result, null, mode, clip);
    }

    /// <summary>
    /// 把滤镜的执行计划录制成一串离屏目标：每一步一个目标，先渲染它读取的输入，再画一个 <see cref="QuadShape.Filter"/> 实例。
    /// 返回最终图像的目标；结果为空（或超出预算）时返回 null。
    /// </summary>
    FrameTarget? RecordFilter(Filter filter, in Matrix3x2 ctm, FrameTarget source, PixelRect limit)
    {
        int last = FilterPlan.Build(filter, ctm, source.Rect, limit, _filterImages, _filterPasses, _degraded);
        var images = _filterImages;
        var targets = new FrameTarget?[images.Count];
        targets[0] = source;
        foreach (var p in _filterPasses)
        {
            var outRect = images[p.Output].Rect;
            var inRect = images[p.Input].Rect;
            if (outRect.IsEmpty || inRect.IsEmpty) return null;
            var input = targets[p.Input]!;
            var t = new FrameTarget(outRect, null);
            _graph.Layers.Add(t);
            t.Ops.Add(new FrameOp { Kind = FrameOpKind.RenderLayer, Layer = input });
            FrameTarget? second = null;
            if (p.Input2 >= 0)
            {
                second = targets[p.Input2];
                if (second is null || images[p.Input2].Rect.IsEmpty) return null;
                t.Ops.Add(new FrameOp { Kind = FrameOpKind.RenderLayer, Layer = second });
            }
            var q = new GpuQuadInstance
            {
                Bounds = Box(outRect),
                Texture = new Vector4(-inRect.X, -inRect.Y, 0, 0),
                RadiiY = new Vector4(0, 0, inRect.Width, inRect.Height),
                Kinds = new QuadKinds { Shape = QuadShape.Filter },
            };
            long taps = 1;
            switch (p.Kind)
            {
                case FilterPassKind.ColorMatrix:
                {
                    var m = p.Matrix!;
                    q.Kinds.Reserved = QuadFilterOp.ColorMatrix;
                    q.Paint0 = new Vector4(m[0], m[1], m[2], m[3]);
                    q.Paint1 = new Vector4(m[5], m[6], m[7], m[8]);
                    q.Shape = new Vector4(m[10], m[11], m[12], m[13]);
                    q.RadiiX = new Vector4(m[15], m[16], m[17], m[18]);
                    q.Extra = new Vector4(m[4], m[9], m[14], m[19]);
                    break;
                }
                case FilterPassKind.Downsample:
                    q.Kinds.Reserved = QuadFilterOp.Downsample;
                    q.Extra = new Vector4(p.Scale, 0, 0, 0);
                    taps = (long)p.Scale * p.Scale;
                    break;
                case FilterPassKind.BlurX:
                case FilterPassKind.BlurY:
                {
                    FilterPlan.Weights(p.Sigma, p.Radius, out float sum);
                    q.Kinds.Reserved = p.Kind == FilterPassKind.BlurX ? QuadFilterOp.BlurX : QuadFilterOp.BlurY;
                    q.Extra = new Vector4(1 / (2 * p.Sigma * p.Sigma), p.Radius, 1 / sum, 0);
                    taps = 2L * p.Radius + 1;
                    break;
                }
                case FilterPassKind.Upsample:
                    q.Kinds.Reserved = QuadFilterOp.Upsample;
                    q.Extra = new Vector4(p.Scale, 0, 0, 0);
                    taps = 4;
                    break;
                case FilterPassKind.DropShadow:
                {
                    var r2 = images[p.Input2].Rect;
                    q.Kinds.Reserved = QuadFilterOp.DropShadow;
                    q.Color = p.Color;
                    q.Paint0 = new Vector4(-(p.Dx + r2.X), -(p.Dy + r2.Y), 0, 0);
                    q.RadiiX = new Vector4(0, 0, r2.Width, r2.Height);
                    taps = 2;
                    break;
                }
            }
            int index = _graph.Add(q);
            _instancePixels += (long)outRect.Width * outRect.Height * taps;
            t.Ops.Add(new FrameOp
            {
                Kind = FrameOpKind.Draw, First = index, Count = 1, Coverage = input, Lut = second, Blend = GpuBlendState.Replace,
            });
            targets[p.Output] = t;
        }
        var result = targets[last];
        return result is null || result.Rect.IsEmpty ? null : result;
    }

    /// <summary>
    /// 背景滤镜（见软件后端的 RasterContext.DrawBackdrop）：把当前目标在读取范围内的像素拷贝到离屏纹理，执行滤镜，再在形状之内以 Src
    /// 写回（覆盖率 × 不透明度插值）。
    /// </summary>
    void DrawBackdrop(in DrawBackdropCmd c, Filter filter, bool aa)
    {
        var clip = _state.Clip;
        if (clip.IsEmpty || filter.IsEmpty) return;
        var ctm = _state.Ctm;
        var shapeDev = c.Shape.Rect.TransformBounds(ctm);
        var shapePx = PixelRect.RoundOut(shapeDev);
        var area = shapePx.Intersect(clip.Bounds);
        if (area.IsEmpty) return;
        FilterPlan.DeviceOutset(filter, ctm, out int l, out int t, out int r, out int b);
        var input = PixelRect.FromLTRB(shapePx.X - l, shapePx.Y - t, shapePx.Right + r, shapePx.Bottom + b).Intersect(_window);
        if ((long)input.Width * input.Height > RasterContext.MaxFilterLayerPixels || !FitsTexture(_window))
        {
            Degrade($"Backdrop filter larger than {RasterContext.MaxFilterLayerPixels} device pixels ignored.");
            return;
        }
        var copyRect = input.Intersect(_target.Rect);
        if (copyRect.IsEmpty) return;
        var source = new FrameTarget(copyRect, null);
        _graph.Layers.Add(source);
        _target.Ops.Add(new FrameOp { Kind = FrameOpKind.CopyToLayer, Layer = source, CopyRect = copyRect });
        _dstOp = null;
        var result = RecordFilter(filter, ctm, source, _window);
        if (result is null) return;
        _target.Ops.Add(new FrameOp { Kind = FrameOpKind.RenderLayer, Layer = result });

        // 形状作为裁剪：解析的圆角矩形或蒙版（与软件后端的覆盖率相同的算法）
        var saved = _state;
        if (c.Shape.IsRect) ClipRect(c.Shape.Rect, aa);
        else if (!TryClipShape(c.Shape, aa)) ClipPath(VectorPath.FromRoundRect(c.Shape), aa);
        var shapeClip = _state.Clip;
        _state = saved;
        var bounds = area.Intersect(shapeClip.Bounds).Intersect(result.Rect);
        if (bounds.IsEmpty) return;
        var q = new GpuQuadInstance
        {
            Bounds = Box(bounds),
            Color = new Vector4(c.Opacity, 0, 0, 0),
            Texture = new Vector4(-result.Rect.X, -result.Rect.Y, 0, 0),
            Kinds = new QuadKinds { Shape = QuadShape.Layer },
        };
        Emit(ref q, result, null, BlendMode.Src, shapeClip);
    }
}
