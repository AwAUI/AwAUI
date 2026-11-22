using System;
using System.Collections.Generic;
using System.Linq;
using AwAUI.Controls.UnitTests.Integration;
using AwAUI.UI;

namespace AwAUI.Shadcn.Integration.S2;

/// <summary>
/// S2 DataTable acceptance: headless sorting / filtering / pagination / selection, the rendered
/// <c>aria-sort</c> state, and the keyboard / pointer paths of the select-all checkbox and pager.
/// </summary>
public class DataTableTests
{
    static DataTableTests() => ShadcnTheme.Register();

    sealed record Person(string Name, int Age);

    static readonly Person[] People =
    {
        new("Alice", 30),
        new("Bob", 25),
        new("Carol", 40),
        new("Dave", 35),
        new("Eve", 28),
    };

    static UIHarness Harness(Func<Node> render) =>
        new(render, appStyleSheets: true, width: 640, height: 480);

    static string? A(Element e, string name) => e.GetAttribute(name);

    static DataTableColumn<Person> NameColumn(bool sortable = true) => new()
    {
        Key = "name",
        Header = "Name",
        Value = person => person.Name,
        Sortable = sortable,
    };

    static DataTableColumn<Person> AgeColumn() => new()
    {
        Key = "age",
        Header = "Age",
        Value = person => person.Age,
        Sortable = true,
        Align = DataTableColumnAlign.End,
    };

    static DataTableColumn<Person>[] Columns() => new[] { NameColumn(), AgeColumn() };

    static Element? Descendant(Element root, Func<Element, bool> match)
    {
        var stack = new Stack<Element>();
        for (int i = root.Children.Count - 1; i >= 0; i--) stack.Push(root.Children[i]);
        while (stack.Count > 0)
        {
            var element = stack.Pop();
            if (match(element)) return element;
            for (int i = element.Children.Count - 1; i >= 0; i--) stack.Push(element.Children[i]);
        }
        return null;
    }

    static Element Head(UIHarness h, string key) =>
        h.FindAll(e => A(e, "data-slot") == "table-head" && A(e, "data-column-key") == key).Single();

    static Element SortButton(UIHarness h, string key) =>
        Descendant(Head(h, key), e => e.Tag == "button")
        ?? throw new Xunit.Sdk.XunitException($"no sort button for column \"{key}\"");

    static string[] CellTexts(UIHarness h, string key) =>
        h.FindAll(e => A(e, "data-slot") == "table-cell" && A(e, "data-column-key") == key)
            .Select(e => e.TextContent.Trim())
            .ToArray();

    static Element Status(UIHarness h) =>
        h.FindAll(e => A(e, "data-slot") == "data-table-status").Single();

    static Element PagerButton(UIHarness h, string label) =>
        h.FindAll(e => e.Tag == "button" && A(e, "aria-label") == label).Single();

    static List<Element> Checkboxes(UIHarness h) => h.FindAll(e => e.Role == Role.CheckBox);

    // ------------------------------------------------------------ sorting

    [Fact]
    public void Sortable_headers_toggle_sort_and_expose_aria_sort()
    {
        var h = Harness(() => new DataTable<Person>
        {
            Rows = People,
            Columns = Columns(),
            Selectable = false,
            PageSize = 10,
        });

        Assert.Equal("none", A(Head(h, "name"), "aria-sort"));
        Assert.Equal("none", A(Head(h, "age"), "aria-sort"));
        Assert.Equal(new[] { "Alice", "Bob", "Carol", "Dave", "Eve" }, CellTexts(h, "name"));

        h.Click(SortButton(h, "name"));
        Assert.Equal("ascending", A(Head(h, "name"), "aria-sort"));
        Assert.Equal("none", A(Head(h, "age"), "aria-sort"));

        h.Click(SortButton(h, "name"));
        Assert.Equal("descending", A(Head(h, "name"), "aria-sort"));
        Assert.Equal(new[] { "Eve", "Dave", "Carol", "Bob", "Alice" }, CellTexts(h, "name"));

        h.Click(SortButton(h, "name"));
        Assert.Equal("none", A(Head(h, "name"), "aria-sort"));
        Assert.Equal(new[] { "Alice", "Bob", "Carol", "Dave", "Eve" }, CellTexts(h, "name"));
    }

