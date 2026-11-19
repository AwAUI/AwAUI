using System;
using System.Collections.Generic;
using AwAUI.Css;
using AwAUI.Css.Values;
using AwAUI.Graphics;
using AwAUI.Layout;
using AwAUI.Svg;
using AwAUI.Text;

namespace AwAUI.UI;

/// <summary>
/// 替换元素（<see cref="ImageElement"/>、<see cref="SvgElement"/>）的基类：内容不由子元素构成，尺寸来自内容的固有尺寸，
/// 绘制时内容画在内容盒内（裁剪到内容盒）。子节点不参与布局与绘制。
/// </summary>
public abstract class ReplacedElement : Element
{
    private protected ReplacedElement(string tag) : base(tag) { }

    internal readonly ReplacedMeasure Measure = new();

    /// <summary>布局阶段取得当前内容（必要时向图片缓存登记）。没有内容时返回 null。</summary>
    internal abstract ReplacedContent? GetContent(RenderPipeline pipeline);

    /// <summary>元素被移出树：释放占用的缓存项。</summary>
    internal virtual void ReleaseContent() { }

    /// <summary>内容变化（异步解码完成、动画换帧）：重新布局（固有尺寸可能变化）或只重绘。</summary>
    internal void OnContentChanged(bool layout) =>
        MarkDirty(layout ? ElementDirtyFlags.Layout | ElementDirtyFlags.Paint : ElementDirtyFlags.Paint);

    /// <summary>固有尺寸的提示（HTML 的 <c>width</c> / <c>height</c> 属性）：内容加载前也能占位，加载后覆盖对应的固有尺寸。</summary>
    internal float? HintWidth, HintHeight;
}

/// <summary>
/// 替换元素的叶子测量：返回固有尺寸（与 CSS 一致，<c>auto</c> 尺寸的图片不随容器收缩，会溢出）。
/// 使用的宽高比（CSS <c>aspect-ratio</c> 与固有宽高比按 <see cref="AspectRatioValue.Resolve"/> 合成）经
/// <see cref="LayoutStyle.AspectRatio"/> 交给布局引擎（一个轴由样式或 flex 确定时推出另一个轴）。
/// <c>aspect-ratio: &lt;ratio&gt;</c> 与固有宽高比不同、宽高都是 <c>auto</c> 时，与浏览器一样保留固有宽度、按该比值推出高度。
/// 长度形式的 min / max 宽高在测量中按 CSS 2.1 §10.4 保持宽高比地施加（见 <see cref="Measure"/>）；百分比形式的 min / max
/// 由引擎解析并跨宽高比传递。<see cref="RenderPipeline"/> 经 <see cref="LayoutMapper.ForReplaced"/> 设置
/// <see cref="LayoutStyle.IsReplaced"/>：块级替换元素不撑满包含块，绝对定位给出 left + right 时不拉伸，网格中 normal 对齐按 start。
/// </summary>
internal sealed class ReplacedMeasure : LayoutMeasure
{
    Size _natural;
    Size _default;

    /// <summary>使用的宽高比（CSS <c>aspect-ratio</c> 与固有宽高比合成）；没有时为 null。</summary>
    public float? AspectRatio { get; private set; }

    /// <summary>固有宽高比（内容或尺寸提示给出）；没有时为 null。</summary>
    public float? NaturalRatio { get; private set; }

