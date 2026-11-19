using AwAUI.Tests.Shared;
using static AwAUI.Layout.UnitTests.T;

namespace AwAUI.Layout.UnitTests;

/// <summary>
/// CSS Grid Layout 1：显式 / 隐式网格、放置、轨道尺寸算法与对齐。期望值按规范推导（注释中写出推导过程），
/// 其中有文本或固定尺寸项目的 20 个场景另在 headless Chromium（Playwright chromium_headless_shell-1243）中用等价 HTML
/// （等宽字体按每字符 10px、行高 20px）量过，结果一致（字体造成的 ≤ 0.05px 差异除外）。基线、rtl 以外的少数情形只按规范推导。
/// </summary>
public class GridLayoutTests
{
    static LayoutStyle G(GridTrackList? cols = null, GridTrackList? rows = null) =>
        LayoutStyle.Default with { Display = Display.Grid, GridTemplateColumns = cols, GridTemplateRows = rows };

    static GridTrackList L(params GridTrackSize[] t) => GridTrackList.Of(t);

    static LayoutStyle Item(GridLine rs = default, GridLine re = default, GridLine cs = default, GridLine ce = default) =>
        Block with { GridRowStart = rs, GridRowEnd = re, GridColumnStart = cs, GridColumnEnd = ce };

    [Fact]
    public void Fixed_tracks_and_auto_placement_fill_rows_then_create_implicit_rows()
    {
        // 两列 100px 200px、一行 50px：第 3 个项目进入隐式的第 2 行（grid-auto-rows: auto，空项目 → 高 0）
        var a = N(Block); var b = N(Block); var c = N(Block with { Height = 30 });
        var g = N(G(L(100, 200), L(50)), a, b, c);
        Run(N(Block, g), 500);
        Box(a, 0, 0, 100, 50);
        Box(b, 100, 0, 200, 50);
        Box(c, 0, 50, 100, 30);
        T.Size(g, 500, 80);
    }

    [Fact]
    public void Fr_tracks_share_free_space_after_gaps()
    {
        // 600 - 30（间距）= 570 → 1fr = 190：列 190、380，第二列起点 190 + 30
        var a = N(Block); var b = N(Block);
        var g = N(G(L(GridTrackSize.Fr(1), GridTrackSize.Fr(2)), L(10)) with { ColumnGap = 30 }, a, b);
        Run(N(Block, g), 600);
        Box(a, 0, 0, 190, 10);
        Box(b, 220, 0, 380, 10);
    }

    [Fact]
    public void Fr_track_does_not_shrink_below_min_content_but_minmax_zero_does()
    {
        // 1fr = minmax(auto, 1fr)：自动最小尺寸为 min-content（100）。求 fr：100/2 = 50 < 100，第一列按不可伸缩处理，
        // 剩余 0 → 第二列 0（Chrome 相同，经典的 1fr 溢出）。minmax(0, 1fr) 时两列各 50
        var t1 = Text("aaaaaaaaaa"); var t2 = N(Block);
        var g1 = N(G(L(GridTrackSize.Fr(1), GridTrackSize.Fr(1))), t1, t2);
        var m = GridTrackSize.MinMax(GridBreadth.Px(0), GridBreadth.Fr(1));
        var u1 = Text("aaaaaaaaaa"); var u2 = N(Block);
        var g2 = N(G(L(m, m)), u1, u2);
        Run(N(Block with { Width = 100 }, g1, g2), 500);
        Box(t1, 0, 0, 100, 20);
        T.Eq(100, t2.X);
        T.Eq(0, t2.Width);
        T.Eq(50, u1.Width);
        T.Eq(50, u2.X);
        T.Eq(50, u2.Width);
    }

    [Fact]
    public void Auto_tracks_size_to_content_then_stretch_equally()
    {
        // auto auto：基准 = 最小贡献 20 / 40，上限 = max-content 20 / 40；最大化后仍 20 / 40；
        // justify-content: normal 拉伸 auto 轨道：(500 - 60) / 2 = 220 → 240、260（Chrome 相同）
        var a = Text("aa"); var b = Text("bbbb");
        var g = N(G(L(GridTrackSize.Auto, GridTrackSize.Auto)), a, b);
        Run(N(Block, g), 500);
        Box(a, 0, 0, 240, 20);
        Box(b, 240, 0, 260, 20);
    }

