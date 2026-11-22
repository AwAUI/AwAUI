using AwAUI.UI;

namespace AwAUI.UI.UnitTests;

/// <summary>
/// DevTools 的运行时 Tailwind 类名收集（<see cref="UIDevToolsOptions.ClassCandidates"/> /
/// <see cref="UIDevToolsOptions.ClassCandidatesRemoved"/>）：按帧比较整棵树上的类名集合，报告新出现与不再出现的
/// 类名；稳定之后不再调用回调，消失后再出现会重新报告。
/// </summary>
public class ClassCandidateObserverTests
{
    static List<string> Sorted(IReadOnlyList<string> batch) => batch.OrderBy(x => x, StringComparer.Ordinal).ToList();

    [Fact]
    public void Reports_initial_classes_once_then_only_new_class_changes()
    {
        var h = new PipelineHarness(new Div { Id = "a", Class = "alpha beta" });
        var batches = new List<IReadOnlyList<string>>();
        var observer = new ClassCandidateObserver(h.Root, batches.Add);
        h.Root.AddObserver(observer);

        observer.Flush();
        Assert.Equal(new[] { "alpha", "beta" }, Sorted(Assert.Single(batches)));

        // 没有变化时不报告空批次
        observer.Flush();
        Assert.Single(batches);

        h.El("a").SetClasses(new ClassValue("beta gamma"));
        observer.Flush();
        Assert.Equal(new[] { "gamma" }, Sorted(batches[1]));
    }

    [Fact]
    public void Reports_classes_that_leave_the_tree_and_reports_them_again_when_they_return()
    {
        var state = new State<int>(0);
        var h = new PipelineHarness(new Lambda
        {
            Render = () => new Div
            {
                Id = "host",
                Children = state.Value == 0
                    ? [new Div { Class = "alpha" }]
                    : [new Div { Class = "beta" }],
            },
        });
        var added = new List<IReadOnlyList<string>>();
        var removed = new List<IReadOnlyList<string>>();
        var observer = new ClassCandidateObserver(h.Root, added.Add, removed.Add);
        h.Root.AddObserver(observer);

        observer.Flush();
        Assert.Equal(new[] { "alpha" }, Sorted(Assert.Single(added)));

        // 换掉子树：旧类名报告移除，新类名报告新增
        state.Value = 1;
        h.Frame();
        observer.Flush();
        Assert.Equal(new[] { "alpha" }, Sorted(Assert.Single(removed)));
        Assert.Equal(new[] { "beta" }, Sorted(added[1]));

        // 切回来：同一个类名重新报告新增，移除也再次报告
        state.Value = 0;
        h.Frame();
        observer.Flush();
        Assert.Equal(new[] { "alpha" }, Sorted(added[2]));
        Assert.Equal(new[] { "beta" }, Sorted(removed[1]));
    }

    [Fact]
    public void Reports_classes_of_newly_inserted_subtrees()
    {
        var state = new State<int>(0);
        var h = new PipelineHarness(new Lambda
        {
            Render = () => new Div
            {
                Id = "host",
                Children = state.Value == 0
                    ? [new Div { Class = "first" }]
                    : [new Div { Class = "second", Children = [new Span { Class = "deep" }] }],
            },
        });
        var batches = new List<IReadOnlyList<string>>();
        var observer = new ClassCandidateObserver(h.Root, batches.Add);
        h.Root.AddObserver(observer);
        observer.Flush();
        Assert.Equal(new[] { "first" }, Sorted(Assert.Single(batches)));

        state.Value = 1;
        h.Frame();
        observer.Flush();

        Assert.Equal(new[] { "deep", "second" }, Sorted(batches[1]));
    }
}
