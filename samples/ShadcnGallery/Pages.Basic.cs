using AwAUI.Icons.Lucide;
using AwAUI.Shadcn;
using AwAUI.UI;
using Label = AwAUI.Shadcn.Label;
using Text = AwAUI.UI.Text;
using ShadcnButton = AwAUI.Shadcn.Button;
using ShadcnInput = AwAUI.Shadcn.Input;
using SvgDocument = AwAUI.Svg.SvgDocument;

namespace ShadcnGallery;

/// <summary>The basic component reference: primitives, surfaces, and loading states.</summary>
public static class BasicPages
{
    public static void Register()
    {
        DocsCatalog.Add("alert", "Alert",
            DocsExample.Of("Basic", BasicAlert),
            DocsExample.Of("Destructive", DestructiveAlert),
            DocsExample.Of("With icon and action", AlertWithAction));

        DocsCatalog.Add("aspect-ratio", "Aspect Ratio",
            DocsExample.Of("Basic", AspectBasic),
            DocsExample.Of("Square", AspectSquare),
            DocsExample.Of("Portrait", AspectPortrait));

        DocsCatalog.Add("avatar", "Avatar",
            DocsExample.Of("Basic", AvatarBasic),
            DocsExample.Of("Badge", AvatarBadgeDemo),
            DocsExample.Of("Avatar group", AvatarGroupDemo),
            DocsExample.Of("Avatar group count", AvatarGroupCountDemo),
            DocsExample.Of("Sizes", AvatarSizes),
            DocsExample.Of("Fallback", AvatarFallbackDemo));

        DocsCatalog.Add("badge", "Badge",
            DocsExample.Of("Variants", BadgeVariants),
            DocsExample.Of("With icon", BadgeWithIcon),
            DocsExample.Of("With spinner", BadgeWithSpinner),
            DocsExample.Of("As link", BadgeAsLink));

        DocsCatalog.Add("button", "Button",
            DocsExample.Of("Variants", ButtonVariants),
            DocsExample.Of("Sizes", ButtonSizes),
            DocsExample.Of("States", ButtonStates),
            DocsExample.Of("AsChild", ButtonAsChild));

        DocsCatalog.Add("button-group", "Button Group",
            DocsExample.Of("Basic", ButtonGroupBasic),
            DocsExample.Of("Vertical", ButtonGroupVertical),
            DocsExample.Of("With text and separator", ButtonGroupWithText));

        DocsCatalog.Add("card", "Card",
            DocsExample.Of("Basic", CardBasic),
            DocsExample.Of("Small", CardSmall),
            DocsExample.Of("Grid", CardGrid));

        DocsCatalog.Add("empty", "Empty",
            DocsExample.Of("Basic", EmptyBasic),
            DocsExample.Of("With actions", EmptyWithActions));

        DocsCatalog.Add("item", "Item",
            DocsExample.Of("Basic", ItemBasic),
            DocsExample.Of("Variants", ItemVariants),
            DocsExample.Of("With header and footer", ItemWithHeaderAndFooter));

        DocsCatalog.Add("kbd", "Kbd",
            DocsExample.Of("Basic", KbdBasic),
            DocsExample.Of("Group", KbdGroupDemo),
            DocsExample.Of("Combinations", KbdCombinations),
            DocsExample.Of("In context", KbdInContext));

        DocsCatalog.Add("label", "Label",
            DocsExample.Of("Basic", LabelBasic),
            DocsExample.Of("With checkbox", LabelWithCheckbox),
            DocsExample.Of("Disabled", LabelDisabled));

        DocsCatalog.Add("separator", "Separator",
            DocsExample.Of("Basic", SeparatorBasic),
            DocsExample.Of("Vertical", SeparatorVertical),
            DocsExample.Of("In a menu", SeparatorInMenu));

        DocsCatalog.Add("skeleton", "Skeleton",
            DocsExample.Of("Basic", SkeletonBasic),
            DocsExample.Of("Loading card", SkeletonCard));

        DocsCatalog.Add("spinner", "Spinner",
            DocsExample.Of("Sizes", SpinnerSizes),
            DocsExample.Of("Loading card", SpinnerLoadingCard),
            DocsExample.Of("In a button", SpinnerInButton));

        DocsCatalog.Add("toggle", "Toggle",
            DocsExample.Of("Basic", ToggleBasic),
            DocsExample.Of("Outline", ToggleOutline),
            DocsExample.Of("With text", ToggleWithText),
            DocsExample.Of("Sizes", ToggleSizes),
            DocsExample.Of("Disabled", ToggleDisabled));

        DocsCatalog.Add("toggle-group", "Toggle Group",
            DocsExample.Of("Basic", ToggleGroupBasic),
            DocsExample.Of("Outline", ToggleGroupOutline),
            DocsExample.Of("Single selection", ToggleGroupSingle),
            DocsExample.Of("Sizes", ToggleGroupSizes),
            DocsExample.Of("Spacing", ToggleGroupSpacing),
            DocsExample.Of("Vertical", ToggleGroupVertical),
            DocsExample.Of("Disabled", ToggleGroupDisabled));

        DocsCatalog.Add("typography", "Typography",
            DocsExample.Of("Headings", TypographyHeadings),
            DocsExample.Of("Paragraphs and inline code", TypographyParagraphs),
            DocsExample.Of("Lists and blockquote", TypographyLists));
    }

