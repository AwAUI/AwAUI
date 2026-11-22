using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using AwAUI.Svg;
using AwAUI.UI;
using UIText = AwAUI.UI.Text;
using UISvg = AwAUI.UI.Svg;

namespace AwAUI.Shadcn;

/// <summary>Label and optional color for one series or pie category.</summary>
public sealed record ChartConfig(string Label, string? Color = null);

/// <summary>The plot type rendered by <see cref="Chart"/>.</summary>
public enum ChartKind
{
    Line,
    Bar,
    Area,
    Pie,
}

/// <summary>One named data series. <see cref="Values"/> align with the chart's labels.</summary>
public sealed record ChartSeries(string Name, IReadOnlyList<double> Values, string? Color = null);

/// <summary>
/// A native chart composed from AwAUI primitives. The plot is real SVG (polyline, rect, polygon and arc paths),
/// while the axis labels, legend and tooltip are regular AwAUI text nodes. Colors default to the
/// <c>--chart-1…5</c> tokens and therefore follow the light/dark theme.
/// <para>
/// All kinds share one hovered-index state: bar, line and area put one transparent <c>data-slot="chart-hit"</c>
/// overlay per category, while pie/donut keeps a single overlay and converts the pointer position to an angle
/// and radius (a hit outside the pie radius clears the state). The hovered index is surfaced through the same
/// tooltip as the other kinds, so no extra callback (OnSliceHover / OnSliceClick) is needed. There is no donut
/// kind yet; a pie drawn with an inner radius would reuse the same geometry.
/// </para>
/// </summary>
public sealed partial class Chart : Component
{
    const string RootClass = "flex w-full flex-col gap-2 text-xs text-foreground";
    const string PlotClass = "relative w-full overflow-hidden";
    const string SvgWrapClass = "absolute inset-0";
    const string AxisClass = "flex items-center justify-between gap-1 px-2 text-[10px] leading-none text-muted-foreground";
    const string AxisLabelClass = "flex-1 truncate text-center";
    const string LegendClass = "flex flex-wrap items-center justify-center gap-3 text-xs text-muted-foreground";
    const string LegendItemClass = "flex items-center gap-1.5";
    const string TooltipClass =
        "pointer-events-none absolute top-1 z-10 min-w-[6rem] rounded-md border bg-popover px-2 py-1 text-xs " +
        "text-popover-foreground shadow-md";
    const string TooltipRowClass = "flex items-center gap-1.5";
    const string HitClass = "absolute top-0 bottom-0";

    readonly State<int?> _hovered = new((int?)null);

    public ChartKind Kind { get; set; }

    /// <summary>Category labels. Missing entries are numbered automatically.</summary>
    public IReadOnlyList<string> Labels { get; set; } = Array.Empty<string>();

    public IReadOnlyList<ChartSeries> Series { get; set; } = Array.Empty<ChartSeries>();

    /// <summary>Optional per-series (or per-category, for pie) label and color overrides.</summary>
    public IReadOnlyDictionary<string, ChartConfig>? Config { get; set; }

    public string? Class { get; set; }

    /// <summary>Extra content appended below the legend.</summary>
    public NodeList? Children { get; set; }

    public bool ShowLegend { get; set; } = true;
    public bool ShowGrid { get; set; } = true;
    public bool ShowAxis { get; set; } = true;

    /// <summary>Height of the plot area in logical pixels.</summary>
    public float Height { get; set; } = 240;

    /// <summary>
    /// The <c>preserveAspectRatio</c> value of the plot SVG documents. The default (<c>null</c>) keeps the
    /// chart's existing <c>"none"</c> output so the plot stretches to the container; a supplied value such as
    /// <c>"xMidYMid meet"</c> is emitted verbatim.
    /// </summary>
    public string? PreserveAspectRatio { get; set; }

    [CaptureUnmatchedAttributes] public AttributeDictionary? Rest { get; set; }

