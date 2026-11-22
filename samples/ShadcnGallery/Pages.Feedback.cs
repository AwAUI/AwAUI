using System;
using System.Threading.Tasks;
using AwAUI.Shadcn;
using AwAUI.UI;

namespace ShadcnGallery;

/// <summary>Feedback pages: toast notifications (including the Sonner-style queue) and progress.</summary>
public static class FeedbackPages
{
    public static void Register()
    {
        DocsCatalog.Add("toast", "Toast",
            DocsExample.Of("Toast", () => new FeedbackToastDemo()),
            DocsExample.Of("With title and description", () => new FeedbackToastDemo { WithDescription = true }),
            DocsExample.Of("With action", () => new FeedbackToastDemo { WithAction = true }),
            DocsExample.Of("Variants", () => new FeedbackToastDemo { Variants = true, WithDescription = true }));

        DocsCatalog.Add("progress", "Progress",
            DocsExample.Of("Progress", ProgressDefault),
            DocsExample.Of("Values", ProgressValues),
            DocsExample.Of("Indeterminate", ProgressIndeterminate),
            DocsExample.Of("Custom maximum", ProgressCustomMax));

        DocsCatalog.Add("sonner", "Sonner",
            DocsExample.Of("Types", () => new FeedbackSonnerDemo { Types = true, Duration = 0 }),
            DocsExample.Of("Action", () => new FeedbackSonnerDemo { WithAction = true, Duration = 8000 }),
            DocsExample.Of("Position", () => new FeedbackSonnerDemo()),
            DocsExample.Of("Stacking", () => new FeedbackSonnerDemo { Stacking = true }),
            DocsExample.Of("Promise", () => new FeedbackSonnerDemo { Promise = true }),
            DocsExample.Of("Dismiss and clear", () => new FeedbackSonnerDemo { WithControls = true }));
    }

    static Node ProgressDefault() => DocsUi.Stack(
        DocsUi.Row(DocsUi.Label("Uploading report.pdf"), DocsUi.Muted("60%")),
        new Progress { Value = 60 });

    static Node ProgressValues() => DocsUi.Stack(
        ProgressRow("25%", 25),
        ProgressRow("50%", 50),
        ProgressRow("75%", 75),
        ProgressRow("100%", 100));

    static Node ProgressIndeterminate() => DocsUi.Stack(
        DocsUi.Note("A null value reports data-state=\"indeterminate\" and leaves aria-valuenow unset, which is what a task with an unknown total should expose."),
        DocsUi.Row(DocsUi.Label("Preparing upload"), DocsUi.Muted("Working")),
        new Progress());

    static Node ProgressCustomMax() => DocsUi.Stack(
        DocsUi.Row(DocsUi.Label("Storage used"), DocsUi.Muted("120 of 200 GB")),
        new Progress
        {
            Value = 120,
            Max = 200,
            GetValueLabel = (value, max) =>
                value.ToString("0", System.Globalization.CultureInfo.InvariantCulture) + " of " +
                max.ToString("0", System.Globalization.CultureInfo.InvariantCulture) + " GB",
        });

    static Div ProgressRow(string label, double value) => DocsUi.Field(
        DocsUi.Row(DocsUi.Label(label), DocsUi.Muted("of 100")),
        new Progress { Value = value });
}

/// <summary>Toast demo: owns its <see cref="ToastStore"/> and keeps a bottom-right <see cref="Toaster"/> mounted.</summary>
sealed partial class FeedbackToastDemo : Component
{
    public bool WithDescription { get; set; }
    public bool WithAction { get; set; }
    public bool Variants { get; set; }

    readonly ToastStore _store = new();

