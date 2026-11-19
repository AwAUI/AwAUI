using AwAUI.UI;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using RoslynSeverity = Microsoft.CodeAnalysis.DiagnosticSeverity;

namespace AwAUI.Markup.UnitTests;

/// <summary>
/// 端到端（design/17「端到端」）：真实的生成器把 <c>.awaui</c> 编译成组件，与等价的手写 C# 组件放在同一份编译里（手写的由 C# 胶水生成器产出 Props 胶水），
/// 编译通过后加载、用 <see cref="UIRoot"/> 挂载、模拟输入，断言两者产出完全相同的元素树。
/// </summary>
public class EndToEndTests
{
    // ------------------------------------------------------------ .awaui 版本

    const string CounterAwaui = """
        <div class="counter">
            <text>计数：@count.Value</text>
            <Button class="inc" @onclick="() => count.Value += Step">增加</Button>
            @if (count.Value > 0)
            {
                <Button class="reset" @onclick="Reset">重置</Button>
            }
        </div>

        @code {
            public int Step { get; set; } = 1;

            readonly State<int> count = new(0);

            void Reset() => count.Value = 0;
        }
        """;

    const string CardAwaui = """
        <div class="card">
            <div class="header">@Header</div>
            <text class="title">@Title</text>
            <div class="body">@Children</div>
        </div>

        @code {
            public string Title { get; set; } = "";
            public NodeList? Header { get; set; }
            public NodeList? Children { get; set; }
        }
        """;

    const string RepeaterAwaui = """
        @typeparam T
        <div class="repeater">
            @foreach (var item in Items)
            {
                @ItemTemplate?.Invoke(item)
            }
        </div>

        @code {
            public IReadOnlyList<T> Items { get; set; } = Array.Empty<T>();
            public Func<T, Node>? ItemTemplate { get; set; }
        }
        """;

    // 位于 Pages 目录：命名空间 TestApp.Pages，经外层命名空间找到 TestApp.Counter / Card / Repeater
    const string DashboardAwaui = """
        <div class="dashboard">
            <Card Title="你好，@name.Value！">
                <Header><text class="h">头部</text></Header>
                正文 @step.Value
                <Counter Step="step.Value" />
            </Card>
            <Button class="step" @onclick="e => Bump(e)">加步长</Button>
            <Repeater<string> Items="labels">
                <ItemTemplate Context="label">
                    <span class="label" data-text="@label">@label</span>
                </ItemTemplate>
            </Repeater>
        </div>

        @code {
            readonly State<string> name = new("世界");
            readonly State<int> step = new(1);
            readonly string[] labels = { "a", "b" };

            void Bump(PointerEvent e) => step.Value++;
        }
        """;

    const string FlowAwaui = """
        <div class="flow">
            @if (items.Value.Count == 0)
            {
                <text class="empty">空</text>
            }
            else
            {
                @foreach (var item in items.Value)
                {
                    <div @key="item.Id" class="item" role="listitem" tabindex="0" @onclick="() => Remove(item)">@item.Name</div>
                }
            }
            @switch (mode.Value)
            {
                case 0:
                    <text class="mode">零</text>
                    break;
                case int n when n > 5:
                    <text class="mode">大 @n</text>
                    break;
                default:
                    <text class="mode">其他</text>
                    break;
            }
            <Button class="reverse" @onclick="Reverse">反转</Button>
            <Button class="mode-up" @onclick="() => mode.Value += 3">模式</Button>
            <div class="keys" tabindex="0" @onkeydown="OnKey" @onpointerdown="() => downs.Value++">@downs.Value/@lastKey.Value</div>
        </div>

        @code {
            public sealed class Item
            {
                public Item(int id, string name) { Id = id; Name = name; }
                public int Id { get; }
                public string Name { get; }
            }

            readonly State<List<Item>> items = new(new List<Item> { new(1, "一"), new(2, "二"), new(3, "三") });
            readonly State<int> mode = new(0);
            readonly State<int> downs = new(0);
            readonly State<string> lastKey = new("");

            void Reverse() => items.Value = Enumerable.Reverse(items.Value).ToList();
            void Remove(Item item) => items.Value = items.Value.Where(i => i != item).ToList();
            void OnKey(KeyEvent e) => lastKey.Value = e.Key;
        }
        """;

