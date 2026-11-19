using static AwAUI.Layout.UnitTests.T;

namespace AwAUI.Layout.UnitTests;

/// <summary>换行、align-content、column、reverse、rtl。</summary>
public class FlexWrapDirectionTests
{
    static LayoutNode Box40x10() => N(Block with { Width = 40, Height = 10 });

    [Fact]
    public void Wrap_breaks_lines_and_auto_height_sums_lines()
    {
        // 100 宽放 40 宽项目：一行两个（80），第三个换行。高度 auto：两行各 10 → 20
        var a = Box40x10(); var b = Box40x10(); var c = Box40x10();
        var root = N(Row with { Width = 100, FlexWrap = FlexWrap.Wrap }, a, b, c);
        Run(root);
        Box(a, 0, 0, 40, 10);
        Box(b, 40, 0, 40, 10);
        Box(c, 0, 10, 40, 10);
        T.Size(root, 100, 20);
    }

    [Fact]
    public void Wrap_counts_gaps_when_breaking()
    {
        // column-gap 30：40+30+40 = 110 > 100 → 每行一个；row-gap 5 → y 0, 15, 30
        var a = Box40x10(); var b = Box40x10(); var c = Box40x10();
        var root = N(Row with { Width = 100, FlexWrap = FlexWrap.Wrap, ColumnGap = 30, RowGap = 5 }, a, b, c);
        Run(root);
        T.Eq(0, b.X);
        T.Eq(15, b.Y);
        T.Eq(30, c.Y);
        T.Size(root, 100, 40);
    }

    [Theory]
    [InlineData(AlignContent.FlexStart, 0, 10)]
    [InlineData(AlignContent.Start, 0, 10)]
    [InlineData(AlignContent.FlexEnd, 80, 90)]
    [InlineData(AlignContent.Center, 40, 50)]          // 自由空间 80，起点 40
    [InlineData(AlignContent.SpaceBetween, 0, 90)]
    [InlineData(AlignContent.SpaceAround, 20, 70)]     // 每行 40，两侧各 20
    [InlineData(AlignContent.SpaceEvenly, 26.667f, 63.333f)] // 80/3
    [InlineData(AlignContent.Stretch, 0, 50)]          // 每行增加 40 → 行高 50
    [InlineData(AlignContent.Normal, 0, 50)]
    public void Align_content_distributes_lines(AlignContent ac, float line1, float line2)
    {
        var a = Box40x10(); var b = Box40x10(); var c = Box40x10();
        Run(N(Row with { Width = 100, Height = 100, FlexWrap = FlexWrap.Wrap, AlignContent = ac }, a, b, c));
        T.Eq(line1, a.Y);
        T.Eq(line1, b.Y);
        T.Eq(line2, c.Y);
    }

    [Fact]
    public void Align_content_stretch_stretches_auto_height_items()
    {
        // 行高被拉伸到 50；高度 auto 的项目随行拉伸
        var a = N(Block with { Width = 40 });
        var b = N(Block with { Width = 40, Height = 10 });
        var c = N(Block with { Width = 40, Height = 10 });
        Run(N(Row with { Width = 100, Height = 100, FlexWrap = FlexWrap.Wrap }, a, b, c));
        Box(a, 0, 0, 40, 50);
        Box(c, 0, 50, 40, 10);
    }

    [Fact]
    public void Align_items_applies_within_each_line()
    {
        // align-content flex-start，各行高 = 行内最高项目；矮项目在行内居中
        var a = N(Block with { Width = 40, Height = 30 });
        var b = Box40x10();
        var c = Box40x10();
        Run(N(Row with { Width = 100, Height = 100, FlexWrap = FlexWrap.Wrap, AlignContent = AlignContent.FlexStart, AlignItems = AlignItems.Center }, a, b, c));
        T.Eq(0, a.Y);
        T.Eq(10, b.Y);
        T.Eq(30, c.Y);
    }

    [Fact]
    public void Wrap_reverse_stacks_lines_from_cross_end()
    {
        // 交叉起点在底部：第一行在 y=90，第二行在 y=80
        var a = Box40x10(); var b = Box40x10(); var c = Box40x10();
        Run(N(Row with { Width = 100, Height = 100, FlexWrap = FlexWrap.WrapReverse, AlignContent = AlignContent.FlexStart }, a, b, c));
        Box(a, 0, 90, 40, 10);
        Box(b, 40, 90, 40, 10);
        Box(c, 0, 80, 40, 10);
    }

    [Fact]
    public void Column_direction_stretches_width_and_grows_height()
    {
        // 300 高：A 50、C 50，B grow → 200；宽度拉伸到 200
        var a = N(Block with { Height = 50 });
        var b = N(Block with { FlexGrow = 1 });
        var c = N(Block with { Height = 50 });
        Run(N(Column with { Width = 200, Height = 300 }, a, b, c));
        Box(a, 0, 0, 200, 50);
        Box(b, 0, 50, 200, 200);
        Box(c, 0, 250, 200, 50);
    }

