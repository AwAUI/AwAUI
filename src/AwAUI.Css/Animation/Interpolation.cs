using System;
using System.Collections.Generic;
using AwAUI.Css.Values;

namespace AwAUI.Css.Animation;

/// <summary>
/// 计算值的插值与加法合成（CSS Values 4 §3「Combining Values」、CSS Transitions / Animations、Web Animations 的 add / accumulate）。
/// <list type="bullet">
/// <item>长度与百分比逐分量线性插值；含非线性 <c>calc()</c>（min / max / clamp）时生成「线性组合」表达式树，相同的子表达式合并系数，
/// 所以反复从中途值开始的插值（过渡反转）不会让表达式无限加深。</item>
/// <item>颜色在 Oklab 中按预乘 alpha 插值（CSS Color 4 §12 的默认插值空间）；依赖 <c>currentColor</c> 的颜色生成延迟求值的
/// <c>color-mix(in oklab, …)</c>，在使用值阶段按元素的 <c>color</c> 求值。</item>
/// <item>阴影、滤镜列表按项插值（较短的列表补「零值」项）；变换见 <see cref="TransformInterpolation"/>。</item>
/// <item>进度可以超出 [0, 1]（缓动函数的超调），结果按属性的取值范围钳制（非负、opacity 0–1、font-weight 1–1000）。</item>
/// </list>
/// 不能插值的组合（如 <c>auto</c> 与长度、不同的形状）返回 false，由调用方按离散动画处理。
/// </summary>
public static class CssInterpolation
{
    /// <summary>
    /// 按属性 <paramref name="id"/> 的计算值类型插值（值为该属性计算值类型的装箱对象，见 <see cref="ComputedStyle"/> 的访问器）。
    /// 不可插值的属性与不可插值的组合按离散规则：进度小于 0.5 取 <paramref name="from"/>，否则取 <paramref name="to"/>（<c>visibility</c>、
    /// <c>display</c> 等有特殊规则）。返回值表示是否按计算值平滑插值。
    /// </summary>
    public static bool Interpolate(CssPropertyId id, object from, object to, double progress, out object result)
    {
        if (from is null) throw new ArgumentNullException(nameof(from));
        if (to is null) throw new ArgumentNullException(nameof(to));
        if ((int)id >= CssProperties.LonghandCount) throw new ArgumentOutOfRangeException(nameof(id));
        if (AnimationDispatch.TryInterpolate(id, from, to, progress, out result)) return true;
        result = Discrete(id, from, to, progress);
        return false;
    }

    /// <summary>属性的值能否按计算值插值（<c>animatable: by-computed-value</c>）；否则只能离散动画。</summary>
    public static bool IsInterpolable(CssPropertyId id) => (int)id < CssProperties.LonghandCount && AnimationDispatch.IsInterpolable(id);

    /// <summary>
    /// 离散插值（CSS Values 4 §3.1）：进度 &lt; 0.5 取起点，否则取终点。<c>display</c>：一端为 <c>none</c> 时，(0, 1) 内的进度都取另一端
    /// （CSS Display 4，使进出场过渡可以保持元素显示到结束）。
    /// </summary>
    internal static object Discrete(CssPropertyId id, object from, object to, double p)
    {
        if (id == CssPropertyId.Display && from is CssDisplay a && to is CssDisplay b && (a == CssDisplay.None) != (b == CssDisplay.None))
        {
            if (p <= 0) return from;
            if (p >= 1) return to;
            return a == CssDisplay.None ? to : from;
        }
        return p < 0.5 ? from : to;
    }

    // ================= 数值 =================

    static double Lerp(double a, double b, double p) => a + (b - a) * p;

    internal static bool Float(float a, float b, double p, bool nonNegative, out float result)
    {
        double v = Lerp(a, b, p);
        if (nonNegative && v < 0) v = 0;
        result = CalcMath.Finite(v);
        return true;
    }

    internal static bool FloatAdd(float a, float b, bool accumulate, bool nonNegative, out float result)
    {
        double v = (double)a + b;
        if (nonNegative && v < 0) v = 0;
        result = CalcMath.Finite(v);
        return true;
    }

    internal static bool Alpha(float a, float b, double p, bool nonNegative, out float result)
    {
        result = (float)CalcMath.Clamp(Lerp(a, b, p), 0, 1);
        return true;
    }

    internal static bool AlphaAdd(float a, float b, bool accumulate, bool nonNegative, out float result)
    {
        result = (float)CalcMath.Clamp((double)a + b, 0, 1);
        return true;
    }

    internal static bool FontWeight(float a, float b, double p, bool nonNegative, out float result)
    {
        result = (float)CalcMath.Clamp(Lerp(a, b, p), 1, 1000);
        return true;
    }