    // ---------------------------------------------------------------- alert

    static Node BasicAlert() => new Alert
    {
        Children =
        [
            new AlertTitle { Children = [T("Heads up!")] },
            new AlertDescription
            {
                Children = [T("You can add components to your app using the AwAUI CLI.")],
            },
        ],
    };

    static Node DestructiveAlert() => new Alert
    {
        Variant = AlertVariant.Destructive,
        Children =
        [
            new AlertTitle { Children = [T("Cannot delete workspace")] },
            new AlertDescription
            {
                Children = [T("This workspace owns shared projects. Transfer them before deleting it.")],
            },
        ],
    };

    static Node AlertWithAction() => new Alert
    {
        Children =
        [
            Glyph(LucideIcons.Circle),
            new AlertTitle { Children = [T("Update available")] },
            new AlertDescription
            {
                Children =
                [
                    T("A new version of AwAUI is ready to install."),
                    DocsUi.Row(new ShadcnButton
                    {
                        Variant = ButtonVariant.Outline,
                        Size = ButtonSize.Sm,
                        Children = [new TextNode("Update now")],
                    }),
                ],
            },
        ],
    };

    // --------------------------------------------------------- aspect ratio

    static Node AspectBasic() => new AspectRatio
    {
        Ratio = 16d / 9d,
        Children = [MediaBox("16 / 9")],
    };

    static Node AspectSquare() => new AspectRatio
    {
        Ratio = 1,
        Children = [MediaBox("1 / 1")],
    };

    static Node AspectPortrait() => new Div
    {
        Class = "demo-narrow",
        Children =
        [
            new AspectRatio
            {
                Ratio = 9d / 16d,
                Children = [MediaBox("9 / 16")],
            },
        ],
    };

    static Div MediaBox(string label) => new()
    {
        Style = "display:flex;align-items:center;justify-content:center;height:100%;border-radius:8px;" +
                "background:var(--muted);color:var(--muted-foreground);font-size:13px",
        Children = [new TextNode(label)],
    };

    // --------------------------------------------------------------- avatar

    static Node AvatarBasic() => DocsUi.Row(
        NewAvatar("CN", AvatarSize.Default),
        DocsUi.Column(
            new Text { Class = "demo-label", Content = "Chad Nelson" },
            DocsUi.Muted("chad@example.com")));

    static Node AvatarSizes() => DocsUi.Row(
        DocsUi.Column(NewAvatar("SM", AvatarSize.Sm), DocsUi.Muted("sm")),
        DocsUi.Column(NewAvatar("MD", AvatarSize.Default), DocsUi.Muted("default")),
        DocsUi.Column(NewAvatar("LG", AvatarSize.Lg), DocsUi.Muted("lg")));

    static Node AvatarFallbackDemo() => DocsUi.Row(
        NewAvatar("AR", AvatarSize.Default),
        new Avatar
        {
            Children =
            [
                new AvatarFallback { Children = [Glyph(LucideIcons.Circle)] },
            ],
        });

    static Node AvatarBadgeDemo() => DocsUi.Row(
        new Avatar
        {
            Children =
            [
                new AvatarFallback { Children = [new TextNode("CN")] },
                new AvatarBadge { Style = "background:var(--destructive)" },
            ],
        },
        new Avatar
        {
            Children =
            [
                new AvatarFallback { Children = [new TextNode("AR")] },
                new AvatarBadge { Children = [Glyph(LucideIcons.CircleCheck)] },
            ],
        });

