using System;
using System.Collections.Generic;
using System.Text;
using AwAUI.Css.Syntax;

namespace AwAUI.Css.Selectors;

/// <summary>组合器。</summary>
public enum Combinator : byte
{
    /// <summary>后代（空白）。</summary>
    Descendant,
    /// <summary>子（<c>&gt;</c>）。</summary>
    Child,
    /// <summary>紧邻兄弟（<c>+</c>）。</summary>
    NextSibling,
    /// <summary>后续兄弟（<c>~</c>）。</summary>
    SubsequentSibling,
}

/// <summary>简单选择器的种类。</summary>
public enum SimpleSelectorKind : byte
{
    Type,
    Universal,
    Id,
    Class,
    Attribute,
    PseudoClass,
    Nth,
    Logical,
    Nesting,
    PseudoElement,
}

/// <summary>选择器列表（逗号分隔）。</summary>
public sealed class SelectorList
{
    internal static readonly SelectorList EmptyList = new(Array.Empty<ComplexSelector>());

    internal SelectorList(IReadOnlyList<ComplexSelector> selectors)
    {
        Selectors = selectors;
        var max = Specificity.Zero;
        int depth = 0;
        bool memo = false;
        foreach (var s in selectors)
        {
            max = Specificity.Max(max, s.Specificity);
            depth = Math.Max(depth, s.MatchDepth);
            if (s.Compounds.Count > 1) memo = true;
        }
        MaxSpecificity = max;
        MatchDepth = depth;
        NeedsMemo = memo;
    }

    /// <summary>
    /// 匹配该列表时递归深度的上界（复合选择器个数与嵌套的 <c>:is()</c>/<c>:not()</c>/<c>of S</c>/<c>&amp;</c> 层数之和）。
    /// 解析器据此拒绝过深的选择器，保证匹配不会栈溢出。
    /// </summary>
    internal int MatchDepth { get; }

    /// <summary>
    /// 列表中有带组合器的选择器：作为 <c>:is()</c>/<c>&amp;</c> 等的参数时，一次匹配内按 (列表, 元素) 记忆结果，
    /// 避免嵌套组合器在祖先链上重复求值导致指数级耗时。
    /// </summary>
    internal bool NeedsMemo { get; }

    public IReadOnlyList<ComplexSelector> Selectors { get; }

    public int Count => Selectors.Count;

    public ComplexSelector this[int index] => Selectors[index];

    /// <summary>列表中最大的特异性（<c>:is()</c>、<c>:not()</c> 与 <c>&amp;</c> 的特异性）。</summary>
    public Specificity MaxSpecificity { get; }

    public void WriteCss(StringBuilder sb)
    {
        for (int i = 0; i < Selectors.Count; i++)
        {
            if (i > 0) sb.Append(", ");
            Selectors[i].WriteCss(sb);
        }
    }

    public override string ToString()
    {
        var sb = new StringBuilder();
        WriteCss(sb);
        return sb.ToString();
    }
}

/// <summary>
/// 复杂选择器：若干复合选择器由组合器连接。<see cref="Compounds"/> 按书写顺序（从左到右），
/// <c>Combinators[i]</c> 连接 <c>Compounds[i]</c> 与 <c>Compounds[i + 1]</c>。匹配从最右侧（主体）开始。
/// </summary>
public sealed class ComplexSelector
{
    internal ComplexSelector(IReadOnlyList<CompoundSelector> compounds, IReadOnlyList<Combinator> combinators, bool implicitNesting,
        bool explicitLeadingCombinator, SourceRange range)
    {
        Compounds = compounds;
        Combinators = combinators;
        HasImplicitNesting = implicitNesting;
        HasExplicitLeadingCombinator = explicitLeadingCombinator;
        Range = range;
        var spec = Specificity.Zero;
        int inner = 0;
        foreach (var c in compounds)
        {
            spec += c.Specificity;
            foreach (var s in c.Selectors) inner = Math.Max(inner, s.InnerMatchDepth);
            foreach (var s in c.PseudoElementStates) inner = Math.Max(inner, s.InnerMatchDepth);
        }
        Specificity = spec;
        MatchDepth = compounds.Count + inner;
    }

