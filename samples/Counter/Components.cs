using System;
using AwAUI.UI;

namespace CounterSample;

/// <summary>
/// design/05 的计数器：显示计数、「增加」与「减少」按钮。<see cref="Step"/> 是可写 Props（父组件改变它时原地采纳，计数保留）。
/// 外观全部来自 Styles/Counter.css。
/// <para>
/// Markup/ 目录下是同一个应用的 <c>.awaui</c> 写法（命名空间 <c>CounterSample.Markup</c>），两者产出相同的元素树
/// （tests/AwAUI.Samples.Tests 逐帧比较元素树与渲染结果）。文字一律作为子节点（<c>Children = [...]</c>），与标记中
/// <c>&lt;text&gt;@表达式&lt;/text&gt;</c> 生成的结构一致。
/// </para>
/// </summary>
public partial class Counter : Component
{
    public int Step { get; set; } = 1;

    readonly State<int> count = new(0);

    protected override Node Build() =>
        new Div
        {
            Class = "counter", Children =
            [
                new Text { Class = "count", Id = "count", Children = [Strings.Current.Count + count.Value] },
                new Div
                {
                    Class = "actions", Children =
                    [
                        new Button { Class = "secondary", Id = "decrement", OnClick = _ => count.Value -= Step, Children = [Strings.Current.Decrement] },
                        new Button { Class = "primary", Id = "increment", OnClick = _ => count.Value += Step, Children = [Strings.Current.Increment] },
                    ],
                },
            ],
        };
}

/// <summary>小表单：受控输入框 + 提交按钮（回车或点击），提交后显示问候语。</summary>
public partial class GreetingForm : Component
{
    readonly State<string> name = new("");
    readonly State<string?> greeting = new((string?)null);

    void Submit()
    {
        var n = name.Value.Trim();
        greeting.Value = n.Length == 0 ? Strings.Current.EmptyName : string.Format(Strings.Current.Hello, n);
    }

    protected override Node Build() =>
        new Div
        {
            Class = "card", Children =
            [
                new Text { Class = "title", Children = [Strings.Current.FormTitle] },
                new Div
                {
                    Class = "field", Children =
                    [
                        new Input
                        {
                            Id = "name",
                            Value = name.Value,
                            Placeholder = Strings.Current.NamePlaceholder,
                            OnInput = e => name.Value = e.Value,
                            OnSubmit = _ => Submit(),
                        },
                        new Button { Class = "primary small", Id = "greet", OnClick = _ => Submit(), Children = [Strings.Current.Greet] },
                    ],
                },
                greeting.Value is { } g
                    ? new Text { Class = "greeting", Id = "greeting", Children = [g] }
                    : new Text { Class = "hint", Children = [Strings.Current.FormHint] },
            ],
        };
}

/// <summary>根组件：计数器卡片（含步长选择）+ 表单卡片。</summary>
public partial class App : Component
{
    readonly State<int> step = new(1);

    Node StepButton(int value) => new Button
    {
        Key = value,
        Class = Cx("chip", ("selected", step.Value == value)),
        OnClick = _ => step.Value = value,
        Children = ["×" + value],
    };

    static ClassValue Cx(string name, (string Name, bool On) flag) => ClassValue.Cx(name, flag);

    protected override Node Build() =>
        new Div
        {
            Class = "app", Children =
            [
                new Div
                {
                    Class = "card", Children =
                    [
                        new Text { Class = "title", Children = [Strings.Current.CounterTitle] },
                        new Counter { Step = step.Value },
                        new Div
                        {
                            Class = "field", Children =
                            [
                                new Text { Class = "label", Children = [Strings.Current.StepLabel] },
                                StepButton(1),
                                StepButton(5),
                                StepButton(10),
                            ],
                        },
                    ],
                },
                new GreetingForm(),
            ],
        };
}

/// <summary>界面文字。没有可用的中日韩字体时（只注册了 Noto Sans 拉丁字体）使用英文，避免显示缺字方框。</summary>
public sealed class Strings
{
    public static readonly Strings Chinese = new()
    {
        CounterTitle = "计数器", Count = "当前计数：", Increment = "增加", Decrement = "减少", StepLabel = "步长",
        FormTitle = "问候", NamePlaceholder = "你的名字", Greet = "问好", Hello = "你好，{0}！", EmptyName = "请先输入名字。",
        FormHint = "输入名字后按回车或点「问好」。",
    };

    public static readonly Strings English = new()
    {
        CounterTitle = "Counter", Count = "Count: ", Increment = "Increment", Decrement = "Decrement", StepLabel = "Step",
        FormTitle = "Greeting", NamePlaceholder = "Your name", Greet = "Greet", Hello = "Hello, {0}!", EmptyName = "Please enter a name.",
        FormHint = "Type a name, then press Enter or Greet.",
    };

    /// <summary>当前使用的文字（静态字段按声明顺序初始化，所以放在两套文字之后）。</summary>
    public static Strings Current { get; set; } = Chinese;

    public string CounterTitle { get; init; } = "";
    public string Count { get; init; } = "";
    public string Increment { get; init; } = "";
    public string Decrement { get; init; } = "";
    public string StepLabel { get; init; } = "";
    public string FormTitle { get; init; } = "";
    public string NamePlaceholder { get; init; } = "";
    public string Greet { get; init; } = "";
    public string Hello { get; init; } = "";
    public string EmptyName { get; init; } = "";
    public string FormHint { get; init; } = "";
}