    static Node AvatarGroupDemo() => new AvatarGroup
    {
        Children =
        [
            NewAvatar("CN", AvatarSize.Default),
            NewAvatar("AR", AvatarSize.Default),
            NewAvatar("JD", AvatarSize.Default),
        ],
    };

    static Node AvatarGroupCountDemo() => new AvatarGroup
    {
        Children =
        [
            NewAvatar("CN", AvatarSize.Default),
            NewAvatar("AR", AvatarSize.Default),
            new AvatarGroupCount { Children = [new TextNode("+3")] },
        ],
    };

    static Avatar NewAvatar(string initials, AvatarSize size) => new()
    {
        Size = size,
        Children = [new AvatarFallback { Children = [new TextNode(initials)] }],
    };

    // ---------------------------------------------------------------- badge

    static Node BadgeVariants() => DocsUi.Row(
        NewBadge("Default", BadgeVariant.Default),
        NewBadge("Secondary", BadgeVariant.Secondary),
        NewBadge("Destructive", BadgeVariant.Destructive),
        NewBadge("Outline", BadgeVariant.Outline),
        NewBadge("Ghost", BadgeVariant.Ghost),
        NewBadge("Link", BadgeVariant.Link));

    static Node BadgeWithIcon() => DocsUi.Row(
        new Badge { Children = [Glyph(LucideIcons.Check), new TextNode("Verified")] },
        new Badge
        {
            Variant = BadgeVariant.Outline,
            Children = [Glyph(LucideIcons.Plus), new TextNode("New")],
        },
        new Badge
        {
            Variant = BadgeVariant.Secondary,
            Children = [Glyph(LucideIcons.Circle), new TextNode("In review")],
        });

    static Node BadgeWithSpinner() => DocsUi.Row(
        new Badge
        {
            Variant = BadgeVariant.Secondary,
            Children = [new Spinner { Style = "width:12px;height:12px" }, new TextNode("Deploying")],
        },
        new Badge
        {
            Variant = BadgeVariant.Outline,
            Children = [new Spinner { Style = "width:12px;height:12px" }, new TextNode("Syncing")],
        });

    static Node BadgeAsLink() => DocsUi.Row(
        new Badge { Children = [new TextNode("Open issue")] },
        new Badge
        {
            Variant = BadgeVariant.Link,
            Children = [new TextNode("View changelog")],
        });

    static Badge NewBadge(string label, BadgeVariant variant) => new()
    {
        Variant = variant,
        Children = [new TextNode(label)],
    };

    // --------------------------------------------------------------- button

    static Node ButtonVariants() => DocsUi.Row(
        NewButton("Default"),
        NewButton("Destructive", ButtonVariant.Destructive),
        NewButton("Outline", ButtonVariant.Outline),
        NewButton("Secondary", ButtonVariant.Secondary),
        NewButton("Ghost", ButtonVariant.Ghost),
        NewButton("Link", ButtonVariant.Link));

    static Node ButtonSizes() => DocsUi.Column(
        DocsUi.Row(
            NewButton("Extra small", size: ButtonSize.Xs),
            NewButton("Small", size: ButtonSize.Sm),
            NewButton("Default", size: ButtonSize.Default),
            NewButton("Large", size: ButtonSize.Lg)),
        DocsUi.Row(
            IconButton(LucideIcons.Plus, "Add", ButtonSize.IconXs),
            IconButton(LucideIcons.Plus, "Add", ButtonSize.IconSm),
            IconButton(LucideIcons.Plus, "Add", ButtonSize.Icon),
            IconButton(LucideIcons.Plus, "Add", ButtonSize.IconLg)));

    static Node ButtonStates() => DocsUi.Stack(
        DocsUi.Row(
            new ShadcnButton { Disabled = true, Children = [new TextNode("Disabled")] },
            new ShadcnButton
            {
                Disabled = true,
                Variant = ButtonVariant.Outline,
                Children = [new TextNode("Disabled")],
            },
            new ShadcnButton
            {
                Variant = ButtonVariant.Outline,
                Rest = Attribute("aria-invalid", "true"),
                Children = [new TextNode("Invalid")],
            }),
        DocsUi.Note("Pressing any button shifts it down one pixel (active:translate-y-px), except triggers that open a popup."));

    static Node ButtonAsChild() => DocsUi.Stack(
        DocsUi.Row(
            new ShadcnButton
            {
                AsChild = true,
                Variant = ButtonVariant.Outline,
                Children =
                [
                    new Div { Children = [T("Rendered through AsChild")] },
                ],
            }),
        DocsUi.Note("AsChild merges the button's styles, accessibility, and click handling into its single child, so custom links or containers inherit button behavior without nesting a button."));