    internal static bool FontWeightAdd(float a, float b, bool accumulate, bool nonNegative, out float result)
    {
        result = (float)CalcMath.Clamp((double)a + b, 1, 1000);
        return true;
    }

    /// <summary>整数插值：四舍五入到最近的整数（.5 向正无穷，CSS Values 4 §3.1）。</summary>
    static int RoundInt(double v)
    {
        v = Math.Floor(v + 0.5);
        return v > int.MaxValue ? int.MaxValue : v < int.MinValue ? int.MinValue : (int)v;
    }

    internal static bool Integer(int a, int b, double p, bool nonNegative, out int result)
    {
        result = RoundInt(Lerp(a, b, p));
        if (nonNegative && result < 0) result = 0;
        return true;
    }

    internal static bool IntegerAdd(int a, int b, bool accumulate, bool nonNegative, out int result)
    {
        result = RoundInt((double)a + b);
        return true;
    }

    internal static bool IntegerOrAuto(IntegerOrAuto a, IntegerOrAuto b, double p, bool nonNegative, out IntegerOrAuto result)
    {
        if (a.IsAuto || b.IsAuto)
        {
            result = a;
            return false;
        }
        result = new IntegerOrAuto(RoundInt(Lerp(a.Value, b.Value, p)));
        return true;
    }

    internal static bool IntegerOrAutoAdd(IntegerOrAuto a, IntegerOrAuto b, bool accumulate, bool nonNegative, out IntegerOrAuto result)
    {
        if (a.IsAuto || b.IsAuto)
        {
            result = b;
            return false;
        }
        result = new IntegerOrAuto(RoundInt((double)a.Value + b.Value));
        return true;
    }

    internal static bool Visibility(CssVisibility a, CssVisibility b, double p, bool nonNegative, out CssVisibility result)
    {
        if (a != CssVisibility.Visible && b != CssVisibility.Visible)
        {
            result = a;
            return false;
        }
        result = p <= 0 ? a : p >= 1 ? b : CssVisibility.Visible;
        return true;
    }

    // ================= 长度与百分比 =================

    /// <summary>两个长度百分比的线性组合 <c>a·wa + b·wb</c>。</summary>
    internal static LengthPercentage Combine(LengthPercentage a, double wa, LengthPercentage b, double wb)
    {
        if (a.Calc is null && b.Calc is null)
        {
            return Values.LengthPercentage.Linear(CalcMath.Finite(a.Length * wa + b.Length * wb), CalcMath.Finite(a.Percentage * wa + b.Percentage * wb),
                a.HasPercentage || b.HasPercentage);
        }
        var acc = new LinearAccumulator();
        acc.Add(a, wa);
        acc.Add(b, wb);
        return acc.Build();
    }

    /// <summary>把长度百分比（含 calc 树）展开为「px + pct% + Σ 系数 × 非线性子表达式」，相同的子表达式合并系数。</summary>
    struct LinearAccumulator
    {
        double _px, _pct;
        bool _hasPct;
        List<(CalcExpression Expr, double Coef)>? _terms;

        public void Add(LengthPercentage v, double w)
        {
            if (v.Calc is null)
            {
                _px += v.Length * w;
                _pct += v.Percentage * w;
                _hasPct |= v.HasPercentage;
                return;
            }
            AddExpr(v.Calc, w, 0);
        }

        void AddExpr(CalcExpression e, double w, int depth)
        {
            switch (e)
            {
                case CalcExpression.Leaf leaf:
                    _px += leaf.Px * w;
                    _pct += leaf.Percent * w;
                    _hasPct |= leaf.Percent != 0;
                    return;
                case CalcExpression.Sum sum when depth < 64:
                    foreach (var t in sum.Terms) AddExpr(t, w, depth + 1);
                    return;
                case CalcExpression.Product prod when depth < 64:
                    AddExpr(prod.Operand, w * prod.Factor, depth + 1);
                    return;
            }
            _hasPct = true;
            _terms ??= new List<(CalcExpression, double)>();
            for (int i = 0; i < _terms.Count; i++)
            {
                if (!_terms[i].Expr.Equals(e)) continue;
                _terms[i] = (e, _terms[i].Coef + w);
                return;
            }
            _terms.Add((e, w));
        }

        public LengthPercentage Build()
        {
            if (_terms is null) return Values.LengthPercentage.Linear(CalcMath.Finite(_px), CalcMath.Finite(_pct), _hasPct);
            var list = new List<CalcExpression>(_terms.Count + 1);
            if (_px != 0 || _pct != 0) list.Add(new CalcExpression.Leaf(CalcMath.FiniteDouble(_px), CalcMath.FiniteDouble(_pct)));
            foreach (var (expr, coef) in _terms)
            {
                if (coef == 0) continue;
                list.Add(coef == 1 ? expr : new CalcExpression.Product(expr, CalcMath.FiniteDouble(coef)));
            }
            if (list.Count == 0) return Values.LengthPercentage.Linear(0, 0, _hasPct);
            return Values.LengthPercentage.FromCalc(list.Count == 1 ? list[0] : new CalcExpression.Sum(list));
        }
    }