    /// <summary>匹配递归深度的上界，见 <see cref="SelectorList.MatchDepth"/>。</summary>
    internal int MatchDepth { get; }

    public IReadOnlyList<CompoundSelector> Compounds { get; }

    public IReadOnlyList<Combinator> Combinators { get; }

    /// <summary>主体复合选择器（最右侧）。</summary>
    public CompoundSelector Subject => Compounds[Compounds.Count - 1];

    /// <summary>主体上的伪元素；没有则为 null。</summary>
    public PseudoElementSelector? PseudoElement => Subject.PseudoElement;

    public Specificity Specificity { get; }

    /// <summary>嵌套规则中补上了隐式的 <c>&amp;</c>（第一个复合选择器是隐式的）。</summary>
    public bool HasImplicitNesting { get; }

    /// <summary>嵌套规则中以组合器开头的相对选择器（如 <c>&gt; .child</c>）。</summary>
    public bool HasExplicitLeadingCombinator { get; }

    public SourceRange Range { get; }

    /// <summary>按书写形式序列化（不输出隐式的 <c>&amp;</c>）。</summary>
    public void WriteCss(StringBuilder sb) => Write(sb, resolved: false);

    /// <summary>按解析后的形式序列化（隐式 <c>&amp;</c> 也输出）。</summary>
    public string ToResolvedString()
    {
        var sb = new StringBuilder();
        Write(sb, resolved: true);
        return sb.ToString();
    }

    void Write(StringBuilder sb, bool resolved)
    {
        int start = 0;
        if (HasImplicitNesting && !resolved)
        {
            start = 1;
            if (HasExplicitLeadingCombinator)
            {
                sb.Append(CombinatorText(Combinators[0]).Trim());
                sb.Append(' ');
            }
        }
        for (int i = start; i < Compounds.Count; i++)
        {
            if (i > start) sb.Append(CombinatorText(Combinators[i - 1]));
            Compounds[i].WriteCss(sb);
        }
    }

    internal static string CombinatorText(Combinator c) => c switch
    {
        Combinator.Child => " > ",
        Combinator.NextSibling => " + ",
        Combinator.SubsequentSibling => " ~ ",
        _ => " ",
    };

    public override string ToString()
    {
        var sb = new StringBuilder();
        WriteCss(sb);
        return sb.ToString();
    }
}

/// <summary>
/// 复合选择器：无组合器分隔的一串简单选择器。伪元素及其后的伪类单独存放：
/// <see cref="Selectors"/> 作用于原始元素（<c>::part()</c> 时为部件的宿主），<see cref="PseudoElementStates"/> 作用于伪元素本身。
/// </summary>
public sealed class CompoundSelector
{
    internal static readonly IReadOnlyList<SimpleSelector> NoSelectors = Array.Empty<SimpleSelector>();

    internal CompoundSelector(IReadOnlyList<SimpleSelector> selectors, PseudoElementSelector? pseudoElement,
        IReadOnlyList<SimpleSelector> pseudoElementStates, bool isImplicit)
    {
        Selectors = selectors;
        PseudoElement = pseudoElement;
        PseudoElementStates = pseudoElementStates;
        IsImplicit = isImplicit;
        var spec = Specificity.Zero;
        foreach (var s in selectors) spec += s.Specificity;
        if (pseudoElement is not null) spec += pseudoElement.Specificity;
        foreach (var s in pseudoElementStates) spec += s.Specificity;
        Specificity = spec;
    }

    public IReadOnlyList<SimpleSelector> Selectors { get; }

    public PseudoElementSelector? PseudoElement { get; }

    /// <summary>伪元素之后的伪类（如 <c>::part(label):hover</c> 中的 <c>:hover</c>）。</summary>
    public IReadOnlyList<SimpleSelector> PseudoElementStates { get; }

    /// <summary>是否为嵌套规则补上的隐式 <c>&amp;</c>。</summary>
    public bool IsImplicit { get; }

    public Specificity Specificity { get; }

    public void WriteCss(StringBuilder sb)
    {
        foreach (var s in Selectors) s.WriteCss(sb);
        PseudoElement?.WriteCss(sb);
        foreach (var s in PseudoElementStates) s.WriteCss(sb);
    }

