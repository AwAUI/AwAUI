using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AwAUI.Icons.Lucide;
using AwAUI.Svg;
using AwAUI.Tailwind;
using AwAUI.UI;
using UIButton = AwAUI.UI.Button;
using UIText = AwAUI.UI.Text;

namespace AwAUI.Shadcn;

/// <summary>Tone of a toast. It maps to <c>data-variant</c> on the toast.</summary>
public enum ToastVariant { Default, Success, Info, Warning, Error, Loading }

/// <summary>Where the toaster stack is anchored in the window.</summary>
public enum ToasterPosition { TopLeft, TopCenter, TopRight, BottomLeft, BottomCenter, BottomRight }

/// <summary>Description of a toast shown by <see cref="ToastStore.Show(ToastOptions)"/>.</summary>
public sealed class ToastOptions
{
    /// <summary>Optional stable identity. When omitted the store creates one.</summary>
    public string? Id { get; set; }

    public string? Title { get; set; }

    public string? Description { get; set; }

    /// <summary>Label of the single action button. The action runs and then dismisses the toast.</summary>
    public string? ActionLabel { get; set; }

    public Action? OnAction { get; set; }

    /// <summary>Shows the close button in the corner. Off by default, like Sonner's <c>closeButton</c> prop.</summary>
    public bool CloseButton { get; set; }

    /// <summary>
    /// Auto-dismiss delay in milliseconds. Zero or negative keeps the toast until it is dismissed. The default is 4000,
    /// matching Sonner. A <see cref="ToastVariant.Loading"/> toast never auto-dismisses.
    /// </summary>
    public int Duration { get; set; } = 4000;

    public ToastVariant Variant { get; set; }

    /// <summary>Called once when the toast leaves the store, for any reason.</summary>
    public Action? OnDismiss { get; set; }
}

/// <summary>One immutable toast record. The store replaces its array on every change.</summary>
public sealed class ToastEntry
{
    internal ToastEntry(string id, ToastOptions options)
    {
        Id = id;
        Options = options;
    }

    public string Id { get; }

    public ToastOptions Options { get; }
}

/// <summary>
/// Observable FIFO toast queue owned by the application. Write from the UI thread (or let the root marshal the write):
/// <code>
/// var store = new ToastStore();
/// store.Show(new ToastOptions { Title = "Saved", Description = "Changes are stored.", ActionLabel = "Undo", OnAction = Undo });
/// // new Toaster { Store = store }
/// </code>
/// Reading <see cref="Toasts"/> subscribes the calling component, so a <see cref="Toaster"/> updates when toasts change.
/// </summary>
public sealed class ToastStore
{
    static long s_nextId;
    readonly State<ToastEntry[]> _entries = new(Array.Empty<ToastEntry>());

    /// <summary>Current toasts, oldest first. Reading registers a dependency on the calling component.</summary>
    public IReadOnlyList<ToastEntry> Toasts => _entries.Value;

    public int Count => _entries.Peek().Length;

    /// <summary>Adds a toast and returns its identity. A supplied id replaces the existing toast with the same id.</summary>
    public string Show(ToastOptions options)
    {
        if (options is null) throw new ArgumentNullException(nameof(options));
        string id = string.IsNullOrEmpty(options.Id)
            ? "toast-" + Interlocked.Increment(ref s_nextId).ToString(CultureInfo.InvariantCulture)
            : options.Id!;
        var entry = new ToastEntry(id, options);
        var current = _entries.Peek();
        int index = Array.FindIndex(current, e => string.Equals(e.Id, id, StringComparison.Ordinal));
        if (index >= 0)
        {
            var replaced = (ToastEntry[])current.Clone();
            replaced[index] = entry;
            _entries.Value = replaced;
        }
        else
        {
            var next = new ToastEntry[current.Length + 1];
            Array.Copy(current, next, current.Length);
            next[next.Length - 1] = entry;
            _entries.Value = next;
        }
        return id;
    }