    [Fact]
    public void Numeric_sort_uses_value_order_not_lexical_order()
    {
        var h = Harness(() => new DataTable<Person>
        {
            Rows = People,
            Columns = Columns(),
            Selectable = false,
            PageSize = 10,
        });

        h.Click(SortButton(h, "age"));
        Assert.Equal("ascending", A(Head(h, "age"), "aria-sort"));
        Assert.Equal(new[] { "Bob", "Eve", "Alice", "Dave", "Carol" }, CellTexts(h, "name"));

        h.Click(SortButton(h, "age"));
        Assert.Equal("descending", A(Head(h, "age"), "aria-sort"));
        Assert.Equal(new[] { "Carol", "Dave", "Alice", "Eve", "Bob" }, CellTexts(h, "name"));
    }

    // ------------------------------------------------------------ filtering

    [Fact]
    public void Filter_input_narrows_rows_case_insensitively()
    {
        var h = Harness(() => new DataTable<Person>
        {
            Rows = People,
            Columns = Columns(),
            Selectable = false,
            PageSize = 10,
        });

        Assert.Equal(5, CellTexts(h, "name").Length);

        var input = h.FindAll(e => A(e, "data-slot") == "input").Single();
        h.Click(input);
        h.TypeText("BO");

        Assert.Equal(new[] { "Bob" }, CellTexts(h, "name"));
        Assert.Contains("Page 1 of 1", Status(h).TextContent);
    }

    // ------------------------------------------------------------ pagination

    [Fact]
    public void Pagination_footer_clamps_bounds_and_updates_the_live_summary()
    {
        var h = Harness(() => new DataTable<Person>
        {
            Rows = People,
            Columns = new[] { NameColumn() },
            Selectable = false,
            PageSize = 2,
        });

        Assert.Contains("Page 1 of 3", Status(h).TextContent);
        Assert.Equal(new[] { "Alice", "Bob" }, CellTexts(h, "name"));
        Assert.True(PagerButton(h, "Go to previous page").IsDisabled);
        Assert.True(PagerButton(h, "Go to first page").IsDisabled);

        h.Click(PagerButton(h, "Go to next page"));
        Assert.Contains("Page 2 of 3", Status(h).TextContent);
        Assert.Equal(new[] { "Carol", "Dave" }, CellTexts(h, "name"));

        h.Click(PagerButton(h, "Go to next page"));
        Assert.Contains("Page 3 of 3", Status(h).TextContent);
        Assert.Equal(new[] { "Eve" }, CellTexts(h, "name"));
        Assert.True(PagerButton(h, "Go to next page").IsDisabled);
        Assert.True(PagerButton(h, "Go to last page").IsDisabled);

        // A disabled pager ignores the click.
        h.Click(PagerButton(h, "Go to next page"));
        Assert.Contains("Page 3 of 3", Status(h).TextContent);

        h.Click(PagerButton(h, "Go to first page"));
        Assert.Contains("Page 1 of 3", Status(h).TextContent);

        h.Click(PagerButton(h, "Go to last page"));
        Assert.Contains("Page 3 of 3", Status(h).TextContent);

        h.Click(PagerButton(h, "Show 10 rows"));
        Assert.Contains("Page 1 of 1", Status(h).TextContent);
        Assert.Equal(5, CellTexts(h, "name").Length);
    }

    // ------------------------------------------------------------ selection

