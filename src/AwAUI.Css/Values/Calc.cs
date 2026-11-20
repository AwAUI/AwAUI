using System;
using System.Collections.Generic;
using System.Text;
using AwAUI.Css.Syntax;

namespace AwAUI.Css.Values;

/// <summary>calc 表达式（及普通数值）的类型类别（CSS Values 4 §10.8 的简化）。</summary>
internal enum CalcCategory : byte
{
    Number,
    Length,
    Percent,
    /// <summary>长度与百分比的混合。</summary>
    LengthPercent,
    Angle,
    Time,
}

/// <summary>百分比在当前上下文中的含义。</summary>
internal enum PercentMode : byte
{
    /// <summary>不允许百分比。</summary>
    None,
    /// <summary>百分比与长度可混合（&lt;length-percentage&gt;）。</summary>
    Length,
    /// <summary>百分比解析为数值（<c>50%</c> = <c>0.5</c>，如 <c>opacity</c>、颜色的 alpha）。</summary>
    Number,
}

/// <summary>
/// 指定值阶段的数值表达式：普通数值/维度是叶子，<c>calc()/min()/max()/clamp()</c> 是内部节点。
/// 在计算值阶段经 <see cref="Compute"/> 化为 px + % 的线性形式或 <see cref="CalcExpression"/> 树。
/// </summary>
internal abstract class CalcNode
{
    public CalcCategory Category { get; protected set; }

    /// <summary>是否含有需要上下文（em、视口单位）才能换算的长度。</summary>
    public abstract bool IsContextDependent { get; }

    /// <summary>是否含视口单位（视口变化时需要重算）。</summary>
    public abstract bool UsesViewport { get; }

    /// <summary>是否为原样书写的单个数值（非函数）。</summary>
    public virtual bool IsLeaf => false;

    public abstract CalcResult Compute(in LengthContext context);

    public abstract void Write(StringBuilder sb);

    public override string ToString()
    {
        var sb = new StringBuilder();
        Write(sb);
        return sb.ToString();
    }
}

/// <summary>计算结果：数值（Number/Angle/Time 类别，角度为度、时间为毫秒），或长度的线性形式/表达式树。</summary>
internal readonly struct CalcResult
{
    public CalcResult(double number)
    {
        Number = number;
        Px = 0;
        Pct = 0;
        HasPct = false;
        Tree = null;
    }

    public CalcResult(double px, double pct, bool hasPct)
    {
        Number = 0;
        Px = px;
        Pct = pct;
        HasPct = hasPct;
        Tree = null;
    }

    public CalcResult(CalcExpression tree)
    {
        Number = 0;
        Px = 0;
        Pct = 0;
        HasPct = true;
        Tree = tree;
    }

    public double Number { get; }
    public double Px { get; }
    public double Pct { get; }
    public bool HasPct { get; }
    public CalcExpression? Tree { get; }

    public CalcExpression ToExpression() => Tree ?? new CalcExpression.Leaf(Px, Pct);

    public LengthPercentage ToLengthPercentage()
    {
        if (Tree is not null) return LengthPercentage.FromCalc(Tree);
        return LengthPercentage.Linear(CalcMath.Finite(Px), CalcMath.Finite(Pct), HasPct);
    }
}

/// <summary>叶子：数、百分比、长度、角度或时间。</summary>
internal sealed class CalcLeaf : CalcNode
{
    CalcLeaf(CalcCategory category, double value, CssLengthUnit unit, string? unitText)
    {
        Category = category;
        Value = value;
        Unit = unit;
        UnitText = unitText;
    }

    public static CalcLeaf Number(double v) => new(CalcCategory.Number, v, CssLengthUnit.Px, null);
    public static CalcLeaf Percent(double v) => new(CalcCategory.Percent, v, CssLengthUnit.Percent, null);
    public static CalcLeaf Length(CssLength l) => new(l.IsPercentage ? CalcCategory.Percent : CalcCategory.Length, l.Value, l.Unit, null);
    /// <summary>角度（已换算为度）。</summary>
    public static CalcLeaf Angle(double degrees, string unitText) => new(CalcCategory.Angle, degrees, CssLengthUnit.Px, unitText);
    /// <summary>时间（已换算为毫秒）。</summary>
    public static CalcLeaf Time(double ms, string unitText) => new(CalcCategory.Time, ms, CssLengthUnit.Px, unitText);

