using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Threading;
using AwAUI.Controls.Primitives;
using AwAUI.Tailwind;
using AwAUI.UI;
using BuiltInInput = AwAUI.UI.Input;
using BuiltInTextarea = AwAUI.UI.TextArea;
using ShadcnInput = AwAUI.Shadcn.Input;
using ShadcnTextarea = AwAUI.Shadcn.Textarea;

namespace AwAUI.Shadcn;

/// <summary>
/// A validator for one field value: returns an error message, or <c>null</c> when the value is valid.
/// Rules run in order and the first failure wins. Values are strings because AwAUI's form controls edit text;
/// convert inside the delegate when typed validation is needed:
/// <code>
/// FormValidators.Custom(v =&gt; int.TryParse(v, out var n) &amp;&amp; n &gt; 0 ? null : "Enter a positive number.")
/// </code>
/// </summary>
public delegate string? FormValidator(string? value);

/// <summary>Ready-made <see cref="FormValidator"/> factories. Combine them in <see cref="FormField.Validators"/>.</summary>
public static class FormValidators
{
    /// <summary>Fails when the value is null, empty, or only whitespace.</summary>
    public static FormValidator Required(string message = "This field is required.") =>
        value => string.IsNullOrWhiteSpace(value) ? message : null;

    /// <summary>Fails when the trimmed value is shorter than <paramref name="length"/>.</summary>
    public static FormValidator MinLength(int length, string? message = null)
    {
        if (length < 0) throw new ArgumentOutOfRangeException(nameof(length));
        return value => (value ?? "").Length >= length ? null
            : message ?? string.Format(CultureInfo.InvariantCulture, "Use at least {0} characters.", length);
    }

    /// <summary>Fails when the value is longer than <paramref name="length"/>.</summary>
    public static FormValidator MaxLength(int length, string? message = null)
    {
        if (length < 0) throw new ArgumentOutOfRangeException(nameof(length));
        return value => (value ?? "").Length <= length ? null
            : message ?? string.Format(CultureInfo.InvariantCulture, "Use at most {0} characters.", length);
    }

    /// <summary>Fails when the value does not match the regular expression in full.</summary>
    public static FormValidator Pattern(string pattern, string message = "The value has the wrong format.")
    {
        if (pattern is null) throw new ArgumentNullException(nameof(pattern));
        var regex = new Regex(pattern, RegexOptions.CultureInvariant);
        return value =>
        {
            string text = value ?? "";
            var match = regex.Match(text);
            return match.Success && match.Index == 0 && match.Length == text.Length ? null : message;
        };
    }

    /// <summary>A pragmatic e-mail check (non-empty local part, one <c>@</c>, dotted domain).</summary>
    public static FormValidator Email(string message = "Enter a valid e-mail address.") =>
        value => string.IsNullOrWhiteSpace(value) || !s_email.IsMatch(value!) ? message : null;

    /// <summary>Wraps an arbitrary predicate and message.</summary>
    public static FormValidator Custom(Func<string?, bool> isValid, string message)
    {
        if (isValid is null) throw new ArgumentNullException(nameof(isValid));
        if (message is null) throw new ArgumentNullException(nameof(message));
        return value => isValid(value) ? null : message;
    }

    static readonly Regex s_email = new(
        @"^[^@\s]+@[^@\s]+\.[^@\s]+$", RegexOptions.CultureInvariant);
}

/// <summary>
/// Per-field state owned by a <see cref="FormController"/>. Read <see cref="Value"/>, <see cref="Error"/>, and the
/// flags from the controller; the form parts read it automatically.
/// </summary>
public sealed class FormFieldState
{
    internal FormFieldState(string name, string initialValue)
    {
        Name = name;
        InitialValue = initialValue ?? "";
        Value = InitialValue;
    }

    /// <summary>The field name used by <see cref="FormController.GetValue"/> and the parts.</summary>
    public string Name { get; }

