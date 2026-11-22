using System;
using System.Collections.Generic;
using System.Linq;
using AwAUI.Controls.UnitTests.Integration;
using AwAUI.Platform;
using AwAUI.UI;
using UIText = AwAUI.UI.Text;

namespace AwAUI.Shadcn.Integration.S2;

/// <summary>
/// Forms &amp; inputs family acceptance: the shadcn new-york-v4 default class strings, the toggle-group
/// variant/size/spacing contract (including the connected segmented default), radio indicators, and the
/// DOM/ARIA state that the styling depends on.
/// </summary>
public class FormsInputsTests
{
    static FormsInputsTests() => ShadcnTheme.Register();

    static UIHarness Harness(Func<Node> render) =>
        new(render, appStyleSheets: true, width: 640, height: 480);

    static string? A(Element e, string name) => e.GetAttribute(name);

    static string Classes(Element e) => e.GetAttribute("class") ?? "";

    static List<Element> Slots(UIHarness h, string slot) =>
        h.FindAll(e => A(e, "data-slot") == slot);

    static Element Slot(UIHarness h, string slot) => Slots(h, slot).Single();

    // ------------------------------------------------------------ toggle / toggle group

    [Fact]
    public void Toggle_exposes_variant_and_size_and_uses_the_shadcn_class_string()
    {
        var h = Harness(() => new Toggle
        {
            Id = "t",
            Variant = ToggleVariant.Outline,
            Size = ToggleSize.Sm,
            Children = [new UIText("Bold")],
        });

        var toggle = h.ById("t");
        Assert.Equal("outline", A(toggle, "data-variant"));
        Assert.Equal("sm", A(toggle, "data-size"));
        Assert.Equal("button", A(toggle, "type"));
        Assert.Equal("toggle", A(toggle, "data-slot"));

        string classes = Classes(toggle);
        Assert.Contains("hover:bg-accent", classes);
        Assert.Contains("hover:text-accent-foreground", classes);
        Assert.Contains("focus-visible:ring-[3px]", classes);
        Assert.Contains("data-[state=on]:bg-accent", classes);
        Assert.Contains("aria-invalid:border-destructive", classes);
        Assert.Contains("border-input", classes);
        Assert.Contains("h-8", classes);
        Assert.Contains("min-w-8", classes);
    }

    [Fact]
    public void ToggleGroup_default_spacing_is_connected_and_shares_variant_and_size()
    {
        var h = Harness(() => new ToggleGroup
        {
            Variant = ToggleVariant.Outline,
            Children =
            [
                new ToggleGroupItem { Value = "left", Children = [new UIText("Left")] },
                new ToggleGroupItem { Value = "right", Children = [new UIText("Right")] },
            ],
        });

        var root = Slot(h, "toggle-group");
        Assert.Equal("outline", A(root, "data-variant"));
        Assert.Equal("default", A(root, "data-size"));
        Assert.Equal("0", A(root, "data-spacing"));
        Assert.Equal("horizontal", A(root, "data-orientation"));
        Assert.DoesNotContain("gap-1", Classes(root));

        var items = Slots(h, "toggle-group-item");
        Assert.Equal(2, items.Count);
        Assert.All(items, item =>
        {
            Assert.Equal("outline", A(item, "data-variant"));
            Assert.Equal("default", A(item, "data-size"));
            Assert.Equal("0", A(item, "data-spacing"));
        });

        // Connected segments: only the outer corners stay rounded and the shared inner borders collapse.
        string classes = Classes(items[0]);
        Assert.Contains("data-[spacing=0]:rounded-none", classes);
        Assert.Contains("data-[spacing=0]:first:rounded-l-md", classes);
        Assert.Contains("data-[spacing=0]:last:rounded-r-md", classes);
        Assert.Contains("data-[spacing=0]:data-[variant=outline]:border-l-0", classes);
        Assert.Contains("data-[spacing=0]:data-[variant=outline]:first:border-l", classes);
        Assert.Contains("px-3", classes);
        Assert.Contains("w-auto", classes);
        Assert.Contains("min-w-0", classes);
        Assert.Contains("focus-visible:z-10", classes);
    }

