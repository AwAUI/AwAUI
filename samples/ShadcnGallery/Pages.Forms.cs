using System;
using System.Collections.Generic;
using AwAUI.Shadcn;
using AwAUI.UI;
using Label = AwAUI.Shadcn.Label;
using ShadcnInput = AwAUI.Shadcn.Input;
using Text = AwAUI.UI.Text;

namespace ShadcnGallery;

/// <summary>
/// Form pages: text entry, selection controls, field composition, validation, and date entry.
/// The examples are uncontrolled wherever possible so they work without page-level state.
/// </summary>
public static partial class FormsPages
{
    public static void Register()
    {
        DocsCatalog.Add("checkbox", "Checkbox",
            DocsExample.Of("Basic", CheckboxBasic),
            DocsExample.Of("With label", CheckboxLabel),
            DocsExample.Of("Description", CheckboxDescription),
            DocsExample.Of("Group", CheckboxGroupExample),
            DocsExample.Of("Disabled", CheckboxDisabled),
            DocsExample.Of("Invalid", CheckboxInvalid));

        DocsCatalog.Add("field", "Field",
            DocsExample.Of("Field with description", FieldBasic),
            DocsExample.Of("Field group", FieldGroupExample),
            DocsExample.Of("Horizontal field", FieldHorizontal),
            DocsExample.Of("Error state", FieldErrorExample));

        DocsCatalog.Add("form", "Form",
            DocsExample.Of("Account form", () => new FormsAccountForm()),
            DocsExample.Of("Sign in", () => new FormsAccountForm { SignIn = true }));

        DocsCatalog.Add("input", "Input",
            DocsExample.Of("Basic", InputBasic),
            DocsExample.Of("Types", InputTypes),
            DocsExample.Of("Disabled and read only", InputDisabled),
            DocsExample.Of("Invalid", InputInvalid));

        DocsCatalog.Add("input-group", "Input Group",
            DocsExample.Of("With text addon", InputGroupTextExample),
            DocsExample.Of("With button", InputGroupButtonExample),
            DocsExample.Of("With textarea", InputGroupTextareaExample));

        DocsCatalog.Add("input-otp", "Input OTP",
            DocsExample.Of("Basic", InputOtpBasic),
            DocsExample.Of("Alphanumeric", InputOtpAlphanumeric),
            DocsExample.Of("Disabled", InputOtpDisabled),
            DocsExample.Of("Invalid", InputOtpInvalid));

        DocsCatalog.Add("native-select", "Native Select",
            DocsExample.Of("Basic", NativeSelectBasic),
            DocsExample.Of("Grouped", NativeSelectGrouped),
            DocsExample.Of("Small", NativeSelectSmall),
            DocsExample.Of("Invalid", NativeSelectInvalid),
            DocsExample.Of("Disabled", NativeSelectDisabled));

        DocsCatalog.Add("radio-group", "Radio Group",
            DocsExample.Of("Basic", RadioGroupBasic),
            DocsExample.Of("With descriptions", RadioGroupDescriptions),
            DocsExample.Of("Disabled", RadioGroupDisabled));

        DocsCatalog.Add("select", "Select",
            DocsExample.Of("Basic", SelectBasic),
            DocsExample.Of("Grouped", SelectGrouped),
            DocsExample.Of("Small", SelectSmall),
            DocsExample.Of("Invalid", SelectInvalid),
            DocsExample.Of("Disabled", SelectDisabled));

        DocsCatalog.Add("slider", "Slider",
            DocsExample.Of("Basic", SliderBasic),
            DocsExample.Of("Range", () => new SliderRangeDemo()),
            DocsExample.Of("Stepped value", SliderStepped),
            DocsExample.Of("Disabled", SliderDisabled));

        DocsCatalog.Add("switch", "Switch",
            DocsExample.Of("Basic", SwitchBasic),
            DocsExample.Of("With label", SwitchLabel),
            DocsExample.Of("Small", SwitchSmall),
            DocsExample.Of("Disabled", SwitchDisabled));

        DocsCatalog.Add("textarea", "Textarea",
            DocsExample.Of("Basic", TextareaBasic),
            DocsExample.Of("With label", TextareaLabel),
            DocsExample.Of("Disabled", TextareaDisabled));

        DocsCatalog.Add("calendar", "Calendar",
            DocsExample.Of("Basic", CalendarBasic),
            DocsExample.Of("Range", () => new CalendarRangeDemo()),
            DocsExample.Of("Caption dropdown", CalendarCaptionDropdownExample),
            DocsExample.Of("Week numbers", CalendarWeekNumbers),
            DocsExample.Of("Disabled dates", CalendarDisabledDates));

        DocsCatalog.Add("date-picker", "Date Picker",
            DocsExample.Of("Basic", DatePickerBasic),
            DocsExample.Of("Range", DatePickerRange),
            DocsExample.Of("With clear action", DatePickerClear),
            DocsExample.Of("Date of birth", DatePickerBirth),
            DocsExample.Of("Disabled", DatePickerDisabled));
    }

