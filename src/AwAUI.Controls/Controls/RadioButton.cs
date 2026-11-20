using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using AwAUI.Controls.Primitives;
using AwAUI.Layout;
using AwAUI.Platform;
using AwAUI.UI;

namespace AwAUI.Controls;

/// <summary>A single choice in a radio group. Use non-interactive children for the label, or set <see cref="Control.AriaLabel"/>.</summary>
public sealed partial class RadioButton : Control
{
    static readonly ConditionalWeakTable<UIRoot, Groups> s_groups = new();
    static readonly FocusOptions s_keyboardFocus = new() { Origin = FocusOrigin.Keyboard };
    readonly ControllableState<bool> _checked = new();
    Element? _element;
    Group? _group;

    /// <summary>Matching names share a group within a UI root. Null or empty names group by immediate element parent.</summary>
    public string? GroupName { get; set; }

    /// <summary>Set to control the checked state. Bind all controlled members to one selection so only one value is true.</summary>
    public bool? IsChecked { get; set; }

    public bool DefaultIsChecked { get; set; }

    /// <summary>Reports a change from an uncontrolled activation or requests selection of a controlled radio.</summary>
    public Action<bool>? IsCheckedChanged { get; set; }

    public bool IsEnabled { get; set; } = true;
    public NodeList? Children { get; set; }

    protected override Node Build()
    {
        bool requested = _checked.Sync(IsChecked, DefaultIsChecked, null);
        bool selected = _group is null ? requested : ReferenceEquals(_group.Selected, this);
        var indicator = new Div();
        indicator.Attributes["part"] = "indicator";
        indicator.Attributes["aria-hidden"] = "true";
        if (selected)
        {
            var mark = new Div();
            mark.Attributes["part"] = "mark";
            indicator.Children.Add(mark);
        }

        var button = ControlParts.BinaryButton(Role.Radio, selected, IsEnabled, indicator, Children);
        button.OnKeyDown = Move;
        Apply(button, "aw-radio-button");
        button.TabIndex = _group is null || ReferenceEquals(_group.TabStop, this) ? 0 : -1;
        button.Attributes["aria-disabled"] = button.Disabled ? "true" : null;
        ControlParts.OnActivate(button, Select);
        UseEffect(() =>
        {
            UpdateMembership();
            return null;
        }, GroupName, IsChecked.HasValue, requested);
        return button;
    }

    protected override void OnUnmount()
    {
        _group?.Remove(this);
        _group = null;
        _element = null;
    }

    void UpdateMembership()
    {
        if (Root is not { } root) return;
        if (_element is null)
        {
            var elements = GetRootElements();
            if (elements.Count == 0) return;
            _element = elements[0];
        }
        object key = GroupName is { Length: > 0 } name ? name : (_element.Parent ?? root.RootElement);
        var groups = s_groups.GetValue(root, static r => new Groups(r));
        var group = groups.Get(key);
        if (!ReferenceEquals(group, _group))
        {
            _group?.Remove(this);
            _group = group;
            group.Members.Add(this);
            root.RequestAfterLayout(InvalidateFromLayout);
        }
        group.SelectionDirty = true;
    }

    void Select()
    {
        if (_group is not { } group || _element is not { IsFocusable: true } || ReferenceEquals(group.Selected, this)) return;
        if (_checked.IsControlled)
        {
            if (!_checked.Value) IsCheckedChanged?.Invoke(true);
            return;
        }

        var previous = group.Selected;
        // A controlled selection stays selected until its owner changes the bound value.
        if (previous is not null && previous._checked.IsControlled) return;
        _checked.Set(true);
        previous?._checked.Set(false);
        group.SetSelected(this);
        previous?.IsCheckedChanged?.Invoke(false);
        IsCheckedChanged?.Invoke(true);
    }