    [Fact]
    public void ToggleGroup_spacing_adds_a_gap_between_items()
    {
        var h = Harness(() => new ToggleGroup
        {
            Variant = ToggleVariant.Outline,
            Spacing = 2,
            Children =
            [
                new ToggleGroupItem { Value = "a", Children = [new UIText("A")] },
                new ToggleGroupItem { Value = "b", Children = [new UIText("B")] },
            ],
        });

        var root = Slot(h, "toggle-group");
        Assert.Equal("2", A(root, "data-spacing"));
        Assert.Contains("gap: 8px", root.InlineStyle ?? "");

        var item = Slots(h, "toggle-group-item")[0];
        Assert.Equal("2", A(item, "data-spacing"));
    }

    [Fact]
    public void ToggleGroup_disabled_group_does_not_toggle_items()
    {
        var h = Harness(() => new ToggleGroup
        {
            Disabled = true,
            Children =
            [
                new ToggleGroupItem { Value = "a", Children = [new UIText("A")] },
            ],
        });

        var item = Slot(h, "toggle-group-item");
        Assert.Equal("false", A(item, "aria-pressed"));
        h.Click(item);
        Assert.Equal("false", A(item, "aria-pressed"));
        Assert.Equal("off", A(item, "data-state"));
    }

    // ------------------------------------------------------------ radio group / checkbox / switch

    [Fact]
    public void RadioGroup_renders_the_indicator_only_on_the_checked_item()
    {
        var h = Harness(() => new RadioGroup
        {
            DefaultValue = "b",
            Children =
            [
                new RadioGroupItem { Id = "ra", Value = "a" },
                new RadioGroupItem { Id = "rb", Value = "b" },
            ],
        });

        var first = h.ById("ra");
        var second = h.ById("rb");
        Assert.Equal(Role.Radio, first.Role);
        Assert.Equal("radio-group-item", A(first, "data-slot"));
        Assert.Contains("aspect-square", Classes(first));
        Assert.Contains("transition-[color,box-shadow]", Classes(first));

        Assert.DoesNotContain(Slots(h, "radio-group-indicator"), e => first.IsInclusiveAncestorOf(e));
        var indicator = Assert.Single(Slots(h, "radio-group-indicator"));
        Assert.True(second.IsInclusiveAncestorOf(indicator));
        Assert.Equal("true", A(indicator, "aria-hidden"));
        Assert.Contains("relative", Classes(indicator));
        Assert.Contains("flex", Classes(indicator));
        Assert.Equal("true", A(second, "aria-checked"));
        Assert.Equal("false", A(first, "aria-checked"));
    }

    [Fact]
    public void RadioGroup_moves_the_indicator_when_the_selection_changes()
    {
        var h = Harness(() => new RadioGroup
        {
            DefaultValue = "a",
            Children =
            [
                new RadioGroupItem { Id = "ra", Value = "a" },
                new RadioGroupItem { Id = "rb", Value = "b" },
            ],
        });

        h.Click(h.ById("rb"));
        Assert.Equal("true", A(h.ById("rb"), "aria-checked"));
        Assert.Equal("false", A(h.ById("ra"), "aria-checked"));
        var indicator = Assert.Single(Slots(h, "radio-group-indicator"));
        Assert.True(h.ById("rb").IsInclusiveAncestorOf(indicator));
    }

    [Fact]
    public void Checkbox_and_switch_use_the_shadcn_v4_class_strings()
    {
        var h = Harness(() => new Div
        {
            Children =
            [
                new Checkbox { Id = "c" },
                new Switch { Id = "s", Size = SwitchSize.Sm },
            ],
        });

        string checkbox = Classes(h.ById("c"));
        Assert.Contains("rounded-[4px]", checkbox);
        Assert.Contains("data-[state=checked]:bg-primary", checkbox);
        Assert.Contains("dark:bg-input/30", checkbox);

        var toggle = h.ById("s");
        Assert.Equal("sm", A(toggle, "data-size"));
        Assert.Contains("transition-all", Classes(toggle));
        Assert.Contains("data-[size=sm]:h-3.5", Classes(toggle));

        var thumb = Assert.Single(Slots(h, "switch-thumb"));
        Assert.Contains("bg-background", Classes(thumb));
        Assert.Contains("dark:data-[state=checked]:bg-primary-foreground", Classes(thumb));
    }

    // ------------------------------------------------------------ input / textarea / slider