    // ------------------------------------------------------------------ Checkbox

    static Node CheckboxBasic() => DocsUi.Stack(
        CheckboxRow("forms-check-basic-a", "Email notifications"),
        CheckboxRow("forms-check-basic-b", "Product updates", isChecked: true),
        DocsUi.Note("Leave DefaultChecked unset for an unchecked box; set it to show an initial selection."));

    static Node CheckboxLabel() => DocsUi.Stack(
        new Field
        {
            Orientation = FieldOrientation.Horizontal,
            Children =
            [
                new Checkbox { Id = "forms-check-terms", DefaultChecked = true },
                new FieldContent
                {
                    Children =
                    [
                        new FieldLabel { For = "forms-check-terms", Children = [new Text("Accept terms and conditions")] },
                        new FieldDescription
                        {
                            Children = [new Text("You agree to our Terms of Service and Privacy Policy.")],
                        },
                    ],
                },
            ],
        });

    static Node CheckboxDescription() => new Field
    {
        Orientation = FieldOrientation.Horizontal,
        Children =
        [
            new Checkbox { Id = "forms-check-desc", DefaultChecked = true },
            new FieldContent
            {
                Children =
                [
                    new FieldLabel { For = "forms-check-desc", Children = [new Text("Enable notifications")] },
                    new FieldDescription
                    {
                        Children = [new Text("You can enable or disable notifications at any time.")],
                    },
                ],
            },
        ],
    };

    static Node CheckboxGroupExample() => new FieldSet
    {
        Children =
        [
            new FieldLegend { Children = [new Text("Show these items")] },
            new FieldGroup
            {
                Children =
                [
                    CheckboxRow("forms-check-group-a", "Recents"),
                    CheckboxRow("forms-check-group-b", "Home", isChecked: true),
                    CheckboxRow("forms-check-group-c", "Applications"),
                ],
            },
        ],
    };

    static Node CheckboxDisabled() => DocsUi.Stack(
        CheckboxRow("forms-check-disabled-a", "Disabled unchecked", disabled: true),
        CheckboxRow("forms-check-disabled-b", "Disabled checked", isChecked: true, disabled: true));

    static Node CheckboxInvalid() => DocsUi.Stack(
        new Field
        {
            Invalid = true,
            Children =
            [
                new Checkbox
                {
                    Id = "forms-check-invalid",
                    Rest = new AttributeDictionary { ["aria-invalid"] = "true" },
                },
                new FieldLabel { For = "forms-check-invalid", Children = [new Text("Accept the terms to continue")] },
                new FieldError { Errors = new string?[] { "You must accept the terms." } },
            ],
        },
        DocsUi.Note("aria-invalid switches the box to the destructive border; FieldError supplies the message."));

    static Div CheckboxRow(string id, string text, bool isChecked = false, bool disabled = false) => DocsUi.Row(
        new Checkbox { Id = id, DefaultChecked = isChecked, Disabled = disabled },
        new Label { For = id, Children = [new Text(text)] });

    // ------------------------------------------------------------------ Field

    static Node FieldBasic() => DocsUi.Stack(
        new Field
        {
            Children =
            [
                new FieldLabel { For = "forms-field-email", Children = [new Text("Email")] },
                new ShadcnInput { Id = "forms-field-email", InputType = "email", Placeholder = "you@example.com" },
                new FieldDescription { Children = [new Text("We'll only use this address for account notifications.")] },
            ],
        });

    static Node FieldGroupExample() => new FieldGroup
    {
        Children =
        [
            new Field
            {
                Children =
                [
                    new FieldLabel { For = "forms-field-first", Children = [new Text("First name")] },
                    new ShadcnInput { Id = "forms-field-first", Placeholder = "Alex" },
                ],
            },
            new Field
            {
                Children =
                [
                    new FieldLabel { For = "forms-field-last", Children = [new Text("Last name")] },
                    new ShadcnInput { Id = "forms-field-last", Placeholder = "Rivera" },
                ],
            },
            new FieldSeparator { Children = [new Text("Or")] },
            new Field
            {
                Children =
                [
                    new FieldLabel { For = "forms-field-handle", Children = [new Text("Username")] },
                    new ShadcnInput { Id = "forms-field-handle", Placeholder = "@alexrivera" },
                    new FieldDescription { Children = [new Text("This is your public display name.")] },
                ],
            },
        ],
    };