    /// <summary>Convenience overload for a title-only toast.</summary>
    public string Show(string title, string? description = null) =>
        Show(new ToastOptions { Title = title, Description = description });

    /// <summary>
    /// Replaces the options of an existing toast, keeping its identity and position in the queue. Returns false when no
    /// toast with <paramref name="id"/> is present (dismissed or cleared), in which case nothing changes. A
    /// <see cref="ToastVariant.Loading"/> toast that gains a non-loading variant starts its auto-dismiss timer.
    /// </summary>
    public bool Update(string id, ToastOptions options)
    {
        if (options is null) throw new ArgumentNullException(nameof(options));
        if (string.IsNullOrEmpty(id)) return false;
        var current = _entries.Peek();
        int index = Array.FindIndex(current, e => string.Equals(e.Id, id, StringComparison.Ordinal));
        if (index < 0) return false;
        var replaced = (ToastEntry[])current.Clone();
        replaced[index] = new ToastEntry(id, options);
        _entries.Value = replaced;
        return true;
    }

    /// <summary>
    /// Runs <paramref name="work"/> while showing a loading toast, then updates that same toast to the success or error
    /// options produced by <paramref name="success"/> / <paramref name="error"/>. The returned task yields the work's
    /// result (or rethrows its exception). If the loading toast is dismissed before the work finishes, the completion
    /// update is skipped. Loading toasts never auto-dismiss, regardless of the supplied duration.
    /// </summary>
    public async Task<T> Promise<T>(
        Task<T> work,
        ToastOptions loading,
        Func<T, ToastOptions> success,
        Func<Exception, ToastOptions> error)
    {
        if (work is null) throw new ArgumentNullException(nameof(work));
        if (loading is null) throw new ArgumentNullException(nameof(loading));
        if (success is null) throw new ArgumentNullException(nameof(success));
        if (error is null) throw new ArgumentNullException(nameof(error));

        bool dismissed = false;
        var loadingOptions = new ToastOptions
        {
            Id = loading.Id,
            Title = loading.Title,
            Description = loading.Description,
            ActionLabel = loading.ActionLabel,
            OnAction = loading.OnAction,
            CloseButton = loading.CloseButton,
            Duration = 0,
            Variant = loading.Variant == ToastVariant.Default ? ToastVariant.Loading : loading.Variant,
            OnDismiss = () =>
            {
                dismissed = true;
                loading.OnDismiss?.Invoke();
            },
        };
        string id = Show(loadingOptions);

        T result;
        try
        {
            result = await work;
        }
        catch (Exception ex)
        {
            if (!dismissed)
            {
                var failure = error(ex);
                if (failure is not null) Update(id, failure);
            }
            throw;
        }

        if (!dismissed)
        {
            var completed = success(result);
            if (completed is not null) Update(id, completed);
        }
        return result;
    }

    /// <summary>Overload for fixed success and error options instead of factories.</summary>
    public Task<T> Promise<T>(Task<T> work, ToastOptions loading, ToastOptions success, ToastOptions error)
    {
        if (success is null) throw new ArgumentNullException(nameof(success));
        if (error is null) throw new ArgumentNullException(nameof(error));
        return Promise(work, loading, _ => success, _ => error);
    }

    /// <summary>Removes a toast and reports whether it was present. Runs the toast's <see cref="ToastOptions.OnDismiss"/>.</summary>
    public bool Dismiss(string id)
    {
        if (string.IsNullOrEmpty(id)) return false;
        var current = _entries.Peek();
        int index = Array.FindIndex(current, e => string.Equals(e.Id, id, StringComparison.Ordinal));
        if (index < 0) return false;
        var next = new ToastEntry[current.Length - 1];
        if (index > 0) Array.Copy(current, 0, next, 0, index);
        if (index < current.Length - 1) Array.Copy(current, index + 1, next, index, current.Length - index - 1);
        var removed = current[index];
        _entries.Value = next;
        removed.Options.OnDismiss?.Invoke();
        return true;
    }

