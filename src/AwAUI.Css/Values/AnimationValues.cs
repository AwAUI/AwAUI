using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using AwAUI.Css.Syntax;

namespace AwAUI.Css.Values;

/// <summary>缓动函数的种类（CSS Easing 2）。</summary>
public enum CssEasingKind : byte
{
    /// <summary><c>linear</c>（无参数）。</summary>
    Linear,
    /// <summary><c>cubic-bezier()</c> 及 <c>ease / ease-in / ease-out / ease-in-out</c>。</summary>
    CubicBezier,
    /// <summary><c>steps()</c> 及 <c>step-start / step-end</c>。</summary>
    Steps,
    /// <summary>带控制点的 <c>linear(…)</c>。</summary>
    LinearPoints,
}

/// <summary><c>steps()</c> 的跳变位置。</summary>
public enum CssStepPosition : byte
{
    JumpEnd,
    JumpStart,
    JumpNone,
    JumpBoth,
}

/// <summary>
/// 缓动函数的计算值。关键字形式保留关键字名（序列化与 Chrome 一致），参数已规范化：<c>linear()</c> 的输入位置已按规范补齐。
/// <see cref="Evaluate"/> 供动画引擎求值。
/// </summary>
public sealed class CssEasingFunction : IEquatable<CssEasingFunction>
{
    public static readonly CssEasingFunction Linear = new(CssEasingKind.Linear, "linear", 0, 0, 1, 1, 0, CssStepPosition.JumpEnd, null, null);
    public static readonly CssEasingFunction Ease = Bezier(0.25, 0.1, 0.25, 1, "ease");
    public static readonly CssEasingFunction EaseIn = Bezier(0.42, 0, 1, 1, "ease-in");
    public static readonly CssEasingFunction EaseOut = Bezier(0, 0, 0.58, 1, "ease-out");
    public static readonly CssEasingFunction EaseInOut = Bezier(0.42, 0, 0.58, 1, "ease-in-out");
    public static readonly CssEasingFunction StepStart = new(CssEasingKind.Steps, "step-start", 0, 0, 0, 0, 1, CssStepPosition.JumpStart, null, null);
    public static readonly CssEasingFunction StepEnd = new(CssEasingKind.Steps, "step-end", 0, 0, 0, 0, 1, CssStepPosition.JumpEnd, null, null);

    CssEasingFunction(CssEasingKind kind, string? keyword, double x1, double y1, double x2, double y2, int steps, CssStepPosition position,
        double[]? outputs, double[]? inputs)
    {
        Kind = kind;
        Keyword = keyword;
        X1 = x1;
        Y1 = y1;
        X2 = x2;
        Y2 = y2;
        StepCount = steps;
        StepPosition = position;
        _outputs = outputs;
        _inputs = inputs;
    }

    static CssEasingFunction Bezier(double x1, double y1, double x2, double y2, string? keyword)
        => new(CssEasingKind.CubicBezier, keyword, x1, y1, x2, y2, 0, CssStepPosition.JumpEnd, null, null);

    internal static CssEasingFunction CubicBezier(double x1, double y1, double x2, double y2) => Bezier(x1, y1, x2, y2, null);

    internal static CssEasingFunction Steps(int count, CssStepPosition position) => new(CssEasingKind.Steps, null, 0, 0, 0, 0, count, position, null, null);

    internal static CssEasingFunction Points(double[] outputs, double[] inputs) => new(CssEasingKind.LinearPoints, null, 0, 0, 1, 1, 0, CssStepPosition.JumpEnd, outputs, inputs);

    readonly double[]? _outputs;
    readonly double[]? _inputs;

    public CssEasingKind Kind { get; }

    /// <summary>以关键字书写时的关键字（<c>ease</c>、<c>step-end</c> …）；函数形式为 null。</summary>
    public string? Keyword { get; }

    public double X1 { get; }
    public double Y1 { get; }
    public double X2 { get; }
    public double Y2 { get; }

    /// <summary><c>steps()</c> 的步数。</summary>
    public int StepCount { get; }

    public CssStepPosition StepPosition { get; }

    /// <summary><c>linear()</c> 控制点的输出值。</summary>
    public IReadOnlyList<double> PointOutputs => _outputs ?? Array.Empty<double>();