    protected override Node Build()
    {
        var config = Config;
        if (config is null && TryUse<ChartConfigContext>(out var provided)) config = provided.Config;

        var series = Series ?? Array.Empty<ChartSeries>();
        float height = ChartParts.SafeHeight(Height);
        var labels = ChartParts.NormalizeLabels(Labels, series, Kind);
        var entries = ChartParts.LegendEntries(Kind, series, labels, config);
        // Read once so a rebuild is scheduled for every kind (pie included) when the hovered slice changes.
        int? hovered = _hovered.Value;

        var svgWrap = new Div { Class = SvgWrapClass };
        svgWrap.Attributes["data-slot"] = "chart-svg";
        svgWrap.Style = "position:absolute;top:0;right:0;bottom:0;left:0";
        foreach (var layer in ChartParts.BuildLayers(Kind, series, labels, config, ShowGrid, height, PreserveAspectRatio))
            svgWrap.Children.Add(layer);

        var plot = new Div { Class = PlotClass, Children = { svgWrap } };
        plot.Attributes["data-slot"] = "chart-plot";
        plot.Style = "position:relative;width:100%;height:" + ChartParts.N(height) + "px";
        plot.Role = Role.Image;
        plot.AriaLabel = ChartParts.Summary(Kind, entries);

        if (Kind == ChartKind.Pie)
        {
            var values = ChartParts.PieValues(series);
            if (values.Count > 0)
            {
                AddPieHitTarget(plot, values, height);
                if (hovered is { } index && index >= 0 && index < values.Count)
                    plot.Children.Add(BuildPieTooltip(index, labels, values, config));
            }
        }
        else if (labels.Count > 0)
        {
            AddHitTargets(plot, labels.Count);
            if (hovered is { } index && index >= 0 && index < labels.Count)
                plot.Children.Add(BuildTooltip(index, labels, series, config));
        }

        var rootChildren = new NodeList { plot };
        if (ShowAxis && Kind != ChartKind.Pie && labels.Count > 0) rootChildren.Add(BuildAxis(labels));
        if (ShowLegend && entries.Count > 0) rootChildren.Add(BuildLegend(entries));
        if (Children is not null) rootChildren.AddRange(Children);

        return StaticParts.Style(new Div { Children = rootChildren }, "chart", RootClass, Class, Rest);
    }

    void AddHitTargets(Div plot, int count)
    {
        double width = 100.0 / count;
        for (int i = 0; i < count; i++)
        {
            int index = i;
            var hit = new Div { Class = HitClass };
            hit.Attributes["data-slot"] = "chart-hit";
            hit.Attributes["aria-hidden"] = "true";
            hit.Style = "position:absolute;top:0;bottom:0;left:" + ChartParts.N(i * width) + "%;width:" +
                        ChartParts.N(width) + "%";
            hit.OnPointerEnter = e => _hovered.Value = index;
            hit.OnPointerMove = e => _hovered.Value = index;
            plot.Children.Add(hit);
        }
        plot.OnPointerLeave = e => _hovered.Value = null;
    }

    /// <summary>
    /// Pie/donut hit-testing: one transparent overlay covers the plot; the pointer is mapped into the SVG viewBox
    /// (undoing the default stretch) and then to polar coordinates around the pie center. A point outside the
    /// radius produces <c>null</c>, which clears the tooltip. The center point is deterministic: its angle is 0°
    /// (towards the right), so it belongs to the slice containing that angle. A non-default
    /// <see cref="PreserveAspectRatio"/> changes the drawn geometry but not this hit-test mapping.
    /// </summary>
    /// </summary>
    void AddPieHitTarget(Div plot, IReadOnlyList<double> values, float height)
    {
        var hit = new Div { Class = "absolute inset-0" };
        hit.Attributes["data-slot"] = "chart-hit";
        hit.Attributes["aria-hidden"] = "true";
        hit.Style = "position:absolute;top:0;right:0;bottom:0;left:0";

        void Hover(PointerEvent e)
        {
            if (e.CurrentTarget is not Element target) return;
            var box = target.GetBoundingClientRect();
            _hovered.Value = ChartParts.PieHit(e.Position.X - box.X, e.Position.Y - box.Y, box.Width,
                box.Height, values, height);
        }

        hit.OnPointerEnter = Hover;
        hit.OnPointerMove = Hover;
        hit.OnPointerLeave = e => _hovered.Value = null;
        plot.Children.Add(hit);
        plot.OnPointerLeave = e => _hovered.Value = null;
    }

