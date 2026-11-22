using AwAUI.Shadcn;
using AwAUI.UI;
using Label = AwAUI.Shadcn.Label;
using PopperAlign = AwAUI.Controls.Primitives.PopperAlign;
using ShadcnInput = AwAUI.Shadcn.Input;
using Side = AwAUI.Controls.Primitives.Side;
using Text = AwAUI.UI.Text;

namespace ShadcnGallery;

/// <summary>
/// Overlay component pages: alert dialog, dialog, drawer, hover card, popover, sheet, and tooltip.
/// Every trigger is uncontrolled so the examples open by click or hover without page state.
/// </summary>
public static class OverlayPages
{
    const string DialogCode = """
        <Dialog>
            <DialogTrigger AsChild="true">
                <Button Variant="ButtonVariant.Outline">Edit profile</Button>
            </DialogTrigger>
            <DialogContent>
                <DialogHeader>
                    <DialogTitle>Edit profile</DialogTitle>
                    <DialogDescription>Make changes to your profile here.</DialogDescription>
                </DialogHeader>
                <Field>
                    <Label For="profile-name">Name</Label>
                    <Input Id="profile-name" Placeholder="Alex Rivera" />
                </Field>
                <DialogFooter>
                    <DialogClose AsChild="true">
                        <Button Variant="ButtonVariant.Outline">Cancel</Button>
                    </DialogClose>
                    <Button>Save changes</Button>
                </DialogFooter>
            </DialogContent>
        </Dialog>
        """;

    const string PopoverCode = """
        <Popover>
            <PopoverTrigger AsChild="true">
                <Button Variant="ButtonVariant.Outline">Open popover</Button>
            </PopoverTrigger>
            <PopoverContent>
                <PopoverHeader>
                    <PopoverTitle>Dimensions</PopoverTitle>
                    <PopoverDescription>Set the dimensions for the layer.</PopoverDescription>
                </PopoverHeader>
                <Field>
                    <Label For="popover-width">Width</Label>
                    <Input Id="popover-width" Placeholder="100%" />
                </Field>
                <Field>
                    <Label For="popover-height">Height</Label>
                    <Input Id="popover-height" Placeholder="25px" />
                </Field>
                <PopoverClose AsChild="true">
                    <Button>Set dimensions</Button>
                </PopoverClose>
            </PopoverContent>
        </Popover>
        """;

    public static void Register()
    {
        DocsCatalog.Add("alert-dialog", "Alert Dialog",
            DocsExample.Of("Destructive confirmation", AlertDialogDelete),
            DocsExample.Of("Small", AlertDialogSmall));

        DocsCatalog.Add("dialog", "Dialog",
            DocsExample.Of("Edit profile", DialogCode, DialogEditProfile),
            DocsExample.Of("Share link", DialogShareLink));

        DocsCatalog.Add("drawer", "Drawer",
            DocsExample.Of("Default", DrawerDefault),
            DocsExample.Of("Scrollable content", DrawerScrollable),
            DocsExample.Of("Right edge", DrawerRight));

        DocsCatalog.Add("hover-card", "Hover Card",
            DocsExample.Of("User preview", HoverCardUser),
            DocsExample.Of("Repository preview", HoverCardRepository));

        DocsCatalog.Add("popover", "Popover",
            DocsExample.Of("Dimensions form", PopoverCode, PopoverForm),
            DocsExample.Of("Placement", PopoverPlacement));

        DocsCatalog.Add("sheet", "Sheet",
            DocsExample.Of("Right sheet", SheetRight),
            DocsExample.Of("Left sheet", SheetLeft),
            DocsExample.Of("Top sheet", SheetTop));

        DocsCatalog.Add("tooltip", "Tooltip",
            DocsExample.Of("Basic", TooltipBasic),
            DocsExample.Of("Sides", TooltipSides));
    }

    // ---------------------------------------------------------------- Alert Dialog

