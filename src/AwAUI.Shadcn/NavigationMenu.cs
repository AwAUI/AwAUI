using System;
using System.Collections.Generic;
using System.Globalization;
using AwAUI.Controls.Primitives;
using AwAUI.Icons.Lucide;
using AwAUI.Layout;
using AwAUI.Platform;
using AwAUI.UI;
using PopoverPrimitive = AwAUI.Controls.Primitives.Popover;
using UIButton = AwAUI.UI.Button;

namespace AwAUI.Shadcn;

/// <summary>
/// A navigation menu whose top-level triggers open anchored panels. The open panel is rendered inside one shared
/// viewport element (<c>data-slot="navigation-menu-viewport"</c>) that is positioned under the active trigger.
/// <para>
/// The open item is a controlled value: <see cref="Value"/> / <see cref="DefaultValue"/> with
/// <see cref="ValueChanged"/> / <see cref="OnValueChange"/>. Opening a trigger sets the value to the item's
/// <see cref="NavigationMenuItem.Value"/>; Escape, an outside pointer press or an explicit close clears it to null.
/// </para>
/// </summary>
public sealed partial class NavigationMenu : Component
{
    readonly NavigationMenuState _state = new();
    readonly ControllableState<string?> _value = new(StringComparer.Ordinal);
    readonly Ref<Element> _nav = new();

    /// <summary>受控的打开项（<see cref="NavigationMenuItem.Value"/>）；为 null 时使用非受控状态。</summary>
    public string? Value { get; set; }

    /// <summary>受控模式下 <see cref="Value"/> 为 null（菜单关闭）也是有效状态；默认按 <see cref="Value"/> 是否为 null 推断。</summary>
    public bool IsValueControlled { get; set; }

    /// <summary>非受控模式的初始打开项。</summary>
    public string? DefaultValue { get; set; }

    /// <summary>打开项变化时调用（受控与非受控都会触发）。</summary>
    public Action<string?>? ValueChanged { get; set; }

    /// <summary><see cref="ValueChanged"/> 的别名（Radix / shadcn 命名）。</summary>
    public Action<string?>? OnValueChange { get; set; }

    public int DelayDuration { get; set; } = 200;
    public int CloseDelayDuration { get; set; } = 150;

    /// <summary>指针离开后再进入另一个触发器时，在此毫秒内的移动不再等待 <see cref="DelayDuration"/>。</summary>
    public int SkipDelayDuration { get; set; } = 300;

    public Direction? Dir { get; set; }
    public string? Class { get; set; }
    public Ref<Element>? Ref { get; set; }
    public NodeList? Children { get; set; }
    [CaptureUnmatchedAttributes] public AttributeDictionary? Rest { get; set; }

    protected override Node Build()
    {
        // 受控 / 非受控统一经 ControllableState：受控时只读取属性，不在 Build 中写 State（否则会报 AW1005）。
        var value = _value.Sync(IsValueControlled || Value is not null, Value, DefaultValue, ValueChanged + OnValueChange);
        _state.NoteOpen(value);
        // 项在挂载效果中登记；读取版本号让根在登记 / 卸载后重建视口。
        _ = _state.Version.Value;

        var navRef = Ref ?? _nav;
        var scope = new Scope(_state, _value, navRef, value, DelayDuration, CloseDelayDuration, SkipDelayDuration);
        var nav = new NavigationTag("nav") { Role = Role.Navigation, Ref = navRef, Children = new NodeList(Children) };
        TabsParts.Style(nav, "navigation-menu",
            "group/navigation-menu relative flex max-w-max flex-1 items-center justify-center", Class, Rest);
        TabsParts.DirectionAttribute(nav, Dir);
        // 视口始终启用（内容在唯一的 NavigationMenuViewport 中渲染），与官方 data-viewport 的取值一致。
        nav.Attributes["data-viewport"] = "true";
        nav.Children.Add(new NavigationMenuViewport());
        return Provide(scope, nav);
    }

    /// <summary>
    /// 每次 Build 生成一个新的 Scope 交给上下文：读取者订阅的是 Scope 对象，受控值变化（属性）与非受控值变化
    /// （ControllableState 内部 State）都会让根重建并换发新 Scope，从而重建项与视口。
    /// </summary>
    internal sealed class Scope
    {
        internal Scope(NavigationMenuState state, ControllableState<string?> value, Ref<Element> nav, string? openValue,
            int openDelay, int closeDelay, int skipDelay)
        {
            State = state;
            Value = value;
            Nav = nav;
            OpenValue = openValue;
            OpenDelay = openDelay;
            CloseDelay = closeDelay;
            SkipDelay = skipDelay;
        }