    public override string ToString()
    {
        var sb = new StringBuilder();
        WriteCss(sb);
        return sb.ToString();
    }
}

/// <summary>简单选择器基类。层级是封闭的：匹配逻辑由引擎实现。</summary>
public abstract class SimpleSelector
{
    private protected SimpleSelector()
    {
    }

    public abstract SimpleSelectorKind Kind { get; }

    public abstract Specificity Specificity { get; }

    public abstract void WriteCss(StringBuilder sb);

    internal abstract bool Matches(StyleElement element, ref MatchContext context);

    /// <summary>该简单选择器内部嵌套的选择器列表带来的额外匹配递归深度（见 <see cref="SelectorList.MatchDepth"/>）。</summary>
    internal virtual int InnerMatchDepth => 0;

    public override string ToString()
    {
        var sb = new StringBuilder();
        WriteCss(sb);
        return sb.ToString();
    }
}

/// <summary>类型选择器，如 <c>Div</c>。与元素 Tag 按 ASCII 大小写不敏感匹配（<c>input</c> 匹配 <c>Input</c>）。</summary>
public sealed class TypeSelector : SimpleSelector
{
    internal TypeSelector(string name) => Name = name;

    public override SimpleSelectorKind Kind => SimpleSelectorKind.Type;

    public string Name { get; }

    public override Specificity Specificity => new(0, 0, 1);

    public override void WriteCss(StringBuilder sb) => CssSerializer.WriteIdent(sb, Name);

    internal override bool Matches(StyleElement element, ref MatchContext context) => AsciiCase.EqualsIgnoreCase(element.Tag, Name);
}

/// <summary>通配选择器 <c>*</c>。</summary>
public sealed class UniversalSelector : SimpleSelector
{
    internal static readonly UniversalSelector Instance = new();

    UniversalSelector()
    {
    }

    public override SimpleSelectorKind Kind => SimpleSelectorKind.Universal;

    public override Specificity Specificity => Specificity.Zero;

    public override void WriteCss(StringBuilder sb) => sb.Append('*');

    internal override bool Matches(StyleElement element, ref MatchContext context) => true;
}

/// <summary>ID 选择器 <c>#id</c>（大小写敏感）。</summary>
public sealed class IdSelector : SimpleSelector
{
    internal IdSelector(string id) => Id = id;

    public override SimpleSelectorKind Kind => SimpleSelectorKind.Id;

    public string Id { get; }

    public override Specificity Specificity => new(1, 0, 0);

    public override void WriteCss(StringBuilder sb)
    {
        sb.Append('#');
        CssSerializer.WriteIdent(sb, Id);
    }

    internal override bool Matches(StyleElement element, ref MatchContext context) => string.Equals(element.Id, Id, StringComparison.Ordinal);
}

/// <summary>类选择器 <c>.name</c>（大小写敏感）。</summary>
public sealed class ClassSelector : SimpleSelector
{
    internal ClassSelector(string name) => Name = name;

    public override SimpleSelectorKind Kind => SimpleSelectorKind.Class;

    public string Name { get; }

    public override Specificity Specificity => new(0, 1, 0);

    public override void WriteCss(StringBuilder sb)
    {
        sb.Append('.');
        CssSerializer.WriteIdent(sb, Name);
    }

    internal override bool Matches(StyleElement element, ref MatchContext context) => element.HasClass(Name);
}

/// <summary>属性选择器的运算符。</summary>
public enum AttributeOperator : byte
{
    /// <summary><c>[attr]</c></summary>
    Exists,
    /// <summary><c>[attr=v]</c></summary>
    Equals,
    /// <summary><c>[attr~=v]</c>：空白分隔的列表包含 v。</summary>
    Includes,
    /// <summary><c>[attr|=v]</c>：等于 v 或以 <c>v-</c> 开头。</summary>
    DashMatch,
    /// <summary><c>[attr^=v]</c></summary>
    Prefix,
    /// <summary><c>[attr$=v]</c></summary>
    Suffix,
    /// <summary><c>[attr*=v]</c></summary>
    Substring,
}

