using System;
using AwAUI.Controls.Primitives;
using AwAUI.UI;
using UIButton = AwAUI.UI.Button;

namespace AwAUI.Shadcn;

internal static class ToggleHost
{
    internal const string Checked = "checked";
    internal const string Unchecked = "unchecked";
    internal const string Indeterminate = "indeterminate";

    internal static UIButton Create(Role role, string part, string state, string? id, bool disabled, Ref<Element>? reference,
        string defaults, string? classes, AttributeDictionary? rest, Action<PointerEvent>? onClick, Action activate)
    {
        var button = new UIButton { Role = role, Id = id, Disabled = disabled, Ref = reference, OnClick = onClick };
        button.MergeAttributes(rest);
        button.Class = AwAUI.Tailwind.TailwindMerge.Cn(defaults, button.Class.Text, classes);
        button.Attributes["type"] = "button";
        button.Attributes["data-slot"] = part;
        button.Attributes["data-state"] = state;
        button.Attributes["data-disabled"] = button.Disabled ? "" : null;
        button.Attributes["aria-checked"] = state == Indeterminate ? "mixed" : state == Checked ? "true" : "false";
        button.OnClick = EventComposition.Compose(button.OnClick, _ =>
        {
            if (!button.Disabled) activate();
        });
        return button;
    }
}
