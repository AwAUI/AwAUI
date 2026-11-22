using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.CompilerServices;
using AwAUI.UI;

namespace AwAUI.Controls;

/// <summary>
/// Positions children relative to each other and to the panel edges, in logical pixels.
/// </summary>
/// <remarks>
/// Each child keeps its natural size and is placed by one horizontal and one vertical relation. Set relations with
/// the static <c>Set…</c> methods in C#, or with <c>data-relative-left-of</c>, <c>data-relative-right-of</c>,
/// <c>data-relative-above</c>, <c>data-relative-below</c>, <c>data-relative-align-left-with</c>,
/// <c>data-relative-align-right-with</c>, <c>data-relative-align-top-with</c>, <c>data-relative-align-bottom-with</c>,
/// <c>data-relative-align-horizontal-center-with</c>, or <c>data-relative-align-vertical-center-with</c> in markup.
/// Attribute values name the reference child by its <c>Id</c>. Panel-relative alignment uses
/// <c>data-relative-align-left-with-panel="true</c> and the matching <c>-right</c>, <c>-top</c>, <c>-bottom</c>,
/// <c>-horizontal-center</c> and <c>-vertical-center</c> attributes.
/// <para>
/// The panel does not size to its children; give it a size through CSS or its parent. A child without a relation on an
/// axis starts at the panel's start edge on that axis. Positions are resolved after layout and reapplied when the panel
/// or a child changes size.
/// </para>
/// </remarks>
public sealed partial class RelativePanel : Control
{
    /// <summary>One axis' relation to another child or to the panel.</summary>
    sealed class Relation
    {
        internal Node? Node;
        internal string? Id;
        internal bool Panel;

        internal Relation() { }
        internal Relation(string id) => Id = id;
        internal Relation(Node node) => Node = node;

        internal static Relation ToPanel() => new() { Panel = true };
    }

    sealed class Relations
    {
        internal Relation? LeftOf, RightOf, AlignLeftWith, AlignRightWith, AlignHorizontalCenterWith;
        internal Relation? Above, Below, AlignTopWith, AlignBottomWith, AlignVerticalCenterWith;
        internal Relation? AlignLeftWithPanel, AlignRightWithPanel, AlignHorizontalCenterWithPanel;
        internal Relation? AlignTopWithPanel, AlignBottomWithPanel, AlignVerticalCenterWithPanel;
    }

    sealed class Item
    {
        internal readonly Ref<Element> Ref = new();
        internal Relations Relations = null!;
        internal Item? LeftOf, RightOf, AlignLeftWith, AlignRightWith, AlignHorizontalCenterWith;
        internal Item? Above, Below, AlignTopWith, AlignBottomWith, AlignVerticalCenterWith;
        internal bool AlignLeftWithPanel, AlignRightWithPanel, AlignHorizontalCenterWithPanel;
        internal bool AlignTopWithPanel, AlignBottomWithPanel, AlignVerticalCenterWithPanel;
        internal float Width, Height;
        internal float X, Y;
        internal float AppliedX, AppliedY;
        internal bool PlacedX, PlacedY, HasApplied;
        // 期望尺寸求解：与面板对齐的子项从面板起点开始，结果与当前面板尺寸无关
        internal float VirtualX, VirtualY;
        internal bool VirtualPlacedX, VirtualPlacedY;
    }

    static readonly ConditionalWeakTable<Node, Relations> s_relations = new();

    readonly Ref<Element> _panel = new();
    readonly List<Item> _items = new();
    bool _widthOwned, _heightOwned;

    public NodeList? Children { get; set; }

