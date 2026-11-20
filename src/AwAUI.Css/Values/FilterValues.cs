using System;
using System.Collections.Generic;
using System.Text;
using AwAUI.Css.Syntax;

namespace AwAUI.Css.Values;

/// <summary>滤镜函数的种类（Filter Effects 1）。</summary>
public enum CssFilterKind : byte
{
    Blur,
    Brightness,
    Contrast,
    DropShadow,
    Grayscale,
    HueRotate,
    Invert,
    Opacity,
    Saturate,
    Sepia,
    /// <summary><c>url()</c> 引用的 SVG 滤镜（只解析，不渲染）。</summary>
    Url,
}

/// <summary>
/// 一个滤镜函数的计算值。<see cref="Amount"/>：blur 为半径（px），hue-rotate 为度，其余为比例（100% = 1；grayscale、invert、
/// opacity、sepia 已钳制到 [0, 1]）。drop-shadow 的偏移与模糊在 <see cref="OffsetX"/>、<see cref="OffsetY"/>、<see cref="Amount"/>，颜色在 <see cref="Color"/>。
/// </summary>
public readonly struct CssFilterFunction : IEquatable<CssFilterFunction>
{
    public CssFilterFunction(CssFilterKind kind, float amount, float offsetX = 0, float offsetY = 0, CssColor color = default, string? url = null)
    {
        Kind = kind;
        Amount = amount;
        OffsetX = offsetX;
        OffsetY = offsetY;
        Color = color;
        Url = url;
    }

    public CssFilterKind Kind { get; }
    public float Amount { get; }
    public float OffsetX { get; }
    public float OffsetY { get; }

    /// <summary>drop-shadow 的颜色（未指定为 currentColor）。</summary>
    public CssColor Color { get; }

    /// <summary><c>url()</c> 的地址。</summary>
    public string? Url { get; }

    public bool Equals(CssFilterFunction o) => Kind == o.Kind && Amount == o.Amount && OffsetX == o.OffsetX && OffsetY == o.OffsetY && Color.Equals(o.Color) && Url == o.Url;
    public override bool Equals(object? obj) => obj is CssFilterFunction f && Equals(f);
    public override int GetHashCode() => HashCodeCompat.Combine((int)Kind, Amount.GetHashCode(), OffsetX.GetHashCode(), Color.GetHashCode());

    public override string ToString()
    {
        string n = ValueText.Number(Amount);
        return Kind switch
        {
            CssFilterKind.Blur => "blur(" + ValueText.Px(Amount) + ")",
            CssFilterKind.Brightness => "brightness(" + n + ")",
            CssFilterKind.Contrast => "contrast(" + n + ")",
            CssFilterKind.Grayscale => "grayscale(" + n + ")",
            CssFilterKind.HueRotate => "hue-rotate(" + n + "deg)",
            CssFilterKind.Invert => "invert(" + n + ")",
            CssFilterKind.Opacity => "opacity(" + n + ")",
            CssFilterKind.Saturate => "saturate(" + n + ")",
            CssFilterKind.Sepia => "sepia(" + n + ")",
            CssFilterKind.DropShadow => "drop-shadow(" + Color + " " + ValueText.Px(OffsetX) + " " + ValueText.Px(OffsetY) + " " + ValueText.Px(Amount) + ")",
            _ => "url(" + CssSerializer.SerializeString(Url ?? string.Empty) + ")",
        };
    }
}

/// <summary><c>filter</c> / <c>backdrop-filter</c> 的计算值：函数列表；<c>none</c> 为空列表。</summary>
public sealed class CssFilterList : IEquatable<CssFilterList>
{
    public static readonly CssFilterList None = new(Array.Empty<CssFilterFunction>());

    internal CssFilterList(CssFilterFunction[] functions) => _functions = functions;

    readonly CssFilterFunction[] _functions;

    public IReadOnlyList<CssFilterFunction> Functions => _functions;

    public bool IsNone => _functions.Length == 0;

