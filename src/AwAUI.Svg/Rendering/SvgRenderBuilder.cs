using System;
using System.Collections.Generic;
using System.Numerics;
using AwAUI.Graphics;
using AwAUI.Svg.Model;
using AwAUI.Svg.Values;

namespace AwAUI.Svg.Rendering;

/// <summary>
/// SVG 节点树 → 渲染树。use 在这里展开（带环检测、嵌套层数与全局节点预算），几何、画刷、裁剪路径
/// 在这里一次性算好；绘制时只遍历渲染树发出命令。所有开销与 <see cref="SvgParseOptions"/> 的预算成线性。
/// </summary>
internal sealed class SvgRenderBuilder
{
    readonly Dictionary<string, SvgNode> _ids;
    readonly SvgParseOptions _options;
    readonly DiagnosticBag _diag;
    readonly int _maxDepth;
    readonly Size _rootViewport;
    readonly HashSet<SvgNode> _activeUses = new();
    readonly HashSet<string> _reported = new(StringComparer.Ordinal);
    readonly Dictionary<SvgNode, GeometryEntry> _geometry = new();
    readonly Dictionary<SvgNode, ComputedStyle> _originalStyles = new();
    readonly Dictionary<SvgNode, StopInfo[]> _stopCache = new();
    readonly Dictionary<SvgNode, ClipTemplate> _clipCache = new();
    int _segmentBudget;
    int _nodeBudget;
    int _depth;
    int _useDepth;
    Size _viewport;
    long _flattenBudget;
    /// <summary>当前用户空间 → 根用户空间（viewBox 坐标）的变换，用于估计展平代价。</summary>
    Matrix3x2 _ctm = Matrix3x2.Identity;
    /// <summary>参考渲染尺寸下（根 viewBox 的长边映射为 <see cref="ReferencePixels"/> 像素）根用户单位的像素数。</summary>
    readonly double _refScale;

    /// <summary>估计展平代价时假设的渲染尺寸（像素）。</summary>
    internal const double ReferencePixels = 2048;

    /// <summary>参考尺寸下形状绘制范围的上限（像素）。</summary>
    const double MaxReferenceExtent = 1e9;

    /// <summary>每个渐变最多使用的色标数（软件后端的色表只有 256 项，更多色标没有可见意义）。</summary>
    const int MaxStops = 256;

    /// <summary>每个色标折算的渲染工作量（展平点数）：光栅化时每个渐变画法都要按色标建一张 256 项的色表。</summary>
    const int GradientStopWork = 8;

    sealed class GeometryEntry
    {
        public GeometryEntry(Size viewport, float fontSize, VectorPath? path, Rect bbox)
        {
            Viewport = viewport;
            FontSize = fontSize;
            Path = path;
            BBox = bbox;
        }

        public readonly Size Viewport;
        public readonly float FontSize;
        public readonly VectorPath? Path;
        public readonly Rect BBox;
    }

    public SvgRenderBuilder(Dictionary<string, SvgNode> ids, SvgParseOptions options, DiagnosticBag diag, Size rootViewport)
    {
        _ids = ids;
        _options = options;
        _diag = diag;
        _maxDepth = options.EffectiveMaxDepth;
        _segmentBudget = Math.Max(0, options.MaxPathSegments);
        _nodeBudget = Math.Max(0, options.MaxRenderNodes);
        _rootViewport = rootViewport;
        _viewport = rootViewport;
        _flattenBudget = Math.Max(0, options.MaxFlattenedPoints);
        double longest = Math.Max(rootViewport.Width, rootViewport.Height);
        _refScale = longest > 0 && !double.IsInfinity(longest) ? ReferencePixels / longest : 1;
    }

    /// <summary>元素的 transform 属性。无效时记录诊断并视为没有变换；奇异变换返回 false（元素不可见）。</summary>
    bool TryElementTransform(SvgNode n, out Matrix3x2 m)
    {
        m = Matrix3x2.Identity;
        if (n.Attr("transform") is not { } t) return true;
        if (!SvgTransform.TryParse(t, out m))
        {
            Report(SvgDiagnosticCode.InvalidValue, "Invalid transform '" + Truncate(t) + "' was ignored.", n);
            m = Matrix3x2.Identity;
            return true;
        }
        return m.GetDeterminant() != 0;
    }

    /// <summary>把 <paramref name="local"/> 附加到当前变换，返回之前的变换（调用方在 finally 中恢复）。</summary>
    Matrix3x2 PushTransform(in Matrix3x2 local)
    {
        var saved = _ctm;
        _ctm = local * _ctm;
        return saved;
    }

    /// <summary>构建根元素。根的 x/y/width/height/viewBox 由文档在绘制时处理，这里把根当作普通组。</summary>
    public RenderNode? BuildRoot(SvgNode root)
    {
        var style = ComputedStyle.Compute(ComputedStyle.Initial, root, _viewport, _diag);
        if (style.DisplayNone || style.Opacity <= 0) return null;
        if (!TryElementTransform(root, out var m)) return null;
        RenderGroup? group;
        var saved = PushTransform(m);
        try { group = BuildChildren(root, style); }
        finally { _ctm = saved; }
        return group == null ? null : ApplyCommon(root, style, group, m);
    }

    // ---------------------------------------------------------------- 元素

    RenderNode? BuildNode(SvgNode n, ComputedStyle parent)
    {
        switch (n.Kind)
        {
            case SvgElementKind.Defs:
            case SvgElementKind.Symbol:
            case SvgElementKind.LinearGradient:
            case SvgElementKind.RadialGradient:
            case SvgElementKind.Stop:
            case SvgElementKind.ClipPath:
            case SvgElementKind.Style:
            case SvgElementKind.Descriptive:
                _nodeBudget--; // 跳过的子元素也计入预算：否则被 use 大量复用的组每个实例都要白白遍历它们
                return null;
            case SvgElementKind.Unsupported:
                _nodeBudget--;
                ReportOnce(SvgDiagnosticCode.UnsupportedElement, "<" + n.Name + "> is not supported and was ignored.", n);
                return null;
            case SvgElementKind.Unknown:
                _nodeBudget--;
                ReportOnce(SvgDiagnosticCode.UnsupportedElement, "Unknown element <" + n.Name + "> was ignored.", n);
                return null;
        }

        if (!Enter(n)) return null;
        try
        {
            var style = ComputedStyle.Compute(parent, n, _viewport, _diag);
            if (style.DisplayNone || style.Opacity <= 0) return null;
            if (!TryElementTransform(n, out var m)) return null;
            RenderNode? r;
            var saved = PushTransform(m);
            try
            {
                r = n.Kind switch
                {
                    SvgElementKind.G or SvgElementKind.A => BuildChildren(n, style),
                    SvgElementKind.Switch => BuildSwitch(n, style),
                    SvgElementKind.Svg => BuildViewport(n, style, null, null),
                    SvgElementKind.Use => BuildUse(n, style),
                    SvgElementKind.Image => BuildImage(n, style),
                    _ => BuildShape(n, style),
                };
            }
            finally
            {
                _ctm = saved;
            }
            return r == null ? null : ApplyCommon(n, style, r, m);
        }
        finally
        {
            _depth--;
        }
    }

