using AwAUI.Css;

namespace AwAUI.DevTools.Tests;

/// <summary>编辑的往返细节：!important 不能在编辑值时被悄悄丢掉；没有改动的提交不发请求。</summary>
public class EditRoundTripTests
{
    static StyleSheet[] SheetsWithImportant() =>
        Harness.CounterSheets().Append(StyleSheet.Parse("#count { color: #00ff00 !important; }", "important.css")).ToArray();

    static StyleBlock CountIdRule(Harness h) => h.Model.Styles!.Sections[0].Blocks.Single(b => b.Selector == "#count");

    [Fact]
    public void Editing_the_value_of_an_important_declaration_keeps_it_important()
    {
        using var h = new Harness(sheets: SheetsWithImportant());
        h.Open();
        var count = h.AppById("count");
        h.ClickPanel(h.PanelRow(count.UniqueId)!);
        var rule = CountIdRule(h);
        var decl = rule.Decls.Single(d => d.Name == "color");
        Assert.True(decl.Important);

        // 编辑框里的初始文本就是显示的值（含 !important，同浏览器）；用户只改颜色部分
        h.Model.BeginEdit(rule, rule.Decls.IndexOf(decl));
        Assert.Equal("#00ff00 !important", h.Model.EditDraft);
        h.Model.CommitEdit(h.Model.EditDraft.Replace("#00ff00", "#ff0000"));
        h.Settle();

        var after = CountIdRule(h).Decls.Single(d => d.Name == "color");
        Assert.Equal("#ff0000", after.Value);
        Assert.True(after.Important, "!important was dropped by the edit");
        Assert.True(h.CountAppPixels(count.BoundingRect, Harness.IsRed) > 20);
    }

    [Fact]
    public void Committing_an_unchanged_value_sends_no_request()
    {
        using var h = new Harness(sheets: SheetsWithImportant());
        h.Open();
        h.ClickPanel(h.PanelRow(h.AppById("count").UniqueId)!);
        var rule = CountIdRule(h);
        int requests = h.Model.RequestCount;
        h.Model.BeginEdit(rule, rule.Decls.FindIndex(d => d.Name == "color"));
        h.Model.CommitEdit(h.Model.EditDraft);
        h.Settle();
        Assert.Equal(requests, h.Model.RequestCount);
        Assert.Null(h.Model.Editing);
    }
}

public class EditDeferredRefreshTests
{
    [Fact]
    public void A_refresh_deferred_during_an_edit_still_happens_when_the_edit_changes_nothing()
    {
        using var h = new Harness();
        h.Open();
        var count = h.AppById("count");
        h.ClickPanel(h.PanelRow(count.UniqueId)!);
        var rule = h.Model.Styles!.Sections[0].Blocks.Single(b => b.Selector == ".count");
        h.Model.BeginEdit(rule, rule.Decls.FindIndex(d => d.Name == "color"));
        h.Settle();
        // 编辑期间应用变化（文本变了 → textChanged → 刷新被推迟）
        int requests = h.Model.RequestCount;
        h.ClickApp(h.AppById("increment"));
        Assert.Equal(requests, h.Model.RequestCount);
        // 原样提交：没有修改要发，但推迟的刷新要补上（getMatchedRules + getComputedStyle + getBoxModel）
        h.Model.CommitEdit(h.Model.EditDraft);
        h.Settle();
        Assert.Equal(requests + 3, h.Model.RequestCount);
    }
}
