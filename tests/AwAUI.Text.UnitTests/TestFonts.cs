using AwAUI.Text.OpenType;

namespace AwAUI.Text.UnitTests;

/// <summary>仓库内提交的测试字体（tests/TestFiles/Fonts）与可选的系统字体。</summary>
public static class TestFonts
{
    public static string RepoRoot { get; } = FindRoot();

    static string FindRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(System.IO.Path.Combine(dir.FullName, "global.json"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }

    public static string FontsDir => System.IO.Path.Combine(RepoRoot, "tests", "TestFiles", "Fonts");

    public const string NotoSans = "NotoSans-Regular.ttf";
    public const string Abel = "Abel-Regular.ttf";

    static readonly Dictionary<string, byte[]> Cache = new();

    public static byte[] Bytes(string file)
    {
        lock (Cache)
        {
            if (!Cache.TryGetValue(file, out var b)) Cache[file] = b = File.ReadAllBytes(System.IO.Path.Combine(FontsDir, file));
            return b;
        }
    }

    public static OpenTypeFace Load(string file) => OpenTypeFace.Load(Bytes(file));

    // macOS 系统字体：不提交到仓库，文件不存在时相关测试跳过
    public const string Helvetica = "/System/Library/Fonts/Helvetica.ttc";
    public const string Menlo = "/System/Library/Fonts/Menlo.ttc";
    public const string Arial = "/System/Library/Fonts/Supplemental/Arial.ttf";
    public const string Geneva = "/System/Library/Fonts/Geneva.ttf";
    public const string StixCff = "/System/Library/Fonts/Supplemental/STIXGeneral.otf";
}

/// <summary>依赖的系统字体文件不存在时跳过的测试。</summary>
public sealed class SystemFontFactAttribute : FactAttribute
{
    public SystemFontFactAttribute(params string[] paths)
    {
        foreach (var p in paths)
            if (!File.Exists(p))
            {
                Skip = $"System font '{p}' is not present on this machine.";
                break;
            }
    }
}

/// <summary>系统字体不能显示汉字（精简容器、无 CJK 字体的机器）时跳过的测试。</summary>
public sealed class ChineseSystemFontFactAttribute : FactAttribute
{
    public ChineseSystemFontFactAttribute()
    {
        if (SystemFonts.CreateCollection().Fallback(0x4E2D, new FontQuery("sans-serif"), "zh-Hans") is null)
            Skip = "No system font covers U+4E2D on this machine.";
    }
}
