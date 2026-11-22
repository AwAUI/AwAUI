using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using AwAUI.Controls.Primitives;
using AwAUI.Icons.Lucide;
using AwAUI.Shadcn;
using AwAUI.Svg;
using AwAUI.UI;

namespace DeclarativeDemo.Pages;

// Code-behind for Projects.awaui. The page owns the headless DataTableModel; the markup renders it.
public partial class Projects
{
    static readonly int[] _pageSizes = [8, 12, 24];

    static readonly IReadOnlyList<ColumnChoice> ColumnChoices =
    [
        new("name", "Project"),
        new("status", "Status"),
        new("owner", "Owner"),
        new("priority", "Priority"),
        new("updated", "Updated"),
        new("budget", "Budget"),
    ];

    static readonly IReadOnlyList<ColumnChoice> SortChoices =
    [
        new("status", "Status"),
        new("priority", "Priority"),
        new("updated", "Updated"),
        new("name", "Project name"),
    ];

    readonly List<ProjectRow> _rows = SampleData.Projects.ToList();
    readonly DataTableModel<ProjectRow> _model;
    readonly State<string> _status = new("all");
    readonly State<bool> _dialogOpen = new(false);

    public Projects()
    {
        _model = new DataTableModel<ProjectRow>(_rows, BuildColumns(), row => row.Id, 8)
        {
            EnableMultiSort = true,
        };
    }

    /// <summary>Passed down from the shell so the New project dialog can raise a success toast.</summary>
    public ToastStore? Toasts { get; set; }

    /// <summary>True when the shell was launched with --page project-dialog.</summary>
    public bool StartWithDialog { get; set; }

    protected override void OnMount()
    {
        _model.Changed += OnModelChanged;
        if (StartWithDialog) _dialogOpen.Value = true;
    }

    protected override void OnUnmount() => _model.Changed -= OnModelChanged;

    void OnModelChanged()
    {
        if (IsMounted) Invalidate();
    }

    IReadOnlyList<DataTableColumn<ProjectRow>> BuildColumns() =>
    [
        new()
        {
            Key = "name", Header = "Project", EnableSorting = true,
            Value = row => row.Name, FilterValue = row => row.Name, Cell = row => NameCell(row),
        },
        new()
        {
            Key = "status", Header = "Status", EnableSorting = true, Width = "8.5rem",
            Value = row => row.Status, Cell = row => StatusCell(row),
        },
        new()
        {
            Key = "owner", Header = "Owner", EnableSorting = true, Width = "11rem",
            Value = row => row.Owner, FilterValue = row => row.Owner,
        },
        new()
        {
            Key = "priority", Header = "Priority", EnableSorting = true, Width = "8rem",
            Value = row => row.Priority,
        },
        new()
        {
            Key = "updated", Header = "Updated", EnableSorting = true, Width = "8rem",
            Value = row => row.Updated, SortValue = row => row.Updated, Cell = row => UpdatedCell(row),
        },
        new()
        {
            Key = "budget", Header = "Budget", EnableSorting = true, Width = "7rem", Align = DataTableColumnAlign.End,
            Value = row => row.Budget, SortValue = row => row.Budget, Cell = row => BudgetCell(row),
        },
        new()
        {
            Key = "actions", Header = "", Width = "3rem", EnableHiding = false, Cell = row => RowActions(row),
        },
    ];

    static Node NameCell(ProjectRow row)
    {
        var cell = new Div { Class = "project-name-cell" };
        cell.Children.Add(new Text { Class = "project-name", Content = row.Name });
        cell.Children.Add(new Text { Class = "project-id", Content = row.Id.ToUpperInvariant() });
        return cell;
    }

    static Node StatusCell(ProjectRow row)
    {
        var badge = new Badge
        {
            Variant = row.Status switch
            {
                "Active" => BadgeVariant.Secondary,
                "Completed" => BadgeVariant.Outline,
                "On hold" => BadgeVariant.Ghost,
                _ => BadgeVariant.Default,
            },
            Class = "project-status",
            Children = new NodeList([new TextNode(row.Status)]),
        };
        var rest = badge.Rest ??= new AttributeDictionary();
        rest["data-status"] = row.Status.ToLowerInvariant().Replace(' ', '-');
        return badge;
    }

    static Node UpdatedCell(ProjectRow row) =>
        new Text { Class = "project-updated", Content = row.Updated.ToString("MMM d, yyyy", CultureInfo.InvariantCulture) };

