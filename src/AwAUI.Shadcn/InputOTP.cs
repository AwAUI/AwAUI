using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using AwAUI.Controls.Primitives;
using AwAUI.Icons.Lucide;
using AwAUI.Platform;
using AwAUI.Tailwind;
using AwAUI.UI;
using BuiltInInput = AwAUI.UI.Input;

namespace AwAUI.Shadcn;

/// <summary>
/// A one-time-code input made of individual character slots over one logical value. Typing advances to the next slot;
/// Backspace clears the current slot or steps back; arrows, Home, and End move focus; pasting fills several slots.
/// <para>
/// <see cref="Value"/> makes the value controlled (the parent owns it); otherwise <see cref="DefaultValue"/> seeds an
/// internal value. <see cref="Pattern"/> filters each character (default: ASCII digits). The group has role
/// <c>group</c> with <see cref="AriaLabel"/> as its accessible name; every slot is a separate text box with its own
/// label and <c>aria-invalid</c>.
/// </para>
/// </summary>
public sealed partial class InputOTP : Component
{
    const string DefaultContainerClass =
        "flex items-center gap-2 has-disabled:opacity-50";

    readonly ControllableState<string> _value = new(StringComparer.Ordinal);
    readonly State<int?> _focused = new((int?)null);
    readonly OtpSlots _slots = new();

    string? _patternText;
    Regex? _patternRegex;

    /// <summary>Controlled value. A null value leaves the value in the component.</summary>
    public string? Value { get; set; }

    /// <summary>Initial value when <see cref="Value"/> is not set.</summary>
    public string? DefaultValue { get; set; }

    /// <summary>Receives each accepted value and supports <c>@bind-Value</c>.</summary>
    public Action<string>? ValueChanged { get; set; }

    /// <summary>Alias of <see cref="ValueChanged"/>, after the Radix convention.</summary>
    public Action<string>? OnValueChange { get; set; }

    /// <summary>Disables every slot and sets <c>aria-disabled</c> on the group.</summary>
    public bool Disabled { get; set; }

    /// <summary>Marks every slot invalid (<c>aria-invalid</c>) and the destructive border.</summary>
    public bool Invalid { get; set; }

    /// <summary>Maximum number of characters. Defaults to the number of registered slots.</summary>
    public int? MaxLength { get; set; }

    /// <summary>
    /// Per-character filter regular expression. The pattern is tested against one character at a time, so
    /// <c>[0-9]</c>, <c>[A-Za-z]</c>, and <c>[A-Za-z0-9]</c> work as expected. Defaults to ASCII digits.
    /// </summary>
    public string? Pattern { get; set; }

    /// <summary>The accessible name of the group.</summary>
    public string? AriaLabel { get; set; }

    public string? Id { get; set; }

    /// <summary>Classes applied to the group container.</summary>
    public string? Class { get; set; }

    /// <summary>Additional container classes (the shadcn <c>containerClassName</c> prop).</summary>
    public string? ContainerClass { get; set; }

    public Ref<Element>? Ref { get; set; }
    public NodeList? Children { get; set; }
    [CaptureUnmatchedAttributes] public AttributeDictionary? Rest { get; set; }

    protected override Node Build()
    {
        string value = _value.Sync(Value is not null, Value ?? "", DefaultValue ?? "", ValueChanged + OnValueChange);
        var scope = new Scope(_value, value, Disabled, Invalid, MaxLength, CreateFilter(), AriaLabel, _slots, _focused,
            Invalidate);
        var container = new Div { Id = Id, Ref = Ref, Children = Children ?? new NodeList() };
        container.MergeAttributes(Rest);
        container.Role = Role.Group;
        if (AriaLabel is not null) container.AriaLabel = AriaLabel;
        container.Attributes["data-slot"] = "input-otp";
        container.Attributes["data-disabled"] = Disabled ? "" : null;
        container.Attributes["aria-disabled"] = Disabled ? "true" : null;
        container.Class = TailwindMerge.Cn(
            TailwindMerge.Cn(DefaultContainerClass, Class),
            container.Class.Text);
        return Provide(scope, container);
    }

