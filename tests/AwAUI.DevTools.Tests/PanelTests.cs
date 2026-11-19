using AwAUI.Diagnostics.Inspection;
using AwAUI.Platform;
using AwAUI.UI;

namespace AwAUI.DevTools.Tests;

/// <summary>
/// 面板的端到端行为：两个无窗口窗口（Counter 应用 + 面板），面板只经协议与应用通信。
/// 断言同时看面板的状态、面板自己渲染出的元素，以及应用帧缓冲里的像素。
/// </summary>
public class PanelTests
{
    [Fact]
    public void Tree_shows_the_counter_elements()
    {
        using var h = new Harness();
        h.Open();
        var m = h.Model;
        // 应用里每个元素都在镜像里（getDocument depth -1，树很小）
        foreach (var e in Harness.Descendants(h.App.Root.RootElement))
            Assert.True(m.Tree.Get(e.UniqueId) is not null, "missing " + e);
        // 关键元素的行已经渲染在面板窗口里，标签文字带 id / class
        foreach (var id in new[] { "count", "increment", "decrement", "name", "greet" })
        {
            var row = h.PanelRow(h.AppById(id).UniqueId);
            Assert.True(row is not null, "no panel row for #" + id);
            Assert.Contains("id=\"" + id + "\"", row!.TextContent);
        }
        var countRow = h.PanelRow(h.AppById("count").UniqueId)!;
        Assert.Contains("Count: 0", countRow.TextContent);      // 唯一的短文本子节点画在同一行
        Assert.Contains("class=\"count\"", countRow.TextContent);
        // 组件的第一个元素旁标出组件名
        Assert.Contains(h.PanelRows(), r => Harness.Descendants(r).Any(e => e.HasClass("t-comp") && e.TextContent.Contains("Counter")));
        Assert.True(h.PanelWindow.Framebuffer is not null);
    }

    [Fact]
    public void Tree_follows_app_changes_through_protocol_events()
    {
        using var h = new Harness();
        h.Open();
        var countId = h.AppById("count").UniqueId;
        h.ClickApp(h.AppById("increment"));
        h.Settle();
        Assert.Contains("Count: 1", h.PanelRow(countId)!.TextContent);
        // 新出现的元素（提交问候后 .hint 变成 #greeting）以 childrenChanged 到达
        h.ClickApp(h.AppById("name"));
        h.AppWindow.SendText("Ada");
        Harness.Key(h.AppWindow, PhysicalKey.Enter);
        h.Settle();
        var greeting = h.AppById("greeting");
        Assert.NotNull(h.Model.Tree.Get(greeting.UniqueId));
        Assert.NotNull(h.PanelRow(greeting.UniqueId));
    }

    [Fact]
    public void Selecting_a_row_highlights_the_element_in_the_app_frame()
    {
        using var h = new Harness();
        h.Open();
        var target = h.AppById("increment");
        var probe = new Point(target.BoundingRect.X + 6, target.BoundingRect.Center.Y);
        var far = new Point(4, 4);
        var before = h.AppPixel(probe);
        var farBefore = h.AppPixel(far);

        h.ClickPanel(h.PanelRow(target.UniqueId)!);
        Assert.Equal(target.UniqueId, h.P.SelectedNodeId);
        Assert.Same(target, h.App.DevTools!.HighlightedElement);
        Assert.Contains("selected", h.PanelRow(target.UniqueId)!.ClassName);
        var after = h.AppPixel(probe);
        Assert.True(Distance(before, after) > 0.15f, $"highlight did not change the app pixel: {before} → {after}");
        Assert.True(Distance(farBefore, h.AppPixel(far)) < 0.01f, "highlight leaked outside the element");
        // 盒模型来自 Layout.getBoxModel
        Assert.NotNull(h.Model.Box);
        Assert.Equal(target.BoundingRect.Width, h.Model.Box!.Width, 1);
        Assert.Contains("button.primary", h.Model.Styles!.Sections[0].Blocks.Select(b => b.Selector));
    }

    [Fact]
    public void Hovering_rows_highlights_them_and_leaving_returns_to_the_selection()
    {
        using var h = new Harness();
        h.Open();
        var inc = h.AppById("increment");
        var dec = h.AppById("decrement");
        h.ClickPanel(h.PanelRow(inc.UniqueId)!);
        var decRow = h.PanelRow(dec.UniqueId)!;
        h.MovePanel(decRow.BoundingRect.Center);
        Assert.Same(dec, h.App.DevTools!.HighlightedElement);
        h.LeavePanel();
        Assert.Same(inc, h.App.DevTools!.HighlightedElement);
    }

