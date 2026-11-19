using System.Text;
using System.Text.RegularExpressions;
using AwAUI.Markup.Compiler;
using AwAUI.Markup.Compiler.CodeGen;
using AwAUI.Markup.Compiler.Syntax;
using AwAUI.Markup.Compiler.Transforms;
using AwAUI.Svg;
using AwAUI.Tests.Shared;
using static AwAUI.Markup.UnitTests.TestUtil;

namespace AwAUI.Markup.UnitTests;

/// <summary>
/// HTML 注释 <c>&lt;!-- --&gt;</c>、枚举属性 <c>draggable</c>，以及内联 svg 的 <c>&lt;style&gt;</c>、名称大小写修正与图片解析选项。
/// </summary>
public class HtmlCommentAndEnumTests
{
    static string Tree(string text) => Dump(WhitespaceNormalizer.Normalize(ParseClean(text))).Replace("\r", "");

    static (string Code, List<MarkupDiagnostic> Diagnostics) Compile(string text)
    {
        var c = MarkupCompiler.Compile(text, "M.awaui", new CSharpEmitOptions { EmitLineDirectives = false });
        return (c.GeneratedCode, c.Diagnostics.ToList());
    }

    // ------------------------------------------------------------ HTML 注释

    [Fact]
    public void Html_comments_parse_as_comments_inside_elements()
    {
        Assert.Equal("""
            Document
              Element div
                Text "a"
                Comment html " <b>x</b> @if "
                Text "b"

            """.Replace("\r", ""), Tree("<div>a<!-- <b>x</b> @if -->b</div>"));
    }

    [Theory]
    [InlineData("<!-- top -->\n<div/>")]
    [InlineData("<div><!-- only --></div>")]
    [InlineData("<div>@if (x) { <!-- in block --><span/> }</div>")]
    [InlineData("@{ <!-- statement position --> var a = 1; }\n<div/>")]
    [InlineData("<!-- c --> @using X\n<div/>")]
    [InlineData("<!---->\n<!-->\n<!--->\n<div/>")]                      // 空注释与 HTML 的突然结束
    [InlineData("<div><!-- a -- b --!></div>")]                          // -- 可以出现在内容中；--!> 也结束注释
    [InlineData("<text>a <!-- <style>.x{}</style> --> b</text>")]
    public void Html_comments_produce_no_diagnostics_and_no_output(string text)
    {
        Assert.Empty(Diagnose(text));
        var (code, diags) = Compile(text);
        Assert.DoesNotContain(diags, d => d.IsError);
        Assert.DoesNotContain("<!--", code);
        Assert.DoesNotContain("statement position", code);
    }

    [Fact]
    public void Html_comment_may_precede_directives()
    {
        var doc = ParseClean("<!-- header -->\n@using X\n<div/>");
        var c = Assert.IsType<CommentSyntax>(doc.Items[0]);
        Assert.True(c.IsHtml);
        Assert.Equal(" header ", c.Content);
    }

    [Fact]
    public void Unterminated_html_comment_is_reported_and_swallows_the_rest()
    {
        var diags = Diagnose("<div>a</div>\n<!-- never closed <div>");
        var d = Assert.Single(diags);
        Assert.Equal(DiagnosticCodes.UnbalancedDelimiter, d.Code);
        Assert.Equal("<div>a</div>\n".Length, d.Span.Start);
        Assert.Contains("-->", d.Message);
    }

    [Fact]
    public void Html_comment_inside_a_start_tag_is_reported_and_skipped()
    {
        var diags = Diagnose("<div <!-- c --> class=\"a\"></div>");
        var d = Assert.Single(diags);
        Assert.Equal(DiagnosticCodes.UnclosedTag, d.Code);
        Assert.Equal(5, d.Span.Start);
        var el = Single<ElementSyntax>(ParseCleanAllowingErrors("<div <!-- c --> class=\"a\"></div>"));
        Assert.Equal("class", Assert.Single(el.Attributes).Name);
    }

    static DocumentSyntax ParseCleanAllowingErrors(string text) => MarkupSyntaxTree.Parse(text).Root;

    [Fact]
    public void Html_comments_round_trip_through_the_formatter()
    {
        RoundTrip.Check("<div>x<!-- c -->y</div>");
        RoundTrip.Check("<!-- top -->\n<div>\n  <!-- inner -->\n  <span/>\n</div>");
        string formatted = RoundTrip.Check("<div><!----></div>");
        Assert.Contains("<!---->", formatted);
    }

