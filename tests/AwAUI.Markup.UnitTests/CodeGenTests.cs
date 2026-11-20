using System.Runtime.CompilerServices;
using AwAUI.Markup.Compiler;
using AwAUI.Markup.Compiler.Analysis;
using AwAUI.Markup.Compiler.CodeGen;

namespace AwAUI.Markup.UnitTests;

/// <summary>
/// C# 生成的快照测试（design/17「快照测试」）：校验生成代码的形状，审阅改动时看快照的差异。
/// 生成的代码能否对 AwAUI.UI 编译、运行见 <see cref="EndToEndTests"/>。
/// 设置环境变量 AWAUI_UPDATE_SNAPSHOTS=1 重新生成 Snapshots/*.g.cs.txt（不参与编译）。
/// </summary>
public class CodeGenTests
{
    /// <summary>源码中的快照目录（只在更新快照时写入）。</summary>
    static string SourceSnapshotDir([CallerFilePath] string path = "") => Path.Combine(Path.GetDirectoryName(path)!, "Snapshots");

    static void AssertSnapshot(string name, string text, CSharpEmitOptions options)
    {
        var analysis = MarkupCompiler.Analyze(text, name + ".awaui");
        Assert.False(analysis.HasErrors, string.Join("\n", analysis.Diagnostics));
        string actual = CSharpEmitter.Emit(analysis, options);
        if (Environment.GetEnvironmentVariable("AWAUI_UPDATE_SNAPSHOTS") == "1")
        {
            File.WriteAllText(Path.Combine(SourceSnapshotDir(), name + ".g.cs.txt"), actual);
            return;
        }
        // 读取复制到输出目录的快照，不依赖编译期源路径（CI 的路径映射下 CallerFilePath 不可用）
        string file = Path.Combine(AppContext.BaseDirectory, "Snapshots", name + ".g.cs.txt");
        Assert.True(File.Exists(file), $"snapshot {file} missing; run with AWAUI_UPDATE_SNAPSHOTS=1");
        Assert.Equal(File.ReadAllText(file).Replace("\r\n", "\n"), actual);
    }

    static readonly ComponentCatalog Catalog = new StaticComponentCatalog(new[]
    {
        new ComponentInfo("AwAUI.Controls.Button", new[] { new PropInfo("Variant", PropKind.String), new PropInfo("Disabled", PropKind.Bool) }),
        new ComponentInfo("MyApp.TodoRow", new[] { new PropInfo("Todo", PropKind.Other), new PropInfo("OnRemove", PropKind.Delegate) }),
        new ComponentInfo("MyApp.Card", new[] { new PropInfo("Header", PropKind.Slot), new PropInfo("Title", PropKind.String) }),
        new ComponentInfo("MyApp.VirtualList", new[] { new PropInfo("Items", PropKind.Other), new PropInfo("ItemTemplate", PropKind.Template) }),
    });

    [Fact]
    public void Counter_matches_design_shape() =>
        AssertSnapshot("Counter", Samples.Counter, new CSharpEmitOptions { Namespace = "MyApp", EmitLineDirectives = false });

    [Fact]
    public void Counter_with_line_directives() =>
        AssertSnapshot("CounterLines", Samples.Counter, new CSharpEmitOptions { Namespace = "MyApp" });

    [Fact]
    public void TodoApp() =>
        AssertSnapshot("TodoApp", Samples.TodoApp.Replace("@using MyApp.Models", "@using MyApp.Models\n@using AwAUI.Controls"),
            new CSharpEmitOptions { Namespace = "MyApp", EmitLineDirectives = false, Catalog = Catalog });

    [Fact]
    public void Slots_templates_and_directives() =>
        AssertSnapshot("Slots", """
            @namespace MyApp
            @using AwAUI.Controls
            @typeparam TItem where TItem : class
            @inherits MyApp.PageBase
            @implements System.IDisposable
            @attribute [System.Obsolete]
            <Card Title="Hello @name">
                <Header><text class="h">标题</text></Header>
                正文 &amp; 内容
            </Card>
            <VirtualList Items="rows">
                <ItemTemplate Context="row">
                    <div @key="row.Id" tabindex="0" hidden aria-label="行" class="row @(row.Selected ? "sel" : "")">@row.Name</div>
                </ItemTemplate>
            </VirtualList>
            @{
                var total = rows.Count;
                @:共 @total 行
            }
            @switch (total)
            {
                case 0:
                    <text>空</text>
                    break;
                default:
                    <><span>多</span></>
                    break;
            }
            @code {
                public void Dispose() { }
            }
            """, new CSharpEmitOptions { EmitLineDirectives = false, Catalog = Catalog });

