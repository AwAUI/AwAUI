using System;
using System.Linq;
using System.Threading.Tasks;
using AwAUI.Controls.UnitTests.Integration;
using AwAUI.Graphics;
using AwAUI.Platform;
using AwAUI.UI;

// Tasks completed with a TaskCompletionSource run their continuations inline here, so reading the already completed
// promise keeps the headless UI on the test thread; awaiting would move later UI calls off it.
#pragma warning disable xUnit1031

namespace AwAUI.Shadcn.Integration;

/// <summary>Sonner: observable toast queue, live region, dismissal and clock-driven auto-dismiss.</summary>
public class SonnerTests
{
    static SonnerTests() => ShadcnTheme.Register();

    static UIHarness Harness(Func<Node> render) =>
        new(render, appStyleSheets: true, width: 640, height: 480);

    static string? A(Element e, string name) => e.GetAttribute(name);

    static Element? Slot(UIHarness h, string slot) =>
        h.FindAll(e => A(e, "data-slot") == slot).FirstOrDefault();

    static Element SlotOrThrow(UIHarness h, string slot) =>
        Slot(h, slot) ?? throw new Xunit.Sdk.XunitException($"no element with data-slot=\"{slot}\"");

    [Fact]
    public void Toaster_renders_a_polite_live_region_and_toast_parts()
    {
        var store = new ToastStore();
        var h = Harness(() => new Toaster { Store = store, Position = ToasterPosition.BottomRight });

        var viewport = SlotOrThrow(h, "sonner-toaster");
        Assert.Equal(Role.Status, viewport.Role);
        Assert.Equal("polite", A(viewport, "aria-live"));
        Assert.Equal("false", A(viewport, "aria-atomic"));
        Assert.Equal("bottom-right", A(viewport, "data-position"));

        store.Show(new ToastOptions
        {
            Title = "Saved",
            Description = "Changes are stored.",
            ActionLabel = "Undo",
            CloseButton = true,
            Variant = ToastVariant.Success,
            Duration = 0,
        });
        h.Frame();

        var toast = SlotOrThrow(h, "sonner-toast");
        Assert.Equal(Role.ListItem, toast.Role);
        Assert.Equal("success", A(toast, "data-variant"));
        Assert.Contains("Saved", toast.TextContent);
        Assert.Contains("Changes are stored.", toast.TextContent);
        Assert.NotNull(Slot(h, "sonner-icon"));
        Assert.NotNull(Slot(h, "sonner-action"));
        Assert.NotNull(Slot(h, "sonner-close"));
    }

    [Fact]
    public void Close_button_dismisses_the_toast_and_reports_it()
    {
        int dismissed = 0;
        var store = new ToastStore();
        var h = Harness(() => new Toaster { Store = store });
        store.Show(new ToastOptions
        {
            Title = "Notice",
            Duration = 0,
            CloseButton = true,
            OnDismiss = () => dismissed++,
        });
        h.Frame();

        h.Click(SlotOrThrow(h, "sonner-close"));
        h.Frame();

        Assert.Equal(1, dismissed);
        Assert.Equal(0, store.Count);
        Assert.Null(Slot(h, "sonner-toast"));
    }

    [Fact]
    public void Action_runs_and_dismisses_the_toast()
    {
        int actions = 0;
        var store = new ToastStore();
        var h = Harness(() => new Toaster { Store = store });
        store.Show(new ToastOptions
        {
            Title = "Message archived",
            ActionLabel = "Undo",
            OnAction = () => actions++,
            Duration = 0,
        });
        h.Frame();

        h.Click(SlotOrThrow(h, "sonner-action"));
        h.Frame();

        Assert.Equal(1, actions);
        Assert.Equal(0, store.Count);
    }