    const string StepperAwaui = """
        <Button class="stepper" @onclick="() => ValueChanged?.Invoke(Value + 1)">@Value</Button>

        @code {
            public int Value { get; set; }
            public Action<int>? ValueChanged { get; set; }
        }
        """;

    const string FormAwaui = """
        <div class="form">
            <input class="name" @bind="draft" placeholder="名字" />
            <input class="title" @bind="model.Title" @bind:event="change" />
            <text class="echo">@draft.Value|@model.Title</text>
            <Stepper @bind-Value="level" />
            <text class="level">@level.Value</text>
        </div>

        @code {
            public sealed class Model
            {
                public string Title { get; set; } = "t";
            }

            readonly State<string> draft = new("");
            readonly Model model = new();
            readonly State<int> level = new(0);

            public string ModelTitle => model.Title;
        }
        """;

    // ------------------------------------------------------------ 等价的手写 C#

    const string Handwritten = """
        using System;
        using System.Collections.Generic;
        using System.Linq;
        using AwAUI.UI;

        namespace TestApp.Handwritten;

        public partial class Counter : Component
        {
            public int Step { get; set; } = 1;
            readonly State<int> count = new(0);
            void Reset() => count.Value = 0;

            protected override Node Build()
            {
                var children = new NodeList
                {
                    new Text { Children = { "计数：", count.Value.ToString() } },
                    new Button { Class = "inc", OnClick = _ => count.Value += Step, Children = { "增加" } },
                };
                if (count.Value > 0) children.Add(new Button { Class = "reset", OnClick = _ => Reset(), Children = { "重置" } });
                return new Div { Class = "counter", Children = children };
            }
        }

        public partial class Card : Component
        {
            public string Title { get; set; } = "";
            public NodeList? Header { get; set; }
            public NodeList? Children { get; set; }

            protected override Node Build() =>
                new Div
                {
                    Class = "card",
                    Children =
                    {
                        new Div { Class = "header", Children = { Node.From(Header) } },
                        new Text { Class = "title", Children = { Title } },
                        new Div { Class = "body", Children = { Node.From(Children) } },
                    },
                };
        }

        public partial class Repeater<T> : Component
        {
            public IReadOnlyList<T> Items { get; set; } = Array.Empty<T>();
            public Func<T, Node>? ItemTemplate { get; set; }

            protected override Node Build()
            {
                var list = new NodeList();
                foreach (var item in Items) list.Add(ItemTemplate?.Invoke(item));
                return new Div { Class = "repeater", Children = list };
            }
        }

        public partial class Dashboard : Component
        {
            readonly State<string> name = new("世界");
            readonly State<int> step = new(1);
            readonly string[] labels = { "a", "b" };

            void Bump(PointerEvent e) => step.Value++;

            protected override Node Build() =>
                new Div
                {
                    Class = "dashboard",
                    Children =
                    {
                        new Card
                        {
                            Title = "你好，" + name.Value + "！",
                            Header = new NodeList { new Text { Class = "h", Children = { "头部" } } },
                            Children = new NodeList { "正文 ", step.Value.ToString(), new Counter { Step = step.Value } },
                        },
                        new Button { Class = "step", OnClick = e => Bump(e), Children = { "加步长" } },
                        new Repeater<string>
                        {
                            Items = labels,
                            ItemTemplate = label => new Span { Class = "label", Attributes = { ["data-text"] = label }, Children = { label } },
                        },
                    },
                };
        }

        public partial class Flow : Component
        {
            public sealed class Item
            {
                public Item(int id, string name) { Id = id; Name = name; }
                public int Id { get; }
                public string Name { get; }
            }

            readonly State<List<Item>> items = new(new List<Item> { new(1, "一"), new(2, "二"), new(3, "三") });
            readonly State<int> mode = new(0);
            readonly State<int> downs = new(0);
            readonly State<string> lastKey = new("");

            void Reverse() => items.Value = Enumerable.Reverse(items.Value).ToList();
            void Remove(Item item) => items.Value = items.Value.Where(i => i != item).ToList();
            void OnKey(KeyEvent e) => lastKey.Value = e.Key;

            protected override Node Build()
            {
                var children = new NodeList();
                if (items.Value.Count == 0) children.Add(new Text { Class = "empty", Children = { "空" } });
                else
                {
                    foreach (var item in items.Value)
                        children.Add(new Div { Key = item.Id, Class = "item", Role = Role.ListItem, TabIndex = 0, OnClick = _ => Remove(item), Children = { item.Name } });
                }
                children.Add(mode.Value switch
                {
                    0 => new Text { Class = "mode", Children = { "零" } },
                    int n when n > 5 => new Text { Class = "mode", Children = { "大 ", n.ToString() } },
                    _ => new Text { Class = "mode", Children = { "其他" } },
                });
                children.Add(new Button { Class = "reverse", OnClick = _ => Reverse(), Children = { "反转" } });
                children.Add(new Button { Class = "mode-up", OnClick = _ => mode.Value += 3, Children = { "模式" } });
                children.Add(new Div
                {
                    Class = "keys",
                    TabIndex = 0,
                    OnKeyDown = OnKey,
                    OnPointerDown = _ => downs.Value++,
                    Children = { downs.Value.ToString(), "/", lastKey.Value },
                });
                return new Div { Class = "flow", Children = children };
            }
        }

        public partial class Stepper : Component
        {
            public int Value { get; set; }
            public Action<int>? ValueChanged { get; set; }

            protected override Node Build() =>
                new Button { Class = "stepper", OnClick = _ => ValueChanged?.Invoke(Value + 1), Children = { Value.ToString() } };
        }

        public partial class Form : Component
        {
            public sealed class Model
            {
                public string Title { get; set; } = "t";
            }

            readonly State<string> draft = new("");
            readonly Model model = new();
            readonly State<int> level = new(0);

            public string ModelTitle => model.Title;

            protected override Node Build() =>
                new Div
                {
                    Class = "form",
                    Children =
                    {
                        new Input { Class = "name", Value = draft.Value, OnInput = e => draft.Value = e.Value, Placeholder = "名字" },
                        new Input { Class = "title", Value = model.Title, OnChange = e => model.Title = e.Value },
                        new Text { Class = "echo", Children = { draft.Value, "|", model.Title } },
                        new Stepper { Value = level.Value, ValueChanged = v => level.Value = v },
                        new Text { Class = "level", Children = { level.Value.ToString() } },
                    },
                };
        }
        """;