    [Fact]
    public void Editing_a_color_value_changes_the_app_pixels_and_can_be_toggled_and_reset()
    {
        using var h = new Harness();
        h.Open();
        var count = h.AppById("count");
        h.ClickPanel(h.PanelRow(count.UniqueId)!);
        var rule = h.Model.Styles!.Sections[0].Blocks.Single(b => b.Selector == ".count");
        Assert.Equal("#1e1b4b", rule.Decls.Single(d => d.Name == "color").Value);
        Assert.Equal(0, h.CountAppPixels(count.BoundingRect, Harness.IsRed));

        var value = RuleValue(h, rule.RuleId!, "color");
        h.ClickPanel(value);
        var edit = h.PanelById("dt-edit");
        Assert.NotNull(edit);
        Assert.Same(edit, h.P.Host!.Root.FocusManager.FocusedElement);
        for (int i = 0; i < "#1e1b4b".Length; i++) Harness.Key(h.PanelWindow, PhysicalKey.Backspace);
        h.PanelWindow.SendText("#ff0000");
        Harness.Key(h.PanelWindow, PhysicalKey.Enter);
        h.Settle();

        Assert.Null(h.PanelById("dt-edit"));
        Assert.Null(h.Model.StylesError);
        Assert.Equal("#ff0000", h.Model.Styles!.Sections[0].Blocks.Single(b => b.Selector == ".count").Decls.Single(d => d.Name == "color").Value);
        int red = h.CountAppPixels(count.BoundingRect, Harness.IsRed);
        Assert.True(red > 20, "the count text should now be red; red pixels: " + red);

        // 勾选框注释掉声明：颜色回到继承值
        var check = DeclElement(h, rule.RuleId!, "color").Children.First(c => c.HasClass("dt-check"));
        h.ClickPanel(check);
        var colorDecl = h.Model.Styles!.Sections[0].Blocks.Single(b => b.Selector == ".count").Decls.Single(d => d.Name == "color");
        Assert.True(colorDecl.Disabled, $"not disabled: {colorDecl.Value} {colorDecl.Status} error={h.Model.StylesError} requests={h.Model.RequestCount}");
        Assert.Equal(0, h.CountAppPixels(count.BoundingRect, Harness.IsRed));
        h.ClickPanel(DeclElement(h, rule.RuleId!, "color").Children.First(c => c.HasClass("dt-check")));
        Assert.True(h.CountAppPixels(count.BoundingRect, Harness.IsRed) > 20);

        // Reset edits = Css.clearOverrides：样式表恢复原样
        h.ClickPanel(h.PanelById("dt-reset")!);
        Assert.Equal(0, h.CountAppPixels(count.BoundingRect, Harness.IsRed));
        Assert.Equal("#1e1b4b", h.Model.Styles!.Sections[0].Blocks.Single(b => b.Selector == ".count").Decls.Single(d => d.Name == "color").Value);
    }

    [Fact]
    public void Adding_an_inline_property_changes_the_app_pixels()
    {
        using var h = new Harness();
        h.Open();
        var greet = h.AppById("greet");
        h.ClickPanel(h.PanelRow(greet.UniqueId)!);
        var inline = Harness.Descendants(h.PanelRoot).First(e => e.GetAttribute("data-rule") == "inline");
        var add = Harness.Descendants(inline).First(e => e.HasClass("dt-add"));
        h.ClickPanel(add);
        Assert.NotNull(h.PanelById("dt-edit"));
        h.PanelWindow.SendText("background-image: none");
        Harness.Key(h.PanelWindow, PhysicalKey.Enter);
        h.Settle();
        var add2 = Harness.Descendants(Harness.Descendants(h.PanelRoot).First(e => e.GetAttribute("data-rule") == "inline")).First(e => e.HasClass("dt-add"));
        h.ClickPanel(add2);
        h.PanelWindow.SendText("background-color: #ff0000");
        Harness.Key(h.PanelWindow, PhysicalKey.Enter);
        h.Settle();
        var inlineDecls = h.Model.Styles!.Sections[0].Blocks.Single(b => b.IsInline).Decls;
        Assert.Contains(inlineDecls, d => d.Name == "background-color" && d.Value == "#ff0000");
        Assert.True(h.CountAppPixels(greet.BoundingRect, Harness.IsRed) > 50);
    }

