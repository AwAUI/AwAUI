using System;
using AwAUI.UI;
using UIButton = AwAUI.UI.Button;
using UIPortal = AwAUI.UI.Portal;

namespace AwAUI.Controls.Primitives;

/// <summary>Popover / Dialog 等可打开的复合原语在部件之间共享的可变状态（触发器、内容的组件引用与 id）。</summary>
public sealed class OpenableShared
{
    internal OpenableShared(string prefix)
    {
        ContentId = PrimitiveIds.Next(prefix + "-content");
        TitleId = PrimitiveIds.Next(prefix + "-title");
        DescriptionId = PrimitiveIds.Next(prefix + "-description");
    }

    internal ControllableState<bool> State { get; } = new();

    /// <summary>内容元素的 id（触发器的 <c>aria-controls</c>）。</summary>
    public string ContentId { get; }

    /// <summary>标题的 id（内容的 <c>aria-labelledby</c>）。</summary>
    public string TitleId { get; }

    /// <summary>描述的 id（内容的 <c>aria-describedby</c>）。</summary>
    public string DescriptionId { get; }

    /// <summary>触发器组件（关闭后焦点回到它；非模态时在它上面按下不算外部）。</summary>
    public Component? Trigger { get; internal set; }

    /// <summary>触发器的元素。</summary>
    public Element? TriggerElement => Trigger is { IsMounted: true } t ? DismissableLayer.FirstElement(t) : null;

    /// <summary>当前是否打开（不登记依赖）。</summary>
    public bool IsOpen => State.Value;

    /// <summary>请求打开 / 关闭（受控时只调用 <c>OnOpenChange</c>）。</summary>
    public void SetOpen(bool open) => State.Set(open);

    /// <summary>切换。</summary>
    public void Toggle() => State.Set(!State.Value);
}

/// <summary>
/// Popover 原语（Radix <c>@radix-ui/react-popover</c>）：<see cref="Root"/>、<see cref="Trigger"/>、<see cref="Anchor"/>、<see cref="Portal"/>、
/// <see cref="Content"/>、<see cref="Close"/>、<see cref="Arrow"/>。无样式，只输出结构、状态属性（<c>data-state</c>、<c>data-side</c>、<c>data-align</c>）与 ARIA。
/// <code>
/// new Popover.Root { Children = [
///     new Popover.Trigger { Children = ["打开"] },
///     new Popover.Portal { Children = [
///         new Popover.Content { SideOffset = 4, Class = "popover", Children = [ ..., new Popover.Close { Children = ["×"] } ] } ] } ] }
/// </code>
/// </summary>
public static partial class Popover
{
    /// <summary>向部件提供的上下文：打开状态在这里（值相等则部件不重建），可变的引用在 <see cref="Shared"/> 中。</summary>
    public sealed class Context : IEquatable<Context>
    {
        internal Context(bool open, bool modal, OpenableShared shared, PopperScope popper)
        {
            Open = open;
            Modal = modal;
            Shared = shared;
            Popper = popper;
        }

        public bool Open { get; }

        public bool Modal { get; }

        public OpenableShared Shared { get; }

        internal PopperScope Popper { get; }

        public bool Equals(Context? other) =>
            other is not null && other.Open == Open && other.Modal == Modal && ReferenceEquals(other.Shared, Shared);

        public override bool Equals(object? obj) => Equals(obj as Context);

        public override int GetHashCode() => Shared.GetHashCode() ^ (Open ? 1 : 0) ^ (Modal ? 2 : 0);
    }

    /// <summary>根：打开状态（受控 <see cref="Open"/> + <see cref="OnOpenChange"/>，或非受控 <see cref="DefaultOpen"/>）与 <see cref="Modal"/>。</summary>
    public sealed partial class Root : Component
    {
        readonly OpenableShared _shared = new("popover");
        readonly PopperScope _popper = new();

        public bool? Open { get; set; }

        public bool DefaultOpen { get; set; }

        public Action<bool>? OnOpenChange { get; set; }

        /// <summary>模态：屏蔽外部指针、困住焦点、锁定滚动。默认 false。</summary>
        public bool Modal { get; set; }

        public NodeList? Children { get; set; }

        /// <summary>部件共享的状态（测试与命令式操作）。</summary>
        public OpenableShared Shared => _shared;

        protected override Node Build()
        {
            bool open = _shared.State.Sync(Open, DefaultOpen, OnOpenChange);
            var ctx = new Context(open, Modal, _shared, _popper);
            return Provide(ctx, Provide(_popper, new Fragment(Children ?? new NodeList())));
        }
    }

    /// <summary>触发器：按钮（或 <see cref="AsChild"/> 时的子元素），点击切换；<c>aria-haspopup="dialog"</c>、<c>aria-expanded</c>、<c>aria-controls</c>、<c>data-state</c>。</summary>
    public sealed partial class Trigger : Component
    {
        public bool AsChild { get; set; }

        public NodeList? Children { get; set; }

        public ClassValue Class { get; set; }

