using AwAUI.Css.Cascade;
using AwAUI.Css.Media;
using AwAUI.Css.Selectors;
using AwAUI.Css.Values;

namespace AwAUI.Css.UnitTests.Cascade;

/// <summary><c>@supports</c>、<c>@starting-style</c>、<c>@container</c> 与新的媒体特性。</summary>
public class ConditionalRuleTests
{
    static bool Supports(string condition)
    {
        var s = StyleSheet.Parse($"@supports {condition} {{ a {{ color: red }} }}");
        return Assert.IsType<CssSupportsRule>(Assert.Single(s.Rules)).Matches;
    }

    [Theory]
    [InlineData("(display: grid)", true)]
    [InlineData("(display: grid !important)", true)]
    [InlineData("(display: foo)", false)]
    [InlineData("(float: left)", false)]
    [InlineData("(--x: anything at all)", true)]
    [InlineData("(color: oklch(0.5 0.1 120))", true)]
    [InlineData("(color: rgb(from red r g b))", true)]
    [InlineData("(background-image: linear-gradient(in lab, red, red))", true)]
    [InlineData("(-webkit-hyphens: none)", true)]
    [InlineData("(margin-trim: inline)", false)]
    [InlineData("not (margin-trim: inline)", true)]
    [InlineData("(display: grid) and (not (display: inline-grid))", false)]
    [InlineData("(display: grid) or (foo: bar)", true)]
    [InlineData("((display: grid) and (color: red)) or (x: y)", true)]
    [InlineData("selector(:has(> img))", true)]
    [InlineData("selector(a:hover::before)", true)]
    [InlineData("selector(:foo)", false)]
    [InlineData("selector(a, b)", false)]
    [InlineData("font-tech(color-COLRv1)", false)]
    [InlineData("(foo bar)", false)]
    public void Supports_conditions(string condition, bool expected) => Assert.Equal(expected, Supports(condition));

    [Fact]
    public void Tailwind_property_fallback_condition_is_evaluated_like_chrome()
    {
        // Tailwind v4 的 @layer properties 回退：Chrome 中前半成立（支持 -webkit-hyphens、不支持 margin-trim）
        Assert.True(Supports("((-webkit-hyphens: none) and (not (margin-trim: inline))) or ((-moz-orient: inline) and (not (color:rgb(from red r g b))))"));
    }

    [Fact]
    public void Invalid_supports_condition_never_applies()
    {
        var s = StyleSheet.Parse("@supports (a: b) and (c: d) or (e: f) { a { color: red } }");
        var rule = Assert.IsType<CssSupportsRule>(Assert.Single(s.Rules));
        Assert.False(rule.Matches);
        Assert.Contains(s.Diagnostics, d => d.Code == CssDiagnosticCodes.InvalidAtRule);
    }

    [Fact]
    public void Supports_rules_gate_the_cascade_including_nested()
    {
        var engine = CascadeTestHelper.Engine("@supports (display: grid) { .a { color: red } } @supports (display: nope) { .a { color: blue } }"
            + " .b { color: green; @supports (display: flex) { color: purple } @supports not (display: flex) { color: orange } }");
        var root = new El("div").Add(new El("p", null, "a"), new El("p", null, "b"));
        var map = CascadeTestHelper.ResolveTree(engine, root);
        Assert.Equal("rgb(255, 0, 0)", map[(El)root[0]].GetPropertyValueText("color"));
        Assert.Equal("rgb(128, 0, 128)", map[(El)root[1]].GetPropertyValueText("color"));
        Assert.Empty(StyleSheet.Parse("@supports (display: grid) { .a { color: red } }").Diagnostics);
    }

    [Fact]
    public void Import_supports_condition_controls_layer_registration()
    {
        var s = StyleSheet.Parse("@import 'a.css' layer(x) supports(display: nope); @import 'b.css' supports(selector(:has(a)));");
        Assert.False(((CssImportRule)s.Rules[0]).SupportsMatches);
        Assert.True(((CssImportRule)s.Rules[1]).SupportsMatches);
    }