    [Fact]
    public void Justify_content_start_disables_auto_track_stretching()
    {
        var a = Text("aa"); var b = Text("bbbb");
        var g = N(G(L(GridTrackSize.Auto, GridTrackSize.Auto)) with { JustifyContent = JustifyContent.Start }, a, b);
        Run(N(Block, g), 500);
        Box(a, 0, 0, 20, 20);
        Box(b, 20, 0, 40, 20);
    }

    [Fact]
    public void Fit_content_track_is_capped_by_its_argument()
    {
        // fit-content(100px)：基准 = min-content 40，上限 = min(max-content 140, 100) = 100；第二列 1fr 取剩余 400
        var a = Text("aaaa bbbb cccc"); var b = N(Block);
        var g = N(G(L(GridTrackSize.FitContent(100), GridTrackSize.Fr(1))), a, b);
        Run(N(Block, g), 500);
        Box(a, 0, 0, 100, 40);   // 100 宽："aaaa bbbb" 90 / "cccc"
        Box(b, 100, 0, 400, 40);
        // 内容短于上限时取 max-content
        var c = Text("ab"); var d = N(Block);
        var g2 = N(G(L(GridTrackSize.FitContent(100), GridTrackSize.Fr(1))), c, d);
        Run(N(Block, g2), 500);
        T.Eq(20, c.Width);
        T.Eq(480, d.Width);
    }

    [Fact]
    public void Spanning_item_grows_tracks_with_infinite_growth_limit()
    {
        // inline-grid（max-content）：列 auto auto。A（"aa" = 20）在第一列；B 跨两列，min-content 200。
        // 第 2 步：列 1 基准 20 / 上限 20，列 2 基准 0 / 上限 ∞。第 3 步把 200 - 20 = 180 分给两列：
        // 列 1 已到上限，列 2 得到 180。结果 20、180，容器宽 200
        var a = Text("aa");
        var b = N(Item(cs: 1, ce: 3), Text("xxxxxxxxxxxxxxxxxxxx"));
        var g = N(G(L(GridTrackSize.Auto, GridTrackSize.Auto)) with { Display = Display.InlineGrid }, a, b);
        Run(N(Block, g), 1000);
        T.Size(g, 200, 40);
        Box(a, 0, 0, 20, 20);
        Box(b, 0, 20, 200, 20);
    }

    [Fact]
    public void Template_areas_place_items_by_name()
    {
        var areas = GridAreas.FromRows("head head", "side main");
        Assert.NotNull(areas);
        var head = N(Block with { GridRowStart = GridLine.Named("head"), GridColumnStart = GridLine.Named("head"),
            GridRowEnd = GridLine.Named("head"), GridColumnEnd = GridLine.Named("head") });
        var side = N(Item(GridLine.Named("side"), GridLine.Named("side"), GridLine.Named("side"), GridLine.Named("side")));
        var main = N(Item(GridLine.Named("main"), GridLine.Named("main"), GridLine.Named("main"), GridLine.Named("main")));
        var g = N(G(L(100, GridTrackSize.Fr(1)), L(50, GridTrackSize.Fr(1))) with { GridTemplateAreas = areas, Height = 300 }, main, side, head);
        Run(N(Block, g), 400);
        Box(head, 0, 0, 400, 50);
        Box(side, 0, 50, 100, 250);
        Box(main, 100, 50, 300, 250);
    }

    [Fact]
    public void Areas_without_sized_tracks_use_grid_auto_sizes()
    {
        // 区域定义了 2 行 2 列，轨道列表都是 none：行列尺寸取 grid-auto-rows / columns（40px、auto）
        var areas = GridAreas.FromRows("a b", "c d");
        var d = N(Item(GridLine.Named("d"), default, GridLine.Named("d")));
        var g = N(G() with { GridTemplateAreas = areas, GridAutoRows = L(40) }, d);
        Run(N(Block, g), 300);
        // 两个 auto 列（只有 d 在第二列且无内容）拉伸为各 150
        Box(d, 150, 40, 150, 40);
        T.Eq(80, g.Height);
    }

