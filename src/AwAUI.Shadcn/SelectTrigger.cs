using AwAUI.Controls.Primitives;
using AwAUI.Platform;
using AwAUI.UI;
using PopoverPrimitive = AwAUI.Controls.Primitives.Popover;
using UIButton = AwAUI.UI.Button;

namespace AwAUI.Shadcn;

public enum SelectSize : byte
{
    Default,
    Sm,
}

/// <summary>The select-only combobox button and popup anchor.</summary>
public sealed partial class SelectTrigger : Component
{
    const string TriggerClass =
        "flex w-fit items-center justify-between gap-2 whitespace-nowrap rounded-md border border-input bg-transparent px-3 py-2 text-sm shadow-xs outline-none transition-[color,box-shadow] " +
        "focus-visible:border-ring focus-visible:ring-[3px] focus-visible:ring-ring/50 aria-invalid:border-destructive aria-invalid:ring-destructive/20 dark:aria-invalid:ring-destructive/40 " +
        "disabled:cursor-not-allowed disabled:opacity-50 data-[placeholder]:text-muted-foreground data-[size=default]:h-9 data-[size=sm]:h-8 dark:bg-input/30 dark:hover:bg-input/50";

    public SelectSize Size { get; set; }
    public string? Class { get; set; }
    public NodeList? Children { get; set; }
    [CaptureUnmatchedAttributes] public AttributeDictionary? Rest { get; set; }

    protected override Node Build()
    {
        var ctx = Use<SelectContext>();
        var popover = Use<PopoverPrimitive.Context>();
        var button = SelectParts.Style(new UIButton { Role = Role.ComboBox, Disabled = ctx.Disabled },
            "select-trigger", TriggerClass, Class, Rest);
        button.Id ??= ctx.Scope.TriggerId;
        button.Attributes["type"] = "button";
        button.Attributes["aria-haspopup"] = "listbox";
        button.Attributes["aria-expanded"] = SelectParts.Bool(popover.Open);
        button.Attributes["aria-controls"] = popover.Shared.ContentId;
        button.Attributes["aria-required"] = SelectParts.Bool(ctx.Required);
        button.Attributes["aria-disabled"] = SelectParts.Bool(button.Disabled);
        button.Attributes["data-state"] = popover.Open ? "open" : "closed";
        button.Attributes["data-disabled"] = button.Disabled ? "" : null;
        button.Attributes["data-placeholder"] = ctx.Value.Length == 0 ? "" : null;
        button.Attributes["data-size"] = Size == SelectSize.Sm ? "sm" : "default";
        ctx.Scope.TriggerId = button.Id!;
        button.OnPointerDown = EventComposition.Compose(button.OnPointerDown, _ => ctx.Scope.OpenAtEnd = false);
        button.OnKeyDown = EventComposition.Compose(button.OnKeyDown, e =>
        {
            if (!button.Disabled) HandleKey(e, ctx.Scope, popover.Shared);
        });
        button.Children = Children is null ? [new SelectValue()] : new NodeList(Children);
        button.Children.Add(SelectParts.Glyph(SelectParts.ChevronDown, "select-icon", "pointer-events-none size-4 shrink-0 opacity-50"));
        return new PopoverPrimitive.Trigger { AsChild = true, Children = [button] };
    }

    static void HandleKey(KeyEvent e, SelectScope scope, OpenableShared popup)
    {
        if (e.DefaultPrevented || popup.IsOpen) return;
        switch (e.Key)
        {
            case "ArrowDown" or "ArrowUp" when
                (e.Modifiers & (SelectParts.CommandModifiers | KeyModifiers.Shift)) == 0:
                e.PreventDefault();
                scope.OpenAtEnd = e.Key == "ArrowUp";
                if (!e.IsRepeat) popup.SetOpen(true);
                return;
            case "Enter":
            case " " when !scope.Search.IsTypingAhead(e.Timestamp):
                // The native button dispatches click for these keys.
                if (e.IsRepeat || (e.Modifiers & SelectParts.CommandModifiers) != 0) e.PreventDefault();
                return;
        }
        if (!TypeAhead.IsTypeAheadKey(e.Key, e.Modifiers)) return;
        e.PreventDefault();
        scope.Content?.FindTypedOption(e.Key, e.Timestamp, open: false);
    }
}

/// <summary>Shows the selected item's text, or the placeholder when the selection is empty.</summary>
public sealed partial class SelectValue : Component
{
    public string? Placeholder { get; set; }
    /// <summary>Overrides the selected label when a value is set.</summary>
    public NodeList? Children { get; set; }
    public string? Class { get; set; }
    [CaptureUnmatchedAttributes] public AttributeDictionary? Rest { get; set; }

    protected override Node Build()
    {
        var ctx = Use<SelectContext>();
        bool empty = ctx.Value.Length == 0;
        var content = empty ? new NodeList { Placeholder }
            : Children ?? new NodeList { SelectedLabel(ctx) };
        var value = SelectParts.Style(new Div { Children = content }, "select-value",
            "pointer-events-none min-w-0 truncate", Class, Rest);
        value.Attributes["data-placeholder"] = empty ? "" : null;
        return value;
    }

    static string SelectedLabel(SelectContext ctx)
    {
        var label = ctx.Scope.SelectedLabel.Value;
        return label.Value == ctx.Value ? label.Label ?? ctx.Value : ctx.Value;
    }
}