    [Fact]
    public void Escape_cancels_an_edit_and_invalid_text_is_reported()
    {
        using var h = new Harness();
        h.Open();
        var count = h.AppById("count");
        h.ClickPanel(h.PanelRow(count.UniqueId)!);
        var rule = h.Model.Styles!.Sections[0].Blocks.Single(b => b.Selector == ".count");
        h.ClickPanel(RuleValue(h, rule.RuleId!, "color"));
        h.PanelWindow.SendText("zzz");
        Harness.Key(h.PanelWindow, PhysicalKey.Escape);
        h.Settle();
        Assert.Null(h.PanelById("dt-edit"));
        Assert.Equal("#1e1b4b", h.Model.Styles!.Sections[0].Blocks.Single(b => b.Selector == ".count").Decls.Single(d => d.Name == "color").Value);

        // 含花括号的值会被 Agent 拒绝（-32005），面板显示错误而不是改坏样式表
        h.ClickPanel(RuleValue(h, rule.RuleId!, "color"));
        h.PanelWindow.SendText("} .x { color: red");
        Harness.Key(h.PanelWindow, PhysicalKey.Enter);
        h.Settle();
        Assert.True(h.Model.StylesError is not null, "expected an error; value now: " + h.Model.Styles!.Sections[0].Blocks.Single(b => b.Selector == ".count").Decls.Single(d => d.Name == "color").Value);
        Assert.NotNull(h.PanelById("dt-styles-error"));
        Assert.Equal(0, h.CountAppPixels(count.BoundingRect, Harness.IsRed));
    }

    [Fact]
    public void F12_in_the_app_opens_the_panel_and_the_pick_selects_the_element()
    {
        using var h = new Harness();
        Assert.False(h.P.IsOpen);
        Harness.Key(h.AppWindow, PhysicalKey.F12);
        h.Settle();
        Assert.True(h.P.IsOpen);
        Assert.Equal(InspectMode.Pick, h.App.DevTools!.InspectMode);
        var inc = h.AppById("increment");
        h.ClickApp(inc);
        h.Settle();
        Assert.Equal(inc.UniqueId, h.P.SelectedNodeId);
        Assert.NotNull(h.PanelRow(inc.UniqueId));
        Assert.Contains("selected", h.PanelRow(inc.UniqueId)!.ClassName);
        Assert.Equal(InspectMode.None, h.App.DevTools.InspectMode);
        // 拾取的那次点击没有到达应用
        Assert.Contains("Count: 0", h.AppById("count").TextContent);
    }

    [Fact]
    public void Closing_the_panel_while_the_app_is_picking_does_not_reopen_it()
    {
        using var h = new Harness();
        Harness.Key(h.AppWindow, PhysicalKey.F12);
        h.Settle();
        Assert.True(h.P.IsOpen);
        h.P.Close();
        h.Settle();
        Assert.False(h.P.IsOpen);
        Assert.Equal(InspectMode.Pick, h.App.DevTools!.InspectMode);   // 本地拾取仍在进行
        Harness.Key(h.AppWindow, PhysicalKey.Escape);
        h.Settle();
        Assert.False(h.P.IsOpen);
        Harness.Key(h.AppWindow, PhysicalKey.F12);
        h.Settle();
        Assert.True(h.P.IsOpen);
    }

    [Fact]
    public void The_panel_pick_button_starts_a_protocol_pick_that_ends_after_one_pick()
    {
        using var h = new Harness();
        h.Open();
        h.ClickPanel(h.PanelById("dt-pick")!);
        Assert.True(h.Model.Picking);
        Assert.Equal(InspectMode.Pick, h.App.DevTools!.InspectMode);
        Assert.Contains("on", h.PanelById("dt-pick")!.ClassName);
        var dec = h.AppById("decrement");
        h.ClickApp(dec);
        Assert.Equal(dec.UniqueId, h.P.SelectedNodeId);
        Assert.False(h.Model.Picking);
        Assert.Equal(InspectMode.None, h.App.DevTools.InspectMode);
        Assert.Same(dec, h.App.DevTools.HighlightedElement);
        Assert.Contains("Count: 0", h.AppById("count").TextContent);
        // F12 在面板窗口里也切换拾取，Esc 取消
        Harness.Key(h.PanelWindow, PhysicalKey.F12);
        h.Settle();
        Assert.Equal(InspectMode.Pick, h.App.DevTools.InspectMode);
        Harness.Key(h.PanelWindow, PhysicalKey.Escape);
        h.Settle();
        Assert.Equal(InspectMode.None, h.App.DevTools.InspectMode);
    }

