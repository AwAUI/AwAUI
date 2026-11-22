using System;
using System.Collections.Generic;
using System.Linq;
using AwAUI.Controls.UnitTests.Integration;
using AwAUI.Platform;
using AwAUI.UI;
using Direction = AwAUI.Layout.Direction;
using UIText = AwAUI.UI.Text;

namespace AwAUI.Shadcn.Integration.S2;

/// <summary>
/// S2 NativeSelect 验收：wrapper / trigger / icon 的插槽、选项插槽、非受控与受控选择、禁用、optgroup
/// 标签，以及 RTL 下的触发器和图标。
/// </summary>
public class NativeSelectTests
{
    static NativeSelectTests() => ShadcnTheme.Register();

    static UIHarness Harness(Func<Node> render) =>
        new(render, appStyleSheets: true, width: 640, height: 480);

    static string? A(Element e, string name) => e.GetAttribute(name);

    static List<Element> Slots(UIHarness h, string slot) =>
        h.FindAll(e => A(e, "data-slot") == slot);

    static Element SlotOrThrow(UIHarness h, string slot) =>
        Slots(h, slot).FirstOrDefault()
        ?? throw new Xunit.Sdk.XunitException($"no element with data-slot=\"{slot}\"");

    static List<Element> VisibleOptions(UIHarness h) =>
        h.FindAll(e => e.Role == Role.Option && e.IsVisible).ToList();

    static NativeSelectOption Opt(string value, string label, bool disabled = false) => new()
    {
        Value = value,
        Disabled = disabled,
        Children = [new UIText(label)],
    };

    static NativeSelect TwoOptions(string? defaultValue = null, Action<string>? valueChanged = null) => new()
    {
        DefaultValue = defaultValue ?? "",
        ValueChanged = valueChanged,
        Children =
        [
            new NativeSelectOption { Value = "projects", Children = [new UIText("Projects")] },
            new NativeSelectOption { Value = "activity", Children = [new UIText("Activity")] },
        ],
    };

    [Fact]
    public void NativeSelect_renders_wrapper_trigger_and_icon_slots()
    {
        var h = Harness(() => TwoOptions());

        var wrapper = SlotOrThrow(h, "native-select-wrapper");
        Assert.True(wrapper.HasClass("group/native-select"));

        var trigger = SlotOrThrow(h, "native-select");
        Assert.Equal(Role.ComboBox, trigger.Role);
        Assert.Equal("button", A(trigger, "type"));
        Assert.Equal("listbox", A(trigger, "aria-haspopup"));
        Assert.Equal("false", A(trigger, "aria-expanded"));
        Assert.Equal("default", A(trigger, "data-size"));

        var icon = SlotOrThrow(h, "native-select-icon");
        Assert.Equal("true", A(icon, "aria-hidden"));

        Assert.Equal(2, Slots(h, "native-select-option").Count);
    }

    [Fact]
    public void NativeSelect_size_sm_sets_data_size()
    {
        var h = Harness(() => new NativeSelect
        {
            Size = NativeSelectSize.Sm,
            Children = [new NativeSelectOption { Value = "one", Children = [new UIText("One")] }],
        });

        Assert.Equal("sm", A(SlotOrThrow(h, "native-select"), "data-size"));
    }

    [Fact]
    public void NativeSelect_uncontrolled_selection_updates_value_and_display()
    {
        string? value = null;
        var h = Harness(() => TwoOptions("projects", v => value = v));

        var trigger = SlotOrThrow(h, "native-select");
        Assert.Equal("Projects", trigger.TextContent.Trim());

        h.Tab();
        Assert.True(trigger.IsFocused);
        h.Key(PhysicalKey.ArrowDown, "ArrowDown");
        Assert.Equal("true", A(trigger, "aria-expanded"));

        var options = VisibleOptions(h);
        Assert.Equal(2, options.Count);
        Assert.Equal("checked", A(options.Single(e => e.TextContent.Trim() == "Projects"), "data-state"));

        h.Click(options.Single(e => e.TextContent.Trim() == "Activity"));

        Assert.Equal("activity", value);
        Assert.Equal("false", A(trigger, "aria-expanded"));
        Assert.Empty(VisibleOptions(h));
        Assert.Equal("Activity", trigger.TextContent.Trim());
    }