    /// <summary>深度与节点预算检查；通过时深度 +1（调用方负责 -1）。</summary>
    bool Enter(SvgNode n)
    {
        if (_depth >= _maxDepth)
        {
            ReportOnce(SvgDiagnosticCode.LimitExceeded, "Render tree nesting exceeds the depth limit; deeper content was dropped.", n);
            return false;
        }
        if (_nodeBudget <= 0)
        {
            ReportOnce(SvgDiagnosticCode.LimitExceeded, "Render node limit exceeded; remaining content was dropped.", n);
            return false;
        }
        _nodeBudget--;
        _depth++;
        return true;
    }

    /// <summary>渲染节点预算是否还有余量；耗尽时记录一次诊断，调用方停止遍历其余子元素。</summary>
    bool HasNodeBudget(SvgNode n)
    {
        if (_nodeBudget > 0) return true;
        ReportOnce(SvgDiagnosticCode.LimitExceeded, "Render node limit exceeded; remaining content was dropped.", n);
        return false;
    }

    RenderGroup? BuildChildren(SvgNode n, ComputedStyle style)
    {
        List<RenderNode>? list = null;
        foreach (var c in n.Children)
        {
            if (!HasNodeBudget(c)) break;
            var r = BuildNode(c, style);
            if (r != null) (list ??= new List<RenderNode>()).Add(r);
        }
        return list == null ? null : MakeGroup(list.ToArray());
    }

    RenderNode? BuildSwitch(SvgNode n, ComputedStyle style)
    {
        foreach (var c in n.Children)
        {
            if (!HasNodeBudget(c)) break;
            var ext = c.Attr("requiredExtensions");
            if (c.Kind is SvgElementKind.Descriptive or SvgElementKind.Style || ext != null && ext.Trim().Length > 0)
            {
                _nodeBudget--; // 不支持任何扩展；跳过的子元素同样计入预算
                continue;
            }
            var r = BuildNode(c, style);
            return r == null ? null : MakeGroup(new[] { r });
        }
        return null;
    }

    /// <summary>
    /// 建立新视口的元素（嵌套 svg、被 use 引用的 symbol/svg）：外层组按视口裁剪，内层组做 viewBox 映射。
    /// </summary>
    RenderNode? BuildViewport(SvgNode n, ComputedStyle style, float? width, float? height)
    {
        bool isSymbol = n.Kind == SvgElementKind.Symbol;
        float x = isSymbol ? 0 : Length(n, "x", LengthAxis.X, 0, style);
        float y = isSymbol ? 0 : Length(n, "y", LengthAxis.Y, 0, style);
        float w = width ?? Length(n, "width", LengthAxis.X, -1, style, percentDefault: 100);
        float h = height ?? Length(n, "height", LengthAxis.Y, -1, style, percentDefault: 100);
        if (!(w > 0 && h > 0)) return null;

        Rect? viewBox = null;
        if (n.Attr("viewBox") is { } vbText)
        {
            if (!TryParseViewBox(vbText, out var vb, out bool disable))
                Report(SvgDiagnosticCode.InvalidValue, "Invalid viewBox '" + Truncate(vbText) + "' was ignored.", n);
            else if (disable) return null; // 宽或高为 0：不渲染
            else viewBox = vb;
        }
        var par = ParseAspect(n.Attr("preserveAspectRatio"), n);
        var viewport = new Rect(x, y, w, h);

        var contentTransform = viewBox is { } v ? par.GetTransform(v, viewport) : Matrix3x2.CreateTranslation(x, y);
        if (contentTransform.GetDeterminant() == 0 || !SvgTransform.IsFinite(contentTransform)) return null;
        var savedViewport = _viewport;
        var savedCtm = PushTransform(contentTransform);
        _viewport = viewBox?.Size ?? new Size(w, h);
        RenderGroup? content;
        try { content = BuildChildren(n, style); }
        finally
        {
            _viewport = savedViewport;
            _ctm = savedCtm;
        }
        if (content == null) return null;

        content.Transform = contentTransform;
        Finish(content);
        var outer = MakeGroup(new RenderNode[] { content });
        if (!style.OverflowVisible) outer.ClipRect = viewport;
        return outer;
    }

    RenderNode? BuildUse(SvgNode n, ComputedStyle style)
    {
        var href = n.Href;
        var id = SvgRef.FragmentId(href);
        if (id == null || !_ids.TryGetValue(id, out var target))
        {
            Report(SvgDiagnosticCode.MissingReference, "<use> reference '" + Truncate(href ?? "") + "' was not found.", n);
            return null;
        }
        if (_activeUses.Contains(target) || IsSelfOrAncestor(target, n))
        {
            Report(SvgDiagnosticCode.CircularReference, "<use> reference '#" + Truncate(id) + "' is circular and was ignored.", n);
            return null;
        }
        if (_useDepth >= _options.MaxUseDepth)
        {
            ReportOnce(SvgDiagnosticCode.LimitExceeded, "<use> nesting exceeds the limit; deeper references were dropped.", n);
            return null;
        }

        float x = Length(n, "x", LengthAxis.X, 0, style), y = Length(n, "y", LengthAxis.Y, 0, style);
        float? w = OptionalLength(n, "width", LengthAxis.X, style), h = OptionalLength(n, "height", LengthAxis.Y, style);

        _activeUses.Add(target);
        _useDepth++;
        var savedCtm = PushTransform(Matrix3x2.CreateTranslation(x, y));
        RenderNode? content;
        try
        {
            content = target.Kind is SvgElementKind.Symbol or SvgElementKind.Svg
                ? BuildReferencedViewport(target, style, w, h)
                : BuildNode(target, style);
        }
        finally
        {
            _ctm = savedCtm;
            _useDepth--;
            _activeUses.Remove(target);
        }
        if (content == null) return null;

        // use 的 x/y 是附加在内容上的平移；use 自身的 transform 与 clip-path 由 ApplyCommon 作用在外层组上
        if (x != 0 || y != 0)
        {
            var inner = MakeGroup(new[] { content });
            inner.Transform = Matrix3x2.CreateTranslation(x, y);
            Finish(inner);
            content = inner;
        }
        return MakeGroup(new[] { content });
    }

    /// <summary>
    /// use 引用自身或自己在文档中的祖先是环（SVG 2 §5.6.3），整个 use 不渲染。
    /// 只靠 <see cref="_activeUses"/> 会先多展开一层：祖先第一次是作为普通元素而不是 use 目标进入的。
    /// </summary>
    static bool IsSelfOrAncestor(SvgNode target, SvgNode use)
    {
        for (var p = use; p != null; p = p.Parent)
            if (ReferenceEquals(p, target)) return true;
        return false;
    }

    RenderNode? BuildReferencedViewport(SvgNode target, ComputedStyle useStyle, float? w, float? h)
    {
        if (!Enter(target)) return null;
        try
        {
            var style = ComputedStyle.Compute(useStyle, target, _viewport, _diag);
            if (style.DisplayNone || style.Opacity <= 0) return null;
            if (target.Kind == SvgElementKind.Symbol)
            {
                w ??= Length(target, "width", LengthAxis.X, -1, style, percentDefault: 100);
                h ??= Length(target, "height", LengthAxis.Y, -1, style, percentDefault: 100);
            }
            if (!TryElementTransform(target, out var m)) return null;
            RenderNode? r;
            var saved = PushTransform(m);
            try { r = BuildViewport(target, style, w, h); }
            finally { _ctm = saved; }
            return r == null ? null : ApplyCommon(target, style, r, m);
        }
        finally
        {
            _depth--;
        }
    }

