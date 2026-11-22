using System;
using System.Collections.Generic;
using AwAUI.Css.Values;
using AwAUI.Layout;
using AwAUI.Platform;
using AwAUI.UI;

namespace AwAUI.Controls.Primitives;

/// <summary>
/// 一个打开的菜单面（菜单栏、上下文菜单或子菜单）的项共享的状态。项只在所属面挂载期间存在；
/// 面的打开状态决定退场动画期间是否还接受输入。
/// </summary>
internal sealed class MenuLevel
{
    internal MenuLevel(MenuLevel? parent, ControllableState<bool>? surface, Ref<Element>? content, int zOrder)
    {
        Parent = parent;
        Surface = surface;
        Content = content;
        ZOrder = zOrder;
    }

    /// <summary>外层菜单面；菜单栏的根面为 null。</summary>
    internal MenuLevel? Parent { get; }

    /// <summary>本面的打开状态；菜单栏为 null（始终打开）。</summary>
    internal ControllableState<bool>? Surface { get; }

    /// <summary>本面的内容元素（初始焦点与键盘遍历）。</summary>
    internal Ref<Element>? Content { get; }

    internal int ZOrder { get; }

    /// <summary>关闭本面时不要恢复焦点（例如 Tab 移出、指针在外部按下）。</summary>
    internal bool SkipRestore { get; set; }

    internal TypeAhead Search { get; } = new();

    internal SubmenuGraceArea Grace { get; } = new();

    internal ControllableState<bool>? GraceOwner { get; set; }

    /// <summary>本面及其全部外层是否都打开。</summary>
    internal bool IsOpen => (Surface?.Value ?? true) && (Parent?.IsOpen ?? true);

    /// <summary>本面内容的书写方向（从菜单内容元素继承的 CSS <c>direction</c>）。</summary>
    internal Direction Direction =>
        Content?.Current?.ComputedStyle is { Direction: CssDirection.Rtl } ? Direction.Rtl : Direction.Ltr;

    /// <summary>关闭本面与外层的全部菜单面。</summary>
    internal void CloseChain(bool restoreFocus = true)
    {
        for (var level = this; level is not null; level = level.Parent)
        {
            if (!restoreFocus) level.SkipRestore = true;
            level.Surface?.Set(false);
        }
    }

    internal void ClearGrace(ControllableState<bool> submenu)
    {
        if (!ReferenceEquals(GraceOwner, submenu)) return;
        Grace.Clear();
        GraceOwner = null;
    }

    /// <summary>指针是否正沿着「离开子菜单时」的宽容区域移向它（Radix 的斜向移动宽容）。</summary>
    internal bool InPointerGrace(PointerEvent e)
    {
        Grace.TrackPointerMove(e.Position);
        if (GraceOwner is not { } submenu) return false;
        if (submenu.Value && Grace.IsPointerMovingToSubmenu(e.Position, e.Timestamp)) return true;
        ClearGrace(submenu);
        return false;
    }

    /// <summary>把焦点移到本面的第一项（没有可聚焦项时移到内容本身）。</summary>
    internal void FocusFirst(FocusOrigin origin) => FocusFirst(Content?.Current, origin);

    internal static void FocusFirst(Element? content, FocusOrigin origin)
    {
        if (content is null) return;
        var options = new FocusOptions { Origin = origin };
        foreach (var item in Items(content))
            if (item.Focus(options)) return;
        content.Focus(options);
    }

    /// <summary>把焦点移到本面的最后一项。</summary>
    internal void FocusLast(FocusOrigin origin)
    {
        if (Content?.Current is not { } content) return;
        var options = new FocusOptions { Origin = origin };
        var items = Items(content);
        for (int i = items.Count - 1; i >= 0; i--)
            if (items[i].Focus(options)) return;
        content.Focus(options);
    }

