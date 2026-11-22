using System;
using System.Collections.Generic;
using System.Globalization;
using AwAUI.Icons.Lucide;
using AwAUI.Platform;
using AwAUI.UI;
using CheckboxPart = AwAUI.Shadcn.Checkbox;
using InputPart = AwAUI.Shadcn.Input;
using SvgDocument = AwAUI.Svg.SvgDocument;
using UIText = AwAUI.UI.Text;
using UISvg = AwAUI.UI.Svg;

namespace AwAUI.Shadcn;

/// <summary>
/// Data table rendered with the shadcn table parts, optional filter input, sortable headers,
/// checkbox selection across the filtered row set, and a pagination footer with a polite live summary.
/// <para>
/// Supply <see cref="Model"/> to control the state from outside, or set <see cref="Rows"/> and
/// <see cref="Columns"/> to let the component own a <see cref="DataTableModel{T}"/>. Every state change
/// is reported through <see cref="StateChanged"/> / <see cref="OnStateChange"/>.
/// </para>
/// </summary>
public sealed partial class DataTable<T> : Component
{
    static readonly T[] NoRows = Array.Empty<T>();
    static readonly DataTableColumn<T>[] NoColumns = Array.Empty<DataTableColumn<T>>();

    readonly string _id = TabsParts.NextId("data-table");
    DataTableModel<T>? _internalModel;
    DataTableModel<T>? _activeModel;
    int _appliedPageSize;

    /// <summary>Source rows for the self-contained model. Ignored when <see cref="Model"/> is set.</summary>
    public IReadOnlyList<T>? Rows { get; set; }

    /// <summary>Column definitions for the self-contained model. Ignored when <see cref="Model"/> is set.</summary>
    public IReadOnlyList<DataTableColumn<T>>? Columns { get; set; }

    /// <summary>External model. When set, the component renders its rows, columns, and state.</summary>
    public DataTableModel<T>? Model { get; set; }

    /// <summary>Row identity used by the self-contained model. Required for stable selection across sorting and filtering.</summary>
    public Func<T, string>? RowKey { get; set; }

    /// <summary>Initial page size of the self-contained model.</summary>
    public int PageSize { get; set; } = 10;

    /// <summary>Page sizes offered by the footer buttons. Use an empty array to hide them.</summary>
    public int[]? PageSizes { get; set; } = new[] { 10, 20, 50 };

    /// <summary>Optional table caption (the accessible name is <see cref="Label"/>).</summary>
    public string? Caption { get; set; }

    /// <summary>Accessible name of the table. Defaults to "Data table".</summary>
    public string? Label { get; set; }

    /// <summary>Placeholder of the global filter input.</summary>
    public string? FilterPlaceholder { get; set; } = "Filter rows...";

    /// <summary>Shows the global filter input.</summary>
    public bool Filterable { get; set; } = true;

    /// <summary>Shows the pagination footer and limits body rows to one page.</summary>
    public bool Paginated { get; set; } = true;

    /// <summary>Shows the selection column and header select-all checkbox.</summary>
    public bool Selectable { get; set; } = true;

    /// <summary>
    /// Scope of the header select-all checkbox. Defaults to <see cref="DataTableSelectionScope.Global"/>:
    /// the header selects every row that survives the current filters, spanning pages. Set
    /// <see cref="DataTableSelectionScope.Page"/> for a page-scoped affordance; the page-scoped model
    /// helpers (<see cref="DataTableModel{T}.IsPageAllSelected"/> etc.) remain available either way.
    /// </summary>
    public DataTableSelectionScope SelectAllScope { get; set; } = DataTableSelectionScope.Global;

    public string? Class { get; set; }

    public Ref<Element>? Ref { get; set; }

    /// <summary>Called with the current model after every table state change.</summary>
    public Action<DataTableModel<T>>? StateChanged { get; set; }

    /// <summary>Alternative spelling of <see cref="StateChanged"/>.</summary>
    public Action<DataTableModel<T>>? OnStateChange { get; set; }

    [CaptureUnmatchedAttributes] public AttributeDictionary? Rest { get; set; }

    protected override Node Build()
    {
        var model = ResolveModel();
        var changed = StateChanged + OnStateChange;

        UseEffect(() =>
        {
            Action handler = () =>
            {
                if (!ReferenceEquals(_activeModel, model)) return;
                changed?.Invoke(model);
                Invalidate();
            };
            model.Changed += handler;
            return () => model.Changed -= handler;
        }, model, changed);

        return BuildShell(model);
    }

