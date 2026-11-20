using System;
using System.Windows.Input;
using AwAUI.Controls.Primitives;
using AwAUI.Platform;
using AwAUI.UI;
using UIButton = AwAUI.UI.Button;

namespace AwAUI.Controls;

/// <summary>
/// A button that repeats click activation while held by a primary pointer or Space. A short press
/// clicks on release. A press that already repeated does not click again on release.
/// </summary>
public sealed partial class RepeatButton : Control
{
    // Bound a hold even if the host loses a release event (for example, on window deactivation).
    static readonly TimeSpan MaxHold = TimeSpan.FromMinutes(1);
    Hold? _hold;
    IDisposable? _pending;
    long? _repeatedPointer;
    bool _repeatedSpace;
    KeyEvent? _spaceRelease;

    /// <summary>Milliseconds before the first repeat. Values below 1 are treated as 1.</summary>
    public int Delay { get; set; } = 300;

    /// <summary>Milliseconds between repeats. Values below 16 are treated as 16.</summary>
    public int Interval { get; set; } = 100;

    public bool IsEnabled { get; set; } = true;
    /// <summary>Raise <see cref="ICommand.CanExecuteChanged"/> on the UI thread.</summary>
    public ICommand? Command { get; set; }
    public object? CommandParameter { get; set; }
    public Action<PointerEvent>? OnClick { get; set; }
    public NodeList? Children { get; set; }

    protected override Node Build()
    {
        var command = Command;
        var button = new UIButton
        {
            Disabled = !CanActivate,
            OnClick = OnClick,
            Children = Children ?? [],
        };
        button.Attributes["part"] = "button";
        Apply(button, "aw-button aw-repeat-button");

        bool disabled = button.Disabled;
        UseEffect(() =>
        {
            if (command is null) return null;
            EventHandler refresh = (_, _) =>
            {
                if (!CanActivate) StopHold();
                Invalidate();
            };
            command.CanExecuteChanged += refresh;
            return () => command.CanExecuteChanged -= refresh;
        }, command);
        UseEffect(() =>
        {
            if (disabled) StopHold();
            return null;
        }, disabled);

        var click = button.OnClick;
        button.OnClick = e =>
        {
            if (e.CurrentTarget is not { IsDisabled: false } || !CanActivate)
            {
                StopHold();
                return;
            }
            click?.Invoke(e);
            if (!e.DefaultPrevented && Command is { } current && current.CanExecute(CommandParameter))
                current.Execute(CommandParameter);
        };
        var clickCapture = button.OnClickCapture;
        button.OnClickCapture = e =>
        {
            if (IsRepeatRelease(e))
            {
                e.PreventDefault();
                e.StopImmediatePropagation();
                return;
            }
            clickCapture?.Invoke(e);
        };
        button.OnPointerDownCapture = EventComposition.Chain<PointerEvent>(ResetPointerPress, button.OnPointerDownCapture);
        button.OnPointerDown = EventComposition.Chain<PointerEvent>(button.OnPointerDown, PointerDown);
        button.OnPointerMove = EventComposition.Chain<PointerEvent>(PointerMove, button.OnPointerMove);
        button.OnKeyDownCapture = EventComposition.Chain<KeyEvent>(ResetKeyPress, button.OnKeyDownCapture);
        button.OnKeyDown = EventComposition.Chain<KeyEvent>(button.OnKeyDown, KeyDown);
        button.OnKeyUpCapture = EventComposition.Chain<KeyEvent>(KeyUp, button.OnKeyUpCapture);
        button.OnPointerCancel = EventComposition.Chain<PointerEvent>(PointerCancel, button.OnPointerCancel);
        button.OnPointerLeave = EventComposition.Chain<PointerEvent>(PointerLeave, button.OnPointerLeave);
        button.OnBlur = EventComposition.Chain<FocusEvent>(_ => Blur(), button.OnBlur);
        return button;
    }

    bool CanActivate => IsEnabled && (Command is not { } command || command.CanExecute(CommandParameter));

    bool IsRepeatRelease(PointerEvent e)
    {
        if (!e.IsSynthetic && e.Button == PointerButtons.Left && _repeatedPointer == e.PointerId)
        {
            _repeatedPointer = null;
            return true;
        }
        // Let InputManager process keyup so it can clear the native pressed state. Only
        // suppress the click synthesized after that keyup, not activation inside its handlers.
        if (!e.IsSynthetic || e.CurrentTarget is not { } target) return false;
        bool completedRelease = _spaceRelease is { Phase: EventPhase.None, DefaultPrevented: false } release
            && ReferenceEquals(release.Target, target);
        // An ancestor may stop propagation of keyup without canceling its default action.
        bool releaseMissed = _repeatedSpace && (target.States & PseudoStates.Active) == 0;
        if (!completedRelease && !releaseMissed) return false;
        _spaceRelease = null;
        _repeatedSpace = false;
        if (_hold?.Start is KeyEvent) StopHold();
        return true;
    }

    void ResetPointerPress(PointerEvent e)
    {
        if (e.Button != PointerButtons.Left) return;
        if (_repeatedPointer == e.PointerId) _repeatedPointer = null;
        PointerCancel(e);
    }