    /// <summary>The current value. Empty string when the user has not entered anything.</summary>
    public string Value { get; internal set; }

    /// <summary>The current error message, or <c>null</c> when the field is valid.</summary>
    public string? Error { get; internal set; }

    /// <summary>Whether the field currently has an error.</summary>
    public bool HasError => Error is not null;

    /// <summary>Whether the user has changed the value.</summary>
    public bool IsDirty { get; internal set; }

    /// <summary>Whether the containing form has been submitted at least once.</summary>
    public bool IsSubmitted { get; internal set; }

    internal string InitialValue { get; }

    internal IReadOnlyList<FormValidator>? Validators { get; set; }

    internal Action<string>? ValueChanged { get; set; }
}

/// <summary>
/// A lightweight form model in the spirit of react-hook-form: it owns the field values, runs the validators on submit
/// and, after the first submit, on every value change. Create one and pass it to <see cref="Form.Controller"/>, or let
/// <see cref="Form"/> create its own.
/// <para>
/// Validation: <see cref="Submit"/> marks every field as submitted, validates all of them, and invokes
/// <see cref="SubmitSucceeded"/> or <see cref="SubmitFailed"/>. <see cref="Validate"/> validates without marking the
/// fields as submitted. A change after the first submit revalidates that field immediately, so <see cref="FormMessage"/>
/// updates as the user types.
/// </para>
/// </summary>
public sealed class FormController
{
    readonly Dictionary<string, FormFieldState> _fields = new(StringComparer.Ordinal);
    readonly State<long> _revision = new(0L);

    /// <summary>Invoked after every <see cref="Submit"/>, on success and on failure.</summary>
    public Action<FormController>? Submitted { get; set; }

    /// <summary>Invoked after a <see cref="Submit"/> that found no errors.</summary>
    public Action<FormController>? SubmitSucceeded { get; set; }

    /// <summary>Invoked after a <see cref="Submit"/> that found at least one error.</summary>
    public Action<FormController>? SubmitFailed { get; set; }

    /// <summary>Whether the form has been submitted at least once.</summary>
    public bool IsSubmitted { get; private set; }

    /// <summary>Whether any field currently has an error.</summary>
    public bool HasErrors
    {
        get
        {
            foreach (var entry in _fields.Values)
                if (entry.Error is not null) return true;
            return false;
        }
    }

    /// <summary>The number of fields with an error.</summary>
    public int ErrorCount
    {
        get
        {
            int count = 0;
            foreach (var entry in _fields.Values)
                if (entry.Error is not null) count++;
            return count;
        }
    }

    /// <summary>Increments on every model change. Form parts read it to rebuild when validation results change.</summary>
    public long Revision => _revision.Value;

    /// <summary>The registered fields, keyed by name.</summary>
    public IReadOnlyDictionary<string, FormFieldState> Fields => _fields;

    /// <summary>
    /// Registers a field (or returns the existing registration) with its initial value and validators. Called by
    /// <see cref="FormField"/>; applications normally do not call it, but it is public so a hand-built form can share
    /// the same validation logic.
    /// </summary>
    public FormFieldState Register(string name, string? defaultValue, IReadOnlyList<FormValidator>? validators)
    {
        if (name is null) throw new ArgumentNullException(nameof(name));
        if (!_fields.TryGetValue(name, out var state))
        {
            state = new FormFieldState(name, defaultValue ?? "");
            state.IsSubmitted = IsSubmitted;
            _fields.Add(name, state);
        }
        state.Validators = validators;
        return state;
    }

    /// <summary>Registers a field without validators when it does not exist yet; keeps an existing field unchanged.</summary>
    public FormFieldState GetOrAdd(string name, string? defaultValue = null)
    {
        if (_fields.TryGetValue(name, out var existing)) return existing;
        return Register(name, defaultValue, null);
    }