    DataTableModel<T> ResolveModel()
    {
        if (Model is not null)
        {
            _activeModel = Model;
            return Model;
        }

        var rows = Rows ?? NoRows;
        var columns = Columns ?? NoColumns;
        var model = _internalModel;
        if (model is null)
        {
            model = new DataTableModel<T>(rows, columns, RowKey, PageSize);
            _internalModel = model;
            _appliedPageSize = PageSize;
        }
        else
        {
            model.SyncSource(rows, columns, RowKey);
            if (PageSize != _appliedPageSize)
            {
                model.SyncPageSize(PageSize);
                _appliedPageSize = PageSize;
            }
        }

        _activeModel = model;
        return model;
    }

    Node BuildShell(DataTableModel<T> model)
    {
        var content = new NodeList();
        if (Filterable) content.Add(BuildToolbar(model));
        content.Add(BuildTable(model));
        if (Paginated) content.Add(BuildFooter(model));

        return TabsParts.Style(new Div { Ref = Ref, Children = content }, "data-table",
            "w-full space-y-4", Class, Rest);
    }

    Node BuildToolbar(DataTableModel<T> model)
    {
        var input = new InputPart
        {
            Id = _id + "-filter",
            Value = model.GlobalFilter,
            Placeholder = FilterPlaceholder,
            ValueChanged = value => model.SetGlobalFilter(value),
            Class = "max-w-sm",
            Rest = new AttributeDictionary { ["aria-label"] = "Filter rows" },
        };

        var toolbar = new Div { Class = "flex flex-wrap items-center gap-2", Children = [input] };
        toolbar.Attributes["data-slot"] = "data-table-toolbar";
        return toolbar;
    }

    Node BuildTable(DataTableModel<T> model)
    {
        var columns = model.VisibleColumns;
        var children = new NodeList();
        string? caption = Caption;
        if (!string.IsNullOrEmpty(caption))
            children.Add(new TableCaption { Children = [new UIText(caption!)] });
        children.Add(new TableHeader { Children = [BuildHeaderRow(model)] });
        children.Add(new TableBody { Children = BuildBodyRows(model) });

        string? label = Label;
        return new Table
        {
            Columns = BuildGridTemplate(columns),
            Rest = new AttributeDictionary
            {
                ["aria-label"] = string.IsNullOrWhiteSpace(label) ? "Data table" : label!,
            },
            Children = children,
        };
    }

    string BuildGridTemplate(IReadOnlyList<DataTableColumn<T>> columns)
    {
        var tracks = new List<string>(columns.Count + 1);
        if (Selectable) tracks.Add("2.5rem");
        for (int i = 0; i < columns.Count; i++)
        {
            string? width = columns[i].Width;
            tracks.Add(string.IsNullOrWhiteSpace(width) ? "minmax(0,1fr)" : width!.Trim());
        }
        if (tracks.Count == 0) tracks.Add("minmax(0,1fr)");
        return string.Join(" ", tracks);
    }

    Node BuildHeaderRow(DataTableModel<T> model)
    {
        var cells = new NodeList();
        if (Selectable)
        {
            bool globalScope = SelectAllScope == DataTableSelectionScope.Global;
            var checkbox = new CheckboxPart
            {
                Id = _id + "-select-all",
                Checked = globalScope ? model.IsAllRowsSelected : model.IsPageAllSelected,
                Indeterminate = globalScope ? model.IsSomeRowsSelected : model.IsPageIndeterminate,
                CheckedChanged = selected =>
                {
                    if (!globalScope)
                    {
                        model.SetPageSelected(selected);
                        return;
                    }
                    if (selected) model.SelectAll();
                    else model.ClearSelection();
                },
                Rest = new AttributeDictionary
                {
                    ["aria-label"] = globalScope ? "Select all rows" : "Select all rows on this page",
                },
            };
            cells.Add(new TableHead
            {
                Rest = new AttributeDictionary { ["style"] = "width: 2.5rem" },
                Children = [checkbox],
            });
        }

        var columns = model.VisibleColumns;
        for (int i = 0; i < columns.Count; i++) cells.Add(BuildHeaderCell(model, columns[i]));
        return new TableRow { Class = "motion-reduce:transition-none", Children = cells };
    }

