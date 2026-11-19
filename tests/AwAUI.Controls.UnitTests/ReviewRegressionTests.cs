using System.Collections.ObjectModel;
using System.ComponentModel;
using AwAUI.Controls.Primitives;
using AwAUI.Controls.UnitTests.Selection;

namespace AwAUI.Controls.UnitTests;

/// <summary>评审中发现的缺陷的回归测试（每个测试在修复前失败）。</summary>
public class ReviewRegressionTests
{
    // ------------------------------------------------------------ SelectionModel

    [Fact]
    public void SelectedIndexes_view_is_a_consistent_snapshot_after_later_changes()
    {
        var m = new SelectionModel<int>(Enumerable.Range(0, 10).ToList()) { SingleSelect = false };
        m.SelectRange(0, 2);
        var view = m.SelectedIndexes;
        var items = m.SelectedItems;
        m.Clear();
        // 修复前：视图的计数是旧的，区间却是活的 → 越界异常
        Assert.Equal(new[] { 0, 1, 2 }, view.ToArray());
        Assert.Equal(2, view[2]);
        Assert.Equal(new[] { 0, 1, 2 }, items.ToArray());
        Assert.Empty(m.SelectedIndexes);
    }

    [Fact]
    public void Deselecting_while_enumerating_SelectedIndexes_does_not_throw()
    {
        var m = new SelectionModel<int>(Enumerable.Range(0, 10).ToList()) { SingleSelect = false };
        m.SelectRange(2, 6);
        foreach (var i in m.SelectedIndexes)
            if (i % 2 == 0) m.Deselect(i);
        Assert.Equal(new[] { 3, 5 }, m.SelectedIndexes);
    }

    [Fact]
    public void Reentrant_change_from_handler_still_raises_outer_property_changes()
    {
        var m = new SelectionModel<int>(Enumerable.Range(0, 10).ToList()) { SingleSelect = false };
        var props = new List<string?>();
        m.PropertyChanged += (_, e) => props.Add(e.PropertyName);
        m.SelectionChanged += (_, e) =>
        {
            if (e.SelectedIndexes.Contains(3)) m.Select(4);
        };
        m.Select(3);
        // 修复前：外层的快照被内层覆盖，SelectedIndex / SelectedItem / AnchorIndex 的变化通知丢失
        Assert.Contains(nameof(SelectionModel<int>.SelectedIndex), props);
        Assert.Contains(nameof(SelectionModel<int>.SelectedItem), props);
        Assert.Equal(3, m.SelectedIndex);
    }

    [Fact]
    public void Replacing_source_reports_old_items_as_deselected()
    {
        var a = new ObservableCollection<string>(new[] { "a0", "a1", "a2" });
        var b = new ObservableCollection<string>(new[] { "b0", "b1", "b2" });
        var m = new SelectionModel<string>(a) { SingleSelect = false };
        m.SelectRange(1, 2);
        SelectionModelSelectionChangedEventArgs<string>? args = null;
        m.SelectionChanged += (_, e) => args = e;
        m.Source = b;
        Assert.NotNull(args);
        Assert.Equal(new[] { 1, 2 }, args!.DeselectedIndexes);
        // 修复前：按新数据源取项，报告成 b1、b2
        Assert.Equal(new[] { "a1", "a2" }, args.DeselectedItems);
    }

    [Fact]
    public void Replacing_source_in_single_mode_reports_old_item_when_not_kept()
    {
        var a = new ObservableCollection<string>(new[] { "x", "y" });
        var b = new ObservableCollection<string>(new[] { "p", "q" });
        var m = new SelectionModel<string>(a) { SelectedIndex = 1 };
        SelectionModelSelectionChangedEventArgs<string>? args = null;
        m.SelectionChanged += (_, e) => args = e;
        m.Source = b;
        Assert.Equal(-1, m.SelectedIndex);
        Assert.Equal(new[] { "y" }, args!.DeselectedItems);
    }