    /// <summary>Pie tooltip, anchored at the hovered slice's mid-angle rather than at a column.</summary>
    Div BuildPieTooltip(int index, IReadOnlyList<string> labels, IReadOnlyList<double> values,
        IReadOnlyDictionary<string, ChartConfig>? config)
    {
        string name = index < labels.Count ? labels[index] : (index + 1).ToString(CultureInfo.InvariantCulture);
        string label = name;
        if (config is not null && config.TryGetValue(name, out var entry) && !string.IsNullOrEmpty(entry.Label))
            label = entry.Label!;
        var tooltip = StaticParts.Style(new Div(), "chart-tooltip", TooltipClass, null, null);
        var (x, y) = ChartParts.PieAnchor(values, index, ChartParts.PieGeometryOf(ChartParts.SafeHeight(Height)));
        tooltip.Style = "position:absolute;left:" + ChartParts.N(x / ChartParts.RefWidth * 100.0) +
                        "%;top:" + ChartParts.N(y) + "px;transform:translate(-50%, -50%)";
        tooltip.Children.Add(new Div { Class = "font-medium", Children = { new UIText(label) } });
        if (index < values.Count)
        {
            var row = new Div { Class = TooltipRowClass };
            row.Children.Add(ChartParts.Swatch(ChartParts.CategoryColor(name, index, config)));
            row.Children.Add(new UIText(ChartParts.N(values[index])));
            tooltip.Children.Add(row);
        }
        return tooltip;
    }

    Div BuildTooltip(int index, IReadOnlyList<string> labels, IReadOnlyList<ChartSeries> series,
        IReadOnlyDictionary<string, ChartConfig>? config)
    {
        int count = Math.Max(1, labels.Count);
        var tooltip = StaticParts.Style(new Div(), "chart-tooltip", TooltipClass, null, null);
        tooltip.Style = "position:absolute;left:" + ChartParts.N((index + 0.5) * 100.0 / count) +
                        "%;transform:translateX(-50%)";
        tooltip.Children.Add(new Div { Class = "font-medium", Children = { new UIText(labels[index]) } });
        for (int i = 0; i < series.Count; i++)
        {
            var values = series[i].Values;
            if (values is null || index >= values.Count) continue;
            var row = new Div { Class = TooltipRowClass };
            row.Children.Add(ChartParts.Swatch(ChartParts.SeriesColor(series[i], i, config)));
            row.Children.Add(new UIText(series[i].Name + ": " + ChartParts.N(values[index])));
            tooltip.Children.Add(row);
        }
        return tooltip;
    }

    static Div BuildAxis(IReadOnlyList<string> labels)
    {
        var axis = StaticParts.Style(new Div(), "chart-axis", AxisClass, null, null);
        foreach (var label in labels)
        {
            var cell = new Div { Class = AxisLabelClass };
            cell.Children.Add(new UIText(label));
            axis.Children.Add(cell);
        }
        return axis;
    }

    internal static Div BuildLegend(IReadOnlyList<ChartParts.LegendEntry> entries)
    {
        var legend = StaticParts.Style(new Div(), "chart-legend", LegendClass, null, null);
        foreach (var entry in entries)
        {
            var item = new Div { Class = LegendItemClass };
            item.Attributes["data-slot"] = "chart-legend-item";
            item.Children.Add(ChartParts.Swatch(entry.Color));
            item.Children.Add(new UIText(entry.Label));
            legend.Children.Add(item);
        }
        return legend;
    }
}

/// <summary>
/// Optional wrapper that shares one <see cref="ChartConfig"/> with nested <see cref="Chart"/> and
/// <see cref="ChartLegend"/> instances.
/// </summary>
public sealed partial class ChartContainer : Component
{
    const string RootClass = "flex w-full flex-col justify-center gap-2 text-xs text-foreground";

    public IReadOnlyDictionary<string, ChartConfig>? Config { get; set; }
    public string? Class { get; set; }
    public NodeList? Children { get; set; }
    [CaptureUnmatchedAttributes] public AttributeDictionary? Rest { get; set; }

    protected override Node Build()
    {
        var root = StaticParts.Style(new Div { Children = Children ?? new NodeList() }, "chart", RootClass, Class, Rest);
        return Config is null ? root : Provide(new ChartConfigContext(Config), root);
    }
}