    [Fact]
    public void NativeSelect_controlled_value_reports_but_keeps_the_controlled_selection()
    {
        string? reported = null;
        var h = Harness(() => new NativeSelect
        {
            Value = "projects",
            ValueChanged = v => reported = v,
            Children =
            [
                new NativeSelectOption { Value = "projects", Children = [new UIText("Projects")] },
                new NativeSelectOption { Value = "activity", Children = [new UIText("Activity")] },
            ],
        });

        var trigger = SlotOrThrow(h, "native-select");
        h.Tab();
        h.Key(PhysicalKey.ArrowDown, "ArrowDown");

        h.Click(VisibleOptions(h).Single(e => e.TextContent.Trim() == "Activity"));

        Assert.Equal("activity", reported);
        Assert.Equal("Projects", trigger.TextContent.Trim());
    }

    [Fact]
    public void NativeSelect_disabled_prevents_opening()
    {
        var h = Harness(() => new NativeSelect
        {
            Disabled = true,
            Children = [new NativeSelectOption { Value = "one", Children = [new UIText("One")] }],
        });

        var trigger = SlotOrThrow(h, "native-select");
        Assert.True(trigger.IsDisabled);
        Assert.Equal("true", A(trigger, "aria-disabled"));

        h.Click(trigger);
        h.Key(PhysicalKey.ArrowDown, "ArrowDown");

        Assert.Equal("false", A(trigger, "aria-expanded"));
        Assert.Empty(VisibleOptions(h));
    }

    [Fact]
    public void NativeSelect_optgroup_renders_its_label_and_options()
    {
        var h = Harness(() => new NativeSelect
        {
            DefaultValue = "apple",
            Children =
            [
                new NativeSelectOptGroup
                {
                    Label = "Fruits",
                    Children =
                    [
                        new NativeSelectOption { Value = "apple", Children = [new UIText("Apple")] },
                        new NativeSelectOption { Value = "banana", Children = [new UIText("Banana")] },
                    ],
                },
            ],
        });

        Assert.NotNull(SlotOrThrow(h, "native-select-optgroup"));
        Assert.Equal("Fruits", SlotOrThrow(h, "native-select-label").TextContent.Trim());

        var trigger = SlotOrThrow(h, "native-select");
        h.Tab();
        h.Key(PhysicalKey.ArrowDown, "ArrowDown");
        Assert.Equal(2, VisibleOptions(h).Count);
    }

    [Fact]
    public void NativeSelect_disabled_optgroup_disables_its_options()
    {
        var h = Harness(() => new NativeSelect
        {
            Children =
            [
                new NativeSelectOptGroup
                {
                    Label = "Unavailable",
                    Disabled = true,
                    Children = [new NativeSelectOption { Value = "one", Children = [new UIText("One")] }],
                },
            ],
        });

        var trigger = SlotOrThrow(h, "native-select");
        h.Tab();
        h.Key(PhysicalKey.ArrowDown, "ArrowDown");
        var option = VisibleOptions(h).Single();
        Assert.True(option.IsDisabled);
        Assert.Equal("true", A(option, "aria-disabled"));
    }

    [Fact]
    public void NativeSelect_renders_inside_rtl_direction()
    {
        var h = Harness(() => new DirectionProvider
        {
            Dir = Direction.Rtl,
            Children = [TwoOptions("projects")],
        });

        Assert.Equal("rtl", A(SlotOrThrow(h, "direction-provider"), "dir"));
        var trigger = SlotOrThrow(h, "native-select");
        Assert.Equal(AwAUI.Css.Values.CssDirection.Rtl, trigger.ComputedStyle?.Direction);
        Assert.NotNull(SlotOrThrow(h, "native-select-icon"));
    }

    [Fact]
    public void NativeSelect_ignores_non_option_children()
    {
        var h = Harness(() => new NativeSelect
        {
            Children =
            [
                new Div { Children = [new UIText("Not an option")] },
                new NativeSelectOption { Value = "one", Children = [new UIText("One")] },
            ],
        });

        Assert.Single(Slots(h, "native-select-option"));
    }

    /// <summary>Dispatch a key at an explicit timestamp so the typeahead reset window can be exercised.</summary>
    static void KeyAt(UIHarness h, TimeSpan timestamp, PhysicalKey key, string logical)
    {
        h.Root.Input.DispatchKey(new RawKeyEvent(RawKeyEventType.Down, key, KeyModifiers.None, false, 0, timestamp, logical));
        h.Root.Input.DispatchKey(new RawKeyEvent(RawKeyEventType.Up, key, KeyModifiers.None, false, 0, timestamp, logical));
        h.Frame();
    }

