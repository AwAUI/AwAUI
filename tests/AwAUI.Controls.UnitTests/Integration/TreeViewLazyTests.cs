using AwAUI.Platform;
using AwAUI.UI;

namespace AwAUI.Controls.UnitTests.Integration;

/// <summary>Lazy <see cref="TreeView"/> children: on-demand loading, caching, empty / failed results and refresh.</summary>
public class TreeViewLazyTests
{
    static List<Element> Items(UIHarness h) =>
        h.FindAll(e => e.Role == Role.TreeItem)
            .OrderBy(e => e.GetBoundingClientRect().Top)
            .ToList();

    static TreeView Tree(TreeViewItem root, Func<TreeViewItem, Task<IReadOnlyList<TreeViewItem>>> loader,
        Action<TreeViewItem, Exception>? failed = null, IReadOnlyCollection<object>? expanded = null) => new()
    {
        Items = [root],
        LoadChildren = loader,
        ChildLoadFailed = failed,
        DefaultExpandedKeys = expanded,
    };

    static IReadOnlyList<TreeViewItem> Children(string prefix) =>
        [new TreeViewItem(prefix + "/a", "A"), new TreeViewItem(prefix + "/b", "B")];

    [Fact]
    public void Expanding_an_item_loads_and_mounts_its_children()
    {
        int calls = 0;
        var root = new TreeViewItem("root", "Root");
        var h = new UIHarness(() => Tree(root, item =>
        {
            calls++;
            Assert.Equal("root", item.Key);
            return Task.FromResult(Children("root"));
        }));

        Assert.Single(Items(h));
        Assert.Equal(0, calls);

        h.Tab();
        h.Key(PhysicalKey.ArrowRight, "ArrowRight");

        Assert.Equal(1, calls);
        var items = Items(h);
        Assert.Equal(3, items.Count);
        var parent = items.Single(i => i.GetAttribute("aria-level") == "1");
        Assert.Equal("true", parent.GetAttribute("aria-expanded"));
        Assert.Null(parent.GetAttribute("aria-busy"));
        Assert.Null(parent.GetAttribute("data-loading"));
        Assert.Equal(2, items.Count(i => i.GetAttribute("aria-level") == "2"));
    }

    [Fact]
    public void Loaded_children_are_cached_across_collapse_and_expand()
    {
        int calls = 0;
        var root = new TreeViewItem("root", "Root");
        var h = new UIHarness(() => Tree(root, _ =>
        {
            calls++;
            return Task.FromResult(Children("root"));
        }));

        h.Tab();
        h.Key(PhysicalKey.ArrowRight, "ArrowRight");
        Assert.Equal(3, Items(h).Count);

        h.Key(PhysicalKey.ArrowLeft, "ArrowLeft");
        Assert.Single(Items(h));
        h.Key(PhysicalKey.ArrowRight, "ArrowRight");

        Assert.Equal(1, calls);
        Assert.Equal(3, Items(h).Count);
    }

    [Fact]
    public void Items_declared_as_leaves_do_not_load()
    {
        int calls = 0;
        var root = new TreeViewItem("leaf", "Leaf", HasChildren: false);
        var h = new UIHarness(() => Tree(root, _ =>
        {
            calls++;
            return Task.FromResult(Children("leaf"));
        }));

        h.Tab();
        h.Key(PhysicalKey.ArrowRight, "ArrowRight");

        Assert.Equal(0, calls);
        Assert.Single(Items(h));
        Assert.Null(Items(h)[0].GetAttribute("aria-expanded"));
    }

    [Fact]
    public void An_empty_result_removes_the_expander()
    {
        int calls = 0;
        var root = new TreeViewItem("root", "Root");
        var h = new UIHarness(() => Tree(root, _ =>
        {
            calls++;
            return Task.FromResult<IReadOnlyList<TreeViewItem>>([]);
        }));

        h.Tab();
        h.Key(PhysicalKey.ArrowRight, "ArrowRight");

        Assert.Equal(1, calls);
        Assert.Single(Items(h));
        Assert.Null(Items(h)[0].GetAttribute("aria-expanded"));

        // 再次右移不会重复加载（已缓存为空）
        h.Key(PhysicalKey.ArrowRight, "ArrowRight");
        Assert.Equal(1, calls);
    }

    [Fact]
    public void A_failure_collapses_the_item_and_retries_on_the_next_expansion()
    {
        int calls = 0;
        bool fail = true;
        Exception? reported = null;
        var root = new TreeViewItem("root", "Root");
        var h = new UIHarness(() => Tree(root, _ =>
        {
            calls++;
            return fail
                ? Task.FromException<IReadOnlyList<TreeViewItem>>(new InvalidOperationException("boom"))
                : Task.FromResult(Children("root"));
        }, (item, error) =>
        {
            Assert.Equal("root", item.Key);
            reported = error;
        }));

        h.Tab();
        h.Key(PhysicalKey.ArrowRight, "ArrowRight");

        Assert.Equal(1, calls);
        Assert.NotNull(reported);
        Assert.Equal("boom", reported!.Message);
        Assert.Single(Items(h));
        Assert.Equal("false", Items(h)[0].GetAttribute("aria-expanded"));

        fail = false;
        h.Key(PhysicalKey.ArrowRight, "ArrowRight");

        Assert.Equal(2, calls);
        Assert.Equal(3, Items(h).Count);
    }

    [Fact]
    public void A_pending_load_marks_the_item_busy_until_it_completes()
    {
        int calls = 0;
        var completion = new TaskCompletionSource<IReadOnlyList<TreeViewItem>>();
        var root = new TreeViewItem("root", "Root");
        var h = new UIHarness(() => Tree(root, _ =>
        {
            calls++;
            return completion.Task;
        }));

        h.Tab();
        h.Key(PhysicalKey.ArrowRight, "ArrowRight");

        Assert.Equal(1, calls);
        var parent = Items(h).Single();
        Assert.Equal("true", parent.GetAttribute("aria-busy"));
        Assert.Equal("true", parent.GetAttribute("data-loading"));

        completion.SetResult(Children("root"));
        h.Frame();

        Assert.Equal(3, Items(h).Count);
        Assert.Null(Items(h)[0].GetAttribute("aria-busy"));
    }

    [Fact]
    public void RefreshChildren_reloads_an_expanded_item()
    {
        int calls = 0;
        var root = new TreeViewItem("root", "Root");
        var h = new UIHarness(() => Tree(root, _ =>
        {
            calls++;
            return Task.FromResult(Children("root"));
        }));

        h.Tab();
        h.Key(PhysicalKey.ArrowRight, "ArrowRight");
        Assert.Equal(1, calls);

        var live = h.FindAll(e => e.Role == Role.Tree).Single().Owner as TreeView;
        Assert.NotNull(live);
        live!.RefreshChildren("root");
        h.Frame();

        Assert.Equal(2, calls);
        Assert.Equal(3, Items(h).Count);
    }

    [Fact]
    public void Programmatically_expanded_items_load_during_the_build()
    {
        int calls = 0;
        var root = new TreeViewItem("root", "Root");
        var h = new UIHarness(() => Tree(root, _ =>
        {
            calls++;
            return Task.FromResult(Children("root"));
        }, expanded: ["root"]));

        Assert.Equal(1, calls);
        Assert.Equal(3, Items(h).Count);
        Assert.Equal("true", Items(h).Single(i => i.GetAttribute("aria-level") == "1").GetAttribute("aria-expanded"));
    }
}