/// <summary>属性值的大小写敏感性标志。</summary>
public enum AttributeCaseSensitivity : byte
{
    /// <summary>未指定：大小写敏感。</summary>
    Default,
    /// <summary><c>i</c>：ASCII 大小写不敏感。</summary>
    Insensitive,
    /// <summary><c>s</c>：大小写敏感。</summary>
    Sensitive,
}

/// <summary>属性选择器。属性名按书写原样交给 <see cref="StyleElement.GetAttribute"/>。</summary>
public sealed class AttributeSelector : SimpleSelector
{
    internal AttributeSelector(string name, AttributeOperator op, string value, AttributeCaseSensitivity caseSensitivity)
    {
        Name = name;
        Operator = op;
        Value = value;
        CaseSensitivity = caseSensitivity;
    }

    public override SimpleSelectorKind Kind => SimpleSelectorKind.Attribute;

    public string Name { get; }

    public AttributeOperator Operator { get; }

    public string Value { get; }

    public AttributeCaseSensitivity CaseSensitivity { get; }

    public override Specificity Specificity => new(0, 1, 0);

    public override void WriteCss(StringBuilder sb)
    {
        sb.Append('[');
        CssSerializer.WriteIdent(sb, Name);
        if (Operator != AttributeOperator.Exists)
        {
            sb.Append(Operator switch
            {
                AttributeOperator.Equals => "=",
                AttributeOperator.Includes => "~=",
                AttributeOperator.DashMatch => "|=",
                AttributeOperator.Prefix => "^=",
                AttributeOperator.Suffix => "$=",
                _ => "*=",
            });
            CssSerializer.WriteString(sb, Value);
            if (CaseSensitivity == AttributeCaseSensitivity.Insensitive) sb.Append(" i");
            else if (CaseSensitivity == AttributeCaseSensitivity.Sensitive) sb.Append(" s");
        }
        sb.Append(']');
    }

    internal override bool Matches(StyleElement element, ref MatchContext context)
    {
        var actual = element.GetAttribute(Name);
        if (actual is null) return false;
        bool ic = CaseSensitivity == AttributeCaseSensitivity.Insensitive;
        switch (Operator)
        {
            case AttributeOperator.Exists:
                return true;
            case AttributeOperator.Equals:
                return actual.Length == Value.Length && AsciiCase.RegionEquals(actual, 0, Value, 0, Value.Length, ic);
            case AttributeOperator.Includes:
            {
                if (Value.Length == 0 || ContainsWhitespace(Value)) return false;
                int i = 0;
                while (i < actual.Length)
                {
                    while (i < actual.Length && IsWhitespace(actual[i])) i++;
                    int start = i;
                    while (i < actual.Length && !IsWhitespace(actual[i])) i++;
                    if (i - start == Value.Length && AsciiCase.RegionEquals(actual, start, Value, 0, Value.Length, ic)) return true;
                }
                return false;
            }
            case AttributeOperator.DashMatch:
                return AsciiCase.StartsWith(actual, Value, ic) && (actual.Length == Value.Length || actual[Value.Length] == '-');
            case AttributeOperator.Prefix:
                return Value.Length > 0 && AsciiCase.StartsWith(actual, Value, ic);
            case AttributeOperator.Suffix:
                return Value.Length > 0 && AsciiCase.EndsWith(actual, Value, ic);
            default:
                return Value.Length > 0 && AsciiCase.Contains(actual, Value, ic);
        }
    }

    static bool IsWhitespace(char c) => c == ' ' || c == '\t' || c == '\n' || c == '\r' || c == '\f';

    static bool ContainsWhitespace(string s)
    {
        foreach (char c in s)
            if (IsWhitespace(c)) return true;
        return false;
    }
}

/// <summary>受支持的关键字伪类（design/08 的支持范围）。</summary>
public enum PseudoClass : byte
{
    Hover,
    Active,
    Focus,
    FocusVisible,
    FocusWithin,
    Disabled,
    Enabled,
    Checked,
    PlaceholderShown,
    FirstChild,
    LastChild,
    OnlyChild,
    FirstOfType,
    LastOfType,
    OnlyOfType,
    Empty,
    Root,
    Scope,
}

/// <summary>关键字伪类，如 <c>:hover</c>、<c>:first-child</c>。</summary>
public sealed class PseudoClassSelector : SimpleSelector
{
    internal PseudoClassSelector(PseudoClass pseudoClass) => PseudoClass = pseudoClass;

