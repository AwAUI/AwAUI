using System;
using System.Collections.Generic;
using System.Numerics;

namespace AwAUI.Graphics.Text;

/// <summary>
/// 一个字型（字体文件中的一个面）。不可变，可跨线程读取。
/// 所有度量为设计单位；轮廓坐标为设计单位、y 向下、原点在基线上的字形原点。
/// 抽象位于 Graphics，渲染后端借此读取字形轮廓而无需引用文本栈；具体实现（如 OpenType 解析）在 AwAUI.Text。
/// 后端按对象引用识别字型（字形缓存的键），因此同一字体应复用同一个实例。
/// </summary>
public abstract class FontFace
{
    /// <summary>族名、字重、字宽、风格。</summary>
    public abstract FontDescriptor Descriptor { get; }

    /// <summary>设计单位下的上升、下降、行距、x 高度等。</summary>
    public abstract FontMetrics Metrics { get; }

    /// <summary>字形数量（有效字形 ID 为 0 到 GlyphCount − 1，0 为 .notdef）。</summary>
    public abstract int GlyphCount { get; }

    /// <summary>轮廓格式。不支持的格式下 <see cref="GetOutline(ushort, in FontVariation, PathBuilder, in Matrix3x2)"/> 返回 false。</summary>
    public abstract OutlineFormat OutlineFormat { get; }

    /// <summary>原始表数据（供任何后端创建自己的字体对象）。表不存在时返回空。</summary>
    public abstract ReadOnlyMemory<byte> GetTable(uint tag);

    /// <summary>
    /// 把码点（0..0x10FFFF 的整数，见 <c>AwAUI.Unicode.CodePoint</c>）映射到字形。
    /// 没有对应字形或码点无效时返回 false，且 <paramref name="glyph"/> 为 0（.notdef）。
    /// </summary>
    public abstract bool TryGetGlyph(int codePoint, out ushort glyph);

    /// <summary>字形的水平前进量（设计单位）。</summary>
    public abstract int GetAdvance(ushort glyph);

    /// <summary>两个相邻字形之间的成对字距调整（设计单位）。默认没有字距。</summary>
    public virtual int GetKerning(ushort left, ushort right) => 0;

    /// <summary>字形的边界框（设计单位，y 向下，与轮廓坐标一致）。空字形或未知时返回 false。</summary>
    public virtual bool TryGetGlyphBounds(ushort glyph, out Rect bounds)
    {
        bounds = default;
        return false;
    }

    /// <summary>
    /// 把字形轮廓追加到 <paramref name="sink"/>（设计单位、y 向下）。
    /// 字形 ID 无效、轮廓格式不受支持或数据损坏时返回 false 且不写入任何内容；空字形返回 true。
    /// </summary>
    public bool GetOutline(ushort glyph, in FontVariation variation, PathBuilder sink) =>
        GetOutline(glyph, variation, sink, Matrix3x2.Identity);

    /// <summary>
    /// 与 <see cref="GetOutline(ushort, in FontVariation, PathBuilder)"/> 相同，但先对坐标施加 <paramref name="transform"/>。
    /// 例如 <c>Matrix3x2.CreateScale(size / unitsPerEm) * Matrix3x2.CreateTranslation(x, baseline)</c> 得到逻辑像素坐标。
    /// </summary>
    public abstract bool GetOutline(ushort glyph, in FontVariation variation, PathBuilder sink, in Matrix3x2 transform);

    /// <summary>位图或分层矢量的彩色字形；不是彩色字形时返回 null。</summary>
    public abstract GlyphImage? GetColorGlyph(ushort glyph, float size);

    /// <summary>可变字体的设计轴；静态字体为空。</summary>
    public abstract IReadOnlyList<VariationAxis> Axes { get; }

    public override string ToString() => Descriptor.ToString();
}