        internal readonly NavigationMenuState State;
        internal readonly ControllableState<string?> Value;
        internal readonly Ref<Element> Nav;
        internal readonly string? OpenValue;
        internal readonly int OpenDelay;
        internal readonly int CloseDelay;
        internal readonly int SkipDelay;

        internal NavigationMenuItem? OpenItem => State.FindItem(OpenValue);
        internal NavigationMenuItem? FindItem(string? value) => State.FindItem(value);

        internal void SetOpen(NavigationMenuItem item, bool open)
        {
            var value = open ? item.ItemKey : null;
            if (string.Equals(OpenValue, value, StringComparison.Ordinal)) return;
            // 同一次交互可能重复请求同一个值（悬停立即打开 + 点击打开）；受控模式下父级尚未写回时去重，
            // 避免 ValueChanged 收到两次相同的请求。
            if (!State.TryRequest(value)) return;
            // 受控：只报告请求，父级写回 Value 后由下一次 Build 生效；非受控：写内部 State 触发根重建。
            Value.Set(value);
        }
    }
}

internal sealed class NavigationMenuState
{
    readonly List<NavigationMenuItem> _items = new();

    internal readonly State<int> ActiveIndex = new(-1);
    internal readonly State<int> Version = new(0);
    internal readonly TypeAhead Search = new();
    internal readonly string ViewportId = TabsParts.NextId("navigation-menu-viewport");

    /// <summary>上一次 Build 的打开项；只用于计算 <see cref="PreviousIndex"/>（普通字段，Build 中写入不触发 AW1005）。</summary>
    internal string? LastOpenValue;
    internal int PreviousIndex = -1;
    internal string? LastRequestedValue;

    /// <summary>记录本次打开项，并在切换时记住上一项的位置（供面板的 data-motion 方向使用）。</summary>
    internal void NoteOpen(string? value)
    {
        if (string.Equals(LastOpenValue, value, StringComparison.Ordinal)) return;
        if (FindItem(LastOpenValue) is { } previous) PreviousIndex = previous.Index;
        LastOpenValue = value;
        LastRequestedValue = value;
    }

    /// <summary>记录一次打开 / 关闭请求；与上一次请求相同（父级尚未写回受控值时）返回 false。</summary>
    internal bool TryRequest(string? value)
    {
        if (string.Equals(LastRequestedValue, value, StringComparison.Ordinal)) return false;
        LastRequestedValue = value;
        return true;
    }

    internal void Register(NavigationMenuItem item)
    {
        if (!_items.Contains(item)) _items.Add(item);
        Bump();
    }

    internal void Unregister(NavigationMenuItem item)
    {
        if (_items.Remove(item)) Bump();
    }

    internal List<NavigationMenuItem> Items()
    {
        var ordered = new List<NavigationMenuItem>(_items);
        ordered.Sort(static (a, b) => a.Index.CompareTo(b.Index));
        return ordered;
    }

    internal NavigationMenuItem? FindItem(string? value)
    {
        if (value is null) return null;
        foreach (var item in _items)
            if (string.Equals(item.ItemKey, value, StringComparison.Ordinal)) return item;
        return null;
    }

    internal void Bump() => Version.Value = Version.Peek() + 1;
}

internal sealed partial class NavigationMenuItemState
{
    internal readonly string TriggerId = TabsParts.NextId("navigation-menu-trigger");
    internal readonly string ContentId = TabsParts.NextId("navigation-menu-content");
    internal readonly Ref<Element> Trigger = new();
    internal readonly Ref<Element> Content = new();
    internal readonly TooltipInteraction Hover = new();
    internal OpenableShared? Popup;
    internal NavigationMenuItem? Owner;
    internal bool FocusOnOpen;
    /// <summary>Set while focus returns to the trigger after a dismissal so the focus does not reopen the panel.</summary>
    internal bool SuppressFocusOpen;
}

public sealed partial class NavigationMenuList : Component
{
    NavigationMenu.Scope? _root;

    public string? Class { get; set; }
    public NodeList? Children { get; set; }
    [CaptureUnmatchedAttributes] public AttributeDictionary? Rest { get; set; }

