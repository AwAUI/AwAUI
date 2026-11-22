using AwAUI.Controls.Primitives;
using AwAUI.Icons.Lucide;
using AwAUI.Layout;
using AwAUI.Shadcn;
using AwAUI.UI;
using ShadcnButton = AwAUI.Shadcn.Button;
using Text = AwAUI.UI.Text;

namespace ShadcnGallery;

/// <summary>Layout component pages: accordion, collapsible, direction, resizable, scroll area,
/// sidebar, table, data table, and carousel.</summary>
public static partial class LayoutPages
{
    public static void Register()
    {
        DocsCatalog.Add("accordion", "Accordion",
            DocsExample.Of("Basic", AccordionBasic),
            DocsExample.Of("Multiple", AccordionMultiple),
            DocsExample.Of("Disabled item", AccordionDisabled));

        DocsCatalog.Add("collapsible", "Collapsible",
            DocsExample.Of("Basic", CollapsibleBasic),
            DocsExample.Of("Open by default", CollapsibleOpen));

        DocsCatalog.Add("direction", "Direction",
            DocsExample.Of("Right to left", DirectionRtl),
            DocsExample.Of("LTR and RTL", DirectionComparison));

        DocsCatalog.Add("resizable", "Resizable",
            DocsExample.Of("Horizontal", ResizableHorizontal),
            DocsExample.Of("Vertical", ResizableVertical));

        DocsCatalog.Add("scroll-area", "Scroll Area",
            DocsExample.Of("Vertical", ScrollAreaVertical),
            DocsExample.Of("Horizontal", ScrollAreaHorizontal));

        DocsCatalog.Add("sidebar", "Sidebar",
            DocsExample.Of("Static sidebar", SidebarLeftPreview),
            DocsExample.Of("On the right", SidebarRightPreview));

        DocsCatalog.Add("table", "Table",
            DocsExample.Of("Basic", TableBasic),
            DocsExample.Of("With footer", TableFooterExample),
            DocsExample.Of("With caption", TableCaptionExample),
            DocsExample.Of("Column spans", TableColumnSpanExample));

        DocsCatalog.Add("data-table", "Data Table",
            DocsExample.Of("Basic", DataTableBasic),
            DocsExample.Of("Simple", DataTableSimple),
            DocsExample.Of("Full featured", DataTableFullFeatured));

        DocsCatalog.Add("carousel", "Carousel",
            DocsExample.Of("Basic", CarouselBasic),
            DocsExample.Of("Looping", CarouselLoop));
    }

    // ------------------------------------------------------------------ accordion

    static AccordionItem AccordionEntry(string value, string question, string answer) => new()
    {
        Value = value,
        Children =
        [
            new AccordionTrigger { Children = [new Text(question)] },
            new AccordionContent { Children = [DocsUi.Muted(answer)] },
        ],
    };

    static Node AccordionBasic() => DocsUi.Stack(
        new Accordion
        {
            DefaultValue = "shipping",
            Collapsible = true,
            Children =
            [
                AccordionEntry("shipping", "How do I track an order?",
                    "Every order ships with a tracking link. Open the order confirmation email to follow the parcel until it arrives."),
                AccordionEntry("billing", "Can I change my billing address?",
                    "Billing details can be updated at any time from the workspace settings before the next invoice is issued."),
                AccordionEntry("returns", "What is the return policy?",
                    "Items can be returned within 30 days of delivery, provided they are in their original packaging."),
            ],
        },
        DocsUi.Note("One section stays open at a time; Collapsible lets the open section close again."));

    static Node AccordionMultiple() => DocsUi.Stack(
        new Accordion
        {
            Multiple = true,
            DefaultValues = new[] { "profile", "security" },
            Children =
            [
                AccordionEntry("profile", "Profile",
                    "Name, avatar, and contact details used across the workspace."),
                AccordionEntry("security", "Security",
                    "Two-factor authentication, active sessions, and recovery codes."),
                AccordionEntry("notifications", "Notifications",
                    "Choose which activity sends an email digest to your inbox."),
            ],
        },
        DocsUi.Note("With Multiple set, several sections can remain open at the same time."));