    static Node FieldHorizontal() => DocsUi.Stack(
        new Field
        {
            Orientation = FieldOrientation.Horizontal,
            Children =
            [
                new Checkbox { Id = "forms-field-news" },
                new FieldContent
                {
                    Children =
                    [
                        new FieldLabel { For = "forms-field-news", Children = [new Text("Newsletter")] },
                        new FieldDescription { Children = [new Text("Receive a monthly summary of changes.")] },
                    ],
                },
            ],
        },
        new Field
        {
            Orientation = FieldOrientation.Horizontal,
            Children =
            [
                new Switch { Id = "forms-field-2fa", DefaultChecked = true },
                new FieldContent
                {
                    Children =
                    [
                        new FieldLabel { For = "forms-field-2fa", Children = [new Text("Two-factor authentication")] },
                        new FieldDescription { Children = [new Text("Require a code when signing in from a new device.")] },
                    ],
                },
            ],
        });

    static Node FieldErrorExample() => DocsUi.Stack(
        new Field
        {
            Invalid = true,
            Children =
            [
                new FieldLabel { For = "forms-field-user", Children = [new Text("Username")] },
                new ShadcnInput
                {
                    Id = "forms-field-user",
                    Value = "al",
                    Rest = new AttributeDictionary { ["aria-invalid"] = "true" },
                },
                new FieldDescription { Children = [new Text("Pick a unique username.")] },
                new FieldError { Errors = new string?[] { "Username must be at least 3 characters." } },
            ],
        },
        DocsUi.Note("Set Invalid on Field and aria-invalid on the control; FieldError is announced as an alert."));

    // ------------------------------------------------------------------ Input

    static Node InputBasic() => DocsUi.Stack(
        DocsUi.Field(
            new Label { For = "forms-input-basic", Children = [new Text("Name")] },
            new ShadcnInput { Id = "forms-input-basic", Placeholder = "Alex Rivera" }),
        DocsUi.Note("Input renders a single-line editor with the shared focus ring and invalid styling."));

    static Node InputTypes() => DocsUi.Stack(
        DocsUi.Field(
            new Label { For = "forms-input-email", Children = [new Text("Email")] },
            new ShadcnInput { Id = "forms-input-email", InputType = "email", Placeholder = "you@example.com" }),
        DocsUi.Field(
            new Label { For = "forms-input-password", Children = [new Text("Password")] },
            new ShadcnInput { Id = "forms-input-password", InputType = "password", Placeholder = "Enter your password" }),
        DocsUi.Field(
            new Label { For = "forms-input-number", Children = [new Text("Seats")] },
            new ShadcnInput { Id = "forms-input-number", InputType = "number", Placeholder = "10" }));

    static Node InputDisabled() => DocsUi.Stack(
        DocsUi.Field(
            new Label { For = "forms-input-disabled", Children = [new Text("Disabled")] },
            new ShadcnInput { Id = "forms-input-disabled", Value = "Not editable", Disabled = true }),
        DocsUi.Field(
            new Label { For = "forms-input-readonly", Children = [new Text("Read only")] },
            new ShadcnInput { Id = "forms-input-readonly", Value = "Copy this value", ReadOnly = true }));

    static Node InputInvalid() => DocsUi.Stack(
        new Field
        {
            Invalid = true,
            Children =
            [
                new FieldLabel { For = "forms-input-invalid", Children = [new Text("Email")] },
                new ShadcnInput
                {
                    Id = "forms-input-invalid",
                    InputType = "email",
                    Value = "not-an-email",
                    Rest = new AttributeDictionary { ["aria-invalid"] = "true" },
                },
                new FieldError { Errors = new string?[] { "Enter a valid email address." } },
            ],
        });

    // ------------------------------------------------------------------ Input Group

    static Node InputGroupTextExample() => DocsUi.Stack(
        Box(
            new InputGroup
            {
                Children =
                [
                    new InputGroupAddon
                    {
                        Align = InputGroupAlign.InlineStart,
                        Children = [new InputGroupText { Children = [new Text("https://")] }],
                    },
                    new InputGroupInput
                    {
                        Placeholder = "example.com",
                        Rest = new AttributeDictionary { ["aria-label"] = "Website" },
                    },
                    new InputGroupAddon
                    {
                        Align = InputGroupAlign.InlineEnd,
                        Children = [new InputGroupText { Children = [new Text(".dev")] }],
                    },
                ],
            },
            360));