    protected override Node Build()
    {
        var panel = new Div { Ref = _panel, Style = "position:relative;min-width:0;min-height:0;" };
        panel.Attributes["part"] = "panel";
        _items.Clear();
        if (Children is { Count: > 0 } children)
        {
            var byId = new Dictionary<string, Item>(StringComparer.Ordinal);
            var byNode = new Dictionary<Node, Item>();
            for (int i = 0; i < children.Count; i++)
            {
                var child = children[i];
                var item = new Item();
                _items.Add(item);
                var wrapper = new Div
                {
                    Ref = item.Ref,
                    Style = "position:absolute;left:0;top:0;width:max-content;height:auto;",
                    Children = [child],
                };
                wrapper.Attributes["part"] = "item";
                panel.Children.Add(wrapper);
                byNode[child] = item;
                if (NodeId(child) is { Length: > 0 } id) byId[id] = item;
            }
            for (int i = 0; i < children.Count; i++)
                Resolve(_items[i], ReadRelations(children[i]), byId, byNode);
        }
        Apply(panel, "aw-relative-panel");
        panel.Ref = Ref ?? _panel;
        return panel;
    }

    protected override void OnMount()
    {
        if (Root is { } root) root.LayoutCompleted += Arrange;
    }

    protected override void OnUnmount()
    {
        if (Root is { } root) root.LayoutCompleted -= Arrange;
        _widthOwned = _heightOwned = false;
    }

    void Arrange(UIRoot _)
    {
        if (_panel.Current is not { IsRendered: true } panel || _items.Count == 0) return;
        var panelRect = panel.BoundingRect;
        if (!panelRect.IsFinite) return;

        // Sizes come from the natural layout; positions are resolved against the panel box and applied as overrides.
        bool sizeChanged = false;
        foreach (var item in _items)
        {
            item.PlacedX = false;
            item.PlacedY = false;
            if (item.Ref.Current is not { IsRendered: true } wrapper) continue;
            if (wrapper.BoundingRect.Width != item.Width || wrapper.BoundingRect.Height != item.Height)
            {
                item.Width = wrapper.BoundingRect.Width;
                item.Height = wrapper.BoundingRect.Height;
                sizeChanged = true;
            }
        }

        foreach (var item in _items)
        {
            item.X = Solve(item, horizontal: true, panelRect.Width);
            item.Y = Solve(item, horizontal: false, panelRect.Height);
        }

        foreach (var item in _items)
        {
            if (item.Ref.Current is not { } wrapper) continue;
            if (!sizeChanged && item.HasApplied && item.AppliedX == item.X && item.AppliedY == item.Y) continue;
            wrapper.SetStyleOverride("left", FormattableString.Invariant($"{item.X.ToString("R", CultureInfo.InvariantCulture)}px"));
            wrapper.SetStyleOverride("top", FormattableString.Invariant($"{item.Y.ToString("R", CultureInfo.InvariantCulture)}px"));
            (item.AppliedX, item.AppliedY, item.HasApplied) = (item.X, item.Y, true);
        }

        // 面板尺寸为 auto 的轴按子项范围回报期望尺寸：用面板起点作为与面板对齐子项的虚拟位置，
        // 结果与当前面板尺寸无关，因此父布局给出的尺寸不会反过来改变回报值（Avalonia 的 DesiredSize 行为）。
        UpdateDesiredSize(panel);
    }

    void UpdateDesiredSize(Element panel)
    {
        if (_items.Count == 0) return;
        foreach (var item in _items) (item.VirtualPlacedX, item.VirtualPlacedY) = (false, false);
        ApplyMinSize(panel, "min-width", horizontal: true, Extent(horizontal: true));
        ApplyMinSize(panel, "min-height", horizontal: false, Extent(horizontal: false));
    }

    float Extent(bool horizontal)
    {
        float extent = 0;
        foreach (var item in _items)
        {
            if (item.Ref.Current is not { IsRendered: true }) continue;
            extent = Math.Max(extent, Solve(item, horizontal, 0, virtualPanel: true) + (horizontal ? item.Width : item.Height));
        }
        return Math.Max(0, extent);
    }

