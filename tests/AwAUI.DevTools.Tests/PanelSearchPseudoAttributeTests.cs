using AwAUI.Css;
using AwAUI.Platform;
using AwAUI.UI;

namespace AwAUI.DevTools.Tests;

/// <summary>
/// 元素搜索、强制伪状态与属性 / class 编辑的面板行为：经进程内协议驱动真实 Agent，
/// 断言模型状态、面板渲染出的控件与应用元素本身。
/// </summary>
public partial class PanelSearchPseudoAttributeTests
{
    // ------------------------------------------------------------ 搜索

    [Fact]
    public void Search_lists_matches_and_selecting_a_result_reveals_the_node()
    {
        using var h = new Harness();
        h.Open();
        Assert.NotNull(h.PanelById("dt-search"));
        var inc = h.AppById("increment");

        h.Model.SetSearchQuery("increment");
        h.Settle();

        var results = Assert.IsType<List<SearchResult>>(h.Model.SearchResults);
        var result = Assert.Single(results);
        Assert.Equal(inc.UniqueId, result.NodeId);
        Assert.Equal("id", result.Match);
        Assert.Contains("increment", result.Label);
        Assert.False(h.Model.Searching);
        Assert.Null(h.Model.SearchError);

        h.Model.SelectSearchResult(result);
        h.Settle();
        Assert.Null(h.Model.SearchResults);                 // 选中后收起结果列表
        Assert.Equal(inc.UniqueId, h.P.SelectedNodeId);
        Assert.NotNull(h.PanelRow(inc.UniqueId));
        Assert.Contains("selected", h.PanelRow(inc.UniqueId)!.ClassName);
    }

    [Fact]
    public void Clicking_a_search_result_selects_and_reveals_the_node()
    {
        using var h = new Harness();
        h.Open();
        var inc = h.AppById("increment");

        h.Model.SetSearchQuery("increment");
        h.Settle();
        var row = h.PanelById("dt-search-result-" + inc.UniqueId);
        Assert.NotNull(row);
        h.ClickPanel(row!);

        Assert.Equal(inc.UniqueId, h.P.SelectedNodeId);
        Assert.Null(h.Model.SearchResults);
        Assert.Contains("selected", h.PanelRow(inc.UniqueId)!.ClassName);
    }

    [Fact]
    public void Search_reports_no_results_and_empty_query_clears_them()
    {
        using var h = new Harness();
        h.Open();

        h.Model.SetSearchQuery("definitely-not-in-the-app");
        h.Settle();
        Assert.NotNull(h.Model.SearchResults);
        Assert.Empty(h.Model.SearchResults!);
        Assert.NotNull(h.PanelById("dt-search-empty"));

        h.Model.SetSearchQuery("");
        h.Settle();
        Assert.Null(h.Model.SearchResults);
        Assert.Null(h.PanelById("dt-search-results"));
    }

    [Fact]
    public void Submit_search_runs_immediately_and_matches_classes_and_selectors()
    {
        using var h = new Harness();
        h.Open();
        var inc = h.AppById("increment");

        h.Model.SubmitSearch("primary");
        h.Settle();
        Assert.Contains(h.Model.SearchResults!, r => r.NodeId == inc.UniqueId && r.Match == "class");

        h.Model.SubmitSearch("button.primary");
        h.Settle();
        Assert.Contains(h.Model.SearchResults!, r => r.NodeId == inc.UniqueId && r.Match == "selector");
    }

    // ------------------------------------------------------------ 强制伪状态