    [Fact]
    public void Input_uses_the_shadcn_selection_and_file_classes()
    {
        var h = Harness(() => new Input { Id = "i", Placeholder = "Email" });

        string classes = Classes(h.ById("i"));
        Assert.Contains("selection:bg-primary", classes);
        Assert.Contains("selection:text-primary-foreground", classes);
        Assert.Contains("file:inline-flex", classes);
        Assert.Contains("file:h-7", classes);
        Assert.Contains("file:text-foreground", classes);
        Assert.DoesNotContain("selection:bg-primary/25", classes);
    }

    [Fact]
    public void Textarea_sizes_to_its_content()
    {
        var h = Harness(() => new Textarea { Id = "t" });

        Assert.Contains("field-sizing-content", Classes(h.ById("t")));
        Assert.Contains("min-h-16", Classes(h.ById("t")));
    }

    [Fact]
    public void Slider_thumb_matches_the_shadcn_ring_and_fill()
    {
        var h = Harness(() => new Slider { Id = "s", DefaultValue = 50 });

        var thumb = Assert.Single(Slots(h, "slider-thumb"));
        string classes = Classes(thumb);
        Assert.Contains("bg-white", classes);
        Assert.Contains("hover:ring-4", classes);
        Assert.Contains("ring-ring/50", classes);

        var track = Assert.Single(Slots(h, "slider-track"));
        Assert.Contains("overflow-hidden", Classes(track));
        Assert.Contains("bg-muted", Classes(track));
    }

    // ------------------------------------------------------------ select

    [Fact]
    public void NativeSelect_supports_an_empty_placeholder_option()
    {
        string? reported = null;
        var h = Harness(() => new NativeSelect
        {
            ValueChanged = v => reported = v,
            Children =
            [
                new NativeSelectOption { Value = "", Children = [new UIText("Select status")] },
                new NativeSelectOption { Value = "todo", Children = [new UIText("Todo")] },
            ],
        });

        var trigger = Slot(h, "native-select");
        Assert.Equal("Select status", trigger.TextContent.Trim());

        h.Tab();
        h.Key(PhysicalKey.ArrowDown, "ArrowDown");
        var options = h.FindAll(e => e.Role == Role.Option && e.IsVisible);
        Assert.Equal(2, options.Count);

        h.Click(options.Single(e => e.TextContent.Trim() == "Todo"));
        Assert.Equal("todo", reported);
        Assert.Equal("Todo", trigger.TextContent.Trim());

        // Selecting the empty option clears the selection and restores the placeholder label.
        h.Key(PhysicalKey.ArrowDown, "ArrowDown");
        h.Click(h.FindAll(e => e.Role == Role.Option && e.IsVisible)
            .Single(e => e.TextContent.Trim() == "Select status"));
        Assert.Equal("", reported);
        Assert.Equal("Select status", trigger.TextContent.Trim());
    }

    [Fact]
    public void Field_label_inherits_the_shadcn_label_typography()
    {
        var h = Harness(() => new Field
        {
            Children =
            [
                new FieldLabel { For = "email", Children = [new UIText("Email")] },
                new Input { Id = "email" },
            ],
        });

        string classes = Classes(Slot(h, "field-label"));
        Assert.Contains("text-sm", classes);
        Assert.Contains("font-medium", classes);
        Assert.Contains("items-center", classes);
        Assert.Contains("leading-snug", classes);
        Assert.Contains("peer-disabled:opacity-50", classes);
    }

    [Fact]
    public void DatePicker_marks_the_empty_trigger()
    {
        var h = Harness(() => new DatePicker());

        var empty = Slot(h, "date-picker-trigger");
        Assert.Equal("true", A(empty, "data-empty"));
        Assert.Equal("", A(empty, "data-placeholder"));
        Assert.Equal("Pick a date", empty.TextContent.Trim());
    }

    [Fact]
    public void Select_trigger_and_items_carry_the_shadcn_layout_classes()
    {
        var h = Harness(() => new Select
        {
            DefaultValue = "apple",
            Children =
            [
                new SelectTrigger { Children = [new SelectValue()] },
                new SelectContent
                {
                    Children =
                    [
                        new SelectItem { Value = "apple", Children = [new UIText("Apple")] },
                        new SelectItem { Value = "banana", Children = [new UIText("Banana")] },
                    ],
                },
            ],
        });

        string trigger = Classes(Slot(h, "select-trigger"));
        Assert.Contains("*:data-[slot=select-value]:line-clamp-1", trigger);
        Assert.Contains("*:data-[slot=select-value]:flex", trigger);
        Assert.Contains("*:data-[slot=select-value]:gap-2", trigger);
        Assert.Contains("[&_svg:not([class*='size-'])]:size-4", trigger);
        Assert.Contains("[&_svg:not([class*='text-'])]:text-muted-foreground", trigger);

        var item = Slots(h, "select-item")[0];
        string itemClasses = Classes(item);
        Assert.Contains("[&_svg]:shrink-0", itemClasses);
        Assert.Contains("scroll-my-1", itemClasses);
        Assert.Equal("checked", A(item, "data-state"));
        Assert.Equal("true", A(item, "aria-selected"));
    }