/// <summary>A standalone legend. Use <see cref="Items"/> for names or supply <see cref="Children"/> directly.</summary>
public sealed partial class ChartLegend : Component
{
    const string RootClass = "flex flex-wrap items-center justify-center gap-3 text-xs text-muted-foreground";

    public IReadOnlyDictionary<string, ChartConfig>? Config { get; set; }

    /// <summary>Series or category names shown in the legend.</summary>
    public IReadOnlyList<string>? Items { get; set; }

    public string? Class { get; set; }
    public NodeList? Children { get; set; }
    [CaptureUnmatchedAttributes] public AttributeDictionary? Rest { get; set; }

    protected override Node Build()
    {
        var config = Config;
        if (config is null && TryUse<ChartConfigContext>(out var provided)) config = provided.Config;

        var root = StaticParts.Style(new Div(), "chart-legend", RootClass, Class, Rest);
        foreach (var entry in ChartParts.LegendEntries(Items, config))
        {
            var item = new Div { Class = "flex items-center gap-1.5" };
            item.Attributes["data-slot"] = "chart-legend-item";
            item.Children.Add(ChartParts.Swatch(entry.Color));
            item.Children.Add(new UIText(entry.Label));
            root.Children.Add(item);
        }
        if (Children is not null) root.Children.AddRange(Children);
        return root;
    }
}

internal sealed class ChartConfigContext
{
    public ChartConfigContext(IReadOnlyDictionary<string, ChartConfig>? config) => Config = config;

    public IReadOnlyDictionary<string, ChartConfig>? Config { get; }
}

/// <summary>Geometry, markup and color helpers shared by the chart parts.</summary>
internal static class ChartParts
{
    internal const double RefWidth = 520;
    const double PadLeft = 12;
    const double PadRight = 12;
    const double PadTop = 12;
    const double PadBottom = 12;

    static readonly string[] Palette =
    {
        "var(--chart-1)",
        "var(--chart-2)",
        "var(--chart-3)",
        "var(--chart-4)",
        "var(--chart-5)",
    };

    static readonly ConcurrentDictionary<string, SvgDocument> Documents = new(StringComparer.Ordinal);

    internal sealed class LegendEntry
    {
        public LegendEntry(string label, string color)
        {
            Label = label;
            Color = color;
        }

        public string Label { get; }
        public string Color { get; }
    }

    internal static float SafeHeight(float height) =>
        height > 0 && !float.IsNaN(height) && !float.IsInfinity(height) ? height : 240f;

    internal static string N(double value)
    {
        if (double.IsNaN(value) || double.IsInfinity(value)) return "0";
        return value.ToString("0.###", CultureInfo.InvariantCulture);
    }

    internal static IReadOnlyList<string> NormalizeLabels(IReadOnlyList<string>? labels, IReadOnlyList<ChartSeries> series,
        ChartKind kind)
    {
        int dataCount = 0;
        if (kind == ChartKind.Pie)
        {
            if (series.Count > 0 && series[0].Values is { } pieValues) dataCount = pieValues.Count;
        }
        else
        {
            foreach (var item in series)
            {
                if (item.Values is { } values && values.Count > dataCount) dataCount = values.Count;
            }
        }
        int count = Math.Max(dataCount, labels?.Count ?? 0);
        var result = new List<string>(count);
        for (int i = 0; i < count; i++)
        {
            string? label = labels is not null && i < labels.Count ? labels[i] : null;
            result.Add(string.IsNullOrEmpty(label) ? (i + 1).ToString(CultureInfo.InvariantCulture) : label!);
        }
        return result;
    }

    internal static string SeriesColor(ChartSeries series, int index, IReadOnlyDictionary<string, ChartConfig>? config)
    {
        if (!string.IsNullOrEmpty(series.Color)) return series.Color!;
        if (config is not null && config.TryGetValue(series.Name, out var entry) && !string.IsNullOrEmpty(entry.Color))
            return entry.Color!;
        return Palette[index % Palette.Length];
    }

    internal static string CategoryColor(string name, int index, IReadOnlyDictionary<string, ChartConfig>? config)
    {
        if (config is not null && config.TryGetValue(name, out var entry) && !string.IsNullOrEmpty(entry.Color))
            return entry.Color!;
        return Palette[index % Palette.Length];
    }

