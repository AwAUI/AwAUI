using AwAUI.Css;
using AwAUI.Headless;
using AwAUI.Platform;
using AwAUI.UI;

namespace AwAUI.DevTools.Tests;

/// <summary>
/// 面板不再包装窗口事件：滚轮经 UI 的 wheel 事件到达虚拟化的树（阻止默认滚动），面板快捷键挂在面板根元素的 keydown 上。
/// </summary>
public partial class PanelScrollTests
{
    sealed partial class LongList : Component
    {
        protected override Node Build()
        {
            var rows = new NodeList();
            for (int i = 0; i < 400; i++) rows.Add(new Div { Key = i, Class = "row", Children = [new AwAUI.UI.Text { Children = ["Row " + i] }] });
            return new Div { Class = "list", Id = "list", Children = rows };
        }
    }

    [Fact]
    public void Panel_window_events_go_straight_to_the_host_and_wheel_and_keys_still_work()
    {
        var sheet = StyleSheet.Parse(".row { display: flex; height: 18px; } .badge { width: 20px; }", "big.css");
        using var h = new Harness(new LongList(), new[] { sheet }, appSize: new Size(800, 600));
        h.Open();
        Assert.Same(h.P.Host, h.PanelWindow.Events);
        var m = h.Model;
        m.Expand(m.Tree.Get(h.AppById("list").UniqueId)!);
        h.Settle();
        var p = h.PanelRows().OrderBy(r => r.BoundingRect.Y).ElementAt(3).BoundingRect.Center;

        // 分格滚轮（框架换算：向下一格 100 像素）
        h.PanelWindow.WheelNotches(p, 1);
        h.Settle();
        Assert.Equal(100, m.TreeScrollTop, 1);
        // 触控板（精确增量）同样交给模型
        h.PanelWindow.TrackpadScroll(p, -30, 0, ScrollPhase.Began);
        h.Settle();
        Assert.Equal(70, m.TreeScrollTop, 1);
        // 树视口不是用户滚动容器（overflow: hidden），滚动系统没有移动任何元素
        var tree = h.PanelById("dt-tree")!;
        Assert.Equal(new Point(0, 0), tree.ScrollController.Offset);

        // 面板根元素一打开就持有焦点：方向键选择下一行（而不是滚动）
        Assert.NotNull(h.P.Host!.Root.FocusManager.FocusedElement);
        var before = m.Selected?.Id ?? 0;
        Harness.Key(h.PanelWindow, PhysicalKey.ArrowDown);
        h.Settle();
        Assert.NotEqual(before, m.Selected?.Id ?? 0);
        // 点击树中的一行之后焦点仍在面板内，快捷键照常工作
        h.ClickPanel(h.PanelRows().OrderBy(r => r.BoundingRect.Y).ElementAt(2));
        var selected = m.Selected?.Id;
        Harness.Key(h.PanelWindow, PhysicalKey.ArrowDown);
        h.Settle();
        Assert.NotEqual(selected, m.Selected?.Id);
    }
}
