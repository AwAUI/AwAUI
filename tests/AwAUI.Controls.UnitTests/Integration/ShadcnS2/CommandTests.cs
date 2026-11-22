using AwAUI.Controls.UnitTests.Integration;
using AwAUI.Platform;
using AwAUI.UI;
using UIText = AwAUI.UI.Text;

namespace AwAUI.Shadcn.Integration.S2;

/// <summary>Command 的过滤、排序、键盘导航、空状态与 ARIA。</summary>
public class CommandTests
{
    static CommandTests() => ShadcnTheme.Register();

    static UIHarness Harness(Func<Node> render) =>
        new(render, appStyleSheets: true, width: 640, height: 480);

    static string? A(Element e, string name) => e.GetAttribute(name);

    static Element SlotOrThrow(UIHarness h, string slot) =>
        h.FindAll(e => A(e, "data-slot") == slot).FirstOrDefault()
        ?? throw new Xunit.Sdk.XunitException($"no element with data-slot=\"{slot}\"");

    static List<Element> Options(UIHarness h) => h.FindAll(e => e.Role == Role.Option);

    [Fact]
    public void Command_filters_ranks_and_keeps_focus_in_the_input()
    {
        int selected = 0;
        var h = Harness(() => new Command
        {
            Children =
            [
                new CommandInput { Placeholder = "Search commands" },
                new CommandList
                {
                    Children =
                    [
                        new CommandGroup
                        {
                            Heading = "Suggestions",
                            Children =
                            [
                                new CommandItem
                                {
                                    Value = "project",
                                    Children = [new UIText("Project")],
                                    OnSelect = _ => selected++,
                                },
                                new CommandItem
                                {
                                    Value = "my-projects",
                                    Children = [new UIText("My projects")],
                                    OnSelect = _ => selected++,
                                },
                            ],
                        },
                        new CommandEmpty { Children = [new UIText("No results")] },
                    ],
                },
            ],
        });

        Assert.NotNull(SlotOrThrow(h, "command"));
        var input = h.FindAll(e => A(e, "data-slot") == "command-input").Single();
        Assert.Equal(Role.ComboBox, input.Role);
        Assert.Equal("list", A(input, "aria-autocomplete"));

        var list = h.FindAll(e => e.Role == Role.ListBox).Single();
        Assert.Equal(list.Id, A(input, "aria-controls"));

        var group = h.FindAll(e => e.Role == Role.Group).Single();
        var heading = h.FindAll(e => A(e, "data-slot") == "command-group-heading").Single();
        Assert.Equal(heading.Id, A(group, "aria-labelledby"));

        h.Tab();
        Assert.True(input.IsFocused);

        // "proj" is a subsequence of both items, but the prefix match ranks first.
        h.TypeText("proj");
        var options = Options(h);
        Assert.Equal(2, options.Count);
        Assert.Equal("Project", options[0].TextContent.Trim());
        Assert.Equal("true", A(options[0], "aria-selected"));
        Assert.Equal(options[0].Id, A(input, "aria-activedescendant"));
        Assert.True(input.IsFocused);

        // Arrow keys move the highlight without moving focus out of the search field.
        h.Key(PhysicalKey.ArrowDown, "ArrowDown");
        Assert.True(input.IsFocused);
        Assert.Equal("true", A(options[1], "aria-selected"));
        Assert.Equal(options[1].Id, A(input, "aria-activedescendant"));

        h.Key(PhysicalKey.Enter, "Enter");
        Assert.Equal(1, selected);
        Assert.True(input.IsFocused);
    }

    [Fact]
    public void Command_empty_state_appears_when_nothing_matches()
    {
        var h = Harness(() => new Command
        {
            Children =
            [
                new CommandInput(),
                new CommandList
                {
                    Children =
                    [
                        new CommandItem { Value = "alpha", Children = [new UIText("Alpha")] },
                        new CommandEmpty { Children = [new UIText("Nothing found")] },
                    ],
                },
            ],
        });

        h.Tab();
        h.TypeText("zzz");

        Assert.Empty(Options(h));
        var empty = SlotOrThrow(h, "command-empty");
        Assert.True(empty.IsVisible);
        Assert.Equal(Role.Presentation, empty.Role);
    }

    [Fact]
    public void Command_home_end_wrap_and_skip_disabled_items()
    {
        int selected = -1;
        var h = Harness(() => new Command
        {
            Children =
            [
                new CommandInput(),
                new CommandList
                {
                    Children =
                    [
                        new CommandItem { Value = "one", Children = [new UIText("One")], OnSelect = _ => selected = 1 },
                        new CommandItem { Value = "two", Disabled = true, Children = [new UIText("Two")], OnSelect = _ => selected = 2 },
                        new CommandItem { Value = "three", Children = [new UIText("Three")], OnSelect = _ => selected = 3 },
                    ],
                },
            ],
        });

        var input = h.FindAll(e => A(e, "data-slot") == "command-input").Single();
        h.Tab();
        h.Key(PhysicalKey.ArrowDown, "ArrowDown");
        var options = Options(h);
        Assert.Equal("true", A(options[0], "aria-selected"));

        // Disabled items are visible but never receive the highlight.
        h.Key(PhysicalKey.ArrowDown, "ArrowDown");
        Assert.Equal("true", A(options[2], "aria-selected"));
        Assert.Equal("false", A(options[1], "aria-selected"));
        Assert.Equal("true", A(options[1], "aria-disabled"));

        h.Key(PhysicalKey.ArrowUp, "ArrowUp");
        Assert.Equal("true", A(options[0], "aria-selected"));

        h.Key(PhysicalKey.End, "End");
        Assert.Equal("true", A(options[2], "aria-selected"));
        h.Key(PhysicalKey.Enter, "Enter");
        Assert.Equal(3, selected);

        h.Key(PhysicalKey.Home, "Home");
        Assert.Equal("true", A(options[0], "aria-selected"));
        Assert.True(input.IsFocused);

        h.Click(options[1]);
        Assert.Equal(3, selected); // disabled item ignores pointer activation
    }

    [Fact]
    public void Command_separator_uses_separator_role()
    {
        var h = Harness(() => new Command
        {
            Children =
            [
                new CommandList
                {
                    Children =
                    [
                        new CommandItem { Value = "one", Children = [new UIText("One")] },
                        new CommandSeparator(),
                    ],
                },
            ],
        });

        var separator = SlotOrThrow(h, "command-separator");
        Assert.Equal(Role.Separator, separator.Role);
    }

    [Fact]
    public void CommandDialog_composes_a_dialog_with_command_parts()
    {
        var h = Harness(() => new CommandDialog
        {
            DefaultOpen = true,
            Title = "Command palette",
            Description = "Run a command",
            Children =
            [
                new CommandInput(),
                new CommandList
                {
                    Children = [new CommandItem { Value = "go", Children = [new UIText("Go home")] }],
                },
            ],
        });

        var dialog = h.FindAll(e => e.Role == Role.Dialog && e.IsVisible).FirstOrDefault();
        Assert.NotNull(dialog);
        Assert.Equal("Command palette", SlotOrThrow(h, "dialog-title").TextContent.Trim());
        Assert.NotNull(SlotOrThrow(h, "command"));
        Assert.Equal(Role.ComboBox, h.FindAll(e => A(e, "data-slot") == "command-input").Single().Role);

        h.Escape();
        Assert.Empty(h.FindAll(e => e.Role == Role.Dialog && e.IsVisible));
    }
}
