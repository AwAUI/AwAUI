using System.IO;
using AwAUI.Graphics.Text;

namespace AwAUI.Text.UnitTests;

/// <summary>
/// 系统字体回退（design/19「字体回退」）：应用注册的字体缺字时，按语言环境从系统字体中找字型；
/// 都找不到时触发 <see cref="FontManager.MissingGlyphs"/>。用仓库内的 Noto Sans 冒充系统字体，结果与平台无关。
/// </summary>
public class SystemFontFallbackTests
{
    static string NotoPath => Path.Combine(TestFonts.FontsDir, TestFonts.NotoSans);

    /// <summary>只认识 Noto Sans 一个系统族名的提供者，记录回退查询的语言环境。</summary>
    sealed class StubProvider : SystemFontProvider
    {
        readonly string[] _files;

        public StubProvider(string? defaultFamily, params string[] files)
        {
            DefaultFamilyName = defaultFamily;
            _files = files;
        }

        public List<(int CodePoint, string? Locale)> Queries { get; } = new();

        public override IReadOnlyList<string> FontFiles => _files;
        public override string? DefaultFamilyName { get; }

        public override IReadOnlyList<string> GetFallbackFamilies(int codePoint, string? locale)
        {
            Queries.Add((codePoint, locale));
            return DefaultFamilyName is null ? Array.Empty<string>() : new[] { DefaultFamilyName };
        }
    }

    [Fact]
    public void Generic_family_resolves_to_the_system_default_font()
    {
        var fonts = new FontCollection { SystemFonts = new StubProvider("Noto Sans", NotoPath) };
        var face = fonts.Match(new FontQuery("sans-serif"));
        Assert.NotNull(face);
        Assert.Equal("Noto Sans", face!.Descriptor.FamilyName);
    }

    [Fact]
    public void Fallback_asks_the_provider_with_the_locale_and_caches_per_locale()
    {
        var provider = new StubProvider("Noto Sans", NotoPath);
        var fonts = new FontCollection { SystemFonts = provider };
        var query = new FontQuery("Missing");

        var face = fonts.Fallback('Ж', query, "ru");
        Assert.NotNull(face);
        Assert.Equal("Noto Sans", face!.Descriptor.FamilyName);

        // 同一（码点、语言、样式）命中缓存
        Assert.Same(face, fonts.Fallback('Ж', query, "ru"));
        Assert.Single(provider.Queries);

        // 另一个语言环境是另一个缓存键
        Assert.Same(face, fonts.Fallback('Ж', query, "en"));
        Assert.Equal(2, provider.Queries.Count);
        Assert.Equal("en", provider.Queries[1].Locale);
    }

    [Fact]
    public void Fallback_reports_missing_glyphs_when_no_system_font_covers_the_code_point()
    {
        var fonts = new FontCollection { SystemFonts = new StubProvider("Noto Sans", NotoPath) };
        MissingGlyphEventArgs? missing = null;
        fonts.MissingGlyphs += (_, e) => missing = e;

        // 随附的 Noto Sans 没有汉字：回退链走完仍然没有字型
        Assert.Null(fonts.Fallback(0x4E2D, new FontQuery("Missing"), "zh-Hans"));
        Assert.NotNull(missing);
        Assert.Equal(0x4E2D, missing!.CodePoint);
        Assert.Equal("zh-Hans", missing.Locale);
    }

    [Fact]
    public void Registered_fonts_take_precedence_over_system_fonts()
    {
        var provider = new StubProvider("Noto Sans", NotoPath);
        var fonts = new FontCollection { SystemFonts = provider };
        var abel = fonts.Register(TestFonts.Bytes(TestFonts.Abel)).Faces[0];

        Assert.Same(abel, fonts.Fallback('A', new FontQuery("Abel"), null));
        Assert.Empty(provider.Queries);   // 已注册的字体能显示，不查询系统字体
    }

    [Fact]
    public void Without_a_provider_there_is_no_fallback()
    {
        var fonts = new FontCollection();
        fonts.Register(TestFonts.Bytes(TestFonts.Abel));
        Assert.Null(fonts.Fallback(0x4E2D, new FontQuery("Abel"), "zh"));
        Assert.Null(fonts.Match(new FontQuery("sans-serif")));
    }

    [Fact]
    public void Setting_the_provider_clears_fallback_caches()
    {
        var fonts = new FontCollection();
        fonts.Register(TestFonts.Bytes(TestFonts.Abel));
        Assert.Null(fonts.Fallback(0x4E2D, new FontQuery("Abel"), "zh"));

        fonts.SystemFonts = SystemFontProvider.Empty;
        Assert.Null(fonts.Fallback(0x4E2D, new FontQuery("Abel"), "zh"));
    }