    [Fact]
    public void Named_lines_and_negative_indices()
    {
        // [a] 100px [b] 200px [c]：b / c → x 100、宽 200；1 / -1 跨全部
        var cols = new GridTrackList(new[]
        {
            new GridTrackRepeat(GridRepeatKind.Count, 1, new GridTrackSize[] { 100, 200 }, new[] { new[] { "a" }, new[] { "b" }, new[] { "c" } }),
        });
        var x = N(Item(cs: GridLine.Named("b"), ce: GridLine.Named("c")));
        var y = N(Item(cs: 1, ce: -1));
        // 第一条名为 c 的线（第 3 条线）→ 隐式第 3 列。三个项目都是「列确定、行自动」：稀疏光标放 x 于第 0 行；
        // y 的列起点 0 小于光标列 1 → 光标进到第 1 行；z 的列起点 2 不小于光标列 → 仍在第 1 行且不冲突
        var z = N(Item(cs: GridLine.Line(1, "c")));
        var g = N(G(cols, L(10)) with { GridAutoColumns = L(50), GridAutoRows = L(10) }, x, y, z);
        Run(N(Block, g), 400);
        Box(x, 100, 0, 200, 10);
        Box(y, 0, 10, 300, 10);
        Box(z, 300, 10, 50, 10);
    }

    [Fact]
    public void Missing_named_line_resolves_to_first_implicit_line()
    {
        // 没有名为 foo 的线时，显式网格之后的隐式线都视作名为 foo（§8.3.1）：两列时显式线为 1–3，grid-column-start: foo
        // → 第一条隐式线 = 第 4 条线。线 3–4 之间多出一条空的隐式列（30px），项目在线 4–5：x = 100 + 100 + 30
        var x = N(Item(cs: GridLine.Named("foo")));
        var g = N(G(L(100, 100), L(10)) with { GridAutoColumns = L(30) }, x);
        Run(N(Block, g), 400);
        Box(x, 230, 0, 30, 10);
    }

    [Fact]
    public void Sparse_and_dense_auto_placement()
    {
        // 三列：A span 2、B span 2、C span 1。稀疏：A 第 0 行 0–2；B 放不下第 0 行第 2 列 → 第 1 行 0–2；C 跟随光标 → 第 1 行第 2 列。
        // 稠密：C 回填第 0 行第 2 列
        foreach (var dense in new[] { false, true })
        {
            var a = N(Item(cs: GridLine.Span(2)));
            var b = N(Item(cs: GridLine.Span(2)));
            var c = N(Block);
            var g = N(G(L(100, 100, 100)) with { GridAutoRows = L(10), GridAutoFlow = dense ? GridAutoFlow.RowDense : GridAutoFlow.Row }, a, b, c);
            Run(N(Block, g), 300);
            Box(a, 0, 0, 200, 10);
            Box(b, 0, 10, 200, 10);
            if (dense) Box(c, 200, 0, 100, 10);
            else Box(c, 200, 10, 100, 10);
        }
    }

    [Fact]
    public void Items_locked_to_a_row_are_placed_before_fully_auto_items()
    {
        // 第 1 步先放「行确定、列自动」的项目：B 在第 1 行（第 2 条线）→ 占第 1 行第 0 列；A、C 完全自动：A 第 0 行第 0 列，
        // C 第 0 行第 1 列；D（行确定为第 1 行）在第 1 步中排在 B 之后 → 第 1 行第 1 列
        var a = N(Block);
        var b = N(Item(rs: 2));
        var c = N(Block);
        var d = N(Item(rs: 2));
        var g = N(G(L(100, 100)) with { GridAutoRows = L(10) }, a, b, c, d);
        Run(N(Block, g), 200);
        Box(a, 0, 0, 100, 10);
        Box(b, 0, 10, 100, 10);
        Box(c, 100, 0, 100, 10);
        Box(d, 100, 10, 100, 10);
    }

    [Fact]
    public void Column_auto_flow_fills_columns()
    {
        var a = N(Block); var b = N(Block); var c = N(Block);
        var g = N(G(null, L(20, 20)) with { GridAutoFlow = GridAutoFlow.Column, GridAutoColumns = L(50) }, a, b, c);
        Run(N(Block, g), 300);
        Box(a, 0, 0, 50, 20);
        Box(b, 0, 20, 50, 20);
        Box(c, 50, 0, 50, 20);
    }