    [Fact]
    public void Pseudo_state_toggles_force_states_and_reset_when_the_selection_changes()
    {
        using var h = new Harness();
        h.Open();
        var inc = h.AppById("increment");
        var dec = h.AppById("decrement");
        h.Model.Select(h.Model.Tree.Get(inc.UniqueId)!);
        h.Settle();

        // 开关已经渲染在 Styles 页里
        var hover = h.PanelById("dt-pseudo-hover");
        Assert.NotNull(hover);
        Assert.Contains(InspectorModel.ForceablePseudoStates, s => s == "hover");

        h.Model.TogglePseudoState("hover");
        h.Settle();
        Assert.True(h.Model.IsPseudoStateForced("hover"));
        Assert.True(inc.ForcedStates.HasFlag(PseudoStates.Hover), "the app element did not enter :hover");
        Assert.Contains("on", h.PanelById("dt-pseudo-hover")!.ClassName);

        // 再点一次渲染出来的开关：取消 :hover
        h.ClickPanel(h.PanelById("dt-pseudo-hover")!);
        Assert.False(h.Model.IsPseudoStateForced("hover"));
        Assert.False(inc.ForcedStates.HasFlag(PseudoStates.Hover));

        h.Model.TogglePseudoState("hover");
        h.Model.TogglePseudoState("active");
        h.Settle();
        Assert.True(inc.ForcedStates.HasFlag(PseudoStates.Active));
        Assert.True(inc.ForcedStates.HasFlag(PseudoStates.Hover));

        // 选中另一个节点：旧元素的强制状态被撤销，开关复位
        h.Model.Select(h.Model.Tree.Get(dec.UniqueId)!);
        h.Settle();
        Assert.Empty(h.Model.ForcedPseudoStates);
        Assert.False(inc.ForcedStates.HasFlag(PseudoStates.Hover));
        Assert.False(inc.ForcedStates.HasFlag(PseudoStates.Active));
        Assert.False(h.Model.IsPseudoStateForced("hover"));
    }

    [Fact]
    public void Clearing_pseudo_states_sends_an_empty_array_and_resets_the_toggles()
    {
        using var h = new Harness();
        h.Open();
        var inc = h.AppById("increment");
        h.Model.Select(h.Model.Tree.Get(inc.UniqueId)!);
        h.Model.TogglePseudoState("focus-visible");
        h.Settle();
        Assert.True(inc.ForcedStates.HasFlag(PseudoStates.FocusVisible));

        h.Model.ClearPseudoStates();
        h.Settle();
        Assert.Empty(h.Model.ForcedPseudoStates);
        Assert.Equal(PseudoStates.None, inc.ForcedStates);
        Assert.DoesNotContain("on", h.PanelById("dt-pseudo-focus-visible")!.ClassName);
    }

    // ------------------------------------------------------------ 属性 / class 编辑

    sealed partial class AttributeRoot : Component
    {
        protected override Node Build()
        {
            var target = new Div { Id = "attr-target", Class = "alpha beta", Style = "width:120px;height:30px" };
            target.Attributes["data-test"] = "one";
            target.Attributes["role"] = "note";
            return new Div { Id = "attr-root", Children = [target] };
        }
    }

    static Harness AttributesHarness() =>
        new(root: new AttributeRoot(), sheets: Array.Empty<StyleSheet>());

    static DomNode SelectTarget(Harness h)
    {
        var node = h.Model.Tree.Get(h.AppById("attr-target").UniqueId)!;
        h.Model.Select(node);
        h.Model.SetTab(SideTab.Attributes);
        h.Settle();
        return node;
    }

