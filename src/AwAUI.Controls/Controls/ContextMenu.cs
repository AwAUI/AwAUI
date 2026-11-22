using System;
using AwAUI.Controls.Primitives;
using AwAUI.Platform;
using AwAUI.UI;

namespace AwAUI.Controls;

/// <summary>
/// A menu opened at the pointer on a right-click or long press. One directly wrapped built-in target
/// (<c>button</c>, <c>input</c>, <c>div</c>, <c>span</c>, <c>text</c> or <c>img</c>) receives the input;
/// the target keeps its own layout and focus behavior.
/// </summary>
/// <remarks>
/// The context menu key and Shift+F10 open it anchored to the target's bottom-left. Focus moves into the
/// menu and returns to the previously focused element on close. Escape and an outside press dismiss it.
/// Put <see cref="MenuItem"/> nodes in <see cref="Items"/>; nested items open submenus.
/// </remarks>
public sealed partial class ContextMenu : Control
{
    readonly ControllableState<bool> _open = new();
    readonly Ref<Element> _content = new();
    readonly Ref<Element> _targetRef = new();
    readonly string _contentId = PrimitiveIds.Next("context-menu");
    readonly MenuLevel _level;
    Rect _anchorRect;
    IDisposable? _longPress;
    int _longPressVersion;
    Point _pressPosition;

    public ContextMenu() => _level = new MenuLevel(null, _open, _content, OverlayZOrder.Default);

    /// <summary>Exactly one supported target. No layout wrapper or extra Tab stop is inserted.</summary>
    public NodeList? Children { get; set; }

    /// <summary>The menu entries, typically <see cref="MenuItem"/> nodes.</summary>
    public NodeList? Items { get; set; }

    /// <summary>Set to control visibility, or leave null to let the menu manage it.</summary>
    public bool? IsOpen { get; set; }

    public bool DefaultIsOpen { get; set; }

    /// <summary>Requests a change to <see cref="IsOpen"/>. Escape or an outside press requests false.</summary>
    public Action<bool>? IsOpenChanged { get; set; }

    /// <summary>How far a touch may move before a long press is cancelled, in logical pixels.</summary>
    public float LongPressTolerance { get; set; } = 8;

    /// <summary>Long press duration in milliseconds. Values below 1 are treated as 1.</summary>
    public int LongPressDelay { get; set; } = 500;

    public int ZOrder { get; set; } = OverlayZOrder.Default;

    protected override string GlobalStyleSheet => MenuStyles.StyleSheet;

    protected override Node Build()
    {
        if (Children is not { Count: > 0 })
        {
            Reset();
            return new Fragment();
        }
        var child = Slot.SingleChild(Children);
        if (Children.Count != 1 || (child is not (UI.Button or Div or Span or UI.Text or Image) && child?.GetType() != typeof(Input)))
            throw new InvalidOperationException("ContextMenu requires a single built-in button, input, div, span, text or img as its child.");

        bool open = _open.Sync(IsOpen, DefaultIsOpen, IsOpenChanged);

        var target = (ElementNode)Slot.Merge(new Div
        {
            Class = "aw-context-menu-target",
            Ref = _targetRef,
            OnPointerDown = OnTargetPointerDown,
            OnPointerUp = OnTargetPointerUp,
            OnPointerMove = OnTargetPointerMove,
            OnPointerCancel = _ => CancelLongPress(),
            OnKeyDown = OnTargetKeyDown,
        }, child);
        target.Attributes["aria-haspopup"] = "menu";
        target.Attributes["aria-expanded"] = PartProps.Bool(open);
        if (open) target.Attributes["aria-controls"] = _contentId;

        var anchor = new PopperAnchor { AsChild = true, VirtualRect = () => _anchorRect, Children = [target] };
        return new Popper { Children = [anchor, BuildSurface(open)] };
    }