    [Fact]
    public void Implicit_tracks_before_the_explicit_grid()
    {
        // grid-column: span 2 / 1 → 线 -1 与 0（第 1 条线之前的两条隐式列，各 grid-auto-columns 30px）。
        // 隐式列在前：x 0–60；显式列从 60 开始
        var x = N(Item(cs: GridLine.Span(2), ce: 1));
        var y = N(Item(rs: 1, cs: 1));
        var g = N(G(L(100), L(10)) with { GridAutoColumns = L(30) }, x, y);
        Run(N(Block, g), 400);
        Box(x, 0, 0, 60, 10);
        Box(y, 60, 0, 100, 10);
    }

    [Fact]
    public void Implicit_track_sizes_cycle_through_grid_auto_columns()
    {
        // grid-auto-columns: 10px 20px：显式之后依次 10、20、10；显式之前的最后一条取最后一个尺寸（20）
        var items = new LayoutNode[4];
        items[0] = N(Item(rs: 1, cs: 2));
        items[1] = N(Item(rs: 1, cs: 3));
        items[2] = N(Item(rs: 1, cs: 4));
        items[3] = N(Item(rs: 1, cs: -3, ce: -2)); // 一条显式列（线 1、2）时 -3 是线 0 → 第 1 条线之前的隐式列
        var g = N(G(L(100), L(10)) with { GridAutoColumns = L(10, 20), JustifyContent = JustifyContent.Start }, items);
        Run(N(Block, g), 400);
        T.Eq(20, items[3].Width);
        T.Eq(0, items[3].X);
        T.Eq(120, items[0].X); T.Eq(10, items[0].Width);
        T.Eq(130, items[1].X); T.Eq(20, items[1].Width);
        T.Eq(150, items[2].X); T.Eq(10, items[2].Width);
    }

    [Fact]
    public void Percentage_tracks_and_gaps_resolve_against_the_content_box()
    {
        // 宽 400：列 50% 25%（200、100），列间距 10% = 40
        var a = N(Block); var b = N(Block);
        var g = N(G(L(GridTrackSize.Percent(50), GridTrackSize.Percent(25)), L(10)) with { ColumnGap = LengthPercentage.Percent(10) }, a, b);
        Run(N(Block, g), 400);
        Box(a, 0, 0, 200, 10);
        Box(b, 240, 0, 100, 10);
    }

    [Fact]
    public void Content_distribution_moves_tracks()
    {
        // 两列 100px，宽 400：center → 起点 100；space-between → 0 与 300；space-evenly → 200/3
        var cases = new (JustifyContent J, float A, float B)[]
        {
            (JustifyContent.Center, 100, 200),
            (JustifyContent.SpaceBetween, 0, 300),
            (JustifyContent.SpaceEvenly, 200f / 3, 2 * 200f / 3 + 100),
            (JustifyContent.End, 200, 300),
        };
        foreach (var (j, ea, eb) in cases)
        {
            var a = N(Block); var b = N(Block);
            Run(N(Block, N(G(L(100, 100), L(10)) with { JustifyContent = j }, a, b)), 400);
            T.Eq(ea, a.X);
            T.Eq(eb, b.X);
        }
    }

    [Fact]
    public void Align_content_with_definite_height()
    {
        var a = N(Block);
        var g = N(G(L(100), L(50)) with { Height = 200, AlignContent = AlignContent.Center }, a);
        Run(N(Block, g), 400);
        Box(a, 0, 75, 100, 50);
    }

    [Fact]
    public void Self_alignment_inside_the_area()
    {
        // 区域 200×100；项目 50×20：center → (75, 40)；end → (150, 80)；auto 外边距与 center 相同
        var c = N(Block with { Width = 50, Height = 20, JustifySelf = JustifySelf.Center, AlignSelf = AlignSelf.Center });
        var e = N(Block with { Width = 50, Height = 20, JustifySelf = JustifySelf.End, AlignSelf = AlignSelf.End });
        var m = N(Block with { Width = 50, Height = 20, Margin = new(LengthPercentageAuto.Auto) });
        var g = N(G(L(200), L(100, 100, 100)), c, e, m);
        Run(N(Block, g), 400);
        Box(c, 75, 40, 50, 20);
        Box(e, 150, 180, 50, 20);
        Box(m, 75, 240, 50, 20);
    }