    static CalcExpression ToExpression(LengthPercentage v) => v.Calc ?? new CalcExpression.Leaf(v.Length, v.Percentage);

    /// <summary>非负钳制：纯长度直接截断；含百分比且可能为负时包成 <c>max(值, 0px)</c>，在布局时求值。</summary>
    static LengthPercentage ClampNonNegative(LengthPercentage v)
    {
        if (v.Calc is null)
        {
            if (!v.HasPercentage) return v.Length < 0 ? Values.LengthPercentage.FromPx(0) : v;
            if (v.Length >= 0 && v.Percentage >= 0) return v;
        }
        else if (v.Calc is CalcExpression.MinMax { IsMin: false } m && m.Arguments.Count == 2 && m.Arguments[1] is CalcExpression.Leaf { Px: 0, Percent: 0 })
        {
            return v;
        }
        return Values.LengthPercentage.FromCalc(new CalcExpression.MinMax(false, new[] { ToExpression(v), new CalcExpression.Leaf(0, 0) }));
    }

    internal static LengthPercentage LerpLength(LengthPercentage a, LengthPercentage b, double p, bool nonNegative)
    {
        if (a.Equals(b)) return a;
        var r = Combine(a, 1 - p, b, p);
        // 进度在 [0, 1] 内时两个非负值的凸组合仍非负：只有超调（缓动函数越界）时才需要钳制
        return nonNegative && (p < 0 || p > 1 || r.Calc is null && r.Length < 0 && !r.HasPercentage) ? ClampNonNegative(r) : r;
    }

    internal static bool LengthPercentage(LengthPercentage a, LengthPercentage b, double p, bool nonNegative, out LengthPercentage result)
    {
        result = LerpLength(a, b, p, nonNegative);
        return true;
    }

    internal static bool LengthPercentageAdd(LengthPercentage a, LengthPercentage b, bool accumulate, bool nonNegative, out LengthPercentage result)
    {
        result = Combine(a, 1, b, 1);
        if (nonNegative) result = ClampNonNegative(result);
        return true;
    }

    internal static bool Size(SizeValue a, SizeValue b, double p, bool nonNegative, out SizeValue result)
    {
        if (!a.IsLengthPercentage || !b.IsLengthPercentage)
        {
            result = a;
            return false;
        }
        result = SizeValue.From(LerpLength(a.Value, b.Value, p, nonNegative));
        return true;
    }

    internal static bool SizeAdd(SizeValue a, SizeValue b, bool accumulate, bool nonNegative, out SizeValue result)
    {
        if (!a.IsLengthPercentage || !b.IsLengthPercentage)
        {
            result = b;
            return false;
        }
        LengthPercentageAdd(a.Value, b.Value, accumulate, nonNegative, out var lp);
        result = SizeValue.From(lp);
        return true;
    }

    internal static bool Radius(CornerRadius a, CornerRadius b, double p, bool nonNegative, out CornerRadius result)
    {
        result = new CornerRadius(LerpLength(a.Horizontal, b.Horizontal, p, true), LerpLength(a.Vertical, b.Vertical, p, true));
        return true;
    }

    internal static bool RadiusAdd(CornerRadius a, CornerRadius b, bool accumulate, bool nonNegative, out CornerRadius result)
    {
        result = new CornerRadius(ClampNonNegative(Combine(a.Horizontal, 1, b.Horizontal, 1)), ClampNonNegative(Combine(a.Vertical, 1, b.Vertical, 1)));
        return true;
    }

    internal static bool Position(PositionValue a, PositionValue b, double p, bool nonNegative, out PositionValue result)
    {
        result = new PositionValue(LerpLength(a.X, b.X, p, false), LerpLength(a.Y, b.Y, p, false));
        return true;
    }

    internal static bool PositionAdd(PositionValue a, PositionValue b, bool accumulate, bool nonNegative, out PositionValue result)
    {
        result = new PositionValue(Combine(a.X, 1, b.X, 1), Combine(a.Y, 1, b.Y, 1));
        return true;
    }

    internal static bool LineHeight(LineHeight a, LineHeight b, double p, bool nonNegative, out LineHeight result)
    {
        if (a.Kind != b.Kind || a.Kind == LineHeightKind.Normal)
        {
            result = a;
            return false;
        }
        float v = (float)Math.Max(0, Lerp(a.Value, b.Value, p));
        result = a.Kind == LineHeightKind.Number ? Values.LineHeight.FromNumber(v) : Values.LineHeight.FromPx(v);
        return true;
    }

