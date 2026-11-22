using AwAUI.Shadcn;
using AwAUI.UI;
using Label = AwAUI.Shadcn.Label;
using Text = AwAUI.UI.Text;
using ShadcnButton = AwAUI.Shadcn.Button;

namespace ShadcnGallery;

/// <summary>Documentation pages for navigation components: breadcrumbs, commands, menus, pagination and tabs.</summary>
public static partial class NavigationPages
{
    public static void Register()
    {
        DocsCatalog.Add("breadcrumb", "Breadcrumb",
            DocsExample.Of("Basic", BreadcrumbBasic),
            DocsExample.Of("Custom separator", BreadcrumbCustomSeparator),
            DocsExample.Of("Collapsed", BreadcrumbCollapsed));

        DocsCatalog.Add("command", "Command",
            DocsExample.Of("Basic", CommandBasic),
            DocsExample.Of("Groups and shortcuts", CommandGroupsAndShortcuts));

        DocsCatalog.Add("combobox", "Combobox",
            DocsExample.Of("Basic", ComboboxBasic),
            DocsExample.Of("With a label", ComboboxWithLabel),
            DocsExample.Of("Disabled", ComboboxDisabled),
            DocsExample.Of("With groups", ComboboxGrouped));

        DocsCatalog.Add("context-menu", "Context Menu",
            DocsExample.Of("Basic", ContextMenuBasic),
            DocsExample.Of("Checkboxes, radios, and submenus", ContextMenuRich));

        DocsCatalog.Add("dropdown-menu", "Dropdown Menu",
            DocsExample.Of("Basic", DropdownMenuBasic),
            DocsExample.Of("Checkbox and radio items", DropdownMenuChoices),
            DocsExample.Of("With a submenu", DropdownMenuSubmenu));

        DocsCatalog.Add("menubar", "Menubar",
            DocsExample.Of("Basic", MenubarBasic),
            DocsExample.Of("Submenus and shortcuts", MenubarSubmenus));

        DocsCatalog.Add("navigation-menu", "Navigation Menu",
            DocsExample.Of("Basic", NavigationMenuBasic),
            DocsExample.Of("Active link", NavigationMenuActiveLink),
            DocsExample.Of("Controlled value", () => new NavigationMenuControlledDemo()),
            DocsExample.Of("Shared viewport", NavigationMenuSharedViewport));

        DocsCatalog.Add("pagination", "Pagination",
            DocsExample.Of("Basic", PaginationBasic),
            DocsExample.Of("With ellipsis", PaginationEllipsisExample));

        DocsCatalog.Add("tabs", "Tabs",
            DocsExample.Of("Basic", TabsBasic),
            DocsExample.Of("Line", TabsLine),
            DocsExample.Of("Disabled", TabsDisabled));
    }

    // ---------------------------------------------------------------- Breadcrumb

    static Node BreadcrumbBasic() => DocsUi.Stack(
        new Breadcrumb
        {
            Children =
            [
                new BreadcrumbList
                {
                    Children =
                    [
                        new BreadcrumbItem
                        {
                            Children = [new BreadcrumbLink { Href = "#", Children = [new Text("Home")] }],
                        },
                        new BreadcrumbSeparator(),
                        new BreadcrumbItem
                        {
                            Children = [new BreadcrumbLink { Href = "#", Children = [new Text("Components")] }],
                        },
                        new BreadcrumbSeparator(),
                        new BreadcrumbItem
                        {
                            Children = [new BreadcrumbPage { Children = [new Text("Breadcrumb")] }],
                        },
                    ],
                },
            ],
        });

    static Node BreadcrumbCustomSeparator() => DocsUi.Stack(
        new Breadcrumb
        {
            Children =
            [
                new BreadcrumbList
                {
                    Children =
                    [
                        new BreadcrumbItem
                        {
                            Children = [new BreadcrumbLink { Href = "#", Children = [new Text("Workspace")] }],
                        },
                        new BreadcrumbSeparator { Children = [new Text("/")] },
                        new BreadcrumbItem
                        {
                            Children = [new BreadcrumbLink { Href = "#", Children = [new Text("Projects")] }],
                        },
                        new BreadcrumbSeparator { Children = [new Text("/")] },
                        new BreadcrumbItem
                        {
                            Children = [new BreadcrumbPage { Children = [new Text("Design system")] }],
                        },
                    ],
                },
            ],
        });

