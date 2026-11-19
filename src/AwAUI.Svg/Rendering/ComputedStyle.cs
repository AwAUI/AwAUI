using System;
using System.Collections.Generic;
using AwAUI.Graphics;
using AwAUI.Svg.Model;
using AwAUI.Svg.Values;

namespace AwAUI.Svg.Rendering;

internal enum PaintKind : byte { None, Color, CurrentColor, Url }

/// <summary>fill / stroke 的值：none、颜色、currentColor，或 url(#id) 加可选的回退。</summary>
internal readonly struct SvgPaint
{
    public SvgPaint(PaintKind kind, Color color = default, string? url = null, PaintKind fallbackKind = PaintKind.None, Color fallbackColor = default)
    {
        Kind = kind;
        Color = color;
        Url = url;
        FallbackKind = fallbackKind;
        FallbackColor = fallbackColor;
    }

    public PaintKind Kind { get; }
    public Color Color { get; }
    public string? Url { get; }
    public PaintKind FallbackKind { get; }
    public Color FallbackColor { get; }

    public static SvgPaint None => new(PaintKind.None);

    public static bool TryParse(string text, out SvgPaint paint)
    {
        paint = None;
        var s = text.Trim();
        if (s.StartsWith("url(", StringComparison.OrdinalIgnoreCase))
        {
            if (!SvgRef.TryParseUrl(s, out var id, out int end)) return false;
            var rest = s.Substring(end).Trim();
            if (rest.Length == 0) { paint = new SvgPaint(PaintKind.Url, url: id); return true; }
            if (!TryParseSimple(rest, out var fb)) return false;
            paint = new SvgPaint(PaintKind.Url, url: id, fallbackKind: fb.Kind, fallbackColor: fb.Color);
            return true;
        }
        return TryParseSimple(s, out paint);
    }

    static bool TryParseSimple(string s, out SvgPaint paint)
    {
        paint = None;
        if (s.Equals("none", StringComparison.OrdinalIgnoreCase)) return true;
        if (s.Equals("currentColor", StringComparison.OrdinalIgnoreCase)) { paint = new SvgPaint(PaintKind.CurrentColor); return true; }
        // context-fill / context-stroke 只对 marker 与 use 有意义，此处按 none 处理
        if (s.Equals("context-fill", StringComparison.OrdinalIgnoreCase) || s.Equals("context-stroke", StringComparison.OrdinalIgnoreCase))
            return true;
        if (SvgColor.TryParse(s, out var c)) { paint = new SvgPaint(PaintKind.Color, c); return true; }
        return false;
    }
}

/// <summary>url(#id) 与 "#id" 引用的解析。外部文件引用不支持。</summary>
internal static class SvgRef
{
    /// <summary>解析 url(#id)、url('#id')、url("#id")。<paramref name="end"/> 为 ')' 之后的位置。</summary>
    public static bool TryParseUrl(string s, out string id, out int end)
    {
        id = string.Empty;
        end = 0;
        if (!s.StartsWith("url(", StringComparison.OrdinalIgnoreCase)) return false;
        int close = s.IndexOf(')', 4);
        if (close < 0) return false;
        var inner = s.Substring(4, close - 4).Trim();
        if (inner.Length >= 2 && (inner[0] == '"' || inner[0] == '\'') && inner[inner.Length - 1] == inner[0])
            inner = inner.Substring(1, inner.Length - 2).Trim();
        end = close + 1;
        if (inner.Length < 2 || inner[0] != '#') return false;
        id = inner.Substring(1);
        return true;
    }

    /// <summary>href 属性形式的 "#id"。</summary>
    public static string? FragmentId(string? href)
    {
        if (href == null) return null;
        var s = href.Trim();
        return s.Length >= 2 && s[0] == '#' ? s.Substring(1) : null;
    }
}

/// <summary>
/// 计算样式。可继承属性从父样式复制，其余取初始值；然后应用元素的已声明值。
/// 不可变使用：计算完成后不再修改，可被多个子元素共享为父样式。
/// </summary>
internal sealed class ComputedStyle
{
    // 可继承
    public SvgPaint Fill = new(PaintKind.Color, Color.Black);
    public float FillOpacity = 1;
    public FillRule FillRule = FillRule.NonZero;
    public SvgPaint Stroke = SvgPaint.None;
    public float StrokeWidth = 1;
    public float StrokeOpacity = 1;
    public StrokeCap StrokeCap = StrokeCap.Butt;
    public StrokeJoin StrokeJoin = StrokeJoin.Miter;
    public float StrokeMiterLimit = 4;
    public float[]? StrokeDashes;
    public float StrokeDashOffset;
    /// <summary>color 属性；null 表示沿用调用方提供的 currentColor。</summary>
    public Color? CurrentColor;
    public bool Visible = true;
    public FillRule ClipRule = FillRule.NonZero;
    public float FontSize = 16;
    public bool StrokeFirst;
    public bool AntiAlias = true;
    /// <summary>image-rendering 为 pixelated / crisp-edges / optimizeSpeed：&lt;image&gt; 用最近邻采样。</summary>
    public bool PixelatedImages;

