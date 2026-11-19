using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using AwAUI.Controls.Primitives;

namespace AwAUI.Controls.UnitTests.Selection;

/// <summary>可以在保留内容的前提下引发 Reset 的集合（ObservableCollection 只有 Clear 会引发 Reset）。</summary>
sealed class ResettableCollection<T> : ObservableCollection<T>
{
    public ResettableCollection() { }

    public ResettableCollection(IEnumerable<T> items) : base(items) { }

    public void ResetTo(IEnumerable<T> items)
    {
        var list = items.ToList();
        Items.Clear();
        foreach (var i in list) Items.Add(i);
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }
}

public class SelectionModelTests
{
    static SelectionModel<string> Create(out ResettableCollection<string> source, int count = 10, bool single = true)
    {
        source = new ResettableCollection<string>(Enumerable.Range(0, count).Select(i => "item" + i));
        return new SelectionModel<string>(source) { SingleSelect = single };
    }

    [Fact]
    public void Single_select_replaces_selection_and_sets_anchor()
    {
        var m = Create(out _);
        m.Select(3);
        m.Select(5);
        Assert.Equal(5, m.SelectedIndex);
        Assert.Equal("item5", m.SelectedItem);
        Assert.Equal(new[] { 5 }, m.SelectedIndexes);
        Assert.Equal(5, m.AnchorIndex);
        Assert.Equal(1, m.Count);
    }

    [Fact]
    public void Multiple_select_keeps_first_as_selected_index()
    {
        var m = Create(out _, single: false);
        m.Select(4);
        m.Select(2);
        m.Select(7);
        Assert.Equal(4, m.SelectedIndex);
        Assert.Equal(new[] { 2, 4, 7 }, m.SelectedIndexes);
        Assert.Equal(new[] { "item2", "item4", "item7" }, m.SelectedItems);
        m.Deselect(4);
        Assert.Equal(2, m.SelectedIndex);   // 变为最小的选中索引
        Assert.Equal(7, m.AnchorIndex);
    }

    [Fact]
    public void SelectRange_and_DeselectRange()
    {
        var m = Create(out _, single: false);
        m.SelectRange(6, 2);
        Assert.Equal(new[] { 2, 3, 4, 5, 6 }, m.SelectedIndexes);
        Assert.Equal(2, m.SelectedIndex);
        m.DeselectRange(3, 4);
        Assert.Equal(new[] { 2, 5, 6 }, m.SelectedIndexes);
        m.SelectRange(-5, 100);   // 越界部分被裁掉
        Assert.Equal(10, m.Count);
        Assert.Single(m.SelectedRanges);
    }

    [Fact]
    public void Range_and_select_all_are_rejected_in_single_mode()
    {
        var m = Create(out _);
        Assert.Throws<InvalidOperationException>(() => m.SelectRange(0, 2));
        Assert.Throws<InvalidOperationException>(() => m.SelectAll());
    }

    [Fact]
    public void Out_of_range_select_is_ignored()
    {
        var m = Create(out _);
        m.Select(10);
        m.Select(-1);
        Assert.Equal(-1, m.SelectedIndex);
        Assert.Equal(0, m.Count);
    }

    [Fact]
    public void Clear_keeps_anchor_for_following_range()
    {
        var m = Create(out _, single: false);
        m.Select(3);
        m.Clear();
        Assert.Equal(3, m.AnchorIndex);
        Assert.Equal(-1, m.SelectedIndex);
    }

    [Fact]
    public void SelectionChanged_reports_diff_once_per_batch()
    {
        var m = Create(out _, single: false);
        m.SelectRange(0, 3);
        var events = new List<SelectionModelSelectionChangedEventArgs<string>>();
        m.SelectionChanged += (_, e) => events.Add(e);
        using (m.BatchUpdate())
        {
            m.Clear();
            m.Select(2);
            m.Select(5);
            m.Select(6);
        }
        var e = Assert.Single(events);
        Assert.Equal(new[] { 5, 6 }, e.SelectedIndexes);
        Assert.Equal(new[] { "item5", "item6" }, e.SelectedItems);
        Assert.Equal(new[] { 0, 1, 3 }, e.DeselectedIndexes);
        Assert.Equal(new[] { "item0", "item1", "item3" }, e.DeselectedItems);
    }

