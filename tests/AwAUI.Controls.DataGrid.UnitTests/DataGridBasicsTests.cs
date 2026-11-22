using AwAUI.Controls.UnitTests.Integration;
using AwAUI.UI;

namespace AwAUI.Controls.DataGrid.UnitTests;

public class DataGridBasicsTests
{
    sealed class Person
    {
        public string Name { get; set; } = "";
        public int Age { get; set; }
        public bool Active { get; set; }
    }

    static List<Person> People() => new()
    {
        new Person { Name = "Alice", Age = 30, Active = true },
        new Person { Name = "Bob", Age = 25, Active = false },
        new Person { Name = "Cara", Age = 41, Active = true },
    };

    static DataGrid Grid(List<Person> people)
    {
        var grid = new DataGrid
        {
            Style = "height:200px;width:500px;",
            ItemsSource = people,
            RowHeight = 24,
            HeaderHeight = 24,
        };
        grid.Columns.Add(new DataGridTextColumn { Header = "Name", Binding = nameof(Person.Name), Width = DataGridLength.Star(2) });
        grid.Columns.Add(new DataGridTextColumn { Header = "Age", Binding = nameof(Person.Age), Width = DataGridLength.Star(1) });
        grid.Columns.Add(new DataGridCheckBoxColumn { Header = "Active", Binding = nameof(Person.Active), Width = DataGridLength.Pixel(80) });
        return grid;
    }

    [Fact]
    public void Headers_rows_and_cells_have_their_roles_and_aria_indices()
    {
        var people = People();
        var h = new UIHarness(() => Grid(people));

        var root = GridTest.GridRoot(h);
        Assert.Equal("grid", root.GetAttribute("role"));
        Assert.Equal("4", root.GetAttribute("aria-rowcount"));
        Assert.Equal(3, GridTest.Rows(h).Count);
        Assert.Equal(3, GridTest.HeaderCells(h).Count);
        Assert.Equal(new[] { "Name", "Age", "Active" }, GridTest.HeaderTexts(h));

        var firstCells = GridTest.RowCells(h, 0);
        Assert.Equal(3, firstCells.Count);
        Assert.Equal(new[] { "1", "2", "3" }, firstCells.Select(c => c.GetAttribute("aria-colindex")!).ToArray());
        Assert.Equal("gridcell", firstCells[0].GetAttribute("role"));
        Assert.Equal("Alice", firstCells[0].TextContent.Trim());
        Assert.Equal("30", firstCells[1].TextContent.Trim());

        var rows = GridTest.Rows(h);
        Assert.Equal(new[] { "2", "3", "4" }, rows.Select(GridTest.RowIndex).Select(i => (i + 2).ToString()).ToArray());
        Assert.Equal("false", rows[1].GetAttribute("aria-selected"));
    }

    [Fact]
    public void Css_classes_and_part_attributes_are_emitted()
    {
        var h = new UIHarness(() => Grid(People()));
        var classes = h.FindAll(e => e.GetAttribute("class") is not null).Select(e => e.GetAttribute("class")).ToList();
        Assert.Contains(classes, c => c!.Contains("aw-datagrid"));
        Assert.Contains(classes, c => c!.Contains("aw-datagrid-header"));
        Assert.Contains(classes, c => c!.Contains("aw-datagrid-row"));
        Assert.Contains(classes, c => c!.Contains("aw-datagrid-cell"));
        Assert.Contains(classes, c => c!.Contains("aw-datagrid-column-header"));
        Assert.Equal("header", h.FindAll(e => e.GetAttribute("class")?.Contains("aw-datagrid-header") == true).Single().GetAttribute("part"));
        Assert.Equal("cell", GridTest.RowCells(h, 0)[0].GetAttribute("part"));
    }

    [Fact]
    public void Auto_generate_columns_creates_one_text_column_per_property()
    {
        var people = People();
        var grid = new DataGrid
        {
            Style = "height:200px;width:400px;",
            ItemsSource = people,
            AutoGenerateColumns = true,
        };
        var h = new UIHarness(() => grid);

        Assert.Equal(3, grid.Columns.Count);
        Assert.All(grid.Columns, c => Assert.IsType<DataGridTextColumn>(c));
        Assert.Equal(new[] { "Name", "Age", "Active" }, GridTest.HeaderTexts(h));
        Assert.Equal("Alice", GridTest.RowCells(h, 0)[0].TextContent.Trim());
        Assert.Equal("41", GridTest.RowCells(h, 2)[1].TextContent.Trim());
    }

    [Fact]
    public void Auto_generated_columns_are_not_duplicated_for_explicit_bindings()
    {
        var people = People();
        var grid = new DataGrid
        {
            Style = "height:100px;width:300px;",
            ItemsSource = people,
            AutoGenerateColumns = true,
        };
        grid.Columns.Add(new DataGridTextColumn { Header = "Name", Binding = nameof(Person.Name) });
        var h = new UIHarness(() => grid);

        Assert.Equal(3, grid.Columns.Count);
        Assert.Single(grid.Columns, c => c.Binding == nameof(Person.Name));
        Assert.Equal(3, GridTest.HeaderCells(h).Count);
    }

    [Fact]
    public void Replacing_the_items_source_after_mount_rerenders_the_rows()
    {
        var grid = Grid(People());
        var h = new UIHarness(() => grid);
        Assert.Equal(3, grid.RowCount);

        grid.ItemsSource = new List<Person> { new Person { Name = "Dana", Age = 50 }, new Person { Name = "Eli", Age = 60 } };
        h.Frame();

        Assert.Equal(2, grid.RowCount);
        Assert.Equal(2, GridTest.Rows(h).Count);
        Assert.Equal(new[] { "Dana", "Eli" }, GridTest.ColumnTexts(h, 0));
    }

    [Fact]
    public void Changing_the_row_height_after_mount_relayouts_the_rows()
    {
        var grid = Grid(People());
        var h = new UIHarness(() => grid);
        Assert.Contains("top:48", GridTest.Rows(h)[1].GetAttribute("style"));

        grid.RowHeight = 40;
        h.Frame();

        Assert.Contains("top:64", GridTest.Rows(h)[1].GetAttribute("style"));
        Assert.Contains("height:40", GridTest.Rows(h)[1].GetAttribute("style"));
        Assert.Equal(3, GridTest.Rows(h).Count);
    }

    [Fact]
    public void Headers_visibility_none_removes_header_row()
    {
        var people = People();
        var grid = Grid(people);
        grid.HeadersVisibility = DataGridHeadersVisibility.None;
        var h = new UIHarness(() => grid);

        Assert.Empty(GridTest.HeaderCells(h));
        Assert.Equal(3, GridTest.Rows(h).Count);
        Assert.Equal("Alice", GridTest.RowCells(h, 0)[0].TextContent.Trim());
    }
}