    // 不可继承
    public float Opacity = 1;
    public bool DisplayNone;
    public bool OverflowVisible;
    public string? ClipPath;
    public bool StopColorIsCurrent;
    public Color StopColor = Color.Black;
    public float StopOpacity = 1;
    public string? Mask;
    public string? Filter;
    public bool HasMarkers;

    public static ComputedStyle Initial { get; } = new();

    ComputedStyle InheritFrom()
    {
        return new ComputedStyle
        {
            Fill = Fill, FillOpacity = FillOpacity, FillRule = FillRule,
            Stroke = Stroke, StrokeWidth = StrokeWidth, StrokeOpacity = StrokeOpacity,
            StrokeCap = StrokeCap, StrokeJoin = StrokeJoin, StrokeMiterLimit = StrokeMiterLimit,
            StrokeDashes = StrokeDashes, StrokeDashOffset = StrokeDashOffset,
            CurrentColor = CurrentColor, Visible = Visible, ClipRule = ClipRule, FontSize = FontSize,
            StrokeFirst = StrokeFirst, AntiAlias = AntiAlias, PixelatedImages = PixelatedImages,
        };
    }

    /// <summary>计算 <paramref name="node"/> 的样式。<paramref name="viewport"/> 用于百分比长度。</summary>
    public static ComputedStyle Compute(ComputedStyle parent, SvgNode node, Size viewport, DiagnosticBag? diag)
    {
        var s = parent.InheritFrom();
        var decl = node.Style;
        if (decl == null) return s;

        // font-size 先算：其他属性的 em 依赖它
        if (decl[SvgProperty.FontSize] is { } fs) s.ApplyFontSize(fs, parent, viewport, node, diag);
        for (int i = 0; i < SvgProperties.Count; i++)
        {
            var p = (SvgProperty)i;
            if (p == SvgProperty.FontSize) continue;
            var v = decl[p];
            if (v == null) continue;
            var key = Keyword(v);
            if (key == "inherit" || key == "unset" && SvgProperties.IsInherited(p))
            {
                s.CopyFrom(parent, p);
                continue;
            }
            if (key == "initial" || key == "unset")
            {
                s.CopyFrom(Initial, p);
                continue;
            }
            if (!s.Apply(p, v, viewport))
                diag?.Add(SvgDiagnosticCode.InvalidValue, $"Invalid value '{Truncate(v)}' for property '{Name(p)}' was ignored.", node.Offset);
        }
        return s;
    }

    static string Truncate(string s) => s.Length > 40 ? s.Substring(0, 40) + "…" : s;

    static string Name(SvgProperty p) => SvgProperties.NameOf(p);

    static string Keyword(string v)
    {
        var t = v.Trim();
        return t.Length <= 8 ? t.ToLowerInvariant() : string.Empty;
    }

    void ApplyFontSize(string v, ComputedStyle parent, Size viewport, SvgNode node, DiagnosticBag? diag)
    {
        var key = Keyword(v);
        if (key is "inherit" or "unset") { FontSize = parent.FontSize; return; }
        if (key == "initial") { FontSize = 16; return; }
        if (SvgLength.TryParse(v, out var len) && len.Value >= 0)
        {
            FontSize = len.Unit == SvgUnit.Percent ? parent.FontSize * len.Value / 100f : len.Resolve(viewport, LengthAxis.Other, parent.FontSize);
            return;
        }
        diag?.Add(SvgDiagnosticCode.InvalidValue, $"Invalid font-size '{Truncate(v)}' was ignored.", node.Offset);
    }