    static ShadcnButton NewButton(string label,
        ButtonVariant variant = ButtonVariant.Outline,
        ButtonSize size = ButtonSize.Default) => new()
    {
        Variant = variant,
        Size = size,
        Children = [new TextNode(label)],
    };

    static ShadcnButton IconButton(SvgDocument glyph, string label,
        ButtonSize size = ButtonSize.Icon,
        ButtonVariant variant = ButtonVariant.Outline) => new()
    {
        Variant = variant,
        Size = size,
        Rest = Attribute("aria-label", label),
        Children = [Glyph(glyph)],
    };

    // --------------------------------------------------------- button group

    static Node ButtonGroupBasic() => new ButtonGroup
    {
        Children =
        [
            NewButton("Previous"),
            NewButton("Next"),
            NewButton("Save"),
        ],
    };

    static Node ButtonGroupVertical() => new ButtonGroup
    {
        Orientation = ButtonGroupOrientation.Vertical,
        Children =
        [
            NewButton("Top"),
            NewButton("Middle"),
            NewButton("Bottom"),
        ],
    };

    static Node ButtonGroupWithText() => DocsUi.Column(
        new ButtonGroup
        {
            Children =
            [
                new ButtonGroupText { Children = [new TextNode("https://")] },
                NewButton("Copy"),
                new ButtonGroupSeparator(),
                IconButton(LucideIcons.Plus, "Add"),
            ],
        },
        new ButtonGroup
        {
            Children =
            [
                NewButton("Save"),
                new ButtonGroupSeparator(),
                IconButton(LucideIcons.ChevronDown, "More options"),
            ],
        });

    // ----------------------------------------------------------------- card

    static Node CardBasic() => new Card
    {
        Children =
        [
            new CardHeader
            {
                Children =
                [
                    new CardTitle { Children = [T("Team plan")] },
                    new CardDescription { Children = [T("Manage your team and billing in one place.")] },
                    new CardAction
                    {
                        Children = [NewBadge("Active", BadgeVariant.Secondary)],
                    },
                ],
            },
            new CardContent
            {
                Children =
                [
                    T("You are on the Team plan with 12 seats and unlimited projects."),
                    DocsUi.Split(
                        new Text { Class = "demo-label", Content = "Seats used" },
                        DocsUi.Muted("8 of 12")),
                ],
            },
            new CardFooter
            {
                Children =
                [
                    NewButton("Upgrade", ButtonVariant.Default),
                    NewButton("Manage billing"),
                ],
            },
        ],
    };

    static Node CardSmall() => new Card
    {
        Children =
        [
            new CardHeader
            {
                Children =
                [
                    new CardTitle { Children = [T("Small card")] },
                    new CardDescription { Children = [T("A compact card with a single action.")] },
                ],
            },
            new CardFooter
            {
                Children = [NewButton("Open", size: ButtonSize.Sm)],
            },
        ],
    };

    static Node CardGrid() => DocsUi.Grid(
        SimpleCard("Storage", "12.4 GB of 50 GB used", LucideIcons.Circle),
        SimpleCard("Members", "24 people in this workspace", LucideIcons.Check));

    static Card SimpleCard(string title, string body, SvgDocument glyph) => new()
    {
        Children =
        [
            new CardHeader
            {
                Children =
                [
                    DocsUi.Row(Glyph(glyph), new CardTitle { Children = [T(title)] }),
                    new CardDescription { Children = [T(body)] },
                ],
            },
        ],
    };

    // ---------------------------------------------------------------- empty

    static Node EmptyBasic() => new Empty
    {
        Children =
        [
            new EmptyHeader
            {
                Children =
                [
                    new EmptyMedia
                    {
                        Variant = EmptyMediaVariant.Icon,
                        Children = [Glyph(LucideIcons.Search)],
                    },
                    new EmptyTitle { Children = [T("No results found")] },
                    new EmptyDescription
                    {
                        Children = [T("Try adjusting your search or filters to find what you are looking for.")],
                    },
                ],
            },
            new EmptyContent
            {
                Children = [NewButton("Clear filters")],
            },
        ],
    };