    [Fact]
    public void Auto_dismiss_uses_the_root_clock_and_pauses_while_hovered()
    {
        var store = new ToastStore();
        var h = Harness(() => new Toaster { Store = store });
        var clock = new ManualUIClock();
        h.Root.Clock = clock;

        store.Show(new ToastOptions { Title = "Auto", Duration = 1000 });
        h.Frame();
        Assert.NotNull(Slot(h, "sonner-toast"));

        clock.Advance(TimeSpan.FromMilliseconds(500));
        h.Frame();
        Assert.NotNull(Slot(h, "sonner-toast"));

        var toast = SlotOrThrow(h, "sonner-toast");
        h.PointerMove(toast.GetBoundingClientRect().Center);
        clock.Advance(TimeSpan.FromMilliseconds(800));
        h.Frame();
        Assert.NotNull(Slot(h, "sonner-toast"));

        h.PointerMove(new Point(1, 1));
        clock.Advance(TimeSpan.FromMilliseconds(400));
        h.Frame();
        Assert.NotNull(Slot(h, "sonner-toast"));

        clock.Advance(TimeSpan.FromMilliseconds(200));
        h.Frame();
        Assert.Null(Slot(h, "sonner-toast"));
        Assert.Equal(0, store.Count);
    }

    [Fact]
    public void A_non_positive_duration_keeps_the_toast_until_dismissed()
    {
        var store = new ToastStore();
        var h = Harness(() => new Toaster { Store = store });
        var clock = new ManualUIClock();
        h.Root.Clock = clock;

        store.Show(new ToastOptions { Title = "Sticky", Duration = 0 });
        h.Frame();
        clock.Advance(TimeSpan.FromSeconds(30));
        h.Frame();

        Assert.NotNull(Slot(h, "sonner-toast"));
        Assert.Equal(1, store.Count);
    }

    [Fact]
    public void Toaster_uses_sonner_defaults_for_label_close_button_and_duration()
    {
        var store = new ToastStore();
        var h = Harness(() => new Toaster { Store = store });
        var clock = new ManualUIClock();
        h.Root.Clock = clock;

        var viewport = SlotOrThrow(h, "sonner-toaster");
        Assert.Equal("Notifications alt+T", A(viewport, "aria-label"));

        store.Show(new ToastOptions { Title = "Event created", Description = "Sunday at 9:00 AM" });
        h.Frame();

        // closeButton defaults to false in Sonner, and the title/description carry data attributes.
        Assert.Null(Slot(h, "sonner-close"));
        Assert.NotNull(h.FindAll(e => A(e, "data-title") is not null).FirstOrDefault());
        Assert.NotNull(h.FindAll(e => A(e, "data-description") is not null).FirstOrDefault());

        // The default duration is 4000 ms.
        clock.Advance(TimeSpan.FromMilliseconds(3900));
        h.Frame();
        Assert.NotNull(Slot(h, "sonner-toast"));

        clock.Advance(TimeSpan.FromMilliseconds(200));
        h.Frame();
        Assert.Null(Slot(h, "sonner-toast"));
        Assert.Equal(0, store.Count);
    }

    [Fact]
    public void A_loading_toast_never_auto_dismisses()
    {
        var store = new ToastStore();
        var h = Harness(() => new Toaster { Store = store });
        var clock = new ManualUIClock();
        h.Root.Clock = clock;

        store.Show(new ToastOptions { Title = "Working", Variant = ToastVariant.Loading, Duration = 1000 });
        h.Frame();
        Assert.NotNull(Slot(h, "sonner-icon"));

        clock.Advance(TimeSpan.FromSeconds(30));
        h.Frame();

        Assert.NotNull(Slot(h, "sonner-toast"));
        Assert.Equal(1, store.Count);
    }

    [Fact]
    public void Close_button_uses_the_sonner_accessible_label_when_requested()
    {
        var store = new ToastStore();
        var h = Harness(() => new Toaster { Store = store });
        store.Show(new ToastOptions { Title = "Notice", Duration = 0, CloseButton = true });
        h.Frame();

        var close = SlotOrThrow(h, "sonner-close");
        Assert.Equal("Close toast", A(close, "aria-label"));
    }

    // ------------------------------------------------------------ stacking and expand

    static List<Element> ToastSlots(UIHarness h) =>
        h.FindAll(e => A(e, "data-slot") == "sonner-toast");

    [Fact]
    public void VisibleToasts_limits_the_collapsed_stack_to_the_newest_toasts()
    {
        var store = new ToastStore();
        var h = Harness(() => new Toaster { Store = store, VisibleToasts = 2 });
        for (int i = 1; i <= 3; i++)
            store.Show(new ToastOptions { Title = "Toast " + i, Duration = 0 });
        h.Frame();

        var toasts = ToastSlots(h);
        Assert.Equal(2, toasts.Count);
        // Newest first: index 0 is the front of the stack.
        Assert.Equal(new[] { "0", "1" }, toasts.Select(t => A(t, "data-index")).ToArray());
        Assert.Equal(new[] { "true", "false" }, toasts.Select(t => A(t, "data-front")).ToArray());
        Assert.Contains("Toast 3", toasts[0].TextContent);
        Assert.Contains("Toast 2", toasts[1].TextContent);
        Assert.DoesNotContain(toasts, t => t.TextContent.Contains("Toast 1"));
    }