    /// <summary>Removes a field from the model.</summary>
    public bool Unregister(string name)
    {
        if (!_fields.Remove(name)) return false;
        Bump();
        return true;
    }

    /// <summary>The current value, or <c>null</c> when the field is not registered.</summary>
    public string? GetValue(string name) => _fields.TryGetValue(name, out var state) ? state.Value : null;

    /// <summary>The current error for a field, or <c>null</c> when it is valid or unknown.</summary>
    public string? GetError(string name) => _fields.TryGetValue(name, out var state) ? state.Error : null;

    /// <summary>
    /// Updates a field value. After the first submit the field is revalidated immediately; before that the error stays
    /// unchanged. Calls the field's <c>ValueChanged</c> callback and schedules a rebuild when the value actually changed.
    /// </summary>
    public void SetValue(string name, string? value)
    {
        if (name is null || !_fields.TryGetValue(name, out var state)) return;
        value ??= "";
        if (string.Equals(state.Value, value, StringComparison.Ordinal)) return;
        state.Value = value;
        state.IsDirty = true;
        if (IsSubmitted || state.IsSubmitted) ValidateField(state);
        state.ValueChanged?.Invoke(value);
        Bump();
    }

    /// <summary>Validates one field and stores its error, without marking it submitted.</summary>
    public string? ValidateField(string name)
    {
        if (!_fields.TryGetValue(name, out var state)) return null;
        ValidateField(state);
        Bump();
        return state.Error;
    }

    /// <summary>Validates every registered field. Returns <c>true</c> when there are no errors.</summary>
    public bool Validate()
    {
        foreach (var state in _fields.Values) ValidateField(state);
        Bump();
        return ErrorCount == 0;
    }

    /// <summary>
    /// Marks every field submitted, validates, and invokes <see cref="Submitted"/> and the success or failure callback.
    /// Returns <c>true</c> when the form is valid.
    /// </summary>
    public bool Submit()
    {
        IsSubmitted = true;
        foreach (var state in _fields.Values)
        {
            state.IsSubmitted = true;
            ValidateField(state);
        }
        bool valid = ErrorCount == 0;
        Bump();
        Submitted?.Invoke(this);
        if (valid) SubmitSucceeded?.Invoke(this);
        else SubmitFailed?.Invoke(this);
        return valid;
    }

    /// <summary>Clears every error. The submitted flag and values stay.</summary>
    public void ClearErrors()
    {
        foreach (var state in _fields.Values) state.Error = null;
        Bump();
    }

    /// <summary>Restores the initial values and clears errors and the submitted flag.</summary>
    public void Reset()
    {
        foreach (var state in _fields.Values)
        {
            state.Value = state.InitialValue;
            state.Error = null;
            state.IsDirty = false;
            state.IsSubmitted = false;
        }
        IsSubmitted = false;
        Bump();
    }

    void Bump() => _revision.Value = _revision.Peek() + 1;

    static void ValidateField(FormFieldState state)
    {
        var validators = state.Validators;
        if (validators is null || validators.Count == 0)
        {
            state.Error = null;
            return;
        }
        string? error = null;
        for (int i = 0; i < validators.Count; i++)
        {
            var validator = validators[i];
            if (validator is null) continue;
            error = validator(state.Value);
            if (error is not null) break;
        }
        state.Error = error;
    }
}

/// <summary>
/// Provides field state and the submit entry point to <see cref="FormField"/>. <see cref="Form"/> creates its own
/// controller when <see cref="Controller"/> is not set. Call <see cref="FormController.Submit"/> from a submit button's
/// click handler (or from your own command) to validate and render errors.
/// </summary>
public sealed partial class Form : Component
{
    readonly FormController _controller = new();

    /// <summary>The form model. When omitted, the component creates and owns one.</summary>
    public FormController? Controller { get; set; }

    /// <summary>Assigned to <see cref="FormController.Submitted"/> when set.</summary>
    public Action<FormController>? OnSubmit { get; set; }