    [Fact]
    public void No_event_when_nothing_changes()
    {
        var m = Create(out _);
        m.Select(2);
        int n = 0;
        m.SelectionChanged += (_, _) => n++;
        m.SelectedIndex = 2;
        m.Select(2);
        Assert.Equal(0, n);
    }

    [Fact]
    public void PropertyChanged_for_index_item_anchor_count()
    {
        var m = Create(out _, single: false);
        var names = new List<string>();
        ((INotifyPropertyChanged)m).PropertyChanged += (_, e) => names.Add(e.PropertyName!);
        m.Select(1);
        Assert.Equal(new[] { "SelectedIndex", "SelectedItem", "AnchorIndex", "Count" }, names);
        names.Clear();
        m.Select(2);
        Assert.Equal(new[] { "AnchorIndex", "Count" }, names);
    }

    [Fact]
    public void SelectedItem_set_before_source_is_applied_when_source_arrives()
    {
        var m = new SelectionModel<string>();
        m.SelectedItem = "c";
        Assert.Equal("c", m.SelectedItem);
        Assert.Equal(-1, m.SelectedIndex);
        int changed = 0;
        m.SelectionChanged += (_, _) => changed++;
        m.Source = new[] { "a", "b", "c" };
        Assert.Equal(2, m.SelectedIndex);
        Assert.Equal("c", m.SelectedItem);
        Assert.Equal(1, changed);
    }

    [Fact]
    public void Index_selection_before_source_is_trimmed()
    {
        var m = new SelectionModel<int> { SingleSelect = false };
        m.Select(1);
        m.Select(8);
        m.Source = new[] { 10, 20, 30 };
        Assert.Equal(new[] { 1 }, m.SelectedIndexes);
        Assert.Equal(20, m.SelectedItem);
    }

    [Fact]
    public void SelectedItem_not_in_source_clears()
    {
        var m = Create(out _);
        m.Select(1);
        m.SelectedItem = "nope";
        Assert.Equal(-1, m.SelectedIndex);
        m.SelectedItem = "item4";
        Assert.Equal(4, m.SelectedIndex);
    }

    [Fact]
    public void Switching_to_single_keeps_selected_index_only()
    {
        var m = Create(out _, single: false);
        m.Select(5);
        m.SelectRange(1, 3);
        m.SingleSelect = true;
        Assert.Equal(new[] { 5 }, m.SelectedIndexes);
    }

    // ------------------------------------------------------------ 数据源变更

    [Fact]
    public void Insert_before_selection_shifts_indexes()
    {
        var m = Create(out var src, single: false);
        m.SelectRange(2, 4);
        SelectionModelIndexesChangedEventArgs? shifted = null;
        m.IndexesChanged += (_, e) => shifted = e;
        int changed = 0;
        m.SelectionChanged += (_, _) => changed++;
        src.Insert(0, "new");
        Assert.Equal(new[] { 3, 4, 5 }, m.SelectedIndexes);
        Assert.Equal(new[] { "item2", "item3", "item4" }, m.SelectedItems);
        Assert.Equal(3, m.SelectedIndex);
        Assert.Equal((0, 1), (shifted!.StartIndex, shifted.Delta));
        Assert.Equal(0, changed);
    }

    [Fact]
    public void Insert_inside_selected_range_splits_it()
    {
        var m = Create(out var src, single: false);
        m.SelectRange(2, 4);
        src.Insert(3, "new");
        Assert.Equal(new[] { 2, 4, 5 }, m.SelectedIndexes);
        Assert.False(m.IsSelected(3));
    }

