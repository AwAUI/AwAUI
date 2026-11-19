using System.Text;
using AwAUI.Graphics;

namespace AwAUI.Svg.UnitTests;

/// <summary>XML 子集、文档尺寸、样式层叠、引用（use/defs/渐变/裁剪）与绘制语义。</summary>
public class DocumentTests
{
    const string Ns = "xmlns=\"http://www.w3.org/2000/svg\"";

    static SvgDocument Doc(string body, string attrs = "viewBox=\"0 0 10 10\"") =>
        SvgDocument.Parse($"<svg {Ns} {attrs}>{body}</svg>");

    static (byte R, byte G, byte B, byte A) Probe(SvgDocument doc, float x, float y, int size = 10, SvgRenderOptions? options = null)
    {
        using var bmp = SvgGolden.Render(doc, size, size, options, Color.Transparent);
        return SvgGolden.Pixel(bmp, (int)x, (int)y);
    }

    static void AssertColor(string hex, (byte R, byte G, byte B, byte A) px, int tolerance = 2)
    {
        var c = Color.Parse(hex);
        int Ch(float v) => (int)Math.Round(v * 255);
        Assert.True(Math.Abs(Ch(c.R) - px.R) <= tolerance && Math.Abs(Ch(c.G) - px.G) <= tolerance &&
                    Math.Abs(Ch(c.B) - px.B) <= tolerance && Math.Abs(Ch(c.A) - px.A) <= tolerance,
            $"expected {hex}, got #{px.R:x2}{px.G:x2}{px.B:x2}{px.A:x2}");
    }

    static bool HasDiagnostic(SvgDocument doc, SvgDiagnosticCode code) => doc.Diagnostics.Any(d => d.Code == code);

    // ---------------------------------------------------------------- XML

    [Fact]
    public void Predefined_and_numeric_entities_are_decoded()
    {
        var doc = SvgDocument.Parse($"<svg {Ns} viewBox=\"0 0 10 10\"><rect width=\"10\" height=\"10\" fill=\"&#x23;f&#48;0\" id=\"a&amp;b&lt;&gt;&quot;&apos;\"/></svg>");
        Assert.Empty(doc.Diagnostics);
        AssertColor("#ff0000", Probe(doc, 5, 5));
    }

    [Fact]
    public void Doctype_is_skipped_and_its_entities_are_not_expanded()
    {
        var svg = """
            <?xml version="1.0" encoding="UTF-8" standalone="no"?>
            <!DOCTYPE svg PUBLIC "-//W3C//DTD SVG 1.1//EN" "http://www.w3.org/Graphics/SVG/1.1/DTD/svg11.dtd" [
              <!ENTITY ns_svg "http://www.w3.org/2000/svg">
              <!ENTITY red "#ff0000">
              <!-- ] > tricky comment -->
              <!ATTLIST svg foo CDATA "]>">
            ]>
            <svg xmlns="&ns_svg;" viewBox="0 0 10 10"><rect width="10" height="10" fill="&red;"/></svg>
            """;
        var doc = SvgDocument.Parse(svg);
        Assert.True(HasDiagnostic(doc, SvgDiagnosticCode.DoctypeIgnored));
        Assert.True(HasDiagnostic(doc, SvgDiagnosticCode.UndefinedEntity));
        // &red; 未展开 → fill 无效 → 默认黑色
        AssertColor("#000000", Probe(doc, 5, 5));
    }

    [Fact]
    public void Comments_processing_instructions_and_cdata()
    {
        var doc = SvgDocument.Parse($$"""
            <?xml version="1.0"?>
            <!-- leading comment -->
            <svg {{Ns}} viewBox="0 0 10 10">
              <?some-pi data?>
              <style><![CDATA[ rect { fill: #00ff00 } /* <not a tag> */ ]]></style>
              <!-- <rect width="10" height="10" fill="red"/> -->
              <rect width="10" height="10"/>
            </svg>
            <!-- trailing comment -->
            """);
        Assert.Empty(doc.Diagnostics);
        AssertColor("#00ff00", Probe(doc, 5, 5));
    }

