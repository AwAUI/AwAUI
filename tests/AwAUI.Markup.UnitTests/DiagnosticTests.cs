using AwAUI.Markup.Compiler;
using static AwAUI.Markup.UnitTests.TestUtil;

namespace AwAUI.Markup.UnitTests;

public class DiagnosticTests
{
    const char Marker = '‸';

    /// <summary>输入中的 ‸ 标出期望的诊断起点（‸ 本身被删除）；断言该位置有给定代码的诊断。</summary>
    static MarkupDiagnostic AssertDiag(string marked, string code, string? path = null, DiagnosticSeverity severity = DiagnosticSeverity.Error)
    {
        int at = marked.IndexOf(Marker);
        Assert.True(at >= 0, "missing marker");
        string text = marked.Remove(at, 1);
        var diags = Diagnose(text, path);
        var match = diags.FirstOrDefault(d => d.Code == code && d.Span.Start == at);
        Assert.True(match != null,
            $"expected {code} at offset {at}; got:\n" + string.Join("\n", diags.Select(d => $"{d} @{d.Span}")));
        Assert.Equal(severity, match!.Severity);
        // 行列与偏移一致
        var src = new SourceText(text);
        Assert.Equal(src.GetLinePosition(at), match.Start);
        return match;
    }

    static void AssertNoDiag(string text, string code, string? path = null) =>
        Assert.DoesNotContain(Diagnose(text, path), d => d.Code == code);

    // ---------------------------------------------------------------- AW4001

    [Theory]
    [InlineData("<‸div>")]                                   // 文件末尾未闭合
    [InlineData("<div><‸span></div>")]                       // 结束标签不匹配：span 未闭合，div 正常闭合
    [InlineData("‸</div>")]                                  // 多余的结束标签
    [InlineData("<div>‸</span></div>")]                      // 元素内多余的结束标签
    [InlineData("<‸div class=\"a\"\n<span/></div>")]         // 开始标签缺少 >
    [InlineData("<‸img src=\"a.png\">")]                     // 空元素必须自闭合
    [InlineData("a ‸< b")]                                   // 孤立的 <
    [InlineData("‸<!DOCTYPE html>")]                         // <! 只在 <!-- 注释时被识别
    [InlineData("‸<>abc")]                                   // 片段未闭合
    [InlineData("<‸style>.a{}")]                             // style 未闭合
    [InlineData("<div ‸\"x\"></div>")]                       // 开始标签中的意外字符
    [InlineData("@if (x) { <‸div> }")]                       // 代码体内未闭合的元素
    [InlineData("<div>‸</div</div>")]                        // 结束标签缺少 >（被视为多余标签后继续）
    public void AW4001_unclosed_or_mismatched_tags(string marked) => AssertDiag(marked, DiagnosticCodes.UnclosedTag);

    [Fact]
    public void AW4001_mismatch_recovers_and_closes_ancestor()
    {
        var a = MarkupCompiler.Analyze("<div><span></div><text>ok</text>");
        Assert.Single(a.Diagnostics);
        Assert.Equal(2, a.Tree.Root.Items.Count);
        Assert.True(((Compiler.Syntax.ElementSyntax)a.Tree.Root.Items[0]).IsClosed);
    }

    [Fact]
    public void AW4001_end_tags_do_not_cross_code_bodies()
    {
        // </div> 不能闭合代码体外的 div：它在代码体内是多余的，外层 div 由最后的 </div> 闭合
        var diags = Diagnose("<div>@if (x) { <span></div> }</div>");
        Assert.Contains(diags, d => d.Code == DiagnosticCodes.UnclosedTag && d.Message.Contains("span"));
    }

    // ---------------------------------------------------------------- AW4002 / AW4003 / AW4004 / AW4005

    [Fact]
    public void AW4002_unknown_intrinsic_suggests_nearest()
    {
        var d = AssertDiag("<‸dvi></dvi>", DiagnosticCodes.UnknownElement);
        Assert.Contains("<div>", d.Message);
        var d2 = AssertDiag("<‸button />", DiagnosticCodes.UnknownElement);
        Assert.Contains("大写", d2.Message);
        AssertNoDiag("<div/><span/><text/><img/><input/><textarea/><canvas/><svg><path d=\"M0 0\"/></svg>", DiagnosticCodes.UnknownElement);
    }

    [Fact]
    public void AW4003_unknown_attribute_and_event()
    {
        Assert.Contains("class", AssertDiag("<div ‸clas=\"a\" />", DiagnosticCodes.UnknownAttribute).Message);
        Assert.Contains("@onclick", AssertDiag("<div ‸onclick=\"Go\" />", DiagnosticCodes.UnknownAttribute).Message);
        Assert.Contains("@onclick", AssertDiag("<div ‸@onclck=\"Go\" />", DiagnosticCodes.UnknownAttribute).Message);
        AssertNoDiag("<div aria-label=\"x\" data-id=\"1\" role=\"button\" tabindex=\"0\" @onclick=\"Go\" />", DiagnosticCodes.UnknownAttribute);
        AssertNoDiag("<Button anything=\"x\" @onwhatever=\"y\" />", DiagnosticCodes.UnknownAttribute); // 组件属性由 C# 编译器检查
    }