    public double Value { get; }

    public CssLengthUnit Unit { get; }

    /// <summary>角度/时间的原始单位（序列化用）。</summary>
    public string? UnitText { get; }

    public override bool IsLeaf => true;

    public override bool IsContextDependent => Category == CalcCategory.Length && !new CssLength(Value, Unit).IsAbsolute;

    public override bool UsesViewport => Category == CalcCategory.Length && (new CssLength(Value, Unit).IsViewportRelative || new CssLength(Value, Unit).IsContainerRelative);

    public override CalcResult Compute(in LengthContext context)
    {
        switch (Category)
        {
            case CalcCategory.Length: return new CalcResult(new CssLength(Value, Unit).ToPx(context), 0, false);
            case CalcCategory.Percent: return new CalcResult(0, Value, true);
            default: return new CalcResult(Value);
        }
    }

    public override void Write(StringBuilder sb)
    {
        switch (Category)
        {
            case CalcCategory.Length:
            case CalcCategory.Percent: sb.Append(new CssLength(Value, Unit).ToString()); break;
            case CalcCategory.Angle: sb.Append(CssSerializer.FormatNumber(Value)).Append("deg"); break;
            case CalcCategory.Time: sb.Append(CssSerializer.FormatNumber(Value)).Append("ms"); break;
            default: sb.Append(CssSerializer.FormatNumber(Value)); break;
        }
    }
}

/// <summary>内部节点：求和、乘积、倒数、min/max/clamp。</summary>
internal sealed class CalcOperation : CalcNode
{
    public enum Op : byte { Sum, Product, Invert, Negate, Min, Max, Clamp, Calc }

    public CalcOperation(Op op, CalcCategory category, CalcNode[] operands)
    {
        Operator = op;
        Category = category;
        Operands = operands;
        foreach (var o in operands)
        {
            if (o.IsContextDependent) _contextDependent = true;
            if (o.UsesViewport) _viewport = true;
        }
    }

    readonly bool _contextDependent;
    readonly bool _viewport;

    public Op Operator { get; }

    public CalcNode[] Operands { get; }

    public override bool IsContextDependent => _contextDependent;

    public override bool UsesViewport => _viewport;

    public override CalcResult Compute(in LengthContext context)
    {
        bool numeric = Category is CalcCategory.Number or CalcCategory.Angle or CalcCategory.Time;
        switch (Operator)
        {
            case Op.Calc:
                return Operands[0].Compute(context);
            case Op.Negate:
            {
                var r = Operands[0].Compute(context);
                return Scale(r, -1, numeric);
            }
            case Op.Invert:
            {
                var r = Operands[0].Compute(context);
                return new CalcResult(1 / r.Number);
            }
            case Op.Sum:
            {
                if (numeric)
                {
                    double s = 0;
                    foreach (var o in Operands) s += o.Compute(context).Number;
                    return new CalcResult(s);
                }
                var results = new CalcResult[Operands.Length];
                bool tree = false;
                for (int i = 0; i < Operands.Length; i++)
                {
                    results[i] = Operands[i].Compute(context);
                    tree |= results[i].Tree is not null;
                }
                if (!tree)
                {
                    double px = 0, pct = 0;
                    bool hasPct = false;
                    foreach (var r in results)
                    {
                        px += r.Px;
                        pct += r.Pct;
                        hasPct |= r.HasPct;
                    }
                    return new CalcResult(px, pct, hasPct);
                }
                var terms = new CalcExpression[results.Length];
                for (int i = 0; i < results.Length; i++) terms[i] = results[i].ToExpression();
                return new CalcResult(new CalcExpression.Sum(terms));
            }
            case Op.Product:
            {
                double factor = 1;
                CalcResult? dimension = null;
                foreach (var o in Operands)
                {
                    var r = o.Compute(context);
                    if (o.Category == CalcCategory.Number) factor *= r.Number;
                    else dimension = r;
                }
                if (dimension is null) return new CalcResult(factor);
                return Scale(dimension.Value, factor, numeric);
            }
            case Op.Min:
            case Op.Max:
            {
                bool isMin = Operator == Op.Min;
                var results = new CalcResult[Operands.Length];
                bool linear = true;
                for (int i = 0; i < Operands.Length; i++)
                {
                    results[i] = Operands[i].Compute(context);
                    if (results[i].HasPct) linear = false;
                }
                if (numeric)
                {
                    double v = results[0].Number;
                    for (int i = 1; i < results.Length; i++) v = isMin ? Math.Min(v, results[i].Number) : Math.Max(v, results[i].Number);
                    return new CalcResult(v);
                }
                if (linear)
                {
                    double v = results[0].Px;
                    for (int i = 1; i < results.Length; i++) v = isMin ? Math.Min(v, results[i].Px) : Math.Max(v, results[i].Px);
                    return new CalcResult(v, 0, false);
                }
                var args = new CalcExpression[results.Length];
                for (int i = 0; i < results.Length; i++) args[i] = results[i].ToExpression();
                return new CalcResult(new CalcExpression.MinMax(isMin, args));
            }
            case Op.Clamp:
            {
                var a = Operands[0].Compute(context);
                var b = Operands[1].Compute(context);
                var c = Operands[2].Compute(context);
                if (numeric) return new CalcResult(Math.Max(a.Number, Math.Min(b.Number, c.Number)));
                if (!a.HasPct && !b.HasPct && !c.HasPct) return new CalcResult(Math.Max(a.Px, Math.Min(b.Px, c.Px)), 0, false);
                return new CalcResult(new CalcExpression.Clamp(a.ToExpression(), b.ToExpression(), c.ToExpression()));
            }
            default:
                throw new InvalidOperationException();
        }
    }