    static Node EmptyWithActions() => new Empty
    {
        Children =
        [
            new EmptyHeader
            {
                Children =
                [
                    new EmptyMedia
                    {
                        Variant = EmptyMediaVariant.Icon,
                        Children = [Glyph(LucideIcons.Plus)],
                    },
                    new EmptyTitle { Children = [T("No team members yet")] },
                    new EmptyDescription
                    {
                        Children = [T("Invite people to collaborate on this workspace.")],
                    },
                ],
            },
            new EmptyContent
            {
                Children =
                [
                    DocsUi.Row(
                        NewButton("Invite member", ButtonVariant.Default),
                        NewButton("Learn more")),
                ],
            },
        ],
    };

    // ----------------------------------------------------------------- item

    static Node ItemBasic() => new ItemGroup
    {
        Children =
        [
            NewItem(ItemVariant.Outline, LucideIcons.Circle, "Design system",
                "12 files changed", "Draft"),
            NewItem(ItemVariant.Muted, LucideIcons.Check, "Documentation",
                "Updated yesterday", "Published"),
        ],
    };

    static Node ItemVariants() => new ItemGroup
    {
        Children =
        [
            SimpleItem(ItemVariant.Default, "Default item", "Transparent background"),
            SimpleItem(ItemVariant.Outline, "Outlined item", "Bordered surface"),
            SimpleItem(ItemVariant.Muted, "Muted item", "Tinted background"),
        ],
    };

    static Node ItemWithHeaderAndFooter() => new Item
    {
        Variant = ItemVariant.Outline,
        Children =
        [
            new ItemHeader
            {
                Children =
                [
                    new Text { Class = "demo-label", Content = "Workspace access" },
                    NewBadge("Admin", BadgeVariant.Secondary),
                ],
            },
            new ItemMedia
            {
                Variant = ItemMediaVariant.Icon,
                Children = [Glyph(LucideIcons.Circle)],
            },
            new ItemContent
            {
                Children =
                [
                    new ItemTitle { Children = [T("Alex Rivera")] },
                    new ItemDescription { Children = [T("Can manage members, billing, and settings.")] },
                ],
            },
            new ItemFooter
            {
                Children =
                [
                    DocsUi.Muted("Last active 2 hours ago"),
                    NewButton("Manage", size: ButtonSize.Sm),
                ],
            },
        ],
    };

    static Item NewItem(ItemVariant variant, SvgDocument glyph, string title,
        string description, string badge) => new()
    {
        Variant = variant,
        Children =
        [
            new ItemMedia
            {
                Variant = ItemMediaVariant.Icon,
                Children = [Glyph(glyph)],
            },
            new ItemContent
            {
                Children =
                [
                    new ItemTitle { Children = [new TextNode(title)] },
                    new ItemDescription { Children = [new TextNode(description)] },
                ],
            },
            new ItemActions
            {
                Children =
                [
                    NewBadge(badge, BadgeVariant.Secondary),
                    new ShadcnButton
                    {
                        Variant = ButtonVariant.Ghost,
                        Size = ButtonSize.Sm,
                        Children = [new TextNode("View")],
                    },
                ],
            },
        ],
    };

    static Item SimpleItem(ItemVariant variant, string title, string description) => new()
    {
        Variant = variant,
        Children =
        [
            new ItemMedia
            {
                Variant = ItemMediaVariant.Icon,
                Children = [Glyph(LucideIcons.Circle)],
            },
            new ItemContent
            {
                Children =
                [
                    new ItemTitle { Children = [new TextNode(title)] },
                    new ItemDescription { Children = [new TextNode(description)] },
                ],
            },
        ],
    };

    // ------------------------------------------------------------------ kbd

    static Node KbdBasic() => DocsUi.Row(
        new Kbd { Children = [new TextNode("Ctrl")] },
        new Kbd { Children = [new TextNode("B")] },
        DocsUi.Muted("Bold"));

    static Node KbdGroupDemo() => DocsUi.Row(
        new KbdGroup
        {
            Children =
            [
                new Kbd { Children = [new TextNode("Ctrl")] },
                new Kbd { Children = [new TextNode("B")] },
            ],
        },
        DocsUi.Muted("Bold"),
        new KbdGroup
        {
            Children =
            [
                new Kbd { Children = [new TextNode("⌘")] },
                new Kbd { Children = [new TextNode("K")] },
            ],
        },
        DocsUi.Muted("Quick search"));