    Node BuildSurface(bool open)
    {
        var level = _level;
        var attributes = new AttributeDictionary
        {
            ["part"] = "content",
            ["data-state"] = PartProps.OpenState(open),
            ["aria-hidden"] = open ? null : "true",
            ["aria-label"] = AriaLabel ?? Attributes?["aria-label"],
            ["onkeydown"] = (Action<KeyEvent>)(e => level.HandleKeyDown(e, null)),
        };
        var content = new PopperContent
        {
            Id = _contentId,
            Role = Role.Menu,
            TabIndex = -1,
            Ref = _content,
            Class = "aw-menu-content",
            Side = Side.Bottom,
            Align = PopperAlign.Start,
            SideOffset = 0,
            CollisionPadding = 8,
            Attributes = attributes,
            Children = [Provide(level, new Fragment(Items ?? []))],
        };
        var layer = new DismissableLayer
        {
            Active = open,
            OnInteractOutside = e =>
            {
                // The target press opens the menu; do not treat it as outside.
                if (_targetRef.Current?.IsInclusiveAncestorOf(e.Target) == true) e.PreventDefault();
            },
            OnDismiss = _ =>
            {
                if (!open) return;
                Close();
            },
            Children = [content],
        };
        return new PresenceGate
        {
            Present = open,
            Children = [new Portal { ZOrder = ZOrder, Children = [BuildScope(layer, open, level)] }],
        };
    }

    Node BuildScope(DismissableLayer layer, bool open, MenuLevel level) => new FocusScope
    {
        Active = open,
        OnMountAutoFocus = e =>
        {
            e.PreventDefault();
            if (!open) return;
            level.FocusFirst(Root?.FocusManager.LastInteractionWasKeyboard == true
                ? FocusOrigin.Keyboard : FocusOrigin.Programmatic);
        },
        OnUnmountAutoFocus = e =>
        {
            e.PreventDefault();
            var root = Root;
            if (root is null) return;
            var focused = root.FocusManager.FocusedElement;
            if (focused is not null && !e.Container.IsInclusiveAncestorOf(focused)) return;
            if (_targetRef.Current is { IsVisible: true, IsFocusable: true } target)
                target.Focus(new FocusOptions { Origin = FocusOrigin.Programmatic, PreventScroll = true });
        },
        Children = [layer],
    };

    void OnTargetPointerDown(PointerEvent e)
    {
        if (e.DefaultPrevented || e.CurrentTarget is not { IsDisabled: false } target) return;
        bool rightClick = e.Button == PointerButtons.Right
            || (e.Button == PointerButtons.Left && (e.Modifiers & KeyModifiers.Control) != 0);
        if (rightClick)
        {
            e.PreventDefault();
            OpenAt(e.Position);
            return;
        }
        if (e.PointerType == PointerType.Touch && e.Button == PointerButtons.Left)
        {
            _pressPosition = e.Position;
            if (Root?.Clock is { } clock)
            {
                int version = ++_longPressVersion;
                _longPress = clock.Schedule(TimeSpan.FromMilliseconds(Math.Max(1, LongPressDelay)), () =>
                {
                    if (version != _longPressVersion) return;
                    _longPress = null;
                    OpenAt(_pressPosition);
                });
            }
        }
    }

    void OnTargetPointerUp(PointerEvent e)
    {
        if (e.PointerType == PointerType.Touch) CancelLongPress();
    }

    void OnTargetPointerMove(PointerEvent e)
    {
        if (e.PointerType != PointerType.Touch) return;
        float dx = e.Position.X - _pressPosition.X;
        float dy = e.Position.Y - _pressPosition.Y;
        if (dx * dx + dy * dy > LongPressTolerance * LongPressTolerance) CancelLongPress();
    }

    void OnTargetKeyDown(KeyEvent e)
    {
        if (e.DefaultPrevented || e.CurrentTarget is not { IsDisabled: false } target) return;
        if (e.Key != "ContextMenu" && !(e.Key == "F10" && (e.Modifiers & KeyModifiers.Shift) != 0)) return;
        e.PreventDefault();
        var rect = target.GetBoundingClientRect();
        OpenAt(new Point(rect.X, rect.Y + rect.Height));
    }

    void OpenAt(Point position)
    {
        _anchorRect = new Rect(position.X, position.Y, 0, 0);
        // A context menu on a non-focusable target still needs a place to return focus to.
        _open.Set(true);
    }

    void Close()
    {
        CancelLongPress();
        _open.Set(false);
    }

    void CancelLongPress()
    {
        _longPressVersion++;
        _longPress?.Dispose();
        _longPress = null;
    }

    void Reset() => CancelLongPress();

    protected override void OnUnmount() => Reset();
}