    [Fact]
    public void Line_directives_point_at_source_lines()
    {
        var a = MarkupCompiler.Analyze("<div>\n  @if (x)\n  {\n    <span>@y</span>\n  }\n</div>\n@code {\n  bool x; string y;\n}", "Pages/Home.awaui");
        string cs = CSharpEmitter.Emit(a);
        Assert.Contains("if (\n#line 2 \"Pages/Home.awaui\"\n       x)\n#line hidden", cs);          // x 在第 2 行第 8 列
        Assert.Contains(".Add(global::AwAUI.UI.Node.From(\n#line 4 \"Pages/Home.awaui\"\n           y));", cs); // y 在第 4 行第 12 列
        Assert.Contains("#line 7 \"Pages/Home.awaui\"\n\n  bool x; string y;\n#line hidden", cs);
        Assert.Contains("namespace AwAUI.Generated", cs);
        Assert.Contains("public partial class Home : global::AwAUI.UI.Component", cs);
        Assert.EndsWith("#line default\n", cs);
    }

    public static TheoryData<string> LineSamples() => new() { Samples.Counter, Samples.TodoApp, Samples.Flow };

    /// <summary>
    /// 每个 <c>#line N</c> 之后的那一行（补齐空格之后）与源文件第 N 行在同一列上有相同的文本：
    /// C# 编译器报告的行列因此正是 <c>.awaui</c> 中的行列。
    /// </summary>
    [Theory]
    [MemberData(nameof(LineSamples))]
    public void Mapped_code_is_column_aligned_with_the_source(string sample)
    {
        string cs = CSharpEmitter.Emit(MarkupCompiler.Analyze(sample, "S.awaui"));
        string[] src = sample.Replace("\r\n", "\n").Split('\n');
        string[] gen = cs.Split('\n');
        int checkedLines = 0;
        for (int i = 0; i < gen.Length - 1; i++)
        {
            if (!gen[i].StartsWith("#line ", StringComparison.Ordinal) || gen[i].StartsWith("#line hidden", StringComparison.Ordinal)
                || gen[i].StartsWith("#line default", StringComparison.Ordinal)) continue;
            int n = int.Parse(gen[i].Split(' ')[1], System.Globalization.CultureInfo.InvariantCulture);
            string next = gen[i + 1];
            int col = next.Length - next.TrimStart().Length;
            string code = next.TrimStart();
            // 整行映射（属性赋值等）与组件的类型名（与标签名对齐，但不是源文本）不比较
            if (code.Length == 0 || code.StartsWith("__", StringComparison.Ordinal) || code.EndsWith("();", StringComparison.Ordinal)) continue;
            string line = src[n - 1];
            Assert.True(col < line.Length && line.Substring(col).StartsWith(code.Length > 8 ? code.Substring(0, 8) : code.TrimEnd(';', ')'), StringComparison.Ordinal),
                $"#line {n}: generated '{next}' not aligned with source '{line}'");
            checkedLines++;
        }
        Assert.True(checkedLines >= 3, checkedLines.ToString());
    }

    [Fact]
    public void Generated_code_parses_as_csharp_7_3()
    {
        foreach (var sample in new[] { Samples.Counter, Samples.Flow })
        {
            var compilation = MarkupCompiler.Compile(sample, "S.awaui", new CSharpEmitOptions { EmitNullableRestore = false });
            var tree = Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree.ParseText(compilation.GeneratedCode,
                new Microsoft.CodeAnalysis.CSharp.CSharpParseOptions(Microsoft.CodeAnalysis.CSharp.LanguageVersion.CSharp7_3));
            Assert.Empty(tree.GetDiagnostics());
        }
    }

    [Fact]
    public void String_literals_are_escaped_for_csharp()
    {
        Assert.Equal("\"a\\\"b\\\\c\\n\\u0001\\u2028\"", CSharpEmitter.Literal("a\"b\\c\n\u0001\u2028"));
    }

    [Fact]
    public void Emitter_never_throws_on_broken_input()
    {
        foreach (var s in new[] { "<div", "@(", "@{ <span> }", "</x>", "<style>", "@switch (x) { case", "<a b=\"c", "@foreach" })
            CSharpEmitter.Emit(MarkupCompiler.Analyze(s, "X.awaui"));
    }