    static Node InputGroupButtonExample() => DocsUi.Stack(
        Box(
            new InputGroup
            {
                Children =
                [
                    new InputGroupInput
                    {
                        Placeholder = "Search components",
                        Rest = new AttributeDictionary { ["aria-label"] = "Search components" },
                    },
                    new InputGroupAddon
                    {
                        Align = InputGroupAlign.InlineEnd,
                        Children =
                        [
                            new InputGroupButton
                            {
                                Variant = ButtonVariant.Outline,
                                Size = InputGroupButtonSize.Sm,
                                Children = [new Text("Search")],
                            },
                        ],
                    },
                ],
            },
            360),
        DocsUi.Note("InputGroupButton uses a compact shadcn button; clicking the addon focuses the editor."));

    static Node InputGroupTextareaExample() => DocsUi.Stack(
        Box(
            new InputGroup
            {
                Children =
                [
                    new InputGroupTextarea
                    {
                        Placeholder = "Leave a comment",
                        Rest = new AttributeDictionary { ["aria-label"] = "Comment" },
                    },
                    new InputGroupAddon
                    {
                        Align = InputGroupAlign.BlockEnd,
                        Children =
                        [
                            new InputGroupButton
                            {
                                Variant = ButtonVariant.Default,
                                Size = InputGroupButtonSize.Sm,
                                Children = [new Text("Send")],
                            },
                        ],
                    },
                ],
            },
            360));

    // ------------------------------------------------------------------ Input OTP

    static Node InputOtpBasic() => OtpExample(defaultValue: "123456", pattern: null, disabled: false, invalid: false);

    static Node InputOtpAlphanumeric() => DocsUi.Stack(
        OtpExample(defaultValue: "A1B2C3", pattern: "[A-Za-z0-9]", disabled: false, invalid: false),
        DocsUi.Note("Pattern filters each character; the slot labels and inputmode follow it."));

    static Node InputOtpDisabled() => OtpExample(defaultValue: "123456", pattern: null, disabled: true, invalid: false);

    static Node InputOtpInvalid() => DocsUi.Stack(
        OtpExample(defaultValue: "123", pattern: null, disabled: false, invalid: true),
        DocsUi.Note("Invalid marks every slot aria-invalid and adds the destructive border."));

    static Node OtpExample(string? defaultValue, string? pattern, bool disabled, bool invalid) => new InputOTP
    {
        DefaultValue = defaultValue,
        MaxLength = 6,
        Pattern = pattern,
        Disabled = disabled,
        Invalid = invalid,
        AriaLabel = "One-time code",
        Children =
        [
            OtpGroup(0, 2),
            new InputOTPSeparator(),
            OtpGroup(3, 5),
        ],
    };

    static InputOTPGroup OtpGroup(int first, int last)
    {
        var slots = new NodeList();
        for (int index = first; index <= last; index++)
            slots.Add(new InputOTPSlot { Index = index });
        return new InputOTPGroup { Children = slots };
    }

    // ------------------------------------------------------------------ Native Select

    static Node NativeSelectBasic() => new NativeSelect
    {
        Rest = new AttributeDictionary { ["aria-label"] = "Status" },
        Children =
        [
            new NativeSelectOption { Value = "", Children = [new Text("Select status")] },
            new NativeSelectOption { Value = "todo", Children = [new Text("Todo")] },
            new NativeSelectOption { Value = "in-progress", Children = [new Text("In Progress")] },
            new NativeSelectOption { Value = "done", Children = [new Text("Done")] },
            new NativeSelectOption { Value = "cancelled", Children = [new Text("Cancelled")] },
        ],
    };

    static Node NativeSelectGrouped() => new NativeSelect
    {
        DefaultValue = "carrot",
        Rest = new AttributeDictionary { ["aria-label"] = "Produce" },
        Children =
        [
            new NativeSelectOptGroup
            {
                Label = "Fruits",
                Children =
                [
                    new NativeSelectOption { Value = "apple", Children = [new Text("Apple")] },
                    new NativeSelectOption { Value = "banana", Children = [new Text("Banana")] },
                    new NativeSelectOption
                    {
                        Value = "passion-fruit",
                        Disabled = true,
                        Children = [new Text("Passion fruit (sold out)")],
                    },
                ],
            },
            new NativeSelectOptGroup
            {
                Label = "Vegetables",
                Children =
                [
                    new NativeSelectOption { Value = "carrot", Children = [new Text("Carrot")] },
                    new NativeSelectOption { Value = "potato", Children = [new Text("Potato")] },
                ],
            },
        ],
    };