    [Fact]
    public void Selection_is_page_scoped_and_select_all_reports_mixed_state()
    {
        var model = new DataTableModel<Person>(People, new[] { NameColumn() }, person => person.Name, pageSize: 2);
        int callbacks = 0;
        var h = Harness(() => new DataTable<Person>
        {
            Model = model,
            Selectable = true,
            // The page-scoped helpers are still exercised explicitly; the component default is global.
            SelectAllScope = DataTableSelectionScope.Page,
            StateChanged = _ => callbacks++,
        });

        var boxes = Checkboxes(h);
        Assert.Equal(3, boxes.Count); // header + two rows on page one
        Assert.Equal("false", A(boxes[0], "aria-checked"));

        // Select Alice: the page select-all becomes mixed.
        h.Click(Checkboxes(h)[1]);
        boxes = Checkboxes(h);
        Assert.Equal("mixed", A(boxes[0], "aria-checked"));
        Assert.Equal("true", A(boxes[1], "aria-checked"));
        Assert.Equal(1, model.SelectedCount);
        Assert.True(callbacks > 0);

        // Select Bob: the whole page is selected.
        h.Click(Checkboxes(h)[2]);
        boxes = Checkboxes(h);
        Assert.Equal("true", A(boxes[0], "aria-checked"));
        Assert.Equal(2, model.SelectedCount);
        Assert.True(model.IsSelected("Alice"));
        Assert.True(model.IsSelected("Bob"));
        Assert.False(model.IsSelected("Carol"));

        // Header select-all clears the page.
        h.Click(Checkboxes(h)[0]);
        Assert.Equal(0, model.SelectedCount);
        Assert.Equal("false", A(Checkboxes(h)[0], "aria-checked"));

        // Header select-all covers only the current page.
        h.Click(Checkboxes(h)[0]);
        boxes = Checkboxes(h);
        Assert.Equal("true", A(boxes[0], "aria-checked"));
        Assert.Equal(2, model.SelectedCount);
        Assert.False(model.IsSelected("Carol"));

        // The next page has its own select-all state.
        h.Click(PagerButton(h, "Go to next page"));
        boxes = Checkboxes(h);
        Assert.Equal(3, boxes.Count);
        Assert.Equal("false", A(boxes[0], "aria-checked"));
        Assert.False(model.IsSelected("Carol"));

        // One of two rows selected: mixed.
        h.Click(Checkboxes(h)[1]);
        boxes = Checkboxes(h);
        Assert.Equal("mixed", A(boxes[0], "aria-checked"));
        Assert.True(model.IsSelected("Carol"));

        // Both page rows selected: checked, but page one stays selected and page three untouched.
        h.Click(Checkboxes(h)[2]);
        boxes = Checkboxes(h);
        Assert.Equal("true", A(boxes[0], "aria-checked"));
        Assert.Equal(4, model.SelectedCount);
        Assert.True(model.IsSelected("Alice"));
        Assert.False(model.IsSelected("Eve"));
    }

    // ------------------------------------------------------------ headless model

