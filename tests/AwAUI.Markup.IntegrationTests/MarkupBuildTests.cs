using System.Reflection;
using AwAUI.Markup.IntegrationTests.Components;
using AwAUI.Text.Testing;
using AwAUI.UI;

namespace AwAUI.Markup.IntegrationTests;

/// <summary>
/// 本项目中的 .awaui 由真实的 MSBuild 构建编译（生成器作为分析器、*.awaui 自动成为 AdditionalFiles）：
/// 能编译通过本身就证明了 MSBuild 集成、命名空间推导、代码后置文件与 C# / .awaui 组件互相引用都是通的。
/// </summary>
public class MarkupBuildTests
{
    static Element Find(UIRoot root, string cls) => FindAll(root, cls).Single();

    static List<Element> FindAll(UIRoot root, string cls)
    {
        var result = new List<Element>();
        var stack = new Stack<Element>();
        stack.Push(root.RootElement);
        while (stack.Count > 0)
        {
            var e = stack.Pop();
            if (e.HasClass(cls)) result.Add(e);
            for (int i = e.Children.Count - 1; i >= 0; i--) stack.Push(e.Children[i]);
        }
        return result;
    }

    [Fact]
    public void Awaui_components_are_ordinary_component_classes_in_folder_namespaces()
    {
        Assert.True(typeof(Component).IsAssignableFrom(typeof(Counter)));
        Assert.Equal("AwAUI.Markup.IntegrationTests.Components", typeof(Counter).Namespace);
        var source = typeof(Counter).GetCustomAttribute<ComponentSourceAttribute>();
        Assert.Equal("Components/Counter.awaui", source!.Path);
    }

    [Fact]
    public void Counter_with_code_behind_runs()
    {
        var root = new UIRoot();
        root.Mount(new Counter { Step = 3, Label = "数", Limit = 7 });
        Assert.Equal("数：0", Find(root, "value").TextContent);
        Assert.Equal("+3", Find(root, "inc").TextContent);
        root.Input.Click(Find(root, "inc"));
        root.RunFrame();
        Assert.Equal("数：3", Find(root, "value").TextContent);
        root.Input.Click(Find(root, "inc"));
        root.Input.Click(Find(root, "inc"));
        root.RunFrame();
        Assert.Equal("数：7", Find(root, "value").TextContent);
        Assert.Single(FindAll(root, "limit"));
    }

    [Fact]
    public void Scoped_styles_are_generated_and_applied_to_own_elements()
    {
        Assert.Contains($".counter[{Counter.__StyleScope}]", Counter.StyleSheetSource);
        Assert.Contains($".counter > .value[{Counter.__StyleScope}]::after", Counter.StyleSheetSource);
        var root = new UIRoot();
        root.Mount(new Counter());
        Assert.Equal(Counter.__StyleScope, Find(root, "counter").Scope);
        Assert.Equal(Counter.__StyleScope, Find(root, "inc").Scope);
    }

    [Fact]
    public void Scoped_style_sheet_applies_through_the_runtime_style_engine_to_own_elements_only()
    {
        // 组件类型首次挂载时，运行时读取生成的 ScopedStyleSheet 钩子并把 StyleSheetSource 注册到帧管线（无需宿主手动加入）
        var pipeline = new RenderPipeline(new TestTextEngine());
        pipeline.SetViewport(new Size(400, 300), 1);
        var root = new UIRoot { Pipeline = pipeline };
        var diagnostics = new List<UIDiagnostic>();
        root.Diagnostic += diagnostics.Add;
        root.Mount(new Themed());
        root.RunFrame();
        Assert.Empty(diagnostics);
        var registered = Assert.Single(root.ComponentStyleSheets, s => s.ComponentType == typeof(Themed));
        Assert.Equal(Themed.StyleSheetSource, registered.Text);
        Assert.Contains(pipeline.StyleEngine.StyleSheets, s => s.Sheet.Text == Themed.StyleSheetSource);

        // 同一类型再次挂载不会重复注册
        int count = pipeline.StyleEngine.StyleSheets.Count;
        root.Mount(new Div { Children = [new Themed(), new Themed()] });
        root.RunFrame();
        Assert.Equal(count, pipeline.StyleEngine.StyleSheets.Count);
        root.Mount(new Themed());
        root.RunFrame();

        static Color ColorOf(Element e) => e.ComputedStyle!.ResolveColor(e.ComputedStyle.Color);
        var badges = FindAll(root, "badge");
        Assert.Equal(2, badges.Count);
        var own = badges.Single(e => e.Scope == Themed.__StyleScope);
        var child = badges.Single(e => e.Scope is null); // 手写的 Badge 组件产出的元素
        Assert.Equal(Color.Parse("#123456"), ColorOf(Find(root, "label")));
        Assert.Equal(Color.Parse("#654321"), ColorOf(own));
        Assert.NotEqual(Color.Parse("#654321"), ColorOf(child));
    }

    [Fact]
    public void External_awaui_css_is_scoped_and_applied_in_a_real_build()
    {
        // Components/StyledPanel.awaui.css 由 MSBuild 自动纳入 AdditionalFiles，并与内联 <style> 一样改写选择器
        Assert.Contains($".styled-title[{StyledPanel.__StyleScope}]", StyledPanel.StyleSheetSource);
        Assert.Contains($".styled-badge[{StyledPanel.__StyleScope}]::before", StyledPanel.StyleSheetSource);
        var root = new UIRoot();
        root.Mount(new StyledPanel { Title = "Externally styled" });
        Assert.Equal(StyledPanel.__StyleScope, Find(root, "styled-panel").Scope);
        Assert.Equal(StyledPanel.__StyleScope, Find(root, "styled-title").Scope);
        Assert.Equal("Externally styled", Find(root, "styled-title").TextContent);
    }

