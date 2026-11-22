using System;
using System.Collections.Generic;
using System.Linq;
using AwAUI.Controls.UnitTests.Integration;
using AwAUI.Svg;
using AwAUI.UI;

namespace AwAUI.Shadcn.Integration.S2;

/// <summary>
/// S2 Chart: each kind renders real SVG geometry at a deterministic 520×240 plot, the legend and axis are
/// visible text, colors follow the configured overrides, dark mode keeps rendering, and hovering a data
/// column shows the tooltip.
/// </summary>
public class ChartTests
{
    static ChartTests() => ShadcnTheme.Register();

    static UIHarness Harness(Func<Node> render) =>
        new(render, appStyleSheets: true, width: 520, height: 480);

    static string? A(Element e, string name) => e.GetAttribute(name);

    static List<Element> Slots(UIHarness h, string slot) =>
        h.FindAll(e => A(e, "data-slot") == slot && e.IsVisible);

    static Element Slot(UIHarness h, string slot) =>
        Slots(h, slot).First();

    static List<SvgElement> SvgLayers(UIHarness h) =>
        h.FindAll(e => e is SvgElement && e.IsVisible).Cast<SvgElement>().ToList();

    static Chart Sample(ChartKind kind) => new()
    {
        Kind = kind,
        Height = 240,
        Labels = ["Jan", "Feb", "Mar", "Apr"],
        Series =
        [
            new ChartSeries("Desktop", new[] { 120.0, 90.0, 150.0, 80.0 }),
            new ChartSeries("Mobile", new[] { 80.0, 110.0, 70.0, 140.0 }),
        ],
    };

    [Theory]
    [InlineData(ChartKind.Line)]
    [InlineData(ChartKind.Bar)]
    [InlineData(ChartKind.Area)]
    [InlineData(ChartKind.Pie)]
    public void Chart_each_kind_renders_visible_non_empty_svg(ChartKind kind)
    {
        var h = Harness(() => Sample(kind));

        var layers = SvgLayers(h);
        Assert.NotEmpty(layers);
        foreach (var layer in layers)
        {
            Assert.NotNull(layer.Document);
            Assert.False(layer.Document!.IsEmpty);
            var box = layer.GetBoundingClientRect();
            Assert.True(box.Width > 0 && box.Height > 0, $"{kind} svg layer has an empty box");
        }

        var plot = Slot(h, "chart-plot");
        Assert.Equal(Role.Image, plot.Role);
        Assert.Contains("height:240px", plot.InlineStyle ?? "");
        var box2 = plot.GetBoundingClientRect();
        Assert.True(box2.Width >= 519 && box2.Width <= 521, $"plot width was {box2.Width}");
        Assert.True(box2.Height >= 239 && box2.Height <= 241, $"plot height was {box2.Height}");
    }

    [Fact]
    public void Chart_legend_entries_match_series_names_and_axis_labels_are_text()
    {
        var h = Harness(() => Sample(ChartKind.Line));

        var legend = Slot(h, "chart-legend");
        var items = Slots(h, "chart-legend-item");
        Assert.Equal(2, items.Count);
        Assert.Equal(new[] { "Desktop", "Mobile" }, items.Select(e => e.TextContent.Trim()).ToArray());

        var axis = Slot(h, "chart-axis");
        Assert.Contains("Jan", axis.TextContent);
        Assert.Contains("Apr", axis.TextContent);

        var plot = Slot(h, "chart-plot");
        Assert.Contains("Desktop", A(plot, "aria-label") ?? "");
        Assert.Contains("Mobile", A(plot, "aria-label") ?? "");
    }

