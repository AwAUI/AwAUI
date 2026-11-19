using AwAUI.Markup.Compiler;
using AwAUI.Markup.Compiler.Analysis;

namespace AwAUI.Markup.UnitTests;

public class ApiTests
{
    [Fact]
    public void Source_text_maps_offsets_to_lines_for_all_newline_kinds()
    {
        var s = new SourceText("ab\ncd\r\nef\rgh");
        Assert.Equal(4, s.LineCount);
        Assert.Equal(new LinePosition(1, 1), s.GetLinePosition(0));
        Assert.Equal(new LinePosition(1, 3), s.GetLinePosition(2));
        Assert.Equal(new LinePosition(2, 1), s.GetLinePosition(3));
        Assert.Equal(new LinePosition(3, 1), s.GetLinePosition(7));
        Assert.Equal(new LinePosition(4, 2), s.GetLinePosition(11));
        Assert.Equal(new LinePosition(4, 3), s.GetLinePosition(999)); // 钳制
        Assert.Equal(7, s.GetLineStart(3));
    }

    [Fact]
    public void Text_span_basics()
    {
        var a = TextSpan.FromBounds(2, 5);
        Assert.Equal(3, a.Length);
        Assert.True(a.Contains(new TextSpan(3, 2)));
        Assert.False(a.Contains(5));
        Assert.Throws<ArgumentOutOfRangeException>(() => new TextSpan(-1, 0));
    }

    [Theory]
    [InlineData("x", true)]
    [InlineData("  this.model.Items[i + 1].Name ", true)]
    [InlineData("(a.b)", true)]
    [InlineData("a!.b", true)]
    [InlineData("@class", true)]
    [InlineData("F()", false)]
    [InlineData("a.F()", false)]
    [InlineData("1", false)]
    [InlineData("\"s\"", false)]
    [InlineData("a + b", false)]
    [InlineData("a?.b", false)]
    [InlineData("this", false)]
    [InlineData("null", false)]
    [InlineData("", false)]
    public void Assignable_target_heuristic(string code, bool expected) =>
        Assert.Equal(expected, MarkupValidator.IsAssignable(code));

    [Fact]
    public void Static_catalog_resolves_through_usings_and_namespace()
    {
        var cat = new StaticComponentCatalog(new[]
        {
            new ComponentInfo("Lib.Controls.Button", new[] { new PropInfo("Text", PropKind.String) }),
            new ComponentInfo("App.Pages.Row", Array.Empty<PropInfo>()),
        });
        Assert.NotNull(cat.Resolve("Button", new ImportScope("App", new[] { "Lib.Controls" })));
        Assert.Null(cat.Resolve("Button", new ImportScope("App", Array.Empty<string>())));
        Assert.NotNull(cat.Resolve("Row", new ImportScope("App.Pages", Array.Empty<string>())));
        Assert.NotNull(cat.Resolve("Lib.Controls.Button", new ImportScope(null, Array.Empty<string>())));
        Assert.Equal(PropKind.String, cat.Resolve("Button", new ImportScope(null, new[] { "Lib.Controls" }))!.FindProp("Text")!.Kind);
    }

    [Fact]
    public void Intrinsic_table_lookups()
    {
        Assert.Equal(new[] { "div", "span", "text", "img", "input", "textarea", "canvas", "svg", "path" }, IntrinsicElements.Names);
        Assert.True(IntrinsicElements.TryGetAttribute("input", "maxlength", out var info));
        Assert.Equal(AttributeValueType.Integer, info!.Type);
        Assert.True(IntrinsicElements.TryGetAttribute("div", "aria-describedby", out _));
        Assert.False(IntrinsicElements.TryGetAttribute("div", "aria-", out _));
        Assert.False(IntrinsicElements.TryGetAttribute("div", "src", out _));
        Assert.True(IntrinsicElements.IsKnownEvent("pointerdown"));
    }

    [Fact]
    public void Analysis_reports_has_errors_only_for_errors()
    {
        Assert.False(MarkupCompiler.Analyze("@foreach (var x in xs) { <div/> }").HasErrors); // 只有 AW4008 警告
        Assert.True(MarkupCompiler.Analyze("<div>").HasErrors);
    }
}