    /// <summary><c>linear()</c> 控制点的输入进度（0–1，已补齐且单调不减）。</summary>
    public IReadOnlyList<double> PointInputs => _inputs ?? Array.Empty<double>();

    /// <summary>按输入进度 <paramref name="t"/>（通常 0–1）求输出进度。<paramref name="before"/> 为 CSS Easing 的「before 标志」。</summary>
    public double Evaluate(double t, bool before = false)
    {
        switch (Kind)
        {
            case CssEasingKind.Linear:
                return t;
            case CssEasingKind.CubicBezier:
                return EvaluateBezier(t);
            case CssEasingKind.Steps:
            {
                int jumps = StepPosition switch
                {
                    CssStepPosition.JumpNone => StepCount - 1,
                    CssStepPosition.JumpBoth => StepCount + 1,
                    _ => StepCount,
                };
                double current = Math.Floor(t * StepCount);
                if (StepPosition is CssStepPosition.JumpStart or CssStepPosition.JumpBoth) current++;
                if (before && Math.Floor(t * StepCount) == t * StepCount) current--;
                if (t >= 0 && current < 0) current = 0;
                if (t <= 1 && current > jumps) current = jumps;
                return jumps <= 0 ? 0 : current / jumps;
            }
            default:
            {
                var o = _outputs!;
                var i = _inputs!;
                if (o.Length == 1) return o[0];
                int k = 0;
                while (k < i.Length - 2 && t > i[k + 1]) k++;
                double span = i[k + 1] - i[k];
                if (span <= 0) return t < i[k] ? o[k] : o[k + 1];
                return o[k] + (o[k + 1] - o[k]) * (t - i[k]) / span;
            }
        }
    }

    double EvaluateBezier(double t)
    {
        // 起止之外按端点切线外推（CSS Easing 2 §2.4）
        if (t <= 0)
        {
            if (X1 > 0) return Y1 / X1 * t;
            if (Y1 == 0 && X2 > 0) return Y2 / X2 * t;
            return 0;
        }
        if (t >= 1)
        {
            if (X2 < 1) return 1 + (Y2 - 1) / (X2 - 1) * (t - 1);
            if (Y2 == 1 && X1 < 1) return 1 + (Y1 - 1) / (X1 - 1) * (t - 1);
            return 1;
        }
        // 先用牛顿法，再二分兜底，求 x(s) = t
        double s = t;
        for (int n = 0; n < 8; n++)
        {
            double x = Coord(s, X1, X2) - t;
            if (Math.Abs(x) < 1e-7) return Coord(s, Y1, Y2);
            double d = Deriv(s, X1, X2);
            if (Math.Abs(d) < 1e-6) break;
            s -= x / d;
        }
        double lo = 0, hi = 1;
        s = t;
        for (int n = 0; n < 64 && hi - lo > 1e-9; n++)
        {
            double x = Coord(s, X1, X2);
            if (x < t) lo = s; else hi = s;
            s = (lo + hi) / 2;
        }
        return Coord(s, Y1, Y2);

        static double Coord(double s, double p1, double p2) => ((1 - 3 * p2 + 3 * p1) * s + (3 * p2 - 6 * p1)) * s * s + 3 * p1 * s;
        static double Deriv(double s, double p1, double p2) => 3 * (1 - 3 * p2 + 3 * p1) * s * s + 2 * (3 * p2 - 6 * p1) * s + 3 * p1;
    }

    public bool Equals(CssEasingFunction? o)
    {
        if (ReferenceEquals(this, o)) return true;
        if (o is null || Kind != o.Kind || Keyword != o.Keyword) return false;
        switch (Kind)
        {
            case CssEasingKind.Linear: return true;
            case CssEasingKind.CubicBezier: return X1 == o.X1 && Y1 == o.Y1 && X2 == o.X2 && Y2 == o.Y2;
            case CssEasingKind.Steps: return StepCount == o.StepCount && StepPosition == o.StepPosition;
            default:
                if (_outputs!.Length != o._outputs!.Length) return false;
                for (int k = 0; k < _outputs.Length; k++)
                    if (_outputs[k] != o._outputs[k] || _inputs![k] != o._inputs![k]) return false;
                return true;
        }
    }

