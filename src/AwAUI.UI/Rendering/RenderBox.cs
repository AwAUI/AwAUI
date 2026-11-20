using AwAUI.Css;
using AwAUI.Layout;

namespace AwAUI.UI;

/// <summary>元素在帧管线中的角色：决定它在布局树中如何出现。</summary>
internal enum BoxKind : byte
{
    /// <summary>普通盒（div、button ...），子元素各自成盒。</summary>
    Container,

    /// <summary>段落叶子：<c>text</c> 元素、<c>input</c>、文本容器之外的 <c>span</c>；子节点并入同一个段落，不单独布局。</summary>
    Paragraph,

    /// <summary>容器中的裸文本节点：匿名段落叶子，样式取父元素。</summary>
    AnonymousText,

    /// <summary>段落内部的元素或文本节点（span、文本）：没有自己的布局盒。</summary>
    Inline,

    /// <summary>替换元素（<c>img</c>、<c>svg</c>）：叶子，尺寸来自固有尺寸（<see cref="ReplacedContent"/>），子节点不参与布局与绘制。</summary>
    Replaced,
}

/// <summary>
/// 元素录制结果所依赖的局部几何（相对自身边框盒原点）。两帧之间不变时，元素的 DisplayList 只需在合成时平移，不必重录。
/// </summary>
internal readonly struct PaintGeometry : System.IEquatable<PaintGeometry>
{
    public PaintGeometry(float width, float height, float contentX, float contentY, float contentWidth, float contentHeight,
        float contentRight, float contentBottom, float fraction)
    {
        Width = width;
        Height = height;
        ContentX = contentX;
        ContentY = contentY;
        ContentWidth = contentWidth;
        ContentHeight = contentHeight;
        ContentRight = contentRight;
        ContentBottom = contentBottom;
        Fraction = fraction;
        Valid = true;
    }

    public readonly float Width, Height, ContentX, ContentY, ContentWidth, ContentHeight;

    /// <summary>对齐后的内容盒右、下边缘（相对边框盒原点；替换元素的内容矩形用它们）。</summary>
    public readonly float ContentRight, ContentBottom;

    /// <summary>
    /// 绘制结果还依赖的未对齐小数部分（输入框的文本垂直居中按未对齐的内容盒计算后再对齐）；其他元素为 0。
    /// </summary>
    public readonly float Fraction;

    /// <summary>为 false 表示「没有盒」（尚未布局或 display:none）。</summary>
    public readonly bool Valid;

    public bool Equals(PaintGeometry o) =>
        Valid == o.Valid && Width.Equals(o.Width) && Height.Equals(o.Height) && ContentX.Equals(o.ContentX) && ContentY.Equals(o.ContentY)
        && ContentWidth.Equals(o.ContentWidth) && ContentHeight.Equals(o.ContentHeight)
        && ContentRight.Equals(o.ContentRight) && ContentBottom.Equals(o.ContentBottom) && Fraction.Equals(o.Fraction);

    public override bool Equals(object? obj) => obj is PaintGeometry g && Equals(g);

    public override int GetHashCode() => HashCodeCompat.Combine(Width.GetHashCode(), Height.GetHashCode(), ContentX.GetHashCode(), ContentY.GetHashCode());
}

/// <summary>
/// 帧管线为一个元素保存的全部数据（design/05 元素表中的「ComputedStyle / 布局结果 / 绘制缓存」）：
/// 样式视图、计算样式、失效标志、布局节点、段落与绘制阶段算出的绝对位置。只在 UI 线程上访问。
/// </summary>
internal sealed class RenderBox
{
    RenderBox(Element element) => Element = element;

    public static RenderBox Get(Element e) => e.Render ??= new RenderBox(e);

    public readonly Element Element;

    ElementStyleView? _view;

    public ElementStyleView GetView(RenderPipeline pipeline)
    {
        if (_view is null || !ReferenceEquals(_viewPipeline, pipeline))
        {
            _view = new ElementStyleView(Element, pipeline);
            _viewPipeline = pipeline;
        }
        return _view;
    }

