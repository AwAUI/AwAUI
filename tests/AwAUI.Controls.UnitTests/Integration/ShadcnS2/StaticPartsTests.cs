using AwAUI.Controls.UnitTests.Integration;
using AwAUI.Platform;
using AwAUI.UI;
using UIText = AwAUI.UI.Text;

namespace AwAUI.Shadcn.Integration;

/// <summary>
/// Shadcn S2 structural components (breadcrumb, pagination, empty, item, button group, input group, field) verified through the
/// headless end-to-end harness: roles, ARIA state, keyboard activation and pointer focus behavior.
/// </summary>
public class StaticPartsTests
{
    static StaticPartsTests() => ShadcnTheme.Register();

    static UIHarness Harness(Func<Node> render) =>
        new(render, appStyleSheets: true, width: 640, height: 480);

    static string? A(Element e, string name) => e.GetAttribute(name);

    static Element? Slot(UIHarness h, string slot) =>
        h.FindAll(e => A(e, "data-slot") == slot).FirstOrDefault();

    static Element SlotOrThrow(UIHarness h, string slot) =>
        Slot(h, slot) ?? throw new Xunit.Sdk.XunitException($"no element with data-slot=\"{slot}\"");

    // ------------------------------------------------------------ 面包屑

    [Fact]
    public void Breadcrumb_exposes_navigation_list_and_current_page()
    {
        var h = Harness(() => new Breadcrumb
        {
            Children =
            [
                new BreadcrumbList
                {
                    Children =
                    [
                        new BreadcrumbItem
                        {
                            Children =
                            [
                                new BreadcrumbLink { Href = "/docs", Children = [new UIText("Docs")] },
                            ],
                        },
                        new BreadcrumbSeparator(),
                        new BreadcrumbItem
                        {
                            Children = [new BreadcrumbPage { Children = [new UIText("Components")] }],
                        },
                        new BreadcrumbEllipsis(),
                    ],
                },
            ],
        });

        var nav = SlotOrThrow(h, "breadcrumb");
        Assert.Equal(Role.Navigation, nav.Role);
        Assert.Equal("breadcrumb", A(nav, "aria-label"));

        var list = SlotOrThrow(h, "breadcrumb-list");
        Assert.Equal(Role.List, list.Role);

        var item = SlotOrThrow(h, "breadcrumb-item");
        Assert.Equal(Role.ListItem, item.Role);

        var link = SlotOrThrow(h, "breadcrumb-link");
        Assert.Equal(Role.Link, link.Role);
        Assert.Equal("/docs", A(link, "href"));

        var page = SlotOrThrow(h, "breadcrumb-page");
        Assert.Equal(Role.Link, page.Role);
        Assert.Equal("page", A(page, "aria-current"));
        Assert.Equal("true", A(page, "aria-disabled"));
        Assert.Equal("Components", page.TextContent.Trim());

        var separator = SlotOrThrow(h, "breadcrumb-separator");
        Assert.Equal(Role.Presentation, separator.Role);
        Assert.Equal("true", A(separator, "aria-hidden"));

        var ellipsis = SlotOrThrow(h, "breadcrumb-ellipsis");
        Assert.Equal(Role.Presentation, ellipsis.Role);
        Assert.Equal("true", A(ellipsis, "aria-hidden"));
    }

    [Fact]
    public void BreadcrumbLink_activates_with_enter_and_not_space()
    {
        int clicks = 0;
        var h = Harness(() => new Breadcrumb
        {
            Children =
            [
                new BreadcrumbList
                {
                    Children =
                    [
                        new BreadcrumbItem
                        {
                            Children =
                            [
                                new BreadcrumbLink
                                {
                                    Href = "/docs",
                                    OnClick = _ => clicks++,
                                    Children = [new UIText("Docs")],
                                },
                            ],
                        },
                    ],
                },
            ],
        });

        var link = SlotOrThrow(h, "breadcrumb-link");
        Assert.True(link.IsTabStop);

        h.Tab();
        Assert.True(link.IsFocused);
        h.Key(PhysicalKey.Enter, "Enter");
        Assert.Equal(1, clicks);
        h.Key(PhysicalKey.Space, " ");
        Assert.Equal(1, clicks);
    }