    [Fact]
    public void Remove_selected_item_reports_deselection_and_lost_selection()
    {
        var m = Create(out var src);
        m.Select(4);
        SelectionModelSelectionChangedEventArgs<string>? ev = null;
        int lost = 0;
        m.SelectionChanged += (_, e) => ev = e;
        m.LostSelection += (_, _) => lost++;
        src.RemoveAt(4);
        Assert.Equal(-1, m.SelectedIndex);
        Assert.Equal(new[] { 4 }, ev!.DeselectedIndexes);
        Assert.Equal(new[] { "item4" }, ev.DeselectedItems);
        Assert.Equal(1, lost);
    }

    [Fact]
    public void Remove_before_selection_shifts_down()
    {
        var m = Create(out var src, single: false);
        m.Select(5);
        m.Select(7);
        src.RemoveAt(1);
        Assert.Equal(new[] { 4, 6 }, m.SelectedIndexes);
        Assert.Equal(4, m.SelectedIndex);
        Assert.Equal(6, m.AnchorIndex);
    }

    [Fact]
    public void Replace_deselects_replaced_item()
    {
        var m = Create(out var src);
        m.Select(2);
        src[2] = "replaced";
        Assert.Equal(-1, m.SelectedIndex);
        Assert.Equal(0, m.Count);
    }

    [Fact]
    public void Move_keeps_selection_on_the_item()
    {
        var m = Create(out var src, single: false);
        m.Select(2);
        m.Select(3);
        src.Move(2, 7);
        Assert.Equal(new[] { "item3", "item2" }, m.SelectedItems);
        Assert.Equal(7, m.SelectedIndex);
        Assert.Equal(new[] { 2, 7 }, m.SelectedIndexes);
    }

    [Fact]
    public void Reset_single_keeps_item_if_still_present()
    {
        var m = Create(out var src);
        m.Select(3);
        int changed = 0, reset = 0;
        m.SelectionChanged += (_, _) => changed++;
        m.SourceReset += (_, _) => reset++;
        src.ResetTo(new[] { "x", "item3", "y" });
        Assert.Equal(1, m.SelectedIndex);
        Assert.Equal("item3", m.SelectedItem);
        Assert.Equal(0, changed);
        Assert.Equal(1, reset);
    }

    [Fact]
    public void Reset_single_clears_if_item_gone_and_multi_always_clears()
    {
        var m = Create(out var src);
        m.Select(3);
        SelectionModelSelectionChangedEventArgs<string>? ev = null;
        m.SelectionChanged += (_, e) => ev = e;
        src.ResetTo(new[] { "x", "y" });
        Assert.Equal(-1, m.SelectedIndex);
        Assert.Equal(new[] { "item3" }, ev!.DeselectedItems);

        var multi = Create(out var src2, single: false);
        multi.SelectRange(0, 2);
        src2.ResetTo(src2.ToList());
        Assert.Equal(0, multi.Count);
    }

    [Fact]
    public void Changing_source_keeps_single_item_if_present()
    {
        var m = new SelectionModel<string>(new[] { "a", "b", "c" });
        m.Select(1);
        m.Source = new[] { "z", "y", "b" };
        Assert.Equal(2, m.SelectedIndex);
        m.Source = new[] { "q" };
        Assert.Equal(-1, m.SelectedIndex);
    }

    [Fact]
    public void Old_source_is_unsubscribed()
    {
        var src = new ObservableCollection<int>(Enumerable.Range(0, 5));
        var m = new SelectionModel<int>(src);
        m.Select(2);
        m.Source = new ObservableCollection<int>(Enumerable.Range(0, 5));
        int before = m.SelectedIndex;
        src.Insert(0, 99);
        Assert.Equal(before, m.SelectedIndex);
    }