    public override bool Equals(object? obj) => obj is CssEasingFunction e && Equals(e);

    public override int GetHashCode() => HashCodeCompat.Combine((int)Kind, X1.GetHashCode(), Y2.GetHashCode(), StepCount);

    public override string ToString()
    {
        if (Keyword is not null) return Keyword;
        var inv = CultureInfo.InvariantCulture;
        switch (Kind)
        {
            case CssEasingKind.CubicBezier:
                return "cubic-bezier(" + N(X1) + ", " + N(Y1) + ", " + N(X2) + ", " + N(Y2) + ")";
            case CssEasingKind.Steps:
                return StepPosition == CssStepPosition.JumpEnd
                    ? "steps(" + StepCount.ToString(inv) + ")"
                    : "steps(" + StepCount.ToString(inv) + ", " + StepPosition switch
                    {
                        CssStepPosition.JumpStart => "jump-start",
                        CssStepPosition.JumpNone => "jump-none",
                        _ => "jump-both",
                    } + ")";
            default:
            {
                var sb = new StringBuilder("linear(");
                for (int k = 0; k < _outputs!.Length; k++)
                {
                    if (k > 0) sb.Append(", ");
                    sb.Append(N(_outputs[k])).Append(' ').Append(N(_inputs![k] * 100)).Append('%');
                }
                return sb.Append(')').ToString();
            }
        }

        static string N(double d) => CssSerializer.FormatNumber(Math.Round(d, 6));
    }
}

/// <summary>过渡与动画属性的解析（CSS Transitions 1/2、CSS Animations 1/2）。列表在解析时即算好（不依赖上下文）。</summary>
internal static class AnimationParsers
{
    /// <summary>&lt;time&gt;#（毫秒）。<paramref name="allowAuto"/>：<c>auto</c> 记为 -1（<c>animation-duration</c>）。</summary>
    public static CssValue? TimeList(ref ValueReader r, ValueFlags flags)
    {
        bool nonNegative = (flags & ValueFlags.NonNegative) != 0, allowAuto = (flags & ValueFlags.AllowAuto) != 0;
        var list = new List<float>();
        while (true)
        {
            if (!TryTime(ref r, nonNegative, allowAuto, out var ms)) return null;
            list.Add(ms);
            if (r.AtEnd) break;
            if (!r.TryComma()) return null;
        }
        return new PrecomputedValue<CssList<float>>(new CssList<float>(list.ToArray()));
    }

    internal static bool TryTime(ref ValueReader r, bool nonNegative, bool allowAuto, out float ms)
    {
        ms = 0;
        int save = r.Position;
        var v = r.Next();
        if (allowAuto && v is CssToken t && t.IsIdent("auto")) { ms = -1; return true; }
        if (!ValueParsers.TryTime(v, out var d) || (nonNegative && d < 0)) { r.Position = save; return false; }
        ms = (float)d;
        return true;
    }

    public static CssValue? EasingList(ref ValueReader r)
    {
        var list = new List<CssEasingFunction>();
        while (true)
        {
            var e = Easing(r.Next());
            if (e is null) return null;
            list.Add(e);
            if (r.AtEnd) break;
            if (!r.TryComma()) return null;
        }
        return new PrecomputedValue<CssList<CssEasingFunction>>(new CssList<CssEasingFunction>(list.ToArray()));
    }