    /// <summary>transform、clip-path、opacity 与不支持特性的诊断；最后计算绘制范围。返回 null 表示不渲染。</summary>
    RenderNode? ApplyCommon(SvgNode n, ComputedStyle style, RenderNode r, in Matrix3x2 transform)
    {
        if (!transform.IsIdentity) r.Transform *= transform;
        if (r is not (RenderShape or RenderImage) && style.Opacity < 1) r.Opacity = style.Opacity;
        if (style.Mask != null) ReportOnce(SvgDiagnosticCode.UnsupportedFeature, "Masks are not supported; the mask was ignored.", n);
        if (style.Filter != null) ReportOnce(SvgDiagnosticCode.UnsupportedFeature, "Filters are not supported; the filter was ignored.", n);
        if (style.HasMarkers && r is RenderShape)
            ReportOnce(SvgDiagnosticCode.UnsupportedFeature, "Markers are not supported and were ignored.", n);

        if (style.ClipPath != null)
        {
            if (SvgRef.TryParseUrl(style.ClipPath, out var id, out _))
            {
                if (_ids.TryGetValue(id, out var clipNode) && clipNode.Kind == SvgElementKind.ClipPath)
                {
                    VectorPath? clip;
                    bool clipAll;
                    var saved = PushTransform(transform);
                    try { clip = BuildClip(clipNode, r.LocalBBox, out clipAll); }
                    finally { _ctm = saved; }
                    if (clipAll) return null;
                    r.Clip = clip;
                }
                else
                {
                    Report(SvgDiagnosticCode.MissingReference, "clip-path reference '#" + Truncate(id) + "' was not found.", n);
                }
            }
            else
            {
                ReportOnce(SvgDiagnosticCode.UnsupportedFeature, "Only url(#id) clip paths are supported; '" + Truncate(style.ClipPath) + "' was ignored.", n);
            }
        }
        Finish(r);
        return r;
    }

    // ---------------------------------------------------------------- 图像

    /// <summary>每个 &lt;image&gt; 元素解析出的位图（被 use 多次引用的同一元素只解码一次；null 表示失败）。</summary>
    readonly Dictionary<SvgNode, Bitmap?> _images = new();
    long _imagePixels;
    bool _imageBudgetExhausted;
    const string ImageBudgetMessage = "Images exceed SvgParseOptions.MaxImagePixels; the rest were ignored.";

    /// <summary>
    /// &lt;image&gt;（SVG 2 §5.7）：x / y / width / height（auto 取位图的固有尺寸，只给一边时按宽高比推出另一边）、preserveAspectRatio
    /// （slice 直接裁成源矩形，不需要裁剪）、image-rendering（pixelated / crisp-edges / optimizeSpeed 为最近邻）、opacity 乘到图像上。
    /// 位图经 <see cref="SvgParseOptions.ImageResolver"/> 得到；data: URI 在这里解码，image/svg+xml 不支持。
    /// </summary>
    RenderNode? BuildImage(SvgNode n, ComputedStyle style)
    {
        var bitmap = ResolveImage(n);
        if (bitmap is null || bitmap.IsDisposed || bitmap.Width <= 0 || bitmap.Height <= 0) return null;
        float iw = bitmap.Width, ih = bitmap.Height;
        float x = Length(n, "x", LengthAxis.X, 0, style), y = Length(n, "y", LengthAxis.Y, 0, style);
        float? ow = OptionalNonNegative(n, "width", LengthAxis.X, style), oh = OptionalNonNegative(n, "height", LengthAxis.Y, style);
        float w = ow ?? (oh is { } h0 ? h0 * iw / ih : iw);
        float h = oh ?? (ow is { } w0 ? w0 * ih / iw : ih);
        if (!(w > 0 && h > 0) || !SvgNumber.IsFinite(w) || !SvgNumber.IsFinite(h) || !SvgNumber.IsFinite(x) || !SvgNumber.IsFinite(y)) return null;

        var par = ParseAspect(n.Attr("preserveAspectRatio"), n);
        var viewport = new Rect(x, y, w, h);
        var imageRect = new Rect(0, 0, iw, ih);
        var m = par.GetTransform(imageRect, viewport);
        if (!(m.M11 > 0 && m.M22 > 0) || !SvgTransform.IsFinite(m)) return null;
        var src = imageRect;
        var dst = imageRect.TransformBounds(m);
        if (par.Slice)
        {
            var visible = dst.Intersect(viewport);
            if (visible.IsEmpty) return null;
            src = Rect.FromLTRB((visible.Left - m.M31) / m.M11, (visible.Top - m.M32) / m.M22,
                (visible.Right - m.M31) / m.M11, (visible.Bottom - m.M32) / m.M22).Intersect(imageRect);
            dst = visible;
            if (src.IsEmpty) return null;
        }
        var sampling = style.PixelatedImages ? ImageSampling.Nearest : ImageSampling.Linear;
        return new RenderImage(bitmap, src, dst, sampling, Math.Max(0, Math.Min(1, style.Opacity))) { LocalBBox = viewport };
    }

    Bitmap? ResolveImage(SvgNode n)
    {
        if (_images.TryGetValue(n, out var cached)) return cached;
        Bitmap? bitmap = null;
        try { bitmap = LoadImage(n); }
        finally { _images[n] = bitmap; }
        return bitmap;
    }

    Bitmap? LoadImage(SvgNode n)
    {
        var href = n.Href;
        if (href == null || href.Trim().Length == 0) return null;
        if (_options.ImageResolver is not { } resolver)
        {
            ReportOnce(SvgDiagnosticCode.UnsupportedFeature, "<image> requires SvgParseOptions.ImageResolver; the image was ignored.", n);
            return null;
        }
        // 预算已用完时不再解码（否则每个后续的 <image> 仍要付一次解码的时间与内存，只是结果被丢弃）
        if (_imageBudgetExhausted || _imagePixels >= _options.MaxImagePixels)
        {
            ReportOnce(SvgDiagnosticCode.LimitExceeded, ImageBudgetMessage, n);
            return null;
        }
        Bitmap? bitmap;
        try
        {
            if (SvgDataUri.IsDataUri(href))
            {
                if (!SvgDataUri.TryDecode(href, out var mediaType, out var data))
                {
                    Report(SvgDiagnosticCode.InvalidValue, "Invalid data: URI in <image> was ignored.", n);
                    return null;
                }
                if (mediaType.StartsWith("image/svg", StringComparison.Ordinal))
                {
                    ReportOnce(SvgDiagnosticCode.UnsupportedFeature, "SVG documents inside <image> are not supported and were ignored.", n);
                    return null;
                }
                bitmap = resolver.Decode(data, mediaType);
            }
            else
            {
                bitmap = resolver.ResolveExternal(href.Trim());
            }
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            Report(SvgDiagnosticCode.InvalidValue, "<image> could not be decoded (" + Truncate(e.Message) + ") and was ignored.", n);
            return null;
        }
        if (bitmap is null)
        {
            Report(SvgDiagnosticCode.InvalidValue, "<image> '" + Truncate(href) + "' could not be loaded and was ignored.", n);
            return null;
        }
        long pixels = (long)bitmap.Width * bitmap.Height;
        if (_imagePixels + pixels > _options.MaxImagePixels)
        {
            ReportOnce(SvgDiagnosticCode.LimitExceeded, ImageBudgetMessage, n);
            _imageBudgetExhausted = true;
            return null;
        }
        _imagePixels += pixels;
        return bitmap;
    }