    static Node AccordionDisabled() => DocsUi.Stack(
        new Accordion
        {
            Children =
            [
                AccordionEntry("workspace", "Workspace name",
                    "The name shown in the sidebar and on shared documents."),
                new AccordionItem
                {
                    Value = "domain",
                    Disabled = true,
                    Children =
                    [
                        new AccordionTrigger { Children = [new Text("Custom domain (unavailable)")] },
                        new AccordionContent { Children = [DocsUi.Muted("Upgrade to a paid plan to connect a custom domain.")] },
                    ],
                },
            ],
        },
        DocsUi.Note("A disabled item cannot be expanded and its trigger reports aria-disabled to assistive technology."));

    // ------------------------------------------------------------------ collapsible

    static Node CollapsibleBasic() => DocsUi.Stack(
        new Collapsible
        {
            Class = "flex w-full flex-col gap-2",
            Children =
            [
                new CollapsibleTrigger
                {
                    AsChild = true,
                    Children = [DocsUi.Action("Toggle details")],
                },
                new CollapsibleContent
                {
                    Children =
                    [
                        DocsUi.Panel(
                            DocsUi.Label("Release notes"),
                            DocsUi.Muted("The disclosure panel keeps its height transition and restores focus to the trigger when it closes.")),
                    ],
                },
            ],
        },
        DocsUi.Note("The trigger is a shadcn Button merged through AsChild, so it keeps the button look and behavior."));

    static Node CollapsibleOpen() => DocsUi.Stack(
        new Collapsible
        {
            Class = "flex w-full flex-col gap-2",
            DefaultOpen = true,
            Children =
            [
                new CollapsibleTrigger
                {
                    AsChild = true,
                    Children = [DocsUi.Action("Workspace details", variant: ButtonVariant.Secondary)],
                },
                new CollapsibleContent
                {
                    Children =
                    [
                        DocsUi.Panel(
                            DocsUi.Label("Plan"),
                            DocsUi.Muted("Team plan, billed yearly. Three seats in use, one seat available.")),
                    ],
                },
            ],
        });

    // ------------------------------------------------------------------ direction

    static Div DirectionCard(string number, string title) => new()
    {
        Children =
        [
            new Card
            {
                Children =
                [
                    new CardHeader
                    {
                        Children =
                        [
                            new CardTitle { Children = [new Text(number + " · " + title)] },
                        ],
                    },
                    new CardContent
                    {
                        Children = [DocsUi.Muted("Card content follows the writing direction of its subtree.")],
                    },
                ],
            },
        ],
    };

    static Div DirectionBox(string number) => new()
    {
        Class = "demo-radius",
        Children = [new Text(number)],
    };

    static Node DirectionRtl() => DocsUi.Stack(
        new DirectionProvider
        {
            Dir = Direction.Rtl,
            Children =
            [
                DocsUi.Row(
                    DirectionCard("1", "First"),
                    DirectionCard("2", "Second"),
                    DirectionCard("3", "Third")),
            ],
        },
        DocsUi.Note("Inside DirectionProvider the row lays out from right to left, and the same direction is mirrored by the gallery navigation shell."));

    static Div DirectionPanel(Direction dir, string label) => new()
    {
        Class = "demo-panel",
        Children =
        [
            new DirectionProvider
            {
                Dir = dir,
                Children =
                [
                    DocsUi.Label(label),
                    DocsUi.Row(DirectionBox("A"), DirectionBox("B"), DirectionBox("C")),
                ],
            },
        ],
    };

    static Node DirectionComparison() => DocsUi.Stack(
        DocsUi.Grid(
            DirectionPanel(Direction.Ltr, "Left to right"),
            DirectionPanel(Direction.Rtl, "Right to left")),
        DocsUi.Note("DirectionProvider renders display: contents, so it changes inheritance without adding a layout box."));

    // ------------------------------------------------------------------ resizable

    static ResizablePanel ResizablePane(string title, string body, double size) => new()
    {
        DefaultSize = size,
        MinSize = 20,
        Class = "demo-panel",
        Children =
        [
            DocsUi.Label(title),
            DocsUi.Muted(body),
        ],
    };

