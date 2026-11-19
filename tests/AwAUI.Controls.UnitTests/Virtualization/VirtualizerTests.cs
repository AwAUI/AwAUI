using AwAUI.Controls.Primitives;
using AwAUI.Tests.Shared;
using AwAUI.UI;

namespace AwAUI.Controls.UnitTests.Virtualization;

public class VirtualizerTests
{
    [Fact]
    public void Fixed_size_range_and_total()
    {
        var v = new Virtualizer(100, new VirtualizerOptions { ItemSize = 20, Overscan = 2 }) { ViewportSize = 100, ScrollOffset = 205 };
        Assert.Equal(2000, v.TotalSize);
        var r = v.GetRange();
        Assert.Equal(10, r.StartIndex);   // 200..220 覆盖 205
        Assert.Equal(15, r.EndIndex);     // 300..320 覆盖 305
        Assert.Equal(8, r.OverscanStartIndex);
        Assert.Equal(17, r.OverscanEndIndex);
        var items = v.GetVirtualItems();
        Assert.Equal(Enumerable.Range(8, 10), items.Select(i => i.Index));
        Assert.Equal(160, items[0].Start);
        Assert.Equal(20, items[0].Size);
    }

    [Fact]
    public void Exact_viewport_boundary_is_exclusive()
    {
        var v = new Virtualizer(100, new VirtualizerOptions { ItemSize = 20, Overscan = 0 }) { ViewportSize = 100, ScrollOffset = 200 };
        var r = v.GetRange();
        Assert.Equal(10, r.StartIndex);
        Assert.Equal(14, r.EndIndex);   // 第 15 项从 300 开始，不在 [200, 300) 内
    }

    [Fact]
    public void Padding_gap_and_scroll_margin()
    {
        var v = new Virtualizer(10, new VirtualizerOptions { ItemSize = 30, Gap = 10, PaddingStart = 5, PaddingEnd = 7, ScrollMargin = 100 });
        Assert.Equal(5 + 10 * 30 + 9 * 10 + 7, v.TotalSize);
        Assert.Equal(100 + 5 + 3 * 40, v.GetItem(3).Start);
        Assert.Equal(3, v.GetIndexAtOffset(100 + 5 + 3 * 40 + 35));   // 落在第 3 项之后的间距里：取第 3 项
        Assert.Equal(0, v.GetIndexAtOffset(-50));
        Assert.Equal(9, v.GetIndexAtOffset(1e9));
    }

    [Fact]
    public void Empty_list()
    {
        var v = new Virtualizer(0, new VirtualizerOptions { PaddingStart = 4, PaddingEnd = 4 }) { ViewportSize = 100 };
        Assert.True(v.GetRange().IsEmpty);
        Assert.Empty(v.GetVirtualItems());
        Assert.Equal(8, v.TotalSize);
        Assert.Equal(-1, v.GetIndexAtOffset(0));
        Assert.Equal(0, v.GetOffsetForIndex(5, ScrollAlignment.Center));
    }

    [Fact]
    public void Variable_sizes_with_estimates_and_measurements()
    {
        var v = new Virtualizer(5, new VirtualizerOptions { EstimatedItemSize = 50 });
        Assert.Equal(250, v.TotalSize);
        Assert.False(v.IsMeasured(2));
        v.Measure(1, 80);
        Assert.True(v.IsMeasured(1));
        Assert.Equal(280, v.TotalSize);
        Assert.Equal(130, v.GetItem(2).Start);
        Assert.Equal(80, v.GetItem(1).Size);
        Assert.Equal(1, v.GetIndexAtOffset(129));
        Assert.Equal(2, v.GetIndexAtOffset(130));
        // 非法测量被忽略
        Assert.Equal(0, v.Measure(2, double.NaN));
        Assert.Equal(0, v.Measure(2, -5));
        Assert.Equal(280, v.TotalSize);
    }

    [Fact]
    public void Estimate_callback_per_index()
    {
        var v = new Virtualizer(4, new VirtualizerOptions { EstimateSize = i => i % 2 == 0 ? 10 : 30 });
        Assert.Equal(80, v.TotalSize);
        Assert.Equal(40, v.GetItem(2).Start);
    }