    internal static bool LineHeightAdd(LineHeight a, LineHeight b, bool accumulate, bool nonNegative, out LineHeight result)
    {
        if (a.Kind != b.Kind || a.Kind == LineHeightKind.Normal)
        {
            result = b;
            return false;
        }
        float v = Math.Max(0, a.Value + b.Value);
        result = a.Kind == LineHeightKind.Number ? Values.LineHeight.FromNumber(v) : Values.LineHeight.FromPx(v);
        return true;
    }

    /// <summary>宽高比按比值的对数插值（CSS Sizing 4 §7.1：<c>aspect-ratio</c> 的插值）；带 <c>auto</c> 的值离散。</summary>
    internal static bool AspectRatio(AspectRatioValue a, AspectRatioValue b, double p, bool nonNegative, out AspectRatioValue result)
    {
        if (a.IsAuto || b.IsAuto || a.Ratio is not { } ra || b.Ratio is not { } rb)
        {
            result = a;
            return false;
        }
        double r = Math.Exp(Lerp(Math.Log(ra), Math.Log(rb), p));
        result = new AspectRatioValue(false, CalcMath.Finite(r), 1);
        return true;
    }

    internal static bool VerticalAlign(CssVerticalAlign a, CssVerticalAlign b, double p, bool nonNegative, out CssVerticalAlign result)
    {
        if (a.Kind != CssVerticalAlignKind.Length || b.Kind != CssVerticalAlignKind.Length)
        {
            result = a;
            return false;
        }
        result = new CssVerticalAlign(CssVerticalAlignKind.Length, LerpLength(a.Length, b.Length, p, false));
        return true;
    }

    internal static bool TextIndent(CssTextIndent a, CssTextIndent b, double p, bool nonNegative, out CssTextIndent result)
    {
        if (a.Hanging != b.Hanging || a.EachLine != b.EachLine)
        {
            result = a;
            return false;
        }
        result = new CssTextIndent(LerpLength(a.Length, b.Length, p, false), a.Hanging, a.EachLine);
        return true;
    }

    internal static bool TabSize(CssTabSize a, CssTabSize b, double p, bool nonNegative, out CssTabSize result)
    {
        if (a.IsLength != b.IsLength)
        {
            result = a;
            return false;
        }
        result = new CssTabSize((float)Math.Max(0, Lerp(a.Value, b.Value, p)), a.IsLength);
        return true;
    }

    internal static bool DecorationThickness(CssDecorationThickness a, CssDecorationThickness b, double p, bool nonNegative, out CssDecorationThickness result)
    {
        if (a.Kind != CssDecorationThicknessKind.Length || b.Kind != CssDecorationThicknessKind.Length)
        {
            result = a;
            return false;
        }
        result = new CssDecorationThickness(CssDecorationThicknessKind.Length, LerpLength(a.Length, b.Length, p, false));
        return true;
    }

    internal static bool BorderSpacing(CssBorderSpacing a, CssBorderSpacing b, double p, bool nonNegative, out CssBorderSpacing result)
    {
        result = new CssBorderSpacing((float)Math.Max(0, Lerp(a.Horizontal, b.Horizontal, p)), (float)Math.Max(0, Lerp(a.Vertical, b.Vertical, p)));
        return true;
    }

    internal static bool FontVariations(CssList<CssFontVariation> a, CssList<CssFontVariation> b, double p, bool nonNegative, out CssList<CssFontVariation> result)
    {
        result = a;
        if (a.Count != b.Count) return false;
        var items = new CssFontVariation[a.Count];
        for (int i = 0; i < items.Length; i++)
        {
            if (!string.Equals(a[i].Tag, b[i].Tag, StringComparison.Ordinal)) return false;
            items[i] = new CssFontVariation(a[i].Tag, CalcMath.Finite(Lerp(a[i].Value, b[i].Value, p)));
        }
        result = new CssList<CssFontVariation>(items, ", ");
        return true;
    }

    // ================= 颜色 =================

    /// <summary>
    /// 颜色插值：Oklab、预乘 alpha（CSS Color 4 §12）。结果裁剪到 sRGB（与渲染一致）。依赖 <c>currentColor</c> 的一端使结果成为延迟求值的
    /// <c>color-mix(in oklab, from, to p)</c>。
    /// </summary>
    public static CssColor InterpolateColor(CssColor from, CssColor to, double progress)
    {
        Color(from, to, progress, false, out var r);
        return r;
    }