    [Fact]
    public void Non_list_enumerable_is_snapshotted()
    {
        var m = new SelectionModel<int>(Enumerable.Range(0, 4).Select(i => i * 10));
        m.Select(2);
        Assert.Equal(20, m.SelectedItem);
        Assert.Equal(4, m.ItemCount);
    }

    [Fact]
    public void Collection_change_during_batch_flushes_pending_diff_first()
    {
        var m = Create(out var src, single: false);
        var log = new List<string>();
        m.SelectionChanged += (_, e) => log.Add("+" + string.Join(",", e.SelectedIndexes) + " -" + string.Join(",", e.DeselectedIndexes));
        m.BeginBatchUpdate();
        m.Select(5);
        src.RemoveAt(5);
        m.Select(1);
        m.EndBatchUpdate();
        Assert.Equal(new[] { "+5 -", "+ -5", "+1 -" }, log);
        Assert.Equal(new[] { 1 }, m.SelectedIndexes);
    }

    [Fact]
    public void Select_all_on_ten_million_items_is_constant_memory()
    {
        var m = new SelectionModel<int>(new FakeList(10_000_000)) { SingleSelect = false };
#if NET
        long before = GC.GetAllocatedBytesForCurrentThread();
#endif
        m.SelectAll();
        var indexes = m.SelectedIndexes;
        Assert.Equal(10_000_000, indexes.Count);
        Assert.Equal(9_999_999, indexes[9_999_999]);
        m.Deselect(5_000_000);
        Assert.Equal(2, m.SelectedRanges.Count);
        Assert.Equal(5_000_001, m.SelectedIndexes[5_000_000]);
#if NET
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(allocated < 64 * 1024, $"allocated {allocated} bytes");
#endif
    }

    sealed class FakeList : IReadOnlyList<int>
    {
        public FakeList(int count) => Count = count;

        public int Count { get; }

        public int this[int index] => index;

        public IEnumerator<int> GetEnumerator() => Enumerable.Range(0, Count).GetEnumerator();

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    // ------------------------------------------------------------ 与参考模型对比的随机测试

    sealed class Reference
    {
        public readonly List<int> Items;
        public readonly List<bool> Selected;
        public int SelectedIndex = -1;
        public int Anchor = -1;
        public bool Single;

        public Reference(IEnumerable<int> items, bool single)
        {
            Items = items.ToList();
            Selected = Items.Select(_ => false).ToList();
            Single = single;
        }

        int First() => Selected.IndexOf(true);

        public void Select(int i)
        {
            if (i < 0 || i >= Items.Count) return;
            if (Single)
            {
                for (int k = 0; k < Selected.Count; k++) Selected[k] = false;
                SelectedIndex = i;
            }
            else if (SelectedIndex < 0) SelectedIndex = i;
            Selected[i] = true;
            Anchor = i;
        }

        public void Deselect(int i)
        {
            if (i < 0 || i >= Items.Count) return;
            Selected[i] = false;
            if (SelectedIndex == i) SelectedIndex = First();
        }

        public void SelectRange(int a, int b)
        {
            if (a > b) (a, b) = (b, a);
            a = Math.Max(0, a);
            b = Math.Min(Items.Count - 1, b);
            if (b < a) return;
            for (int k = a; k <= b; k++) Selected[k] = true;
            if (SelectedIndex < 0) SelectedIndex = a;
        }

        public void DeselectRange(int a, int b)
        {
            if (a > b) (a, b) = (b, a);
            for (int k = Math.Max(0, a); k <= Math.Min(Items.Count - 1, b); k++) Selected[k] = false;
            if (SelectedIndex >= a && SelectedIndex <= b) SelectedIndex = First();
        }

        public void Clear()
        {
            for (int k = 0; k < Selected.Count; k++) Selected[k] = false;
            SelectedIndex = -1;
        }

        public void Insert(int i, int item)
        {
            Items.Insert(i, item);
            Selected.Insert(i, false);
            if (SelectedIndex >= i) SelectedIndex++;
            if (Anchor >= i) Anchor++;
        }