    static Node BreadcrumbCollapsed() => DocsUi.Stack(
        new Breadcrumb
        {
            Children =
            [
                new BreadcrumbList
                {
                    Children =
                    [
                        new BreadcrumbItem
                        {
                            Children = [new BreadcrumbLink { Href = "#", Children = [new Text("Workspace")] }],
                        },
                        new BreadcrumbSeparator(),
                        new BreadcrumbItem { Children = [new BreadcrumbEllipsis()] },
                        new BreadcrumbSeparator(),
                        new BreadcrumbItem
                        {
                            Children = [new BreadcrumbLink { Href = "#", Children = [new Text("Settings")] }],
                        },
                        new BreadcrumbSeparator(),
                        new BreadcrumbItem
                        {
                            Children = [new BreadcrumbPage { Children = [new Text("Members")] }],
                        },
                    ],
                },
            ],
        });

    // ---------------------------------------------------------------- Command

    static CommandItem CommandEntry(string label, string value, string? shortcut = null)
    {
        var children = new NodeList { new Text(label) };
        if (shortcut is not null)
            children.Add(new CommandShortcut { Children = [new Text(shortcut)] });
        return new CommandItem { Value = value, Children = children };
    }

    static Node CommandBasic() => DocsUi.Stack(
        new Command
        {
            Class = "demo-medium",
            Children =
            [
                new CommandInput { Placeholder = "Type a command or search..." },
                new CommandList
                {
                    Children =
                    [
                        new CommandGroup
                        {
                            Heading = "Suggestions",
                            Children =
                            [
                                CommandEntry("Calendar", "calendar"),
                                CommandEntry("Search emoji", "emoji"),
                                CommandEntry("Calculator", "calculator"),
                            ],
                        },
                        new CommandEmpty { Children = [new Text("No results found.")] },
                    ],
                },
            ],
        },
        DocsUi.Muted("Typing filters the list. Arrow keys move the highlight and Enter runs the active command."));

    static Node CommandGroupsAndShortcuts() => DocsUi.Stack(
        new Command
        {
            Class = "demo-medium",
            Children =
            [
                new CommandInput { Placeholder = "Search settings and actions..." },
                new CommandList
                {
                    Children =
                    [
                        new CommandGroup
                        {
                            Heading = "Suggestions",
                            Children =
                            [
                                CommandEntry("Calendar", "calendar", "⌘C"),
                                CommandEntry("Search emoji", "emoji", "⌘E"),
                                CommandEntry("Calculator", "calculator"),
                            ],
                        },
                        new CommandSeparator(),
                        new CommandGroup
                        {
                            Heading = "Settings",
                            Children =
                            [
                                CommandEntry("Profile", "profile", "⌘P"),
                                CommandEntry("Billing", "billing"),
                                CommandEntry("Settings", "settings", "⌘,"),
                            ],
                        },
                        new CommandEmpty { Children = [new Text("No results found.")] },
                    ],
                },
            ],
        },
        DocsUi.Note("Groups disappear when none of their items match the query."));

    // ---------------------------------------------------------------- Combobox

    static CommandItem FrameworkItem(string label, string value) =>
        new() { Value = value, Children = [new Text(label)] };

    static Combobox FrameworkCombobox(string? id = null) => new()
    {
        Id = id,
        Placeholder = "Select a framework",
        Children =
        [
            FrameworkItem("Next.js", "next"),
            FrameworkItem("SvelteKit", "sveltekit"),
            FrameworkItem("Nuxt.js", "nuxt"),
            FrameworkItem("Remix", "remix"),
            FrameworkItem("Astro", "astro"),
        ],
    };

    static Node ComboboxBasic() => DocsUi.Stack(
        new Div { Class = "demo-medium", Children = [FrameworkCombobox()] },
        DocsUi.Muted("Focus the input to open the list, or type to filter it."));

    static Node ComboboxWithLabel() => DocsUi.Stack(
        DocsUi.Field(
            new Label { For = "docs-framework-combobox", Children = [new Text("Framework")] },
            FrameworkCombobox("docs-framework-combobox"),
            DocsUi.Muted("The framework used for this project.")));

