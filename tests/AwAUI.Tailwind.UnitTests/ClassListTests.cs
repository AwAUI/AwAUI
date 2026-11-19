using System.IO.Compression;
using System.Text;
using Xunit.Abstractions;

namespace AwAUI.Tailwind.UnitTests;

/// <summary>
/// 全量语料：Tailwind v4.3.5 默认主题下 <c>getClassList()</c> 的全部类名 + 全部变体（共 23506 个候选），
/// 以每 64 个候选一组的规范化输出哈希比对（完整参考输出约 7 MB，不入库；生成脚本见 tools/classlist.mjs、tools/digest.py）。
/// </summary>
public class ClassListTests
{
    readonly ITestOutputHelper _output;

    public ClassListTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public void Every_tailwind_class_matches()
    {
        var path = Path.Combine(TestFiles.Dir, "classlist", "candidates.txt.gz");
        string text;
        using (var gz = new GZipStream(File.OpenRead(path), CompressionMode.Decompress))
        using (var reader = new StreamReader(gz, Encoding.UTF8))
            text = reader.ReadToEnd();
        var candidates = text.Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries);
        var expected = TestFiles.Read("classlist", "digest.txt").Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Split(' ')[1]).ToArray();
        var compiler = TailwindCompiler.CreateDefault(new TailwindOptions { Preflight = PreflightKind.Tailwind });
        var actual = compiler.CandidatesToCss(candidates);
        var failures = new List<string>();
        for (int chunk = 0; chunk * 64 < candidates.Length; chunk++)
        {
            var sb = new StringBuilder();
            for (int i = chunk * 64; i < Math.Min(candidates.Length, chunk * 64 + 64); i++)
                sb.Append(candidates[i]).Append('\n').Append(TestFiles.Normalize(actual[i])).Append('\n');
            string hash = Fnv1a64(Encoding.UTF8.GetBytes(sb.ToString())).ToString("x16");
            if (hash != expected[chunk]) failures.Add($"chunk {chunk}: {string.Join(" ", candidates.Skip(chunk * 64).Take(64))}");
        }
        _output.WriteLine($"{candidates.Length} candidates in {expected.Length} chunks; {expected.Length - failures.Count} chunks match");
        Assert.True(failures.Count == 0, string.Join("\n", failures));
    }

    static ulong Fnv1a64(byte[] data)
    {
        ulong h = 0xcbf29ce484222325;
        foreach (var b in data)
        {
            h ^= b;
            h *= 0x100000001b3;
        }
        return h;
    }
}
