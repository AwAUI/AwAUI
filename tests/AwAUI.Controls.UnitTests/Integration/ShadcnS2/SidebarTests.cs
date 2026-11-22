using System;
using AwAUI.Controls.UnitTests.Integration;
using AwAUI.Platform;
using AwAUI.UI;
using UIText = AwAUI.UI.Text;

namespace AwAUI.Shadcn.Integration;

/// <summary>Sidebar: provider state, shortcut, persistence, collapse modes, menu semantics and mobile sheet.</summary>
public class SidebarTests
{
    static SidebarTests() => ShadcnTheme.Register();

    static UIHarness Harness(Func<Node> render, float width = 1024) =>
        new(render, appStyleSheets: true, width: width, height: 480);

    static string? A(Element e, string name) => e.GetAttribute(name);

    static Element? Slot(UIHarness h, string slot) =>
        h.FindAll(e => A(e, "data-slot") == slot).FirstOrDefault();

    static Element SlotOrThrow(UIHarness h, string slot) =>
        Slot(h, slot) ?? throw new Xunit.Sdk.XunitException($"no element with data-slot=\"{slot}\"");

    static bool IsInside(Element root, Element? element)
    {
        for (var e = element; e is not null; e = e.Parent)
            if (ReferenceEquals(e, root)) return true;
        return false;
    }

    static Node Shell(params Node[] children) => new SidebarProvider
    {
        Children = new NodeList(children),
    };

    static Node MenuContents(params Node[] buttons) => new SidebarContent
    {
        Children =
        [
            new SidebarGroup
            {
                Children =
                [
                    new SidebarGroupLabel { Children = [new UIText("Application")] },
                    new SidebarGroupContent
                    {
                        Children =
                        [
                            new SidebarMenu
                            {
                                Children =
                                [
                                    new SidebarMenuItem { Children = new NodeList(buttons) },
                                ],
                            },
                        ],
                    },
                ],
            },
        ],
    };

    static Node Menu(params Node[] buttons) => new Sidebar { Children = [MenuContents(buttons)] };

    static Node FlatMenu(params Node[] buttons) => new Sidebar
    {
        Collapsible = SidebarCollapsible.None,
        Children = [MenuContents(buttons)],
    };

    [Fact]
    public void Shortcut_toggles_the_sidebar_and_persists_the_state()
    {
        bool? persisted = null;
        var h = Harness(() => new SidebarProvider
        {
            Persist = value => persisted = value,
            Children =
            [
                new SidebarTrigger(),
                Menu(new SidebarMenuButton { Children = [new UIText("Inbox")] }),
            ],
        });

        var sidebar = SlotOrThrow(h, "sidebar");
        Assert.Equal("expanded", A(sidebar, "data-state"));
        Assert.Null(A(sidebar, "data-collapsible"));
        Assert.Equal("left", A(sidebar, "data-side"));
        Assert.NotNull(Slot(h, "sidebar-wrapper"));

        h.Key(PhysicalKey.KeyB, "b", KeyModifiers.Control);
        Assert.Equal("collapsed", A(sidebar, "data-state"));
        Assert.Equal("offcanvas", A(sidebar, "data-collapsible"));
        Assert.False(persisted ?? true);

        h.Key(PhysicalKey.KeyB, "b", KeyModifiers.Meta);
        Assert.Equal("expanded", A(sidebar, "data-state"));
        Assert.True(persisted ?? false);
    }

    [Fact]
    public void Trigger_button_toggles_and_reports_the_state()
    {
        // At a desktop width the fixed sidebar paints over the wrapper's leading edge; the trigger sits in the
        // content region (right of the sidebar), as it does inside SidebarInset in the registry examples.
        var h = Harness(() => Shell(Menu(), new SidebarTrigger()));
        var trigger = SlotOrThrow(h, "sidebar-trigger");
        Assert.Equal(Role.Button, trigger.Role);
        Assert.Equal("Toggle Sidebar", A(trigger, "aria-label"));
        Assert.Equal("true", A(trigger, "aria-expanded"));

        h.Click(trigger);

        Assert.Equal("collapsed", A(SlotOrThrow(h, "sidebar"), "data-state"));
        Assert.Equal("false", A(trigger, "aria-expanded"));
    }