        public void RemoveAt(int i)
        {
            Items.RemoveAt(i);
            Selected.RemoveAt(i);
            if (SelectedIndex == i) SelectedIndex = First();
            else if (SelectedIndex > i) SelectedIndex--;
            if (Anchor == i) Anchor = -1;
            else if (Anchor > i) Anchor--;
        }

        public void Replace(int i, int item)
        {
            Items[i] = item;
            Selected[i] = false;
            if (SelectedIndex == i) SelectedIndex = First();
            if (Anchor == i) Anchor = -1;
        }

        public void Move(int from, int to)
        {
            var item = Items[from];
            bool sel = Selected[from];
            Items.RemoveAt(from);
            Selected.RemoveAt(from);
            Items.Insert(to, item);
            Selected.Insert(to, sel);
            SelectedIndex = Map(SelectedIndex, from, to);
            Anchor = Map(Anchor, from, to);
        }

        static int Map(int i, int from, int to)
        {
            if (i < 0) return i;
            if (i == from) return to;
            int j = i > from ? i - 1 : i;
            return j >= to ? j + 1 : j;
        }

        public void Reset(List<int> items)
        {
            int kept = Single && SelectedIndex >= 0 ? items.IndexOf(Items[SelectedIndex]) : -1;
            Items.Clear();
            Items.AddRange(items);
            Selected.Clear();
            Selected.AddRange(items.Select(_ => false));
            SelectedIndex = -1;
            Anchor = -1;
            if (kept >= 0) Select(kept);
        }
    }