    static Node BudgetCell(ProjectRow row) =>
        new Text { Class = "project-budget", Content = "$" + row.Budget.ToString("N0", CultureInfo.InvariantCulture) };

    Node RowActions(ProjectRow row)
    {
        var trigger = new AwAUI.Shadcn.Button
        {
            Variant = ButtonVariant.Ghost,
            Size = ButtonSize.IconXs,
            Class = "row-action",
            Children = new NodeList([Glyph(LucideIcons.MoreHorizontal)]),
        };
        var rest = trigger.Rest ??= new AttributeDictionary();
        rest["aria-label"] = "Actions for " + row.Name;

        var menu = new DropdownMenu
        {
            Children = new NodeList(
            [
                new DropdownMenuTrigger { AsChild = true, Children = new NodeList([trigger]) },
                new DropdownMenuContent
                {
                    Align = PopperAlign.End,
                    Children = new NodeList(
                    [
                        new DropdownMenuItem { OnSelect = _ => Edit(row), Children = new NodeList([new TextNode("Edit project")]) },
                        new DropdownMenuItem { OnSelect = _ => Duplicate(row), Children = new NodeList([new TextNode("Duplicate")]) },
                        new DropdownMenuSeparator(),
                        new DropdownMenuItem { Destructive = true, OnSelect = _ => Delete(row), Children = new NodeList([new TextNode("Delete")]) },
                    ]),
                },
            ]),
        };
        return new Div { Class = "row-actions", Children = new NodeList([menu]) };
    }

    static Node Glyph(SvgDocument document)
    {
        var svg = new AwAUI.UI.Svg(document) { Class = "row-action-icon" };
        svg.Attributes["aria-hidden"] = "true";
        return svg;
    }

    void OnSearch(string value) => _model.SetGlobalFilter(value);

    void OnStatusFilter(string value)
    {
        _status.Value = value;
        _model.SetColumnFilter("status", value == "all" ? null : value);
    }

    void ToggleColumn(string key, bool visible) => _model.SetColumnVisibility(key, visible);

    void ToggleSort(string key) => _model.ToggleSort(key, multiColumn: true);

    void ClearSorts() => _model.ClearSort();

    void ClearSelection() => _model.ClearSelection();

    string SortState(string key) => _model.GetSortDirection(key) switch
    {
        DataTableSortDirection.Ascending => "ascending",
        DataTableSortDirection.Descending => "descending",
        _ => "off",
    };

    string SortChip(DataTableSortDescriptor sort)
    {
        string label = ColumnChoices.FirstOrDefault(choice => choice.Key == sort.ColumnKey)?.Label ?? sort.ColumnKey;
        return label + (sort.Direction == DataTableSortDirection.Ascending ? " asc" : " desc");
    }

    void OpenDialog() => _dialogOpen.Value = true;

    void OnDialogOpenChange(bool open) => _dialogOpen.Value = open;

    void OnProjectCreated(ProjectDraft draft)
    {
        var row = new ProjectRow(
            "p-" + (_rows.Count + 1).ToString("00", CultureInfo.InvariantCulture),
            draft.Name,
            string.IsNullOrWhiteSpace(draft.Owner) ? "Unassigned" : draft.Owner,
            "Planning",
            draft.Priority,
            DateTime.Today,
            0,
            0);
        _rows.Insert(0, row);
        _model.SetRows(_rows);
        _dialogOpen.Value = false;
    }

    void Edit(ProjectRow row) => Notify("Edit requested", row.Name + " can be edited in the project workspace.", ToastVariant.Info);

    void Duplicate(ProjectRow row)
    {
        var copy = row with
        {
            Id = "p-" + (_rows.Count + 1).ToString("00", CultureInfo.InvariantCulture),
            Name = row.Name + " (copy)",
            Status = "Planning",
            Updated = DateTime.Today,
            Progress = 0,
        };
        _rows.Insert(0, copy);
        _model.SetRows(_rows);
        Notify("Project duplicated", copy.Name + " was added to the table.", ToastVariant.Success);
    }

    void Delete(ProjectRow row)
    {
        _rows.Remove(row);
        _model.SetRows(_rows);
        Notify("Project deleted", row.Name + " was removed from the workspace.", ToastVariant.Warning);
    }

    void Notify(string title, string description, ToastVariant variant) =>
        Toasts?.Show(new ToastOptions { Title = title, Description = description, Variant = variant });
}