    [Fact]
    public void Collapsible_none_renders_a_static_sidebar_on_the_requested_side()
    {
        var h = Harness(() => new SidebarProvider
        {
            Children =
            [
                new Sidebar
                {
                    Side = SidebarSide.Right,
                    Collapsible = SidebarCollapsible.None,
                    Children = [new SidebarHeader { Children = [new UIText("Static")] }],
                },
            ],
        });

        var sidebar = SlotOrThrow(h, "sidebar");
        Assert.Equal("right", A(sidebar, "data-side"));
        Assert.Null(A(sidebar, "data-state"));
        Assert.Null(A(sidebar, "data-collapsible"));
    }

    [Fact]
    public void Icon_collapse_exposes_the_collapsible_mode()
    {
        var h = Harness(() => new SidebarProvider
        {
            Children =
            [
                new Sidebar { Collapsible = SidebarCollapsible.Icon, Children = [new SidebarContent()] },
                new SidebarTrigger(),
            ],
        });

        h.Click(SlotOrThrow(h, "sidebar-trigger"));

        var sidebar = SlotOrThrow(h, "sidebar");
        Assert.Equal("collapsed", A(sidebar, "data-state"));
        Assert.Equal("icon", A(sidebar, "data-collapsible"));
    }

    [Fact]
    public void Menu_button_reports_active_state_size_and_activates()
    {
        int clicks = 0;
        var h = Harness(() => Shell(FlatMenu(
            new SidebarMenuButton
            {
                IsActive = true,
                OnClick = _ => clicks++,
                Children = [new UIText("Inbox")],
            },
            new SidebarMenuButton
            {
                Variant = SidebarMenuButtonVariant.Outline,
                Size = SidebarMenuButtonSize.Sm,
                Disabled = true,
                Children = [new UIText("Archive")],
            })));

        var buttons = h.FindAll(e => A(e, "data-slot") == "sidebar-menu-button");
        Assert.Equal(2, buttons.Count);
        Assert.Equal(Role.Button, buttons[0].Role);
        Assert.Equal("true", A(buttons[0], "data-active"));
        Assert.Equal("default", A(buttons[0], "data-size"));
        Assert.Equal("sm", A(buttons[1], "data-size"));

        h.Tab();
        Assert.True(buttons[0].IsFocused);
        h.Key(PhysicalKey.Enter, "Enter");
        Assert.Equal(1, clicks);

        h.Click(buttons[0]);
        Assert.Equal(2, clicks);

        h.Click(buttons[1]);
        Assert.Equal(2, clicks);
    }

    [Fact]
    public void Menu_action_and_rail_are_accessible_buttons()
    {
        int actions = 0;
        var h = Harness(() => Shell(
            new SidebarTrigger(),
            new Sidebar
            {
                Collapsible = SidebarCollapsible.None,
                Children =
                [
                    MenuContents(
                        new SidebarMenuButton { Children = [new UIText("Inbox")] },
                        new SidebarMenuAction
                        {
                            OnClick = _ => actions++,
                            Children = [new UIText("...")],
                        }),
                    new SidebarRail(),
                ],
            }));

        var action = SlotOrThrow(h, "sidebar-menu-action");
        Assert.Equal(Role.Button, action.Role);
        h.Click(action);
        Assert.Equal(1, actions);

        var rail = SlotOrThrow(h, "sidebar-rail");
        Assert.Equal(Role.Button, rail.Role);
        Assert.Equal("Toggle Sidebar", A(rail, "aria-label"));
        Assert.True(rail.Focus());
        h.Key(PhysicalKey.Enter, "Enter");
        Assert.Equal("false", A(SlotOrThrow(h, "sidebar-trigger"), "aria-expanded"));
    }