    /// <summary>按内容、尺寸提示与 CSS <c>aspect-ratio</c> 更新固有尺寸与宽高比；返回是否变化。</summary>
    public bool Update(ReplacedContent? content, float? hintW, float? hintH, AspectRatioValue cssRatio = default)
    {
        // 负数、NaN、无穷的尺寸提示与 HTML 中无效的 width / height 属性一样忽略（而不是按 0 把图片压扁）
        if (hintW is float bw && !(bw >= 0 && bw <= float.MaxValue)) hintW = null;
        if (hintH is float bh && !(bh >= 0 && bh <= float.MaxValue)) hintH = null;
        var n = content?.NaturalSize ?? default;
        float w = Clean(n.Width), h = Clean(n.Height);
        // 占位（失败图标、替代文本）有尺寸但不是图片的固有宽高比（CSS aspect-ratio: auto w/h 此时用 w/h）
        float? ratio = w > 0 && h > 0 && content is not { SizeDefinesRatio: false } ? w / h : content?.NaturalRatio;
        if (hintW is float hw && hintH is float hh)
        {
            w = Clean(hw);
            h = Clean(hh);
            if (w > 0 && h > 0) ratio = w / h;
        }
        else if (hintW is float hw2)
        {
            w = Clean(hw2);
            h = ratio is float r && r > 0 ? w / r : h;
        }
        else if (hintH is float hh2)
        {
            h = Clean(hh2);
            w = ratio is float r && r > 0 ? h * r : w;
        }
        ratio = Sane(ratio);
        var natural = new Size(w, h);
        // 两个尺寸提示都给出时与浏览器一致（width / height 属性是两个确定的尺寸）：CSS aspect-ratio 不起作用，比值即提示的比值
        bool bothHints = hintW is not null && hintH is not null;
        var used = bothHints ? ratio : Sane(cssRatio.Resolve(ratio));
        // 首选宽高比与固有的不同（aspect-ratio: <ratio>）：保留固有宽度（没有时保留高度），另一边按首选比值
        var dflt = natural;
        if (used is float u && used != ratio)
        {
            if (w > 0) dflt = new Size(w, Clean(w / u));
            else if (h > 0) dflt = new Size(Clean(h * u), h);
        }
        bool changed = !natural.Equals(_natural) || !dflt.Equals(_default) || ratio != NaturalRatio || used != AspectRatio;
        _natural = natural;
        _default = dflt;
        NaturalRatio = ratio;
        AspectRatio = used;
        return changed;
    }

    static float? Sane(float? r) => r is float v && v > 0 && !float.IsInfinity(v) && v <= 1e6f && v >= 1e-6f ? v : null;

    static float Clean(float v) => float.IsNaN(v) || v < 0 ? 0 : Math.Min(v, 1e7f);

    /// <summary>固有尺寸（尺寸提示已覆盖，未按 CSS <c>aspect-ratio</c> 调整）。</summary>
    public Size NaturalSize => _natural;

    /// <summary>宽高都是 <c>auto</c> 时的尺寸（已按 CSS <c>aspect-ratio</c> 调整）。</summary>
    public Size DefaultSize => _default;

    /// <remarks>
    /// 宽高都是 <c>auto</c> 时按 CSS 2.1 §10.4 的表把默认尺寸放进 min / max 约束（保持宽高比：<c>max-height</c> 也会按比例缩小宽度），
    /// 只用不依赖包含块的约束（长度、不含百分比的 calc；<c>box-sizing: border-box</c> 时内边距也须是长度）。测量约束中的确定宽度 / 高度
    /// 不参与：它只是可用空间（替换元素不随它收缩）。百分比形式的 min / max、跨宽高比传递的限制以及由钳制后的宽度推出高度，
    /// 都由布局引擎按 <see cref="LayoutStyle.IsReplaced"/> 与 <see cref="LayoutStyle.AspectRatio"/> 处理。
    /// </remarks>
    public override Size Measure(LayoutNode node, in MeasureConstraints constraints) =>
        AspectRatio is not null ? Constrain(_default, node.Style) : _default;

    /// <summary>不依赖包含块的长度（px）；auto / none 与无法解析的为 NaN。</summary>
    static float Absolute(Dimension d) => d.Kind switch
    {
        DimensionKind.Length => d.Value,
        DimensionKind.Calc when d.Calc is { HasPercentage: false } c => c.Resolve(0),
        _ => float.NaN,
    };