    static GeneratorHarness App() => new GeneratorHarness()
        .Awaui("Counter.awaui", CounterAwaui)
        .Awaui("Card.awaui", CardAwaui)
        .Awaui("Repeater.awaui", RepeaterAwaui)
        .Awaui("Pages/Dashboard.awaui", DashboardAwaui)
        .Awaui("Flow.awaui", FlowAwaui)
        .Awaui("Stepper.awaui", StepperAwaui)
        .Awaui("Form.awaui", FormAwaui)
        .CSharp("Handwritten.cs", Handwritten);

    static readonly Lazy<GeneratorHarness.Result> AppResult = new(() => App().Run());

    /// <summary>同时挂载 .awaui 版本与手写版本，每一步之后比较元素树。</summary>
    sealed class Pair
    {
        public Pair(LoadedAssembly asm, string markupType, string handwrittenType)
        {
            Markup = new UIRoot();
            Markup.Mount(asm.Create(markupType));
            Hand = new UIRoot();
            Hand.Mount(asm.Create(handwrittenType));
            AssertSame();
        }

        public UIRoot Markup { get; }
        public UIRoot Hand { get; }

        public string Dump => TreeDump.Dump(Markup.RootElement);

        public void AssertSame() => Assert.Equal(TreeDump.Dump(Hand.RootElement), TreeDump.Dump(Markup.RootElement));

        /// <summary>在两棵树中按 class 找到元素并执行同样的操作，然后各跑一帧。</summary>
        public void Do(string cls, Action<UIRoot, Element> action, int index = 0)
        {
            action(Markup, Markup.RootElement.FindAll(e => e.HasClass(cls))[index]);
            action(Hand, Hand.RootElement.FindAll(e => e.HasClass(cls))[index]);
            Markup.RunUntilIdle();
            Hand.RunUntilIdle();
            AssertSame();
        }

        public void Click(string cls, int index = 0) => Do(cls, (r, e) => r.Input.Click(e), index);
    }

