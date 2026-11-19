using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Threading;
using AwAUI.Graphics;
using AwAUI.Graphics.Text;

namespace AwAUI.Text.OpenType;

/// <summary>'name' 表中的常用名称。缺失的条目为 null。</summary>
public sealed class FontNames
{
    internal FontNames(ReadOnlySpan<byte> name)
    {
        Family = NameTable.Get(name, NameTable.Family);
        Subfamily = NameTable.Get(name, NameTable.Subfamily);
        FullName = NameTable.Get(name, NameTable.FullName);
        PostScriptName = NameTable.Get(name, NameTable.PostScriptName);
        TypographicFamily = NameTable.Get(name, NameTable.TypographicFamily);
        TypographicSubfamily = NameTable.Get(name, NameTable.TypographicSubfamily);
    }

    internal FontNames() { }

    /// <summary>nameID 1（旧式族名，每族最多四个风格）。</summary>
    public string? Family { get; }
    /// <summary>nameID 2。</summary>
    public string? Subfamily { get; }
    /// <summary>nameID 4。</summary>
    public string? FullName { get; }
    /// <summary>nameID 6。</summary>
    public string? PostScriptName { get; }
    /// <summary>nameID 16（排版族名，优先于 nameID 1）。</summary>
    public string? TypographicFamily { get; }
    /// <summary>nameID 17。</summary>
    public string? TypographicSubfamily { get; }
}

/// <summary>
/// OpenType / TrueType 字型（.ttf、.otf 或 .ttc 中的一个面），直接在字体字节上按需解析，只读取用到的表。
/// 必需表（head、hhea、maxp）损坏时加载失败并抛出 <see cref="InvalidFontException"/>；
/// 可选表（name、OS/2、post、kern、cmap、GSUB、GPOS、GDEF、CFF）损坏时视为不存在。
/// 轮廓：TrueType 'glyf'、'CFF '（含 CID 键字体）与 'CFF2'（默认实例）。
/// </summary>
public sealed class OpenTypeFace : FontFace
{
    readonly SfntDirectory _directory;
    readonly HeadTable _head;
    readonly HheaTable _hhea;
    readonly HmtxTable _hmtx;
    readonly int _numGlyphs;
    readonly FontMetrics _metrics;
    readonly FontDescriptor _descriptor;


    OpenTypeFace(SfntDirectory directory, int faceIndex)
    {
        _directory = directory;
        FaceIndex = faceIndex;

        // Apple 纯位图字体以 'bhed' 代替 'head'（布局相同）
        if (!directory.TryGetTable(OpenTypeTag.Head, out var head) && !directory.TryGetTable(BhedTag, out head))
            throw new InvalidFontException("Missing required 'head' table.");
        if (!directory.TryGetTable(OpenTypeTag.Hhea, out var hhea)) throw new InvalidFontException("Missing required 'hhea' table.");
        if (!directory.TryGetTable(OpenTypeTag.Maxp, out var maxp)) throw new InvalidFontException("Missing required 'maxp' table.");
        _head = HeadTable.Parse(head.Span);
        _hhea = HheaTable.Parse(hhea.Span);
        _numGlyphs = MaxpTable.ParseNumGlyphs(maxp.Span);
        _hmtx = new HmtxTable(directory.GetTable(OpenTypeTag.Hmtx), _hhea.NumberOfHMetrics, _numGlyphs);

        Os2Table? os2 = TryParse(OpenTypeTag.OS2, t => Os2Table.Parse(t.Span));
        PostTable? post = TryParse(OpenTypeTag.Post, t => PostTable.Parse(t.Span));
        Names = TryParse(OpenTypeTag.Name, t => new FontNames(t.Span)) ?? new FontNames();

        if (directory.Contains(OpenTypeTag.Glyf) && directory.Contains(OpenTypeTag.Loca)) OutlineFormat = OutlineFormat.TrueType;
        else if (directory.Contains(OpenTypeTag.Cff)) OutlineFormat = OutlineFormat.Cff;
        else if (directory.Contains(OpenTypeTag.Cff2)) OutlineFormat = OutlineFormat.Cff2;
        else OutlineFormat = OutlineFormat.None;

        _metrics = BuildMetrics(os2, post);
        _descriptor = BuildDescriptor(os2, post);
    }

    static readonly uint BhedTag = OpenTypeTag.Make("bhed");

    T? TryParse<T>(uint tag, Func<ReadOnlyMemory<byte>, T?> parse) where T : class
    {
        if (!_directory.TryGetTable(tag, out var t)) return null;
        try
        {
            return parse(t);
        }
        catch (InvalidFontException)
        {
            return null;
        }
    }

    // ---------------------------------------------------------------- 加载

