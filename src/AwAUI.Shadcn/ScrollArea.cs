using AwAUI.UI;

namespace AwAUI.Shadcn;

public enum ScrollAreaOrientation { Vertical, Horizontal, Both }

/// <summary>A clipped viewport using the framework's scroll controller and scrollbar rendering.</summary>
public sealed partial class ScrollArea : Component
{
    public ScrollAreaOrientation Orientation { get; set; }
    public ScrollController? ScrollController { get; set; }
    public string? Class { get; set; }
    public string? ViewportClass { get; set; }
    public NodeList? Children { get; set; }
    [CaptureUnmatchedAttributes] public AttributeDictionary? Rest { get; set; }

    protected override Node Build()
    {
        string overflow = Orientation switch
        {
            ScrollAreaOrientation.Horizontal => "overflow-x-auto overflow-y-hidden",
            ScrollAreaOrientation.Both => "overflow-auto",
            _ => "overflow-x-hidden overflow-y-auto",
        };
        var viewport = TabsParts.Style(new Div { ScrollController = ScrollController, TabIndex = 0, Children = Children ?? [] },
            "scroll-area-viewport", "size-full min-h-0 min-w-0 rounded-[inherit] outline-none focus-visible:ring-[3px] focus-visible:ring-ring/50 " + overflow, ViewportClass, null);
        return TabsParts.Style(new Div { Children = [viewport] }, "scroll-area", "relative overflow-hidden", Class, Rest);
    }
}
