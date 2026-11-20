using AwAUI.Css.Cascade;

namespace AwAUI.Css.UnitTests.Cascade;

/// <summary>表现提示层级：位于 UA 普通声明与作者普通声明之间，特异性 0（CSS Cascade 5 §6.1）。</summary>
public class PresentationalHintTests
{
    static El Img(string hints) => new El("img") { Hints = CssInlineStyle.Parse(hints) };

    [Fact]
    public void Hints_override_user_agent_and_lose_to_any_author_rule()
    {
        var img = Img("width: 100px; height: 50px; aspect-ratio: auto 100 / 50");
        var root = new El("body").Add(img);
        var engine = CascadeTestHelper.Engine("img { height: auto }", uaCss: "img { width: 1px; height: 1px }");
        var map = CascadeTestHelper.ResolveTree(engine, root);
        var s = map[img];
        Assert.Equal("100px", s.GetPropertyValueText("width")); // 提示胜过 UA
        Assert.Equal("auto", s.GetPropertyValueText("height")); // 任何作者规则胜过提示（特异性 0 也一样）
        Assert.Equal("auto 100 / 50", s.GetPropertyValueText("aspect-ratio"));
    }

    [Fact]
    public void Inline_style_and_universal_rules_beat_hints()
    {
        var img = Img("width: 100px; height: 50px");
        img.Style("width: 200px");
        var root = new El("body").Add(img);
        var engine = CascadeTestHelper.Engine("* { height: 10px }");
        var s = CascadeTestHelper.ResolveTree(engine, root)[img];
        Assert.Equal("200px", s.GetPropertyValueText("width"));
        Assert.Equal("10px", s.GetPropertyValueText("height"));
    }

    [Fact]
    public void Important_in_hints_is_ignored_and_revert_skips_hints()
    {
        var img = Img("width: 100px !important; height: 50px");
        var root = new El("body").Add(img);
        var engine = CascadeTestHelper.Engine("img { height: revert }", uaCss: "img { height: 7px }");
        var s = CascadeTestHelper.ResolveTree(engine, root)[img];
        Assert.Equal("auto", s.GetPropertyValueText("width"));
        // revert 回到 UA 来源：表现提示属于作者来源，同样被跳过
        Assert.Equal("7px", s.GetPropertyValueText("height"));
    }

    [Fact]
    public void Hints_do_not_apply_to_pseudo_elements_and_show_in_match_record()
    {
        var img = Img("color: red");
        var root = new El("body").Add(img);
        var engine = CascadeTestHelper.Engine("");
        var rs = engine.Resolve(root, null);
        var record = new StyleMatchRecord();
        var s = engine.Resolve(img, rs, record);
        Assert.Equal("rgb(255, 0, 0)", s.GetPropertyValueText("color"));
        Assert.Contains(record.MatchedRules, r => r.SelectorText == "presentational hints");
        Assert.Equal("rgb(255, 0, 0)", engine.Resolve(img, s, Selectors.PseudoElementKind.Before, null).GetPropertyValueText("color")); // 继承自原始元素
    }
}
