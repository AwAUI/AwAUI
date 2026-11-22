using AwAUI.Controls.UnitTests.Integration;
using AwAUI.Platform;
using AwAUI.UI;

namespace AwAUI.Controls.DataGrid.UnitTests;

public class DataGridSelectionTests
{
    sealed class Row
    {
        public string Name { get; set; } = "";
    }

    static (DataGrid Grid, List<Row> Items) Create(SelectionMode mode = SelectionMode.Extended)
    {
        var items = new List<Row> { new() { Name = "A" }, new() { Name = "B" }, new() { Name = "C" }, new() { Name = "D" } };
        var grid = new DataGrid
        {
            Style = "height:300px;width:300px;",
            ItemsSource = items,
            RowHeight = 24,
            SelectionMode = mode,
        };
        grid.Columns.Add(new DataGridTextColumn { Header = "Name", Binding = nameof(Row.Name) });
        return (grid, items);
    }

    [Fact]
    public void Clicking_a_row_selects_it_and_reports_the_change()
    {
        var (grid, items) = Create();
        var h = new UIHarness(() => grid);
        DataGridSelectionChangedEventArgs? changed = null;
        grid.SelectionChanged = args => changed = args;

        h.Click(GridTest.RowCells(h, 2)[0]);

        Assert.Equal(2, grid.SelectedIndex);
        Assert.Equal(items[2], grid.SelectedItem);
        Assert.Equal(new[] { items[2] }, grid.SelectedItems);
        Assert.NotNull(changed);
        Assert.Equal(new[] { items[2] }, changed!.AddedItems);
        Assert.Equal("true", GridTest.Rows(h)[2].GetAttribute("data-selected"));
    }

    [Fact]
    public void Shift_arrow_extends_the_row_range()
    {
        var (grid, items) = Create();
        var h = new UIHarness(() => grid);
        h.Tab();
        h.Key(PhysicalKey.ArrowDown, "ArrowDown");
        Assert.Equal(1, grid.SelectedIndex);

        h.Key(PhysicalKey.ArrowDown, "ArrowDown", KeyModifiers.Shift);
        Assert.Equal(1, grid.SelectedIndex);
        Assert.Equal(new[] { items[1], items[2] }, grid.SelectedItems);

        h.Key(PhysicalKey.ArrowUp, "ArrowUp", KeyModifiers.Shift);
        Assert.Single(grid.SelectedItems);
    }

    [Fact]
    public void Extended_plain_arrow_selects_only_the_focused_row()
    {
        var (grid, items) = Create();
        var h = new UIHarness(() => grid);
        h.Tab();
        h.Key(PhysicalKey.ArrowDown, "ArrowDown");
        h.Key(PhysicalKey.ArrowDown, "ArrowDown");
        Assert.Equal(new[] { items[2] }, grid.SelectedItems);
    }

    [Fact]
    public void Multiple_mode_clicking_toggles_rows()
    {
        var (grid, items) = Create(SelectionMode.Multiple);
        var h = new UIHarness(() => grid);

        h.Click(GridTest.RowCells(h, 0)[0]);
        h.Click(GridTest.RowCells(h, 1)[0]);
        Assert.Equal(new[] { items[0], items[1] }, grid.SelectedItems);

        h.Click(GridTest.RowCells(h, 0)[0]);
        Assert.Equal(new[] { items[1] }, grid.SelectedItems);
    }

    [Fact]
    public void Primary_modifier_click_toggles_in_extended_mode()
    {
        var (grid, items) = Create();
        var h = new UIHarness(() => grid);
        h.Click(GridTest.RowCells(h, 0)[0]);

        var cell = GridTest.RowCells(h, 2)[0];
        var center = cell.GetBoundingClientRect().Center;
        h.PointerDown(center, PointerButtons.Left, Shortcuts.PrimaryModifier);
        h.PointerUp(center, PointerButtons.Left, Shortcuts.PrimaryModifier);

        Assert.Equal(new[] { items[0], items[2] }, grid.SelectedItems);
    }

    [Fact]
    public void Primary_A_selects_every_row_in_multi_select_modes()
    {
        var (grid, items) = Create();
        var h = new UIHarness(() => grid);
        h.Tab();
        h.Key(PhysicalKey.KeyA, "a", Shortcuts.PrimaryModifier);

        Assert.Equal(4, grid.SelectedItems.Count);
        Assert.Equal(items, grid.SelectedItems);
    }

    [Fact]
    public void Single_mode_replaces_the_selection()
    {
        var (grid, items) = Create(SelectionMode.Single);
        var h = new UIHarness(() => grid);
        h.Click(GridTest.RowCells(h, 0)[0]);
        h.Click(GridTest.RowCells(h, 3)[0]);

        Assert.Equal(new[] { items[3] }, grid.SelectedItems);
        Assert.Equal(3, grid.SelectedIndex);
    }

    [Fact]
    public void A_declarative_selected_index_applies_once()
    {
        var (grid, _) = Create();
        grid.SelectedIndex = 2;
        var h = new UIHarness(() => grid);
        Assert.Equal(2, grid.SelectedIndex);

        h.Click(GridTest.RowCells(h, 0)[0]);
        h.Frame();
        Assert.Equal(0, grid.SelectedIndex);
    }

    [Fact]
    public void Page_down_moves_by_a_visible_page()
    {
        var items = Enumerable.Range(0, 100).Select(i => new Row { Name = i.ToString() }).ToList();
        var grid = new DataGrid
        {
            Style = "height:120px;width:300px;",
            ItemsSource = items,
            RowHeight = 20,
            HeaderHeight = 20,
        };
        grid.Columns.Add(new DataGridTextColumn { Header = "Name", Binding = nameof(Row.Name) });
        var h = new UIHarness(() => grid);
        h.Tab();
        h.Key(PhysicalKey.PageDown, "PageDown");

        Assert.True(grid.SelectedIndex >= 4, "selected " + grid.SelectedIndex);
    }
}