    /// <summary>CSS 2.1 §10.4「宽高都是 auto 且有宽高比的替换元素」的约束表；尺寸为内容盒。</summary>
    static Size Constrain(Size size, LayoutStyle s)
    {
        float w = size.Width, h = size.Height;
        if (!(w > 0) || !(h > 0)) return size;
        float adjW = 0, adjH = 0;
        if (s.BoxSizing == BoxSizing.BorderBox)
        {
            var p = s.Padding;
            if (p.Left.Kind != DimensionKind.Length || p.Right.Kind != DimensionKind.Length
                || p.Top.Kind != DimensionKind.Length || p.Bottom.Kind != DimensionKind.Length)
                return size;
            adjW = Math.Max(0, p.Left.Value) + Math.Max(0, p.Right.Value) + Math.Max(0, s.Border.Left) + Math.Max(0, s.Border.Right);
            adjH = Math.Max(0, p.Top.Value) + Math.Max(0, p.Bottom.Value) + Math.Max(0, s.Border.Top) + Math.Max(0, s.Border.Bottom);
        }
        static float Content(float v, float adj, float none) => float.IsNaN(v) || float.IsInfinity(v) ? none : Math.Max(0, v - adj);
        float minW = Content(Absolute(s.MinWidth), adjW, 0), maxW = Content(Absolute(s.MaxWidth), adjW, float.PositiveInfinity);
        float minH = Content(Absolute(s.MinHeight), adjH, 0), maxH = Content(Absolute(s.MaxHeight), adjH, float.PositiveInfinity);
        if (maxW < minW) maxW = minW;
        if (maxH < minH) maxH = minH;
        bool overW = w > maxW, underW = w < minW, overH = h > maxH, underH = h < minH;
        if (!(overW || underW || overH || underH)) return size;
        if (overW && overH)
        {
            if (maxW / w <= maxH / h) return new Size(maxW, Math.Max(minH, maxW * h / w));
            return new Size(Math.Max(minW, maxH * w / h), maxH);
        }
        if (underW && underH)
        {
            if (minW / w <= minH / h) return new Size(Math.Min(maxW, minH * w / h), minH);
            return new Size(minW, Math.Min(maxH, minW * h / w));
        }
        if (underW && overH) return new Size(minW, maxH);
        if (overW && underH) return new Size(maxW, minH);
        if (overW) return new Size(maxW, Math.Max(maxW * h / w, minH));
        if (underW) return new Size(minW, Math.Min(minW * h / w, maxH));
        if (overH) return new Size(Math.Max(maxH * w / h, minW), maxH);
        return new Size(Math.Min(minH * w / h, maxW), minH);
    }

    /// <summary>与浏览器一致：替换元素的基线是其外边距盒底边（由引擎合成）。</summary>
    public override float GetBaseline(LayoutNode node, Size contentSize) => float.NaN;
}

/// <summary>替换元素的内容：固有尺寸与绘制。</summary>
internal abstract class ReplacedContent
{
    /// <summary>固有尺寸；没有时为 0。</summary>
    public abstract Size NaturalSize { get; }

    /// <summary>只有宽高比、没有固有尺寸时的宽高比（SVG 只有 viewBox）。</summary>
    public virtual float? NaturalRatio => null;

    /// <summary><see cref="NaturalSize"/> 是否同时给出固有宽高比；占位内容（失败图标、替代文本）为 false。</summary>
    public virtual bool SizeDefinesRatio => true;

    /// <summary>在内容盒 <paramref name="contentBox"/>（逻辑像素，已对齐设备像素）内绘制。</summary>
    public abstract void Paint(DrawingContext dc, Rect contentBox, ComputedStyle style, float scale);

    /// <summary>本轮绘制中可见（录制或只遍历都会调用）。</summary>
    public virtual void NotePainted() { }

    /// <summary>
    /// 开始绘制替换内容前施加内容裁剪，返回是否保存了状态（之后要 <see cref="DrawingContext.Restore"/>）。
    /// 元素有 <c>border-radius</c> 时按内容盒的圆角形状裁剪（与浏览器一致：替换内容裁到内容边缘的曲线，
    /// 半径 = 外缘半径 − 边框 − 内边距，不小于 0）；否则只在 <paramref name="needRect"/> 时裁剪到内容盒。
    /// </summary>
    protected static bool BeginContentClip(DrawingContext dc, Rect contentBox, ComputedStyle style, Element? owner, bool needRect)
    {
        if (ContentShape(contentBox, style, owner) is { } shape)
        {
            dc.Save();
            dc.ClipRoundRect(shape);
            return true;
        }
        if (!needRect) return false;
        dc.Save();
        dc.ClipRect(contentBox);
        return true;
    }

    /// <summary>目标矩形 <paramref name="dst"/> 是否超出内容盒（<c>cover</c>、<c>none</c>、偏移的位置）。</summary>
    protected static bool Overflows(Rect dst, Rect contentBox) =>
        dst.Left < contentBox.Left || dst.Top < contentBox.Top || dst.Right > contentBox.Right || dst.Bottom > contentBox.Bottom;