    /// <summary>是否含 <c>url()</c>（本引擎不渲染 SVG 滤镜；浏览器在引用无效时整个滤镜不生效）。</summary>
    public bool HasUrl
    {
        get
        {
            foreach (var f in _functions) if (f.Kind == CssFilterKind.Url) return true;
            return false;
        }
    }

    public bool Equals(CssFilterList? o)
    {
        if (ReferenceEquals(this, o)) return true;
        if (o is null || o._functions.Length != _functions.Length) return false;
        for (int i = 0; i < _functions.Length; i++) if (!_functions[i].Equals(o._functions[i])) return false;
        return true;
    }

    public override bool Equals(object? obj) => obj is CssFilterList l && Equals(l);

    public override int GetHashCode()
    {
        int h = _functions.Length;
        foreach (var f in _functions) h = HashCodeCompat.Combine(h, f.GetHashCode());
        return h;
    }

    public override string ToString()
    {
        if (IsNone) return "none";
        var sb = new StringBuilder();
        for (int i = 0; i < _functions.Length; i++)
        {
            if (i > 0) sb.Append(' ');
            sb.Append(_functions[i].ToString());
        }
        return sb.ToString();
    }
}

/// <summary><c>filter</c> 的解析与计算。</summary>
internal static class FilterParsers
{
    internal readonly struct SpecifiedFilter
    {
        public SpecifiedFilter(CssFilterKind kind, double amount, CalcNode? length, CalcNode? x, CalcNode? y, CssColor color, string? url)
        {
            Kind = kind;
            Amount = amount;
            Length = length;
            X = x;
            Y = y;
            Color = color;
            Url = url;
        }

        public CssFilterKind Kind { get; }
        public double Amount { get; }

        /// <summary>blur 半径或 drop-shadow 的模糊（可含 em）。</summary>
        public CalcNode? Length { get; }
        public CalcNode? X { get; }
        public CalcNode? Y { get; }
        public CssColor Color { get; }
        public string? Url { get; }
    }

    internal sealed class FilterListValue : CssValue
    {
        public FilterListValue(SpecifiedFilter[] filters, CssFilterList? computed)
        {
            Filters = filters;
            Computed = computed;
        }

        public SpecifiedFilter[] Filters { get; }
        public CssFilterList? Computed { get; }
    }

    static readonly CssEnvironment s_env = new();

    public static CssValue? FilterList(ref ValueReader r)
    {
        if (r.TryIdent("none")) return new FilterListValue(Array.Empty<SpecifiedFilter>(), CssFilterList.None);
        var list = new List<SpecifiedFilter>();
        while (!r.AtEnd)
        {
            if (list.Count >= 1024) return null;
            var v = r.Next();
            SpecifiedFilter? f = v switch
            {
                CssToken { Type: CssTokenType.Url } u => new SpecifiedFilter(CssFilterKind.Url, 0, null, null, null, default, u.Value),
                CssFunction fn => Function(fn),
                _ => null,
            };
            if (f is null) return null;
            list.Add(f.Value);
        }
        if (list.Count == 0) return null;
        var arr = list.ToArray();
        bool dependent = false;
        foreach (var f in arr)
            if (f.Length is { IsContextDependent: true } || f.X is { IsContextDependent: true } || f.Y is { IsContextDependent: true }) dependent = true;
        return new FilterListValue(arr, dependent ? null : Compute(arr, new LengthContext(16, 16, s_env)));
    }

    internal static CssFilterList Compute(SpecifiedFilter[] arr, in LengthContext ctx)
    {
        var result = new CssFilterFunction[arr.Length];
        for (int i = 0; i < arr.Length; i++)
        {
            var f = arr[i];
            float Px(CalcNode? n, in LengthContext c) => n is null ? 0 : CalcMath.Finite(n.Compute(c).Px);
            result[i] = f.Kind switch
            {
                CssFilterKind.Blur => new CssFilterFunction(CssFilterKind.Blur, Math.Max(0, Px(f.Length, ctx))),
                CssFilterKind.DropShadow => new CssFilterFunction(CssFilterKind.DropShadow, Math.Max(0, Px(f.Length, ctx)), Px(f.X, ctx), Px(f.Y, ctx), f.Color),
                CssFilterKind.Url => new CssFilterFunction(CssFilterKind.Url, 0, url: f.Url),
                _ => new CssFilterFunction(f.Kind, (float)f.Amount),
            };
        }
        return new CssFilterList(result);
    }

