using System.Text;

namespace AwAUI.Tailwind.UnitTests;

/// <summary>tests/TestFiles/Tailwind 下的参考输出（由 tools/ref.mjs 用 tailwindcss v4.3.5 生成）。</summary>
static class TestFiles
{
    public static string RepoRoot { get; } = FindRoot();

    static string FindRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "global.json"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }

    public static string Dir => Path.Combine(RepoRoot, "tests", "TestFiles", "Tailwind");

    public static string Read(params string[] parts) => File.ReadAllText(Path.Combine(Dir, Path.Combine(parts))).Replace("\r\n", "\n");

    /// <summary>读取「### 候选\n输出」格式的参考文件。</summary>
    public static List<KeyValuePair<string, string>> ReadReference(string corpus)
    {
        var text = Read(corpus, "reference.txt");
        var result = new List<KeyValuePair<string, string>>();
        string? name = null;
        var body = new StringBuilder();
        foreach (var line in text.Split('\n'))
        {
            if (line.StartsWith("### ", StringComparison.Ordinal))
            {
                if (name != null) result.Add(new(name, body.ToString().TrimEnd('\n')));
                name = line.Substring(4);
                body.Clear();
                continue;
            }
            body.Append(line).Append('\n');
        }
        if (name != null) result.Add(new(name, body.ToString().TrimEnd('\n')));
        return result;
    }

    /// <summary>与 tools/normalize.py 相同的规范化：@property 块折叠为名字列表。</summary>
    public static string Normalize(string? css)
    {
        if (css == null) return "(none)";
        var lines = css.TrimEnd('\n').Split('\n');
        var rules = new List<string>();
        var props = new List<string>();
        for (int i = 0; i < lines.Length; i++)
        {
            var l = lines[i];
            if (l.StartsWith("@property ", StringComparison.Ordinal))
            {
                props.Add(l.Substring("@property ".Length, l.Length - "@property ".Length - 2).Trim());
                while (i < lines.Length && lines[i] != "}") i++;
                continue;
            }
            rules.Add(l);
        }
        if (props.Count > 0) rules.Add("@property " + string.Join(" ", props));
        return string.Join("\n", rules);
    }

    /// <summary>为测试解析相对 @import（shadcn-tailwind.css）。</summary>
    public sealed class DirectoryResolver : StylesheetResolver
    {
        readonly string _dir;

        public DirectoryResolver(string dir) => _dir = dir;

        public override ResolvedStylesheet? Resolve(string id, string? baseDirectory)
        {
            var path = Path.Combine(baseDirectory ?? _dir, id);
            return File.Exists(path) ? new ResolvedStylesheet(File.ReadAllText(path).Replace("\r\n", "\n"), Path.GetDirectoryName(path)) : null;
        }
    }

    public static TailwindCompiler CreateCompiler(string corpus, PreflightKind preflight = PreflightKind.Tailwind)
    {
        var dir = Path.Combine(Dir, corpus);
        return TailwindCompiler.Create(Read(corpus, "input.css"), new TailwindOptions { Resolver = new DirectoryResolver(dir), BaseDirectory = dir, Preflight = preflight });
    }
}
