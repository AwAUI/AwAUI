using System.Text;
using System.Text.RegularExpressions;
using AwAUI.Css;
using AwAUI.Css.Values;
using Xunit.Abstractions;

namespace AwAUI.Tailwind.UnitTests;

/// <summary>
/// AwAUI.Css 差距报告（design/23「引擎前置条件」的待办清单）：用 AwAUI.Css 的公开解析器解析 shadcn 语料生成的完整样式表，
/// 列出被拒绝或忽略的 at 规则、选择器、媒体查询、属性与值。只报告、不判定失败（AwAUI.Css 在持续补齐）。
/// 值检查会先把 <c>var()</c> 按样式表中的自定义属性（:root 与 @property 初始值）代入，以暴露 oklch / color-mix 等真实值的问题。
/// </summary>
public class CssGapReportTests
{
    readonly ITestOutputHelper _output;

    public CssGapReportTests(ITestOutputHelper output) => _output = output;

    sealed class Gap
    {
        public int Count;
        public string Example = "";
    }

    [Theory]
    [InlineData("shadcn")]
    [InlineData("default")]
    public void Report_unsupported_css(string corpus)
    {
        var compiler = TestFiles.CreateCompiler(corpus, PreflightKind.AwAUI);
        var candidates = TestFiles.ReadReference(corpus).Select(r => r.Key).ToArray();
        string css = compiler.Build(candidates);
        string report = BuildReport(css, corpus);
        _output.WriteLine(report);
        var dir = Path.Combine(TestFiles.RepoRoot, "tests", "TestFiles", "Output", "Tailwind");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "awaui-css-gaps-" + corpus + ".txt"), report);
        Assert.False(string.IsNullOrEmpty(report));
    }

    static string BuildReport(string css, string corpus)
    {
        var sheet = StyleSheet.Parse(css, corpus + ".css");
        var gaps = new SortedDictionary<string, Gap>(StringComparer.Ordinal);
        void Add(string key, string example)
        {
            if (!gaps.TryGetValue(key, out var g)) gaps[key] = g = new Gap { Example = example };
            g.Count++;
        }

        foreach (var d in sheet.Diagnostics)
        {
            string snippet = Snip(css, d.Range);
            Add($"[{d.Code} {d.Severity}] {Classify(d, snippet)}", snippet);
        }

        // 收集自定义属性（文本中首次出现的定义与 @property 初始值），用于代入 var()；不依赖 AwAUI.Css 是否接受了所在规则
        var custom = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (Match m in Regex.Matches(css, @"(?<![\w-])(--[\w-]+):\s*([^;{}]+);"))
        {
            if (!custom.ContainsKey(m.Groups[1].Value)) custom[m.Groups[1].Value] = m.Groups[2].Value.Trim();
        }
        foreach (Match m in Regex.Matches(css, @"@property (--[\w-]+) \{[^}]*?initial-value: ([^;]+);"))
        {
            if (!custom.ContainsKey(m.Groups[1].Value)) custom[m.Groups[1].Value] = m.Groups[2].Value;
        }

        foreach (var rule in sheet.EnumerateRules())
        {
            IReadOnlyList<CssDeclaration>? decls = rule switch
            {
                CssStyleRule s => s.Declarations,
                CssNestedDeclarationsRule n => n.Declarations,
                CssKeyframeRule k => k.Declarations,
                _ => null,
            };
            if (rule is CssUnknownAtRule at) Add($"[at-rule] @{at.Name} ({(at.IsKnownUnsupported ? "known, unsupported" : "unknown")})", "@" + at.Name);
            if (decls == null) continue;
            var diags = new List<CssDiagnostic>();
            var block = PropertyDeclarationBlock.Parse(decls, diags);
            foreach (var r in block.RejectedDeclarations)
            {
                if (r.Status == DeclarationStatus.Unsupported) Add($"[property] {r.Source.Name}", r.Source.ToString());
                else Add($"[value] {r.Source.Name}: {ValueShape(r.Source.ValueText)}", r.Source.ToString());
            }
            // var() 代入后再校验一次
            foreach (var decl in decls)
            {
                if (decl.IsCustomProperty || !decl.ValueText.Contains("var(")) continue;
                string substituted = Substitute(decl.ValueText, custom, 0);
                if (substituted.Contains("var(") || substituted.Contains("initial")) continue;
                var d2 = new List<CssDiagnostic>();
                var parsed = StyleSheet.ParseDeclarations(decl.Name + ": " + substituted, d2);
                var b2 = PropertyDeclarationBlock.Parse(parsed, d2);
                foreach (var r in b2.RejectedDeclarations)
                {
                    if (r.Status == DeclarationStatus.Invalid) Add($"[value after var() substitution] {decl.Name}: {ValueShape(substituted)}", decl.Name + ": " + substituted);
                }
            }
        }

        var sb = new StringBuilder();
        sb.Append("AwAUI.Css gaps for the ").Append(corpus).Append(" corpus stylesheet (").Append(css.Length / 1024).Append(" KiB, ").Append(gaps.Count).Append(" distinct issues)\n");
        foreach (var kv in gaps)
        {
            sb.Append(kv.Key).Append("  ×").Append(kv.Value.Count).Append('\n');
            sb.Append("    e.g. ").Append(Truncate(kv.Value.Example.Replace('\n', ' '), 160)).Append('\n');
        }
        return sb.ToString();
    }

    static string Classify(CssDiagnostic d, string snippet) => d.Code switch
    {
        CssDiagnosticCodes.UnsupportedSelector or CssDiagnosticCodes.InvalidSelector => "selector: " + SelectorFeature(snippet + " " + d.Message),
        CssDiagnosticCodes.InvalidMediaQuery => "media: " + MediaFeature(snippet + " " + d.Message),
        CssDiagnosticCodes.UnsupportedAtRule or CssDiagnosticCodes.InvalidAtRule => "at-rule: " + snippet.Split('{')[0].Trim().Split(' ')[0],
        _ => Truncate(d.Message, 90),
    };

    static string SelectorFeature(string text)
    {
        foreach (var f in new[] { ":has(", ":where(", ":is(", ":not(", ":popover-open", ":open", ":dir(", "::placeholder", "::file-selector-button", "::selection", "::marker", "::backdrop", "::before", "::after", ":focus-visible", ":focus-within", ":hover", ":autofill", ":user-valid", ":user-invalid", ":placeholder-shown", ":checked", ":disabled", ":first-child", ":last-child", ":nth-child(", "::-webkit-", ":-moz-", "[", ">", "~", ":scope" })
        {
            if (text.Contains(f)) return f;
        }
        return Truncate(text, 60);
    }

    static string MediaFeature(string text)
    {
        var m = Regex.Match(text, @"\(([a-z-]+)\s*(:|>=|<|>|<=)");
        return m.Success ? "(" + m.Groups[1].Value + " " + m.Groups[2].Value + ")" : Truncate(text, 60);
    }

    static string ValueShape(string v)
    {
        var fn = Regex.Match(v, @"(oklch|oklab|color-mix|calc\(infinity|lab|lch|light-dark|linear-gradient|radial-gradient|conic-gradient|cubic-bezier|steps|translateZ|drop-shadow|blur|rgb\(from|oklab\(from)");
        return fn.Success ? fn.Groups[1].Value + "(…)" : Truncate(v, 50);
    }

    static string Substitute(string value, Dictionary<string, string> custom, int depth)
    {
        if (depth > 8) return value;
        return Regex.Replace(value, @"var\((--[\w-]+)(?:,\s*([^()]*(?:\([^()]*\)[^()]*)*))?\)", m =>
        {
            if (custom.TryGetValue(m.Groups[1].Value, out var v)) return Substitute(v, custom, depth + 1);
            return m.Groups[2].Success ? Substitute(m.Groups[2].Value.Trim(), custom, depth + 1) : "initial";
        });
    }

    static string Snip(string css, SourceRange range)
    {
        int start = Math.Max(0, Math.Min(css.Length, range.Start.Offset));
        int end = Math.Max(start, Math.Min(css.Length, range.End.Offset));
        return css.Substring(start, Math.Min(end - start, 200));
    }

    static string Truncate(string s, int max) => s.Length <= max ? s : s.Substring(0, max) + "…";
}