    protected override Node Build()
    {
        var children = Children ?? new NodeList();
        int index = 0;
        foreach (var node in children)
            if (node is NavigationMenuItem item) item.Index = index++;

        _root = Use<NavigationMenu.Scope>();
        var list = new NavigationTag("ul") { Role = Role.List, Children = new NodeList(children) };
        TabsParts.Style(list, "navigation-menu-list",
            "group flex flex-1 list-none items-center justify-center gap-1", Class, Rest);
        list.OnKeyDown = e => OnListKeyDown(e, list);
        return list;
    }

    void OnListKeyDown(KeyEvent e, ElementNode list)
    {
        if (e.DefaultPrevented || e.Modifiers != KeyModifiers.None || _root is null) return;
        var state = _root.State;
        if (state.Items().Count == 0) return;
        var focused = Root?.FocusManager.FocusedElement;
        if (focused is null) return;
        var items = state.Items();
        int current = items.FindIndex(i => i.TriggerElement is { } trigger && trigger.IsInclusiveAncestorOf(focused));
        if (current < 0) return;

        var direction = TabsParts.ResolveDirection(e.CurrentTarget, null);
        var intent = RovingFocus.GetFocusIntent(e.Key, Orientation.Horizontal, direction);
        if (intent != FocusIntent.None)
        {
            e.PreventDefault();
            int next = RovingFocus.Navigate(intent, current, items.Count, i => items[i].CanFocus, loop: false);
            if (next < 0) return;
            state.ActiveIndex.Value = items[next].Index;
            items[next].FocusTrigger(FocusOrigin.Keyboard);
            return;
        }

        if (TypeAhead.IsTypeAheadKey(e.Key, e.Modifiers))
        {
            var labels = new string?[items.Count];
            for (int i = 0; i < items.Count; i++) labels[i] = items[i].TextValue;
            int next = state.Search.HandleKey(e.Key, e.Timestamp, labels, current, i => items[i].CanFocus);
            if (next < 0) return;
            e.PreventDefault();
            state.ActiveIndex.Value = items[next].Index;
            items[next].FocusTrigger(FocusOrigin.Keyboard);
        }
    }
}

public sealed partial class NavigationMenuItem : Component
{
    internal int Index;
    internal readonly NavigationMenuItemState State = new();

    /// <summary>受控值：打开该项时 <see cref="NavigationMenu.Value"/> 变为这个字符串。</summary>
    public string? Value { get; set; }

    public string? Class { get; set; }
    public NodeList? Children { get; set; }
    [CaptureUnmatchedAttributes] public AttributeDictionary? Rest { get; set; }

    internal Element? TriggerElement => State.Trigger.Current;
    internal bool CanFocus => TriggerElement is { IsFocusable: true, IsVisible: true };
    internal string? TextValue => TriggerElement?.TextContent.Trim();

    /// <summary>该项在根 <see cref="NavigationMenuState.OpenValue"/> 中使用的稳定键。</summary>
    internal string ItemKey => Value ?? State.TriggerId;

    protected override Node Build()
    {
        var root = Use<NavigationMenu.Scope>();
        State.Owner = this;

        // 内容由根中唯一的视口渲染（见 NavigationMenuViewport）；这里只保留触发器及其它子节点。
        var item = new NavigationTag("li") { Role = Role.ListItem };
        TabsParts.Style(item, "navigation-menu-item", "relative", Class, Rest);
        foreach (var child in Children ?? new NodeList())
            if (child is not NavigationMenuContent) item.Children.Add(child);

        UseEffect(() =>
        {
            root.State.Register(this);
            return () => root.State.Unregister(this);
        }, root.State);

        bool open = string.Equals(root.OpenValue, ItemKey, StringComparison.Ordinal);
        return Provide(new ItemScope(State, root), new PopoverPrimitive.Root
        {
            Open = open,
            OnOpenChange = value => root.SetOpen(this, value),
            Children = [item],
        });
    }

    internal void CloseMenu() => State.Popup?.SetOpen(false);

    internal bool FocusTrigger(FocusOrigin origin) =>
        TriggerElement is { IsFocusable: true, IsVisible: true } trigger
        && trigger.Focus(new FocusOptions { Origin = origin, PreventScroll = true });

    /// <param name="Exiting">
    /// True while the viewport keeps this item's content mounted only to run the popup-out animation after the menu
    /// closed. The content then renders <c>data-state="closed"</c> so <see cref="PopupMotion.Classes"/> starts the
    /// exit animation.
    /// </param>
    internal sealed record ItemScope(NavigationMenuItemState State, NavigationMenu.Scope Root, bool Exiting = false);
}

