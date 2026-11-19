using System;
using System.Collections.Generic;
using AwAUI.Graphics;
using AwAUI.Svg.Model;
using AwAUI.Svg.Rendering;
using AwAUI.Svg.Values;
using AwAUI.Svg.Xml;

namespace AwAUI.Svg;

/// <summary>
/// 解析后的静态 SVG 文档（图标与插画）。不可变、可跨线程共享与绘制。
/// 支持的子集：svg / g / a / switch / defs / symbol / use，path / rect / circle / ellipse / line / polyline / polygon，
/// 线性与径向渐变（含焦点 fx / fy / fr）、clipPath、透明度、填充规则、全部描边属性、transform、表现属性 + style="" + 最小 &lt;style&gt;、
/// currentColor 着色，image（位图经 <see cref="SvgParseOptions.ImageResolver"/> 解码，见 <see cref="SvgImageResolver"/>）。
/// 文本、滤镜、蒙版、图案、标记不支持，会记录在 <see cref="Diagnostics"/> 中。
/// </summary>
public sealed class SvgDocument
{
    readonly RenderNode? _root;

    SvgDocument(Size intrinsicSize, Rect viewBox, bool hasViewBox, SvgPreserveAspectRatio aspect, RenderNode? root,
        SvgDiagnostic[] diagnostics)
    {
        IntrinsicSize = intrinsicSize;
        ViewBox = viewBox;
        HasViewBox = hasViewBox;
        PreserveAspectRatio = aspect;
        _root = root;
        Diagnostics = diagnostics;
        UsesCurrentColor = root?.DependsOnCurrentColor ?? false;
    }

    /// <summary>
    /// 固有尺寸（用户单位 = 逻辑像素）：width/height 属性；只有一个时按 viewBox 宽高比推出另一个；
    /// 都没有时取 viewBox 的尺寸；再没有时为 300×150。
    /// </summary>
    public Size IntrinsicSize { get; }

    /// <summary>根元素的 viewBox；没有 viewBox 时为 (0, 0, IntrinsicSize)，即按固有尺寸缩放到目标矩形。</summary>
    public Rect ViewBox { get; }

    /// <summary>根元素是否声明了有效的 viewBox。</summary>
    public bool HasViewBox { get; }

    /// <summary>根元素的 preserveAspectRatio。</summary>
    public SvgPreserveAspectRatio PreserveAspectRatio { get; }

    /// <summary>解析与构建中的非致命问题（不支持的特性、无效值、被截断的内容等）。</summary>
    public IReadOnlyList<SvgDiagnostic> Diagnostics { get; }

    /// <summary>
    /// 绘制结果是否依赖 <see cref="SvgRenderOptions.CurrentColor"/>。为 false 时同一尺寸的 DisplayList 可以跨颜色复用。
    /// </summary>
    public bool UsesCurrentColor { get; }

    /// <summary>文档是否没有任何可见内容。</summary>
    public bool IsEmpty => _root == null;

    // ---------------------------------------------------------------- 解析

    /// <summary>解析 SVG 文本。</summary>
    /// <exception cref="ArgumentNullException"><paramref name="text"/> 为 null。</exception>
    /// <exception cref="SvgParseException">XML 不是良构的、根元素不是 svg，或超出长度 / 深度 / 元素数 / 属性数上限。</exception>
    public static SvgDocument Parse(string text, SvgParseOptions? options = null)
    {
        if (text is null) throw new ArgumentNullException(nameof(text));
        options ??= SvgParseOptions.Default;
        if (text.Length > options.MaxInputLength)
            throw new SvgParseException($"The input is longer than {options.MaxInputLength} characters.", 0, 0);
        return ParseCore(text, options);
    }

    /// <summary>解析 SVG 字节（UTF-8；识别 UTF-8/UTF-16 BOM 与 XML 声明中的 Latin-1 编码）。</summary>
    /// <exception cref="SvgParseException">同 <see cref="Parse(string, SvgParseOptions)"/>。</exception>
    public static SvgDocument Parse(ReadOnlySpan<byte> data, SvgParseOptions? options = null)
    {
        options ??= SvgParseOptions.Default;
        if (data.Length > options.MaxInputLength)
            throw new SvgParseException($"The input is longer than {options.MaxInputLength} bytes.", 0, 0);
        return ParseCore(XmlEncoding.Decode(data), options);
    }

    /// <summary>解析失败时返回 false 与异常信息而不是抛出。</summary>
    public static bool TryParse(string text, out SvgDocument? document, out SvgParseException? error, SvgParseOptions? options = null)
    {
        try
        {
            document = Parse(text, options);
            error = null;
            return true;
        }
        catch (SvgParseException e)
        {
            document = null;
            error = e;
            return false;
        }
    }