    Func<char, bool> CreateFilter()
    {
        if (string.IsNullOrEmpty(Pattern)) return static c => c >= '0' && c <= '9';
        if (!string.Equals(_patternText, Pattern, StringComparison.Ordinal))
        {
            _patternText = Pattern;
            _patternRegex = new Regex(Pattern!, RegexOptions.CultureInvariant);
        }
        var regex = _patternRegex!;
        return c => regex.IsMatch(c.ToString());
    }

    internal sealed class Scope
    {
        readonly ControllableState<string> _state;
        readonly State<int?> _focused;
        readonly Action _requestRender;

        internal Scope(ControllableState<string> state, string value, bool disabled, bool invalid, int? maxLength,
            Func<char, bool> isAllowed, string? ariaLabel, OtpSlots slots, State<int?> focused, Action requestRender)
        {
            _state = state;
            Value = value;
            Disabled = disabled;
            Invalid = invalid;
            MaxLength = maxLength;
            IsAllowed = isAllowed;
            AriaLabel = ariaLabel;
            Slots = slots;
            _focused = focused;
            _requestRender = requestRender;
        }

        internal string Value { get; }
        internal bool Disabled { get; }
        internal bool Invalid { get; }
        internal int? MaxLength { get; }
        internal Func<char, bool> IsAllowed { get; }
        internal string? AriaLabel { get; }
        internal OtpSlots Slots { get; }

        internal int Capacity
        {
            get
            {
                var max = MaxLength;
                if (max is { } limit && limit > 0) return limit;
                return Slots.Count;
            }
        }

        internal bool IsNumeric => IsDigitOnly();

        internal string CharAt(int index) => index >= 0 && index < Value.Length ? Value.Substring(index, 1) : "";

        internal bool IsFocused(int index) => _focused.Value == index;

        internal int? FocusedIndex => _focused.Peek();

        internal void SetFocused(int? index)
        {
            if (_focused.Peek() == index) return;
            _focused.Value = index;
        }

        internal void SetValue(string value)
        {
            _state.Set(value);
            _requestRender();
        }

        internal void RequestRender() => _requestRender();

        internal bool FocusSlot(int index)
        {
            int capacity = Capacity;
            if (capacity > 0 && index >= capacity) index = capacity - 1;
            if (index < 0) index = 0;
            return Slots.Focus(index);
        }

        internal string SlotLabel(int index)
        {
            int capacity = Capacity;
            if (capacity <= 0) capacity = Slots.Count;
            return AriaLabel is { Length: > 0 } name
                ? string.Format(CultureInfo.InvariantCulture, "{0}, character {1} of {2}", name, index + 1, capacity)
                : string.Format(CultureInfo.InvariantCulture, "Character {0} of {1}", index + 1, capacity);
        }

        bool IsDigitOnly() => IsAllowed('0') && !IsAllowed('a') && !IsAllowed('A');
    }
}

/// <summary>A horizontal group of <see cref="InputOTPSlot"/> nodes.</summary>
public sealed partial class InputOTPGroup : Component
{
    public string? Class { get; set; }
    public NodeList? Children { get; set; }
    [CaptureUnmatchedAttributes] public AttributeDictionary? Rest { get; set; }

    protected override Node Build()
    {
        var group = new Div { Children = Children ?? new NodeList() };
        group.MergeAttributes(Rest);
        group.Attributes["data-slot"] = "input-otp-group";
        group.Class = TailwindMerge.Cn("flex items-center", group.Class.Text, Class);
        return group;
    }
}

/// <summary>
/// One character slot: a real single-line editor with strict filtering. The slot shows its character from the shared
/// value, reports focus through <c>data-active</c>, and participates in the value's keyboard navigation.
/// </summary>
public sealed partial class InputOTPSlot : Component
{
    const string SlotClass =
        "relative flex h-9 w-9 min-w-0 items-center justify-center border-y border-r border-input p-0 text-center text-sm shadow-xs " +
        "outline-none transition-all motion-reduce:transition-none first:rounded-l-md first:border-l last:rounded-r-md " +
        "aria-invalid:border-destructive data-[active=true]:z-10 data-[active=true]:border-ring data-[active=true]:ring-[3px] " +
        "data-[active=true]:ring-ring/50 data-[active=true]:aria-invalid:border-destructive " +
        "dark:bg-input/30 dark:data-[active=true]:aria-invalid:ring-destructive/20 dark:data-[active=true]:aria-invalid:ring-destructive/40";