    static Node AlertDialogDelete() => DocsUi.Stack(
        DocsUi.Row(
            new AlertDialog
            {
                Children =
                [
                    new AlertDialogTrigger
                    {
                        AsChild = true,
                        Children = [DocsUi.Action("Delete project", null, ButtonVariant.Destructive)],
                    },
                    new AlertDialogContent
                    {
                        Children =
                        [
                            new AlertDialogHeader
                            {
                                Children =
                                [
                                    new AlertDialogTitle { Children = [new Text("Delete project?")] },
                                    new AlertDialogDescription
                                    {
                                        Children =
                                        [
                                            new Text("This permanently removes the project, its files, and every member's access. This action cannot be undone."),
                                        ],
                                    },
                                ],
                            },
                            new AlertDialogFooter
                            {
                                Children =
                                [
                                    new AlertDialogCancel { Children = [new Text("Cancel")] },
                                    new AlertDialogAction
                                    {
                                        Variant = ButtonVariant.Destructive,
                                        Children = [new Text("Delete project")],
                                    },
                                ],
                            },
                        ],
                    },
                ],
            }),
        DocsUi.Muted("The cancel action receives focus first, so the safe choice is one keypress away."));

    static Node AlertDialogSmall() => DocsUi.Row(
        new AlertDialog
        {
            Children =
            [
                new AlertDialogTrigger
                {
                    AsChild = true,
                    Children = [DocsUi.Action("Leave workspace")],
                },
                new AlertDialogContent
                {
                    Size = AlertDialogSize.Sm,
                    Children =
                    [
                        new AlertDialogHeader
                        {
                            Children =
                            [
                                new AlertDialogTitle { Children = [new Text("Leave workspace?")] },
                                new AlertDialogDescription
                                {
                                    Children = [new Text("You will lose access to every project in this workspace.")],
                                },
                            ],
                        },
                        new AlertDialogFooter
                        {
                            Children =
                            [
                                new AlertDialogCancel { Children = [new Text("Stay")] },
                                new AlertDialogAction
                                {
                                    Variant = ButtonVariant.Destructive,
                                    Children = [new Text("Leave")],
                                },
                            ],
                        },
                    ],
                },
            ],
        });

    // ---------------------------------------------------------------- Dialog

    static Node DialogEditProfile() => DocsUi.Row(
        new Dialog
        {
            Children =
            [
                new DialogTrigger { AsChild = true, Children = [DocsUi.Action("Edit profile")] },
                new DialogContent
                {
                    Children =
                    [
                        new DialogHeader
                        {
                            Children =
                            [
                                new DialogTitle { Children = [new Text("Edit profile")] },
                                new DialogDescription
                                {
                                    Children = [new Text("Make changes to your profile here. Select Save when you are done.")],
                                },
                            ],
                        },
                        new Div
                        {
                            Class = "demo-stack",
                            Children =
                            [
                                DocsUi.Field(
                                    new Label { For = "overlay-dialog-name", Children = [new Text("Name")] },
                                    new ShadcnInput { Id = "overlay-dialog-name", Placeholder = "Alex Rivera" }),
                                DocsUi.Field(
                                    new Label { For = "overlay-dialog-username", Children = [new Text("Username")] },
                                    new ShadcnInput { Id = "overlay-dialog-username", Placeholder = "@alexrivera" }),
                            ],
                        },
                        new DialogFooter
                        {
                            Children =
                            [
                                new DialogClose { AsChild = true, Children = [DocsUi.Action("Cancel")] },
                                DocsUi.Action("Save changes", null, ButtonVariant.Default),
                            ],
                        },
                    ],
                },
            ],
        });

    static Node DialogShareLink() => DocsUi.Row(
        new Dialog
        {
            Children =
            [
                new DialogTrigger { AsChild = true, Children = [DocsUi.Action("Share link")] },
                new DialogContent
                {
                    Children =
                    [
                        new DialogHeader
                        {
                            Children =
                            [
                                new DialogTitle { Children = [new Text("Share this project")] },
                                new DialogDescription
                                {
                                    Children = [new Text("Anyone with the link can view the project without joining.")],
                                },
                            ],
                        },
                        DocsUi.Field(
                            new Label { For = "overlay-dialog-share", Children = [new Text("Link")] },
                            new ShadcnInput
                            {
                                Id = "overlay-dialog-share",
                                Value = "https://awaui.dev/p/design-system",
                                ReadOnly = true,
                            }),
                        DocsUi.Note("Link access can be revoked at any time from project settings."),
                        new DialogFooter
                        {
                            Children =
                            [
                                new DialogClose { AsChild = true, Children = [DocsUi.Action("Done")] },
                                DocsUi.Action("Copy link", null, ButtonVariant.Default),
                            ],
                        },
                    ],
                },
            ],
        });

    // ---------------------------------------------------------------- Drawer