    [Fact]
    public void Model_covers_sort_filter_pagination_and_page_scoped_selection()
    {
        var model = new DataTableModel<Person>(People, Columns(), person => person.Name, pageSize: 2);

        Assert.Equal(3, model.PageCount);
        Assert.Equal(5, model.FilteredCount);

        model.ToggleSort("age");
        Assert.Equal(DataTableSortDirection.Ascending, model.SortDirection);
        Assert.Equal("age", model.SortColumnKey);
        Assert.Equal(new[] { "Bob", "Eve", "Alice", "Dave", "Carol" }, model.SortedRows.Select(p => p.Name).ToArray());

        model.ToggleSort("age");
        Assert.Equal(DataTableSortDirection.Descending, model.SortDirection);
        Assert.Equal(new[] { "Carol", "Dave", "Alice", "Eve", "Bob" }, model.SortedRows.Select(p => p.Name).ToArray());

        model.ToggleSort("age");
        Assert.Equal(DataTableSortDirection.None, model.SortDirection);
        Assert.Null(model.SortColumnKey);
        Assert.Equal(new[] { "Alice", "Bob", "Carol", "Dave", "Eve" }, model.SortedRows.Select(p => p.Name).ToArray());

        // Non-sortable columns do not toggle.
        model.SetColumns(new[] { new DataTableColumn<Person> { Key = "name", Header = "Name", Value = p => p.Name } });
        model.ToggleSort("name");
        Assert.Equal(DataTableSortDirection.None, model.SortDirection);

        model.SetColumns(Columns());
        model.SetGlobalFilter("A");
        Assert.Equal(new[] { "Alice", "Carol", "Dave" }, model.FilteredRows.Select(p => p.Name).ToArray());
        Assert.Equal(2, model.PageCount);
        Assert.Equal(0, model.PageIndex);

        model.NextPage();
        Assert.Equal(1, model.PageIndex);
        model.NextPage();
        Assert.Equal(1, model.PageIndex);
        Assert.False(model.CanNext);

        model.SetPageIndex(99);
        Assert.Equal(1, model.PageIndex);
        model.PreviousPage();
        Assert.Equal(0, model.PageIndex);
        model.PreviousPage();
        Assert.Equal(0, model.PageIndex);
        Assert.False(model.CanPrevious);

        model.SetPageSize(10);
        Assert.Equal(0, model.PageIndex);
        Assert.Equal(1, model.PageCount);

        model.SetGlobalFilter("");
        Assert.Equal(5, model.FilteredCount);
        Assert.Equal(0, model.PageCount - 1);
    }

    [Fact]
    public void Value_comparer_is_numeric_aware_and_ordinal_for_text()
    {
        var values = new object?[] { "item10", "item2", "item1" };
        Array.Sort(values, DataTableValueComparer.Instance);
        Assert.Equal(new object?[] { "item1", "item2", "item10" }, values);

        var numbers = new object?[] { 10, 2, 1 };
        Array.Sort(numbers, DataTableValueComparer.Instance);
        Assert.Equal(new object?[] { 1, 2, 10 }, numbers);
    }

    // ------------------------------------------------------------ empty state

    [Fact]
    public void Empty_state_renders_a_message_and_disables_paging()
    {
        var h = Harness(() => new DataTable<Person>
        {
            Rows = Array.Empty<Person>(),
            Columns = new[] { NameColumn() },
            Selectable = false,
            PageSize = 10,
        });

        var cell = h.FindAll(e => A(e, "data-slot") == "table-cell").Single();
        Assert.Contains("No data.", cell.TextContent);
        Assert.Contains("No results", Status(h).TextContent);
        Assert.Equal("polite", A(Status(h), "aria-live"));
        Assert.True(PagerButton(h, "Go to next page").IsDisabled);
    }

    // ------------------------------------------------------------ TanStack core state

    sealed record Sale(string Region, int Amount, string Name);

    static readonly Sale[] Sales =
    {
        new("North", 10, "alpha"),
        new("South", 20, "beta"),
        new("North", 20, "gamma"),
        new("South", 20, "delta"),
        new("North", 5, "epsilon"),
    };

    static DataTableColumn<Sale>[] SaleColumns() => new[]
    {
        new DataTableColumn<Sale> { Key = "region", Header = "Region", Value = sale => sale.Region, Sortable = true },
        new DataTableColumn<Sale> { Key = "amount", Header = "Amount", Value = sale => sale.Amount, Sortable = true },
        new DataTableColumn<Sale>
        {
            Key = "name", Header = "Name", Value = sale => sale.Name, Sortable = true, EnableHiding = false,
        },
    };

    static string[] SortedNames(DataTableModel<Sale> model) =>
        model.SortedRows.Select(sale => sale.Name).ToArray();

    static string[] FilteredNames(DataTableModel<Sale> model) =>
        model.FilteredRows.Select(sale => sale.Name).ToArray();

