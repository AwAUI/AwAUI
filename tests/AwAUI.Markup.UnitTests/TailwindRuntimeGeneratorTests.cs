using Microsoft.CodeAnalysis;
using DiagnosticSeverity = Microsoft.CodeAnalysis.DiagnosticSeverity;

namespace AwAUI.Markup.UnitTests;

/// <summary>
/// MSBuild 属性 <c>AwAUITailwindRuntime</c>（design/23 机制 3、6）：为 true 时生成的 <c>TailwindStyles</c> 额外包含入口 CSS 原文
/// 与 <c>CreateRuntime()</c>；默认关闭，普通构建不生成这些成员。生成代码引用的 <c>AwAUI.Tailwind.Runtime.TailwindRuntime</c>
/// 不在测试编译的引用集中，因此这里只检查生成的文本。
/// </summary>
public class TailwindRuntimeGeneratorTests
{
    static string Styles(GeneratorHarness.Result r) => r.Generated("TailwindStyles.");

    [Fact]
    public void Runtime_property_emits_entry_css_and_factory()
    {
        var r = new GeneratorHarness { AssemblyName = "RuntimeApp" }
            .Property("AwAUITailwindRegister", "false")
            .Property("AwAUITailwindRuntime", "true")
            .TailwindStylesheet("Styles/app.css", "@import \"tailwindcss/utilities.css\";\n@theme { --color-brand: #123456; }")
            .Awaui("A.awaui", """<div class="p-2 bg-brand">x</div>""")
            .Run();
        Assert.DoesNotContain(r.GeneratorDiagnostics, d => d.Severity == DiagnosticSeverity.Error);
        string code = Styles(r);
        Assert.Contains("public const string EntryCss = @\"", code);
        Assert.Contains("@import \"\"tailwindcss/utilities.css\"\";", code);
        Assert.Contains("public static global::AwAUI.Tailwind.Runtime.TailwindRuntime CreateRuntime()", code);
        Assert.Contains("=> new global::AwAUI.Tailwind.Runtime.TailwindRuntime(EntryCss, Candidates, new global::AwAUI.Tailwind.TailwindOptions", code);
        // 运行时必须沿用构建期的主题根选择器，否则 :host 会让 AwAUI.Css 丢弃整条主题规则
        Assert.Contains("ThemeRootSelector = \":root\",", code);
        Assert.Contains("Preflight = global::AwAUI.Tailwind.PreflightKind.AwAUI,", code);
    }

    [Fact]
    public void Runtime_members_are_absent_by_default()
    {
        var r = new GeneratorHarness { AssemblyName = "NormalApp" }
            .Property("AwAUITailwindRegister", "false")
            .TailwindStylesheet("Styles/app.css", "@import \"tailwindcss/utilities.css\";")
            .Awaui("A.awaui", """<div class="p-2">x</div>""")
            .Run();
        Assert.DoesNotContain(r.GeneratorDiagnostics, d => d.Severity == DiagnosticSeverity.Error);
        string code = Styles(r);
        Assert.Contains("public const string Css", code);
        Assert.DoesNotContain("EntryCss", code);
        Assert.DoesNotContain("CreateRuntime", code);
    }
}