    [Theory]
    [InlineData("<div tabindex=‸\"abc\" />")]
    [InlineData("<input type=‸\"txt\" />")]
    [InlineData("<input maxlength=‸\"1@x\" />")]             // 非字符串类型不能拼接
    [InlineData("<div hidden=‸\"yes\" />")]
    [InlineData("<img width=‸\"10px\" />")]
    [InlineData("<div ‸tabindex />")]                         // 缺少值
    [InlineData("<div ‸@onclick />")]
    [InlineData("<div title=‸></div>")]
    public void AW4004_attribute_value_conversion(string marked) => AssertDiag(marked, DiagnosticCodes.InvalidAttributeValue);

    [Fact]
    public void AW4004_accepts_valid_values()
    {
        AssertNoDiag("""
            <div tabindex="-1" hidden dir="rtl" />
            <input type="password" maxlength=" @max " disabled="@flag" readonly="readonly" />
            <img width="1.5e2" height="@h" />
            <div title="a @b c" />
            """, DiagnosticCodes.InvalidAttributeValue);
    }

    [Fact]
    public void AW4005_duplicate_attribute()
    {
        AssertDiag("<div class=\"a\" ‸class=\"b\" />", DiagnosticCodes.DuplicateAttribute);
        AssertDiag("<Row @key=\"a\" ‸@key=\"b\" />", DiagnosticCodes.DuplicateAttribute);
        AssertNoDiag("<input @bind=\"a\" @bind:event=\"change\" />", DiagnosticCodes.DuplicateAttribute);
    }

    // ---------------------------------------------------------------- AW4006

    [Theory]
    [InlineData("‸@(a + b")]
    [InlineData("<div>@Foo‸(a</div>")]
    [InlineData("@‸{ var x = 1;")]
    [InlineData("@if ‸(x")]
    [InlineData("@if‸ x > 1 { }")]
    [InlineData("@if (x)‸ <div/>")]
    [InlineData("@foreach (var x in xs)‸ ")]
    [InlineData("@switch (x) ‸{ case 1: <div/>")]
    [InlineData("‸@* never closed")]
    [InlineData("a ‸@ b")]
    [InlineData("‸@code")]
    [InlineData("@code ‸{ void M() {")]
    [InlineData("<div title=‸\"abc />\n<span/>")]
    [InlineData("‸@()")]
    [InlineData("@{ if ‸(a { } }")]
    [InlineData("@switch (x) { ‸case 1 <div/> }")]
    [InlineData("@switch (x) { ‸case 1\n case 2: <div/> break; }")]
    [InlineData("@switch (x) { ‸case Point { X: 0 } <div/> break; }")]
    [InlineData("@switch (x) { case 1: break; ‸default <div/> }")]
    public void AW4006_unbalanced_delimiters(string marked) => AssertDiag(marked, DiagnosticCodes.UnbalancedDelimiter);

    [Fact]
    public void Switch_label_missing_colon_does_not_swallow_following_labels()
    {
        var diags = Diagnose("@switch (x) { case 1\n case 2: <div/> break; case List<int> l: <span/> break; }");
        Assert.Equal(DiagnosticCodes.UnbalancedDelimiter, Assert.Single(diags).Code);
    }

    // ---------------------------------------------------------------- AW4007

    [Theory]
    [InlineData("‸@page \"/x\"\n<div/>")]
    [InlineData("‸@inject IFoo Foo\n<div/>")]
    [InlineData("‸@functions {\n void M() { }\n}\n<div/>")]
    [InlineData("<div/>\n‸@using X")]                         // 指令必须在开头
    [InlineData("<div>\n‸@namespace X\n</div>")]              // 指令不能嵌套
    [InlineData("<div>‸@code { }</div>")]                      // @code 只能在顶层
    [InlineData("‸@else { }")]
    [InlineData("<div ‸@foo=\"x\" />")]                        // 未知指令属性
    [InlineData("@inherits A\n‸@inherits B\n<div/>")]           // 重复的指令
    [InlineData("‸@using\n<div/>")]                            // 缺少参数
    [InlineData("<div ‸@onclick:prevent=\"Go\" />")]
    public void AW4007_directives(string marked) => AssertDiag(marked, DiagnosticCodes.InvalidDirective);

    [Fact]
    public void Razor_directive_names_mid_text_are_expressions()
    {
        AssertNoDiag("<div>model: @model.Name, page @page</div>", DiagnosticCodes.InvalidDirective);
    }

    // ---------------------------------------------------------------- AW4008