    [Fact]
    public void Hovering_the_region_expands_the_stack_and_leaving_collapses_it()
    {
        var store = new ToastStore();
        var h = Harness(() => new Toaster { Store = store, VisibleToasts = 2 });
        store.Show(new ToastOptions { Title = "Older", Duration = 0 });
        store.Show(new ToastOptions { Title = "Newer", Duration = 0 });
        store.Show(new ToastOptions { Title = "Newest", Duration = 0 });
        h.Frame();

        var region = SlotOrThrow(h, "sonner-toaster");
        Assert.Equal("false", A(region, "data-expanded"));
        Assert.Equal(2, ToastSlots(h).Count);
        Assert.All(ToastSlots(h), t => Assert.Equal("false", A(t, "data-expanded")));

        h.PointerMove(region.GetBoundingClientRect().Center);
        h.Frame();
        Assert.Equal("true", A(SlotOrThrow(h, "sonner-toaster"), "data-expanded"));
        Assert.Equal(3, ToastSlots(h).Count);

        h.PointerMove(new Point(1, 1));
        h.Frame();
        Assert.Equal("false", A(SlotOrThrow(h, "sonner-toaster"), "data-expanded"));
        Assert.Equal(2, ToastSlots(h).Count);
    }

    [Fact]
    public void Collapsed_toasts_overlap_with_relative_stack_offsets()
    {
        var store = new ToastStore();
        var h = Harness(() => new Toaster { Store = store });
        store.Show(new ToastOptions { Title = "Back", Duration = 0 });
        store.Show(new ToastOptions { Title = "Front", Duration = 0 });
        h.Frame();

        var back = ToastSlots(h)[1];
        Assert.Contains("--index:1", back.InlineStyle);
        Assert.Contains("--toasts-before:1", back.InlineStyle);
        // The older toast is taken out of flow and translated behind the front toast.
        Assert.Contains("position:absolute", back.InlineStyle);
        Assert.Contains("translateY(", back.InlineStyle);
        Assert.Contains("scale(", back.InlineStyle);

        var front = ToastSlots(h)[0];
        Assert.Contains("position:relative", front.InlineStyle);
    }

    // ------------------------------------------------------------ promise

    [Fact]
    public void Promise_shows_loading_and_updates_the_same_toast_on_success()
    {
        var store = new ToastStore();
        var h = Harness(() => new Toaster { Store = store });
        var clock = new ManualUIClock();
        h.Root.Clock = clock;

        var work = new TaskCompletionSource<int>();
        var promise = store.Promise(
            work.Task,
            new ToastOptions { Title = "Saving", Description = "Working on it", Variant = ToastVariant.Loading, Duration = 1000 },
            value => new ToastOptions { Title = "Saved " + value, Variant = ToastVariant.Success, Duration = 0 },
            ex => new ToastOptions { Title = "Failed", Description = ex.Message, Variant = ToastVariant.Error, Duration = 0 });
        h.Frame();

        var loading = SlotOrThrow(h, "sonner-toast");
        Assert.Equal("loading", A(loading, "data-variant"));
        Assert.Contains("Saving", loading.TextContent);
        Assert.Equal(1, store.Count);

        // A loading toast ignores the supplied duration.
        clock.Advance(TimeSpan.FromSeconds(10));
        h.Frame();
        Assert.Equal(1, store.Count);
        Assert.Equal("loading", A(SlotOrThrow(h, "sonner-toast"), "data-variant"));

        work.SetResult(7);
        Assert.True(promise.IsCompletedSuccessfully);
        Assert.Equal(7, promise.GetAwaiter().GetResult());
        h.Frame();

        Assert.Equal(1, store.Count);
        var updated = SlotOrThrow(h, "sonner-toast");
        Assert.Equal("success", A(updated, "data-variant"));
        Assert.Contains("Saved 7", updated.TextContent);
        Assert.DoesNotContain("Saving", updated.TextContent);
    }