    [Fact]
    public void App_compiles_without_errors_or_warnings()
    {
        var r = AppResult.Value;
        r.AssertNoErrors();
        var warnings = r.AllDiagnostics.Where(d => d.Severity == RoslynSeverity.Warning).ToList();
        if (warnings.Count > 0) Assert.Fail(string.Join("\n", warnings.Select(w => w + " @ " + w.Location.SourceTree?.GetText().Lines.GetLineFromPosition(w.Location.SourceSpan.Start))));
        Assert.True(warnings.Count == 0, string.Join("\n", warnings));
        // 每个 .awaui 一个生成文件；手写组件由 C# 胶水生成器各生成一个；两者不会为同一个类生成胶水
        Assert.Equal(7, r.Run.GeneratedTrees.Count(t => t.FilePath.Contains(".awaui.", StringComparison.Ordinal)));
        Assert.DoesNotContain(r.Run.GeneratedTrees, t => t.FilePath.EndsWith("TestApp.Counter.Props.g.cs", StringComparison.Ordinal));
        Assert.Contains(r.Run.GeneratedTrees, t => t.FilePath.EndsWith("TestApp.Handwritten.Counter.Props.g.cs", StringComparison.Ordinal));
    }

    [Fact]
    public void Counter_state_and_click_match_handwritten()
    {
        using var asm = AppResult.Value.Load();
        var p = new Pair(asm, "TestApp.Counter", "TestApp.Handwritten.Counter");
        Assert.Equal("div.counter\n  text\n    '计数：'\n    '0'\n  button.inc\n    '增加'\n", p.Dump);
        p.Click("inc");
        p.Click("inc");
        Assert.Contains("'2'", p.Dump);
        Assert.Contains("button.reset", p.Dump);
        var inc = p.Markup.RootElement.ByClass("inc");
        p.Click("reset");
        Assert.Contains("'0'", p.Dump);
        Assert.DoesNotContain("reset", p.Dump);
        Assert.Same(inc, p.Markup.RootElement.ByClass("inc")); // 元素被保留，只更新了文本
    }

    [Fact]
    public void Props_child_content_slots_and_templates_match_handwritten()
    {
        using var asm = AppResult.Value.Load();
        var p = new Pair(asm, "TestApp.Pages.Dashboard", "TestApp.Handwritten.Dashboard");
        Assert.Equal("""
            div.dashboard
              div.card
                div.header
                  text.h
                    '头部'
                text.title
                  '你好，世界！'
                div.body
                  '正文 '
                  '1'
                  div.counter
                    text
                      '计数：'
                      '0'
                    button.inc
                      '增加'
              button.step
                '加步长'
              div.repeater
                span.label[data-text=a]
                  'a'
                span.label[data-text=b]
                  'b'

            """.Replace("\r\n", "\n"), p.Dump);

        // 子组件的 State 在父组件重建（Props 变化）后保留：Step 由 AdoptProps 原地更新
        p.Click("inc");
        Assert.Contains("'1'\n", p.Dump);
        p.Click("step");
        p.Click("inc");
        Assert.Contains("      '3'\n", p.Dump); // 1 + 新的步长 2
        Assert.Contains("'2'\n", p.Dump);       // 正文中的步长
    }

    [Fact]
    public void Control_flow_keys_and_event_lambdas_match_handwritten()
    {
        using var asm = AppResult.Value.Load();
        var p = new Pair(asm, "TestApp.Flow", "TestApp.Handwritten.Flow");
        var first = p.Markup.RootElement.FindAll(e => e.HasClass("item"))[0];
        Assert.Equal("一", first.TextContent);
        Assert.Equal("listitem", first.GetAttribute("role"));

        p.Click("reverse");
        var items = p.Markup.RootElement.FindAll(e => e.HasClass("item"));
        Assert.Equal(new[] { "三", "二", "一" }, items.Select(e => e.TextContent));
        Assert.Same(first, items[2]); // 带 @key：元素被移动而不是重建

        p.Click("item", 1); // 删除「二」（lambda 捕获循环变量）
        Assert.Equal(new[] { "三", "一" }, p.Markup.RootElement.FindAll(e => e.HasClass("item")).Select(e => e.TextContent));

        p.Click("mode-up");
        Assert.Contains("'其他'", p.Dump);
        p.Click("mode-up");
        Assert.True(p.Dump.Contains("'大 '\n    '6'"), p.Dump);

        p.Do("keys", (r, e) =>
        {
            r.FocusManager.Focus(e);
            r.Input.DispatchKey(new AwAUI.Platform.RawKeyEvent(AwAUI.Platform.RawKeyEventType.Down, AwAUI.Platform.PhysicalKey.KeyA,
                AwAUI.Platform.KeyModifiers.None, false, 0, TimeSpan.Zero), "a");
        });
        p.Click("keys");
        Assert.True(p.Dump.Contains("'1'\n    '/'\n    'a'"), p.Dump);

        p.Click("item");
        p.Click("item");
        Assert.Contains("text.empty", p.Dump);
    }