    [Fact]
    public void Measuring_above_viewport_adjusts_scroll_offset()
    {
        var v = new Virtualizer(100, new VirtualizerOptions { EstimatedItemSize = 50 }) { ViewportSize = 200, ScrollOffset = 1000 };
        int first = v.GetRange().StartIndex;
        Assert.Equal(20, first);
        double adj = v.Measure(3, 70);      // 视口之上
        Assert.Equal(20, adj);
        Assert.Equal(1020, v.ScrollOffset);
        Assert.Equal(20, v.GetRange().StartIndex);   // 可见的第一项不变
        Assert.Equal(0, v.Measure(30, 90)); // 视口之下：不调整
        Assert.Equal(1020, v.ScrollOffset);
        var noAnchor = new Virtualizer(100, new VirtualizerOptions { EstimatedItemSize = 50, AdjustScrollOnMeasure = false }) { ScrollOffset = 1000 };
        Assert.Equal(0, noAnchor.Measure(3, 70));
    }

    [Fact]
    public void Offset_for_index_alignments()
    {
        var v = new Virtualizer(100, new VirtualizerOptions { ItemSize = 20 }) { ViewportSize = 100, ScrollOffset = 200 };
        Assert.Equal(500, v.GetOffsetForIndex(25, ScrollAlignment.Start));
        Assert.Equal(420, v.GetOffsetForIndex(25, ScrollAlignment.End));
        Assert.Equal(460, v.GetOffsetForIndex(25, ScrollAlignment.Center));
        Assert.Equal(420, v.GetOffsetForIndex(25, ScrollAlignment.Nearest));   // 在下方：对齐到末端
        Assert.Equal(100, v.GetOffsetForIndex(5, ScrollAlignment.Nearest));    // 在上方：对齐到开头
        Assert.Equal(200, v.GetOffsetForIndex(12, ScrollAlignment.Nearest));   // 完整可见：不动
        Assert.Equal(0, v.GetOffsetForIndex(-10, ScrollAlignment.Center));
        Assert.Equal(1900, v.GetOffsetForIndex(1000, ScrollAlignment.Start));  // 钳制到最大偏移
        var padded = new Virtualizer(100, new VirtualizerOptions { ItemSize = 20, ScrollPaddingStart = 30, ScrollPaddingEnd = 10 }) { ViewportSize = 100 };
        Assert.Equal(470, padded.GetOffsetForIndex(25, ScrollAlignment.Start));
        Assert.Equal(430, padded.GetOffsetForIndex(25, ScrollAlignment.End));
    }

    [Fact]
    public void Sticky_header_is_included_and_pushed_by_next()
    {
        var v = new Virtualizer(100, new VirtualizerOptions { ItemSize = 20, Overscan = 0, StickyIndices = new[] { 30, 0, 10, 10, -3, 500 } })
        {
            ViewportSize = 100,
            ScrollOffset = 250,
        };
        var items = v.GetVirtualItems();
        Assert.Equal(10, items[0].Index);
        Assert.True(items[0].IsActiveSticky);
        Assert.Equal(250, items[0].StickyStart);
        Assert.Equal(Enumerable.Range(12, 6), items.Skip(1).Select(i => i.Index));
        // 下一个粘性项（30，起点 600）逼近时被推上去
        v.ScrollOffset = 590;
        items = v.GetVirtualItems();
        var active = items.Single(i => i.IsActiveSticky);
        Assert.Equal(10, active.Index);
        Assert.Equal(580, active.StickyStart);
        Assert.True(items.Single(i => i.Index == 30).IsSticky);
    }

    [Fact]
    public void Lanes_grid_rows_use_max_item_size()
    {
        var v = new Virtualizer(10, new VirtualizerOptions { Lanes = 3, EstimatedItemSize = 40, Overscan = 0 }) { ViewportSize = 50 };
        Assert.Equal(4, v.RowCount);
        Assert.Equal(160, v.TotalSize);
        v.Measure(4, 100);   // 第 1 行（项 3、4、5）的高度变为 100
        Assert.Equal(220, v.TotalSize);
        Assert.Equal(140, v.GetItem(6).Start);
        Assert.Equal(1, v.GetItem(4).Lane);
        Assert.Equal(40, v.GetItem(5).Size);   // 项自身尺寸不变
        v.ScrollOffset = 60;
        var r = v.GetRange();
        Assert.Equal(3, r.StartIndex);
        Assert.Equal(5, r.EndIndex);
        Assert.Equal(new[] { 3, 4, 5 }, v.GetVirtualItems().Select(i => i.Index));
        var fixedGrid = new Virtualizer(10, new VirtualizerOptions { Lanes = 4, ItemSize = 25 });
        Assert.Equal(75, fixedGrid.TotalSize);
    }