    // ------------------------------------------------------------ range slider

    static Point TrackPoint(UIHarness h, double fraction)
    {
        var bounds = Slot(h, "slider-track").GetBoundingClientRect();
        return new Point(bounds.Left + (float)(bounds.Width * fraction), bounds.Center.Y);
    }

    static double[] ThumbValues(UIHarness h) =>
        Slots(h, "slider-thumb")
            .Select(e => double.Parse(A(e, "aria-valuenow")!, System.Globalization.CultureInfo.InvariantCulture))
            .ToArray();

    [Fact]
    public void Slider_range_renders_one_focusable_thumb_per_value_with_aria()
    {
        var h = Harness(() => new Slider { Id = "s", Min = 0, Max = 100, Step = 1, DefaultValues = [20, 80] });

        var root = Slot(h, "slider");
        Assert.Equal("slider", A(root, "data-slot"));
        Assert.Equal("horizontal", A(root, "data-orientation"));

        var thumbs = Slots(h, "slider-thumb");
        Assert.Equal(2, thumbs.Count);
        Assert.Single(Slots(h, "slider-track"));
        Assert.Single(Slots(h, "slider-range"));

        Assert.All(thumbs, thumb =>
        {
            Assert.Equal(Role.Slider, thumb.Role);
            Assert.Equal("0", A(thumb, "aria-valuemin"));
            Assert.Equal("100", A(thumb, "aria-valuemax"));
            Assert.Equal("horizontal", A(thumb, "aria-orientation"));
            Assert.Equal("0", A(thumb, "tabindex"));
            Assert.Null(A(thumb, "aria-disabled"));
        });
        Assert.Equal("20", A(thumbs[0], "aria-valuenow"));
        Assert.Equal("80", A(thumbs[1], "aria-valuenow"));

        // The range spans from the first thumb to the last one.
        string style = Slot(h, "slider-range").InlineStyle ?? "";
        Assert.Contains("left: 20%", style);
        Assert.Contains("right: 20%", style);
    }

    [Fact]
    public void Slider_single_value_api_still_renders_one_semantic_thumb()
    {
        var h = Harness(() => new Slider
        {
            Id = "s",
            Min = 0,
            Max = 100,
            Step = 10,
            DefaultValue = 50,
            ValueText = "Volume",
        });

        var slider = h.ById("s");
        Assert.Equal(Role.Slider, slider.Role);
        Assert.Equal("50", A(slider, "aria-valuenow"));
        Assert.Equal("Volume", A(slider, "aria-valuetext"));
        Assert.Single(Slots(h, "slider-thumb"));

        h.Tab();
        Assert.True(slider.IsFocused);
        h.Key(PhysicalKey.ArrowRight, "ArrowRight");
        Assert.Equal("60", A(slider, "aria-valuenow"));
    }

    [Fact]
    public void Slider_track_click_moves_the_nearest_thumb()
    {
        var values = new State<double[]>(new[] { 20d, 80d });
        var h = Harness(() => new Slider
        {
            Min = 0,
            Max = 100,
            Step = 1,
            Values = values.Value,
            ValuesChanged = next => values.Value = next.ToArray(),
        });

        h.ClickAt(TrackPoint(h, 0.30));
        Assert.Equal(new[] { 30d, 80d }, values.Value);

        h.ClickAt(TrackPoint(h, 0.90));
        Assert.Equal(new[] { 30d, 90d }, values.Value);
    }

    [Fact]
    public void Slider_drag_clamps_thumbs_and_they_do_not_cross()
    {
        var h = Harness(() => new Slider { Id = "s", Min = 0, Max = 100, Step = 1, DefaultValues = [20, 80] });

        h.PointerDown(TrackPoint(h, 0.20));
        h.PointerMove(TrackPoint(h, 0.60));
        h.PointerMove(TrackPoint(h, 0.95));
        h.PointerUp(TrackPoint(h, 0.95));

        Assert.Equal(new[] { 80d, 80d }, ThumbValues(h));
    }