    static Node ComboboxDisabled() => DocsUi.Stack(
        DocsUi.Field(
            DocsUi.Label("Framework"),
            new Combobox
            {
                Disabled = true,
                DefaultValue = "next",
                Placeholder = "Select a framework",
                Children =
                [
                    FrameworkItem("Next.js", "next"),
                    FrameworkItem("SvelteKit", "sveltekit"),
                    FrameworkItem("Nuxt.js", "nuxt"),
                ],
            },
            DocsUi.Muted("A disabled combobox keeps its selected value but cannot be opened.")));

    static Node ComboboxGrouped() => DocsUi.Stack(
        new Div
        {
            Class = "demo-medium",
            Children =
            [
                new Combobox
                {
                    Placeholder = "Select a framework",
                    Children =
                    [
                        new ComboboxGroup
                        {
                            Label = "Frameworks",
                            Children =
                            [
                                new ComboboxItem { Value = "next", Children = [new Text("Next.js")] },
                                new ComboboxItem { Value = "sveltekit", Children = [new Text("SvelteKit")] },
                                new ComboboxItem { Value = "nuxt", Children = [new Text("Nuxt.js")] },
                            ],
                        },
                        new ComboboxSeparator(),
                        new ComboboxGroup
                        {
                            Label = "Static site generators",
                            Children =
                            [
                                new ComboboxItem { Value = "astro", Children = [new Text("Astro")] },
                                new ComboboxItem { Value = "eleventy", Disabled = true, Children = [new Text("Eleventy (coming soon)")] },
                            ],
                        },
                        new ComboboxEmpty { Text = "No framework found." },
                    ],
                },
            ],
        },
        DocsUi.Muted("Groups and separators disappear when nothing inside them matches; the empty state appears only when the whole list has no match. The disabled item stays visible but cannot be selected."));

    // ---------------------------------------------------------------- Context Menu

    static Div ContextTriggerArea(string title, string hint) => new()
    {
        TabIndex = 0,
        Class = "demo-panel demo-dashed",
        Style = "align-items:center;justify-content:center;min-height:150px;text-align:center;cursor:context-menu",
        Children =
        [
            DocsUi.Label(title),
            DocsUi.Muted(hint),
        ],
    };

    static Node ContextMenuBasic() => DocsUi.Stack(
        new ContextMenu
        {
            Children =
            [
                new ContextMenuTrigger
                {
                    AsChild = true,
                    Children =
                    [
                        ContextTriggerArea("Right-click this area",
                            "The menu opens at the pointer. Focus the area and press Shift+F10 for the keyboard menu."),
                    ],
                },
                new ContextMenuContent
                {
                    Children =
                    [
                        new ContextMenuLabel { Children = [new Text("Actions")] },
                        new ContextMenuSeparator(),
                        new ContextMenuItem
                        {
                            Children =
                            [
                                new Text("Back"),
                                new ContextMenuShortcut { Children = [new Text("⌘[")] },
                            ],
                        },
                        new ContextMenuItem
                        {
                            Children =
                            [
                                new Text("Forward"),
                                new ContextMenuShortcut { Children = [new Text("⌘]")] },
                            ],
                        },
                        new ContextMenuItem { Disabled = true, Children = [new Text("Reload")] },
                        new ContextMenuSeparator(),
                        new ContextMenuItem { Destructive = true, Children = [new Text("Delete")] },
                    ],
                },
            ],
        },
        DocsUi.Note("Also available on touch devices: press and hold the trigger area."));