public sealed partial class NavigationMenuTrigger : Component
{
    const string TriggerClass =
        "group inline-flex h-9 w-max items-center justify-center rounded-md bg-background px-4 py-2 text-sm font-medium " +
        "transition-[color,box-shadow] outline-none hover:bg-accent hover:text-accent-foreground focus:bg-accent " +
        "focus:text-accent-foreground focus-visible:ring-[3px] focus-visible:ring-ring/50 focus-visible:outline-1 " +
        "disabled:pointer-events-none disabled:opacity-50 data-[state=open]:bg-accent/50 data-[state=open]:text-accent-foreground " +
        "data-[state=open]:hover:bg-accent data-[state=open]:focus:bg-accent";

    public bool Disabled { get; set; }
    public string? Class { get; set; }
    public Ref<Element>? Ref { get; set; }
    public NodeList? Children { get; set; }
    [CaptureUnmatchedAttributes] public AttributeDictionary? Rest { get; set; }

    NavigationMenuItem.ItemScope? _item;

    protected override Node Build()
    {
        var root = Use<NavigationMenu.Scope>();
        var item = _item = Use<NavigationMenuItem.ItemScope>();
        var popup = Use<PopoverPrimitive.Context>();
        item.State.Popup ??= popup.Shared;
        var index = item.State.Owner?.Index ?? 0;

        // 每次 Build 都会追加箭头，必须拷贝子节点列表；直接复用 Children 会在每次重渲染时多出一个箭头。
        var button = new UIButton { Ref = Ref ?? item.State.Trigger, Disabled = Disabled, Children = new NodeList(Children) };
        button.Children.Add(SelectParts.Glyph(LucideIcons.ChevronDown, "navigation-menu-trigger-icon",
            "relative top-[1px] ml-1 size-3 motion-safe:transition-transform duration-300 group-data-[state=open]:rotate-180"));
        TabsParts.Style(button, "navigation-menu-trigger", TriggerClass, Class, Rest);
        button.Id = item.State.TriggerId;
        button.Attributes["type"] = "button";
        button.Attributes["aria-expanded"] = TabsParts.Bool(popup.Open);
        // 共享视口始终存在，是该项内容的 ARIA 容器。
        button.Attributes["aria-controls"] = root.State.ViewportId;
        button.Attributes["data-state"] = TabsParts.OpenState(popup.Open);
        button.TabIndex = root.State.ActiveIndex.Value == index || (root.State.ActiveIndex.Value < 0 && index == 0) ? 0 : -1;
        button.OnClick = e =>
        {
            if (e.DefaultPrevented || button.Disabled) return;
            e.PreventDefault();
            item.State.Hover.CancelPending();
            if (popup.Open) popup.Shared.SetOpen(false);
            else
            {
                item.State.FocusOnOpen = false;
                popup.Shared.SetOpen(true);
            }
        };
        button.OnKeyDown = e =>
        {
            if (button.Disabled || e.Modifiers != KeyModifiers.None
                || !ReferenceEquals(e.Target, item.State.Trigger.Current)) return;
            if (e.Key == "Escape" && popup.Open)
            {
                e.PreventDefault();
                item.State.Hover.CancelPending();
                popup.Shared.SetOpen(false);
                return;
            }
            if (e.DefaultPrevented) return;
            if (e.Key == "ArrowDown")
            {
                e.PreventDefault();
                if (popup.Open) NavigationMenuParts.FocusFirst(item.State.Content.Current, FocusOrigin.Keyboard, last: false);
                else
                {
                    item.State.FocusOnOpen = true;
                    popup.Shared.SetOpen(true);
                }
            }
        };
        button.OnPointerEnter = e =>
        {
            if (e.DefaultPrevented || e.PointerType != PointerType.Mouse || e.Buttons != PointerButtons.None || button.Disabled) return;
            int delay = root.OpenItem is { } openItem && !ReferenceEquals(openItem, item.State.Owner)
                ? SkipDelay(root)
                : root.OpenDelay;
            item.State.FocusOnOpen = false;
            item.State.Hover.SetHover(popup.Shared, Root?.Clock, true, false, delay, root.CloseDelay);
        };
        button.OnPointerLeave = e =>
        {
            if (e.PointerType != PointerType.Mouse) return;
            item.State.Hover.SetHover(popup.Shared, Root?.Clock, false, false, root.OpenDelay, root.CloseDelay);
        };
        button.OnFocusIn = e =>
        {
            if (item.State.SuppressFocusOpen) { item.State.SuppressFocusOpen = false; return; }
            if (e.DefaultPrevented || e.Origin == FocusOrigin.Pointer) return;
            root.State.ActiveIndex.Value = index;
            item.State.FocusOnOpen = false;
            item.State.Hover.SetFocus(popup.Shared, true);
        };
        button.OnFocusOut = e =>
        {
            if (e.CurrentTarget?.IsInclusiveAncestorOf(e.RelatedTarget) == true) return;
            // 焦点移到已经打开的面板里不算离开（否则按 ArrowDown 进入链接时会立刻关闭）。
            if (item.State.Content.Current is { } content && content.IsInclusiveAncestorOf(e.RelatedTarget)) return;
            item.State.Hover.SetFocus(popup.Shared, false);
        };
        return new PopoverPrimitive.Anchor { AsChild = true, Children = [button] };
    }