    [Fact]
    public void Starting_style_applies_only_to_starting_style_resolution()
    {
        var engine = CascadeTestHelper.Engine(".a { opacity: 1; @starting-style { opacity: 0 } } @starting-style { .b { color: red } } .b { color: blue }");
        var a = new El("div", null, "a");
        var b = new El("div", null, "b");
        var root = new El("body").Add(a, b);
        var rs = engine.Resolve(root, null);
        Assert.Equal(1f, engine.Resolve(a, rs).Opacity);
        Assert.Equal(0f, engine.ResolveStartingStyle(a, rs).Opacity);
        // @starting-style 中的规则按其原有的级联位置：.b 的 blue 在后，胜出
        Assert.Equal("rgb(0, 0, 255)", engine.ResolveStartingStyle(b, rs).GetPropertyValueText("color"));
        Assert.True(engine.HasStartingStyle(a));
        Assert.False(engine.HasStartingStyle(root));
        var rule = StyleSheet.Parse("@starting-style { .x { color: red } }").Rules[0];
        Assert.Equal(CssRuleKind.StartingStyle, rule.Kind);
    }

    // ---------------- @container ----------------

    static (StyleEngine Engine, El Root, El Container, El Item) ContainerTree(string css, (float, float)? size, string containerCss = "")
    {
        var engine = CascadeTestHelper.Engine(".c { container-type: inline-size } .s { container-type: size } .n { container-name: card } " + containerCss + " " + css);
        var item = new El("p", null, "item");
        var container = new El("div", null, "c n").Add(new El("div").Add(item));
        var root = new El("body").Add(container);
        container.ContainerSize = size;
        return (engine, root, container, item);
    }

    static ComputedStyle Restyle(StyleEngine engine, El root)
    {
        // 两遍：第一遍得到容器的计算样式（宿主保存为 CurrentStyle），第二遍求值容器查询
        ComputedStyle? last = null;
        for (int pass = 0; pass < 2; pass++)
        {
            void Walk(El e, ComputedStyle? parent)
            {
                var s = engine.Resolve(e, parent);
                e.Style0 = s;
                last = s;
                foreach (var c in e.Children) Walk(c, s);
            }
            Walk(root, null);
        }
        return last!;
    }

    [Theory]
    [InlineData("(width > 400px)", 500, true)]
    [InlineData("(width > 400px)", 300, false)]
    [InlineData("(400px <= width <= 600px)", 500, true)]
    [InlineData("(min-width: 30em)", 500, true)]
    [InlineData("(inline-size < 25rem)", 300, true)]
    [InlineData("not (width < 100px)", 300, true)]
    [InlineData("(width > 100px) and (width < 200px)", 150, true)]
    [InlineData("(width > 1000px) or (width < 200px)", 150, true)]
    [InlineData("card (width > 100px)", 150, true)]
    [InlineData("other (width > 100px)", 150, false)]
    [InlineData("(height > 10px)", 150, false)]
    [InlineData("style(--x: 1)", 150, false)]
    public void Container_size_queries(string query, float width, bool matches)
    {
        var (engine, root, _, _) = ContainerTree($"@container {query} {{ .item {{ color: red }} }}", (width, 100));
        var s = Restyle(engine, root);
        Assert.Equal(matches ? "rgb(255, 0, 0)" : "rgb(0, 0, 0)", s.GetPropertyValueText("color"));
        Assert.NotEqual(0, (int)(s.Flags & ComputedStyleFlags.DependsOnContainer));
    }

    [Fact]
    public void Size_container_supports_block_queries_and_unknown_size_never_matches()
    {
        var (engine, root, container, _) = ContainerTree("@container (height >= 100px) and (orientation: landscape) { .item { color: red } }", (300, 100), ".c { container-type: size }");
        Assert.Equal("rgb(255, 0, 0)", Restyle(engine, root).GetPropertyValueText("color"));
        container.ContainerSize = null; // 尚未布局
        Assert.Equal("rgb(0, 0, 0)", Restyle(engine, root).GetPropertyValueText("color"));
        Assert.True(engine.HasContainerQueries);
    }

    [Fact]
    public void Nested_container_rules_and_nesting_inside_style_rules()
    {
        var (engine, root, _, _) = ContainerTree("@container (width > 100px) { @container card (width < 400px) { .item { color: red } } }"
            + " .item { @container (width > 1px) { font-size: 20px } }", (200, 0));
        var s = Restyle(engine, root);
        Assert.Equal("rgb(255, 0, 0)", s.GetPropertyValueText("color"));
        Assert.Equal("20px", s.GetPropertyValueText("font-size"));
    }