    /// <summary>内容盒的圆角形状；元素没有圆角（或圆角被边框与内边距完全吃掉）时为 null。</summary>
    internal static RoundRect? ContentShape(Rect contentBox, ComputedStyle style, Element? owner)
    {
        if (owner?.Render is not { HasBox: true } b) return null;
        var border = b.Border;
        if (!(border.Width > 0) || !(border.Height > 0) || !(contentBox.Width > 0) || !(contentBox.Height > 0)) return null;
        var outer = BoxPainter.OuterShape(style, border);
        if (outer.IsRect) return null;
        // 各边的「边框 + 内边距」= 内容盒与边框盒（都已对齐设备像素）之差
        float l = Math.Max(0, contentBox.Left - border.Left), t = Math.Max(0, contentBox.Top - border.Top);
        float r = Math.Max(0, border.Right - contentBox.Right), btm = Math.Max(0, border.Bottom - contentBox.Bottom);
        static Size Shrink(Size s, float w, float h) => new(Math.Max(0, s.Width - w), Math.Max(0, s.Height - h));
        var shape = new RoundRect(contentBox, Shrink(outer.TopLeft, l, t), Shrink(outer.TopRight, r, t),
            Shrink(outer.BottomRight, r, btm), Shrink(outer.BottomLeft, l, btm));
        return shape.IsRect ? null : shape;
    }

    /// <summary>元素子树被移出树：释放其中替换元素占用的缓存项（显式栈）。</summary>
    internal static void ReleaseSubtree(Element top)
    {
        if (top is ReplacedElement r0 && top.ChildCount == 0)
        {
            r0.ReleaseContent();
            return;
        }
        var stack = new Stack<Element>();
        stack.Push(top);
        while (stack.Count > 0)
        {
            var e = stack.Pop();
            if (e is ReplacedElement r) r.ReleaseContent();
            var ch = e.Children;
            for (int i = 0; i < ch.Count; i++) stack.Push(ch[i]);
        }
    }

    /// <summary>
    /// 把 SVG 文档画到目标矩形：按尺寸（与 currentColor，只在文档用到它时）缓存录制结果，命中时只合成。
    /// </summary>
    internal sealed class SvgPicture
    {
        DisplayList? _list;
        SvgDocument? _doc;
        float _w, _h;
        Color _color;

        /// <summary>重新录制的次数（测试与诊断）。</summary>
        public int Recordings { get; private set; }

        public DisplayList Get(SvgDocument doc, float width, float height, Color currentColor)
        {
            bool colorMatters = doc.UsesCurrentColor;
            if (_list is not null && ReferenceEquals(_doc, doc) && _w == width && _h == height && (!colorMatters || _color.Equals(currentColor)))
                return _list;
            _list = doc.ToDisplayList(new Rect(0, 0, width, height), new SvgRenderOptions { CurrentColor = currentColor });
            _doc = doc;
            _w = width;
            _h = height;
            _color = currentColor;
            Recordings++;
            return _list;
        }

        public void Draw(DrawingContext dc, SvgDocument doc, Rect destination, Color currentColor)
        {
            if (!(destination.Width > 0) || !(destination.Height > 0)) return;
            var list = Get(doc, destination.Width, destination.Height, currentColor);
            if (list.IsEmpty) return;
            dc.Save();
            dc.Translate(destination.X, destination.Y);
            dc.DrawDisplayList(list);
            dc.Restore();
        }
    }
}

// ============================================================ img

/// <summary>
/// <c>img</c> 元素：显示 <see cref="ImageSource"/>（位图、动画或 SVG）。内容按计算样式的 <c>object-fit</c> / <c>object-position</c>
/// 放进内容盒、按 <c>image-rendering</c> 采样，<c>aspect-ratio</c> 与固有宽高比一起参与布局；这些都只经 CSS 设置（与 HTML 相同）。
/// <see cref="Alt"/> 是无障碍名称。
/// <para>
/// 没有图片可画时（与浏览器相同）：加载中什么也不画（没有固有尺寸，只有 <c>width</c> / <c>height</c> 提示占位）；
/// 加载失败时画「图片损坏」图标（16×16）并在其右侧画替代文本（元素的字体与颜色，可换行，裁剪到内容盒），
/// 固有尺寸为图标 + 间距 + 单行文本的宽度、二者中较高者的高度；<c>alt=""</c>（装饰性图片）失败时什么也不画、尺寸为 0；
/// 没有来源但有替代文本时只画文本（没有图标）。
/// </para>
/// </summary>
public sealed class ImageElement : ReplacedElement
{
    ImageEntry? _entry;
    ImageContentView? _view;
    AltTextView? _altOnly;

    public ImageElement() : base("img") { }

    public ImageSource? Source { get; private set; }

    /// <summary>替代文本，即元素的无障碍名称（<c>aria-label</c> 优先；没有替代文本的图片在无障碍树中应视为装饰）。</summary>
    public string? Alt { get; private set; }

    /// <summary>无障碍名称：<c>aria-label</c>，没有时为 <see cref="Alt"/>。</summary>
    public string? AccessibleName => AriaLabel ?? Alt;