    [Fact]
    public void Bind_is_two_way_for_state_assignable_targets_and_components()
    {
        using var asm = AppResult.Value.Load();
        var p = new Pair(asm, "TestApp.Form", "TestApp.Handwritten.Form");

        // @bind="draft"（State<string>）：每次输入
        p.Do("name", (r, e) =>
        {
            r.FocusManager.Focus(e);
            r.Input.DispatchTextInput("ab");
        });
        Assert.Contains("'ab'\n    '|'\n    't'", p.Dump);
        Assert.Equal("ab", ((InputElement)p.Markup.RootElement.ByClass("name")).Value);

        // @bind="model.Title" @bind:event="change"（可赋值表达式）：失焦时提交
        p.Do("title", (r, e) =>
        {
            r.FocusManager.Focus(e);
            r.Input.DispatchTextInput("X");
            r.FocusManager.ClearFocus();
        });
        var markupForm = p.Markup.RootNode!;
        Assert.Equal("tX", (string)markupForm.GetType().GetProperty("ModelTitle")!.GetValue(markupForm)!);

        // 组件上的 @bind-Value：Value + ValueChanged
        p.Click("stepper");
        p.Click("stepper");
        Assert.Contains("text.level\n    '2'", p.Dump);
        Assert.Contains("button.stepper\n    '2'", p.Dump);
    }

    [Fact]
    public void Generated_bind_expansion_distinguishes_state_from_assignable_targets()
    {
        string cs = AppResult.Value.Generated("Form.awaui");
        Assert.Contains("global::AwAUI.UI.Binding.Set(draft, __e.Value)", cs);
        Assert.Contains("OnChange = __e => model.Title = __e.Value;", cs);
        Assert.Contains("ValueChanged = __v => global::AwAUI.UI.Binding.Set(level, __v);", cs);
    }

    // ------------------------------------------------------------ 作用域样式

    const string StyledAwaui = """
        <div class="box">
            <text class="t">x</text>
            <Button class="b">按钮</Button>
            <Card Title="c" />
        </div>

        <style>
            .box > .t::before { content: "»"; }
            .box { color: red; }
            @media (min-width: 100px) { .t:hover, :is(.a, .b) { color: blue } }
        </style>

        <style global>
            .global-thing { color: green; }
        </style>
        """;

    [Fact]
    public void Scoped_style_is_exposed_and_marks_only_own_elements()
    {
        var r = new GeneratorHarness().Awaui("Styled.awaui", StyledAwaui).Awaui("Card.awaui", CardAwaui).Run();
        using var asm = r.Load();
        var type = asm.Type("TestApp.Styled");
        string scope = (string)type.GetField("__StyleScope")!.GetRawConstantValue()!;
        string css = (string)type.GetField("StyleSheetSource")!.GetRawConstantValue()!;
        string global = (string)type.GetField("GlobalStyleSheetSource")!.GetRawConstantValue()!;
        Assert.Matches("^aw-[0-9a-f]{8}$", scope);
        Assert.Contains($".box > .t[{scope}]::before", css);
        Assert.Contains($".box[{scope}] {{ color: red; }}", css);
        Assert.Contains($".t:hover[{scope}], :is(.a, .b)[{scope}]", css);
        Assert.Contains(".global-thing { color: green; }", global);
        Assert.Null(asm.Type("TestApp.Card").GetField("__StyleScope"));

        var root = new UIRoot();
        root.Mount(asm.Create("TestApp.Styled"));
        Assert.Equal(scope, root.RootElement.ByClass("box").Scope);
        Assert.Equal(scope, root.RootElement.ByClass("t").Scope);
        Assert.Equal(scope, root.RootElement.ByClass("b").Scope); // 大写标签解析到的元素描述类型也属于本组件
        Assert.Equal("", root.RootElement.ByClass("t").GetAttribute(scope));
        Assert.Null(root.RootElement.ByClass("card").Scope);     // 子组件 Build() 产出的元素不带本组件的作用域
        Assert.Null(root.RootElement.ByClass("title").Scope);
    }