    static Node ResizableHorizontal() => DocsUi.Stack(
        new Div
        {
            Style = "height: 220px",
            Children =
            [
                new ResizablePanelGroup
                {
                    Orientation = Orientation.Horizontal,
                    Children =
                    [
                        ResizablePane("Sidebar", "Drag the divider or use the arrow keys to resize.", 30),
                        new ResizableHandle { WithHandle = true },
                        ResizablePane("Content", "Double-click the handle to restore the default split.", 70),
                    ],
                },
            ],
        },
        DocsUi.Note("Sizes are percentages of the group. A double click on the handle resets the layout."));

    static Node ResizableVertical() => DocsUi.Stack(
        new Div
        {
            Style = "height: 260px",
            Children =
            [
                new ResizablePanelGroup
                {
                    Orientation = Orientation.Vertical,
                    Children =
                    [
                        ResizablePane("Preview", "The top pane keeps at least a fifth of the height.", 55),
                        new ResizableHandle(),
                        ResizablePane("Console", "The vertical handle reports its orientation to assistive technology.", 45),
                    ],
                },
            ],
        });

    // ------------------------------------------------------------------ scroll area

    static Div ScrollRow(string title, string meta) => new()
    {
        Class = "demo-split",
        Children = [new Text(title), DocsUi.Muted(meta)],
    };

    static Node ScrollAreaVertical()
    {
        var list = DocsUi.Column();
        for (int i = 1; i <= 14; i++)
            list.Children.Add(ScrollRow("Activity " + i, i + " minute" + (i == 1 ? "" : "s") + " ago"));

        return DocsUi.Stack(
            new ScrollArea
            {
                Rest = new AttributeDictionary
                {
                    ["style"] = "height: 220px; border: 1px solid var(--border); border-radius: 10px",
                    ["aria-label"] = "Recent activity",
                },
                Children = [new Div { Style = "padding: 12px", Children = [list] }],
            },
            DocsUi.Note("The viewport scrolls on its own; the surrounding page keeps its layout."));
    }

    static Div ScrollChip(string label) => new()
    {
        Style = "width: 120px; flex: none",
        Class = "demo-radius",
        Children = [new Text(label)],
    };

    static Node ScrollAreaHorizontal() => DocsUi.Stack(
        new ScrollArea
        {
            Orientation = ScrollAreaOrientation.Horizontal,
            Rest = new AttributeDictionary
            {
                ["style"] = "height: 92px; border: 1px solid var(--border); border-radius: 10px",
                ["aria-label"] = "Horizontal cards",
            },
            Children =
            [
                new Div
                {
                    Style = "display: flex; flex-wrap: nowrap; gap: 8px; padding: 12px",
                    Children =
                    [
                        ScrollChip("Card 1"),
                        ScrollChip("Card 2"),
                        ScrollChip("Card 3"),
                        ScrollChip("Card 4"),
                        ScrollChip("Card 5"),
                        ScrollChip("Card 6"),
                    ],
                },
            ],
        },
        DocsUi.Note("With Orientation set to Horizontal the viewport scrolls sideways and clips the overflow."));

    // ------------------------------------------------------------------ sidebar

    static SidebarMenuItem SidebarEntry(string label, bool active) => new()
    {
        Children =
        [
            new SidebarMenuButton
            {
                IsActive = active,
                Children = [new Text(label)],
            },
        ],
    };

    static Node SidebarMenuArea() => new SidebarContent
    {
        Children =
        [
            new SidebarGroup
            {
                Children =
                [
                    new SidebarGroupLabel { Children = [new Text("Workspace")] },
                    new SidebarGroupContent
                    {
                        Children =
                        [
                            new SidebarMenu
                            {
                                Children =
                                [
                                    SidebarEntry("Inbox", true),
                                    SidebarEntry("Drafts", false),
                                    SidebarEntry("Sent", false),
                                    SidebarEntry("Archive", false),
                                ],
                            },
                        ],
                    },
                ],
            },
        ],
    };