    static Node KbdCombinations() => DocsUi.Column(
        DocsUi.Row(
            new Kbd { Children = [new TextNode("Ctrl")] },
            new Kbd { Children = [new TextNode("Shift")] },
            new Kbd { Children = [new TextNode("P")] },
            DocsUi.Muted("Command palette")),
        DocsUi.Row(
            new Kbd { Children = [new TextNode("Cmd")] },
            new Kbd { Children = [new TextNode("K")] },
            DocsUi.Muted("Quick search")),
        DocsUi.Row(
            new Kbd { Children = [new TextNode("Esc")] },
            DocsUi.Muted("Close a popup")));

    static Node KbdInContext() => DocsUi.Panel(
        DocsUi.Split(
            T("Search the docs"),
            DocsUi.Row(
                new Kbd { Children = [new TextNode("Cmd")] },
                new Kbd { Children = [new TextNode("K")] })),
        DocsUi.Divider(),
        DocsUi.Split(T("Toggle theme"), new Kbd { Children = [new TextNode("T")] }),
        DocsUi.Divider(),
        DocsUi.Split(T("Close"), new Kbd { Children = [new TextNode("Esc")] }));

    // ---------------------------------------------------------------- label

    static Node LabelBasic() => DocsUi.Field(
        new Label { For = "basic-label-email", Children = [T("Email")] },
        new ShadcnInput
        {
            Id = "basic-label-email",
            InputType = "email",
            Placeholder = "you@example.com",
        });

    static Node LabelWithCheckbox() => DocsUi.Row(
        new Checkbox { Id = "basic-label-terms", DefaultChecked = true },
        new Label
        {
            For = "basic-label-terms",
            Children = [T("Accept terms and conditions")],
        });

    static Node LabelDisabled() => DocsUi.Row(
        new Checkbox { Id = "basic-label-disabled", Disabled = true },
        new Label
        {
            For = "basic-label-disabled",
            Children = [T("Disabled option")],
        });

    // ------------------------------------------------------------ separator

    static Node SeparatorBasic() => DocsUi.Stack(
        T("A set of beautifully designed components you can copy into your apps."),
        new Separator(),
        DocsUi.Muted("Accessible, customizable, and open source."),
        new Separator(),
        T("Built with the same layout and paint pipeline as the rest of AwAUI."));

    static Node SeparatorVertical() => new Div
    {
        Class = "demo-row",
        Style = "height:28px",
        Children =
        [
            T("Blog"),
            new Separator { Orientation = SeparatorOrientation.Vertical },
            T("Docs"),
            new Separator { Orientation = SeparatorOrientation.Vertical },
            T("Source"),
        ],
    };

    static Node SeparatorInMenu() => new Div
    {
        Class = "demo-column",
        Children =
        [
            T("Settings"),
            new Separator(),
            T("Account"),
            new Separator(),
            T("Log out"),
        ],
    };

    // ------------------------------------------------------------- skeleton

    static Node SkeletonBasic() => DocsUi.Row(
        new Skeleton { Style = "width:48px;height:48px;border-radius:999px" },
        new Div
        {
            Class = "demo-column",
            Children =
            [
                new Skeleton { Style = "width:180px;height:14px" },
                new Skeleton { Style = "width:120px;height:14px" },
            ],
        });

    static Node SkeletonCard() => new Card
    {
        Children =
        [
            new CardHeader
            {
                Children =
                [
                    new Div
                    {
                        Class = "demo-column",
                        Children =
                        [
                            new Skeleton { Style = "width:200px;height:18px" },
                            new Skeleton { Style = "width:140px;height:14px" },
                        ],
                    },
                ],
            },
            new CardContent
            {
                Children =
                [
                    new Div
                    {
                        Class = "demo-column",
                        Children =
                        [
                            new Skeleton { Style = "width:100%;height:12px" },
                            new Skeleton { Style = "width:92%;height:12px" },
                            new Skeleton { Style = "width:64%;height:12px" },
                        ],
                    },
                ],
            },
            new CardFooter
            {
                Children = [new Skeleton { Style = "width:96px;height:32px;border-radius:8px" }],
            },
        ],
    };

    // -------------------------------------------------------------- spinner

    static Node SpinnerSizes() => DocsUi.Row(
        SpinnerSample(16, "sm"),
        SpinnerSample(24, "default"),
        SpinnerSample(32, "lg"));

    static Node SpinnerLoadingCard() => new Card
    {
        Children =
        [
            new CardHeader
            {
                Children =
                [
                    new CardTitle { Children = [T("Preparing your workspace")] },
                    new CardDescription { Children = [T("This should only take a moment.")] },
                ],
            },
            new CardContent
            {
                Children = [DocsUi.Row(new Spinner(), DocsUi.Muted("Syncing files..."))],
            },
        ],
    };