    /// <summary>另一个触发器已经打开时，短时间内移入不再等待打开延迟。</summary>
    static int SkipDelay(NavigationMenu.Scope root) => root.SkipDelay > 0 ? 0 : root.OpenDelay;

    protected override void OnUnmount() => _item?.State.Hover.Reset();
}

/// <summary>
/// The single shared viewport: always rendered (exactly one per <see cref="NavigationMenu"/>) directly under the root
/// element, and holding the active item's content. It is positioned under the active trigger by measuring layout.
/// </summary>
internal sealed partial class NavigationMenuViewport : Component
{
    const string ViewportClass =
        "absolute left-0 top-0 z-50 overflow-hidden rounded-md border bg-popover text-popover-foreground shadow-lg";

    // Matches --animate-aw-popup-out (the exit animation on the content) in Shadcn's stylesheet.
    const int ExitDurationMs = 150;

    readonly Ref<Element> _viewport = new();
    NavigationMenu.Scope? _scope;
    UIRoot? _attachedRoot;
    Action<UIRoot>? _handler;
    bool _positioned;
    // The item whose content the viewport last displayed. Kept mounted (with data-state="closed") while the exit
    // animation runs after the menu closes, so PopupMotion's popup-out can play.
    NavigationMenuItem? _lastOpenItem;
    bool _exiting;
    IDisposable? _exitTimer;

    public string? Class { get; set; }

    /// <summary>The item whose content the viewport should render: the open item, or the last one while exiting.</summary>
    NavigationMenuItem? DisplayItem => _scope?.OpenItem ?? (_exiting ? _lastOpenItem : null);

    protected override Node Build()
    {
        var scope = _scope = Use<NavigationMenu.Scope>();
        var state = scope.State;
        _ = state.Version.Value;
        var openItem = scope.FindItem(scope.OpenValue);

        if (openItem is not null)
        {
            // A panel is (again) open: cancel any in-flight exit and remember the item.
            CancelExit();
            _lastOpenItem = openItem;
        }
        else if (_lastOpenItem is not null)
        {
            if (Root?.Clock is { } clock)
            {
                if (!_exiting)
                {
                    _exiting = true;
                    _exitTimer = clock.Schedule(TimeSpan.FromMilliseconds(ExitDurationMs), OnExitElapsed);
                }
            }
            else
            {
                // Headless (no clock): close immediately, exactly as before.
                _lastOpenItem = null;
                _exiting = false;
            }
        }

        bool exiting = openItem is null && _exiting;
        var displayItem = openItem ?? (exiting ? _lastOpenItem : null);
        bool visible = displayItem is not null;

        var viewport = new Div { Id = state.ViewportId, Ref = _viewport };
        // 关闭时视口没有内容，边框 / 阴影仍会画出一个 0 尺寸的残影；用 display:none 让它完全不参与绘制与命中。
        // 退出动画期间内容仍在，视口必须保持可见，动画结束后再隐藏。
        TabsParts.Style(viewport, "navigation-menu-viewport",
            visible ? ViewportClass : ViewportClass + " hidden", Class, null);
        // data-state 表达打开状态（退出动画期间已经是 closed），可见性只由 hidden 控制。
        viewport.Attributes["data-state"] = TabsParts.OpenState(openItem is not null);
        if (openItem is null) viewport.Attributes["aria-hidden"] = "true";

        if (displayItem is not null)
        {
            var itemScope = new NavigationMenuItem.ItemScope(displayItem.State, scope, exiting);
            foreach (var child in displayItem.Children ?? new NodeList())
                if (child is NavigationMenuContent content)
                    viewport.Children.Add(Provide(itemScope, content));
        }

        return viewport;
    }