    static SvgDocument ParseCore(string text, SvgParseOptions options)
    {
        var locator = new SourceLocator(text);
        var diag = new DiagnosticBag(locator, options.MaxDiagnostics);
        var xml = XmlLiteParser.Parse(text, options, diag, locator);
        var root = SvgTreeBuilder.Build(xml, options, diag, locator, out var ids, out _);

        // 固有尺寸与 viewBox
        Rect? viewBox = null;
        if (root.Attr("viewBox") is { } vbText)
        {
            if (SvgRenderBuilder.TryParseViewBox(vbText, out var vb, out bool disable) && !disable) viewBox = vb;
            else diag.Add(SvgDiagnosticCode.InvalidValue, "Invalid root viewBox was ignored.", root.Offset);
        }
        var aspect = SvgPreserveAspectRatio.Default;
        if (root.Attr("preserveAspectRatio") is { } parText && !SvgRenderBuilder.TryParseAspect(parText, out aspect))
        {
            diag.Add(SvgDiagnosticCode.InvalidValue, "Invalid root preserveAspectRatio was ignored.", root.Offset);
            aspect = SvgPreserveAspectRatio.Default;
        }
        float? w = AbsoluteLength(root.Attr("width")), h = AbsoluteLength(root.Attr("height"));
        Size size;
        if (w is { } ww && h is { } hh) size = new Size(ww, hh);
        else if (w is { } w1 && viewBox is { } v1) size = new Size(w1, w1 * v1.Height / v1.Width);
        else if (h is { } h1 && viewBox is { } v2) size = new Size(h1 * v2.Width / v2.Height, h1);
        else if (viewBox is { } v3) size = v3.Size;
        else size = new Size(w ?? 300, h ?? 150);
        if (!(size.Width >= 0) || !SvgNumber.IsFinite(size.Width)) size = new Size(0, size.Height);
        if (!(size.Height >= 0) || !SvgNumber.IsFinite(size.Height)) size = new Size(size.Width, 0);

        var effectiveViewBox = viewBox ?? new Rect(0, 0, size.Width, size.Height);
        var builder = new SvgRenderBuilder(ids, options, diag, effectiveViewBox.Size);
        var tree = size.IsEmpty ? null : builder.BuildRoot(root);
        return new SvgDocument(size, effectiveViewBox, viewBox.HasValue, aspect, tree, diag.ToArray());
    }

    static float? AbsoluteLength(string? text)
    {
        if (text == null || !SvgLength.TryParse(text, out var len) || len.IsPercent || len.Value < 0) return null;
        return len.Resolve(default, LengthAxis.Other, 16);
    }

    // ---------------------------------------------------------------- 绘制

    /// <summary>
    /// 把文档绘制到 <paramref name="destination"/>：viewBox 按 preserveAspectRatio 映射到目标矩形，
    /// 默认裁剪到目标矩形。目标为空或非有限值时不绘制。
    /// </summary>
    public void Draw(DrawingContext context, Rect destination, SvgRenderOptions? options = null)
    {
        if (context is null) throw new ArgumentNullException(nameof(context));
        options ??= SvgRenderOptions.Default;
        if (_root == null || destination.IsEmpty || !destination.IsFinite) return;
        float opacity = options.Opacity;
        if (!(opacity > 0)) return;
        var aspect = options.PreserveAspectRatio ?? PreserveAspectRatio;
        var m = aspect.GetTransform(ViewBox, destination);

        bool layer = opacity < 1;
        if (layer) context.PushLayer(new LayerOptions(opacity, BlendMode.SrcOver, destination));
        else context.Save();
        if (options.ClipToViewport) context.ClipRect(destination);
        context.Transform(m);
        _root.Emit(context, options.CurrentColor);
        if (layer) context.PopLayer();
        else context.Restore();
    }

    /// <summary>录制为 <see cref="DisplayList"/>（可缓存；<see cref="UsesCurrentColor"/> 为 true 时缓存键需含颜色）。</summary>
    public DisplayList ToDisplayList(Rect destination, SvgRenderOptions? options = null)
    {
        using var dc = new DrawingContext();
        Draw(dc, destination, options);
        return dc.Finish();
    }

    /// <summary>按固有尺寸录制，原点在 (0, 0)。</summary>
    public DisplayList ToDisplayList(SvgRenderOptions? options = null) =>
        ToDisplayList(new Rect(0, 0, IntrinsicSize.Width, IntrinsicSize.Height), options);
}