    internal static List<LegendEntry> LegendEntries(ChartKind kind, IReadOnlyList<ChartSeries> series,
        IReadOnlyList<string> labels, IReadOnlyDictionary<string, ChartConfig>? config)
    {
        var entries = new List<LegendEntry>();
        if (kind == ChartKind.Pie)
        {
            for (int i = 0; i < labels.Count; i++)
            {
                string name = labels[i];
                string label = name;
                if (config is not null && config.TryGetValue(name, out var entry) && !string.IsNullOrEmpty(entry.Label))
                    label = entry.Label;
                entries.Add(new LegendEntry(label, CategoryColor(name, i, config)));
            }
            return entries;
        }

        for (int i = 0; i < series.Count; i++)
        {
            var item = series[i];
            string label = item.Name;
            if (config is not null && config.TryGetValue(item.Name, out var entry) && !string.IsNullOrEmpty(entry.Label))
                label = entry.Label;
            entries.Add(new LegendEntry(label, SeriesColor(item, i, config)));
        }
        return entries;
    }

    internal static List<LegendEntry> LegendEntries(IReadOnlyList<string>? items,
        IReadOnlyDictionary<string, ChartConfig>? config)
    {
        var entries = new List<LegendEntry>();
        if (items is null) return entries;
        for (int i = 0; i < items.Count; i++)
        {
            string name = items[i];
            string label = name;
            if (config is not null && config.TryGetValue(name, out var entry) && !string.IsNullOrEmpty(entry.Label))
                label = entry.Label;
            entries.Add(new LegendEntry(label, CategoryColor(name, i, config)));
        }
        return entries;
    }

    internal static Div Swatch(string color)
    {
        var swatch = new Div { Class = "size-2 shrink-0 rounded-[2px]" };
        swatch.Style = "background-color:" + color;
        swatch.Attributes["aria-hidden"] = "true";
        return swatch;
    }

    internal static List<UISvg> BuildLayers(ChartKind kind, IReadOnlyList<ChartSeries> series,
        IReadOnlyList<string> labels, IReadOnlyDictionary<string, ChartConfig>? config, bool showGrid, float height,
        string? preserveAspectRatio)
    {
        var layers = new List<UISvg>();
        if (height <= 0) return layers;

        if (kind == ChartKind.Pie)
        {
            BuildPie(series, labels, config, height, preserveAspectRatio, layers);
            return layers;
        }

        double min, max;
        Domain(series, kind, out min, out max);
        if (showGrid) layers.Add(Layer(GridMarkup(height, preserveAspectRatio), "var(--border)"));

        for (int i = 0; i < series.Count; i++)
        {
            string markup = kind switch
            {
                ChartKind.Bar => BarMarkup(series[i], series.Count, i, labels.Count, min, max, height, preserveAspectRatio),
                ChartKind.Area => AreaMarkup(series[i], labels.Count, min, max, height, preserveAspectRatio),
                _ => LineMarkup(series[i], labels.Count, min, max, height, preserveAspectRatio),
            };
            layers.Add(Layer(markup, SeriesColor(series[i], i, config)));
        }
        return layers;
    }

    static UISvg Layer(string markup, string color)
    {
        var document = Documents.GetOrAdd(markup, static text => SvgDocument.Parse(text));
        var svg = new UISvg(document) { Class = "absolute inset-0 size-full" };
        svg.Style = "position:absolute;top:0;right:0;bottom:0;left:0;width:100%;height:100%;color:" + color;
        svg.Attributes["aria-hidden"] = "true";
        return svg;
    }

    /// <summary>
    /// Wraps the plot in its SVG document. <paramref name="preserveAspectRatio"/> is <c>null</c> for the default
    /// stretched output (<c>"none"</c>) or emitted verbatim, matching the shadcn/Recharts attribute.
    /// </summary>
    static string Root(string content, float height, string? preserveAspectRatio) =>
        "<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 " + N(RefWidth) + " " + N(height) + "\" " +
        "preserveAspectRatio=\"" + (preserveAspectRatio ?? "none") + "\" width=\"100%\" height=\"100%\">" +
        content + "</svg>";

