using AwAUI.UI;
using RoslynSeverity = Microsoft.CodeAnalysis.DiagnosticSeverity;

namespace AwAUI.Markup.UnitTests;

/// <summary>
/// 属性展开（design/17、23）：元素上的 <c>@attributes="..."</c> 在显式属性之后调用 <c>ElementNode.MergeAttributes</c>（显式优先，class / style 拼接）；
/// 组件上标了 <c>[CaptureUnmatchedAttributes]</c> 的 <c>AttributeDictionary</c> Prop 收集没有对应 Prop 的属性（Blazor 的 CaptureUnmatchedValues）。
/// </summary>
public class AttributeSplattingTests
{
    // shadcn 式组件：自己的 data-slot 与默认类，调用方的类拼在后面，其余属性原样透传到根部件
    const string ButtonAwaui = """
        <div id="inner" role="button" data-slot="button" class="btn @Class" style="padding: 1px" @attributes="Rest">@Children</div>

        @code {
            public string? Class { get; set; }
            public NodeList? Children { get; set; }
            [CaptureUnmatchedAttributes] public AttributeDictionary? Rest { get; set; }
        }
        """;

    const string PageAwaui = """
        <div class="page">
            <Btn class="primary" id="ignored" data-slot="ignored" data-state="@(open.Value ? "open" : "closed")" aria-expanded="@open.Value"
                 aria-label="切换" tabindex="2" title="@Title" disabled style="color: red; padding: 9px" @onclick="Toggle" @attributes="Extra">切换</Btn>
            <text class="count">@clicks</text>
        </div>

        @code {
            readonly State<bool> open = new(false);
            int clicks;
            // 展开值：显式写在标签上的 data-state / style 优先（与 @attributes 的位置无关）；title 显式为 null 时保留展开值
            public Dictionary<string, object?> Extra { get; set; } = new() { ["data-extra"] = 1.5, ["aria-hidden"] = false, ["data-state"] = "from-extra", ["style"] = "margin: 3px", ["title"] = "提示" };
            public string? Title { get; set; }
            void Toggle() { open.Value = !open.Value; clicks++; }
        }
        """;

    static GeneratorHarness.Result Build() =>
        new GeneratorHarness().Awaui("Btn.awaui", ButtonAwaui).Awaui("Page.awaui", PageAwaui).Run();

    [Fact]
    public void Unmatched_attributes_are_captured_in_source_order_and_splatted_after_explicit_ones()
    {
        var r = Build();
        r.AssertNoErrors();
        string page = r.Generated("Page.awaui");
        Assert.Contains("var __n2_attrs = new global::AwAUI.UI.AttributeDictionary();", page);
        Assert.Contains("__n2_attrs.Merge(\"id\", \"ignored\");", page);
        Assert.Contains("__n2_attrs.Merge(\"disabled\", true);", page); // HTML 布尔写法
        Assert.Contains("__n2_attrs.Merge(\"aria-expanded\", (", page);
        Assert.Contains("__n2_attrs.Merge(\"onclick\", global::AwAUI.UI.EventHandlers.Wrap<global::AwAUI.UI.PointerEvent>(", page);
        // 展开值先写入（与位置无关），显式属性随后覆盖
        Assert.True(page.IndexOf("__n2_attrs.AddRange(", StringComparison.Ordinal) < page.IndexOf("__n2_attrs.Merge(\"id\"", StringComparison.Ordinal));
        Assert.Contains(".Rest = __n2_attrs;", page);
        Assert.Contains(".Class = \"primary\";", page); // class 对应组件的 Class Prop，不被收集
        string button = r.Generated("Btn.awaui");
        int splat = button.IndexOf(".MergeAttributes(", StringComparison.Ordinal);
        Assert.True(splat > button.IndexOf(".Attributes[\"data-slot\"] = \"button\";", StringComparison.Ordinal));
        Assert.True(splat > button.IndexOf(".Id = \"inner\";", StringComparison.Ordinal));
    }

