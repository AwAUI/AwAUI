using System;

namespace AwAUI.Svg;

/// <summary>
/// 解析上限。SVG 常来自不可信来源，所有上限都保证解析与构建的时间、内存与递归深度和输入规模成线性关系。
/// 长度、深度、元素数、属性数超限会抛出 <see cref="SvgParseException"/>；
/// 路径段数、渲染节点数、use 嵌套等「内容」上限超出时截断并记录诊断。
/// </summary>
public sealed class SvgParseOptions
{
    /// <summary>默认选项（适合图标与插画）。</summary>
    public static SvgParseOptions Default { get; } = new();

    /// <summary>输入最大长度：字符串按 UTF-16 字符数、字节输入按字节数计。默认 16 MiB。</summary>
    public int MaxInputLength { get; init; } = 16 * 1024 * 1024;

    /// <summary>元素最大嵌套深度，同时限制 use 展开后的渲染树深度。默认 256，硬上限 1024。</summary>
    public int MaxDepth { get; init; } = 256;

    /// <summary>元素总数上限。默认 200 000。</summary>
    public int MaxElements { get; init; } = 200_000;

    /// <summary>单个元素的属性数上限。默认 256。</summary>
    public int MaxAttributesPerElement { get; init; } = 256;

    /// <summary>整个文档的路径段（含 polyline/polygon 的点与弧线转换出的曲线段）总数上限。默认 4 000 000。</summary>
    public int MaxPathSegments { get; init; } = 4_000_000;

    /// <summary>渲染树节点总数上限（use 展开后的实例都计入，被跳过的不渲染子元素也计入），防止 use 的指数级扇出。默认 100 000。</summary>
    public int MaxRenderNodes { get; init; } = 100_000;

    /// <summary>use 引用链的最大嵌套层数。默认 32。</summary>
    public int MaxUseDepth { get; init; } = 32;

    /// <summary>
    /// 一次绘制的渲染工作量预算：按根 viewBox 长边渲染为 2048 像素时估计的折线点数（每条曲线至多 1000 点，
    /// 填充、描边与裁剪路径各算一遍；use 的每个实例都单独计算），每个渐变画法另按色标数折算。
    /// 不够时曲线先退化为弦（直线），连弦也付不起时其余形状不再渲染，渐变退化为最后一个色标的纯色；都会记录诊断。
    /// 默认 4 000 000。
    /// </summary>
    public int MaxFlattenedPoints { get; init; } = 4_000_000;

    /// <summary>单次描边虚线最多产生的段数（估计值），超过则忽略虚线按实线描边。默认 100 000。</summary>
    public int MaxDashSegments { get; init; } = 100_000;

    /// <summary>&lt;style&gt; 选择器匹配与层叠的总工作量上限（步数；复合选择器按类名数、匹配的规则按声明数计）。默认 20 000 000。</summary>
    public int MaxSelectorWork { get; init; } = 20_000_000;

    /// <summary>
    /// &lt;image&gt; 元素的图像来源（见 <see cref="SvgImageResolver"/>）。为 null（默认）时 &lt;image&gt; 被忽略并记录诊断。
    /// </summary>
    public SvgImageResolver? ImageResolver { get; init; }

    /// <summary>一个文档中 &lt;image&gt; 解码出的位图像素总数上限，超出的图像被忽略并记录诊断。默认 64M 像素（约 256 MB）。</summary>
    public long MaxImagePixels { get; init; } = 64L * 1024 * 1024;

    /// <summary>
    /// 径向渐变的焦点在结束圆之外时按 SVG 1.1 处理：把焦点移到圆心与焦点连线和结束圆的交点上（略向内，避免退化）。
    /// 默认 false，按 SVG 2 / Canvas 的双点锥形渐变绘制（焦点保持原位，圆外的区域形成锥形）。
    /// </summary>
    public bool Svg11FocalPoint { get; init; }

    /// <summary>最多保留的诊断条数。默认 100。</summary>
    public int MaxDiagnostics { get; init; } = 100;

    internal int EffectiveMaxDepth => Math.Max(1, Math.Min(MaxDepth, 1024));
}