    RenderPipeline? _viewPipeline;

    // ---------------- 样式

    /// <summary>计算样式；文本节点与父元素共享同一实例。</summary>
    public ComputedStyle? Style;

    /// <summary>上次样式计算时的 class 数组（元素每次修改 class 都换成新数组），用于算出变化的 class。</summary>
    public System.Collections.Generic.IReadOnlyList<string>? StyledClasses;

    /// <summary>选择器输入的变化可能影响后代的匹配（祖先组合器中用到了它）：整棵子树重算样式。</summary>
    public bool RestyleSubtree;

    /// <summary>子节点结构变化而样式表用到了兄弟 / 结构伪类：所有直接子元素重算样式（<see cref="RestyleChildSubtrees"/> 时含其子树）。</summary>
    public bool RestyleChildren;

    public bool RestyleChildSubtrees;

    /// <summary>DevTools 对内联样式的修改（替代元素自身的 <see cref="Element.InlineStyle"/>，组件重建不会冲掉；为 null 表示没有修改）。</summary>
    public string? InlineOverride;

    /// <summary>本帧样式差异中含布局类变化，需要重新映射 <see cref="LayoutStyle"/>。</summary>
    public bool LayoutStyleDirty = true;

    // ---------------- 布局

    public BoxKind Kind;

    public LayoutNode? Layout;

    /// <summary>上次映射 <see cref="LayoutStyle"/> 所用的计算样式（引用相同则不必重新映射）。</summary>
    public ComputedStyle? MappedStyle;

    /// <summary>上次映射时的设备像素比（边框宽度按它对齐）。</summary>
    public float MappedScale;

    /// <summary>段落叶子的文本与测量。</summary>
    public TextParagraph? Paragraph;

    // ---------------- 绘制（本帧的绝对几何，逻辑像素，已对齐设备像素）

    /// <summary>绝对边框盒；display:none 或不生成盒时为空。</summary>
    public Rect Border;

    /// <summary>布局给出的内容盒左上角（绝对，未对齐）与宽度（未对齐，用于段落断行）。</summary>
    public float ContentX, ContentY, ContentWidth, ContentHeight;

    /// <summary>本帧是否生成了盒（祖先与自身都不是 display:none）。</summary>
    public bool HasBox;

    /// <summary>
    /// 墨迹范围（绝对坐标）：自身边框盒连同阴影与轮廓的外扩、段落文本的溢出，以及全部后代的墨迹。
    /// 每次重绘前自底向上算出，与视口不相交的子树整体跳过录制。
    /// </summary>
    public Rect Ink;

    // ---------------- 增量绘制（元素级 DisplayList 缓存，design/18「不提升为层的元素通过 DrawDisplayList 嵌入」）

    /// <summary>上次布局后的局部几何；变化时本元素（及包含它的录制单元）需要重录。</summary>
    public PaintGeometry Geometry;

    /// <summary>边框盒相对父元素边框盒的偏移；变化时父元素需要重录（本元素只在合成时平移）。</summary>
    public float OffsetX, OffsetY;

    /// <summary>
    /// 本元素作为录制单元的 DisplayList（局部坐标：录制时先平移到自身边框盒原点）。为 null 表示没有缓存。
    /// 普通元素缓存「背景边框 → 内容 → 轮廓」；层叠上下文缓存其内部（不含外部裁剪链）。
    /// </summary>
    public Graphics.DisplayList? Cache;

    /// <summary>缓存是作为层叠上下文录制的（否则是普通元素）。</summary>
    public bool CacheIsContext;

    /// <summary>录制缓存时所在的绘制轮次、缓存世代与边框盒原点。</summary>
    public long CacheSerial;
    public int CacheEpoch;

    /// <summary>录制时的剔除矩形（相对边框盒原点）；<see cref="CacheComplete"/> 表示录制时整个墨迹都在视口内（没有剔除任何内容）。</summary>
    public Rect CacheCullLocal;
    public bool CacheComplete;