    [ChineseSystemFontFact]
    public void System_fonts_render_chinese()
    {
        // 真实的系统字体：没有 CJK 字体的机器（精简容器）跳过
        var fonts = SystemFonts.CreateCollection();
        var face = fonts.Fallback(0x4E2D, new FontQuery("sans-serif"), "zh-Hans");
        Assert.NotNull(face);
        Assert.True(face!.TryGetGlyph(0x4E2D, out var glyph));
        Assert.NotEqual(0, glyph);
    }

    [Fact]
    public void Directory_provider_normalizes_family_names()
    {
        var provider = new DirectorySystemFontProvider(new[] { TestFonts.FontsDir });
        Assert.Contains(provider.FontFiles, p => p.EndsWith(TestFonts.NotoSans, StringComparison.Ordinal));

        var fonts = new FontCollection { SystemFonts = provider };
        // 文件名不是族名（NotoSans-Regular.ttf → Noto Sans），仍能解析
        var face = fonts.Match(new FontQuery("Noto Sans"));
        Assert.NotNull(face);
        Assert.Equal("Noto Sans", face!.Descriptor.FamilyName);
    }

    [Fact]
    public void Name_index_reads_family_names_without_loading_the_face()
    {
        var names = OpenType.OpenTypeNameIndex.ReadFamilyNames(NotoPath);
        var face = Assert.Single(names);
        Assert.Contains("Noto Sans", face);

        // 不是字体的文件、不存在的文件：空结果，不抛异常
        Assert.Empty(OpenType.OpenTypeNameIndex.ReadFamilyNames(Path.Combine(TestFonts.FontsDir, "README.md")));
        Assert.Empty(OpenType.OpenTypeNameIndex.ReadFamilyNames(Path.Combine(TestFonts.FontsDir, "missing.ttf")));
    }

    [Fact]
    public void Full_index_only_loads_the_files_of_the_requested_family()
    {
        // 目录里有多个字体文件；按族名（与文件名无关）解析只应加载该族所在的文件，
        // 而不是把整个目录读进内存（Windows 的 Fonts 目录约 470 MB）
        var provider = new DirectorySystemFontProvider(new[] { TestFonts.FontsDir });
        Assert.True(provider.FontFiles.Count > 2);
        var fonts = new FontCollection { SystemFonts = provider };

        Assert.NotNull(fonts.Match(new FontQuery("AwATest Layout")) ?? fonts.Match(new FontQuery("Abel")));
        Assert.InRange(fonts.LoadedSystemFaceFiles, 1, 2);
    }

    [Fact]
    public void Full_scan_keeps_only_the_file_that_covers_the_code_point()
    {
        // 没有回退族名、没有默认族：走全量 cmap 扫描。未命中的文件立即释放，命中的文件留在缓存中
        var abel = Path.Combine(TestFonts.FontsDir, TestFonts.Abel);
        var fonts = new FontCollection { SystemFonts = new StubProvider(null, abel, NotoPath) };

        var face = fonts.Fallback('Ж', new FontQuery("Missing"), "ru");   // Abel 没有西里尔字母
        Assert.NotNull(face);
        Assert.Equal("Noto Sans", face!.Descriptor.FamilyName);
        Assert.True(face.TryGetGlyph('Ж', out var glyph));
        Assert.NotEqual(0, glyph);
        Assert.Equal(1, fonts.LoadedSystemFaceFiles);

        // 都不覆盖：没有文件被留下
        var none = new FontCollection { SystemFonts = new StubProvider(null, abel, NotoPath) };
        Assert.Null(none.Fallback(0x4E2D, new FontQuery("Missing"), "zh-Hans"));
        Assert.Equal(0, none.LoadedSystemFaceFiles);
    }

    [Fact]
    public void Mapped_font_file_matches_the_bytes_on_disk()
    {
        var data = FontFileData.Open(NotoPath, out var owner);
        try
        {
            Assert.NotNull(owner);   // 大于 64 KB 的文件走内存映射
            Assert.True(data.Span.SequenceEqual(File.ReadAllBytes(NotoPath)));
        }
        finally
        {
            owner?.Dispose();
        }
        Assert.Throws<FileNotFoundException>(() => FontFileData.Open(Path.Combine(TestFonts.FontsDir, "missing.ttf"), out _));
    }
}