    static Node DrawerDefault() => DocsUi.Row(
        new Drawer
        {
            Children =
            [
                new DrawerTrigger { AsChild = true, Children = [DocsUi.Action("Open drawer")] },
                new DrawerContent
                {
                    Children =
                    [
                        new DrawerHeader
                        {
                            Children =
                            [
                                new DrawerTitle { Children = [new Text("Set a goal")] },
                                new DrawerDescription
                                {
                                    Children = [new Text("Choose a target and we will track your progress.")],
                                },
                            ],
                        },
                        new Div
                        {
                            Class = "demo-stack",
                            Children =
                            [
                                DocsUi.Field(
                                    new Label { For = "overlay-drawer-goal", Children = [new Text("Goal")] },
                                    new ShadcnInput { Id = "overlay-drawer-goal", Placeholder = "Read 12 books" }),
                                DocsUi.Note("Drag the handle down to dismiss the drawer."),
                            ],
                        },
                        new DrawerFooter
                        {
                            Children =
                            [
                                DocsUi.Action("Save goal", null, ButtonVariant.Default),
                                new DrawerClose { AsChild = true, Children = [DocsUi.Action("Cancel")] },
                            ],
                        },
                    ],
                },
            ],
        });

    static Node DrawerScrollable() => DocsUi.Row(
        new Drawer
        {
            Children =
            [
                new DrawerTrigger
                {
                    AsChild = true,
                    Children = [DocsUi.Action("Open scrollable drawer")],
                },
                new DrawerContent
                {
                    Children =
                    [
                        new DrawerHeader
                        {
                            Children =
                            [
                                new DrawerTitle { Children = [new Text("Activity")] },
                                new DrawerDescription
                                {
                                    Children = [new Text("Recent changes in this workspace.")],
                                },
                            ],
                        },
                        DocsUi.Scroll(
                            DocsUi.Stack(
                                ActivityRow("Project added", "2 minutes ago"),
                                ActivityRow("Notes edited", "18 minutes ago"),
                                ActivityRow("Layout changed", "1 hour ago"),
                                ActivityRow("Review opened", "3 hours ago"),
                                ActivityRow("Workspace renamed", "Yesterday"),
                                ActivityRow("Owner updated", "Yesterday"),
                                ActivityRow("Member invited", "2 days ago"))),
                        new DrawerFooter
                        {
                            Children =
                            [
                                new DrawerClose { AsChild = true, Children = [DocsUi.Action("Close")] },
                            ],
                        },
                    ],
                },
            ],
        });

    static Node DrawerRight() => DocsUi.Row(
        new Drawer
        {
            Children =
            [
                new DrawerTrigger { AsChild = true, Children = [DocsUi.Action("Open right drawer")] },
                new DrawerContent
                {
                    Side = Side.Right,
                    Children =
                    [
                        new DrawerHeader
                        {
                            Children =
                            [
                                new DrawerTitle { Children = [new Text("Filters")] },
                                new DrawerDescription
                                {
                                    Children = [new Text("Only the bottom edge shows the drag handle.")],
                                },
                            ],
                        },
                        DocsUi.Note("The panel follows the right edge and animates in from that side."),
                        new DrawerFooter
                        {
                            Children =
                            [
                                new DrawerClose { AsChild = true, Children = [DocsUi.Action("Close")] },
                            ],
                        },
                    ],
                },
            ],
        });

    static Div ActivityRow(string title, string when) =>
        DocsUi.Split(DocsUi.Label(title), DocsUi.Muted(when));

    // ---------------------------------------------------------------- Hover Card

    static Node HoverCardUser() => DocsUi.Row(
        new HoverCard
        {
            Children =
            [
                new HoverCardTrigger
                {
                    AsChild = true,
                    Children = [DocsUi.Action("@alexrivera", null, ButtonVariant.Link)],
                },
                new HoverCardContent
                {
                    Children =
                    [
                        DocsUi.Row(
                            new Avatar
                            {
                                Children = [new AvatarFallback { Children = [new Text("AR")] }],
                            },
                            DocsUi.Column(
                                DocsUi.Label("Alex Rivera"),
                                DocsUi.Muted("@alexrivera"))),
                        DocsUi.Divider(),
                        DocsUi.Note("Design systems lead. Building accessible components for desktop and web."),
                        DocsUi.Row(
                            new Badge
                            {
                                Variant = BadgeVariant.Secondary,
                                Children = [new Text("Owner")],
                            },
                            new Badge
                            {
                                Variant = BadgeVariant.Outline,
                                Children = [new Text("Design")],
                            }),
                        DocsUi.Muted("Joined March 2021 · 128 followers"),
                    ],
                },
            ],
        });