    [Theory]
    [InlineData("<svg><g></svg>")]
    [InlineData("<svg><g></g>")]
    [InlineData("<svg></svg><svg></svg>")]
    [InlineData("<svg a=\"1\" a=\"2\"></svg>")]
    [InlineData("<svg a=1></svg>")]
    [InlineData("<svg a=\"<\"></svg>")]
    [InlineData("<svg a=\"1\"b=\"2\"></svg>")]
    [InlineData("text<svg></svg>")]
    [InlineData("<svg><!-- unterminated </svg>")]
    [InlineData("<svg><![CDATA[ x </svg>")]
    [InlineData("")]
    [InlineData("<html/>")]
    [InlineData("<!DOCTYPE svg [ <!ENTITY a \"b\"> <svg/>")]
    [InlineData("<svg/><!ELEMENT x ANY>")]
    public void Malformed_xml_throws_SvgParseException(string text)
    {
        var e = Assert.Throws<SvgParseException>(() => SvgDocument.Parse(text));
        Assert.False(string.IsNullOrEmpty(e.Message));
        Assert.False(SvgDocument.TryParse(text, out var doc, out var error));
        Assert.Null(doc);
        Assert.NotNull(error);
    }

    [Fact]
    public void Parse_exception_reports_line_and_column()
    {
        var e = Assert.Throws<SvgParseException>(() => SvgDocument.Parse("<svg>\n  <g>\n  </h>\n</svg>"));
        Assert.Equal(3, e.Line);
        Assert.Equal(3, e.Column);
    }

    [Fact]
    public void Foreign_namespaces_are_ignored_and_prefixed_svg_works()
    {
        var doc = SvgDocument.Parse("""
            <svg:svg xmlns:svg="http://www.w3.org/2000/svg" xmlns:ink="http://www.inkscape.org/namespaces/inkscape" viewBox="0 0 10 10">
              <ink:layer><svg:rect width="10" height="10" fill="red"/></ink:layer>
              <svg:rect width="10" height="5" fill="blue" ink:label="top"/>
            </svg:svg>
            """);
        AssertColor("#0000ff", Probe(doc, 5, 2));
        AssertColor("#00000000", Probe(doc, 5, 8));
    }

    [Fact]
    public void Byte_input_handles_bom_and_encodings()
    {
        var svg = $"<svg {Ns} viewBox=\"0 0 10 10\"><rect width=\"10\" height=\"10\" fill=\"#0f0\"/><!-- é --></svg>";
        var utf8 = new byte[] { 0xEF, 0xBB, 0xBF }.Concat(Encoding.UTF8.GetBytes(svg)).ToArray();
        AssertColor("#00ff00", Probe(SvgDocument.Parse(utf8), 5, 5));
        var utf16 = new byte[] { 0xFF, 0xFE }.Concat(Encoding.Unicode.GetBytes(svg)).ToArray();
        AssertColor("#00ff00", Probe(SvgDocument.Parse(utf16), 5, 5));
        var latin1Text = "<?xml version=\"1.0\" encoding=\"ISO-8859-1\"?>" + svg;
        var latin1 = latin1Text.Select(c => (byte)c).ToArray();
        AssertColor("#00ff00", Probe(SvgDocument.Parse(latin1), 5, 5));
        // 非法 UTF-8 序列不抛异常
        var broken = Encoding.UTF8.GetBytes(svg).Concat(new byte[] { 0xC3, 0x28, 0xFF }).ToArray();
        Assert.Throws<SvgParseException>(() => SvgDocument.Parse(broken)); // 根元素之后的文本
    }

    // ---------------------------------------------------------------- 尺寸

    [Theory]
    [InlineData("width=\"48\" height=\"32\"", 48, 32)]
    [InlineData("viewBox=\"0 0 24 24\"", 24, 24)]
    [InlineData("width=\"48\" viewBox=\"0 0 24 12\"", 48, 24)]
    [InlineData("height=\"48\" viewBox=\"0 0 24 12\"", 96, 48)]
    [InlineData("width=\"1in\" height=\"72pt\"", 96, 96)]
    [InlineData("width=\"100%\" height=\"100%\" viewBox=\"0 0 20 10\"", 20, 10)]
    [InlineData("", 300, 150)]
    public void Intrinsic_size(string attrs, float w, float h)
    {
        var doc = SvgDocument.Parse($"<svg {Ns} {attrs}/>");
        Assert.Equal(w, doc.IntrinsicSize.Width, 3);
        Assert.Equal(h, doc.IntrinsicSize.Height, 3);
        Assert.True(doc.IsEmpty);
    }