    void OnExitElapsed()
    {
        _exitTimer = null;
        _exiting = false;
        _lastOpenItem = null;
        Invalidate();
    }

    void CancelExit()
    {
        _exitTimer?.Dispose();
        _exitTimer = null;
        _exiting = false;
    }

    protected override void OnMount()
    {
        var root = Root!;
        _attachedRoot = root;
        _handler = Position;
        root.LayoutCompleted += _handler;
    }

    protected override void OnUnmount()
    {
        CancelExit();
        _lastOpenItem = null;
        if (_attachedRoot is not null && _handler is not null) _attachedRoot.LayoutCompleted -= _handler;
        _attachedRoot = null;
        _handler = null;
    }

    void Position(UIRoot root)
    {
        var scope = _scope;
        var viewport = _viewport.Current;
        if (scope is null || viewport is null || !viewport.IsRendered) return;

        var nav = scope.Nav.Current;
        // While exiting, keep the viewport anchored under the trigger of the item being animated out.
        var trigger = DisplayItem?.TriggerElement;
        if (nav is null || !nav.IsRendered || trigger is null || !trigger.IsRendered)
        {
            Clear(viewport);
            return;
        }

        var content = viewport.ElementChildren.Count > 0 ? viewport.ElementChildren[0] : null;
        if (content is null || !content.IsRendered)
        {
            Clear(viewport);
            return;
        }

        var triggerRect = trigger.GetBoundingClientRect();
        var navRect = nav.GetBoundingClientRect();
        var contentRect = content.GetBoundingClientRect();

        // Center the viewport under the trigger and hang it just below the bar (shadcn side offset 6px).
        // +2 accounts for the viewport's border so the measured content is not clipped.
        float width = contentRect.Width + 2;
        float left = triggerRect.X - navRect.X + (triggerRect.Width - width) * 0.5f;
        float top = triggerRect.Bottom - navRect.Y + 6;
        viewport.SetStyleOverride("left", Px(left));
        viewport.SetStyleOverride("top", Px(top));
        viewport.SetStyleOverride("width", Px(width));
        _positioned = true;
    }

    void Clear(Element viewport)
    {
        if (!_positioned) return;
        _positioned = false;
        viewport.SetStyleOverride("left", null);
        viewport.SetStyleOverride("top", null);
        viewport.SetStyleOverride("width", null);
    }

    static string Px(float value) => value.ToString("0.###", CultureInfo.InvariantCulture) + "px";
}

public sealed partial class NavigationMenuContent : Component
{
    const string ContentClass =
        "relative w-64 min-w-48 overflow-hidden rounded-md p-2 text-popover-foreground " +
        "outline-hidden " + PopupMotion.Classes;

    public Side Side { get; set; } = Side.Bottom;
    public PopperAlign Align { get; set; } = PopperAlign.Center;
    public float SideOffset { get; set; } = 6;
    public float AlignOffset { get; set; }
    public bool AvoidCollisions { get; set; } = true;
    public float CollisionPadding { get; set; }
    public int ZOrder { get; set; }
    public Action<DismissableLayerEvent<Element>>? OnEscapeKeyDown { get; set; }
    public string? Class { get; set; }
    public NodeList? Children { get; set; }
    [CaptureUnmatchedAttributes] public AttributeDictionary? Rest { get; set; }

    NavigationMenuItemState? _state;

