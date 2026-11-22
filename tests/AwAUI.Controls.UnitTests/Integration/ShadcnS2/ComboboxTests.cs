using AwAUI.Controls.UnitTests.Integration;
using AwAUI.Platform;
using AwAUI.UI;
using UIText = AwAUI.UI.Text;

namespace AwAUI.Shadcn.Integration.S2;

/// <summary>Combobox 的打开、过滤、键盘选择、受控值与 ARIA 关系。</summary>
public class ComboboxTests
{
    static ComboboxTests() => ShadcnTheme.Register();

    static UIHarness Harness(Func<Node> render) =>
        new(render, appStyleSheets: true, width: 640, height: 480);

    static string? A(Element e, string name) => e.GetAttribute(name);

    static Element Input(UIHarness h) =>
        h.FindAll(e => A(e, "data-slot") == "combobox-input").Single();

    static List<Element> Options(UIHarness h) => h.FindAll(e => e.Role == Role.Option && e.IsVisible);

    [Fact]
    public void Combobox_opens_on_focus_filters_and_commits_with_enter()
    {
        string? changed = null;
        int selected = 0;
        var h = Harness(() => new Combobox
        {
            Placeholder = "Pick a fruit",
            ValueChanged = v => changed = v,
            Children =
            [
                new CommandItem
                {
                    Value = "apple",
                    Children = [new UIText("Apple")],
                    OnSelect = _ => selected++,
                },
                new CommandItem { Value = "banana", Children = [new UIText("Banana")] },
            ],
        });

        var input = Input(h);
        Assert.Equal(Role.ComboBox, input.Role);
        Assert.Equal("listbox", A(input, "aria-haspopup"));
        Assert.Equal("false", A(input, "aria-expanded"));

        h.Tab();
        Assert.True(input.IsFocused);
        Assert.Equal("true", A(input, "aria-expanded"));

        var list = h.FindAll(e => e.Role == Role.ListBox && e.IsVisible).Single();
        Assert.Equal(list.Id, A(input, "aria-controls"));
        Assert.Equal(2, Options(h).Count);

        h.TypeText("app");
        var options = Options(h);
        Assert.Single(options);
        Assert.Contains("Apple", options[0].TextContent);
        Assert.True(input.IsFocused);

        h.Key(PhysicalKey.Enter, "Enter");
        Assert.Equal(1, selected);
        Assert.Equal("apple", changed);
        Assert.Empty(h.FindAll(e => e.Role == Role.ListBox && e.IsVisible));
        Assert.Equal("false", A(input, "aria-expanded"));
        Assert.Equal("Apple", Assert.IsType<InputElement>(input).Value);
    }

    [Fact]
    public void Combobox_escape_closes_and_keyboard_moves_the_active_option()
    {
        var h = Harness(() => new Combobox
        {
            Children =
            [
                new CommandItem { Value = "one", Children = [new UIText("One")] },
                new CommandItem { Value = "two", Children = [new UIText("Two")] },
                new CommandItem { Value = "three", Children = [new UIText("Three")] },
            ],
        });

        var input = Input(h);
        h.Tab();
        Assert.Equal("true", A(input, "aria-expanded"));

        var options = Options(h);
        Assert.Equal("true", A(options[0], "aria-selected"));

        h.Key(PhysicalKey.ArrowDown, "ArrowDown");
        Assert.Equal("true", A(options[1], "aria-selected"));
        Assert.True(input.IsFocused);

        h.Key(PhysicalKey.ArrowUp, "ArrowUp");
        Assert.Equal("true", A(options[0], "aria-selected"));

        h.Escape();
        Assert.Equal("false", A(input, "aria-expanded"));
        Assert.Empty(h.FindAll(e => e.Role == Role.ListBox && e.IsVisible));
    }