    static Node SpinnerInButton() => DocsUi.Row(
        new ShadcnButton
        {
            Disabled = true,
            Children = [new Spinner { Style = "width:16px;height:16px" }, new TextNode("Please wait")],
        },
        new ShadcnButton
        {
            Variant = ButtonVariant.Outline,
            Disabled = true,
            Children = [new Spinner { Style = "width:16px;height:16px" }, new TextNode("Saving")],
        });

    static Div SpinnerSample(int size, string label) => new()
    {
        Class = "demo-column",
        Style = "align-items:center",
        Children =
        [
            new Spinner { Style = $"width:{size}px;height:{size}px" },
            DocsUi.Muted(label),
        ],
    };

    // ----------------------------------------------------------- typography

    static Node TypographyHeadings() => DocsUi.Column(
        new Text
        {
            Class = "demo-heading",
            Content = "Taxing Laughter: The Joke Tax Chronicles",
            Role = Role.Heading,
            Attributes = { ["aria-level"] = "1" },
        },
        new Text
        {
            Class = "demo-subheading",
            Content = "The Joke Tax",
            Role = Role.Heading,
            Attributes = { ["aria-level"] = "2" },
        },
        new Text { Class = "demo-label", Content = "The People of the Kingdom" },
        DocsUi.Muted("A short hierarchy built from the demo heading and subheading classes."));

    static Node TypographyParagraphs() => DocsUi.Column(
        new Text
        {
            Content = "Once upon a time, in a far-off land, there was a very lazy king who spent all day lounging on his throne.",
        },
        DocsUi.Muted("His subjects were unhappy, and the royal advisors struggled to keep the kingdom running."),
        new Text
        {
            Children =
            [
                "Install the package with ",
                InlineCode("dotnet add package AwAUI.Shadcn"),
                " and import the stylesheet once at startup.",
            ],
        });

    static Node TypographyLists() => DocsUi.Column(
        new Text { Class = "demo-label", Content = "Ordered list" },
        new Div
        {
            Class = "demo-column",
            Children =
            [
                ListRow("1.", "Register the theme"),
                ListRow("2.", "Compose the primitives"),
                ListRow("3.", "Override the tokens"),
            ],
        },
        DocsUi.Divider(),
        new Text { Class = "demo-label", Content = "Unordered list" },
        new Div
        {
            Class = "demo-column",
            Children =
            [
                ListRow("-", "Accessible by default"),
                ListRow("-", "Composable parts"),
                ListRow("-", "Light and dark tokens"),
            ],
        },
        DocsUi.Divider(),
        new Div
        {
            Style = "border-left:3px solid var(--border);padding-left:14px",
            Children =
            [
                new Text
                {
                    Content = "“A complete set of accessible primitives, packaged for AwAUI.”",
                    Style = "font-style:italic",
                },
                DocsUi.Muted("The docs team"),
            ],
        });

    static Div ListRow(string marker, string body) => DocsUi.Row(
        new Text { Class = "demo-muted", Content = marker },
        new Text { Content = body });

    static Span InlineCode(string source) => new(source)
    {
        Style = "font-family:monospace;font-size:0.9em;background:var(--muted);" +
                "border:1px solid var(--border);border-radius:4px;padding:1px 5px",
    };

    // --------------------------------------------------------------- toggle

    static Node ToggleBasic() => DocsUi.Row(
        new Toggle { Children = [T("Bold")] },
        new Toggle { DefaultPressed = true, Children = [T("Italic")] },
        new Toggle { Children = [T("Underline")] });

    static Node ToggleOutline() => DocsUi.Row(
        new Toggle { Variant = ToggleVariant.Outline, Children = [T("Bold")] },
        new Toggle { Variant = ToggleVariant.Outline, DefaultPressed = true, Children = [T("Italic")] },
        new Toggle { Variant = ToggleVariant.Outline, Children = [T("Underline")] });

    static Node ToggleWithText() => DocsUi.Row(
        new Toggle { Variant = ToggleVariant.Outline, Children = [T("Bold")] },
        new Toggle { Variant = ToggleVariant.Outline, DefaultPressed = true, Children = [T("Italic")] },
        new Toggle { Variant = ToggleVariant.Outline, Children = [T("Underline")] });

    static Node ToggleSizes() => DocsUi.Row(
        new Toggle { Size = ToggleSize.Sm, Children = [T("Small")] },
        new Toggle { Size = ToggleSize.Default, Children = [T("Default")] },
        new Toggle { Size = ToggleSize.Lg, Children = [T("Large")] });

