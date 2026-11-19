using AwAUI.Controls.Primitives;
using AwAUI.Platform;
using AwAUI.UI;

namespace AwAUI.Controls.UnitTests.Selection;

public class SelectionGesturesTests
{
    static SelectionModel<int> Model(int count = 10) => new(Enumerable.Range(0, count).ToList());

    static void Click(SelectionModel<int> m, int i, SelectionMode mode, bool shift = false, bool primary = false) =>
        SelectionGestures.Update(m, i, mode, range: shift, toggle: primary, rightButton: false, fromFocus: false);

    static void Arrow(SelectionModel<int> m, int i, SelectionMode mode, bool shift = false, bool primary = false) =>
        SelectionGestures.Update(m, i, mode, range: shift, toggle: primary, rightButton: false, fromFocus: true);

    [Fact]
    public void Click_selects_single_item()
    {
        var m = Model();
        Click(m, 3, SelectionMode.Multiple);
        Click(m, 5, SelectionMode.Multiple);
        Assert.Equal(new[] { 5 }, m.SelectedIndexes);
        Assert.Equal(5, m.AnchorIndex);
    }

    [Fact]
    public void Primary_click_toggles_in_multiple_mode()
    {
        var m = Model();
        Click(m, 3, SelectionMode.Multiple);
        Click(m, 5, SelectionMode.Multiple, primary: true);
        Click(m, 7, SelectionMode.Multiple, primary: true);
        Assert.Equal(new[] { 3, 5, 7 }, m.SelectedIndexes);
        Click(m, 5, SelectionMode.Multiple, primary: true);
        Assert.Equal(new[] { 3, 7 }, m.SelectedIndexes);
        Assert.Equal(5, m.AnchorIndex);
    }

    [Fact]
    public void Primary_click_in_single_mode_toggles_the_item()
    {
        var m = Model();
        Click(m, 4, SelectionMode.Single, primary: true);
        Assert.Equal(4, m.SelectedIndex);
        Click(m, 4, SelectionMode.Single, primary: true);
        Assert.Equal(-1, m.SelectedIndex);
    }

    [Fact]
    public void Shift_click_selects_range_from_anchor()
    {
        var m = Model();
        Click(m, 2, SelectionMode.Multiple);
        Click(m, 6, SelectionMode.Multiple, shift: true);
        Assert.Equal(new[] { 2, 3, 4, 5, 6 }, m.SelectedIndexes);
        Assert.Equal(2, m.AnchorIndex);
        // 锚点不变：再次 Shift 点击替换范围
        Click(m, 0, SelectionMode.Multiple, shift: true);
        Assert.Equal(new[] { 0, 1, 2 }, m.SelectedIndexes);
    }

    [Fact]
    public void Shift_primary_click_adds_range()
    {
        var m = Model();
        Click(m, 0, SelectionMode.Multiple);
        Click(m, 5, SelectionMode.Multiple, primary: true);
        Click(m, 7, SelectionMode.Multiple, shift: true, primary: true);
        Assert.Equal(new[] { 0, 5, 6, 7 }, m.SelectedIndexes);
    }

    [Fact]
    public void Shift_click_in_single_mode_is_plain_click()
    {
        var m = Model();
        Click(m, 2, SelectionMode.Single);
        Click(m, 6, SelectionMode.Single, shift: true);
        Assert.Equal(new[] { 6 }, m.SelectedIndexes);
    }

    [Fact]
    public void Toggle_mode_toggles_without_modifier()
    {
        var m = Model();
        Click(m, 1, SelectionMode.Multiple | SelectionMode.Toggle);
        Click(m, 3, SelectionMode.Multiple | SelectionMode.Toggle);
        Assert.Equal(new[] { 1, 3 }, m.SelectedIndexes);
        Click(m, 1, SelectionMode.Multiple | SelectionMode.Toggle);
        Assert.Equal(new[] { 3 }, m.SelectedIndexes);
    }

    [Fact]
    public void Always_selected_never_deselects_last_item()
    {
        var m = Model();
        var mode = SelectionMode.Multiple | SelectionMode.AlwaysSelected;
        Click(m, 1, mode);
        Click(m, 1, mode, primary: true);
        Assert.Equal(new[] { 1 }, m.SelectedIndexes);
        Click(m, 2, SelectionMode.Single | SelectionMode.AlwaysSelected, primary: true);
        Click(m, 2, SelectionMode.Single | SelectionMode.AlwaysSelected, primary: true);
        Assert.Equal(2, m.SelectedIndex);
    }