    // ---------------------------------------------------------------- 形状

    RenderNode? BuildShape(SvgNode n, ComputedStyle style)
    {
        if (!style.Visible) return null;
        var geom = Geometry(n, style);
        if (geom?.Path == null || geom.Path.IsEmpty) return null;
        var path = geom.Path;
        var bbox = geom.BBox;

        bool canFill = n.Kind != SvgElementKind.Line;
        var fill = canFill ? BuildPaint(style.Fill, style.FillOpacity, style, bbox, n) : null;
        Stroke? strokeStyle = MakeStroke(style, path, n);
        var stroke = strokeStyle != null ? BuildPaint(style.Stroke, style.StrokeOpacity, style, bbox, n) : null;
        if (fill == null && stroke == null) return null;

        float opacity = 1;
        if (style.Opacity < 1)
        {
            // 只有填充或只有描边时，组透明度等价于乘到画法上，省掉离屏图层
            if (stroke == null) fill = BuildPaint(style.Fill, style.FillOpacity * style.Opacity, style, bbox, n);
            else if (fill == null) stroke = BuildPaint(style.Stroke, style.StrokeOpacity * style.Opacity, style, bbox, n);
            else opacity = style.Opacity;
            if (fill == null && stroke == null) return null;
        }
        if (stroke == null) strokeStyle = null;

        // 绘制范围（含描边外扩）在参考尺寸下超过 ±1e9 像素的形状没有可见意义，却会让光栅器的浮点运算溢出
        if (!InReferenceRange(path.Bounds.Inflate(strokeStyle?.Outset ?? 0)))
        {
            ReportOnce(SvgDiagnosticCode.LimitExceeded, "Shapes with coordinates or stroke widths far out of range were not rendered.", n);
            return null;
        }
        var spent = SpendFlattening(path, (fill != null ? 1 : 0) + (stroke != null ? 1 : 0), n);
        if (spent == null) return null;
        path = spent;

        var shape = new RenderShape(path.WithFillRule(style.FillRule), fill, stroke, strokeStyle, style.StrokeFirst)
        {
            LocalBBox = bbox,
            Opacity = opacity,
        };
        return shape;
    }

    Stroke? MakeStroke(ComputedStyle style, VectorPath path, SvgNode n)
    {
        if (style.Stroke.Kind == PaintKind.None) return null;
        float width = style.StrokeWidth;
        if (!(width > 0) || !SvgNumber.IsFinite(width)) return null;
        var dashes = style.StrokeDashes;
        if (dashes != null)
        {
            double period = 0;
            foreach (var d in dashes) period += d;
            int count = dashes.Length;
            if (count % 2 == 1) { period *= 2; count *= 2; } // 奇数个时模式重复一遍
            double estimate = period > 0 ? PathGeometry.LengthUpperBound(path) / period * count : double.PositiveInfinity;
            // 坐标远大于虚线周期时，展平点的浮点舍入本身就会产生大量（甚至无法推进的）虚线段
            var b = path.Bounds;
            double magnitude = Math.Max(Math.Max(Math.Abs((double)b.Left), Math.Abs((double)b.Right)),
                Math.Max(Math.Abs((double)b.Top), Math.Abs((double)b.Bottom)));
            if (!(estimate <= _options.MaxDashSegments) || !(magnitude <= period * 1e6))
            {
                ReportOnce(SvgDiagnosticCode.LimitExceeded, "Dash pattern would produce too many segments; the stroke was drawn solid.", n);
                dashes = null;
            }
        }
        return new Stroke(width, style.StrokeCap, style.StrokeJoin, style.StrokeMiterLimit, dashes, style.StrokeDashOffset);
    }

    GeometryEntry? Geometry(SvgNode n, ComputedStyle style)
    {
        if (_geometry.TryGetValue(n, out var e) && e.Viewport == _viewport && e.FontSize == style.FontSize) return e;
        var path = BuildGeometry(n, style);
        e = new GeometryEntry(_viewport, style.FontSize, path, path == null ? Rect.Empty : PathGeometry.TightBounds(path));
        _geometry[n] = e;
        return e;
    }

    VectorPath? BuildGeometry(SvgNode n, ComputedStyle style)
    {
        switch (n.Kind)
        {
            case SvgElementKind.Path:
            {
                var d = n.Attr("d");
                if (string.IsNullOrEmpty(d)) return null;
                var pb = new PathBuilder();
                var error = SvgPathData.Parse(d.AsSpan(), pb, ref _segmentBudget);
                if (error != null)
                {
                    if (_segmentBudget < 0)
                        ReportOnce(SvgDiagnosticCode.LimitExceeded, "Path segment limit exceeded; remaining path data was dropped.", n);
                    else
                        Report(SvgDiagnosticCode.PathDataError, error, n);
                }
                return pb.ToPath();
            }
            case SvgElementKind.Rect:
            {
                float x = Length(n, "x", LengthAxis.X, 0, style), y = Length(n, "y", LengthAxis.Y, 0, style);
                float w = Length(n, "width", LengthAxis.X, 0, style), h = Length(n, "height", LengthAxis.Y, 0, style);
                if (w < 0 || h < 0) Report(SvgDiagnosticCode.InvalidValue, "<rect> with a negative size was not rendered.", n);
                if (!(w > 0 && h > 0) || !Spend(5, n)) return null;
                float? rx = OptionalNonNegative(n, "rx", LengthAxis.X, style), ry = OptionalNonNegative(n, "ry", LengthAxis.Y, style);
                float rxv = rx ?? ry ?? 0, ryv = ry ?? rx ?? 0;
                rxv = Math.Min(rxv, w / 2);
                ryv = Math.Min(ryv, h / 2);
                var rect = new Rect(x, y, w, h);
                var pb = new PathBuilder();
                if (rxv > 0 && ryv > 0)
                {
                    var r = new Size(rxv, ryv);
                    pb.AddRoundRect(new RoundRect(rect, r, r, r, r));
                }
                else pb.AddRect(rect);
                return pb.ToPath();
            }
            case SvgElementKind.Circle:
            {
                float cx = Length(n, "cx", LengthAxis.X, 0, style), cy = Length(n, "cy", LengthAxis.Y, 0, style);
                float r = Length(n, "r", LengthAxis.Other, 0, style);
                if (!(r > 0) || !Spend(5, n)) return null;
                return new PathBuilder().AddCircle(new Point(cx, cy), r).ToPath();
            }
            case SvgElementKind.Ellipse:
            {
                float cx = Length(n, "cx", LengthAxis.X, 0, style), cy = Length(n, "cy", LengthAxis.Y, 0, style);
                float? rx = OptionalNonNegative(n, "rx", LengthAxis.X, style), ry = OptionalNonNegative(n, "ry", LengthAxis.Y, style);
                float rxv = rx ?? ry ?? 0, ryv = ry ?? rx ?? 0;
                if (!(rxv > 0 && ryv > 0) || !Spend(5, n)) return null;
                return new PathBuilder().AddEllipse(new Rect(cx - rxv, cy - ryv, rxv * 2, ryv * 2)).ToPath();
            }
            case SvgElementKind.Line:
            {
                if (!Spend(2, n)) return null;
                var pb = new PathBuilder();
                pb.MoveTo(Length(n, "x1", LengthAxis.X, 0, style), Length(n, "y1", LengthAxis.Y, 0, style));
                pb.LineTo(Length(n, "x2", LengthAxis.X, 0, style), Length(n, "y2", LengthAxis.Y, 0, style));
                return pb.ToPath();
            }
            case SvgElementKind.Polyline:
            case SvgElementKind.Polygon:
            {
                var text = n.Attr("points");
                if (string.IsNullOrEmpty(text)) return null;
                var nums = SvgNumber.ParseList(text!, out bool error, Math.Max(0, _segmentBudget) * 2 + 2);
                int count = nums.Length / 2;
                if (error || nums.Length % 2 == 1)
                    Report(SvgDiagnosticCode.InvalidValue, "The points list has an error; only the points before it are used.", n);
                if (count < 2 || !Spend(count, n)) return null;
                var pb = new PathBuilder();
                pb.MoveTo(nums[0], nums[1]);
                for (int i = 1; i < count; i++) pb.LineTo(nums[2 * i], nums[2 * i + 1]);
                if (n.Kind == SvgElementKind.Polygon) pb.Close();
                return pb.ToPath();
            }
        }
        return null;
    }