    [Fact]
    public void Multi_sort_applies_precedence_and_stays_stable()
    {
        var model = new DataTableModel<Sale>(Sales, SaleColumns(), sale => sale.Name, pageSize: 10)
        {
            EnableMultiSort = true,
        };

        model.AddSort("region", DataTableSortDirection.Ascending);
        model.AddSort("amount", DataTableSortDirection.Descending);

        Assert.Equal(2, model.SortDescriptors.Count);
        Assert.Equal("region", model.SortDescriptors[0].ColumnKey);
        Assert.Equal(0, model.SortDescriptors[0].Precedence);
        Assert.Equal(DataTableSortDirection.Ascending, model.SortDescriptors[0].Direction);
        Assert.Equal("amount", model.SortDescriptors[1].ColumnKey);
        Assert.Equal(1, model.SortDescriptors[1].Precedence);
        Assert.Equal(DataTableSortDirection.Descending, model.SortDescriptors[1].Direction);
        Assert.Equal(0, model.GetSortPrecedence("region"));
        Assert.Equal(1, model.GetSortPrecedence("amount"));

        // Region groups first; inside a group the shared amount keeps source order (beta before delta).
        Assert.Equal(new[] { "gamma", "alpha", "epsilon", "beta", "delta" }, SortedNames(model));

        // Shift-click flips the primary sort in place and keeps the secondary sort.
        model.ToggleSort("region", multiColumn: true);
        Assert.Equal(2, model.SortDescriptors.Count);
        Assert.Equal(DataTableSortDirection.Descending, model.SortDescriptors[0].Direction);
        Assert.Equal(new[] { "beta", "delta", "gamma", "alpha", "epsilon" }, SortedNames(model));

        // A second shift-click removes it and promotes the next sort.
        model.ToggleSort("region", multiColumn: true);
        Assert.Single(model.SortDescriptors);
        Assert.Equal("amount", model.SortDescriptors[0].ColumnKey);
        Assert.Equal(0, model.SortDescriptors[0].Precedence);
        Assert.Equal(new[] { "beta", "gamma", "delta", "alpha", "epsilon" }, SortedNames(model));
    }

    [Fact]
    public void Column_visibility_tracks_overrides_and_hiding_rules()
    {
        var model = new DataTableModel<Sale>(Sales, SaleColumns(), sale => sale.Name);
        int changed = 0;
        model.Changed += () => changed++;

        Assert.True(model.GetColumnVisibility("amount"));
        Assert.Equal(3, model.VisibleColumns.Count);

        model.SetColumnVisibility("amount", false);
        Assert.False(model.GetColumnVisibility("amount"));
        Assert.Equal(1, changed);
        Assert.Equal(new[] { "region", "name" }, model.VisibleColumns.Select(c => c.Key).ToArray());

        model.ToggleColumnVisibility("amount");
        Assert.True(model.GetColumnVisibility("amount"));
        Assert.Equal(2, changed);

        // A column can opt out of hiding.
        model.SetColumnVisibility("name", false);
        Assert.True(model.GetColumnVisibility("name"));
        model.ToggleColumnVisibility("name");
        Assert.True(model.GetColumnVisibility("name"));
        Assert.Equal(2, changed);
    }

    [Fact]
    public void Per_column_filters_intersect_with_the_global_filter()
    {
        var model = new DataTableModel<Sale>(Sales, SaleColumns(), sale => sale.Name);
        int changed = 0;
        model.Changed += () => changed++;

        model.SetColumnFilter("region", "north");
        Assert.Equal(new[] { "alpha", "gamma", "epsilon" }, FilteredNames(model));
        Assert.Equal("north", model.GetColumnFilter("region"));
        Assert.True(model.HasActiveFilters);

        model.SetColumnFilter("amount", "20");
        Assert.Equal(new[] { "gamma" }, FilteredNames(model));

        model.ClearColumnFilters();
        Assert.Empty(model.ColumnFilters);
        Assert.Equal(5, model.FilteredCount);
        Assert.False(model.HasActiveFilters);

        model.SetGlobalFilter("south");
        model.SetColumnFilter("amount", "20");
        Assert.Equal(new[] { "beta", "delta" }, FilteredNames(model));

        model.ClearAllFilters();
        Assert.Equal("", model.GlobalFilter);
        Assert.Empty(model.ColumnFilters);
        Assert.Equal(5, model.FilteredCount);
        Assert.Equal(6, changed);
    }