    [Fact]
    public void Right_click_keeps_existing_multi_selection_when_on_selected_item()
    {
        var m = Model();
        Click(m, 1, SelectionMode.Multiple);
        Click(m, 4, SelectionMode.Multiple, shift: true);
        SelectionGestures.Update(m, 3, SelectionMode.Multiple, false, false, rightButton: true, fromFocus: false);
        Assert.Equal(4, m.Count);
        SelectionGestures.Update(m, 8, SelectionMode.Multiple, false, false, rightButton: true, fromFocus: false);
        Assert.Equal(new[] { 8 }, m.SelectedIndexes);
    }

    [Fact]
    public void Keyboard_focus_moves_select_shift_extends_primary_only_moves()
    {
        var m = Model();
        Arrow(m, 2, SelectionMode.Multiple);
        Assert.Equal(new[] { 2 }, m.SelectedIndexes);
        Arrow(m, 3, SelectionMode.Multiple, shift: true);
        Arrow(m, 4, SelectionMode.Multiple, shift: true);
        Assert.Equal(new[] { 2, 3, 4 }, m.SelectedIndexes);
        Arrow(m, 5, SelectionMode.Multiple, primary: true);
        Assert.Equal(new[] { 2, 3, 4 }, m.SelectedIndexes);   // 只移动焦点
        Arrow(m, 1, SelectionMode.Multiple, shift: true);
        Assert.Equal(new[] { 1, 2 }, m.SelectedIndexes);       // 从锚点 2 重新计算范围
    }

    [Fact]
    public void HandleKeyDown_navigates_and_selects_skipping_disabled()
    {
        var m = Model();
        Func<int, bool> enabled = i => i != 1;
        Assert.True(SelectionGestures.HandleKeyDown(m, "ArrowDown", KeyModifiers.None, SelectionMode.Multiple, 0, 10, enabled, null, out int f));
        Assert.Equal(2, f);
        Assert.Equal(new[] { 2 }, m.SelectedIndexes);
        Assert.True(SelectionGestures.HandleKeyDown(m, "End", KeyModifiers.Shift, SelectionMode.Multiple, f, 10, enabled, null, out f));
        Assert.Equal(9, f);
        Assert.Equal(new[] { 2, 3, 4, 5, 6, 7, 8, 9 }, m.SelectedIndexes);
        Assert.False(SelectionGestures.HandleKeyDown(m, "x", KeyModifiers.None, SelectionMode.Multiple, f, 10, enabled, null, out _));
    }

    [Fact]
    public void HandleKeyDown_select_all_and_space()
    {
        var m = Model();
        var primary = Shortcuts.PrimaryModifier;
        Assert.True(SelectionGestures.HandleKeyDown(m, "a", primary, SelectionMode.Multiple, 0, 10, null, null, out _));
        Assert.Equal(10, m.Count);
        Assert.False(SelectionGestures.HandleKeyDown(Model(), "a", primary, SelectionMode.Single, 0, 10, null, null, out _));
        var s = Model();
        Assert.True(SelectionGestures.HandleKeyDown(s, " ", KeyModifiers.None, SelectionMode.Single, 4, 10, null, null, out _));
        Assert.Equal(4, s.SelectedIndex);
        Assert.True(SelectionGestures.HandleKeyDown(s, " ", primary, SelectionMode.Single, 4, 10, null, null, out _));
        Assert.Equal(-1, s.SelectedIndex);
    }

    [Fact]
    public void HandleKeyDown_horizontal_rtl_and_page_size()
    {
        var m = Model(100);
        var nav = new RovingFocusOptions { Orientation = Orientation.Horizontal, Direction = AwAUI.Layout.Direction.Rtl, PageSize = 10 };
        Assert.True(SelectionGestures.HandleKeyDown(m, "ArrowLeft", KeyModifiers.None, SelectionMode.Single, 5, 100, null, nav, out int f));
        Assert.Equal(6, f);   // RTL 下左键 = 下一个
        Assert.False(SelectionGestures.HandleKeyDown(m, "ArrowDown", KeyModifiers.None, SelectionMode.Single, 5, 100, null, nav, out _));
        Assert.True(SelectionGestures.HandleKeyDown(m, "PageDown", KeyModifiers.None, SelectionMode.Single, 6, 100, null, nav, out f));
        Assert.Equal(16, f);
        Assert.Equal(16, m.SelectedIndex);
    }
}
