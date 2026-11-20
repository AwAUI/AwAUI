using System;
using System.Collections.Generic;
using AwAUI.Css.Animation;
using AwAUI.UI;

namespace AwAUI.Controls.Primitives;

/// <summary>
/// Keeps its children mounted during finite CSS motion started on their root elements when <see cref="Present"/> becomes false.
/// Children should render their closed styles as soon as <see cref="Present"/> changes. Without a clock, closing unmounts immediately.
/// <see cref="ForceMount"/> keeps the children mounted regardless of <see cref="Present"/>.
/// </summary>
public sealed partial class PresenceGate : Component
{
    Presence? _presence;
    UIRoot? _exitRoot;
    UIClock? _exitClock;
    List<Element>? _exitTargets;
    HashSet<CssPendingMotion>? _motion;
    IDisposable? _deadline;

    public bool Present { get; set; }

    public bool ForceMount { get; set; }

    public NodeList? Children { get; set; }

    /// <summary>Optional content to mount after the exit finishes, or while initially absent.</summary>
    public Node? Fallback { get; set; }

    /// <summary>当前的状态（测试与诊断）。</summary>
    public PresenceState State => _presence?.State ?? PresenceState.Unmounted;

    protected override Node Build()
    {
        _presence ??= new Presence(Present);
        if (Present || ForceMount || Root is not { Clock: { } clock, Pipeline: RenderPipeline pipeline } root)
        {
            StopWatchingExit();
            if (Present) _presence.SetPresent(true, default, TimeSpan.Zero);
            else _presence.FinishExit();
        }
        else if (_presence.State == PresenceState.Mounted && _exitRoot is null)
        {
            // Capture the live elements and their motion before the closed styles are resolved.
            var targets = new List<Element>();
            AddTargets(GetRootElements(), targets);
            // GetRootElements omits portals. Include only those emitted directly by this gate.
            foreach (var layer in root.Overlay.Layers)
                if (ReferenceEquals(layer.Owner, this)) AddTargets(layer.Children, targets);

            if (targets.Count == 0) _presence.FinishExit();
            else
            {
                _exitRoot = root;
                _exitClock = clock;
                _exitTargets = targets;
                _motion = new HashSet<CssPendingMotion>(PendingMotion(pipeline));
                root.LayoutCompleted += ObserveExit;
            }
        }

        if (!ForceMount && !_presence.IsPresent) return new Fragment { Children = [Fallback] };
        return new Fragment(Children ?? new NodeList());
    }

    static void AddTargets(IReadOnlyList<Element> roots, List<Element> targets)
    {
        foreach (var element in roots)
        {
            if (element.IsTextNode) continue;
            // Popper positions a wrapper; the inner element owns the authored styles and data-state.
            var target = element.Owner is PopperContent && element.GetAttribute("data-aw-popper-content-wrapper") is not null
                && element.ElementChildren.Count > 0 ? element.ElementChildren[0] : element;
            targets.Add(target);
        }
    }

    IEnumerable<CssPendingMotion> PendingMotion(RenderPipeline pipeline)
    {
        foreach (var target in _exitTargets!)
            if (target.IsRendered && pipeline.GetAnimationState(target) is { } state)
                foreach (var motion in state.PendingFiniteMotion) yield return motion;
    }

    void ObserveExit(UIRoot root)
    {
        if (_exitTargets is not { } targets) return;
        if (root.Pipeline is not RenderPipeline pipeline || root.Clock is not { } clock || !ReferenceEquals(clock, _exitClock))
        {
            CompleteExit();
            return;
        }

        if (_presence!.Tick(clock.Now))
        {
            StopWatchingExit();
            Invalidate();
            return;
        }

        // Focus and positioning callbacks can request another style pass in this frame.
        foreach (var target in targets)
        {
            if (!ReferenceEquals(target.Root, root)) continue;
            for (Element? e = target; e is not null; e = e.Parent)
                if ((e.DirtyFlags & ElementDirtyFlags.Style) != 0)
                {
                    Invalidate();
                    return;
                }
        }

        if (_presence.State == PresenceState.Mounted)
        {
            var exits = new HashSet<CssPendingMotion>();
            var now = clock.Now;
            double longest = 0;
            foreach (var motion in PendingMotion(pipeline))
            {
                if (_motion!.Contains(motion)) continue;
                exits.Add(motion);
                longest = Math.Max(longest, motion.EndTime - now.TotalSeconds);
            }
            if (exits.Count != 0)
            {
                _motion = exits;
                var duration = longest >= TimeSpan.MaxValue.TotalSeconds ? TimeSpan.MaxValue : TimeSpan.FromSeconds(longest);
                _presence.SuspendExit(duration, now);
                ScheduleDeadline(clock, exits);
                return;
            }
        }
        else
        {
            // Settled and cancelled instances disappear from the runtime's pending set.
            // Matching by identity also ignores replacements and events from descendants.
            foreach (var motion in PendingMotion(pipeline))
                if (_motion!.Contains(motion)) return;
        }
        CompleteExit();
    }

    void ScheduleDeadline(UIClock clock, HashSet<CssPendingMotion> exits)
    {
        var remaining = _presence!.Deadline - clock.Now;
        if (remaining <= TimeSpan.Zero)
        {
            // Let the pipeline sample the final frame and dispatch its events before removing the targets.
            Invalidate();
            return;
        }
        _deadline = clock.Schedule(remaining, () =>
        {
            // A system clock callback may already have been posted when an exit is cancelled.
            if (!ReferenceEquals(_motion, exits)) return;
            ScheduleDeadline(clock, exits);
        });
    }

    void CompleteExit()
    {
        StopWatchingExit();
        _presence!.FinishExit();
        Invalidate();
    }

    void StopWatchingExit()
    {
        if (_exitRoot is { } root) root.LayoutCompleted -= ObserveExit;
        _exitRoot = null;
        _exitClock = null;
        _exitTargets = null;
        _motion = null;
        _deadline?.Dispose();
        _deadline = null;
    }

    protected override void OnUnmount()
    {
        StopWatchingExit();
        _presence = null;
    }
}