    [Fact]
    public void Justify_items_start_uses_fit_content_width()
    {
        var t = Text("aaa bbb");
        var g = N(G(L(200), L(30)) with { JustifyItems = JustifyItems.Start, AlignItems = AlignItems.Start }, t);
        Run(N(Block, g), 400);
        Box(t, 0, 0, 70, 20);
    }

    [Fact]
    public void Rows_size_to_wrapped_text_in_their_columns()
    {
        // 列 100px：文本 "aaa bbb ccc" 在 100 宽时折成两行（70 / 30）→ 行高 40；1fr 行与 auto 行
        var t = Text("aaa bbb ccc");
        var other = N(Block);
        var g = N(G(L(100, 100)), t, other);
        Run(N(Block, g), 400);
        T.Eq(40, g.Height);
        Box(other, 100, 0, 100, 40);  // 被拉伸到行高
    }

    [Fact]
    public void Row_spanning_item_increases_auto_rows()
    {
        // 两行 auto：A 跨两行、高 100；B 在第 1 行高 20。第 2 步：行 1 = 20（上限 20），行 2 没有单跨项目 → 0（上限 ∞）。
        // 第 3 步：100 - 20 = 80 → 行 1 已到上限，全部给上限无穷的行 2 → 20、80
        var a = N(Item(rs: 1, re: 3, cs: 1) with { Height = 100 });
        var b = N(Item(rs: 1, cs: 2) with { Height = 20 });
        var g = N(G(L(50, 50), L(GridTrackSize.Auto, GridTrackSize.Auto)) with { AlignItems = AlignItems.Start }, a, b);
        Run(N(Block, g), 400);
        Box(b, 50, 0, 50, 20);
        T.Eq(100, g.Height);
        // 行 2 有一个空项目时它的增长上限是 0（有限）：两行都到上限，超出部分分给所有固有最大尺寸的轨道 → 各 40：60、40
        var a2 = N(Item(rs: 1, re: 3, cs: 1) with { Height = 100 });
        var b2 = N(Item(rs: 1, cs: 2) with { Height = 20 });
        var c2 = N(Item(rs: 2, cs: 2));
        var g2 = N(G(L(50, 50), L(GridTrackSize.Auto, GridTrackSize.Auto)), a2, b2, c2);
        Run(N(Block, g2), 400);
        Box(c2, 50, 60, 50, 40);
        T.Eq(100, g2.Height);
    }

    [Fact]
    public void Inline_grid_with_fr_columns_uses_max_content_flex_fraction()
    {
        // 1fr 2fr，两个项目 max-content 30：不确定空间下 1fr = max(30/1, 30/2) = 30 → 列 30、60，容器宽 90
        var a = Text("aaa"); var b = Text("bbb");
        var g = N(G(L(GridTrackSize.Fr(1), GridTrackSize.Fr(2))) with { Display = Display.InlineGrid }, a, b);
        Run(N(Block, g), 1000);
        T.Eq(90, g.Width);
        Box(a, 0, 0, 30, 20);
        Box(b, 30, 0, 60, 20);
    }

    [Fact]
    public void Auto_fill_repeats_as_many_tracks_as_fit()
    {
        // 宽 350，repeat(auto-fill, 100px)，间距 10：n = floor((350 + 10) / 110) = 3。第 4 个项目换到第 2 行
        var rep = new GridTrackList(new[] { new GridTrackRepeat(GridRepeatKind.AutoFill, 1, new GridTrackSize[] { 100 }) });
        var items = Enumerable.Range(0, 4).Select(_ => N(Block)).ToArray();
        var g = N(G(rep) with { ColumnGap = 10, GridAutoRows = L(10) }, items);
        Run(N(Block, g), 350);
        T.Eq(220, items[2].X);
        Box(items[3], 0, 10, 100, 10);
    }

