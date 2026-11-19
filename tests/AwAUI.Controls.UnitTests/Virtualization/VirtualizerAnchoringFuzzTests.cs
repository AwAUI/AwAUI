using AwAUI.Controls.Primitives;
using AwAUI.UI;

namespace AwAUI.Controls.UnitTests.Virtualization;

/// <summary>
/// 补充的随机测试：滚动锚定（Measure 的调整量与 TanStack resizeItem 一致，锚定后测量行之后的内容相对视口不动）、
/// MoveItems、粘性项、GetVirtualItems 的内容，以及 GetOffsetForIndex 的四种对齐，全部与逐行累加的朴素模型比较。
/// </summary>
public class VirtualizerAnchoringFuzzTests
{
    [Theory]
    [InlineData(21, 1)]
    [InlineData(22, 1)]
    [InlineData(23, 2)]
    [InlineData(24, 3)]
    [InlineData(25, 1)]
    public void Anchoring_moves_and_items_match_naive_model(int seed, int lanes)
    {
        var rng = new Random(seed);
        int count = rng.Next(1, 300);
        double gap = rng.Next(0, 4);
        double padStart = rng.Next(0, 6), padEnd = rng.Next(0, 6), margin = rng.Next(0, 30);
        int overscan = rng.Next(0, 4);
        var sticky = Enumerable.Range(0, count).Where(_ => rng.Next(12) == 0).ToArray();
        var opts = new VirtualizerOptions
        {
            EstimatedItemSize = 20, Lanes = lanes, Gap = gap, PaddingStart = padStart, PaddingEnd = padEnd, ScrollMargin = margin,
            Overscan = overscan, StickyIndices = sticky,
        };
        var v = new Virtualizer(count, opts);
        var sizes = Enumerable.Repeat(20.0, count).ToList();
        double offset = 0, viewport = rng.Next(20, 200);
        v.ViewportSize = viewport;

        (double[] Start, double[] Size, double Total) Model()
        {
            int rows = (sizes.Count + lanes - 1) / lanes;
            var start = new double[rows];
            var size = new double[rows];
            double acc = margin + padStart;
            for (int r = 0; r < rows; r++)
            {
                start[r] = acc;
                size[r] = Enumerable.Range(r * lanes, Math.Min(lanes, sizes.Count - r * lanes)).Max(k => sizes[k]);
                acc += size[r] + gap;
            }
            double total = rows == 0 ? padStart + padEnd : acc - gap + padEnd - margin;
            return (start, size, total);
        }

        for (int step = 0; step < 1500; step++)
        {
            int op = rng.Next(10);
            if (op <= 4)
            {
                // 测量：锚定调整量 = 行尺寸变化（当该行起点在滚动偏移之上）
                int i = rng.Next(sizes.Count);
                double s = rng.Next(6) == 0 ? 0 : rng.Next(1, 80) + (rng.Next(3) == 0 ? 0.5 : 0);
                var (start, size, _) = Model();
                int row = i / lanes;
                double oldRow = size[row];
                sizes[i] = s;
                var (_, size2, _) = Model();
                double expectedDelta = start[row] < offset ? size2[row] - oldRow : 0;
                // 锚定前，测量行之后第一行相对视口的位置
                int probeRow = Math.Min(size.Length - 1, row + 1 + rng.Next(3));
                double before = start[probeRow] - offset;
                double delta = v.Measure(i, s);
                Assert.True(Math.Abs(expectedDelta - delta) < 1e-9, $"step {step}: delta {delta} != {expectedDelta}");
                offset += delta;
                Assert.Equal(offset, v.ScrollOffset, 9);
                if (expectedDelta != 0 && probeRow > row)
                {
                    var (start3, _, _) = Model();
                    Assert.True(Math.Abs(start3[probeRow] - offset - before) < 1e-6, $"step {step}: 锚定后第 {probeRow} 行相对视口移动了");
                }
            }
            else if (op == 5)
            {
                int i = rng.Next(sizes.Count + 1), n = rng.Next(1, 4);
                v.InsertItems(i, n);
                sizes.InsertRange(i, Enumerable.Repeat(20.0, n));
            }
            else if (op == 6 && sizes.Count > 1)
            {
                int i = rng.Next(sizes.Count), n = Math.Min(rng.Next(1, 4), sizes.Count - i - (i == 0 ? 1 : 0));
                if (n <= 0) continue;
                v.RemoveItems(i, n);
                sizes.RemoveRange(i, n);
            }
            else if (op == 7 && sizes.Count > 1)
            {
                int n = rng.Next(1, Math.Min(4, sizes.Count));
                int from = rng.Next(sizes.Count - n + 1), to = rng.Next(sizes.Count - n + 1);
                v.MoveItems(from, to, n);
                var block = sizes.GetRange(from, n);
                sizes.RemoveRange(from, n);
                sizes.InsertRange(to, block);
            }
            else
            {
                var (_, _, t) = Model();
                offset = rng.NextDouble() * (t + margin + 40) - 10;
                v.ScrollOffset = offset;
            }

            // 粘性索引在插入 / 删除后可能越界：选项里的索引按当前项数过滤（与实现相同）
            var (rs, rz, total) = Model();
            Assert.True(Math.Abs(total - v.TotalSize) < 1e-6, $"step {step}: total {v.TotalSize} != {total}");
            int probe = rng.Next(sizes.Count);
            Assert.True(Math.Abs(rs[probe / lanes] - v.GetItem(probe).Start) < 1e-6, $"step {step}: start of {probe}");
            Assert.Equal(sizes[probe], v.GetItem(probe).Size);

            // 渲染项：预渲染范围内的全部项，按索引升序，外加当前粘性项
            int rows = rs.Length;
            int startRow = rows - 1;
            for (int r = 0; r < rows; r++)
                if (rs[r] + rz[r] > offset || rs[r] >= offset) { startRow = r; break; }
            int endRow = startRow;
            for (int r = 0; r < rows; r++) if (rs[r] < offset + viewport && r > endRow) endRow = r;
            int os = Math.Max(0, startRow - overscan), oe = Math.Min(rows - 1, endRow + overscan);
            var expected = Enumerable.Range(os * lanes, Math.Min(sizes.Count, (oe + 1) * lanes) - os * lanes).ToList();
            var activeSticky = sticky.Where(k => k < sizes.Count && k <= startRow * lanes).DefaultIfEmpty(-1).Max();
            if (activeSticky >= 0 && activeSticky < os * lanes) expected.Insert(0, activeSticky);
            var items = v.GetVirtualItems();
            Assert.Equal(expected, items.Select(it => it.Index).ToList());
            foreach (var it in items)
            {
                Assert.Equal(it.Index == activeSticky, it.IsActiveSticky);
                if (it.IsActiveSticky)
                {
                    // 吸附在视口开头，被下一个粘性项推走
                    double expectedSticky = Math.Max(offset, it.Start);
                    int nextSticky = sticky.Where(k => k < sizes.Count && k > it.Index).DefaultIfEmpty(-1).Min();
                    if (nextSticky >= 0) expectedSticky = Math.Min(expectedSticky, rs[nextSticky / lanes] - it.Size);
                    Assert.True(Math.Abs(expectedSticky - it.StickyStart) < 1e-6, $"step {step}: sticky start {it.StickyStart} != {expectedSticky}");
                }
                else Assert.Equal(it.Start, it.StickyStart);
            }

            // 对齐：与 TanStack getOffsetForIndex 的规则逐项比较
            int target = rng.Next(sizes.Count);
            var align = (ScrollAlignment)rng.Next(4);
            double itemStart = rs[target / lanes], itemSize = sizes[target], itemEnd = itemStart + itemSize;
            double max = Math.Max(0, total + margin - viewport);
            double expectedOffset;
            var a = align;
            if (a == ScrollAlignment.Nearest)
            {
                if (itemEnd >= offset + viewport) a = ScrollAlignment.End;
                else if (itemStart <= offset) a = ScrollAlignment.Start;
            }
            if (a == ScrollAlignment.Nearest) expectedOffset = offset;
            else
            {
                double to = a == ScrollAlignment.End ? itemEnd : itemStart;
                if (a == ScrollAlignment.Center) to += (itemSize - viewport) / 2;
                else if (a == ScrollAlignment.End) to -= viewport;
                expectedOffset = Math.Max(0, Math.Min(max, to));
            }
            Assert.True(Math.Abs(expectedOffset - v.GetOffsetForIndex(target, align)) < 1e-6, $"step {step}: offset for {target} {align}");
        }
    }
}
