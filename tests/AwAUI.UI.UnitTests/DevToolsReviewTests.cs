using AwAUI.Diagnostics.Json;
using AwAUI.Graphics;

namespace AwAUI.UI.UnitTests;

/// <summary>评审补充：DevTools 写入的文本不能改变样式表的规则结构。</summary>
public partial class DevToolsTests
{
    [Theory]
    [InlineData("color: red; @media screen")]
    [InlineData("color: red; @font-face")]
    [InlineData("color: red; .x &")]
    public void Set_style_text_cannot_change_the_rule_structure_with_at_rules_or_nested_selectors(string text)
    {
        var f = new Fixture();
        var rule = f.Client.Call("Css.getMatchedRules", DevToolsClient.Node(f.Id("i2")))["matched"]!.AsArray()
            .Single(m => m["rule"]!["selectorText"]!.GetString() == ".item")["rule"]!;
        string sheetId = rule["sheetId"]!.GetString();
        string before = f.Client.Call("Css.getStyleSheetText", new JsonObject { ["sheetId"] = sheetId })["text"]!.GetString();
        long code = 0;
        try
        {
            code = f.Client.Fail("Css.setStyleText", new JsonObject
            {
                ["target"] = new JsonObject { ["ruleId"] = rule["id"]!.GetString() },
                ["text"] = text,
            });
        }
        catch (Exception)
        {
            // 接受了：之后的规则必须仍然生效
        }
        f.Frame();
        string after = f.Client.Call("Css.getStyleSheetText", new JsonObject { ["sheetId"] = sheetId })["text"]!.GetString();
        Assert.True(code is 0 or -32005, "unexpected error " + code);
        // 无论接受与否，.item.hot 等后续规则仍在顶层生效
        var sheet = AwAUI.Css.StyleSheet.Parse(after);
        Assert.Equal(AwAUI.Css.StyleSheet.Parse(before).Rules.Count, sheet.Rules.Count);
    }
}