    protected override Node Build()
    {
        Node[] buttons;
        if (Variants)
        {
            buttons =
            [
                DocsUi.Action("Default", _ => Show(ToastVariant.Default)),
                DocsUi.Action("Success", _ => Show(ToastVariant.Success)),
                DocsUi.Action("Error", _ => Show(ToastVariant.Error)),
            ];
        }
        else
        {
            buttons = [DocsUi.Action(WithAction ? "Delete file" : "Show toast", _ => Show(ToastVariant.Default))];
        }

        string hint = WithAction
            ? "The action runs first and then the toast dismisses; here Undo queues a confirmation toast."
            : "Toasts stack at the bottom-right and pause their auto-dismiss timer while hovered.";

        return DocsUi.Stack(
            DocsUi.Note(hint),
            DocsUi.Row(buttons),
            new Toaster { Store = _store, Position = ToasterPosition.BottomRight });
    }

    void Show(ToastVariant variant)
    {
        string title = WithAction
            ? "File deleted"
            : variant switch
            {
                ToastVariant.Success => "Saved successfully",
                ToastVariant.Error => "Something went wrong",
                _ => "Event created",
            };

        string? description = WithAction
            ? "report-final.pdf moved to Trash."
            : !WithDescription
                ? null
                : variant switch
                {
                    ToastVariant.Success => "Your changes have been saved.",
                    ToastVariant.Error => "The server returned an error. Try again.",
                    _ => "Sunday, December 3 at 9:00 AM",
                };

        var options = new ToastOptions
        {
            Title = title,
            Description = description,
            Variant = variant,
        };
        if (WithAction)
        {
            options.ActionLabel = "Undo";
            options.OnAction = () => _store.Show("Restored", "report-final.pdf is back.");
        }
        _store.Show(options);
    }
}

/// <summary>Sonner-style demo: top-right toaster with actions, a configurable duration, and queue controls.</summary>
sealed partial class FeedbackSonnerDemo : Component
{
    public bool WithAction { get; set; }
    public bool WithControls { get; set; }
    public bool Types { get; set; }
    public bool Stacking { get; set; }
    public bool Promise { get; set; }
    public int Duration { get; set; } = 6000;

    readonly ToastStore _store = new();
    int _stackIndex;

    protected override Node Build()
    {
        Node[] buttons;
        string hint;
        if (Types)
        {
            buttons =
            [
                DocsUi.Action("Success", _ => ShowTyped(ToastVariant.Success)),
                DocsUi.Action("Info", _ => ShowTyped(ToastVariant.Info)),
                DocsUi.Action("Warning", _ => ShowTyped(ToastVariant.Warning)),
                DocsUi.Action("Error", _ => ShowTyped(ToastVariant.Error)),
                DocsUi.Action("Loading", _ => ShowTyped(ToastVariant.Loading)),
            ];
            hint = "Each call sets a variant, so the toast shows the matching Lucide status icon. Loading toasts stay open until they are dismissed or replaced.";
        }
        else if (Stacking)
        {
            buttons =
            [
                DocsUi.Action("Add toast", _ => ShowStacked(1)),
                DocsUi.Action("Add five", _ => ShowStacked(5)),
                DocsUi.Action("Dismiss last", _ => DismissLast()),
                DocsUi.Action("Clear all", _ => _store.Clear()),
            ];
            hint = "Every call appends to the same column. VisibleToasts is 3, so only the newest three stay in the collapsed stack; hover or focus the region to expand every toast, newest first.";
        }
        else if (Promise)
        {
            buttons =
            [
                DocsUi.Action("Upload file", _ => StartPromise(succeeds: true)),
                DocsUi.Action("Upload and fail", _ => StartPromise(succeeds: false)),
            ];
            hint = "The button passes a Task to ToastStore.Promise. The loading toast stays open until the task resolves after 1.5 s, then the same toast updates in place to its success or error variant.";
        }
        else if (WithControls)
        {
            buttons =
            [
                DocsUi.Action("Show persistent toast", _ => ShowToast(0)),
                DocsUi.Action("Dismiss last", _ => DismissLast()),
                DocsUi.Action("Clear all", _ => _store.Clear()),
            ];
            hint = "A duration of zero keeps the toast until it is dismissed; Dismiss last and Clear all act on this example's queue.";
        }
        else if (WithAction)
        {
            buttons = [DocsUi.Action("Show action toast", _ => ShowActionToast())];
            hint = "The action toast stays for 8 seconds and restores the file when Undo is pressed.";
        }
        else
        {
            buttons = [DocsUi.Action("Show toast", _ => ShowToast(Duration))];
            hint = "This toaster is anchored to the top-right, unlike the bottom-right Toast page. Alt+T focuses the live region, and hovering or focusing it expands the stack; the dismiss timer pauses while the pointer is over a toast.";
        }

        return DocsUi.Stack(
            DocsUi.Note(hint),
            DocsUi.Row(buttons),
            new Toaster
            {
                Store = _store,
                Position = ToasterPosition.TopRight,
                VisibleToasts = 3,
                Hotkey = "Alt+T",
            });
    }