    [Fact]
    public void Insert_remove_move_shift_measurements()
    {
        var v = new Virtualizer(5, new VirtualizerOptions { EstimatedItemSize = 10 });
        v.Measure(2, 50);
        v.InsertItems(1, 2);
        Assert.Equal(7, v.Count);
        Assert.Equal(50, v.GetItem(4).Size);
        Assert.False(v.IsMeasured(1));
        v.RemoveItems(0, 3);
        Assert.Equal(4, v.Count);
        Assert.Equal(50, v.GetItem(1).Size);
        v.MoveItems(1, 3, 1);
        Assert.Equal(50, v.GetItem(3).Size);
        Assert.Equal(10, v.GetItem(1).Size);
        v.SetCount(6);
        Assert.Equal(50, v.GetItem(3).Size);
        Assert.Equal(10, v.GetItem(5).Size);
        v.ResetMeasurements();
        Assert.Equal(60, v.TotalSize);
        Assert.Throws<ArgumentOutOfRangeException>(() => v.RemoveItems(4, 5));
        Assert.Throws<ArgumentOutOfRangeException>(() => v.Measure(6, 1));
    }

    [Fact]
    public void Max_rendered_items_bounds_zero_size_items()
    {
        var v = new Virtualizer(1_000_000, new VirtualizerOptions { ItemSize = 0, MaxRenderedItems = 100 }) { ViewportSize = 500 };
        Assert.True(v.GetVirtualItems().Count <= 100);
        var w = new Virtualizer(1_000_000, new VirtualizerOptions { EstimatedItemSize = 0, MaxRenderedItems = 100 }) { ViewportSize = 500 };
        Assert.True(w.GetVirtualItems().Count <= 100);
    }

    [Fact]
    public void Non_finite_offsets_are_ignored()
    {
        var v = new Virtualizer(10, new VirtualizerOptions { ItemSize = 10 }) { ScrollOffset = 30, ViewportSize = 20 };
        v.ScrollOffset = double.NaN;
        v.ViewportSize = double.PositiveInfinity;
        Assert.Equal(30, v.ScrollOffset);
        Assert.Equal(20, v.ViewportSize);
        v.ViewportSize = -5;
        Assert.Equal(0, v.ViewportSize);
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(2, 1)]
    [InlineData(3, 3)]
    [InlineData(4, 2)]
    public void Random_sizes_match_naive_model(int seed, int lanes)
    {
        var rng = new Random(seed);
        int count = rng.Next(1, 400);
        double gap = rng.Next(0, 3);
        var opts = new VirtualizerOptions { EstimatedItemSize = 25, Lanes = lanes, Gap = gap, PaddingStart = 3, PaddingEnd = 4, Overscan = rng.Next(0, 3), AdjustScrollOnMeasure = false };
        var v = new Virtualizer(count, opts);
        var sizes = Enumerable.Repeat(25.0, count).ToList();
        for (int step = 0; step < 600; step++)
        {
            switch (rng.Next(6))
            {
                case 0:
                case 1:
                {
                    int i = rng.Next(sizes.Count == 0 ? 1 : sizes.Count);
                    if (sizes.Count == 0) break;
                    double s = rng.Next(5) == 0 ? 0 : rng.Next(0, 120);   // 常有零尺寸（折叠的项）
                    v.Measure(i, s);
                    sizes[i] = s;
                    break;
                }
                case 2:
                {
                    int i = rng.Next(sizes.Count + 1), n = rng.Next(1, 4);
                    v.InsertItems(i, n);
                    sizes.InsertRange(i, Enumerable.Repeat(25.0, n));
                    break;
                }
                case 3:
                    if (sizes.Count > 0)
                    {
                        int i = rng.Next(sizes.Count), n = Math.Min(rng.Next(1, 4), sizes.Count - i);
                        v.RemoveItems(i, n);
                        sizes.RemoveRange(i, n);
                    }
                    break;
                default:
                    break;
            }
            // 朴素模型：逐行累加
            int rows = (sizes.Count + lanes - 1) / lanes;
            var rowStart = new double[rows];
            var rowSize = new double[rows];
            double acc = 3;
            for (int r = 0; r < rows; r++)
            {
                rowStart[r] = acc;
                rowSize[r] = Enumerable.Range(r * lanes, Math.Min(lanes, sizes.Count - r * lanes)).Max(k => sizes[k]);
                acc += rowSize[r] + gap;
            }
            double total = rows == 0 ? 7 : acc - gap + 4;
            Assert.True(Math.Abs(total - v.TotalSize) < 1e-6, $"step {step}: total {v.TotalSize} != {total}");
            if (sizes.Count == 0) continue;
            int probe = rng.Next(sizes.Count);
            Assert.True(Math.Abs(rowStart[probe / lanes] - v.GetItem(probe).Start) < 1e-6, $"step {step}: start of {probe}");
            Assert.Equal(sizes[probe], v.GetItem(probe).Size);

            double offset = rng.NextDouble() * (total + 50) - 20;
            double viewport = rng.Next(0, 300);
            v.ScrollOffset = offset;
            v.ViewportSize = viewport;
            // 起始行：与 [offset, ∞) 相交的第一行（末端在 offset 之后，或零尺寸且起点不早于 offset）；落在间距里时取下一行
            int expectedStart = rows - 1;
            for (int r = 0; r < rows; r++)
                if (rowStart[r] + rowSize[r] > offset || rowStart[r] >= offset) { expectedStart = r; break; }
            int expectedEnd = expectedStart;
            for (int r = 0; r < rows; r++) if (rowStart[r] < offset + viewport && r > expectedEnd) expectedEnd = r;
            var range = v.GetRange();
            Assert.True(expectedStart * lanes == range.StartIndex, $"step {step}: start {range.StartIndex} != {expectedStart * lanes} (offset {offset})");
            Assert.Equal(Math.Min(sizes.Count - 1, expectedEnd * lanes + lanes - 1), range.EndIndex);
        }
    }