    static Node ContextMenuRich() => DocsUi.Stack(
        new ContextMenu
        {
            Children =
            [
                new ContextMenuTrigger
                {
                    AsChild = true,
                    Children =
                    [
                        ContextTriggerArea("Right-click for options",
                            "Toggle checkboxes, pick a radio option, or open the submenu with the arrow keys."),
                    ],
                },
                new ContextMenuContent
                {
                    Children =
                    [
                        new ContextMenuCheckboxItem { DefaultChecked = true, Children = [new Text("Show bookmarks bar")] },
                        new ContextMenuCheckboxItem { Children = [new Text("Show full URLs")] },
                        new ContextMenuSeparator(),
                        new ContextMenuRadioGroup
                        {
                            DefaultValue = "bottom",
                            Children =
                            [
                                new ContextMenuRadioItem { Value = "top", Children = [new Text("Top")] },
                                new ContextMenuRadioItem { Value = "bottom", Children = [new Text("Bottom")] },
                            ],
                        },
                        new ContextMenuSeparator(),
                        new ContextMenuSub
                        {
                            Children =
                            [
                                new ContextMenuSubTrigger { Children = [new Text("More tools")] },
                                new ContextMenuSubContent
                                {
                                    Children =
                                    [
                                        new ContextMenuItem { Children = [new Text("Developer tools")] },
                                        new ContextMenuItem { Children = [new Text("Task manager")] },
                                        new ContextMenuItem { Children = [new Text("Extensions")] },
                                    ],
                                },
                            ],
                        },
                    ],
                },
            ],
        });

    // ---------------------------------------------------------------- Dropdown Menu

    static ShadcnButton MenuTrigger(string label) => new()
    {
        Variant = ButtonVariant.Outline,
        Children = [new Text(label)],
    };

    static Node DropdownMenuBasic() => DocsUi.Stack(
        DocsUi.Inline(new DropdownMenu
        {
            Children =
            [
                new DropdownMenuTrigger { AsChild = true, Children = [MenuTrigger("Open menu")] },
                new DropdownMenuContent
                {
                    Children =
                    [
                        new DropdownMenuLabel { Children = [new Text("My account")] },
                        new DropdownMenuSeparator(),
                        new DropdownMenuItem
                        {
                            Children =
                            [
                                new Text("Profile"),
                                new DropdownMenuShortcut { Children = [new Text("⇧⌘P")] },
                            ],
                        },
                        new DropdownMenuItem
                        {
                            Children =
                            [
                                new Text("Billing"),
                                new DropdownMenuShortcut { Children = [new Text("⌘B")] },
                            ],
                        },
                        new DropdownMenuItem
                        {
                            Children =
                            [
                                new Text("Settings"),
                                new DropdownMenuShortcut { Children = [new Text("⌘,")] },
                            ],
                        },
                        new DropdownMenuSeparator(),
                        new DropdownMenuItem { Children = [new Text("Log out")] },
                    ],
                },
            ],
        }),
        DocsUi.Muted("Arrow keys move through the items; Escape closes the menu and restores focus to the trigger."));

    static Node DropdownMenuChoices() => DocsUi.Stack(
        DocsUi.Inline(new DropdownMenu
        {
            Children =
            [
                new DropdownMenuTrigger { AsChild = true, Children = [MenuTrigger("View options")] },
                new DropdownMenuContent
                {
                    Children =
                    [
                        new DropdownMenuLabel { Children = [new Text("Appearance")] },
                        new DropdownMenuCheckboxItem { DefaultChecked = true, Children = [new Text("Show status bar")] },
                        new DropdownMenuCheckboxItem { Children = [new Text("Show activity feed")] },
                        new DropdownMenuSeparator(),
                        new DropdownMenuLabel { Children = [new Text("Panel position")] },
                        new DropdownMenuRadioGroup
                        {
                            DefaultValue = "bottom",
                            Children =
                            [
                                new DropdownMenuRadioItem { Value = "top", Children = [new Text("Top")] },
                                new DropdownMenuRadioItem { Value = "bottom", Children = [new Text("Bottom")] },
                            ],
                        },
                    ],
                },
            ],
        }));

    static Node DropdownMenuSubmenu() => DocsUi.Stack(
        DocsUi.Inline(new DropdownMenu
        {
            Children =
            [
                new DropdownMenuTrigger { AsChild = true, Children = [MenuTrigger("More actions")] },
                new DropdownMenuContent
                {
                    Children =
                    [
                        new DropdownMenuItem { Children = [new Text("Rename")] },
                        new DropdownMenuItem { Children = [new Text("Duplicate")] },
                        new DropdownMenuSeparator(),
                        new DropdownMenuSub
                        {
                            Children =
                            [
                                new DropdownMenuSubTrigger { Children = [new Text("Share")] },
                                new DropdownMenuSubContent
                                {
                                    Children =
                                    [
                                        new DropdownMenuItem { Children = [new Text("Email")] },
                                        new DropdownMenuItem { Children = [new Text("Message")] },
                                        new DropdownMenuItem { Children = [new Text("Copy link")] },
                                    ],
                                },
                            ],
                        },
                        new DropdownMenuSeparator(),
                        new DropdownMenuItem { Destructive = true, Children = [new Text("Delete")] },
                    ],
                },
            ],
        }));

