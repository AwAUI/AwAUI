using AwAUI.Controls.UnitTests.Integration;
using AwAUI.Platform;

namespace AwAUI.Controls.DataGrid.UnitTests;

public class DataGridEditingTests
{
    sealed class Row
    {
        public string Name { get; set; } = "";
        public bool Active { get; set; }
        public int Count { get; set; }
    }

    sealed class ProtectedRow
    {
        string _name = "start";
        public string Name
        {
            get => _name;
            set
            {
                if (value == "bad") throw new InvalidOperationException("rejected");
                _name = value;
            }
        }
    }

    static (DataGrid Grid, List<Row> Items) Create()
    {
        var items = new List<Row>
        {
            new() { Name = "Alice", Active = false, Count = 1 },
            new() { Name = "Bob", Active = true, Count = 2 },
        };
        var grid = new DataGrid
        {
            Style = "height:200px;width:400px;",
            ItemsSource = items,
            RowHeight = 24,
        };
        grid.Columns.Add(new DataGridTextColumn { Header = "Name", Binding = nameof(Row.Name) });
        grid.Columns.Add(new DataGridCheckBoxColumn { Header = "Active", Binding = nameof(Row.Active) });
        grid.Columns.Add(new DataGridTextColumn { Header = "Count", Binding = nameof(Row.Count) });
        return (grid, items);
    }

    [Fact]
    public void Begin_and_commit_write_the_value_back_through_the_property_setter()
    {
        var (grid, items) = Create();
        var h = new UIHarness(() => grid);

        Assert.True(grid.BeginEdit(0, 0));
        Assert.True(grid.IsEditing);
        Assert.Equal(0, grid.EditRowIndex);
        grid.SetEditText("Changed");
        Assert.True(grid.CommitEdit());

        Assert.False(grid.IsEditing);
        Assert.Equal("Changed", items[0].Name);
        h.Frame();
        Assert.Equal("Changed", GridTest.RowCells(h, 0)[0].TextContent.Trim());
    }

    [Fact]
    public void Cancel_discards_the_draft_and_keeps_the_source_value()
    {
        var (grid, items) = Create();
        var h = new UIHarness(() => grid);

        Assert.True(grid.BeginEdit(0, 0));
        grid.SetEditText("Nope");
        grid.CancelEdit();

        Assert.False(grid.IsEditing);
        Assert.Equal("Alice", items[0].Name);
        h.Frame();
        Assert.Equal("Alice", GridTest.RowCells(h, 0)[0].TextContent.Trim());
    }

    [Fact]
    public void A_throwing_setter_keeps_the_editor_open_and_marks_data_invalid()
    {
        var item = new ProtectedRow();
        var grid = new DataGrid
        {
            Style = "height:150px;width:300px;",
            ItemsSource = new[] { item },
        };
        grid.Columns.Add(new DataGridTextColumn { Header = "Name", Binding = nameof(ProtectedRow.Name) });
        var h = new UIHarness(() => grid);

        Assert.True(grid.BeginEdit(0, 0));
        grid.SetEditText("bad");
        Assert.False(grid.CommitEdit());
        Assert.True(grid.IsEditing);
        Assert.True(grid.IsEditInvalid);
        Assert.Equal("start", item.Name);

        h.Frame();
        var cell = GridTest.RowCells(h, 0)[0];
        Assert.Equal("true", cell.GetAttribute("data-invalid"));
        Assert.Equal("true", cell.GetAttribute("aria-invalid"));
    }

    [Fact]
    public void Invalid_text_for_a_numeric_property_is_rejected()
    {
        var (grid, items) = Create();
        var h = new UIHarness(() => grid);
        Assert.True(grid.BeginEdit(0, 2));
        grid.SetEditText("not a number");
        Assert.False(grid.CommitEdit());
        Assert.True(grid.IsEditing);
        Assert.Equal(1, items[0].Count);

        grid.SetEditText("42");
        Assert.True(grid.CommitEdit());
        Assert.Equal(42, items[0].Count);
    }