    [Fact]
    public void Splatted_attributes_reach_the_root_element_with_merge_rules()
    {
        var r = Build();
        using var loaded = r.Load();
        var root = new UIRoot();
        root.Mount(loaded.Create("TestApp.Page"));
        var el = root.RootElement.FindAll(e => e.Id == "inner").Single();
        // 显式属性优先：id、data-slot、role 保持组件自己的
        Assert.Equal("inner", el.Id);
        Assert.Equal("button", el.GetAttribute("data-slot"));
        Assert.Equal("button", el.GetAttribute("role"));
        // class 拼接（组件的在前），style 拼接（展开的在前，显式的同名声明生效）
        Assert.Equal(new[] { "btn", "primary" }, el.Classes.ToArray());
        Assert.Equal("margin: 3px; color: red; padding: 9px; padding: 1px", el.GetAttribute("style"));
        // 其余透传：data-* / aria-* / title / tabindex / aria-label
        Assert.Equal("closed", el.GetAttribute("data-state"));
        Assert.Equal("false", el.GetAttribute("aria-expanded"));
        Assert.Equal("1.5", el.GetAttribute("data-extra"));
        Assert.Equal("false", el.GetAttribute("aria-hidden"));
        Assert.Equal("提示", el.GetAttribute("title"));
        Assert.Equal("切换", el.GetAttribute("aria-label"));
        Assert.Equal("2", el.GetAttribute("tabindex"));
        Assert.Equal("", el.GetAttribute("disabled"));

        // 事件处理器也被透传；重新渲染后的属性随状态更新
        root.Input.Click(el);
        root.RunFrame();
        el = root.RootElement.FindAll(e => e.Id == "inner").Single();
        Assert.Equal("open", el.GetAttribute("data-state"));
        Assert.Equal("true", el.GetAttribute("aria-expanded"));
        Assert.Equal("1", root.RootElement.ByClass("count").TextContent);
    }

    [Fact]
    public void Capture_prop_compares_by_content_so_unchanged_attributes_skip_the_rebuild()
    {
        var r = new GeneratorHarness()
            .Awaui("Leaf.awaui", """
                <span class="leaf" @attributes="Rest">@(++Builds)</span>
                @code {
                    public static int Builds;
                    [CaptureUnmatchedAttributes] public AttributeDictionary? Rest { get; set; }
                }
                """)
            .Awaui("Host.awaui", """
                <div><Leaf data-x="1" aria-label="@label" /><text class="tick">@tick.Value</text></div>
                @code {
                    readonly State<int> tick = new(0);
                    string label = "a";
                    public void Tick() => tick.Value++;
                    public void Relabel() { label = "b"; tick.Value++; }
                }
                """)
            .Run();
        using var loaded = r.Load();
        var host = loaded.Create("TestApp.Host");
        var root = new UIRoot();
        root.Mount(host);
        var builds = loaded.Type("TestApp.Leaf").GetField("Builds")!;
        Assert.Equal(1, (int)builds.GetValue(null)!);
        host.GetType().GetMethod("Tick")!.Invoke(host, null);
        root.RunFrame();
        Assert.Equal(1, (int)builds.GetValue(null)!); // 属性内容相同：PropsEqual 跳过重建
        host.GetType().GetMethod("Relabel")!.Invoke(host, null);
        root.RunFrame();
        Assert.Equal(2, (int)builds.GetValue(null)!);
        Assert.Equal("b", root.RootElement.ByClass("leaf").GetAttribute("aria-label"));
    }

    [Fact]
    public void Explicitly_passing_the_capture_prop_merges_with_unmatched_attributes()
    {
        // 包装组件把收到的属性表按名字传给内层组件，同时再加自己的属性：两者都要到达元素，
        // 而不是收集到的表在最后整体赋值时把显式传入的 Rest 悄悄覆盖掉
        var r = new GeneratorHarness()
            .Awaui("Btn.awaui", ButtonAwaui)
            .Awaui("Wrap.awaui", """
                <Btn Rest="@Rest" data-wrap="1" class="w">x</Btn>
                @code { [CaptureUnmatchedAttributes] public AttributeDictionary? Rest { get; set; } }
                """)
            .Awaui("Use.awaui", """<Wrap data-outer="2" aria-label="外层" />""")
            .Run();
        r.AssertNoErrors();
        using var loaded = r.Load();
        var root = new UIRoot();
        root.Mount(loaded.Create("TestApp.Use"));
        var el = root.RootElement.FindAll(e => e.Id == "inner").Single();
        Assert.Equal("1", el.GetAttribute("data-wrap"));
        Assert.Equal("2", el.GetAttribute("data-outer"));
        Assert.Equal("外层", el.GetAttribute("aria-label"));
        Assert.Equal(new[] { "btn", "w" }, el.Classes.ToArray());
    }