    /// <summary>最近一次被判定失效的绘制轮次（几何变化、层叠列表变化）；大于 <see cref="CacheSerial"/> 时缓存无效。</summary>
    public long DirtySerial;

    /// <summary>本轮已计入损伤区域（避免重复）。</summary>
    public long DamageSerial;

    /// <summary>层叠上下文的层叠项（负 z、0 / auto、正 z），按 <see cref="LayerSerial"/> 缓存。</summary>
    public System.Collections.Generic.List<(RenderBox Box, int Z, int Order)>? Negative, Positive;
    public System.Collections.Generic.List<RenderBox>? Zero;
    public long LayerSerial = -1;

    /// <summary>
    /// 层叠上下文内有绝对 / 固定定位的后代，其包含块链跳过了本上下文外部裁剪链中的某个溢出裁剪祖先：
    /// 本上下文不能把外部裁剪链整体施加于自己（否则会错误地裁掉那个后代），改为逐块施加，且不作为独立的录制单元。
    /// </summary>
    public bool ClipEscape;

    /// <summary>替换元素的内容（图片、SVG）；其他元素为 null。</summary>
    public ReplacedContent? Replaced;

    // ---------------- 几何与视觉效果（transform、filter、backdrop-filter、clip-path，见 RenderPipeline.Effects.cs）

    /// <summary>效果缓存所依据的计算样式与边框盒；与当前不同时重新计算（<see cref="RenderPipeline.EnsureEffects"/>）。</summary>
    public ComputedStyle? EffectsStyle;
    public Rect EffectsBorder;
    public float EffectsScale;

    /// <summary>
    /// 元素的变换（绝对坐标：把未变换的布局坐标映射到父元素的坐标空间，已含 transform-origin 与参考盒的位置）。
    /// <see cref="HasTransform"/> 为 false 时是单位矩阵。
    /// </summary>
    public bool HasTransform;
    public System.Numerics.Matrix3x2 Transform = System.Numerics.Matrix3x2.Identity;
    public System.Numerics.Matrix3x2 InverseTransform = System.Numerics.Matrix3x2.Identity;

    /// <summary>变换不可逆（缩放为 0 等）或背面朝前且 <c>backface-visibility: hidden</c>：元素及其后代不绘制、不可命中。</summary>
    public bool EffectsInvisible;

    /// <summary><c>filter</c> 换算成的滤镜链（none 或含 url() 时为 null）。</summary>
    public Graphics.Filter? Filter;

    /// <summary><c>backdrop-filter</c> 换算成的滤镜链（none 或含 url() 时为 null）。</summary>
    public Graphics.Filter? BackdropFilter;

    /// <summary><c>clip-path</c> 的形状（绝对坐标，未变换的布局空间）；none 或 url() 时为 null。</summary>
    public Graphics.VectorPath? ClipPath;

    /// <summary>元素是否有任何几何 / 视觉效果（变换、滤镜、背景滤镜、裁剪路径）。</summary>
    public bool HasEffects;

    /// <summary>
    /// 内容墨迹：自身与全部后代的墨迹之并，位于元素自己（未变换）的坐标空间，未计入滤镜外扩，<b>相对边框盒原点</b>保存
    /// （滚动只平移几何、不重算墨迹，相对值随边框盒一起移动）。<see cref="Ink"/> 是它经滤镜外扩、变换后的结果。
    /// 滤镜图层以它为输入范围。
    /// </summary>
    public Rect ContentInkLocal;

    /// <summary><see cref="ContentInkLocal"/> 的绝对坐标。</summary>
    public Rect ContentInk => ContentInkLocal.Width <= 0 && ContentInkLocal.Height <= 0 ? default : ContentInkLocal.Offset(Border.X, Border.Y);

    /// <summary>录制缓存时是否以边框盒原点为局部原点（否则按绝对坐标录制，见 RenderPipeline.PaintUnit）与当时的原点。</summary>
    public bool CacheLocalOrigin = true;
    public Point CacheOrigin;

    public override string ToString() => "RenderBox(" + Element + ", " + Kind + ")";
}