    void PointerDown(PointerEvent e)
    {
        if (e.IsPrimary && e.Button == PointerButtons.Left) StartHold(e);
    }

    void ResetKeyPress(KeyEvent e)
    {
        if (!ReferenceEquals(e.Target, e.CurrentTarget)) return;
        _spaceRelease = null;
        if (e.PhysicalKey != PhysicalKey.Space || e.IsRepeat) return;
        _repeatedSpace = false;
        if (_hold?.Start is KeyEvent) StopHold();
    }

    void KeyDown(KeyEvent e)
    {
        if (e.PhysicalKey == PhysicalKey.Space && !e.IsRepeat && ReferenceEquals(e.Target, e.CurrentTarget))
            StartHold(e);
    }

    void KeyUp(KeyEvent e)
    {
        if (e.PhysicalKey != PhysicalKey.Space || !ReferenceEquals(e.Target, e.CurrentTarget)) return;
        _spaceRelease = _repeatedSpace ? e : null;
        _repeatedSpace = false;
        if (_hold?.Start is KeyEvent) StopHold();
    }

    void PointerUp(PointerEvent e)
    {
        if (_hold?.Start is PointerEvent start && start.PointerId == e.PointerId
            && (e.Button == PointerButtons.Left || (e.Buttons & PointerButtons.Left) == 0))
            StopHold();
    }

    void PointerMove(PointerEvent e)
    {
        if (e.PointerType == PointerType.Mouse && (e.Buttons & PointerButtons.Left) == 0)
            PointerCancel(e);
    }

    void PointerCancel(PointerEvent e)
    {
        if (_hold?.Start is PointerEvent start && start.PointerId == e.PointerId) StopHold();
    }

    void PointerLeave(PointerEvent e)
    {
        // Layout-driven hover can report a different mouse ID from the native down event.
        if (_hold?.Start is PointerEvent start && start.PointerType == e.PointerType
            && (start.PointerId == e.PointerId || e.PointerType == PointerType.Mouse))
            StopHold();
    }

    void StartHold(Event e)
    {
        if (_hold is not null || e.DefaultPrevented || e.IsImmediatePropagationStopped
            || e.CurrentTarget is not { IsDisabled: false, Root: { } root } target
            || root.Clock is not { } clock || !CanActivate)
            return;

        var hold = new Hold(target, root, clock, e, clock.Now);
        _hold = hold;
        // Observe release even if it lands outside or does not bubble back here. Leave normal click synthesis to UI.
        if (e is PointerEvent) root.Input.PreviewPointerUp += PointerUp;
        try
        {
            Schedule(hold, Math.Max(1, Delay));
        }
        catch
        {
            if (ReferenceEquals(_hold, hold)) StopHold();
            throw;
        }
    }

    void Schedule(Hold hold, int delay)
    {
        var remaining = MaxHold - (hold.Clock.Now - hold.StartedAt);
        if (remaining <= TimeSpan.Zero)
        {
            StopHold();
            return;
        }
        var duration = TimeSpan.FromMilliseconds(delay);
        _pending = hold.Clock.Schedule(duration < remaining ? duration : remaining, () => Tick(hold));
    }

    void Tick(Hold hold)
    {
        // A system timer may already have posted its callback before Dispose was called.
        if (!ReferenceEquals(_hold, hold)) return;
        _pending = null;
        try
        {
            var target = hold.Target;
            var root = hold.Root;
            // Read DefaultPrevented after the entire initiating event (including ancestor handlers) has run.
            if (hold.Start.DefaultPrevented || hold.Clock.Now - hold.StartedAt >= MaxHold
                || !ReferenceEquals(target.Root, root) || !ReferenceEquals(root.Clock, hold.Clock)
                || target.IsDisabled || !target.IsVisible || root.IsPointerBlocked(target) || !CanActivate
                || (hold.Start is PointerEvent
                    ? !target.IsInclusiveAncestorOf(root.Input.ActiveElement)
                    : !target.IsFocused || (target.States & PseudoStates.Active) == 0))
                return;

            if (hold.Start is PointerEvent pointer) _repeatedPointer = pointer.PointerId;
            else _repeatedSpace = true;
            root.Input.Activate(target);
            if (ReferenceEquals(_hold, hold)) Schedule(hold, Math.Max(16, Interval));
        }
        finally
        {
            if (ReferenceEquals(_hold, hold) && _pending is null) StopHold();
        }
    }

    void StopHold()
    {
        var hold = _hold;
        _hold = null;
        if (hold?.Start is PointerEvent) hold.Root.Input.PreviewPointerUp -= PointerUp;
        _pending?.Dispose();
        _pending = null;
    }

    void Blur()
    {
        StopHold();
        _repeatedSpace = false;
        _spaceRelease = null;
    }

    protected override void OnUnmount()
    {
        Blur();
        _repeatedPointer = null;
    }

    sealed class Hold(Element target, UIRoot root, UIClock clock, Event start, TimeSpan startedAt)
    {
        public Element Target { get; } = target;
        public UIRoot Root { get; } = root;
        public UIClock Clock { get; } = clock;
        public Event Start { get; } = start;
        public TimeSpan StartedAt { get; } = startedAt;
    }
}