    [Theory]
    [InlineData(1, true)]
    [InlineData(2, false)]
    [InlineData(3, false)]
    [InlineData(4, true)]
    [InlineData(5, false)]
    public void Random_operations_match_reference_model(int seed, bool single)
    {
        var rng = new Random(seed);
        int next = 1000;
        var src = new ResettableCollection<int>(Enumerable.Range(0, 20));
        var model = new SelectionModel<int>(src) { SingleSelect = single };
        var reference = new Reference(src, single);
        // 事件累计的净变化必须等于实际状态
        var tracked = new HashSet<int>();
        model.SelectionChanged += (_, e) =>
        {
            foreach (var item in e.DeselectedItems) Assert.True(tracked.Remove(item), "取消了未选中的项 " + item);
            foreach (var item in e.SelectedItems) Assert.True(tracked.Add(item), "重复选中 " + item);
        };
        model.SourceReset += (_, _) =>
        {
            // 重置后旧项（多选）不可得：以实际状态为准重新同步
            tracked.Clear();
            foreach (var i in model.SelectedItems) tracked.Add(i);
        };

        for (int step = 0; step < 3000; step++)
        {
            int n = src.Count;
            int op = rng.Next(14);
            int a = rng.Next(-2, n + 2), b = rng.Next(-2, n + 2);
            string desc;
            switch (op)
            {
                case 0: case 1: model.Select(a); reference.Select(a); desc = $"Select({a})"; break;
                case 2: model.Deselect(a); reference.Deselect(a); desc = $"Deselect({a})"; break;
                case 3:
                    if (single) { model.SelectedIndex = a; reference.Clear(); reference.Select(a); desc = $"SelectedIndex={a}"; }
                    else { model.SelectRange(a, b); reference.SelectRange(a, b); desc = $"SelectRange({a},{b})"; }
                    break;
                case 4: model.DeselectRange(a, b); reference.DeselectRange(a, b); desc = $"DeselectRange({a},{b})"; break;
                case 5:
                    if (rng.Next(4) == 0) { model.Clear(); reference.Clear(); desc = "Clear"; }
                    else { model.Select(a); reference.Select(a); desc = $"Select({a})"; }
                    break;
                case 6: case 7:
                {
                    int i = rng.Next(0, n + 1);
                    int item = next++;
                    src.Insert(i, item);
                    reference.Insert(i, item);
                    desc = $"Insert({i})";
                    break;
                }
                case 8: case 9:
                    if (n == 0) { desc = "noop"; break; }
                    {
                        int i = rng.Next(n);
                        src.RemoveAt(i);
                        reference.RemoveAt(i);
                        desc = $"RemoveAt({i})";
                    }
                    break;
                case 10:
                    if (n == 0) { desc = "noop"; break; }
                    {
                        int i = rng.Next(n);
                        int item = next++;
                        src[i] = item;
                        reference.Replace(i, item);
                        desc = $"Replace({i})";
                    }
                    break;
                case 11:
                    if (n < 2) { desc = "noop"; break; }
                    {
                        int f = rng.Next(n), t = rng.Next(n);
                        src.Move(f, t);
                        reference.Move(f, t);
                        desc = $"Move({f},{t})";
                    }
                    break;
                case 12:
                    if (rng.Next(5) == 0)
                    {
                        var shuffled = src.Where(_ => rng.Next(3) != 0).OrderBy(_ => rng.Next()).ToList();
                        src.ResetTo(shuffled);
                        reference.Reset(shuffled);
                        desc = "Reset";
                    }
                    else { model.Select(a); reference.Select(a); desc = $"Select({a})"; }
                    break;
                default:
                    if (!single && rng.Next(3) == 0) { model.SelectAll(); reference.SelectRange(0, n - 1); desc = "SelectAll"; }
                    else { model.Deselect(a); reference.Deselect(a); desc = $"Deselect({a})"; }
                    break;
            }
            var expected = Enumerable.Range(0, reference.Items.Count).Where(i => reference.Selected[i]).ToArray();
            Assert.True(expected.SequenceEqual(model.SelectedIndexes), $"step {step} {desc}: indexes {string.Join(",", model.SelectedIndexes)} != {string.Join(",", expected)}");
            Assert.True(reference.SelectedIndex == model.SelectedIndex, $"step {step} {desc}: SelectedIndex {model.SelectedIndex} != {reference.SelectedIndex}");
            Assert.True(reference.Anchor == model.AnchorIndex, $"step {step} {desc}: Anchor {model.AnchorIndex} != {reference.Anchor}");
            Assert.Equal(expected.Select(i => reference.Items[i]), model.SelectedItems);
            Assert.Equal(expected.Length, model.Count);
            Assert.True(tracked.SetEquals(model.SelectedItems), $"step {step} {desc}: event-tracked set differs");
            if (model.SelectedIndex >= 0) Assert.Equal(reference.Items[reference.SelectedIndex], model.SelectedItem);
        }
    }
}

public class SelectionModelEdgeTests
{
    [Fact]
    public void Huge_range_without_source_does_not_overflow_count()
    {
        var m = new SelectionModel<int> { SingleSelect = false };
        m.SelectRange(int.MinValue, int.MaxValue);
        Assert.Equal(int.MaxValue, m.Count);
        Assert.True(m.IsSelected(int.MaxValue));
        m.Source = new[] { 1, 2, 3 };
        Assert.Equal(3, m.Count);
    }

    [Fact]
    public void Reentrant_change_from_selection_changed_handler()
    {
        var m = new SelectionModel<int>(Enumerable.Range(0, 10).ToList()) { SingleSelect = false };
        int events = 0;
        m.SelectionChanged += (_, e) =>
        {
            events++;
            if (e.SelectedIndexes.Contains(3)) m.Select(4);   // 处理器中再改：作为新的一次变化
        };
        m.Select(3);
        Assert.Equal(new[] { 3, 4 }, m.SelectedIndexes);
        Assert.Equal(2, events);
    }

    [Fact]
    public void End_without_begin_throws()
    {
        Assert.Throws<InvalidOperationException>(() => new SelectionModel<int>().EndBatchUpdate());
    }
}
