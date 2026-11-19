using AwAUI.Css.Cascade;
using AwAUI.Css.Selectors;
using AwAUI.Css.Values;

namespace AwAUI.Css.UnitTests.Cascade;

public class MatchRecordTests
{
    [Fact]
    public void Records_matched_rules_in_priority_order_with_winners()
    {
        var e = new StyleEngine();
        var ua = e.AddStyleSheet(StyleSheet.Parse("div { color: gray; display: block }", "ua.css"), CascadeOrigin.UserAgent);
        var app = e.AddStyleSheet(StyleSheet.Parse(
            "@layer base { div { color: red; margin: 1px } }\n" +
            "@media (min-width: 1px) { .a { color: blue; width: nope; colr: x } }\n" +
            "#x, div { color: green !important }", "app.css"));
        var el = new El("div", "x", "a").Style("color: black; height: 2px");
        var record = new StyleMatchRecord();
        var s = e.Resolve(el, null, record);
        Assert.Equal("rgb(0, 128, 0)", s.GetPropertyValueText("color"));

        var rules = record.MatchedRules;
        Assert.Equal(5, rules.Count);
        // !important 的规则优先级最高
        Assert.Equal("#x, div", rules[0].SelectorText);
        Assert.Equal(new Specificity(1, 0, 0), rules[0].Specificity);
        Assert.Equal("#x", rules[0].MatchedSelector!.ToString());
        Assert.True(rules[1].IsInline);
        Assert.Equal(".a", rules[2].SelectorText);
        Assert.Single(rules[2].Media);
        Assert.Equal("base", rules[3].LayerName);
        Assert.Equal(CascadeOrigin.UserAgent, rules[4].Origin);
        Assert.Same(ua, rules[4].StyleSheet);
        Assert.Same(app, rules[0].StyleSheet);
        Assert.Equal(1, rules[3].SourceRange.Start.Line);

        var winner = record.GetWinner(CssPropertyId.Color)!;
        Assert.True(winner.Important);
        Assert.Same(rules[0], winner.Rule);
        var colors = record.Declarations.Where(d => d.PropertyName == "color").ToList();
        Assert.Equal(5, colors.Count);
        Assert.Equal(1, colors.Count(d => d.Status == DeclarationStatus.Active));
        Assert.Equal(4, colors.Count(d => d.Status == DeclarationStatus.Overridden));

        // 简写展开为长属性，各自有结果
        Assert.Equal(4, record.Declarations.Count(d => d.Declaration.Name == "margin"));
        Assert.Equal("margin", record.GetWinner(CssPropertyId.MarginTop)!.Declaration.Name);
        Assert.True(record.GetWinner(CssPropertyId.Height)!.Rule.IsInline);
        Assert.Equal("display", record.GetWinner(CssPropertyId.Display)!.PropertyName);

        // 无效与不支持的声明
        var invalid = record.Declarations.Single(d => d.Declaration.Name == "width");
        Assert.Equal(DeclarationStatus.Invalid, invalid.Status);
        Assert.NotNull(invalid.Reason);
        var unsupported = record.Declarations.Single(d => d.Declaration.Name == "colr");
        Assert.Equal(DeclarationStatus.Unsupported, unsupported.Status);
        Assert.Null(unsupported.PropertyId);
        Assert.Contains("color", unsupported.Reason);

        // 值层诊断记在样式表上
        Assert.Equal(2, app.Diagnostics.Count);
        Assert.Empty(ua.Diagnostics);
    }

    [Fact]
    public void Record_marks_invalid_at_computed_value_time()
    {
        var e = new StyleEngine();
        e.AddStyleSheet(StyleSheet.Parse("div { --a: var(--b); --b: var(--a); width: var(--missing); color: var(--ok); --ok: red }"));
        var record = new StyleMatchRecord();
        e.Resolve(new El("div"), null, record);
        Assert.Equal(DeclarationStatus.Invalid, record.Declarations.Single(d => d.PropertyName == "width").Status);
        Assert.Equal(DeclarationStatus.Active, record.Declarations.Single(d => d.PropertyName == "color").Status);
        Assert.Equal(DeclarationStatus.Invalid, record.GetWinner(CssPropertyId.Width) is null ? DeclarationStatus.Invalid : DeclarationStatus.Active);
        Assert.Equal(DeclarationStatus.Invalid, record.Declarations.Single(d => d.PropertyName == "--a").Status);
        Assert.NotNull(record.GetWinner("--ok"));
    }

    [Fact]
    public void Record_is_reusable_and_matches_fast_path()
    {
        var e = new StyleEngine();
        e.AddStyleSheet(StyleSheet.Parse(".a { color: red } .b { color: blue; width: 3px }"));
        var record = new StyleMatchRecord();
        var a = new El("div", classes: "a");
        var b = new El("div", classes: "a b");
        var sa = e.Resolve(a, null, record);
        Assert.Single(record.MatchedRules);
        var sb = e.Resolve(b, null, record);
        Assert.Equal(2, record.MatchedRules.Count);
        Assert.Equal(StyleDifference.None, ComputedStyle.Diff(sb, e.Resolve(b, null)));
        Assert.Equal(StyleDifference.None, ComputedStyle.Diff(sa, e.Resolve(a, null)));
    }

    [Fact]
    public void Record_for_pseudo_element()
    {
        var e = new StyleEngine();
        e.AddStyleSheet(StyleSheet.Parse("div::after { color: red } div { color: blue }"));
        var record = new StyleMatchRecord();
        var el = new El("div");
        var s = e.Resolve(el, null);
        e.Resolve(el, s, PseudoElementKind.After, record);
        Assert.Equal("div::after", Assert.Single(record.MatchedRules).SelectorText);
    }

    [Fact]
    public void Computed_values_serialize_for_every_property()
    {
        var e = new StyleEngine();
        e.AddStyleSheet(StyleSheet.Parse("div { border: 1px solid red; background: linear-gradient(red, blue); box-shadow: 1px 2px; font: bold 12px/2 serif }"));
        var s = e.Resolve(new El("div"), null);
        foreach (var p in CssProperties.All)
        {
            var text = s.GetPropertyValueText(p.Name);
            if (p.IsShorthand) Assert.Null(text);
            else Assert.False(string.IsNullOrEmpty(text), p.Name);
        }
        Assert.Null(s.GetPropertyValueText("--nope"));
        Assert.Null(s.GetPropertyValueText("not-a-property"));
    }
}
