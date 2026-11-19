using System.Text;
using System.Text.RegularExpressions;
using AwAUI.Markup.Compiler;
using AwAUI.Markup.Compiler.CodeGen;
using AwAUI.Svg;

namespace AwAUI.Markup.UnitTests;

/// <summary><c>img</c> 的数值尺寸属性与内联 <c>svg</c> 的编译期序列化（<c>AwAUI.UI.Image</c> / <c>AwAUI.UI.Svg</c>）。</summary>
public class MediaElementTests
{
    static CompileResultView Compile(string text)
    {
        var c = MarkupCompiler.Compile(text, "M.awaui", new CSharpEmitOptions { EmitLineDirectives = false });
        return new CompileResultView(c.GeneratedCode, c.Diagnostics.ToList());
    }

    sealed record CompileResultView(string Code, List<MarkupDiagnostic> Diagnostics)
    {
        public IEnumerable<MarkupDiagnostic> Errors => Diagnostics.Where(d => d.IsError);
    }

    /// <summary>取出生成代码中 <c>SvgDocument.Parse("...")</c> 的参数（反转义 C# 字面量）。</summary>
    static List<string> SvgTexts(string code)
    {
        var result = new List<string>();
        foreach (Match m in Regex.Matches(code, "SvgDocument\\.Parse\\(\"((?:[^\"\\\\]|\\\\.)*)\"(?:, [^)]*)?\\)"))
        {
            var s = m.Groups[1].Value;
            var sb = new StringBuilder();
            for (int i = 0; i < s.Length; i++)
            {
                if (s[i] != '\\') { sb.Append(s[i]); continue; }
                char e = s[++i];
                switch (e)
                {
                    case 'n': sb.Append('\n'); break;
                    case 'r': sb.Append('\r'); break;
                    case 't': sb.Append('\t'); break;
                    case '0': sb.Append('\0'); break;
                    case 'u': sb.Append((char)Convert.ToInt32(s.Substring(i + 1, 4), 16)); i += 4; break;
                    default: sb.Append(e); break;
                }
            }
            result.Add(sb.ToString());
        }
        return result;
    }

    // ------------------------------------------------------------ img

    [Fact]
    public void Img_width_and_height_map_to_numeric_hints_and_alt_to_the_accessible_name()
    {
        var c = Compile("""<div><img src="a.png" alt="标志" width="24" height="1.5e1" /><img src="@src" width="@w" height="@(h * 2)" /></div>""");
        Assert.Empty(c.Diagnostics);
        Assert.Contains("new global::AwAUI.UI.Image();", c.Code);
        Assert.Contains(".Source = \"a.png\";", c.Code);
        Assert.Contains(".Alt = \"标志\";", c.Code);
        Assert.Contains(".Width = 24f;", c.Code);
        Assert.Contains(".Height = 15f;", c.Code);
        Assert.Contains(".Width = w;", c.Code);
        Assert.Contains(".Height = h * 2;", c.Code);
        Assert.DoesNotContain("Attributes[\"width\"]", c.Code);
    }

    [Fact]
    public void Img_width_out_of_float_range_is_clamped_and_invalid_literals_are_diagnosed()
    {
        var big = Compile("""<img width="1e300" />""");
        Assert.Empty(big.Errors);
        Assert.Contains($".Width = {float.MaxValue.ToString("R", System.Globalization.CultureInfo.InvariantCulture)}f;", big.Code);
        var bad = Compile("""<img width="10px" />""");
        Assert.Contains(bad.Errors, d => d.Code == DiagnosticCodes.InvalidAttributeValue);
        Assert.DoesNotContain(".Width =", bad.Code);
    }

    [Fact]
    public void Img_loading_is_still_a_pass_through_with_a_warning()
    {
        var c = Compile("""<img loading="lazy" />""");
        Assert.Empty(c.Errors);
        Assert.Contains(c.Diagnostics, d => d.Code == DiagnosticCodes.NotSupportedByRuntime && d.Message.Contains("loading"));
    }

    // ------------------------------------------------------------ svg

    [Fact]
    public void Inline_svg_maps_host_attributes_to_the_node_and_serializes_the_rest()
    {
        var c = Compile("""<svg id="logo" class="icon" style="color: red" aria-label="标志" data-k="1" @onclick="Go" viewBox="0 0 24 24" width="24" height="24" fill="currentColor"><path d="M0 0L24 24" stroke-width="2"/><circle cx="12" cy="12" r="4"></circle><title>A &amp; B</title></svg>""");
        Assert.Empty(c.Diagnostics);
        Assert.Contains("new global::AwAUI.UI.Svg();", c.Code);
        Assert.Contains(".Id = \"logo\";", c.Code);
        Assert.Contains(".Class = \"icon\";", c.Code);
        Assert.Contains(".Style = \"color: red\";", c.Code);
        Assert.Contains(".AriaLabel = \"标志\";", c.Code);
        Assert.Contains(".Attributes[\"data-k\"] = \"1\";", c.Code);
        Assert.Contains(".OnClick = global::AwAUI.UI.EventHandlers.Wrap<global::AwAUI.UI.PointerEvent>(Go);", c.Code);
        Assert.Contains(".Document = __svg0 ?? (__svg0 = global::AwAUI.Svg.SvgDocument.Parse(", c.Code);
        Assert.Contains("private static global::AwAUI.Svg.SvgDocument __svg0;", c.Code);
        var xml = Assert.Single(SvgTexts(c.Code));
        Assert.Equal("<svg viewBox=\"0 0 24 24\" width=\"24\" height=\"24\" fill=\"currentColor\"><path d=\"M0 0L24 24\" stroke-width=\"2\"/>" +
                     "<circle cx=\"12\" cy=\"12\" r=\"4\"/><title>A &amp; B</title></svg>", xml);

        // 序列化结果能被运行时的 SVG 解析器无诊断地解析
        var doc = SvgDocument.Parse(xml);
        Assert.Empty(doc.Diagnostics);
        Assert.Equal(new Size(24, 24), doc.IntrinsicSize);
        Assert.True(doc.UsesCurrentColor);
    }