    /// <summary>
    /// 扣除展平预算（参考尺寸下估计的折线点数 × 绘制遍数）。预算不够时曲线退化为弦，
    /// 防止大量巨大曲线让光栅器分配海量折线点；连弦（每段一点）也付不起时返回 null，调用方不渲染该形状——
    /// 否则同一条大路径经 use 大量复用时，每个实例都要光栅化一遍，总工作量就不再有界。
    /// </summary>
    VectorPath? SpendFlattening(VectorPath path, int passes, SvgNode n)
    {
        if (passes <= 0) return path;
        long chordCost = (long)path.Verbs.Length * passes;
        if (chordCost > _flattenBudget)
        {
            ReportOnce(SvgDiagnosticCode.LimitExceeded, "Rendering work budget exhausted; remaining shapes were not rendered.", n);
            return null;
        }
        double scale = Math.Min(1e30, _ctm.MaxScale() * _refScale);
        long cost = PathGeometry.FlattenCost(path, scale) * passes;
        if (cost > _flattenBudget)
        {
            ReportOnce(SvgDiagnosticCode.LimitExceeded, "Curve flattening budget exceeded; remaining curves were drawn as straight segments.", n);
            path = PathGeometry.ToChords(path);
            cost = chordCost;
        }
        _flattenBudget -= cost;
        return path;
    }

    /// <summary>当前用户空间中的矩形映射到参考渲染尺寸后是否仍在 ±<see cref="MaxReferenceExtent"/> 像素之内（NaN 视为超出）。</summary>
    bool InReferenceRange(Rect local)
    {
        var extent = local.TransformBounds(_ctm);
        double reach = Math.Max(Math.Max(Math.Abs((double)extent.Left), Math.Abs((double)extent.Right)),
            Math.Max(Math.Abs((double)extent.Top), Math.Abs((double)extent.Bottom))) * _refScale;
        return reach <= MaxReferenceExtent;
    }

    /// <summary>扣除一份不依赖路径的渲染工作量（如渐变色表）。预算不够时返回 false。</summary>
    bool SpendRenderWork(long cost)
    {
        if (cost > _flattenBudget) return false;
        _flattenBudget -= cost;
        return true;
    }

    bool Spend(int segments, SvgNode n)
    {
        _segmentBudget -= segments;
        if (_segmentBudget >= 0) return true;
        _segmentBudget = -1;
        ReportOnce(SvgDiagnosticCode.LimitExceeded, "Path segment limit exceeded; remaining shapes were dropped.", n);
        return false;
    }

    // ---------------------------------------------------------------- 画法与渐变

    RenderPaint? BuildPaint(SvgPaint paint, float opacity, ComputedStyle style, Rect bbox, SvgNode n)
    {
        bool aa = style.AntiAlias;
        switch (paint.Kind)
        {
            case PaintKind.None: return null;
            case PaintKind.Color: return Solid(paint.Color, opacity, aa);
            case PaintKind.CurrentColor: return CurrentColor(style, opacity, aa);
        }

        if (_ids.TryGetValue(paint.Url!, out var target))
        {
            if (target.Kind is SvgElementKind.LinearGradient or SvgElementKind.RadialGradient)
                return BuildGradient(target, opacity, bbox, aa, style);
            ReportOnce(SvgDiagnosticCode.UnsupportedFeature, "Paint server <" + target.Name + "> is not supported; the fallback was used.", n);
        }
        else
        {
            Report(SvgDiagnosticCode.MissingReference, "Paint reference '#" + Truncate(paint.Url!) + "' was not found.", n);
        }
        return paint.FallbackKind switch
        {
            PaintKind.Color => Solid(paint.FallbackColor, opacity, aa),
            PaintKind.CurrentColor => CurrentColor(style, opacity, aa),
            _ => null,
        };
    }

    static RenderPaint? Solid(Color c, float opacity, bool aa)
    {
        float a = c.A * opacity;
        if (!(a > 0)) return null;
        return new RenderPaint(new Paint(c.WithAlpha(Math.Min(1, a)), BlendMode.SrcOver, aa));
    }

    static RenderPaint? CurrentColor(ComputedStyle style, float opacity, bool aa)
    {
        if (style.CurrentColor is { } c) return Solid(c, opacity, aa);
        if (!(opacity > 0)) return null;
        return new RenderPaint(cc => new Paint(cc.WithAlpha(Math.Max(0, Math.Min(1, cc.A * opacity))), BlendMode.SrcOver, aa));
    }

    readonly struct StopInfo
    {
        public StopInfo(float offset, Color? color, float alpha) { Offset = offset; Color = color; Alpha = alpha; }
        public float Offset { get; }
        public Color? Color { get; }   // null 表示调用方的 currentColor
        public float Alpha { get; }
    }