    [Fact]
    public void Filter_fn_supports_contains_equals_starts_with_and_custom()
    {
        Assert.Equal(new[] { "alpha", "beta", "gamma", "delta" },
            FilteredNamesFor(DataTableFilterFn.Contains, "a"));
        Assert.Equal(new[] { "beta" }, FilteredNamesFor(DataTableFilterFn.Equals, "beta"));
        Assert.Equal(new[] { "gamma" }, FilteredNamesFor(DataTableFilterFn.StartsWith, "ga"));
        Assert.Equal(new[] { "alpha", "beta", "gamma", "delta" },
            FilteredNamesFor(DataTableFilterFn.Custom(
                static (value, _) => value is string text && text.EndsWith("a", StringComparison.Ordinal)), "ignored"));
    }

    static string[] FilteredNamesFor(DataTableFilterFn filterFn, string text)
    {
        var column = new DataTableColumn<Sale>
        {
            Key = "name", Header = "Name", Value = sale => sale.Name, FilterFn = filterFn,
        };
        var model = new DataTableModel<Sale>(Sales, new[] { column }, sale => sale.Name);
        model.SetColumnFilter("name", text);
        return FilteredNames(model);
    }

    [Fact]
    public void Pagination_counts_follow_the_filtered_set_and_clamp_the_page()
    {
        var model = new DataTableModel<Sale>(Sales, SaleColumns(), sale => sale.Name, pageSize: 2);
        Assert.Equal(3, model.PageCount);

        model.LastPage();
        Assert.Equal(2, model.PageIndex);

        // A filter change returns to the first page.
        model.SetColumnFilter("region", "north");
        Assert.Equal(3, model.FilteredCount);
        Assert.Equal(2, model.PageCount);
        Assert.Equal(0, model.PageIndex);

        model.LastPage();
        Assert.Equal(1, model.PageIndex);

        // Narrowing the filter clamps the page index as well.
        model.SetColumnFilter("amount", "20");
        Assert.Equal(1, model.FilteredCount);
        Assert.Equal(1, model.PageCount);
        Assert.Equal(0, model.PageIndex);
        Assert.False(model.CanNext);
        Assert.False(model.CanPrevious);
    }

    [Fact]
    public void Selection_is_global_across_pages_and_filters()
    {
        var model = new DataTableModel<Sale>(Sales, SaleColumns(), sale => sale.Name, pageSize: 2);
        int changed = 0;
        model.Changed += () => changed++;

        model.SelectAll();
        Assert.Equal(5, model.SelectedCount);
        Assert.True(model.IsAllRowsSelected);
        Assert.False(model.IsSomeRowsSelected);
        Assert.Equal(1, changed);

        model.ToggleRowSelected(Sales[0]);
        Assert.Equal(4, model.SelectedCount);
        Assert.False(model.IsAllRowsSelected);
        Assert.True(model.IsSomeRowsSelected);

        // Not all selected: toggle selects all filtered rows.
        model.ToggleAllRowsSelected();
        Assert.Equal(5, model.SelectedCount);

        // All selected: toggle clears the filtered rows.
        model.ToggleAllRowsSelected();
        Assert.Equal(0, model.SelectedCount);
        Assert.False(model.IsAllRowsSelected);

        // Select-all respects the filter and spans every page of it.
        model.SetColumnFilter("region", "north");
        model.ToggleAllRowsSelected();
        Assert.Equal(new[] { "alpha", "epsilon", "gamma" },
            model.SelectedKeys.OrderBy(key => key, StringComparer.Ordinal).ToArray());

        // Clearing the filter keeps the global selection and lets another filter add to it.
        model.ClearColumnFilters();
        Assert.True(model.IsSomeRowsSelected);
        model.SetColumnFilter("region", "south");
        model.ToggleAllRowsSelected();
        Assert.Equal(5, model.SelectedCount);
        Assert.True(model.IsAllRowsSelected);

        // Page-scoped helpers remain available and independent of the global flags.
        model.ClearSelection();
        model.ClearColumnFilters();
        model.SetPageSelected(true);
        Assert.True(model.IsPageAllSelected);
        Assert.Equal(2, model.SelectedCount);
        Assert.False(model.IsAllRowsSelected);
    }

