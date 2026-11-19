using System.Diagnostics;
using AwAUI.Css;
using AwAUI.Tests.Shared;
using AwAUI.UI;
using Xunit.Abstractions;
using UIText = AwAUI.UI.Text;

namespace AwAUI.DevTools.Tests;

/// <summary>
/// 大树上的增量更新：应用一帧里插入 / 移除几百个元素时，面板按 Tree 事件增量维护镜像，不重新 getDocument，
/// 并且每一批事件只摊平一次行列表（而不是每条事件一次，否则一万行的树上是「事件数 × 行数」的工作量）。
/// </summary>
public partial class IncrementalTreeTests
{
    readonly ITestOutputHelper _out;

    public IncrementalTreeTests(ITestOutputHelper output) => _out = output;

    sealed partial class GrowingList : Component
    {
        public readonly State<int> Count = new(1700);

        protected override Node Build()
        {
            var rows = new NodeList();
            for (int i = 0; i < Count.Value; i++)
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
    public void Hundreds_of_inserts_in_one_frame_are_applied_incrementally_with_one_flatten_per_batch()
    {
        var sheet = StyleSheet.Parse(".row { display: flex; height: 18px; } .badge { width: 20px; }", "big.css");
        var app = new GrowingList();
        using var h = new Harness(app, new[] { sheet }, appSize: new Size(800, 600));
        h.Open();
        var m = h.Model;
        var listNode = m.Tree.Get(h.AppById("list").UniqueId)!;
        m.Expand(listNode);
        h.Settle();
        Assert.Equal(1700, listNode.Children!.Count);

        int requests = m.RequestCount, flattens = m.Tree.FlattenCount, log = (int)(m.Log.LastOrDefault()?.Seq ?? 0);
        var sw = Stopwatch.StartNew();
        double cpu0 = ThreadCpuClock.NowMs();
        app.Count.Value = 2100;
        h.Settle();
        double ms = sw.Elapsed.TotalMilliseconds, cpu = ThreadCpuClock.NowMs() - cpu0;
        int events = (int)(m.Log.LastOrDefault()?.Seq ?? 0) - log;
        int flattenDelta = m.Tree.FlattenCount - flattens;
        _out.WriteLine($"insert 400 rows: {events} protocol events, {flattenDelta} flattens, {m.RequestCount - requests} requests, {ms:F1} ms wall, {cpu:F1} ms thread CPU");

        // 镜像增量跟上了应用：新行都在，且与应用的顺序一致
        Assert.Same(listNode, m.Tree.Get(listNode.Id));
        Assert.Equal(2100, listNode.Children!.Count);
        var appRows = h.AppById("list").Children;
        for (int i = 0; i < appRows.Count; i++) Assert.Equal(appRows[i].UniqueId, listNode.Children[i].Id);
        Assert.True(events >= 400, "expected one childrenChanged per insert; events: " + events);
        // 没有整树刷新（getDocument）；只有样式刷新之类的少量请求
        Assert.True(m.RequestCount - requests < 10, "requests: " + (m.RequestCount - requests));
        // 一批事件最多摊平几次（每帧 / 每次重建一次），与事件数无关
        Assert.True(flattenDelta <= 12, $"flattened {flattenDelta} times for {events} events");

        // 移除同样是增量的；滚到底部时移除，滚动位置在下一次重建时夹紧，视口里仍有行
        m.ScrollTreeTo(float.MaxValue);
        h.Settle();
        flattens = m.Tree.FlattenCount;
        app.Count.Value = 1000;
        h.Settle();
        Assert.Equal(1000, listNode.Children!.Count);
        Assert.Equal(m.MaxTreeScroll, m.TreeScrollTop);
        Assert.InRange(h.PanelRows().Count, 10, 40);
        Assert.True(m.Tree.FlattenCount - flattens <= 12, $"flattened {m.Tree.FlattenCount - flattens} times on removal");
        Assert.Equal(m.Tree.Count, CountMirror(m.Tree.Root!));
    }

    [Fact]
    public void Wheel_scrolling_under_a_hovering_pointer_moves_the_hover_highlight_to_the_row_now_under_it()
    {
        var sheet = StyleSheet.Parse(".row { display: flex; height: 18px; } .badge { width: 20px; }", "big.css");
        using var h = new Harness(new GrowingList(), new[] { sheet }, appSize: new Size(800, 600));
        h.Open();
        var m = h.Model;
        m.Expand(m.Tree.Get(h.AppById("list").UniqueId)!);
        h.Settle();
        var rows = h.PanelRows().OrderBy(r => r.BoundingRect.Y).ToList();
        var p = rows[5].BoundingRect.Center;
        h.MovePanel(p);
        Assert.Equal(long.Parse(rows[5].GetAttribute("data-node")!), m.HoveredId);

        h.WheelPanel(p, -120);
        Assert.True(m.TreeScrollTop > 0);
        // 指针没动，但它下面现在是另一行：悬停与应用里的高亮跟着换（同浏览器）
        var under = h.PanelRows().Single(r => r.BoundingRect.Y <= p.Y && p.Y < r.BoundingRect.Bottom);
        long underId = long.Parse(under.GetAttribute("data-node")!);
        Assert.NotEqual(long.Parse(rows[5].GetAttribute("data-node")!), underId);
        Assert.Equal(underId, m.HoveredId);
        Assert.Equal(underId, h.App.DevTools!.HighlightedElement!.UniqueId);
    }

    static int CountMirror(DomNode root)
    {
        int n = 0;
        var stack = new Stack<DomNode>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var x = stack.Pop();
            n++;
            if (x.Children is { } c) foreach (var ch in c) stack.Push(ch);
        }
        return n;
    }
}