    static Node SidebarPreview(bool right)
    {
        var sidebar = new Sidebar
        {
            Side = right ? SidebarSide.Right : SidebarSide.Left,
            Collapsible = SidebarCollapsible.None,
            Children =
            [
                new SidebarHeader
                {
                    Children =
                    [
                        DocsUi.Label("Acme Inc."),
                        DocsUi.Muted("Workspace"),
                    ],
                },
                SidebarMenuArea(),
                new SidebarFooter { Children = [DocsUi.Muted("Signed in as Avery")] },
            ],
        };

        var inset = new SidebarInset
        {
            Children =
            [
                new Div
                {
                    Style = "padding: 16px",
                    Children =
                    [
                        DocsUi.Column(
                            DocsUi.Label("Overview"),
                            DocsUi.Muted("The inset region fills the remaining width next to the sidebar.")),
                    ],
                },
            ],
        };

        var provider = new SidebarProvider
        {
            Width = "12rem",
            Rest = new AttributeDictionary
            {
                ["style"] = "height: 300px; min-height: 0",
            },
            Children = new NodeList(right
                ? new Node[] { inset, sidebar }
                : new Node[] { sidebar, inset }),
        };

        return new Div
        {
            Class = "demo-panel",
            Children = [provider],
        };
    }

    static Node SidebarLeftPreview() => DocsUi.Stack(
        SidebarPreview(false),
        DocsUi.Note("Collapsible=None renders a static column inside the preview; the provider still owns the shared width tokens and Ctrl/Cmd+B shortcut."));

    static Node SidebarRightPreview() => DocsUi.Stack(
        SidebarPreview(true),
        DocsUi.Note("Set Side=Right to place the sidebar at the trailing edge. The inset keeps filling the rest of the row."));

    // ------------------------------------------------------------------ table

    static Node TableBasic() => DocsUi.Stack(
        new Table
        {
            Columns = "minmax(0, 2fr) minmax(0, 1fr) minmax(0, 1fr)",
            Rest = new AttributeDictionary { ["aria-label"] = "Workspace projects" },
            Children =
            [
                new TableHeader
                {
                    Children =
                    [
                        new TableRow
                        {
                            Children =
                            [
                                new TableHead { Children = [new Text("Project")] },
                                new TableHead { Children = [new Text("Status")] },
                                new TableHead { Children = [new Text("Owner")] },
                            ],
                        },
                    ],
                },
                new TableBody
                {
                    Children =
                    [
                        TableDataRow("Design system", "Active", "Avery"),
                        TableDataRow("Documentation", "Draft", "Sam"),
                        TableDataRow("Release notes", "Active", "Jordan"),
                    ],
                },
            ],
        },
        DocsUi.Note("Table keeps the native table, thead, tbody, tr and td elements so assistive technology sees real " +
                    "table semantics. The layout engine has no table formatting context, so every row shares the grid " +
                    "track list from Columns (a bare count expands to repeat(N, minmax(0, 1fr))) and the container " +
                    "scrolls horizontally when space is tight."));

    static TableRow TableDataRow(string project, string status, string owner) => new()
    {
        Children =
        [
            new TableCell { Children = [new Text(project)] },
            new TableCell { Children = [new Text(status)] },
            new TableCell { Children = [new Text(owner)] },
        ],
    };

    static Node TableFooterExample() => DocsUi.Stack(
        new Table
        {
            Columns = "minmax(0, 2fr) minmax(0, 1fr) minmax(0, 1fr)",
            Rest = new AttributeDictionary { ["aria-label"] = "Team members" },
            Children =
            [
                new TableHeader
                {
                    Children =
                    [
                        new TableRow
                        {
                            Children =
                            [
                                new TableHead { Children = [new Text("Member")] },
                                new TableHead { Children = [new Text("Role")] },
                                new TableHead { Children = [new Text("Seats")] },
                            ],
                        },
                    ],
                },
                new TableBody
                {
                    Children =
                    [
                        TableDataRow("Avery Chen", "Admin", "1"),
                        TableDataRow("Sam Patel", "Editor", "1"),
                    ],
                },
                new TableFooter
                {
                    Children =
                    [
                        new TableRow
                        {
                            Children =
                            [
                                new TableCell { Children = [new Text("Total")] },
                                new TableCell { Children = [new Text("2 members")] },
                                new TableCell { Children = [new Text("2")] },
                            ],
                        },
                    ],
                },
            ],
        },
        DocsUi.Note("TableFooter shares the same grid template and adds a muted summary band under the body."));

