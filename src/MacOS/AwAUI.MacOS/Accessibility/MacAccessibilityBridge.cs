using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;
using AwAUI.Platform;
using AwAUI.MacOS.Interop;
using static AwAUI.MacOS.Interop.ObjC;

namespace AwAUI.MacOS.Accessibility;

/// <summary>
/// macOS NSAccessibility 桥（design/10「平台桥」）：把语义树快照映射为一棵
/// <c>AwAUIAccessibilityElement</c> 代理元素，并给窗口内容视图补上 <c>accessibilityChildren</c> /
/// <c>accessibilityFocusedUIElement</c> 等消息。
/// <para>
/// 只使用裸 ObjC 运行时（design/04 D13），不依赖 net-macos workload。类注册在第一次创建窗口时惰性完成；
/// 注册失败或设置 <c>AWAUI_MAC_A11Y=0</c> 时 <see cref="TryCreate"/> 返回 null，窗口的
/// <see cref="WindowImpl.Accessibility"/> 因此为 null，框架不会创建语义树。
/// </para>
/// <para>所有原生对象都只有托管状态的一份映射（<see cref="MacAccessibilityRegistry"/>），回调经静态入口回到托管世界。</para>
/// </summary>
internal sealed class MacAccessibilityBridge : AccessibilityBridge, IDisposable
{
    /// <summary>代理元素的原生类名。</summary>
    internal const string ElementClassName = "AwAUIAccessibilityElement";

    const string FallbackSuperclass = "NSObject";

    static readonly object RegistrationGate = new object();
    static readonly Dictionary<string, IntPtr> NotificationConstants = new Dictionary<string, IntPtr>(StringComparer.Ordinal);
    static bool s_registered;
    static bool s_available;

    readonly IntPtr _view;
    readonly IntPtr _window;
    readonly Dictionary<string, MacA11yNode> _nodesById = new Dictionary<string, MacA11yNode>(StringComparer.Ordinal);
    readonly List<MacA11yNode> _nodes = new List<MacA11yNode>();

    MacA11yNode? _root;
    string? _focusedId;
    long _version = -1;
    bool _disposed;

    MacAccessibilityBridge(IntPtr view, IntPtr window)
    {
        _view = view;
        _window = window;
    }