    /// <summary>只有该轴自身尺寸为 auto 时才写入 <c>min-*</c>；作者已给出更大的最小尺寸或确定尺寸时不覆盖。</summary>
    void ApplyMinSize(Element panel, string property, bool horizontal, float extent)
    {
        ref bool owned = ref (horizontal ? ref _widthOwned : ref _heightOwned);
        var style = panel.ComputedStyle;
        bool auto = style is null || (horizontal ? style.Width : style.Height).IsAuto;
        if (!auto)
        {
            if (owned) panel.SetStyleOverride(property, null);
            owned = false;
            return;
        }
        if (!owned && style is not null)
        {
            var current = horizontal ? style.MinWidth : style.MinHeight;
            if (current.IsLengthPercentage && current.Value.IsFixed && current.Value.Length >= extent) return;
        }
        panel.SetStyleOverride(property, FormattableString.Invariant($"{extent.ToString("R", CultureInfo.InvariantCulture)}px"));
        owned = true;
    }

    float Solve(Item item, bool horizontal, float panelExtent, bool virtualPanel = false)
    {
        ref bool placed = ref virtualPanel
            ? ref (horizontal ? ref item.VirtualPlacedX : ref item.VirtualPlacedY)
            : ref (horizontal ? ref item.PlacedX : ref item.PlacedY);
        if (placed) return virtualPanel ? (horizontal ? item.VirtualX : item.VirtualY) : (horizontal ? item.X : item.Y);
        placed = true; // break cycles: a repeated child keeps the position assigned by its first visitor

        // Resolve the anchor first so its position is final before this child is placed.
        var anchor = horizontal
            ? item.LeftOf ?? item.RightOf ?? item.AlignLeftWith ?? item.AlignRightWith ?? item.AlignHorizontalCenterWith
            : item.Above ?? item.Below ?? item.AlignTopWith ?? item.AlignBottomWith ?? item.AlignVerticalCenterWith;
        if (anchor is not null) Solve(anchor, horizontal, panelExtent, virtualPanel);

        float value;
        if (horizontal)
        {
            if (item.LeftOf is { } left) value = Pos(left, horizontal, virtualPanel) - item.Width;
            else if (item.RightOf is { } right) value = Pos(right, horizontal, virtualPanel) + right.Width;
            else if (item.AlignLeftWith is { } alignLeft) value = Pos(alignLeft, horizontal, virtualPanel);
            else if (item.AlignRightWith is { } alignRight) value = Pos(alignRight, horizontal, virtualPanel) + alignRight.Width - item.Width;
            else if (item.AlignHorizontalCenterWith is { } center) value = Pos(center, horizontal, virtualPanel) + (center.Width - item.Width) / 2;
            else if (item.AlignLeftWithPanel) value = 0;
            else if (item.AlignRightWithPanel) value = virtualPanel ? 0 : panelExtent - item.Width;
            else if (item.AlignHorizontalCenterWithPanel) value = virtualPanel ? 0 : (panelExtent - item.Width) / 2;
            else value = 0;
            if (virtualPanel) item.VirtualX = value; else item.X = value;
        }
        else
        {
            if (item.Above is { } above) value = Pos(above, horizontal, virtualPanel) - item.Height;
            else if (item.Below is { } below) value = Pos(below, horizontal, virtualPanel) + below.Height;
            else if (item.AlignTopWith is { } alignTop) value = Pos(alignTop, horizontal, virtualPanel);
            else if (item.AlignBottomWith is { } alignBottom) value = Pos(alignBottom, horizontal, virtualPanel) + alignBottom.Height - item.Height;
            else if (item.AlignVerticalCenterWith is { } center) value = Pos(center, horizontal, virtualPanel) + (center.Height - item.Height) / 2;
            else if (item.AlignTopWithPanel) value = 0;
            else if (item.AlignBottomWithPanel) value = virtualPanel ? 0 : panelExtent - item.Height;
            else if (item.AlignVerticalCenterWithPanel) value = virtualPanel ? 0 : (panelExtent - item.Height) / 2;
            else value = 0;
            if (virtualPanel) item.VirtualY = value; else item.Y = value;
        }
        return value;
    }