    [Fact]
    public void Styled_component_inheriting_a_styled_component_compiles_without_warnings()
    {
        // 两者都有 <style>：派生类的样式常量隐藏基类的同名常量，曾报 CS0108（TreatWarningsAsErrors 的项目里是错误）
        var r = new GeneratorHarness()
            .Awaui("Base.awaui", "<div class=\"base\">@A</div>\n<style>.base { color: red; }</style>\n@code { public int A { get; set; } }")
            .Awaui("Derived.awaui", "@inherits Base\n<div class=\"derived\">@A/@B</div>\n<style>.derived { color: blue; }</style>\n<style global>.g { }</style>\n@code { public int B { get; set; } }")
            .Run();
        r.AssertNoErrors();
        Assert.DoesNotContain(r.AllDiagnostics, d => d.Severity >= RoslynSeverity.Warning);
        using var asm = r.Load();
        string Const(string type, string name) => (string)asm.Type(type).GetField(name)!.GetRawConstantValue()!;
        string baseScope = Const("TestApp.Base", "__StyleScope"), derivedScope = Const("TestApp.Derived", "__StyleScope");
        Assert.NotEqual(baseScope, derivedScope);
        Assert.Contains($".derived[{derivedScope}]", Const("TestApp.Derived", "StyleSheetSource"));
        var root = new UIRoot();
        root.Mount(asm.Create("TestApp.Derived"));
        Assert.Equal(derivedScope, root.RootElement.ByClass("derived").Scope);
    }

    // ------------------------------------------------------------ 语言版本与可空

    [Fact]
    public void Generated_code_compiles_as_csharp_7_3_without_nullable_directive()
    {
        var h = new GeneratorHarness { LanguageVersion = LanguageVersion.CSharp7_3, Nullable = NullableContextOptions.Disable, IncludeCSharpGlueGenerator = false }
            .Awaui("Legacy.awaui", """
                @using System.Text
                <div class="legacy" @onclick="Inc">
                    @foreach (var i in Enumerable.Range(0, count.Value))
                    {
                        <span @key="i">@i</span>
                    }
                    <input @bind="text" />
                    <Child Label="@text.Value" Items="new[] { 1, 2 }" />
                </div>
                <style>.legacy { color: red }</style>
                @code {
                    readonly State<int> count = new State<int>(1);
                    readonly State<string> text = new State<string>("");
                    public string Name { get; set; }
                    void Inc() { count.Value = count.Value + 1; }
                }
                """)
            .Awaui("Child.awaui", """
                <text>@Label @Items.Length</text>
                @code {
                    public string Label { get; set; }
                    public int[] Items { get; set; }
                }
                """);
        var r = h.Run();
        r.AssertNoErrors();
        Assert.DoesNotContain("#nullable", r.Generated("Legacy.awaui"));
        using var asm = r.Load();
        var root = new UIRoot();
        root.Mount(asm.Create("TestApp.Legacy"));
        root.Input.Click(root.RootElement.ByClass("legacy"));
        root.RunFrame();
        Assert.Equal(2, root.RootElement.FindAll(e => e.Tag == "span").Count);
    }

    // ------------------------------------------------------------ 胶水：代码后置文件与基类