    [Fact]
    public void ViewBox_is_mapped_with_preserveAspectRatio()
    {
        // 20×10 的 viewBox 放进 20×20 的目标：meet → 居中，上下各留 5
        var doc = Doc("<rect width=\"20\" height=\"10\" fill=\"red\"/>", "viewBox=\"0 0 20 10\"");
        using (var bmp = SvgGolden.Render(doc, 20, 20, null, Color.Transparent))
        {
            Assert.Equal(0, SvgGolden.Pixel(bmp, 10, 2).A);
            AssertColor("#ff0000", SvgGolden.Pixel(bmp, 10, 10));
            Assert.Equal(0, SvgGolden.Pixel(bmp, 10, 17).A);
        }
        var opts = new SvgRenderOptions { PreserveAspectRatio = new SvgPreserveAspectRatio(SvgAlign.XMinYMin) };
        using (var bmp = SvgGolden.Render(doc, 20, 20, opts, Color.Transparent))
        {
            AssertColor("#ff0000", SvgGolden.Pixel(bmp, 10, 2));
            Assert.Equal(0, SvgGolden.Pixel(bmp, 10, 12).A);
        }
        opts = new SvgRenderOptions { PreserveAspectRatio = new SvgPreserveAspectRatio(SvgAlign.None) };
        using (var bmp = SvgGolden.Render(doc, 20, 20, opts, Color.Transparent))
        {
            AssertColor("#ff0000", SvgGolden.Pixel(bmp, 10, 2));
            AssertColor("#ff0000", SvgGolden.Pixel(bmp, 10, 18));
        }
    }

    [Fact]
    public void PreserveAspectRatio_transform_math()
    {
        var vb = new Rect(0, 0, 100, 50);
        var vp = new Rect(10, 10, 200, 200);
        var meet = new SvgPreserveAspectRatio(SvgAlign.XMaxYMax).GetTransform(vb, vp);
        Assert.Equal(2, meet.M11);
        Assert.Equal(10, meet.M31);
        Assert.Equal(110, meet.M32);
        var slice = new SvgPreserveAspectRatio(SvgAlign.XMidYMid, slice: true).GetTransform(vb, vp);
        Assert.Equal(4, slice.M11);
        Assert.Equal(-90, slice.M31);
        Assert.Equal(10, slice.M32);
    }

    [Fact]
    public void Content_is_clipped_to_the_destination_unless_disabled()
    {
        var doc = Doc("<rect x=\"-5\" y=\"-5\" width=\"20\" height=\"20\" fill=\"red\"/>");
        var list = doc.ToDisplayList(new Rect(10, 10, 10, 10));
        Assert.True(list.Bounds.Left >= 10 && list.Bounds.Right <= 20.01f, list.Bounds.ToString());
        var unclipped = doc.ToDisplayList(new Rect(10, 10, 10, 10), new SvgRenderOptions { ClipToViewport = false });
        Assert.True(unclipped.Bounds.Left < 10, unclipped.Bounds.ToString());
    }

    // ---------------------------------------------------------------- 样式

    [Fact]
    public void Cascade_order_presentation_sheet_style_important()
    {
        var doc = SvgDocument.Parse($$"""
            <svg {{Ns}} viewBox="0 0 40 10">
              <style>
                rect { fill: blue }
                .a { fill: green }
                #c { fill: yellow !important }
                g > .d { fill: purple }
                svg .e.f { fill: orange }
                rect.e { fill: black }
              </style>
              <rect x="0" width="5" height="10" fill="red"/>
              <rect x="5" width="5" height="10" fill="red" class="a"/>
              <rect x="10" width="5" height="10" class="a" style="fill: #00ffff"/>
              <rect x="15" width="5" height="10" id="c" style="fill: #00ffff"/>
              <g><rect x="20" width="5" height="10" class="d"/></g>
              <rect x="25" width="5" height="10" class="d"/>
              <rect x="30" width="5" height="10" class="e f"/>
              <rect x="35" width="5" height="10" fill="red" style="fill: #ff00ff !important; fill: white"/>
            </svg>
            """);
        Assert.Empty(doc.Diagnostics);
        using var bmp = SvgGolden.Render(doc, 40, 10, null, Color.Transparent);
        AssertColor("#0000ff", SvgGolden.Pixel(bmp, 2, 5));   // 样式表胜过表现属性
        AssertColor("#008000", SvgGolden.Pixel(bmp, 7, 5));   // 类选择器胜过类型选择器
        AssertColor("#00ffff", SvgGolden.Pixel(bmp, 12, 5));  // style 属性胜过样式表
        AssertColor("#ffff00", SvgGolden.Pixel(bmp, 17, 5));  // !important 胜过 style 属性
        AssertColor("#800080", SvgGolden.Pixel(bmp, 22, 5));  // 子代组合符
        AssertColor("#0000ff", SvgGolden.Pixel(bmp, 27, 5));  // 父元素不是 g
        AssertColor("#ffa500", SvgGolden.Pixel(bmp, 32, 5));  // 后代 + 复合（特异性 0,2,1 > 0,1,1）
        AssertColor("#ff00ff", SvgGolden.Pixel(bmp, 37, 5));  // style 中的 !important 胜过之后的普通声明
    }