    [Fact]
    public void Auto_fit_collapses_empty_repeated_tracks()
    {
        // auto-fit：三条重复轨道只有一个项目 → 另两条折叠为 0、间距合并；justify-content: center 时唯一的列居中
        var rep = new GridTrackList(new[] { new GridTrackRepeat(GridRepeatKind.AutoFit, 1, new GridTrackSize[] { 100 }) });
        var a = N(Block);
        var g = N(G(rep, L(10)) with { ColumnGap = 10, JustifyContent = JustifyContent.Center }, a);
        Run(N(Block, g), 350);
        Box(a, 125, 0, 100, 10);
        // auto-fill 不折叠：三列 100 + 两个间距 = 320，居中起点 15
        var rep2 = new GridTrackList(new[] { new GridTrackRepeat(GridRepeatKind.AutoFill, 1, new GridTrackSize[] { 100 }) });
        var b = N(Block);
        Run(N(Block, N(G(rep2, L(10)) with { ColumnGap = 10, JustifyContent = JustifyContent.Center }, b)), 350);
        T.Eq(15, b.X);
    }

    [Fact]
    public void Auto_fill_with_indefinite_width_uses_max_width_or_one_repetition()
    {
        var rep = new GridTrackList(new[] { new GridTrackRepeat(GridRepeatKind.AutoFill, 1, new GridTrackSize[] { 50 }) });
        var g1 = N(G(rep) with { Display = Display.InlineGrid, MaxWidth = 160 });
        var g2 = N(G(rep) with { Display = Display.InlineGrid });
        Run(N(Block, g1, g2), 1000);
        T.Eq(150, g1.Width);   // floor(160 / 50) = 3
        T.Eq(50, g2.Width);    // 没有确定尺寸：重复 1 次
    }

    [Fact]
    public void Absolutely_positioned_child_uses_its_grid_area_as_containing_block()
    {
        // 定位的网格；绝对定位子节点 grid-column: 2 / 3、grid-row: 1 / 2，left/right/top/bottom 0 → 填满该区域
        var abs = N(Item(rs: 1, re: 2, cs: 2, ce: 3) with { Position = Position.Absolute, Inset = new(LengthPercentageAuto.Px(0)) });
        var auto = N(Block with { Position = Position.Absolute, Inset = new(LengthPercentageAuto.Px(0)) });
        var g = N(G(L(100, 200), L(50, 50)) with { Position = Position.Relative, Padding = new(5) }, abs, auto);
        Run(N(Block, g), 400);
        Box(abs, 105, 5, 200, 50);
        // 没有放置属性：包含块是内边距盒
        Box(auto, 0, 0, 400, 110);
    }

    [Fact]
    public void Absolute_child_with_lines_outside_the_grid_falls_back_to_padding_edges()
    {
        var abs = N(Item(cs: 2, ce: 9) with { Position = Position.Absolute, Inset = new(LengthPercentageAuto.Px(0)) });
        var g = N(G(L(100, 200), L(50)) with { Position = Position.Relative }, abs);
        Run(N(Block, g), 400);
        // 起始线 2 存在（x 100），结束线 9 超出 → 内边距边（400）
        Box(abs, 100, 0, 300, 50);
    }

    [Fact]
    public void Order_changes_auto_placement_order()
    {
        var a = N(Block);
        var b = N(Block with { Order = -1 });
        var g = N(G(L(100, 100), L(10)), a, b);
        Run(N(Block, g), 200);
        T.Eq(0, b.X);
        T.Eq(100, a.X);
    }

    [Fact]
    public void Grid_baseline_comes_from_first_item_in_grid_order()
    {
        // 第 1 行中网格顺序最前的是放在第 1 列的 B（文本基线 16）；B 在区域中 align-self: end，区域高 50、文本高 20 → 基线 30 + 16
        var a = N(Item(rs: 1, cs: 2), Text("x"));
        var b = Text("y", Item(rs: 1, cs: 1) with { AlignSelf = AlignSelf.End });
        var g = N(G(L(50, 50), L(50)), a, b);
        Run(N(Block, g), 200);
        T.Eq(46, g.Baseline);
    }

    [Fact]
    public void Rtl_grid_places_columns_from_the_right()
    {
        var a = N(Block); var b = N(Block with { JustifySelf = JustifySelf.Start, Width = 10 });
        var g = N(G(L(100, 50), L(10)) with { Direction = Direction.Rtl, JustifyContent = JustifyContent.Start }, a, b);
        Run(N(Block, g), 400);
        Box(a, 300, 0, 100, 10);
        Box(b, 290, 0, 10, 10);    // start = 行内起始侧 = 右
    }

