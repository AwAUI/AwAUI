using System;
using System.Collections.Generic;
using System.Linq;
using AwAUI.Controls.UnitTests.Integration;
using AwAUI.Platform;
using AwAUI.UI;
using UIText = AwAUI.UI.Text;

namespace AwAUI.Shadcn.Integration;

/// <summary>
/// S2 menu families: ContextMenu, Menubar and NavigationMenu. Every case drives the real components through the
/// headless harness and checks roles, ARIA state, pointer/keyboard behavior and focus restoration.
/// </summary>
public class MenuFamilyTests
{
    static MenuFamilyTests() => ShadcnTheme.Register();

    static UIHarness Harness(Func<Node> render) =>
        new(render, appStyleSheets: true, width: 640, height: 480);

    static string? A(Element e, string name) => e.GetAttribute(name);

    static List<Element> Slots(UIHarness h, string slot) =>
        h.FindAll(e => A(e, "data-slot") == slot);

    static List<Element> VisibleSlots(UIHarness h, string slot) =>
        Slots(h, slot).Where(e => e.IsVisible).ToList();

    static Element SlotOrThrow(UIHarness h, string slot) =>
        Slots(h, slot).FirstOrDefault()
        ?? throw new Xunit.Sdk.XunitException($"no element with data-slot=\"{slot}\"");

    static bool IsVisible(UIHarness h, string slot) => Slots(h, slot).Any(e => e.IsVisible);

    static Element Item(UIHarness h, string slot, string text) =>
        VisibleSlots(h, slot).Single(e => e.TextContent.Trim() == text);

    static List<Element> MenubarTriggers(UIHarness h) =>
        VisibleSlots(h, "menubar-trigger");

    static List<Element> NavigationTriggers(UIHarness h) =>
        VisibleSlots(h, "navigation-menu-trigger");

    // ------------------------------------------------------------ ContextMenu

    [Fact]
    public void ContextMenu_opens_at_the_pointer_with_menu_semantics_and_selects_an_item()
    {
        bool selected = false;
        var h = Harness(() => new ContextMenu
        {
            Children =
            [
                new ContextMenuTrigger { Children = [new UIText("Canvas")] },
                new ContextMenuContent
                {
                    Children =
                    [
                        new ContextMenuItem { Children = [new UIText("Copy")], OnSelect = _ => selected = true },
                        new ContextMenuSeparator(),
                        new ContextMenuCheckboxItem { DefaultChecked = true, Children = [new UIText("Show grid")] },
                    ],
                },
            ],
        });

        var trigger = SlotOrThrow(h, "context-menu-trigger");
        Assert.Equal("menu", A(trigger, "aria-haspopup"));
        Assert.Equal("false", A(trigger, "aria-expanded"));
        Assert.False(IsVisible(h, "context-menu-content"));

        h.RightClick(trigger);

        Assert.Equal("true", A(trigger, "aria-expanded"));
        Assert.NotNull(A(trigger, "aria-controls"));
        var menu = h.FindAll(e => e.Role == Role.Menu && e.IsVisible).Single();
        Assert.Equal("context-menu-content", A(menu, "data-slot"));
        Assert.Equal("open", A(menu, "data-state"));

        var item = Item(h, "context-menu-item", "Copy");
        Assert.Equal("menuitem", A(item, "role"));
        Assert.True(item.IsFocused);

        var check = Item(h, "context-menu-checkbox-item", "Show grid");
        Assert.Equal("menuitemcheckbox", A(check, "role"));
        Assert.Equal("true", A(check, "aria-checked"));

        h.Click(item);

        Assert.True(selected);
        Assert.False(IsVisible(h, "context-menu-content"));
        Assert.Equal("false", A(trigger, "aria-expanded"));
    }

    [Fact]
    public void ContextMenu_keyboard_opens_at_the_trigger_and_escape_restores_focus()
    {
        var h = Harness(() => new ContextMenu
        {
            Children =
            [
                new ContextMenuTrigger { Children = [new UIText("Canvas")] },
                new ContextMenuContent
                {
                    Children = [new ContextMenuItem { Children = [new UIText("Paste")] }],
                },
            ],
        });

        var trigger = SlotOrThrow(h, "context-menu-trigger");
        trigger.Focus();
        h.Frame();
        Assert.True(trigger.IsFocused);

        h.Key(PhysicalKey.F10, "F10", KeyModifiers.Shift);
        Assert.True(IsVisible(h, "context-menu-content"));
        Assert.Equal("true", A(trigger, "aria-expanded"));

        h.Escape();

        Assert.False(IsVisible(h, "context-menu-content"));
        Assert.Equal("false", A(trigger, "aria-expanded"));
        Assert.True(trigger.IsFocused);
    }