    [Fact]
    public void NativeSelect_typeahead_selects_first_match_and_cycles_repeated_letter()
    {
        string? value = null;
        var h = Harness(() => new NativeSelect
        {
            ValueChanged = v => value = v,
            Children = [Opt("apple", "Apple"), Opt("apricot", "Apricot"), Opt("banana", "Banana")],
        });

        var trigger = SlotOrThrow(h, "native-select");
        h.Tab();
        Assert.True(trigger.IsFocused);

        // Case-insensitive: "a" matches "Apple" first.
        h.Key(PhysicalKey.KeyA, "a");
        Assert.Equal("apple", value);
        Assert.Equal("Apple", trigger.TextContent.Trim());

        // Repeated same letter cycles through the other matches.
        h.Key(PhysicalKey.KeyA, "a");
        Assert.Equal("apricot", value);

        // ...and wraps back around.
        h.Key(PhysicalKey.KeyA, "a");
        Assert.Equal("apple", value);
    }

    [Fact]
    public void NativeSelect_typeahead_buffer_resets_after_about_500ms()
    {
        string? value = null;
        var h = Harness(() => new NativeSelect
        {
            ValueChanged = v => value = v,
            Children = [Opt("apple", "Apple"), Opt("banana", "Banana")],
        });

        h.Tab();

        KeyAt(h, TimeSpan.FromMilliseconds(1000), PhysicalKey.KeyA, "a");
        Assert.Equal("apple", value);

        // 600 ms later the buffer has reset, so "b" starts a fresh search instead of extending "a" to "ab".
        KeyAt(h, TimeSpan.FromMilliseconds(1600), PhysicalKey.KeyB, "b");
        Assert.Equal("banana", value);
    }

    [Fact]
    public void NativeSelect_home_and_end_select_first_and_last_enabled_option()
    {
        string? value = null;
        var h = Harness(() => new NativeSelect
        {
            ValueChanged = v => value = v,
            Children =
            [
                Opt("one", "One", disabled: true),
                Opt("two", "Two"),
                Opt("three", "Three", disabled: true),
                Opt("four", "Four"),
            ],
        });

        var trigger = SlotOrThrow(h, "native-select");
        h.Tab();
        Assert.True(trigger.IsFocused);

        h.Key(PhysicalKey.End, "End");
        Assert.Equal("four", value);
        Assert.Equal("Four", trigger.TextContent.Trim());

        h.Key(PhysicalKey.Home, "Home");
        Assert.Equal("two", value);
        Assert.Equal("Two", trigger.TextContent.Trim());
    }

    [Fact]
    public void NativeSelect_alt_arrow_down_opens_the_popup()
    {
        var h = Harness(() => TwoOptions());

        var trigger = SlotOrThrow(h, "native-select");
        h.Tab();
        Assert.True(trigger.IsFocused);

        h.Key(PhysicalKey.ArrowDown, "ArrowDown", KeyModifiers.Alt);

        Assert.Equal("true", A(trigger, "aria-expanded"));
        Assert.Equal(2, VisibleOptions(h).Count);

        h.Escape();
        Assert.Equal("false", A(trigger, "aria-expanded"));
        Assert.Empty(VisibleOptions(h));
    }

    [Fact]
    public void NativeSelect_opens_and_selects_with_the_keyboard_only()
    {
        string? value = null;
        var h = Harness(() => TwoOptions(valueChanged: v => value = v));

        var trigger = SlotOrThrow(h, "native-select");
        h.Tab();
        Assert.True(trigger.IsFocused);

        // Enter opens the popup without any pointer input.
        h.Key(PhysicalKey.Enter, "Enter");
        Assert.Equal("true", A(trigger, "aria-expanded"));
        Assert.Equal(2, VisibleOptions(h).Count);

        // Enter on the focused option selects it.
        h.Key(PhysicalKey.Enter, "Enter");
        Assert.Equal("projects", value);
        Assert.Equal("false", A(trigger, "aria-expanded"));
        Assert.Equal("Projects", trigger.TextContent.Trim());
    }

    [Fact]
    public void NativeSelect_space_selects_the_focused_option()
    {
        string? value = null;
        var h = Harness(() => TwoOptions(valueChanged: v => value = v));

        var trigger = SlotOrThrow(h, "native-select");
        h.Tab();
        h.Key(PhysicalKey.Space, " ");
        Assert.Equal("true", A(trigger, "aria-expanded"));

        h.Key(PhysicalKey.Space, " ");
        Assert.Equal("projects", value);
        Assert.Equal("false", A(trigger, "aria-expanded"));
    }