    static Node NativeSelectSmall() => new NativeSelect
    {
        DefaultValue = "banana",
        Size = NativeSelectSize.Sm,
        Rest = new AttributeDictionary { ["aria-label"] = "Fruit" },
        Children =
        [
            new NativeSelectOption { Value = "apple", Children = [new Text("Apple")] },
            new NativeSelectOption { Value = "banana", Children = [new Text("Banana")] },
            new NativeSelectOption { Value = "cherry", Children = [new Text("Cherry")] },
        ],
    };

    static Node NativeSelectInvalid() => new NativeSelect
    {
        Rest = new AttributeDictionary { ["aria-label"] = "Fruit", ["aria-invalid"] = "true" },
        Children =
        [
            new NativeSelectOption { Value = "", Children = [new Text("Select a fruit")] },
            new NativeSelectOption { Value = "apple", Children = [new Text("Apple")] },
            new NativeSelectOption { Value = "banana", Children = [new Text("Banana")] },
        ],
    };

    static Node NativeSelectDisabled() => new NativeSelect
    {
        DefaultValue = "apple",
        Disabled = true,
        Rest = new AttributeDictionary { ["aria-label"] = "Fruit" },
        Children =
        [
            new NativeSelectOption { Value = "apple", Children = [new Text("Apple")] },
            new NativeSelectOption { Value = "banana", Children = [new Text("Banana")] },
        ],
    };

    // ------------------------------------------------------------------ Radio Group

    static Node RadioGroupBasic() => new RadioGroup
    {
        DefaultValue = "comfortable",
        Children =
        [
            RadioRow("forms-radio-comfortable", "comfortable", "Comfortable"),
            RadioRow("forms-radio-compact", "compact", "Compact"),
            RadioRow("forms-radio-roomy", "roomy", "Roomy"),
        ],
    };

    static Node RadioGroupDescriptions() => new RadioGroup
    {
        DefaultValue = "all",
        Children =
        [
            RadioRow("forms-radio-all", "all", "All activity", "Show every update from the workspace."),
            RadioRow("forms-radio-mentions", "mentions", "Mentions only", "Only notify me when someone mentions me."),
            RadioRow("forms-radio-none", "none", "Nothing", "Turn off all activity notifications."),
        ],
    };

    static Node RadioGroupDisabled() => new RadioGroup
    {
        DefaultValue = "comfortable",
        Disabled = true,
        Children =
        [
            RadioRow("forms-radio-disabled-a", "comfortable", "Comfortable"),
            RadioRow("forms-radio-disabled-b", "compact", "Compact"),
        ],
    };

    static Node RadioRow(string id, string value, string label, string? description = null)
    {
        var item = new RadioGroupItem { Id = id, Value = value };
        if (description is null)
            return DocsUi.Row(item, new Label { For = id, Children = [new Text(label)] });

        return new Field
        {
            Orientation = FieldOrientation.Horizontal,
            Children =
            [
                item,
                new FieldContent
                {
                    Children =
                    [
                        new FieldLabel { For = id, Children = [new Text(label)] },
                        new FieldDescription { Children = [new Text(description)] },
                    ],
                },
            ],
        };
    }

    // ------------------------------------------------------------------ Select

    static Node SelectBasic() => new Select
    {
        Children =
        [
            new SelectTrigger
            {
                // 页面里的 Class 不参与库的 Tailwind 生成，官方的 w-[180px] 用行内样式表达。
                Rest = new AttributeDictionary { ["aria-label"] = "Fruit", ["style"] = "width:180px" },
                Children = [new SelectValue { Placeholder = "Select a fruit" }],
            },
            new SelectContent
            {
                Children =
                [
                    new SelectGroup
                    {
                        Children =
                        [
                            new SelectLabel { Children = [new Text("Fruits")] },
                            new SelectItem { Value = "apple", Children = [new Text("Apple")] },
                            new SelectItem { Value = "banana", Children = [new Text("Banana")] },
                            new SelectItem { Value = "blueberry", Children = [new Text("Blueberry")] },
                            new SelectItem { Value = "grapes", Children = [new Text("Grapes")] },
                            new SelectItem { Value = "pineapple", Children = [new Text("Pineapple")] },
                        ],
                    },
                ],
            },
        ],
    };