    [Fact]
    public void ContextMenu_checkbox_and_radio_items_report_and_update_state()
    {
        bool? checkedChanged = null;
        string? radioChanged = null;
        var h = Harness(() => new ContextMenu
        {
            Children =
            [
                new ContextMenuTrigger { Children = [new UIText("Canvas")] },
                new ContextMenuContent
                {
                    Children =
                    [
                        new ContextMenuCheckboxItem
                        {
                            DefaultChecked = true,
                            CheckedChanged = v => checkedChanged = v,
                            Children = [new UIText("Bold")],
                        },
                        new ContextMenuRadioGroup
                        {
                            DefaultValue = "low",
                            ValueChanged = v => radioChanged = v,
                            Children =
                            [
                                new ContextMenuRadioItem { Value = "low", Children = [new UIText("Low")] },
                                new ContextMenuRadioItem { Value = "high", Children = [new UIText("High")] },
                            ],
                        },
                    ],
                },
            ],
        });

        var trigger = SlotOrThrow(h, "context-menu-trigger");
        h.RightClick(trigger);

        var check = Item(h, "context-menu-checkbox-item", "Bold");
        Assert.Equal("menuitemcheckbox", A(check, "role"));
        Assert.Equal("true", A(check, "aria-checked"));
        h.Click(check);
        Assert.False(checkedChanged);
        Assert.False(IsVisible(h, "context-menu-content"));

        h.RightClick(trigger);
        var low = Item(h, "context-menu-radio-item", "Low");
        var high = Item(h, "context-menu-radio-item", "High");
        Assert.Equal("menuitemradio", A(low, "role"));
        Assert.Equal("true", A(low, "aria-checked"));
        Assert.Equal("false", A(high, "aria-checked"));

        h.Click(high);
        Assert.Equal("high", radioChanged);
        Assert.False(IsVisible(h, "context-menu-content"));
    }

    [Fact]
    public void ContextMenu_submenu_opens_with_arrow_right_and_escape_returns_to_the_item()
    {
        var h = Harness(() => new ContextMenu
        {
            Children =
            [
                new ContextMenuTrigger { Children = [new UIText("Canvas")] },
                new ContextMenuContent
                {
                    Children =
                    [
                        new ContextMenuSub
                        {
                            Children =
                            [
                                new ContextMenuSubTrigger { Children = [new UIText("Share")] },
                                new ContextMenuSubContent
                                {
                                    Children = [new ContextMenuItem { Children = [new UIText("Copy link")] }],
                                },
                            ],
                        },
                    ],
                },
            ],
        });

        var trigger = SlotOrThrow(h, "context-menu-trigger");
        h.RightClick(trigger);

        var share = Item(h, "context-menu-sub-trigger", "Share");
        Assert.True(share.IsFocused);
        h.Key(PhysicalKey.ArrowRight, "ArrowRight");

        Assert.True(IsVisible(h, "context-menu-sub-content"));
        var nested = Item(h, "context-menu-item", "Copy link");
        Assert.True(nested.IsFocused);
        Assert.True(IsVisible(h, "context-menu-content"));

        h.Escape();

        Assert.False(IsVisible(h, "context-menu-sub-content"));
        Assert.True(IsVisible(h, "context-menu-content"));
        Assert.True(share.IsFocused);

        h.Escape();
        Assert.False(IsVisible(h, "context-menu-content"));
    }

    [Fact]
    public void ContextMenu_touch_long_press_opens_after_the_configured_delay()
    {
        var clock = new ManualUIClock();
        var h = Harness(() => new ContextMenu
        {
            Children =
            [
                new ContextMenuTrigger { LongPressDelay = 500, Children = [new UIText("Canvas")] },
                new ContextMenuContent
                {
                    Children = [new ContextMenuItem { Children = [new UIText("Inspect")] }],
                },
            ],
        });
        h.Root.Clock = clock;

        var trigger = SlotOrThrow(h, "context-menu-trigger");
        var point = trigger.GetBoundingClientRect().Center;
        h.Root.Input.DispatchPointer(Pointer(RawPointerEventType.Down, PointerType.Touch, point,
            PointerButtons.Left, PointerButtons.Left));
        h.Frame();
        Assert.False(IsVisible(h, "context-menu-content"));

        clock.Advance(TimeSpan.FromMilliseconds(499));
        h.Frame();
        Assert.False(IsVisible(h, "context-menu-content"));

        clock.Advance(TimeSpan.FromMilliseconds(2));
        h.Frame();
        Assert.True(IsVisible(h, "context-menu-content"));
    }

    [Fact]
    public void ContextMenu_touch_movement_cancels_the_long_press()
    {
        var clock = new ManualUIClock();
        var h = Harness(() => new ContextMenu
        {
            Children =
            [
                new ContextMenuTrigger { LongPressDelay = 500, LongPressTolerance = 8, Children = [new UIText("Canvas")] },
                new ContextMenuContent
                {
                    Children = [new ContextMenuItem { Children = [new UIText("Inspect")] }],
                },
            ],
        });
        h.Root.Clock = clock;

        var trigger = SlotOrThrow(h, "context-menu-trigger");
        var point = trigger.GetBoundingClientRect().Center;
        h.Root.Input.DispatchPointer(Pointer(RawPointerEventType.Down, PointerType.Touch, point,
            PointerButtons.Left, PointerButtons.Left));
        h.Frame();

        h.Root.Input.DispatchPointer(Pointer(RawPointerEventType.Move, PointerType.Touch,
            new Point(point.X + 40, point.Y), PointerButtons.None, PointerButtons.Left));
        h.Frame();

        clock.Advance(TimeSpan.FromMilliseconds(800));
        h.Frame();
        Assert.False(IsVisible(h, "context-menu-content"));
    }

    // ------------------------------------------------------------ Menubar

    static UIHarness MenubarHarness(bool loop = true)
    {
        return Harness(() => new Menubar
        {
            Loop = loop,
            Children =
            [
                new MenubarMenu
                {
                    Children =
                    [
                        new MenubarTrigger { Children = [new UIText("File")] },
                        new MenubarContent
                        {
                            Children =
                            [
                                new MenubarItem { Children = [new UIText("New")] },
                                new MenubarSub
                                {
                                    Children =
                                    [
                                        new MenubarSubTrigger { Children = [new UIText("Export")] },
                                        new MenubarSubContent
                                        {
                                            Children = [new MenubarItem { Children = [new UIText("PDF")] }],
                                        },
                                    ],
                                },
                                new MenubarSeparator(),
                                new MenubarCheckboxItem { Children = [new UIText("Auto save")] },
                            ],
                        },
                    ],
                },
                new MenubarMenu
                {
                    Children =
                    [
                        new MenubarTrigger { Children = [new UIText("Edit")] },
                        new MenubarContent
                        {
                            Children = [new MenubarItem { Children = [new UIText("Undo")] }],
                        },
                    ],
                },
            ],
        });
    }