    /// <summary>当前图片的加载状态；没有来源或尚未布局时为 null。</summary>
    public ImageLoadState? LoadState => _entry?.State;

    /// <summary>当前显示的内容（解码完成后）；动画的 <see cref="ImageContent.Bitmap"/> 是当前帧。</summary>
    public ImageContent? Content => _entry?.Content;

    /// <summary>动画当前帧的序号（静态图片为 0）。</summary>
    public int CurrentFrame => _entry?.FrameIndex ?? 0;

    protected override string? GetReflectedAttribute(string name) => name switch
    {
        "src" => Source?.ToString(),
        "alt" => Alt,
        _ => base.GetReflectedAttribute(name),
    };

    internal void SetSource(ImageSource? source)
    {
        if (Source == source) return;
        Source = source;
        // 固有尺寸可能变化；旧缓存项在下一次布局取内容时释放
        PropertyChanged("src", ElementDirtyFlags.Style | ElementDirtyFlags.Layout | ElementDirtyFlags.Paint);
    }

    internal void SetAlt(string? alt)
    {
        if (string.Equals(Alt, alt, StringComparison.Ordinal)) return;
        Alt = alt;
        // 替代文本只在显示占位时（没有来源，或加载失败）影响尺寸与绘制
        var flags = ElementDirtyFlags.Style | ElementDirtyFlags.Semantics;
        if (Source is null || _entry?.State == ImageLoadState.Failed) flags |= ElementDirtyFlags.Layout | ElementDirtyFlags.Paint;
        PropertyChanged("alt", flags);
    }

    internal void SetSizeHints(float? width, float? height)
    {
        if (HintWidth == width && HintHeight == height) return;
        HintWidth = width;
        HintHeight = height;
        MarkDirty(ElementDirtyFlags.Layout | ElementDirtyFlags.Paint);
    }

    internal override ReplacedContent? GetContent(RenderPipeline pipeline)
    {
        var root = Root;
        if (_entry is not null && (root is null || _entry.Released || !Equals(_entry.Source, Source) || !ReferenceEquals(_entry.Cache, root.Images)))
            ReleaseContent();
        if (_entry is null && Source is not null && root is not null)
        {
            _entry = root.Images.Acquire(Source, this);
            _view = new ImageContentView(this, _entry);
        }
        if (_view is not null)
        {
            _altOnly = null;
            _view.Fallback.Update(pipeline.TextEngine, _entry!.State == ImageLoadState.Failed ? FallbackKind.Broken : FallbackKind.None, Alt, Render?.Style);
            return _view;
        }
        // 没有来源：有替代文本时显示文本（与浏览器的 <img alt="…"> 相同）
        if (Source is null && !string.IsNullOrEmpty(Alt))
        {
            _altOnly ??= new AltTextView(this);
            _altOnly.Fallback.Update(pipeline.TextEngine, FallbackKind.AltOnly, Alt, Render?.Style);
            return _altOnly;
        }
        _altOnly = null;
        return null;
    }

    /// <summary>加载失败时是否显示了占位（图标或替代文本）。测试与诊断用。</summary>
    internal bool ShowsFallback => (_view?.Fallback ?? _altOnly?.Fallback)?.Kind is FallbackKind.Broken or FallbackKind.AltOnly;

    internal override void ReleaseContent()
    {
        var entry = _entry;
        _entry = null;
        _view = null;
        if (entry is not null && !entry.Released) entry.Cache.Release(entry, this);
    }

    /// <summary>图片元素的内容视图：绘制位图（或作为图片的 SVG），并向缓存报告可见性。</summary>
    sealed class ImageContentView : ReplacedContent
    {
        readonly ImageElement _owner;
        readonly ImageEntry _entry;
        readonly SvgPicture _svg = new();

        /// <summary>加载失败时的占位（图标与替代文本）。</summary>
        public readonly ImageFallback Fallback = new();

        public ImageContentView(ImageElement owner, ImageEntry entry)
        {
            _owner = owner;
            _entry = entry;
        }

        public override Size NaturalSize => _entry.Content?.NaturalSize ?? Fallback.Size;

        public override bool SizeDefinesRatio => _entry.Content is not null;

        public override float? NaturalRatio =>
            _entry.Content?.Vector is { HasViewBox: true } v && v.ViewBox.Width > 0 && v.ViewBox.Height > 0 ? v.ViewBox.Width / v.ViewBox.Height : null;