    static float Pos(Item item, bool horizontal, bool virtualPanel) =>
        virtualPanel ? (horizontal ? item.VirtualX : item.VirtualY) : (horizontal ? item.X : item.Y);

    static void Resolve(Item item, Relations relations, Dictionary<string, Item> byId, Dictionary<Node, Item> byNode)
    {
        item.LeftOf = Find(relations.LeftOf);
        item.RightOf = Find(relations.RightOf);
        item.AlignLeftWith = Find(relations.AlignLeftWith);
        item.AlignRightWith = Find(relations.AlignRightWith);
        item.AlignHorizontalCenterWith = Find(relations.AlignHorizontalCenterWith);
        item.Above = Find(relations.Above);
        item.Below = Find(relations.Below);
        item.AlignTopWith = Find(relations.AlignTopWith);
        item.AlignBottomWith = Find(relations.AlignBottomWith);
        item.AlignVerticalCenterWith = Find(relations.AlignVerticalCenterWith);
        item.AlignLeftWithPanel = relations.AlignLeftWithPanel?.Panel == true;
        item.AlignRightWithPanel = relations.AlignRightWithPanel?.Panel == true;
        item.AlignHorizontalCenterWithPanel = relations.AlignHorizontalCenterWithPanel?.Panel == true;
        item.AlignTopWithPanel = relations.AlignTopWithPanel?.Panel == true;
        item.AlignBottomWithPanel = relations.AlignBottomWithPanel?.Panel == true;
        item.AlignVerticalCenterWithPanel = relations.AlignVerticalCenterWithPanel?.Panel == true;

        Item? Find(Relation? relation)
        {
            if (relation is null) return null;
            if (relation.Node is { } node) return byNode.TryGetValue(node, out var target) ? target : null;
            if (relation.Id is { } id) return byId.TryGetValue(id, out var target) ? target : null;
            return null;
        }
    }

    static string? NodeId(Node node) => node switch
    {
        ElementNode element => element.Id,
        Control control => control.Id,
        _ => null,
    };

    static Relations ReadRelations(Node child)
    {
        if (s_relations.TryGetValue(child, out var relations)) return relations;
        relations = new Relations
        {
            LeftOf = AttributeRelation(child, "data-relative-left-of"),
            RightOf = AttributeRelation(child, "data-relative-right-of"),
            Above = AttributeRelation(child, "data-relative-above"),
            Below = AttributeRelation(child, "data-relative-below"),
            AlignLeftWith = AttributeRelation(child, "data-relative-align-left-with"),
            AlignRightWith = AttributeRelation(child, "data-relative-align-right-with"),
            AlignTopWith = AttributeRelation(child, "data-relative-align-top-with"),
            AlignBottomWith = AttributeRelation(child, "data-relative-align-bottom-with"),
            AlignHorizontalCenterWith = AttributeRelation(child, "data-relative-align-horizontal-center-with"),
            AlignVerticalCenterWith = AttributeRelation(child, "data-relative-align-vertical-center-with"),
            AlignLeftWithPanel = PanelFlag(child, "data-relative-align-left-with-panel"),
            AlignRightWithPanel = PanelFlag(child, "data-relative-align-right-with-panel"),
            AlignHorizontalCenterWithPanel = PanelFlag(child, "data-relative-align-horizontal-center"),
            AlignTopWithPanel = PanelFlag(child, "data-relative-align-top-with-panel"),
            AlignBottomWithPanel = PanelFlag(child, "data-relative-align-bottom-with-panel"),
            AlignVerticalCenterWithPanel = PanelFlag(child, "data-relative-align-vertical-center"),
        };
        s_relations.Add(child, relations);
        return relations;
    }