    RenderPaint? BuildGradient(SvgNode gradient, float opacity, Rect bbox, bool aa, ComputedStyle refStyle)
    {
        // href 继承链
        var chain = new List<SvgNode>();
        for (var g = gradient; g != null;)
        {
            if (chain.Contains(g))
            {
                Report(SvgDiagnosticCode.CircularReference, "Gradient href chain is circular.", gradient);
                break;
            }
            if (chain.Count >= 16)
            {
                ReportOnce(SvgDiagnosticCode.LimitExceeded, "Gradient href chain is too long.", gradient);
                break;
            }
            chain.Add(g);
            var id = SvgRef.FragmentId(g.Href);
            if (id == null) break;
            if (!_ids.TryGetValue(id, out var next) || next.Kind is not (SvgElementKind.LinearGradient or SvgElementKind.RadialGradient))
            {
                Report(SvgDiagnosticCode.MissingReference, "Gradient href '#" + Truncate(id) + "' was not found.", g);
                break;
            }
            g = next;
        }

        bool linear = gradient.Kind == SvgElementKind.LinearGradient;
        string? Attr(string name, SvgElementKind? only = null)
        {
            foreach (var g in chain)
            {
                if (only != null && g.Kind != only) continue;
                var v = g.Attr(name);
                if (v != null) return v;
            }
            return null;
        }

        // 色标：链上第一个有 stop 子元素的渐变（每个渐变元素只扫描一次子元素，结果缓存）
        StopInfo[] own = Array.Empty<StopInfo>();
        foreach (var g in chain)
        {
            own = StopsOf(g);
            if (own.Length > 0) break;
        }
        if (own.Length == 0) return null;

        bool obb = !string.Equals(Attr("gradientUnits")?.Trim(), "userSpaceOnUse", StringComparison.Ordinal);
        if (obb && !(bbox.Width > 0 && bbox.Height > 0)) return null; // 规范：空包围盒时不渲染

        var gt = Matrix3x2.Identity;
        if (Attr("gradientTransform") is { } gtText && !SvgTransform.TryParse(gtText, out gt))
        {
            Report(SvgDiagnosticCode.InvalidValue, "Invalid gradientTransform was ignored.", gradient);
            gt = Matrix3x2.Identity;
        }
        var spread = (Attr("spreadMethod")?.Trim()) switch
        {
            "reflect" => SpreadMode.Reflect,
            "repeat" => SpreadMode.Repeat,
            _ => SpreadMode.Pad,
        };
        var matrix = obb ? gt * new Matrix3x2(bbox.Width, 0, 0, bbox.Height, bbox.X, bbox.Y) : gt;
        if (matrix.GetDeterminant() == 0 || !SvgTransform.IsFinite(matrix)) return null;

        // 每次实例化渐变画法都会在光栅化时建一张色表（约 色标数 × 表长 的工作量），计入渲染预算
        if (!SpendRenderWork((long)own.Length * GradientStopWork))
        {
            ReportOnce(SvgDiagnosticCode.LimitExceeded, "Rendering work budget exhausted; remaining gradients were drawn with their last stop color.", gradient);
            own = new[] { own[own.Length - 1] };
        }
        var stops = new List<StopInfo>(own.Length);
        foreach (var s in own) stops.Add(new StopInfo(s.Offset, s.Color, s.Alpha * opacity));

        float Coord(string name, string fallback, LengthAxis axis, SvgElementKind kind)
        {
            var text = Attr(name, kind) ?? fallback;
            if (obb)
                return SvgNumber.TryParseNumberOrPercent(text, out float f) ? f : Frac(fallback);
            return SvgLength.TryParse(text, out var len) ? len.Resolve(_viewport, axis, refStyle.FontSize) : 0;
        }
        static float Frac(string s) => SvgNumber.TryParseNumberOrPercent(s, out float f) ? f : 0;

        Func<Color, Paint> factory;
        bool single = stops.Count == 1;
        if (linear)
        {
            var k = SvgElementKind.LinearGradient;
            var p1 = new Point(Coord("x1", "0%", LengthAxis.X, k), Coord("y1", "0%", LengthAxis.Y, k));
            var p2 = new Point(Coord("x2", "100%", LengthAxis.X, k), Coord("y2", "0%", LengthAxis.Y, k));
            if (single || p1 == p2)
                factory = cc => SolidPaint(stops[stops.Count - 1], cc, aa);
            else
                factory = cc => new Paint(new LinearGradientBrush(p1, p2, Resolve(stops, cc), spread, matrix), BlendMode.SrcOver, aa);
        }
        else
        {
            var k = SvgElementKind.RadialGradient;
            float cx = Coord("cx", "50%", LengthAxis.X, k), cy = Coord("cy", "50%", LengthAxis.Y, k);
            float r = Coord("r", "50%", LengthAxis.Other, k);
            if (r < 0) return null;
            // 焦点（SVG 2）：fx / fy 缺省为圆心，fr 缺省为 0%；焦点圆按双点锥形渐变绘制（RadialGradientBrush 的焦点构造函数）
            var fxText = Attr("fx", k);
            var fyText = Attr("fy", k);
            float fx = fxText != null ? Coord("fx", "50%", LengthAxis.X, k) : cx;
            float fy = fyText != null ? Coord("fy", "50%", LengthAxis.Y, k) : cy;
            float fr = Attr("fr", k) != null ? Coord("fr", "0%", LengthAxis.Other, k) : 0;
            if (!(fr >= 0) || float.IsInfinity(fr))
            {
                Report(SvgDiagnosticCode.InvalidValue, "Negative or invalid focal radius 'fr' was treated as 0.", gradient);
                fr = 0;
            }
            if (!SvgNumber.IsFinite(fx) || !SvgNumber.IsFinite(fy)) { fx = cx; fy = cy; }
            if (_options.Svg11FocalPoint && r > 0)
            {
                // SVG 1.1：圆外的焦点移到圆心与焦点的连线和结束圆的交点上（略向内，避免焦点恰在圆上时的退化）
                double dx = fx - (double)cx, dy = fy - (double)cy, d = Math.Sqrt(dx * dx + dy * dy);
                double limit = r * 0.999;
                if (d > limit)
                {
                    fx = (float)(cx + dx * limit / d);
                    fy = (float)(cy + dy * limit / d);
                }
            }
            var center = new Point(cx, cy);
            var focal = new Point(fx, fy);
            bool hasFocus = fx != cx || fy != cy || fr > 0;
            if (single || r == 0)
                factory = cc => SolidPaint(stops[stops.Count - 1], cc, aa);
            else if (hasFocus)
                factory = cc => new Paint(new RadialGradientBrush(center, r, focal, fr, Resolve(stops, cc), spread, matrix), BlendMode.SrcOver, aa);
            else
                factory = cc => new Paint(new RadialGradientBrush(center, r, Resolve(stops, cc), spread, matrix), BlendMode.SrcOver, aa);
        }

        bool dynamic = false;
        foreach (var s in stops) if (s.Color == null) dynamic = true;
        if (dynamic) return new RenderPaint(factory);
        bool visible = false;
        foreach (var s in stops) if (s.Alpha > 0) visible = true;
        return visible ? new RenderPaint(factory(Color.Black)) : null;
    }

    /// <summary>渐变元素自身的 stop 子元素（不含画法透明度），每个元素只计算一次。超过 <see cref="MaxStops"/> 个时截断。</summary>
    StopInfo[] StopsOf(SvgNode g)
    {
        if (_stopCache.TryGetValue(g, out var cached)) return cached;
        var list = new List<StopInfo>();
        foreach (var c in g.Children)
        {
            if (c.Kind != SvgElementKind.Stop) continue;
            if (list.Count >= MaxStops)
            {
                ReportOnce(SvgDiagnosticCode.LimitExceeded, "Gradient has more than 256 stops; the rest were ignored.", g);
                break;
            }
            float offset = SvgNumber.TryParseNumberOrPercent(c.Attr("offset"), out float o) ? Math.Max(0, Math.Min(1, o)) : 0;
            var ss = OriginalStyle(c);
            Color? color = ss.StopColorIsCurrent ? ss.CurrentColor : ss.StopColor;
            float alpha = (color?.A ?? 1) * ss.StopOpacity;
            list.Add(new StopInfo(offset, color, alpha));
        }
        var result = list.ToArray();
        _stopCache[g] = result;
        return result;
    }

