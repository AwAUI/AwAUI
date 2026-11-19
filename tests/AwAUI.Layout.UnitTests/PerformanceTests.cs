using System.Diagnostics;
using System.Globalization;
using Xunit.Abstractions;
using static AwAUI.Layout.UnitTests.T;

namespace AwAUI.Layout.UnitTests;

public class PerformanceTests
{
    readonly ITestOutputHelper _output;

    public PerformanceTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public void Lays_out_ten_thousand_nodes()
    {
        // 根 column → 50 个分区（column：标题 + 4 行）→ 每行 row 容器（交替 nowrap / wrap）含 49 个文本叶子：
        // 偶数位 flex: 1 1 0，奇数位按内容。节点总数 1 + 50 × (1 + 1 + 4 × 50) = 10101
        var root = N(Column with { Padding = new(8), RowGap = 4 });
        var leaves = new List<(LayoutNode node, FakeText text)>();
        int nodes = 1;
        for (int sct = 0; sct < 50; sct++)
        {
            var section = N(Column with { Padding = new(4), Border = new(1f), RowGap = 2 });
            var header = Text("section header " + sct, out var hm, Block with { Margin = Margin(0, 0, 4, 0) });
            section.AddChild(header);
            leaves.Add((header, hm));
            nodes += 2;
            for (int r = 0; r < 4; r++)
            {
                var row = N(Row with { ColumnGap = 2, Padding = new(2), FlexWrap = r % 2 == 0 ? FlexWrap.NoWrap : FlexWrap.Wrap, AlignItems = AlignItems.Center });
                nodes++;
                for (int i = 0; i < 49; i++)
                {
                    var style = i % 2 == 0 ? Block with { FlexGrow = 1, FlexBasis = 0, MinWidth = 0 } : Block with { Padding = new(1, 3) };
                    var leaf = Text(i % 3 == 0 ? "lorem ipsum" : "do", out var m, style);
                    row.AddChild(leaf);
                    leaves.Add((leaf, m));
                    nodes++;
                }
                section.AddChild(row);
            }
            root.AddChild(section);
        }
        Assert.True(nodes >= 10000);

        var engine = new LayoutEngine();
        // 预热：交替视口宽度（每次都是完整重排），持续约 0.5 秒，让分层编译把热点方法提升到优化代码
        var warm = Stopwatch.StartNew();
        for (int i = 0; warm.ElapsedMilliseconds < 500; i++) engine.Compute(root, new Size(i % 2 == 0 ? 1600 : 1500, 900));

        // 完整重排：每次改变视口宽度，所有行都要重新求解。机器上可能有其他负载，报告最小值与中位数
        long runsBefore = engine.AlgorithmRuns;
        long allocBefore = GC.GetAllocatedBytesForCurrentThread();
        const int Rounds = 15;
        var times = new double[Rounds];
        var sw = new Stopwatch();
        for (int i = 0; i < Rounds; i++)
        {
            sw.Restart();
            engine.Compute(root, new Size(i % 2 == 0 ? 1400 : 1500, 900));
            times[i] = sw.Elapsed.TotalMilliseconds;
        }
        Array.Sort(times);
        double coldMin = times[0], coldMedian = times[Rounds / 2];
        long runs = (engine.AlgorithmRuns - runsBefore) / Rounds;
        long allocated = (GC.GetAllocatedBytesForCurrentThread() - allocBefore) / Rounds;

        // 与最后一轮相同的视口：根命中缓存，O(1)
        var lastViewport = new Size((Rounds - 1) % 2 == 0 ? 1400 : 1500, 900);
        long callsBefore = SumCalls(leaves);
        sw.Restart();
        engine.Compute(root, lastViewport);
        double clean = sw.Elapsed.TotalMilliseconds;
        Assert.True(allocated < 1024, "full relayout allocated " + allocated + " bytes");
        Assert.Equal(callsBefore, SumCalls(leaves));

        // 改变一个叶子：只有它和同一行里尺寸因此改变的 flex:1 兄弟被重新测量（约 25 个），其余子树全部命中缓存
        var target = leaves[5000];
        target.node.Measure = new FakeText("changed text here");
        sw.Restart();
        engine.Compute(root, lastViewport);
        double incremental = sw.Elapsed.TotalMilliseconds;
        long extra = SumCalls(leaves) - callsBefore;
        Assert.True(extra < 50, "re-measured " + extra);

        _output.WriteLine(string.Format(CultureInfo.InvariantCulture,
            "{0} nodes: full relayout (new viewport) min {1:F2} ms / median {7:F2} ms ({5} algorithm runs), clean relayout {2:F3} ms, one dirty leaf {3:F3} ms ({4} sibling re-measures), {6} bytes allocated per full relayout",
            nodes, coldMin, clean, incremental, extra, runs, allocated, coldMedian));
        Assert.True(root.Height > 0);
    }

    static long SumCalls(List<(LayoutNode node, FakeText text)> leaves)
    {
        long s = 0;
        foreach (var l in leaves) s += l.text.Calls;
        return s;
    }
}