    /// <summary>从字节加载一个字型。<paramref name="faceIndex"/> 用于 .ttc 集合。</summary>
    public static OpenTypeFace Load(ReadOnlyMemory<byte> data, int faceIndex = 0)
    {
        var dir = SfntDirectory.Read(data, faceIndex);
        return new OpenTypeFace(dir, faceIndex);
    }

    /// <summary>从文件加载一个字型。</summary>
    public static OpenTypeFace Load(string path, int faceIndex = 0) => Load(File.ReadAllBytes(path), faceIndex);

    /// <summary>加载文件中的全部字型（单字体文件返回一个）。</summary>
    public static IReadOnlyList<OpenTypeFace> LoadAll(ReadOnlyMemory<byte> data)
    {
        int count = SfntDirectory.GetFaceCount(data.Span);
        var faces = new OpenTypeFace[count];
        for (int i = 0; i < count; i++) faces[i] = Load(data, i);
        return faces;
    }

    /// <summary>文件中的字型数量（.ttc 为集合大小，否则为 1）。</summary>
    public static int GetFaceCount(ReadOnlySpan<byte> data) => SfntDirectory.GetFaceCount(data);

    /// <summary>不抛出异常的加载；数据无效或格式不受支持时返回 false。</summary>
    public static bool TryLoad(ReadOnlyMemory<byte> data, int faceIndex, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out OpenTypeFace? face)
    {
        try
        {
            face = Load(data, faceIndex);
            return true;
        }
        catch (Exception e) when (e is InvalidFontException or NotSupportedException or ArgumentOutOfRangeException)
        {
            face = null;
            return false;
        }
    }

    // ---------------------------------------------------------------- 属性

    public int FaceIndex { get; }
    public FontNames Names { get; }
    public override FontDescriptor Descriptor => _descriptor;
    public override FontMetrics Metrics => _metrics;
    public override int GlyphCount => _numGlyphs;
    public override OutlineFormat OutlineFormat { get; }
    public override IReadOnlyList<VariationAxis> Axes => Array.Empty<VariationAxis>();

    /// <summary>字体中存在的表。</summary>
    public IEnumerable<uint> TableTags => _directory.Tags;

    public override ReadOnlyMemory<byte> GetTable(uint tag) => _directory.GetTable(tag);

    // 延迟解析的表：null = 尚未解析，Absent = 不存在或损坏。结果不可变，并发时重复解析也无害
    static readonly object Absent = new();
    object? _cmap, _kern, _glyf, _cff, _layout;

    /// <summary>OpenType 布局数据与整形计划缓存（<see cref="OpenTypeShaper"/> 使用）。</summary>
    internal Layout.OtFaceData LayoutData
    {
        get
        {
            if (Volatile.Read(ref _layout) is Layout.OtFaceData d) return d;
            var created = new Layout.OtFaceData(this);
            return Interlocked.CompareExchange(ref _layout, created, null) as Layout.OtFaceData ?? created;
        }
    }

    static T? Publish<T>(ref object? field, T? value) where T : class
    {
        Volatile.Write(ref field, (object?)value ?? Absent);
        return value;
    }

    internal CmapTable? Cmap => Volatile.Read(ref _cmap) is { } v
        ? v as CmapTable
        : Publish(ref _cmap, TryParse(OpenTypeTag.Cmap, CmapTable.Parse));

    internal KernTable? Kern => Volatile.Read(ref _kern) is { } v
        ? v as KernTable
        : Publish(ref _kern, TryParse(OpenTypeTag.Kern, KernTable.Parse));

    internal GlyfTable? Glyf => Volatile.Read(ref _glyf) is { } v
        ? v as GlyfTable
        : Publish(ref _glyf, OutlineFormat == OutlineFormat.TrueType && _head.IndexToLocFormat is 0 or 1
            ? new GlyfTable(GetTable(OpenTypeTag.Glyf), GetTable(OpenTypeTag.Loca), _head.IndexToLocFormat == 1, _numGlyphs)
            : null);

    /// <summary>'CFF ' 或 'CFF2' 表（<see cref="OutlineFormat"/> 为 Cff / Cff2 时）；损坏时为 null。</summary>
    internal CffTable? Cff => Volatile.Read(ref _cff) is { } v
        ? v as CffTable
        : Publish(ref _cff, OutlineFormat is OutlineFormat.Cff or OutlineFormat.Cff2
            ? TryParse(OutlineFormat == OutlineFormat.Cff2 ? OpenTypeTag.Cff2 : OpenTypeTag.Cff,
                t => CffTable.Parse(t, OutlineFormat == OutlineFormat.Cff2, _numGlyphs))
            : null);

    /// <summary>是否含有可用的水平成对字距（'kern' 格式 0）。</summary>
    public bool HasKerning => Kern is not null;