    [Fact]
    public void Collection_change_inside_batch_reports_pre_change_items()
    {
        var src = new ObservableCollection<string>(new[] { "a", "b", "c", "d" });
        var m = new SelectionModel<string>(src) { SingleSelect = false };
        var reported = new List<string>();
        m.SelectionChanged += (_, e) => reported.AddRange(e.SelectedItems);
        using (m.BatchUpdate())
        {
            m.SelectRange(2, 3);   // c, d
            src.Insert(0, "x");    // 通知到达时数据源已变；累积的变化按旧编号 2..3 报告
        }
        // 修复前：按新数据源取旧编号 → 报告成 b、c
        Assert.Equal(new[] { "c", "d" }, reported);
        Assert.Equal(new[] { "c", "d" }, m.SelectedItems);
    }

    [Fact]
    public void Reset_inside_batch_uses_the_item_selected_before_the_reset()
    {
        var src = new ResettableCollection<string>(new[] { "a", "b", "c" });
        var m = new SelectionModel<string>(src);
        using (m.BatchUpdate())
        {
            m.Select(0);                         // a
            src.ResetTo(new[] { "c", "b" });     // a 已不在：应清空
        }
        // 修复前：刷新缓存时按新数据源取到 c，于是「保留」了 c
        Assert.Equal(-1, m.SelectedIndex);

        m.Select(1);   // b
        using (m.BatchUpdate())
        {
            m.Select(0);                         // c
            src.ResetTo(new[] { "b", "z", "c" });
        }
        Assert.Equal(2, m.SelectedIndex);
        Assert.Equal("c", m.SelectedItem);
    }

    // ------------------------------------------------------------ Virtualizer

    [Fact]
    public void Zero_size_rows_at_the_start_are_rendered()
    {
        // 估计尺寸为 0（或项被测得为 0）时，开头的项必须进入渲染范围，否则永远测不到它们
        var v = new Virtualizer(100, new VirtualizerOptions { EstimatedItemSize = 0, Overscan = 0 }) { ViewportSize = 500 };
        var r = v.GetRange();
        Assert.Equal(0, r.StartIndex);
        Assert.Equal(0, v.GetVirtualItems()[0].Index);
    }

    [Fact]
    public void Zero_size_rows_inside_the_list_are_not_skipped()
    {
        var v = new Virtualizer(10, new VirtualizerOptions { EstimatedItemSize = 20, Overscan = 0 }) { ViewportSize = 40 };
        for (int i = 3; i < 6; i++) v.Measure(i, 0);   // 3..5 折叠，都位于 60
        v.ScrollOffset = 60;
        var r = v.GetRange();
        Assert.Equal(3, r.StartIndex);   // 修复前为 6：零尺寸的 3..5 被跳过
        Assert.Equal(7, r.EndIndex);

        // 定高为 0：全部都在 0 处
        var f = new Virtualizer(5, new VirtualizerOptions { ItemSize = 0, Overscan = 0 }) { ViewportSize = 100 };
        Assert.Equal(0, f.GetRange().StartIndex);
        Assert.Equal(4, f.GetRange().EndIndex);
    }

    [Fact]
    public void Start_index_at_exact_row_boundary_is_unchanged()
    {
        var v = new Virtualizer(10, new VirtualizerOptions { EstimatedItemSize = 20, Gap = 5, Overscan = 0 }) { ViewportSize = 50 };
        v.ScrollOffset = 50;   // 第 2 行从 50 开始
        Assert.Equal(2, v.GetRange().StartIndex);
        v.ScrollOffset = 47;   // 落在第 1 行之后的间距里
        Assert.InRange(v.GetRange().StartIndex, 1, 2);
        v.ScrollOffset = 44;   // 第 1 行内部（25..45）
        Assert.Equal(1, v.GetRange().StartIndex);
    }