    [Fact]
    public void Menubar_exposes_menubar_semantics_and_arrow_down_enters_the_menu()
    {
        var h = MenubarHarness();

        var bar = h.FindAll(e => e.Role == Role.MenuBar).Single();
        Assert.Equal("menubar", A(bar, "data-slot"));
        Assert.Equal("horizontal", A(bar, "aria-orientation"));

        var triggers = MenubarTriggers(h);
        Assert.Equal(2, triggers.Count);
        Assert.Equal("menuitem", A(triggers[0], "role"));
        Assert.Equal("menu", A(triggers[0], "aria-haspopup"));
        Assert.Equal("false", A(triggers[0], "aria-expanded"));
        Assert.NotNull(A(triggers[0], "aria-controls"));

        h.Tab();
        Assert.True(triggers[0].IsFocused);

        h.Key(PhysicalKey.ArrowDown, "ArrowDown");

        Assert.True(IsVisible(h, "menubar-content"));
        Assert.Equal("true", A(triggers[0], "aria-expanded"));
        var content = SlotOrThrow(h, "menubar-content");
        Assert.Equal("menu", A(content, "role"));
        Assert.Equal(triggers[0].Id, A(content, "aria-labelledby"));
        Assert.Equal("New", h.Focused?.TextContent.Trim());

        h.Key(PhysicalKey.ArrowRight, "ArrowRight");

        Assert.Equal("Undo", h.Focused?.TextContent.Trim());
        Assert.Equal("false", A(triggers[0], "aria-expanded"));
        Assert.Equal("true", A(triggers[1], "aria-expanded"));

        h.Escape();

        Assert.False(IsVisible(h, "menubar-content"));
        Assert.Equal("false", A(triggers[1], "aria-expanded"));
        Assert.True(triggers[1].IsFocused);
    }

    [Fact]
    public void Menubar_click_opens_and_typeahead_moves_between_top_level_triggers()
    {
        var h = MenubarHarness();
        var triggers = MenubarTriggers(h);

        h.Click(triggers[0]);
        Assert.True(IsVisible(h, "menubar-content"));
        Assert.Equal("true", A(triggers[0], "aria-expanded"));
        Assert.True(Item(h, "menubar-item", "New").IsFocused);

        h.Escape();
        Assert.True(triggers[0].IsFocused);

        h.Key(PhysicalKey.KeyE, "e");

        Assert.True(MenubarTriggers(h)[1].IsFocused);
        Assert.False(IsVisible(h, "menubar-content"));

        h.Key(PhysicalKey.ArrowRight, "ArrowRight");
        Assert.True(MenubarTriggers(h)[0].IsFocused);
    }

    [Fact]
    public void Menubar_loop_false_stops_at_the_edges()
    {
        var h = MenubarHarness(loop: false);
        var triggers = MenubarTriggers(h);

        h.Tab();
        h.Key(PhysicalKey.ArrowRight, "ArrowRight");
        Assert.True(triggers[1].IsFocused);
        h.Key(PhysicalKey.ArrowRight, "ArrowRight");
        Assert.True(triggers[1].IsFocused);

        h.Key(PhysicalKey.ArrowLeft, "ArrowLeft");
        Assert.True(triggers[0].IsFocused);
        h.Key(PhysicalKey.ArrowLeft, "ArrowLeft");
        Assert.True(triggers[0].IsFocused);
    }

    [Fact]
    public void Menubar_submenu_opens_with_arrow_right_and_escape_closes_one_level()
    {
        var h = MenubarHarness();

        h.Tab();
        h.Key(PhysicalKey.ArrowDown, "ArrowDown");
        Assert.Equal("New", h.Focused?.TextContent.Trim());

        h.Key(PhysicalKey.ArrowDown, "ArrowDown");
        var export = Item(h, "menubar-sub-trigger", "Export");
        Assert.True(export.IsFocused);

        h.Key(PhysicalKey.ArrowRight, "ArrowRight");
        Assert.True(IsVisible(h, "menubar-sub-content"));
        Assert.Equal("PDF", h.Focused?.TextContent.Trim());
        Assert.True(IsVisible(h, "menubar-content"));

        h.Escape();
        Assert.False(IsVisible(h, "menubar-sub-content"));
        Assert.True(IsVisible(h, "menubar-content"));
        Assert.True(export.IsFocused);

        h.Escape();
        Assert.False(IsVisible(h, "menubar-content"));
    }

    // ------------------------------------------------------------ NavigationMenu

    static UIHarness NavigationHarness()
    {
        return Harness(() => new NavigationMenu
        {
            Children =
            [
                new NavigationMenuList
                {
                    Children =
                    [
                        new NavigationMenuItem
                        {
                            Children =
                            [
                                new NavigationMenuTrigger { Children = [new UIText("Products")] },
                                new NavigationMenuContent
                                {
                                    Children =
                                    [
                                        new NavigationMenuLink { Href = "#analytics", Children = [new UIText("Analytics")] },
                                        new NavigationMenuLink { Href = "#reports", Active = true, Children = [new UIText("Reports")] },
                                    ],
                                },
                            ],
                        },
                        new NavigationMenuItem
                        {
                            Children =
                            [
                                new NavigationMenuTrigger { Children = [new UIText("Docs")] },
                                new NavigationMenuContent
                                {
                                    Children = [new NavigationMenuLink { Children = [new UIText("Getting started")] }],
                                },
                            ],
                        },
                    ],
                },
            ],
        });
    }