    static Node ToggleDisabled() => DocsUi.Row(
        new Toggle { Disabled = true, Children = [T("Disabled")] },
        new Toggle { Disabled = true, DefaultPressed = true, Variant = ToggleVariant.Outline, Children = [T("Disabled on")] });

    // --------------------------------------------------------- toggle group

    static Node ToggleGroupBasic() => new ToggleGroup
    {
        DefaultValue = "center",
        Children =
        [
            new ToggleGroupItem { Value = "left", Children = [T("Left")] },
            new ToggleGroupItem { Value = "center", Children = [T("Center")] },
            new ToggleGroupItem { Value = "right", Children = [T("Right")] },
        ],
    };

    static Node ToggleGroupOutline() => new ToggleGroup
    {
        Variant = ToggleVariant.Outline,
        DefaultValue = "day",
        Children =
        [
            new ToggleGroupItem { Value = "day", Children = [T("Day")] },
            new ToggleGroupItem { Value = "week", Children = [T("Week")] },
            new ToggleGroupItem { Value = "month", Children = [T("Month")] },
        ],
    };

    static Node ToggleGroupSingle() => new ToggleGroup
    {
        Variant = ToggleVariant.Outline,
        DefaultValue = "bold",
        Children =
        [
            new ToggleGroupItem { Value = "bold", Children = [T("Bold")] },
            new ToggleGroupItem { Value = "italic", Children = [T("Italic")] },
            new ToggleGroupItem { Value = "underline", Children = [T("Underline")] },
        ],
    };

    static Node ToggleGroupSizes() => DocsUi.Column(
        new ToggleGroup
        {
            Size = ToggleSize.Sm,
            Variant = ToggleVariant.Outline,
            DefaultValue = "a",
            Children =
            [
                new ToggleGroupItem { Value = "a", Children = [T("A")] },
                new ToggleGroupItem { Value = "b", Children = [T("B")] },
            ],
        },
        new ToggleGroup
        {
            Size = ToggleSize.Default,
            Variant = ToggleVariant.Outline,
            DefaultValue = "a",
            Children =
            [
                new ToggleGroupItem { Value = "a", Children = [T("A")] },
                new ToggleGroupItem { Value = "b", Children = [T("B")] },
            ],
        },
        new ToggleGroup
        {
            Size = ToggleSize.Lg,
            Variant = ToggleVariant.Outline,
            DefaultValue = "a",
            Children =
            [
                new ToggleGroupItem { Value = "a", Children = [T("A")] },
                new ToggleGroupItem { Value = "b", Children = [T("B")] },
            ],
        });

    static Node ToggleGroupSpacing() => new ToggleGroup
    {
        Multiple = true,
        Variant = ToggleVariant.Outline,
        Size = ToggleSize.Sm,
        Spacing = 2,
        DefaultValues = ["star"],
        Children =
        [
            new ToggleGroupItem { Value = "star", Children = [T("Star")] },
            new ToggleGroupItem { Value = "heart", Children = [T("Heart")] },
            new ToggleGroupItem { Value = "bookmark", Children = [T("Bookmark")] },
        ],
    };

    static Node ToggleGroupVertical() => new ToggleGroup
    {
        Orientation = AwAUI.Controls.Primitives.Orientation.Vertical,
        Variant = ToggleVariant.Outline,
        DefaultValue = "top",
        Children =
        [
            new ToggleGroupItem { Value = "top", Children = [T("Top")] },
            new ToggleGroupItem { Value = "middle", Children = [T("Middle")] },
            new ToggleGroupItem { Value = "bottom", Children = [T("Bottom")] },
        ],
    };

    static Node ToggleGroupDisabled() => new ToggleGroup
    {
        Disabled = true,
        Variant = ToggleVariant.Outline,
        DefaultValue = "center",
        Children =
        [
            new ToggleGroupItem { Value = "left", Children = [T("Left")] },
            new ToggleGroupItem { Value = "center", Children = [T("Center")] },
            new ToggleGroupItem { Value = "right", Children = [T("Right")] },
        ],
    };

    // -------------------------------------------------------------- helpers

    static Text T(string content, string? className = null) => new()
    {
        Content = content,
        Class = className,
    };

    static AwAUI.UI.Svg Glyph(SvgDocument document) => new(document);

    static AttributeDictionary Attribute(string name, string value) => new() { [name] = value };
}