    [Fact]
    public void Glue_covers_code_behind_props_and_custom_base_components()
    {
        var r = new GeneratorHarness()
            .Awaui("Fancy.awaui", """
                @inherits FancyBase
                <text class="fancy">@Title/@Subtitle/@Extra</text>
                @code {
                    public string Title { get; set; } = "";
                    public override string ToString() => Title;
                }
                """)
            .CSharp("Fancy.awaui.cs", """
                namespace TestApp;
                public partial class Fancy
                {
                    public string Extra { get; set; } = "";
                }
                """)
            .CSharp("FancyBase.cs", """
                using AwAUI.UI;
                namespace TestApp;
                public abstract class FancyBase : Component   // 不是 partial、也没有手写胶水：由派生类负责它的 Props
                {
                    public string Subtitle { get; set; } = "";
                }
                """)
            .Awaui("Host.awaui", """
                <Fancy Title="@title.Value" Subtitle="s" Extra="e" />
                <Button class="go" @onclick='() => title.Value += "!"'>go</Button>
                @code { readonly State<string> title = new("t"); }
                """)
            .Run();
        r.AssertNoErrors();
        string fancy = r.Generated("Fancy.awaui");
        Assert.Contains("this.Title = __o.Title;", fancy);
        Assert.Contains("this.Extra = __o.Extra;", fancy);      // 代码后置文件中的 Prop
        Assert.Contains("this.Subtitle = __o.Subtitle;", fancy); // 没有胶水的基类上的 Prop
        Assert.DoesNotContain("base.AdoptProps", fancy);

        using var asm = r.Load();
        var root = new UIRoot();
        root.Mount(asm.Create("TestApp.Host"));
        Assert.Equal("t/s/e", root.RootElement.ByClass("fancy").TextContent);
        root.Input.Click(root.RootElement.ByClass("go"));
        root.RunFrame();
        Assert.Equal("t!/s/e", root.RootElement.ByClass("fancy").TextContent);
    }

    [Fact]
    public void Handwritten_glue_in_code_block_is_not_generated_again()
    {
        var r = new GeneratorHarness()
            .Awaui("Manual.awaui", """
                <text>@Value</text>
                @code {
                    public int Value { get; set; }
                    protected override bool PropsEqual(Component other) => other is Manual m && m.Value / 10 == Value / 10;
                }
                """)
            .Run();
        r.AssertNoErrors();
        string cs = r.Generated("Manual.awaui");
        // 手写的 PropsEqual 只出现一次（来自 @code），生成器只补出 AdoptProps
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(cs, "override bool PropsEqual"));
        Assert.Contains("protected override void AdoptProps", cs);
    }

    // ------------------------------------------------------------ 增量

    [Fact]
    public void Unrelated_changes_do_not_regenerate_and_edits_regenerate_only_that_file()
    {
        var h = new GeneratorHarness().Awaui("A.awaui", "<text>a</text>").Awaui("B.awaui", "<text>b @X</text>\n@code { public int X { get; set; } }");
        var compilation = h.CreateCompilation();
        var driver = h.CreateDriver(trackSteps: true).RunGenerators(compilation);

        // 无关的 C# 改动：编译变了，但解析结果相等，源输出全部沿用
        driver = driver.RunGenerators(compilation.AddSyntaxTrees(CSharpSyntaxTree.ParseText("class Unrelated { }", h.ParseOptions)));
        var reasons = driver.GetRunResult().Results[0].TrackedOutputSteps.SelectMany(s => s.Value).SelectMany(s => s.Outputs).Select(o => o.Reason).ToList();
        Assert.NotEmpty(reasons);
        Assert.All(reasons, r => Assert.True(r is IncrementalStepRunReason.Cached or IncrementalStepRunReason.Unchanged, r.ToString()));

        // 修改 A：只有 A 重新生成
        var oldA = h.AdditionalTexts.Single(t => t.Path.EndsWith("A.awaui", StringComparison.Ordinal));
        driver = driver.ReplaceAdditionalText(oldA, new GeneratorHarness.InMemoryAdditionalText(oldA.Path, "<text>a2</text>"));
        driver = driver.RunGenerators(compilation);
        var result = driver.GetRunResult().Results[0];
        var outputs = result.TrackedOutputSteps.SelectMany(s => s.Value).SelectMany(s => s.Outputs).ToList();
        Assert.Equal(1, outputs.Count(o => o.Reason == IncrementalStepRunReason.Modified));
        Assert.Contains("\"a2\"", result.GeneratedSources.Single(s => s.HintName.StartsWith("A.awaui", StringComparison.Ordinal)).SourceText.ToString());
    }

}