        public StyleValue Style { get; set; }

        [CaptureUnmatchedAttributes]
        public AttributeDictionary? Attributes { get; set; }

        protected override Node Build()
        {
            var ctx = Use<Context>();
            ctx.Shared.Trigger = this;
            ctx.Popper.DefaultAnchor = this;
            var props = new UIButton { OnClick = PartProps.Internal(ctx.Shared.Toggle) };
            props.Attributes["aria-haspopup"] = "dialog";
            props.Attributes["aria-expanded"] = PartProps.Bool(ctx.Open);
            props.Attributes["aria-controls"] = ctx.Shared.ContentId;
            props.Attributes["data-state"] = PartProps.OpenState(ctx.Open);
            PartProps.Apply(props, Class, Style, Attributes);
            if (AsChild) return Slot.Merge(props, Slot.SingleChild(Children));
            props.Children = Children ?? new NodeList();
            return props;
        }

        protected override void OnUnmount()
        {
            if (TryUse<Context>(out var ctx) && ReferenceEquals(ctx.Shared.Trigger, this)) ctx.Shared.Trigger = null;
        }
    }

    /// <summary>自定义锚点（浮层相对它而不是触发器定位）。</summary>
    public sealed partial class Anchor : Component
    {
        public bool AsChild { get; set; }

        public NodeList? Children { get; set; }

        public ClassValue Class { get; set; }

        public StyleValue Style { get; set; }

        [CaptureUnmatchedAttributes]
        public AttributeDictionary? Attributes { get; set; }

        protected override Node Build()
        {
            // PopperAnchor 登记的锚点优先于触发器（PopperScope.DefaultAnchor）
            return new PopperAnchor { AsChild = AsChild, Children = Children, Class = Class, Style = Style, Attributes = Attributes };
        }
    }

    /// <summary>传送门：打开时（或 <see cref="ForceMount"/>）把子节点挂到覆盖层（<see cref="UIPortal"/>）。</summary>
    public sealed partial class Portal : Component
    {
        public bool ForceMount { get; set; }

        public int ZOrder { get; set; }

        public NodeList? Children { get; set; }

        protected override Node Build()
        {
            var ctx = Use<Context>();
            return new PresenceGate
            {
                Present = ctx.Open,
                ForceMount = ForceMount,
                Children = [new UIPortal { ZOrder = ZOrder, Children = Children ?? new NodeList() }],
            };
        }
    }

    /// <summary>
    /// 内容：可关闭层 + 焦点作用域 + 定位（<see cref="PopperContent"/>），<c>role="dialog"</c>、<c>data-state</c>、<c>data-side / data-align</c>。
    /// 非模态：点击外部或焦点移出关闭（在触发器上按下不算外部），关闭后焦点回到触发器（除非在外部交互过）；
    /// 模态：屏蔽外部指针、困住焦点、锁定滚动，关闭后焦点回到触发器。
    /// </summary>
    public sealed partial class Content : Component
    {
        bool _wasOpen;
        bool _hasInteractedOutside;
        bool _hasPointerDownOutside;
        bool _isRightClickOutside;

        public bool ForceMount { get; set; }

        public Side Side { get; set; } = Side.Bottom;

        public float SideOffset { get; set; }

        public PopperAlign Align { get; set; } = PopperAlign.Center;

        public float AlignOffset { get; set; }

        public bool AvoidCollisions { get; set; } = true;

        public float CollisionPadding { get; set; }

        public PopperSticky Sticky { get; set; } = PopperSticky.Partial;

        public bool HideWhenDetached { get; set; }

        public float ArrowPadding { get; set; }

        public Action<FocusScopeAutoFocusEvent>? OnOpenAutoFocus { get; set; }

        public Action<FocusScopeAutoFocusEvent>? OnCloseAutoFocus { get; set; }

        public Action<DismissableLayerEvent<Element>>? OnEscapeKeyDown { get; set; }

        public Action<DismissableLayerEvent<Element>>? OnPointerDownOutside { get; set; }

        public Action<DismissableLayerEvent<Element>>? OnFocusOutside { get; set; }

        public Action<DismissableLayerEvent<Element>>? OnInteractOutside { get; set; }

        public NodeList? Children { get; set; }

        public ClassValue Class { get; set; }

        public StyleValue Style { get; set; }

        [CaptureUnmatchedAttributes]
        public AttributeDictionary? Attributes { get; set; }