    static SpecifiedFilter? Function(CssFunction fn)
    {
        var name = CssSerializer.ToAsciiLower(fn.Name);
        var r = new ValueReader(fn.Arguments);
        switch (name)
        {
            case "url":
                if (r.Next() is CssToken { Type: CssTokenType.String } s && r.AtEnd) return new SpecifiedFilter(CssFilterKind.Url, 0, null, null, null, default, s.Value);
                return null;
            case "blur":
            {
                if (r.AtEnd) return new SpecifiedFilter(CssFilterKind.Blur, 0, null, null, null, default, null);
                var len = ValueParsers.LengthPercentageNode(r.Next(), nonNegative: true, allowPercent: false);
                if (len is null || !r.AtEnd) return null;
                return new SpecifiedFilter(CssFilterKind.Blur, 0, len, null, null, default, null);
            }
            case "hue-rotate":
            {
                double deg = 0;
                if (!r.AtEnd && (!ValueParsers.TryAngle(r.Next(), out deg) || !r.AtEnd)) return null;
                return new SpecifiedFilter(CssFilterKind.HueRotate, deg, null, null, null, default, null);
            }
            case "brightness":
            case "contrast":
            case "grayscale":
            case "invert":
            case "opacity":
            case "saturate":
            case "sepia":
            {
                var kind = name switch
                {
                    "brightness" => CssFilterKind.Brightness,
                    "contrast" => CssFilterKind.Contrast,
                    "grayscale" => CssFilterKind.Grayscale,
                    "invert" => CssFilterKind.Invert,
                    "opacity" => CssFilterKind.Opacity,
                    "saturate" => CssFilterKind.Saturate,
                    _ => CssFilterKind.Sepia,
                };
                double amount = 1;
                if (!r.AtEnd)
                {
                    if (!TransformParsers.NumPct(r.Next()!, out amount) || !r.AtEnd || amount < 0) return null;
                }
                // 这四个函数的值超过 100% 时钳制到 1（Filter Effects 1 §13）
                if (kind is CssFilterKind.Grayscale or CssFilterKind.Invert or CssFilterKind.Opacity or CssFilterKind.Sepia) amount = Math.Min(1, amount);
                return new SpecifiedFilter(kind, amount, null, null, null, default, null);
            }
            case "drop-shadow":
            {
                // <color>? && <length>{2,3}
                CssColor? color = null;
                var lengths = new List<CalcNode>(3);
                bool lengthsDone = false;
                while (!r.AtEnd)
                {
                    var v = r.Peek()!;
                    if (color is null && CssColorParser.TryParse(v, out var c))
                    {
                        if (lengths.Count > 0) lengthsDone = true;
                        r.Advance();
                        color = c;
                        continue;
                    }
                    if (!lengthsDone && lengths.Count < 3 && ValueParsers.LengthPercentageNode(v, nonNegative: false, allowPercent: false) is { } node)
                    {
                        r.Advance();
                        lengths.Add(node);
                        continue;
                    }
                    return null;
                }
                if (lengths.Count < 2) return null;
                if (lengths.Count == 3 && lengths[2] is CalcLeaf { Value: < 0 }) return null;
                return new SpecifiedFilter(CssFilterKind.DropShadow, 0, lengths.Count == 3 ? lengths[2] : null, lengths[0], lengths[1],
                    color ?? CssColor.CurrentColor, null);
            }
        }
        return null;
    }

    public static CssFilterList ComputeList(CssValue v, in LengthContext ctx, out bool dependent)
    {
        var f = (FilterListValue)v;
        dependent = f.Computed is null;
        return f.Computed ?? Compute(f.Filters, ctx);
    }
}
