using System;
using System.Collections.Generic;

namespace AwAUI.Svg.Model;

/// <summary>支持的样式属性（既可作表现属性，也可写在 style="" 与 &lt;style&gt; 中）。</summary>
internal enum SvgProperty : byte
{
    Fill, FillOpacity, FillRule,
    Stroke, StrokeWidth, StrokeOpacity, StrokeLinecap, StrokeLinejoin, StrokeMiterlimit, StrokeDasharray, StrokeDashoffset,
    Opacity, Color, Display, Visibility, Overflow,
    ClipPath, ClipRule, StopColor, StopOpacity, FontSize, PaintOrder, ShapeRendering, ImageRendering,
    // 只为诊断而识别（不支持）
    Mask, Filter, MarkerStart, MarkerMid, MarkerEnd, Marker,
}

internal static class SvgProperties
{
    public const int Count = (int)SvgProperty.Marker + 1;

    static readonly Dictionary<string, SvgProperty> ByName = new(StringComparer.Ordinal)
    {
        ["fill"] = SvgProperty.Fill,
        ["fill-opacity"] = SvgProperty.FillOpacity,
        ["fill-rule"] = SvgProperty.FillRule,
        ["stroke"] = SvgProperty.Stroke,
        ["stroke-width"] = SvgProperty.StrokeWidth,
        ["stroke-opacity"] = SvgProperty.StrokeOpacity,
        ["stroke-linecap"] = SvgProperty.StrokeLinecap,
        ["stroke-linejoin"] = SvgProperty.StrokeLinejoin,
        ["stroke-miterlimit"] = SvgProperty.StrokeMiterlimit,
        ["stroke-dasharray"] = SvgProperty.StrokeDasharray,
        ["stroke-dashoffset"] = SvgProperty.StrokeDashoffset,
        ["opacity"] = SvgProperty.Opacity,
        ["color"] = SvgProperty.Color,
        ["display"] = SvgProperty.Display,
        ["visibility"] = SvgProperty.Visibility,
        ["overflow"] = SvgProperty.Overflow,
        ["clip-path"] = SvgProperty.ClipPath,
        ["clip-rule"] = SvgProperty.ClipRule,
        ["stop-color"] = SvgProperty.StopColor,
        ["stop-opacity"] = SvgProperty.StopOpacity,
        ["font-size"] = SvgProperty.FontSize,
        ["paint-order"] = SvgProperty.PaintOrder,
        ["shape-rendering"] = SvgProperty.ShapeRendering,
        ["image-rendering"] = SvgProperty.ImageRendering,
        ["mask"] = SvgProperty.Mask,
        ["filter"] = SvgProperty.Filter,
        ["marker-start"] = SvgProperty.MarkerStart,
        ["marker-mid"] = SvgProperty.MarkerMid,
        ["marker-end"] = SvgProperty.MarkerEnd,
        ["marker"] = SvgProperty.Marker,
    };

    static readonly string[] Names = BuildNames();

    static string[] BuildNames()
    {
        var names = new string[Count];
        foreach (var kv in ByName) names[(int)kv.Value] = kv.Key;
        return names;
    }

    public static bool TryGet(string name, out SvgProperty property) => ByName.TryGetValue(name, out property);

    public static string NameOf(SvgProperty p) => Names[(int)p];

    /// <summary>可继承的属性（SVG 2 属性索引）。</summary>
    public static bool IsInherited(SvgProperty p) => p switch
    {
        SvgProperty.Opacity or SvgProperty.Display or SvgProperty.Overflow or SvgProperty.ClipPath or
        SvgProperty.StopColor or SvgProperty.StopOpacity or SvgProperty.Mask or SvgProperty.Filter => false,
        _ => true,
    };
}

/// <summary>一个元素上的已声明值（层叠后的胜出者）。</summary>
internal sealed class DeclaredStyle
{
    readonly string?[] _values = new string?[SvgProperties.Count];

    public string? this[SvgProperty p] => _values[(int)p];

    public void Set(SvgProperty p, string value) => _values[(int)p] = value;

    public bool IsEmpty
    {
        get
        {
            foreach (var v in _values) if (v != null) return false;
            return true;
        }
    }
}