    static string GridMarkup(float height, string? preserveAspectRatio)
    {
        var sb = new StringBuilder();
        sb.Append("<g stroke=\"currentColor\" stroke-opacity=\"0.35\" stroke-width=\"1\">");
        const int ticks = 4;
        for (int i = 0; i < ticks; i++)
        {
            double y = PadTop + (height - PadTop - PadBottom) * i / (ticks - 1);
            sb.Append("<line x1=\"").Append(N(PadLeft)).Append("\" y1=\"").Append(N(y))
              .Append("\" x2=\"").Append(N(RefWidth - PadRight)).Append("\" y2=\"").Append(N(y)).Append("\"/>");
        }
        sb.Append("</g>");
        return Root(sb.ToString(), height, preserveAspectRatio);
    }

    static void Domain(IReadOnlyList<ChartSeries> series, ChartKind kind, out double min, out double max)
    {
        min = double.PositiveInfinity;
        max = double.NegativeInfinity;
        foreach (var item in series)
        {
            if (item.Values is null) continue;
            foreach (double value in item.Values)
            {
                if (double.IsNaN(value) || double.IsInfinity(value)) continue;
                if (value < min) min = value;
                if (value > max) max = value;
            }
        }
        if (double.IsInfinity(min) || double.IsInfinity(max)) { min = 0; max = 1; }
        if (kind == ChartKind.Bar || kind == ChartKind.Area)
        {
            if (min > 0) min = 0;
            if (max < 0) max = 0;
        }
        double span = max - min;
        if (!(span > 1e-9)) { min -= 1; max += 1; span = 2; }
        min -= span * 0.08;
        max += span * 0.08;
    }

    static double XAt(int index, int count)
    {
        double plotWidth = RefWidth - PadLeft - PadRight;
        if (count <= 1) return PadLeft + plotWidth / 2;
        return PadLeft + plotWidth * index / (count - 1);
    }

    static double YAt(double value, double min, double max, float height)
    {
        double plotHeight = height - PadTop - PadBottom;
        return PadTop + (max - value) / (max - min) * plotHeight;
    }

    static string Points(IReadOnlyList<double> values, int count, double min, double max, float height)
    {
        var sb = new StringBuilder();
        int n = Math.Min(count, values.Count);
        for (int i = 0; i < n; i++)
        {
            if (sb.Length > 0) sb.Append(' ');
            double x = XAt(i, count);
            double y = YAt(values[i], min, max, height);
            sb.Append(N(x)).Append(',').Append(N(y));
        }
        return sb.ToString();
    }

    static string LineMarkup(ChartSeries series, int count, double min, double max, float height,
        string? preserveAspectRatio)
    {
        var values = series.Values;
        if (values is null || values.Count == 0 || count <= 0) return Root("", height, preserveAspectRatio);
        var points = Points(values, count, min, max, height);
        var sb = new StringBuilder();
        sb.Append("<polyline points=\"").Append(points)
          .Append("\" fill=\"none\" stroke=\"currentColor\" stroke-width=\"2\" stroke-linejoin=\"round\" stroke-linecap=\"round\"/>");
        sb.Append("<g fill=\"currentColor\">");
        int n = Math.Min(count, values.Count);
        for (int i = 0; i < n; i++)
            sb.Append("<circle cx=\"").Append(N(XAt(i, count))).Append("\" cy=\"")
              .Append(N(YAt(values[i], min, max, height))).Append("\" r=\"2.5\"/>");
        sb.Append("</g>");
        return Root(sb.ToString(), height, preserveAspectRatio);
    }

