using System.Runtime.CompilerServices;
using AwAUI.Css.Cascade;
using AwAUI.Css.Selectors;
using AwAUI.Css.Values;

namespace AwAUI.Css.UnitTests.Cascade;

/// <summary>
/// shadcn new-york-v4 语料经官方 Tailwind v4.3.5 生成的整份样式表（tests/TestFiles/Tailwind/shadcn/full.css，原版 preflight）：
/// 解析、编译与令牌计算的验收。
/// </summary>
public class ShadcnCorpusTests
{
    static string RepoRoot([CallerFilePath] string path = "")
    {
        foreach (var start in new[] { AppContext.BaseDirectory, Path.GetDirectoryName(path)! })
        {
            var dir = new DirectoryInfo(start);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "global.json"))) dir = dir.Parent;
            if (dir != null) return dir.FullName;
        }
        throw new InvalidOperationException("找不到仓库根目录");
    }

    static readonly Lazy<string> s_css = new(() =>
        File.ReadAllText(Path.Combine(RepoRoot(), "tests", "TestFiles", "Tailwind", "shadcn", "full.css")).Replace("\r\n", "\n"));

    [Fact]
    public void Stylesheet_parses_without_selector_at_rule_or_media_gaps()
    {
        var sheet = StyleSheet.Parse(s_css.Value, "shadcn.css");
        var gaps = sheet.Diagnostics.Where(d => d.Code is CssDiagnosticCodes.UnsupportedSelector or CssDiagnosticCodes.InvalidSelector
            or CssDiagnosticCodes.UnsupportedAtRule or CssDiagnosticCodes.InvalidAtRule or CssDiagnosticCodes.InvalidMediaQuery).ToList();
        // 原版 preflight 有意使用 Firefox 专有的 :-moz-focusring / :-moz-ui-invalid（Chromium 同样丢弃这两条规则）
        Assert.All(gaps, d => Assert.Contains("-moz-", d.Message + " " + s_css.Value.Substring(d.Range.Start.Offset, Math.Min(40, s_css.Value.Length - d.Range.Start.Offset))));
        var engine = new StyleEngine();
        var cs = engine.AddStyleSheet(sheet);
        engine.Resolve(new El("html"), null);
        var unsupported = cs.Diagnostics.Where(d => d.Code == CssDiagnosticCodes.UnsupportedProperty).Select(d => d.Message).ToList();
        // 原版 preflight 的浏览器私有属性：Chromium 也不支持 -moz-tab-size，-webkit-tap-highlight-color 等只影响移动浏览器
        Assert.All(unsupported, m => Assert.True(m.Contains("-moz-") || m.Contains("-webkit-"), m));
        var invalid = cs.Diagnostics.Where(d => d.Code == CssDiagnosticCodes.InvalidValue).Select(d => d.Message).ToList();
        Assert.True(invalid.Count == 0, string.Join("\n", invalid));
        Assert.True(engine.Registry.Count > 40);
    }

    [Fact]
    public void Tokens_and_utilities_compute_like_chrome()
    {
        var engine = new StyleEngine();
        engine.AddStyleSheet(StyleSheet.Parse(s_css.Value));
        var btn = new El("button", null, "bg-primary text-primary-foreground hover:bg-primary/90 shadow-xs rounded-md");
        var dark = new El("div", null, "dark").Add(new El("p", null, "bg-background text-foreground"));
        var ring = new El("input", null, "focus-visible:ring-ring/50 focus-visible:ring-[3px] border-input");
        var root = new El("html").Add(new El("body").Add(btn, dark, ring));
        var map = CascadeTestHelper.ResolveTree(engine, root);
        string Rgba(Color c) => $"{(int)Math.Round(c.R * 255)},{(int)Math.Round(c.G * 255)},{(int)Math.Round(c.B * 255)},{(int)Math.Round(c.A * 255)}";
        var b = map[btn];
        Assert.Equal("23,23,23,255", Rgba(b.BackgroundColor.Value)); // --primary: oklch(0.205 0 0)
        Assert.Equal("250,250,250,255", Rgba(b.Color.Value)); // --primary-foreground: oklch(0.985 0 0)
        btn.State = PseudoStates.Hover;
        var hovered = CascadeTestHelper.ResolveTree(engine, root)[btn];
        Assert.Equal("23,23,23,230", Rgba(hovered.BackgroundColor.Value)); // color-mix(in oklab, var(--primary) 90%, transparent)
        var p = map[(El)dark[0]];
        Assert.Equal("10,10,10,255", Rgba(p.BackgroundColor.Value)); // .dark --background: oklch(0.145 0 0)
        Assert.Equal("250,250,250,255", Rgba(p.Color.Value));
        ring.State = PseudoStates.FocusVisible | PseudoStates.Focus;
        var r = CascadeTestHelper.ResolveTree(engine, root)[ring];
        Assert.False(r.BoxShadow.IsNone);
        Assert.Contains(r.BoxShadow.Shadows, s => s.Spread == 3 && Rgba(r.ResolveColor(s.Color)) == "161,161,161,128");
    }
}
