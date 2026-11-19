using System.Collections;
using System.Collections.Specialized;
using AwAUI.Controls.Primitives;

namespace AwAUI.Controls.UnitTests.Selection;

/// <summary>
/// 补充的随机测试：多项的集合变更通知（一次插入 / 删除 / 替换 / 移动多项）、批量更新中穿插集合变更、单选 / 多选切换、
/// 更换数据源。与逐项的参考模型对比，并核对事件累计出的选中集合、IndexesChanged 与 PropertyChanged。
/// </summary>
public class SelectionModelRangeFuzzTests
{
    /// <summary>引发多项变更通知的列表（ObservableCollection 只引发单项通知）。</summary>
    sealed class RangeList : IReadOnlyList<int>, INotifyCollectionChanged
    {
        readonly List<int> _items;

        public RangeList(IEnumerable<int> items) => _items = items.ToList();

        public event NotifyCollectionChangedEventHandler? CollectionChanged;

        public int Count => _items.Count;

        public int this[int index] => _items[index];

        public IEnumerator<int> GetEnumerator() => _items.GetEnumerator();

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        public void InsertRange(int index, IList<int> items)
        {
            _items.InsertRange(index, items);
            CollectionChanged?.Invoke(this, new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Add, (IList)items.ToList(), index));
        }

