using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace AwAUI.Tailwind.Internal;

/// <summary>任意值的数据类型推断（移植自 Tailwind <c>infer-data-type.ts</c> 与 <c>is-color.ts</c>）。</summary>
internal static class DataTypes
{
    static readonly HashSet<string> s_namedColors = new(StringComparer.Ordinal)
    {
        "black", "silver", "gray", "white", "maroon", "red", "purple", "fuchsia", "green", "lime", "olive", "yellow", "navy", "blue", "teal", "aqua",
        "aliceblue", "antiquewhite", "aquamarine", "azure", "beige", "bisque", "blanchedalmond", "blueviolet", "brown", "burlywood", "cadetblue",
        "chartreuse", "chocolate", "coral", "cornflowerblue", "cornsilk", "crimson", "cyan", "darkblue", "darkcyan", "darkgoldenrod", "darkgray",
        "darkgreen", "darkgrey", "darkkhaki", "darkmagenta", "darkolivegreen", "darkorange", "darkorchid", "darkred", "darksalmon", "darkseagreen",
        "darkslateblue", "darkslategray", "darkslategrey", "darkturquoise", "darkviolet", "deeppink", "deepskyblue", "dimgray", "dimgrey",
        "dodgerblue", "firebrick", "floralwhite", "forestgreen", "gainsboro", "ghostwhite", "gold", "goldenrod", "greenyellow", "grey", "honeydew",
        "hotpink", "indianred", "indigo", "ivory", "khaki", "lavender", "lavenderblush", "lawngreen", "lemonchiffon", "lightblue", "lightcoral",
        "lightcyan", "lightgoldenrodyellow", "lightgray", "lightgreen", "lightgrey", "lightpink", "lightsalmon", "lightseagreen", "lightskyblue",
        "lightslategray", "lightslategrey", "lightsteelblue", "lightyellow", "limegreen", "linen", "magenta", "mediumaquamarine", "mediumblue",
        "mediumorchid", "mediumpurple", "mediumseagreen", "mediumslateblue", "mediumspringgreen", "mediumturquoise", "mediumvioletred",
        "midnightblue", "mintcream", "mistyrose", "moccasin", "navajowhite", "oldlace", "olivedrab", "orange", "orangered", "orchid",
        "palegoldenrod", "palegreen", "paleturquoise", "palevioletred", "papayawhip", "peachpuff", "peru", "pink", "plum", "powderblue",
        "rebeccapurple", "rosybrown", "royalblue", "saddlebrown", "salmon", "sandybrown", "seagreen", "seashell", "sienna", "skyblue", "slateblue",
        "slategray", "slategrey", "snow", "springgreen", "steelblue", "tan", "thistle", "tomato", "turquoise", "violet", "wheat", "whitesmoke",
        "yellowgreen", "transparent", "currentcolor", "canvas", "canvastext", "linktext", "visitedtext", "activetext", "buttonface", "buttontext",
        "buttonborder", "field", "fieldtext", "highlight", "highlighttext", "selecteditem", "selecteditemtext", "mark", "marktext", "graytext",
        "accentcolor", "accentcolortext",
    };