    [Fact]
    public void Mobile_sidebar_is_a_sheet_with_a_trap_and_escape_close()
    {
        var h = Harness(() => new SidebarProvider
        {
            IsMobile = true,
            Children =
            [
                new SidebarTrigger(),
                new Sidebar
                {
                    Children =
                    [
                        new SidebarContent { Children = [new UIText("Mobile nav")] },
                    ],
                },
            ],
        });

        Assert.Empty(h.FindAll(e => e.Role == Role.Dialog && e.IsVisible));
        var trigger = SlotOrThrow(h, "sidebar-trigger");
        Assert.Equal("false", A(trigger, "aria-expanded"));

        h.Click(trigger);

        var dialog = h.FindAll(e => e.Role == Role.Dialog && e.IsVisible).Single();
        Assert.Equal("true", A(dialog, "aria-modal"));
        Assert.True(IsInside(dialog, h.Focused));
        var mobile = SlotOrThrow(h, "sidebar");
        Assert.Equal("true", A(mobile, "data-mobile"));
        Assert.Contains("Mobile nav", dialog.TextContent);

        h.Escape();
        Assert.Empty(h.FindAll(e => e.Role == Role.Dialog && e.IsVisible));
        Assert.Equal("false", A(trigger, "aria-expanded"));
    }

    [Fact]
    public void Missing_sidebar_parts_render_their_slots_and_semantics()
    {
        int actionClicks = 0;
        var h = Harness(() => new SidebarProvider
        {
            Children =
            [
                new Sidebar
                {
                    Collapsible = SidebarCollapsible.None,
                    Children =
                    [
                        new SidebarHeader
                        {
                            Children = [new SidebarInput { Placeholder = "Search", Value = "query" }],
                        },
                        new SidebarContent
                        {
                            Children =
                            [
                                new SidebarGroup
                                {
                                    Children =
                                    [
                                        new SidebarGroupLabel { Children = [new UIText("Application")] },
                                        new SidebarGroupAction
                                        {
                                            OnClick = _ => actionClicks++,
                                            Children = [new UIText("+")],
                                        },
                                        new SidebarGroupContent
                                        {
                                            Children =
                                            [
                                                new SidebarMenu
                                                {
                                                    Children =
                                                    [
                                                        new SidebarMenuItem
                                                        {
                                                            Children =
                                                            [
                                                                new SidebarMenuButton
                                                                {
                                                                    Children = [new UIText("Inbox")],
                                                                },
                                                                new SidebarMenuBadge { Children = [new UIText("3")] },
                                                                new SidebarMenuSub
                                                                {
                                                                    Children =
                                                                    [
                                                                        new SidebarMenuSubItem
                                                                        {
                                                                            Children =
                                                                            [
                                                                                new SidebarMenuSubButton
                                                                                {
                                                                                    Href = "#threads",
                                                                                    IsActive = true,
                                                                                    Children = [new UIText("Threads")],
                                                                                },
                                                                            ],
                                                                        },
                                                                    ],
                                                                },
                                                            ],
                                                        },
                                                        new SidebarMenuItem
                                                        {
                                                            Children = [new SidebarMenuSkeleton { ShowIcon = true }],
                                                        },
                                                    ],
                                                },
                                            ],
                                        },
                                    ],
                                },
                                new SidebarSeparator(),
                            ],
                        },
                    ],
                },
            ],
        });

        var input = SlotOrThrow(h, "sidebar-input");
        Assert.Equal("input", A(input, "data-sidebar"));

        var groupAction = SlotOrThrow(h, "sidebar-group-action");
        Assert.Equal("group-action", A(groupAction, "data-sidebar"));
        h.Click(groupAction);
        Assert.Equal(1, actionClicks);

        var menu = SlotOrThrow(h, "sidebar-menu");
        Assert.Equal("ul", menu.Tag);
        var menuItem = SlotOrThrow(h, "sidebar-menu-item");
        Assert.Equal("li", menuItem.Tag);

        var sub = SlotOrThrow(h, "sidebar-menu-sub");
        Assert.Equal("ul", sub.Tag);
        var subItem = SlotOrThrow(h, "sidebar-menu-sub-item");
        Assert.Equal("li", subItem.Tag);

        var subButton = SlotOrThrow(h, "sidebar-menu-sub-button");
        Assert.Equal(Role.Link, subButton.Role);
        Assert.Equal("#threads", A(subButton, "href"));
        Assert.Equal("true", A(subButton, "data-active"));
        Assert.Equal("md", A(subButton, "data-size"));

        Assert.Equal("3", SlotOrThrow(h, "sidebar-menu-badge").TextContent.Trim());
        Assert.NotNull(Slot(h, "sidebar-menu-skeleton"));
        Assert.Equal("separator", A(SlotOrThrow(h, "sidebar-separator"), "data-sidebar"));
    }

