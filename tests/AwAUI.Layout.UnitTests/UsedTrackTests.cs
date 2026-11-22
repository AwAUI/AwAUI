using static AwAUI.Layout.UnitTests.T;

namespace AwAUI.Layout.UnitTests;

/// <summary>
/// <see cref="LayoutNode.UsedTracksX"/> / <see cref="LayoutNode.UsedTracksY"/>：最近一次完整布局使用的轨道尺寸
/// （逻辑像素，不含间距）与起点（相对容器内容盒）。DevTools 的 <c>Layout.getLayoutInfo</c> 与布局网格叠加读取它们。
/// </summary>
public class UsedTrackTests
{
    [Fact]
    public void Flex_row_records_each_items_main_size_and_position()
    {
        var a = N(Block with { Width = 100 });
        var b = N(Block with { Width = 200 });
        var f = N(LayoutStyle.Default with { Display = Display.Flex, ColumnGap = 10 }, a, b);

        Run(N(Block with { Width = 600 }, f), 600);

        Assert.Equal(new[] { 100f, 200f }, f.UsedTracksX);
        Assert.Equal(new[] { 0f, 110f }, f.UsedTrackPositionsX);
        // flex 只有主轴上的轨道
        Assert.Null(f.UsedTracksY);
        Assert.Null(f.UsedTrackPositionsY);
        Assert.Null(f._usedTracksY);
    }

    [Fact]
    public void Flex_column_records_y_axis_only()
    {
        var a = N(Block with { Height = 30 });
        var b = N(Block with { Height = 50 });
        var f = N(LayoutStyle.Default with { Display = Display.Flex, FlexDirection = FlexDirection.Column, RowGap = 15 }, a, b);

        Run(N(Block with { Height = 200 }, f), 1000, 200);

        Assert.Null(f._usedTracksX);
        Assert.Equal(new[] { 30f, 50f }, f.UsedTracksY);
        Assert.Equal(new[] { 0f, 45f }, f.UsedTrackPositionsY);
    }

    [Fact]
    public void Grid_fr_tracks_exclude_gaps_from_sizes_but_not_positions()
    {
        var a = N(Block);
        var b = N(Block);
        var g = N(LayoutStyle.Default with
        {
            Display = Display.Grid,
            GridTemplateColumns = GridTrackList.Of(GridTrackSize.Fr(1), GridTrackSize.Fr(2)),
            GridTemplateRows = GridTrackList.Of(GridTrackSize.Px(10)),
            ColumnGap = 30,
        }, a, b);

        Run(N(Block with { Width = 600 }, g), 600);

        // 600 - 30（间距）= 570 → 1fr = 190、2fr = 380
        Assert.Equal(new[] { 190f, 380f }, g.UsedTracksX);
        Assert.Equal(new[] { 0f, 220f }, g.UsedTrackPositionsX);
        Assert.Equal(new[] { 10f }, g.UsedTracksY);
        Assert.Equal(new[] { 0f }, g.UsedTrackPositionsY);
    }

    [Fact]
    public void Tracks_are_cleared_when_display_is_no_longer_flex_or_grid()
    {
        var f = N(LayoutStyle.Default with { Display = Display.Flex }, N(Block with { Width = 100 }));
        var root = N(Block, f);

        Run(root);
        Assert.NotNull(f._usedTracksX);

        f.Style = Block;
        Run(root);
        Assert.Null(f._usedTracksX);
        Assert.Null(f._usedTracksY);
        Assert.Null(f._usedTrackPositionsX);
        Assert.Null(f._usedTrackPositionsY);
    }
}