    [Fact]
    public void Render_cap_keeps_the_visible_items()
    {
        // 预渲染很大、超过 MaxRenderedItems 时，截取必须保留与视口相交的项
        // 修复前：从预渲染范围的开头截取 1000 项（全在视口之上），可见项一个都不渲染
        var v = new Virtualizer(100_000, new VirtualizerOptions { ItemSize = 10, Overscan = 50_000, MaxRenderedItems = 1000 }) { ViewportSize = 100 };
        v.ScrollOffset = 500_000;
        var r = v.GetRange();
        var items = v.GetVirtualItems();
        Assert.True(items.Count <= 1000);
        for (int i = r.StartIndex; i <= r.EndIndex; i++) Assert.Contains(items, it => it.Index == i);
        // 剩余名额前后平分，索引连续升序
        Assert.Equal(Enumerable.Range(items[0].Index, items.Count), items.Select(it => it.Index));
        Assert.InRange(r.StartIndex - items[0].Index, 400, 500);

        // 视口本身就超过上限：从第一个可见项开始
        var w = new Virtualizer(100_000, new VirtualizerOptions { ItemSize = 1, Overscan = 5, MaxRenderedItems = 100 }) { ViewportSize = 5000, ScrollOffset = 2000 };
        Assert.Equal(w.GetRange().StartIndex, w.GetVirtualItems()[0].Index);
        Assert.Equal(100, w.GetVirtualItems().Count);
    }

    // ------------------------------------------------------------ ItemCollection / TypeAhead

    [Fact]
    public void Value_type_keys_can_search_without_a_current_item()
    {
        var items = new ItemCollection<int>();
        items.Register(0, "Apple");
        items.Register(1, "Banana");
        items.Register(2, "Avocado");
        // default(int) 是合法的键 0：传它表示「当前项是 Apple」，于是单字符查找跳到 Avocado
        Assert.Equal(2, items.FindNextMatch("a", 0)!.Key);
        // 没有当前项：从第一项开始
        Assert.Equal(0, items.FindNextMatch("a")!.Key);
        var ta = new TypeAhead();
        Assert.Equal(0, ta.HandleKey("a", TimeSpan.Zero, items)!.Key);
    }

    // ------------------------------------------------------------ Presence

    [Fact]
    public void Huge_durations_do_not_overflow_the_deadline()
    {
        var p = new Presence(false);
        p.SetPresent(true, new PresenceStyle { AnimationName = "in" }, TimeSpan.FromSeconds(1));
        // 修复前：now + 时长 + 宽限 溢出 TimeSpan，抛 OverflowException
        p.SetPresent(false, new PresenceStyle { AnimationName = "out", AnimationDuration = TimeSpan.MaxValue }, TimeSpan.FromSeconds(2));
        Assert.Equal(PresenceState.UnmountSuspended, p.State);
        Assert.Equal(TimeSpan.MaxValue, p.Deadline);
        p.OnAnimationEnd("out");
        Assert.Equal(PresenceState.Unmounted, p.State);
    }
}

public class FixedVirtualizerRangeTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void Fixed_range_matches_naive_model(int seed)
    {
        var rng = new Random(seed);
        for (int iter = 0; iter < 400; iter++)
        {
            int count = rng.Next(1, 60);
            double size = rng.Next(4) == 0 ? 0 : rng.Next(1, 40) + (rng.Next(2) == 0 ? 0.3 : 0);
            double gap = rng.Next(3) == 0 ? 0 : rng.Next(0, 6) + 0.1;
            var v = new Virtualizer(count, new VirtualizerOptions { ItemSize = size, Gap = gap, PaddingStart = 2, Overscan = 0 });
            double extent = size + gap;
            double offset = rng.NextDouble() * (count * extent + 20) - 5;
            // 每隔几次让偏移恰好落在行边界上
            if (rng.Next(3) == 0) offset = 2 + rng.Next(count) * extent;
            double viewport = rng.Next(0, 200);
            v.ScrollOffset = offset;
            v.ViewportSize = viewport;
            // 在列表坐标（减去开头内边距）中逐行比较，避免 2 + r·e 与 offset − 2 的舍入差异
            double t = offset - 2;
            int expectedStart = count - 1;
            for (int r = 0; r < count; r++)
            {
                double start = r * extent;
                if (start + size > t || start >= t) { expectedStart = r; break; }
            }
            if (t <= 0) expectedStart = 0;
            var range = v.GetRange();
            Assert.True(expectedStart == range.StartIndex, $"iter {iter}: size {size} gap {gap} offset {offset}: start {range.StartIndex} != {expectedStart}");
            Assert.True(range.EndIndex >= range.StartIndex);
        }
    }
}