        protected override Node Build()
        {
            var ctx = Use<Context>();
            if (ctx.Open && !_wasOpen) ResetOutsideInteraction();
            _wasOpen = ctx.Open;
            var shared = ctx.Shared;
            bool modal = ctx.Modal;
            var attrs = new AttributeDictionary(Attributes) { ["data-state"] = PartProps.OpenState(ctx.Open) };
            if (!ctx.Open) attrs["aria-hidden"] = "true";
            var popper = new PopperContent
            {
                Side = Side,
                SideOffset = SideOffset,
                Align = Align,
                AlignOffset = AlignOffset,
                AvoidCollisions = AvoidCollisions,
                CollisionPadding = CollisionPadding,
                Sticky = Sticky,
                HideWhenDetached = HideWhenDetached,
                ArrowPadding = ArrowPadding,
                Id = shared.ContentId,
                Role = Role.Dialog,
                TabIndex = ctx.Open ? -1 : null,
                Class = Class,
                Style = Style,
                Attributes = attrs,
                Children = Children,
            };
            var layer = new DismissableLayer
            {
                Active = ctx.Open,
                DisableOutsidePointerEvents = modal,
                OnEscapeKeyDown = ctx.Open ? OnEscapeKeyDown : null,
                // 模态（Radix PopoverContentModal）：记下是否右键点击外部（不论是否被阻止），关闭后据此不把焦点还给触发器
                OnPointerDownOutside = !ctx.Open ? null : modal ? ComposeAlways(OnPointerDownOutside, e => _isRightClickOutside = PartProps.IsRightClick(e)) : OnPointerDownOutside,
                OnFocusOutside = !ctx.Open ? null : modal ? ComposeAlways(OnFocusOutside, e => e.PreventDefault()) : OnFocusOutside,
                OnInteractOutside = !ctx.Open ? null : modal ? OnInteractOutside : e => NonModalInteractOutside(e, shared),
                OnDismiss = ctx.Open ? _ => shared.SetOpen(false) : null,
                Children = [popper],
            };
            var scope = new FocusScope
            {
                Active = ctx.Open,
                Loop = ctx.Open,
                Trapped = modal && ctx.Open,
                OnMountAutoFocus = OnOpenAutoFocus,
                OnUnmountAutoFocus = e => CloseAutoFocus(e, shared, modal),
                Children = [layer],
            };
            return new PresenceGate
            {
                Present = ctx.Open,
                ForceMount = ForceMount,
                Children = [modal ? new ScrollLock { Children = [scope] } : scope],
            };
        }

        void NonModalInteractOutside(DismissableLayerEvent<Element> e, OpenableShared shared)
        {
            OnInteractOutside?.Invoke(e);
            if (!e.DefaultPrevented)
            {
                _hasInteractedOutside = true;
                if (e.Reason == DismissReason.PointerDownOutside) _hasPointerDownOutside = true;
            }
            // 在触发器上按下：触发器自己会切换，不能先关再开
            if (e.Target is { } target && shared.TriggerElement is { } trigger && trigger.IsInclusiveAncestorOf(target)) e.PreventDefault();
            // 指针按下已经处理过外部交互，随后的焦点移出不再重复
            if (e.Reason == DismissReason.FocusOutside && _hasPointerDownOutside) e.PreventDefault();
        }

        void CloseAutoFocus(FocusScopeAutoFocusEvent e, OpenableShared shared, bool modal)
        {
            OnCloseAutoFocus?.Invoke(e);
            if (!e.DefaultPrevented)
            {
                if ((modal ? !_isRightClickOutside : !_hasInteractedOutside)
                    && shared.TriggerElement is { Root: { } root } trigger)
                {
                    var focused = root.FocusManager.FocusedElement;
                    if (focused is null || e.Container.IsInclusiveAncestorOf(focused)) trigger.Focus();
                }
                e.PreventDefault();
            }
            ResetOutsideInteraction();
        }

        void ResetOutsideInteraction()
        {
            _hasInteractedOutside = false;
            _hasPointerDownOutside = false;
            _isRightClickOutside = false;
        }

        static Action<DismissableLayerEvent<Element>> ComposeAlways(Action<DismissableLayerEvent<Element>>? user, Action<DismissableLayerEvent<Element>> own) =>
            e =>
            {
                user?.Invoke(e);
                own(e);
            };
    }

    /// <summary>关闭按钮（或 <see cref="AsChild"/> 时的子元素）。</summary>
    public sealed partial class Close : Component
    {
        public bool AsChild { get; set; }

        public NodeList? Children { get; set; }

        public ClassValue Class { get; set; }

        public StyleValue Style { get; set; }

        [CaptureUnmatchedAttributes]
        public AttributeDictionary? Attributes { get; set; }

        protected override Node Build()
        {
            var ctx = Use<Context>();
            var props = new UIButton { OnClick = PartProps.Internal(() => ctx.Shared.SetOpen(false)) };
            PartProps.Apply(props, Class, Style, Attributes);
            if (AsChild) return Slot.Merge(props, Slot.SingleChild(Children));
            props.Children = Children ?? new NodeList();
            return props;
        }
    }

    /// <summary>箭头（<see cref="PopperArrow"/>）。</summary>
    public sealed partial class Arrow : Component
    {
        public float Width { get; set; } = 10;

        public float Height { get; set; } = 5;

        public NodeList? Children { get; set; }

        public ClassValue Class { get; set; }

        public StyleValue Style { get; set; }

        protected override Node Build() =>
            new PopperArrow { Width = Width, Height = Height, Children = Children, Class = Class, Style = Style };
    }
}
