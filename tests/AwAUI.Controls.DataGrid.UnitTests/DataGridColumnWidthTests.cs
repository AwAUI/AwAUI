using AwAUI.Controls.UnitTests.Integration;

namespace AwAUI.Controls.DataGrid.UnitTests;

public class DataGridColumnWidthTests
{
    [Fact]
    public void Allocation_gives_fixed_and_auto_widths_then_splits_star_space()
    {
        var lengths = new[]
        {
            DataGridLength.Pixel(100),
            DataGridLength.Star(1),
            DataGridLength.Star(2),
            DataGridLength.Auto,
        };
        var widths = DataGridColumnWidths.Allocate(lengths, new[] { 0.0, 0.0, 0.0, 80.0 }, 600);

        Assert.Equal(100, widths[0], 6);
        Assert.Equal(140, widths[1], 6); // (600 - 100 - 80) / 3
        Assert.Equal(280, widths[2], 6);
        Assert.Equal(80, widths[3], 6);
        Assert.Equal(600, widths.Sum(), 6);
    }

    [Fact]
    public void Allocation_falls_back_to_a_default_star_width_before_measurement()
    {
        var widths = DataGridColumnWidths.Allocate(new[] { DataGridLength.Star(1), DataGridLength.Star(2) },
            Array.Empty<double>(), 0);
        Assert.Equal(DataGridColumnWidths.UnmeasuredStarWidth, widths[0], 6);
        Assert.Equal(DataGridColumnWidths.UnmeasuredStarWidth * 2, widths[1], 6);
    }

    sealed class Item
    {
        public string Name { get; set; } = "";
    }

    [Fact]
    public void Control_assigns_pixel_star_and_auto_widths()
    {
        var items = Enumerable.Range(0, 4).Select(i => new Item { Name = i == 3 ? "A very long value indeed" : "x" }).ToList();
        var grid = new DataGrid
        {
            Style = "height:150px;width:400px;",
            ItemsSource = items,
        };
        grid.Columns.Add(new DataGridTextColumn { Header = "Fixed", Binding = "Name", Width = DataGridLength.Pixel(100) });
        grid.Columns.Add(new DataGridTextColumn { Header = "Star1", Binding = "Name", Width = DataGridLength.Star(1) });
        grid.Columns.Add(new DataGridTextColumn { Header = "Star2", Binding = "Name", Width = DataGridLength.Star(2) });
        grid.Columns.Add(new DataGridTextColumn { Header = "Auto", Binding = "Name", Width = DataGridLength.Auto });
        var h = new UIHarness(() => grid);

        var widths = grid.ColumnActualWidths;
        Assert.Equal(4, widths.Count);
        Assert.Equal(100, widths[0], 3);
        // auto 宽度由内容估算得到，剩余空间按 1:2 分给两个 star 列
        Assert.True(widths[3] > 100, $"auto {widths[3]}");
        Assert.Equal(2 * widths[1], widths[2], 3);
        Assert.Equal(400, widths.Sum(), 1);
        Assert.Equal(widths[0], grid.Columns[0].ActualWidth, 3);
    }

    [Fact]
    public void Changing_a_column_width_after_mount_reallocates_the_columns()
    {
        var items = new List<Item> { new Item { Name = "a" } };
        var grid = new DataGrid { Style = "height:120px;width:400px;", ItemsSource = items };
        grid.Columns.Add(new DataGridTextColumn { Header = "A", Binding = "Name", Width = DataGridLength.Pixel(100) });
        grid.Columns.Add(new DataGridTextColumn { Header = "B", Binding = "Name", Width = DataGridLength.Star(1) });
        var h = new UIHarness(() => grid);
        Assert.Equal(100, grid.ColumnActualWidths[0], 3);
        Assert.Equal(300, grid.ColumnActualWidths[1], 3);

        grid.Columns[0].Width = 150;
        h.Frame();
        Assert.Equal(150, grid.ColumnActualWidths[0], 3);
        Assert.Equal(250, grid.ColumnActualWidths[1], 3);
    }

    [Fact]
    public void Changing_the_grid_column_width_after_mount_reallocates_the_columns()
    {
        var items = new List<Item> { new Item { Name = "a" } };
        var grid = new DataGrid { Style = "height:120px;width:400px;", ItemsSource = items };
        grid.Columns.Add(new DataGridTextColumn { Header = "A", Binding = "Name" });
        grid.Columns.Add(new DataGridTextColumn { Header = "B", Binding = "Name" });
        var h = new UIHarness(() => grid);
        Assert.Equal(200, grid.ColumnActualWidths[0], 3);

        grid.ColumnWidth = DataGridLength.Pixel(80);
        h.Frame();

        Assert.Equal(80, grid.ColumnActualWidths[0], 3);
        Assert.Equal(80, grid.ColumnActualWidths[1], 3);
    }

    [Fact]
    public void Auto_width_grows_with_content_and_is_capped()
    {
        var small = new DataGrid { AutoGenerateColumns = true, ItemsSource = new[] { new Item { Name = "a" } } };
        var large = new DataGrid { AutoGenerateColumns = true, ItemsSource = new[] { new Item { Name = new string('x', 80) } } };
        small.ColumnWidth = DataGridLength.Auto;
        large.ColumnWidth = DataGridLength.Auto;
        new UIHarness(() => small);
        new UIHarness(() => large);

        Assert.True(large.Columns[0].ActualWidth > small.Columns[0].ActualWidth);
        Assert.True(large.Columns[0].ActualWidth <= 400);
    }
}