    static Node SelectGrouped() => new Select
    {
        DefaultValue = "apple",
        Children =
        [
            new SelectTrigger
            {
                Rest = new AttributeDictionary { ["aria-label"] = "Produce" },
                Children = [new SelectValue()],
            },
            new SelectContent
            {
                Children =
                [
                    new SelectGroup
                    {
                        Children =
                        [
                            new SelectLabel { Children = [new Text("Fruits")] },
                            new SelectItem { Value = "apple", Children = [new Text("Apple")] },
                            new SelectItem { Value = "banana", Children = [new Text("Banana")] },
                        ],
                    },
                    new SelectSeparator(),
                    new SelectGroup
                    {
                        Children =
                        [
                            new SelectLabel { Children = [new Text("Vegetables")] },
                            new SelectItem { Value = "carrot", Children = [new Text("Carrot")] },
                            new SelectItem { Value = "potato", Children = [new Text("Potato")] },
                        ],
                    },
                ],
            },
        ],
    };

    static Node SelectSmall() => new Select
    {
        DefaultValue = "banana",
        Children =
        [
            new SelectTrigger
            {
                Size = SelectSize.Sm,
                Rest = new AttributeDictionary { ["aria-label"] = "Fruit" },
                Children = [new SelectValue()],
            },
            new SelectContent
            {
                Children =
                [
                    new SelectItem { Value = "apple", Children = [new Text("Apple")] },
                    new SelectItem { Value = "banana", Children = [new Text("Banana")] },
                    new SelectItem { Value = "cherry", Children = [new Text("Cherry")] },
                ],
            },
        ],
    };

    static Node SelectInvalid() => new Select
    {
        Children =
        [
            new SelectTrigger
            {
                Rest = new AttributeDictionary { ["aria-label"] = "Fruit", ["aria-invalid"] = "true" },
                Children = [new SelectValue { Placeholder = "Select a fruit" }],
            },
            new SelectContent
            {
                Children =
                [
                    new SelectItem { Value = "apple", Children = [new Text("Apple")] },
                    new SelectItem { Value = "banana", Children = [new Text("Banana")] },
                ],
            },
        ],
    };

    static Node SelectDisabled() => new Select
    {
        DefaultValue = "apple",
        Disabled = true,
        Children =
        [
            new SelectTrigger
            {
                Rest = new AttributeDictionary { ["aria-label"] = "Fruit" },
                Children = [new SelectValue()],
            },
            new SelectContent
            {
                Children =
                [
                    new SelectItem { Value = "apple", Children = [new Text("Apple")] },
                    new SelectItem { Value = "banana", Children = [new Text("Banana")] },
                ],
            },
        ],
    };

    // ------------------------------------------------------------------ Slider

    static Node SliderBasic() => DocsUi.Stack(
        Box(new Slider
        {
            DefaultValue = 50,
            Rest = new AttributeDictionary { ["aria-label"] = "Volume" },
        }, 320),
        DocsUi.Row(DocsUi.Muted("0"), DocsUi.Muted("50"), DocsUi.Muted("100")));

    static Node SliderStepped() => DocsUi.Stack(
        Box(new Slider
        {
            Min = 0,
            Max = 100,
            Step = 25,
            DefaultValue = 75,
            FormatValueText = value => value.ToString("0", System.Globalization.CultureInfo.InvariantCulture) + "%",
            Rest = new AttributeDictionary { ["aria-label"] = "Budget" },
        }, 320),
        DocsUi.Note("Step 25 snaps to quarter marks; FormatValueText is exposed to assistive technology."));

    static Node SliderDisabled() => Box(new Slider
    {
        DefaultValue = 40,
        Disabled = true,
        Rest = new AttributeDictionary { ["aria-label"] = "Volume" },
    }, 320);

    /// <summary>Owns the range values so the muted summary label follows both thumbs.</summary>
    sealed partial class SliderRangeDemo : Component
    {
        readonly State<IReadOnlyList<double>> _values = new(new double[] { 25, 75 });

        protected override Node Build()
        {
            IReadOnlyList<double> values = _values.Value;
            return DocsUi.Stack(
                Box(new Slider
                {
                    Min = 0,
                    Max = 100,
                    Step = 5,
                    Values = values,
                    MinStepsBetweenThumbs = 1,
                    ValuesChanged = next => _values.Value = next,
                    Rest = new AttributeDictionary { ["aria-label"] = "Price range" },
                }, 320),
                DocsUi.Muted("Selected range: " + FormatValues(values)),
                DocsUi.Note("Two thumbs stay at least one step apart; the label updates as either thumb moves."));
        }

        static string FormatValues(IReadOnlyList<double> values)
        {
            var parts = new List<string>(values.Count);
            foreach (double value in values)
                parts.Add(value.ToString("0", System.Globalization.CultureInfo.InvariantCulture));
            return string.Join(" - ", parts);
        }
    }

    // ------------------------------------------------------------------ Switch

