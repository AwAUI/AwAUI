using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using AwAUI.Graphics.Text;
using AwAUI.Unicode;

namespace AwAUI.Text;

/// <summary>
/// 系统字体：按操作系统约定的字体目录枚举字体文件，并按文字与语言环境给出回退族名（design/19「字体回退」）。
/// 只枚举路径，字型由 <see cref="FontCollection"/> 按需加载。平台后端没有系统字体（Web、Headless）时使用 <see cref="SystemFontProvider.Empty"/>。
/// </summary>
public static class SystemFonts
{
    static readonly Lazy<SystemFontProvider> s_default = new(
        () => new DirectorySystemFontProvider(GetDefaultDirectories()));

    /// <summary>当前操作系统的系统字体提供者（延迟枚举，第一次用到字体时才访问磁盘）。</summary>
    public static SystemFontProvider Default => s_default.Value;

    /// <summary>创建一个接入系统字体的字体集合：应用注册的字体优先，缺字时回退到系统字体。</summary>
    public static FontCollection CreateCollection() => new() { SystemFonts = Default };

    /// <summary>当前操作系统的字体目录（可能包含不存在或空的目录）。</summary>
    public static IReadOnlyList<string> GetDefaultDirectories()
    {
        var dirs = new List<string>();
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            dirs.Add("/System/Library/Fonts");
            dirs.Add("/System/Library/Fonts/Supplemental");
            dirs.Add("/Library/Fonts");
            AddUserDir(dirs, "Library", "Fonts");
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            var windir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            if (windir.Length > 0) dirs.Add(Path.Combine(windir, "Fonts"));
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (local.Length > 0) dirs.Add(Path.Combine(local, "Microsoft", "Windows", "Fonts"));
        }
        else
        {
            dirs.Add("/usr/share/fonts");
            dirs.Add("/usr/local/share/fonts");
            AddUserDir(dirs, ".fonts");
            AddUserDir(dirs, ".local", "share", "fonts");
        }
        return dirs;

        static void AddUserDir(List<string> dirs, params string[] parts)
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (home.Length > 0) dirs.Add(Path.Combine(home, Path.Combine(parts)));
        }
    }
}

/// <summary>
/// 扫描一组目录（递归）的 <see cref="SystemFontProvider"/>：字体文件在第一次访问时枚举并缓存。
/// 回退族名按文字（UAX #24）给出各平台的常见字体，顺序即优先级；族名是否存在由 <see cref="FontCollection"/> 解析时校验。
/// </summary>
public sealed class DirectorySystemFontProvider : SystemFontProvider
{
    static readonly string[] s_extensions = { ".ttf", ".otf", ".ttc", ".otc" };

    readonly IReadOnlyList<string> _directories;
    readonly object _lock = new();
    IReadOnlyList<string>? _files;

    public DirectorySystemFontProvider(IEnumerable<string> directories)
    {
        _directories = directories as IReadOnlyList<string> ?? new List<string>(directories ?? Array.Empty<string>());
    }

    public override IReadOnlyList<string> FontFiles
    {
        get
        {
            lock (_lock) return _files ??= Enumerate();
        }
    }

