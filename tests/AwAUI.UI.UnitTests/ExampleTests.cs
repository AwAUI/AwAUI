using AwAUI.Platform;

namespace AwAUI.UI.UnitTests;

// ------------------------------------------------------------ design/05 的 Counter

public partial class Counter : Component
{
    public int Step { get; init; } = 1;            // Props：父组件传入，不可变

    readonly State<int> count = new(0);            // State：组件实例持有，跨重建保留

    protected override Node Build() =>
        new Div
        {
            Class = "counter", Children =
            [
                new Text($"当前计数：{count.Value}"),
                new Button
                {
                    Class = "primary",
                    OnClick = _ => count.Value += Step,
                    Children = ["增加"],
                },
            ]
        };
}

// ------------------------------------------------------------ design/12 的 TodoApp

public sealed record Todo(Guid Id, string Title);

public partial class TodoApp : Component
{
    readonly State<List<Todo>> todos = new(new List<Todo>());
    readonly State<string> draft = new("");

    void Add()
    {
        if (draft.Value.Length == 0) return;
        todos.Update(list => list.Append(new Todo(Guid.NewGuid(), draft.Value)).ToList());
        draft.Value = "";
    }

    void Remove(Todo t) => todos.Update(l => l.Where(x => x.Id != t.Id).ToList());

    protected override Node Build() =>
        new Div
        {
            Class = "todo-app", Children =
            [
                new Text("待办事项") { Class = "title" },

                new Div
                {
                    Class = "row", Children =
                    [
                        new Input
                        {
                            Value = draft.Value,
                            Placeholder = "要做什么？",
                            OnInput = e => draft.Value = e.Value,
                            OnSubmit = _ => Add(),
                        },
                        new Button { Class = "primary", OnClick = _ => Add(), Children = ["添加"] },
                    ]
                },

                todos.Value.Count == 0
                    ? new Text("暂无事项") { Class = "empty" }
                    : new Div
                    {
                        Class = "list", Children =
                        [
                            .. todos.Value.Select(t => new TodoRow { Key = t.Id, Todo = t, OnRemove = Remove }),
                        ]
                    },
            ]
        };
}

public partial class TodoRow : Component
{
    public Todo Todo { get; set; } = null!;

    public Action<Todo>? OnRemove { get; set; }

    public int Builds { get; private set; }

    protected override Node Build()
    {
        Builds++;
        return new Div
        {
            Class = "todo-row", Children =
            [
                new Text(Todo.Title) { Class = "todo-title" },
                new Button { Class = "remove", OnClick = _ => OnRemove?.Invoke(Todo), Children = ["删除"] },
            ]
        };
    }
}

public class ExampleTests
{
    [Fact]
    public void Counter_increments_on_click()
    {
        var root = new UIRoot();
        root.Mount(new Counter { Step = 2 });
        var counter = root.RootElement.ByClass("counter");
        Assert.Equal("div", counter.Tag);
        Assert.Equal("当前计数：0", ((TextElement)counter.ByTag("text")).Content);

        var button = counter.ByTag("button");
        Assert.Equal("增加", button.TextContent);
        root.Input.Click(button);
        Assert.True(root.NeedsFrame);
        root.RunFrame();
        Assert.Equal("当前计数：2", ((TextElement)counter.ByTag("text")).Content);

        // 同一个按钮元素被保留（只更新了文本）
        Assert.Same(button, counter.ByTag("button"));
        Assert.False(root.NeedsFrame);
    }

    [Fact]
    public void Counter_button_activates_with_keyboard()
    {
        var root = new UIRoot();
        root.Mount(new Counter());
        var button = root.RootElement.ByTag("button");
        root.Press(PhysicalKey.Tab);
        Assert.Same(button, root.FocusManager.FocusedElement);
        Assert.True(button.States.HasFlag(PseudoStates.FocusVisible));
        root.Press(PhysicalKey.Enter);
        root.Press(PhysicalKey.Space);
        root.RunFrame();
        Assert.Equal("当前计数：2", root.RootElement.ByTag("text").TextContent);
    }

    [Fact]
    public void TodoApp_add_type_submit_and_remove()
    {
        var root = new UIRoot();
        root.Mount(new TodoApp());
        var app = root.RootElement.ByClass("todo-app");
        Assert.Equal("待办事项", app.ByClass("title").TextContent);
        Assert.Equal("暂无事项", app.ByClass("empty").TextContent);
        var input = (InputElement)app.ByTag("input");
        Assert.True(input.States.HasFlag(PseudoStates.PlaceholderShown));

        // 指针聚焦输入框、输入文字、回车提交
        root.Input.Click(input);
        Assert.True(input.IsFocused);
        root.Input.DispatchTextInput("买牛奶");
        root.RunFrame();
        Assert.Equal("买牛奶", input.Value);
        Assert.False(input.States.HasFlag(PseudoStates.PlaceholderShown));
        root.Press(PhysicalKey.Enter);
        root.RunFrame();

        Assert.Empty(app.AllByClass("empty"));
        var rows = app.AllByClass("todo-row");
        Assert.Single(rows);
        Assert.Equal("买牛奶", rows[0].ByClass("todo-title").TextContent);
        Assert.Equal("", input.Value); // draft 被清空，受控输入框随之更新
        Assert.Same(input, app.ByTag("input"));

        // 第二项：用「添加」按钮
        root.Input.DispatchTextInput("写代码");
        root.Input.Click(app.ByClass("primary"));
        root.RunFrame();
        Assert.Equal(new[] { "买牛奶", "写代码" }, app.AllByClass("todo-title").Select(e => e.TextContent).ToArray());

        // 删除第一项
        var firstRowElement = app.AllByClass("todo-row")[0];
        var secondRowElement = app.AllByClass("todo-row")[1];
        root.Input.Click(firstRowElement.ByClass("remove"));
        root.RunFrame();
        var remaining = app.AllByClass("todo-row");
        Assert.Single(remaining);
        Assert.Same(secondRowElement, remaining[0]); // 按 Key 保留了第二项的元素
        Assert.Equal("写代码", remaining[0].ByClass("todo-title").TextContent);

        root.Input.Click(remaining[0].ByClass("remove"));
        root.RunFrame();
        Assert.Equal("暂无事项", app.ByClass("empty").TextContent);
    }

    [Fact]
    public void TodoApp_rows_with_equal_props_are_not_rebuilt()
    {
        var root = new UIRoot();
        root.Mount(new TodoApp());
        var app = root.RootElement.ByClass("todo-app");
        var input = app.ByTag("input");
        input.Focus();
        foreach (var title in new[] { "a", "b", "c" })
        {
            root.Input.DispatchTextInput(title);
            root.Press(PhysicalKey.Enter);
            root.RunFrame();
        }
        var rows = CollectRows(root);
        Assert.Equal(3, rows.Count);
        var builds = rows.Select(r => r.Builds).ToArray();

        // 只改 draft：TodoApp 重建，行组件的 Props（同一 Todo、方法组委托 Delegate.Equals 相等）不变 → 跳过
        root.Input.DispatchTextInput("x");
        root.RunFrame();
        Assert.Equal(builds, CollectRows(root).Select(r => r.Builds).ToArray());
    }

    static List<TodoRow> CollectRows(UIRoot root) =>
        root.RootElement.AllByClass("todo-row").Select(e => (TodoRow)e.Owner!).ToList();
}