    [Fact]
    public void Project_global_stylesheet_is_registered_and_applies_to_every_component()
    {
        // <AwAUIGlobalStylesheet Include="Styles/global.css" /> 在真实构建中生成 AwAUIGlobalStyles 并自动登记
        Assert.Contains(".awaui-integration-global", AwAUI.Markup.IntegrationTests.AwAUIGlobalStyles.Css);
        Assert.Contains(AppStyleSheets.Registered, s => s.SourceName == "global");

        var pipeline = new RenderPipeline(new TestTextEngine());
        pipeline.SetViewport(new Size(400, 300), 1);
        var root = new UIRoot { Pipeline = pipeline };
        root.Mount(new Div
        {
            Class = "awaui-integration-global",
            Children = new NodeList([new AwAUI.UI.Text("shared")]),
        });
        root.RunFrame();
        var element = Find(root, "awaui-integration-global");
        Assert.Equal(Color.Parse("#0f5f0f"), element.ComputedStyle!.ResolveColor(element.ComputedStyle.Color));
    }

    [Fact]
    public void Todo_list_binds_input_adds_and_removes_keyed_children()
    {
        var root = new UIRoot();
        root.Mount(new TodoList());
        Assert.Single(FindAll(root, "empty"));
        Assert.Equal("0", Find(root, "badge").TextContent);

        foreach (var text in new[] { "买菜", "做饭", "洗碗" })
        {
            root.FocusManager.Focus(Find(root, "draft"));
            root.Input.DispatchTextInput(text);
            root.RunFrame();
            root.Input.Click(Find(root, "add"));
            root.RunFrame();
        }
        Assert.Equal(new[] { "买菜", "做饭", "洗碗" }, FindAll(root, "title").Select(e => e.TextContent));
        Assert.Equal("", ((InputElement)Find(root, "draft")).Value);
        Assert.Equal("3", Find(root, "badge").TextContent);
        Assert.Equal("删除 做饭", FindAll(root, "remove")[1].GetAttribute("aria-label"));

        var last = FindAll(root, "item")[2];
        root.Input.Click(FindAll(root, "remove")[1]);
        root.RunFrame();
        Assert.Equal(new[] { "买菜", "洗碗" }, FindAll(root, "title").Select(e => e.TextContent));
        Assert.Same(last, FindAll(root, "item")[1]); // 带 @key 的子组件保留了它的元素
        Assert.Equal("3", FindAll(root, "item")[1].GetAttribute("data-id"));
    }

    [Fact]
    public void Handwritten_components_and_awaui_components_compose_both_ways()
    {
        var root = new UIRoot();
        root.Mount(new Shell());
        Assert.Equal("外壳：0", Find(root, "value").TextContent);
        root.Input.Click(Find(root, "inc"));
        root.RunFrame();
        Assert.Equal("外壳：5", Find(root, "value").TextContent);
        Assert.Equal("0", Find(root, "badge").TextContent);
    }
    [Fact]
    public void Img_and_inline_svg_mount_with_hints_alt_and_a_shared_static_document()
    {
        var pipeline = new RenderPipeline(new TestTextEngine());
        pipeline.SetViewport(new Size(400, 300), 1);
        var root = new UIRoot { Pipeline = pipeline };
        var diagnostics = new List<UIDiagnostic>();
        root.Diagnostic += diagnostics.Add;
        root.Mount(new Div { Children = [new Media(), new Media { PhotoHeight = 30 }] });
        root.RunFrame();
        Assert.Empty(diagnostics);

        var photos = FindAll(root, "photo").Cast<ImageElement>().ToList();
        Assert.Equal("照片", photos[0].Alt);
        Assert.Equal("照片", photos[0].AccessibleName);
        Assert.Null(photos[0].Source);
        // 没有来源：尺寸提示即占位尺寸
        Assert.Equal(new Size(40, 20), photos[0].BoundingRect.Size);
        Assert.Equal(new Size(40, 30), photos[1].BoundingRect.Size);
        Assert.Equal(AwAUI.Css.Values.CssObjectFit.Contain, photos[0].ComputedStyle!.ObjectFit);

        var icons = FindAll(root, "icon").Cast<SvgElement>().ToList();
        Assert.Equal(2, icons.Count);
        Assert.NotNull(icons[0].Document);
        Assert.Same(icons[0].Document, icons[1].Document);   // 每个内联 svg 只解析一次
        Assert.Empty(icons[0].Document!.Diagnostics);
        Assert.True(icons[0].Document!.UsesCurrentColor);
        Assert.Equal(new Size(10, 10), icons[0].BoundingRect.Size);
        Assert.Equal(Color.Parse("#ff0000"), icons[0].ComputedStyle!.ResolveColor(icons[0].ComputedStyle!.Color));
        Assert.Equal(1, icons[0].Recordings);                // 已按尺寸与颜色录制

        // 动态文档用组件标签 <Svg Document="@doc" />（AwAUI.* 命名空间内也解析为 AwAUI.UI.Svg，而不是命名空间 AwAUI.Svg）
        var dyn = FindAll(root, "dyn").Cast<SvgElement>().ToList();
        Assert.Equal(2, dyn.Count);
        Assert.Null(dyn[0].Document);

        // 内联 svg：viewbox → viewBox、文档内的 <style>、<image> 的 data: PNG 用 ImagingSvgImageResolver 解码（没有诊断）
        var pic = FindAll(root, "pic").Cast<SvgElement>().First();
        Assert.True(pic.Document!.HasViewBox);
        Assert.Empty(pic.Document.Diagnostics);
    }
}
