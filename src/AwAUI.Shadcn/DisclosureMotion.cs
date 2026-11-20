using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using AwAUI.Css.Animation;
using AwAUI.UI;

namespace AwAUI.Shadcn;

internal sealed class DisclosureMotion
{
    const string Moving = "data-aw-accordion-moving";

    internal const string StyleSheet = """
        @layer components {
          :where([data-slot="accordion-content"][data-aw-accordion-moving]) {
            transition: height 200ms ease-out;
          }
          :where([data-slot="accordion-content"][data-state="closed"]) {
            pointer-events: none;
          }
          @media (prefers-reduced-motion: reduce) {
            :where([data-slot="accordion-content"][data-aw-accordion-moving]) {
              transition: none;
            }
          }
        }
        """;

    bool? _wasOpen;

    internal Action? Update(UIRoot? root, Element? panel, Element? body, Element? trigger, bool open, bool forceMount)
    {
        bool toggled = _wasOpen is { } previous && open != previous;
        _wasOpen = open;
        if (root is null || panel is null || body is null) return null;

        if (!open) RestoreFocus(panel, trigger);

        if (!toggled || root.Clock is null || root.Pipeline is not RenderPipeline pipeline
            || pipeline.StyleEngine.Environment.Media.PrefersReducedMotion)
        {
            SetHidden(panel, !open && forceMount);
            ResetSize(panel);
            return null;
        }

        SetHidden(panel, false);
        // Seed a numeric height before the first style pass. An interrupted transition already
        // has one; leaving it in place lets CSS reverse from the current interpolated height.
        if (panel.GetStyleOverride("height") is null)
            SetHeight(panel, panel.IsRendered ? panel.BoundingRect.Height : 0);

        // ForceMount bypasses PresenceGate's exit watcher. Snapshot existing motion so a custom
        // exit can finish before the retained panel is hidden, just as it can before unmounting.
        var before = !open && forceMount && pipeline.GetAnimationState(panel) is { } state
            ? new HashSet<CssPendingMotion>(state.PendingFiniteMotion) : null;
        bool cancelled = false;
        root.RequestAfterLayout(() =>
        {
            if (cancelled || panel.Root != root) return;
            panel.SetAttributeOverride(Moving, "");
            SetHeight(panel, open && body.IsRendered ? body.BoundingRect.Height : 0);
            // A non-retained close is completed by PresenceGate on this same element.
            if (open || forceMount)
                root.RequestAfterLayout(() =>
                {
                    if (cancelled) return;
                    root.LayoutCompleted += Settle;
                    Settle(root);
                });
        });
        return () =>
        {
            cancelled = true;
            root.LayoutCompleted -= Settle;
        };

        void Settle(UIRoot _)
        {
            if (panel.Root == root)
            {
                if (pipeline.GetAnimationState(panel) is { } motion && (open
                    ? motion.TransitioningProperties.Contains("height")
                    : motion.PendingFiniteMotion.Any(m => before?.Contains(m) != true))) return;
                if (!open) SetHidden(panel, true);
                // Let the open panel return to its authored size so it can respond to content changes.
                ResetSize(panel);
            }
            root.LayoutCompleted -= Settle;
        }
    }

    internal static void RestoreFocus(Element? panel, Element? trigger)
    {
        if (panel?.Root?.FocusManager.FocusedElement is not { } focused || !panel.IsInclusiveAncestorOf(focused)) return;
        if (trigger is not { IsVisible: true } || !trigger.Focus(new FocusOptions { PreventScroll = true }))
            focused.Blur();
    }

    static void SetHeight(Element panel, float height) =>
        panel.SetStyleOverride("height", height.ToString(CultureInfo.InvariantCulture) + "px");

    static void ResetSize(Element panel)
    {
        panel.SetAttributeOverride(Moving, null);
        panel.SetStyleOverride("height", null);
    }

    static void SetHidden(Element panel, bool hidden)
    {
        panel.SetAttributeOverride("hidden", hidden ? "" : null);
        panel.SetStyleOverride("display", hidden ? "none !important" : null);
    }
}