    // ------------------------------------------------------------ 规模

    [Fact]
    public void Ten_million_fixed_items_are_constant_time_and_memory()
    {
#if NET
        long before = GC.GetAllocatedBytesForCurrentThread();
#endif
        var v = new Virtualizer(10_000_000, new VirtualizerOptions { ItemSize = 24 }) { ViewportSize = 800 };
        Assert.Equal(240_000_000, v.TotalSize);
        double sum = 0;
        for (int i = 0; i < 1000; i++)
        {
            v.ScrollOffset = i * 239_999.0;
            foreach (var item in v.GetVirtualItems()) sum += item.Start;
        }
        Assert.Equal(9_999_999, v.GetIndexAtOffset(240_000_000));
        Assert.Equal(240_000_000 - 800, v.GetOffsetForIndex(9_999_999, ScrollAlignment.End));
        Assert.True(sum > 0);
#if NET
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(allocated < 1_000_000, $"allocated {allocated} bytes");   // 与项数无关
#endif
    }

    [Fact]
    public void Hundred_thousand_variable_items_scale_logarithmically()
    {
        // 测量（单点修改 + 前缀和）与定位都是 O(log n)：n 增大 100 倍，每次操作的时间增长远小于线性。
        // 用线程 CPU 时间，取多轮最小值，抵抗机器负载。
        double Measure(int n)
        {
            var v = new Virtualizer(n, new VirtualizerOptions { EstimatedItemSize = 30 }) { ViewportSize = 600 };
            var rng = new Random(7);
            double best = double.MaxValue;
            for (int round = 0; round < 5; round++)
            {
                double t0 = ThreadCpuClock.NowMs();
                for (int k = 0; k < 20_000; k++)
                {
                    v.Measure(rng.Next(n), 20 + rng.Next(40));
                    v.ScrollOffset = rng.NextDouble() * v.TotalSize;
                    var r = v.GetRange();
                    if (r.StartIndex < 0) throw new InvalidOperationException();
                }
                best = Math.Min(best, ThreadCpuClock.NowMs() - t0);
            }
            return best;
        }
        Measure(1000);   // 预热
        double small = Math.Max(Measure(1000), 0.5);
        double large = Measure(100_000);
        Assert.True(large < small * 12, $"1k: {small:F2} ms, 100k: {large:F2} ms（线性增长会是约 100 倍）");
    }
}