    /// <summary>
    /// 菜单项的键盘导航（Radix 的 <c>MenuContent</c>）：上下方向键、Home / End 与类型前缀查找；
    /// <paramref name="closeToTrigger"/> 非 null 时左方向键关闭子菜单并把焦点还给触发器。Tab 关闭整个菜单链。
    /// </summary>
    internal void HandleKeyDown(KeyEvent e, Action? closeToTrigger)
    {
        if (e.DefaultPrevented || !IsOpen || Content?.Current is not { } menu) return;
        const KeyModifiers command = KeyModifiers.Alt | KeyModifiers.Control | KeyModifiers.Meta;
        if (e.Key == "Tab" && (e.Modifiers & command) == 0)
        {
            // 焦点仍由 Tab 顺序移动，只关闭菜单链（与 Radix 相同）。
            CloseChain(restoreFocus: false);
            return;
        }
        if (closeToTrigger is not null && e.Modifiers == KeyModifiers.None
            && e.Key == (Direction == Direction.Rtl ? "ArrowRight" : "ArrowLeft"))
        {
            e.PreventDefault();
            closeToTrigger();
            return;
        }

        var items = Items(menu);
        if (items.Count == 0) return;
        int current = menu.Root?.FocusManager.FocusedElement is { } focused ? items.IndexOf(focused) : -1;
        var intent = RovingFocus.GetFocusIntent(e.Key, Orientation.Vertical, Direction);
        int next = -1;
        if (intent != FocusIntent.None && e.Modifiers == KeyModifiers.None)
        {
            e.PreventDefault();
            next = RovingFocus.Navigate(intent, current, items.Count, i => items[i].IsFocusable, loop: true);
        }
        else if (TypeAhead.IsTypeAheadKey(e.Key, e.Modifiers) && (e.Key != " " || Search.IsTypingAhead(e.Timestamp)))
        {
            next = Search.HandleKey(e.Key, e.Timestamp, items.ConvertAll(MenuText), current, i => items[i].IsFocusable);
            if (next >= 0 || e.Key == " ") e.PreventDefault();
        }
        if (next >= 0) items[next].Focus(new FocusOptions { Origin = FocusOrigin.Keyboard });
    }

    /// <summary>类型前缀查找用的文本：显式的 <c>data-menu-text-value</c>，否则可访问名称，最后是渲染文本。</summary>
    internal static string? MenuText(Element item) =>
        (item.GetAttribute("data-menu-text-value") ?? item.GetAttribute("aria-label") ?? item.TextContent).Trim();

    /// <summary>
    /// 收集一个菜单面里的项元素（跳过嵌套的子菜单内容）：角色为 menu item 且可见的元素。
    /// 只有可聚焦的项参与导航。
    /// </summary>
    internal static List<Element> Items(Element menu)
    {
        var items = new List<Element>();
        Visit(menu);
        return items;

        void Visit(Element parent)
        {
            foreach (var child in parent.ElementChildren)
            {
                if (child.Role == Role.Menu || child.GetAttribute("role") == "menu") continue;
                if (child.Role == Role.MenuItem || child.GetAttribute("role") is "menuitemcheckbox" or "menuitemradio")
                {
                    if (child.IsVisible) items.Add(child);
                    continue;
                }
                Visit(child);
            }
        }
    }
}

/// <summary>
/// 菜单栏共享的状态：顶层的漫游焦点（一组顶层项只占一个 Tab 位）与当前打开的顶层项。
/// </summary>
internal sealed class MenuBarContext
{
    /// <summary>最近一次聚焦或打开的顶层项下标；-1 表示还没有项获得过焦点。</summary>
    internal readonly State<int> ActiveIndex = new(-1);

    /// <summary>当前打开的顶层菜单项（同时最多一个）。</summary>
    internal MenuItem? OpenItem;

    /// <summary>打开另一个顶层项时先关闭当前打开的那个。</summary>
    internal void Open(MenuItem item)
    {
        if (ReferenceEquals(OpenItem, item)) return;
        var previous = OpenItem;
        OpenItem = item;
        previous?.CloseSubMenu();
    }

    internal void Closed(MenuItem item)
    {
        if (ReferenceEquals(OpenItem, item)) OpenItem = null;
    }
}