    protected override Node Build()
    {
        var root = Use<NavigationMenu.Scope>();
        var item = Use<NavigationMenuItem.ItemScope>();
        var state = _state = item.State;
        var popup = state.Popup;

        var attrs = DialogAttributes.ForPart("navigation-menu-content", Rest, Class, ContentClass);
        // 内容只在打开时挂载；关闭由视口卸载完成。退出动画期间视口仍挂着内容并把它标为 closed，从而播放 popup-out。
        attrs["data-state"] = item.Exiting ? "closed" : "open";
        attrs["role"] = "region";
        attrs["aria-labelledby"] = state.TriggerId;
        attrs["data-motion"] = Motion(state.Owner, root.State.PreviousIndex, item.Exiting);
        attrs["onpointerenter"] = ComposePointer(attrs["onpointerenter"], e =>
        {
            if (!e.DefaultPrevented && e.PointerType != PointerType.Touch && popup is not null)
                state.Hover.SetHover(popup, Root?.Clock, true, true, root.OpenDelay, root.CloseDelay);
        });
        attrs["onpointerleave"] = ComposePointer(attrs["onpointerleave"], e =>
        {
            if (e.PointerType != PointerType.Touch && popup is not null)
                state.Hover.SetHover(popup, Root?.Clock, false, true, root.OpenDelay, root.CloseDelay);
        });
        attrs["onfocusin"] = ComposeFocus(attrs["onfocusin"], _ =>
        {
            if (popup is not null) state.Hover.SetFocus(popup, true);
        });
        attrs["onfocusout"] = ComposeFocus(attrs["onfocusout"], e =>
        {
            if (popup is not null && e.CurrentTarget?.IsInclusiveAncestorOf(e.RelatedTarget) != true) state.Hover.SetFocus(popup, false);
        });
        attrs["onkeydown"] = ComposeKey(attrs["onkeydown"], OnContentKey);

        var content = new Div
        {
            Id = state.ContentId,
            Ref = state.Content,
            Role = Role.Region,
            Children = Children ?? new NodeList(),
        };
        content.MergeAttributes(attrs);

        var layer = new DismissableLayer
        {
            Active = true,
            OnEscapeKeyDown = OnEscapeKeyDown,
            OnInteractOutside = e =>
            {
                if (state.Trigger.Current?.IsInclusiveAncestorOf(e.Target) == true) e.PreventDefault();
            },
            OnDismiss = _ =>
            {
                if (state.Owner is { } owner) root.SetOpen(owner, false);
            },
            Children = [content],
        };
        return new FocusScope
        {
            Active = true,
            OnMountAutoFocus = e =>
            {
                e.PreventDefault();
                if (!state.FocusOnOpen) return;
                state.FocusOnOpen = false;
                var uiRoot = Root!;
                uiRoot.RequestAfterLayout(() => NavigationMenuParts.FocusFirst(state.Content.Current, FocusOrigin.Keyboard, last: false));
            },
            OnUnmountAutoFocus = e =>
            {
                var focused = Root?.FocusManager.FocusedElement;
                // Restore to the trigger only when focus was inside the panel; an outside press keeps its focus.
                if (focused is null) return;
                if (!e.Container.IsInclusiveAncestorOf(focused)) { e.PreventDefault(); return; }
                e.PreventDefault();
                state.SuppressFocusOpen = true;
                state.Trigger.Current?.Focus(new FocusOptions { Origin = FocusOrigin.Keyboard, PreventScroll = true });
            },
            Children = [layer],
        };
    }

    void OnContentKey(KeyEvent e)
    {
        if (e.DefaultPrevented || e.Modifiers != KeyModifiers.None || _state is null) return;
        if (e.Key is "Escape") return;
        var intent = RovingFocus.GetFocusIntent(e.Key, Orientation.Vertical, Direction.Ltr);
        if (intent == FocusIntent.None) return;
        var content = _state.Content.Current;
        if (content is null) return;
        var links = NavigationMenuParts.Links(content);
        if (links.Count == 0) return;
        var focused = Root?.FocusManager.FocusedElement;
        int current = focused is null ? -1 : links.FindIndex(l => ReferenceEquals(l, focused));
        e.PreventDefault();
        int next = RovingFocus.Navigate(intent, current, links.Count, i => links[i].IsFocusable, loop: true);
        if (next >= 0) links[next].Focus(new FocusOptions { Origin = FocusOrigin.Keyboard });
    }

    /// <summary>
    /// Enter uses <c>from-start</c> / <c>from-end</c>; while exiting it uses the mirrored <c>to-start</c> /
    /// <c>to-end</c> so the panel leaves toward the side it would have entered from.
    /// </summary>
    static string Motion(NavigationMenuItem? owner, int previousIndex, bool exiting)
    {
        bool forward = owner is { } o && o.Index > previousIndex;
        if (exiting) return forward ? "to-start" : "to-end";
        return forward ? "from-end" : "from-start";
    }

    static object? ComposeKey(object? handler, Action<KeyEvent> own) => handler switch
    {
        Action<KeyEvent> typed => e => { typed(e); own(e); },
        Action plain => e => { plain(); own(e); },
        null => own,
        _ => own,
    };

