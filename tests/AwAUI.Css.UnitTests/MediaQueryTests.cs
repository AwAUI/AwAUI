using AwAUI.Css.Media;

namespace AwAUI.Css.UnitTests;

public class MediaQueryTests
{
    static bool Eval(string query, MediaEnvironment env, List<CssDiagnostic>? d = null) => MediaQueryList.Parse(query, d).Matches(env);

    static MediaEnvironment Env(double w = 1024, double h = 768) => new() { Width = w, Height = h };

    [Theory]
    [InlineData("screen and (min-width: 600px)", 800, true)]
    [InlineData("screen and (min-width: 600px)", 500, false)]
    [InlineData("(max-width: 600px)", 600, true)]
    [InlineData("(width > 600px)", 600, false)]
    [InlineData("(width >= 600px)", 600, true)]
    [InlineData("(600px < width)", 601, true)]
    [InlineData("(400px <= width <= 700px)", 700, true)]
    [InlineData("(400px <= width <= 700px)", 701, false)]
    [InlineData("(700px > width > 400px)", 500, true)]
    [InlineData("(width = 500px)", 500, true)]
    [InlineData("(width: 500px)", 500, true)]
    [InlineData("(min-width: 40em)", 640, true)]
    [InlineData("(min-width: 40em)", 639, false)]
    [InlineData("(min-width: 30rem)", 480, true)]
    [InlineData("(width > 0)", 1, true)]
    [InlineData("(width)", 1, true)]
    [InlineData("(width < 6.25in)", 599, true)]
    [InlineData("print", 800, false)]
    [InlineData("all", 800, true)]
    [InlineData("SCREEN", 800, true)]
    [InlineData("only screen and (min-width: 1px)", 800, true)]
    [InlineData("not screen", 800, false)]
    [InlineData("not print", 800, true)]
    [InlineData("not screen and (min-width: 900px)", 800, true)]
    [InlineData("not (width > 1000px)", 800, true)]
    [InlineData("(width > 1000px) or (height > 500px)", 800, true)]
    [InlineData("(width > 1000px) and (height > 500px)", 800, false)]
    [InlineData("((width > 1000px) or (width < 900px)) and (height > 1px)", 800, true)]
    [InlineData("print, (min-width: 700px)", 800, true)]
    [InlineData("", 800, true)]
    [InlineData("tv", 800, false)]
    public void Width_and_type_queries(string query, double width, bool expected)
        => Assert.Equal(expected, Eval(query, Env(width, 600)));

    [Fact]
    public void Discrete_features()
    {
        var env = new MediaEnvironment
        {
            Width = 400, Height = 800, ColorScheme = MediaColorScheme.Dark, PrefersReducedMotion = true,
            Hover = MediaHoverCapability.None, Pointer = MediaPointerCapability.Coarse,
            AnyHover = MediaHoverCapability.Hover, AnyPointer = MediaPointerCapability.Fine,
        };
        Assert.True(Eval("(orientation: portrait)", env));
        Assert.False(Eval("(orientation: landscape)", env));
        Assert.True(Eval("(prefers-color-scheme: dark)", env));
        Assert.True(Eval("(prefers-reduced-motion)", env));
        Assert.True(Eval("(prefers-reduced-motion: reduce)", env));
        Assert.False(Eval("(hover)", env));
        Assert.True(Eval("(hover: none) and (pointer: coarse)", env));
        Assert.True(Eval("(any-hover: hover) and (any-pointer: fine)", env));
        Assert.False(Eval("(pointer: fine)", env));
        Assert.True(Eval("(pointer)", env));
        Assert.False(Eval("(prefers-reduced-motion)", new MediaEnvironment()));
        Assert.True(Eval("(prefers-color-scheme: light)", new MediaEnvironment()));
    }

    [Fact]
    public void Resolution_and_aspect_ratio()
    {
        var env = new MediaEnvironment { Width = 1600, Height = 900, Resolution = 2 };
        Assert.True(Eval("(resolution >= 2dppx)", env));
        Assert.True(Eval("(min-resolution: 192dpi)", env));
        Assert.False(Eval("(min-resolution: 193dpi)", env));
        Assert.True(Eval("(resolution: 2x)", env));
        Assert.True(Eval("(max-resolution: infinite)", env));
        Assert.True(Eval("(aspect-ratio: 16/9)", env));
        Assert.True(Eval("(aspect-ratio: 16 / 9)", env));
        Assert.True(Eval("(min-aspect-ratio: 1)", env));
        Assert.False(Eval("(max-aspect-ratio: 4/3)", env));
    }