    [Fact]
    public void NativeSelect_optgroup_label_is_not_selectable_and_has_group_roles()
    {
        var h = Harness(() => new NativeSelect
        {
            DefaultValue = "apple",
            Children =
            [
                new NativeSelectOptGroup
                {
                    Label = "Fruits",
                    Children = [Opt("apple", "Apple"), Opt("banana", "Banana")],
                },
            ],
        });

        var trigger = SlotOrThrow(h, "native-select");
        h.Tab();
        h.Key(PhysicalKey.ArrowDown, "ArrowDown");

        var label = h.FindAll(e => e.Owner is SelectLabel && !e.IsTextNode).Single();
        Assert.Equal(Role.Presentation, label.Role);

        var group = Slots(h, "select-group").Single();
        Assert.Equal(Role.Group, group.Role);
        Assert.Equal(label.Id, A(group, "aria-labelledby"));

        var options = VisibleOptions(h);
        Assert.Equal(new[] { "Apple", "Banana" }, options.Select(e => e.TextContent.Trim()));
        Assert.DoesNotContain(options, e => e.TextContent.Trim() == "Fruits");

        // Arrow navigation moves between options only; the group label is never focused.
        Assert.Equal("Apple", h.Focused?.TextContent.Trim());
        h.Key(PhysicalKey.ArrowDown, "ArrowDown");
        Assert.Equal("Banana", h.Focused?.TextContent.Trim());
    }

    [Fact]
    public void NativeSelect_disabled_option_is_skipped_and_exposed_as_aria_disabled()
    {
        string? value = null;
        var h = Harness(() => new NativeSelect
        {
            ValueChanged = v => value = v,
            Children = [Opt("apple", "Apple"), Opt("banana", "Banana", disabled: true), Opt("cherry", "Cherry")],
        });

        var trigger = SlotOrThrow(h, "native-select");
        h.Tab();
        h.Key(PhysicalKey.ArrowDown, "ArrowDown");

        var banana = VisibleOptions(h).Single(e => e.TextContent.Trim() == "Banana");
        Assert.True(banana.IsDisabled);
        Assert.Equal("true", A(banana, "aria-disabled"));

        Assert.Equal("Apple", h.Focused?.TextContent.Trim());
        h.Key(PhysicalKey.ArrowDown, "ArrowDown");
        Assert.Equal("Cherry", h.Focused?.TextContent.Trim());

        // Home/End skip the disabled option too.
        h.Key(PhysicalKey.Home, "Home");
        Assert.Equal("Apple", h.Focused?.TextContent.Trim());
        h.Key(PhysicalKey.End, "End");
        Assert.Equal("Cherry", h.Focused?.TextContent.Trim());

        h.Key(PhysicalKey.Enter, "Enter");
        Assert.Equal("cherry", value);
    }

    [Fact]
    public void NativeSelect_selected_option_is_exposed_as_aria_selected()
    {
        var h = Harness(() => TwoOptions("projects"));

        var trigger = SlotOrThrow(h, "native-select");
        h.Tab();
        h.Key(PhysicalKey.ArrowDown, "ArrowDown");

        var options = VisibleOptions(h);
        Assert.Equal("true", A(options.Single(e => e.TextContent.Trim() == "Projects"), "aria-selected"));
        Assert.Equal("false", A(options.Single(e => e.TextContent.Trim() == "Activity"), "aria-selected"));
    }

    [Fact]
    public void NativeSelect_exposes_required_name_and_invalid_attributes()
    {
        var h = Harness(() => new NativeSelect
        {
            Required = true,
            Name = "fruit",
            Invalid = true,
            Children = [Opt("apple", "Apple")],
        });

        var trigger = SlotOrThrow(h, "native-select");
        Assert.Equal("true", A(trigger, "aria-required"));
        Assert.Equal("fruit", A(trigger, "name"));
        Assert.Equal("fruit", A(trigger, "data-name"));
        Assert.Equal("true", A(trigger, "aria-invalid"));
        Assert.Equal("", A(trigger, "data-invalid"));
    }

    [Fact]
    public void NativeSelect_omits_aria_invalid_when_valid()
    {
        var h = Harness(() => TwoOptions());

        var trigger = SlotOrThrow(h, "native-select");
        Assert.Null(A(trigger, "aria-invalid"));
        Assert.Equal("false", A(trigger, "aria-required"));
    }
}