    [Fact]
    public void Container_units_resolve_against_the_nearest_container()
    {
        var (engine, root, _, item) = ContainerTree(".item { width: 50cqw; height: 10cqh; min-width: 10cqmin; padding-left: 1cqi }", (400, 300), ".c { container-type: size }");
        var s = Restyle(engine, root);
        Assert.Equal("200px", s.GetPropertyValueText("width"));
        Assert.Equal("30px", s.GetPropertyValueText("height"));
        Assert.Equal("30px", s.GetPropertyValueText("min-width"));
        Assert.Equal("4px", s.GetPropertyValueText("padding-left"));
        Assert.NotEqual(0, (int)(s.Flags & ComputedStyleFlags.DependsOnContainer));
        // 没有容器时按小视口
        var engine2 = CascadeTestHelper.Engine("p { width: 10cqw }", env: new CssEnvironment { SmallViewportWidthOverride = 500 });
        Assert.Equal("50px", engine2.Resolve(new El("p"), null).GetPropertyValueText("width"));
        Assert.True(item.Style0 is not null);
    }

    [Theory]
    [InlineData("@container { }")]
    [InlineData("@container none (width > 1px) { }")]
    [InlineData("@container card { }")]
    [InlineData("@container (width > 1px) and (width > 2px) or (width > 3px) { }")]
    public void Invalid_container_rules_are_dropped(string css)
    {
        var s = StyleSheet.Parse(css);
        Assert.Empty(s.Rules);
        Assert.Contains(s.Diagnostics, d => d.Code == CssDiagnosticCodes.InvalidAtRule);
    }

    [Fact]
    public void Shadcn_card_header_container_variant()
    {
        // shadcn 的 CardHeader：@container/card-header + @[...]:
        var (engine, _, _, _) = ContainerTree(".\\@container\\/card-header { container-type: inline-size; container-name: card-header }"
            + " @container card-header (width >= 20rem) { .item { display: grid } }", (400, 0));
        var e2 = new El("div", null, "@container/card-header") { ContainerSize = (400, 0) };
        var it = new El("p", null, "item");
        e2.Add(it);
        var r2 = new El("body").Add(e2);
        Assert.Equal(CssDisplay.Grid, Restyle(engine, r2).Display);
    }

    // ---------------- 媒体特性 ----------------

    [Theory]
    [InlineData("(forced-colors: active)", false)]
    [InlineData("(forced-colors: none)", true)]
    [InlineData("(prefers-contrast: more)", false)]
    [InlineData("(prefers-contrast)", false)]
    [InlineData("(inverted-colors: none)", true)]
    [InlineData("(scripting: enabled)", true)]
    [InlineData("(scripting: none)", false)]
    [InlineData("(prefers-reduced-transparency: no-preference)", true)]
    [InlineData("(color-gamut: srgb)", true)]
    [InlineData("(color-gamut: p3)", false)]
    [InlineData("(dynamic-range: standard)", true)]
    [InlineData("(update: fast)", true)]
    [InlineData("(display-mode: standalone)", true)]
    public void New_media_features_default_environment(string query, bool expected)
    {
        var diags = new List<CssDiagnostic>();
        var mq = MediaQueryList.Parse(query, diags);
        Assert.Empty(diags);
        Assert.Equal(expected, mq.Matches(new MediaEnvironment()));
    }

    [Fact]
    public void New_media_features_follow_the_environment()
    {
        var env = new MediaEnvironment
        {
            ForcedColors = true, PrefersContrast = MediaContrastPreference.More, InvertedColors = true, Scripting = MediaScripting.None,
            PrefersReducedTransparency = true, ColorGamut = MediaColorGamut.P3, HighDynamicRange = true,
        };
        foreach (var q in new[] { "(forced-colors: active)", "(prefers-contrast: more)", "(prefers-contrast)", "(inverted-colors)", "(scripting: none)",
                     "(prefers-reduced-transparency)", "(color-gamut: p3)", "(color-gamut: srgb)", "(dynamic-range: high)" })
            Assert.True(MediaQueryList.Parse(q).Matches(env), q);
        Assert.False(MediaQueryList.Parse("(color-gamut: rec2020)").Matches(env));
        Assert.False(MediaQueryList.Parse("(scripting)").Matches(env));
    }

    [Fact]
    public void Forced_colors_rule_toggles_with_environment_changes()
    {
        var css = new CssEnvironment();
        var engine = CascadeTestHelper.Engine("a { color: red } @media (forced-colors: active) { a { color: blue } }", env: css);
        var a = new El("a");
        Assert.Equal("rgb(255, 0, 0)", engine.Resolve(a, null).GetPropertyValueText("color"));
        css.Media.ForcedColors = true;
        Assert.True(engine.EnvironmentChanged());
        Assert.Equal("rgb(0, 0, 255)", engine.Resolve(a, null).GetPropertyValueText("color"));
    }
}