    public override SimpleSelectorKind Kind => SimpleSelectorKind.PseudoClass;

    public PseudoClass PseudoClass { get; }

    public override Specificity Specificity => new(0, 1, 0);

    /// <summary>是否为用户交互/元素状态类伪类（可出现在 <c>::part()</c> 之后）。</summary>
    public bool IsStatePseudoClass => PseudoClass <= PseudoClass.PlaceholderShown;

    internal static string NameOf(PseudoClass pc) => pc switch
    {
        PseudoClass.Hover => "hover",
        PseudoClass.Active => "active",
        PseudoClass.Focus => "focus",
        PseudoClass.FocusVisible => "focus-visible",
        PseudoClass.FocusWithin => "focus-within",
        PseudoClass.Disabled => "disabled",
        PseudoClass.Enabled => "enabled",
        PseudoClass.Checked => "checked",
        PseudoClass.PlaceholderShown => "placeholder-shown",
        PseudoClass.FirstChild => "first-child",
        PseudoClass.LastChild => "last-child",
        PseudoClass.OnlyChild => "only-child",
        PseudoClass.FirstOfType => "first-of-type",
        PseudoClass.LastOfType => "last-of-type",
        PseudoClass.OnlyOfType => "only-of-type",
        PseudoClass.Empty => "empty",
        PseudoClass.Root => "root",
        _ => "scope",
    };

    public override void WriteCss(StringBuilder sb)
    {
        sb.Append(':');
        sb.Append(NameOf(PseudoClass));
    }

    internal override bool Matches(StyleElement e, ref MatchContext context)
    {
        switch (PseudoClass)
        {
            case PseudoClass.Hover: return (e.States & PseudoStates.Hover) != 0;
            case PseudoClass.Active: return (e.States & PseudoStates.Active) != 0;
            case PseudoClass.Focus: return (e.States & PseudoStates.Focus) != 0;
            case PseudoClass.FocusVisible: return (e.States & PseudoStates.FocusVisible) != 0;
            case PseudoClass.FocusWithin: return (e.States & PseudoStates.FocusWithin) != 0;
            case PseudoClass.Disabled: return (e.States & PseudoStates.Disabled) != 0;
            case PseudoClass.Enabled: return e.CanBeDisabled && (e.States & PseudoStates.Disabled) == 0;
            case PseudoClass.Checked: return (e.States & PseudoStates.Checked) != 0;
            case PseudoClass.PlaceholderShown: return (e.States & PseudoStates.PlaceholderShown) != 0;
            case PseudoClass.FirstChild: return e.PreviousSibling is null;
            case PseudoClass.LastChild: return e.NextSibling is null;
            case PseudoClass.OnlyChild: return e.PreviousSibling is null && e.NextSibling is null;
            case PseudoClass.FirstOfType: return NthSelector.SameTypeSibling(e, forward: false) is null;
            case PseudoClass.LastOfType: return NthSelector.SameTypeSibling(e, forward: true) is null;
            case PseudoClass.OnlyOfType: return NthSelector.SameTypeSibling(e, false) is null && NthSelector.SameTypeSibling(e, true) is null;
            case PseudoClass.Empty: return e.IsEmpty;
            default: return e.IsRoot; // :root、:scope（样式表上下文中 :scope 即根）
        }
    }
}

/// <summary><c>:nth-*()</c> 伪类的种类。</summary>
public enum NthKind : byte
{
    Child,
    LastChild,
    OfType,
    LastOfType,
}

/// <summary><c>:nth-child(An+B [of S])</c>、<c>:nth-last-child()</c>、<c>:nth-of-type()</c>、<c>:nth-last-of-type()</c>。</summary>
public sealed class NthSelector : SimpleSelector
{
    internal NthSelector(NthKind nthKind, int a, int b, SelectorList? of)
    {
        NthKind = nthKind;
        A = a;
        B = b;
        Of = of;
    }

    public override SimpleSelectorKind Kind => SimpleSelectorKind.Nth;

    public NthKind NthKind { get; }

    public int A { get; }

    public int B { get; }