    [Fact]
    public void Chart_pie_legend_lists_every_category_with_its_config_label()
    {
        var config = new Dictionary<string, ChartConfig>
        {
            ["Chrome"] = new ChartConfig("Chrome", "var(--chart-1)"),
            ["Safari"] = new ChartConfig("Safari", "var(--chart-2)"),
            ["Other"] = new ChartConfig("Other browsers", "var(--chart-5)"),
        };
        var h = Harness(() => new Chart
        {
            Kind = ChartKind.Pie,
            Height = 240,
            Labels = ["Chrome", "Safari", "Other"],
            Series = [new ChartSeries("share", new[] { 60.0, 30.0, 10.0 })],
            Config = config,
            ShowAxis = false,
        });

        var items = Slots(h, "chart-legend-item");
        Assert.Equal(new[] { "Chrome", "Safari", "Other browsers" }, items.Select(e => e.TextContent.Trim()).ToArray());
        Assert.Contains("var(--chart-5)", items[2].Children[0].InlineStyle ?? "");
        Assert.Empty(Slots(h, "chart-axis")); // pie charts do not draw an axis
    }

    [Theory]
    [InlineData(ChartKind.Line)]
    [InlineData(ChartKind.Bar)]
    [InlineData(ChartKind.Area)]
    [InlineData(ChartKind.Pie)]
    public void Chart_rendering_is_deterministic(ChartKind kind)
    {
        var first = Harness(() => Sample(kind));
        var second = Harness(() => Sample(kind));

        using var a = first.Render();
        using var b = second.Render();
        var bytesA = a.ToRgbaUnpremultiplied();
        var bytesB = b.ToRgbaUnpremultiplied();
        Assert.Equal(bytesA.Length, bytesB.Length);
        Assert.True(bytesA.SequenceEqual(bytesB), $"{kind} rendered differently on two mounts");
    }

    [Fact]
    public void Chart_renders_in_dark_theme()
    {
        var h = Harness(() => Sample(ChartKind.Area));
        ShadcnTheme.SetDark(h.Root, true);
        h.Frame();

        Assert.Equal("dark", h.Root.RootElement.GetAttribute("data-aw-theme") ?? "");
        Assert.NotEmpty(SvgLayers(h));
        Assert.NotEmpty(Slots(h, "chart-legend-item"));
        using var bitmap = h.Render();
        Assert.Equal(520, bitmap.Width);
        Assert.Equal(480, bitmap.Height);
    }

    [Fact]
    public void Chart_uses_series_then_config_colors_and_config_labels()
    {
        var config = new Dictionary<string, ChartConfig>
        {
            ["Revenue"] = new ChartConfig("Turnover", "#abcdef"),
            ["Churn"] = new ChartConfig("Churn", "#fedcba"),
        };
        var h = Harness(() => new Chart
        {
            Kind = ChartKind.Bar,
            Height = 240,
            Labels = ["Q1", "Q2", "Q3"],
            Series =
            [
                new ChartSeries("Revenue", new[] { 10.0, 14.0, 18.0 }, "#123456"),
                new ChartSeries("Churn", new[] { 4.0, 3.0, 2.0 }),
            ],
            Config = config,
        });

        var items = Slots(h, "chart-legend-item");
        Assert.Equal(new[] { "Turnover", "Churn" }, items.Select(e => e.TextContent.Trim()).ToArray());

        // Explicit series color wins over the config color; config color is the fallback.
        Assert.Contains("#123456", items[0].Children[0].InlineStyle ?? "");
        Assert.Contains("#fedcba", items[1].Children[0].InlineStyle ?? "");

        var styles = h.FindAll(e => e is SvgElement).Select(e => e.InlineStyle ?? "").ToList();
        Assert.Contains(styles, s => s.Contains("#123456"));
        Assert.Contains(styles, s => s.Contains("#fedcba"));
    }

    [Fact]
    public void Chart_default_series_colors_use_chart_tokens()
    {
        var h = Harness(() => Sample(ChartKind.Line));
        var items = Slots(h, "chart-legend-item");

        Assert.Contains("var(--chart-1)", items[0].Children[0].InlineStyle ?? "");
        Assert.Contains("var(--chart-2)", items[1].Children[0].InlineStyle ?? "");
    }

    [Fact]
    public void Chart_hover_shows_a_tooltip_for_the_column()
    {
        var h = Harness(() => Sample(ChartKind.Line));

        Assert.Empty(Slots(h, "chart-tooltip"));
        var hits = Slots(h, "chart-hit");
        Assert.Equal(4, hits.Count);

        h.PointerMove(hits[1].GetBoundingClientRect().Center);

        var tooltip = Slot(h, "chart-tooltip");
        Assert.Contains("Feb", tooltip.TextContent);
        Assert.Contains("Desktop", tooltip.TextContent);
        Assert.Contains("Mobile", tooltip.TextContent);
    }

