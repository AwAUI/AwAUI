using System;
using System.Collections.Generic;
using AwAUI.Css.Syntax;
using AwAUI.Css.Values;

namespace AwAUI.Css.Animation;

/// <summary>
/// 自定义属性的插值（CSS Properties and Values API 1 §2.5）：只有以带类型的语法（不是 <c>*</c>）注册的属性按计算值插值 ——
/// 计算后的记号序列结构相同时逐个数值插值（数、百分比、同单位的维度），颜色按 <see cref="CssInterpolation.InterpolateColor"/>；
/// 其余（未注册、<c>*</c> 语法、结构不同）离散。
/// </summary>
internal static class CustomPropertyInterpolation
{
    const int MaxDepth = 16;

    public static bool TryInterpolate(RegisteredCustomProperty? registration, CustomPropertyValue a, CustomPropertyValue b, double p, out CustomPropertyValue result)
    {
        result = p < 0.5 ? a : b;
        if (registration is null || registration.Syntax.IsUniversal) return false;
        if (ReferenceEquals(a, b)) return true;
        var list = Interpolate(a.Tokens, b.Tokens, p, 0);
        if (list is null) return false;
        result = new CustomPropertyValue(list, hasReferences: false);
        return true;
    }

    public static bool IsTyped(RegisteredCustomProperty? registration) => registration is not null && !registration.Syntax.IsUniversal;

    static IReadOnlyList<CssComponentValue>? Interpolate(IReadOnlyList<CssComponentValue> a, IReadOnlyList<CssComponentValue> b, double p, int depth)
    {
        if (depth > MaxDepth) return null;
        var xa = Significant(a);
        var xb = Significant(b);
        if (xa.Count != xb.Count) return null;
        var result = new List<CssComponentValue>(xa.Count * 2);
        for (int i = 0; i < xa.Count; i++)
        {
            if (i > 0) result.Add(CssToken.CreateWhitespace());
            var r = InterpolateOne(xa[i], xb[i], p, depth);
            if (r is null) return null;
            result.AddRange(r);
        }
        return result;
    }

    static List<CssComponentValue> Significant(IReadOnlyList<CssComponentValue> list)
    {
        var r = new List<CssComponentValue>(list.Count);
        foreach (var c in list)
            if (c is not CssToken { Type: CssTokenType.Whitespace }) r.Add(c);
        return r;
    }

    static IReadOnlyList<CssComponentValue>? InterpolateOne(CssComponentValue a, CssComponentValue b, double p, int depth)
    {
        // 颜色（含 rgb() 等函数形式与命名色）
        if (CssColorParser.TryParse(a, out var ca) && CssColorParser.TryParse(b, out var cb))
        {
            var c = CssInterpolation.InterpolateColor(ca, cb, p);
            return CssToken.ParseList(c.ToString());
        }
        if (a is CssToken ta && b is CssToken tb)
        {
            if (ta.Type != tb.Type) return null;
            double v = ta.Number + (tb.Number - ta.Number) * p;
            switch (ta.Type)
            {
                case CssTokenType.Number:
                    return new[] { CssToken.CreateNumber(CalcMath.FiniteDouble(ta.NumberType == CssNumberType.Integer && tb.NumberType == CssNumberType.Integer ? Math.Floor(v + 0.5) : v)) };
                case CssTokenType.Percentage:
                    return new[] { CssToken.CreatePercentage(CalcMath.FiniteDouble(v)) };
                case CssTokenType.Dimension:
                    if (!string.Equals(ta.Unit, tb.Unit, StringComparison.OrdinalIgnoreCase)) return null;
                    return new[] { CssToken.CreateDimension(CalcMath.FiniteDouble(v), ta.Unit) };
                default:
                    return ta.ToCss() == tb.ToCss() ? new CssComponentValue[] { ta } : null;
            }
        }
        if (a is CssFunction fa && b is CssFunction fb && string.Equals(fa.Name, fb.Name, StringComparison.OrdinalIgnoreCase))
        {
            var args = InterpolateArguments(fa.Arguments, fb.Arguments, p, depth + 1);
            return args is null ? null : new CssComponentValue[] { new CssFunction(fa.Name, args, default, true) };
        }
        return null;
    }

    /// <summary>函数参数：逗号保持原位，其余逐项插值。</summary>
    static IReadOnlyList<CssComponentValue>? InterpolateArguments(IReadOnlyList<CssComponentValue> a, IReadOnlyList<CssComponentValue> b, double p, int depth)
    {
        if (depth > MaxDepth) return null;
        var xa = Significant(a);
        var xb = Significant(b);
        if (xa.Count != xb.Count) return null;
        var result = new List<CssComponentValue>(xa.Count * 2);
        for (int i = 0; i < xa.Count; i++)
        {
            if (xa[i] is CssToken { Type: CssTokenType.Comma })
            {
                if (xb[i] is not CssToken { Type: CssTokenType.Comma }) return null;
                result.Add(xa[i]);
                continue;
            }
            if (i > 0 && xa[i - 1] is not CssToken { Type: CssTokenType.Comma }) result.Add(CssToken.CreateWhitespace());
            var r = InterpolateOne(xa[i], xb[i], p, depth);
            if (r is null) return null;
            result.AddRange(r);
        }
        return result;
    }
}