        public override void NotePainted()
        {
            if (!_entry.Released) _entry.Cache.NotePainted(_entry);
        }

        public override void Paint(DrawingContext dc, Rect contentBox, ComputedStyle style, float scale)
        {
            var content = _entry.Content;
            if (content is null)
            {
                Fallback.Paint(dc, contentBox, style, _owner);
                return;
            }
            // 放置按内容自身的固有尺寸（HTML 的 width / height 是尺寸而不是固有尺寸：<img width=100 height=50> 的 contain 仍按图片的宽高比）
            var dst = ObjectFitting.Place(style.ObjectFit, style.ObjectPosition, content.NaturalSize, NaturalRatio, contentBox);
            if (!(dst.Width > 0) || !(dst.Height > 0)) return;
            if (content.Vector is { } doc)
            {
                bool saved = BeginContentClip(dc, contentBox, style, _owner, Overflows(dst, contentBox));
                _svg.Draw(dc, doc, dst, Color.Black);
                if (saved) dc.Restore();
                return;
            }
            if (content.Bitmap is not { } bmp) return;
            dst = ObjectFitting.SnapToDevice(dst, scale);
            // 位图只画落在内容盒内的部分（源矩形随之裁小）：超出内容盒的 cover / none 不需要矩形裁剪，只有圆角时才裁剪
            if (!ObjectFitting.Crop(dst, contentBox, bmp.Width, bmp.Height, out var src, out var visible)) return;
            bool clipped = BeginContentClip(dc, contentBox, style, _owner, needRect: false);
            ImagePainter.DrawBitmap(dc, bmp, src, visible, style.ImageRendering);
            if (clipped) dc.Restore();
        }
    }
}

/// <summary>没有来源、只有替代文本的图片。</summary>
internal sealed class AltTextView : ReplacedContent
{
    readonly ImageElement _owner;

    public AltTextView(ImageElement owner) => _owner = owner;

    public readonly ImageFallback Fallback = new();

    public override Size NaturalSize => Fallback.Size;

    public override bool SizeDefinesRatio => false;

    public override void Paint(DrawingContext dc, Rect contentBox, ComputedStyle style, float scale) =>
        Fallback.Paint(dc, contentBox, style, _owner);
}

internal enum FallbackKind : byte
{
    /// <summary>不显示占位（加载中，或已加载）。</summary>
    None,

    /// <summary>加载失败：图标 + 替代文本。</summary>
    Broken,

    /// <summary>没有来源：只有替代文本。</summary>
    AltOnly,
}

/// <summary>
/// 图片的占位：「图片损坏」图标与替代文本（见 <see cref="ImageElement"/>）。替代文本用元素的计算样式排版（字体、颜色、行高、
/// 空白处理），段落在文本或样式变化时重建；只取前 <see cref="MaxAltLength"/> 个字符，排版与绘制的开销有界。
/// </summary>
internal sealed class ImageFallback
{
    /// <summary>图标边长（逻辑像素）。</summary>
    public const float IconSize = 16;

    /// <summary>图标与文本的间距。</summary>
    public const float Gap = 4;

    /// <summary>参与排版的替代文本的最大长度（字符）。</summary>
    public const int MaxAltLength = 1000;

    TextStyleMapper? _styles;
    TextEngine? _engine;
    TextLayout? _layout;
    string? _text;
    ComputedStyle? _style;

    public FallbackKind Kind { get; private set; }

    /// <summary>占位的固有尺寸（没有占位时为 0）。</summary>
    public Size Size { get; private set; }

    /// <summary>失败时画图标（<c>alt=""</c> 时整个占位都不画，见 <see cref="Update"/>）。</summary>
    bool HasIcon(string? alt) => Kind == FallbackKind.Broken && alt is not { Length: 0 };

    /// <summary>按状态、替代文本与计算样式更新占位与固有尺寸（布局阶段调用）。</summary>
    public void Update(TextEngine engine, FallbackKind kind, string? alt, ComputedStyle? style)
    {
        // alt=""：装饰性图片，失败时什么也不画
        Kind = alt is { Length: 0 } ? FallbackKind.None : kind;
        _engine = engine;
        if (Kind == FallbackKind.None)
        {
            _layout = null;
            _text = null;
            Size = default;
            return;
        }
        bool icon = HasIcon(alt);
        var layout = alt is { Length: > 0 } && style is not null ? GetLayout(alt, style) : null;
        float textW = 0, textH = 0;
        if (layout is not null)
        {
            layout.Layout(float.PositiveInfinity);
            textW = layout.MaxIntrinsicWidth;
            textH = layout.Height;
        }
        if (icon)
            Size = layout is null ? new Size(IconSize, IconSize) : new Size(IconSize + Gap + textW, Math.Max(IconSize, textH));
        else
            Size = new Size(textW, textH);
    }