    /// <summary>Removes every toast.</summary>
    public void Clear()
    {
        var current = _entries.Peek();
        if (current.Length == 0) return;
        _entries.Value = Array.Empty<ToastEntry>();
        foreach (var entry in current) entry.Options.OnDismiss?.Invoke();
    }
}

/// <summary>
/// Viewport that renders the toasts of a <see cref="Store"/> in a polite live region. Stacks from the chosen
/// position, supports title, description, action and close button, pauses auto-dismiss while hovered, and respects
/// reduced motion. Place one at the root of the window.
/// </summary>
public sealed partial class Toaster : Component
{
    const string ViewportBase =
        "pointer-events-none fixed z-50 flex w-[356px] max-w-[calc(100vw-2rem)] flex-col gap-2 p-4";

    readonly State<bool> _expanded = new(false);
    readonly Ref<Element> _ownRef = new();

    // Expansion is driven by either the pointer or keyboard focus; leaving one source must not collapse while the
    // other is still inside the region.
    bool _pointerInside;
    bool _focusInside;

    public ToastStore? Store { get; set; }
    public ToasterPosition Position { get; set; } = ToasterPosition.BottomRight;

    /// <summary>
    /// Maximum number of toasts shown while collapsed. Newer toasts are shown in front and older ones stack behind
    /// them; the region expands on hover or keyboard focus to show every toast. A non-positive value hides the
    /// collapsed stack until it expands. Defaults to 3, matching Sonner.
    /// </summary>
    public int VisibleToasts { get; set; } = 3;

    /// <summary>
    /// Keyboard shortcut that focuses the toast region (making its actions reachable). Defaults to <c>Alt+T</c>,
    /// matching Sonner; an empty value disables it. See also <see cref="HotkeyEnabled"/>.
    /// </summary>
    public string? Hotkey { get; set; } = "Alt+T";

    /// <summary>Whether <see cref="Hotkey"/> is active. Defaults to true.</summary>
    public bool HotkeyEnabled { get; set; } = true;

    public string? Id { get; set; }
    public string? Class { get; set; }
    public Ref<Element>? Ref { get; set; }
    [CaptureUnmatchedAttributes] public AttributeDictionary? Rest { get; set; }

    protected override Node Build()
    {
        var store = Store ?? throw new InvalidOperationException(
            "Toaster.Store must reference the ToastStore owned by the application.");
        var toasts = store.Toasts;
        bool expanded = _expanded.Value;

        int count = toasts.Count;
        int first = 0;
        if (!expanded && VisibleToasts > 0 && count > VisibleToasts)
            first = count - VisibleToasts;

        // Sonner renders the newest toast first; for bottom anchors the list is column-reverse so the newest toast
        // sits closest to the screen edge while older toasts peek out behind it.
        var items = new NodeList();
        int index = 0;
        for (int i = count - 1; i >= first; i--, index++)
        {
            var entry = toasts[i];
            items.Add(new ToastRow
            {
                Key = entry.Id,
                Entry = entry,
                Store = store,
                Position = Position,
                Index = index,
                Expanded = expanded,
            });
        }

        string listClass = "relative flex w-full flex-col gap-2" +
            (ToastRow.IsBottom(Position) ? " flex-col-reverse" : "");

        var attributes = DialogAttributes.ForPart("sonner-toaster", Rest, Class, ViewportBase + " " + PositionClass(Position));
        attributes["role"] = Role.Status;
        attributes["aria-live"] = "polite";
        attributes["aria-atomic"] = "false";
        attributes["aria-relevant"] = "additions text";
        attributes["aria-label"] = attributes["aria-label"] ?? "Notifications alt+T";
        attributes["data-position"] = PositionName(Position);
        attributes["data-expanded"] = expanded ? "true" : "false";

        var regionRef = Ref ?? _ownRef;
        var viewport = new Div
        {
            Id = Id,
            Ref = regionRef,
            TabIndex = -1,
            Children = [new Div { Role = Role.List, Class = listClass, Children = items }],
        };
        viewport.MergeAttributes(attributes);

        viewport.OnPointerEnter = e =>
        {
            if (e.PointerType != AwAUI.Platform.PointerType.Touch) SetPointerInside(true);
        };
        viewport.OnPointerLeave = e =>
        {
            if (e.PointerType != AwAUI.Platform.PointerType.Touch) SetPointerInside(false);
        };
        viewport.OnFocusIn = _ => SetFocusInside(true);
        viewport.OnFocusOut = e =>
        {
            var region = e.CurrentTarget;
            var next = e.RelatedTarget;
            SetFocusInside(next is not null && region is not null && region.IsInclusiveAncestorOf(next));
        };

        UseEffect(() =>
        {
            if (!HotkeyEnabled || string.IsNullOrWhiteSpace(Hotkey)) return null;
            if (!KeyGesture.TryParse(Hotkey, out var gesture)) return null;
            if (Root?.Input is not { } input) return null;

            void Handler(KeyEvent e)
            {
                if (e.DefaultPrevented || e.IsPropagationStopped) return;
                if (!gesture.Matches(e)) return;
                e.PreventDefault();
                if ((Ref ?? _ownRef).Current is { } region)
                    Root?.FocusManager.Focus(region, FocusOrigin.Keyboard);
            }

            input.PreviewKeyDown += Handler;
            return () => input.PreviewKeyDown -= Handler;
        }, Hotkey, HotkeyEnabled, Ref);

        return viewport;
    }

