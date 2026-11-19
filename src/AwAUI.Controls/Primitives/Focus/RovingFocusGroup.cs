using System;
using AwAUI.Platform;

namespace AwAUI.Controls.Primitives;

/// <summary>
/// 漫游焦点组的状态（Radix <c>RovingFocusGroup</c> 的 <c>currentTabStopId</c>）：组内只有一项的 <c>tabindex</c> 为 0（当前 Tab 位），
/// 其余为 -1。当前 Tab 位无效（未设置、已注销或已禁用）时退回第一个可聚焦项。
/// </summary>
public sealed class RovingFocusGroup<TKey> where TKey : notnull
{
    TKey? _current;
    bool _hasCurrent;

    public RovingFocusGroup(ItemCollection<TKey> items, RovingFocusOptions? options = null)
    {
        Items = items ?? throw new ArgumentNullException(nameof(items));
        Options = options ?? RovingFocusOptions.Default;
    }

    public ItemCollection<TKey> Items { get; }

    public RovingFocusOptions Options { get; set; }

    /// <summary>当前 Tab 位（有效时），否则为第一个可聚焦项；组内没有可聚焦项时返回 false。</summary>
    public bool TryGetTabStop(out TKey key)
    {
        if (_hasCurrent && Items.TryGet(_current!, out var item) && !item.Disabled)
        {
            key = _current!;
            return true;
        }
        foreach (var it in Items.EnabledItems)
        {
            key = it.Key;
            return true;
        }
        key = default!;
        return false;
    }

    /// <summary>项的 <c>tabindex</c>：当前 Tab 位为 0，其余为 -1。</summary>
    public int GetTabIndex(TKey key)
    {
        int index = Items.IndexOf(key);
        if (index < 0) return -1;
        return TryGetTabStop(out var stop) && Items.IndexOf(stop) == index ? 0 : -1;
    }

    /// <summary>某项获得焦点（指针或键盘）：它成为当前 Tab 位（Radix 的 <c>onItemFocus</c>）。</summary>
    public void OnItemFocused(TKey key)
    {
        _current = key;
        _hasCurrent = true;
    }

    /// <summary>
    /// 处理焦点项上的按键：返回应聚焦的项（并把它设为当前 Tab 位）；不处理的键返回 false。
    /// 返回 true 时调用方应 <c>PreventDefault</c>。
    /// </summary>
    public bool HandleKeyDown(string? key, KeyModifiers modifiers, TKey focusedKey, out TKey target)
    {
        target = default!;
        int current = Items.IndexOf(focusedKey);
        int index = RovingFocus.HandleKeyDown(key, modifiers, current, Items.Count, i => !Items[i].Disabled, Options);
        if (index < 0) return false;
        target = Items[index].Key;
        OnItemFocused(target);
        return true;
    }
}