    [Fact]
    public void Min_and_max_width_of_inline_grid_bound_flex_fraction()
    {
        // inline-grid, 1fr 1fr，内容 30 + 30 = 60 < min-width 200 → 按 200 重新求 fr：各 100
        var a = Text("aaa"); var b = Text("bbb");
        var g = N(G(L(GridTrackSize.Fr(1), GridTrackSize.Fr(1))) with { Display = Display.InlineGrid, MinWidth = 200 }, a, b);
        Run(N(Block, g), 1000);
        T.Eq(200, g.Width);
        T.Eq(100, a.Width);
        T.Eq(100, b.X);
    }

    [Fact]
    public void Max_content_minimum_and_min_content_maximum_tracks()
    {
        // minmax(max-content, 50px)：基准 = max-content 70 > 上限 50 → 上限提升到 70；min-content 列（"aaa bbb" → 30）
        var a = Text("aaa bbb"); var b = Text("aaa bbb");
        var g = N(G(L(GridTrackSize.MinMax(GridBreadth.MaxContent, GridBreadth.Px(50)), GridTrackSize.MinContent)) with { JustifyContent = JustifyContent.Start }, a, b);
        Run(N(Block, g), 400);
        T.Eq(70, a.Width);
        T.Eq(30, b.Width);
        T.Eq(40, b.Height);
    }

    [Fact]
    public void Empty_grid_with_explicit_tracks_has_their_size()
    {
        var g = N(G(L(100, 50), L(30, 30)) with { Display = Display.InlineGrid, RowGap = 5 });
        Run(N(Block, g), 400);
        T.Size(g, 150, 65);
    }

    [Fact]
    public void Margins_and_padding_of_items_and_container()
    {
        var a = N(Block with { Margin = new(LengthPercentageAuto.Px(5)) });
        var g = N(G(L(100), L(50)) with { Padding = new(10), Border = new(2f) }, a);
        Run(N(Block, g), 400);
        Box(a, 17, 17, 90, 40);
        T.Eq(74, g.Height);
    }

    [Fact]
    public void Huge_line_numbers_and_spans_are_clamped()
    {
        // grid-row: 1 / 100000000 → 钳制到 MaxLine：行数有界，布局很快
        var a = N(Item(rs: 1, re: 100_000_000, cs: GridLine.Span(100_000_000)));
        var b = N(Item(rs: -100_000_000));
        var c = N(Block);
        var g = N(G(L(10), L(10)), a, b, c);
        double t0 = ThreadCpuClock.NowMs();
        Run(N(Block, g), 400);
        double ms = ThreadCpuClock.NowMs() - t0;
        Assert.True(ms < 5000, ms + " ms");
        Assert.False(float.IsNaN(a.Height) || float.IsNaN(g.Height));
        Assert.True(a.Y >= 0 && b.Y >= 0 && c.Y >= 0);
    }

    [Fact]
    public void Huge_repeat_counts_are_capped()
    {
        var rep = new GridTrackList(new[] { new GridTrackRepeat(GridRepeatKind.Count, int.MaxValue, new GridTrackSize[] { 1 }) });
        Assert.Equal(GridLine.MaxLine, rep.Segments[0].Count);
        var a = N(Item(cs: -1));
        var g = N(G(rep, L(10)), a);
        Run(N(Block, g), 400);
        // 显式网格被截到 MaxLine - 1 = 9999 列（各 1px）；grid-column-start: -1 是最后一条显式线 → 项目在其后的隐式列，x = 9999
        T.Eq(GridLine.MaxLine - 1, a.X);
    }

    [Fact]
    public void Many_auto_placed_spanning_items_stay_fast()
    {
        // 2000 个跨 5000 列的自动项目（列数被钳制）：占用表按带存储，时间与跨度数值无关
        var items = Enumerable.Range(0, 2000).Select(i => N(Item(rs: GridLine.Span(1 + i % 7), cs: GridLine.Span(5000)))).ToArray();
        var g = N(G(L(1)) with { GridAutoFlow = GridAutoFlow.RowDense }, items);
        double t0 = ThreadCpuClock.NowMs();
        Run(N(Block, g), 400);
        double ms = ThreadCpuClock.NowMs() - t0;
        Assert.True(ms < 10000, ms + " ms");
    }
}