    internal static bool Color(CssColor a, CssColor b, double p, bool nonNegative, out CssColor result)
    {
        if (a.Equals(b))
        {
            result = a;
            return true;
        }
        if (!a.DependsOnCurrentColor && !b.DependsOnCurrentColor)
        {
            var wa = WideColor.FromSrgb(a.Value);
            var wb = WideColor.FromSrgb(b.Value);
            var m = CssColorMath.Interpolate(wa, wb, p, CssColorSpace.Oklab, CssHueInterpolation.Shorter);
            if (m.Alpha < 0 || m.Alpha > 1) m = new WideColor(m.Space, m.C0, m.C1, m.C2, CalcMath.Clamp(m.Alpha, 0, 1), m.Missing);
            result = new CssColor(CssColorMath.ToSrgb(m));
            return true;
        }
        string text = "color-mix(in oklab, " + a + ", " + b + " " + ValueText.Percent((float)(p * 100)) + ")";
        result = CssColor.Deferred(new CssColorMixExpression(CssColorSpace.Oklab, CssHueInterpolation.Shorter, a.ToParsed(), b.ToParsed(), p, 1, text));
        return true;
    }

    /// <summary>颜色加法（Web Animations：逐分量相加，按预乘 alpha 在 sRGB 中计算并钳制）。依赖 <c>currentColor</c> 时按替换处理。</summary>
    internal static bool ColorAdd(CssColor a, CssColor b, bool accumulate, bool nonNegative, out CssColor result)
    {
        if (a.DependsOnCurrentColor || b.DependsOnCurrentColor)
        {
            result = b;
            return false;
        }
        var x = a.Value;
        var y = b.Value;
        float alpha = Math.Min(1, x.A + y.A);
        if (alpha <= 0)
        {
            result = new CssColor(default(Color));
            return true;
        }
        float r = (x.R * x.A + y.R * y.A) / alpha, g = (x.G * x.A + y.G * y.A) / alpha, bl = (x.B * x.A + y.B * y.A) / alpha;
        result = new CssColor(new Color(Math.Min(1, r), Math.Min(1, g), Math.Min(1, bl), alpha));
        return true;
    }

    internal static bool AutoColor(CssAutoColor a, CssAutoColor b, double p, bool nonNegative, out CssAutoColor result)
    {
        if (a.IsAuto || b.IsAuto)
        {
            result = a;
            return false;
        }
        Color(a.Color, b.Color, p, false, out var c);
        result = new CssAutoColor(c);
        return true;
    }

    internal static bool ScrollbarColor(CssScrollbarColor a, CssScrollbarColor b, double p, bool nonNegative, out CssScrollbarColor result)
    {
        if (a.IsAuto || b.IsAuto)
        {
            result = a;
            return false;
        }
        Color(a.Thumb, b.Thumb, p, false, out var thumb);
        Color(a.Track, b.Track, p, false, out var track);
        result = new CssScrollbarColor(thumb, track);
        return true;
    }

    internal static bool SvgPaint(CssSvgPaint a, CssSvgPaint b, double p, bool nonNegative, out CssSvgPaint result)
    {
        if (a.Kind != CssSvgPaintKind.Color || b.Kind != CssSvgPaintKind.Color)
        {
            result = a;
            return false;
        }
        Color(a.Color, b.Color, p, false, out var c);
        result = new CssSvgPaint(CssSvgPaintKind.Color, c);
        return true;
    }

    // ================= 阴影 =================

    static Shadow ZeroShadow(bool inset) => new(0, 0, 0, 0, CssColor.Transparent, inset);

    /// <summary>
    /// 阴影列表（CSS Backgrounds 3 §7.2）：较短的列表在末尾补 <c>0 0 0 0 transparent</c>（inset 与对方一致）；同一位置一个 inset 一个不是时不能插值。
    /// </summary>
    internal static bool Shadows(ShadowList a, ShadowList b, double p, bool nonNegative, out ShadowList result)
    {
        result = a;
        int n = Math.Max(a.Shadows.Count, b.Shadows.Count);
        if (n == 0) return true;
        var items = new Shadow[n];
        for (int i = 0; i < n; i++)
        {
            var x = i < a.Shadows.Count ? a.Shadows[i] : ZeroShadow(b.Shadows[i].Inset);
            var y = i < b.Shadows.Count ? b.Shadows[i] : ZeroShadow(x.Inset);
            if (x.Inset != y.Inset) return false;
            Color(x.Color, y.Color, p, false, out var c);
            items[i] = new Shadow(CalcMath.Finite(Lerp(x.OffsetX, y.OffsetX, p)), CalcMath.Finite(Lerp(x.OffsetY, y.OffsetY, p)),
                CalcMath.Finite(Math.Max(0, Lerp(x.Blur, y.Blur, p))), CalcMath.Finite(Lerp(x.Spread, y.Spread, p)), c, x.Inset);
        }
        result = new ShadowList(items);
        return true;
    }

