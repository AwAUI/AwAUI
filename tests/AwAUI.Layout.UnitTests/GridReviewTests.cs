using AwAUI.Tests.Shared;
using static AwAUI.Layout.UnitTests.T;

namespace AwAUI.Layout.UnitTests;

/// <summary>
/// 第八批评审发现的问题的回归测试。期望值都在 headless Chromium（Playwright chromium_headless_shell-1243，
/// 等宽字体按每字符 10px、行高 20px）中用等价 HTML 量过，注释给出 HTML 与按规范的推导。
/// </summary>
public class GridReviewTests
{
    static LayoutStyle G(GridTrackList? cols = null, GridTrackList? rows = null) =>
        LayoutStyle.Default with { Display = Display.Grid, GridTemplateColumns = cols, GridTemplateRows = rows };

    static GridTrackList L(params GridTrackSize[] t) => GridTrackList.Of(t);

    static readonly GridTrackSize MinMaxAuto40 = GridTrackSize.MinMax(GridBreadth.Auto, GridBreadth.Px(40));

    [Fact]
    public void Non_replaced_grid_item_with_aspect_ratio_fills_its_column()
    {
        // §6.2 normal：有宽高比的项目按块级元素的规则——非替换元素在行内轴撑满，块轴高度由比例推出。
        // <div style="grid-template-columns:200px"><div style="aspect-ratio:2">aaaaa</div></div> → 200×100（Chrome）
        // <div style="grid-template-columns:1fr 100px;width:300px"><div style="aspect-ratio:1"></div>…</div> → 200×200，行高 200（Chrome）
        // 评审前 normal 对一切有宽高比的项目按 start：分别得 50×20、0×0
        var a = Text("aaaaa", Block with { AspectRatio = 2 });
        var g1 = N(G(L(200)), a);
        var e = N(Block with { AspectRatio = 1 }); var f = N(Block with { Height = 5 });
        var g2 = N(G(L(GridTrackSize.Fr(1), 100), L(GridTrackSize.Auto)) with { Width = 300 }, e, f);
        Run(N(Block, g1, g2), 500);
        Box(a, 0, 0, 200, 100);
        Box(e, 0, 0, 200, 200);   // 坐标相对父节点
        T.Size(g2, 300, 200);
        T.Eq(200, f.X);
    }

    [Fact]
    public void Explicit_start_or_stretch_with_aspect_ratio()
    {
        // justify-self:start → fit-content 宽 50、高 25；align-self:stretch（行高 auto）→ 300×150（Chrome 相同：比例优先于拉伸）
        var a = Text("aaaaa", Block with { AspectRatio = 2, JustifySelf = JustifySelf.Start });
        var b = Text("aaaaa", Block with { AspectRatio = 2, AlignSelf = AlignSelf.Stretch });
        Run(N(Block, N(G(L(300)), a, b)), 400);
        Box(a, 0, 0, 50, 25);
        Box(b, 0, 25, 300, 150);
    }

    [Fact]
    public void Replaced_leaf_in_grid_keeps_its_natural_size()
    {
        // 标记了 IsReplaced 的叶子按替换元素：normal = start，100×50。
        // 未标记的叶子即使测量比值与样式比值一致也按非替换元素（只有 IsReplaced 决定）：行内轴撑满 300，高度由比例推出 150
        var flagged = new LayoutNode(Block with { AspectRatio = 2, IsReplaced = true }) { Measure = new FixedMeasure(100, 50) };
        var unflagged = new LayoutNode(Block with { AspectRatio = 2 }) { Measure = new FixedMeasure(100, 50) };
        Run(N(Block, N(G(L(300)), flagged, unflagged)), 400);
        Box(flagged, 0, 0, 100, 50);
        Box(unflagged, 0, 50, 300, 150);
    }

    [Fact]
    public void Absolutely_positioned_text_leaf_uses_its_aspect_ratio()
    {
        // position:absolute; aspect-ratio:2 / 1/2，内容 "aaaaa"（50×20）：宽 = fit-content 50，高 = 25 / 100（Chrome）。
        // width:50px; aspect-ratio:5 的三行文本：比例高 10 < 内容 60 → 60（自动最小尺寸，Chrome）。
        // min-width:100px; aspect-ratio:2 → 100×50（Chrome）。
        // 评审前叶子在宽高都 auto 时用测量尺寸的比值（忽略样式比值）：分别得 50×20、50×20、50×10
        var a = Text("aaaaa", Block with { AspectRatio = 2, Position = Position.Absolute });
        var b = Text("aaaaa", Block with { AspectRatio = 0.5f, Position = Position.Absolute, Inset = new Edges<LengthPercentageAuto>(100, Auto, Auto, Auto) });
        var c = Text("aaa bbb ccc", Block with { AspectRatio = 5, Width = 50, Position = Position.Absolute });
        var d = Text("aaaaa", Block with { AspectRatio = 2, MinWidth = 100, Position = Position.Absolute, Inset = new Edges<LengthPercentageAuto>(200, Auto, Auto, Auto) });
        Run(N(Block with { Width = 300, Height = 300, Position = Position.Relative }, a, b, c, d), 400);
        Box(a, 0, 0, 50, 25);
        Box(b, 0, 100, 50, 100);
        Box(c, 0, 0, 50, 60);
        Box(d, 0, 200, 100, 50);
    }

