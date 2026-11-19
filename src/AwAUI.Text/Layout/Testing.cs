using System;
using System.Collections.Generic;
using System.Numerics;
using AwAUI.Graphics;
using AwAUI.Graphics.Text;
using AwAUI.Unicode;

namespace AwAUI.Text.Testing;

/// <summary>
/// 确定性的方块字体（与 Ahem 字体的约定相同）：每个字符都是 1 em 宽的方块，上升 0.8 em、下降 0.2 em、无行间距。
/// 空白与控制字符映射为空字形（同样 1 em 宽，可用 <paramref name="advance"/> 调整全部字形的宽度）。
/// 用于布局与控件的单元测试：结果只取决于字符数，不受真实字体与平台差异影响（design/19「测试钩子」）。
/// </summary>
public sealed class BoxFontFace : FontFace
{
    /// <summary>设计单位每 em。</summary>
    public const int UnitsPerEm = 1000;

    /// <summary>.notdef（空心方框）。</summary>
    public const ushort NotDefGlyph = 0;
    /// <summary>实心方块。</summary>
    public const ushort BoxGlyph = 1;
    /// <summary>空字形（空白、控制字符）。</summary>
    public const ushort EmptyGlyph = 2;

    readonly Func<int, bool>? _coverage;
    readonly int _advance;

    /// <param name="familyName">族名。</param>
    /// <param name="advance">全部字形的前进量（设计单位，默认 1000 即 1 em）。</param>
    /// <param name="coverage">可选：哪些码点有字形（用于测试字体回退）；为 null 时覆盖全部码点。</param>
    /// <param name="weight">字重描述符。</param>
    /// <param name="style">风格描述符。</param>
    public BoxFontFace(string familyName = "Ahem", int advance = UnitsPerEm, Func<int, bool>? coverage = null,
        FontWeight weight = default, FontStyle style = FontStyle.Normal)
    {
        Descriptor = new FontDescriptor(familyName ?? throw new ArgumentNullException(nameof(familyName)), weight, style);
        _advance = Math.Max(0, advance);
        _coverage = coverage;
        Metrics = new FontMetrics
        {
            UnitsPerEm = UnitsPerEm,
            Ascender = 800,
            Descender = -200,
            LineGap = 0,
            Hhea = new VerticalMetrics(800, -200, 0),
            Typo = new VerticalMetrics(800, -200, 0),
            Win = new VerticalMetrics(800, -200, 0),
            XHeight = 800,
            CapHeight = 800,
            UnderlinePosition = -100,
            UnderlineThickness = 50,
            StrikeoutPosition = 400,
            StrikeoutThickness = 50,
            IsFixedPitch = true,
            XMin = 0,
            YMin = -200,
            XMax = Math.Max(_advance, 1),
            YMax = 800,
            AdvanceWidthMax = _advance,
        };
    }

    public override FontDescriptor Descriptor { get; }
    public override FontMetrics Metrics { get; }
    public override int GlyphCount => 3;
    public override OutlineFormat OutlineFormat => OutlineFormat.TrueType;
    public override ReadOnlyMemory<byte> GetTable(uint tag) => ReadOnlyMemory<byte>.Empty;
    public override IReadOnlyList<VariationAxis> Axes => Array.Empty<VariationAxis>();
    public override GlyphImage? GetColorGlyph(ushort glyph, float size) => null;

    public override bool TryGetGlyph(int codePoint, out ushort glyph)
    {
        glyph = NotDefGlyph;
        if (!CodePoint.IsValid(codePoint) || (_coverage != null && !_coverage(codePoint))) return false;
        var gc = UnicodeData.GetGeneralCategory(codePoint);
        glyph = gc is GeneralCategory.SpaceSeparator or GeneralCategory.Control or GeneralCategory.LineSeparator
            or GeneralCategory.ParagraphSeparator or GeneralCategory.Format
            ? EmptyGlyph : BoxGlyph;
        return true;
    }

    public override int GetAdvance(ushort glyph) => _advance;

    public override bool TryGetGlyphBounds(ushort glyph, out Rect bounds)
    {
        bounds = glyph is BoxGlyph or NotDefGlyph ? new Rect(0, -800, _advance, 1000) : default;
        return glyph is BoxGlyph or NotDefGlyph && _advance > 0;
    }

    public override bool GetOutline(ushort glyph, in FontVariation variation, PathBuilder sink, in Matrix3x2 transform)
    {
        if (sink is null) throw new ArgumentNullException(nameof(sink));
        if (glyph >= GlyphCount) return false;
        if (glyph == EmptyGlyph || _advance == 0) return true;
        AddBox(sink, transform, 0, -800, _advance, 200);
        if (glyph == NotDefGlyph)
        {
            // 空心：内框反向（非零环绕下挖空）
            float t = Math.Min(_advance, 1000) * 0.1f;
            if (_advance > 2 * t) AddBox(sink, transform, _advance - t, -800 + t, t, 200 - t);
        }
        return true;
    }

    static void AddBox(PathBuilder sink, in Matrix3x2 m, float x0, float y0, float x1, float y1)
    {
        // (x0, y0) → (x1, y0) → (x1, y1) → (x0, y1)；传入 x0 > x1 时方向相反
        sink.MoveTo(Vector2.Transform(new Vector2(x0, y0), m));
        sink.LineTo(Vector2.Transform(new Vector2(x1, y0), m));
        sink.LineTo(Vector2.Transform(new Vector2(x1, y1), m));
        sink.LineTo(Vector2.Transform(new Vector2(x0, y1), m));
        sink.Close();
    }
}

/// <summary>
/// 确定性的测试文本引擎（design/19「测试钩子」）：字体集合中只有 <see cref="BoxFontFace"/>（族名 "Ahem"，通用族名都映射到它），
/// 因此每个字符宽 1 em，断行只取决于字符数。可向 <see cref="Collection"/> 注册更多字型来测试字体回退。
/// 默认使用 <see cref="BasicTextShaper"/>，也可传入自定义整形器（例如统计调用次数）。
/// </summary>
public sealed class TestTextEngine : TextEngine
{
    static readonly string[] s_families = { "Ahem" };

    public TestTextEngine(TextShaper? shaper = null, int shapeCacheEntries = ShapeCache.DefaultMaxEntries)
        : base(shapeCacheEntries)
    {
        Shaper = shaper ?? BasicTextShaper.Instance;
        Face = new BoxFontFace();
        Collection = new FontCollection();
        Collection.Register(Face);
        foreach (var generic in new[] { "sans-serif", "serif", "monospace", "system-ui", "cursive", "fantasy" })
            Collection.SetGenericFamily(generic, "Ahem");
    }

    /// <summary>默认的方块字型。</summary>
    public BoxFontFace Face { get; }

    /// <summary>字体集合（可注册更多字型）。</summary>
    public FontCollection Collection { get; }

    public override FontManager Fonts => Collection;
    public override TextShaper Shaper { get; }
    public override IReadOnlyList<string> DefaultFontFamilies => s_families;
}