    [Fact]
    public void Combobox_controlled_value_displays_its_label_and_reports_changes()
    {
        string? reported = null;
        var h = Harness(() => new Combobox
        {
            Value = "banana",
            ValueChanged = v => reported = v,
            Children =
            [
                new CommandItem { Value = "apple", Children = [new UIText("Apple")] },
                new CommandItem { Value = "banana", Children = [new UIText("Banana")] },
            ],
        });

        var input = Input(h);
        Assert.Equal("Banana", Assert.IsType<InputElement>(input).Value);

        h.Tab();
        h.TypeText("app");
        h.Key(PhysicalKey.Enter, "Enter");
        Assert.Equal("apple", reported);
        // Controlled: the displayed value still comes from the Value property.
        Assert.Equal("Banana", Assert.IsType<InputElement>(input).Value);
    }

    [Fact]
    public void Combobox_click_selects_an_option()
    {
        string? changed = null;
        var h = Harness(() => new Combobox
        {
            ValueChanged = v => changed = v,
            Children =
            [
                new CommandItem { Value = "apple", Children = [new UIText("Apple")] },
                new CommandItem { Value = "banana", Children = [new UIText("Banana")] },
            ],
        });

        var input = Input(h);
        h.Tab();
        var banana = Options(h)[1];
        h.Click(banana);
        Assert.Equal("banana", changed);
        Assert.Equal("Banana", Assert.IsType<InputElement>(input).Value);
        Assert.Equal("false", A(input, "aria-expanded"));
    }

    [Fact]
    public void Combobox_groups_render_labels_and_collapse_when_filtered()
    {
        static Combobox Build() => new()
        {
            Children =
            [
                new ComboboxGroup
                {
                    Label = "Fruits",
                    Children =
                    [
                        new ComboboxItem { Value = "apple", Children = [new UIText("Apple")] },
                        new ComboboxItem { Value = "banana", Children = [new UIText("Banana")] },
                    ],
                },
                new ComboboxSeparator(),
                new ComboboxGroup
                {
                    Label = "Vegetables",
                    Children =
                    [
                        new ComboboxItem { Value = "carrot", Children = [new UIText("Carrot")] },
                    ],
                },
            ],
        };

        var h = Harness(Build);
        var input = Input(h);
        h.Tab();

        Assert.Equal(2, h.FindAll(e => e.Role == Role.Group && e.IsVisible).Count);
        Assert.Equal(2, h.FindAll(e => A(e, "data-slot") == "combobox-label" && e.IsVisible).Count);
        Assert.Single(h.FindAll(e => e.Role == Role.Separator && e.IsVisible));

        h.TypeText("app");
        Assert.Single(Options(h));
        var groups = h.FindAll(e => e.Role == Role.Group && e.IsVisible);
        Assert.Single(groups);
        var label = h.FindAll(e => A(e, "data-slot") == "combobox-label" && e.IsVisible).Single();
        Assert.Equal("Fruits", label.TextContent.Trim());
        Assert.Equal(label.Id, A(groups[0], "aria-labelledby"));
        // The separator between the two groups has nothing visible after it any more.
        Assert.Empty(h.FindAll(e => e.Role == Role.Separator && e.IsVisible));

        var h2 = Harness(Build);
        var input2 = Input(h2);
        h2.Tab();
        h2.TypeText("car");
        Assert.Single(Options(h2));
        Assert.Equal("Vegetables", h2.FindAll(e => A(e, "data-slot") == "combobox-label" && e.IsVisible).Single().TextContent.Trim());
        Assert.True(input2.IsFocused);
    }

    [Fact]
    public void Combobox_empty_state_shows_only_when_nothing_matches()
    {
        var h = Harness(() => new Combobox
        {
            Children =
            [
                new ComboboxGroup
                {
                    Label = "Fruits",
                    Children =
                    [
                        new ComboboxItem { Value = "apple", Children = [new UIText("Apple")] },
                    ],
                },
                new ComboboxEmpty { Text = "No results" },
            ],
        });

        var input = Input(h);
        h.Tab();
        Assert.Empty(h.FindAll(e => A(e, "data-slot") == "combobox-empty" && e.IsVisible));

        h.TypeText("zzz");
        Assert.Empty(Options(h));
        Assert.Empty(h.FindAll(e => e.Role == Role.Group && e.IsVisible));
        var empty = h.FindAll(e => A(e, "data-slot") == "combobox-empty" && e.IsVisible).Single();
        Assert.Equal("No results", empty.TextContent.Trim());
        Assert.Equal(Role.Presentation, empty.Role);
    }