    [Fact]
    public void Unsupported_css_is_reported_and_skipped()
    {
        var doc = Doc("""
            <style>
              @media (prefers-color-scheme: dark) { rect { fill: red } }
              rect:hover { fill: red }
              rect[x] { fill: red }
              rect + rect { fill: red }
              rect { fill: lime }
            </style>
            <rect width="10" height="10"/>
            """);
        Assert.True(HasDiagnostic(doc, SvgDiagnosticCode.UnsupportedCss));
        AssertColor("#00ff00", Probe(doc, 5, 5));
    }

    [Fact]
    public void Inheritance_and_inherit_keyword()
    {
        var doc = Doc("""
            <g fill="red" opacity="1" stroke="none">
              <g fill="blue"><rect width="5" height="10"/></g>
              <rect x="5" width="5" height="10" style="fill: inherit"/>
            </g>
            """);
        AssertColor("#0000ff", Probe(doc, 2, 5));
        AssertColor("#ff0000", Probe(doc, 7, 5));
    }

    [Fact]
    public void Invalid_values_are_ignored_with_a_diagnostic()
    {
        var doc = Doc("""<g fill="lime"><rect width="10" height="10" fill="notacolor" stroke-width="-1"/></g>""");
        Assert.True(HasDiagnostic(doc, SvgDiagnosticCode.InvalidValue));
        AssertColor("#00ff00", Probe(doc, 5, 5));
    }

    [Fact]
    public void CurrentColor_uses_the_caller_color_unless_overridden()
    {
        var doc = Doc("""
            <rect width="5" height="10" fill="currentColor"/>
            <g color="#00ff00"><rect x="5" width="5" height="10" fill="currentColor"/></g>
            """);
        Assert.True(doc.UsesCurrentColor);
        var opts = new SvgRenderOptions { CurrentColor = Color.Parse("#ff8800") };
        using var bmp = SvgGolden.Render(doc, 10, 10, opts, Color.Transparent);
        AssertColor("#ff8800", SvgGolden.Pixel(bmp, 2, 5));
        AssertColor("#00ff00", SvgGolden.Pixel(bmp, 7, 5));

        var fixedDoc = Doc("""<rect width="10" height="10" fill="red"/>""");
        Assert.False(fixedDoc.UsesCurrentColor);
    }

    [Fact]
    public void CurrentColor_is_resolved_where_used_not_where_declared()
    {
        // fill=currentColor 继承为关键字，在使用处按该元素的 color 解析
        var doc = Doc("""<g fill="currentColor" color="red"><rect width="10" height="10" color="#0000ff"/></g>""");
        AssertColor("#0000ff", Probe(doc, 5, 5));
    }

    [Fact]
    public void Opacity_properties_combine()
    {
        var doc = Doc("""
            <rect width="10" height="10" fill="#ff0000" fill-opacity="0.5" opacity="0.5"/>
            """);
        var px = Probe(doc, 5, 5);
        Assert.InRange(px.A, 62, 66);
        // 同时有填充与描边时，opacity 作为组透明度（重叠处不叠加）
        var both = Doc("""<rect x="2" y="2" width="6" height="6" fill="#ff0000" stroke="#ff0000" stroke-width="2" opacity="0.5"/>""");
        Assert.InRange(Probe(both, 5, 5).A, 126, 129);
        Assert.InRange(Probe(both, 2, 5).A, 126, 129);
    }