    TextLayout GetLayout(string alt, ComputedStyle style)
    {
        string text = alt.Length > MaxAltLength ? alt.Substring(0, MaxAltLength) : alt;
        if (_layout is not null && ReferenceEquals(_style, style) && string.Equals(_text, text, StringComparison.Ordinal)) return _layout;
        _styles ??= new TextStyleMapper();
        var ts = _styles.Get(style);
        var builder = _engine!.CreateParagraphBuilder(_styles.CreateParagraphStyle(style, ts, singleLine: false));
        builder.AddText(text);
        _layout = builder.Build();
        _text = text;
        _style = style;
        return _layout;
    }

    /// <summary>在内容盒内绘制（裁剪到内容盒）：图标在左上角，替代文本在其右侧，按剩余宽度换行。</summary>
    public void Paint(DrawingContext dc, Rect contentBox, ComputedStyle style, Element owner)
    {
        if (Kind == FallbackKind.None || _engine is null || !(contentBox.Width > 0) || !(contentBox.Height > 0)) return;
        string? alt = owner is ImageElement img ? img.Alt : null;
        if (alt is { Length: 0 }) return;
        bool icon = HasIcon(alt);
        var layout = alt is { Length: > 0 } ? GetLayout(alt, style) : null;
        if (!icon && layout is null) return;
        dc.Save();
        if (ReplacedContent.ContentShape(contentBox, style, owner) is { } shape) dc.ClipRoundRect(shape);
        else dc.ClipRect(contentBox);
        float x = contentBox.X;
        if (icon)
        {
            DrawBrokenIcon(dc, new Point(contentBox.X, contentBox.Y));
            x += IconSize + Gap;
        }
        if (layout is not null && contentBox.Right - x > 0)
        {
            layout.Layout(contentBox.Right - x + TextParagraph.WidthSlack);
            layout.Paint(dc, new Point(x, contentBox.Y));
        }
        dc.Restore();
    }

    static readonly Color Frame = Color.FromRgb(0x8A, 0x90, 0x99);
    static readonly Color Fill = Color.FromRgb(0xE8, 0xEA, 0xED);
    static readonly Color Tear = Color.FromRgb(0xD9, 0x30, 0x25);
    static VectorPath? s_mountain, s_tear;

    /// <summary>「图片损坏」图标：灰色相框、山与太阳，一道红色裂痕（矢量，任何缩放下都清晰）。</summary>
    internal static void DrawBrokenIcon(DrawingContext dc, Point origin)
    {
        dc.Save();
        dc.Translate(origin.X, origin.Y);
        var box = new RoundRect(new Rect(1.5f, 1.5f, 13, 13), 2);
        dc.FillRoundRect(box, Fill);
        dc.StrokeRoundRect(box, Frame, new Stroke(1));
        s_mountain ??= new PathBuilder().MoveTo(3, 12.5f).LineTo(6.5f, 8).LineTo(9, 10.5f).LineTo(10.5f, 9).LineTo(13, 12.5f).Close().ToPath();
        dc.FillPath(s_mountain, Frame);
        dc.FillCircle(new Point(10.5f, 5.5f), 1.5f, Frame);
        s_tear ??= new PathBuilder().MoveTo(4, 1).LineTo(7, 6).LineTo(5, 9).LineTo(9, 15).ToPath();
        dc.StrokePath(s_tear, Tear, new Stroke(1.5f, StrokeCap.Round, StrokeJoin.Round));
        dc.Restore();
    }
}

// ============================================================ svg

/// <summary>
/// <c>svg</c> 元素：显示一个 <see cref="SvgDocument"/>（图标、插图）。<c>currentColor</c> 取元素的计算 <c>color</c>（图标随文字颜色着色），
/// 录制结果按尺寸缓存（文档用到 currentColor 时颜色也是键的一部分）。
/// 与 <c>&lt;img src="x.svg"&gt;</c> 一致：先按 CSS <c>object-fit</c> / <c>object-position</c> 求出对象矩形（初始值 <c>fill</c> 即整个内容盒），
/// 文档再按自身的 <c>preserveAspectRatio</c> 画进该矩形；<c>aspect-ratio</c> 参与布局，<c>image-rendering</c> 对矢量内容没有作用。
/// </summary>
public sealed class SvgElement : ReplacedElement
{
    SvgContentView? _view;