    [Fact]
    public void AW4008_missing_key_in_loop_is_a_warning()
    {
        AssertDiag("@foreach (var x in xs) { <‸div>@x</div> }", DiagnosticCodes.MissingKeyInLoop, severity: DiagnosticSeverity.Warning);
        AssertDiag("@for (int i = 0; i < 3; i++) { @if (i > 0) { <‸Row /> } }", DiagnosticCodes.MissingKeyInLoop, severity: DiagnosticSeverity.Warning);
        AssertDiag("@while (a) { @switch (k) { case 1: <‸Row /> break; } }", DiagnosticCodes.MissingKeyInLoop, severity: DiagnosticSeverity.Warning);
        AssertNoDiag("@foreach (var x in xs) { <div @key=\"x\"><span>@x</span></div> }", DiagnosticCodes.MissingKeyInLoop);
        AssertNoDiag("@foreach (var x in xs) { @x }", DiagnosticCodes.MissingKeyInLoop);
    }

    // ---------------------------------------------------------------- AW4009

    [Theory]
    [InlineData("<div ‸@bind=\"x\" />")]
    [InlineData("<input @bind=\"x\" ‸value=\"y\" />")]
    [InlineData("<input @bind=\"x\" ‸@oninput=\"F\" />")]
    [InlineData("<input @bind=\"x\" @bind:event=\"change\" ‸@onchange=\"F\" />")]
    [InlineData("<input ‸@bind:event=\"change\" />")]
    [InlineData("<input @bind=\"x\" @bind:event=‸\"blur\" />")]
    [InlineData("<input @bind=‸\"Foo()\" />")]
    [InlineData("<input @bind=‸\"1\" />")]
    [InlineData("<input @bind=‸\"a + b\" />")]
    [InlineData("<input ‸@bind />")]
    [InlineData("<Slider ‸@bind=\"x\" />")]
    [InlineData("<input ‸@bind-Value=\"x\" />")]
    [InlineData("<Slider @bind-Value=\"v\" ‸Value=\"1\" />")]
    [InlineData("<Slider @bind-Value=\"v\" ‸ValueChanged=\"F\" />")]
    [InlineData("<input ‸@bind:format=\"x\" @bind=\"y\" />")]
    public void AW4009_bind(string marked) => AssertDiag(marked, DiagnosticCodes.InvalidBind);

    [Theory]
    [InlineData("<input @bind=\"draft\" />")]
    [InlineData("<input @bind=\"@model.Items[i].Name\" @bind:event=\"change\" @oninput=\"Typing\" />")]
    [InlineData("<textarea @bind=\"@(this.text)\"></textarea>")]
    [InlineData("<input @bind=\"user!.Name\" />")]
    [InlineData("<Slider @bind-Value=\"level\" @bind-Max=\"max\" />")]
    public void AW4009_valid_bindings(string text) => AssertNoDiag(text, DiagnosticCodes.InvalidBind);

    // ---------------------------------------------------------------- AW4010 / AW4011

    [Theory]
    [InlineData("<style ‸scoped>.a{}</style>")]
    [InlineData("<style ‸global=\"true\">.a{}</style>")]
    [InlineData("<div><‸style>.a{}</style></div>")]
    public void AW4010_style_blocks(string marked) => AssertDiag(marked, DiagnosticCodes.InvalidStyleBlock);

    [Theory]
    [InlineData("my-page.awaui")]
    [InlineData("class.awaui")]
    [InlineData("1Foo.awaui")]
    [InlineData("dir/Foo Bar.awaui")]
    public void AW4011_invalid_file_name(string path) => AssertDiag("‸<div/>", DiagnosticCodes.InvalidClassName, path);

    [Theory]
    [InlineData("Counter.awaui")]
    [InlineData("src\\Pages\\_Home2.awaui")]
    [InlineData("页面.awaui")]
    public void AW4011_valid_file_name(string path) => AssertNoDiag("<div/>", DiagnosticCodes.InvalidClassName, path);

    // ---------------------------------------------------------------- 一次报出多个错误

    [Fact]
    public void Reports_many_errors_in_one_pass()
    {
        const string text = """
            @using A
            <div class="a" class="b">
                <dvi>x</dvi>
                @(unclosed
                <span>
                @foreach (var x in xs) { <Row /> }
                <input @bind="F()" />
                <div tabindex="no" clas="x" />
            </div>
            @using B
            <style bogus>.x{}</style>
            """;
        var codes = Diagnose(text, "bad-name.awaui").Select(d => d.Code).Distinct().OrderBy(c => c).ToArray();
        Assert.Equal(new[] { "AW4001", "AW4002", "AW4003", "AW4004", "AW4005", "AW4006", "AW4007", "AW4008", "AW4009", "AW4010", "AW4011" }, codes);
    }

    [Fact]
    public void Diagnostics_are_sorted_and_formatted_msbuild_style()
    {
        var a = MarkupCompiler.Analyze("<div>\n  <dvi/>\n</span>", "Pages/Home.awaui");
        Assert.Equal(a.Diagnostics.OrderBy(d => d.Span.Start).Select(d => d.Span.Start), a.Diagnostics.Select(d => d.Span.Start));
        Assert.StartsWith("Pages/Home.awaui(1,2): error AW4001:", a.Diagnostics[0].ToString());
        Assert.StartsWith("Pages/Home.awaui(2,4): error AW4002:", a.Diagnostics[1].ToString());
    }
}