    [Fact]
    public void Display_none_and_visibility()
    {
        var doc = Doc("""
            <g display="none"><rect width="10" height="10" fill="red"/></g>
            <g visibility="hidden">
              <rect width="5" height="10" fill="red"/>
              <rect x="5" width="5" height="10" fill="lime" visibility="visible"/>
            </g>
            """);
        Assert.Equal(0, Probe(doc, 2, 5).A);
        AssertColor("#00ff00", Probe(doc, 7, 5));
    }

    // ---------------------------------------------------------------- 引用

    [Fact]
    public void Use_with_defs_symbol_and_xy()
    {
        var doc = SvgDocument.Parse($"""
            <svg {Ns} xmlns:xlink="http://www.w3.org/1999/xlink" viewBox="0 0 20 20">
              <defs><rect id="r" width="5" height="5" fill="red"/></defs>
              <symbol id="s" viewBox="0 0 1 1"><rect width="1" height="1" fill="blue"/></symbol>
              <use href="#r"/>
              <use xlink:href="#r" x="10" fill="lime"/>
              <use href="#s" x="0" y="10" width="10" height="10"/>
            </svg>
            """);
        Assert.Empty(doc.Diagnostics);
        using var bmp = SvgGolden.Render(doc, 20, 20, null, Color.Transparent);
        AssertColor("#ff0000", SvgGolden.Pixel(bmp, 2, 2));
        AssertColor("#ff0000", SvgGolden.Pixel(bmp, 12, 2));   // 元素自身的 fill 胜过从 use 继承的
        Assert.Equal(0, SvgGolden.Pixel(bmp, 7, 2).A);
        AssertColor("#0000ff", SvgGolden.Pixel(bmp, 5, 15));
        Assert.Equal(0, SvgGolden.Pixel(bmp, 15, 15).A);
    }

    [Fact]
    public void Use_content_inherits_from_the_use_element()
    {
        var doc = Doc("""
            <defs><rect id="r" width="10" height="10"/></defs>
            <use href="#r" fill="lime"/>
            """);
        AssertColor("#00ff00", Probe(doc, 5, 5));
    }

    [Theory]
    [InlineData("""<g id="a"><use href="#a"/></g>""")]
    [InlineData("""<use id="self" href="#self"/>""")]
    [InlineData("""<defs><g id="a"><use href="#b"/></g><g id="b"><use href="#a"/></g></defs><use href="#a"/>""")]
    public void Use_cycles_are_detected(string body)
    {
        var doc = Doc(body);
        Assert.True(HasDiagnostic(doc, SvgDiagnosticCode.CircularReference));
    }

    [Fact]
    public void Missing_references_are_reported()
    {
        var doc = Doc("""
            <use href="#nope"/>
            <rect width="5" height="10" fill="url(#nope)"/>
            <rect x="5" width="5" height="10" fill="url(#nope) lime" clip-path="url(#nope)"/>
            """);
        Assert.True(HasDiagnostic(doc, SvgDiagnosticCode.MissingReference));
        Assert.Equal(0, Probe(doc, 2, 5).A);
        AssertColor("#00ff00", Probe(doc, 7, 5));
    }

    [Fact]
    public void Linear_gradient_with_href_inheritance_and_units()
    {
        var doc = Doc("""
            <defs>
              <linearGradient id="base"><stop offset="0" stop-color="#ff0000"/><stop offset="1" stop-color="#0000ff"/></linearGradient>
              <linearGradient id="obb" href="#base"/>
              <linearGradient id="user" href="#base" gradientUnits="userSpaceOnUse" x1="0" x2="100"/>
            </defs>
            <rect width="100" height="10" fill="url(#obb)"/>
            <rect y="10" width="50" height="10" fill="url(#user)"/>
            """, "viewBox=\"0 0 100 20\"");
        Assert.Empty(doc.Diagnostics);
        using var bmp = SvgGolden.Render(doc, 100, 20, null, Color.Transparent);
        var left = SvgGolden.Pixel(bmp, 1, 5);
        var right = SvgGolden.Pixel(bmp, 98, 5);
        Assert.True(left.R > 240 && left.B < 15, left.ToString());
        Assert.True(right.B > 240 && right.R < 15, right.ToString());
        // userSpaceOnUse：50 宽的矩形只覆盖渐变的前一半，右端约为中间色
        var mid = SvgGolden.Pixel(bmp, 49, 15);
        Assert.InRange((int)mid.R, 115, 140);
        Assert.InRange((int)mid.B, 115, 140);
    }