    // ---------------------------------------------------------------- Menubar

    static MenubarTrigger BarTrigger(string label) => new() { Children = [new Text(label)] };

    static Node MenubarBasic() => DocsUi.Stack(
        DocsUi.Inline(new Menubar
        {
            Children =
            [
                new MenubarMenu
                {
                    Children =
                    [
                        BarTrigger("File"),
                        new MenubarContent
                        {
                            Children =
                            [
                                new MenubarLabel { Children = [new Text("Project")] },
                                new MenubarItem
                                {
                                    Children =
                                    [
                                        new Text("New tab"),
                                        new MenubarShortcut { Children = [new Text("⌘T")] },
                                    ],
                                },
                                new MenubarItem
                                {
                                    Children =
                                    [
                                        new Text("New window"),
                                        new MenubarShortcut { Children = [new Text("⌘N")] },
                                    ],
                                },
                                new MenubarSeparator(),
                                new MenubarItem { Disabled = true, Children = [new Text("Share")] },
                                new MenubarSeparator(),
                                new MenubarItem { Destructive = true, Children = [new Text("Delete workspace")] },
                            ],
                        },
                    ],
                },
                new MenubarMenu
                {
                    Children =
                    [
                        BarTrigger("Edit"),
                        new MenubarContent
                        {
                            Children =
                            [
                                new MenubarItem
                                {
                                    Children =
                                    [
                                        new Text("Undo"),
                                        new MenubarShortcut { Children = [new Text("⌘Z")] },
                                    ],
                                },
                                new MenubarItem
                                {
                                    Children =
                                    [
                                        new Text("Redo"),
                                        new MenubarShortcut { Children = [new Text("⇧⌘Z")] },
                                    ],
                                },
                                new MenubarSeparator(),
                                new MenubarItem { Children = [new Text("Cut")] },
                                new MenubarItem { Children = [new Text("Copy")] },
                                new MenubarItem { Children = [new Text("Paste")] },
                                new MenubarSeparator(),
                                new MenubarItem
                                {
                                    Children =
                                    [
                                        new Text("Find"),
                                        new MenubarShortcut { Children = [new Text("⌘F")] },
                                    ],
                                },
                            ],
                        },
                    ],
                },
                new MenubarMenu
                {
                    Children =
                    [
                        BarTrigger("View"),
                        new MenubarContent
                        {
                            Children =
                            [
                                new MenubarCheckboxItem { DefaultChecked = true, Children = [new Text("Show sidebar")] },
                                new MenubarCheckboxItem { Children = [new Text("Show status bar")] },
                                new MenubarSeparator(),
                                new MenubarRadioGroup
                                {
                                    DefaultValue = "comfortable",
                                    Children =
                                    [
                                        new MenubarRadioItem { Value = "compact", Children = [new Text("Compact")] },
                                        new MenubarRadioItem { Value = "comfortable", Children = [new Text("Comfortable")] },
                                    ],
                                },
                            ],
                        },
                    ],
                },
            ],
        }),
        DocsUi.Muted("A menubar is persistent: arrow keys move between menus and the open menu follows focus."));

