using System;
using System.Text.RegularExpressions;
using AwAUI.Tailwind.Internal;

namespace AwAUI.Tailwind.Merge;

/// <summary>tailwind-merge v3 的值校验器（移植自 <c>validators.ts</c>）。</summary>
internal static class MergeValidators
{
    const RegexOptions Js = RegexOptions.ECMAScript | RegexOptions.CultureInvariant;

    static readonly Regex s_arbitraryValue = new(@"^\[(?:(\w[\w-]*):)?(.+)\]$", Js | RegexOptions.IgnoreCase);
    static readonly Regex s_arbitraryVariable = new(@"^\((?:(\w[\w-]*):)?(.+)\)$", Js | RegexOptions.IgnoreCase);
    static readonly Regex s_fraction = new(@"^\d+(?:\.\d+)?\/\d+(?:\.\d+)?$", Js);
    static readonly Regex s_tshirt = new(@"^(\d+(\.\d+)?)?(xs|sm|md|lg|xl)$", Js);
    static readonly Regex s_lengthUnit = new(@"\d+(%|px|r?em|[sdl]?v([hwib]|min|max)|pt|pc|in|cm|mm|cap|ch|ex|r?lh|cq(w|h|i|b|min|max))|\b(calc|min|max|clamp)\(.+\)|^0$", Js);
    static readonly Regex s_colorFunction = new(@"^(rgba?|hsla?|hwb|(ok)?(lab|lch)|color-mix|color|light-dark)\(.+\)$", Js);
    static readonly Regex s_shadow = new(@"^(inset_)?-?((\d+)?\.?(\d+)[a-z]+|0)_-?((\d+)?\.?(\d+)[a-z]+|0)", Js);
    static readonly Regex s_image = new(@"^(url|image|image-set|cross-fade|element|(repeating-)?(linear|radial|conic)-gradient)\(.+\)$", Js);

    public static readonly Func<string, bool> IsFraction = v => s_fraction.IsMatch(v);
    public static readonly Func<string, bool> IsNumber = v => v.Length > 0 && !double.IsNaN(Internal.Js.Number(v));
    public static readonly Func<string, bool> IsInteger = v =>
    {
        if (v.Length == 0) return false;
        double n = Internal.Js.Number(v);
        return !double.IsNaN(n) && !double.IsInfinity(n) && n == Math.Floor(n);
    };
    public static readonly Func<string, bool> IsPercent = v => v.Length > 0 && v[v.Length - 1] == '%' && IsNumber(v.Substring(0, v.Length - 1));
    public static readonly Func<string, bool> IsTshirtSize = v => s_tshirt.IsMatch(v);
    public static readonly Func<string, bool> IsAny = _ => true;
    public static readonly Func<string, bool> IsAnyNonArbitrary = v => !s_arbitraryValue.IsMatch(v) && !s_arbitraryVariable.IsMatch(v);

    public static readonly Func<string, bool> IsNamedContainerQuery = v =>
        v.StartsWith("@container", StringComparison.Ordinal) &&
        ((v.CharAt(10) == '/' && v.Length > 11) ||
         (v.CharAt(11) == 's' && v.Length > 16 && string.CompareOrdinal(v, 10, "-size/", 0, 6) == 0) ||
         (v.CharAt(11) == 'n' && v.Length > 18 && string.CompareOrdinal(v, 10, "-normal/", 0, 8) == 0));

    public static readonly Func<string, bool> IsArbitraryValue = v => s_arbitraryValue.IsMatch(v);
    public static readonly Func<string, bool> IsArbitraryVariable = v => s_arbitraryVariable.IsMatch(v);

    static bool IsLengthOnly(string v) => s_lengthUnit.IsMatch(v) && !s_colorFunction.IsMatch(v);

    static bool ArbitraryValue(string value, Func<string, bool> testLabel, Func<string, bool> testValue)
    {
        var m = s_arbitraryValue.Match(value);
        if (!m.Success) return false;
        if (m.Groups[1].Success && m.Groups[1].Length > 0) return testLabel(m.Groups[1].Value);
        return testValue(m.Groups[2].Value);
    }

    static bool ArbitraryVariable(string value, Func<string, bool> testLabel, bool matchNoLabel = false)
    {
        var m = s_arbitraryVariable.Match(value);
        if (!m.Success) return false;
        if (m.Groups[1].Success && m.Groups[1].Length > 0) return testLabel(m.Groups[1].Value);
        return matchNoLabel;
    }

    static bool LabelPosition(string l) => l == "position" || l == "percentage";
    static bool LabelImage(string l) => l == "image" || l == "url";
    static bool LabelSize(string l) => l == "length" || l == "size" || l == "bg-size";
    static bool LabelLength(string l) => l == "length";
    static bool LabelNumber(string l) => l == "number";
    static bool LabelFamilyName(string l) => l == "family-name";
    static bool LabelWeight(string l) => l == "number" || l == "weight";
    static bool LabelShadow(string l) => l == "shadow";
    static bool Never(string _) => false;

    public static readonly Func<string, bool> IsArbitrarySize = v => ArbitraryValue(v, LabelSize, Never);
    public static readonly Func<string, bool> IsArbitraryLength = v => ArbitraryValue(v, LabelLength, IsLengthOnly);
    public static readonly Func<string, bool> IsArbitraryNumber = v => ArbitraryValue(v, LabelNumber, IsNumber);
    public static readonly Func<string, bool> IsArbitraryWeight = v => ArbitraryValue(v, LabelWeight, IsAny);
    public static readonly Func<string, bool> IsArbitraryFamilyName = v => ArbitraryValue(v, LabelFamilyName, Never);
    public static readonly Func<string, bool> IsArbitraryPosition = v => ArbitraryValue(v, LabelPosition, Never);
    public static readonly Func<string, bool> IsArbitraryImage = v => ArbitraryValue(v, LabelImage, x => s_image.IsMatch(x));
    public static readonly Func<string, bool> IsArbitraryShadow = v => ArbitraryValue(v, LabelShadow, x => s_shadow.IsMatch(x));
    public static readonly Func<string, bool> IsArbitraryVariableLength = v => ArbitraryVariable(v, LabelLength);
    public static readonly Func<string, bool> IsArbitraryVariableFamilyName = v => ArbitraryVariable(v, LabelFamilyName);
    public static readonly Func<string, bool> IsArbitraryVariablePosition = v => ArbitraryVariable(v, LabelPosition);
    public static readonly Func<string, bool> IsArbitraryVariableSize = v => ArbitraryVariable(v, LabelSize);
    public static readonly Func<string, bool> IsArbitraryVariableImage = v => ArbitraryVariable(v, LabelImage);
    public static readonly Func<string, bool> IsArbitraryVariableShadow = v => ArbitraryVariable(v, LabelShadow, true);
    public static readonly Func<string, bool> IsArbitraryVariableWeight = v => ArbitraryVariable(v, LabelWeight, true);
}