    [Fact]
    public void Each_inline_svg_gets_its_own_static_document_and_indented_markup_parses()
    {
        var c = Compile("""
            <div>
                <svg viewBox="0 0 10 10">
                    <g transform="translate(1 1)">
                        <rect width="8" height="8" rx="1" />
                    </g>
                </svg>
                @if (show)
                {
                    <svg width="5" height="5"><circle r="2" cx="2.5" cy="2.5" fill="#f00" /></svg>
                }
            </div>
            """);
        Assert.Empty(c.Diagnostics);
        Assert.Contains("private static global::AwAUI.Svg.SvgDocument __svg0;", c.Code);
        Assert.Contains("private static global::AwAUI.Svg.SvgDocument __svg1;", c.Code);
        var texts = SvgTexts(c.Code);
        Assert.Equal(2, texts.Count);
        foreach (var t in texts) Assert.Empty(SvgDocument.Parse(t).Diagnostics);
        Assert.Contains("<rect width=\"8\" height=\"8\" rx=\"1\"/>", texts[0]);
    }

    [Fact]
    public void Svg_text_and_attribute_values_are_xml_escaped()
    {
        var c = Compile("""<svg><desc lang='x"y'>a &lt; b &amp; c</desc><path d="M0 0" class="a&gt;b"/></svg>""");
        Assert.Empty(c.Diagnostics);
        var xml = Assert.Single(SvgTexts(c.Code));
        Assert.Equal("<svg><desc lang=\"x&quot;y\">a &lt; b &amp; c</desc><path d=\"M0 0\" class=\"a&gt;b\"/></svg>", xml);
        Assert.Empty(SvgDocument.Parse(xml).Diagnostics);
    }

    [Fact]
    public void Svg_elements_and_attributes_outside_the_intrinsic_table_are_accepted()
    {
        var diags = MarkupCompiler.Analyze("""
            <svg version="1.1" xmlns:xlink="http://www.w3.org/1999/xlink">
                <defs><linearGradient id="g" gradientUnits="userSpaceOnUse"><stop offset="0" stop-color="#fff"/></linearGradient></defs>
                <use href="#g" x="1"/>
            </svg>
            """, "S.awaui").Diagnostics;
        Assert.Empty(diags);
        // 内建表里已知的属性仍做类型检查
        var typed = MarkupCompiler.Analyze("""<svg opacity="half"><path opacity="x" /></svg>""", "T.awaui").Diagnostics;
        Assert.Equal(2, typed.Count(d => d.Code == DiagnosticCodes.InvalidAttributeValue));
        // svg 之外仍然报未知元素
        Assert.Contains(MarkupCompiler.Analyze("<circle />", "U.awaui").Diagnostics, d => d.Code == DiagnosticCodes.UnknownElement);
    }

    [Fact]
    public void Dynamic_content_inside_inline_svg_is_an_error_and_no_document_is_set()
    {
        var c = Compile("""
            <svg width="@w" class="@cls">
                <path d="@d" />
                @label
                <Icon />
                <g @onclick="X"><rect /></g>
                @if (a) { <rect /> }
            </svg>
            """);
        var errors = c.Errors.Where(d => d.Code == DiagnosticCodes.NotSupportedByRuntime).Select(d => d.Message).ToList();
        Assert.Equal(6, errors.Count);
        Assert.Contains(errors, m => m.Contains("属性 width"));
        Assert.Contains(errors, m => m.Contains("属性 d"));
        Assert.Contains(errors, m => m.Contains("表达式"));
        Assert.Contains(errors, m => m.Contains("组件 <Icon>"));
        Assert.Contains(errors, m => m.Contains("@onclick"));
        Assert.Contains(errors, m => m.Contains("控制流"));
        // class 是宿主属性：表达式照常生成
        Assert.Contains(".Class = cls;", c.Code);
        Assert.DoesNotContain("SvgDocument.Parse", c.Code);
        Assert.DoesNotContain("__svg", c.Code);
    }

    [Fact]
    public void Path_outside_svg_is_still_unsupported()
    {
        var c = Compile("<div><path d=\"M0 0\" /></div>");
        Assert.Contains(c.Errors, d => d.Code == DiagnosticCodes.NotSupportedByRuntime && d.Message.Contains("<path>"));
    }
}
