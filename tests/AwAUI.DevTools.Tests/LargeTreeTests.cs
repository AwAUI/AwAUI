using System.Diagnostics;
using AwAUI.Css;
using AwAUI.Tests.Shared;
using AwAUI.UI;
using Xunit.Abstractions;
using UIText = AwAUI.UI.Text;

namespace AwAUI.DevTools.Tests;

/// <summary>
/// 一万多个元素的应用：面板的树视图是虚拟化的，面板自己的元素数与应用树的大小无关；滚动、选中、展开全部只做与可见行成比例的工作。
/// 断言只用确定性的量（元素数、行数、请求数）；耗时写进测试输出，供报告引用（负载很高的机器上只能当上界）。
/// </summary>
public partial class LargeTreeTests
{
    readonly ITestOutputHelper _out;

    public LargeTreeTests(ITestOutputHelper output) => _out = output;

    /// <summary>1700 行 × 6 个元素（行、名字文本及其文本节点、徽章及其文本与文本节点）≈ 10 200 个元素。</summary>
    sealed partial class BigList : Component
    {
        protected override Node Build()
        {
            var rows = new NodeList();
            for (int i = 0; i < 1700; i++)
                rows.Add(new Div
                {
                    Key = i,
                    Class = "row",
                    Children =
                    [
                        new UIText { Class = "name", Children = ["Row " + i] },
                        new Div { Class = "badge", Children = [new UIText { Children = [(i % 7).ToString()] }] },
                    ],
                });
            return new Div { Class = "list", Id = "list", Children = rows };
        }
    }

    [Fact]
    public void Ten_thousand_elements_stay_virtualized()
    {
        var sheet = StyleSheet.Parse(".row { display: flex; height: 18px; } .badge { width: 20px; }", "big.css");
        var cpu = Stopwatch.StartNew();
        using var h = new Harness(new BigList(), new[] { sheet }, appSize: new Size(800, 600));
        int appElements = Harness.Descendants(h.App.Root.RootElement).Count();
        Assert.True(appElements >= 10_000, "app elements: " + appElements);

        var sw = Stopwatch.StartNew();
        double cpu0 = ThreadCpuClock.NowMs();
        h.Open();
        double openMs = sw.Elapsed.TotalMilliseconds, openCpu = ThreadCpuClock.NowMs() - cpu0;
        var m = h.Model;
        int panelElements = Harness.Descendants(h.PanelRoot).Count();
        _out.WriteLine($"app elements {appElements}; mirror after getDocument(depth -1): {m.Tree.Count} nodes; open + load + first frames {openMs:F1} ms wall, {openCpu:F1} ms thread CPU; panel elements {panelElements}");

        // 展开全部：只有第一次展开未加载的节点时才发请求（每个节点一次 requestChildren）
        var listNode = m.Tree.Get(h.AppById("list").UniqueId)!;
        m.Expand(listNode);
        h.Settle();
        int requestsBefore = m.RequestCount;
        sw.Restart();
        cpu0 = ThreadCpuClock.NowMs();
        foreach (var row in listNode.Children!.ToList())
        {
            m.Expand(row);
            foreach (var c in row.Children ?? new List<DomNode>()) m.Expand(c);
        }
        h.Settle();
        foreach (var row in listNode.Children!.ToList())
            foreach (var c in row.Children ?? new List<DomNode>()) m.Expand(c);
        h.Settle();
        double expandMs = sw.Elapsed.TotalMilliseconds, expandCpu = ThreadCpuClock.NowMs() - cpu0;
        int rows = m.Tree.Rows.Count;
        Assert.True(rows >= 1700 * 3, "rows: " + rows);
        _out.WriteLine($"expanded everything: {rows} rows, {m.Tree.Count} nodes, {m.RequestCount - requestsBefore} requests, {expandMs:F1} ms wall, {expandCpu:F1} ms thread CPU");

        // 面板只为可见行建元素
        int visibleRows = h.PanelRows().Count;
        int expectedRows = (int)Math.Ceiling(m.TreeViewportHeight / InspectorModel.RowHeight) + 1;
        Assert.InRange(visibleRows, 1, expectedRows);
        panelElements = Harness.Descendants(h.PanelRoot).Count();
        Assert.True(panelElements < 1500, "panel elements: " + panelElements);

        // 滚轮：每次滚动只重建可见行（键 = 节点 id，复用存活元素）
        var tree = h.PanelById("dt-tree")!;
        var p = tree.BoundingRect.Center;
        var frameTimes = new List<double>();
        var raster = new List<double>();
        var pipeline = new List<double>();
        var builds = new List<double>();
        var flatten = new List<double>();
        for (int i = 0; i < 60; i++)
        {
            h.WheelPanel(p, -120);
            // 滚动后的第一帧（Settle 的后几帧没有变化，不计入）
            var f = h.WheelFrame;
            frameTimes.Add(f.TotalMs);
            raster.Add(f.RasterMs);
            builds.Add(f.BuildMs);
            pipeline.Add(f.Pipeline.StyleMs + f.Pipeline.LayoutMs + f.Pipeline.PaintMs);
        }
        Assert.True(m.TreeScrollTop > 60 * 100, "scroll: " + m.TreeScrollTop);
        Assert.InRange(h.PanelRows().Count, 1, expectedRows);
        var firstRow = h.PanelRows().OrderBy(r => r.BoundingRect.Y).First();
        Assert.Equal(m.Tree.Rows[(int)(m.TreeScrollTop / InspectorModel.RowHeight)].Node.Id.ToString(), firstRow.GetAttribute("data-node"));

        // 摊平行列表（结构变化后）：折叠再展开一行，测重新摊平的时间
        for (int i = 0; i < 10; i++)
        {
            var t = Stopwatch.StartNew();
            m.Tree.ExpansionChanged();
            _ = m.Tree.Rows;
            flatten.Add(t.Elapsed.TotalMilliseconds);
        }

        // 选中滚动后可见的一行：样式请求与高亮
        sw.Restart();
        h.ClickPanel(firstRow);
        double selectMs = sw.Elapsed.TotalMilliseconds;
        Assert.Equal(long.Parse(firstRow.GetAttribute("data-node")!), h.P.SelectedNodeId);

        // 跳到底部（End 键没有绑定：拖动到最后）
        m.ScrollTreeTo(float.MaxValue);
        h.Settle();
        Assert.Equal(m.MaxTreeScroll, m.TreeScrollTop);
        Assert.InRange(h.PanelRows().Count, 1, expectedRows);

        frameTimes.Sort();
        flatten.Sort();
        raster.Sort();
        pipeline.Sort();
        builds.Sort();
        _out.WriteLine($"wheel scroll panel frame: median {frameTimes[frameTimes.Count / 2]:F2} ms, max {frameTimes[frameTimes.Count - 1]:F2} ms (60 wheel steps; " +
                       $"build {builds[builds.Count / 2]:F2}, style+layout+paint {pipeline[pipeline.Count / 2]:F2}, raster {raster[raster.Count / 2]:F2} ms median); " +
                       $"re-flatten {rows} rows: median {flatten[flatten.Count / 2]:F2} ms; select row (8 settle frames incl. styles round trip): {selectMs:F1} ms; " +
                       $"visible rows {visibleRows}, panel elements {panelElements}; total test {cpu.Elapsed.TotalSeconds:F1} s");
    }
}