    void CopyFrom(ComputedStyle o, SvgProperty p)
    {
        switch (p)
        {
            case SvgProperty.Fill: Fill = o.Fill; break;
            case SvgProperty.FillOpacity: FillOpacity = o.FillOpacity; break;
            case SvgProperty.FillRule: FillRule = o.FillRule; break;
            case SvgProperty.Stroke: Stroke = o.Stroke; break;
            case SvgProperty.StrokeWidth: StrokeWidth = o.StrokeWidth; break;
            case SvgProperty.StrokeOpacity: StrokeOpacity = o.StrokeOpacity; break;
            case SvgProperty.StrokeLinecap: StrokeCap = o.StrokeCap; break;
            case SvgProperty.StrokeLinejoin: StrokeJoin = o.StrokeJoin; break;
            case SvgProperty.StrokeMiterlimit: StrokeMiterLimit = o.StrokeMiterLimit; break;
            case SvgProperty.StrokeDasharray: StrokeDashes = o.StrokeDashes; break;
            case SvgProperty.StrokeDashoffset: StrokeDashOffset = o.StrokeDashOffset; break;
            case SvgProperty.Opacity: Opacity = o.Opacity; break;
            case SvgProperty.Color: CurrentColor = o.CurrentColor; break;
            case SvgProperty.Display: DisplayNone = o.DisplayNone; break;
            case SvgProperty.Visibility: Visible = o.Visible; break;
            case SvgProperty.Overflow: OverflowVisible = o.OverflowVisible; break;
            case SvgProperty.ClipPath: ClipPath = o.ClipPath; break;
            case SvgProperty.ClipRule: ClipRule = o.ClipRule; break;
            case SvgProperty.StopColor: StopColor = o.StopColor; StopColorIsCurrent = o.StopColorIsCurrent; break;
            case SvgProperty.StopOpacity: StopOpacity = o.StopOpacity; break;
            case SvgProperty.PaintOrder: StrokeFirst = o.StrokeFirst; break;
            case SvgProperty.ShapeRendering: AntiAlias = o.AntiAlias; break;
            case SvgProperty.ImageRendering: PixelatedImages = o.PixelatedImages; break;
            case SvgProperty.Mask: Mask = o.Mask; break;
            case SvgProperty.Filter: Filter = o.Filter; break;
            case SvgProperty.MarkerStart: case SvgProperty.MarkerMid: case SvgProperty.MarkerEnd: case SvgProperty.Marker:
                HasMarkers = o.HasMarkers; break;
        }
    }

    /// <summary>
    /// 声明在语法上是否有效（CSS 关键字 inherit / initial / unset 总是有效）。CSS 声明（&lt;style&gt; 与 style=""）
    /// 在层叠前就要丢弃无效值，否则一条写错的规则会盖掉有效的表现属性。
    /// </summary>
    public static bool IsValidDeclaration(SvgProperty p, string value)
    {
        var key = Keyword(value);
        if (key is "inherit" or "initial" or "unset") return true;
        if (p == SvgProperty.FontSize) return SvgLength.TryParse(value, out var len) && len.Value >= 0;
        return new ComputedStyle().Apply(p, value, default);
    }