    static readonly Regex s_colorFunction = new(@"^(rgba?|hsla?|hwb|color|(ok)?(lab|lch)|light-dark|color-mix|--alpha)\(", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static bool IsNamedColor(string value) => s_namedColors.Contains(value.ToLowerInvariant());

    public static bool IsColor(string value) => (value.Length > 0 && value[0] == '#') || s_colorFunction.IsMatch(value) || IsNamedColor(value);

    static readonly string[] s_mathFunctions =
    {
        "calc", "min", "max", "clamp", "mod", "rem", "sin", "cos", "tan", "asin", "acos", "atan", "atan2", "pow", "sqrt", "hypot", "log", "exp", "round",
    };

    /// <summary>是否包含数学函数调用。</summary>
    public static bool HasMathFn(string value)
    {
        if (value.IndexOf('(') == -1) return false;
        foreach (var fn in s_mathFunctions)
        {
            if (value.IndexOf(fn + "(", StringComparison.Ordinal) >= 0) return true;
        }
        return false;
    }

    const string NumberPattern = @"[+-]?\d*\.?\d+(?:[eE][+-]?\d+)?";
    static readonly Regex s_number = new("^" + NumberPattern + "$", RegexOptions.CultureInvariant);
    static readonly Regex s_percentage = new("^" + NumberPattern + "%$", RegexOptions.CultureInvariant);
    static readonly Regex s_ratio = new("^" + NumberPattern + @"\s*/\s*" + NumberPattern + "$", RegexOptions.CultureInvariant);
    static readonly Regex s_length = new("^" + NumberPattern + "(cm|mm|Q|in|pc|pt|px|em|ex|ch|cap|ic|rem|rex|rch|rcap|ric|lh|rlh|vw|vh|vmin|vmax|vb|vi|svw|svh|svi|svb|svmin|svmax|lvw|lvh|lvi|lvb|lvmin|lvmax|dvw|dvh|dvi|dvb|dvmin|dvmax|cqw|cqh|cqi|cqb|cqmin|cqmax)$", RegexOptions.CultureInvariant);
    static readonly Regex s_spacingFn = new(@"^(--spacing)\(", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    static readonly Regex s_angle = new("^" + NumberPattern + "(deg|rad|grad|turn)$", RegexOptions.CultureInvariant);
    static readonly Regex s_vector = new("^" + NumberPattern + " +" + NumberPattern + " +" + NumberPattern + "$", RegexOptions.CultureInvariant);
    static readonly Regex s_url = new(@"^url\(.*\)$", RegexOptions.CultureInvariant | RegexOptions.Singleline);
    static readonly Regex s_imageFn = new(@"^(?:element|image|cross-fade|image-set)\(", RegexOptions.CultureInvariant);
    static readonly Regex s_gradient = new(@"^(repeating-)?(conic|linear|radial)-gradient\(", RegexOptions.CultureInvariant);

    public static bool IsNumber(string v) => s_number.IsMatch(v) || HasMathFn(v);
    public static bool IsPercentage(string v) => s_percentage.IsMatch(v) || HasMathFn(v);
    public static bool IsFraction(string v) => s_ratio.IsMatch(v) || HasMathFn(v);
    public static bool IsLength(string v) => s_length.IsMatch(v) || s_spacingFn.IsMatch(v) || HasMathFn(v);
    public static bool IsAngle(string v) => s_angle.IsMatch(v);
    public static bool IsVector(string v) => s_vector.IsMatch(v);
    public static bool IsUrl(string v) => s_url.IsMatch(v);

    static bool IsLineWidth(string v)
    {
        foreach (var part in Js.Segment(v, ' '))
        {
            if (!(IsLength(part) || IsNumber(part) || part == "thin" || part == "medium" || part == "thick")) return false;
        }
        return true;
    }

    static bool IsImage(string v)
    {
        int count = 0;
        foreach (var part in Js.Segment(v, ','))
        {
            if (part.StartsWith("var(", StringComparison.Ordinal)) continue;
            if (IsUrl(part) || s_gradient.IsMatch(part) || s_imageFn.IsMatch(part))
            {
                count++;
                continue;
            }
            return false;
        }
        return count > 0;
    }

    static bool IsGenericName(string v) => v is "serif" or "sans-serif" or "monospace" or "cursive" or "fantasy" or "system-ui" or "ui-serif" or "ui-sans-serif" or "ui-monospace" or "ui-rounded" or "math" or "emoji" or "fangsong";

    static bool IsFamilyName(string v)
    {
        int count = 0;
        foreach (var part in Js.Segment(v, ','))
        {
            if (part.Length > 0 && part[0] >= '0' && part[0] <= '9') return false;
            if (!part.StartsWith("var(", StringComparison.Ordinal)) count++;
        }
        return count > 0;
    }

    static bool IsAbsoluteSize(string v) => v is "xx-small" or "x-small" or "small" or "medium" or "large" or "x-large" or "xx-large" or "xxx-large";

    static bool IsRelativeSize(string v) => v is "larger" or "smaller";

    static bool IsPosition(string v)
    {
        int count = 0;
        foreach (var part in Js.Segment(v, ' '))
        {
            if (part is "center" or "top" or "right" or "bottom" or "left")
            {
                count++;
                continue;
            }
            if (part.StartsWith("var(", StringComparison.Ordinal)) continue;
            if (IsLength(part) || IsPercentage(part))
            {
                count++;
                continue;
            }
            return false;
        }
        return count > 0;
    }

    static bool IsBackgroundSize(string v)
    {
        int count = 0;
        foreach (var size in Js.Segment(v, ','))
        {
            if (size == "cover" || size == "contain")
            {
                count++;
                continue;
            }
            var values = Js.Segment(size, ' ');
            if (values.Count != 1 && values.Count != 2) return false;
            bool all = true;
            foreach (var x in values)
            {
                if (!(x == "auto" || IsLength(x) || IsPercentage(x)))
                {
                    all = false;
                    break;
                }
            }
            if (all) count++;
        }
        return count > 0;
    }

    static bool Check(string type, string value) => type switch
    {
        "color" => IsColor(value),
        "length" => IsLength(value),
        "percentage" => IsPercentage(value),
        "ratio" => IsFraction(value),
        "number" => IsNumber(value),
        "integer" => Js.IsPositiveInteger(value),
        "url" => IsUrl(value),
        "position" => IsPosition(value),
        "bg-size" => IsBackgroundSize(value),
        "line-width" => IsLineWidth(value),
        "image" => IsImage(value),
        "family-name" => IsFamilyName(value),
        "generic-name" => IsGenericName(value),
        "absolute-size" => IsAbsoluteSize(value),
        "relative-size" => IsRelativeSize(value),
        "angle" => IsAngle(value),
        "vector" => IsVector(value),
        _ => false,
    };

    /// <summary>按候选类型顺序推断任意值的数据类型；<c>var(...)</c> 开头的值无法推断，返回 null。</summary>
    public static string? Infer(string value, string[] types)
    {
        if (value.StartsWith("var(", StringComparison.Ordinal)) return null;
        foreach (var t in types)
        {
            if (Check(t, value)) return t;
        }
        return null;
    }

    /// <summary>在数学函数中的运算符两侧补空格（移植自 Tailwind <c>addWhitespaceAroundMathOperators</c>）。</summary>
    public static string AddWhitespaceAroundMathOperators(string input)
    {
        bool any = false;
        foreach (var fn in s_mathFunctions)
        {
            if (input.IndexOf(fn, StringComparison.Ordinal) >= 0)
            {
                any = true;
                break;
            }
        }
        if (!any) return input;
        var result = new StringBuilder(input.Length + 8);
        var formattable = new List<bool>();
        int valuePos = -1;
        int lastValuePos = -1;
        bool hasValuePos = false, hasLastValuePos = false;
        for (int i = 0; i < input.Length; i++)
        {
            char ch = input[i];
            if ((ch >= '0' && ch <= '9') || (hasValuePos && (ch == '%' || (ch >= 'a' && ch <= 'z') || (ch >= 'A' && ch <= 'Z'))))
            {
                valuePos = i;
                hasValuePos = true;
            }
            else
            {
                lastValuePos = valuePos;
                hasLastValuePos = hasValuePos;
                hasValuePos = false;
            }
            if (ch == '(')
            {
                result.Append(ch);
                int start = i;
                for (int j = i - 1; j >= 0; j--)
                {
                    char inner = input[j];
                    if (inner >= '0' && inner <= '9') start = j;
                    else if (inner >= 'a' && inner <= 'z') start = j;
                    else break;
                }
                string fn = input.Substring(start, i - start);
                if (Array.IndexOf(s_mathFunctions, fn) >= 0)
                {
                    formattable.Insert(0, true);
                    continue;
                }
                if (formattable.Count > 0 && formattable[0] && fn.Length == 0)
                {
                    formattable.Insert(0, true);
                    continue;
                }
                formattable.Insert(0, false);
                continue;
            }
            if (ch == ')')
            {
                result.Append(ch);
                if (formattable.Count > 0) formattable.RemoveAt(0);
            }
            else if (ch == ',' && formattable.Count > 0 && formattable[0])
            {
                result.Append(", ");
            }
            else if (ch == ' ' && formattable.Count > 0 && formattable[0] && result.Length > 0 && result[result.Length - 1] == ' ')
            {
                // 跳过重复空格
            }
            else if ((ch == '+' || ch == '*' || ch == '/' || ch == '-') && formattable.Count > 0 && formattable[0])
            {
                string trimmed = TrimEnd(result);
                char prev = trimmed.Length > 0 ? trimmed[trimmed.Length - 1] : '\0';
                char prevPrev = trimmed.Length > 1 ? trimmed[trimmed.Length - 2] : '\0';
                char next = i + 1 < input.Length ? input[i + 1] : '\0';
                if ((prev == 'e' || prev == 'E') && prevPrev >= '0' && prevPrev <= '9')
                {
                    result.Append(ch);
                    continue;
                }
                if (prev == '+' || prev == '*' || prev == '/' || prev == '-')
                {
                    result.Append(ch);
                    continue;
                }
                if (prev == '(' || prev == ',')
                {
                    result.Append(ch);
                    continue;
                }
                if (i > 0 && input[i - 1] == ' ')
                {
                    result.Append(ch).Append(' ');
                }
                else if ((prev >= '0' && prev <= '9') || (next >= '0' && next <= '9') || prev == ')' || next == '(' || next == '+' || next == '*' || next == '/' || next == '-'
                         || (hasLastValuePos && lastValuePos == i - 1))
                {
                    result.Append(' ').Append(ch).Append(' ');
                }
                else
                {
                    result.Append(ch);
                }
            }
            else
            {
                result.Append(ch);
            }
        }
        return result.ToString();
    }

    static string TrimEnd(StringBuilder sb)
    {
        int end = sb.Length;
        while (end > 0 && char.IsWhiteSpace(sb[end - 1])) end--;
        return sb.ToString(0, end);
    }
}