    static Node HoverCardRepository() => DocsUi.Row(
        new HoverCard
        {
            Children =
            [
                new HoverCardTrigger
                {
                    AsChild = true,
                    Children = [DocsUi.Action("awaui/core", null, ButtonVariant.Link)],
                },
                new HoverCardContent
                {
                    Children =
                    [
                        DocsUi.Column(
                            DocsUi.Label("awaui/core"),
                            DocsUi.Muted("The component runtime behind AwAUI.")),
                        DocsUi.Divider(),
                        DocsUi.Note("Layout, text, paint, and the shadcn component layer, all in one package."),
                        DocsUi.Row(
                            new Badge
                            {
                                Variant = BadgeVariant.Outline,
                                Children = [new Text("C#")],
                            },
                            new Badge
                            {
                                Variant = BadgeVariant.Outline,
                                Children = [new Text("MIT")],
                            }),
                        DocsUi.Muted("Updated 2 hours ago · 2,431 stars"),
                    ],
                },
            ],
        });

    // ---------------------------------------------------------------- Popover

    static Node PopoverForm() => DocsUi.Row(
        new Popover
        {
            Children =
            [
                new PopoverTrigger { AsChild = true, Children = [DocsUi.Action("Open popover")] },
                new PopoverContent
                {
                    Children =
                    [
                        new PopoverHeader
                        {
                            Children =
                            [
                                new PopoverTitle { Children = [new Text("Dimensions")] },
                                new PopoverDescription
                                {
                                    Children = [new Text("Set the dimensions for the layer.")],
                                },
                            ],
                        },
                        new Div
                        {
                            Class = "demo-stack",
                            Children =
                            [
                                DocsUi.Field(
                                    new Label { For = "overlay-popover-width", Children = [new Text("Width")] },
                                    new ShadcnInput { Id = "overlay-popover-width", Placeholder = "100%" }),
                                DocsUi.Field(
                                    new Label { For = "overlay-popover-height", Children = [new Text("Height")] },
                                    new ShadcnInput { Id = "overlay-popover-height", Placeholder = "25px" }),
                                DocsUi.Field(
                                    new Label { For = "overlay-popover-max-width", Children = [new Text("Max. width")] },
                                    new ShadcnInput { Id = "overlay-popover-max-width", Placeholder = "none" }),
                            ],
                        },
                        new Div
                        {
                            Class = "demo-row",
                            Children =
                            [
                                new PopoverClose { AsChild = true, Children = [DocsUi.Action("Close")] },
                                DocsUi.Action("Set dimensions", null, ButtonVariant.Default),
                            ],
                        },
                    ],
                },
            ],
        });

    static Node PopoverPlacement() => DocsUi.Stack(
        DocsUi.Row(
            PlacedPopover("Align start", Side.Bottom, PopperAlign.Start),
            PlacedPopover("Align end", Side.Bottom, PopperAlign.End),
            PlacedPopover("Side right", Side.Right, PopperAlign.Center)),
        DocsUi.Muted("The popover flips to stay inside the window when there is not enough room on the chosen side."));

    static Node PlacedPopover(string label, Side side, PopperAlign align) => new Popover
    {
        Children =
        [
            new PopoverTrigger { AsChild = true, Children = [DocsUi.Action(label)] },
            new PopoverContent
            {
                Side = side,
                Align = align,
                Children =
                [
                    DocsUi.Note(label + " anchors the panel to the " + SideName(side) + " edge of its trigger."),
                ],
            },
        ],
    };

    static string SideName(Side side) => side switch
    {
        Side.Top => "top",
        Side.Right => "right",
        Side.Bottom => "bottom",
        _ => "left",
    };

    // ---------------------------------------------------------------- Sheet