        public void RemoveRange(int index, int count)
        {
            var old = _items.GetRange(index, count);
            _items.RemoveRange(index, count);
            CollectionChanged?.Invoke(this, new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Remove, (IList)old, index));
        }

        public void ReplaceRange(int index, IList<int> items)
        {
            var old = _items.GetRange(index, items.Count);
            for (int i = 0; i < items.Count; i++) _items[index + i] = items[i];
            CollectionChanged?.Invoke(this, new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Replace, (IList)items.ToList(), (IList)old, index));
        }

        public void MoveRange(int from, int to, int count)
        {
            var block = _items.GetRange(from, count);
            _items.RemoveRange(from, count);
            _items.InsertRange(to, block);
            CollectionChanged?.Invoke(this, new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Move, (IList)block, to, from));
        }

        public void ResetTo(IEnumerable<int> items)
        {
            var list = items.ToList();
            _items.Clear();
            _items.AddRange(list);
            CollectionChanged?.Invoke(this, new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
        }
    }

    /// <summary>逐项的参考模型（与 SelectionModelTests 中的相同语义，扩展到多项）。</summary>
    sealed class Model
    {
        public List<int> Items = new();
        public List<bool> Selected = new();
        public int SelectedIndex = -1;
        public int Anchor = -1;
        public bool Single = true;

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

        public void Clear()
        {
            for (int k = 0; k < Selected.Count; k++) Selected[k] = false;
            SelectedIndex = -1;
        }

        public void SetSingle(bool single)
        {
            if (Single == single) return;
            Single = single;
            if (single)
            {
                for (int k = 0; k < Selected.Count; k++) Selected[k] = k == SelectedIndex;
            }
        }

        public void InsertRange(int i, IList<int> items)
        {
            Items.InsertRange(i, items);
            Selected.InsertRange(i, items.Select(_ => false));
            if (SelectedIndex >= i) SelectedIndex += items.Count;
            if (Anchor >= i) Anchor += items.Count;
        }

        public void RemoveRange(int i, int n)
        {
            Items.RemoveRange(i, n);
            Selected.RemoveRange(i, n);
            if (SelectedIndex >= i + n) SelectedIndex -= n;
            else if (SelectedIndex >= i) SelectedIndex = First();
            if (Anchor >= i + n) Anchor -= n;
            else if (Anchor >= i) Anchor = -1;
        }

        public void ReplaceRange(int i, IList<int> items)
        {
            for (int k = 0; k < items.Count; k++)
            {
                Items[i + k] = items[k];
                Selected[i + k] = false;
            }
            if (SelectedIndex >= i && SelectedIndex < i + items.Count) SelectedIndex = First();
            if (Anchor >= i && Anchor < i + items.Count) Anchor = -1;
        }

        public void MoveRange(int from, int to, int n)
        {
            var items = Items.GetRange(from, n);
            var sel = Selected.GetRange(from, n);
            Items.RemoveRange(from, n);
            Selected.RemoveRange(from, n);
            Items.InsertRange(to, items);
            Selected.InsertRange(to, sel);
            SelectedIndex = Map(SelectedIndex, from, to, n);
            Anchor = Map(Anchor, from, to, n);
        }

        static int Map(int i, int from, int to, int n)
        {
            if (i < 0) return i;
            if (i >= from && i < from + n) return to + (i - from);
            int j = i >= from + n ? i - n : i;
            return j >= to ? j + n : j;
        }

        public void Reset(List<int> items)
        {
            int kept = Single && SelectedIndex >= 0 ? items.IndexOf(Items[SelectedIndex]) : -1;
            Items = items.ToList();
            Selected = items.Select(_ => false).ToList();
            SelectedIndex = -1;
            Anchor = -1;
            if (kept >= 0) Select(kept);
        }
    }

    [Theory]
    [InlineData(11)]
    [InlineData(12)]
    [InlineData(13)]
    [InlineData(14)]
    [InlineData(15)]
    [InlineData(16)]
    public void Range_changes_batches_and_mode_switches_match_reference(int seed)
    {
        var rng = new Random(seed);
        int next = 1000;
        var src = new RangeList(Enumerable.Range(0, 30));
        var model = new SelectionModel<int>(src) { SingleSelect = rng.Next(2) == 0 };
        var r = new Model { Items = src.ToList(), Selected = src.Select(_ => false).ToList(), Single = model.SingleSelect };

        var tracked = new HashSet<int>();
        model.SelectionChanged += (_, e) =>
        {
            foreach (var item in e.DeselectedItems) Assert.True(tracked.Remove(item), "取消了未选中的项 " + item);
            foreach (var item in e.SelectedItems) Assert.True(tracked.Add(item), "重复选中 " + item);
        };
        model.SourceReset += (_, _) =>
        {
            // 多选重置后旧项不可得：以实际状态重新同步
            tracked.Clear();
            foreach (var i in model.SelectedItems) tracked.Add(i);
        };
        // 选中索引的 PropertyChanged 必须反映实际值（最后一次通知后的值等于当前值）
        int lastNotifiedIndex = model.SelectedIndex;
        model.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SelectionModel<int>.SelectedIndex)) lastNotifiedIndex = model.SelectedIndex;
        };

        var history = new List<string>();
        for (int step = 0; step < 4000; step++)
        {
            bool batch = rng.Next(4) == 0;
            int ops = batch ? rng.Next(1, 5) : 1;
            if (batch) model.BeginBatchUpdate();
            var desc = new List<string>();
            for (int o = 0; o < ops; o++)
            {
                int n = src.Count;
                int a = rng.Next(-1, n + 1), b = rng.Next(-1, n + 1);
                switch (rng.Next(13))
                {
                    case 0: case 1: model.Select(a); r.Select(a); desc.Add($"Select({a})"); break;
                    case 2: model.Deselect(a); r.Deselect(a); desc.Add($"Deselect({a})"); break;
                    case 3:
                        if (!model.SingleSelect) { model.SelectRange(a, b); r.SelectRange(a, b); desc.Add($"SelectRange({a},{b})"); }
                        break;
                    case 4: model.Clear(); r.Clear(); desc.Add("Clear"); break;
                    case 5:
                    {
                        int i = rng.Next(n + 1);
                        var items = Enumerable.Range(0, rng.Next(1, 5)).Select(_ => next++).ToList();
                        src.InsertRange(i, items);
                        r.InsertRange(i, items);
                        desc.Add($"InsertRange({i},{items.Count})");
                        break;
                    }
                    case 6: case 7:
                        if (n > 0)
                        {
                            int i = rng.Next(n), k = rng.Next(1, Math.Min(6, n - i) + 1);
                            src.RemoveRange(i, k);
                            r.RemoveRange(i, k);
                            desc.Add($"RemoveRange({i},{k})");
                        }
                        break;
                    case 8:
                        if (n > 0)
                        {
                            int i = rng.Next(n), k = rng.Next(1, Math.Min(4, n - i) + 1);
                            var items = Enumerable.Range(0, k).Select(_ => next++).ToList();
                            src.ReplaceRange(i, items);
                            r.ReplaceRange(i, items);
                            desc.Add($"ReplaceRange({i},{k})");
                        }
                        break;
                    case 9:
                        if (n > 1)
                        {
                            int k = rng.Next(1, Math.Min(5, n) + 1), from = rng.Next(n - k + 1), to = rng.Next(n - k + 1);
                            src.MoveRange(from, to, k);
                            r.MoveRange(from, to, k);
                            desc.Add($"MoveRange({from},{to},{k})");
                        }
                        break;
                    case 10:
                    {
                        bool single = rng.Next(2) == 0;
                        model.SingleSelect = single;
                        r.SetSingle(single);
                        desc.Add($"Single={single}");
                        break;
                    }
                    case 11:
                        if (rng.Next(4) == 0)
                        {
                            var shuffled = src.Where(_ => rng.Next(4) != 0).OrderBy(_ => rng.Next()).ToList();
                            src.ResetTo(shuffled);
                            r.Reset(shuffled);
                            desc.Add("Reset");
                        }
                        break;
                    default:
                        if (!model.SingleSelect && rng.Next(3) == 0) { model.SelectAll(); r.SelectRange(0, n - 1); desc.Add("SelectAll"); }
                        else { model.Select(b); r.Select(b); desc.Add($"Select({b})"); }
                        break;
                }
                // 批量更新中状态也是即时的：每个操作之后都核对索引
                var exp = Enumerable.Range(0, r.Items.Count).Where(i => r.Selected[i]).ToArray();
                Assert.True(exp.SequenceEqual(model.SelectedIndexes) && r.SelectedIndex == model.SelectedIndex,
                    $"after [{string.Join(" | ", history.Skip(Math.Max(0, history.Count - 6)))}] step {step} [{string.Join("; ", desc)}]: indexes {string.Join(",", model.SelectedIndexes)} (sel {model.SelectedIndex}) != {string.Join(",", exp)} (sel {r.SelectedIndex})");
            }
            if (batch) model.EndBatchUpdate();
            if (r.SelectedIndex >= 0)
                Assert.True(r.Items[r.SelectedIndex] == model.SelectedItem, $"step {step}: SelectedItem {model.SelectedItem} != {r.Items[r.SelectedIndex]}");
            history.Add($"{step}:{(batch ? "B" : "")}[{string.Join("; ", desc)}] sel={model.SelectedIndex}/{string.Join(",", model.SelectedIndexes)} single={model.SingleSelect}");

            string where = $"step {step} [{string.Join("; ", desc)}]";
            var expected = Enumerable.Range(0, r.Items.Count).Where(i => r.Selected[i]).ToArray();
            Assert.True(r.Items.SequenceEqual(src), where + ": reference items diverged");
            Assert.True(expected.SequenceEqual(model.SelectedIndexes), $"{where}: indexes {string.Join(",", model.SelectedIndexes)} != {string.Join(",", expected)}");
            Assert.True(r.SelectedIndex == model.SelectedIndex, $"{where}: SelectedIndex {model.SelectedIndex} != {r.SelectedIndex}");
            Assert.True(r.Anchor == model.AnchorIndex, $"{where}: Anchor {model.AnchorIndex} != {r.Anchor}");
            Assert.Equal(expected.Length, model.Count);
            Assert.True(tracked.SetEquals(model.SelectedItems), $"{where}: event-tracked {string.Join(",", tracked.OrderBy(x => x))} != {string.Join(",", model.SelectedItems.OrderBy(x => x))}");
            Assert.True(lastNotifiedIndex == model.SelectedIndex, $"{where}: last notified SelectedIndex {lastNotifiedIndex} != {model.SelectedIndex}");
        }
    }

    [Fact]
    public void IndexesChanged_describes_the_shift_of_selected_indexes()
    {
        var src = new RangeList(Enumerable.Range(0, 20));
        var m = new SelectionModel<int>(src) { SingleSelect = false };
        m.SelectRange(10, 12);
        var log = new List<(int Start, int Delta)>();
        m.IndexesChanged += (_, e) => log.Add((e.StartIndex, e.Delta));
        src.InsertRange(3, new[] { 100, 101 });
        Assert.Equal(new[] { 12, 13, 14 }, m.SelectedIndexes);
        src.RemoveRange(0, 2);
        Assert.Equal(new[] { 10, 11, 12 }, m.SelectedIndexes);
        src.InsertRange(15, new[] { 200 });   // 在所有选中项之后：不平移
        Assert.Equal(2, log.Count);
        Assert.Equal(2, log[0].Delta);
        Assert.Equal(-2, log[1].Delta);
        // 应用通知到旧索引得到新索引
        Assert.True(log[0].Start <= 10);
        Assert.True(log[1].Start <= 12);
    }
}