    [Fact]
    public void Twisty_collapses_and_expands_and_arrow_keys_navigate()
    {
        using var h = new Harness();
        h.Open();
        var card = h.AppById("count").Parent!.Parent!;   // div.card
        var cardRow = h.PanelRow(card.UniqueId)!;
        var twisty = cardRow.Children.First(c => c.HasClass("dt-twisty"));
        int rowsBefore = h.Model.Tree.Rows.Count;
        h.ClickPanel(twisty);
        Assert.False(h.Model.Tree.Get(card.UniqueId)!.Expanded);
        Assert.Null(h.PanelRow(h.AppById("count").UniqueId));
        Assert.True(h.Model.Tree.Rows.Count < rowsBefore);
        h.ClickPanel(h.PanelRow(card.UniqueId)!.Children.First(c => c.HasClass("dt-twisty")));
        Assert.NotNull(h.PanelRow(h.AppById("count").UniqueId));
        // 点击箭头不会选中该行
        Assert.Equal(0, h.P.SelectedNodeId);

        h.ClickPanel(h.PanelRow(card.UniqueId)!);
        Harness.Key(h.PanelWindow, PhysicalKey.ArrowDown);
        h.Settle();
        int row = h.Model.Tree.RowOf(card.UniqueId);
        Assert.Equal(h.Model.Tree.Rows[row + 1].Node.Id, h.P.SelectedNodeId);
        Harness.Key(h.PanelWindow, PhysicalKey.ArrowLeft);   // 回到父节点（叶子）或折叠
        h.Settle();
        Harness.Key(h.PanelWindow, PhysicalKey.ArrowUp);
        h.Settle();
        Assert.NotEqual(0, h.P.SelectedNodeId);
    }

    [Fact]
    public void Tabs_show_computed_values_and_protocol_events()
    {
        using var h = new Harness();
        h.Open();
        var count = h.AppById("count");
        h.ClickPanel(h.PanelRow(count.UniqueId)!);
        h.ClickPanel(h.PanelById("dt-tab-computed")!);
        Assert.Equal(SideTab.Computed, h.Model.Tab);
        var text = h.PanelById("dt-side")!.TextContent;
        Assert.Contains("font-size", text);
        Assert.Contains("26px", text);
        h.ClickApp(h.AppById("increment"));
        h.ClickPanel(h.PanelById("dt-tab-events")!);
        Assert.Contains("Tree.textChanged", h.PanelById("dt-side")!.TextContent);
    }

    [Fact]
    public void The_panel_never_inspects_itself()
    {
        using var h = new Harness();
        h.Open();
        // 面板窗口没有启用 DevTools，不是 Agent 的目标；Agent 只有应用窗口一个目标
        Assert.Null(h.P.Host!.DevTools);
        var targets = h.App.DevTools!.Agent.Targets;
        Assert.Equal(h.App.DevTools.Target.Id, Assert.Single(targets).Id);
        // 镜像里没有面板自己的元素
        var panelIds = new HashSet<long>(Harness.Descendants(h.PanelRoot).Select(e => e.UniqueId));
        foreach (var row in h.Model.Tree.Rows)
        {
            Assert.DoesNotContain(row.Node.Id, panelIds);
            Assert.False(row.Node.Classes?.Any(c => c.StartsWith("dt", StringComparison.Ordinal)) ?? false);
        }
        // F12 拾取只命中应用窗口：在面板窗口里点击不会产生 nodePicked
        Harness.Key(h.AppWindow, PhysicalKey.F12);
        h.Settle();
        h.ClickPanel(h.PanelRow(h.AppById("count").UniqueId)!);
        Assert.Equal(InspectMode.Pick, h.App.DevTools.InspectMode);
        Harness.Key(h.AppWindow, PhysicalKey.Escape);
        h.Settle();
    }

    [Fact]
    public void Closing_and_reopening_the_panel_keeps_working_and_disposing_cleans_up()
    {
        using var h = new Harness();
        h.Open();
        h.ClickPanel(h.PanelRow(h.AppById("increment").UniqueId)!);
        Assert.NotNull(h.App.DevTools!.HighlightedElement);
        int sessions = h.App.DevTools.Agent.Sessions.Count;
        h.P.Close();
        h.Settle();
        Assert.False(h.P.IsOpen);
        Assert.Null(h.App.DevTools.HighlightedElement);     // 关闭时撤销高亮
        Assert.Single(h.Platform.Windows);
        h.Open();
        Assert.NotNull(h.PanelRow(h.AppById("count").UniqueId));
        h.P.Dispose();
        h.Settle();
        Assert.Equal(sessions - 1, h.App.DevTools.Agent.Sessions.Count);
        Assert.Single(h.Platform.Windows);
    }

    static Element DeclElement(Harness h, string ruleId, string prop)
    {
        var block = Harness.Descendants(h.PanelRoot).First(e => e.GetAttribute("data-rule") == ruleId);
        return Harness.Descendants(block).First(e => e.GetAttribute("data-decl") == prop);
    }

    static Element RuleValue(Harness h, string ruleId, string prop) =>
        Harness.Descendants(DeclElement(h, ruleId, prop)).First(e => e.GetAttribute("data-prop") == prop);

    static float Distance(Color a, Color b) => Math.Max(Math.Abs(a.R - b.R), Math.Max(Math.Abs(a.G - b.G), Math.Abs(a.B - b.B)));
}
