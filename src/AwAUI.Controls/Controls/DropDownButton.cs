using System;
using AwAUI.Controls.Primitives;
using AwAUI.UI;

namespace AwAUI.Controls;

/// <summary>A labeled button with a dropdown indicator and a flat menu of actions.</summary>
public sealed partial class DropDownButton : ContentControl
{
    /// <summary>The menu entries, typically <see cref="MenuFlyoutItem"/> nodes.</summary>
    public NodeList? Items { get; set; }
    public string? MenuAriaLabel { get; set; }
    public bool IsEnabled { get; set; } = true;

    /// <summary>Set to control visibility, or leave null to let the menu manage it.</summary>
    public bool? IsOpen { get; set; }
    public bool DefaultIsOpen { get; set; }
    public Action<bool>? IsOpenChanged { get; set; }
    public Placement Placement { get; set; } = Placement.BottomStart;
    public float SideOffset { get; set; } = 4;

    protected override Node BuildTemplate()
    {
        var label = BuildContent();
        var indicator = new Div();
        indicator.Attributes["part"] = "indicator";
        indicator.Attributes["aria-hidden"] = "true";
        return new MenuFlyout
        {
            Id = Id,
            Class = new ClassValue("aw-drop-down-button " + Class.Text),
            Style = Style,
            Ref = Ref,
            Attributes = Attributes,
            Trigger = new Fragment { Children = [label.Count == 0 ? Present("Open menu", null) : new Fragment(label), indicator] },
            TriggerClass = "aw-drop-down-button-trigger",
            TriggerAriaLabel = AriaLabel,
            MenuAriaLabel = MenuAriaLabel,
            IsEnabled = IsEnabled,
            IsOpen = IsOpen,
            DefaultIsOpen = DefaultIsOpen,
            IsOpenChanged = IsOpenChanged,
            Placement = Placement,
            SideOffset = SideOffset,
            Children = Items,
        };
    }
}