    [Fact]
    public void BreadcrumbSeparator_shows_children_when_supplied()
    {
        var h = Harness(() => new Breadcrumb
        {
            Children = [new BreadcrumbSeparator { Children = [new UIText("/")] }],
        });

        var separator = SlotOrThrow(h, "breadcrumb-separator");
        Assert.Equal("/", separator.TextContent.Trim());
        Assert.DoesNotContain(separator.ElementChildren, e => e.Tag == "svg");
    }

    // ------------------------------------------------------------ 分页

    [Fact]
    public void Pagination_marks_the_active_link_and_labels_the_edges()
    {
        var h = Harness(() => new Pagination
        {
            Children =
            [
                new PaginationContent
                {
                    Children =
                    [
                        new PaginationItem { Children = [new PaginationPrevious { Href = "#prev" }] },
                        new PaginationItem { Children = [new PaginationLink { Href = "#1", IsActive = true, Children = [new UIText("1")] }] },
                        new PaginationItem { Children = [new PaginationLink { Href = "#2", Children = [new UIText("2")] }] },
                        new PaginationItem { Children = [new PaginationEllipsis()] },
                        new PaginationItem { Children = [new PaginationNext { Href = "#next" }] },
                    ],
                },
            ],
        });

        var nav = SlotOrThrow(h, "pagination");
        Assert.Equal(Role.Navigation, nav.Role);
        Assert.Equal("pagination", A(nav, "aria-label"));

        Assert.Equal(Role.List, SlotOrThrow(h, "pagination-content").Role);
        Assert.Equal(Role.ListItem, SlotOrThrow(h, "pagination-item").Role);

        var links = h.FindAll(e => A(e, "data-slot") == "pagination-link");
        Assert.Equal(4, links.Count);

        Assert.Equal("Go to previous page", A(links[0], "aria-label"));
        Assert.True(links[0].IsTabStop);

        Assert.Equal("page", A(links[1], "aria-current"));
        Assert.Equal("true", A(links[1], "data-active"));
        Assert.Null(A(links[2], "aria-current"));
        Assert.Equal("false", A(links[2], "data-active"));
        Assert.Equal("Go to next page", A(links[3], "aria-label"));

        var ellipsis = SlotOrThrow(h, "pagination-ellipsis");
        Assert.Equal(Role.Presentation, ellipsis.Role);
        Assert.Equal("true", A(ellipsis, "aria-hidden"));
    }

    [Fact]
    public void PaginationLink_focuses_and_activates_with_enter()
    {
        int clicks = 0;
        var h = Harness(() => new Pagination
        {
            Children =
            [
                new PaginationContent
                {
                    Children =
                    [
                        new PaginationItem
                        {
                            Children =
                            [
                                new PaginationLink
                                {
                                    Href = "#3",
                                    OnClick = _ => clicks++,
                                    Children = [new UIText("3")],
                                },
                            ],
                        },
                    ],
                },
            ],
        });

        var link = SlotOrThrow(h, "pagination-link");
        h.Tab();
        Assert.True(link.IsFocused);
        h.Key(PhysicalKey.Enter, "Enter");
        Assert.Equal(1, clicks);
        h.Key(PhysicalKey.Space, " ");
        Assert.Equal(1, clicks);
    }

    [Fact]
    public void PaginationLink_disabled_is_not_reachable()
    {
        var h = Harness(() => new Pagination
        {
            Children =
            [
                new PaginationContent
                {
                    Children =
                    [
                        new PaginationItem { Children = [new PaginationLink { Disabled = true, Children = [new UIText("1")] }] },
                    ],
                },
            ],
        });

        var link = SlotOrThrow(h, "pagination-link");
        Assert.False(link.IsTabStop);
        Assert.Equal("true", A(link, "aria-disabled"));
    }

    // ------------------------------------------------------------ 空状态