    void SetPointerInside(bool inside)
    {
        _pointerInside = inside;
        SyncExpanded();
    }

    void SetFocusInside(bool inside)
    {
        _focusInside = inside;
        SyncExpanded();
    }

    void SyncExpanded() => _expanded.Value = _pointerInside || _focusInside;

    static string PositionClass(ToasterPosition position) => position switch
    {
        ToasterPosition.TopLeft => "top-0 left-0 items-start",
        ToasterPosition.TopCenter => "top-0 left-1/2 -translate-x-1/2 items-center",
        ToasterPosition.TopRight => "top-0 right-0 items-end",
        ToasterPosition.BottomLeft => "bottom-0 left-0 items-start flex-col-reverse",
        ToasterPosition.BottomCenter => "bottom-0 left-1/2 -translate-x-1/2 items-center flex-col-reverse",
        _ => "bottom-0 right-0 items-end flex-col-reverse",
    };

    static string PositionName(ToasterPosition position) => position switch
    {
        ToasterPosition.TopLeft => "top-left",
        ToasterPosition.TopCenter => "top-center",
        ToasterPosition.TopRight => "top-right",
        ToasterPosition.BottomLeft => "bottom-left",
        ToasterPosition.BottomCenter => "bottom-center",
        _ => "bottom-right",
    };
}

/// <summary>One toast row. Owns its auto-dismiss timer so pause and resume stay local and deterministic.</summary>
internal sealed partial class ToastRow : Component
{
    const string ToastBase =
        "group pointer-events-auto relative flex w-full items-start gap-3 overflow-hidden rounded-md border bg-popover p-4 " +
        "text-popover-foreground shadow-lg data-[state=open]:animate-aw-popup-in motion-reduce:animate-none! " +
        "data-[variant=error]:border-destructive/50";

    ToastStore _store = null!;
    ToastEntry _entry = null!;

    IDisposable? _timer;
    int _version;
    bool _paused;
    int _remainingMs;
    TimeSpan _startedAt;

    public ToastEntry Entry { get; set; } = null!;
    public ToastStore Store { get; set; } = null!;
    public ToasterPosition Position { get; set; }

    /// <summary>0 is the newest toast (front of the stack).</summary>
    public int Index { get; set; }

    /// <summary>Whether the region is expanded; collapsed non-front toasts overlap behind the front one.</summary>
    public bool Expanded { get; set; }

    internal static bool IsBottom(ToasterPosition position) =>
        position is ToasterPosition.BottomLeft or ToasterPosition.BottomCenter or ToasterPosition.BottomRight;

