namespace AwAUI.Markup.UnitTests;

/// <summary>
/// 目录级 <c>_Imports.awaui</c>（相当于 Blazor 的 <c>_Imports.razor</c>）：
/// 导入作用于同目录及子目录、由外向内累积，写入生成的 C#，文件自身的 <c>@using</c> 优先。
/// </summary>
public class ImportsTests
{
    const string Widget = """
        <div class="views-widget"><text>@Title</text></div>
        @code { public string Title { get; set; } = ""; }
        """;

    const string AdminWidget = """
        <div class="admin-widget"><text>@Title</text></div>
        @code { public string Title { get; set; } = ""; }
        """;

    const string CustomBadge = """
        namespace CustomLib
        {
            public sealed class Badge : global::AwAUI.UI.Component
            {
                protected override global::AwAUI.UI.Node Build() => new global::AwAUI.UI.Div();
            }
        }
        """;

    const string FrameworkWidget = """
        namespace AwAUI.Controls
        {
            public sealed class Widget : global::AwAUI.UI.Component
            {
                protected override global::AwAUI.UI.Node Build() => new global::AwAUI.UI.Div();
            }
        }
        """;

    [Fact]
    public void Imports_apply_to_nested_files_and_are_written_into_generated_code()
    {
        var result = new GeneratorHarness()
            .Awaui("_Imports.awaui", "@using TestApp.Views")
            .Awaui("Views/Widget.awaui", Widget)
            .Awaui("Pages/Home.awaui", "<div><Widget Title=\"hi\" /></div>")
            .Run();

        result.AssertNoErrors();
        string home = result.Generated("Home");
        Assert.Contains("using TestApp.Views;", home);
        Assert.Contains("global::TestApp.Views.Widget()", home);
    }

    [Fact]
    public void Imports_do_not_leak_out_of_their_directory()
    {
        var result = new GeneratorHarness()
            .Awaui("_Imports.awaui", "@using TestApp.Views")
            .Awaui("Admin/_Imports.awaui", "@using TestApp.Admin.Widgets")
            .Awaui("Views/Widget.awaui", Widget)
            .Awaui("Admin/Widgets/Panel.awaui", AdminWidget)
            .Awaui("Admin/Page.awaui", "<div><Widget Title=\"root\" /><Panel Title=\"admin\" /></div>")
            .Awaui("Public/Page.awaui", "<div><Widget Title=\"root\" /></div>")
            .Run();

        result.AssertNoErrors();
        string admin = result.Generated("Admin.Page");
        Assert.Contains("global::TestApp.Views.Widget()", admin);
        Assert.Contains("global::TestApp.Admin.Widgets.Panel()", admin);

        string publicPage = result.Generated("Public.Page");
        Assert.Contains("global::TestApp.Views.Widget()", publicPage);
        Assert.DoesNotContain("TestApp.Admin.Widgets", publicPage);
    }

    [Fact]
    public void File_using_wins_over_directory_imports()
    {
        var result = new GeneratorHarness()
            .Awaui("_Imports.awaui", "@using TestApp.Views")
            .Awaui("Views/Widget.awaui", Widget)
            .Awaui("Admin/_Imports.awaui", "@using TestApp.Admin.Widgets")
            .Awaui("Admin/Widgets/Widget.awaui", AdminWidget)
            .Awaui("Admin/Page.awaui", "@using TestApp.Views\n<div><Widget Title=\"x\" /></div>")
            .Run();

        result.AssertNoErrors();
        // 文件显式 using（TestApp.Views）+ 更近的 _Imports（TestApp.Admin.Widgets）都比根导入靠前：
        // 这里文件自身的 using 排在最前，因此 Views.Widget 胜出
        Assert.Contains("global::TestApp.Views.Widget()", result.Generated("Admin.Page"));
    }

    [Fact]
    public void Project_global_usings_resolve_tags()
    {
        // 项目级全局导入（<Using Include="..." /> 生成的 global using）与 _Imports.awaui 一样参与标签解析
        var result = new GeneratorHarness()
            .CSharp("GlobalUsings.g.cs", "global using global::TestApp.Views;")
            .Awaui("Views/Widget.awaui", Widget)
            .Awaui("Home.awaui", "<div><Widget Title=\"x\" /></div>")
            .Run();

        result.AssertNoErrors();
        Assert.Contains("global::TestApp.Views.Widget()", result.Generated("Home"));
    }