    static Node TableCaptionExample() => DocsUi.Stack(
        new Table
        {
            Columns = "minmax(0, 2fr) minmax(0, 1fr) minmax(0, 1fr)",
            Rest = new AttributeDictionary { ["aria-label"] = "Recent invoices" },
            Children =
            [
                new TableCaption { Children = [new Text("Recent invoices from the last 30 days.")] },
                new TableHeader
                {
                    Children =
                    [
                        new TableRow
                        {
                            Children =
                            [
                                new TableHead { Children = [new Text("Invoice")] },
                                new TableHead { Children = [new Text("Status")] },
                                new TableHead { Children = [new Text("Amount")] },
                            ],
                        },
                    ],
                },
                new TableBody
                {
                    Children =
                    [
                        TableDataRow("INV-001", "Paid", "$1,250"),
                        TableDataRow("INV-002", "Pending", "$480"),
                        TableDataRow("INV-003", "Paid", "$2,100"),
                    ],
                },
            ],
        },
        DocsUi.Note("TableCaption keeps the native <caption> element so the table has an accessible name, and the " +
                    "container keeps the actual table element for the same reason. Because the engine has no table " +
                    "formatting context it draws the caption in DOM order (caption-side is class parity only), so put it " +
                    "first in the children to read above the header."));

    static TableRow TableInvoiceRow(string invoice, string status, string method, string amount) => new()
    {
        Children =
        [
            new TableCell { Children = [new Text(invoice)] },
            new TableCell { Children = [new Text(status)] },
            new TableCell { Children = [new Text(method)] },
            new TableCell { Class = "text-end", Children = [new Text(amount)] },
        ],
    };

    static Node TableColumnSpanExample() => DocsUi.Stack(
        new Table
        {
            Columns = "minmax(0, 2fr) 8rem 9rem 6rem",
            Rest = new AttributeDictionary { ["aria-label"] = "Invoices with a total" },
            Children =
            [
                new TableHeader
                {
                    Children =
                    [
                        new TableRow
                        {
                            Children =
                            [
                                new TableHead { Children = [new Text("Invoice")] },
                                new TableHead { Children = [new Text("Status")] },
                                new TableHead { Children = [new Text("Method")] },
                                new TableHead { Class = "justify-end", Children = [new Text("Amount")] },
                            ],
                        },
                    ],
                },
                new TableBody
                {
                    Children =
                    [
                        TableInvoiceRow("INV-001", "Paid", "Card", "$1,250"),
                        TableInvoiceRow("INV-002", "Pending", "Bank transfer", "$480"),
                    ],
                },
                new TableFooter
                {
                    Children =
                    [
                        new TableRow
                        {
                            Children =
                            [
                                new TableCell { ColSpan = 3, Children = [new Text("Total due")] },
                                new TableCell { Class = "text-end", Children = [new Text("$1,730")] },
                            ],
                        },
                    ],
                },
            ],
        },
        DocsUi.Note("Columns sets the shared grid track list: a flexible invoice column followed by fixed status, " +
                    "method and amount tracks. A cell ColSpan becomes grid-column: span N, so the three-cell total lines " +
                    "up under the amount column. RowSpan is forwarded as an attribute for assistive technology only, " +
                    "because each row is an independent grid."));

    // ------------------------------------------------------------------ data table

    sealed record Payment(string Id, string Customer, string Status, int Amount);

    static readonly Payment[] Payments =
    {
        new("INV-001", "Avery Chen", "Paid", 1250),
        new("INV-002", "Sam Patel", "Pending", 480),
        new("INV-003", "Jordan Lee", "Paid", 2100),
        new("INV-004", "Riley Kim", "Overdue", 320),
        new("INV-005", "Morgan Diaz", "Paid", 960),
        new("INV-006", "Casey Wong", "Pending", 740),
        new("INV-007", "Taylor Ng", "Paid", 1580),
        new("INV-008", "Quinn Park", "Overdue", 260),
    };