    [Fact]
    public void NavigationMenu_exposes_navigation_semantics_and_click_opens_an_anchored_region()
    {
        var clock = new ManualUIClock();
        var h = NavigationHarness();
        h.Root.Clock = clock;

        var nav = h.FindAll(e => e.Role == Role.Navigation).Single();
        Assert.Equal("navigation-menu", A(nav, "data-slot"));
        Assert.Equal("true", A(nav, "data-viewport"));

        var list = h.FindAll(e => e.Role == Role.List && A(e, "data-slot") == "navigation-menu-list").Single();
        Assert.NotNull(list);
        Assert.Equal(2, h.FindAll(e => e.Role == Role.ListItem && A(e, "data-slot") == "navigation-menu-item").Count);

        var triggers = NavigationTriggers(h);
        Assert.Equal(2, triggers.Count);
        Assert.Equal("false", A(triggers[0], "aria-expanded"));
        Assert.NotNull(A(triggers[0], "aria-controls"));

        h.Click(triggers[0]);

        Assert.Equal("true", A(triggers[0], "aria-expanded"));
        var content = SlotOrThrow(h, "navigation-menu-content");
        Assert.True(content.IsVisible);
        Assert.Equal("region", A(content, "role"));
        Assert.Equal("open", A(content, "data-state"));
        Assert.Equal(triggers[0].Id, A(content, "aria-labelledby"));

        var links = h.FindAll(e => e.Role == Role.Link && e.IsVisible).ToList();
        Assert.Equal(2, links.Count);
        Assert.Equal("Analytics", links[0].TextContent.Trim());
        Assert.Equal("#analytics", A(links[0], "href"));
        Assert.Equal("true", A(links[1], "data-active"));
        Assert.Equal("page", A(links[1], "aria-current"));

        h.Escape();
        // Let the exit transition finish before the panel is unmounted.
        clock.Advance(TimeSpan.FromMilliseconds(300));
        h.Frame();

        Assert.False(IsVisible(h, "navigation-menu-content"));
        Assert.Equal("false", A(triggers[0], "aria-expanded"));
        Assert.True(triggers[0].IsFocused);
    }

    [Fact]
    public void NavigationMenu_keyboard_focus_opens_and_arrow_down_moves_between_links()
    {
        var h = NavigationHarness();
        var triggers = NavigationTriggers(h);

        h.Tab();
        Assert.True(triggers[0].IsFocused);
        Assert.True(IsVisible(h, "navigation-menu-content"));
        Assert.Equal("true", A(triggers[0], "aria-expanded"));

        h.Key(PhysicalKey.ArrowDown, "ArrowDown");
        Assert.Equal("Analytics", h.Focused?.TextContent.Trim());

        h.Key(PhysicalKey.ArrowDown, "ArrowDown");
        Assert.Equal("Reports", h.Focused?.TextContent.Trim());

        h.Escape();

        Assert.False(IsVisible(h, "navigation-menu-content"));
        Assert.True(triggers[0].IsFocused);
    }

    [Fact]
    public void NavigationMenu_hover_waits_for_the_delay_and_stays_open_over_the_panel()
    {
        var clock = new ManualUIClock();
        var h = NavigationHarness();
        h.Root.Clock = clock;

        var trigger = NavigationTriggers(h)[0];
        h.PointerMove(trigger.GetBoundingClientRect().Center, PointerButtons.None);
        Assert.False(IsVisible(h, "navigation-menu-content"));

        clock.Advance(TimeSpan.FromMilliseconds(199));
        h.Frame();
        Assert.False(IsVisible(h, "navigation-menu-content"));

        clock.Advance(TimeSpan.FromMilliseconds(2));
        h.Frame();
        Assert.True(IsVisible(h, "navigation-menu-content"));
        Assert.Equal("true", A(trigger, "aria-expanded"));
        var content = SlotOrThrow(h, "navigation-menu-content");

        h.PointerMove(content.GetBoundingClientRect().Center, PointerButtons.None);
        clock.Advance(TimeSpan.FromMilliseconds(1000));
        h.Frame();
        Assert.True(content.IsVisible);

        h.PointerMove(new Point(620, 460), PointerButtons.None);
        clock.Advance(TimeSpan.FromMilliseconds(2000));
        h.Frame();
        clock.Advance(TimeSpan.FromMilliseconds(300));
        h.Frame();
        Assert.False(IsVisible(h, "navigation-menu-content"));
        Assert.Equal("false", A(trigger, "aria-expanded"));
    }

    [Fact]
    public void NavigationMenu_arrow_keys_move_between_triggers_and_typeahead_matches()
    {
        var h = NavigationHarness();
        var triggers = NavigationTriggers(h);

        triggers[0].Focus(new FocusOptions { Origin = FocusOrigin.Keyboard });
        h.Frame();
        Assert.True(triggers[0].IsFocused);
        Assert.True(IsVisible(h, "navigation-menu-content"));

        h.Escape();
        Assert.True(triggers[0].IsFocused);

        h.Key(PhysicalKey.ArrowRight, "ArrowRight");
        Assert.True(NavigationTriggers(h)[1].IsFocused);
        Assert.Equal("true", A(NavigationTriggers(h)[1], "aria-expanded"));

        h.Escape();
        Assert.True(NavigationTriggers(h)[1].IsFocused);

        h.Key(PhysicalKey.KeyP, "p");
        Assert.True(NavigationTriggers(h)[0].IsFocused);
    }