    [Fact]
    public void Empty_composes_header_media_title_description_and_content()
    {
        var h = Harness(() => new Empty
        {
            Children =
            [
                new EmptyHeader
                {
                    Children =
                    [
                        new EmptyMedia { Variant = EmptyMediaVariant.Icon, Children = [new Div()] },
                        new EmptyTitle { Children = [new UIText("No results")] },
                        new EmptyDescription { Children = [new UIText("Try another search.")] },
                    ],
                },
                new EmptyContent { Children = [new Button { Children = [new UIText("Clear")] }] },
            ],
        });

        Assert.Contains("flex", A(SlotOrThrow(h, "empty"), "class")!);
        Assert.NotNull(Slot(h, "empty-header"));
        Assert.NotNull(Slot(h, "empty-title"));
        Assert.NotNull(Slot(h, "empty-description"));
        Assert.NotNull(Slot(h, "empty-content"));

        var media = SlotOrThrow(h, "empty-icon");
        Assert.Equal("icon", A(media, "data-variant"));
        Assert.Contains("size-10", A(media, "class")!);
    }

    // ------------------------------------------------------------ 列表项

    [Fact]
    public void Item_exposes_variant_size_and_media()
    {
        var h = Harness(() => new ItemGroup
        {
            Children =
            [
                new Item
                {
                    Variant = ItemVariant.Muted,
                    Size = ItemSize.Sm,
                    Children =
                    [
                        new ItemMedia { Variant = ItemMediaVariant.Icon },
                        new ItemContent
                        {
                            Children =
                            [
                                new ItemTitle { Children = [new UIText("Title")] },
                                new ItemDescription { Children = [new UIText("Description")] },
                            ],
                        },
                        new ItemActions { Children = [new Button { Children = [new UIText("Open")] }] },
                    ],
                },
            ],
        });

        Assert.Equal(Role.List, SlotOrThrow(h, "item-group").Role);

        var item = SlotOrThrow(h, "item");
        Assert.Equal("muted", A(item, "data-variant"));
        Assert.Equal("sm", A(item, "data-size"));
        Assert.Contains("bg-muted/50", A(item, "class")!);

        Assert.Equal("icon", A(SlotOrThrow(h, "item-media"), "data-variant"));
        Assert.NotNull(Slot(h, "item-content"));
        Assert.NotNull(Slot(h, "item-title"));
        Assert.NotNull(Slot(h, "item-description"));
        Assert.NotNull(Slot(h, "item-actions"));
    }

    [Fact]
    public void Item_as_child_merges_onto_the_child_element()
    {
        var h = Harness(() => new Item
        {
            AsChild = true,
            Variant = ItemVariant.Outline,
            Size = ItemSize.Sm,
            Children = [new Div { Id = "row" }],
        });

        var row = h.ById("row");
        Assert.Equal("item", A(row, "data-slot"));
        Assert.Equal("outline", A(row, "data-variant"));
        Assert.Contains("border-border", A(row, "class")!);
        Assert.Contains("px-4", A(row, "class")!);
    }

    // ------------------------------------------------------------ 按钮组

    [Fact]
    public void ButtonGroup_exposes_orientation_and_separator()
    {
        var h = Harness(() => new ButtonGroup
        {
            Orientation = ButtonGroupOrientation.Vertical,
            Children =
            [
                new Button { Children = [new UIText("One")] },
                new ButtonGroupSeparator(),
                new Button { Children = [new UIText("Two")] },
                new ButtonGroupText { Children = [new UIText("or")] },
            ],
        });

        var group = SlotOrThrow(h, "button-group");
        Assert.Equal(Role.Group, group.Role);
        Assert.Equal("vertical", A(group, "data-orientation"));

        var separator = SlotOrThrow(h, "button-group-separator");
        Assert.Equal(Role.Presentation, separator.Role);
        Assert.Equal("vertical", A(separator, "data-orientation"));
        Assert.Equal("true", A(separator, "aria-hidden"));

        Assert.NotNull(Slot(h, "button-group-text"));
    }