    /// <summary><c>of S</c> 子句；没有则为 null。</summary>
    public SelectorList? Of { get; }

    public override Specificity Specificity => new Specificity(0, 1, 0) + (Of?.MaxSpecificity ?? Specificity.Zero);

    internal override int InnerMatchDepth => Of is null ? 0 : Of.MatchDepth + 1;

    public override void WriteCss(StringBuilder sb)
    {
        sb.Append(NthKind switch
        {
            NthKind.Child => ":nth-child(",
            NthKind.LastChild => ":nth-last-child(",
            NthKind.OfType => ":nth-of-type(",
            _ => ":nth-last-of-type(",
        });
        sb.Append(FormatAnB(A, B));
        if (Of is not null)
        {
            sb.Append(" of ");
            Of.WriteCss(sb);
        }
        sb.Append(')');
    }

    /// <summary>按 CSSOM 规则序列化 An+B。</summary>
    public static string FormatAnB(int a, int b)
    {
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        if (a == 0) return b.ToString(inv);
        string s = a == 1 ? "n" : a == -1 ? "-n" : a.ToString(inv) + "n";
        if (b > 0) s += "+" + b.ToString(inv);
        else if (b < 0) s += b.ToString(inv);
        return s;
    }

    /// <summary>1 基位置 <paramref name="index"/> 是否满足 An+B（存在 n ≥ 0）。</summary>
    public static bool MatchesIndex(int a, int b, int index)
    {
        long diff = (long)index - b;
        if (a == 0) return diff == 0;
        return diff % a == 0 && diff / a >= 0;
    }

    internal override bool Matches(StyleElement e, ref MatchContext context)
    {
        if (Of is not null && !SelectorMatcher.MatchesAny(Of, e, ref context)) return false;
        int index;
        switch (NthKind)
        {
            case NthKind.Child:
                if (Of is null) { index = e.GetChildIndex(); break; }
                index = 1;
                for (var s = e.PreviousSibling; s is not null; s = s.PreviousSibling)
                    if (SelectorMatcher.MatchesAny(Of, s, ref context)) index++;
                break;
            case NthKind.LastChild:
                if (Of is null) { index = e.GetChildIndexFromEnd(); break; }
                index = 1;
                for (var s = e.NextSibling; s is not null; s = s.NextSibling)
                    if (SelectorMatcher.MatchesAny(Of, s, ref context)) index++;
                break;
            case NthKind.OfType:
                index = 1;
                for (var s = e.PreviousSibling; s is not null; s = s.PreviousSibling)
                    if (AsciiCase.EqualsIgnoreCase(s.Tag, e.Tag)) index++;
                break;
            default:
                index = 1;
                for (var s = e.NextSibling; s is not null; s = s.NextSibling)
                    if (AsciiCase.EqualsIgnoreCase(s.Tag, e.Tag)) index++;
                break;
        }
        return MatchesIndex(A, B, index);
    }

    internal static StyleElement? SameTypeSibling(StyleElement e, bool forward)
    {
        for (var s = forward ? e.NextSibling : e.PreviousSibling; s is not null; s = forward ? s.NextSibling : s.PreviousSibling)
            if (AsciiCase.EqualsIgnoreCase(s.Tag, e.Tag)) return s;
        return null;
    }
}

/// <summary>逻辑伪类的种类。</summary>
public enum LogicalKind : byte
{
    Not,
    Is,
    Where,
}

/// <summary><c>:not()</c>、<c>:is()</c>、<c>:where()</c>。</summary>
public sealed class LogicalSelector : SimpleSelector
{
    internal LogicalSelector(LogicalKind logicalKind, SelectorList arguments)
    {
        LogicalKind = logicalKind;
        Arguments = arguments;
    }

    public override SimpleSelectorKind Kind => SimpleSelectorKind.Logical;

    public LogicalKind LogicalKind { get; }

    /// <summary>参数列表。<c>:is()</c> 与 <c>:where()</c> 是宽容列表，无效项已被丢弃（可能为空，此时不匹配任何元素）。</summary>
    public SelectorList Arguments { get; }

    public override Specificity Specificity => LogicalKind == LogicalKind.Where ? Specificity.Zero : Arguments.MaxSpecificity;