    static Relation? AttributeRelation(Node child, string name) =>
        Convert.ToString(PanelChild.Attribute(child, name), CultureInfo.InvariantCulture) is { Length: > 0 } id
            ? new Relation(id) : null;

    static Relation? PanelFlag(Node child, string name) =>
        string.Equals(Convert.ToString(PanelChild.Attribute(child, name), CultureInfo.InvariantCulture), "true", StringComparison.OrdinalIgnoreCase)
            ? Relation.ToPanel() : null;

    // ------------------------------------------------------------ attached properties

    public static T SetLeftOf<T>(T child, Node reference) where T : Node => Set(child, reference, static (r, v) => r.LeftOf = v);
    public static T SetRightOf<T>(T child, Node reference) where T : Node => Set(child, reference, static (r, v) => r.RightOf = v);
    public static T SetAbove<T>(T child, Node reference) where T : Node => Set(child, reference, static (r, v) => r.Above = v);
    public static T SetBelow<T>(T child, Node reference) where T : Node => Set(child, reference, static (r, v) => r.Below = v);
    public static T SetAlignLeftWith<T>(T child, Node reference) where T : Node => Set(child, reference, static (r, v) => r.AlignLeftWith = v);
    public static T SetAlignRightWith<T>(T child, Node reference) where T : Node => Set(child, reference, static (r, v) => r.AlignRightWith = v);
    public static T SetAlignTopWith<T>(T child, Node reference) where T : Node => Set(child, reference, static (r, v) => r.AlignTopWith = v);
    public static T SetAlignBottomWith<T>(T child, Node reference) where T : Node => Set(child, reference, static (r, v) => r.AlignBottomWith = v);
    public static T SetAlignHorizontalCenterWith<T>(T child, Node reference) where T : Node => Set(child, reference, static (r, v) => r.AlignHorizontalCenterWith = v);
    public static T SetAlignVerticalCenterWith<T>(T child, Node reference) where T : Node => Set(child, reference, static (r, v) => r.AlignVerticalCenterWith = v);

    public static T SetAlignLeftWithPanel<T>(T child, bool value = true) where T : Node => SetPanel(child, value, static (r, v) => r.AlignLeftWithPanel = v);
    public static T SetAlignRightWithPanel<T>(T child, bool value = true) where T : Node => SetPanel(child, value, static (r, v) => r.AlignRightWithPanel = v);
    public static T SetAlignHorizontalCenterWithPanel<T>(T child, bool value = true) where T : Node => SetPanel(child, value, static (r, v) => r.AlignHorizontalCenterWithPanel = v);
    public static T SetAlignTopWithPanel<T>(T child, bool value = true) where T : Node => SetPanel(child, value, static (r, v) => r.AlignTopWithPanel = v);
    public static T SetAlignBottomWithPanel<T>(T child, bool value = true) where T : Node => SetPanel(child, value, static (r, v) => r.AlignBottomWithPanel = v);
    public static T SetAlignVerticalCenterWithPanel<T>(T child, bool value = true) where T : Node => SetPanel(child, value, static (r, v) => r.AlignVerticalCenterWithPanel = v);

    static T Set<T>(T child, Node reference, Action<Relations, Relation> set) where T : Node
    {
        if (child is null) throw new ArgumentNullException(nameof(child));
        if (reference is null) throw new ArgumentNullException(nameof(reference));
        set(RelationsOf(child), new Relation(reference));
        return child;
    }

    static T SetPanel<T>(T child, bool value, Action<Relations, Relation?> set) where T : Node
    {
        if (child is null) throw new ArgumentNullException(nameof(child));
        set(RelationsOf(child), value ? Relation.ToPanel() : null);
        return child;
    }

    static Relations RelationsOf(Node child) =>
        s_relations.GetValue(child ?? throw new ArgumentNullException(nameof(child)), static _ => new Relations());
}