    static CalcResult Scale(CalcResult r, double factor, bool numeric)
    {
        if (numeric) return new CalcResult(r.Number * factor);
        if (r.Tree is not null) return new CalcResult(new CalcExpression.Product(r.Tree, factor));
        return new CalcResult(r.Px * factor, r.Pct * factor, r.HasPct);
    }

    public override void Write(StringBuilder sb)
    {
        switch (Operator)
        {
            case Op.Calc:
                sb.Append("calc(");
                Operands[0].Write(sb);
                sb.Append(')');
                return;
            case Op.Negate:
                sb.Append("(-1 * ");
                Operands[0].Write(sb);
                sb.Append(')');
                return;
            case Op.Invert:
                sb.Append("(1 / ");
                Operands[0].Write(sb);
                sb.Append(')');
                return;
            case Op.Sum:
            case Op.Product:
                sb.Append('(');
                for (int i = 0; i < Operands.Length; i++)
                {
                    if (i > 0) sb.Append(Operator == Op.Sum ? " + " : " * ");
                    Operands[i].Write(sb);
                }
                sb.Append(')');
                return;
            default:
                sb.Append(Operator == Op.Min ? "min(" : Operator == Op.Max ? "max(" : "clamp(");
                for (int i = 0; i < Operands.Length; i++)
                {
                    if (i > 0) sb.Append(", ");
                    Operands[i].Write(sb);
                }
                sb.Append(')');
                return;
        }
    }
}

/// <summary>
/// 解析数值类组件值：单个数/百分比/维度记号，或 <c>calc()/min()/max()/clamp()</c>。
/// 表达式嵌套深度有上界（防止恶意输入导致栈溢出），类型按 CSS Values 4 检查。
/// </summary>
internal static class CalcParser
{
    /// <summary>calc 函数与括号的最大嵌套深度。</summary>
    public const int MaxDepth = 32;

    /// <summary>单个表达式的最大节点数。</summary>
    public const int MaxNodes = 1024;

    /// <summary>是否为数学函数名。</summary>
    public static bool IsMathFunction(string name)
    {
        var n = CssSerializer.ToAsciiLower(name);
        return n is "calc" or "min" or "max" or "clamp" or "-webkit-calc";
    }

    /// <summary>
    /// 把一个组件值解析为数值节点。<paramref name="allowUnitlessZero"/> 允许把 <c>0</c> 当作长度。
    /// 返回节点的类别由调用方检查；不可解析时返回 null。
    /// </summary>
    public static CalcNode? ParseComponent(CssComponentValue value, PercentMode percent, bool allowUnitlessZero)
    {
        switch (value)
        {
            case CssToken t:
                return ParseToken(t, percent, allowUnitlessZero);
            case CssFunction f when IsMathFunction(f.Name):
            {
                int nodes = 0;
                var node = ParseFunction(f, percent, depth: 0, ref nodes);
                return node;
            }
            default:
                return null;
        }
    }