    /// <summary>The zero-based slot index. Slots are expected to use consecutive indexes.</summary>
    public int Index { get; set; }

    /// <summary>Overrides the accessible name (defaults to "Character N of M").</summary>
    public string? AriaLabel { get; set; }

    public string? Id { get; set; }
    public string? Class { get; set; }
    public Ref<Element>? Ref { get; set; }
    [CaptureUnmatchedAttributes] public AttributeDictionary? Rest { get; set; }

    protected override Node Build()
    {
        var scope = Use<InputOTP.Scope>();
        string character = scope.CharAt(Index);
        bool active = scope.IsFocused(Index);
        var input = new BuiltInInput
        {
            Id = Id,
            Value = character,
            Disabled = scope.Disabled,
            Role = Role.TextBox,
            AriaLabel = AriaLabel ?? scope.SlotLabel(Index),
            Ref = Ref,
        };
        input.Attributes["data-slot"] = "input-otp-slot";
        input.Attributes["data-active"] = InputOtpParts.Bool(active);
        input.Attributes["data-index"] = Index.ToString(CultureInfo.InvariantCulture);
        input.Attributes["inputmode"] = InputOtpParts.InputMode(scope);
        input.Attributes["maxlength"] = "1";
        input.Attributes["aria-invalid"] = scope.Invalid ? "true" : null;
        if (Index == 0) input.Attributes["autocomplete"] = "one-time-code";

        input.OnInput = e => InputOtpParts.HandleInput(e, scope, Index);
        input.OnKeyDown = e => InputOtpParts.HandleKey(e, scope, Index);
        input.OnFocus = _ => scope.SetFocused(Index);
        input.OnBlur = _ =>
        {
            if (scope.FocusedIndex == Index) scope.SetFocused(null);
        };
        input.MergeAttributes(Rest);
        input.Class = TailwindMerge.Cn(SlotClass, input.Class.Text, Class);

        UseEffect(() =>
        {
            var entry = scope.Slots.Register(Index, FirstElement());
            return () => scope.Slots.Unregister(entry);
        }, Index);
        return input;
    }

    Element? FirstElement() => GetRootElements() is { Count: > 0 } roots ? roots[0] : null;
}

/// <summary>A visual divider between slots or groups (the shadcn minus icon).</summary>
public sealed partial class InputOTPSeparator : Component
{
    public string? Class { get; set; }
    [CaptureUnmatchedAttributes] public AttributeDictionary? Rest { get; set; }

    protected override Node Build()
    {
        var separator = new Div { Role = Role.Separator };
        separator.MergeAttributes(Rest);
        separator.Attributes["data-slot"] = "input-otp-separator";
        separator.Class = TailwindMerge.Cn(separator.Class.Text, Class);
        separator.Children.Add(SelectParts.Glyph(LucideIcons.Minus, "input-otp-separator-icon", "size-4"));
        return separator;
    }
}

/// <summary>Tracks the mounted slot elements so navigation can focus them by index.</summary>
internal sealed class OtpSlots
{
    internal sealed class Entry(int index, Element? element)
    {
        internal int Index { get; } = index;
        internal Element? Element { get; } = element;
    }

    readonly List<Entry> _entries = new();
    readonly State<int> _count = new(0);

    internal int Count => _count.Value;

    internal Entry Register(int index, Element? element)
    {
        var entry = new Entry(index, element);
        _entries.Add(entry);
        _entries.Sort(static (a, b) => a.Index.CompareTo(b.Index));
        _count.Value = _entries.Count;
        return entry;
    }

    internal void Unregister(Entry entry)
    {
        if (_entries.Remove(entry)) _count.Value = _entries.Count;
    }

    internal bool Focus(int index)
    {
        if (index < 0) return false;
        Element? fallback = null;
        foreach (var entry in _entries)
        {
            var element = entry.Element;
            if (element is not { IsFocusable: true, IsVisible: true }) continue;
            if (entry.Index == index)
                return element.Focus(new FocusOptions { Origin = FocusOrigin.Keyboard });
            if (entry.Index > index && fallback is null) fallback = element;
        }
        return fallback?.Focus(new FocusOptions { Origin = FocusOrigin.Keyboard }) ?? false;
    }
}