    [Fact]
    public void Slider_min_steps_between_thumbs_keeps_neighbours_apart()
    {
        var h = Harness(() => new Slider
        {
            Min = 0,
            Max = 100,
            Step = 1,
            DefaultValues = [20, 80],
            MinStepsBetweenThumbs = 10,
        });

        h.PointerDown(TrackPoint(h, 0.20));
        h.PointerMove(TrackPoint(h, 0.95));
        h.PointerUp(TrackPoint(h, 0.95));

        Assert.Equal(new[] { 70d, 80d }, ThumbValues(h));
    }

    [Fact]
    public void Slider_keyboard_moves_only_the_focused_thumb()
    {
        var h = Harness(() => new Slider { Min = 0, Max = 100, Step = 1, DefaultValues = [20, 80] });

        h.Tab();
        Assert.Same(Slots(h, "slider-thumb")[0], h.Focused);

        h.Key(PhysicalKey.ArrowRight, "ArrowRight");
        Assert.Equal(new[] { 21d, 80d }, ThumbValues(h));
        Assert.Same(Slots(h, "slider-thumb")[0], h.Focused);

        h.Key(PhysicalKey.PageUp, "PageUp");
        Assert.Equal(new[] { 31d, 80d }, ThumbValues(h));

        // Tab moves to the next thumb, and Shift plus an arrow moves ten steps on it only.
        h.Tab();
        Assert.Same(Slots(h, "slider-thumb")[1], h.Focused);
        h.Key(PhysicalKey.ArrowLeft, "ArrowLeft", KeyModifiers.Shift);
        Assert.Equal(new[] { 31d, 70d }, ThumbValues(h));
        Assert.Same(Slots(h, "slider-thumb")[1], h.Focused);

        h.Tab(shift: true);
        Assert.Same(Slots(h, "slider-thumb")[0], h.Focused);

        h.Key(PhysicalKey.Home, "Home");
        Assert.Equal(new[] { 0d, 70d }, ThumbValues(h));

        // End is the maximum, but the thumb cannot cross its neighbour.
        h.Key(PhysicalKey.End, "End");
        Assert.Equal(new[] { 70d, 70d }, ThumbValues(h));
    }

    [Fact]
    public void Slider_controlled_values_round_trip_through_callbacks()
    {
        var values = new State<double[]>(new[] { 25d, 75d });
        int aliasCalls = 0;
        var h = Harness(() => new Slider
        {
            Min = 0,
            Max = 100,
            Step = 1,
            Values = values.Value,
            ValuesChanged = next => values.Value = next.ToArray(),
            OnValuesChanged = _ => aliasCalls++,
        });

        h.Tab();
        h.Key(PhysicalKey.ArrowRight, "ArrowRight");

        Assert.Equal(new[] { 26d, 75d }, values.Value);
        Assert.Equal(1, aliasCalls);
        Assert.Equal(new[] { 26d, 75d }, ThumbValues(h));
    }

    [Fact]
    public void Slider_uncontrolled_default_values_move_independently()
    {
        var h = Harness(() => new Slider { Min = 0, Max = 100, Step = 1, DefaultValues = [10, 90] });

        h.Tab();
        h.Key(PhysicalKey.ArrowRight, "ArrowRight");

        Assert.Equal(new[] { 11d, 90d }, ThumbValues(h));
    }

    [Fact]
    public void Slider_disabled_range_ignores_pointer_and_keyboard_input()
    {
        var h = Harness(() => new Slider
        {
            Min = 0,
            Max = 100,
            Step = 1,
            DefaultValues = [20, 80],
            Disabled = true,
        });

        var root = Slot(h, "slider");
        Assert.Equal("", A(root, "data-disabled"));
        Assert.All(Slots(h, "slider-thumb"), thumb =>
        {
            Assert.Equal("true", A(thumb, "aria-disabled"));
            Assert.Equal("-1", A(thumb, "tabindex"));
        });

        h.ClickAt(TrackPoint(h, 0.50));
        h.PointerDown(TrackPoint(h, 0.20));
        h.PointerMove(TrackPoint(h, 0.90));
        h.PointerUp(TrackPoint(h, 0.90));
        h.Key(PhysicalKey.ArrowRight, "ArrowRight");

        Assert.Equal(new[] { 20d, 80d }, ThumbValues(h));
    }
}