    protected override Node Build()
    {
        _store = Store;
        _entry = Entry;
        var options = Entry.Options;

        string classes = TailwindMerge.Cn(ToastBase, options.CloseButton ? "pr-10" : "pr-4");
        var root = new Div { Class = classes, Style = StackStyle() };
        root.Attributes["data-slot"] = "sonner-toast";
        root.Attributes["data-variant"] = VariantName(options.Variant);
        root.Attributes["data-state"] = "open";
        root.Attributes["data-position"] = PositionName(Position);
        root.Attributes["data-index"] = Index.ToString(CultureInfo.InvariantCulture);
        root.Attributes["data-front"] = Index == 0 ? "true" : "false";
        root.Attributes["data-expanded"] = Expanded ? "true" : "false";
        root.Role = Role.ListItem;

        var icon = IconFor(options.Variant);
        if (icon is not null)
            root.Children.Add(SelectParts.Glyph(icon,
                "sonner-icon",
                "mt-0.5 size-4 shrink-0" + (options.Variant == ToastVariant.Loading
                    ? " animate-spin motion-reduce:animate-none!"
                    : options.Variant == ToastVariant.Error ? " text-destructive" : " text-muted-foreground")));

        var content = new Div { Class = "flex min-w-0 flex-1 flex-col gap-1" };
        if (!string.IsNullOrEmpty(options.Title))
        {
            var title = new UIText(options.Title) { Class = "text-sm font-semibold leading-none" };
            title.Attributes["data-title"] = "";
            content.Children.Add(title);
        }
        if (!string.IsNullOrEmpty(options.Description))
        {
            var description = new UIText(options.Description) { Class = "text-sm text-muted-foreground" };
            description.Attributes["data-description"] = "";
            content.Children.Add(description);
        }
        root.Children.Add(content);

        if (!string.IsNullOrEmpty(options.ActionLabel))
        {
            var action = new UIButton
            {
                Role = Role.Button,
                Children = [new UIText(options.ActionLabel!)],
                Class = "inline-flex h-8 shrink-0 items-center justify-center rounded-md border bg-transparent px-3 text-sm font-medium " +
                    "transition-colors hover:bg-accent hover:text-accent-foreground focus-visible:outline-none focus-visible:ring-[3px] focus-visible:ring-ring/50",
            };
            action.Attributes["data-slot"] = "sonner-action";
            action.Attributes["type"] = "button";
            action.OnClick = e =>
            {
                if (e.DefaultPrevented) return;
                options.OnAction?.Invoke();
                Dismiss();
            };
            root.Children.Add(action);
        }

        if (options.CloseButton)
        {
            var close = new UIButton
            {
                Role = Role.Button,
                Class = "absolute top-2 right-2 inline-flex size-6 items-center justify-center rounded-md opacity-70 " +
                    "transition-opacity hover:opacity-100 focus-visible:outline-none focus-visible:ring-[3px] focus-visible:ring-ring/50",
                Children = [SelectParts.Glyph(LucideIcons.X, "sonner-close-icon", "size-3.5")],
            };
            close.Attributes["data-slot"] = "sonner-close";
            close.Attributes["type"] = "button";
            close.AriaLabel = "Close toast";
            close.OnClick = e =>
            {
                if (!e.DefaultPrevented) Dismiss();
            };
            root.Children.Add(close);
        }

        root.OnPointerEnter = e =>
        {
            if (e.PointerType != AwAUI.Platform.PointerType.Touch) Pause();
        };
        root.OnPointerLeave = e =>
        {
            if (e.PointerType != AwAUI.Platform.PointerType.Touch) Resume();
        };
        UseEffect(() =>
        {
            _remainingMs = options.Duration;
            _paused = false;
            StartTimer();
            return CancelTimer;
        }, Entry.Id, Entry);
        return root;
    }