    static Badge StatusBadge(string status) => new()
    {
        Variant = status switch
        {
            "Paid" => BadgeVariant.Secondary,
            "Pending" => BadgeVariant.Outline,
            _ => BadgeVariant.Destructive,
        },
        Children = [new Text(status)],
    };

    static DataTableColumn<Payment>[] PaymentColumns() => new[]
    {
        new DataTableColumn<Payment>
        {
            Key = "id", Header = "Invoice", Value = payment => payment.Id, Sortable = true, Width = "7rem",
        },
        new DataTableColumn<Payment>
        {
            Key = "customer", Header = "Customer", Value = payment => payment.Customer, Sortable = true,
        },
        new DataTableColumn<Payment>
        {
            Key = "status", Header = "Status", Value = payment => payment.Status,
            Cell = payment => StatusBadge(payment.Status),
        },
        new DataTableColumn<Payment>
        {
            Key = "amount", Header = "Amount", Value = payment => payment.Amount, Sortable = true,
            Align = DataTableColumnAlign.End, Width = "6rem",
        },
    };

    static DataTableColumn<Payment>[] SimpleColumns() => new[]
    {
        new DataTableColumn<Payment>
        {
            Key = "id", Header = "Invoice", Value = payment => payment.Id,
        },
        new DataTableColumn<Payment>
        {
            Key = "customer", Header = "Customer", Value = payment => payment.Customer,
        },
        new DataTableColumn<Payment>
        {
            Key = "amount", Header = "Amount", Value = payment => payment.Amount,
            Align = DataTableColumnAlign.End, Width = "6rem",
        },
    };

    static Node DataTableBasic() => DocsUi.Stack(
        new DataTable<Payment>
        {
            Rows = Payments,
            Columns = PaymentColumns(),
            RowKey = payment => payment.Id,
            PageSize = 5,
            PageSizes = new[] { 5, 10 },
            Label = "Invoices",
            FilterPlaceholder = "Filter invoices...",
        },
        DocsUi.Note("Sortable headers (shift-click adds a secondary sort), a global search filter, selection across every " +
                    "filtered row (the header checkbox selects them all, on every page), and a live pagination summary " +
                    "come from the DataTable model."));

    static Node DataTableSimple() => DocsUi.Stack(
        new DataTable<Payment>
        {
            Rows = Payments,
            Columns = SimpleColumns(),
            RowKey = payment => payment.Id,
            Paginated = false,
            Filterable = false,
            Selectable = false,
            Label = "Invoices",
        },
        DocsUi.Note("Turn off the toolbar, selection, and pagination to render every row as a plain table."));

    static Node DataTableFullFeatured() => new DataTableFullFeaturedDemo();

    /// <summary>
    /// Full-featured data table demo. Column visibility, the per-column status filter, multi-column sorting, and
    /// cross-page selection all live in <see cref="DataTableModel{T}"/>; the toolbar only forwards user intent to it.
    /// </summary>
    sealed partial class DataTableFullFeaturedDemo : Component
    {
        static readonly (string Key, string Label)[] s_toggleableColumns =
        {
            ("id", "Invoice"),
            ("customer", "Customer"),
            ("status", "Status"),
            ("amount", "Amount"),
        };

        readonly DataTableModel<Payment> _model;

        public DataTableFullFeaturedDemo()
        {
            var columns = PaymentColumns();
            foreach (var column in columns)
                if (column.Key == "status")
                    column.FilterFn = DataTableFilterFn.Equals;
            _model = new DataTableModel<Payment>(Payments, columns, payment => payment.Id, pageSize: 5);
        }