    static Node MenubarSubmenus() => DocsUi.Stack(
        DocsUi.Inline(new Menubar
        {
            Children =
            [
                new MenubarMenu
                {
                    Children =
                    [
                        BarTrigger("File"),
                        new MenubarContent
                        {
                            Children =
                            [
                                new MenubarItem { Children = [new Text("New")] },
                                new MenubarItem { Children = [new Text("Open")] },
                                new MenubarSeparator(),
                                new MenubarSub
                                {
                                    Children =
                                    [
                                        new MenubarSubTrigger { Children = [new Text("Export")] },
                                        new MenubarSubContent
                                        {
                                            Children =
                                            [
                                                new MenubarItem { Children = [new Text("PDF")] },
                                                new MenubarItem { Children = [new Text("HTML")] },
                                                new MenubarItem { Children = [new Text("Markdown")] },
                                            ],
                                        },
                                    ],
                                },
                            ],
                        },
                    ],
                },
                new MenubarMenu
                {
                    Children =
                    [
                        BarTrigger("Help"),
                        new MenubarContent
                        {
                            Children =
                            [
                                new MenubarItem
                                {
                                    Children =
                                    [
                                        new Text("Documentation"),
                                        new MenubarShortcut { Children = [new Text("⌘?")] },
                                    ],
                                },
                                new MenubarItem { Children = [new Text("Keyboard shortcuts")] },
                                new MenubarSeparator(),
                                new MenubarSub
                                {
                                    Children =
                                    [
                                        new MenubarSubTrigger { Children = [new Text("Contact")] },
                                        new MenubarSubContent
                                        {
                                            Children =
                                            [
                                                new MenubarItem { Children = [new Text("Email support")] },
                                                new MenubarItem { Children = [new Text("Community forum")] },
                                            ],
                                        },
                                    ],
                                },
                            ],
                        },
                    ],
                },
            ],
        }));

    // ---------------------------------------------------------------- Navigation Menu

    static NavigationMenuLink NavLink(string title, string body, string href, bool active = false) => new()
    {
        Href = href,
        Active = active,
        Children =
        [
            new Text { Class = "demo-label", Content = title },
            DocsUi.Muted(body),
        ],
    };

