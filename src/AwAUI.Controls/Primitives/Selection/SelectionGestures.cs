using System;
using AwAUI.Platform;
using AwAUI.UI;

namespace AwAUI.Controls.Primitives;

/// <summary>
/// 列表类控件的指针与键盘选择约定（Avalonia <c>SelectingItemsControl.UpdateSelection</c> 与 ListBox 的键盘处理）：
/// <list type="bullet">
/// <item>单击：只选中该项并设为锚点；<see cref="SelectionMode.Toggle"/> 下切换该项。</item>
/// <item>主修饰键（Windows / Linux 为 Ctrl，macOS 为 ⌘）+ 单击：多选时切换该项（并设为锚点）；单选时选中 / 取消该项。</item>
/// <item>Shift + 单击：多选时选中从锚点到该项的范围（替换原选择）；Shift + 主修饰键：把范围加入原选择。</item>
/// <item>右键：该项未选中时只选中它（保留现有多选以便上下文菜单作用于全部选中项）。</item>
/// <item>方向键移动焦点并选中；按住 Shift 从锚点扩展范围；按住主修饰键只移动焦点；主修饰键 + 空格切换焦点项；主修饰键 + A 全选。</item>
/// </list>
/// <see cref="SelectionMode.AlwaysSelected"/> 时，交互不会取消最后一个选中项。
/// 每次交互都按 <see cref="SelectionMode"/> 同步模型的 <see cref="SelectionModel{T}.SingleSelect"/>（与 Avalonia 的 SelectingItemsControl 相同，模式由控件拥有）。
/// </summary>
public static class SelectionGestures
{
    /// <summary>指针在 <paramref name="index"/> 上按下（按平台约定解释修饰键）。</summary>
    public static void PointerPressed<T>(SelectionModel<T> model, int index, KeyModifiers modifiers, SelectionMode mode, bool rightButton = false) =>
        Update(model, index, mode,
            range: (modifiers & KeyModifiers.Shift) != 0,
            toggle: Shortcuts.HasPrimaryModifier(modifiers),
            rightButton: rightButton, fromFocus: false);

    /// <summary>键盘导航把焦点移到了 <paramref name="index"/>。</summary>
    public static void FocusMoved<T>(SelectionModel<T> model, int index, KeyModifiers modifiers, SelectionMode mode) =>
        Update(model, index, mode,
            range: (modifiers & KeyModifiers.Shift) != 0,
            toggle: Shortcuts.HasPrimaryModifier(modifiers),
            rightButton: false, fromFocus: true);

    /// <summary>
    /// 核心规则。<paramref name="range"/> / <paramref name="toggle"/> 是已解释好的修饰键（范围键、切换键）；
    /// <paramref name="fromFocus"/> 表示由键盘焦点移动触发（此时切换键只移动焦点、不改变选择）。
    /// </summary>
    public static void Update<T>(SelectionModel<T> model, int index, SelectionMode mode, bool range, bool toggle, bool rightButton, bool fromFocus)
    {
        if (model is null) throw new ArgumentNullException(nameof(model));
        if (index < 0 || (model.Source is not null && index >= model.ItemCount)) return;
        bool multi = (mode & SelectionMode.Multiple) != 0;
        bool alwaysSelected = (mode & SelectionMode.AlwaysSelected) != 0;
        bool toggleMode = toggle || (mode & SelectionMode.Toggle) != 0;
        model.SingleSelect = !multi;

        if (rightButton)
        {
            if (!model.IsSelected(index)) model.SelectedIndex = index;
            return;
        }

        if (multi && range)
        {
            int anchor = model.AnchorIndex >= 0 ? model.AnchorIndex : index;
            using (model.BatchUpdate())
            {
                if (!toggle || fromFocus) model.Clear();
                model.SelectRange(anchor, index);
                model.AnchorIndex = anchor;
            }
            return;
        }

        if (fromFocus && toggle)
        {
            // 主修饰键 + 方向键：只移动焦点
            return;
        }

        if (toggleMode && !fromFocus)
        {
            if (multi)
            {
                if (model.IsSelected(index))
                {
                    if (alwaysSelected && model.Count == 1) return;
                    model.Deselect(index);
                    model.AnchorIndex = index;
                }
                else
                {
                    model.Select(index);
                }
            }
            else
            {
                if (model.SelectedIndex == index)
                {
                    if (!alwaysSelected) model.SelectedIndex = -1;
                }
                else
                {
                    model.SelectedIndex = index;
                }
            }
            return;
        }

        using (model.BatchUpdate())
        {
            model.Clear();
            model.Select(index);
        }
    }

    /// <summary>
    /// 处理列表上的按键。导航键（方向键、Home / End、PageUp / PageDown）按 <paramref name="navigation"/> 求出新焦点并更新选择；
    /// 主修饰键 + 空格切换焦点项，主修饰键 + A 全选（多选）；空格选中焦点项。
    /// 返回是否处理了该键（调用方随之 <c>PreventDefault</c>），<paramref name="newFocusIndex"/> 为新的焦点项（不变时等于原值）。
    /// </summary>
    public static bool HandleKeyDown<T>(SelectionModel<T> model, string? key, KeyModifiers modifiers, SelectionMode mode,
        int focusedIndex, int itemCount, Func<int, bool>? isEnabled, RovingFocusOptions? navigation, out int newFocusIndex)
    {
        if (model is null) throw new ArgumentNullException(nameof(model));
        newFocusIndex = focusedIndex;
        navigation ??= RovingFocusOptions.Default;
        bool primary = Shortcuts.HasPrimaryModifier(modifiers);
        bool multi = (mode & SelectionMode.Multiple) != 0;
        if (primary && (key == "a" || key == "A") && (modifiers & (KeyModifiers.Shift | KeyModifiers.Alt)) == 0)
        {
            if (!multi) return false;
            model.SingleSelect = false;
            model.SelectAll();
            return true;
        }
        if (key == " ")
        {
            if (focusedIndex < 0 || focusedIndex >= itemCount || (isEnabled is not null && !isEnabled(focusedIndex))) return false;
            Update(model, focusedIndex, mode, range: (modifiers & KeyModifiers.Shift) != 0, toggle: primary, rightButton: false, fromFocus: false);
            return true;
        }
        var intent = RovingFocus.GetFocusIntent(key, navigation.Orientation, navigation.Direction, navigation.PageSize);
        if (intent == FocusIntent.None) return false;
        if ((modifiers & (KeyModifiers.Alt | KeyModifiers.Meta | KeyModifiers.Control) & ~Shortcuts.PrimaryModifier) != 0) return false;
        int target = RovingFocus.Navigate(intent, focusedIndex, itemCount, isEnabled, navigation.Loop, navigation.PageSize);
        if (target < 0) return true;
        newFocusIndex = target;
        FocusMoved(model, target, modifiers, mode);
        return true;
    }
}