    [Fact]
    public void ChartContainer_shares_config_with_nested_chart_and_legend()
    {
        var config = new Dictionary<string, ChartConfig>
        {
            ["Desktop"] = new ChartConfig("Workstation", "#010203"),
        };
        var h = Harness(() => new ChartContainer
        {
            Config = config,
            Children =
            [
                new Chart
                {
                    Kind = ChartKind.Line,
                    Height = 240,
                    Labels = ["Jan", "Feb"],
                    Series = [new ChartSeries("Desktop", new[] { 1.0, 2.0 })],
                },
                new ChartLegend { Items = ["Desktop"] },
            ],
        });

        var items = Slots(h, "chart-legend-item");
        Assert.Equal(new[] { "Workstation", "Workstation" }, items.Select(e => e.TextContent.Trim()).ToArray());
        Assert.All(items, item => Assert.Contains("#010203", item.Children[0].InlineStyle ?? ""));
    }

    [Fact]
    public void Chart_show_flags_hide_axis_legend_and_grid()
    {
        var h = Harness(() => new Chart
        {
            Kind = ChartKind.Line,
            Height = 240,
            Labels = ["Jan", "Feb"],
            Series = [new ChartSeries("Desktop", new[] { 1.0, 2.0 })],
            ShowAxis = false,
            ShowLegend = false,
            ShowGrid = false,
        });

        Assert.Empty(Slots(h, "chart-axis"));
        Assert.Empty(Slots(h, "chart-legend"));
        Assert.Single(SvgLayers(h)); // only the series layer; no grid layer
    }

    [Theory]
    [InlineData(ChartKind.Line)]
    [InlineData(ChartKind.Bar)]
    [InlineData(ChartKind.Area)]
    [InlineData(ChartKind.Pie)]
    public void Chart_preserve_aspect_ratio_defaults_to_none(ChartKind kind)
    {
        var h = Harness(() => Sample(kind));

        var layers = SvgLayers(h);
        Assert.NotEmpty(layers);
        Assert.All(layers, layer =>
            Assert.Equal(SvgAlign.None, layer.Document!.PreserveAspectRatio.Align));
    }

    [Theory]
    [InlineData(ChartKind.Line)]
    [InlineData(ChartKind.Bar)]
    [InlineData(ChartKind.Area)]
    [InlineData(ChartKind.Pie)]
    public void Chart_preserve_aspect_ratio_supplied_value_is_emitted_verbatim(ChartKind kind)
    {
        var chart = Sample(kind);
        chart.PreserveAspectRatio = "xMaxYMax slice";
        var h = Harness(() => chart);

        var layers = SvgLayers(h);
        Assert.NotEmpty(layers);
        Assert.All(layers, layer =>
        {
            Assert.Equal(SvgAlign.XMaxYMax, layer.Document!.PreserveAspectRatio.Align);
            Assert.True(layer.Document.PreserveAspectRatio.Slice);
        });
    }

    static Chart PieSample() => new()
    {
        Kind = ChartKind.Pie,
        Height = 240,
        Labels = ["Chrome", "Safari", "Other"],
        Series = [new ChartSeries("share", new[] { 60.0, 30.0, 10.0 })],
        ShowAxis = false,
        ShowLegend = false,
    };

    /// <summary>
    /// A client-space point at <paramref name="degrees"/> (0° = right, −90° = top) and
    /// <paramref name="radiusFraction"/> of the pie radius. The plot SVG stretches 520×240 to the plot box, so
    /// both axes are mapped back through the same scale the renderer uses.
    /// </summary>
    static Point PiePoint(Rect plot, double degrees, double radiusFraction)
    {
        const double refWidth = 520;
        double radius = Math.Min(refWidth, plot.Height) / 2.0 - 12.0;
        double radians = degrees * Math.PI / 180.0;
        double vx = refWidth / 2.0 + Math.Cos(radians) * radius * radiusFraction;
        double vy = plot.Height / 2.0 + Math.Sin(radians) * radius * radiusFraction;
        return new Point((float)(plot.X + vx * plot.Width / refWidth), (float)(plot.Y + vy));
    }

