using AwAUI.Markup.IntegrationTests.Tailwind;
using AwAUI.Text.Testing;
using AwAUI.UI;

namespace AwAUI.Markup.IntegrationTests;

/// <summary>
/// Tailwind 构建期接入走真实的 MSBuild：<c>AwAUITailwindStylesheet</c> 项 → AdditionalFiles 元数据 → 生成器扫描 Tailwind/ 下的 .awaui，
/// 生成 <c>TailwindStyles</c> 并由模块初始化器登记到 <see cref="AppStyleSheets"/>；渲染管线在第一帧把它加入级联。
/// </summary>
public class TailwindBuildTests
{
    [Fact]
    public void Generated_stylesheet_contains_only_classes_from_the_source_scope()
    {
        Assert.Contains(".p-4 {", TailwindStyles.Css);
        Assert.Contains(".bg-primary {", TailwindStyles.Css);
        Assert.Contains(".data-\\[state\\=open\\]\\:p-6[data-state=\"open\"]", TailwindStyles.Css);
        Assert.Contains("@layer theme", TailwindStyles.Css);
        Assert.DoesNotContain(":host", TailwindStyles.Css);
        // Components/ 下的组件不在 @source 范围内（source(none) 关闭了自动检测）
        Assert.DoesNotContain(".counter", TailwindStyles.Css);
        Assert.Contains("p-4", TailwindStyles.Candidates);
        Assert.Equal("tailwind:AwAUI.Markup.IntegrationTests/Tailwind/app.css", TailwindStyles.SourceName);
    }

    [Fact]
    public void Module_initializer_registers_the_stylesheet_and_the_pipeline_applies_it()
    {
        Assert.Contains(AppStyleSheets.Registered, s => s.SourceName == TailwindStyles.SourceName);
        var pipeline = new RenderPipeline(new TestTextEngine());
        pipeline.SetViewport(new Size(400, 300), 1);
        var root = new UIRoot { Pipeline = pipeline };
        root.Mount(new Div
        {
            Children =
            [
                new TwCard { Title = "a", Rest = new AttributeDictionary { ["id"] = "closed", ["aria-label"] = "卡片" } },
                new Div { Class = "dark", Children = [new TwCard { Title = "b", State = "open", Rest = new AttributeDictionary { ["id"] = "open" } }] },
            ],
        });
        root.RunFrame();
        Assert.Contains(pipeline.StyleEngine.StyleSheets, s => ReferenceEquals(s.Sheet, AppStyleSheets.Registered.Single(r => r.SourceName == TailwindStyles.SourceName).StyleSheet));

        var closed = Find(root, "closed");
        var style = closed.ComputedStyle!;
        Assert.Equal(16f, style.PaddingTop.Resolve(0));               // p-4 = calc(var(--spacing) * 4)，--spacing: 0.25rem，根字号 16px
        Assert.Equal(Color.Parse("#3366ff"), style.ResolveColor(style.BackgroundColor)); // bg-primary → var(--color-primary) → var(--primary)
        Assert.Equal(Color.Parse("#fafafa"), style.ResolveColor(style.Color));
        Assert.Equal(8f, style.BorderTopLeftRadius.Horizontal.Resolve(0));       // rounded-md = calc(0.625rem - 2px)
        Assert.Equal("card", closed.GetAttribute("data-slot"));
        Assert.Equal("卡片", closed.GetAttribute("aria-label"));
        var label = closed.Children.Single(c => c.Tag == "span").ComputedStyle!;
        Assert.Equal(14f, label.FontSize);                              // text-sm
        Assert.Equal(500, label.FontWeight);                            // [&_span]:font-medium

        var open = Find(root, "open").ComputedStyle!;
        Assert.Equal(24f, open.PaddingTop.Resolve(0));                 // data-[state=open]:p-6
        Assert.Equal(Color.Parse("#e5e5e5"), open.ResolveColor(open.BackgroundColor)); // .dark 令牌
    }

    [Fact]
    public void App_stylesheets_precede_component_stylesheets_so_tailwind_layer_order_holds()
    {
        // 组件挂载时登记组件样式表（早于第一帧）；Tailwind 样式表若排在它后面，组件里的 @layer components 会先确立为最低的层，
        // preflight（@layer base）的 margin: 0 就会盖过组件层的 margin
        var pipeline = new RenderPipeline(new TestTextEngine());
        pipeline.SetViewport(new Size(400, 300), 1);
        var root = new UIRoot { Pipeline = pipeline };
        root.Mount(new TwLayered { Id = "layered" });
        root.RunFrame();
        var style = Find(root, "layered").ComputedStyle!;
        Assert.Equal(3f, style.MarginTop.Value.Resolve(0));   // @layer components 高于 @layer base（preflight 的 margin: 0）
        Assert.Equal(4f, style.PaddingTop.Resolve(0));  // @layer utilities（p-1）高于 @layer components

        var sheets = pipeline.StyleEngine.StyleSheets.Select(s => s.Sheet).ToList();
        int tailwind = sheets.IndexOf(AppStyleSheets.Registered.Single(r => r.SourceName == TailwindStyles.SourceName).StyleSheet);
        int component = sheets.FindIndex(s => s.SourceName?.Contains("TwLayered", StringComparison.Ordinal) == true);
        Assert.True(tailwind >= 0 && component >= 0, $"tailwind {tailwind}, component {component}");
        Assert.True(tailwind < component, $"tailwind {tailwind}, component {component}");
    }

    static Element Find(UIRoot root, string id)
    {
        var stack = new Stack<Element>();
        stack.Push(root.RootElement);
        while (stack.Count > 0)
        {
            var e = stack.Pop();
            if (e.Id == id) return e;
            foreach (var c in e.Children) stack.Push(c);
        }
        throw new InvalidOperationException("#" + id);
    }
}
