using System;
using AwAUI.UI;
using UIButton = AwAUI.UI.Button;
using UIPortal = AwAUI.UI.Portal;

namespace AwAUI.Controls.Primitives;

/// <summary>
/// Dialog 原语（Radix <c>@radix-ui/react-dialog</c>）：<see cref="Root"/>、<see cref="Trigger"/>、<see cref="Portal"/>、<see cref="Overlay"/>、
/// <see cref="Content"/>、<see cref="Title"/>、<see cref="Description"/>、<see cref="Close"/>。默认模态：内容之外不接收指针、焦点困在内容中
/// （打开时聚焦第一个可 Tab 元素，Tab 回绕）、视口滚动锁定，Escape 或点击外部（遮罩）关闭，关闭后焦点回到触发器。
/// 内容 <c>role="dialog"</c>，<c>aria-labelledby</c> / <c>aria-describedby</c> 指向标题与描述，各部件带 <c>data-state</c>。
/// </summary>
public static partial class Dialog
{
    /// <summary>向部件提供的上下文（打开状态与模态；可变引用在 <see cref="Shared"/> 中）。</summary>
    public sealed class Context : IEquatable<Context>
    {
        internal Context(bool open, bool modal, OpenableShared shared)
        {
            Open = open;
            Modal = modal;
            Shared = shared;
        }

        public bool Open { get; }

        public bool Modal { get; }

        public OpenableShared Shared { get; }

        public bool Equals(Context? other) =>
            other is not null && other.Open == Open && other.Modal == Modal && ReferenceEquals(other.Shared, Shared);

        public override bool Equals(object? obj) => Equals(obj as Context);

        public override int GetHashCode() => Shared.GetHashCode() ^ (Open ? 1 : 0) ^ (Modal ? 2 : 0);
    }

    /// <summary>根：打开状态（受控 <see cref="Open"/> + <see cref="OnOpenChange"/>，或非受控 <see cref="DefaultOpen"/>）与 <see cref="Modal"/>（默认 true）。</summary>
    public sealed partial class Root : Component
    {
        readonly OpenableShared _shared = new("dialog");

        public bool? Open { get; set; }

        public bool DefaultOpen { get; set; }

        public Action<bool>? OnOpenChange { get; set; }

        public bool Modal { get; set; } = true;

        public NodeList? Children { get; set; }

        /// <summary>部件共享的状态（测试与命令式操作）。</summary>
        public OpenableShared Shared => _shared;

        protected override Node Build()
        {
            bool open = _shared.State.Sync(Open, DefaultOpen, OnOpenChange);
            return Provide(new Context(open, Modal, _shared), new Fragment(Children ?? new NodeList()));
        }
    }

    /// <summary>触发器：点击切换；<c>aria-haspopup="dialog"</c>、<c>aria-expanded</c>、<c>aria-controls</c>、<c>data-state</c>。</summary>
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

    /// <summary>传送门：打开时（或 <see cref="ForceMount"/>）把子节点挂到覆盖层。</summary>
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

    /// <summary>遮罩：只在模态时渲染（<c>data-state</c>）。点击它即点击内容的外部，关闭对话框。</summary>
    public sealed partial class Overlay : Component
    {
        public bool ForceMount { get; set; }

        public NodeList? Children { get; set; }

        public ClassValue Class { get; set; }

        public StyleValue Style { get; set; }

        [CaptureUnmatchedAttributes]
        public AttributeDictionary? Attributes { get; set; }

        protected override Node Build()
        {
            var ctx = Use<Context>();
            if (!ctx.Modal) return new Fragment();
            var node = new Div { Children = Children ?? new NodeList() };
            node.Attributes["data-state"] = PartProps.OpenState(ctx.Open);
            return new PresenceGate
            {
                Present = ctx.Open,
                ForceMount = ForceMount,
                Children = [PartProps.Apply(node, Class, Style, Attributes)],
            };
        }
    }

    /// <summary>
    /// 内容：焦点作用域（回绕；模态时困住）+ 可关闭层（模态时屏蔽外部指针）+ <c>role="dialog"</c> 的元素（<c>TabIndex = -1</c>）。
    /// 模态时还锁定视口滚动（内容内可滚动）。
    /// </summary>
    public sealed partial class Content : Component
    {
        bool _wasOpen;
        bool _hasInteractedOutside;
        bool _hasPointerDownOutside;