    Node BuildHeaderCell(DataTableModel<T> model, DataTableColumn<T> column)
    {
        var direction = model.GetSortDirection(column.Key);
        bool sorted = direction != DataTableSortDirection.None;

        var rest = new AttributeDictionary { ["data-column-key"] = column.Key };
        if (column.EnableSorting)
            rest["aria-sort"] = sorted
                ? direction == DataTableSortDirection.Ascending ? "ascending" : "descending"
                : "none";
        if (!string.IsNullOrWhiteSpace(column.Width)) rest["style"] = "width: " + column.Width!.Trim();

        var content = new NodeList();
        if (column.HeaderRenderer is { } headerRenderer) content.Add(headerRenderer());
        else content.Add(new UIText(column.Header ?? column.Key));

        var classes = AwAUI.Tailwind.TailwindMerge.Cn(AlignHead(column.Align), column.HeaderClass);
        if (!column.EnableSorting)
            return new TableHead { Class = classes, Rest = rest, Children = content };

        var buttonChildren = new NodeList();
        foreach (var node in content) buttonChildren.Add(node);
        buttonChildren.Add(BuildSortIcon(sorted, direction));

        var button = new Button
        {
            Variant = ButtonVariant.Ghost,
            Size = ButtonSize.Sm,
            Class = "-ml-2 h-8 gap-1 font-medium motion-reduce:transition-none",
            OnClick = e => model.ToggleSort(column.Key, (e.Modifiers & KeyModifiers.Shift) != 0),
            Children = buttonChildren,
        };

        return new TableHead { Class = classes, Rest = rest, Children = [button] };
    }

    Node BuildSortIcon(bool sorted, DataTableSortDirection direction)
    {
        var document = !sorted
            ? LucideIcons.ChevronsUpDown
            : direction == DataTableSortDirection.Ascending ? LucideIcons.ChevronUp : LucideIcons.ChevronDown;
        var icon = new UISvg(document) { Class = "size-3.5 shrink-0 text-muted-foreground" };
        icon.Attributes["aria-hidden"] = "true";
        icon.Attributes["data-slot"] = "data-table-sort-icon";
        return icon;
    }

    NodeList BuildBodyRows(DataTableModel<T> model)
    {
        var rows = new NodeList();
        var visible = Paginated ? model.PageRows : model.SortedRows;
        if (visible.Count == 0)
        {
            var message = model.HasActiveFilters ? "No results." : "No data.";
            int columnCount = (Selectable ? 1 : 0) + model.VisibleColumns.Count;
            var empty = new TableCell
            {
                Class = "py-8 text-center text-muted-foreground",
                Rest = new AttributeDictionary
                {
                    ["style"] = "grid-column: 1 / -1",
                    ["colspan"] = columnCount < 1 ? 1 : columnCount,
                },
                Children = [new UIText(message)],
            };
            rows.Add(new TableRow { Class = "motion-reduce:transition-none", Children = [empty] });
            return rows;
        }

        for (int i = 0; i < visible.Count; i++) rows.Add(BuildRow(model, visible[i], i));
        return rows;
    }

    Node BuildRow(DataTableModel<T> model, T item, int index)
    {
        string key = model.KeyOf(item);
        bool selected = model.IsSelected(key);
        var cells = new NodeList();

        if (Selectable)
        {
            var checkbox = new CheckboxPart
            {
                Id = TabsParts.ValueId(_id + "-select", key),
                Checked = selected,
                CheckedChanged = value => model.SetSelected(key, value),
                Rest = new AttributeDictionary
                {
                    ["aria-label"] = "Select row " + (index + 1).ToString(CultureInfo.InvariantCulture),
                    ["data-row-key"] = key,
                },
            };
            cells.Add(new TableCell
            {
                Rest = new AttributeDictionary { ["style"] = "width: 2.5rem" },
                Children = [checkbox],
            });
        }

        var columns = model.VisibleColumns;
        for (int i = 0; i < columns.Count; i++) cells.Add(BuildCell(item, columns[i]));

        var rowRest = new AttributeDictionary { ["data-row-key"] = key };
        if (selected) rowRest["data-state"] = "selected";
        return new TableRow { Class = "motion-reduce:transition-none", Rest = rowRest, Children = cells };
    }

    Node BuildCell(T item, DataTableColumn<T> column)
    {
        var rest = new AttributeDictionary { ["data-column-key"] = column.Key };
        if (!string.IsNullOrWhiteSpace(column.Width)) rest["style"] = "width: " + column.Width!.Trim();

        Node content;
        if (column.Cell is { } cellRenderer)
            content = cellRenderer(item);
        else
            content = new UIText(DataTableValueComparer.ToText(column.Value is { } accessor ? accessor(item) : null));

        return new TableCell
        {
            Class = AwAUI.Tailwind.TailwindMerge.Cn(AlignCell(column.Align), column.Class),
            Rest = rest,
            Children = [content],
        };
    }