    [Fact]
    public void Combobox_filters_by_keywords_case_insensitively()
    {
        static Combobox Build() => new()
        {
            Children =
            [
                new ComboboxItem { Value = "lemon", Keywords = "citrus sour", Children = [new UIText("Lemon")] },
                new ComboboxItem { Value = "carrot", Keywords = "root vegetable", Children = [new UIText("Carrot")] },
            ],
        };

        var h = Harness(Build);
        var input = Input(h);
        h.Tab();

        h.TypeText("CITR");
        var options = Options(h);
        Assert.Single(options);
        Assert.Equal("Lemon", options[0].TextContent.Trim());
        Assert.True(input.IsFocused);

        var h2 = Harness(Build);
        Input(h2);
        h2.Tab();
        h2.TypeText("veg");
        Assert.Equal("Carrot", Options(h2).Single().TextContent.Trim());
    }

    [Fact]
    public void Combobox_home_end_and_loop_wrap_the_active_option()
    {
        var h = Harness(() => new Combobox
        {
            Children =
            [
                new ComboboxItem { Value = "one", Children = [new UIText("One")] },
                new ComboboxItem { Value = "two", Children = [new UIText("Two")] },
                new ComboboxItem { Value = "three", Children = [new UIText("Three")] },
            ],
        });

        var input = Input(h);
        h.Tab();
        var options = Options(h);
        Assert.Equal("true", A(options[0], "aria-selected"));

        h.Key(PhysicalKey.End, "End");
        Assert.Equal("true", A(options[2], "aria-selected"));
        Assert.Equal(options[2].Id, A(input, "aria-activedescendant"));

        h.Key(PhysicalKey.ArrowDown, "ArrowDown");
        Assert.Equal("true", A(options[0], "aria-selected"));

        h.Key(PhysicalKey.Home, "Home");
        Assert.Equal("true", A(options[0], "aria-selected"));

        h.Key(PhysicalKey.ArrowUp, "ArrowUp");
        Assert.Equal("true", A(options[2], "aria-selected"));
    }

    [Fact]
    public void Combobox_without_loop_does_not_wrap()
    {
        var h = Harness(() => new Combobox
        {
            Loop = false,
            Children =
            [
                new ComboboxItem { Value = "one", Children = [new UIText("One")] },
                new ComboboxItem { Value = "two", Children = [new UIText("Two")] },
            ],
        });

        Input(h);
        h.Tab();
        var options = Options(h);

        h.Key(PhysicalKey.ArrowUp, "ArrowUp");
        Assert.Equal("true", A(options[0], "aria-selected"));

        h.Key(PhysicalKey.End, "End");
        h.Key(PhysicalKey.ArrowDown, "ArrowDown");
        Assert.Equal("true", A(options[1], "aria-selected"));
    }

    [Fact]
    public void Combobox_controlled_value_round_trips_and_open_is_controlled()
    {
        string? value = "banana";
        bool? open = false;
        var h = Harness(() => new Combobox
        {
            Value = value,
            Open = open,
            ValueChanged = v => value = v,
            OpenChanged = next => open = next,
            Children =
            [
                new ComboboxItem { Value = "apple", Children = [new UIText("Apple")] },
                new ComboboxItem { Value = "banana", Children = [new UIText("Banana")] },
            ],
        });

        var input = Input(h);
        Assert.Equal("Banana", Assert.IsType<InputElement>(input).Value);

        h.Tab();
        // Controlled open: the request is reported but the property still keeps the list closed.
        Assert.True(open);
        Assert.Equal("false", A(input, "aria-expanded"));
    }

