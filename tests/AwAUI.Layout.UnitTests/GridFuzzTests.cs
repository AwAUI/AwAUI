using System.Globalization;
using AwAUI.Tests.Shared;
using static AwAUI.Layout.UnitTests.T;

namespace AwAUI.Layout.UnitTests;

/// <summary>
/// 网格布局的随机化测试：增量布局与全新布局逐节点一致（缓存键与脏标记）、结果总是有限值、嵌套网格的计算量随深度线性增长。
/// 随机数用固定种子，结果可复现。
/// </summary>
public class GridFuzzTests
{
    static readonly string[] Texts = { "a", "aaa bbb", "xx yyyy zzzzz w", "", "lorem ipsum dolor sit amet" };

    sealed class Txt : LayoutMeasure
    {
        readonly FakeText _inner;
        public readonly string Content;
        public Txt(string s) { Content = s; _inner = new FakeText(s); }
        public override Size Measure(LayoutNode node, in MeasureConstraints c) => _inner.Measure(node, c);
        public override float GetBaseline(LayoutNode node, Size contentSize) => _inner.GetBaseline(node, contentSize);
    }

    static GridTrackSize RandomTrack(Random r) => r.Next(9) switch
    {
        0 => GridTrackSize.Px(r.Next(80)),
        1 => GridTrackSize.Percent(r.Next(60)),
        2 => GridTrackSize.Fr(1 + r.Next(3)),
        3 => GridTrackSize.MinContent,
        4 => GridTrackSize.MaxContent,
        5 => GridTrackSize.FitContent(LengthPercentage.Px(r.Next(100))),
        6 => GridTrackSize.MinMax(GridBreadth.Px(r.Next(40)), GridBreadth.Fr(1)),
        7 => GridTrackSize.MinMax(GridBreadth.MinContent, GridBreadth.Px(r.Next(100))),
        _ => GridTrackSize.Auto,
    };

    static GridTrackList? RandomList(Random r)
    {
        int k = r.Next(5);
        if (k == 0) return null;
        var segs = new List<GridTrackRepeat>();
        for (int i = 0; i < k; i++)
        {
            int n = 1 + r.Next(2);
            var tracks = Enumerable.Range(0, n).Select(_ => RandomTrack(r)).ToArray();
            var names = r.Next(3) == 0 ? Enumerable.Range(0, n + 1).Select(j => (IReadOnlyList<string>)new[] { "l" + r.Next(3) }).ToArray() : null;
            if (i == 0 && r.Next(5) == 0)
                segs.Add(new GridTrackRepeat(r.Next(2) == 0 ? GridRepeatKind.AutoFill : GridRepeatKind.AutoFit, 1,
                    new[] { GridTrackSize.Px(20 + r.Next(60)) }));
            else segs.Add(new GridTrackRepeat(GridRepeatKind.Count, 1 + r.Next(2), tracks, names));
        }
        return new GridTrackList(segs);
    }

    static GridLine RandomLine(Random r) => r.Next(7) switch
    {
        0 => GridLine.Line(1 + r.Next(4)),
        1 => GridLine.Line(-1 - r.Next(3)),
        2 => GridLine.Span(1 + r.Next(3)),
        3 => GridLine.Named(r.Next(2) == 0 ? "l1" : "hd"),
        4 => GridLine.Span(1, "l" + r.Next(3)),
        _ => GridLine.Auto,
    };

