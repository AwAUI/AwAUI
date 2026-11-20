using System;
using System.Collections;
using System.Collections.Specialized;
using System.Globalization;
using AwAUI.Controls.Primitives;
using AwAUI.UI;

namespace AwAUI.Controls;

/// <summary>
/// A vertical list that creates elements only for the visible fixed-height rows and their overscan.
/// It reads items by index, without copying the source or enumerating offscreen items.
/// </summary>
public sealed partial class ItemsRepeater : Control
{
    readonly ScrollController _controller = new();
    Virtualizer? _virtualizer;
    VirtualRange _renderedRange;
    UIRoot? _layoutRoot;

    /// <summary>Assign a new source or raise collection change notifications on the UI thread to refresh the list.</summary>
    public IList? ItemsSource { get; set; }

    /// <summary>Creates the content of one realized row. Defaults to presenting a node or formatted text.</summary>
    public Func<object?, Node>? ItemTemplate { get; set; }

    /// <summary>Optional unique, stable keys for keeping row identity across collection changes. The default key is the row index.</summary>
    public Func<object?, object>? ItemKey { get; set; }

    /// <summary>Height of every row in logical pixels. Row content is clipped to these bounds.</summary>
    public double ItemHeight { get; set; } = 32;

    /// <summary>Number of rows mounted just outside each edge of the viewport.</summary>
    public int Overscan { get; set; } = 2;

    /// <summary>Optional controller for scrolling to content outside the currently realized range.</summary>
    public ScrollController? ScrollController { get; set; }

    ScrollController Controller => ScrollController ?? _controller;

    protected override Node Build()
    {
        var source = ItemsSource;
        var controller = Controller;
        double height = ItemHeight > 0 && !double.IsInfinity(ItemHeight) ? ItemHeight : 32;
        int overscan = Math.Max(0, Overscan);
        var virtualizer = _virtualizer ??= new Virtualizer(0);
        if (virtualizer.Options.ItemSize != height || virtualizer.Options.Overscan != overscan)
            virtualizer.SetOptions(new VirtualizerOptions { ItemSize = height, Overscan = overscan });
        int count = source?.Count ?? 0;
        virtualizer.SetCount(count);
        ReadViewport(virtualizer, controller);
        _renderedRange = virtualizer.GetRange();

        UseEffect(() =>
        {
            if (source is not INotifyCollectionChanged observable) return null;
            NotifyCollectionChangedEventHandler handler = (_, _) => Invalidate();
            observable.CollectionChanged += handler;
            return () => observable.CollectionChanged -= handler;
        }, source);

        var rows = new Div { Style = "position:relative;height:" + Pixels(virtualizer.TotalSize) };
        rows.Attributes["part"] = "extent";
        if (source is not null)
        {
            string setSize = count.ToString(CultureInfo.InvariantCulture);
            string rowStyle = "position:absolute;inset-inline:0;box-sizing:border-box;overflow:clip;height:" + Pixels(height) + ";top:";
            foreach (var item in virtualizer.GetVirtualItems())
            {
                var value = source[item.Index];
                var row = new Div
                {
                    Key = ItemKey?.Invoke(value) ?? item.Index,
                    Role = Role.ListItem,
                    Style = rowStyle + Pixels(item.Start),
                    Children = [ItemTemplate?.Invoke(value) ?? Node.From(value)],
                };
                row.Attributes["part"] = "item";
                row.Attributes["aria-posinset"] = (item.Index + 1).ToString(CultureInfo.InvariantCulture);
                row.Attributes["aria-setsize"] = setSize;
                rows.Children.Add(row);
            }
        }

        return Apply(new Div
        {
            Role = Role.List,
            TabIndex = 0,
            ScrollController = controller,
            Style = "overflow-x:hidden;overflow-y:auto;max-height:240px;",
            Children = [rows],
        }, "aw-items-repeater");
    }

    protected override void OnMount()
    {
        _layoutRoot = Root;
        if (_layoutRoot is not null) _layoutRoot.LayoutCompleted += LayoutCompleted;
    }

    protected override void OnUnmount()
    {
        if (_layoutRoot is not null) _layoutRoot.LayoutCompleted -= LayoutCompleted;
        _layoutRoot = null;
    }

    void LayoutCompleted(UIRoot _)
    {
        if (_virtualizer is not { } virtualizer) return;
        ReadViewport(virtualizer, Controller);
        var next = virtualizer.GetRange();
        if (next.StartIndex != _renderedRange.StartIndex || next.EndIndex != _renderedRange.EndIndex
            || next.OverscanStartIndex != _renderedRange.OverscanStartIndex || next.OverscanEndIndex != _renderedRange.OverscanEndIndex)
            InvalidateFromLayout();
    }

    static void ReadViewport(Virtualizer virtualizer, ScrollController controller)
    {
        virtualizer.ScrollOffset = Math.Max(0, Math.Min(controller.Offset.Y, controller.MaxOffset.Y));
        virtualizer.ViewportSize = controller.ViewportSize.Height;
    }

    static string Pixels(double value) => value.ToString("0.#########", CultureInfo.InvariantCulture) + "px";
}