    [Fact]
    public void Mixed_text_and_expression_prop_without_catalog_is_concatenated()
    {
        var a = MarkupCompiler.Analyze("<Button Label=\"Hi @name!\" Count=\"@n + 1\" Size=\"@size\" />", "P.awaui");
        string cs = CSharpEmitter.Emit(a, new CSharpEmitOptions { EmitLineDirectives = false });
        Assert.Contains(".Label = \"Hi \" + (name) + \"!\";", cs);
        Assert.Contains(".Count = (n) + 1;", cs);   // 字面量只有运算符与数字：仍是 C# 表达式
        Assert.Contains(".Size = size;", cs);
    }

    [Fact]
    public void Intrinsic_attributes_map_to_runtime_members()
    {
        var c = MarkupCompiler.Compile("""
            <div id="main" class="a @b" style="padding: @(p)px" role="navigation" tabindex="2" aria-label="主" aria-hidden="true"
                 data-id="@id" title="t" hidden="@h" dir="rtl" @attributes="extra" @ref="box" @key="k">
                <img src="a.png" alt="A" width="10" />
                <input type="password" value="@v" placeholder="p" disabled readonly="false" name="n" @onchange="Changed" @onsubmit="() => Go()" />
                <span @onclick="Go" @onkeydown="e => Key(e)" @onfocusin="F" @onpointerenter="P" @ontap="T" @onpan="Pan">x</span>
            </div>
            """, "A.awaui", new CSharpEmitOptions { EmitLineDirectives = false });
        string cs = c.GeneratedCode;
        Assert.Contains(".Id = \"main\";", cs);
        Assert.Contains(".Class = \"a \" + (b);", cs);
        Assert.Contains(".Style = \"padding: \" + (p) + \"px\";", cs);
        Assert.Contains(".Role = global::AwAUI.UI.Role.Navigation;", cs);
        Assert.Contains(".TabIndex = 2;", cs);
        Assert.Contains(".AriaLabel = \"主\";", cs);
        Assert.Contains(".Attributes[\"aria-hidden\"] = \"true\";", cs);
        Assert.Contains(".Attributes[\"data-id\"] = \"\" + (id);", cs);
        Assert.Contains(".Attributes[\"title\"] = \"t\";", cs);
        Assert.Contains(".Attributes[\"hidden\"] = (h) ? \"\" : null;", cs);
        Assert.Contains(".Attributes[\"dir\"] = \"rtl\";", cs);
        const string splat = ".MergeAttributes(((extra))!);";
        Assert.Contains(splat, cs);
        Assert.True(cs.IndexOf(splat, StringComparison.Ordinal) > cs.IndexOf(".Attributes[\"dir\"]", StringComparison.Ordinal));
        Assert.Contains(".Ref = box;", cs);
        Assert.Contains(".Key = k;", cs);
        Assert.Contains("new global::AwAUI.UI.Image();", cs);
        Assert.Contains(".Source = \"a.png\";", cs);
        Assert.Contains(".Alt = \"A\";", cs);
        Assert.Contains(".Width = 10f;", cs);
        Assert.Contains(".InputType = \"password\";", cs);
        Assert.Contains(".Value = v;", cs);
        Assert.Contains(".Disabled = true;", cs);
        Assert.Contains(".ReadOnly = false;", cs);
        Assert.Contains(".Attributes[\"name\"] = \"n\";", cs);
        Assert.Contains(".OnChange = global::AwAUI.UI.EventHandlers.Wrap<global::AwAUI.UI.InputEvent>(Changed);", cs);
        Assert.Contains(".OnSubmit = global::AwAUI.UI.EventHandlers.Wrap<global::AwAUI.UI.SubmitEvent>(() => Go());", cs);
        Assert.Contains(".OnClick = global::AwAUI.UI.EventHandlers.Wrap<global::AwAUI.UI.PointerEvent>(Go);", cs);
        Assert.Contains(".OnKeyDown = global::AwAUI.UI.EventHandlers.Wrap<global::AwAUI.UI.KeyEvent>(e => Key(e));", cs);
        Assert.Contains(".OnFocusIn = global::AwAUI.UI.EventHandlers.Wrap<global::AwAUI.UI.FocusEvent>(F);", cs);
        Assert.Contains(".OnPointerEnter = global::AwAUI.UI.EventHandlers.Wrap<global::AwAUI.UI.PointerEvent>(P);", cs);
        Assert.Contains(".OnTap = global::AwAUI.UI.EventHandlers.Wrap<global::AwAUI.UI.TapEvent>(T);", cs);
        Assert.Contains(".OnPan = global::AwAUI.UI.EventHandlers.Wrap<global::AwAUI.UI.PanEvent>(Pan);", cs);
        // 只有行为未实现的属性报 AW4012 警告；没有错误
        Assert.False(c.HasErrors, string.Join("\n", c.Diagnostics));
        Assert.Equal(new[] { "hidden" }, c.Diagnostics.Where(d => d.Code == DiagnosticCodes.NotSupportedByRuntime).Select(d => d.Message.Split(' ')[1]));
    }