    [Fact]
    public void NavigationMenu_trigger_keeps_exactly_one_chevron_across_rerenders()
    {
        var clock = new ManualUIClock();
        var h = NavigationHarness();
        h.Root.Clock = clock;

        static int Chevrons(UIHarness h) =>
            h.FindAll(e => A(e, "data-slot") == "navigation-menu-trigger-icon").Count;

        Assert.Equal(2, Chevrons(h));

        for (int i = 0; i < 3; i++)
        {
            h.Click(NavigationTriggers(h)[0]);
            clock.Advance(TimeSpan.FromMilliseconds(300));
            h.Frame();
            h.Escape();
            clock.Advance(TimeSpan.FromMilliseconds(300));
            h.Frame();
        }

        Assert.Equal(2, Chevrons(h));
        foreach (var trigger in NavigationTriggers(h))
            Assert.Single(trigger.Children, e => A(e, "data-slot") == "navigation-menu-trigger-icon");
    }

    static UIHarness NavigationValueHarness(string? value, Action<string?> changed, bool controlled = false)
    {
        return Harness(() => new NavigationMenu
        {
            IsValueControlled = controlled,
            Value = value,
            ValueChanged = changed,
            Children =
            [
                new NavigationMenuList
                {
                    Children =
                    [
                        new NavigationMenuItem
                        {
                            Value = "products",
                            Children =
                            [
                                new NavigationMenuTrigger { Children = [new UIText("Products")] },
                                new NavigationMenuContent
                                {
                                    Children =
                                    [
                                        new NavigationMenuLink { Href = "#analytics", Children = [new UIText("Analytics")] },
                                        new NavigationMenuLink { Href = "#reports", Active = true, Children = [new UIText("Reports")] },
                                    ],
                                },
                            ],
                        },
                        new NavigationMenuItem
                        {
                            Value = "docs",
                            Children =
                            [
                                new NavigationMenuTrigger { Children = [new UIText("Docs")] },
                                new NavigationMenuContent
                                {
                                    Children = [new NavigationMenuLink { Children = [new UIText("Getting started")] }],
                                },
                            ],
                        },
                    ],
                },
            ],
        });
    }

    [Fact]
    public void NavigationMenu_renders_one_viewport_that_holds_the_open_content_under_its_trigger()
    {
        var h = NavigationHarness();

        // Exactly one viewport, rendered inside the root element, even before anything opens.
        var viewports = Slots(h, "navigation-menu-viewport");
        Assert.Single(viewports);
        var nav = h.FindAll(e => e.Role == Role.Navigation).Single();
        Assert.True(nav.IsInclusiveAncestorOf(viewports[0]));
        Assert.Equal("closed", A(viewports[0], "data-state"));

        var triggers = NavigationTriggers(h);
        h.Click(triggers[0]);

        Assert.Single(Slots(h, "navigation-menu-viewport"));
        var content = SlotOrThrow(h, "navigation-menu-content");
        Assert.True(viewports[0].IsInclusiveAncestorOf(content));
        Assert.Equal("open", A(viewports[0], "data-state"));
        Assert.Equal("open", A(content, "data-state"));
        Assert.Equal(viewports[0].Id, A(triggers[0], "aria-controls"));
        Assert.Equal("true", A(triggers[0], "aria-expanded"));
        Assert.Equal("Analytics", h.FindAll(e => e.Role == Role.Link && e.IsVisible).First().TextContent.Trim());

        // The viewport is positioned under the active trigger and sized to its content.
        var triggerRect = triggers[0].GetBoundingClientRect();
        var viewportRect = viewports[0].GetBoundingClientRect();
        Assert.True(viewportRect.Width > 0);
        Assert.True(viewportRect.Height > 0);
        Assert.InRange(viewportRect.Top, triggerRect.Bottom, triggerRect.Bottom + 12);
    }

    [Fact]
    public void NavigationMenu_switching_triggers_moves_the_content_within_the_one_viewport()
    {
        var h = NavigationHarness();
        var triggers = NavigationTriggers(h);

        h.Click(triggers[0]);
        Assert.Equal("Analytics", h.FindAll(e => e.Role == Role.Link && e.IsVisible).First().TextContent.Trim());

        h.Click(NavigationTriggers(h)[1]);

        var viewports = Slots(h, "navigation-menu-viewport");
        Assert.Single(viewports);
        var contents = Slots(h, "navigation-menu-content");
        Assert.Single(contents);
        Assert.True(viewports[0].IsInclusiveAncestorOf(contents[0]));
        Assert.Equal("Getting started", h.FindAll(e => e.Role == Role.Link && e.IsVisible).Single().TextContent.Trim());
        Assert.Equal("false", A(NavigationTriggers(h)[0], "aria-expanded"));
        Assert.Equal("true", A(NavigationTriggers(h)[1], "aria-expanded"));
        Assert.Equal(2, h.FindAll(e => A(e, "data-slot") == "navigation-menu-trigger-icon").Count);
    }