    static Paint SolidPaint(StopInfo s, Color current, bool aa)
    {
        var c = s.Color ?? current;
        float a = s.Color == null ? current.A * s.Alpha : s.Alpha;
        return new Paint(c.WithAlpha(Math.Max(0, Math.Min(1, a))), BlendMode.SrcOver, aa);
    }

    static GradientStop[] Resolve(List<StopInfo> stops, Color current)
    {
        var result = new GradientStop[stops.Count];
        for (int i = 0; i < result.Length; i++)
        {
            var s = stops[i];
            var c = s.Color ?? current;
            float a = s.Color == null ? current.A * s.Alpha : s.Alpha;
            result[i] = new GradientStop(s.Offset, c.WithAlpha(Math.Max(0, Math.Min(1, a))));
        }
        return result;
    }

    /// <summary>元素在原始文档位置上的计算样式（色标、clipPath 内容不从引用处继承）。</summary>
    ComputedStyle OriginalStyle(SvgNode n)
    {
        if (_originalStyles.TryGetValue(n, out var s)) return s;
        // 先收集未计算的祖先链，再自上而下计算，避免递归
        var chain = new List<SvgNode>();
        for (var p = n; p != null && !_originalStyles.ContainsKey(p); p = p.Parent) chain.Add(p);
        for (int i = chain.Count - 1; i >= 0; i--)
        {
            var node = chain[i];
            var parent = node.Parent == null ? ComputedStyle.Initial : _originalStyles[node.Parent];
            _originalStyles[node] = ComputedStyle.Compute(parent, node, _rootViewport, null);
        }
        return _originalStyles[n];
    }

    // ---------------------------------------------------------------- 裁剪

    /// <summary>
    /// clipPath 内容合并成的路径（clipPath 内容坐标系，尚未应用 clipPath 的 transform 与 objectBoundingBox 映射）。
    /// 只依赖 clipPath 元素本身与所在视口，按 (元素, 视口) 缓存：每次引用不必重新遍历子元素。
    /// </summary>
    sealed class ClipTemplate
    {
        public ClipTemplate(Size viewport, VectorPath? path, Matrix3x2 transform, bool boundingBoxUnits)
        {
            Viewport = viewport;
            Path = path;
            Transform = transform;
            BoundingBoxUnits = boundingBoxUnits;
        }

        public readonly Size Viewport;
        /// <summary>null 表示裁剪区域为空。</summary>
        public readonly VectorPath? Path;
        public readonly Matrix3x2 Transform;
        public readonly bool BoundingBoxUnits;
    }

    /// <summary>
    /// 把 clipPath 的子形状合并为一条路径（在引用元素的用户空间中）。单个子形状时精确使用其 clip-rule；
    /// 多个子形状时按非零规则拼接，相互重叠且方向相反时并集不精确（记录诊断）。
    /// <paramref name="clipAll"/> 为 true 表示裁剪区域为空（或付不起渲染预算），引用元素完全不可见。
    /// </summary>
    VectorPath? BuildClip(SvgNode clip, Rect bbox, out bool clipAll)
    {
        clipAll = false;
        var template = ClipTemplateOf(clip);
        if (template.Path == null) { clipAll = true; return null; }

        // objectBoundingBox：内容先映射到包围盒，再应用 clipPath 自身的 transform（与浏览器、resvg 一致）
        var m = template.Transform;
        if (template.BoundingBoxUnits)
        {
            if (!(bbox.Width > 0 && bbox.Height > 0)) { clipAll = true; return null; }
            m = new Matrix3x2(bbox.Width, 0, 0, bbox.Height, bbox.X, bbox.Y) * m;
        }
        var path = template.Path;
        // 范围检查与形状相同：远超可见范围的裁剪几何会让光栅器的浮点运算溢出
        if (!InReferenceRange(path.Bounds.TransformBounds(m)))
        {
            ReportOnce(SvgDiagnosticCode.LimitExceeded, "Shapes with coordinates or stroke widths far out of range were not rendered.", clip);
            clipAll = true;
            return null;
        }
        if (!m.IsIdentity)
        {
            // 变换要复制整条路径：先确认至少付得起按弦绘制的工作量，再分配
            if (path.Verbs.Length > _flattenBudget)
            {
                ReportOnce(SvgDiagnosticCode.LimitExceeded, "Rendering work budget exhausted; remaining shapes were not rendered.", clip);
                clipAll = true;
                return null;
            }
            var pb = new PathBuilder { FillRule = path.FillRule };
            PathGeometry.AppendTransformed(pb, path, m);
            path = pb.ToPath();
        }
        var spent = SpendFlattening(path, 1, clip);
        if (spent == null) clipAll = true;
        return spent;
    }

    ClipTemplate ClipTemplateOf(SvgNode clip)
    {
        if (_clipCache.TryGetValue(clip, out var cached) && cached.Viewport == _viewport) return cached;

        var cs = OriginalStyle(clip);
        var m = Matrix3x2.Identity;
        if (clip.Attr("transform") is { } t && !SvgTransform.TryParse(t, out m))
        {
            Report(SvgDiagnosticCode.InvalidValue, "Invalid transform on <clipPath> was ignored.", clip);
            m = Matrix3x2.Identity;
        }
        bool obb = string.Equals(clip.Attr("clipPathUnits")?.Trim(), "objectBoundingBox", StringComparison.Ordinal);
        if (cs.ClipPath != null)
            ReportOnce(SvgDiagnosticCode.UnsupportedFeature, "clip-path on <clipPath> is not supported and was ignored.", clip);

        var pb = new PathBuilder();
        int count = 0;
        var rule = FillRule.NonZero;
        bool anyEvenOdd = false;
        foreach (var child in clip.Children)
        {
            // 每个子元素至少计一段：模板按视口缓存，视口交替变化的引用会反复重建模板
            if (!Spend(1, child)) break;
            SvgNode shape = child;
            var childStyle = ComputedStyle.Compute(cs, child, _viewport, null);
            var cm = Matrix3x2.Identity;
            if (child.Kind == SvgElementKind.Use)
            {
                var id = SvgRef.FragmentId(child.Href);
                if (id == null || !_ids.TryGetValue(id, out var target) || !IsShape(target.Kind)) continue;
                if (child.Attr("transform") is { } ut && SvgTransform.TryParse(ut, out var um)) cm = um;
                float ux = Length(child, "x", LengthAxis.X, 0, childStyle), uy = Length(child, "y", LengthAxis.Y, 0, childStyle);
                cm = Matrix3x2.CreateTranslation(ux, uy) * cm;
                if (childStyle.DisplayNone) continue;
                childStyle = ComputedStyle.Compute(childStyle, target, _viewport, null);
                shape = target;
            }
            else if (child.Kind is SvgElementKind.Unsupported or SvgElementKind.Image)
            {
                ReportOnce(SvgDiagnosticCode.UnsupportedElement, "<" + child.Name + "> inside <clipPath> is not supported and was ignored.", child);
                continue;
            }
            else if (!IsShape(child.Kind)) continue;

            if (childStyle.DisplayNone || !childStyle.Visible) continue;
            if (shape.Attr("transform") is { } st && SvgTransform.TryParse(st, out var sm)) cm = sm * cm;
            var geom = Geometry(shape, childStyle);
            if (geom?.Path == null || geom.Path.IsEmpty) continue;
            if (!Spend(geom.Path.Verbs.Length, shape)) break;
            PathGeometry.AppendTransformed(pb, geom.Path, cm);
            if (childStyle.ClipRule == FillRule.EvenOdd) anyEvenOdd = true;
            rule = childStyle.ClipRule;
            count++;
        }
        if (count > 1)
        {
            if (anyEvenOdd)
                ReportOnce(SvgDiagnosticCode.UnsupportedFeature, "clip-rule=evenodd with several clip shapes is approximated by a nonzero union.", clip);
            rule = FillRule.NonZero;
        }
        pb.FillRule = rule;
        var path = count == 0 ? null : pb.ToPath();
        if (path is { IsEmpty: true }) path = null;
        var template = new ClipTemplate(_viewport, path, m, obb);
        _clipCache[clip] = template;
        return template;
    }