    /// <summary>应用一个已声明值；值无效时返回 false（保持继承值或初始值）。</summary>
    bool Apply(SvgProperty p, string raw, Size viewport)
    {
        var v = raw.Trim();
        switch (p)
        {
            case SvgProperty.Fill:
                if (!SvgPaint.TryParse(v, out var fill)) return false;
                Fill = fill;
                return true;
            case SvgProperty.Stroke:
                if (!SvgPaint.TryParse(v, out var stroke)) return false;
                Stroke = stroke;
                return true;
            case SvgProperty.FillOpacity: return TryOpacity(v, ref FillOpacity);
            case SvgProperty.StrokeOpacity: return TryOpacity(v, ref StrokeOpacity);
            case SvgProperty.Opacity: return TryOpacity(v, ref Opacity);
            case SvgProperty.StopOpacity: return TryOpacity(v, ref StopOpacity);
            case SvgProperty.FillRule: return TryFillRule(v, ref FillRule);
            case SvgProperty.ClipRule: return TryFillRule(v, ref ClipRule);
            case SvgProperty.StrokeWidth:
            {
                if (!SvgLength.TryParse(v, out var len) || len.Value < 0) return false;
                StrokeWidth = len.Resolve(viewport, LengthAxis.Other, FontSize);
                return true;
            }
            case SvgProperty.StrokeLinecap:
                switch (v.ToLowerInvariant())
                {
                    case "butt": StrokeCap = StrokeCap.Butt; return true;
                    case "round": StrokeCap = StrokeCap.Round; return true;
                    case "square": StrokeCap = StrokeCap.Square; return true;
                    default: return false;
                }
            case SvgProperty.StrokeLinejoin:
                switch (v.ToLowerInvariant())
                {
                    case "miter": case "miter-clip": case "arcs": StrokeJoin = StrokeJoin.Miter; return true;
                    case "round": StrokeJoin = StrokeJoin.Round; return true;
                    case "bevel": StrokeJoin = StrokeJoin.Bevel; return true;
                    default: return false;
                }
            case SvgProperty.StrokeMiterlimit:
                if (!SvgNumber.TryParse(v, out float ml) || ml < 1) return false;
                StrokeMiterLimit = ml;
                return true;
            case SvgProperty.StrokeDasharray: return TryDashArray(v, viewport);
            case SvgProperty.StrokeDashoffset:
            {
                if (!SvgLength.TryParse(v, out var len)) return false;
                StrokeDashOffset = len.Resolve(viewport, LengthAxis.Other, FontSize);
                return true;
            }
            case SvgProperty.Color:
                if (v.Equals("currentColor", StringComparison.OrdinalIgnoreCase)) return true; // 等同于 inherit
                if (!SvgColor.TryParse(v, out var color)) return false;
                CurrentColor = color;
                return true;
            case SvgProperty.Display:
                DisplayNone = v.Equals("none", StringComparison.OrdinalIgnoreCase);
                return true;
            case SvgProperty.Visibility:
                switch (v.ToLowerInvariant())
                {
                    case "visible": Visible = true; return true;
                    case "hidden": case "collapse": Visible = false; return true;
                    default: return false;
                }
            case SvgProperty.Overflow:
                switch (v.ToLowerInvariant())
                {
                    case "visible": case "auto": OverflowVisible = true; return true;
                    case "hidden": case "scroll": case "clip": OverflowVisible = false; return true;
                    default: return false;
                }
            case SvgProperty.ClipPath:
                ClipPath = v.Equals("none", StringComparison.OrdinalIgnoreCase) ? null : v;
                return true;
            case SvgProperty.StopColor:
                if (v.Equals("currentColor", StringComparison.OrdinalIgnoreCase)) { StopColorIsCurrent = true; return true; }
                if (!SvgColor.TryParse(v, out var sc)) return false;
                StopColor = sc;
                StopColorIsCurrent = false;
                return true;
            case SvgProperty.PaintOrder:
            {
                var t = v.ToLowerInvariant();
                if (t == "normal") { StrokeFirst = false; return true; }
                var parts = t.Split(new[] { ' ', '\t', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 0 || parts.Length > 3) return false;
                foreach (var part in parts) if (part is not ("fill" or "stroke" or "markers")) return false;
                int fi = Array.IndexOf(parts, "fill"), si = Array.IndexOf(parts, "stroke");
                // 省略的关键字按 fill → stroke → markers 的默认顺序补在后面
                StrokeFirst = si >= 0 && (fi < 0 || si < fi);
                return true;
            }
            case SvgProperty.ShapeRendering:
                switch (v.ToLowerInvariant())
                {
                    case "auto": case "geometricprecision": AntiAlias = true; return true;
                    case "crispedges": case "optimizespeed": AntiAlias = false; return true;
                    default: return false;
                }
            case SvgProperty.ImageRendering:
                switch (v.ToLowerInvariant())
                {
                    case "auto": case "optimizequality": case "smooth": case "high-quality": PixelatedImages = false; return true;
                    case "pixelated": case "crisp-edges": case "optimizespeed": PixelatedImages = true; return true;
                    default: return false;
                }
            case SvgProperty.Mask:
                Mask = v.Equals("none", StringComparison.OrdinalIgnoreCase) ? null : v;
                return true;
            case SvgProperty.Filter:
                Filter = v.Equals("none", StringComparison.OrdinalIgnoreCase) ? null : v;
                return true;
            case SvgProperty.MarkerStart: case SvgProperty.MarkerMid: case SvgProperty.MarkerEnd: case SvgProperty.Marker:
                HasMarkers = !v.Equals("none", StringComparison.OrdinalIgnoreCase);
                return true;
        }
        return false;
    }

    static bool TryOpacity(string v, ref float target)
    {
        if (!SvgNumber.TryParseNumberOrPercent(v, out float o)) return false;
        target = o < 0 ? 0 : o > 1 ? 1 : o;
        return true;
    }

    static bool TryFillRule(string v, ref FillRule target)
    {
        if (v.Equals("nonzero", StringComparison.OrdinalIgnoreCase)) { target = FillRule.NonZero; return true; }
        if (v.Equals("evenodd", StringComparison.OrdinalIgnoreCase)) { target = FillRule.EvenOdd; return true; }
        return false;
    }

    bool TryDashArray(string v, Size viewport)
    {
        if (v.Equals("none", StringComparison.OrdinalIgnoreCase)) { StrokeDashes = null; return true; }
        var s = v.AsSpan();
        int pos = 0;
        var list = new List<float>();
        SvgNumber.SkipWsp(s, ref pos);
        while (pos < s.Length)
        {
            if (list.Count >= 1024) return false;
            if (!SvgLength.TryScan(s, ref pos, out var len) || len.Value < 0) return false;
            list.Add(len.Resolve(viewport, LengthAxis.Other, FontSize));
            bool comma = SvgNumber.SkipCommaWsp(s, ref pos);
            if (comma && pos >= s.Length) return false;
        }
        if (list.Count == 0) return false;
        double sum = 0;
        foreach (var d in list) sum += d;
        StrokeDashes = sum > 0 ? list.ToArray() : null; // 全为 0 时按实线
        return true;
    }
}