    /// <summary>
    /// 创建桥并把它注册到内容视图；不可用（非 macOS、环境变量关闭、类注册失败）时返回 null，绝不抛异常。
    /// </summary>
    public static MacAccessibilityBridge? TryCreate(IntPtr view, IntPtr window)
    {
        if (view == IntPtr.Zero || window == IntPtr.Zero) return null;
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.OSX)) return null;
        var flag = Environment.GetEnvironmentVariable("AWAUI_MAC_A11Y");
        if (string.Equals(flag, "0", StringComparison.Ordinal)) return null;
        if (!EnsureRegistered()) return null;
        try
        {
            var bridge = new MacAccessibilityBridge(view, window);
            MacAccessibilityRegistry.RegisterView(view, bridge);
            return bridge;
        }
        catch (Exception e)
        {
            Report(e);
            return null;
        }
    }

    /// <summary>一次性注册代理元素类、给 AwAUIView 补消息，并挂接静态回调。</summary>
    static bool EnsureRegistered()
    {
        lock (RegistrationGate)
        {
            if (s_registered) return s_available;
            s_registered = true;
            try
            {
                Callbacks.OnA11yObject = DispatchObject;
                Callbacks.OnA11yObjectArg = DispatchObjectArg;
                Callbacks.OnA11yBool = DispatchBool;
                Callbacks.OnA11yBoolArg = DispatchBoolArg;
                Callbacks.OnA11ySetObject = DispatchSetObject;
                Callbacks.OnA11ySetObject2 = DispatchSetObject2;
                Callbacks.OnA11yRect = DispatchRect;

                string superclass = TryClass("NSAccessibilityElement") != IntPtr.Zero
                    ? "NSAccessibilityElement"
                    : FallbackSuperclass;
                DefineClass(ElementClassName, superclass, BuildElementMethods(), new[] { "NSAccessibility" });

                var viewClass = TryClass("AwAUIView");
                if (viewClass == IntPtr.Zero) throw new InvalidOperationException("AwAUIView is not registered.");

                // 内容视图是容器：向外提供语义根与当前焦点元素。
                AddMethod(viewClass, "isAccessibilityElement", Callbacks.A11yBool, "c@:");
                AddMethod(viewClass, "accessibilityChildren", Callbacks.A11yObject, "@@:");
                AddMethod(viewClass, "accessibilityFocusedUIElement", Callbacks.A11yObject, "@@:");
                AddMethod(viewClass, "accessibilityRole", Callbacks.A11yObject, "@@:");
                AddMethod(viewClass, "accessibilityLabel", Callbacks.A11yObject, "@@:");

                s_available = true;
            }
            catch (Exception e)
            {
                Report(e);
                s_available = false;
            }
            return s_available;
        }
    }

    static List<(string Selector, IntPtr Imp, string Types)> BuildElementMethods()
    {
        const string id = "@@:";
        const string idArg = "@@:@";
        const string boolSel = "c@:";
        const string boolArg = "c@:@";
        const string voidArg = "v@:@";
        const string voidArg2 = "v@:@@";
        const string rect = "{CGRect={CGPoint=dd}{CGSize=dd}}@:";

        return new List<(string, IntPtr, string)>
        {
            // 现代 NSAccessibility 属性访问器
            ("accessibilityRole", Callbacks.A11yObject, id),
            ("accessibilitySubrole", Callbacks.A11yObject, id),
            ("accessibilityLabel", Callbacks.A11yObject, id),
            ("accessibilityTitle", Callbacks.A11yObject, id),
            ("accessibilityValue", Callbacks.A11yObject, id),
            ("accessibilityHelp", Callbacks.A11yObject, id),
            ("accessibilityIdentifier", Callbacks.A11yObject, id),
            ("accessibilityChildren", Callbacks.A11yObject, id),
            ("accessibilityParent", Callbacks.A11yObject, id),
            ("accessibilityMinValue", Callbacks.A11yObject, id),
            ("accessibilityMaxValue", Callbacks.A11yObject, id),
            ("accessibilityOrientation", Callbacks.A11yObject, id),
            ("accessibilityRowCount", Callbacks.A11yObject, id),
            ("accessibilityColumnCount", Callbacks.A11yObject, id),
            ("accessibilityIndexInParent", Callbacks.A11yObject, id),
            ("accessibilityFrame", Callbacks.A11yRect, rect),
            ("isAccessibilityElement", Callbacks.A11yBool, boolSel),
            ("isAccessibilityEnabled", Callbacks.A11yBool, boolSel),
            ("isAccessibilityFocused", Callbacks.A11yBool, boolSel),
            ("isAccessibilitySelected", Callbacks.A11yBool, boolSel),
            ("accessibilityIsIgnored", Callbacks.A11yBool, boolSel),

            // 动作
            ("accessibilityPerformPress", Callbacks.A11yBool, boolSel),
            ("accessibilityPerformIncrement", Callbacks.A11yBool, boolSel),
            ("accessibilityPerformDecrement", Callbacks.A11yBool, boolSel),
            ("accessibilityPerformConfirm", Callbacks.A11yBool, boolSel),
            ("accessibilityPerformShowMenu", Callbacks.A11yBool, boolSel),
            ("accessibilityPerformCancel", Callbacks.A11yBool, boolSel),
            ("accessibilityScrollToVisible", Callbacks.A11yBool, boolSel),
            ("setAccessibilityValue:", Callbacks.A11ySetObject, voidArg),

            // 旧式（attribute / action）协议，兼容未走现代属性访问器的系统
            ("accessibilityAttributeNames", Callbacks.A11yObject, id),
            ("accessibilityAttributeValue:", Callbacks.A11yObjectArg, idArg),
            ("accessibilityActionNames", Callbacks.A11yObject, id),
            ("accessibilityActionDescription:", Callbacks.A11yObjectArg, idArg),
            ("accessibilityIsAttributeSettable:", Callbacks.A11yBoolArg, boolArg),
            ("accessibilitySetValue:forAttribute:", Callbacks.A11ySetObject2, voidArg2),
            ("accessibilityPerformAction:", Callbacks.A11ySetObject, voidArg),
        };
    }

    // ---------------------------------------------------------------- 快照 → 代理树

    /// <inheritdoc />
    public override void Update(AccessibilitySnapshot snapshot)
    {
        if (snapshot is null) throw new ArgumentNullException(nameof(snapshot));
        if (_disposed) return;

        bool initial = _version < 0;
        string? previousRootId = _root?.Id;
        string? previousFocused = _focusedId;
        bool layoutChanged = initial;
        bool removedAny = false;

        var valueChanges = new List<MacA11yNode>();
        var selectedChanges = new List<MacA11yNode>();
        var announcements = new List<Announcement>();
        var source = snapshot.Nodes ?? Array.Empty<AccessibilityNodeSnapshot>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var ordered = new List<MacA11yNode>(source.Count);

        for (int i = 0; i < source.Count; i++)
        {
            var src = source[i];
            if (src is null || string.IsNullOrEmpty(src.Id)) continue;
            if (!seen.Add(src.Id)) continue;

            _nodesById.TryGetValue(src.Id, out var node);
            bool created = node is null;
            if (node is null)
            {
                node = new MacA11yNode(src.Id, this, CreateNativeElement());
                _nodesById[src.Id] = node;
                MacAccessibilityRegistry.RegisterNode(node.Handle, node);
            }

            string? oldValue = node.ValueText;
            string? oldName = node.Name;
            bool oldSelected = node.Selected;
            bool oldFocused = node.Focused;
            Rect oldBounds = node.Bounds;
            string? oldParentId = node.ParentId;

            Apply(node, src);
            ordered.Add(node);

            if (created || !oldBounds.Equals(node.Bounds) || !string.Equals(oldParentId, node.ParentId, StringComparison.Ordinal))
                layoutChanged = true;

            if (!created && !string.Equals(oldValue, node.ValueText, StringComparison.Ordinal)) valueChanges.Add(node);
            if (!created && oldSelected != node.Selected) selectedChanges.Add(node);

            if (node.LiveRegion != AccessibilityPoliteness.Off && node.Handle != IntPtr.Zero &&
                (created || !string.Equals(oldValue, node.ValueText, StringComparison.Ordinal) ||
                 !string.Equals(oldName, node.Name, StringComparison.Ordinal)))
            {
                string text = !string.IsNullOrEmpty(node.Name) ? node.Name! : (node.ValueText ?? "");
                if (text.Length > 0) announcements.Add(new Announcement(node, text, node.LiveRegion));
            }

            // 记录焦点变化（即使焦点节点本身没有值变化）
            if (oldFocused != node.Focused) layoutChanged = true;
        }

        // 移除快照里已不存在的节点
        for (int i = _nodes.Count - 1; i >= 0; i--)
        {
            var node = _nodes[i];
            if (seen.Contains(node.Id)) continue;
            _nodes.RemoveAt(i);
            _nodesById.Remove(node.Id);
            MacAccessibilityRegistry.UnregisterNode(node.Handle);
            Release(node.Handle);
            removedAny = true;
        }
        if (removedAny) layoutChanged = true;

        _nodes.Clear();
        _nodes.AddRange(ordered);

        LinkTree(source);

        _root = snapshot.RootId is not null && _nodesById.TryGetValue(snapshot.RootId, out var root) ? root : (_nodes.Count > 0 ? _nodes[0] : null);
        if (!string.Equals(previousRootId, _root?.Id, StringComparison.Ordinal)) layoutChanged = true;

        MacA11yNode? focused = null;
        for (int i = 0; i < _nodes.Count; i++)
        {
            if (_nodes[i].Focused) { focused = _nodes[i]; break; }
        }
        _focusedId = focused?.Id;
        _version = snapshot.Version;

        if (_disposed || !IsMainThread()) return;

        if (layoutChanged) PostNotification(MacAccessibilityNotification.LayoutChanged, RootElement());
        if (!string.Equals(previousFocused, _focusedId, StringComparison.Ordinal))
            PostNotification(MacAccessibilityNotification.FocusedUIElementChanged, _window);

        for (int i = 0; i < valueChanges.Count; i++)
            PostNotification(MacAccessibilityNotification.ValueChanged, valueChanges[i].Handle);

        for (int i = 0; i < selectedChanges.Count; i++)
        {
            var node = selectedChanges[i];
            PostNotification(MacAccessibilityNotification.SelectedChildrenChanged,
                node.Parent is { } parent ? parent.Handle : node.Handle);
        }

        for (int i = 0; i < announcements.Count; i++)
        {
            var announcement = announcements[i];
            Announce(announcement.Node, announcement.Text, announcement.Politeness);
        }
    }

    void Apply(MacA11yNode node, AccessibilityNodeSnapshot src)
    {
        var states = src.States ?? Array.Empty<string>();
        node.Role = src.Role ?? "";
        node.AxRole = MacAccessibility.Role(node.Role);
        node.AxSubrole = MacAccessibility.Subrole(node.Role);
        node.Name = src.Name;
        node.Description = src.Description;
        node.ValueText = src.Value;
        node.FormattedValue = MacAccessibility.FormatValue(node.Role, src.Value, states);
        node.Bounds = src.Bounds;
        node.ParentId = src.ParentId;
        node.Actions = src.Actions ?? Array.Empty<AccessibilityActionKind>();
        node.LiveRegion = src.LiveRegion;
        node.Disabled = MacAccessibility.ContainsState(states, "disabled");
        node.Focused = MacAccessibility.ContainsState(states, "focused");
        node.Selected = MacAccessibility.ContainsState(states, "selected");
        node.Expanded = MacAccessibility.ContainsState(states, "expanded");
        node.Busy = MacAccessibility.ContainsState(states, "busy");
        node.Required = MacAccessibility.ContainsState(states, "required");
        node.Mixed = MacAccessibility.ContainsState(states, "mixed");
        node.Writable = src.Text is not null && node.HasAction(AccessibilityActionKind.SetValue);
        node.Orientation = node.AxRole == "AXSlider" ? MacAccessibility.Orientation(node.Bounds) : null;

        if (MacAccessibility.TryGetRange(node.Role, out double min, out double max))
        {
            node.HasRange = true;
            node.MinValue = min;
            node.MaxValue = max;
        }
        else
        {
            node.HasRange = false;
            node.MinValue = 0;
            node.MaxValue = 0;
        }
    }

    void LinkTree(IReadOnlyList<AccessibilityNodeSnapshot> source)
    {
        for (int i = 0; i < _nodes.Count; i++) _nodes[i].Children.Clear();

        for (int i = 0; i < source.Count; i++)
        {
            var src = source[i];
            if (src is null || !_nodesById.TryGetValue(src.Id, out var node)) continue;
            var childIds = src.ChildIds ?? Array.Empty<string>();
            for (int c = 0; c < childIds.Count; c++)
            {
                if (!_nodesById.TryGetValue(childIds[c], out var child)) continue;
                child.Parent = node;
                child.IndexInParent = node.Children.Count;
                node.Children.Add(child);
            }
        }

        for (int i = 0; i < _nodes.Count; i++)
        {
            var node = _nodes[i];
            if (node.AxRole == "AXTable" || node.AxRole == "AXList")
            {
                node.RowCount = node.Children.Count;
                int columns = 0;
                if (node.AxRole == "AXTable")
                {
                    for (int c = 0; c < node.Children.Count; c++)
                        if (node.Children[c].Children.Count > columns) columns = node.Children[c].Children.Count;
                }
                node.ColumnCount = node.AxRole == "AXTable" ? columns : -1;
            }
            else
            {
                node.RowCount = -1;
                node.ColumnCount = -1;
            }
        }
    }

    IntPtr CreateNativeElement()
    {
        if (_disposed) return IntPtr.Zero;
        return Send(Send(Class(ElementClassName), Sel("alloc")), Sel("init"));
    }

    // ---------------------------------------------------------------- 原生回调（静态入口）

    static IntPtr DispatchObject(IntPtr self, IntPtr sel)
    {
        if (MacAccessibilityRegistry.TryGetNode(self, out var node) && node is not null)
            return node.Owner.GetNodeObject(node, sel);
        if (MacAccessibilityRegistry.TryGetView(self, out var bridge) && bridge is not null)
            return bridge.GetViewObject(sel);
        return IntPtr.Zero;
    }

    static IntPtr DispatchObjectArg(IntPtr self, IntPtr sel, IntPtr arg)
    {
        if (MacAccessibilityRegistry.TryGetNode(self, out var node) && node is not null)
            return node.Owner.GetNodeObjectArg(node, sel, arg);
        return IntPtr.Zero;
    }

    static bool DispatchBool(IntPtr self, IntPtr sel)
    {
        if (MacAccessibilityRegistry.TryGetNode(self, out var node) && node is not null)
            return node.Owner.GetNodeBool(node, sel);
        if (MacAccessibilityRegistry.TryGetView(self, out var bridge) && bridge is not null)
            return bridge.GetViewBool(sel);
        return false;
    }

    static bool DispatchBoolArg(IntPtr self, IntPtr sel, IntPtr arg)
    {
        if (MacAccessibilityRegistry.TryGetNode(self, out var node) && node is not null)
            return node.Owner.GetNodeBoolArg(node, sel, arg);
        return false;
    }

    static void DispatchSetObject(IntPtr self, IntPtr sel, IntPtr arg)
    {
        if (MacAccessibilityRegistry.TryGetNode(self, out var node) && node is not null)
            node.Owner.SetNodeValue(node, sel, arg);
    }

    static void DispatchSetObject2(IntPtr self, IntPtr sel, IntPtr arg1, IntPtr arg2)
    {
        if (MacAccessibilityRegistry.TryGetNode(self, out var node) && node is not null)
            node.Owner.SetNodeValueForAttribute(node, sel, arg1, arg2);
    }

    static NSRect DispatchRect(IntPtr self, IntPtr sel)
    {
        if (MacAccessibilityRegistry.TryGetNode(self, out var node) && node is not null)
            return node.Owner.GetNodeRect(node, sel);
        return default;
    }

    // ---------------------------------------------------------------- 代理元素属性

    IntPtr GetNodeObject(MacA11yNode node, IntPtr sel)
    {
        if (_disposed) return IntPtr.Zero;
        if (sel == Sel("accessibilityRole")) return TextObject(node.AxRole);
        if (sel == Sel("accessibilitySubrole")) return TextObject(node.AxSubrole);
        if (sel == Sel("accessibilityLabel")) return TextObject(node.Name);
        if (sel == Sel("accessibilityTitle")) return TextObject(node.Name);
        if (sel == Sel("accessibilityValue")) return ValueObject(node.FormattedValue);
        if (sel == Sel("accessibilityHelp")) return TextObject(node.Description);
        if (sel == Sel("accessibilityIdentifier")) return TextObject(node.Id);
        if (sel == Sel("accessibilityChildren")) return ChildrenArray(node);
        if (sel == Sel("accessibilityParent")) return node.Parent is { } parent ? parent.Handle : _view;
        if (sel == Sel("accessibilityMinValue")) return node.HasRange ? NewNumber(node.MinValue) : IntPtr.Zero;
        if (sel == Sel("accessibilityMaxValue")) return node.HasRange ? NewNumber(node.MaxValue) : IntPtr.Zero;
        if (sel == Sel("accessibilityOrientation")) return TextObject(node.Orientation);
        if (sel == Sel("accessibilityRowCount")) return node.RowCount >= 0 ? NewInteger(node.RowCount) : IntPtr.Zero;
        if (sel == Sel("accessibilityColumnCount")) return node.ColumnCount >= 0 ? NewInteger(node.ColumnCount) : IntPtr.Zero;
        if (sel == Sel("accessibilityIndexInParent")) return NewInteger(node.IndexInParent);
        if (sel == Sel("accessibilityAttributeNames")) return AttributeNamesArray();
        if (sel == Sel("accessibilityActionNames")) return ActionNamesArray(node);
        return IntPtr.Zero;
    }

    IntPtr GetNodeObjectArg(MacA11yNode node, IntPtr sel, IntPtr arg)
    {
        if (_disposed) return IntPtr.Zero;
        if (sel == Sel("accessibilityAttributeValue:")) return AttributeValue(node, arg);
        return IntPtr.Zero;
    }

    bool GetNodeBool(MacA11yNode node, IntPtr sel)
    {
        if (_disposed) return false;
        if (sel == Sel("isAccessibilityElement")) return node.Handle != IntPtr.Zero;
        if (sel == Sel("isAccessibilityEnabled")) return !node.Disabled;
        if (sel == Sel("isAccessibilityFocused")) return node.Focused;
        if (sel == Sel("isAccessibilitySelected")) return node.Selected;
        if (sel == Sel("accessibilityIsIgnored")) return false;
        if (sel == Sel("accessibilityPerformPress") || sel == Sel("accessibilityPerformConfirm"))
            return Perform(node, AccessibilityActionKind.Invoke);
        if (sel == Sel("accessibilityPerformIncrement")) return Perform(node, AccessibilityActionKind.Increment);
        if (sel == Sel("accessibilityPerformDecrement")) return Perform(node, AccessibilityActionKind.Decrement);
        if (sel == Sel("accessibilityPerformShowMenu")) return Perform(node, AccessibilityActionKind.Expand);
        if (sel == Sel("accessibilityPerformCancel")) return Perform(node, AccessibilityActionKind.Collapse);
        if (sel == Sel("accessibilityScrollToVisible")) return Perform(node, AccessibilityActionKind.ScrollIntoView);
        return false;
    }

    bool GetNodeBoolArg(MacA11yNode node, IntPtr sel, IntPtr arg)
    {
        if (_disposed) return false;
        if (sel == Sel("accessibilityIsAttributeSettable:"))
        {
            var attribute = GetString(arg);
            return string.Equals(attribute, "AXValue", StringComparison.Ordinal) && node.Writable;
        }
        return false;
    }

    void SetNodeValue(MacA11yNode node, IntPtr sel, IntPtr arg)
    {
        if (_disposed) return;
        if (sel == Sel("accessibilityPerformAction:"))
        {
            var kind = MacAccessibility.ActionKind(GetString(arg));
            if (kind.HasValue) Raise(node, kind.Value, null);
            return;
        }
        if (sel != Sel("setAccessibilityValue:") || !node.Writable) return;
        var value = StringFromObject(arg);
        if (value is not null) Raise(node, AccessibilityActionKind.SetValue, value);
    }

    void SetNodeValueForAttribute(MacA11yNode node, IntPtr sel, IntPtr value, IntPtr attribute)
    {
        if (_disposed || !node.Writable) return;
        if (sel != Sel("accessibilitySetValue:forAttribute:")) return;
        var name = GetString(attribute);
        if (!string.Equals(name, "AXValue", StringComparison.Ordinal)) return;
        var text = StringFromObject(value);
        if (text is not null) Raise(node, AccessibilityActionKind.SetValue, text);
    }

    NSRect GetNodeRect(MacA11yNode node, IntPtr sel)
    {
        if (_disposed || sel != Sel("accessibilityFrame")) return default;
        var rect = FrameFor(node);
        return new NSRect(rect.X, rect.Y, rect.Width, rect.Height);
    }

    IntPtr AttributeValue(MacA11yNode node, IntPtr attribute)
    {
        var name = GetString(attribute);
        if (name is null) return IntPtr.Zero;
        switch (name)
        {
            case "AXRole": return TextObject(node.AxRole);
            case "AXSubrole": return TextObject(node.AxSubrole);
            case "AXTitle": return TextObject(node.Name);
            case "AXDescription": return TextObject(node.Description);
            case "AXHelp": return TextObject(node.Description);
            case "AXValue": return ValueObject(node.FormattedValue);
            case "AXIdentifier": return TextObject(node.Id);
            case "AXEnabled": return NewInteger(node.Disabled ? 0 : 1);
            case "AXFocused": return NewInteger(node.Focused ? 1 : 0);
            case "AXSelected": return NewInteger(node.Selected ? 1 : 0);
            case "AXExpanded": return node.Expanded ? NewInteger(1) : IntPtr.Zero;
            case "AXRequired": return NewInteger(node.Required ? 1 : 0);
            case "AXElementBusy": return NewInteger(node.Busy ? 1 : 0);
            case "AXChildren": return ChildrenArray(node);
            case "AXParent": return node.Parent is { } parent ? parent.Handle : _view;
            case "AXFrame": return NewRectValue(new NSRect(FrameFor(node).X, FrameFor(node).Y, FrameFor(node).Width, FrameFor(node).Height));
            case "AXMinValue": return node.HasRange ? NewNumber(node.MinValue) : IntPtr.Zero;
            case "AXMaxValue": return node.HasRange ? NewNumber(node.MaxValue) : IntPtr.Zero;
            case "AXOrientation": return TextObject(node.Orientation);
            case "AXRowCount": return node.RowCount >= 0 ? NewInteger(node.RowCount) : IntPtr.Zero;
            case "AXColumnCount": return node.ColumnCount >= 0 ? NewInteger(node.ColumnCount) : IntPtr.Zero;
            case "AXIndexInParent": return NewInteger(node.IndexInParent);
            case "AXWindow":
            case "AXTopLevelUIElement": return _window;
            default: return IntPtr.Zero;
        }
    }

    IntPtr AttributeNamesArray()
    {
        var array = NewMutableArray();
        AddString(array, "AXRole");
        AddString(array, "AXSubrole");
        AddString(array, "AXTitle");
        AddString(array, "AXDescription");
        AddString(array, "AXHelp");
        AddString(array, "AXValue");
        AddString(array, "AXIdentifier");
        AddString(array, "AXEnabled");
        AddString(array, "AXFocused");
        AddString(array, "AXSelected");
        AddString(array, "AXExpanded");
        AddString(array, "AXRequired");
        AddString(array, "AXChildren");
        AddString(array, "AXParent");
        AddString(array, "AXFrame");
        AddString(array, "AXMinValue");
        AddString(array, "AXMaxValue");
        AddString(array, "AXOrientation");
        AddString(array, "AXRowCount");
        AddString(array, "AXColumnCount");
        AddString(array, "AXIndexInParent");
        return array;
    }

    IntPtr ActionNamesArray(MacA11yNode node)
    {
        var array = NewMutableArray();
        var names = MacAccessibility.ActionNamesFor(node.Actions, node.Role);
        for (int i = 0; i < names.Count; i++) AddString(array, names[i]);
        return array;
    }

    IntPtr ChildrenArray(MacA11yNode node)
    {
        var array = NewMutableArray();
        for (int i = 0; i < node.Children.Count; i++) ArrayAdd(array, node.Children[i].Handle);
        return array;
    }

    bool Perform(MacA11yNode node, AccessibilityActionKind action) => Raise(node, action, null);

    bool Raise(MacA11yNode node, AccessibilityActionKind action, string? value)
    {
        if (_disposed) return false;
        return RaiseActionRequested(new AccessibilityActionRequest(node.Id, action, value));
    }

    // ---------------------------------------------------------------- 根视图属性

    IntPtr GetViewObject(IntPtr sel)
    {
        if (_disposed) return IntPtr.Zero;
        if (sel == Sel("accessibilityChildren")) return RootChildrenArray();
        if (sel == Sel("accessibilityFocusedUIElement")) return FocusedNode()?.Handle ?? _root?.Handle ?? IntPtr.Zero;
        if (sel == Sel("accessibilityRole")) return TextObject("AXGroup");
        return IntPtr.Zero;
    }

    bool GetViewBool(IntPtr sel)
    {
        if (_disposed) return false;
        if (sel == Sel("isAccessibilityElement")) return false; // 容器
        if (sel == Sel("accessibilityIsIgnored")) return false;
        return false;
    }

    IntPtr RootChildrenArray()
    {
        var array = NewMutableArray();
        if (_root is not null) ArrayAdd(array, _root.Handle);
        return array;
    }

    MacA11yNode? FocusedNode()
    {
        for (int i = 0; i < _nodes.Count; i++)
            if (_nodes[i].Focused) return _nodes[i];
        return null;
    }

    // ---------------------------------------------------------------- 几何与通知

    MacRect FrameFor(MacA11yNode node)
    {
        var viewBounds = SendRect(_view, Sel("bounds"));
        var windowFrame = SendRect(_window, Sel("frame"));
        return MacAccessibility.ToScreenCocoa(node.Bounds, viewBounds.Size.Height,
            windowFrame.Origin.X, windowFrame.Origin.Y);
    }

    IntPtr RootElement() => _root?.Handle ?? _view;

    /// <summary>窗口获得焦点 / 失焦（由 <see cref="MacWindow"/> 转发）：通知辅助技术重新查询焦点元素。</summary>
    internal void OnWindowFocusChanged(bool active)
    {
        if (_disposed || !active || !IsMainThread()) return;
        PostNotification(MacAccessibilityNotification.FocusedUIElementChanged, _window);
    }

    /// <summary>窗口移动 / 缩放（由 <see cref="MacWindow"/> 转发）：屏幕坐标的 AXFrame 已变化。</summary>
    internal void OnGeometryChanged()
    {
        if (_disposed || !IsMainThread()) return;
        PostNotification(MacAccessibilityNotification.LayoutChanged, RootElement());
    }

    void PostNotification(MacAccessibilityNotification kind, IntPtr element)
    {
        if (element == IntPtr.Zero) return;
        var notification = NotificationConstant(kind);
        if (notification == IntPtr.Zero) return;
        PostAccessibilityNotification(element, notification);
    }

    void Announce(MacA11yNode node, string text, AccessibilityPoliteness politeness)
    {
        var notification = NotificationConstant(MacAccessibilityNotification.AnnouncementRequested);
        if (notification == IntPtr.Zero)
        {
            PostNotification(MacAccessibilityNotification.ValueChanged, node.Handle);
            return;
        }

        var announcementKey = CG.GetConstant(AppKit, "NSAccessibilityAnnouncementKey");
        var priorityKey = CG.GetConstant(AppKit, "NSAccessibilityPriorityKey");
        if (announcementKey == IntPtr.Zero || priorityKey == IntPtr.Zero)
        {
            PostAccessibilityNotification(node.Handle, notification);
            return;
        }

        var userInfo = NewMutableDictionary();
        DictionarySet(userInfo, NewAutoreleasedString(text), announcementKey);
        DictionarySet(userInfo, NewInteger(politeness == AccessibilityPoliteness.Assertive ? 90 : 50), priorityKey);
        PostAccessibilityNotificationWithUserInfo(node.Handle, notification, userInfo);
    }

    static IntPtr NotificationConstant(MacAccessibilityNotification kind)
    {
        string name = MacAccessibility.NotificationName(kind);
        lock (NotificationConstants)
        {
            if (NotificationConstants.TryGetValue(name, out var cached)) return cached;
            var constant = CG.GetConstant(AppKit, name);
            NotificationConstants[name] = constant;
            return constant;
        }
    }

    static bool IsMainThread()
    {
        try { return SendByte(Class("NSThread"), Sel("isMainThread")) != 0; }
        catch (Exception) { return false; }
    }

    // ---------------------------------------------------------------- 值辅助

    static IntPtr TextObject(string? text) => text is null ? IntPtr.Zero : NewAutoreleasedString(text);

    static IntPtr ValueObject(MacAccessibilityValue value)
    {
        switch (value.Kind)
        {
            case MacAccessibilityValueKind.Text: return TextObject(value.Text);
            case MacAccessibilityValueKind.Number: return NewNumber(value.Number);
            case MacAccessibilityValueKind.Boolean: return NewInteger(value.Boolean ? 1 : 0);
            default: return IntPtr.Zero;
        }
    }

    static void AddString(IntPtr array, string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        ArrayAdd(array, NewAutoreleasedString(text));
    }

    static string? StringFromObject(IntPtr obj)
    {
        if (obj == IntPtr.Zero) return null;
        if (SendByte(obj, Sel("isKindOfClass:"), Class("NSString")) != 0) return GetString(obj);
        if (SendByte(obj, Sel("isKindOfClass:"), Class("NSNumber")) != 0)
            return SendDouble(obj, Sel("doubleValue")).ToString("R", CultureInfo.InvariantCulture);
        return GetString(Send(obj, Sel("description")));
    }

    static void Report(Exception e)
    {
        try { Console.Error.WriteLine("[AwAUI.MacOS] Accessibility bridge unavailable: " + e); } catch { }
    }

    // ---------------------------------------------------------------- 生命周期

    /// <summary>断开视图注册并释放全部代理元素。</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        MacAccessibilityRegistry.UnregisterView(_view);
        for (int i = 0; i < _nodes.Count; i++)
        {
            var node = _nodes[i];
            MacAccessibilityRegistry.UnregisterNode(node.Handle);
            Release(node.Handle);
        }
        _nodes.Clear();
        _nodesById.Clear();
        _root = null;
        _focusedId = null;
    }

    readonly struct Announcement
    {
        public Announcement(MacA11yNode node, string text, AccessibilityPoliteness politeness)
        {
            Node = node;
            Text = text;
            Politeness = politeness;
        }

        public MacA11yNode Node { get; }
        public string Text { get; }
        public AccessibilityPoliteness Politeness { get; }
    }
}