    [Fact]
    public void NavigationMenu_keeps_the_panel_mounted_for_the_exit_animation()
    {
        var clock = new ManualUIClock();
        var h = NavigationHarness();
        h.Root.Clock = clock;

        var trigger = NavigationTriggers(h)[0];
        h.Click(trigger);
        var open = SlotOrThrow(h, "navigation-menu-content");
        Assert.True(open.IsVisible);
        Assert.Equal("open", A(open, "data-state"));

        h.Escape();

        // The panel stays mounted with data-state="closed" so PopupMotion's popup-out animation can run.
        var exiting = SlotOrThrow(h, "navigation-menu-content");
        Assert.True(exiting.IsVisible);
        Assert.Equal("closed", A(exiting, "data-state"));
        Assert.Equal("to-end", A(exiting, "data-motion"));
        Assert.Equal("false", A(trigger, "aria-expanded"));
        // The viewport is still visible during the exit so the closing panel is not clipped, but its state
        // already reports closed and it is hidden from assistive technology.
        var viewport = Slots(h, "navigation-menu-viewport").Single();
        Assert.True(viewport.IsVisible);
        Assert.Equal("closed", A(viewport, "data-state"));
        Assert.Equal("true", A(viewport, "aria-hidden"));

        clock.Advance(TimeSpan.FromMilliseconds(150));
        h.Frame();

        Assert.Empty(Slots(h, "navigation-menu-content"));
        Assert.Equal("closed", A(Slots(h, "navigation-menu-viewport").Single(), "data-state"));
        Assert.False(IsVisible(h, "navigation-menu-viewport"));
    }

    [Fact]
    public void NavigationMenu_closes_immediately_without_a_clock()
    {
        var h = NavigationHarness();
        Assert.Null(h.Root.Clock);

        var trigger = NavigationTriggers(h)[0];
        h.Click(trigger);
        Assert.True(IsVisible(h, "navigation-menu-content"));

        h.Escape();

        Assert.Empty(Slots(h, "navigation-menu-content"));
        Assert.False(IsVisible(h, "navigation-menu-viewport"));
        Assert.Equal("closed", A(Slots(h, "navigation-menu-viewport").Single(), "data-state"));
        Assert.Equal("false", A(trigger, "aria-expanded"));
    }

    [Fact]
    public void NavigationMenu_reopening_before_the_exit_finishes_cancels_the_removal()
    {
        var clock = new ManualUIClock();
        var h = NavigationHarness();
        h.Root.Clock = clock;

        var trigger = NavigationTriggers(h)[0];
        h.Click(trigger);
        h.Escape();
        Assert.True(IsVisible(h, "navigation-menu-content"));

        // Reopen the same item before the 150ms exit timer fires.
        h.Click(trigger);
        Assert.Equal("open", A(SlotOrThrow(h, "navigation-menu-content"), "data-state"));
        Assert.Equal("true", A(trigger, "aria-expanded"));

        // The cancelled exit must not remove the reopened panel.
        clock.Advance(TimeSpan.FromMilliseconds(300));
        h.Frame();

        var content = SlotOrThrow(h, "navigation-menu-content");
        Assert.True(content.IsVisible);
        Assert.Equal("open", A(content, "data-state"));
        Assert.True(Slots(h, "navigation-menu-viewport").Single().IsVisible);
        Assert.Equal("true", A(trigger, "aria-expanded"));
    }

    [Fact]
    public void NavigationMenu_controlled_value_opens_that_item_and_reports_requested_values()
    {
        var changes = new List<string?>();
        var h = NavigationValueHarness("docs", changes.Add);

        // The controlled Value opens Docs without any interaction.
        var triggers = NavigationTriggers(h);
        Assert.Equal("false", A(triggers[0], "aria-expanded"));
        Assert.Equal("true", A(triggers[1], "aria-expanded"));
        var content = SlotOrThrow(h, "navigation-menu-content");
        Assert.True(Slots(h, "navigation-menu-viewport").Single().IsInclusiveAncestorOf(content));
        Assert.Equal("Getting started", h.FindAll(e => e.Role == Role.Link && e.IsVisible).Single().TextContent.Trim());

        // Opening another item only reports the requested value; the controlled Value stays authoritative.
        h.Click(triggers[0]);
        Assert.Contains("products", changes);
        Assert.Equal("Getting started", h.FindAll(e => e.Role == Role.Link && e.IsVisible).Single().TextContent.Trim());
        Assert.Equal("true", A(triggers[1], "aria-expanded"));

        // Escape requests clearing the value (null).
        h.Escape();
        Assert.Null(changes[^1]);
    }

    [Fact]
    public void NavigationMenu_controlled_null_value_means_closed_and_only_reports_requests()
    {
        var changes = new List<string?>();
        var h = NavigationValueHarness(null, changes.Add, controlled: true);

        // A null Value is a valid controlled state (closed), not a request to switch to uncontrolled mode.
        var triggers = NavigationTriggers(h);
        Assert.Equal("false", A(triggers[0], "aria-expanded"));
        Assert.False(IsVisible(h, "navigation-menu-content"));

        h.Click(triggers[0]);

        // Opening only reports the request; without the parent writing Value the panel stays closed.
        Assert.Equal(new string?[] { "products" }, changes);
        Assert.False(IsVisible(h, "navigation-menu-content"));
        Assert.Equal("false", A(triggers[0], "aria-expanded"));
        Assert.Equal("closed", A(Slots(h, "navigation-menu-viewport").Single(), "data-state"));
    }

    [Fact]
    public void NavigationMenu_controlled_render_does_not_write_state_during_build()
    {
        var changes = new List<string?>();
        var h = NavigationValueHarness("docs", changes.Add, controlled: true);

        h.Click(NavigationTriggers(h)[0]);
        h.Escape();
        h.Frame();

        Assert.DoesNotContain(h.Diagnostics, d => d.Code == "AW1005");
    }