/// <summary>绘制选项。</summary>
public sealed class SvgRenderOptions
{
    /// <summary>默认选项：currentColor 为黑色，不透明，裁剪到目标矩形。</summary>
    public static SvgRenderOptions Default { get; } = new();

    /// <summary>
    /// 文档根的 color 属性，即 currentColor 的取值（图标着色）。文档内显式设置的 color 优先。
    /// </summary>
    public Color CurrentColor { get; init; } = Color.Black;

    /// <summary>整体不透明度（0..1），小于 1 时整个图形作为一个透明度组绘制。</summary>
    public float Opacity { get; init; } = 1f;

    /// <summary>是否把内容裁剪到目标矩形（与浏览器把 SVG 当作图片时一致）。默认 true。</summary>
    public bool ClipToViewport { get; init; } = true;

    /// <summary>覆盖根元素的 preserveAspectRatio；为 null 时使用文档自身的设置。</summary>
    public SvgPreserveAspectRatio? PreserveAspectRatio { get; init; }
}

/// <summary>preserveAspectRatio 的对齐方式。</summary>
public enum SvgAlign : byte
{
    None,
    XMinYMin, XMidYMin, XMaxYMin,
    XMinYMid, XMidYMid, XMaxYMid,
    XMinYMax, XMidYMax, XMaxYMax,
}

/// <summary>preserveAspectRatio 属性值。默认 xMidYMid meet。</summary>
public readonly struct SvgPreserveAspectRatio : IEquatable<SvgPreserveAspectRatio>
{
    public SvgPreserveAspectRatio(SvgAlign align, bool slice = false)
    {
        Align = align;
        Slice = slice;
    }

    public SvgAlign Align { get; }

    /// <summary>true 为 slice（铺满后裁掉多余部分），false 为 meet（完整放入）。</summary>
    public bool Slice { get; }

    public static SvgPreserveAspectRatio Default => new(SvgAlign.XMidYMid);

    /// <summary>把 viewBox 映射到视口的变换（SVG 2 §8.2 的算法）。</summary>
    public System.Numerics.Matrix3x2 GetTransform(Rect viewBox, Rect viewport)
    {
        if (viewBox.Width <= 0 || viewBox.Height <= 0) return System.Numerics.Matrix3x2.Identity;
        float sx = viewport.Width / viewBox.Width, sy = viewport.Height / viewBox.Height;
        if (Align != SvgAlign.None)
        {
            float s = Slice ? Math.Max(sx, sy) : Math.Min(sx, sy);
            sx = sy = s;
        }
        float tx = viewport.X - viewBox.X * sx, ty = viewport.Y - viewBox.Y * sy;
        float dx = viewport.Width - viewBox.Width * sx, dy = viewport.Height - viewBox.Height * sy;
        switch (Align)
        {
            case SvgAlign.XMidYMin: case SvgAlign.XMidYMid: case SvgAlign.XMidYMax: tx += dx / 2; break;
            case SvgAlign.XMaxYMin: case SvgAlign.XMaxYMid: case SvgAlign.XMaxYMax: tx += dx; break;
        }
        switch (Align)
        {
            case SvgAlign.XMinYMid: case SvgAlign.XMidYMid: case SvgAlign.XMaxYMid: ty += dy / 2; break;
            case SvgAlign.XMinYMax: case SvgAlign.XMidYMax: case SvgAlign.XMaxYMax: ty += dy; break;
        }
        return new System.Numerics.Matrix3x2(sx, 0, 0, sy, tx, ty);
    }

    public bool Equals(SvgPreserveAspectRatio other) => Align == other.Align && Slice == other.Slice;
    public override bool Equals(object? obj) => obj is SvgPreserveAspectRatio o && Equals(o);
    public override int GetHashCode() => ((int)Align << 1) | (Slice ? 1 : 0);
    public static bool operator ==(SvgPreserveAspectRatio a, SvgPreserveAspectRatio b) => a.Equals(b);
    public static bool operator !=(SvgPreserveAspectRatio a, SvgPreserveAspectRatio b) => !a.Equals(b);
}
