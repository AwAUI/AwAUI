using System.Collections;
using AwAUI.Controls.UnitTests.Integration;

namespace AwAUI.Controls.DataGrid.UnitTests;

public class DataGridSortingTests
{
    sealed class Row
    {
        public string Name { get; set; } = "";
        public int Value { get; set; }
        public int Id { get; set; }
    }

    static (DataGrid Grid, List<Row> Items) Create(bool compareByName = true)
    {
        var items = new List<Row>
        {
            new Row { Id = 0, Name = "C", Value = 3 },
            new Row { Id = 1, Name = "A", Value = 1 },
            new Row { Id = 2, Name = "B", Value = 2 },
            new Row { Id = 3, Name = "A", Value = 1 },
        };
        var grid = new DataGrid
        {
            Style = "height:300px;width:400px;",
            ItemsSource = items,
            RowHeight = 24,
        };
        grid.Columns.Add(new DataGridTextColumn { Header = "Name", Binding = nameof(Row.Name) });
        grid.Columns.Add(new DataGridTextColumn { Header = "Value", Binding = nameof(Row.Value), CanUserSort = compareByName });
        grid.Columns.Add(new DataGridTextColumn { Header = "Id", Binding = nameof(Row.Id) });
        return (grid, items);
    }

    [Fact]
    public void Clicking_a_header_cycles_none_ascending_descending_none()
    {
        var (grid, _) = Create();
        var h = new UIHarness(() => grid);
        var header = GridTest.HeaderCells(h)[0];

        h.Click(header);
        Assert.Equal(DataGridSortDirection.Ascending, grid.Columns[0].SortDirection);
        Assert.Equal(new[] { "A", "A", "B", "C" }, GridTest.ColumnTexts(h, 0));
        Assert.Equal("ascending", GridTest.HeaderCells(h)[0].GetAttribute("data-sorted"));

        h.Click(GridTest.HeaderCells(h)[0]);
        Assert.Equal(DataGridSortDirection.Descending, grid.Columns[0].SortDirection);
        Assert.Equal(new[] { "C", "B", "A", "A" }, GridTest.ColumnTexts(h, 0));

        h.Click(GridTest.HeaderCells(h)[0]);
        Assert.Equal(DataGridSortDirection.None, grid.Columns[0].SortDirection);
        Assert.Equal(new[] { "C", "A", "B", "A" }, GridTest.ColumnTexts(h, 0));
        Assert.Null(GridTest.HeaderCells(h)[0].GetAttribute("data-sorted"));
    }

    [Fact]
    public void Sorting_is_stable_for_equal_values_in_both_directions()
    {
        var (grid, _) = Create();
        grid.Columns[0].SortDirection = DataGridSortDirection.Ascending;
        var h = new UIHarness(() => grid);
        Assert.Equal(new[] { "A", "A", "B", "C" }, GridTest.ColumnTexts(h, 0));

        // 两个 "A" 行保持源顺序：Id 1 在 Id 3 之前。
        Assert.Equal(new[] { "1", "3", "2", "0" }, GridTest.ColumnTexts(h, 2));

        h.Click(GridTest.HeaderCells(h)[0]);
        Assert.Equal(DataGridSortDirection.Descending, grid.Columns[0].SortDirection);
        Assert.Equal(new[] { "C", "B", "A", "A" }, GridTest.ColumnTexts(h, 0));
        // 降序时相等项仍保持源顺序（Id 1 仍在 Id 3 之前）。
        Assert.Equal(new[] { "0", "2", "1", "3" }, GridTest.ColumnTexts(h, 2));
    }

    [Fact]
    public void IComparable_values_sort_without_a_comparer()
    {
        var (grid, _) = Create();
        grid.Columns[1].SortDirection = DataGridSortDirection.Ascending;
        var h = new UIHarness(() => grid);

        Assert.Equal(new[] { "1", "1", "2", "3" }, GridTest.ColumnTexts(h, 1));
    }

    sealed class ReverseComparer : IComparer
    {
        public int Compare(object? x, object? y) => string.CompareOrdinal(y?.ToString() ?? "", x?.ToString() ?? "");
    }

    [Fact]
    public void A_column_comparer_overrides_IComparable()
    {
        var (grid, _) = Create();
        grid.Columns[0].Comparer = new ReverseComparer();
        grid.Columns[0].SortDirection = DataGridSortDirection.Ascending;
        var h = new UIHarness(() => grid);

        Assert.Equal(new[] { "C", "B", "A", "A" }, GridTest.ColumnTexts(h, 0));
    }

    [Fact]
    public void Changing_a_sort_direction_after_mount_rebuilds_the_rows()
    {
        var (grid, _) = Create();
        var h = new UIHarness(() => grid);
        Assert.Equal(new[] { "C", "A", "B", "A" }, GridTest.ColumnTexts(h, 0));

        grid.Columns[0].SortDirection = DataGridSortDirection.Descending;
        h.Frame();
        Assert.Equal(new[] { "C", "B", "A", "A" }, GridTest.ColumnTexts(h, 0));
    }

    [Fact]
    public void Sort_can_be_disabled_on_the_grid_or_on_a_column()
    {
        var (grid, _) = Create();
        grid.CanUserSortColumns = false;
        var h = new UIHarness(() => grid);
        h.Click(GridTest.HeaderCells(h)[0]);
        Assert.Equal(DataGridSortDirection.None, grid.Columns[0].SortDirection);

        grid.CanUserSortColumns = true;
        grid.Columns[0].CanUserSort = false;
        h.Frame();
        h.Click(GridTest.HeaderCells(h)[0]);
        Assert.Equal(DataGridSortDirection.None, grid.Columns[0].SortDirection);
    }

    [Fact]
    public void Only_one_column_reports_a_sort_direction()
    {
        var (grid, _) = Create();
        var h = new UIHarness(() => grid);
        h.Click(GridTest.HeaderCells(h)[0]);
        h.Click(GridTest.HeaderCells(h)[1]);

        Assert.Equal(DataGridSortDirection.None, grid.Columns[0].SortDirection);
        Assert.Equal(DataGridSortDirection.Ascending, grid.Columns[1].SortDirection);
        Assert.Equal(new[] { "1", "1", "2", "3" }, GridTest.ColumnTexts(h, 1));
    }
}