    [Fact]
    public void Gradient_stop_opacity_spread_and_transform()
    {
        var doc = Doc("""
            <defs>
              <linearGradient id="g" x2="0.5" spreadMethod="reflect" gradientTransform="translate(0 0)">
                <stop offset="0%" stop-color="black" stop-opacity="0"/>
                <stop offset="100%" style="stop-color: black; stop-opacity: 1"/>
              </linearGradient>
            </defs>
            <rect width="100" height="10" fill="url(#g)"/>
            """, "viewBox=\"0 0 100 10\"");
        using var bmp = SvgGolden.Render(doc, 100, 10, null, Color.Transparent);
        Assert.InRange((int)SvgGolden.Pixel(bmp, 1, 5).A, 0, 12);
        Assert.InRange((int)SvgGolden.Pixel(bmp, 49, 5).A, 243, 255);
        Assert.InRange((int)SvgGolden.Pixel(bmp, 98, 5).A, 0, 12);   // reflect：回到透明
    }

    [Fact]
    public void Gradient_edge_cases()
    {
        var doc = Doc("""
            <defs>
              <linearGradient id="none"/>
              <linearGradient id="one"><stop stop-color="lime"/></linearGradient>
              <radialGradient id="zero" r="0"><stop stop-color="red"/><stop offset="1" stop-color="blue"/></radialGradient>
              <linearGradient id="loop1" href="#loop2"/>
              <linearGradient id="loop2" href="#loop1"><stop stop-color="red"/></linearGradient>
            </defs>
            <rect width="2" height="10" fill="url(#none) red"/>
            <rect x="2" width="2" height="10" fill="url(#one)"/>
            <rect x="4" width="2" height="10" fill="url(#zero)"/>
            <rect x="6" width="2" height="10" fill="url(#loop1)"/>
            <line x1="8" y1="5" x2="10" y2="5" stroke="url(#one)" stroke-width="10"/>
            """);
        using var bmp = SvgGolden.Render(doc, 10, 10, null, Color.Transparent);
        Assert.Equal(0, SvgGolden.Pixel(bmp, 1, 5).A);              // 没有色标：none（不用回退色）
        AssertColor("#00ff00", SvgGolden.Pixel(bmp, 3, 5));          // 单个色标：纯色
        AssertColor("#0000ff", SvgGolden.Pixel(bmp, 5, 5));          // r=0：最后一个色标
        Assert.True(HasDiagnostic(doc, SvgDiagnosticCode.CircularReference));
        Assert.Equal(0, SvgGolden.Pixel(bmp, 9, 5).A);              // 水平线的 bbox 高为 0：objectBoundingBox 渐变不绘制
    }

    [Fact]
    public void Radial_focal_point_uses_the_focal_brush()
    {
        // 第七批：fx / fy / fr 映射到 RadialGradientBrush 的焦点（双点锥形渐变），不再回退到圆心
        var doc = Doc("""
            <defs><radialGradient id="g" fx="0.2" fr="0.1"><stop stop-color="red"/><stop offset="1" stop-color="blue"/></radialGradient></defs>
            <circle cx="5" cy="5" r="5" fill="url(#g)"/>
            """);
        Assert.DoesNotContain(doc.Diagnostics, d => d.Message.Contains("focal"));
        var brush = Brushes(doc).OfType<RadialGradientBrush>().Single();
        Assert.True(brush.HasFocus);
        Assert.Equal(0.2f, brush.FocalPoint.X, 4);
        Assert.Equal(0.5f, brush.FocalPoint.Y, 4);
        Assert.Equal(0.1f, brush.FocalRadius, 4);
        // 焦点一侧更接近起始色：左侧（焦点附近）偏红，右侧偏蓝
        using var bmp = SvgGolden.Render(doc, 100, 100, null, Color.Transparent);
        var left = SvgGolden.Pixel(bmp, 22, 50);
        var right = SvgGolden.Pixel(bmp, 80, 50);
        Assert.True(left.R > left.B && right.B > right.R, $"left {left}, right {right}");
    }

    [Fact]
    public void Radial_gradient_without_focus_keeps_the_plain_brush()
    {
        var doc = Doc("""
            <defs><radialGradient id="g" fx="50%" fy="0.5"><stop stop-color="red"/><stop offset="1" stop-color="blue"/></radialGradient></defs>
            <circle cx="5" cy="5" r="5" fill="url(#g)"/>
            """);
        Assert.False(Brushes(doc).OfType<RadialGradientBrush>().Single().HasFocus);
    }