    // ---------------------------------------------------------------- 字形

    public override bool TryGetGlyph(int codePoint, out ushort glyph)
    {
        var cmap = Cmap;
        if (cmap is not null && (uint)codePoint <= 0x10FFFF)
        {
            ushort g;
            try
            {
                g = cmap.Map(codePoint);
            }
            catch (InvalidFontException)
            {
                g = 0;
            }
            if (g != 0 && g < _numGlyphs)
            {
                glyph = g;
                return true;
            }
        }
        glyph = 0;
        return false;
    }

    public override int GetAdvance(ushort glyph)
    {
        try
        {
            return _hmtx.GetAdvance(glyph);
        }
        catch (InvalidFontException)
        {
            return 0;
        }
    }

    /// <summary>字形的左侧支撑（设计单位）。</summary>
    public int GetLeftSideBearing(ushort glyph)
    {
        try
        {
            return _hmtx.GetLeftSideBearing(glyph);
        }
        catch (InvalidFontException)
        {
            return 0;
        }
    }

    public override int GetKerning(ushort left, ushort right)
    {
        var kern = Kern;
        if (kern is null) return 0;
        try
        {
            return kern.GetKerning(left, right);
        }
        catch (InvalidFontException)
        {
            return 0;
        }
    }

    public override bool TryGetGlyphBounds(ushort glyph, out Rect bounds)
    {
        bounds = default;
        if (Cff is not null)
        {
            // CFF 没有逐字形的边界框：解码轮廓后计算（含曲线极值点）
            var o = DecodeCff(glyph);
            if (o is null || !o.TryGetBounds(out float x0, out float y0, out float x1, out float y1)) return false;
            bounds = new Rect(x0, -y1, x1 - x0, y1 - y0);
            return true;
        }
        var glyf = Glyf;
        if (glyf is null) return false;
        try
        {
            if (!glyf.TryGetBounds(glyph, out short xMin, out short yMin, out short xMax, out short yMax)) return false;
            if (xMax < xMin || yMax < yMin) return false;
            bounds = new Rect(xMin, -yMax, xMax - xMin, yMax - yMin);
            return true;
        }
        catch (InvalidFontException)
        {
            return false;
        }
    }

    [ThreadStatic] static CffInterpreter? t_cffInterpreter;
    [ThreadStatic] static CffTable? t_cffTable;
    [ThreadStatic] static CffOutline? t_cffOutline;

    /// <summary>解码 CFF 字形到线程内复用的缓冲；无效或损坏时返回 null。</summary>
    CffOutline? DecodeCff(ushort glyph)
    {
        var cff = Cff;
        if (cff is null || glyph >= _numGlyphs) return null;
        if (!ReferenceEquals(t_cffTable, cff))
        {
            t_cffInterpreter = new CffInterpreter(cff);
            t_cffTable = cff;
        }
        var outline = t_cffOutline ??= new CffOutline();
        try
        {
            t_cffInterpreter!.Decode(glyph, outline);
            return outline;
        }
        catch (InvalidFontException)
        {
            return null;
        }
    }

    [ThreadStatic] static GlyphOutline? t_outline;

    public override bool GetOutline(ushort glyph, in FontVariation variation, PathBuilder sink, in Matrix3x2 transform)
    {
        if (sink is null) throw new ArgumentNullException(nameof(sink));
        if (glyph >= _numGlyphs) return false;
        if (OutlineFormat is OutlineFormat.Cff or OutlineFormat.Cff2)
        {
            var cff = DecodeCff(glyph);
            if (cff is null) return false;
            cff.Emit(sink, transform);
            return true;
        }
        var glyf = Glyf;
        if (glyf is null) return false;
        var outline = t_outline ??= new GlyphOutline();
        try
        {
            glyf.Decode(glyph, outline);
        }
        catch (InvalidFontException)
        {
            return false;
        }
        outline.Emit(sink, transform);
        return true;
    }

    /// <summary>解码字形的原始 TrueType 轮廓（测试与诊断用）。</summary>
    internal GlyphOutline? DecodeOutline(ushort glyph)
    {
        var glyf = Glyf;
        if (glyf is null) return null;
        var outline = new GlyphOutline();
        glyf.Decode(glyph, outline);
        return outline;
    }

    public override GlyphImage? GetColorGlyph(ushort glyph, float size) => null;   // P2

    // ---------------------------------------------------------------- 度量与描述符

