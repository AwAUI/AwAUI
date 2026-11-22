using System;
using System.Collections.Generic;
using System.Numerics;
using AwAUI.Graphics;
using AwAUI.Graphics.Commands;

namespace AwAUI.Render.Raster;

/// <summary>
/// 软件渲染的 DisplayList 解释器。持有可复用的临时缓冲，一个实例同一时间只服务一次渲染。
/// </summary>
internal sealed partial class RasterContext
{
    readonly Rasterizer _rasterizer = new();
    readonly PolyList _poly = new();
    readonly FillSink _fillSink = new();
    readonly Stack<State> _stack = new();
    readonly Stack<LayerFrame> _layers = new();
    readonly Action<string>? _degraded;
    readonly Action<string>? _degradedSink;
    readonly HashSet<string> _reportedThisFrame = new();
    readonly ShadowRasterizer _shadows;
    readonly GradientLutCache _luts = new();
    readonly FilterExecutor _filters = new();

    Surface _surface = null!;
    State _state;

    /// <summary>
    /// 本帧的光栅窗口：整个根目标（不是损伤矩形）。展平的剔除范围、光栅器的累积坐标系与阴影的工作网格都取自它而不是当前裁剪，
    /// 所以裁剪区内的输出与裁剪矩形无关：只光栅化损伤矩形与整帧光栅化在矩形内逐字节相同（见 <see cref="Rasterizer.Fill(PolyList, FillRule, bool, PixelRect, PixelRect, CoverageSink)"/>）。
    /// </summary>
    PixelRect _window;
    int _layerDepth;
    long _commandBudget;
    long _workBudget;
    long _filterWork;
    bool _budgetReported;
    long _frameCommands;
    long _frameRasterPixels;

    /// <summary>最近一帧重放的命令数（含嵌套 DisplayList 展开）。诊断与测量用。</summary>
    public long LastFrameCommands => _frameCommands;

    /// <summary>最近一帧实际着色的像素数（已按裁剪区域截断，不含被裁剪掉的覆盖）。诊断与测量用。</summary>
    public long LastFrameRasterizedPixels => _frameRasterPixels;

    /// <summary>最近一帧执行的帧间位块搬移次数（纯滚动快路径）。</summary>
    public int LastFrameBlitCalls { get; private set; }

    /// <summary>最近一帧位块搬移复制的像素数（纯滚动快路径）。</summary>
    public long LastFrameBlitPixels { get; private set; }

    /// <summary>嵌套 DisplayList 的最大深度；更深的部分忽略并上报降级（防止恶意构造的深嵌套耗尽栈）。</summary>
    public const int MaxNestingDepth = 256;

    /// <summary>
    /// 一帧最多重放的命令数（含嵌套 DisplayList 展开后的命令）。DisplayList 可以多次引用同一个子列表，
    /// 每层引用两次、嵌套 30 层就是 2³⁰ 次重放；超出时停止本帧其余的绘制并上报降级。
    /// </summary>
    public const long MaxCommandsPerFrame = 1 << 22;

    /// <summary>
    /// 同时打开的离屏图层上限。每层分配一块与裁剪范围等大的位图，更深的图层直接画进父表面（不透明度与混合模式被忽略，
    /// 并上报降级），防止恶意构造的深嵌套占用无界的内存。与 GPU 后端相同。
    /// </summary>
    public const int MaxLayerDepth = 32;

    /// <summary>
    /// 一帧的工作量上限（抽象单位：着色混合的像素数 + 光栅化扫过的单元数 + 边数 × 条带数，见 <see cref="FrameWork"/>）。
    /// 命令数上限挡不住「少量命令、每条都很贵」的输入：嵌套 DisplayList 的扇出可以用几百字节重放 2²² 次全屏填充（软件后端数小时）。
    /// 2³⁰ 约为 1280×880 全屏重画 1000 次，是正常界面帧（通常 10⁶–10⁷）的百倍以上；超出时停止本帧其余的绘制并上报降级。
    /// GPU 后端用同一上限（实例按覆盖的像素计）。
    /// </summary>
    public const long MaxWorkPerFrame = 1L << 30;

    /// <summary>测试用：覆盖当前线程的每帧工作量上限。</summary>
    [ThreadStatic] internal static long? WorkBudgetOverride;

    /// <summary>当前帧（或最近一帧）已消耗的工作量（诊断与测试）。</summary>
    internal long FrameWork => _rasterizer.Work + _fillSink.Pixels + _filterWork;

    /// <summary>最近一帧的工作量（诊断与测量；下一次渲染之前保持有效）。</summary>
    public long LastFrameWork => FrameWork;

    /// <summary>
    /// 一个滤镜图层的最大面积（设备像素）。超出时不应用滤镜（按普通图层绘制）并上报降级：滤镜图层不受裁剪限制，
    /// 恶意的巨大图层否则要分配与之成正比的内存。
    /// </summary>
    public const long MaxFilterLayerPixels = 1L << 24;