    /// <summary>&lt;easing-function&gt;。</summary>
    internal static CssEasingFunction? Easing(CssComponentValue? v)
    {
        switch (v)
        {
            case CssToken { Type: CssTokenType.Ident } id:
                return CssSerializer.ToAsciiLower(id.Value) switch
                {
                    "linear" => CssEasingFunction.Linear,
                    "ease" => CssEasingFunction.Ease,
                    "ease-in" => CssEasingFunction.EaseIn,
                    "ease-out" => CssEasingFunction.EaseOut,
                    "ease-in-out" => CssEasingFunction.EaseInOut,
                    "step-start" => CssEasingFunction.StepStart,
                    "step-end" => CssEasingFunction.StepEnd,
                    _ => null,
                };
            case CssFunction f:
                switch (CssSerializer.ToAsciiLower(f.Name))
                {
                    case "cubic-bezier":
                    {
                        var r = new ValueReader(f.Arguments);
                        Span<double> p = stackalloc double[4];
                        for (int i = 0; i < 4; i++)
                        {
                            if (i > 0 && !r.TryComma()) return null;
                            if (!TryNumber(r.Next(), out p[i])) return null;
                        }
                        if (!r.AtEnd || p[0] < 0 || p[0] > 1 || p[2] < 0 || p[2] > 1) return null;
                        return CssEasingFunction.CubicBezier(p[0], p[1], p[2], p[3]);
                    }
                    case "steps":
                    {
                        var r = new ValueReader(f.Arguments);
                        if (!TryNumber(r.Next(), out var n) || n != Math.Floor(n)) return null;
                        var pos = CssStepPosition.JumpEnd;
                        if (r.TryComma())
                        {
                            switch (r.PeekIdentLower())
                            {
                                case "jump-start": case "start": pos = CssStepPosition.JumpStart; break;
                                case "jump-end": case "end": pos = CssStepPosition.JumpEnd; break;
                                case "jump-none": pos = CssStepPosition.JumpNone; break;
                                case "jump-both": pos = CssStepPosition.JumpBoth; break;
                                default: return null;
                            }
                            r.Advance();
                        }
                        if (!r.AtEnd || n < (pos == CssStepPosition.JumpNone ? 2 : 1)) return null;
                        return CssEasingFunction.Steps((int)Math.Min(n, int.MaxValue), pos);
                    }
                    case "linear":
                        return LinearPoints(f.Arguments);
                }
                return null;
            default:
                return null;
        }
    }

    /// <summary><c>linear( [ &lt;number&gt; &amp;&amp; &lt;percentage&gt;{0,2} ]# )</c>，按 CSS Easing 2 §2.2 补齐输入位置。</summary>
    static CssEasingFunction? LinearPoints(IReadOnlyList<CssComponentValue> args)
    {
        var outputs = new List<double>();
        var inputs = new List<double>(); // NaN = 未指定
        var r = new ValueReader(args);
        if (r.AtEnd) return null;
        while (true)
        {
            double? output = null;
            var pcts = new List<double>(2);
            while (!r.AtEnd && r.Peek() is not CssToken { Type: CssTokenType.Comma })
            {
                var v = r.Next();
                if (output is null && TryNumber(v, out var o)) { output = o; continue; }
                if (pcts.Count < 2 && v is CssToken { Type: CssTokenType.Percentage } p) { pcts.Add(p.Number / 100); continue; }
                return null;
            }
            if (output is null) return null;
            if (pcts.Count == 0) { outputs.Add(output.Value); inputs.Add(double.NaN); }
            foreach (var p in pcts) { outputs.Add(output.Value); inputs.Add(p); }
            if (outputs.Count > 10000) return null;
            if (r.AtEnd) break;
            if (!r.TryComma()) return null;
        }
        int n = outputs.Count;
        if (double.IsNaN(inputs[0])) inputs[0] = 0;
        if (n > 1 && double.IsNaN(inputs[n - 1])) inputs[n - 1] = Math.Max(1, MaxDefined(inputs));
        // 位置单调不减
        double max = inputs[0];
        for (int i = 1; i < n; i++)
        {
            if (double.IsNaN(inputs[i])) continue;
            if (inputs[i] < max) inputs[i] = max;
            max = inputs[i];
        }
        // 未指定的位置在相邻已指定位置之间均分
        for (int i = 1; i < n; i++)
        {
            if (!double.IsNaN(inputs[i])) continue;
            int j = i;
            while (double.IsNaN(inputs[j])) j++;
            double a = inputs[i - 1], b = inputs[j];
            for (int k = i; k < j; k++) inputs[k] = a + (b - a) * (k - i + 1) / (j - i + 1);
            i = j;
        }
        return CssEasingFunction.Points(outputs.ToArray(), inputs.ToArray());

        static double MaxDefined(List<double> l)
        {
            double m = 0;
            foreach (var x in l) if (!double.IsNaN(x) && x > m) m = x;
            return m;
        }
    }

    static bool TryNumber(CssComponentValue? v, out double n)
    {
        n = 0;
        var node = ValueParsers.NumberNode(v, nonNegative: false);
        if (node is null || node.IsContextDependent) return false;
        n = CalcMath.FiniteDouble(node.Compute(new LengthContext(16, 16, s_env)).Number);
        return true;
    }