    [Fact]
    public void SidebarInset_is_a_main_landmark()
    {
        var h = Harness(() => new SidebarProvider
        {
            Children =
            [
                new Sidebar { Collapsible = SidebarCollapsible.None },
                new SidebarInset { Children = [new UIText("Content")] },
            ],
        });

        Assert.Equal("main", SlotOrThrow(h, "sidebar-inset").Tag);
    }

    // ------------------------------------------------------------ Automatic mobile detection

    static Node TriggeredShell(Action<SidebarProvider>? configure = null)
    {
        var provider = new SidebarProvider
        {
            Children =
            [
                new SidebarTrigger(),
                Menu(new SidebarMenuButton { Children = [new UIText("Inbox")] }),
            ],
        };
        configure?.Invoke(provider);
        return provider;
    }

    [Fact]
    public void Viewport_width_decides_the_effective_mobile_state()
    {
        var narrow = Harness(() => TriggeredShell(), width: 500);
        // Mobile uses OpenMobile (default false): the desktop Open default does not leak through.
        Assert.Equal("false", A(SlotOrThrow(narrow, "sidebar-trigger"), "aria-expanded"));
        Assert.Null(Slot(narrow, "sidebar"));

        narrow.Click(SlotOrThrow(narrow, "sidebar-trigger"));
        Assert.Equal("true", A(SlotOrThrow(narrow, "sidebar"), "data-mobile"));

        var wide = Harness(() => TriggeredShell(), width: 1024);
        var trigger = SlotOrThrow(wide, "sidebar-trigger");
        Assert.Equal("true", A(trigger, "aria-expanded"));
        var sidebar = SlotOrThrow(wide, "sidebar");
        Assert.Equal("expanded", A(sidebar, "data-state"));
        Assert.Null(A(sidebar, "data-mobile"));
    }

    [Fact]
    public void Resizing_across_the_breakpoint_after_mount_updates_the_mobile_state()
    {
        var h = Harness(() => TriggeredShell(), width: 1024);
        Assert.Equal("true", A(SlotOrThrow(h, "sidebar-trigger"), "aria-expanded"));

        h.Pipeline.SetViewport(new Size(500, 480), 1);
        h.Frame();

        var trigger = SlotOrThrow(h, "sidebar-trigger");
        Assert.Equal("false", A(trigger, "aria-expanded"));
        h.Click(trigger);
        Assert.Equal("true", A(SlotOrThrow(h, "sidebar"), "data-mobile"));

        h.Pipeline.SetViewport(new Size(1024, 480), 1);
        h.Frame();

        Assert.Empty(h.FindAll(e => e.Role == Role.Dialog && e.IsVisible));
        Assert.Equal("true", A(SlotOrThrow(h, "sidebar-trigger"), "aria-expanded"));
        Assert.Equal("expanded", A(SlotOrThrow(h, "sidebar"), "data-state"));
    }

    [Fact]
    public void IsMobile_is_an_explicit_override_on_a_wide_viewport()
    {
        var h = Harness(() => TriggeredShell(p => p.IsMobile = true), width: 1024);

        var trigger = SlotOrThrow(h, "sidebar-trigger");
        Assert.Equal("false", A(trigger, "aria-expanded"));
        h.Click(trigger);
        Assert.Equal("true", A(SlotOrThrow(h, "sidebar"), "data-mobile"));
    }

    [Fact]
    public void AutoDetectMobile_false_respects_the_explicit_desktop_state_on_a_narrow_viewport()
    {
        var h = Harness(() => TriggeredShell(p => p.AutoDetectMobile = false), width: 500);

        var trigger = SlotOrThrow(h, "sidebar-trigger");
        Assert.Equal("true", A(trigger, "aria-expanded"));
        Assert.Equal("expanded", A(SlotOrThrow(h, "sidebar"), "data-state"));

        h.Click(trigger);
        Assert.Equal("collapsed", A(SlotOrThrow(h, "sidebar"), "data-state"));
        Assert.Empty(h.FindAll(e => e.Role == Role.Dialog && e.IsVisible));
    }
}
