using AwAUI.Css.Cascade;
using AwAUI.Css.Media;
using AwAUI.Css.Values;

namespace AwAUI.Css.UnitTests.Cascade;

/// <summary>评审中发现的问题的回归测试。</summary>
public class ReviewRegressionTests
{
    static (ComputedStyle Parent, ComputedStyle Child) Resolve(string css, CssEnvironment? env = null, string? inline = null)
    {
        var e = new StyleEngine(env);
        e.AddStyleSheet(StyleSheet.Parse(css));
        var child = new El("div", "x", "a");
        if (inline is not null) child.Style(inline);
        var root = new El("section").Add(child);
        var ps = e.Resolve(root, null);
        return (ps, e.Resolve(child, ps));
    }

    [Fact]
    public void Layers_inside_non_matching_media_do_not_affect_layer_order()
    {
        // CSS Cascade 5：条件不成立的 @media 中的 @layer 不参与层排序
        var css = "@media (min-width: 5000px) { @layer b { } } @layer a { div { width: 1px } } @layer b { div { width: 2px } }";
        var (_, c) = Resolve(css);
        Assert.Equal("2px", c.GetPropertyValueText("width"));
    }

    [Fact]
    public void Negative_calc_in_non_negative_number_is_clamped()
    {
        var (_, c) = Resolve("div { flex-grow: calc(-2); flex-shrink: calc(1 - 3) }");
        Assert.Equal(0f, c.FlexGrow);
        Assert.Equal(0f, c.FlexShrink);
    }

    [Fact]
    public void All_with_var_is_valid()
    {
        var (_, c) = Resolve("section { color: red } div { color: blue; width: 3px } #x { --k: inherit; all: var(--undefined, initial) }");
        Assert.Equal("rgb(0, 0, 0)", c.GetPropertyValueText("color"));
        Assert.Equal("auto", c.GetPropertyValueText("width"));
    }

    [Fact]
    public void Layer_order_follows_media_changes()
    {
        var env = new CssEnvironment(new MediaEnvironment { Width = 400 });
        var e = new StyleEngine(env);
        e.AddStyleSheet(StyleSheet.Parse("@media (min-width: 600px) { @layer b { } } @layer a { div { width: 1px } } @layer b { div { width: 2px } }"));
        var div = new El("div");
        Assert.Equal("2px", e.Resolve(div, null).GetPropertyValueText("width"));
        env.Media.Width = 800;
        e.EnvironmentChanged();
        // 条件成立后 b 先于 a 声明：a 在后，优先级更高
        Assert.Equal("1px", e.Resolve(div, null).GetPropertyValueText("width"));
        env.Media.Width = 400;
        e.EnvironmentChanged();
        Assert.Equal("2px", e.Resolve(div, null).GetPropertyValueText("width"));
    }

    [Fact]
    public void Environment_change_is_reported_even_when_engine_needs_recompiling()
    {
        // 第一次变化让含 @layer 的条件翻转（引擎需要重编译）；不解析就再次变化，另一个 @media 翻转也必须被报告
        var env = new CssEnvironment(new MediaEnvironment { Width = 400 });
        var e = new StyleEngine(env);
        e.AddStyleSheet(StyleSheet.Parse("@media (min-width: 600px) { @layer b { } } @media (prefers-color-scheme: dark) { div { color: white } }"));
        var div = new El("div");
        e.Resolve(div, null);
        env.Media.Width = 800;
        Assert.True(e.EnvironmentChanged());
        env.Media.ColorScheme = MediaColorScheme.Dark;
        Assert.True(e.EnvironmentChanged());
        Assert.Equal("rgb(255, 255, 255)", e.Resolve(div, null).GetPropertyValueText("color"));
        Assert.False(e.EnvironmentChanged());
    }

    [Fact]
    public void Layer_statement_in_nested_non_matching_media_is_ignored()
    {
        var css = "@media screen { @media (min-width: 5000px) { @layer b, a; } } @layer a { div { width: 1px } } @layer b { div { width: 2px } }";
        Assert.Equal("2px", Resolve(css).Child.GetPropertyValueText("width"));
        var css2 = "@media screen { @layer b, a; } @layer a { div { width: 1px } } @layer b { div { width: 2px } }";
        Assert.Equal("1px", Resolve(css2).Child.GetPropertyValueText("width"));
    }