    [Fact]
    public void File_using_wins_over_project_global_usings()
    {
        var result = new GeneratorHarness()
            .CSharp("GlobalUsings.g.cs", "global using TestApp.Admin.Widgets;")
            .Awaui("Views/Widget.awaui", Widget)
            .Awaui("Admin/Widgets/Widget.awaui", AdminWidget)
            .Awaui("Home.awaui", "@using TestApp.Views\n<div><Widget Title=\"x\" /></div>")
            .Run();

        result.AssertNoErrors();
        Assert.Contains("global::TestApp.Views.Widget()", result.Generated("Home"));
    }

    [Fact]
    public void Project_tag_namespaces_resolve_short_tags()
    {
        // 项目文件中的 AwAUIMarkupNamespaces（分号或逗号分隔）参与标签解析，但不写入生成的 C#
        var result = new GeneratorHarness()
            .Property("AwAUIMarkupNamespaces", " TestApp.Admin.Widgets ;TestApp.Unused,TestApp.Admin.Widgets")
            .Awaui("Views/Widget.awaui", Widget)
            .Awaui("Admin/Widgets/Widget.awaui", AdminWidget)
            .Awaui("Home.awaui", "<div><Widget Title=\"x\" /></div>")
            .Run();

        result.AssertNoErrors();
        string home = result.Generated("Home");
        Assert.Contains("global::TestApp.Admin.Widgets.Widget()", home);
        Assert.DoesNotContain("using TestApp.Admin.Widgets", home);
    }

    [Fact]
    public void Builtin_tag_namespaces_can_be_replaced()
    {
        // AwAUIMarkupBuiltInNamespaces 替换（而不是追加）两个框架内置命名空间
        var result = new GeneratorHarness()
            .CSharp("CustomLib/Badge.cs", CustomBadge)
            .Property("AwAUIMarkupBuiltInNamespaces", " CustomLib ")
            .Awaui("Home.awaui", "<div><Badge /></div>")
            .Run();
        result.AssertNoErrors();
        Assert.Contains("global::CustomLib.Badge", result.Generated("Home"));

        // 未配置该属性时 CustomLib 不参与解析
        var without = new GeneratorHarness()
            .CSharp("CustomLib/Badge.cs", CustomBadge)
            .Awaui("Home.awaui", "<div><Badge /></div>")
            .Run();
        Assert.Contains(without.Errors, d => d.Id == "CS0246");
    }

    [Fact]
    public void Builtin_tag_namespaces_can_be_disabled()
    {
        var withDefault = new GeneratorHarness()
            .CSharp("Framework/Widget.cs", FrameworkWidget)
            .Awaui("Home.awaui", "<div><Widget /></div>")
            .Run();
        withDefault.AssertNoErrors();
        Assert.Contains("global::AwAUI.Controls.Widget", withDefault.Generated("Home"));

        var disabled = new GeneratorHarness()
            .CSharp("Framework/Widget.cs", FrameworkWidget)
            .Property("AwAUIMarkupBuiltInNamespaces", "none")
            .Awaui("Home.awaui", "<div><Widget /></div>")
            .Run();
        Assert.Contains(disabled.Errors, d => d.Id == "CS0246");
    }

    [Fact]
    public void Imports_file_does_not_generate_a_component()
    {
        var result = new GeneratorHarness()
            .Awaui("_Imports.awaui", "@using System.Globalization\n@using TestApp.Views")
            .Awaui("Views/Widget.awaui", Widget)
            .Awaui("Home.awaui", "<div><Widget Title=\"x\" /></div>")
            .Run();

        result.AssertNoErrors();
        Assert.DoesNotContain(result.Run.GeneratedTrees, t => t.FilePath.Contains("_Imports"));
        Assert.Equal(2, result.Run.GeneratedTrees.Count());
    }
}