    /// <summary>阴影的加法合成：列表连接（Web Animations：shadow 列表按 <c>add</c> 连接；<c>accumulate</c> 同样处理）。</summary>
    internal static bool ShadowsAdd(ShadowList a, ShadowList b, bool accumulate, bool nonNegative, out ShadowList result)
    {
        var items = new Shadow[a.Shadows.Count + b.Shadows.Count];
        int k = 0;
        foreach (var s in a.Shadows) items[k++] = s;
        foreach (var s in b.Shadows) items[k++] = s;
        result = new ShadowList(items);
        return true;
    }

    // ================= 滤镜 =================

    /// <summary>滤镜函数的「零值」（Filter Effects 1 §interpolation）。</summary>
    static CssFilterFunction FilterIdentity(CssFilterKind kind) => kind switch
    {
        CssFilterKind.Brightness or CssFilterKind.Contrast or CssFilterKind.Opacity or CssFilterKind.Saturate => new CssFilterFunction(kind, 1),
        CssFilterKind.DropShadow => new CssFilterFunction(kind, 0, 0, 0, CssColor.Transparent),
        _ => new CssFilterFunction(kind, 0),
    };

    static float ClampFilterAmount(CssFilterKind kind, double v) => kind switch
    {
        CssFilterKind.HueRotate => CalcMath.Finite(v),
        CssFilterKind.Grayscale or CssFilterKind.Invert or CssFilterKind.Opacity or CssFilterKind.Sepia => (float)CalcMath.Clamp(v, 0, 1),
        _ => CalcMath.Finite(Math.Max(0, v)),
    };

    /// <summary>
    /// 滤镜列表：一端为 <c>none</c> 时换成另一端各函数的零值；两个列表的公共前缀种类相同时逐项插值，较短的一端补零值；
    /// 种类不同或含 <c>url()</c> 时不能插值（离散）。
    /// </summary>
    internal static bool Filters(CssFilterList a, CssFilterList b, double p, bool nonNegative, out CssFilterList result)
    {
        result = a;
        var fa = a.Functions;
        var fb = b.Functions;
        int n = Math.Max(fa.Count, fb.Count);
        if (n == 0) return true;
        var items = new CssFilterFunction[n];
        for (int i = 0; i < n; i++)
        {
            var x = i < fa.Count ? fa[i] : FilterIdentity(fb[i].Kind);
            var y = i < fb.Count ? fb[i] : FilterIdentity(x.Kind);
            if (x.Kind != y.Kind || x.Kind == CssFilterKind.Url) return false;
            if (x.Kind == CssFilterKind.DropShadow)
            {
                // 未指定颜色的 drop-shadow 为 currentColor
                Color(x.Color, y.Color, p, false, out var c);
                items[i] = new CssFilterFunction(x.Kind, CalcMath.Finite(Math.Max(0, Lerp(x.Amount, y.Amount, p))),
                    CalcMath.Finite(Lerp(x.OffsetX, y.OffsetX, p)), CalcMath.Finite(Lerp(x.OffsetY, y.OffsetY, p)), c);
            }
            else
            {
                items[i] = new CssFilterFunction(x.Kind, ClampFilterAmount(x.Kind, Lerp(x.Amount, y.Amount, p)));
            }
        }
        result = new CssFilterList(items);
        return true;
    }

    /// <summary>滤镜的加法合成：列表连接。</summary>
    internal static bool FiltersAdd(CssFilterList a, CssFilterList b, bool accumulate, bool nonNegative, out CssFilterList result)
    {
        if (a.IsNone) { result = b; return true; }
        if (b.IsNone) { result = a; return true; }
        var items = new CssFilterFunction[a.Functions.Count + b.Functions.Count];
        int k = 0;
        foreach (var f in a.Functions) items[k++] = f;
        foreach (var f in b.Functions) items[k++] = f;
        result = new CssFilterList(items);
        return true;
    }

    // ================= 裁剪路径 =================