    [Fact]
    public void F2_enters_edit_mode_and_escape_cancels()
    {
        var (grid, _) = Create();
        var h = new UIHarness(() => grid);
        h.Tab();
        h.Key(PhysicalKey.F2, "F2");
        Assert.True(grid.IsEditing);
        Assert.Equal(0, grid.EditColumnIndex);

        h.Key(PhysicalKey.Escape, "Escape");
        Assert.False(grid.IsEditing);
    }

    [Fact]
    public void A_read_only_grid_never_enters_edit_mode()
    {
        var (grid, _) = Create();
        grid.IsReadOnly = true;
        var h = new UIHarness(() => grid);

        Assert.False(grid.BeginEdit(0, 0));
        h.Tab();
        h.Key(PhysicalKey.F2, "F2");
        Assert.False(grid.IsEditing);
    }

    [Fact]
    public void Clicking_a_check_box_toggles_and_commits_immediately()
    {
        var (grid, items) = Create();
        var h = new UIHarness(() => grid);

        h.Click(GridTest.RowCells(h, 0)[1]);
        Assert.True(items[0].Active);
        Assert.False(grid.IsEditing);
        h.Frame();
        Assert.Equal("true", GridTest.RowCells(h, 0)[1].GetAttribute("aria-checked")
            ?? GridTest.RowCells(h, 0)[1].Children[0].GetAttribute("aria-checked"));
    }

    [Fact]
    public void Enter_commits_and_moves_to_the_next_row()
    {
        var (grid, items) = Create();
        var h = new UIHarness(() => grid);
        Assert.True(grid.BeginEdit(0, 0));
        grid.SetEditText("Zed");
        h.Frame();

        h.Key(PhysicalKey.Enter, "Enter");

        Assert.Equal("Zed", items[0].Name);
        Assert.False(grid.IsEditing);
        Assert.Equal(1, grid.SelectedIndex);
    }

    [Fact]
    public void Tab_commits_the_editor_and_moves_to_the_next_cell()
    {
        var (grid, items) = Create();
        var h = new UIHarness(() => grid);
        Assert.True(grid.BeginEdit(0, 0));
        grid.SetEditText("Tabbed");
        h.Frame();

        h.Key(PhysicalKey.Tab, "Tab");

        Assert.Equal("Tabbed", items[0].Name);
        Assert.False(grid.IsEditing);
        Assert.Equal(0, grid.CurrentRowIndex);
        Assert.Equal(1, grid.CurrentColumnIndex);
    }

    [Fact]
    public void Home_and_end_move_within_the_row_and_with_the_primary_modifier_across_rows()
    {
        var (grid, _) = Create();
        var h = new UIHarness(() => grid);
        h.Tab();

        h.Key(PhysicalKey.End, "End");
        Assert.Equal(2, grid.CurrentColumnIndex);
        h.Key(PhysicalKey.Home, "Home");
        Assert.Equal(0, grid.CurrentColumnIndex);

        h.Key(PhysicalKey.End, "End", AwAUI.UI.Shortcuts.PrimaryModifier);
        Assert.Equal(1, grid.CurrentRowIndex);
        Assert.Equal(2, grid.CurrentColumnIndex);
        h.Key(PhysicalKey.Home, "Home", AwAUI.UI.Shortcuts.PrimaryModifier);
        Assert.Equal(0, grid.CurrentRowIndex);
        Assert.Equal(0, grid.CurrentColumnIndex);
    }

    [Fact]
    public void A_template_column_is_not_editable()
    {
        var (grid, _) = Create();
        grid.Columns.Add(new DataGridTemplateColumn { Header = "Extra", CellTemplate = row => new AwAUI.UI.Text("x") });
        var h = new UIHarness(() => grid);
        Assert.False(grid.BeginEdit(0, 3));
    }
}