    public SvgElement() : base("svg") { }

    public SvgDocument? Document { get; private set; }

    /// <summary>重新录制 SVG 的次数（缓存失效的诊断；颜色只在文档用到 currentColor 时参与缓存键）。</summary>
    public int Recordings => _view?.Picture.Recordings ?? 0;

    internal void SetDocument(SvgDocument? document)
    {
        if (ReferenceEquals(Document, document)) return;
        Document = document;
        _view = document is null ? null : new SvgContentView(document) { Owner = this };
        PropertyChanged("src", ElementDirtyFlags.Layout | ElementDirtyFlags.Paint);
    }

    internal void SetSizeHints(float? width, float? height)
    {
        if (HintWidth == width && HintHeight == height) return;
        HintWidth = width;
        HintHeight = height;
        MarkDirty(ElementDirtyFlags.Layout | ElementDirtyFlags.Paint);
    }

    internal override ReplacedContent? GetContent(RenderPipeline pipeline) => _view;

    sealed class SvgContentView : ReplacedContent
    {
        readonly SvgDocument _doc;

        public SvgContentView(SvgDocument doc) => _doc = doc;

        public readonly SvgPicture Picture = new();

        public override Size NaturalSize => _doc.IntrinsicSize;

        public override float? NaturalRatio =>
            _doc.HasViewBox && _doc.ViewBox.Width > 0 && _doc.ViewBox.Height > 0 ? _doc.ViewBox.Width / _doc.ViewBox.Height : null;

        public override void Paint(DrawingContext dc, Rect contentBox, ComputedStyle style, float scale)
        {
            // 尺寸提示（Svg.Width / Height）覆盖文档的固有尺寸
            var natural = Owner?.Measure.NaturalSize ?? _doc.IntrinsicSize;
            var dst = ObjectFitting.Place(style.ObjectFit, style.ObjectPosition, natural, NaturalRatio, contentBox);
            if (!(dst.Width > 0) || !(dst.Height > 0)) return;
            var color = style.ResolveColor(style.Color);
            bool saved = BeginContentClip(dc, contentBox, style, Owner, Overflows(dst, contentBox));
            Picture.Draw(dc, _doc, dst, color);
            if (saved) dc.Restore();
        }

        /// <summary>所属元素（尺寸提示覆盖文档的固有尺寸）。</summary>
        public SvgElement? Owner;
    }
}

/// <summary>
/// 位图绘制的唯一出口：<see cref="DrawingContext.DrawImage(Bitmap, Rect, Rect, ImageSampling, float)"/>（design/13，操作码 34）。
/// <c>image-rendering</c> 的 <see cref="CssImageRendering.Pixelated"/> / <see cref="CssImageRendering.CrispEdges"/> 映射为最近邻采样，
/// 其余（<c>auto</c>、<c>smooth</c>、<c>high-quality</c>）为双线性 —— 渲染器的双线性在大比例缩小时自动改用盒式预滤波的 mip 级别，
/// 已是高质量缩放，没有单独的高质量模式。元素的 <c>opacity</c> 照旧由层叠上下文的图层施加，这里不透明度总是 1。
/// <para>
/// 位图以引用进入 DisplayList，像素在光栅化时读取：动画换帧在同一张位图上改写后由 <see cref="ImageCache"/> 调用 <see cref="Bitmap.Invalidate"/>，
/// GPU 纹理缓存据此更新；位图在最后一个使用者卸载后的下一次绘制之后才释放，仍可能被光栅化的帧不会引用已释放的位图。
/// </para>
/// </summary>
internal static class ImagePainter
{
    /// <summary>本进程中请求绘制位图的次数（测试用来确认图片确实走到了绘制这一步）。</summary>
    internal static long DrawRequests;

    /// <summary><c>image-rendering</c> → 采样方式。</summary>
    public static ImageSampling SamplingFor(CssImageRendering rendering) =>
        rendering is CssImageRendering.Pixelated or CssImageRendering.CrispEdges ? ImageSampling.Nearest : ImageSampling.Linear;

    public static void DrawBitmap(DrawingContext dc, Bitmap bitmap, Rect source, Rect destination, CssImageRendering rendering)
    {
        System.Threading.Interlocked.Increment(ref DrawRequests);
        dc.DrawImage(bitmap, source, destination, SamplingFor(rendering));
    }
}