    /// <summary>
    /// <c>clip-path</c>（CSS Shapes 1 §interpolation）：参考盒相同的同种基本形状才能插值 —— <c>inset()</c> 逐边与圆角；<c>circle()/ellipse()</c>
    /// 半径都为长度时插值半径与中心；<c>polygon()</c> 点数与填充规则相同时逐点。其余离散。
    /// </summary>
    internal static bool ClipPath(CssClipPath a, CssClipPath b, double p, bool nonNegative, out CssClipPath result)
    {
        result = a;
        if (a.Kind != CssClipPathKind.Shape || b.Kind != CssClipPathKind.Shape || a.ShapeKind != b.ShapeKind || a.GeometryBox != b.GeometryBox) return false;
        switch (a.ShapeKind)
        {
            case CssBasicShapeKind.Inset:
            {
                int n = Math.Max(a.InsetRadii.Count, b.InsetRadii.Count);
                var radii = new CornerRadius[n];
                for (int i = 0; i < n; i++)
                {
                    var ra = i < a.InsetRadii.Count ? a.InsetRadii[i] : default;
                    var rb = i < b.InsetRadii.Count ? b.InsetRadii[i] : default;
                    Radius(ra, rb, p, true, out radii[i]);
                }
                result = CssClipPath.Inset(LerpLength(a.InsetTop, b.InsetTop, p, false), LerpLength(a.InsetRight, b.InsetRight, p, false),
                    LerpLength(a.InsetBottom, b.InsetBottom, p, false), LerpLength(a.InsetLeft, b.InsetLeft, p, false), radii, a.GeometryBox);
                return true;
            }
            case CssBasicShapeKind.Circle:
            case CssBasicShapeKind.Ellipse:
            {
                if (a.RadiusXKind != b.RadiusXKind || a.RadiusYKind != b.RadiusYKind) return false;
                var rx = a.RadiusXKind == CssShapeRadiusKind.Length ? LerpLength(a.RadiusX, b.RadiusX, p, true) : a.RadiusX;
                var ry = a.RadiusYKind == CssShapeRadiusKind.Length ? LerpLength(a.RadiusY, b.RadiusY, p, true) : a.RadiusY;
                Position(a.Center, b.Center, p, false, out var center);
                result = CssClipPath.Ellipse(a.ShapeKind == CssBasicShapeKind.Circle, a.RadiusXKind, rx, a.RadiusYKind, ry, center, a.GeometryBox);
                return true;
            }
            case CssBasicShapeKind.Polygon:
            {
                if (a.FillRule != b.FillRule || a.Points.Count != b.Points.Count) return false;
                var pts = new (LengthPercentage, LengthPercentage)[a.Points.Count];
                for (int i = 0; i < pts.Length; i++)
                    pts[i] = (LerpLength(a.Points[i].X, b.Points[i].X, p, false), LerpLength(a.Points[i].Y, b.Points[i].Y, p, false));
                result = CssClipPath.Polygon(a.FillRule, pts, a.GeometryBox);
                return true;
            }
            default:
                return false;
        }
    }

    // ================= 变换 =================

    internal static bool Transform(CssTransformList a, CssTransformList b, double p, bool nonNegative, out CssTransformList result)
    {
        result = TransformInterpolation.Interpolate(a, b, p);
        return true;
    }

    internal static bool TransformAdd(CssTransformList a, CssTransformList b, bool accumulate, bool nonNegative, out CssTransformList result)
    {
        result = accumulate ? TransformInterpolation.Accumulate(a, b) : TransformInterpolation.Concat(a, b);
        return true;
    }

    internal static bool TransformOrigin(CssTransformOrigin a, CssTransformOrigin b, double p, bool nonNegative, out CssTransformOrigin result)
    {
        result = new CssTransformOrigin(LerpLength(a.X, b.X, p, false), LerpLength(a.Y, b.Y, p, false), CalcMath.Finite(Lerp(a.Z, b.Z, p)));
        return true;
    }

    internal static bool TransformOriginAdd(CssTransformOrigin a, CssTransformOrigin b, bool accumulate, bool nonNegative, out CssTransformOrigin result)
    {
        result = new CssTransformOrigin(Combine(a.X, 1, b.X, 1), Combine(a.Y, 1, b.Y, 1), a.Z + b.Z);
        return true;
    }

    internal static bool Perspective(float a, float b, double p, bool nonNegative, out float result)
    {
        if (a < 0 || b < 0)
        {
            result = a;
            return false;
        }
        result = CalcMath.Finite(Math.Max(0, Lerp(a, b, p)));
        return true;
    }

    internal static bool Translate(CssTranslate a, CssTranslate b, double p, bool nonNegative, out CssTranslate result)
    {
        if (a.IsNone && b.IsNone)
        {
            result = a;
            return true;
        }
        result = new CssTranslate(LerpLength(a.X, b.X, p, false), LerpLength(a.Y, b.Y, p, false), CalcMath.Finite(Lerp(a.Z, b.Z, p)));
        return true;
    }

    internal static bool TranslateAdd(CssTranslate a, CssTranslate b, bool accumulate, bool nonNegative, out CssTranslate result)
    {
        if (a.IsNone) { result = b; return true; }
        if (b.IsNone) { result = a; return true; }
        result = new CssTranslate(Combine(a.X, 1, b.X, 1), Combine(a.Y, 1, b.Y, 1), a.Z + b.Z);
        return true;
    }