    void ShowTyped(ToastVariant variant) => _store.Show(new ToastOptions
    {
        Title = variant switch
        {
            ToastVariant.Success => "Event has been created",
            ToastVariant.Info => "Be careful with that",
            ToastVariant.Warning => "This action cannot be undone",
            ToastVariant.Error => "Something went wrong",
            ToastVariant.Loading => "Uploading file",
            _ => "Event created",
        },
        Description = variant switch
        {
            ToastVariant.Success => "Sunday, December 3 at 9:00 AM",
            ToastVariant.Info => "The report is ready to review.",
            ToastVariant.Warning => "Deleting this file removes it for everyone.",
            ToastVariant.Error => "The server returned an error. Try again.",
            ToastVariant.Loading => "This toast stays until it is dismissed.",
            _ => null,
        },
        Variant = variant,
        Duration = Duration,
    });

    void ShowToast(int duration) => _store.Show(new ToastOptions
    {
        Title = "Event created",
        Description = "Sunday, December 3 at 9:00 AM",
        Duration = duration,
    });

    void ShowActionToast() => _store.Show(new ToastOptions
    {
        Title = "File deleted",
        Description = "report-final.pdf moved to Trash.",
        ActionLabel = "Undo",
        OnAction = () => _store.Show("Restored", "report-final.pdf is back."),
        Duration = Duration,
    });

    void ShowStacked(int count)
    {
        string[] titles = ["Event created", "Message sent", "File uploaded"];
        for (int i = 0; i < count; i++)
        {
            int index = ++_stackIndex;
            _store.Show(new ToastOptions
            {
                Title = titles[(index - 1) % titles.Length],
                Description = "Notification " + index + " stays in the queue.",
                Variant = ToastVariant.Success,
                Duration = 6000,
            });
        }
    }

    void StartPromise(bool succeeds) => _ = RunPromiseAsync(succeeds);

    async Task RunPromiseAsync(bool succeeds)
    {
        try
        {
            await _store.Promise(
                UploadAsync(succeeds),
                new ToastOptions
                {
                    Title = "Uploading report.pdf",
                    Description = "Preparing the upload…",
                },
                new ToastOptions
                {
                    Title = "Upload complete",
                    Description = "report.pdf is ready to share.",
                    Variant = ToastVariant.Success,
                    Duration = 5000,
                },
                new ToastOptions
                {
                    Title = "Upload failed",
                    Description = "The network dropped. Try again.",
                    Variant = ToastVariant.Error,
                    Duration = 6000,
                });
        }
        catch (InvalidOperationException)
        {
            // ToastStore.Promise rethrows after updating the toast; the failure path is already visible.
        }
    }

    static async Task<string> UploadAsync(bool succeeds)
    {
        await Task.Delay(1500);
        if (!succeeds) throw new InvalidOperationException("Upload failed");
        return "report.pdf";
    }

    void DismissLast()
    {
        var toasts = _store.Toasts;
        if (toasts.Count > 0) _store.Dismiss(toasts[toasts.Count - 1].Id);
    }
}