    public RasterContext(Action<string>? degraded = null, GlyphCache? glyphCache = null)
    {
        _degradedSink = degraded;
        // 同一帧内相同的降级信息只上报一次：恶意输入可以让同一种降级在一帧里出现数百万次
        _degraded = degraded is null ? null : ReportOnce;
        _glyphCache = glyphCache ?? new GlyphCache();
        _glyphRasterizer = new GlyphRasterizer(_rasterizer);
        _shadows = new ShadowRasterizer(_rasterizer);
    }

    void ReportOnce(string message)
    {
        if (_reportedThisFrame.Count < 1024 && _reportedThisFrame.Add(message)) _degradedSink!(message);
    }

    /// <summary>本上下文的渐变查找表缓存（测试与诊断用）。</summary>
    internal GradientLutCache GradientLuts => _luts;

    sealed class Surface
    {
        public Surface(Bitmap bitmap, int offsetX, int offsetY) { Bitmap = bitmap; OffsetX = offsetX; OffsetY = offsetY; }
        public Bitmap Bitmap { get; }
        public int OffsetX { get; }
        public int OffsetY { get; }
    }

    readonly struct State
    {
        public State(Matrix3x2 ctm, ClipState clip, bool isLayer) { Ctm = ctm; Clip = clip; IsLayer = isLayer; }
        public Matrix3x2 Ctm { get; }
        public ClipState Clip { get; }
        public bool IsLayer { get; }
        public State With(Matrix3x2 ctm) => new(ctm, Clip, IsLayer);
        public State With(ClipState clip) => new(Ctm, clip, IsLayer);
    }

    sealed class LayerFrame
    {
        public Surface? Parent;
        public Surface? Layer;
        public float Opacity;
        public BlendMode BlendMode;
        public bool Offscreen;

        /// <summary>滤镜图层：滤镜、录制时的变换、离屏范围与滤镜的范围上限（见 <see cref="FilterPlan"/>）。</summary>
        public Filter? Filter;
        public Matrix3x2 Ctm;
        public PixelRect FilterLimit;
    }

    /// <summary>把 DisplayList 画到位图上。DisplayList 坐标乘以 scale 得到设备像素。</summary>
    public void Render(DisplayList list, Bitmap target, double scale, in RenderOptions options)
    {
        var full = new PixelRect(0, 0, target.Width, target.Height);
        _frameCommands = 0;
        _frameRasterPixels = 0;
        LastFrameBlitCalls = 0;
        LastFrameBlitPixels = 0;
        // 受损区域可以分解成多个互不相连的矩形（纯滚动帧：新露出的条带 + 滚动条条带）。包围盒是整个视口，
        // 只按包围盒重画会失去像素复用的意义；每个矩形单独清屏 + 重放（光栅结果与裁剪无关，见 Rasterizer）。
        bool multi = options.DamageRects is { Count: > 0 };
        var single = (options.Damage is { } dmg ? dmg : full).Intersect(full);
        if (!multi && single.IsEmpty) return;

        _surface = new Surface(target, 0, 0);
        _window = full;
        _layerDepth = 0;
        _commandBudget = MaxCommandsPerFrame;
        _workBudget = WorkBudgetOverride ?? MaxWorkPerFrame;
        _rasterizer.Work = 0;
        _fillSink.Pixels = 0;
        _fillSink.RasterPixels = 0;
        _filterWork = 0;
        _budgetReported = false;
        _reportedThisFrame.Clear();

        BeginImageFrame();
        // 纯滚动快路径：先把上一帧的像素搬到新位置，再只重画受损区域（新露出的条带、滚动条）。
        // 只有 Damage 非空（局部光栅化）时才搬移；整帧重画时搬移没有意义。
        if (options.Damage is not null && options.Shift is { } shift)
        {
            long moved = FramebufferBlit.Shift(target, shift);
            if (moved > 0)
            {
                LastFrameBlitCalls = 1;
                LastFrameBlitPixels = moved;
            }
        }
        if (multi)
        {
            var rects = options.DamageRects!;
            for (int i = 0; i < rects.Count; i++)
            {
                var area = rects[i].Intersect(full);
                if (!area.IsEmpty) RenderArea(list, target, scale, area, options.ClearColor);
            }
        }
        else
        {
            RenderArea(list, target, scale, single, options.ClearColor);
        }
        _frameRasterPixels = _fillSink.RasterPixels;
        _surface = null!;
        // 目标位图的内容变了：把它当作图片画的缓存（按版本做键）随之失效
        target.Invalidate();
    }

    void RenderArea(DisplayList list, Bitmap target, double scale, PixelRect area, Color clearColor)
    {
        _stack.Clear();
        _layers.Clear();
        _layerDepth = 0;
        _state = new State(Matrix3x2.CreateScale((float)scale), new ClipState(area), false);
        ClearArea(target, area, clearColor);
        Replay(list, 0);
        // 损坏的输入可能留下未关闭的图层：合成回去并释放离屏位图
        while (_layers.Count > 0) PopLayer();
    }