    internal static bool Scale(CssScale a, CssScale b, double p, bool nonNegative, out CssScale result)
    {
        if (a.IsNone && b.IsNone)
        {
            result = a;
            return true;
        }
        float ax = a.IsNone ? 1 : a.X, ay = a.IsNone ? 1 : a.Y, az = a.IsNone ? 1 : a.Z;
        float bx = b.IsNone ? 1 : b.X, by = b.IsNone ? 1 : b.Y, bz = b.IsNone ? 1 : b.Z;
        result = new CssScale(CalcMath.Finite(Lerp(ax, bx, p)), CalcMath.Finite(Lerp(ay, by, p)), CalcMath.Finite(Lerp(az, bz, p)));
        return true;
    }

    /// <summary><c>scale</c> 的加法：逐轴相乘（CSS Transforms 2 §scale 的加法定义）；accumulate 为 (a − 1) + (b − 1) + 1。</summary>
    internal static bool ScaleAdd(CssScale a, CssScale b, bool accumulate, bool nonNegative, out CssScale result)
    {
        if (a.IsNone) { result = b; return true; }
        if (b.IsNone) { result = a; return true; }
        result = accumulate
            ? new CssScale(a.X + b.X - 1, a.Y + b.Y - 1, a.Z + b.Z - 1)
            : new CssScale(a.X * b.X, a.Y * b.Y, a.Z * b.Z);
        return true;
    }

    /// <summary>
    /// <c>rotate</c>：轴相同（或都是 2D）时插值角度；轴不同时按四元数球面插值（CSS Transforms 2 §interpolation of rotate），
    /// <c>none</c> 视为另一端轴上的 0deg。
    /// </summary>
    internal static bool Rotate(CssRotate a, CssRotate b, double p, bool nonNegative, out CssRotate result)
    {
        if (a.IsNone && b.IsNone)
        {
            result = a;
            return true;
        }
        if (a.IsNone) a = new CssRotate(b.AxisX, b.AxisY, b.AxisZ, 0);
        if (b.IsNone) b = new CssRotate(a.AxisX, a.AxisY, a.AxisZ, 0);
        if (SameAxis(a, b, out var nx, out var ny, out var nz))
        {
            result = new CssRotate(nx, ny, nz, CalcMath.Finite(Lerp(a.Angle, b.Angle, p)));
            return true;
        }
        var qa = TransformInterpolation.AxisAngleToQuaternion(a.AxisX, a.AxisY, a.AxisZ, a.Angle);
        var qb = TransformInterpolation.AxisAngleToQuaternion(b.AxisX, b.AxisY, b.AxisZ, b.Angle);
        var q = TransformInterpolation.Slerp(qa, qb, p);
        TransformInterpolation.QuaternionToAxisAngle(q, out var x, out var y, out var z, out var angle);
        result = new CssRotate((float)x, (float)y, (float)z, CalcMath.Finite(angle));
        return true;
    }

    internal static bool RotateAdd(CssRotate a, CssRotate b, bool accumulate, bool nonNegative, out CssRotate result)
    {
        if (a.IsNone) { result = b; return true; }
        if (b.IsNone) { result = a; return true; }
        if (SameAxis(a, b, out var nx, out var ny, out var nz))
        {
            result = new CssRotate(nx, ny, nz, a.Angle + b.Angle);
            return true;
        }
        // 不同轴：四元数相乘（先 a 后 b）
        var q = TransformInterpolation.Multiply(TransformInterpolation.AxisAngleToQuaternion(b.AxisX, b.AxisY, b.AxisZ, b.Angle),
            TransformInterpolation.AxisAngleToQuaternion(a.AxisX, a.AxisY, a.AxisZ, a.Angle));
        TransformInterpolation.QuaternionToAxisAngle(q, out var x, out var y, out var z, out var angle);
        result = new CssRotate((float)x, (float)y, (float)z, CalcMath.Finite(angle));
        return true;
    }

    static bool SameAxis(CssRotate a, CssRotate b, out float x, out float y, out float z)
    {
        x = a.AxisX;
        y = a.AxisY;
        z = a.AxisZ;
        double la = Math.Sqrt((double)a.AxisX * a.AxisX + (double)a.AxisY * a.AxisY + (double)a.AxisZ * a.AxisZ);
        double lb = Math.Sqrt((double)b.AxisX * b.AxisX + (double)b.AxisY * b.AxisY + (double)b.AxisZ * b.AxisZ);
        if (la == 0 || lb == 0) return la == lb;
        double dx = a.AxisX / la - b.AxisX / lb, dy = a.AxisY / la - b.AxisY / lb, dz = a.AxisZ / la - b.AxisZ / lb;
        return dx * dx + dy * dy + dz * dz < 1e-12;
    }
}