    static readonly CssEnvironment s_env = new();

    /// <summary><c>transition-property: none | [ all | &lt;custom-ident&gt; ]#</c>（<c>none</c> 为空列表）。</summary>
    public static CssValue? TransitionProperty(ref ValueReader r)
    {
        if (r.TryIdent("none")) return r.AtEnd ? new PrecomputedValue<CssList<string>>(CssList<string>.Empty) : null;
        var list = new List<string>();
        while (true)
        {
            var name = TransitionPropertyName(r.Next());
            if (name is null) return null;
            list.Add(name);
            if (r.AtEnd) break;
            if (!r.TryComma()) return null;
        }
        return new PrecomputedValue<CssList<string>>(new CssList<string>(list.ToArray()));
    }

    /// <summary>单个过渡属性名：<c>all</c>、已知属性名（ASCII 小写）、自定义属性或任意 &lt;custom-ident&gt;；<c>none</c> 与全局关键字无效。</summary>
    internal static string? TransitionPropertyName(CssComponentValue? v)
    {
        if (v is not CssToken { Type: CssTokenType.Ident } id) return null;
        if (id.Value.StartsWith("--", StringComparison.Ordinal)) return id.Value;
        var lower = CssSerializer.ToAsciiLower(id.Value);
        if (lower is "none" or "default" || CssWideKeywordValue.TryParse(lower, out _)) return null;
        return lower;
    }

    /// <summary><c>animation-name: [ none | &lt;keyframes-name&gt; ]#</c>（<c>none</c> 记为空串）。</summary>
    public static CssValue? AnimationName(ref ValueReader r)
    {
        var list = new List<string>();
        while (true)
        {
            var name = KeyframesName(r.Next());
            if (name is null) return null;
            list.Add(name);
            if (r.AtEnd) break;
            if (!r.TryComma()) return null;
        }
        return new PrecomputedValue<CssList<string>>(new CssList<string>(list.ToArray()));
    }

    internal static string? KeyframesName(CssComponentValue? v)
    {
        switch (v)
        {
            case CssToken { Type: CssTokenType.String } s:
                return s.Value;
            case CssToken { Type: CssTokenType.Ident } id:
                var lower = CssSerializer.ToAsciiLower(id.Value);
                if (lower == "none") return string.Empty;
                if (lower == "default" || CssWideKeywordValue.TryParse(lower, out _)) return null;
                return id.Value;
            default:
                return null;
        }
    }

    /// <summary><c>animation-iteration-count: [ infinite | &lt;number [0,∞]&gt; ]#</c>（infinite 为 +∞）。</summary>
    public static CssValue? IterationCountList(ref ValueReader r)
    {
        var list = new List<float>();
        while (true)
        {
            if (!TryIterationCount(ref r, out var n)) return null;
            list.Add(n);
            if (r.AtEnd) break;
            if (!r.TryComma()) return null;
        }
        return new PrecomputedValue<CssList<float>>(new CssList<float>(list.ToArray()));
    }

    internal static bool TryIterationCount(ref ValueReader r, out float n)
    {
        n = 1;
        int save = r.Position;
        var v = r.Next();
        if (v is CssToken t && t.IsIdent("infinite")) { n = float.PositiveInfinity; return true; }
        if (!TryNumber(v, out var d) || d < 0) { r.Position = save; return false; }
        n = (float)d;
        return true;
    }

    /// <summary>逗号分隔的关键字列表（<c>animation-direction</c> 等）。</summary>
    public static CssValue? KeywordList<T>(ref ValueReader r, KeywordTable table, Func<int, T> convert)
    {
        var list = new List<T>();
        while (true)
        {
            if (r.Next() is not CssToken { Type: CssTokenType.Ident } t || !table.TryGet(t.Value, out int k)) return null;
            list.Add(convert(k));
            if (r.AtEnd) break;
            if (!r.TryComma()) return null;
        }
        return new PrecomputedValue<CssList<T>>(new CssList<T>(list.ToArray()));
    }
}