    static Node NavigationMenuBasic() => DocsUi.Stack(
        DocsUi.Inline(new NavigationMenu
        {
            Class = "mx-auto",
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
                                new NavigationMenuTrigger { Children = [new Text("Products")] },
                                new NavigationMenuContent
                                {
                                    Children =
                                    [
                                        NavLink("Analytics", "Track usage and retention", "#analytics"),
                                        NavLink("Reports", "Build and share dashboards", "#reports", active: true),
                                        NavLink("Automation", "Schedule recurring work", "#automation"),
                                    ],
                                },
                            ],
                        },
                        new NavigationMenuItem
                        {
                            Children =
                            [
                                new NavigationMenuTrigger { Children = [new Text("Resources")] },
                                new NavigationMenuContent
                                {
                                    Children =
                                    [
                                        NavLink("Documentation", "Guides and API reference", "#docs"),
                                        NavLink("Changelog", "What shipped this week", "#changelog"),
                                        NavLink("Community", "Ask questions and share work", "#community"),
                                    ],
                                },
                            ],
                        },
                    ],
                },
            ],
        }),
        DocsUi.Muted("Triggers open an anchored panel; moving between triggers swaps the open panel."));

    static Node NavigationMenuActiveLink() => DocsUi.Stack(
        DocsUi.Inline(new NavigationMenu
        {
            Class = "mx-auto",
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
                                new NavigationMenuLink
                                {
                                    Href = "#overview",
                                    Active = true,
                                    Children = [new Text("Overview")],
                                },
                            ],
                        },
                        new NavigationMenuItem
                        {
                            Children =
                            [
                                new NavigationMenuLink
                                {
                                    Href = "#components",
                                    Children = [new Text("Components")],
                                },
                            ],
                        },
                        new NavigationMenuItem
                        {
                            Children =
                            [
                                new NavigationMenuLink
                                {
                                    Href = "#examples",
                                    Children = [new Text("Examples")],
                                },
                            ],
                        },
                    ],
                },
            ],
        }),
        DocsUi.Muted("An active link is exposed with aria-current=\"page\" and keeps the accent background."));

    /// <summary>
    /// Controlled example: the open item lives in app state and is mirrored by the status row.
    /// <c>IsValueControlled</c> keeps the menu controlled even when the value is null (closed).
    /// </summary>
    sealed partial class NavigationMenuControlledDemo : Component
    {
        readonly State<string?> _open = new((string?)null);

        protected override Node Build()
        {
            string openLabel = _open.Value switch
            {
                "products" => "Products",
                "resources" => "Resources",
                _ => "None",
            };

            return DocsUi.Stack(
                DocsUi.Row(DocsUi.Label("Open item"), DocsUi.Muted(openLabel)),
                DocsUi.Inline(new NavigationMenu
                {
                    Class = "mx-auto",
                    IsValueControlled = true,
                    Value = _open.Value,
                    ValueChanged = value => _open.Value = value,
                    // Each trigger renders exactly one chevron; do not add another icon here.
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
                                        new NavigationMenuTrigger { Children = [new Text("Products")] },
                                        new NavigationMenuContent
                                        {
                                            Children =
                                            [
                                                NavLink("Analytics", "Track usage and retention", "#analytics"),
                                                NavLink("Reports", "Build and share dashboards", "#reports"),
                                            ],
                                        },
                                    ],
                                },
                                new NavigationMenuItem
                                {
                                    Value = "resources",
                                    Children =
                                    [
                                        new NavigationMenuTrigger { Children = [new Text("Resources")] },
                                        new NavigationMenuContent
                                        {
                                            Children =
                                            [
                                                NavLink("Documentation", "Guides and API reference", "#docs"),
                                                NavLink("Changelog", "What shipped this week", "#changelog"),
                                            ],
                                        },
                                    ],
                                },
                            ],
                        },
                    ],
                }),
                DocsUi.Note("Value, IsValueControlled and ValueChanged keep the open item controlled: opening a " +
                    "trigger reports that item's Value and dismissing reports null, which closes the menu without " +
                    "leaving controlled mode."));
        }
    }

    static Node NavigationMenuSharedViewport() => DocsUi.Stack(
        DocsUi.Inline(new NavigationMenu
        {
            Class = "mx-auto",
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
                                new NavigationMenuTrigger { Children = [new Text("Products")] },
                                new NavigationMenuContent
                                {
                                    Children =
                                    [
                                        NavLink("Analytics", "Track usage and retention", "#analytics"),
                                        NavLink("Reports", "Build and share dashboards", "#reports"),
                                        NavLink("Automation", "Schedule recurring work", "#automation"),
                                    ],
                                },
                            ],
                        },
                        new NavigationMenuItem
                        {
                            Value = "resources",
                            Children =
                            [
                                new NavigationMenuTrigger { Children = [new Text("Resources")] },
                                new NavigationMenuContent
                                {
                                    Children =
                                    [
                                        NavLink("Documentation", "Guides and API reference", "#docs"),
                                        NavLink("Changelog", "What shipped this week", "#changelog"),
                                    ],
                                },
                            ],
                        },
                        new NavigationMenuItem
                        {
                            Value = "company",
                            Children =
                            [
                                new NavigationMenuTrigger { Children = [new Text("Company")] },
                                new NavigationMenuContent
                                {
                                    Children =
                                    [
                                        NavLink("About", "The team and the mission", "#about"),
                                        NavLink("Careers", "Open roles across the company", "#careers"),
                                    ],
                                },
                            ],
                        },
                    ],
                },
            ],
        }),
        DocsUi.Note("Every trigger renders into one shared viewport: switching triggers moves the content inside " +
            "that single panel and re-anchors it under the active trigger instead of stacking panels. Hover opens " +
            "after DelayDuration (200 ms); moving between triggers within SkipDelayDuration (300 ms) opens the next " +
            "panel immediately, and CloseDelayDuration (150 ms) delays closing when the pointer leaves. The open " +
            "item is the same Value state used by the controlled example, and Dir = Direction.Rtl flips the list " +
            "for right-to-left layouts."));

    // ---------------------------------------------------------------- Pagination

    static Node PaginationBasic() => DocsUi.Stack(
        new Pagination
        {
            Children =
            [
                new PaginationContent
                {
                    Children =
                    [
                        new PaginationItem { Children = [new PaginationPrevious { Href = "#" }] },
                        new PaginationItem { Children = [new PaginationLink { Href = "#", Children = [new Text("1")] }] },
                        new PaginationItem { Children = [new PaginationLink { Href = "#", IsActive = true, Children = [new Text("2")] }] },
                        new PaginationItem { Children = [new PaginationLink { Href = "#", Children = [new Text("3")] }] },
                        new PaginationItem { Children = [new PaginationNext { Href = "#" }] },
                    ],
                },
            ],
        });

    static Node PaginationEllipsisExample() => DocsUi.Stack(
        new Pagination
        {
            Children =
            [
                new PaginationContent
                {
                    Children =
                    [
                        new PaginationItem { Children = [new PaginationPrevious { Href = "#" }] },
                        new PaginationItem { Children = [new PaginationLink { Href = "#", Children = [new Text("1")] }] },
                        new PaginationItem { Children = [new PaginationEllipsis()] },
                        new PaginationItem { Children = [new PaginationLink { Href = "#", Children = [new Text("9")] }] },
                        new PaginationItem { Children = [new PaginationLink { Href = "#", IsActive = true, Children = [new Text("10")] }] },
                        new PaginationItem { Children = [new PaginationLink { Href = "#", Children = [new Text("11")] }] },
                        new PaginationItem { Children = [new PaginationEllipsis()] },
                        new PaginationItem { Children = [new PaginationLink { Href = "#", Children = [new Text("20")] }] },
                        new PaginationItem { Children = [new PaginationNext { Href = "#" }] },
                    ],
                },
            ],
        },
        DocsUi.Muted("The active page is marked with aria-current=\"page\" and an outlined button."));

    // ---------------------------------------------------------------- Tabs

    static Div TabPanel(string title, string body, string action) => DocsUi.Column(
        DocsUi.Label(title),
        DocsUi.Muted(body),
        DocsUi.Row(DocsUi.Action(action), DocsUi.Ghost("Cancel")));

    static Node TabsBasic() => DocsUi.Stack(
        new Tabs
        {
            DefaultValue = "account",
            Children =
            [
                new TabsList
                {
                    Children =
                    [
                        new TabsTrigger { Value = "account", Children = [new Text("Account")] },
                        new TabsTrigger { Value = "password", Children = [new Text("Password")] },
                    ],
                },
                new TabsContent
                {
                    Value = "account",
                    Children =
                    [
                        TabPanel("Account", "Update your display name, email address, and profile details.", "Save changes"),
                    ],
                },
                new TabsContent
                {
                    Value = "password",
                    Children =
                    [
                        TabPanel("Password", "Change your password and review active sessions.", "Update password"),
                    ],
                },
            ],
        });

    static Node TabsLine() => DocsUi.Stack(
        new Tabs
        {
            DefaultValue = "overview",
            Children =
            [
                new TabsList
                {
                    Variant = TabsListVariant.Line,
                    Children =
                    [
                        new TabsTrigger { Value = "overview", Children = [new Text("Overview")] },
                        new TabsTrigger { Value = "activity", Children = [new Text("Activity")] },
                        new TabsTrigger { Value = "settings", Children = [new Text("Settings")] },
                    ],
                },
                new TabsContent
                {
                    Value = "overview",
                    Children =
                    [
                        TabPanel("Overview", "A summary of the workspace and its current state.", "Open report"),
                    ],
                },
                new TabsContent
                {
                    Value = "activity",
                    Children =
                    [
                        TabPanel("Activity", "Recent changes made by you and your teammates.", "View all"),
                    ],
                },
                new TabsContent
                {
                    Value = "settings",
                    Children =
                    [
                        TabPanel("Settings", "Configure the workspace name, visibility, and defaults.", "Edit settings"),
                    ],
                },
            ],
        });

    static Node TabsDisabled() => DocsUi.Stack(
        new Tabs
        {
            DefaultValue = "preview",
            Children =
            [
                new TabsList
                {
                    Children =
                    [
                        new TabsTrigger { Value = "preview", Children = [new Text("Preview")] },
                        new TabsTrigger { Value = "code", Children = [new Text("Code")] },
                        new TabsTrigger { Value = "disabled", Disabled = true, Children = [new Text("Disabled")] },
                    ],
                },
                new TabsContent
                {
                    Value = "preview",
                    Children =
                    [
                        TabPanel("Preview", "This tab is selected by default and can be changed.", "Render"),
                    ],
                },
                new TabsContent
                {
                    Value = "code",
                    Children =
                    [
                        TabPanel("Code", "The disabled trigger cannot receive focus or be selected.", "Copy"),
                    ],
                },
            ],
        });
}