    void Move(KeyEvent e)
    {
        const KeyModifiers modifiers = KeyModifiers.Shift | KeyModifiers.Control | KeyModifiers.Alt | KeyModifiers.Meta;
        if (e.DefaultPrevented || (e.Modifiers & modifiers) != 0 || !ReferenceEquals(e.Target, _element)
            || _element is not { IsFocusable: true } element || _group is not { } group) return;

        if (e.Key is not ("ArrowLeft" or "ArrowRight" or "ArrowUp" or "ArrowDown")) return;
        var direction = Direction.Ltr;
        for (Element? current = element; current is not null; current = current.Parent)
        {
            var dir = current.GetAttribute("dir");
            if (string.Equals(dir, "rtl", StringComparison.OrdinalIgnoreCase)) { direction = Direction.Rtl; break; }
            if (string.Equals(dir, "ltr", StringComparison.OrdinalIgnoreCase)) break;
        }

        e.PreventDefault();
        var items = group.Members.FindAll(static item => item.CanNavigate);
        items.Sort(static (a, b) => Element.CompareTreeOrder(a._element!, b._element!));
        var intent = RovingFocus.GetFocusIntent(e.Key, null, direction);
        int next = RovingFocus.Navigate(intent, items.IndexOf(this), items.Count, null, loop: true);
        if (next >= 0 && items[next]._element is { } target && target.Focus(s_keyboardFocus) && target.IsFocused)
            items[next].Select();
    }

    bool CanNavigate => _element is { IsFocusable: true, IsVisible: true };

    sealed class Groups
    {
        readonly UIRoot _root;
        readonly Dictionary<object, Group> _groups = new();

        internal Groups(UIRoot root) => _root = root;

        internal Group Get(object key)
        {
            if (_groups.TryGetValue(key, out var group)) return group;
            if (_groups.Count == 0) _root.LayoutCompleted += Update;
            _groups.Add(key, group = new Group(this, key));
            return group;
        }

        internal void Remove(object key)
        {
            _groups.Remove(key);
            if (_groups.Count == 0) _root.LayoutCompleted -= Update;
        }

        void Update(UIRoot root)
        {
            var focused = root.FocusManager.FocusedElement?.Owner as RadioButton;
            foreach (var group in _groups.Values) group.Update(focused);
        }
    }

    sealed class Group(Groups owner, object key)
    {
        internal readonly List<RadioButton> Members = new();
        internal RadioButton? Selected { get; private set; }
        internal RadioButton? TabStop { get; private set; }
        internal bool SelectionDirty { get; set; }

        internal void Remove(RadioButton member)
        {
            Members.Remove(member);
            if (ReferenceEquals(Selected, member)) Selected = null;
            if (ReferenceEquals(TabStop, member)) TabStop = null;
            SelectionDirty = true;
            if (Members.Count == 0) owner.Remove(key);
        }

        internal void SetSelected(RadioButton? member)
        {
            if (ReferenceEquals(Selected, member)) return;
            var previous = Selected;
            Selected = member;
            previous?.InvalidateFromLayout();
            member?.InvalidateFromLayout();
        }

        internal void Update(RadioButton? focused)
        {
            if (SelectionDirty)
            {
                SelectionDirty = false;
                var selected = Selected?._checked.Value == true ? Selected : null;
                foreach (var member in Members)
                {
                    if (member._checked.Value && (selected is null || (member._checked.IsControlled && !selected._checked.IsControlled)))
                        selected = member;
                }
                SetSelected(selected);
                // Discard competing defaults so removing the selected radio does not resurrect one.
                foreach (var member in Members)
                    if (!ReferenceEquals(member, selected) && !member._checked.IsControlled && member._checked.Value)
                        member._checked.Set(false);
            }

            // Visibility and order can change without any radio props changing.
            RadioButton? tabStop = focused is { CanNavigate: true } && ReferenceEquals(focused._group, this) ? focused
                : Selected is { CanNavigate: true } selectedStop ? selectedStop : null;
            if (tabStop is null)
            {
                foreach (var member in Members)
                    if (member.CanNavigate && (tabStop is null || Element.CompareTreeOrder(member._element!, tabStop._element!) < 0))
                        tabStop = member;
            }
            if (ReferenceEquals(tabStop, TabStop)) return;
            var previous = TabStop;
            TabStop = tabStop;
            previous?.InvalidateFromLayout();
            tabStop?.InvalidateFromLayout();
        }
    }
}