internal static class InputOtpParts
{
    const KeyModifiers CommandModifiers = KeyModifiers.Control | KeyModifiers.Alt | KeyModifiers.Meta;

    internal static string Bool(bool value) => value ? "true" : "false";

    internal static string InputMode(InputOTP.Scope scope) => scope.IsNumeric ? "numeric" : "text";

    internal static void HandleInput(InputEvent e, InputOTP.Scope scope, int index)
    {
        if (scope.Disabled || e.DefaultPrevented) return;
        string oldText = scope.CharAt(index);
        string newText = e.Value ?? "";
        if (string.Equals(oldText, newText, StringComparison.Ordinal))
        {
            scope.RequestRender();
            return;
        }

        if (newText.Length == 0)
        {
            // A deletion that did not go through Backspace (for example cut or select-all then type nothing).
            if (oldText.Length > 0) scope.SetValue(RemoveAt(scope.Value, index));
            else scope.RequestRender();
            return;
        }

        string inserted;
        if (oldText.Length > 0 && newText.Length > oldText.Length && newText.StartsWith(oldText, StringComparison.Ordinal))
            inserted = newText.Substring(oldText.Length);
        else if (oldText.Length > 0 && newText.Length > oldText.Length && newText.EndsWith(oldText, StringComparison.Ordinal))
            inserted = newText.Substring(0, newText.Length - oldText.Length);
        else
            inserted = newText;

        string filtered = Filter(inserted, scope.IsAllowed);
        if (filtered.Length == 0)
        {
            scope.RequestRender();
            return;
        }

        int capacity = scope.Capacity;
        string value = Place(scope.Value, index, filtered, capacity);
        scope.SetValue(value);

        int next = index + filtered.Length;
        if (capacity > 0 && next > capacity - 1) next = capacity - 1;
        scope.FocusSlot(next);
    }

    internal static void HandleKey(KeyEvent e, InputOTP.Scope scope, int index)
    {
        if (scope.Disabled || e.DefaultPrevented) return;
        if ((e.Modifiers & CommandModifiers) != 0) return;

        switch (e.Key)
        {
            case "Backspace":
                e.PreventDefault();
                if (index < scope.Value.Length)
                {
                    scope.SetValue(RemoveAt(scope.Value, index));
                    scope.FocusSlot(index > 0 ? index - 1 : 0);
                }
                else if (index > 0)
                {
                    scope.SetValue(RemoveAt(scope.Value, index - 1));
                    scope.FocusSlot(index - 1);
                }
                return;

            case "Delete":
                e.PreventDefault();
                if (index < scope.Value.Length) scope.SetValue(RemoveAt(scope.Value, index));
                scope.FocusSlot(index);
                return;

            case "ArrowLeft":
                e.PreventDefault();
                scope.FocusSlot(index - 1);
                return;

            case "ArrowRight":
                e.PreventDefault();
                scope.FocusSlot(index + 1);
                return;

            case "Home":
                e.PreventDefault();
                scope.FocusSlot(0);
                return;

            case "End":
                e.PreventDefault();
                scope.FocusSlot(scope.Capacity - 1);
                return;
        }
    }

    static string RemoveAt(string value, int index) =>
        index >= 0 && index < value.Length ? value.Remove(index, 1) : value;

    static string Filter(string text, Func<char, bool> allowed)
    {
        StringBuilder? builder = null;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (char.IsSurrogate(c) || !allowed(c)) continue;
            builder ??= new StringBuilder(text.Length);
            builder.Append(c);
        }
        return builder?.ToString() ?? "";
    }

    static string Place(string value, int index, string characters, int capacity)
    {
        if (index < 0) index = 0;
        if (index > value.Length) index = value.Length;
        var builder = new StringBuilder(value);
        int position = index;
        for (int i = 0; i < characters.Length; i++)
        {
            if (capacity > 0 && position >= capacity) break;
            if (position < builder.Length) builder[position] = characters[i];
            else builder.Append(characters[i]);
            position++;
        }
        return builder.ToString();
    }
}