    [Fact]
    public void Focal_point_outside_the_circle_follows_svg2_unless_svg11_is_requested()
    {
        const string text = """
            <defs><radialGradient id="g" gradientUnits="userSpaceOnUse" cx="5" cy="5" r="4" fx="12" fy="5"><stop stop-color="red"/><stop offset="1" stop-color="blue"/></radialGradient></defs>
            <rect width="10" height="10" fill="url(#g)"/>
            """;
        var svg2 = Brushes(Doc(text)).OfType<RadialGradientBrush>().Single();
        Assert.Equal(12f, svg2.FocalPoint.X);
        var svg11 = Brushes(SvgDocument.Parse($"<svg {Ns} viewBox=\"0 0 10 10\">{text}</svg>", new SvgParseOptions { Svg11FocalPoint = true }))
            .OfType<RadialGradientBrush>().Single();
        Assert.InRange(svg11.FocalPoint.X, 8.9f, 9f);       // 移到结束圆上（略向内）
        Assert.Equal(5f, svg11.FocalPoint.Y, 4);
    }

    [Fact]
    public void Negative_focal_radius_is_reported_and_treated_as_zero()
    {
        var doc = Doc("""
            <defs><radialGradient id="g" fx="0.3" fr="-2"><stop stop-color="red"/><stop offset="1" stop-color="blue"/></radialGradient></defs>
            <circle cx="5" cy="5" r="5" fill="url(#g)"/>
            """);
        Assert.Contains(doc.Diagnostics, d => d.Code == SvgDiagnosticCode.InvalidValue && d.Message.Contains("fr"));
        Assert.Equal(0f, Brushes(doc).OfType<RadialGradientBrush>().Single().FocalRadius);
    }

    /// <summary>DisplayList 资源表中的画刷。</summary>
    static IEnumerable<Brush> Brushes(SvgDocument doc) => doc.ToDisplayList(new Rect(0, 0, 10, 10)).Resources.OfType<Brush>();

    [Fact]
    public void ClipPath_user_space_and_object_bounding_box()
    {
        var doc = Doc("""
            <defs>
              <clipPath id="left"><rect width="5" height="10"/></clipPath>
              <clipPath id="topHalf" clipPathUnits="objectBoundingBox"><rect width="1" height="0.5"/></clipPath>
              <clipPath id="empty"/>
            </defs>
            <rect width="10" height="10" fill="red" clip-path="url(#left)"/>
            <rect x="5" y="0" width="5" height="10" fill="lime" style="clip-path: url(#topHalf)"/>
            <rect width="10" height="10" fill="blue" clip-path="url(#empty)"/>
            """);
        Assert.Empty(doc.Diagnostics);
        using var bmp = SvgGolden.Render(doc, 10, 10, null, Color.Transparent);
        AssertColor("#ff0000", SvgGolden.Pixel(bmp, 2, 5));
        AssertColor("#00ff00", SvgGolden.Pixel(bmp, 7, 2));
        Assert.Equal(0, SvgGolden.Pixel(bmp, 7, 7).A);
    }

    [Fact]
    public void ClipPath_with_transform_union_and_clip_rule()
    {
        var doc = Doc("""
            <defs>
              <clipPath id="two"><rect width="3" height="10"/><rect x="7" width="3" height="10" transform="translate(0 0)"/></clipPath>
              <clipPath id="ring"><path clip-rule="evenodd" d="M0 0H10V10H0Z M3 3H7V7H3Z"/></clipPath>
            </defs>
            <rect width="10" height="5" fill="red" clip-path="url(#two)"/>
            <rect y="5" width="10" height="5" fill="blue" clip-path="url(#ring)"/>
            """);
        using var bmp = SvgGolden.Render(doc, 10, 10, null, Color.Transparent);
        AssertColor("#ff0000", SvgGolden.Pixel(bmp, 1, 2));
        Assert.Equal(0, SvgGolden.Pixel(bmp, 5, 2).A);
        AssertColor("#ff0000", SvgGolden.Pixel(bmp, 8, 2));
        AssertColor("#0000ff", SvgGolden.Pixel(bmp, 1, 8));
        Assert.Equal(0, SvgGolden.Pixel(bmp, 5, 6).A);     // evenodd 洞
    }