    static LayoutStyle RandomStyle(Random r, bool leaf)
    {
        var s = Block;
        if (!leaf)
        {
            int d = r.Next(6);
            s = s with { Display = d < 3 ? Display.Grid : d == 3 ? Display.InlineGrid : d == 4 ? Display.Flex : Display.Block };
            if (s.Display is Display.Grid or Display.InlineGrid)
            {
                s = s with
                {
                    GridTemplateColumns = RandomList(r),
                    GridTemplateRows = r.Next(2) == 0 ? RandomList(r) : null,
                    GridAutoFlow = (GridAutoFlow)r.Next(4),
                    GridAutoColumns = r.Next(3) == 0 ? GridTrackList.Of(RandomTrack(r)) : null,
                    GridAutoRows = r.Next(3) == 0 ? GridTrackList.Of(RandomTrack(r), RandomTrack(r)) : null,
                    JustifyItems = (JustifyItems)r.Next(12),
                    AlignItems = (AlignItems)r.Next(8),
                    JustifyContent = (JustifyContent)r.Next(10),
                    AlignContent = (AlignContent)r.Next(10),
                    ColumnGap = r.Next(3) == 0 ? LengthPercentage.Percent(r.Next(10)) : r.Next(12),
                    RowGap = r.Next(8),
                };
                if (r.Next(4) == 0) s = s with { GridTemplateAreas = GridAreas.FromRows("hd hd .", "a b b") };
            }
            s = s with { Direction = (Direction)r.Next(2) };
        }
        if (r.Next(2) == 0) s = s with { GridColumnStart = RandomLine(r), GridColumnEnd = RandomLine(r) };
        if (r.Next(2) == 0) s = s with { GridRowStart = RandomLine(r), GridRowEnd = RandomLine(r) };
        if (r.Next(4) == 0) s = s with { JustifySelf = (JustifySelf)r.Next(13), AlignSelf = (AlignSelf)r.Next(9) };
        if (r.Next(4) == 0) s = s with { Order = r.Next(3) - 1 };
        if (r.Next(4) == 0) s = s with { Width = r.Next(3) switch { 0 => Dimension.Percent(r.Next(100)), 1 => Dimension.Px(r.Next(200)), _ => Dimension.MinContent } };
        if (r.Next(5) == 0) s = s with { Height = r.Next(2) == 0 ? Dimension.Percent(r.Next(100)) : Dimension.Px(r.Next(200)) };
        if (r.Next(6) == 0) s = s with { MinWidth = Dimension.Px(r.Next(80)), MaxHeight = Dimension.Px(50 + r.Next(200)) };
        if (r.Next(5) == 0) s = s with { Margin = new(r.Next(3) == 0 ? LengthPercentageAuto.Auto : LengthPercentageAuto.Px(r.Next(8))) };
        if (r.Next(5) == 0) s = s with { Padding = new(r.Next(2) == 0 ? LengthPercentage.Percent(r.Next(5)) : LengthPercentage.Px(r.Next(8))) };
        if (r.Next(8) == 0) s = s with { AspectRatio = 0.5f + r.Next(3), IsReplaced = leaf && r.Next(2) == 0 };
        if (r.Next(6) == 0)
            s = s with { Position = r.Next(2) == 0 ? Position.Absolute : Position.Relative, Inset = new(LengthPercentageAuto.Px(r.Next(10))) };
        if (r.Next(10) == 0) s = s with { OverflowX = Overflow.Hidden, OverflowY = Overflow.Hidden };
        return s;
    }

    static LayoutNode Build(Random r, int depth, List<LayoutNode> all)
    {
        LayoutNode n;
        if (depth == 0 || r.Next(4) == 0)
            n = new LayoutNode(RandomStyle(r, true)) { Measure = new Txt(Texts[r.Next(Texts.Length)]) };
        else
        {
            n = new LayoutNode(RandomStyle(r, false));
            int k = r.Next(1, 6);
            for (int i = 0; i < k; i++) n.AddChild(Build(r, depth - 1, all));
        }
        all.Add(n);
        return n;
    }

    static LayoutNode Clone(LayoutNode n)
    {
        var c = new LayoutNode(n.Style);
        if (n.Measure is Txt t) c.Measure = new Txt(t.Content);
        for (int i = 0; i < n.ChildCount; i++) c.AddChild(Clone(n.GetChild(i)));
        return c;
    }

    static void PreOrder(LayoutNode n, List<LayoutNode> list)
    {
        list.Add(n);
        for (int i = 0; i < n.ChildCount; i++) PreOrder(n.GetChild(i), list);
    }

    static string Dump(LayoutNode n) => string.Format(CultureInfo.InvariantCulture,
        "{0:0.###},{1:0.###} {2:0.###}x{3:0.###} baseline {4:0.###} scroll {5:0.###}x{6:0.###} margin {7}",
        n.X, n.Y, n.Width, n.Height, n.Baseline, n.ScrollSize.Width, n.ScrollSize.Height, n.Margin);