    static Node SwitchBasic() => DocsUi.Stack(
        SwitchRow("forms-switch-air", "Airplane mode", isChecked: true),
        SwitchRow("forms-switch-sync", "Background sync"));

    static Node SwitchLabel() => new Field
    {
        Orientation = FieldOrientation.Horizontal,
        Children =
        [
            new Switch { Id = "forms-switch-tfa", DefaultChecked = true },
            new FieldContent
            {
                Children =
                [
                    new FieldLabel { For = "forms-switch-tfa", Children = [new Text("Two-factor authentication")] },
                    new FieldDescription { Children = [new Text("Ask for a code from a trusted device.")] },
                ],
            },
        ],
    };

    static Node SwitchSmall() => DocsUi.Stack(
        DocsUi.Row(
            new Switch { Id = "forms-switch-small", Size = SwitchSize.Sm, DefaultChecked = true },
            new Label { For = "forms-switch-small", Children = [new Text("Compact switch")] }));

    static Node SwitchDisabled() => DocsUi.Stack(
        SwitchRow("forms-switch-disabled-a", "Disabled off", disabled: true),
        SwitchRow("forms-switch-disabled-b", "Disabled on", isChecked: true, disabled: true));

    static Div SwitchRow(string id, string text, bool isChecked = false, bool disabled = false) => DocsUi.Row(
        new Switch { Id = id, DefaultChecked = isChecked, Disabled = disabled },
        new Label { For = id, Children = [new Text(text)] });

    // ------------------------------------------------------------------ Textarea

    static Node TextareaBasic() => DocsUi.Field(
        new Label { For = "forms-textarea-basic", Children = [new Text("Message")] },
        new Textarea { Id = "forms-textarea-basic", Placeholder = "Type your message here." });

    static Node TextareaLabel() => DocsUi.Stack(
        new Field
        {
            Children =
            [
                new FieldLabel { For = "forms-textarea-feedback", Children = [new Text("Feedback")] },
                new Textarea { Id = "forms-textarea-feedback", Placeholder = "What could we improve?" },
                new FieldDescription { Children = [new Text("Your feedback helps us prioritize the roadmap.")] },
            ],
        });

    static Node TextareaDisabled() => DocsUi.Field(
        new Label { For = "forms-textarea-disabled", Children = [new Text("Message")] },
        new Textarea
        {
            Id = "forms-textarea-disabled",
            Value = "This textarea is disabled.",
            Disabled = true,
        });

    // ------------------------------------------------------------------ Calendar

    static Node CalendarBasic() => DocsUi.Stack(
        new Calendar { DefaultSelectedDate = DateTime.Today },
        DocsUi.Note("Uncontrolled selection starts at today; clicking a day updates the highlighted cell."));

    static Node CalendarWeekNumbers() => DocsUi.Stack(
        new Calendar
        {
            DefaultSelectedDate = DateTime.Today,
            ShowWeekNumbers = true,
            FirstDayOfWeek = DayOfWeek.Monday,
        },
        DocsUi.Note("ShowWeekNumbers adds a column using the current culture's week rule."));

    static Node CalendarDisabledDates() => DocsUi.Stack(
        new Calendar
        {
            DefaultSelectedDate = DateTime.Today,
            FirstDayOfWeek = DayOfWeek.Monday,
            DisabledDates = day => day.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday,
        },
        DocsUi.Note("DisabledDates marks weekends unselectable; MinDate and MaxDate bound the whole range."));

    static Node CalendarCaptionDropdownExample() => DocsUi.Stack(
        new Calendar
        {
            CaptionLayout = CalendarCaptionLayout.Dropdown,
            DefaultSelectedDate = DateTime.Today,
            FirstDayOfWeek = DayOfWeek.Monday,
            MinDate = new DateTime(DateTime.Today.Year - 5, 1, 1),
            MaxDate = new DateTime(DateTime.Today.Year + 5, 12, 31),
        },
        DocsUi.Note("CaptionLayout.Dropdown replaces the month caption with month and year selects."));

    /// <summary>Controlled range so the muted summary follows the two endpoints and the in-progress preview.</summary>
    sealed partial class CalendarRangeDemo : Component
    {
        readonly State<DateTime?> _start = new(DateTime.Today);
        readonly State<DateTime?> _end = new(DateTime.Today.AddDays(6));