    public override string? DefaultFamilyName
    {
        get
        {
            // 界面字体：macOS 的系统字体是 .AppleSystemUIFont（没有可枚举的族名），用 Helvetica 作后备
            if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX)) return "Helvetica";
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return "Segoe UI";
            return "DejaVu Sans";
        }
    }

    public override IReadOnlyList<string> GetFallbackFamilies(int codePoint, string? locale)
    {
        var (simplified, traditional, japanese, korean) = ResolveCjkLocale(locale);
        // 汉字标点与全角形式（，。、「」【】等）的 Script 是 Common，但只有 CJK 字体收录
        if (IsCjkPunctuation(codePoint)) return Han(simplified, traditional, japanese, korean);
        var script = UnicodeData.GetScript(codePoint);
        return script switch
        {
            Script.Han => Han(simplified, traditional, japanese, korean),
            Script.Hiragana or Script.Katakana or Script.Bopomofo => Japanese(),
            Script.Hangul => KoreanFonts(),
            Script.Arabic or Script.Syriac or Script.Nko or Script.Thaana or Script.Mongolian => new[]
            {
                "Geeza Pro", "Noto Sans Arabic", "Segoe UI", "Tahoma", "DejaVu Sans",
            },
            Script.Hebrew => new[] { "Arial Hebrew", "Noto Sans Hebrew", "Segoe UI", "DejaVu Sans" },
            Script.Devanagari or Script.Bengali or Script.Gurmukhi or Script.Gujarati or Script.Oriya or Script.Tamil
                or Script.Telugu or Script.Kannada or Script.Malayalam or Script.Sinhala => new[]
            {
                "Kohinoor Devanagari", "Nirmala UI", "Noto Sans Devanagari", "DejaVu Sans",
            },
            Script.Thai or Script.Lao or Script.Khmer or Script.Myanmar => new[]
            {
                "Thonburi", "Leelawadee UI", "Noto Sans Thai", "DejaVu Sans",
            },
            Script.Armenian => new[] { "Mshtakan", "Segoe UI", "Noto Sans Armenian", "DejaVu Sans" },
            Script.Georgian => new[] { "Sylfaen", "Noto Sans Georgian", "DejaVu Sans" },
            _ => codePoint >= 0x1F000
                ? new[] { "Apple Color Emoji", "Segoe UI Emoji", "Noto Color Emoji", "Noto Emoji" }
                : Array.Empty<string>(),
        };
    }

    IReadOnlyList<string> Enumerate()
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var files = new List<string>();
        foreach (var dir in _directories)
        {
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) continue;
            try
            {
                foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
                {
                    if (!HasFontExtension(file)) continue;
                    if (seen.Add(file)) files.Add(file);
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // 目录不可读：跳过（系统字体目录偶尔包含无法访问的链接）
            }
        }
        files.Sort(StringComparer.Ordinal);
        return files;
    }

    static bool HasFontExtension(string file)
    {
        foreach (var ext in s_extensions)
            if (file.EndsWith(ext, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    /// <summary>汉字标点、全角形式与竖排标点所在的区块（Script 为 Common，但通常只有 CJK 字体收录）。</summary>
    static bool IsCjkPunctuation(int codePoint) =>
        codePoint is >= 0x2E80 and <= 0x2EFF      // CJK Radicals Supplement
            or >= 0x3000 and <= 0x303F            // CJK Symbols and Punctuation
            or >= 0xFE30 and <= 0xFE4F            // CJK Compatibility Forms
            or >= 0xFF00 and <= 0xFFEF            // Halfwidth and Fullwidth Forms
            or >= 0xFE10 and <= 0xFE1F;           // Vertical Forms

    // ---------------------------------------------------------------- 语言 → 汉字变体

    /// <summary>从 BCP 47 标签解析汉字变体偏好：简体（zh 大陆/新加坡）、繁体（zh-TW/HK/MO 与 Hant）、日本、韩国。</summary>
    static (bool Simplified, bool Traditional, bool Japanese, bool Korean) ResolveCjkLocale(string? locale)
    {
        if (string.IsNullOrEmpty(locale)) return (true, true, true, true);
        var parts = locale!.Replace('_', '-').Split('-');
        bool simplified = false, traditional = false, japanese = false, korean = false;
        foreach (var part in parts)
        {
            if (part.Equals("Hant", StringComparison.OrdinalIgnoreCase) || part.Equals("TW", StringComparison.OrdinalIgnoreCase)
                || part.Equals("HK", StringComparison.OrdinalIgnoreCase) || part.Equals("MO", StringComparison.OrdinalIgnoreCase))
                traditional = true;
            else if (part.Equals("Hans", StringComparison.OrdinalIgnoreCase) || part.Equals("CN", StringComparison.OrdinalIgnoreCase)
                || part.Equals("SG", StringComparison.OrdinalIgnoreCase))
                simplified = true;
            else if (part.Equals("ja", StringComparison.OrdinalIgnoreCase) || part.Equals("Jpan", StringComparison.OrdinalIgnoreCase))
                japanese = true;
            else if (part.Equals("ko", StringComparison.OrdinalIgnoreCase) || part.Equals("Kore", StringComparison.OrdinalIgnoreCase))
                korean = true;
        }
        if (!simplified && !traditional && !japanese && !korean) simplified = true;   // 未知语言：简体优先
        return (simplified, traditional, japanese, korean);
    }

    static IReadOnlyList<string> Han(bool simplified, bool traditional, bool japanese, bool korean)
    {
        var families = new List<string>();
        if (simplified)
            families.AddRange(new[]
            {
                "PingFang SC", "Hiragino Sans GB", "Noto Sans CJK SC", "Noto Sans SC", "Source Han Sans SC",
                "Microsoft YaHei", "SimHei", "SimSun", "WenQuanYi Micro Hei",
            });
        if (traditional)
            families.AddRange(new[]
            {
                "PingFang TC", "Noto Sans CJK TC", "Noto Sans TC", "Source Han Sans TC", "Microsoft JhengHei", "Heiti TC",
            });
        if (japanese) families.AddRange(Japanese());
        if (korean) families.AddRange(KoreanFonts());
        return families;
    }

    static string[] Japanese() => new[]
    {
        "Hiragino Sans", "Hiragino Kaku Gothic ProN", "Noto Sans CJK JP", "Noto Sans JP", "Yu Gothic", "Meiryo", "MS Gothic",
    };

    static string[] KoreanFonts() => new[]
    {
        "Apple SD Gothic Neo", "Noto Sans CJK KR", "Noto Sans KR", "Malgun Gothic", "NanumGothic",
    };
}
