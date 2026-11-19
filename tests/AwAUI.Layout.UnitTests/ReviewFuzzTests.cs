using System.Globalization;
using static AwAUI.Layout.UnitTests.T;

namespace AwAUI.Layout.UnitTests;

/// <summary>
/// 评审时加入的随机化测试：增量布局必须与全新布局逐节点一致；嵌套的各种样式组合不能让计算量超线性增长。
/// 随机数用固定种子，结果可复现。
/// </summary>
public class ReviewFuzzTests
{
    static LayoutStyle RandomStyle(Random r)
    {
        var s = Block;
        int d = r.Next(7);
        s = s with
        {
            Display = d switch
            {
                0 => Display.Block,
                2 => Display.InlineFlex,
                4 => r.Next(3) == 0 ? Display.None : Display.Block,
                5 => Display.Contents,
                _ => Display.Flex,
            },
            FlexDirection = (FlexDirection)r.Next(4),
            FlexWrap = (FlexWrap)r.Next(3),
            Direction = (Direction)r.Next(2),
        };
        if (r.Next(3) == 0) s = s with { AlignItems = (AlignItems)r.Next(8) };
        if (r.Next(3) == 0) s = s with { AlignSelf = (AlignSelf)r.Next(9) };
        if (r.Next(3) == 0) s = s with { AlignContent = (AlignContent)r.Next(10) };
        if (r.Next(3) == 0) s = s with { JustifyContent = (JustifyContent)r.Next(10) };
        if (r.Next(2) == 0) s = s with { FlexGrow = r.Next(3) };
        if (r.Next(4) == 0) s = s with { FlexShrink = r.Next(3) };
        if (r.Next(3) == 0)
            s = s with { FlexBasis = r.Next(3) switch { 0 => Dimension.Percent(r.Next(100)), 1 => Dimension.Px(r.Next(200)), _ => Dimension.Content } };
        if (r.Next(3) == 0)
            s = s with { Padding = new(r.Next(2) == 0 ? LengthPercentage.Percent(r.Next(5)) : LengthPercentage.Px(r.Next(10))) };
        if (r.Next(3) == 0)
            s = s with
            {
                Margin = new(r.Next(3) switch
                {
                    0 => LengthPercentageAuto.Percent(r.Next(5)),
                    1 => LengthPercentageAuto.Px(r.Next(10)),
                    _ => LengthPercentageAuto.Auto,
                }),
            };
        if (r.Next(3) == 0) s = s with { Border = new((float)r.Next(4)) };
        if (r.Next(4) == 0)
            s = s with
            {
                Width = r.Next(5) switch
                {
                    0 => Dimension.MinContent,
                    1 => Dimension.FitContent,
                    2 => Dimension.Percent(r.Next(100)),
                    3 => Dimension.Px(r.Next(300)),
                    _ => Dimension.MaxContent,
                },
            };
        if (r.Next(4) == 0) s = s with { Height = r.Next(2) == 0 ? Dimension.Percent(r.Next(100)) : Dimension.Px(r.Next(300)) };
        if (r.Next(5) == 0) s = s with { MinWidth = r.Next(2) == 0 ? Dimension.MinContent : Dimension.Px(r.Next(100)) };
        if (r.Next(5) == 0) s = s with { MaxWidth = Dimension.Percent(r.Next(100)) };
        if (r.Next(5) == 0) s = s with { MinHeight = Dimension.Px(r.Next(100)), MaxHeight = Dimension.Px(r.Next(300)) };
        if (r.Next(6) == 0) s = s with { AspectRatio = 0.5f + r.Next(3) };
        if (r.Next(4) == 0) s = s with { Position = (Position)r.Next(4) };
        if (r.Next(3) == 0)
            s = s with
            {
                Inset = new(LengthPercentageAuto.Px(r.Next(20)), LengthPercentageAuto.Auto,
                    r.Next(2) == 0 ? LengthPercentageAuto.Auto : LengthPercentageAuto.Percent(10), LengthPercentageAuto.Px(r.Next(20))),
            };
        if (r.Next(4) == 0) s = s with { ColumnGap = LengthPercentage.Percent(2), RowGap = r.Next(10) };
        if (r.Next(6) == 0) s = s with { OverflowX = Overflow.Hidden };
        if (r.Next(4) == 0) s = s with { BoxSizing = BoxSizing.BorderBox };
        return s;
    }

    static readonly string[] Texts = { "a", "aaa bbb", "xx yyyy zzzzz w", "", "lorem ipsum dolor sit amet consectetur" };