        protected override Node Build() => DocsUi.Stack(
            DocsUi.Row(StatusFilter(), ColumnMenu()),
            new DataTable<Payment>
            {
                Model = _model,
                PageSizes = new[] { 5, 10 },
                Label = "Invoices",
                FilterPlaceholder = "Search invoices...",
                Caption = "Full-featured invoice table.",
                SelectAllScope = DataTableSelectionScope.Global,
            },
            DocsUi.Note("Columns toggles visibility through the model, the status filter is a per-column filter, and " +
                        "the filter input is the global search. Shift-click a header to add a secondary sort; the model " +
                        "keeps the precedence. The header checkbox selects every filtered row across pages, so the footer " +
                        "counts selections made on other pages."));

        Select StatusFilter() => new()
        {
            DefaultValue = "",
            ValueChanged = SetStatus,
            Children =
            [
                new SelectTrigger
                {
                    Rest = new AttributeDictionary { ["aria-label"] = "Filter by status", ["style"] = "width:160px" },
                    Children = [new SelectValue { Placeholder = "All statuses" }],
                },
                new SelectContent
                {
                    Children =
                    [
                        new SelectGroup
                        {
                            Children =
                            [
                                new SelectLabel { Children = [new Text("Status")] },
                                new SelectItem { Value = "all", Children = [new Text("All statuses")] },
                                new SelectItem { Value = "Paid", Children = [new Text("Paid")] },
                                new SelectItem { Value = "Pending", Children = [new Text("Pending")] },
                                new SelectItem { Value = "Overdue", Children = [new Text("Overdue")] },
                            ],
                        },
                    ],
                },
            ],
        };

        DropdownMenu ColumnMenu()
        {
            var items = new NodeList
            {
                new DropdownMenuLabel { Children = [new Text("Toggle columns")] },
                new DropdownMenuSeparator(),
            };
            foreach (var (key, label) in s_toggleableColumns)
            {
                string columnKey = key;
                items.Add(new DropdownMenuCheckboxItem
                {
                    DefaultChecked = _model.GetColumnVisibility(columnKey),
                    CheckedChanged = visible => _model.SetColumnVisibility(columnKey, visible),
                    Children = [new Text(label)],
                });
            }

            return new DropdownMenu
            {
                Children =
                [
                    new DropdownMenuTrigger
                    {
                        AsChild = true,
                        Children =
                        [
                            new ShadcnButton
                            {
                                Variant = ButtonVariant.Outline,
                                Size = ButtonSize.Sm,
                                Children =
                                [
                                    new Text("Columns"),
                                    new AwAUI.UI.Svg(LucideIcons.ChevronDown) { Class = "size-4" },
                                ],
                            },
                        ],
                    },
                    new DropdownMenuContent { Children = items },
                ],
            };
        }

        void SetStatus(string status) =>
            _model.SetColumnFilter("status", status is "" or "all" ? "" : status);
    }

    // ------------------------------------------------------------------ carousel

    static Div CarouselSlide(string title, string body) => new()
    {
        Class = "demo-panel",
        Style = "height: 150px; justify-content: center",
        Children =
        [
            DocsUi.Label(title),
            DocsUi.Muted(body),
        ],
    };

    static Node CarouselExample(bool loop) => DocsUi.Stack(
        DocsUi.Center(
            new Carousel
            {
                Loop = loop,
                Rest = new AttributeDictionary { ["style"] = "width: 320px" },
                Children =
                [
                    new CarouselContent
                    {
                        Children =
                        [
                            new CarouselItem
                            {
                                Children =
                                [
                                    CarouselSlide("Slide one", "Page with the arrows, swipe, or the arrow keys."),
                                ],
                            },
                            new CarouselItem
                            {
                                Children =
                                [
                                    CarouselSlide("Slide two", "Each slide fills the viewport width of the carousel."),
                                ],
                            },
                            new CarouselItem
                            {
                                Children =
                                [
                                    CarouselSlide("Slide three", "The content track translates instead of scrolling."),
                                ],
                            },
                        ],
                    },
                    new CarouselPrevious(),
                    new CarouselNext(),
                    new CarouselDots(),
                ],
            }),
        DocsUi.Note(loop
            ? "Loop wraps from the last slide back to the first in both directions."
            : "At the first and last slide the matching arrow is disabled."));

    static Node CarouselBasic() => CarouselExample(false);

    static Node CarouselLoop() => CarouselExample(true);
}