    /// <summary>
    /// Stacking offsets, exposed as custom properties for styling, plus the literal transform that makes collapsed
    /// toasts overlap. Non-front toasts sit behind the front toast, peeking out by 14px each and scaling down slightly.
    /// </summary>
    string StackStyle()
    {
        int z = 200 - Index;
        var style = new StringBuilder();
        style.Append("--index:").Append(Index.ToString(CultureInfo.InvariantCulture));
        style.Append(";--toasts-before:").Append(Index.ToString(CultureInfo.InvariantCulture));
        style.Append(";--z-index:").Append(z.ToString(CultureInfo.InvariantCulture));
        style.Append(";--offset:").Append((Index * 14).ToString(CultureInfo.InvariantCulture)).Append("px;");
        style.Append("position:").Append(!Expanded && Index > 0 ? "absolute" : "relative").Append(';');
        style.Append("z-index:").Append(z.ToString(CultureInfo.InvariantCulture)).Append(';');
        if (!Expanded && Index > 0)
        {
            bool bottom = IsBottom(Position);
            int offset = (bottom ? -1 : 1) * Index * 14;
            double scale = Math.Max(0.75, 1 - Index * 0.05);
            style.Append("left:0;right:0;")
                .Append(bottom ? "bottom:0;" : "top:0;")
                .Append("transform:translateY(").Append(offset.ToString(CultureInfo.InvariantCulture))
                .Append("px) scale(").Append(scale.ToString("0.###", CultureInfo.InvariantCulture)).Append(");")
                .Append("transform-origin:").Append(bottom ? "bottom center;" : "top center;");
        }
        return style.ToString();
    }

    static SvgDocument? IconFor(ToastVariant variant) => variant switch
    {
        ToastVariant.Success => LucideIcons.CircleCheck,
        ToastVariant.Error => LucideIcons.OctagonX,
        ToastVariant.Info => LucideIcons.Info,
        ToastVariant.Warning => LucideIcons.TriangleAlert,
        ToastVariant.Loading => LucideIcons.LoaderCircle,
        _ => null,
    };

    static string VariantName(ToastVariant variant) => variant switch
    {
        ToastVariant.Success => "success",
        ToastVariant.Info => "info",
        ToastVariant.Warning => "warning",
        ToastVariant.Error => "error",
        ToastVariant.Loading => "loading",
        _ => "default",
    };

    static string PositionName(ToasterPosition position) => position switch
    {
        ToasterPosition.TopLeft => "top-left",
        ToasterPosition.TopCenter => "top-center",
        ToasterPosition.TopRight => "top-right",
        ToasterPosition.BottomLeft => "bottom-left",
        ToasterPosition.BottomCenter => "bottom-center",
        _ => "bottom-right",
    };

    void Dismiss() => _store.Dismiss(_entry.Id);

    void StartTimer()
    {
        CancelTimer();
        // Sonner keeps loading toasts open until they are dismissed or replaced by an updated toast.
        if (_entry.Options.Variant == ToastVariant.Loading) return;
        if (_remainingMs <= 0)
        {
            if (!_paused && _entry.Options.Duration > 0) Dismiss();
            return;
        }
        if (_paused) return;
        if (Root?.Clock is not { } clock) return;
        _startedAt = clock.Now;
        int version = _version;
        _timer = clock.Schedule(TimeSpan.FromMilliseconds(_remainingMs), () =>
        {
            if (version != _version) return;
            _timer = null;
            Dismiss();
        });
    }

    void CancelTimer()
    {
        _version++;
        _timer?.Dispose();
        _timer = null;
    }

    void Pause()
    {
        if (_paused) return;
        _paused = true;
        if (_timer is null) return;
        if (Root?.Clock is { } clock)
        {
            double elapsed = (clock.Now - _startedAt).TotalMilliseconds;
            _remainingMs = Math.Max(0, _remainingMs - (int)Math.Round(elapsed));
        }
        CancelTimer();
    }

    void Resume()
    {
        if (!_paused) return;
        _paused = false;
        StartTimer();
    }
}