    [Fact]
    public void Unsupported_elements_and_events_are_diagnosed_and_skipped()
    {
        var c = MarkupCompiler.Compile("""
            <div>
                <textarea @bind="text" />
                <canvas width="10"><span>inner</span></canvas>
                <path d="M0 0" />
                <div @onpinch="W" @ondblclick="D" @onclick="C" />
                <span @onchange="X" />
            </div>
            """, "U.awaui", new CSharpEmitOptions { EmitLineDirectives = false });
        var errors = c.Diagnostics.Where(d => d.Code == DiagnosticCodes.NotSupportedByRuntime && d.IsError).Select(d => d.Message).ToList();
        Assert.Equal(5, errors.Count);
        Assert.Contains(errors, m => m.Contains("<canvas>"));
        Assert.Contains(errors, m => m.Contains("<path>"));   // path 只能用在内联 <svg> 中
        Assert.Contains(errors, m => m.Contains("@onpinch"));
        Assert.Contains(errors, m => m.Contains("@ondblclick"));
        Assert.Contains(errors, m => m.Contains("@onchange")); // change 只在 input 上存在
        Assert.Contains("new global::AwAUI.UI.TextArea()", c.GeneratedCode);
        Assert.DoesNotContain("inner", c.GeneratedCode);       // 跳过整个子树
        Assert.DoesNotContain("Path", c.GeneratedCode);
        Assert.Contains(".OnClick = ", c.GeneratedCode);
        Assert.DoesNotContain("OnPinch", c.GeneratedCode);
    }

    [Fact]
    public void Scroll_and_wheel_events_map_to_runtime_handlers()
    {
        var c = MarkupCompiler.Compile("""
            <div style="overflow: auto" @onscroll="S" @onwheel="e => W(e)">x</div>
            """, "S.awaui", new CSharpEmitOptions { EmitLineDirectives = false });
        Assert.False(c.HasErrors, string.Join("\n", c.Diagnostics));
        Assert.DoesNotContain(c.Diagnostics, d => d.Code == DiagnosticCodes.NotSupportedByRuntime);
        Assert.Contains(".OnScroll = global::AwAUI.UI.EventHandlers.Wrap<global::AwAUI.UI.ScrollEvent>(S);", c.GeneratedCode);
        Assert.Contains(".OnWheel = global::AwAUI.UI.EventHandlers.Wrap<global::AwAUI.UI.WheelEvent>(e => W(e));", c.GeneratedCode);
    }

