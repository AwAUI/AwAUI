namespace AwAUI.Markup.UnitTests;

/// <summary>测试共用的 .awaui 样例。</summary>
static class Samples
{
    public const string TodoApp = """
        @* TodoApp.awaui *@
        @using MyApp.Models

        <div class="todo-app">
            <text class="title">待办事项（@todos.Value.Count）</text>

            <div class="row">
                <input @bind="draft" placeholder="要做什么？" @onsubmit="Add" />
                <Button class="primary" @onclick="Add">添加</Button>
            </div>

            @if (todos.Value.Count == 0)
            {
                <text class="empty">暂无事项</text>
            }
            else
            {
                <div class="list">
                    @foreach (var t in todos.Value)
                    {
                        <TodoRow @key="t.Id" Todo="t" OnRemove="Remove" />
                    }
                </div>
            }
        </div>

        @code {
            public string Title { get; set; } = "待办";          // Props：公共可写属性

            readonly State<List<Todo>> todos = new(new List<Todo>());
            readonly State<string> draft = new("");

            void Add()
            {
                if (draft.Value.Length == 0) return;
                todos.Update(l => l.Append(new Todo(Guid.NewGuid(), draft.Value)).ToList());
                draft.Value = "";
            }

            void Remove(Todo t) => todos.Update(l => l.Where(x => x.Id != t.Id).ToList());
        }

        <style>
            .todo-app { display: flex; flex-direction: column; gap: 12px; padding: 16px; }
            .row { display: flex; gap: 8px; }
            .row > input { flex: 1; }
            .empty { color: var(--text-secondary); }
        </style>
        """;

    public const string Counter = """
        <div class="counter">
            <text>计数：@count.Value</text>
            @if (count.Value > 0)
            {
                <Button @onclick="Reset">重置</Button>
            }
        </div>

        @code {
            readonly State<int> count = new State<int>(0);
            void Reset() => count.Value = 0;
        }
        """;

    /// <summary>控制流、事件 lambda 与多行表达式（列对齐与 C# 7.3 解析测试用；@code 只用 C# 7.3 语法）。</summary>
    public const string Flow = """
        @using System.Text
        <div class="flow" @onclick="e => Clicked(e)">
            @foreach (var item in items.Value)
            {
                <div @key="item" class="item">@item</div>
            }
            @if (items.Value.Count == 0) { <text>空</text> } else if (items.Value.Count > 9) { <text>多</text> }
            @switch (mode)
            {
                case 1:
                    <span>一</span>
                    break;
                default:
                    break;
            }
            @{ var total = items.Value.Count; }
            <text title="共 @total 项">@(total
                + 1)</text>
        </div>
        @code {
            readonly State<List<string>> items = new State<List<string>>(new List<string>());
            int mode;
            void Clicked(PointerEvent e) { mode++; }
        }
        """;
}
