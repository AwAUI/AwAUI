using System.Runtime.CompilerServices;
using AwAUI.Css.Values;
using AwAUI.Tools.CssPropertyGen;

namespace AwAUI.Css.UnitTests;

/// <summary>属性清单（Properties.json）与生成代码的一致性。</summary>
public class PropertyManifestTests
{
    static string RepoRoot([CallerFilePath] string path = "")
    {
        foreach (var start in new[] { AppContext.BaseDirectory, Path.GetDirectoryName(path)! })
        {
            var dir = new DirectoryInfo(start);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "global.json"))) dir = dir.Parent;
            if (dir != null) return dir.FullName;
        }
        throw new InvalidOperationException("找不到仓库根目录");
    }

    [Fact]
    public void Generated_files_are_up_to_date()
    {
        var root = RepoRoot();
        var css = Path.Combine(root, "src", "AwAUI.Css");
        var files = PropertyCodeGenerator.Generate(File.ReadAllText(Path.Combine(css, "Properties.json")));
        Assert.NotEmpty(files);
        foreach (var kv in files)
        {
            var path = Path.Combine(css, "Generated", kv.Key);
            Assert.True(File.Exists(path), $"缺少生成文件 {path}；请运行 dotnet run --project src/tools/CssPropertyGen");
            var actual = File.ReadAllText(path).Replace("\r\n", "\n");
            Assert.True(actual == kv.Value, $"{kv.Key} 已过期；请运行 dotnet run --project src/tools/CssPropertyGen");
        }
        // 没有残留的旧生成文件
        foreach (var f in Directory.GetFiles(Path.Combine(css, "Generated"), "*.g.cs"))
            Assert.True(files.ContainsKey(Path.GetFileName(f)), $"多余的生成文件 {f}");
    }

    [Fact]
    public void Generator_is_deterministic()
    {
        var json = File.ReadAllText(Path.Combine(RepoRoot(), "src", "AwAUI.Css", "Properties.json"));
        var a = PropertyCodeGenerator.Generate(json);
        var b = PropertyCodeGenerator.Generate(json);
        Assert.Equal(a, b);
    }

    [Fact]
    public void Generator_rejects_inconsistent_manifest()
    {
        const string bad = """
            { "groups": [ { "name": "Box", "inherited": false, "doc": "x" } ], "enums": [],
              "longhands": [ { "name": "color", "type": "Color", "initial": "black", "inherited": true, "animatable": "no", "affects": "paint", "group": "Box", "phase": 1 } ] }
            """;
        Assert.Throws<FormatException>(() => PropertyCodeGenerator.Generate(bad));
        Assert.Throws<FormatException>(() => PropertyCodeGenerator.Generate("{ \"groups\": [ }"));
    }

    [Fact]
    public void Every_phase1_property_from_design16_is_present()
    {
        string[] expected =
        {
            "display", "box-sizing", "width", "height", "min-width", "min-height", "max-width", "max-height",
            "margin-top", "margin-right", "margin-bottom", "margin-left", "padding-top", "padding-right", "padding-bottom", "padding-left",
            "border-top-width", "border-right-width", "border-bottom-width", "border-left-width", "overflow-x", "overflow-y", "visibility",
            "position", "top", "right", "bottom", "left", "z-index",
            "flex-direction", "flex-wrap", "flex-grow", "flex-shrink", "flex-basis", "justify-content", "align-items", "align-self", "align-content",
            "row-gap", "column-gap",
            "background-color", "background-image", "border-top-color", "border-top-style", "border-top-left-radius", "box-shadow",
            "opacity", "color", "font-family", "font-size", "font-weight", "font-style", "font-stretch", "line-height", "text-align",
            "white-space", "word-break", "overflow-wrap", "text-overflow", "cursor", "pointer-events",
            "object-fit", "object-position", "image-rendering", "aspect-ratio",
            "margin", "padding", "inset", "border", "border-top", "border-width", "border-style", "border-color", "border-radius",
            "flex", "flex-flow", "gap", "overflow", "background", "font", "all",
        };
        foreach (var name in expected) Assert.True(CssProperties.TryGet(name, out _), name);
    }

    [Fact]
    public void Metadata_matches_manifest()
    {
        Assert.True(CssProperties.TryGet("COLOR", out var color));
        Assert.True(color.Inherited);
        Assert.Equal(StyleDifference.Paint, color.Affects);
        Assert.Equal(CssPropertyGroup.Text, color.Group);
        Assert.Equal("black", color.InitialText);
        Assert.False(color.IsShorthand);

        var opacity = CssProperties.Get(CssPropertyId.Opacity);
        Assert.Equal(StyleDifference.Composite, opacity.Affects);
        Assert.Equal(CssAnimatable.ByComputedValue, opacity.Animatable);
        Assert.Equal(StyleDifference.Behavior, CssProperties.Get(CssPropertyId.Cursor).Affects);
        Assert.Equal(StyleDifference.Structure, CssProperties.Get(CssPropertyId.Display).Affects);

        var margin = CssProperties.Get(CssPropertyId.Margin);
        Assert.True(margin.IsShorthand);
        Assert.Equal(new[] { CssPropertyId.MarginTop, CssPropertyId.MarginRight, CssPropertyId.MarginBottom, CssPropertyId.MarginLeft }, margin.Longhands);
        Assert.Equal(CssProperties.LonghandCount, CssProperties.Get(CssPropertyId.All).Longhands.Count);
        Assert.Equal(CssProperties.Count, CssProperties.All.Count);
        for (int i = 0; i < CssProperties.All.Count; i++) Assert.Equal((CssPropertyId)i, CssProperties.All[i].Id);
    }

    [Fact]
    public void Every_initial_value_parses_and_round_trips()
    {
        for (int i = 0; i < CssProperties.LonghandCount; i++)
        {
            var p = CssProperties.All[i];
            Assert.False(string.IsNullOrEmpty(ComputedStyle.Initial.GetPropertyValueText(p.Id)), p.Name);
        }
    }

    [Theory]
    [InlineData("colr", "color")]
    [InlineData("widht", "width")]
    [InlineData("backgroud-color", "background-color")]
    [InlineData("justify-contnet", "justify-content")]
    public void Suggests_close_names(string typo, string expected) => Assert.Equal(expected, CssProperties.SuggestName(typo));

    [Fact]
    public void No_suggestion_for_unrelated_or_huge_names()
    {
        Assert.Null(CssProperties.SuggestName("zzzzzzzzzzzz"));
        Assert.Null(CssProperties.SuggestName(new string('a', 100000)));
    }

    [Fact]
    public void Planned_and_unsupported_lookup()
    {
        Assert.True(CssProperties.TryGetPlannedPhase("transform", out var phase));
        Assert.Equal(2, phase);
        Assert.True(CssProperties.TryGetUnsupportedHint("float", out var hint));
        Assert.Contains("flex", hint);
        Assert.False(CssProperties.TryGetPlannedPhase("color", out _));
    }
}