    [Fact]
    public void Uppercase_element_node_types_and_components_use_catalog_kinds()
    {
        var catalog = new StaticComponentCatalog(new[]
        {
            new ComponentInfo("AwAUI.UI.Button", new[] { new PropInfo("Disabled", PropKind.Bool), new PropInfo("Class", PropKind.String) }, isElement: true),
            new ComponentInfo("App.Row", new[]
            {
                new PropInfo("OnPick", PropKind.Delegate, "global::System.Action<global::AwAUI.UI.PointerEvent>"),
                new PropInfo("OnRemove", PropKind.Delegate, "global::System.Action<global::App.Item>"),
                new PropInfo("Value", PropKind.Other, "int"),
                new PropInfo("ValueChanged", PropKind.Delegate, "global::System.Action<int>"),
                new PropInfo("Label", PropKind.String, "string"),
            }),
        });
        var c = MarkupCompiler.Compile("""
            @namespace App
            <Button class="b" disabled aria-label="按钮" tabindex="1" @onclick="Go">x</Button>
            <Row @onpick="Go" @onremove="Remove" @bind-Value="level" Label="L @n" />
            <style>.b { color: red }</style>
            @code {
                readonly State<int> level = new(0);
            }
            """, "C.awaui", new CSharpEmitOptions { EmitLineDirectives = false, Catalog = catalog });
        string cs = c.GeneratedCode;
        Assert.Contains("new global::AwAUI.UI.Button();", cs);
        Assert.Contains(".Class = \"b\";", cs);
        Assert.Contains(".Disabled = true;", cs);
        Assert.Contains(".AriaLabel = \"按钮\";", cs);
        Assert.Contains(".TabIndex = 1;", cs);
        Assert.Contains("__n1.__Scope = __StyleScope;", cs);       // 元素描述类型参与作用域
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(cs, "__Scope = ")); // 组件不参与
        Assert.Contains(".OnPick = global::AwAUI.UI.EventHandlers.Wrap<global::AwAUI.UI.PointerEvent>(Go);", cs);
        Assert.Contains(".OnRemove = Remove;", cs);                // 非事件类型的委托 Prop 直接赋值
        Assert.Contains(".Value = global::AwAUI.UI.Binding.Get(level);", cs);
        Assert.Contains(".ValueChanged = __v => global::AwAUI.UI.Binding.Set(level, __v);", cs);
        Assert.Contains(".Label = \"L \" + (n);", cs);
    }

    [Fact]
    public void Compile_reports_style_scope_and_sheets()
    {
        var c = MarkupCompiler.Compile("<div class=\"a\"/>\n<style>.a { color: red }</style>\n<style global>body { margin: 0 }</style>", "S.awaui",
            new CSharpEmitOptions { Namespace = "N" });
        Assert.Equal(AwAUI.Markup.Compiler.Styles.ScopedCss.ScopeAttributeFor("N.S"), c.StyleScope);
        Assert.Equal($".a[{c.StyleScope}] {{ color: red }}", c.ScopedStyleSheet);
        Assert.Equal("body { margin: 0 }", c.GlobalStyleSheet);
        Assert.Contains("public const string StyleSheetSource = ", c.GeneratedCode);
        Assert.Contains("public const string GlobalStyleSheetSource = \"body { margin: 0 }\";", c.GeneratedCode);
        var none = MarkupCompiler.Compile("<div/>", "S.awaui");
        Assert.Null(none.StyleScope);
        Assert.DoesNotContain("__Scope", none.GeneratedCode);
    }

    [Fact]
    public void Syntactic_glue_scan_finds_props_state_members_and_handwritten_glue()
    {
        var c = MarkupCompiler.Compile("""
            <input @bind="draft" /><input @bind="this.draft" /><input @bind="plain" />
            @code {
                [System.Obsolete] public string Title { get; set; } = "x";      // Prop
                public List<(int A, string B)> Rows { get; set; } = new();       // 元组泛型实参
                public (int, int) Pair { get; init; }                            // init Prop
                public int Count { get; private set; }                           // 非公共 setter
                public static int Shared { get; set; }                           // 静态
                public int ReadOnly => 1;                                        // 只读
                public int this[int i] { get => i; set { } }                     // 索引器
                public object Key { get; set; } = 1;                             // Key 不是 Prop
                public Func<int, Node>? Template { get; set; }
                readonly State<string> draft = new("");
                public AwAUI.UI.State<int> Counter { get; } = new(0);
                string plain = "";
                protected override bool PropsEqual(Component other) => false;
                public class Nested { public int Inner { get; set; } }
                void M() { var s = new { Title = 1 }; }
            }
            """, "G.awaui", new CSharpEmitOptions { EmitLineDirectives = false });
        string cs = c.GeneratedCode;
        Assert.Contains("this.Title = __o.Title;", cs);
        Assert.Contains("this.Rows = __o.Rows;", cs);
        Assert.Contains("this.Template = __o.Template;", cs);
        Assert.Contains("protected override bool CanAdoptProps", cs);
        Assert.Contains("EqualityComparer<(int, int)>.Default.Equals(this.Pair, __o.Pair)", cs);
        foreach (var notProp in new[] { "Count", "Shared", "ReadOnly", "Key", "Inner", "Counter" })
            Assert.DoesNotContain("this." + notProp + " = __o.", cs);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(cs, "override bool PropsEqual")); // 手写的不再生成
        Assert.Contains("Binding.Set(draft, __e.Value)", cs);
        Assert.Contains("Binding.Set(this.draft, __e.Value)", cs);
        Assert.Contains("OnInput = __e => plain = __e.Value;", cs);
    }
}