    [Fact]
    public void Long_html_comment_scan_is_linear()
    {
        // 大量 "--" 而没有结束符：每个位置只扫描一次
        var text = "<!--" + new StringBuilder().Insert(0, "-- ", 200_000) + "-->";
        double start = ThreadCpuClock.NowMs();
        Assert.Empty(Diagnose(text));
        // 线程 CPU 时间（不受机器负载影响）；线性扫描约几十毫秒，平方级会是数分钟
        Assert.True(ThreadCpuClock.NowMs() - start < 10_000);
        var unterminated = "<!--" + new string('-', 300_000);
        Assert.Single(Diagnose(unterminated));
    }

    [Fact]
    public void Doctype_and_other_bang_markup_still_report_aw4001()
    {
        var d = Assert.Single(Diagnose("<!DOCTYPE html>"));
        Assert.Equal(DiagnosticCodes.UnclosedTag, d.Code);
    }

    // ------------------------------------------------------------ draggable

    [Theory]
    [InlineData("true")]
    [InlineData("false")]
    public void Draggable_is_an_enumerated_attribute_and_keeps_its_value(string value)
    {
        var (code, diags) = Compile($"<div draggable=\"{value}\" />");
        Assert.DoesNotContain(diags, d => d.IsError);
        Assert.Contains($".Attributes[\"draggable\"] = \"{value}\";", code);
    }

    [Fact]
    public void Draggable_requires_true_or_false()
    {
        Assert.Contains(Diagnose("<div draggable=\"yes\" />"), d => d.Code == DiagnosticCodes.InvalidAttributeValue);
        Assert.Contains(Diagnose("<div draggable />"), d => d.Code == DiagnosticCodes.InvalidAttributeValue);
    }

    [Fact]
    public void Draggable_expression_is_written_as_a_lowercase_keyword()
    {
        var (code, diags) = Compile("<div draggable=\"@canDrag\" />");
        Assert.DoesNotContain(diags, d => d.IsError);
        Assert.Contains(".Attributes[\"draggable\"] = global::System.Convert.ToString((object)(canDrag), global::System.Globalization.CultureInfo.InvariantCulture).ToLowerInvariant();", code);
        // 与 C# 中的 bool 一致
        Assert.Equal("false", Convert.ToString((object)false, System.Globalization.CultureInfo.InvariantCulture)!.ToLowerInvariant());
    }

    // ------------------------------------------------------------ spellcheck

    [Theory]
    [InlineData("input", "true")]
    [InlineData("input", "false")]
    [InlineData("textarea", "false")]
    public void Spellcheck_is_an_enumerated_attribute_and_keeps_false(string element, string value)
    {
        // HTML 的枚举属性（true / false / 空串）：spellcheck="false" 与不写不同，不能像布尔属性那样丢掉
        var diags = Diagnose($"<{element} spellcheck=\"{value}\" />");
        Assert.DoesNotContain(diags, d => d.Code == DiagnosticCodes.InvalidAttributeValue);
        if (element == "input")
        {
            var (code, _) = Compile($"<input spellcheck=\"{value}\" />");
            Assert.Contains($".Attributes[\"spellcheck\"] = \"{value}\";", code);
        }
    }

    [Fact]
    public void Spellcheck_without_a_value_or_with_an_empty_value_is_valid()
    {
        Assert.DoesNotContain(Diagnose("<input spellcheck />"), d => d.Code == DiagnosticCodes.InvalidAttributeValue);
        Assert.DoesNotContain(Diagnose("<input spellcheck=\"\" />"), d => d.Code == DiagnosticCodes.InvalidAttributeValue);
        var (code, _) = Compile("<input spellcheck />");
        Assert.Contains(".Attributes[\"spellcheck\"] = \"\";", code);
        Assert.Contains(Diagnose("<input spellcheck=\"yes\" />"), d => d.Code == DiagnosticCodes.InvalidAttributeValue);
    }

    [Fact]
    public void Spellcheck_expression_is_written_as_a_lowercase_keyword()
    {
        var (code, diags) = Compile("<input spellcheck=\"@check\" />");
        Assert.DoesNotContain(diags, d => d.IsError);
        Assert.Contains(".Attributes[\"spellcheck\"] = global::System.Convert.ToString((object)(check), global::System.Globalization.CultureInfo.InvariantCulture).ToLowerInvariant();", code);
    }