    /// <summary>Assigned to <see cref="FormController.SubmitSucceeded"/> when set.</summary>
    public Action<FormController>? OnValid { get; set; }

    /// <summary>Assigned to <see cref="FormController.SubmitFailed"/> when set.</summary>
    public Action<FormController>? OnInvalid { get; set; }

    public NodeList? Children { get; set; }

    protected override Node Build()
    {
        var controller = Controller ?? _controller;
        if (OnSubmit is not null) controller.Submitted = OnSubmit;
        if (OnValid is not null) controller.SubmitSucceeded = OnValid;
        if (OnInvalid is not null) controller.SubmitFailed = OnInvalid;
        return Provide(new Scope(controller), new Fragment(Children ?? new NodeList()));
    }

    internal sealed class Scope(FormController controller)
    {
        internal FormController Controller { get; } = controller;
    }
}

/// <summary>
/// Declares a named field, registers it with the nearest <see cref="Form"/>, and provides its state to
/// <see cref="FormItem"/> and the label/description/message parts. <see cref="DefaultValue"/> seeds the model;
/// <see cref="Value"/> and <see cref="ValueChanged"/> also allow the value to be driven from outside.
/// </summary>
public sealed partial class FormField : Component
{
    /// <summary>The field name used by <see cref="FormController"/>.</summary>
    public string Name { get; set; } = "";

    /// <summary>The initial value. Used only the first time the field is registered.</summary>
    public string? DefaultValue { get; set; }

    /// <summary>
    /// A controlled value. When set, it is synchronized into the model, so the caller can update the field from outside.
    /// Leave <c>null</c> to let the input drive the value.
    /// </summary>
    public string? Value { get; set; }

    /// <summary>Receives each accepted value and supports <c>@bind-Value</c>.</summary>
    public Action<string>? ValueChanged { get; set; }

    /// <summary>Validation rules, run in order; the first non-null message wins.</summary>
    public IReadOnlyList<FormValidator>? Validators { get; set; }

    public NodeList? Children { get; set; }

    protected override Node Build()
    {
        var form = Use<Form.Scope>();
        var controller = form.Controller;
        _ = controller.Revision;
        var state = controller.Register(Name, DefaultValue, Validators);
        state.ValueChanged = ValueChanged;
        UseEffect(() =>
        {
            if (Value is not null) controller.SetValue(Name, Value);
            return null;
        }, Value, Name);
        return Provide(new FormFieldScope(controller, state), new Fragment(Children ?? new NodeList()));
    }
}

/// <summary>The field snapshot read by <see cref="FormItem"/>, <see cref="FormLabel"/>, <see cref="FormControl"/>, and <see cref="FormMessage"/>.</summary>
internal sealed class FormFieldScope(FormController controller, FormFieldState state)
{
    internal FormController Controller { get; } = controller;
    internal FormFieldState State { get; } = state;
    internal string Name => State.Name;
    internal string? Error => State.Error;
    internal string Value => State.Value;
}

/// <summary>
/// Groups a label, control, description, and message for one field and owns their stable IDs, following the shadcn
/// <c>form-item</c> structure.
/// </summary>
public sealed partial class FormItem : Component
{
    readonly string _id = FormParts.NextId("form-item");
    readonly Ref<Element> _element = new();
    readonly FormItemScope _scope;

    public FormItem() => _scope = new FormItemScope(_id, _element);

    public string? Class { get; set; }
    public NodeList? Children { get; set; }
    [CaptureUnmatchedAttributes] public AttributeDictionary? Rest { get; set; }

    protected override Node Build()
    {
        _ = Use<FormFieldScope>();
        var item = new Div { Ref = _element, Children = Children ?? new NodeList() };
        item.MergeAttributes(Rest);
        item.Attributes["data-slot"] = "form-item";
        item.Class = TailwindMerge.Cn("grid gap-2", item.Class.Text, Class);
        return Provide(_scope, item);
    }
}