    FontMetrics BuildMetrics(Os2Table? os2, PostTable? post)
    {
        var hhea = new VerticalMetrics(_hhea.Ascender, _hhea.Descender, _hhea.LineGap);
        var typo = os2 is { HasTypoMetrics: true } ? new VerticalMetrics(os2.TypoAscender, os2.TypoDescender, os2.TypoLineGap) : default;
        var win = os2 is { HasTypoMetrics: true } ? new VerticalMetrics(os2.WinAscent, -os2.WinDescent, 0) : default;

        // 与 HarfBuzz / 浏览器一致：设置 USE_TYPO_METRICS 时用 typo，否则用 hhea；两者都为 0 时依次退回 typo、win、bbox
        VerticalMetrics chosen;
        if (os2 is { UseTypoMetrics: true } && (typo.Ascender != 0 || typo.Descender != 0)) chosen = typo;
        else if (hhea.Ascender != 0 || hhea.Descender != 0) chosen = hhea;
        else if (typo.Ascender != 0 || typo.Descender != 0) chosen = typo;
        else if (win.Ascender != 0 || win.Descender != 0) chosen = win;
        else chosen = new VerticalMetrics(_head.YMax, _head.YMin, 0);

        int xHeight = os2 is { XHeight: > 0 } ? os2.XHeight : GlyphTop('x');
        int capHeight = os2 is { CapHeight: > 0 } ? os2.CapHeight : GlyphTop('H');

        int upem = _head.UnitsPerEm;
        return new FontMetrics
        {
            UnitsPerEm = upem,
            Ascender = chosen.Ascender,
            Descender = chosen.Descender,
            LineGap = chosen.LineGap,
            Hhea = hhea,
            Typo = typo,
            Win = win,
            XHeight = xHeight,
            CapHeight = capHeight,
            UnderlinePosition = post?.UnderlinePosition ?? -upem / 10,
            UnderlineThickness = post is { UnderlineThickness: > 0 } ? post.UnderlineThickness : upem / 14,
            StrikeoutPosition = os2 is { StrikeoutPosition: > 0 } ? os2.StrikeoutPosition : (xHeight > 0 ? xHeight / 2 : upem / 4),
            StrikeoutThickness = os2 is { StrikeoutSize: > 0 } ? os2.StrikeoutSize : (post is { UnderlineThickness: > 0 } ? post.UnderlineThickness : upem / 14),
            ItalicAngle = post?.ItalicAngle ?? 0,
            IsFixedPitch = post?.IsFixedPitch ?? false,
            XMin = _head.XMin,
            YMin = _head.YMin,
            XMax = _head.XMax,
            YMax = _head.YMax,
            AdvanceWidthMax = _hhea.AdvanceWidthMax,
        };
    }

    /// <summary>OS/2 没有 x 高度或大写高度时，取对应字形边界框的顶部。</summary>
    int GlyphTop(char c)
    {
        if (!TryGetGlyph(c, out var g)) return 0;
        return TryGetGlyphBounds(g, out var b) ? (int)-b.Top : 0;
    }

    FontDescriptor BuildDescriptor(Os2Table? os2, PostTable? post)
    {
        string family = Names.TypographicFamily ?? Names.Family ?? Names.FullName ?? string.Empty;

        int weight;
        if (os2 is not null && os2.WeightClass >= 1 && os2.WeightClass <= 1000)
            // 少数旧字体使用 1–9 表示字重
            weight = os2.WeightClass < 10 ? os2.WeightClass * 100 : os2.WeightClass;
        else weight = _head.IsBold ? 700 : 400;

        FontStyle style;
        if (os2 is not null && os2.IsOblique) style = FontStyle.Oblique;
        else if ((os2 is not null && os2.IsItalic) || _head.IsItalic) style = FontStyle.Italic;
        // 两处标志位都未设置时（不少字体如此），依次回退到子族名称与 post.italicAngle
        else if (StyleFromName(Names.TypographicSubfamily) is { } s1) style = s1;
        else if (StyleFromName(Names.Subfamily) is { } s2) style = s2;
        else if (post is not null && post.ItalicAngle != 0) style = FontStyle.Italic;
        else style = FontStyle.Normal;

        var stretch = os2 is not null && os2.WidthClass >= 1 && os2.WidthClass <= 9 ? (FontStretch)os2.WidthClass : FontStretch.Normal;
        return new FontDescriptor(family, new FontWeight(weight), style, stretch);
    }

    /// <summary>从子族名称（如 "Medium Italic"、"Bold Oblique"）推断样式；无法判断时返回 null。</summary>
    static FontStyle? StyleFromName(string? subfamily)
    {
        if (string.IsNullOrEmpty(subfamily)) return null;
        if (subfamily!.IndexOf("Oblique", StringComparison.OrdinalIgnoreCase) >= 0 ||
            subfamily.IndexOf("Slanted", StringComparison.OrdinalIgnoreCase) >= 0) return FontStyle.Oblique;
        if (subfamily.IndexOf("Italic", StringComparison.OrdinalIgnoreCase) >= 0) return FontStyle.Italic;
        return null;
    }
}