    [Fact]
    public void Common_dictionary_types_splat_without_nullability_warnings()
    {
        // Dictionary<string, object>（Blazor 习惯的写法）与 IEnumerable<KeyValuePair<string, object?>> 只差可空注解：
        // 不能因此在开启可空的项目里产生 CS8620（仓库与多数应用把警告当错误）
        const string uses = """
            <div @attributes="A1"></div><div @attributes="A2"></div><div @attributes="A3"></div><div @attributes="A4"></div>
            <Leaf @attributes="A1" /><Leaf @attributes="A2" /><Leaf @attributes="A3" /><Leaf @attributes="A4" /><Leaf Rest="@A5" />
            @code {
                Dictionary<string, object> A1 = new Dictionary<string, object> { ["data-a"] = "1" };
                Dictionary<string, object?> A2 = new Dictionary<string, object?>();
                Dictionary<string, string> A3 = new Dictionary<string, string> { ["data-c"] = "3" };
                IReadOnlyDictionary<string, object> A4 = new Dictionary<string, object>();
                AttributeDictionary? A5 = null;
            }
            """;
        foreach (var version in new[] { Microsoft.CodeAnalysis.CSharp.LanguageVersion.Latest, Microsoft.CodeAnalysis.CSharp.LanguageVersion.CSharp7_3 })
        {
            var h = new GeneratorHarness { LanguageVersion = version };
            if (version == Microsoft.CodeAnalysis.CSharp.LanguageVersion.CSharp7_3) h.Nullable = Microsoft.CodeAnalysis.NullableContextOptions.Disable;
            bool latest = version == Microsoft.CodeAnalysis.CSharp.LanguageVersion.Latest;
            const string leaf = """
                <span @attributes="Rest">x</span>
                @code { [CaptureUnmatchedAttributes] public AttributeDictionary? Rest { get; set; } }
                """;
            string Plain(string text) => latest ? text : text.Replace("object?", "object").Replace("AttributeDictionary?", "AttributeDictionary");
            var r = h.Awaui("Leaf.awaui", Plain(leaf))
                .Awaui("A.awaui", Plain(uses))
                .Run();
            var problems = r.AllDiagnostics.Where(d => d.Severity >= RoslynSeverity.Warning).Select(d => d.ToString()).ToArray();
            Assert.True(problems.Length == 0, version + ": " + string.Join("\n", problems));
        }
    }

    [Fact]
    public void Uppercase_attributes_without_a_prop_are_not_captured()
    {
        // 大写开头按 Prop 处理：拼错的 Prop 名由 C# 编译报告，而不是被悄悄收集
        var r = new GeneratorHarness()
            .Awaui("Btn.awaui", ButtonAwaui)
            .Awaui("Use.awaui", """<Btn Varaint="x" />""")
            .Run();
        Assert.Contains(r.Errors, d => d.Id == "CS0117" || d.Id == "CS1061");
    }

    [Fact]
    public void Splat_on_builtin_element_types_and_svg_hosts()
    {
        var r = new GeneratorHarness()
            .Awaui("A.awaui", """
                <Button class="b" @attributes="Rest" Disabled="false">x</Button>
                <input placeholder="explicit" @attributes="Rest" />
                <svg class="icon" @attributes="Rest" viewBox="0 0 1 1"><path d="M0 0" /></svg>
                @code { public AttributeDictionary Rest { get; set; } = new() { ["class"] = "extra", ["placeholder"] = "splat", ["disabled"] = true, ["data-k"] = "v" }; }
                """)
            .Run();
        r.AssertNoErrors();
        using var loaded = r.Load();
        var root = new UIRoot();
        root.Mount(loaded.Create("TestApp.A"));
        var children = root.RootElement.Children[0].Children;
        var button = root.RootElement.FindAll(e => e.HasClass("b")).Single();
        Assert.Equal(new[] { "b", "extra" }, button.Classes.ToArray());
        Assert.Equal("v", button.GetAttribute("data-k"));
        Assert.NotNull(button.GetAttribute("disabled")); // 展开的 disabled 写入（显式的 Disabled=false 等于没有设置）
        var input = root.RootElement.FindAll(e => e.Tag == "input").Single();
        Assert.Equal("explicit", input.GetAttribute("placeholder"));
        var svg = root.RootElement.FindAll(e => e.HasClass("icon")).Single();
        Assert.True(svg.HasClass("extra"));
        Assert.Equal("v", svg.GetAttribute("data-k"));
        Assert.NotEmpty(children);
    }

    [Fact]
    public void Splat_without_capture_prop_on_component_still_reports_the_runtime_gap()
    {
        var r = new GeneratorHarness()
            .Awaui("Child.awaui", "<div/>\n@code { public int Value { get; set; } }")
            .Awaui("Use.awaui", "<Child @attributes=\"extra\" data-x=\"1\" />\n@code { AttributeDictionary extra = new(); }")
            .Run();
        Assert.Contains(r.AllDiagnostics, d => d.Id == "AW4012" && d.Severity == RoslynSeverity.Error);
    }
}