    [Fact]
    public void Cycle_through_unused_fallback_is_detected()
    {
        // --a 引用自身（在未被使用的回退中）：按依赖图仍构成循环，--a 无效
        var (_, c) = Resolve("div { --x: 1px; --a: var(--x, var(--a)); --b: var(--c, 1px); --c: var(--x, var(--b)); --d: var(--x, var(--nope)) }");
        Assert.Null(c.GetCustomPropertyText("--a"));
        Assert.Null(c.GetCustomPropertyText("--b"));
        Assert.Null(c.GetCustomPropertyText("--c"));
        Assert.Equal("1px", c.GetCustomPropertyText("--d"));
    }

    [Fact]
    public void Font_family_rejects_reserved_keywords_in_any_identifier_position()
    {
        Assert.True(PropertyDeclarationBlock.Parse(StyleSheet.ParseDeclarations("font-family: Foo inherit")).IsEmpty);
        Assert.True(PropertyDeclarationBlock.Parse(StyleSheet.ParseDeclarations("font-family: Foo default, serif")).IsEmpty);
        Assert.False(PropertyDeclarationBlock.Parse(StyleSheet.ParseDeclarations("font-family: Foo Serif Pro, serif")).IsEmpty);
    }

    [Fact]
    public void Long_dotted_layer_name_is_linear()
    {
        // 完整层名曾在建树时逐级拼接：n 段的名称分配 O(n²) 字符（2 万段约 770 MB）
        var sb = new System.Text.StringBuilder("@layer a");
        for (int i = 0; i < 20000; i++) sb.Append(".a");
        sb.Append(" { div { width: 1px } }");
        var sheet = StyleSheet.Parse(sb.ToString());
#if NET
        long before = GC.GetAllocatedBytesForCurrentThread();
#endif
        var e = new StyleEngine();
        e.AddStyleSheet(sheet);
        Assert.Equal("1px", e.Resolve(new El("div"), null).GetPropertyValueText("width"));
#if NET
        Assert.True(GC.GetAllocatedBytesForCurrentThread() - before < 64L * 1024 * 1024);
#endif
        var record = new StyleMatchRecord();
        e.Resolve(new El("div"), null, record);
        Assert.Equal(20001 * 2 - 1, record.MatchedRules[0].LayerName!.Length);
    }

    [Fact]
    public void Layer_display_names_include_anonymous_ancestors()
    {
        var e = new StyleEngine();
        e.AddStyleSheet(StyleSheet.Parse("@layer { @layer x { div { width: 1px } } } @layer a { @layer { span { width: 1px } } }"));
        var record = new StyleMatchRecord();
        e.Resolve(new El("div"), null, record);
        Assert.Equal("<anonymous>.x", record.MatchedRules[0].LayerName);
        e.Resolve(new El("span"), null, record);
        Assert.Equal("a.<anonymous>", record.MatchedRules[0].LayerName);
    }

    [Fact]
    public void Match_record_reports_the_most_specific_matching_selector()
    {
        var e = new StyleEngine();
        e.AddStyleSheet(StyleSheet.Parse("div, #x, .a { color: red }"));
        var record = new StyleMatchRecord();
        e.Resolve(new El("div", "x", "a"), null, record);
        var rule = Assert.Single(record.MatchedRules);
        Assert.Equal(new AwAUI.Css.Selectors.Specificity(1, 0, 0), rule.Specificity);
        Assert.Equal("#x", rule.MatchedSelector!.ToString());
        Assert.Same(rule, record.GetWinner(CssPropertyId.Color)!.Rule);
    }
}

public class ReviewValueTextTests
{
    [Fact]
    public void Unit_texts_round_trip_for_every_unit()
    {
        // 单位文本表必须与枚举顺序一致：每个非百分比单位的文本都能解析回同一单位
        for (var u = CssLengthUnit.Px; u < CssLengthUnit.Percent; u++)
        {
            Assert.True(CssLength.TryParseUnit(CssLength.UnitText(u), out var back), u.ToString());
            Assert.Equal(u, back);
        }
        Assert.Equal("%", CssLength.UnitText(CssLengthUnit.Percent));
        Assert.Equal("revert-layer", CssWideKeywordValue.RevertLayer.ToString());
        Assert.Equal("unset", CssWideKeywordValue.Unset.ToString());
    }
}