    [Fact]
    public void ButtonGroupText_as_child_merges_onto_the_child_element()
    {
        var h = Harness(() => new ButtonGroupText { AsChild = true, Children = [new Div { Id = "label" }] });

        var label = h.ById("label");
        Assert.Equal("button-group-text", A(label, "data-slot"));
        Assert.Contains("bg-muted", A(label, "class")!);
    }

    // ------------------------------------------------------------ 输入组

    [Fact]
    public void InputGroup_addon_click_focuses_the_control()
    {
        var h = Harness(() => new InputGroup
        {
            Children =
            [
                new InputGroupAddon { Children = [new InputGroupText { Children = [new UIText("https://")] }] },
                new InputGroupInput { Id = "url", Placeholder = "example.com" },
                new InputGroupAddon
                {
                    Align = InputGroupAlign.InlineEnd,
                    Children = [new InputGroupButton { Children = [new UIText("Copy")] }],
                },
            ],
        });

        var group = SlotOrThrow(h, "input-group");
        Assert.Equal(Role.Group, group.Role);

        var addons = h.FindAll(e => A(e, "data-slot") == "input-group-addon");
        Assert.Equal(2, addons.Count);
        Assert.Equal("inline-start", A(addons[0], "data-align"));
        Assert.Equal("inline-end", A(addons[1], "data-align"));

        var input = h.ById("url");
        Assert.Equal("input-group-control", A(input, "data-slot"));

        h.Click(addons[0]);
        Assert.True(input.IsFocused);

        // A nested button handles its own activation; the addon must not steal focus back.
        var button = SlotOrThrow(h, "input-group-button");
        Assert.True(button.IsTabStop);
        Assert.Equal("button", A(button, "type"));
        Assert.Equal("xs", A(button, "data-size"));
        Assert.Equal("ghost", A(button, "data-variant"));
        h.Click(button);
        Assert.True(button.IsFocused);
        Assert.False(input.IsFocused);

        Assert.NotNull(Slot(h, "input-group-text"));
    }

    [Fact]
    public void InputGroup_disabled_state_reaches_the_control()
    {
        var h = Harness(() => new InputGroup
        {
            Disabled = true,
            Children =
            [
                new InputGroupAddon { Children = [new InputGroupText { Children = [new UIText("@")] }] },
                new InputGroupInput { Id = "handle" },
            ],
        });

        Assert.Equal("true", A(SlotOrThrow(h, "input-group"), "data-disabled"));
        Assert.True(h.ById("handle").IsDisabled);
    }

    [Fact]
    public void InputGroupTextarea_uses_the_control_slot()
    {
        var h = Harness(() => new InputGroup
        {
            Children = [new InputGroupTextarea { Id = "bio", Placeholder = "Tell us" }],
        });

        var textarea = h.ById("bio");
        Assert.Equal("textarea", textarea.Tag);
        Assert.Equal("input-group-control", A(textarea, "data-slot"));
    }

    // ------------------------------------------------------------ 字段

    [Fact]
    public void Field_exposes_orientation_invalid_and_legend()
    {
        var h = Harness(() => new FieldSet
        {
            Children =
            [
                new FieldLegend { Children = [new UIText("Profile")] },
                new FieldGroup
                {
                    Children =
                    [
                        new Field
                        {
                            Orientation = FieldOrientation.Horizontal,
                            Invalid = true,
                            Children =
                            [
                                new FieldLabel { For = "name", Children = [new UIText("Name")] },
                                new FieldContent
                                {
                                    Children =
                                    [
                                        new Input { Id = "name" },
                                        new FieldDescription { Children = [new UIText("Shown publicly.")] },
                                    ],
                                },
                            ],
                        },
                    ],
                },
            ],
        });

        Assert.Equal(Role.Group, SlotOrThrow(h, "field-set").Role);
        Assert.Equal("legend", A(SlotOrThrow(h, "field-legend"), "data-variant"));
        Assert.NotNull(Slot(h, "field-group"));
        Assert.NotNull(Slot(h, "field-content"));
        Assert.NotNull(Slot(h, "field-description"));

        var field = SlotOrThrow(h, "field");
        Assert.Equal(Role.Group, field.Role);
        Assert.Equal("horizontal", A(field, "data-orientation"));
        Assert.Equal("true", A(field, "data-invalid"));
    }