    [Fact]
    public void NavigationMenu_escape_closes_clears_the_value_and_restores_focus()
    {
        var changes = new List<string?>();
        var h = NavigationValueHarness(null, changes.Add);

        var triggers = NavigationTriggers(h);
        h.Click(triggers[0]);
        Assert.Equal(new string?[] { "products" }, changes);
        Assert.True(IsVisible(h, "navigation-menu-content"));
        Assert.True(triggers[0].IsFocused);

        h.Escape();

        Assert.Equal(new string?[] { "products", null }, changes);
        Assert.False(IsVisible(h, "navigation-menu-content"));
        Assert.Equal("false", A(triggers[0], "aria-expanded"));
        Assert.Equal("closed", A(Slots(h, "navigation-menu-viewport").Single(), "data-state"));
        Assert.True(triggers[0].IsFocused);
    }

    static RawPointerEvent Pointer(RawPointerEventType type, PointerType device, Point position,
        PointerButtons changed, PointerButtons buttons) =>
        new(type, device, 1, position, changed, buttons, KeyModifiers.None, TimeSpan.Zero);

    // ------------------------------------------------------------ DropdownMenu

    static UIHarness DropdownHarness()
    {
        return Harness(() => new DropdownMenu
        {
            Children =
            [
                new DropdownMenuTrigger { Children = [new UIText("Actions")] },
                new DropdownMenuContent
                {
                    Children =
                    [
                        new DropdownMenuItem { Children = [new UIText("Alpha")] },
                        new DropdownMenuItem { Children = [new UIText("Beta")] },
                        new DropdownMenuItem { Children = [new UIText("Gamma")] },
                    ],
                },
            ],
        });
    }

    [Fact]
    public void DropdownMenu_arrow_up_opens_on_the_last_item_and_arrow_down_on_the_first()
    {
        var h = DropdownHarness();
        var trigger = SlotOrThrow(h, "dropdown-menu-trigger");

        trigger.Focus(new FocusOptions { Origin = FocusOrigin.Keyboard });
        h.Frame();
        h.Key(PhysicalKey.ArrowUp, "ArrowUp");

        Assert.True(IsVisible(h, "dropdown-menu-content"));
        Assert.Equal("Gamma", h.Focused?.TextContent.Trim());

        h.Escape();
        Assert.False(IsVisible(h, "dropdown-menu-content"));

        h.Key(PhysicalKey.ArrowDown, "ArrowDown");
        Assert.True(IsVisible(h, "dropdown-menu-content"));
        Assert.Equal("Alpha", h.Focused?.TextContent.Trim());

        h.Escape();
        h.Key(PhysicalKey.ArrowDown, "ArrowDown");
        Assert.Equal("Alpha", h.Focused?.TextContent.Trim());
    }

    [Fact]
    public void DropdownMenu_radio_items_use_a_radio_indicator_and_keep_data_state()
    {
        var h = Harness(() => new DropdownMenu
        {
            DefaultOpen = true,
            Children =
            [
                new DropdownMenuTrigger { Children = [new UIText("Actions")] },
                new DropdownMenuContent
                {
                    Children =
                    [
                        new DropdownMenuRadioGroup
                        {
                            DefaultValue = "low",
                            Children =
                            [
                                new DropdownMenuRadioItem { Value = "low", Children = [new UIText("Low")] },
                                new DropdownMenuRadioItem { Value = "high", Children = [new UIText("High")] },
                            ],
                        },
                    ],
                },
            ],
        });

        var low = Item(h, "dropdown-menu-radio-item", "Low");
        var high = Item(h, "dropdown-menu-radio-item", "High");
        Assert.Equal("menuitemradio", A(low, "role"));
        Assert.Equal("checked", A(low, "data-state"));
        Assert.Equal("unchecked", A(high, "data-state"));
        Assert.Equal("true", A(low, "aria-checked"));
        Assert.Equal("false", A(high, "aria-checked"));
        // v4 renders a 3.5 box around the indicator for both checked and unchecked rows.
        var lowBox = Assert.Single(low.Children, e => e.Tag == "span");
        var highBox = Assert.Single(high.Children, e => e.Tag == "span");
        var indicator = Assert.Single(lowBox.Children, e => A(e, "data-slot") == "dropdown-menu-radio-item-indicator");
        Assert.Contains("fill-current", A(indicator, "class"));
        Assert.Empty(highBox.Children);
    }

    // ------------------------------------------------------------ Accordion / Tabs

    [Fact]
    public void Accordion_only_reports_aria_disabled_for_a_disabled_item()
    {
        var h = Harness(() => new Accordion
        {
            DefaultValue = "project",
            Children =
            [
                new AccordionItem
                {
                    Value = "project",
                    Children =
                    [
                        new AccordionTrigger { Children = [new UIText("Project")] },
                        new AccordionContent { Children = [new UIText("Details")] },
                    ],
                },
                new AccordionItem
                {
                    Value = "locked",
                    Disabled = true,
                    Children = [new AccordionTrigger { Children = [new UIText("Locked")] }],
                },
            ],
        });

        var triggers = Slots(h, "accordion-trigger");
        Assert.Null(A(triggers[0], "aria-disabled"));
        Assert.Equal("true", A(triggers[1], "aria-disabled"));

        // Radix exposes the orientation and open state on the item, header and trigger, and
        // flags disabled triggers with data-disabled.
        Assert.Equal("vertical", A(SlotOrThrow(h, "accordion"), "data-orientation"));
        foreach (var item in Slots(h, "accordion-item"))
            Assert.Equal("vertical", A(item, "data-orientation"));
        var heading = triggers[0].Parent!;
        Assert.Equal(Role.Heading, heading.Role);
        Assert.Equal("vertical", A(heading, "data-orientation"));
        Assert.Equal("open", A(heading, "data-state"));
        Assert.Null(A(heading, "data-disabled"));
        Assert.Equal("vertical", A(triggers[0], "data-orientation"));
        Assert.Null(A(triggers[0], "data-disabled"));
        Assert.Equal("", A(triggers[1], "data-disabled"));
        Assert.Equal("closed", A(triggers[1].Parent!, "data-state"));

        h.Click(triggers[0]);
        Assert.Equal("true", A(triggers[0], "aria-expanded"));
        Assert.Null(A(triggers[0], "aria-disabled"));
    }