/// <summary><c>transition</c> 与 <c>animation</c> 简写。</summary>
internal static partial class Shorthands
{
    /// <summary>
    /// <c>transition: &lt;single-transition&gt;#</c>，
    /// <c>&lt;single-transition&gt; = [ none | &lt;single-transition-property&gt; ] || &lt;time&gt; || &lt;easing-function&gt; || &lt;time&gt; || &lt;transition-behavior-value&gt;</c>。
    /// 长属性顺序：property、duration、timing-function、delay、behavior。
    /// </summary>
    public static bool Transition(IReadOnlyList<CssComponentValue> value, List<PropertyValue> output, CssPropertyId[] longhands)
    {
        var r = new ValueReader(value);
        var props = new List<string>();
        var durations = new List<float>();
        var easings = new List<CssEasingFunction>();
        var delays = new List<float>();
        var behaviors = new List<CssTransitionBehavior>();
        bool sawNone = false;
        int items = 0;
        while (true)
        {
            string? prop = null;
            bool none = false;
            float? duration = null, delay = null;
            CssEasingFunction? easing = null;
            CssTransitionBehavior? behavior = null;
            bool any = false;
            while (!r.AtEnd && r.Peek() is not CssToken { Type: CssTokenType.Comma })
            {
                any = true;
                if (duration is null && AnimationParsers.TryTime(ref r, nonNegative: true, allowAuto: false, out var d)) { duration = d; continue; }
                if (duration is not null && delay is null && AnimationParsers.TryTime(ref r, nonNegative: false, allowAuto: false, out var dl)) { delay = dl; continue; }
                var v = r.Peek()!;
                if (easing is null && AnimationParsers.Easing(v) is { } e) { r.Advance(); easing = e; continue; }
                if (behavior is null && v is CssToken { Type: CssTokenType.Ident } bt && KeywordTables.CssTransitionBehavior.TryGet(bt.Value, out int bk))
                {
                    r.Advance();
                    behavior = (CssTransitionBehavior)bk;
                    continue;
                }
                if (prop is null && !none)
                {
                    if (v is CssToken t && t.IsIdent("none")) { r.Advance(); none = true; continue; }
                    if (AnimationParsers.TransitionPropertyName(v) is { } name) { r.Advance(); prop = name; continue; }
                }
                return false;
            }
            if (!any) return false;
            items++;
            if (none) sawNone = true;
            props.Add(prop ?? (none ? string.Empty : "all"));
            durations.Add(duration ?? 0);
            easings.Add(easing ?? CssEasingFunction.Ease);
            delays.Add(delay ?? 0);
            behaviors.Add(behavior ?? CssTransitionBehavior.Normal);
            if (r.AtEnd) break;
            if (!r.TryComma()) return false;
        }
        // none 只能单独出现
        if (sawNone && items > 1) return false;
        var propList = sawNone ? CssList<string>.Empty : new CssList<string>(props.ToArray());
        output.Add(new PropertyValue(longhands[0], new PrecomputedValue<CssList<string>>(propList)));
        output.Add(new PropertyValue(longhands[1], new PrecomputedValue<CssList<float>>(new CssList<float>(durations.ToArray()))));
        output.Add(new PropertyValue(longhands[2], new PrecomputedValue<CssList<CssEasingFunction>>(new CssList<CssEasingFunction>(easings.ToArray()))));
        output.Add(new PropertyValue(longhands[3], new PrecomputedValue<CssList<float>>(new CssList<float>(delays.ToArray()))));
        output.Add(new PropertyValue(longhands[4], new PrecomputedValue<CssList<CssTransitionBehavior>>(new CssList<CssTransitionBehavior>(behaviors.ToArray()))));
        return true;
    }