    [Fact]
    public void Promise_updates_the_loading_toast_to_the_error_variant()
    {
        var store = new ToastStore();
        var h = Harness(() => new Toaster { Store = store });

        var work = new TaskCompletionSource<int>();
        var promise = store.Promise(
            work.Task,
            new ToastOptions { Title = "Saving", Variant = ToastVariant.Loading },
            value => new ToastOptions { Title = "Saved", Variant = ToastVariant.Success, Duration = 0 },
            ex => new ToastOptions { Title = "Failed", Description = ex.Message, Variant = ToastVariant.Error, Duration = 0 });
        h.Frame();
        Assert.Equal("loading", A(SlotOrThrow(h, "sonner-toast"), "data-variant"));

        work.SetException(new InvalidOperationException("boom"));
        Assert.Throws<InvalidOperationException>(() => promise.GetAwaiter().GetResult());
        h.Frame();

        Assert.Equal(1, store.Count);
        var failed = SlotOrThrow(h, "sonner-toast");
        Assert.Equal("error", A(failed, "data-variant"));
        Assert.Contains("Failed", failed.TextContent);
        Assert.Contains("boom", failed.TextContent);
    }

    [Fact]
    public void Promise_does_not_update_a_toast_dismissed_while_the_work_runs()
    {
        var store = new ToastStore();
        var h = Harness(() => new Toaster { Store = store });

        var work = new TaskCompletionSource<int>();
        var promise = store.Promise(
            work.Task,
            new ToastOptions { Title = "Saving", Variant = ToastVariant.Loading },
            value => new ToastOptions { Title = "Saved " + value, Variant = ToastVariant.Success, Duration = 0 },
            ex => new ToastOptions { Title = "Failed", Variant = ToastVariant.Error, Duration = 0 });
        h.Frame();
        Assert.Equal(1, store.Count);

        store.Dismiss(store.Toasts[0].Id);
        h.Frame();
        Assert.Equal(0, store.Count);
        Assert.Null(Slot(h, "sonner-toast"));

        work.SetResult(1);
        Assert.Equal(1, promise.GetAwaiter().GetResult());
        h.Frame();

        Assert.Equal(0, store.Count);
        Assert.Null(Slot(h, "sonner-toast"));
    }

    [Fact]
    public void Update_replaces_an_existing_toast_and_ignores_one_already_dismissed()
    {
        var store = new ToastStore();
        var h = Harness(() => new Toaster { Store = store });
        string id = store.Show(new ToastOptions { Title = "Before", Duration = 0 });
        h.Frame();

        Assert.True(store.Update(id, new ToastOptions { Title = "After", Variant = ToastVariant.Success, Duration = 0 }));
        h.Frame();
        var toast = SlotOrThrow(h, "sonner-toast");
        Assert.Equal("success", A(toast, "data-variant"));
        Assert.Contains("After", toast.TextContent);

        store.Dismiss(id);
        h.Frame();
        Assert.False(store.Update(id, new ToastOptions { Title = "Late", Duration = 0 }));
        Assert.Equal(0, store.Count);
        Assert.Null(Slot(h, "sonner-toast"));
    }

    // ------------------------------------------------------------ hotkey

    [Fact]
    public void Hotkey_focuses_the_toast_region()
    {
        var store = new ToastStore();
        var h = Harness(() => new Toaster { Store = store });
        var region = SlotOrThrow(h, "sonner-toaster");
        Assert.Equal(-1, region.TabIndex);
        Assert.Null(h.Focused);

        h.Key(PhysicalKey.KeyT, "t", KeyModifiers.Alt);

        Assert.Same(region, h.Focused);
    }

    [Fact]
    public void Empty_or_disabled_hotkey_does_not_focus_the_region()
    {
        var store = new ToastStore();

        var empty = Harness(() => new Toaster { Store = store, Hotkey = "" });
        empty.Key(PhysicalKey.KeyT, "t", KeyModifiers.Alt);
        Assert.Null(empty.Focused);

        var disabled = Harness(() => new Toaster { Store = store, HotkeyEnabled = false });
        disabled.Key(PhysicalKey.KeyT, "t", KeyModifiers.Alt);
        Assert.Null(disabled.Focused);
    }
}

#pragma warning restore xUnit1031
