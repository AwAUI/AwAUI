using System;
using System.Collections.Generic;
using AwAUI.UI;

namespace AwAUI.Controls.Primitives;

/// <summary>
/// 宿主式虚拟化的胶水：把 <see cref="Virtualizer"/> 接到属于宿主的 <see cref="ScrollController"/>，
/// 记录本次构建实现的行、在布局完成后测量变高行，并在渲染范围变化时请求宿主重建。
/// <para>
/// 宿主在 <c>Build</c> 中调用 <see cref="SetOptions"/> 与 <see cref="Begin"/>，为返回的每一项创建绝对定位的行，
/// 再对每一行调用 <see cref="Track"/>；<see cref="Controller"/> 交给滚动容器的节点。
/// 宿主在 <see cref="Mount"/> / <see cref="Unmount"/> 中订阅与取消布局完成事件。
/// </para>
/// </summary>
internal sealed class VirtualizerHost
{
    readonly Action _rebuild;
    readonly List<RowBinding> _rows = new();
    readonly List<VirtualItem> _items = new();
    readonly Virtualizer _virtualizer = new(0);
    readonly ScrollController _owned = new();
    ScrollController? _external;
    UIRoot? _root;
    double _pendingAdjustment;
    bool _active;

    /// <summary>创建宿主；<paramref name="rebuild"/> 为在布局阶段请求重建的回调（通常是 <c>InvalidateFromLayout</c>）。</summary>
    public VirtualizerHost(Action rebuild) => _rebuild = rebuild ?? throw new ArgumentNullException(nameof(rebuild));

    /// <summary>
    /// 滚动容器的控制器（偏移跨重建保留）。默认由宿主创建；<see cref="UseController"/> 指定外部控制器时使用它。
    /// </summary>
    public ScrollController Controller => _external ?? _owned;

    /// <summary>使用宿主提供的滚动控制器（例如组件上可配置的 <see cref="ScrollController"/> 属性），null 则用宿主自己的。</summary>
    public void UseController(ScrollController? controller) => _external = controller;

    public Virtualizer Virtualizer => _virtualizer;

    /// <summary>最近一次 <see cref="Begin"/> 时不带额外行的渲染范围。</summary>
    public VirtualRange Range { get; private set; }

    /// <summary>更新定高 / 估计尺寸 / 列数 / 间距 / 预渲染等选项；选项未变时不重建虚拟化器。</summary>
    public void SetOptions(VirtualizerOptions options)
    {
        if (options is null) throw new ArgumentNullException(nameof(options));
        var current = _virtualizer.Options;
        if (current.ItemSize != options.ItemSize || current.EstimatedItemSize != options.EstimatedItemSize
            || current.Overscan != options.Overscan || current.Lanes != options.Lanes || current.Gap != options.Gap
            || current.Horizontal != options.Horizontal || current.PaddingStart != options.PaddingStart
            || current.PaddingEnd != options.PaddingEnd || current.ScrollPaddingStart != options.ScrollPaddingStart
            || current.ScrollPaddingEnd != options.ScrollPaddingEnd || current.ScrollMargin != options.ScrollMargin
            || current.MaxRenderedItems != options.MaxRenderedItems
            || !ReferenceEquals(current.StickyIndices, options.StickyIndices))
            _virtualizer.SetOptions(options);
    }

    /// <summary>订阅布局完成；重复调用（根变化或重复挂载）是安全的。</summary>
    public void Mount(UIRoot? root)
    {
        if (ReferenceEquals(_root, root)) return;
        Unmount();
        _root = root;
        if (root is not null) root.LayoutCompleted += OnLayoutCompleted;
    }

    /// <summary>取消订阅并丢弃本帧的行与待施加的锚定调整。</summary>
    public void Unmount()
    {
        if (_root is not null) _root.LayoutCompleted -= OnLayoutCompleted;
        _root = null;
        Deactivate();
    }

    /// <summary>停止虚拟化（宿主不再响应布局完成）；滚动位置保留在控制器里。</summary>
    public void Deactivate()
    {
        _active = false;
        _rows.Clear();
        _items.Clear();
        _pendingAdjustment = 0;
    }