    static bool IsShape(SvgElementKind k) => k is SvgElementKind.Path or SvgElementKind.Rect or SvgElementKind.Circle or
        SvgElementKind.Ellipse or SvgElementKind.Line or SvgElementKind.Polyline or SvgElementKind.Polygon;

    // ---------------------------------------------------------------- 组与范围

    static RenderGroup MakeGroup(RenderNode[] children)
    {
        var g = new RenderGroup(children);
        Rect bbox = Rect.Empty;
        bool has = false;
        foreach (var c in children)
        {
            // 零宽或零高的包围盒（如水平线）也参与并集
            var b = c.LocalBBox.TransformBounds(c.Transform);
            bbox = has
                ? Rect.FromLTRB(Math.Min(bbox.Left, b.Left), Math.Min(bbox.Top, b.Top), Math.Max(bbox.Right, b.Right), Math.Max(bbox.Bottom, b.Bottom))
                : b;
            has = true;
        }
        g.LocalBBox = bbox;
        Finish(g);
        return g;
    }

    /// <summary>根据内容、裁剪与变换计算节点在父坐标系中的绘制范围。</summary>
    static void Finish(RenderNode r)
    {
        Rect content;
        if (r is RenderShape s)
        {
            content = s.Path.Bounds;
            if (s.StrokeStyle != null) content = content.Inflate(s.StrokeStyle.Outset);
        }
        else if (r is RenderImage img)
        {
            content = img.Destination;
        }
        else
        {
            content = Rect.Empty;
            foreach (var c in ((RenderGroup)r).Children) content = content.Union(c.Bounds);
        }
        if (r.ClipRect is { } cr) content = content.Intersect(cr);
        if (r.Clip != null) content = content.Intersect(r.Clip.Bounds);
        r.Bounds = content.TransformBounds(r.Transform);
    }

    // ---------------------------------------------------------------- 属性值

    float Length(SvgNode n, string attr, LengthAxis axis, float fallback, ComputedStyle style, float? percentDefault = null)
    {
        var text = n.Attr(attr);
        if (text == null)
            return percentDefault is { } p ? new SvgLength(p, SvgUnit.Percent).Resolve(_viewport, axis, style.FontSize) : fallback;
        if (SvgLength.TryParse(text, out var len)) return len.Resolve(_viewport, axis, style.FontSize);
        if (!text.Trim().Equals("auto", StringComparison.OrdinalIgnoreCase))
            Report(SvgDiagnosticCode.InvalidValue, "Invalid length '" + Truncate(text) + "' for '" + attr + "' was ignored.", n);
        return percentDefault is { } q ? new SvgLength(q, SvgUnit.Percent).Resolve(_viewport, axis, style.FontSize) : fallback;
    }

    float? OptionalLength(SvgNode n, string attr, LengthAxis axis, ComputedStyle style)
    {
        var text = n.Attr(attr);
        if (text == null || text.Trim().Equals("auto", StringComparison.OrdinalIgnoreCase)) return null;
        if (SvgLength.TryParse(text, out var len)) return len.Resolve(_viewport, axis, style.FontSize);
        Report(SvgDiagnosticCode.InvalidValue, "Invalid length '" + Truncate(text) + "' for '" + attr + "' was ignored.", n);
        return null;
    }

    float? OptionalNonNegative(SvgNode n, string attr, LengthAxis axis, ComputedStyle style)
    {
        var v = OptionalLength(n, attr, axis, style);
        if (v is < 0)
        {
            Report(SvgDiagnosticCode.InvalidValue, "Negative '" + attr + "' was ignored.", n);
            return null;
        }
        return v;
    }

    internal static bool TryParseViewBox(string text, out Rect viewBox, out bool disable)
    {
        viewBox = Rect.Empty;
        disable = false;
        var nums = SvgNumber.ParseList(text, out bool error, 5);
        if (error || nums.Length != 4) return false;
        if (nums[2] < 0 || nums[3] < 0) return false;
        if (nums[2] == 0 || nums[3] == 0) { disable = true; return true; }
        viewBox = new Rect(nums[0], nums[1], nums[2], nums[3]);
        return viewBox.IsFinite;
    }

    SvgPreserveAspectRatio ParseAspect(string? text, SvgNode n)
    {
        if (text == null) return SvgPreserveAspectRatio.Default;
        if (TryParseAspect(text, out var par)) return par;
        Report(SvgDiagnosticCode.InvalidValue, "Invalid preserveAspectRatio '" + Truncate(text) + "' was ignored.", n);
        return SvgPreserveAspectRatio.Default;
    }

    internal static bool TryParseAspect(string text, out SvgPreserveAspectRatio result)
    {
        result = SvgPreserveAspectRatio.Default;
        var parts = text.Split(new[] { ' ', '\t', '\r', '\n', ',' }, StringSplitOptions.RemoveEmptyEntries);
        int i = 0;
        if (parts.Length > 0 && parts[0] == "defer") i++;
        if (i >= parts.Length) return false;
        SvgAlign align;
        switch (parts[i++])
        {
            case "none": align = SvgAlign.None; break;
            case "xMinYMin": align = SvgAlign.XMinYMin; break;
            case "xMidYMin": align = SvgAlign.XMidYMin; break;
            case "xMaxYMin": align = SvgAlign.XMaxYMin; break;
            case "xMinYMid": align = SvgAlign.XMinYMid; break;
            case "xMidYMid": align = SvgAlign.XMidYMid; break;
            case "xMaxYMid": align = SvgAlign.XMaxYMid; break;
            case "xMinYMax": align = SvgAlign.XMinYMax; break;
            case "xMidYMax": align = SvgAlign.XMidYMax; break;
            case "xMaxYMax": align = SvgAlign.XMaxYMax; break;
            default: return false;
        }
        bool slice = false;
        if (i < parts.Length)
        {
            if (parts[i] == "slice") slice = true;
            else if (parts[i] != "meet") return false;
            i++;
        }
        if (i != parts.Length) return false;
        result = new SvgPreserveAspectRatio(align, slice);
        return true;
    }

    // ---------------------------------------------------------------- 诊断

    static string Truncate(string s) => s.Length > 40 ? s.Substring(0, 40) + "…" : s;

    void Report(SvgDiagnosticCode code, string message, SvgNode n) => _diag.Add(code, message, n.Offset);

    void ReportOnce(SvgDiagnosticCode code, string message, SvgNode n)
    {
        if (_reported.Add(message)) _diag.Add(code, message, n.Offset);
    }
}