    static Node SheetRight() => DocsUi.Row(
        new Sheet
        {
            Children =
            [
                new SheetTrigger { AsChild = true, Children = [DocsUi.Action("Edit profile")] },
                new SheetContent
                {
                    Side = Side.Right,
                    Children =
                    [
                        new SheetHeader
                        {
                            Children =
                            [
                                new SheetTitle { Children = [new Text("Edit profile")] },
                                new SheetDescription
                                {
                                    Children = [new Text("Make changes to your profile here.")],
                                },
                            ],
                        },
                        new Div
                        {
                            Class = "demo-stack",
                            Children =
                            [
                                DocsUi.Field(
                                    new Label { For = "overlay-sheet-name", Children = [new Text("Name")] },
                                    new ShadcnInput { Id = "overlay-sheet-name", Placeholder = "Alex Rivera" }),
                                DocsUi.Field(
                                    new Label { For = "overlay-sheet-email", Children = [new Text("Email")] },
                                    new ShadcnInput
                                    {
                                        Id = "overlay-sheet-email",
                                        InputType = "email",
                                        Placeholder = "alex@studio.dev",
                                    }),
                            ],
                        },
                        new SheetFooter
                        {
                            Children =
                            [
                                DocsUi.Action("Save changes", null, ButtonVariant.Default),
                                new SheetClose { AsChild = true, Children = [DocsUi.Action("Cancel")] },
                            ],
                        },
                    ],
                },
            ],
        });

    static Node SheetLeft() => DocsUi.Row(
        new Sheet
        {
            Children =
            [
                new SheetTrigger { AsChild = true, Children = [DocsUi.Action("Open menu")] },
                new SheetContent
                {
                    Side = Side.Left,
                    Children =
                    [
                        new SheetHeader
                        {
                            Children =
                            [
                                new SheetTitle { Children = [new Text("Navigation")] },
                                new SheetDescription
                                {
                                    Children = [new Text("Jump to a section of the workspace.")],
                                },
                            ],
                        },
                        new Div
                        {
                            Class = "demo-stack",
                            Children =
                            [
                                MenuRow("Overview", "Projects and recent activity"),
                                DocsUi.Divider(),
                                MenuRow("Team", "Members, roles, and invitations"),
                                MenuRow("Settings", "Workspace preferences"),
                            ],
                        },
                        new SheetFooter
                        {
                            Children =
                            [
                                new SheetClose { AsChild = true, Children = [DocsUi.Action("Close")] },
                            ],
                        },
                    ],
                },
            ],
        });

    static Node SheetTop() => DocsUi.Row(
        new Sheet
        {
            Children =
            [
                new SheetTrigger { AsChild = true, Children = [DocsUi.Action("Open top sheet")] },
                new SheetContent
                {
                    Side = Side.Top,
                    Children =
                    [
                        new SheetHeader
                        {
                            Children =
                            [
                                new SheetTitle { Children = [new Text("Notifications")] },
                                new SheetDescription
                                {
                                    Children = [new Text("Recent activity from every project.")],
                                },
                            ],
                        },
                        DocsUi.Note("The panel slides down from the top edge."),
                        new SheetFooter
                        {
                            Children =
                            [
                                new SheetClose { AsChild = true, Children = [DocsUi.Action("Dismiss")] },
                            ],
                        },
                    ],
                },
            ],
        });

    static Div MenuRow(string title, string description) =>
        DocsUi.Column(DocsUi.Label(title), DocsUi.Muted(description));

    // ---------------------------------------------------------------- Tooltip

    static Node TooltipBasic() => DocsUi.Stack(
        DocsUi.Row(
            Hint("Save", "Save your changes"),
            Hint("Share", "Share this document"),
            Hint("Delete", "Delete permanently", ButtonVariant.Destructive),
            Hint("Keyboard focus", "Shown when the trigger receives keyboard focus")),
        DocsUi.Note("Tooltips appear on hover and on keyboard focus. Press Tab to move through the buttons."));

    static Node TooltipSides() => DocsUi.Row(
        SidedHint("Top", Side.Top),
        SidedHint("Right", Side.Right),
        SidedHint("Bottom", Side.Bottom),
        SidedHint("Left", Side.Left));

    static Node Hint(string label, string description, ButtonVariant variant = ButtonVariant.Outline) =>
        new Tooltip
        {
            Children =
            [
                new TooltipTrigger
                {
                    AsChild = true,
                    Children = [DocsUi.Action(label, null, variant)],
                },
                new TooltipContent { Children = [new Text(description)] },
            ],
        };

    static Node SidedHint(string label, Side side) =>
        new Tooltip
        {
            Children =
            [
                new TooltipTrigger { AsChild = true, Children = [DocsUi.Action(label)] },
                new TooltipContent
                {
                    Side = side,
                    Children = [new Text($"{label} placement")],
                },
            ],
        };
}