        protected override Node Build()
        {
            DateTime? start = _start.Value;
            DateTime? end = _end.Value;
            return DocsUi.Stack(
                new Calendar
                {
                    SelectionMode = CalendarSelectionMode.Range,
                    RangeStart = start,
                    RangeEnd = end,
                    IsRangeControlled = true,
                    RangeChanged = (nextStart, nextEnd) =>
                    {
                        _start.Value = nextStart;
                        _end.Value = nextEnd;
                    },
                    FirstDayOfWeek = DayOfWeek.Monday,
                },
                DocsUi.Muted(Summary(start, end)),
                DocsUi.Note("Click a day to anchor the range, then a second day to complete it; hovering previews the span."));
        }

        static string Summary(DateTime? start, DateTime? end)
        {
            if (start is not { } first) return "No range selected";
            string label = first.ToString("MMM d, yyyy", System.Globalization.CultureInfo.CurrentCulture);
            if (end is not { } last) return label + " - pick an end date";
            return label + " - " + last.ToString("MMM d, yyyy", System.Globalization.CultureInfo.CurrentCulture);
        }
    }

    // ------------------------------------------------------------------ Date Picker

    static Node DatePickerBasic() => Box(
        new DatePicker { Placeholder = "Pick a date" },
        280);

    static Node DatePickerRange() => DocsUi.Stack(
        Box(
            new DatePicker
            {
                Mode = CalendarSelectionMode.Range,
                RangePlaceholder = "Pick a date range",
                RangeFormat = "MMM d, yyyy",
                NumberOfMonths = 2,
                ShowClear = true,
                ContentLabel = "Choose a date range",
            },
            360),
        DocsUi.Note("Range mode keeps the popover open after the first click; RangeFormat formats both endpoints in the trigger label."));

    static Node DatePickerClear() => Box(
        new DatePicker
        {
            DefaultSelectedDate = DateTime.Today,
            ShowClear = true,
            Placeholder = "Pick a date",
        },
        280);

    static Node DatePickerBirth() => DocsUi.Stack(
        Box(
            new DatePicker
            {
                Placeholder = "Date of birth",
                Format = "MMMM d, yyyy",
                MaxDate = DateTime.Today,
                DefaultDisplayDate = new DateTime(2000, 1, 1),
                ShowClear = true,
            },
            280),
        DocsUi.Note("MaxDate stops future dates; DefaultDisplayDate opens the calendar on a sensible month."));

    static Node DatePickerDisabled() => Box(
        new DatePicker { Placeholder = "Not available", Disabled = true },
        280);

    // ------------------------------------------------------------------ helpers

    static Div Box(Node child, int width) => new()
    {
        Style = "width:" + width.ToString(System.Globalization.CultureInfo.InvariantCulture) + "px",
        Children = [child],
    };
}

/// <summary>
/// Owns a <see cref="FormController"/> so validation state survives the docs shell rebuilding the page.
/// The two examples differ only in <see cref="SignIn"/>.
/// </summary>
sealed partial class FormsAccountForm : Component
{
    public bool SignIn { get; set; }

    readonly FormController _controller = new();

    protected override Node Build()
    {
        var fields = new NodeList();
        if (!SignIn)
        {
            fields.Add(TextField(
                "name",
                "Name",
                "Alex Rivera",
                "Shown on your public profile.",
                new FormValidator[]
                {
                    FormValidators.Required("Enter your name."),
                    FormValidators.MinLength(2),
                },
                "text"));
        }

        fields.Add(TextField(
            "email",
            "Email",
            "you@example.com",
            "We'll never share your email.",
            new FormValidator[]
            {
                FormValidators.Required("Enter your email."),
                FormValidators.Email(),
            },
            "email"));

        fields.Add(TextField(
            "password",
            "Password",
            "Enter your password",
            "Use at least 8 characters.",
            new FormValidator[]
            {
                FormValidators.Required("Enter a password."),
                FormValidators.MinLength(8),
            },
            "password"));

        fields.Add(new Div
        {
            Children =
            [
                DocsUi.Action(
                    SignIn ? "Sign in" : "Create account",
                    _ => _controller.Submit(),
                    ButtonVariant.Default),
            ],
        });

        return new Form
        {
            Controller = _controller,
            Children = [new FieldGroup { Children = fields }],
        };
    }

    static FormField TextField(
        string name,
        string label,
        string placeholder,
        string description,
        IReadOnlyList<FormValidator> validators,
        string inputType) => new()
    {
        Name = name,
        DefaultValue = "",
        Validators = validators,
        Children =
        [
            new FormItem
            {
                Children =
                [
                    new FormLabel { Children = [new Text(label)] },
                    new FormControl
                    {
                        Children = [new ShadcnInput { Placeholder = placeholder, InputType = inputType }],
                    },
                    new FormDescription { Children = [new Text(description)] },
                    new FormMessage(),
                ],
            },
        ],
    };
}