    [Fact]
    public void Class_and_attribute_edits_round_trip_and_refresh_the_row()
    {
        using var h = AttributesHarness();
        h.Open();
        var target = h.AppById("attr-target");
        var node = SelectTarget(h);
        Assert.NotNull(h.PanelById("dt-tab-attributes"));
        Assert.NotNull(h.PanelById("dt-attr-add"));

        // class：空格分隔的列表 → Tree.setClasses
        h.Model.BeginClassEdit();
        Assert.Equal("alpha beta", h.Model.AttributeDraft);
        h.Model.CommitAttributeEdit("alpha gamma");
        h.Settle();
        Assert.True(target.HasClass("alpha"));
        Assert.True(target.HasClass("gamma"));
        Assert.False(target.HasClass("beta"));
        Assert.Contains("gamma", node.Classes!);

        // 属性值：Tree.setAttribute + Tree.describeNode 刷新行
        h.Model.BeginAttributeEdit("data-test", "one");
        h.Model.CommitAttributeEdit("two");
        h.Settle();
        Assert.Equal("two", target.GetAttribute("data-test"));
        Assert.Contains(node.Attributes!, kv => kv.Key == "data-test" && kv.Value == "two");

        // 新增与删除
        h.Model.BeginAddAttribute();
        h.Model.CommitAttributeEdit("title=hello");
        h.Settle();
        Assert.Equal("hello", target.GetAttribute("title"));
        h.Model.RemoveAttribute("title");
        h.Settle();
        Assert.Null(target.GetAttribute("title"));
        Assert.DoesNotContain(node.Attributes!, kv => kv.Key == "title");

        // id：Tree.setAttribute 后由 describeNode 更新镜像里的 elementId
        h.Model.BeginIdEdit();
        h.Model.CommitAttributeEdit("attr-renamed");
        h.Settle();
        Assert.Equal("attr-renamed", target.Id);
        Assert.Equal("attr-renamed", node.ElementId);
        Assert.Null(h.Model.AttributesError);
    }

    [Fact]
    public void Editing_an_attribute_value_through_the_attributes_tab_round_trips()
    {
        using var h = AttributesHarness();
        h.Open();
        var target = h.AppById("attr-target");
        var node = SelectTarget(h);

        var value = Harness.Descendants(h.PanelRoot).First(e => e.GetAttribute("data-attr-value") == "data-test");
        h.ClickPanel(value);
        var edit = h.PanelById("dt-attr-edit");
        Assert.NotNull(edit);
        Assert.Same(edit, h.P.Host!.Root.FocusManager.FocusedElement);
        for (int i = 0; i < "one".Length; i++) Harness.Key(h.PanelWindow, PhysicalKey.Backspace);
        h.PanelWindow.SendText("two");
        Harness.Key(h.PanelWindow, PhysicalKey.Enter);
        h.Settle();

        Assert.Null(h.PanelById("dt-attr-edit"));
        Assert.Equal("two", target.GetAttribute("data-test"));
        Assert.Contains(node.Attributes!, kv => kv.Key == "data-test" && kv.Value == "two");
        Assert.Null(h.Model.AttributesError);
    }

    [Fact]
    public void Read_only_attributes_show_the_protocol_error_instead_of_failing_silently()
    {
        using var h = AttributesHarness();
        h.Open();
        var target = h.AppById("attr-target");
        var node = SelectTarget(h);
        Assert.Contains(node.Attributes!, kv => kv.Key == "role" && kv.Value == "note");

        h.Model.BeginAttributeEdit("role", "note");
        h.Model.CommitAttributeEdit("button");
        h.Settle();

        Assert.NotNull(h.Model.AttributesError);
        Assert.Contains("read-only", h.Model.AttributesError!.ToLowerInvariant());
        Assert.Equal("note", target.GetAttribute("role"));            // 没有改坏应用
        Assert.NotNull(h.PanelById("dt-attributes-error"));
        Assert.Contains(node.Attributes!, kv => kv.Key == "role" && kv.Value == "note");
    }

    [Fact]
    public void Invalid_new_attribute_text_is_reported_without_a_request()
    {
        using var h = AttributesHarness();
        h.Open();
        SelectTarget(h);

        int requests = h.Model.RequestCount;
        h.Model.BeginAddAttribute();
        h.Model.CommitAttributeEdit("missing separator");
        h.Settle();
        Assert.Equal(requests, h.Model.RequestCount);
        Assert.NotNull(h.Model.AttributesError);
        Assert.NotNull(h.PanelById("dt-attributes-error"));
    }
}