    [Fact]
    public void Combobox_disabled_items_are_skipped_by_keyboard_navigation()
    {
        int selected = 0;
        var h = Harness(() => new Combobox
        {
            Children =
            [
                new ComboboxItem { Value = "one", Children = [new UIText("One")], OnSelect = _ => selected = 1 },
                new ComboboxItem { Value = "two", Disabled = true, Children = [new UIText("Two")], OnSelect = _ => selected = 2 },
                new ComboboxItem { Value = "three", Children = [new UIText("Three")], OnSelect = _ => selected = 3 },
            ],
        });

        var input = Input(h);
        h.Tab();
        var options = Options(h);

        h.Key(PhysicalKey.ArrowDown, "ArrowDown");
        Assert.Equal("true", A(options[2], "aria-selected"));
        Assert.Equal("true", A(options[1], "aria-disabled"));

        h.Key(PhysicalKey.ArrowUp, "ArrowUp");
        Assert.Equal("true", A(options[0], "aria-selected"));

        h.Key(PhysicalKey.Enter, "Enter");
        Assert.Equal(1, selected);
        Assert.Equal("false", A(input, "aria-expanded"));
    }

    [Fact]
    public void Combobox_escape_closes_and_returns_focus_to_the_input()
    {
        var h = Harness(() => new Combobox
        {
            Children =
            [
                new ComboboxItem { Value = "one", Children = [new UIText("One")] },
                new ComboboxItem { Value = "two", Children = [new UIText("Two")] },
            ],
        });

        var input = Input(h);
        h.Tab();
        h.Key(PhysicalKey.ArrowDown, "ArrowDown");
        Assert.True(input.IsFocused);

        h.Escape();
        Assert.Equal("false", A(input, "aria-expanded"));
        Assert.True(input.IsFocused);
        Assert.Empty(h.FindAll(e => e.Role == Role.ListBox && e.IsVisible));
    }

    [Fact]
    public void Combobox_selection_label_is_shown_in_the_input_after_commit()
    {
        var h = Harness(() => new Combobox
        {
            Children =
            [
                new ComboboxItem { Value = "apple", Children = [new UIText("Apple")] },
                new ComboboxItem { Value = "banana", Children = [new UIText("Banana")] },
            ],
        });

        var input = Input(h);
        h.Tab();
        h.Key(PhysicalKey.End, "End");
        h.Key(PhysicalKey.Enter, "Enter");

        Assert.Equal("Banana", Assert.IsType<InputElement>(input).Value);
        Assert.Equal("false", A(input, "aria-expanded"));
    }

    [Fact]
    public void Combobox_custom_filter_replaces_the_default_contains_match()
    {
        var h = Harness(() => new Combobox
        {
            Filter = (query, text) => text.StartsWith(query, StringComparison.OrdinalIgnoreCase),
            Children =
            [
                new ComboboxItem { Value = "apple", Children = [new UIText("Apple")] },
                new ComboboxItem { Value = "pineapple", Children = [new UIText("Pineapple")] },
            ],
        });

        Input(h);
        h.Tab();
        h.TypeText("app");

        // The default contains match would keep both; the custom prefix filter keeps only Apple.
        var options = Options(h);
        Assert.Single(options);
        Assert.Equal("Apple", options[0].TextContent.Trim());
    }

    [Fact]
    public void Combobox_changing_children_keeps_the_selection()
    {
        var showExtra = new State<bool>(false);
        var h = Harness(() => new Combobox
        {
            Children = showExtra.Value
                ?
                [
                    new ComboboxItem { Value = "apple", Children = [new UIText("Apple")] },
                    new ComboboxItem { Value = "banana", Children = [new UIText("Banana")] },
                    new ComboboxItem { Value = "cherry", Children = [new UIText("Cherry")] },
                ]
                :
                [
                    new ComboboxItem { Value = "apple", Children = [new UIText("Apple")] },
                    new ComboboxItem { Value = "banana", Children = [new UIText("Banana")] },
                ],
        });

        var input = Input(h);
        h.Tab();
        h.TypeText("apple");
        h.Key(PhysicalKey.Enter, "Enter");
        Assert.Equal("Apple", Assert.IsType<InputElement>(input).Value);

        showExtra.Value = true;
        h.Frame();
        Assert.Equal("Apple", Assert.IsType<InputElement>(input).Value);
        Assert.Equal("false", A(input, "aria-expanded"));
    }
}