    [Fact]
    public void Hidden_stays_a_boolean_attribute()
    {
        var (code, _) = Compile("<div hidden=\"false\" /><span hidden />");
        Assert.DoesNotContain("Attributes[\"hidden\"] = \"false\"", code);
        Assert.Contains(".Attributes[\"hidden\"] = \"\";", code);
    }

    // ------------------------------------------------------------ 内联 svg

    static List<string> SvgTexts(string code)
    {
        var result = new List<string>();
        foreach (Match m in Regex.Matches(code, "SvgDocument\\.Parse\\(\"((?:[^\"\\\\]|\\\\.)*)\"(?:, [^)]*)?\\)"))
            result.Add(Regex.Unescape(m.Groups[1].Value));
        return result;
    }

    [Fact]
    public void Inline_svg_is_parsed_with_the_imaging_image_resolver()
    {
        var (code, diags) = Compile("<svg viewBox=\"0 0 4 4\"><rect width=\"4\" height=\"4\" /></svg>");
        Assert.Empty(diags);
        Assert.Contains("SvgDocument.Parse(\"<svg viewBox=\\\"0 0 4 4\\\"><rect width=\\\"4\\\" height=\\\"4\\\"/></svg>\", global::AwAUI.UI.ImagingSvgImageResolver.DefaultOptions)", code);
    }

    [Fact]
    public void Svg_attribute_and_element_names_are_case_adjusted_like_html_foreign_content()
    {
        var (code, diags) = Compile("""
            <svg viewbox="0 0 10 10" PRESERVEASPECTRATIO="xMinYMin meet">
              <defs><lineargradient id="g" gradientunits="userSpaceOnUse" x1="0" x2="10"><stop offset="0" stop-color="#f00" /></lineargradient></defs>
              <clippath id="c" clippathunits="userSpaceOnUse"><rect width="5" height="5" /></clippath>
              <rect width="10" height="10" Fill="url(#g)" clip-path="url(#c)" />
            </svg>
            """);
        Assert.DoesNotContain(diags, d => d.IsError);
        var xml = Assert.Single(SvgTexts(code));
        Assert.Contains("viewBox=\"0 0 10 10\"", xml);
        Assert.Contains("preserveAspectRatio=\"xMinYMin meet\"", xml);
        Assert.Contains("<linearGradient id=\"g\" gradientUnits=\"userSpaceOnUse\"", xml);
        Assert.Contains("</linearGradient>", xml);
        Assert.Contains("<clipPath id=\"c\" clipPathUnits=\"userSpaceOnUse\">", xml);
        Assert.Contains("fill=\"url(#g)\"", xml);
        var doc = SvgDocument.Parse(xml);
        Assert.Empty(doc.Diagnostics);
        Assert.True(doc.HasViewBox);
    }

    [Fact]
    public void Style_inside_inline_svg_belongs_to_the_document()
    {
        const string text = """
            <svg viewBox="0 0 10 10">
              <style type="text/css">.a > rect { fill: #00f }</style>
              <g class="a"><rect width="10" height="10" /></g>
            </svg>
            """;
        Assert.DoesNotContain(Diagnose(text), d => d.IsError);
        var (code, diags) = Compile(text);
        Assert.DoesNotContain(diags, d => d.IsError);
        var xml = Assert.Single(SvgTexts(code));
        Assert.Contains("<style type=\"text/css\">.a &gt; rect { fill: #00f }</style>", xml);
        // AwAUI.Svg 无诊断地解析并应用文档内的样式
        var doc = SvgDocument.Parse(xml);
        Assert.Empty(doc.Diagnostics);
        Assert.False(doc.ToDisplayList(new Rect(0, 0, 10, 10)).IsEmpty);
        // 组件样式块不受影响：<style> 不出现在生成的作用域样式中
        Assert.DoesNotContain("__StyleScope", code);
        RoundTrip.Check(text);
    }

    [Fact]
    public void Style_outside_svg_is_still_top_level_only_and_svg_style_rejects_global()
    {
        Assert.Contains(Diagnose("<div><style>.a{}</style></div>"), d => d.Code == DiagnosticCodes.InvalidStyleBlock);
        Assert.Contains(Diagnose("<svg><style global>.a{}</style></svg>"), d => d.Code == DiagnosticCodes.InvalidStyleBlock);
    }
}