    /// <summary>
    /// <c>animation: &lt;single-animation&gt;#</c>（CSS Animations 1/2）：第一个时间是时长，第二个是延迟；其他属性的关键字优先于动画名。
    /// 长属性顺序：name、duration、timing-function、delay、iteration-count、direction、fill-mode、play-state、composition（只重置）。
    /// </summary>
    public static bool Animation(IReadOnlyList<CssComponentValue> value, List<PropertyValue> output, CssPropertyId[] longhands)
    {
        var r = new ValueReader(value);
        var names = new List<string>();
        var durations = new List<float>();
        var easings = new List<CssEasingFunction>();
        var delays = new List<float>();
        var counts = new List<float>();
        var directions = new List<CssAnimationDirection>();
        var fills = new List<CssAnimationFillMode>();
        var states = new List<CssAnimationPlayState>();
        var compositions = new List<CssAnimationComposition>();
        while (true)
        {
            string? name = null;
            float? duration = null, delay = null, count = null;
            CssEasingFunction? easing = null;
            CssAnimationDirection? direction = null;
            CssAnimationFillMode? fill = null;
            CssAnimationPlayState? state = null;
            bool any = false;
            while (!r.AtEnd && r.Peek() is not CssToken { Type: CssTokenType.Comma })
            {
                any = true;
                if (duration is null && AnimationParsers.TryTime(ref r, nonNegative: true, allowAuto: true, out var d)) { duration = d; continue; }
                if (duration is not null && delay is null && AnimationParsers.TryTime(ref r, nonNegative: false, allowAuto: false, out var dl)) { delay = dl; continue; }
                var v = r.Peek()!;
                if (easing is null && AnimationParsers.Easing(v) is { } e) { r.Advance(); easing = e; continue; }
                if (count is null && AnimationParsers.TryIterationCount(ref r, out var n)) { count = n; continue; }
                if (v is CssToken { Type: CssTokenType.Ident } id)
                {
                    if (direction is null && KeywordTables.CssAnimationDirection.TryGet(id.Value, out int dk)) { r.Advance(); direction = (CssAnimationDirection)dk; continue; }
                    // fill-mode 的 none 优先于动画名（规范：其他属性的关键字优先），`animation: none` 的结果相同
                    if (fill is null && KeywordTables.CssAnimationFillMode.TryGet(id.Value, out int fk))
                    {
                        r.Advance();
                        fill = (CssAnimationFillMode)fk;
                        continue;
                    }
                    if (state is null && KeywordTables.CssAnimationPlayState.TryGet(id.Value, out int sk)) { r.Advance(); state = (CssAnimationPlayState)sk; continue; }
                }
                if (name is null && AnimationParsers.KeyframesName(v) is { } kn) { r.Advance(); name = kn; continue; }
                return false;
            }
            if (!any) return false;
            names.Add(name ?? string.Empty);
            durations.Add(duration ?? 0);
            easings.Add(easing ?? CssEasingFunction.Ease);
            delays.Add(delay ?? 0);
            counts.Add(count ?? 1);
            directions.Add(direction ?? CssAnimationDirection.Normal);
            fills.Add(fill ?? CssAnimationFillMode.None);
            states.Add(state ?? CssAnimationPlayState.Running);
            compositions.Add(CssAnimationComposition.Replace);
            if (r.AtEnd) break;
            if (!r.TryComma()) return false;
        }
        output.Add(new PropertyValue(longhands[0], new PrecomputedValue<CssList<string>>(new CssList<string>(names.ToArray()))));
        output.Add(new PropertyValue(longhands[1], new PrecomputedValue<CssList<float>>(new CssList<float>(durations.ToArray()))));
        output.Add(new PropertyValue(longhands[2], new PrecomputedValue<CssList<CssEasingFunction>>(new CssList<CssEasingFunction>(easings.ToArray()))));
        output.Add(new PropertyValue(longhands[3], new PrecomputedValue<CssList<float>>(new CssList<float>(delays.ToArray()))));
        output.Add(new PropertyValue(longhands[4], new PrecomputedValue<CssList<float>>(new CssList<float>(counts.ToArray()))));
        output.Add(new PropertyValue(longhands[5], new PrecomputedValue<CssList<CssAnimationDirection>>(new CssList<CssAnimationDirection>(directions.ToArray()))));
        output.Add(new PropertyValue(longhands[6], new PrecomputedValue<CssList<CssAnimationFillMode>>(new CssList<CssAnimationFillMode>(fills.ToArray()))));
        output.Add(new PropertyValue(longhands[7], new PrecomputedValue<CssList<CssAnimationPlayState>>(new CssList<CssAnimationPlayState>(states.ToArray()))));
        output.Add(new PropertyValue(longhands[8], new PrecomputedValue<CssList<CssAnimationComposition>>(new CssList<CssAnimationComposition>(compositions.ToArray()))));
        return true;
    }
}