    [Fact]
    public void Chart_pie_and_donut_hover_selects_the_slice_under_the_pointer()
    {
        var h = Harness(PieSample);

        Assert.Empty(Slots(h, "chart-tooltip"));
        Assert.Single(Slots(h, "chart-hit")); // a pie has one polar overlay, not one column per category

        var plot = Slot(h, "chart-plot").GetBoundingClientRect();

        // 60% slice: from −90° to 126° (0° is inside it).
        h.PointerMove(PiePoint(plot, 0, 0.6));
        Assert.Contains("Chrome", Slot(h, "chart-tooltip").TextContent);

        // 30% slice: from 126° to 234°.
        h.PointerMove(PiePoint(plot, 180, 0.6));
        Assert.Contains("Safari", Slot(h, "chart-tooltip").TextContent);

        // 10% slice: from 234° to 270°.
        h.PointerMove(PiePoint(plot, 260, 0.6));
        Assert.Contains("Other", Slot(h, "chart-tooltip").TextContent);

        // The exact center is deterministic: angle 0°, i.e. the first slice.
        h.PointerMove(new Point(plot.X + plot.Width / 2, plot.Y + plot.Height / 2));
        Assert.Contains("Chrome", Slot(h, "chart-tooltip").TextContent);
    }

    [Fact]
    public void Chart_pie_hover_clears_when_the_pointer_is_outside_the_radius()
    {
        var h = Harness(PieSample);
        var plot = Slot(h, "chart-plot").GetBoundingClientRect();

        // Just inside the edge of the first slice.
        h.PointerMove(PiePoint(plot, 0, 0.95));
        Assert.Contains("Chrome", Slot(h, "chart-tooltip").TextContent);

        // Just outside the radius: no slice, no tooltip.
        h.PointerMove(PiePoint(plot, 0, 1.15));
        Assert.Empty(Slots(h, "chart-tooltip"));

        // Moving back inside restores the hovered slice.
        h.PointerMove(PiePoint(plot, 180, 0.5));
        Assert.Contains("Safari", Slot(h, "chart-tooltip").TextContent);
    }

    [Fact]
    public void Chart_pie_hover_keeps_the_legend_and_labels_visible()
    {
        var h = Harness(() => new Chart
        {
            Kind = ChartKind.Pie,
            Height = 240,
            Labels = ["Chrome", "Safari", "Other"],
            Series = [new ChartSeries("share", new[] { 60.0, 30.0, 10.0 })],
            ShowLegend = true,
        });

        var plot = Slot(h, "chart-plot").GetBoundingClientRect();
        h.PointerMove(PiePoint(plot, 260, 0.6));

        Assert.Equal(new[] { "Chrome", "Safari", "Other" },
            Slots(h, "chart-legend-item").Select(e => e.TextContent.Trim()).ToArray());
        Assert.Contains("Other", Slot(h, "chart-tooltip").TextContent);
        Assert.Empty(Slots(h, "chart-axis")); // pie charts do not draw an axis
    }

    [Fact]
    public void Chart_pie_tooltip_uses_the_configured_category_label_and_color()
    {
        var config = new Dictionary<string, ChartConfig>
        {
            ["Other"] = new ChartConfig("Other browsers", "var(--chart-5)"),
        };
        var h = Harness(() => new Chart
        {
            Kind = ChartKind.Pie,
            Height = 240,
            Labels = ["Chrome", "Safari", "Other"],
            Series = [new ChartSeries("share", new[] { 60.0, 30.0, 10.0 })],
            Config = config,
            ShowLegend = false,
        });

        var plot = Slot(h, "chart-plot").GetBoundingClientRect();
        h.PointerMove(PiePoint(plot, 260, 0.6));

        var tooltip = Slot(h, "chart-tooltip");
        Assert.Contains("Other browsers", tooltip.TextContent);
        Assert.Contains("var(--chart-5)", tooltip.Children[1].Children[0].InlineStyle ?? "");
    }
}