/// <summary>IDs and element lookup shared by the parts inside one <see cref="FormItem"/>.</summary>
internal sealed class FormItemScope(string id, Ref<Element> element)
{
    internal string Id { get; } = id;
    internal string FormItemId => Id + "-form-item";
    internal string DescriptionId => Id + "-form-item-description";
    internal string MessageId => Id + "-form-item-message";
    internal Ref<Element> Element { get; } = element;

    /// <summary>Focuses the first focusable control inside the item; used by the label's click fallback.</summary>
    internal bool FocusControl(FocusOrigin origin)
    {
        if (Element.Current is not { } root) return false;
        var control = FormParts.FirstFocusable(root);
        if (control is null) return false;
        return control.Focus(new FocusOptions { Origin = origin });
    }
}

/// <summary>The field label. Turns destructive when the field has an error and focuses the control when clicked.</summary>
public sealed partial class FormLabel : Component
{
    public string? Class { get; set; }
    public Ref<Element>? Ref { get; set; }
    public NodeList? Children { get; set; }
    [CaptureUnmatchedAttributes] public AttributeDictionary? Rest { get; set; }

    protected override Node Build()
    {
        var field = Use<FormFieldScope>();
        var item = Use<FormItemScope>();
        bool error = field.Error is not null;
        var attributes = new AttributeDictionary(Rest);
        attributes["data-error"] = FormParts.Bool(error);
        attributes["data-form-label"] = "true";
        var label = new Label
        {
            Id = null,
            For = item.FormItemId,
            Class = TailwindMerge.Cn("data-[error=true]:text-destructive", Class),
            Ref = Ref,
            Children = Children ?? new NodeList(),
            Rest = attributes,
            OnClick = e =>
            {
                if (e.DefaultPrevented) return;
                var origin = e.IsSynthetic ? FocusOrigin.Programmatic : FocusOrigin.Pointer;
                if (item.FocusControl(origin)) e.PreventDefault();
            },
        };
        return label;
    }
}

/// <summary>
/// Applies the field's ID and validation ARIA attributes to its single child control and binds the child's value to
/// the form model. Supports <see cref="Input"/>, <see cref="Textarea"/>, and the built-in <c>AwAUI.UI.Input</c> /
/// <c>AwAUI.UI.TextArea</c> nodes. For a custom control, set <see cref="FormField.ValueChanged"/> and bind the control
/// yourself; the ID and ARIA attributes are still applied to the first nested input element.
/// </summary>
public sealed partial class FormControl : Component
{
    public NodeList? Children { get; set; }

    protected override Node Build()
    {
        var field = Use<FormFieldScope>();
        var item = Use<FormItemScope>();
        string id = item.FormItemId;
        string describedBy = field.Error is null ? item.DescriptionId : item.DescriptionId + " " + item.MessageId;
        var child = Slot.SingleChild(Children);

        UseEffect(() =>
        {
            if (GetRootElements() is not { Count: > 0 } roots) return null;
            foreach (var root in roots)
                foreach (var input in FormParts.Inputs(root))
                {
                    input.SetAttributeOverride("id", id);
                    input.SetAttributeOverride("aria-invalid", field.Error is null ? "false" : "true");
                    input.SetAttributeOverride("aria-describedby", describedBy);
                    input.SetAttributeOverride("data-form-control", "true");
                }
            return null;
        }, id, describedBy, field.Error is null);

        switch (child)
        {
            case ShadcnInput input:
                return BindInput(input, field, id, describedBy);
            case ShadcnTextarea textarea:
                return BindTextarea(textarea, field, id, describedBy);
            case BuiltInTextarea textarea:
                return BindBuiltInTextarea(textarea, field, id, describedBy);
            case BuiltInInput input:
                return BindBuiltInInput(input, field, id, describedBy);
            default:
                // Custom control: keep it as-is; the effect above adds the IDs and ARIA to the first nested input.
                return new Fragment(Children ?? new NodeList());
        }
    }