    static CalcNode? ParseToken(CssToken t, PercentMode percent, bool allowUnitlessZero)
    {
        switch (t.Type)
        {
            case CssTokenType.Number:
                if (allowUnitlessZero && t.Number == 0) return CalcLeaf.Length(CssLength.Px(0));
                return CalcLeaf.Number(t.Number);
            case CssTokenType.Percentage:
                if (percent == PercentMode.None) return null;
                return percent == PercentMode.Number ? CalcLeaf.Number(t.Number / 100) : CalcLeaf.Percent(t.Number);
            case CssTokenType.Dimension:
                if (CssLength.TryParseUnit(t.Unit, out var unit)) return CalcLeaf.Length(new CssLength(t.Number, unit));
                if (CssUnits.TryAngleToDegrees(t.Number, t.Unit, out var deg)) return CalcLeaf.Angle(deg, t.Unit);
                if (CssUnits.TryTimeToMilliseconds(t.Number, t.Unit, out var ms)) return CalcLeaf.Time(ms, t.Unit);
                return null;
            default:
                return null;
        }
    }

    static CalcNode? ParseFunction(CssFunction f, PercentMode percent, int depth, ref int nodes)
    {
        if (depth >= MaxDepth) return null;
        var name = CssSerializer.ToAsciiLower(f.Name);
        var args = SplitCommas(f.Arguments);
        switch (name)
        {
            case "calc":
            case "-webkit-calc":
            {
                if (args.Count != 1) return null;
                var inner = ParseSum(args[0], percent, depth + 1, ref nodes);
                return inner is null ? null : new CalcOperation(CalcOperation.Op.Calc, inner.Category, new[] { inner });
            }
            case "min":
            case "max":
            {
                if (args.Count < 1) return null;
                var ops = new CalcNode[args.Count];
                for (int i = 0; i < args.Count; i++)
                {
                    var n = ParseSum(args[i], percent, depth + 1, ref nodes);
                    if (n is null) return null;
                    ops[i] = n;
                }
                var cat = Combine(ops);
                return cat is null ? null : new CalcOperation(name == "min" ? CalcOperation.Op.Min : CalcOperation.Op.Max, cat.Value, ops);
            }
            case "clamp":
            {
                if (args.Count != 3) return null;
                var ops = new CalcNode[3];
                for (int i = 0; i < 3; i++)
                {
                    var n = ParseSum(args[i], percent, depth + 1, ref nodes);
                    if (n is null) return null;
                    ops[i] = n;
                }
                var cat = Combine(ops);
                return cat is null ? null : new CalcOperation(CalcOperation.Op.Clamp, cat.Value, ops);
            }
            default:
                return null;
        }
    }

    static List<List<CssComponentValue>> SplitCommas(IReadOnlyList<CssComponentValue> args)
    {
        var result = new List<List<CssComponentValue>>();
        var current = new List<CssComponentValue>();
        foreach (var a in args)
        {
            if (a is CssToken { Type: CssTokenType.Comma })
            {
                result.Add(current);
                current = new List<CssComponentValue>();
                continue;
            }
            current.Add(a);
        }
        result.Add(current);
        return result;
    }

    /// <summary>&lt;calc-sum&gt; = &lt;calc-product&gt; [ [ '+' | '-' ] &lt;calc-product&gt; ]*，运算符两侧必须有空白。</summary>
    static CalcNode? ParseSum(IReadOnlyList<CssComponentValue> list, PercentMode percent, int depth, ref int nodes)
    {
        int i = 0;
        SkipWs(list, ref i);
        var first = ParseProduct(list, ref i, percent, depth, ref nodes);
        if (first is null) return null;
        List<CalcNode>? terms = null;
        while (true)
        {
            int save = i;
            bool ws = SkipWs(list, ref i);
            if (i >= list.Count) break;
            if (!ws || list[i] is not CssToken op || !(op.IsDelim('+') || op.IsDelim('-'))) { i = save; return null; }
            i++;
            if (!SkipWs(list, ref i)) return null;
            var next = ParseProduct(list, ref i, percent, depth, ref nodes);
            if (next is null) return null;
            if (op.IsDelim('-'))
            {
                if (++nodes > MaxNodes) return null;
                next = new CalcOperation(CalcOperation.Op.Negate, next.Category, new[] { next });
            }
            terms ??= new List<CalcNode> { first };
            terms.Add(next);
        }
        if (terms is null) return first;
        var arr = terms.ToArray();
        var cat = Combine(arr);
        if (cat is null || ++nodes > MaxNodes) return null;
        return new CalcOperation(CalcOperation.Op.Sum, cat.Value, arr);
    }