    [Fact]
    public void Incremental_grid_relayout_matches_a_fresh_layout()
    {
        for (int seed = 0; seed < 300; seed++)
        {
            var r = new Random(seed);
            var all = new List<LayoutNode>();
            var root = Build(r, 3, all);
            root.Style = RandomStyle(r, false) with { Display = Display.Grid, Position = Position.Relative };
            var engine = new LayoutEngine();
            float vw = 200 + r.Next(500), vh = 200 + r.Next(500);
            engine.Compute(root, new Size(vw, vh));
            for (int step = 0; step < 5; step++)
            {
                int op = r.Next(4);
                if (op == 0)
                {
                    var n = all[r.Next(all.Count)];
                    if (n != root) n.Style = RandomStyle(r, n.Measure is not null);
                }
                else if (op == 1)
                {
                    var leaves = all.FindAll(x => x.Measure is not null);
                    if (leaves.Count > 0) leaves[r.Next(leaves.Count)].Measure = new Txt(Texts[r.Next(Texts.Length)]);
                }
                else if (op == 2)
                {
                    vw = 150 + r.Next(600);
                    vh = 150 + r.Next(600);
                }
                engine.Compute(root, new Size(vw, vh));
                var fresh = Clone(root);
                new LayoutEngine().Compute(fresh, new Size(vw, vh));
                var a = new List<LayoutNode>();
                var b = new List<LayoutNode>();
                PreOrder(root, a);
                PreOrder(fresh, b);
                for (int i = 0; i < a.Count; i++)
                {
                    var n = a[i];
                    foreach (var v in new[] { n.X, n.Y, n.Width, n.Height, n.ScrollSize.Width, n.ScrollSize.Height })
                        Assert.False(float.IsNaN(v) || float.IsInfinity(v), $"seed {seed} step {step} node {i}: non-finite {Dump(n)}");
                    string da = Dump(n), db = Dump(b[i]);
                    Assert.True(da == db, $"seed {seed} step {step} node {i}: incremental {da} / fresh {db}");
                }
            }
        }
    }

    [Fact]
    public void Nested_grids_stay_linear()
    {
        // 每层：auto / 1fr / min-content 列的网格，内含下一层与一个文本。固有尺寸查询的缓存键在各层相同，计算量应随深度线性增长
        foreach (var cols in new[]
                 {
                     GridTrackList.Of(GridTrackSize.Auto, GridTrackSize.Fr(1)),
                     GridTrackList.Of(GridTrackSize.MinContent, GridTrackSize.MaxContent),
                     GridTrackList.Of(GridTrackSize.FitContent(LengthPercentage.Px(50)), GridTrackSize.Auto),
                 })
        {
            long Runs(int depth)
            {
                LayoutNode cur = Text("aaa bbb ccc");
                for (int i = 0; i < depth; i++)
                    cur = N(Block with { Display = i % 2 == 0 ? Display.Grid : Display.InlineGrid, GridTemplateColumns = cols, Padding = new(1) },
                        cur, Text("xx yy"));
                var e = new LayoutEngine();
                e.Compute(N(Block with { Width = 600 }, cur), new Size(800, 800));
                return e.AlgorithmRuns;
            }
            long r10 = Runs(10), r40 = Runs(40);
            Assert.True(r40 < r10 * 8, $"{cols}: 10 levels {r10}, 40 levels {r40}");
        }
    }

    [Fact]
    public void Large_grid_lays_out_in_reasonable_time()
    {
        // 3000 个自动放置的项目、repeat(auto-fill, minmax(100px, 1fr))：一次完整布局（线程 CPU 时间）应远小于 2 s
        var items = Enumerable.Range(0, 3000).Select(i => Text(i % 3 == 0 ? "aaa bbb" : "cc")).ToArray();
        var cols = new GridTrackList(new[] { new GridTrackRepeat(GridRepeatKind.AutoFill, 1, new[] { GridTrackSize.MinMax(GridBreadth.Px(100), GridBreadth.Fr(1)) }) });
        var g = N(Block with { Display = Display.Grid, GridTemplateColumns = cols, ColumnGap = 8, RowGap = 8 }, items);
        double t0 = ThreadCpuClock.NowMs();
        Run(N(Block, g), 1000);
        double ms = ThreadCpuClock.NowMs() - t0;
        Assert.True(ms < 2000, ms + " ms");
        // 9 列：floor((1000 + 8) / (100 + 8)) = 9，第 10 个项目换行
        T.Eq(0, items[9].X);
        Assert.True(items[8].X > 800);
        T.Eq(items[9].Y, items[0].Y + items[0].Height + 8);
    }
}