    [Fact]
    public void FieldLabel_click_focuses_the_associated_control()
    {
        var h = Harness(() => new Field
        {
            Children =
            [
                new FieldLabel { For = "email", Children = [new UIText("Email")] },
                new FieldContent { Children = [new Input { Id = "email" }] },
            ],
        });

        var label = SlotOrThrow(h, "field-label");
        Assert.Equal("label", label.Tag);
        Assert.Equal("email", A(label, "for"));

        var input = h.ById("email");
        Assert.False(input.IsFocused);
        h.Click(label);
        Assert.True(input.IsFocused);
    }

    [Fact]
    public void FieldError_renders_single_message_and_deduplicates_lists()
    {
        var h = Harness(() => new Div
        {
            Children =
            [
                new FieldError { Errors = ["Required", "Required"] },
                new FieldError { Errors = ["Too short", "Use letters", null] },
                new FieldError(),
            ],
        });

        var errors = h.FindAll(e => A(e, "data-slot") == "field-error");
        Assert.Equal(2, errors.Count);
        Assert.Equal(Role.Alert, errors[0].Role);
        Assert.Equal("Required", errors[0].TextContent.Trim());

        var list = Assert.Single(errors[1].ElementChildren);
        Assert.Equal("ul", list.Tag);
        Assert.Equal(2, list.ElementChildren.Count);
        Assert.Equal("Too short", list.ElementChildren[0].TextContent.Trim());
        Assert.Equal("Use letters", list.ElementChildren[1].TextContent.Trim());
    }

    [Fact]
    public void FieldSeparator_exposes_content_state_and_divider()
    {
        var h = Harness(() => new FieldSeparator { Children = [new UIText("or")] });

        var separator = SlotOrThrow(h, "field-separator");
        Assert.Equal("true", A(separator, "data-content"));
        Assert.NotNull(Slot(h, "field-separator-content"));
        Assert.NotNull(Slot(h, "separator"));
    }

    // ------------------------------------------------------- 头像附件与键盘组

    [Fact]
    public void Avatar_badge_group_and_count_expose_the_official_slots()
    {
        var h = Harness(() => new AvatarGroup
        {
            Children =
            [
                new Avatar
                {
                    Children =
                    [
                        new AvatarFallback { Children = [new UIText("CN")] },
                        new AvatarBadge { Children = [new Div()] },
                    ],
                },
                new Avatar { Children = [new AvatarFallback { Children = [new UIText("AR")] }] },
                new AvatarGroupCount { Children = [new UIText("+3")] },
            ],
        });

        var group = SlotOrThrow(h, "avatar-group");
        Assert.Equal("div", group.Tag);
        Assert.Contains("-space-x-2", A(group, "class")!);

        var badge = SlotOrThrow(h, "avatar-badge");
        Assert.Equal("span", badge.Tag);
        Assert.Contains("ring-2", A(badge, "class")!);
        Assert.Contains("bottom-0", A(badge, "class")!);
        Assert.Contains("[&>svg]:hidden", A(badge, "class")!);

        var count = SlotOrThrow(h, "avatar-group-count");
        Assert.Equal("div", count.Tag);
        Assert.Contains("bg-muted", A(count, "class")!);
        Assert.Contains("ring-background", A(count, "class")!);
    }

    [Fact]
    public void Kbd_group_renders_a_kbd_element_around_keys()
    {
        var h = Harness(() => new KbdGroup
        {
            Children =
            [
                new Kbd { Children = [new UIText("Ctrl")] },
                new Kbd { Children = [new UIText("B")] },
            ],
        });

        var group = SlotOrThrow(h, "kbd-group");
        Assert.Equal("kbd", group.Tag);
        Assert.Contains("inline-flex", A(group, "class")!);
        Assert.Contains("gap-1", A(group, "class")!);
        Assert.Equal(2, h.FindAll(e => A(e, "data-slot") == "kbd").Count);
    }
}