    static ShadcnInput BindInput(ShadcnInput source, FormFieldScope field, string id, string describedBy)
    {
        var attributes = FormParts.ControlAttributes(source.Rest, field, describedBy);
        var externalChanged = source.ValueChanged;
        var copy = new ShadcnInput
        {
            Key = source.Key,
            Id = id,
            Value = field.Value,
            ValueChanged = value =>
            {
                field.Controller.SetValue(field.Name, value);
                externalChanged?.Invoke(value);
            },
            InputType = source.InputType,
            Placeholder = source.Placeholder,
            Disabled = source.Disabled,
            ReadOnly = source.ReadOnly,
            Class = source.Class,
            Ref = source.Ref,
            OnInput = source.OnInput,
            OnChange = source.OnChange,
            OnSubmit = source.OnSubmit,
            Rest = attributes,
        };
        return copy;
    }

    static ShadcnTextarea BindTextarea(ShadcnTextarea source, FormFieldScope field, string id, string describedBy)
    {
        var attributes = FormParts.ControlAttributes(source.Rest, field, describedBy);
        var externalChanged = source.ValueChanged;
        var copy = new ShadcnTextarea
        {
            Key = source.Key,
            Id = id,
            Value = field.Value,
            ValueChanged = value =>
            {
                field.Controller.SetValue(field.Name, value);
                externalChanged?.Invoke(value);
            },
            Placeholder = source.Placeholder,
            Disabled = source.Disabled,
            ReadOnly = source.ReadOnly,
            Class = source.Class,
            Ref = source.Ref,
            OnInput = source.OnInput,
            OnChange = source.OnChange,
            OnSubmit = source.OnSubmit,
            Rest = attributes,
        };
        return copy;
    }

    static BuiltInInput BindBuiltInInput(BuiltInInput source, FormFieldScope field, string id, string describedBy)
    {
        var copy = new BuiltInInput
        {
            Key = source.Key,
            Id = id,
            Value = field.Value,
            Placeholder = source.Placeholder,
            InputType = source.InputType,
            Disabled = source.Disabled,
            ReadOnly = source.ReadOnly,
            Class = source.Class,
            Style = source.Style,
            Ref = source.Ref,
            Role = source.Role,
            AriaLabel = source.AriaLabel,
            TabIndex = source.TabIndex,
            ScrollController = source.ScrollController,
            CaretBlinkInterval = source.CaretBlinkInterval,
            OnInput = e =>
            {
                field.Controller.SetValue(field.Name, e.Value);
                source.OnInput?.Invoke(e);
            },
            OnChange = source.OnChange,
            OnSubmit = source.OnSubmit,
        };
        copy.MergeAttributes(source.Attributes);
        FormParts.ApplyControlAttributes(copy, field, describedBy);
        return copy;
    }

    static BuiltInTextarea BindBuiltInTextarea(BuiltInTextarea source, FormFieldScope field, string id, string describedBy)
    {
        var copy = new BuiltInTextarea
        {
            Key = source.Key,
            Id = id,
            Value = field.Value,
            Placeholder = source.Placeholder,
            Disabled = source.Disabled,
            ReadOnly = source.ReadOnly,
            Class = source.Class,
            Style = source.Style,
            Ref = source.Ref,
            Role = source.Role,
            AriaLabel = source.AriaLabel,
            TabIndex = source.TabIndex,
            ScrollController = source.ScrollController,
            CaretBlinkInterval = source.CaretBlinkInterval,
            OnInput = e =>
            {
                field.Controller.SetValue(field.Name, e.Value);
                source.OnInput?.Invoke(e);
            },
            OnChange = source.OnChange,
            OnSubmit = source.OnSubmit,
        };
        copy.MergeAttributes(source.Attributes);
        FormParts.ApplyControlAttributes(copy, field, describedBy);
        return copy;
    }
}