    internal override int InnerMatchDepth => Arguments.MatchDepth + 1;

    public override void WriteCss(StringBuilder sb)
    {
        sb.Append(LogicalKind switch { LogicalKind.Not => ":not(", LogicalKind.Is => ":is(", _ => ":where(" });
        Arguments.WriteCss(sb);
        sb.Append(')');
    }

    internal override bool Matches(StyleElement element, ref MatchContext context)
    {
        bool any = SelectorMatcher.MatchesAny(Arguments, element, ref context);
        return LogicalKind == LogicalKind.Not ? !any : any;
    }
}

/// <summary>
/// 嵌套选择器 <c>&amp;</c>。嵌套规则中绑定到父规则的选择器列表，行为与特异性同 <c>:is(父选择器)</c>；
/// 不在嵌套规则中时等同 <c>:scope</c>（根元素），特异性为 0。
/// </summary>
public sealed class NestingSelector : SimpleSelector
{
    internal NestingSelector(SelectorList? parent) => Parent = parent;

    public override SimpleSelectorKind Kind => SimpleSelectorKind.Nesting;

    /// <summary>绑定的父选择器列表；顶层为 null。</summary>
    public SelectorList? Parent { get; }

    public override Specificity Specificity => Parent?.MaxSpecificity ?? Specificity.Zero;

    internal override int InnerMatchDepth => Parent is null ? 0 : Parent.MatchDepth + 1;

    public override void WriteCss(StringBuilder sb) => sb.Append('&');

    internal override bool Matches(StyleElement element, ref MatchContext context)
        => Parent is null ? element.IsRoot : SelectorMatcher.MatchesAny(Parent, element, ref context);
}

/// <summary>受支持的伪元素。</summary>
public enum PseudoElementKind : byte
{
    /// <summary>不针对伪元素（普通元素）。</summary>
    None,
    Before,
    After,
    Placeholder,
    Selection,
    /// <summary><c>::part(name …)</c>：匹配组件内部暴露为 part 的元素本身。</summary>
    Part,
}

/// <summary>伪元素选择器。</summary>
public sealed class PseudoElementSelector : SimpleSelector
{
    internal static readonly IReadOnlyList<string> NoParts = Array.Empty<string>();

    internal PseudoElementSelector(PseudoElementKind pseudoElement, IReadOnlyList<string> partNames, bool legacySyntax)
    {
        PseudoElement = pseudoElement;
        PartNames = partNames;
        IsLegacySyntax = legacySyntax;
    }

    public override SimpleSelectorKind Kind => SimpleSelectorKind.PseudoElement;

    public PseudoElementKind PseudoElement { get; }

    /// <summary><c>::part()</c> 的名称列表。</summary>
    public IReadOnlyList<string> PartNames { get; }

    /// <summary>是否以 CSS2 的单冒号形式书写（<c>:before</c>）。</summary>
    public bool IsLegacySyntax { get; }

    public override Specificity Specificity => new(0, 0, 1);

    public override void WriteCss(StringBuilder sb)
    {
        switch (PseudoElement)
        {
            case PseudoElementKind.Before: sb.Append("::before"); break;
            case PseudoElementKind.After: sb.Append("::after"); break;
            case PseudoElementKind.Placeholder: sb.Append("::placeholder"); break;
            case PseudoElementKind.Selection: sb.Append("::selection"); break;
            default:
                sb.Append("::part(");
                for (int i = 0; i < PartNames.Count; i++)
                {
                    if (i > 0) sb.Append(' ');
                    CssSerializer.WriteIdent(sb, PartNames[i]);
                }
                sb.Append(')');
                break;
        }
    }

    /// <summary>对 <c>::part()</c>：元素是否暴露了全部名称。其他伪元素由匹配器按目标种类判定。</summary>
    internal override bool Matches(StyleElement element, ref MatchContext context)
    {
        if (PseudoElement != PseudoElementKind.Part) return true;
        var parts = element.Parts;
        foreach (var name in PartNames)
        {
            bool found = false;
            for (int i = 0; i < parts.Count; i++)
                if (string.Equals(parts[i], name, StringComparison.Ordinal)) { found = true; break; }
            if (!found) return false;
        }
        return true;
    }
}