    [Fact]
    public void Accordion_content_applies_the_consumer_class_to_the_inner_body()
    {
        var h = Harness(() => new Accordion
        {
            DefaultValue = "project",
            Children =
            [
                new AccordionItem
                {
                    Value = "project",
                    Children =
                    [
                        new AccordionTrigger { Children = [new UIText("Project")] },
                        new AccordionContent { Class = "pb-6", Children = [new UIText("Details")] },
                    ],
                },
            ],
        });

        var content = SlotOrThrow(h, "accordion-content");
        Assert.DoesNotContain("pb-6", content.ClassName);
        var body = Assert.Single(content.Children, e => e.Tag == "div");
        Assert.Contains("pb-6", body.ClassName);
    }

    [Fact]
    public void Accordion_trigger_accepts_its_own_disabled_prop()
    {
        var h = Harness(() => new Accordion
        {
            Children =
            [
                new AccordionItem
                {
                    Value = "project",
                    Children =
                    [
                        new AccordionTrigger { Disabled = true, Children = [new UIText("Project")] },
                        new AccordionContent { Children = [new UIText("Details")] },
                    ],
                },
            ],
        });

        var trigger = SlotOrThrow(h, "accordion-trigger");
        Assert.Equal("true", A(trigger, "aria-disabled"));
        Assert.Equal("", A(trigger, "data-disabled"));
        Assert.Equal("closed", A(trigger, "data-state"));

        h.Click(trigger);
        Assert.Equal("closed", A(trigger, "data-state"));
    }

    [Fact]
    public void Tabs_clicking_the_active_trigger_does_not_report_another_change()
    {
        var changes = new List<string?>();
        var h = Harness(() => new Tabs
        {
            DefaultValue = "one",
            ValueChanged = value => changes.Add(value),
            Children =
            [
                new TabsList
                {
                    Children =
                    [
                        new TabsTrigger { Value = "one", Children = [new UIText("One")] },
                        new TabsTrigger { Value = "two", Children = [new UIText("Two")] },
                    ],
                },
                new TabsContent { Value = "one", Children = [new UIText("First")] },
                new TabsContent { Value = "two", Children = [new UIText("Second")] },
            ],
        });

        var triggers = h.FindAll(e => e.Role == Role.Tab);
        h.Click(triggers[0]);
        Assert.Empty(changes);

        h.Click(triggers[1]);
        Assert.Equal(new[] { "two" }, changes);
    }

    // ------------------------------------------------------------ Menubar

    [Fact]
    public void Menubar_disabled_trigger_is_not_focusable_or_openable()
    {
        var h = Harness(() => new Menubar
        {
            Children =
            [
                new MenubarMenu
                {
                    Children =
                    [
                        new MenubarTrigger { Children = [new UIText("File")] },
                        new MenubarContent { Children = [new MenubarItem { Children = [new UIText("New")] }] },
                    ],
                },
                new MenubarMenu
                {
                    Children =
                    [
                        new MenubarTrigger { Disabled = true, Children = [new UIText("Edit")] },
                        new MenubarContent { Children = [new MenubarItem { Children = [new UIText("Undo")] }] },
                    ],
                },
            ],
        });

        var triggers = MenubarTriggers(h);
        Assert.True(triggers[0].IsFocusable);
        Assert.False(triggers[1].IsFocusable);

        h.Click(triggers[1]);
        Assert.False(IsVisible(h, "menubar-content"));

        triggers[0].Focus(new FocusOptions { Origin = FocusOrigin.Keyboard });
        h.Frame();
        h.Key(PhysicalKey.ArrowRight, "ArrowRight");
        Assert.True(MenubarTriggers(h)[0].IsFocused);
        Assert.False(IsVisible(h, "menubar-content"));
    }

    [Fact]
    public void Menubar_menu_accepts_controlled_open_state()
    {
        var h = Harness(() => new Menubar
        {
            Children =
            [
                new MenubarMenu
                {
                    Open = true,
                    Children =
                    [
                        new MenubarTrigger { Children = [new UIText("File")] },
                        new MenubarContent { Children = [new MenubarItem { Children = [new UIText("New")] }] },
                    ],
                },
            ],
        });

        Assert.True(IsVisible(h, "menubar-content"));
        Assert.Equal("true", A(MenubarTriggers(h)[0], "aria-expanded"));
    }

    [Fact]
    public void Menubar_and_context_menu_portals_keep_their_content_working()
    {
        var h = Harness(() => new ContextMenu
        {
            Children =
            [
                new ContextMenuTrigger { Children = [new UIText("Canvas")] },
                new ContextMenuPortal
                {
                    Children =
                    [
                        new ContextMenuContent
                        {
                            Children = [new ContextMenuItem { Children = [new UIText("Copy")] }],
                        },
                    ],
                },
            ],
        });

        h.RightClick(SlotOrThrow(h, "context-menu-trigger"));
        Assert.True(IsVisible(h, "context-menu-content"));

        var bar = Harness(() => new MenubarPortal
        {
            Children = [new UIText("portal content")],
        });
        Assert.True(bar.FindAll(e => e.TextContent.Contains("portal content")).Any());
    }
}
