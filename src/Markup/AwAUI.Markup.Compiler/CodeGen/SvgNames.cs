using System;
using System.Collections.Generic;

namespace AwAUI.Markup.Compiler.CodeGen;

/// <summary>
/// 内联 svg 序列化时的名称修正，与 HTML 解析器处理 SVG 外来内容的规则一致（HTML 标准「adjust SVG attributes」与
/// 「any other start tag」中的 SVG 标签名表）：名称先转为 ASCII 小写，再把表中的名称换成 SVG（XML，区分大小写）的驼峰写法。
/// 因此 <c>viewbox</c>、<c>viewBox</c>、<c>VIEWBOX</c> 都输出为 <c>viewBox</c>，<c>Fill</c> 输出为 <c>fill</c>。
/// </summary>
internal static class SvgNames
{
    static readonly Dictionary<string, string> Attributes = Table(
        "attributeName", "attributeType", "baseFrequency", "baseProfile", "calcMode", "clipPathUnits", "diffuseConstant", "edgeMode",
        "filterUnits", "glyphRef", "gradientTransform", "gradientUnits", "kernelMatrix", "kernelUnitLength", "keyPoints", "keySplines",
        "keyTimes", "lengthAdjust", "limitingConeAngle", "markerHeight", "markerUnits", "markerWidth", "maskContentUnits", "maskUnits",
        "numOctaves", "pathLength", "patternContentUnits", "patternTransform", "patternUnits", "pointsAtX", "pointsAtY", "pointsAtZ",
        "preserveAlpha", "preserveAspectRatio", "primitiveUnits", "refX", "refY", "repeatCount", "repeatDur", "requiredExtensions",
        "requiredFeatures", "specularConstant", "specularExponent", "spreadMethod", "startOffset", "stdDeviation", "stitchTiles",
        "surfaceScale", "systemLanguage", "tableValues", "targetX", "targetY", "textLength", "viewBox", "viewTarget",
        "xChannelSelector", "yChannelSelector", "zoomAndPan");

    static readonly Dictionary<string, string> Elements = Table(
        "altGlyph", "altGlyphDef", "altGlyphItem", "animateColor", "animateMotion", "animateTransform", "clipPath", "feBlend",
        "feColorMatrix", "feComponentTransfer", "feComposite", "feConvolveMatrix", "feDiffuseLighting", "feDisplacementMap",
        "feDistantLight", "feDropShadow", "feFlood", "feFuncA", "feFuncB", "feFuncG", "feFuncR", "feGaussianBlur", "feImage", "feMerge",
        "feMergeNode", "feMorphology", "feOffset", "fePointLight", "feSpecularLighting", "feSpotLight", "feTile", "feTurbulence",
        "foreignObject", "glyphRef", "linearGradient", "radialGradient", "textPath");

    /// <summary>SVG 属性名（小写后按表修正）。</summary>
    public static string Attribute(string name) => Adjust(Attributes, name);

    /// <summary>SVG 元素名（小写后按表修正）。</summary>
    public static string Element(string name) => Adjust(Elements, name);

    static string Adjust(Dictionary<string, string> table, string name)
    {
        string lower = ToAsciiLower(name);
        return table.TryGetValue(lower, out var svg) ? svg : lower;
    }

    static string ToAsciiLower(string s)
    {
        int i = 0;
        while (i < s.Length && !(s[i] >= 'A' && s[i] <= 'Z')) i++;
        if (i == s.Length) return s;
        var chars = s.ToCharArray();
        for (; i < chars.Length; i++)
            if (chars[i] >= 'A' && chars[i] <= 'Z') chars[i] = (char)(chars[i] + 32);
        return new string(chars);
    }

    static Dictionary<string, string> Table(params string[] names)
    {
        var d = new Dictionary<string, string>(names.Length, StringComparer.Ordinal);
        foreach (var n in names) d[ToAsciiLower(n)] = n;
        return d;
    }
}