    [Fact]
    public void Column_auto_height_is_sum_of_items()
    {
        var root = N(Column with { Width = 100 }, N(Block with { Height = 30 }), N(Block with { Height = 40, Margin = Margin(5) }));
        Run(root);
        T.Size(root, 100, 80);
    }

    [Fact]
    public void Column_items_have_content_based_minimum_height()
    {
        // 文本 "aaa bbb ccc ddd" 在 100 宽时两行 → 40 高；容器 30 高也不能把它压到 40 以下
        var t = Text("aaa bbb ccc ddd");
        Run(N(Column with { Width = 100, Height = 30 }, t));
        Box(t, 0, 0, 100, 40);
    }

    [Fact]
    public void Column_text_wraps_at_stretched_width()
    {
        var t = Text("aaa bbb ccc ddd");
        var root = N(Column with { Width = 100 }, t);
        Run(root);
        Box(t, 0, 0, 100, 40);
        T.Size(root, 100, 40);
    }

    [Fact]
    public void Column_with_non_stretch_alignment_uses_fit_content_width()
    {
        // align-items flex-start：文本宽 = max-content 70；center：在 200 中居中 → 65
        var a = Text("aaa bbb");
        var b = Text("aaa bbb", Block with { AlignSelf = AlignSelf.Center });
        Run(N(Column with { Width = 200, AlignItems = AlignItems.FlexStart }, a, b));
        Box(a, 0, 0, 70, 20);
        Box(b, 65, 20, 70, 20);
    }

    [Fact]
    public void Row_reverse_places_first_item_at_right()
    {
        var a = N(Block with { Width = 50, Height = 10 });
        var b = N(Block with { Width = 60, Height = 10 });
        Run(N(Row with { Width = 300, Height = 10, FlexDirection = FlexDirection.RowReverse }, a, b));
        T.Eq(250, a.X);
        T.Eq(190, b.X);
    }

    [Fact]
    public void Column_reverse_places_first_item_at_bottom()
    {
        var a = N(Block with { Height = 50 });
        var b = N(Block with { Height = 60 });
        Run(N(Column with { Width = 100, Height = 300, FlexDirection = FlexDirection.ColumnReverse }, a, b));
        T.Eq(250, a.Y);
        T.Eq(190, b.Y);
    }

    [Fact]
    public void Rtl_row_starts_at_right_and_rtl_row_reverse_at_left()
    {
        var a = N(Block with { Width = 50, Height = 10 });
        var b = N(Block with { Width = 60, Height = 10 });
        Run(N(Row with { Width = 300, Height = 10, Direction = Direction.Rtl }, a, b));
        T.Eq(250, a.X);
        T.Eq(190, b.X);

        var c = N(Block with { Width = 50, Height = 10 });
        var d = N(Block with { Width = 60, Height = 10 });
        Run(N(Row with { Width = 300, Height = 10, Direction = Direction.Rtl, FlexDirection = FlexDirection.RowReverse }, c, d));
        T.Eq(0, c.X);
        T.Eq(50, d.X);
    }

    [Fact]
    public void Rtl_margins_and_padding_are_physical()
    {
        // rtl row：主轴起点在右，起始外边距是 margin-right，内容盒右边界 = 300 - padding-right 20
        var a = N(Block with { Width = 50, Height = 10, Margin = Margin(0, 10, 0, 0) });
        Run(N(Row with { Width = 300, Height = 10, Direction = Direction.Rtl, Padding = new(0, 20, 0, 0), BoxSizing = BoxSizing.BorderBox }, a));
        T.Eq(300 - 20 - 10 - 50, a.X);
    }

    [Fact]
    public void Justify_start_follows_writing_direction_not_flex_direction()
    {
        // row-reverse（ltr）+ justify-content:start：整体靠左，但顺序仍反向 → b 在 0，a 在 60
        var a = N(Block with { Width = 50, Height = 10 });
        var b = N(Block with { Width = 60, Height = 10 });
        Run(N(Row with { Width = 300, Height = 10, FlexDirection = FlexDirection.RowReverse, JustifyContent = JustifyContent.Start }, a, b));
        T.Eq(60, a.X);
        T.Eq(0, b.X);

        // 对照：flex-start 靠右
        var c = N(Block with { Width = 50, Height = 10 });
        Run(N(Row with { Width = 300, Height = 10, FlexDirection = FlexDirection.RowReverse, JustifyContent = JustifyContent.FlexStart }, c));
        T.Eq(250, c.X);
    }

    [Fact]
    public void Rtl_column_cross_axis_starts_at_right()
    {
        // column + rtl：交叉起点在右，flex-start → x = 200 - 50
        var a = N(Block with { Width = 50, Height = 10 });
        Run(N(Column with { Width = 200, Direction = Direction.Rtl, AlignItems = AlignItems.FlexStart }, a));
        T.Eq(150, a.X);
    }
}