    static string BarMarkup(ChartSeries series, int seriesCount, int seriesIndex, int count, double min, double max,
        float height, string? preserveAspectRatio)
    {
        var values = series.Values;
        if (values is null || values.Count == 0 || count <= 0) return Root("", height, preserveAspectRatio);
        double plotWidth = RefWidth - PadLeft - PadRight;
        double band = plotWidth / Math.Max(1, count);
        double inner = band * 0.72;
        double barWidth = inner / Math.Max(1, seriesCount);
        double zero = YAt(Math.Max(min, Math.Min(0, max)), min, max, height);
        var sb = new StringBuilder("<g fill=\"currentColor\">");
        int n = Math.Min(count, values.Count);
        for (int i = 0; i < n; i++)
        {
            double value = values[i];
            double y = YAt(value, min, max, height);
            double top = Math.Min(y, zero);
            double h = Math.Abs(zero - y);
            double x = PadLeft + band * i + (band - inner) / 2 + barWidth * seriesIndex;
            sb.Append("<rect x=\"").Append(N(x)).Append("\" y=\"").Append(N(top))
              .Append("\" width=\"").Append(N(Math.Max(0.5, barWidth - 1)))
              .Append("\" height=\"").Append(N(h)).Append("\" rx=\"2\"/>");
        }
        sb.Append("</g>");
        return Root(sb.ToString(), height, preserveAspectRatio);
    }

    static string AreaMarkup(ChartSeries series, int count, double min, double max, float height,
        string? preserveAspectRatio)
    {
        var values = series.Values;
        if (values is null || values.Count == 0 || count <= 0) return Root("", height, preserveAspectRatio);
        double baseY = YAt(Math.Max(min, Math.Min(0, max)), min, max, height);
        var sb = new StringBuilder("<polygon points=\"");
        int n = Math.Min(count, values.Count);
        for (int i = 0; i < n; i++)
            sb.Append(N(XAt(i, count))).Append(',').Append(N(YAt(values[i], min, max, height))).Append(' ');
        sb.Append(N(XAt(n - 1, count))).Append(',').Append(N(baseY)).Append(' ');
        sb.Append(N(XAt(0, count))).Append(',').Append(N(baseY))
          .Append("\" fill=\"currentColor\" fill-opacity=\"0.22\"/>");
        sb.Append("<polyline points=\"").Append(Points(values, count, min, max, height))
          .Append("\" fill=\"none\" stroke=\"currentColor\" stroke-width=\"2\" stroke-linejoin=\"round\" stroke-linecap=\"round\"/>");
        return Root(sb.ToString(), height, preserveAspectRatio);
    }

    /// <summary>Values used by a pie/donut (the first series only, matching Recharts' pie data shape).</summary>
    internal static IReadOnlyList<double> PieValues(IReadOnlyList<ChartSeries> series)
    {
        if (series.Count == 0 || series[0].Values is not { } first) return Array.Empty<double>();
        var values = new double[first.Count];
        for (int i = 0; i < first.Count; i++) values[i] = first[i];
        return values;
    }

    /// <summary>Center and radius of the pie in SVG viewBox units.</summary>
    internal readonly struct PieGeometry
    {
        public PieGeometry(double cx, double cy, double radius)
        {
            Cx = cx;
            Cy = cy;
            Radius = radius;
        }

        public double Cx { get; }
        public double Cy { get; }
        public double Radius { get; }
    }

    internal static PieGeometry PieGeometryOf(float height) =>
        new(RefWidth / 2, height / 2, Math.Max(1, Math.Min(RefWidth, height) / 2 - 12));

    internal static double PositiveTotal(IReadOnlyList<double> values)
    {
        double total = 0;
        foreach (double value in values)
            if (!double.IsNaN(value) && !double.IsInfinity(value) && value > 0) total += value;
        return total;
    }

    /// <summary>
    /// Maps a plot-local pointer position to the index of the pie slice under it, or <c>null</c> outside the pie
    /// radius (or when there is no positive data). The angle convention matches <see cref="BuildPie"/>: slices
    /// start at the top (−90°) and sweep clockwise.
    /// </summary>
    internal static int? PieHit(double localX, double localY, double boxWidth, double boxHeight,
        IReadOnlyList<double> values, float height)
    {
        if (!(boxWidth > 0) || !(boxHeight > 0) || values.Count == 0) return null;
        double total = PositiveTotal(values);
        if (!(total > 0)) return null;

        var geometry = PieGeometryOf(height);
        // The SVG stretches the viewBox to the plot (preserveAspectRatio "none"), so undo that scale first.
        double x = localX * RefWidth / boxWidth;
        double y = localY * height / boxHeight;
        double dx = x - geometry.Cx;
        double dy = y - geometry.Cy;
        if (dx * dx + dy * dy > geometry.Radius * geometry.Radius) return null;

        double delta = Math.Atan2(dy, dx) + Math.PI / 2;
        delta %= Math.PI * 2;
        if (delta < 0) delta += Math.PI * 2;

        double cursor = 0;
        for (int i = 0; i < values.Count; i++)
        {
            double value = values[i];
            if (double.IsNaN(value) || double.IsInfinity(value) || value <= 0) continue;
            double sweep = value / total * Math.PI * 2;
            if (delta < cursor + sweep) return i;
            cursor += sweep;
        }
        return null;
    }

