using System.Collections.Generic;
using AwAUI.Shadcn;
using AwAUI.UI;
using Text = AwAUI.UI.Text;
using ShadcnChart = AwAUI.Shadcn.Chart;

namespace ShadcnGallery;

/// <summary>Data visualisation pages. Charts are composed from AwAUI SVG and text primitives and read the
/// shared <c>--chart-1…5</c> tokens, so they follow the active theme.</summary>
public static class DataPages
{
    public static void Register()
    {
        DocsCatalog.Add("chart", "Chart",
            DocsExample.Of("Line chart", LineChart),
            DocsExample.Of("Bar chart", BarChart),
            DocsExample.Of("Area chart", AreaChart),
            DocsExample.Of("Pie chart", PieChart),
            DocsExample.Of("Pie with hover", PieHoverChart),
            DocsExample.Of("Aspect ratio", AspectRatioChart));
    }

    static readonly IReadOnlyList<string> s_months =
        new[] { "Jan", "Feb", "Mar", "Apr", "May", "Jun", "Jul" };

    static readonly IReadOnlyDictionary<string, ChartConfig> s_trafficConfig =
        new Dictionary<string, ChartConfig>
        {
            ["desktop"] = new("Desktop", "var(--chart-1)"),
            ["mobile"] = new("Mobile", "var(--chart-2)"),
        };

    static readonly IReadOnlyDictionary<string, ChartConfig> s_signupConfig =
        new Dictionary<string, ChartConfig>
        {
            ["organic"] = new("Organic", "var(--chart-1)"),
            ["paid"] = new("Paid", "var(--chart-2)"),
        };

    static readonly IReadOnlyDictionary<string, ChartConfig> s_browserConfig =
        new Dictionary<string, ChartConfig>
        {
            ["Chrome"] = new("Chrome", "var(--chart-1)"),
            ["Safari"] = new("Safari", "var(--chart-2)"),
            ["Firefox"] = new("Firefox", "var(--chart-3)"),
            ["Edge"] = new("Edge", "var(--chart-4)"),
            ["Other"] = new("Other", "var(--chart-5)"),
        };

    static Node LineChart() => DocsUi.Stack(
        ChartCard(
            "Visitor traffic",
            "Two series over the last seven months. Hover the plot to inspect values.",
            new ShadcnChart
            {
                Kind = ChartKind.Line,
                Labels = s_months,
                Series =
                [
                    new ChartSeries("desktop", DocsUi.Series(7, 80, 240, 11)),
                    new ChartSeries("mobile", DocsUi.Series(7, 40, 180, 23)),
                ],
                Config = s_trafficConfig,
                Height = 240,
                ShowLegend = true,
                ShowGrid = true,
            }),
        DocsUi.Note("Series colors resolve to the --chart-1 … --chart-5 tokens, so charts follow light and dark " +
                    "themes automatically. Override those tokens on any subtree to repaint every chart inside it."));

    static Node BarChart() => DocsUi.Stack(
        ChartCard(
            "Signups by source",
            "Grouped bars compare organic and paid signups each month.",
            new ShadcnChart
            {
                Kind = ChartKind.Bar,
                Labels = s_months,
                Series =
                [
                    new ChartSeries("organic", DocsUi.Series(7, 40, 150, 5)),
                    new ChartSeries("paid", DocsUi.Series(7, 20, 110, 17)),
                ],
                Config = s_signupConfig,
                Height = 220,
                ShowLegend = true,
                ShowGrid = true,
            }));

    static Node AreaChart() => DocsUi.Stack(
        ChartCard(
            "Weekly sessions",
            "Filled areas highlight the volume trend for each platform.",
            new ShadcnChart
            {
                Kind = ChartKind.Area,
                Labels = s_months,
                Series =
                [
                    new ChartSeries("desktop", DocsUi.Series(7, 120, 320, 3)),
                    new ChartSeries("mobile", DocsUi.Series(7, 90, 260, 29)),
                ],
                Config = s_trafficConfig,
                Height = 220,
                ShowLegend = true,
                ShowGrid = true,
            }));

    static Node PieChart() => DocsUi.Stack(
        ChartCard(
            "Browser share",
            "One value per category, colored from the chart palette.",
            new ShadcnChart
            {
                Kind = ChartKind.Pie,
                Labels = new[] { "Chrome", "Safari", "Firefox", "Edge", "Other" },
                Series = [new ChartSeries("share", DocsUi.Series(5, 10, 60, 41))],
                Config = s_browserConfig,
                Height = 240,
                ShowLegend = true,
            }),
        DocsUi.Grid3(
            DocsUi.Swatch("--chart-1", "var(--chart-1)"),
            DocsUi.Swatch("--chart-2", "var(--chart-2)"),
            DocsUi.Swatch("--chart-3", "var(--chart-3)"),
            DocsUi.Swatch("--chart-4", "var(--chart-4)"),
            DocsUi.Swatch("--chart-5", "var(--chart-5)")));

    static Node PieHoverChart() => DocsUi.Stack(
        ChartCard(
            "Traffic sources",
            "Hover a slice to inspect its share; move outside the pie radius to clear the tooltip.",
            new ShadcnChart
            {
                Kind = ChartKind.Pie,
                Labels = new[] { "Organic", "Referral", "Social", "Email", "Direct" },
                Series = [new ChartSeries("visits", DocsUi.Series(5, 20, 90, 53))],
                Height = 240,
                ShowLegend = true,
            }),
        DocsUi.Note("Pie hit-testing maps the pointer into the plot's SVG viewBox and then to polar coordinates: the " +
                    "slice under the cursor opens the same tooltip as the other kinds, and a hit outside the radius " +
                    "clears it. There is no separate OnSliceHover callback."));

    static Node AspectRatioChart() => DocsUi.Stack(
        ChartCard(
            "Sessions with a fixed aspect ratio",
            "preserveAspectRatio xMidYMid meet letterboxes the viewBox instead of stretching it.",
            new ShadcnChart
            {
                Kind = ChartKind.Area,
                Labels = s_months,
                Series =
                [
                    new ChartSeries("desktop", DocsUi.Series(7, 90, 260, 13)),
                    new ChartSeries("mobile", DocsUi.Series(7, 60, 210, 31)),
                ],
                Config = s_trafficConfig,
                Height = 220,
                ShowLegend = true,
                ShowGrid = true,
                PreserveAspectRatio = "xMidYMid meet",
            }),
        DocsUi.Note("PreserveAspectRatio is emitted verbatim on the plot SVG. The default null keeps \"none\", so the " +
                    "chart stretches to its container; \"xMidYMid meet\" keeps the 520-logical-pixel viewBox centered " +
                    "and fully visible when the plot is wider or narrower than that."));

    static Card ChartCard(string title, string description, ShadcnChart chart) => new()
    {
        Children = new NodeList(
        [
            new CardHeader
            {
                Children = new NodeList(
                [
                    new CardTitle { Children = new NodeList([new Text(title)]) },
                    new CardDescription { Children = new NodeList([new Text(description)]) },
                ]),
            },
            new CardContent { Children = new NodeList([chart]) },
        ]),
    };
}