        public bool ForceMount { get; set; }

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
            var node = new Div { Id = shared.ContentId, Role = Role.Dialog, TabIndex = -1, Children = Children ?? new NodeList() };
            node.Attributes["aria-labelledby"] = shared.TitleId;
            node.Attributes["aria-describedby"] = shared.DescriptionId;
            if (modal) node.Attributes["aria-modal"] = "true";
            node.Attributes["data-state"] = PartProps.OpenState(ctx.Open);
            PartProps.Apply(node, Class, Style, Attributes);
            var layer = new DismissableLayer
            {
                DisableOutsidePointerEvents = modal,
                OnEscapeKeyDown = ctx.Open ? OnEscapeKeyDown : null,
                // 模态（Radix DialogContentModal）：右键（或 Ctrl+左键）点击外部相当于右键点击遮罩，不关闭
                OnPointerDownOutside = !ctx.Open ? null : modal ? e => ModalPointerDownOutside(e) : OnPointerDownOutside,
                // 模态：焦点不会真正移出（焦点作用域会拉回），焦点移出不关闭
                OnFocusOutside = !ctx.Open ? null : modal ? e => { OnFocusOutside?.Invoke(e); e.PreventDefault(); } : OnFocusOutside,
                OnInteractOutside = !ctx.Open ? null : modal ? OnInteractOutside : e => NonModalInteractOutside(e, shared),
                OnDismiss = ctx.Open ? _ => shared.SetOpen(false) : null,
                Children = [node],
            };
            var scope = new FocusScope
            {
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

        void ModalPointerDownOutside(DismissableLayerEvent<Element> e)
        {
            OnPointerDownOutside?.Invoke(e);
            if (!e.DefaultPrevented && PartProps.IsRightClick(e)) e.PreventDefault();
        }

        void NonModalInteractOutside(DismissableLayerEvent<Element> e, OpenableShared shared)
        {
            OnInteractOutside?.Invoke(e);
            if (!e.DefaultPrevented)
            {
                _hasInteractedOutside = true;
                if (e.Reason == DismissReason.PointerDownOutside) _hasPointerDownOutside = true;
            }
            if (e.Target is { } target && shared.TriggerElement is { } trigger && trigger.IsInclusiveAncestorOf(target)) e.PreventDefault();
            if (e.Reason == DismissReason.FocusOutside && _hasPointerDownOutside) e.PreventDefault();
        }

        void CloseAutoFocus(FocusScopeAutoFocusEvent e, OpenableShared shared, bool modal)
        {
            OnCloseAutoFocus?.Invoke(e);
            if (!e.DefaultPrevented)
            {
                if (modal || !_hasInteractedOutside) shared.TriggerElement?.Focus();
                e.PreventDefault();
            }
            ResetOutsideInteraction();
        }

        void ResetOutsideInteraction()
        {
            _hasInteractedOutside = false;
            _hasPointerDownOutside = false;
        }
    }

    /// <summary>标题（<c>role="heading"</c>，id 供内容的 <c>aria-labelledby</c> 引用）。</summary>
    public sealed partial class Title : Component
    {
        public NodeList? Children { get; set; }

        public ClassValue Class { get; set; }

        public StyleValue Style { get; set; }

        [CaptureUnmatchedAttributes]
        public AttributeDictionary? Attributes { get; set; }

        protected override Node Build()
        {
            var ctx = Use<Context>();
            var node = new UI.Text { Id = ctx.Shared.TitleId, Role = Role.Heading, Children = Children ?? new NodeList() };
            return PartProps.Apply(node, Class, Style, Attributes);
        }
    }

    /// <summary>描述（id 供内容的 <c>aria-describedby</c> 引用）。</summary>
    public sealed partial class Description : Component
    {
        public NodeList? Children { get; set; }

        public ClassValue Class { get; set; }

        public StyleValue Style { get; set; }

        [CaptureUnmatchedAttributes]
        public AttributeDictionary? Attributes { get; set; }

        protected override Node Build()
        {
            var ctx = Use<Context>();
            var node = new UI.Text { Id = ctx.Shared.DescriptionId, Children = Children ?? new NodeList() };
            return PartProps.Apply(node, Class, Style, Attributes);
        }
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
}