    /// <summary>&lt;calc-product&gt; = &lt;calc-value&gt; [ [ '*' | '/' ] &lt;calc-value&gt; ]*。</summary>
    static CalcNode? ParseProduct(IReadOnlyList<CssComponentValue> list, ref int i, PercentMode percent, int depth, ref int nodes)
    {
        var first = ParseValue(list, ref i, percent, depth, ref nodes);
        if (first is null) return null;
        List<CalcNode>? factors = null;
        while (true)
        {
            int save = i;
            SkipWs(list, ref i);
            if (i >= list.Count || list[i] is not CssToken op || !(op.IsDelim('*') || op.IsDelim('/'))) { i = save; break; }
            i++;
            SkipWs(list, ref i);
            var next = ParseValue(list, ref i, percent, depth, ref nodes);
            if (next is null) return null;
            if (op.IsDelim('/'))
            {
                // 除数必须是数（CSS Values 4 的类型规则）
                if (next.Category != CalcCategory.Number || ++nodes > MaxNodes) return null;
                next = new CalcOperation(CalcOperation.Op.Invert, CalcCategory.Number, new[] { next });
            }
            factors ??= new List<CalcNode> { first };
            factors.Add(next);
        }
        if (factors is null) return first;
        CalcCategory category = CalcCategory.Number;
        bool seenDimension = false;
        foreach (var f in factors)
        {
            if (f.Category == CalcCategory.Number) continue;
            if (seenDimension) return null;
            seenDimension = true;
            category = f.Category;
        }
        if (++nodes > MaxNodes) return null;
        return new CalcOperation(CalcOperation.Op.Product, category, factors.ToArray());
    }

    static CalcNode? ParseValue(IReadOnlyList<CssComponentValue> list, ref int i, PercentMode percent, int depth, ref int nodes)
    {
        if (i >= list.Count || ++nodes > MaxNodes) return null;
        var v = list[i++];
        switch (v)
        {
            case CssToken { Type: CssTokenType.Ident } id:
                switch (CssSerializer.ToAsciiLower(id.Value))
                {
                    case "pi": return CalcLeaf.Number(Math.PI);
                    case "e": return CalcLeaf.Number(Math.E);
                    case "infinity": return CalcLeaf.Number(double.PositiveInfinity);
                    case "-infinity": return CalcLeaf.Number(double.NegativeInfinity);
                    case "nan": return CalcLeaf.Number(double.NaN);
                    default: return null;
                }
            case CssToken t:
                // calc 内部的数 0 不是长度
                return ParseToken(t, percent, allowUnitlessZero: false);
            case CssSimpleBlock { Open: '(' } block:
            {
                if (depth + 1 >= MaxDepth) return null;
                var inner = ParseSum(block.Values, percent, depth + 1, ref nodes);
                return inner;
            }
            case CssFunction f when IsMathFunction(f.Name):
                return ParseFunction(f, percent, depth + 1, ref nodes);
            default:
                return null;
        }
    }

    /// <summary>加法/比较类运算的结果类别；类型不兼容返回 null。</summary>
    static CalcCategory? Combine(CalcNode[] ops)
    {
        var cat = ops[0].Category;
        for (int i = 1; i < ops.Length; i++)
        {
            var c = ops[i].Category;
            if (c == cat) continue;
            if (IsLengthLike(c) && IsLengthLike(cat)) { cat = CalcCategory.LengthPercent; continue; }
            return null;
        }
        return cat;
    }

    static bool IsLengthLike(CalcCategory c) => c is CalcCategory.Length or CalcCategory.Percent or CalcCategory.LengthPercent;

    static bool SkipWs(IReadOnlyList<CssComponentValue> list, ref int i)
    {
        bool any = false;
        while (i < list.Count && list[i] is CssToken { Type: CssTokenType.Whitespace })
        {
            i++;
            any = true;
        }
        return any;
    }
}