    Node BuildFooter(DataTableModel<T> model)
    {
        var status = new Div { Role = Role.Status, Class = "text-sm text-muted-foreground" };
        status.Attributes["aria-live"] = "polite";
        status.Attributes["aria-atomic"] = "true";
        status.Attributes["data-slot"] = "data-table-status";
        status.Children.Add(new UIText(BuildStatus(model)));

        var footer = new Div
        {
            Class = "flex flex-col gap-3 sm:flex-row sm:items-center sm:justify-between",
            Children = [status, BuildPager(model)],
        };
        footer.Attributes["data-slot"] = "data-table-footer";
        return footer;
    }

    string BuildStatus(DataTableModel<T> model)
    {
        var parts = new List<string>(3);
        if (Paginated)
        {
            parts.Add(model.PageCount == 0
                ? "No results"
                : FormattableString.Invariant($"Page {model.PageIndex + 1} of {model.PageCount}"));
        }
        else
        {
            int count = model.FilteredCount;
            parts.Add(count == 1
                ? "1 row"
                : FormattableString.Invariant($"{count} rows"));
        }
        if (Selectable)
            parts.Add(FormattableString.Invariant($"{model.SelectedCount} selected"));
        return string.Join(" · ", parts);
    }

    Node BuildPager(DataTableModel<T> model)
    {
        var pager = new Div { Class = "flex flex-wrap items-center gap-2" };
        pager.Attributes["data-slot"] = "data-table-pagination";

        var sizes = PageSizes;
        if (sizes is not null && sizes.Length > 0)
        {
            var group = new Div { Role = Role.Group, Class = "flex items-center gap-1" };
            group.Attributes["aria-label"] = "Rows per page";
            group.Attributes["data-slot"] = "data-table-page-sizes";
            foreach (int offered in sizes)
            {
                if (offered <= 0) continue;
                int size = offered;
                group.Children.Add(new Button
                {
                    Variant = size == model.PageSize ? ButtonVariant.Default : ButtonVariant.Outline,
                    Size = ButtonSize.Sm,
                    Class = "motion-reduce:transition-none",
                    OnClick = _ => model.SetPageSize(size),
                    Rest = new AttributeDictionary
                    {
                        ["aria-label"] = FormattableString.Invariant($"Show {size} rows"),
                        ["aria-pressed"] = size == model.PageSize ? "true" : "false",
                    },
                    Children = [new UIText(size.ToString(CultureInfo.InvariantCulture))],
                });
            }
            pager.Children.Add(group);
        }

        pager.Children.Add(BuildPagerButton("Go to first page", "First", !model.CanPrevious, model.FirstPage));
        pager.Children.Add(BuildPagerIconButton("Go to previous page", LucideIcons.ChevronLeft, !model.CanPrevious, model.PreviousPage));
        pager.Children.Add(BuildPagerIconButton("Go to next page", LucideIcons.ChevronRight, !model.CanNext, model.NextPage));
        pager.Children.Add(BuildPagerButton("Go to last page", "Last", !model.CanNext, model.LastPage));
        return pager;
    }

    static Button BuildPagerButton(string label, string text, bool disabled, Action activate) => new()
    {
        Variant = ButtonVariant.Outline,
        Size = ButtonSize.Sm,
        Disabled = disabled,
        Class = "motion-reduce:transition-none",
        OnClick = _ => activate(),
        Rest = new AttributeDictionary { ["aria-label"] = label },
        Children = [new UIText(text)],
    };

    static Button BuildPagerIconButton(string label, SvgDocument document, bool disabled, Action activate)
    {
        var icon = new UISvg(document) { Class = "size-4" };
        icon.Attributes["aria-hidden"] = "true";
        return new Button
        {
            Variant = ButtonVariant.Outline,
            Size = ButtonSize.IconSm,
            Disabled = disabled,
            Class = "motion-reduce:transition-none",
            OnClick = _ => activate(),
            Rest = new AttributeDictionary { ["aria-label"] = label },
            Children = [icon],
        };
    }

    static string AlignHead(DataTableColumnAlign align) => align switch
    {
        DataTableColumnAlign.Center => "justify-center text-center",
        DataTableColumnAlign.End => "justify-end text-end",
        _ => "",
    };

    static string AlignCell(DataTableColumnAlign align) => align switch
    {
        DataTableColumnAlign.Center => "text-center",
        DataTableColumnAlign.End => "text-end",
        _ => "",
    };
}