    static object? ComposePointer(object? handler, Action<PointerEvent> own) => handler switch
    {
        Action<PointerEvent> typed => e => { typed(e); own(e); },
        Action plain => e => { plain(); own(e); },
        null => own,
        _ => own,
    };

    static object? ComposeFocus(object? handler, Action<FocusEvent> own) => handler switch
    {
        Action<FocusEvent> typed => e => { typed(e); own(e); },
        Action plain => e => { plain(); own(e); },
        null => own,
        _ => own,
    };
}

public sealed partial class NavigationMenuLink : Component
{
    const string LinkClass =
        "flex flex-col gap-1 rounded-sm p-2 text-sm transition-all outline-none hover:bg-accent hover:text-accent-foreground " +
        "focus:bg-accent focus:text-accent-foreground focus-visible:ring-[3px] focus-visible:ring-ring/50 focus-visible:outline-1 " +
        "data-[active=true]:bg-accent/50 data-[active=true]:text-accent-foreground data-[active=true]:hover:bg-accent " +
        "data-[active=true]:focus:bg-accent [&_svg:not([class*='size-'])]:size-4 [&_svg:not([class*='text-'])]:text-muted-foreground";

    public bool Active { get; set; }
    public string? Href { get; set; }
    public Action<PointerEvent>? OnClick { get; set; }
    public string? Class { get; set; }
    public Ref<Element>? Ref { get; set; }
    public NodeList? Children { get; set; }
    [CaptureUnmatchedAttributes] public AttributeDictionary? Rest { get; set; }

    protected override Node Build()
    {
        var link = new NavigationTag("a") { Role = Role.Link, TabIndex = 0, Ref = Ref, OnClick = OnClick,
            Children = Children ?? new NodeList() };
        TabsParts.Style(link, "navigation-menu-link", LinkClass, Class, Rest);
        if (Href is not null && link.Attributes["href"] is null) link.Attributes["href"] = Href;
        if (Active)
        {
            link.Attributes["data-active"] = "true";
            link.Attributes["aria-current"] = "page";
        }
        return link;
    }
}

/// <summary>A small decorative caret under the open navigation panel.</summary>
public sealed partial class NavigationMenuIndicator : Component
{
    const string IndicatorClass =
        "absolute left-1/2 top-full z-[1] flex h-1.5 -translate-x-1/2 items-end justify-center overflow-hidden " +
        "data-[state=hidden]:animate-out data-[state=hidden]:fade-out data-[state=visible]:animate-in data-[state=visible]:fade-in";

    public string? Class { get; set; }
    [CaptureUnmatchedAttributes] public AttributeDictionary? Rest { get; set; }

    protected override Node Build()
    {
        var root = Use<NavigationMenu.Scope>();
        bool visible = root.OpenValue is not null;
        var indicator = new Div
        {
            Children =
            [
                new Div { Class = "relative top-[60%] h-2 w-2 rotate-45 rounded-tl-sm bg-border shadow-md" },
            ],
        };
        TabsParts.Style(indicator, "navigation-menu-indicator", IndicatorClass, Class, Rest);
        indicator.Attributes["data-state"] = visible ? "visible" : "hidden";
        indicator.Attributes["aria-hidden"] = "true";
        return indicator;
    }
}

internal sealed class NavigationTag(string tag) : ElementNode
{
    protected override Element CreateElement() => new(tag);
}

/// <summary>Shared navigation-panel link discovery and focus movement for triggers and content panels.</summary>
internal static class NavigationMenuParts
{
    internal static List<Element> Links(Element content)
    {
        var links = new List<Element>();
        Visit(content);
        return links;

        void Visit(Element parent)
        {
            foreach (var child in parent.ElementChildren)
            {
                if (child.Role == Role.Link || string.Equals(child.Tag, "a", StringComparison.OrdinalIgnoreCase))
                {
                    if (child.IsFocusable && child.IsVisible) links.Add(child);
                    continue;
                }
                Visit(child);
            }
        }
    }

    internal static void FocusFirst(Element? content, FocusOrigin origin, bool last)
    {
        if (content is null) return;
        var links = NavigationMenuParts.Links(content);
        if (last)
        {
            for (int i = links.Count - 1; i >= 0; i--)
                if (links[i].Focus(new FocusOptions { Origin = origin })) return;
        }
        else
        {
            foreach (var link in links)
                if (link.Focus(new FocusOptions { Origin = origin })) return;
        }
        content.Focus(new FocusOptions { Origin = origin, PreventScroll = true });
    }
}