    [Fact]
    public void Nested_svg_establishes_a_clipped_viewport()
    {
        var doc = Doc("""
            <svg x="5" y="5" width="5" height="5" viewBox="0 0 1 1">
              <rect width="2" height="2" fill="red"/>
            </svg>
            """);
        using var bmp = SvgGolden.Render(doc, 10, 10, null, Color.Transparent);
        AssertColor("#ff0000", SvgGolden.Pixel(bmp, 7, 7));
        Assert.Equal(0, SvgGolden.Pixel(bmp, 2, 2).A);
        Assert.Equal(0, SvgGolden.Pixel(bmp, 7, 2).A);
    }

    [Fact]
    public void Unsupported_elements_are_listed()
    {
        var doc = Doc("""
            <text x="0" y="10">hello</text>
            <filter id="f"><feGaussianBlur stdDeviation="2"/></filter>
            <rect width="10" height="10" filter="url(#f)" mask="url(#m)"/>
            <image href="a.png" width="10" height="10"/>
            <frobnicate/>
            """);
        Assert.Contains(doc.Diagnostics, d => d.Code == SvgDiagnosticCode.UnsupportedElement && d.Message.Contains("<text>"));
        // 第七批起 <image> 受支持，但需要 SvgParseOptions.ImageResolver；没有时按不支持的特性报告
        Assert.Contains(doc.Diagnostics, d => d.Code == SvgDiagnosticCode.UnsupportedFeature && d.Message.Contains("<image>"));
        Assert.Contains(doc.Diagnostics, d => d.Code == SvgDiagnosticCode.UnsupportedElement && d.Message.Contains("frobnicate"));
        Assert.Contains(doc.Diagnostics, d => d.Code == SvgDiagnosticCode.UnsupportedFeature && d.Message.Contains("Filters"));
        Assert.Contains(doc.Diagnostics, d => d.Code == SvgDiagnosticCode.UnsupportedFeature && d.Message.Contains("Masks"));
        Assert.All(doc.Diagnostics, d => Assert.True(d.Line > 0));
        // 不支持的效果被忽略，元素本身照常绘制
        AssertColor("#000000", Probe(doc, 5, 5));
    }

    [Fact]
    public void Shapes_geometry_and_degenerate_cases()
    {
        var doc = Doc("""
            <rect width="10" height="10" rx="20" fill="red"/>
            <rect width="-1" height="10"/>
            <circle r="0"/>
            <ellipse cx="5" cy="5" rx="2" fill="lime"/>
            <polyline points="0,0 1"/>
            <polygon points=""/>
            """);
        Assert.Contains(doc.Diagnostics, d => d.Code == SvgDiagnosticCode.InvalidValue);
        // rx 被限制到宽的一半：圆角矩形退化为圆，角上是空的
        using var bmp = SvgGolden.Render(doc, 10, 10, null, Color.Transparent);
        Assert.Equal(0, SvgGolden.Pixel(bmp, 0, 0).A);
        AssertColor("#ff0000", SvgGolden.Pixel(bmp, 5, 1));
        AssertColor("#00ff00", SvgGolden.Pixel(bmp, 5, 5));   // ry 缺省时等于 rx（SVG 2 的 auto）
    }

    [Fact]
    public void Display_list_is_reusable_and_draw_is_repeatable()
    {
        var doc = Doc("""<circle cx="5" cy="5" r="4" fill="currentColor"/>""");
        var a = doc.ToDisplayList(new Rect(0, 0, 10, 10), new SvgRenderOptions { CurrentColor = Color.Black });
        var b = doc.ToDisplayList(new Rect(0, 0, 10, 10), new SvgRenderOptions { CurrentColor = Color.Black });
        Assert.Equal(a.CommandBytes.ToArray(), b.CommandBytes.ToArray());
        var dc = new DrawingContext();
        doc.Draw(dc, new Rect(0, 0, 10, 10));
        doc.Draw(dc, new Rect(10, 0, 10, 10), new SvgRenderOptions { Opacity = 0.5f });
        doc.Draw(dc, new Rect(float.NaN, 0, 10, 10));
        doc.Draw(dc, Rect.Empty);
        Assert.Equal(0, dc.Depth);
        var list = dc.Finish();
        Assert.False(list.IsEmpty);
    }
}
