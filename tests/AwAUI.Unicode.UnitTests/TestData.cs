using System.Globalization;
using System.Text;

namespace AwAUI.Unicode.UnitTests;

/// <summary>定位 tests/TestFiles/Unicode 下的 Unicode 官方一致性测试文件。</summary>
internal static class TestData
{
    public static string RepoRoot { get; } = FindRoot();

    static string FindRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "global.json"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }

    public static string PathOf(string name) => Path.Combine(RepoRoot, "tests", "TestFiles", "Unicode", name);

    /// <summary>一条分段测试：文本与期望的边界（UTF-16 偏移，含 0 与末尾）。</summary>
    public sealed record BreakCase(int Line, string Text, int[] Boundaries, string Source);

    /// <summary>解析 "÷ 0061 × 0308 ÷" 格式的分段测试文件。</summary>
    public static List<BreakCase> ReadBreakTests(string name)
    {
        var result = new List<BreakCase>();
        int lineNo = 0;
        foreach (var raw in File.ReadLines(PathOf(name), Encoding.UTF8))
        {
            lineNo++;
            int hash = raw.IndexOf('#');
            var line = (hash >= 0 ? raw.Substring(0, hash) : raw).Trim();
            if (line.Length == 0) continue;
            var sb = new StringBuilder();
            var boundaries = new List<int>();
            foreach (var token in line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries))
            {
                if (token == "÷") boundaries.Add(sb.Length);
                else if (token == "×") { }
                else sb.Append(char.ConvertFromUtf32(int.Parse(token, NumberStyles.HexNumber, CultureInfo.InvariantCulture)));
            }
            result.Add(new BreakCase(lineNo, sb.ToString(), boundaries.ToArray(), raw));
        }
        return result;
    }

    public static string Hex(string s)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < s.Length;)
        {
            int cp = char.ConvertToUtf32(s, i);
            if (sb.Length > 0) sb.Append(' ');
            sb.Append(cp.ToString("X4", CultureInfo.InvariantCulture));
            i += cp >= 0x10000 ? 2 : 1;
        }
        return sb.ToString();
    }
}