/// <summary>A helper paragraph under the control; its ID is included in the control's <c>aria-describedby</c>.</summary>
public sealed partial class FormDescription : Component
{
    public string? Class { get; set; }
    public NodeList? Children { get; set; }
    [CaptureUnmatchedAttributes] public AttributeDictionary? Rest { get; set; }

    protected override Node Build()
    {
        _ = Use<FormFieldScope>();
        var item = Use<FormItemScope>();
        var paragraph = new FormParts.Paragraph
        {
            Id = item.DescriptionId,
            Children = Children ?? new NodeList(),
        };
        paragraph.MergeAttributes(Rest);
        paragraph.Attributes["data-slot"] = "form-description";
        paragraph.Class = TailwindMerge.Cn("text-sm text-muted-foreground", paragraph.Class.Text, Class);
        return paragraph;
    }
}

/// <summary>The field's error message. Renders nothing (no element) when the field is valid and there is no fallback content.</summary>
public sealed partial class FormMessage : Component
{
    public string? Class { get; set; }
    public NodeList? Children { get; set; }
    [CaptureUnmatchedAttributes] public AttributeDictionary? Rest { get; set; }

    protected override Node Build()
    {
        var field = Use<FormFieldScope>();
        var item = Use<FormItemScope>();
        string? error = field.Error;
        if (error is null && (Children is null || Children.Count == 0)) return new Fragment();
        var paragraph = new FormParts.Paragraph { Id = item.MessageId };
        if (error is not null) paragraph.Children.Add(error);
        else paragraph.Children.AddRange(Children);
        paragraph.MergeAttributes(Rest);
        paragraph.Attributes["data-slot"] = "form-message";
        paragraph.Class = TailwindMerge.Cn("text-sm text-destructive", paragraph.Class.Text, Class);
        return paragraph;
    }
}

internal static class FormParts
{
    static long s_nextId;

    internal static string NextId(string part) =>
        "aw-" + part + "-" + Interlocked.Increment(ref s_nextId).ToString(CultureInfo.InvariantCulture);

    internal static string Bool(bool value) => value ? "true" : "false";

    /// <summary>Copies a caller's attributes and applies the control's ID/validation ARIA state.</summary>
    internal static AttributeDictionary ControlAttributes(AttributeDictionary? source, FormFieldScope field, string describedBy)
    {
        var attributes = new AttributeDictionary(source);
        attributes["data-form-control"] = "true";
        attributes["aria-invalid"] = field.Error is null ? "false" : "true";
        attributes["aria-describedby"] = describedBy;
        return attributes;
    }

    internal static void ApplyControlAttributes(ElementNode node, FormFieldScope field, string describedBy)
    {
        node.Attributes["data-form-control"] = "true";
        node.Attributes["aria-invalid"] = field.Error is null ? "false" : "true";
        node.Attributes["aria-describedby"] = describedBy;
    }

    internal static IEnumerable<InputElement> Inputs(Element element)
    {
        if (element is InputElement input) yield return input;
        var children = element.ElementChildren;
        for (int i = 0; i < children.Count; i++)
            foreach (var nested in Inputs(children[i])) yield return nested;
    }

    internal static Element? FirstFocusable(Element root)
    {
        var pending = new Stack<Element>();
        var children = root.ElementChildren;
        for (int i = children.Count - 1; i >= 0; i--) pending.Push(children[i]);
        while (pending.Count > 0)
        {
            var element = pending.Pop();
            if (element.IsFocusable && element.IsVisible) return element;
            var nested = element.ElementChildren;
            for (int i = nested.Count - 1; i >= 0; i--) pending.Push(nested[i]);
        }
        return null;
    }

    /// <summary>The element node for a <c>p</c>; Form's description and message are real paragraphs.</summary>
    internal sealed class Paragraph : ElementNode
    {
        protected override Element CreateElement() => new Element("p");
    }
}