    static void ClearArea(Bitmap target, PixelRect area, Color color)
    {
        var p = Shader.Premultiply(color);
        byte r = ToByte(p.X), g = ToByte(p.Y), b = ToByte(p.Z), a = ToByte(p.W);
        if (target.Format == PixelFormat.Bgra8888) (r, b) = (b, r);
        for (int y = area.Y; y < area.Bottom; y++)
            Kernels.FillPixels(target.GetRow(y).Slice(area.X * 4, area.Width * 4), r, g, b, a);
    }

    void Replay(DisplayList list, int depth)
    {
        var reader = list.GetReader();
        LayerIsolation.Summary? isolation = null;
        int layerIndex = 0;
        while (reader.MoveNext())
        {
            _frameCommands++;
            if (--_commandBudget < 0)
            {
                if (!_budgetReported) _degraded?.Invoke($"More than {MaxCommandsPerFrame} draw commands in one frame (including nested display lists); the rest were skipped.");
                _budgetReported = true;
                return;
            }
            if (FrameWork > _workBudget)
            {
                if (!_budgetReported) _degraded?.Invoke($"Frame exceeded the work limit of {_workBudget} pixel operations; the rest of the frame was skipped.");
                _budgetReported = true;
                _commandBudget = 0;
                return;
            }
            bool aa = (reader.Flags & DrawFlags.AntiAlias) != 0;
            switch (reader.Op)
            {
                case DrawOp.Save:
                    _stack.Push(_state);
                    _state = new State(_state.Ctm, _state.Clip, false);
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
                    else ClipPath(VectorPath.FromRoundRect(c.Shape), aa);
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
                    FillPath(VectorPath.FromRoundRect(c.Shape), c.Paint, reader.GetBrush(c.Paint), aa);
                    break;
                }
                case DrawOp.StrokeRect:
                {
                    var c = reader.Read<StrokeRectCmd>();
                    StrokePath(VectorPath.FromRect(c.Rect), c.Paint, reader.GetBrush(c.Paint), reader.GetResource<Stroke>(c.StrokeRef), aa);
                    break;
                }
                case DrawOp.StrokeRoundRect:
                {
                    var c = reader.Read<StrokeRoundRectCmd>();
                    StrokePath(VectorPath.FromRoundRect(c.Shape), c.Paint, reader.GetBrush(c.Paint), reader.GetResource<Stroke>(c.StrokeRef), aa);
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
                    // 不透明度为 1 的 SrcOver 组只在组内有读目标的绘制时才需要离屏（见 LayerIsolation）；滤镜图层总是离屏
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
                    if (depth >= MaxNestingDepth)
                    {
                        _degraded?.Invoke($"DisplayList nesting deeper than {MaxNestingDepth} ignored.");
                        break;
                    }
                    var saved = _state;
                    int stackDepth = _stack.Count;
                    int layers = _layers.Count;
                    Replay(child, depth + 1);
                    // 子列表里未配对的图层与 Save 在这里关闭（录制器保证配对，这里只防御损坏的输入）
                    while (_layers.Count > layers) PopLayer();
                    while (_stack.Count > stackDepth) _stack.Pop();
                    _state = saved;
                    break;
                }
                case DrawOp.Annotation:
                    break;
                default:
                    _degraded?.Invoke($"Unsupported draw op {reader.Op} ignored.");
                    break;
            }
        }
    }

    // ---------------------------------------------------------------- 裁剪

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
        ClipPath(VectorPath.FromRect(rect), aa);
    }

    void ClipPath(VectorPath path, bool aa)
    {
        var clip = _state.Clip;
        if (clip.IsEmpty) return;
        _poly.Clear();
        Flattener.FlattenToPolygons(path, _state.Ctm, Flattener.DefaultTolerance, _poly, CullRect.FromPixels(_window, 1));
        if (!_poly.TryGetBounds(out var l, out var t, out var r, out var b))
        {
            _state = _state.With(new ClipState(default));
            return;
        }
        var rect = PixelRect.RoundOut(Rect.FromLTRB(l, t, r, b)).Intersect(clip.Bounds);
        if (rect.IsEmpty)
        {
            _state = _state.With(new ClipState(default));
            return;
        }
        var mask = new float[rect.Width * rect.Height];
        _rasterizer.Fill(_poly, path.FillRule, aa, rect, _window, new MaskSink(mask, rect, clip));
        _state = _state.With(new ClipState(rect, mask, rect));
    }

    static bool IsPixelAligned(Rect r) =>
        IsInt(r.Left) && IsInt(r.Top) && IsInt(r.Right) && IsInt(r.Bottom);

    static bool IsInt(float v) => MathF.Abs(v - MathF.Round(v)) < 1e-3f;
    static int Round(float v) => (int)MathF.Round(v);

    // ---------------------------------------------------------------- 填充与描边

    void FillRect(Rect rect, in PaintData paint, object? brush, bool aa)
    {
        if (!_state.Ctm.IsScaleTranslate())
        {
            FillPath(VectorPath.FromRect(rect), paint, brush, aa);
            return;
        }
        if (!RectArea(rect, aa, out var dev, out var area)) return;
        var sink = PrepareSink(paint, brush);
        if (sink == null) return;
        RectCoverage(dev, area, sink);
    }

    /// <summary>缩放 + 平移下矩形的设备范围（不抗锯齿时边对齐到整像素）与它和裁剪的交集；不可见时返回 false。</summary>
    bool RectArea(Rect rect, bool aa, out Rect dev, out PixelRect area)
    {
        area = default;
        dev = rect.TransformBounds(_state.Ctm);
        var clip = _state.Clip;
        if (clip.IsEmpty) return false;
        if (!aa) dev = Rect.FromLTRB(MathF.Round(dev.Left), MathF.Round(dev.Top), MathF.Round(dev.Right), MathF.Round(dev.Bottom));
        area = PixelRect.RoundOut(dev).Intersect(clip.Bounds);
        return !area.IsEmpty;
    }

    /// <summary>
    /// 轴对齐矩形的解析式覆盖率（水平覆盖 × 垂直覆盖），逐行交给 <paramref name="sink"/>。每个像素只取决于它自己的坐标，与裁剪区无关。
    /// </summary>
    void RectCoverage(Rect dev, PixelRect area, FillSink sink)
    {
        // 解析式覆盖率：水平覆盖 × 垂直覆盖
        // 行缓冲复用：每个矩形填充（背景、边框、选区……）都走这里，逐次分配会让每一帧产生与绘制命令数成正比的垃圾
        var cov = RowScratch(ref _rowA, area.Width);
        var horizontal = RowScratch(ref _rowB, area.Width);
        for (int x = 0; x < horizontal.Length; x++)
        {
            float px = area.X + x;
            horizontal[x] = Overlap(px, px + 1, dev.Left, dev.Right);
        }
        for (int y = area.Y; y < area.Bottom; y++)
        {
            float vy = Overlap(y, y + 1, dev.Top, dev.Bottom);
            if (vy <= 0) continue;
            Kernels.Scale(horizontal, vy, cov);
            sink.Row(y, area.X, cov);
        }
    }

    // 逐行覆盖率的临时缓冲（只在一次绘制命令内有效；CoverageSink 不保留传入的 Span）
    float[] _rowA = Array.Empty<float>();
    float[] _rowB = Array.Empty<float>();

    static Span<float> RowScratch(ref float[] buffer, int width)
    {
        if (buffer.Length < width) buffer = new float[Math.Max(width, 256)];
        return buffer.AsSpan(0, width);
    }

    static float Overlap(float a0, float a1, float b0, float b1) =>
        MathF.Max(0, MathF.Min(a1, b1) - MathF.Max(a0, b0));

    void FillPath(VectorPath path, in PaintData paint, object? brush, bool aa)
    {
        if (_state.Clip.IsEmpty) return;
        var sink = PrepareSink(paint, brush);
        if (sink == null) return;
        FillPath(path, aa, sink);
    }

    void FillPath(VectorPath path, bool aa, FillSink sink)
    {
        if (_state.Clip.IsEmpty) return;
        _poly.Clear();
        Flattener.FlattenToPolygons(path, _state.Ctm, Flattener.DefaultTolerance, _poly, CullRect.FromPixels(_window, 1));
        _rasterizer.Fill(_poly, path.FillRule, aa, _state.Clip.Bounds, _window, sink);
    }

    void StrokePath(VectorPath path, in PaintData paint, object? brush, Stroke stroke, bool aa)
    {
        if (_state.Clip.IsEmpty) return;
        // 在局部空间展平与描边（非均匀缩放下线宽随之变形），再变换到设备空间
        float scale = _state.Ctm.MaxScale();
        if (scale <= 0) return;
        float tol = Flattener.DefaultTolerance / scale;
        // 剔除范围取整个目标而不是裁剪区：范围外以弦代替的曲线、未生成的虚线段会改变同一行上浮点累积的舍入
        var cull = LocalCull(_window, _state.Ctm, stroke);
        // 虚线不能用弦代替范围外的曲线（会改变弧长与可见部分的虚线相位），只在生成虚线时裁剪
        var lines = Flattener.FlattenToPolylines(path, tol, stroke.Dashes is null ? cull : null);
        _poly.Clear();
        if (!Stroker.Stroke(lines, stroke, tol, _poly, stroke.Dashes is null ? null : cull))
            _degraded?.Invoke($"Dash pattern produces more than {Stroker.MaxDashPieces} visible dashes; stroked as a solid line.");
        _poly.Transform(_state.Ctm);
        var sink = PrepareSink(paint, brush);
        if (sink == null) return;
        _rasterizer.Fill(_poly, FillRule.NonZero, aa, _state.Clip.Bounds, _window, sink);
    }

    /// <summary>
    /// 设备裁剪范围在局部坐标中的外接矩形，按线宽的一半 × 斜接上限（覆盖连接与线帽）外扩；变换不可逆时返回 null（不裁剪）。
    /// 范围外的曲线以弦代替、虚线段不生成几何（GPU 后端共用）。
    /// </summary>
    internal static CullRect? LocalCull(PixelRect device, in Matrix3x2 ctm, Stroke stroke) =>
        CullRect.FromDevice(device, ctm, stroke.Width * 0.5f * MathF.Max(stroke.MiterLimit, 1.5f));

    FillSink? PrepareSink(in PaintData paint, object? brush)
    {
        var mode = paint.BlendMode;
        if (!Blender.IsSupported(mode))
        {
            _degraded?.Invoke($"Blend mode {mode} is not supported by the software renderer; using SrcOver.");
            mode = BlendMode.SrcOver;
        }
        var shader = Shader.Create(brush as Brush, paint.Color, _state.Ctm, _luts);
        if (shader is SolidShader { Color.W: <= 0 } && mode == BlendMode.SrcOver) return null;
        _fillSink.Reset(_surface.Bitmap, _surface.OffsetX, _surface.OffsetY, _state.Clip, shader, mode);
        return _fillSink;
    }

    // ---------------------------------------------------------------- 盒阴影

    /// <summary>σ 的上限（设备像素），见 <see cref="ShadowRasterizer.MaxShadowSigma"/>。</summary>
    internal const float MaxShadowSigma = ShadowRasterizer.MaxShadowSigma;

    void BoxShadow(in BoxShadowCmd c)
    {
        var clip = _state.Clip;
        if (clip.IsEmpty) return;
        if (!ShadowRasterizer.HasFiniteParameters(c))
        {
            _degraded?.Invoke("Box shadow with non-finite parameters ignored.");
            return;
        }
        if (!_shadows.Compute(c, _state.Ctm, clip.Bounds, _window, out var mask, out var maskRect, out var outRect, _degraded)) return;
        Composite(maskRect, mask, outRect, Shader.Premultiply(c.Color));
    }

    void Composite(PixelRect maskRect, float[] mask, PixelRect outRect, Vector4 color)
    {
        _fillSink.Reset(_surface.Bitmap, _surface.OffsetX, _surface.OffsetY, _state.Clip, new SolidShader(color), BlendMode.SrcOver);
        var row = RowScratch(ref _rowA, outRect.Width);
        for (int y = outRect.Y; y < outRect.Bottom; y++)
        {
            mask.AsSpan((y - maskRect.Y) * maskRect.Width + (outRect.X - maskRect.X), outRect.Width).CopyTo(row);
            _fillSink.Row(y, outRect.X, row);
        }
    }

    // ---------------------------------------------------------------- 图层

    void PushLayer(in PushLayerCmd c, bool isolate, Filter? filter)
    {
        _stack.Push(_state);
        var frame = new LayerFrame { Parent = _surface, Opacity = c.Opacity, BlendMode = c.BlendMode };
        _layers.Push(frame);
        if (filter is not null && PushFilterLayer(c, frame, filter)) return;

        var clip = _state.Clip;
        if (c.HasBounds != 0) clip = clip.WithBounds(PixelRect.RoundOut(c.Bounds.TransformBounds(_state.Ctm)));
        _state = new State(_state.Ctm, clip, true);

        // 不需要隔离的组（不透明度 1、SrcOver、组内全是 SrcOver）等价于直接绘制，不分配离屏
        if (!isolate) return;
        if (clip.IsEmpty || c.Opacity <= 0f && c.BlendMode == BlendMode.SrcOver)
        {
            _state = _state.With(new ClipState(default));
            return;
        }
        if (_layerDepth >= MaxLayerDepth)
        {
            _degraded?.Invoke($"Layers nested deeper than {MaxLayerDepth} are drawn without an offscreen group (opacity and blend mode ignored).");
            return;
        }
        _layerDepth++;
        frame.Offscreen = true;
        var b = clip.Bounds;
        var bitmap = new Bitmap(b.Size, _surface.Bitmap.Format);
        frame.Layer = new Surface(bitmap, b.X, b.Y);
        _surface = frame.Layer;
    }

    /// <summary>
    /// 滤镜图层：内容画在不受外层裁剪限制的离屏表面上（范围 = 图层边界 ∩ 渲染目标按滤镜范围外扩，与裁剪无关，所以局部重绘与整帧重绘相同），
    /// 弹出时执行滤镜，再以外层的完整裁剪（含蒙版）合成。返回 false 表示按普通图层处理（超出上限，已上报）。
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
        // 滤镜输出的范围与外层裁剪不相交（或组不可见）：组内容什么都不用画
        var reach = PixelRect.FromLTRB(rect.X - l, rect.Y - t, rect.Right + r, rect.Bottom + b);
        if (rect.IsEmpty || parentClip.IsEmpty || reach.Intersect(parentClip.Bounds).IsEmpty || c.Opacity <= 0f && c.BlendMode == BlendMode.SrcOver)
        {
            _state = new State(ctm, new ClipState(default), true);
            return true;
        }
        if ((long)rect.Width * rect.Height > MaxFilterLayerPixels)
        {
            _degraded?.Invoke($"Filter layer larger than {MaxFilterLayerPixels} device pixels drawn without its filter.");
            return false;
        }
        if (_layerDepth >= MaxLayerDepth)
        {
            _degraded?.Invoke($"Layers nested deeper than {MaxLayerDepth} are drawn without an offscreen group (opacity, blend mode and filter ignored).");
            return false;
        }
        _layerDepth++;
        frame.Offscreen = true;
        frame.Filter = filter;
        frame.Ctm = ctm;
        frame.FilterLimit = limit;
        frame.Layer = new Surface(new Bitmap(rect.Size, _surface.Bitmap.Format), rect.X, rect.Y);
        _surface = frame.Layer;
        _state = new State(ctm, new ClipState(rect), true);
        return true;
    }

    void PopLayer()
    {
        if (_layers.Count == 0) return;
        var frame = _layers.Pop();
        var inner = _state;
        _state = _stack.Count > 0 ? _stack.Pop() : _state;
        if (frame.Offscreen) _layerDepth--;
        if (frame.Layer == null) return;
        if (frame.Filter is not null)
        {
            PopFilterLayer(frame);
            return;
        }

        _surface = frame.Parent!;
        var layer = frame.Layer;
        var mode = frame.BlendMode;
        if (!Blender.IsSupported(mode))
        {
            _degraded?.Invoke($"Blend mode {mode} is not supported by the software renderer; using SrcOver.");
            mode = BlendMode.SrcOver;
        }
        // 图层内部已经应用过裁剪，合成时只按矩形范围写回
        if (mode == BlendMode.SrcOver && layer.Bitmap.Format == _surface.Bitmap.Format)
        {
            var layerRect = new PixelRect(layer.OffsetX, layer.OffsetY, layer.Bitmap.Width, layer.Bitmap.Height);
            var area = layerRect.Intersect(inner.Clip.Bounds);
            _fillSink.Pixels += (long)area.Width * area.Height;
            if (frame.Opacity > 0 && !area.IsEmpty)
            {
                int srcX = (area.X - layer.OffsetX) * 4;
                int dstX = (area.X - _surface.OffsetX) * 4;
                int width = area.Width * 4;
                for (int y = area.Y; y < area.Bottom; y++)
                {
                    var src = layer.Bitmap.GetRow(y - layer.OffsetY).Slice(srcX, width);
                    var dst = _surface.Bitmap.GetRow(y - _surface.OffsetY).Slice(dstX, width);
                    Kernels.SrcOverBitmapRow(dst, src, frame.Opacity);
                }
            }
            layer.Bitmap.Dispose();
            return;
        }
        _fillSink.Reset(_surface.Bitmap, _surface.OffsetX, _surface.OffsetY,
            new ClipState(inner.Clip.Bounds), new BitmapRowShader(layer.Bitmap, layer.OffsetX, layer.OffsetY), mode);
        var cov = new float[layer.Bitmap.Width];
        for (int i = 0; i < cov.Length; i++) cov[i] = frame.Opacity;
        for (int y = 0; y < layer.Bitmap.Height; y++) _fillSink.Row(layer.OffsetY + y, layer.OffsetX, cov.AsSpan());
        layer.Bitmap.Dispose();
    }

    void PopFilterLayer(LayerFrame frame)
    {
        _surface = frame.Parent!;
        var layer = frame.Layer!;
        var layerRect = new PixelRect(layer.OffsetX, layer.OffsetY, layer.Bitmap.Width, layer.Bitmap.Height);
        long budget = _workBudget - (_rasterizer.Work + _fillSink.Pixels);
        Bitmap? result;
        PixelRect rr;
        try
        {
            result = _filters.Run(frame.Filter!, frame.Ctm, layer.Bitmap, layerRect, layerRect, frame.FilterLimit, _degraded, ref _filterWork, budget, out rr);
        }
        finally
        {
            layer.Bitmap.Dispose();
        }
        if (result is null) return;
        try
        {
            var mode = frame.BlendMode;
            if (!Blender.IsSupported(mode))
            {
                _degraded?.Invoke($"Blend mode {mode} is not supported by the software renderer; using SrcOver.");
                mode = BlendMode.SrcOver;
            }
            // 滤镜的结果受外层的完整裁剪（含圆角 / 路径蒙版）
            var clip = _state.Clip;
            var area = rr.Intersect(clip.Bounds);
            if (area.IsEmpty) return;
            _fillSink.Reset(_surface.Bitmap, _surface.OffsetX, _surface.OffsetY, clip, new BitmapRowShader(result, rr.X, rr.Y), mode);
            var cov = new float[area.Width];
            for (int i = 0; i < cov.Length; i++) cov[i] = frame.Opacity;
            for (int y = area.Y; y < area.Bottom; y++) _fillSink.Row(y, area.X, cov.AsSpan());
        }
        finally
        {
            result.Dispose();
        }
    }

    // ---------------------------------------------------------------- 背景滤镜

    /// <summary>
    /// 背景滤镜（DisplayList 的 DrawBackdrop）：读取当前表面在「形状设备包围盒 + 滤镜范围」之内的像素（不受裁剪限制），执行滤镜，
    /// 再在形状之内以 Src 写回（覆盖率 × 不透明度插值）。
    /// </summary>
    void DrawBackdrop(in DrawBackdropCmd c, Filter filter, bool aa)
    {
        var clip = _state.Clip;
        if (clip.IsEmpty || filter.IsEmpty) return;
        var ctm = _state.Ctm;
        var shapeDev = c.Shape.Rect.TransformBounds(ctm);
        var area = PixelRect.RoundOut(shapeDev).Intersect(clip.Bounds);
        if (area.IsEmpty) return;
        FilterPlan.DeviceOutset(filter, ctm, out int l, out int t, out int r, out int b);
        var limit = _window;
        var shapePx = PixelRect.RoundOut(shapeDev);
        var input = PixelRect.FromLTRB(shapePx.X - l, shapePx.Y - t, shapePx.Right + r, shapePx.Bottom + b).Intersect(limit);
        var surfaceRect = new PixelRect(_surface.OffsetX, _surface.OffsetY, _surface.Bitmap.Width, _surface.Bitmap.Height);
        if ((long)input.Width * input.Height > MaxFilterLayerPixels)
        {
            _degraded?.Invoke($"Backdrop filter larger than {MaxFilterLayerPixels} device pixels ignored.");
            return;
        }
        long budget = _workBudget - (_rasterizer.Work + _fillSink.Pixels);
        var result = _filters.Run(filter, ctm, _surface.Bitmap, surfaceRect, input, limit, _degraded, ref _filterWork, budget, out var rr);
        if (result is null) return;
        try
        {
            // 形状的覆盖率（不含裁剪，裁剪由 FillSink 施加）× 不透明度
            var mask = new float[area.Width * area.Height];
            var maskSink = new MaskSink(mask, area, null);
            if (c.Shape.IsRect && ctm.IsScaleTranslate())
            {
                var dev = shapeDev;
                if (!aa) dev = Rect.FromLTRB(MathF.Round(dev.Left), MathF.Round(dev.Top), MathF.Round(dev.Right), MathF.Round(dev.Bottom));
                var row = new float[area.Width];
                for (int y = area.Y; y < area.Bottom; y++)
                {
                    float vy = Overlap(y, y + 1, dev.Top, dev.Bottom);
                    for (int x = 0; x < area.Width; x++) row[x] = vy * Overlap(area.X + x, area.X + x + 1, dev.Left, dev.Right);
                    maskSink.Row(y, area.X, row);
                }
            }
            else
            {
                var path = c.Shape.IsRect ? VectorPath.FromRect(c.Shape.Rect) : VectorPath.FromRoundRect(c.Shape);
                _poly.Clear();
                Flattener.FlattenToPolygons(path, ctm, Flattener.DefaultTolerance, _poly, CullRect.FromPixels(_window, 1));
                _rasterizer.Fill(_poly, FillRule.NonZero, aa, area, _window, maskSink);
            }
            if (c.Opacity < 1)
                for (int i = 0; i < mask.Length; i++) mask[i] *= c.Opacity;
            // 结果之外（被 limit 截掉）的像素按透明写回：与 GPU 后端相同
            _fillSink.Reset(_surface.Bitmap, _surface.OffsetX, _surface.OffsetY, clip, new FilteredBackdropShader(result, rr), BlendMode.Src);
            var cov = new float[area.Width];
            for (int y = area.Y; y < area.Bottom; y++)
            {
                Array.Copy(mask, (y - area.Y) * area.Width, cov, 0, area.Width);
                _fillSink.Row(y, area.X, cov);
            }
        }
        finally
        {
            result.Dispose();
        }
    }

    /// <summary>读取滤镜结果位图；范围外为透明黑。</summary>
    sealed class FilteredBackdropShader : Shader
    {
        readonly Bitmap _bitmap;
        readonly PixelRect _rect;

        public FilteredBackdropShader(Bitmap bitmap, PixelRect rect)
        {
            _bitmap = bitmap;
            _rect = rect;
        }

        public override void Shade(int x, int y, Span<Vector4> destination)
        {
            if (y < _rect.Y || y >= _rect.Bottom)
            {
                destination.Clear();
                return;
            }
            var row = _bitmap.GetRow(y - _rect.Y);
            bool bgra = _bitmap.Format == PixelFormat.Bgra8888;
            const float k = 1 / 255f;
            for (int i = 0; i < destination.Length; i++)
            {
                int px = x + i - _rect.X;
                if ((uint)px >= (uint)_rect.Width)
                {
                    destination[i] = default;
                    continue;
                }
                int o = px * 4;
                float r = row[o] * k, g = row[o + 1] * k, b = row[o + 2] * k, a = row[o + 3] * k;
                destination[i] = bgra ? new Vector4(b, g, r, a) : new Vector4(r, g, b, a);
            }
        }
    }

    static byte ToByte(float v) => (byte)(Math.Max(0f, Math.Min(1f, v)) * 255f + 0.5f);

    /// <summary>覆盖率 → 裁剪蒙版 → 着色 → 混合 写入当前表面。</summary>
    sealed class FillSink : CoverageSink
    {
        Bitmap _target = null!;
        int _ox, _oy;
        ClipState _clip = null!;
        Shader _shader = null!;
        BlendMode _mode;
        Vector4[] _colors = new Vector4[256];
        Vector4 _solid;

        /// <summary>本帧经过裁剪与混合的像素数（工作量统计，见 <see cref="MaxWorkPerFrame"/>）。</summary>
        public long Pixels;

        /// <summary>本帧实际着色的像素数：已按裁剪区域截断并去掉零覆盖的两端（测量用，不计工作量）。</summary>
        public long RasterPixels;
        bool _isSolid;
        float[] _cov = new float[256];

        public void Reset(Bitmap target, int ox, int oy, ClipState clip, Shader shader, BlendMode mode)
        {
            _target = target; _ox = ox; _oy = oy; _clip = clip; _shader = shader; _mode = mode;
            _isSolid = shader is SolidShader;
            _solid = shader is SolidShader solid ? solid.Color : default;
        }

        public override void Row(int y, int x, Span<float> coverage)
        {
            // 限制在裁剪矩形内
            var b = _clip.Bounds;
            if (y < b.Y || y >= b.Bottom) return;
            int start = Math.Max(x, b.X), end = Math.Min(x + coverage.Length, b.Right);
            if (end <= start) return;
            int n = end - start;
            if (_cov.Length < n) { _cov = new float[n]; _colors = new Vector4[n]; }
            var cov = new Span<float>(_cov, 0, n);
            coverage.Slice(start - x, n).CopyTo(cov);
            Blend(y, start, cov);
        }

        /// <summary>
        /// 与 <see cref="Row"/> 相同，但覆盖率为 8 位（字形掩码）：先跳过两端的零字节，再经 <paramref name="lut"/> 转为浮点。
        /// 结果与先转换为浮点再调用 <see cref="Row"/> 逐字节一致。
        /// </summary>
        public void RowA8(int y, int x, ReadOnlySpan<byte> coverage, float[] lut)
        {
            var b = _clip.Bounds;
            if (y < b.Y || y >= b.Bottom) return;
            int start = Math.Max(x, b.X), end = Math.Min(x + coverage.Length, b.Right);
            while (start < end && coverage[start - x] == 0) start++;
            while (end > start && coverage[end - 1 - x] == 0) end--;
            if (end <= start) return;
            int n = end - start;
            if (_cov.Length < n) { _cov = new float[n]; _colors = new Vector4[n]; }
            var cov = new Span<float>(_cov, 0, n);
            var src = coverage.Slice(start - x, n);
            for (int i = 0; i < n; i++) cov[i] = lut[src[i]];
            Blend(y, start, cov);
        }

        void Blend(int y, int start, Span<float> cov)
        {
            int n = cov.Length;
            Pixels += n;
            _clip.Apply(y, start, cov);

            // 去掉两端的零覆盖，减少着色量
            int first = 0, last = n - 1;
            while (first <= last && cov[first] <= 0) first++;
            while (last >= first && cov[last] <= 0) last--;
            if (first > last) return;
            int count = last - first + 1;
            RasterPixels += count;

            var dst = _target.GetRow(y - _oy).Slice((start + first - _ox) * 4, count * 4);
            bool bgra = _target.Format == PixelFormat.Bgra8888;
            if (_isSolid)
            {
                // 纯色不需要逐像素着色
                Blender.BlendSolidRow(dst, bgra, _solid, cov.Slice(first, count), _mode, _colors);
                return;
            }
            var colors = new Span<Vector4>(_colors, 0, count);
            _shader.Shade(start + first, y, colors);
            Blender.BlendRow(dst, bgra, colors, cov.Slice(first, count), _mode);
        }
    }
}
