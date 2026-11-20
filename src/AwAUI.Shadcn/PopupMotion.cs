namespace AwAUI.Shadcn;

internal static class PopupMotion
{
    // Shared by the New York v4 anchored surfaces. Use transform/opacity only so that
    // the generic enter keyframe's neutral filter never allocates a filter layer.
    internal const string Classes = "origin-(--aw-popper-transform-origin) " +
        "data-[state=open]:animate-aw-popup-in data-[state=delayed-open]:animate-aw-popup-in " +
        "data-[state=instant-open]:animate-aw-popup-in data-[state=closed]:animate-aw-popup-out " +
        "data-[state=closed]:pointer-events-none motion-reduce:animate-none!";
}