    /// <summary>ViewBox coordinates of the hovered slice's mid-angle, used to anchor the pie tooltip.</summary>
    internal static (double X, double Y) PieAnchor(IReadOnlyList<double> values, int index, PieGeometry geometry)
    {
        double total = PositiveTotal(values);
        double angle = -Math.PI / 2;
        for (int i = 0; i < values.Count && i <= index; i++)
        {
            double value = values[i];
            if (double.IsNaN(value) || double.IsInfinity(value) || value <= 0) continue;
            double sweep = total > 0 ? value / total * Math.PI * 2 : 0;
            if (i == index)
            {
                double mid = angle + sweep / 2;
                double radius = geometry.Radius * 0.6;
                return (geometry.Cx + radius * Math.Cos(mid), geometry.Cy + radius * Math.Sin(mid));
            }
            angle += sweep;
        }
        return (geometry.Cx, geometry.Cy);
    }

    static void BuildPie(IReadOnlyList<ChartSeries> series, IReadOnlyList<string> labels,
        IReadOnlyDictionary<string, ChartConfig>? config, float height, string? preserveAspectRatio,
        List<UISvg> layers)
    {
        var values = PieValues(series);
        double total = PositiveTotal(values);

        var geometry = PieGeometryOf(height);
        double cx = geometry.Cx;
        double cy = geometry.Cy;
        double radius = geometry.Radius;
        if (total <= 0)
        {
            layers.Add(Layer(Root("", height, preserveAspectRatio), Palette[0]));
            return;
        }

        double angle = -Math.PI / 2;
        for (int i = 0; i < values.Count; i++)
        {
            double value = values[i];
            if (double.IsNaN(value) || double.IsInfinity(value) || value <= 0) continue;
            double sweep = value / total * Math.PI * 2;
            string name = i < labels.Count ? labels[i] : (i + 1).ToString(CultureInfo.InvariantCulture);
            string content;
            if (values.Count == 1 || sweep >= Math.PI * 2 - 1e-6)
            {
                content = "<circle cx=\"" + N(cx) + "\" cy=\"" + N(cy) + "\" r=\"" + N(radius) +
                          "\" fill=\"currentColor\"/>";
            }
            else
            {
                double x1 = cx + radius * Math.Cos(angle);
                double y1 = cy + radius * Math.Sin(angle);
                double end = angle + sweep;
                double x2 = cx + radius * Math.Cos(end);
                double y2 = cy + radius * Math.Sin(end);
                int large = sweep > Math.PI ? 1 : 0;
                content = "<path d=\"M " + N(cx) + " " + N(cy) + " L " + N(x1) + " " + N(y1) +
                          " A " + N(radius) + " " + N(radius) + " 0 " + large + " 1 " + N(x2) + " " + N(y2) +
                          " Z\" fill=\"currentColor\"/>";
            }
            layers.Add(Layer(Root(content, height, preserveAspectRatio), CategoryColor(name, i, config)));
            angle += sweep;
        }
    }

    internal static string Summary(ChartKind kind, IReadOnlyList<LegendEntry> entries)
    {
        string name = kind switch
        {
            ChartKind.Bar => "Bar chart",
            ChartKind.Area => "Area chart",
            ChartKind.Pie => "Pie chart",
            _ => "Line chart",
        };
        int count = entries.Count;
        if (count == 0) return name + " with no data.";
        var names = new StringBuilder();
        for (int i = 0; i < count; i++)
        {
            if (i > 0) names.Append(", ");
            names.Append(entries[i].Label);
        }
        string noun = kind == ChartKind.Pie ? "categories" : "series";
        return name + " with " + count.ToString(CultureInfo.InvariantCulture) + " " + noun + ": " + names + ".";
    }
}
