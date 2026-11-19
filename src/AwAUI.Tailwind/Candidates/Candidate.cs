using System.Collections.Generic;

namespace AwAUI.Tailwind.Candidates;

internal enum CandidateKind : byte
{
    /// <summary>无值工具，如 <c>flex</c>。</summary>
    Static,
    /// <summary>带值工具，如 <c>p-4</c>、<c>bg-red-500/50</c>、<c>w-[200px]</c>。</summary>
    Functional,
    /// <summary>任意属性，如 <c>[mask-type:luminance]</c>。</summary>
    Arbitrary,
}

/// <summary>候选值：具名（<c>4</c>、<c>red-500</c>，可带分数）或任意值（<c>[200px]</c>，可带类型提示）。</summary>
internal sealed class CandidateValue
{
    public bool IsArbitrary;
    public string Value = "";
    /// <summary>具名值的分数形式（<c>1/2</c>），只有修饰符是具名时才有。</summary>
    public string? Fraction;
    /// <summary>任意值的类型提示（<c>[length:...]</c>）。</summary>
    public string? DataType;
}

/// <summary>修饰符：<c>/50</c>、<c>/[0.3]</c>、<c>/(--x)</c>。</summary>
internal sealed class CandidateModifier
{
    public bool IsArbitrary;
    public string Value = "";
}

/// <summary>解析后的候选类名（移植自 Tailwind <c>candidate.ts</c>）。</summary>
internal sealed class Candidate
{
    public CandidateKind Kind;
    public string Root = "";
    public CandidateValue? Value;
    public CandidateModifier? Modifier;
    /// <summary>任意属性的属性名（<see cref="CandidateKind.Arbitrary"/>）。</summary>
    public string Property = "";
    /// <summary>任意属性的值（<see cref="CandidateKind.Arbitrary"/>）。</summary>
    public string ArbitraryValue = "";
    /// <summary>变体，按从内到外的顺序（最右边的变体在前）。</summary>
    public List<Variant> Variants = new();
    public bool Important;
    public string Raw = "";
}

internal enum VariantKind : byte
{
    Arbitrary,
    Static,
    Functional,
    Compound,
}

/// <summary>解析后的变体。</summary>
internal sealed class Variant
{
    public VariantKind Kind;
    public string Root = "";
    /// <summary>任意变体的选择器（<c>&amp;_svg</c> 解码后为 <c>&amp; svg</c>）。</summary>
    public string Selector = "";
    /// <summary>任意变体是否为相对选择器（以 <c>&gt; + ~</c> 开头）。</summary>
    public bool Relative;
    /// <summary>函数式变体的值；<see cref="CandidateValue.IsArbitrary"/> 表示 <c>[...]</c>。</summary>
    public CandidateValue? Value;
    public CandidateModifier? Modifier;
    /// <summary>复合变体包裹的内层变体。</summary>
    public Variant? Inner;
}