    /// <summary>
    /// 同步行数与视口，返回本次需要渲染的项（含预渲染，按索引升序）。
    /// <paramref name="includeIndex"/> 是不在范围内时也要实现的行（Tab 停靠 / 焦点行），-1 表示没有。
    /// 返回的列表在下次调用时复用。
    /// </summary>
    public IReadOnlyList<VirtualItem> Begin(int count, int includeIndex = -1)
    {
        _active = true;
        _virtualizer.SetCount(count);
        SyncViewport();
        Range = _virtualizer.GetRange();
        _rows.Clear();
        _items.Clear();
        foreach (var item in _virtualizer.GetVirtualItems()) _items.Add(item);
        if ((uint)includeIndex < (uint)count && !Contains(includeIndex))
        {
            _items.Add(_virtualizer.GetItem(includeIndex));
            _items.Sort(static (a, b) => a.Index.CompareTo(b.Index));
        }
        return _items;
    }

    /// <summary>登记本次构建实现的一行：变高模式下布局完成后读取它的实际高度。</summary>
    public void Track(int index, Ref<Element> row) => _rows.Add(new RowBinding(index, row));

    /// <summary>把滚动控制器的偏移与视口尺寸写入虚拟化器。</summary>
    public void SyncViewport()
    {
        bool horizontal = _virtualizer.Options.Horizontal;
        double along = horizontal ? Controller.OffsetX : Controller.OffsetY;
        double limit = horizontal ? Controller.MaxOffset.X : Controller.MaxOffset.Y;
        _virtualizer.ScrollOffset = Math.Max(0, Math.Min(along, limit));
        _virtualizer.ViewportSize = horizontal ? Controller.ViewportSize.Width : Controller.ViewportSize.Height;
    }

    void OnLayoutCompleted(UIRoot root)
    {
        if (!_active || !ReferenceEquals(_root, root)) return;
        // 变高：先按本帧的布局结果测量已实现的行；尺寸变化需要重建，重建后的下一轮再同步视口并施加锚定
        if (MeasureRows()) return;
        SyncViewport();
        var next = _virtualizer.GetRange();
        if (next.StartIndex != Range.StartIndex || next.EndIndex != Range.EndIndex
            || next.OverscanStartIndex != Range.OverscanStartIndex || next.OverscanEndIndex != Range.OverscanEndIndex)
        {
            _rebuild();
            return;
        }
        ApplyPendingAdjustment();
    }

    /// <summary>测量已实现的变尺寸项（沿主轴：水平模式取宽度）；任何一项的尺寸变化都请求重建并返回 true。</summary>
    bool MeasureRows()
    {
        if (_virtualizer.IsFixedSize) return false;
        int version = _virtualizer.Version;
        foreach (var binding in _rows)
        {
            if (binding.Row.Current is not { } row || !row.IsRendered) continue;
            double measured = _virtualizer.Options.Horizontal ? row.BoundingRect.Width : row.BoundingRect.Height;
            if (double.IsNaN(measured) || double.IsInfinity(measured) || measured < 0) continue;
            double adjustment = _virtualizer.Measure(binding.Index, measured);
            if (adjustment != 0) _pendingAdjustment += adjustment;
        }
        if (_virtualizer.Version == version) return false;
        _rebuild();
        return true;
    }

    void ApplyPendingAdjustment()
    {
        if (_pendingAdjustment == 0) return;
        double adjustment = _pendingAdjustment;
        _pendingAdjustment = 0;
        // 滚动内容（extent）已按新总尺寸布局，此时钳制不会截掉上方的测量变化
        VirtualizerScrolling.ApplyAdjustment(_virtualizer, Controller, adjustment);
    }

    bool Contains(int index)
    {
        foreach (var item in _items)
            if (item.Index == index) return true;
        return false;
    }

    readonly struct RowBinding
    {
        public RowBinding(int index, Ref<Element> row)
        {
            Index = index;
            Row = row;
        }

        public int Index { get; }

        public Ref<Element> Row { get; }
    }
}