    [Theory]
    [InlineData("screen and")]
    [InlineData("and screen")]
    [InlineData("screen (width > 1px)")]
    [InlineData("(width > 1px) and (height > 1px) or (width > 2px)")]
    [InlineData("screen and (width > 1px) or (height > 1px)")]
    [InlineData("not not screen")]
    [InlineData("only (width > 1px)")]
    [InlineData("screen,")]
    [InlineData("12px")]
    [InlineData("layer")]
    public void Invalid_queries_become_not_all(string query)
    {
        var d = new List<CssDiagnostic>();
        var list = MediaQueryList.Parse(query, d);
        Assert.Contains(list.Queries, q => q.IsInvalid);
        Assert.Contains(d, x => x.Code == CssDiagnosticCodes.InvalidMediaQuery && x.Severity == CssDiagnosticSeverity.Error);
        Assert.False(list.Matches(new MediaEnvironment { MediaType = "screen" }) && list.Queries.Count == 1);
    }

    [Fact]
    public void Invalid_query_in_list_does_not_poison_others()
    {
        var list = MediaQueryList.Parse("screen and, print, screen");
        Assert.True(list.Queries[0].IsInvalid);
        Assert.True(list.Matches(new MediaEnvironment()));
        Assert.Equal("not all, print, screen", list.ToString());
    }

    [Theory]
    [InlineData("(color)")]
    [InlineData("(min-width)")]
    [InlineData("(min-width > 1px)")]
    [InlineData("(width: red)")]
    [InlineData("(width: 10)")]
    [InlineData("(orientation > portrait)")]
    [InlineData("(orientation: sideways)")]
    [InlineData("(width > 1px > 2px)")]
    [InlineData("(1px < width > 2px)")]
    [InlineData("(foo: bar)")]
    [InlineData("foo(bar)")]
    [InlineData("(resolution: 2px)")]
    public void Unknown_features_evaluate_to_unknown_with_warning(string query)
    {
        var d = new List<CssDiagnostic>();
        var list = MediaQueryList.Parse(query, d);
        Assert.False(list.Queries[0].IsInvalid);
        Assert.Equal(MediaConditionKind.GeneralEnclosed, list.Queries[0].Condition!.Kind);
        Assert.False(list.Matches(new MediaEnvironment()));
        Assert.Contains(d, x => x.Code == CssDiagnosticCodes.InvalidMediaQuery && x.Severity == CssDiagnosticSeverity.Warning);
    }

    [Fact]
    public void Unknown_is_not_negated_into_true()
    {
        Assert.False(Eval("not (color)", new MediaEnvironment()));
        Assert.False(Eval("not screen and (color)", new MediaEnvironment()));
        Assert.True(Eval("(color) or (width > 1px)", new MediaEnvironment()));
    }

    [Fact]
    public void Range_forms_normalize_to_constraints()
    {
        var f = Assert.IsType<MediaFeature>(MediaQueryList.Parse("(400px <= width < 700px)").Queries[0].Condition);
        Assert.Equal("width", f.Name);
        Assert.Equal(2, f.Constraints.Count);
        Assert.Equal(MediaComparison.GreaterOrEqual, f.Constraints[0].Comparison);
        Assert.Equal(400, f.Constraints[0].Value.Number);
        Assert.Equal(MediaComparison.Less, f.Constraints[1].Comparison);
        var m = Assert.IsType<MediaFeature>(MediaQueryList.Parse("(MAX-WIDTH: 10PX)").Queries[0].Condition);
        Assert.Equal("width", m.Name);
        Assert.Equal(MediaComparison.LessOrEqual, m.Constraints[0].Comparison);
        Assert.Equal("px", m.Constraints[0].Value.Unit);
    }

    [Fact]
    public void Less_equal_requires_adjacent_tokens()
    {
        // '< =' 中间有空白不是 <=
        var d = new List<CssDiagnostic>();
        var list = MediaQueryList.Parse("(width < = 10px)", d);
        Assert.Equal(MediaConditionKind.GeneralEnclosed, list.Queries[0].Condition!.Kind);
    }

    [Fact]
    public void Unknown_media_type_warns()
    {
        var d = new List<CssDiagnostic>();
        Assert.False(Eval("hologram", new MediaEnvironment(), d));
        Assert.Contains(d, x => x.Severity == CssDiagnosticSeverity.Warning);
    }
}