    /// <summary>记住文本内容的假文本，便于克隆整棵树。</summary>
    sealed class Txt : LayoutMeasure
    {
        readonly FakeText _inner;
        public readonly string Content;
        public Txt(string s) { Content = s; _inner = new FakeText(s); }
        public override Size Measure(LayoutNode node, in MeasureConstraints c) => _inner.Measure(node, c);
        public override float GetBaseline(LayoutNode node, Size contentSize) => _inner.GetBaseline(node, contentSize);
    }

    static LayoutNode Build(Random r, int depth, List<LayoutNode> all)
    {
        LayoutNode n;
        if (depth == 0 || r.Next(4) == 0)
        {
            n = new LayoutNode(RandomStyle(r) with { Display = Display.Block }) { Measure = new Txt(Texts[r.Next(Texts.Length)]) };
        }
        else
        {
            n = new LayoutNode(RandomStyle(r));
            int k = r.Next(1, 4);
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
    public void Incremental_relayout_matches_a_fresh_layout()
    {
        // 随机树（含 none/contents/定位/百分比/固有尺寸关键字/宽高比），随机改样式、换文本、换视口后增量布局，
        // 与克隆出的新树在新引擎上的布局逐节点比较：缓存键规范化或脏标记有遗漏时这里会出现差异
        for (int seed = 0; seed < 400; seed++)
        {
            var r = new Random(seed);
            var all = new List<LayoutNode>();
            var root = Build(r, 4, all);
            root.Style = root.Style with { Display = Display.Flex, Position = Position.Static };
            var engine = new LayoutEngine();
            float vw = 300 + r.Next(400), vh = 300 + r.Next(400);
            engine.Compute(root, new Size(vw, vh));
            for (int step = 0; step < 6; step++)
            {
                int op = r.Next(4);
                if (op == 0)
                {
                    var n = all[r.Next(all.Count)];
                    if (n == root) continue;
                    var st = RandomStyle(r);
                    n.Style = n.Measure is null ? st : st with { Display = Display.Block };
                }
                else if (op == 1)
                {
                    var leaves = all.FindAll(x => x.Measure is not null);
                    if (leaves.Count == 0) continue;
                    leaves[r.Next(leaves.Count)].Measure = new Txt(Texts[r.Next(Texts.Length)]);
                }
                else if (op == 2)
                {
                    vw = 200 + r.Next(500);
                    vh = 200 + r.Next(500);
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

    static long RunChain(int seed, int depth)
    {
        // 同一组 40 个随机样式，从叶子往上取 depth 层；每层另有一个文本兄弟
        var r = new Random(seed);
        var styles = new LayoutStyle[40];
        for (int i = 0; i < styles.Length; i++)
        {
            var st = RandomStyle(r);
            styles[i] = st.Display is Display.None ? st with { Display = Display.Flex } : st;
        }
        LayoutNode cur = Text("aaa bbb ccc ddd");
        for (int i = 0; i < depth; i++)
        {
            var p = new LayoutNode(styles[i]);
            p.AddChild(cur);
            p.AddChild(Text("xx yyyy z"));
            cur = p;
        }
        var root = N(Block with { Width = 600, Height = 700 }, cur);
        var e = new LayoutEngine();
        e.Compute(root, new Size(800, 800));
        return e.AlgorithmRuns;
    }

    [Fact]
    public void Nested_random_styles_stay_linear()
    {
        // 评审前，某些组合（如 flex-grow + 百分比外边距的嵌套 row）每层约 ×1.85，30 层即上亿次。
        // 线性时 40 层（81 个节点）只需几百次算法运行
        for (int seed = 0; seed < 150; seed++)
        {
            long runs = RunChain(seed, 40);
            Assert.True(runs < 2000, $"seed {seed}: {runs} algorithm runs for 40 levels");
        }
    }

    [Fact]
    public void Nested_growing_rows_with_percentage_margins_stay_linear()
    {
        // 最初发现指数增长的具体组合：每层 row + flex-grow:1 + padding 1px + margin 1%。
        // 内容尺寸上下文中的「假想交叉尺寸」布局为每层派生出新的已知宽度/高度，缓存键随深度增长，6 槽缓存被挤爆。
        // 评审前 8 层已需 301 次、30 层实际无法完成；现在只求宽度的固有尺寸查询不再向下布局
        const int Depth = 30;
        var leaf = Text("aaa bbb ccc", out var m);
        LayoutNode cur = leaf;
        for (int i = 0; i < Depth; i++)
            cur = N(Row with { FlexGrow = 1, Padding = new(1), Margin = new(LengthPercentageAuto.Percent(1)) }, cur);
        var root = N(Block with { Width = 600 }, cur);
        var e = new LayoutEngine();
        e.Compute(root, new Size(800, 800));
        Assert.True(e.AlgorithmRuns < Depth * 10, "algorithm runs: " + e.AlgorithmRuns);
        Assert.True(m.Calls < 10, "measure calls: " + m.Calls);
    }
}