    [Fact]
    public void Automatic_minimum_is_clamped_by_fixed_max_tracks()
    {
        // §6.6：只跨最大尺寸函数固定的轨道时，基于内容的最小尺寸不超过这些轨道（含其间间距）之和减去外边距。
        // minmax(auto, 100px) 中 200px 的单词 → 列 100（Chrome；评审前 200）
        var a = Text("aaaaaaaaaaaaaaaaaaaa"); var b = N(Block);
        var g1 = N(G(L(GridTrackSize.MinMax(GridBreadth.Auto, GridBreadth.Px(100)), 50)), a, b);
        // margin-left:30px → 项目宽 70，列仍为 100（Chrome [30, 0, 70×20]）
        var m = Text("aaaaaaaaaaaaaaaaaaaa", Block with { Margin = new Edges<LengthPercentageAuto>(0, 0, 0, 30) });
        var g2 = N(G(L(GridTrackSize.MinMax(GridBreadth.Auto, GridBreadth.Px(100)))), m);
        // 跨两条 minmax(auto, 40px)、间距 10：最小贡献钳制到 90 → 两列各 40（Chrome；评审前超出上限后两列各 95）
        var s = Text("aaaaaaaaaaaaaaaaaaaa", Block with { GridColumnStart = GridLine.Span(2) });
        var s1 = N(Block with { Height = 5 }); var s2 = N(Block with { Height = 5 });
        var g3 = N(G(L(MinMaxAuto40, MinMaxAuto40)) with { ColumnGap = 10 }, s, s1, s2);
        // 行：minmax(auto, 30px) 中三行文本（60 高）→ 行高 30，项目被拉伸到 30（Chrome）
        var r = Text("aaaa aaaa aaaa"); var r2 = N(Block);
        var g4 = N(G(L(50), L(GridTrackSize.MinMax(GridBreadth.Auto, GridBreadth.Px(30)), 10)), r, r2);
        Run(N(Block, g1, g2, g3, g4), 1000);
        Box(a, 0, 0, 100, 20);
        T.Eq(100, b.X);
        T.Eq(30, m.X);
        T.Eq(70, m.Width);
        T.Eq(90, s.Width);
        T.Eq(40, s1.Width);
        T.Eq(50, s2.X);
        T.Eq(40, s2.Width);
        T.Eq(30, r.Height);
        T.Eq(30, r2.Y);
    }

    [Fact]
    public void Many_identically_placed_spanning_items_stay_fast()
    {
        // 不可信 CSS：repeat(9999, auto) 的行列，5000 个项目都是 grid-area: 1 / 1 / -1 / -1。
        // 起止相同的项目在每个分配阶段只按最大贡献分配一次；评审前每个项目各做一次 O(跨度 log 跨度) 的分配
        // （10000 个项目约 18 s CPU），现在约 0.3 s
        var rep = new GridTrackList(new[] { new GridTrackRepeat(GridRepeatKind.Count, 9999, new[] { GridTrackSize.Auto }) });
        var items = Enumerable.Range(0, 5000)
            .Select(i => Text("aaaa", Block with { GridColumnStart = 1, GridColumnEnd = -1, GridRowStart = 1, GridRowEnd = -1 })).ToArray();
        var g = N(G(rep, rep), items);
        double t0 = ThreadCpuClock.NowMs();
        Run(N(Block, g), 400);
        double ms = ThreadCpuClock.NowMs() - t0;
        Assert.True(ms < 4000, ms + " ms");
        // 结果与单个项目相同：列 auto 被拉伸（400 / 9999），行由内容撑开后第一行起点为 0
        Assert.InRange(items[0].Width, 399.5f, 400.5f);   // 9999 条轨道的浮点累加误差
        T.Eq(0, items[0].Y);
    }

    [Fact]
    public void Identical_spanning_items_give_the_same_tracks_as_the_largest_one()
    {
        // 合并起止相同的项目不改变结果：三个跨两列 auto 的项目（min-content 100 / 200 / 150）与只有 200 的那个相同
        var cols = L(GridTrackSize.Auto, GridTrackSize.Auto);
        var p = Text("aaaaaaaaaa", Block with { GridColumnStart = 1, GridColumnEnd = 3, GridRowStart = 1 });
        var q = Text("aaaaaaaaaaaaaaaaaaaa", Block with { GridColumnStart = 1, GridColumnEnd = 3, GridRowStart = 1 });
        var r = Text("aaaaaaaaaaaaaaa", Block with { GridColumnStart = 1, GridColumnEnd = 3, GridRowStart = 1 });
        var x = Text("aa", Block with { GridRowStart = 2 });
        var g = N(G(cols) with { Display = Display.InlineGrid }, p, q, r, x);
        var solo = Text("aaaaaaaaaaaaaaaaaaaa", Block with { GridColumnStart = 1, GridColumnEnd = 3, GridRowStart = 1 });
        var y = Text("aa", Block with { GridRowStart = 2 });
        var g2 = N(G(cols) with { Display = Display.InlineGrid }, solo, y);
        Run(N(Block, g, g2), 1000);
        T.Eq(g2.Width, g.Width);
        T.Eq(y.Width, x.Width);
        T.Eq(200, g.Width);
        T.Eq(20, x.Width);
    }
}