    [Fact]
    public void Reset_methods_and_mutations_raise_changed_exactly_once()
    {
        var model = new DataTableModel<Sale>(Sales, SaleColumns(), sale => sale.Name, pageSize: 2)
        {
            EnableMultiSort = true,
        };
        int changed = 0;
        model.Changed += () => changed++;

        model.AddSort("region", DataTableSortDirection.Ascending);
        Assert.Equal(1, changed);
        changed = 0;
        model.AddSort("amount", DataTableSortDirection.Descending);
        Assert.Equal(1, changed);
        changed = 0;

        model.SetColumnVisibility("amount", false);
        Assert.Equal(1, changed);
        changed = 0;

        model.SetGlobalFilter("north");
        Assert.Equal(1, changed);
        changed = 0;
        model.SetColumnFilter("region", "north");
        Assert.Equal(1, changed);
        changed = 0;

        model.SelectAll();
        Assert.Equal(1, changed);
        changed = 0;

        model.ResetSorting();
        Assert.Equal(1, changed);
        changed = 0;
        model.ResetFilters();
        Assert.Equal(1, changed);
        changed = 0;
        model.ResetSelection();
        Assert.Equal(1, changed);
        changed = 0;

        // No-op resets stay silent.
        model.ResetSorting();
        model.ResetFilters();
        model.ResetSelection();
        Assert.Equal(0, changed);

        // A shift-click toggle is still one logical change.
        model.AddSort("region");
        model.AddSort("amount", DataTableSortDirection.Descending);
        changed = 0;
        model.ToggleSort("region", multiColumn: true);
        Assert.Equal(1, changed);
    }

    [Fact]
    public void Header_select_all_can_target_every_filtered_row()
    {
        var model = new DataTableModel<Person>(People, new[] { NameColumn() }, person => person.Name, pageSize: 2);
        var h = Harness(() => new DataTable<Person>
        {
            Model = model,
            Selectable = true,
        });

        // Global is the default select-all scope: the header checkbox spans every filtered page.
        Assert.Equal("false", A(Checkboxes(h)[0], "aria-checked"));
        h.Click(Checkboxes(h)[0]);
        Assert.Equal(5, model.SelectedCount);
        Assert.True(model.IsAllRowsSelected);
        Assert.Equal("true", A(Checkboxes(h)[0], "aria-checked"));

        h.Click(PagerButton(h, "Go to next page"));
        var boxes = Checkboxes(h);
        Assert.Equal("true", A(boxes[0], "aria-checked"));
        Assert.Equal("true", A(boxes[1], "aria-checked"));

        h.Click(Checkboxes(h)[0]);
        Assert.Equal(0, model.SelectedCount);
        Assert.Equal("false", A(Checkboxes(h)[0], "aria-checked"));
    }

    [Fact]
    public void Hidden_columns_are_not_rendered()
    {
        var h = Harness(() => new DataTable<Person>
        {
            Rows = People,
            Columns = new[]
            {
                NameColumn(),
                new DataTableColumn<Person>
                {
                    Key = "age", Header = "Age", Value = person => person.Age, Sortable = true, Visible = false,
                },
            },
            Selectable = false,
        });

        Assert.Single(h.FindAll(e => A(e, "data-slot") == "table-head" && A(e, "data-column-key") == "name"));
        Assert.Empty(h.FindAll(e => A(e, "data-column-key") == "age"));
        Assert.Equal(new[] { "Alice", "Bob", "Carol", "Dave", "Eve" }, CellTexts(h, "name"));
    }
}
