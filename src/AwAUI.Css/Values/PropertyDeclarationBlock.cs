using System;
using System.Collections.Generic;
using System.Text;
using AwAUI.Css.Syntax;

namespace AwAUI.Css.Values;

/// <summary>一条解析后的长属性声明（简写已展开）或自定义属性声明。</summary>
public readonly struct ParsedDeclaration
{
    internal ParsedDeclaration(CssPropertyId id, string? customName, CssValue value, CssDeclaration source)
    {
        Id = id;
        CustomName = customName;
        Value = value;
        Source = source;
    }

    /// <summary>长属性 ID；自定义属性时无意义。</summary>
    public CssPropertyId Id { get; }

    /// <summary>自定义属性名（<c>--x</c>）；普通属性为 null。</summary>
    public string? CustomName { get; }

    public bool IsCustom => CustomName is not null;

    /// <summary>指定值。</summary>
    public CssValue Value { get; }

    /// <summary>来源声明（简写展开出的各长属性共享同一来源）。</summary>
    public CssDeclaration Source { get; }

    public bool Important => Source.Important;

    /// <summary>属性名（长属性名或自定义属性名）。</summary>
    public string Name => CustomName ?? CssProperties.Get(Id).Name;

    public override string ToString() => Name + ": " + Value;
}

/// <summary>声明的处理结果。</summary>
public enum DeclarationStatus : byte
{
    /// <summary>参与级联并胜出。</summary>
    Active,
    /// <summary>参与级联但被更高优先级的声明覆盖。</summary>
    Overridden,
    /// <summary>值无效（解析期 AW2003，或计算值时无效）。</summary>
    Invalid,
    /// <summary>属性不被支持（AW2002）。</summary>
    Unsupported,
}

/// <summary>被丢弃的声明（无效值或不支持的属性），供 DevTools 标记。</summary>
public readonly struct RejectedDeclaration
{
    internal RejectedDeclaration(CssDeclaration source, DeclarationStatus status, string reason)
    {
        Source = source;
        Status = status;
        Reason = reason;
    }

    public CssDeclaration Source { get; }

    /// <summary><see cref="DeclarationStatus.Invalid"/> 或 <see cref="DeclarationStatus.Unsupported"/>。</summary>
    public DeclarationStatus Status { get; }

    public string Reason { get; }
}

/// <summary>
/// 一个声明块（规则或内联样式）的解析结果：每条声明按属性语法解析一次、简写展开为长属性，结果随规则缓存（design/08）。
/// 普通与 <c>!important</c> 声明分开存放，各自保持源顺序。
/// </summary>
public sealed class PropertyDeclarationBlock
{
    static readonly ParsedDeclaration[] NoDeclarations = Array.Empty<ParsedDeclaration>();

    internal static readonly PropertyDeclarationBlock Empty = new(NoDeclarations, NoDeclarations, Array.Empty<RejectedDeclaration>());

    PropertyDeclarationBlock(ParsedDeclaration[] normal, ParsedDeclaration[] important, RejectedDeclaration[] rejected)
    {
        Normal = normal;
        Important = important;
        Rejected = rejected;
    }

    /// <summary>普通声明（源顺序）。</summary>
    public IReadOnlyList<ParsedDeclaration> NormalDeclarations => Normal;

    /// <summary><c>!important</c> 声明（源顺序）。</summary>
    public IReadOnlyList<ParsedDeclaration> ImportantDeclarations => Important;

    /// <summary>被丢弃的声明。</summary>
    public IReadOnlyList<RejectedDeclaration> RejectedDeclarations => Rejected;

    internal ParsedDeclaration[] Normal { get; }

    internal ParsedDeclaration[] Important { get; }

    internal RejectedDeclaration[] Rejected { get; }

    public bool IsEmpty => Normal.Length == 0 && Important.Length == 0;

    /// <summary>解析声明列表。无效值报告 <see cref="CssDiagnosticCodes.InvalidValue"/>，不支持的属性报告 <see cref="CssDiagnosticCodes.UnsupportedProperty"/>。</summary>
    public static PropertyDeclarationBlock Parse(IReadOnlyList<CssDeclaration> declarations, List<CssDiagnostic>? diagnostics = null)
    {
        if (declarations is null) throw new ArgumentNullException(nameof(declarations));
        if (declarations.Count == 0) return Empty;
        List<ParsedDeclaration>? normal = null, important = null;
        List<RejectedDeclaration>? rejected = null;
        var scratch = new List<PropertyValue>();
        foreach (var d in declarations)
        {
            var target = d.Important ? (important ??= new List<ParsedDeclaration>()) : (normal ??= new List<ParsedDeclaration>());
            if (d.IsCustomProperty)
            {
                // 自定义属性的值只有恰好是一个全局关键字时才是关键字；其余原样保留
                CssValue custom = d.Value.Count == 1 && d.Value[0] is CssToken { Type: CssTokenType.Ident } k
                                  && CssWideKeywordValue.TryParse(k.Value, out var wide)
                    ? CssWideKeywordValue.Of(wide)
                    : new CustomPropertyValue(d.Value, PropertyParsers.ContainsReferences(d.Value, 0));
                target.Add(new ParsedDeclaration(default, d.Name, custom, d));
                continue;
            }
            scratch.Clear();
            var status = PropertyParsers.ParseDeclaration(d, scratch, diagnostics, out var reason);
            if (status != DeclarationStatus.Active)
            {
                (rejected ??= new List<RejectedDeclaration>()).Add(new RejectedDeclaration(d, status, reason!));
                continue;
            }
            foreach (var pv in scratch) target.Add(new ParsedDeclaration(pv.Id, null, pv.Value, d));
        }
        return new PropertyDeclarationBlock(normal?.ToArray() ?? NoDeclarations, important?.ToArray() ?? NoDeclarations,
            rejected?.ToArray() ?? Array.Empty<RejectedDeclaration>());
    }
}

internal static partial class PropertyParsers
{
    /// <summary>
    /// 解析一条非自定义属性的声明，把展开后的长属性值追加到 <paramref name="output"/>。
    /// 返回 <see cref="DeclarationStatus.Active"/> 表示有效。
    /// </summary>
    public static DeclarationStatus ParseDeclaration(CssDeclaration d, List<PropertyValue> output, List<CssDiagnostic>? diagnostics, out string? reason)
    {
        reason = null;
        if (!CssProperties.TryGet(d.Name, out var property))
        {
            reason = UnsupportedMessage(d.Name);
            diagnostics.Report(CssDiagnosticCodes.UnsupportedProperty, CssDiagnosticSeverity.Warning, reason, d.NameRange);
            return DeclarationStatus.Unsupported;
        }
        var value = d.Value;
        if (TryParseValue(property, value, output)) return DeclarationStatus.Active;
        reason = $"属性 {property.Name} 的值“{Truncate(d.ValueText)}”无效，该声明被忽略。";
        diagnostics.Report(CssDiagnosticCodes.InvalidValue, CssDiagnosticSeverity.Error, reason, d.ValueRange);
        return DeclarationStatus.Invalid;
    }

    /// <summary>按属性语法解析值（含全局关键字与 var()），展开到 <paramref name="output"/>。</summary>
    internal static bool TryParseValue(CssProperty property, IReadOnlyList<CssComponentValue> value, List<PropertyValue> output)
    {
        var r = new ValueReader(value);
        if (r.AtEnd) return false;
        // CSS 全局关键字：整个值只有这一个关键字
        if (r.Peek() is CssToken { Type: CssTokenType.Ident } kw && CssWideKeywordValue.TryParse(kw.Value, out var wide))
        {
            r.Advance();
            if (r.AtEnd)
            {
                var v = CssWideKeywordValue.Of(wide);
                if (property.IsShorthand) foreach (var id in property.LonghandIds) output.Add(new PropertyValue(id, v));
                else output.Add(new PropertyValue(property.Id, v));
                return true;
            }
            r.Position = 0;
        }
        if (ContainsReferences(value, 0))
        {
            if (!ValidateReferences(value, 0)) return false;
            var unparsed = new UnparsedValue(value);
            if (property.IsShorthand)
            {
                // all: var(--x) 也合法：替换后只能得到全局关键字（由 TryParseValue 再次检查）
                var pending = new PendingSubstitutionValue(property.Id, unparsed);
                foreach (var id in property.LonghandIds) output.Add(new PropertyValue(id, pending));
            }
            else output.Add(new PropertyValue(property.Id, unparsed));
            return true;
        }
        if (property.IsShorthand) return ParseShorthand(property.Id, value, output);
        var parsed = ParseLonghand(property.Id, value);
        if (parsed is null) return false;
        output.Add(new PropertyValue(property.Id, parsed));
        return true;
    }

    /// <summary>值中是否含 <c>var()</c> 或 <c>env()</c>（任意深度；组件值的嵌套深度由解析器限制）。</summary>
    internal static bool ContainsReferences(IReadOnlyList<CssComponentValue> list, int depth)
    {
        if (depth > 512) return false;
        for (int i = 0; i < list.Count; i++)
        {
            switch (list[i])
            {
                case CssFunction f:
                    if (IsReferenceFunction(f.Name)) return true;
                    if (ContainsReferences(f.Arguments, depth + 1)) return true;
                    break;
                case CssSimpleBlock b:
                    if (ContainsReferences(b.Values, depth + 1)) return true;
                    break;
            }
        }
        return false;
    }

    internal static bool IsReferenceFunction(string name)
        => CssSerializer.EqualsIgnoreAsciiCase(name, "var") || CssSerializer.EqualsIgnoreAsciiCase(name, "env");

    /// <summary>检查 <c>var(--name [, fallback]?)</c> 与 <c>env(name [, fallback]?)</c> 的语法。</summary>
    static bool ValidateReferences(IReadOnlyList<CssComponentValue> list, int depth)
    {
        if (depth > 512) return false;
        for (int i = 0; i < list.Count; i++)
        {
            switch (list[i])
            {
                case CssFunction f:
                    if (IsReferenceFunction(f.Name))
                    {
                        var r = new ValueReader(f.Arguments);
                        if (r.Next() is not CssToken { Type: CssTokenType.Ident } name) return false;
                        bool isVar = CssSerializer.EqualsIgnoreAsciiCase(f.Name, "var");
                        if (isVar && !(name.Value.Length > 2 && name.Value[0] == '-' && name.Value[1] == '-')) return false;
                        if (!r.AtEnd && !r.TryComma()) return false;
                    }
                    if (!ValidateReferences(f.Arguments, depth + 1)) return false;
                    break;
                case CssSimpleBlock b:
                    if (!ValidateReferences(b.Values, depth + 1)) return false;
                    break;
            }
        }
        return true;
    }

    static string UnsupportedMessage(string name)
    {
        var sb = new StringBuilder();
        sb.Append("不支持的 CSS 属性 ").Append(Truncate(name)).Append("，该声明被忽略。");
        if (CssProperties.TryGetPlannedPhase(name, out int phase))
            sb.Append("计划在第 ").Append(phase).Append(" 期支持。");
        else if (CssProperties.TryGetUnsupportedHint(name, out var hint))
            sb.Append("不会支持：").Append(hint).Append('。');
        else if (CssProperties.SuggestName(name) is { } suggestion)
            sb.Append("是否想写 ").Append(suggestion).Append('？');
        return sb.ToString();
    }

    static string Truncate(string s) => s.Length <= 80 ? s : s.Substring(0, 77) + "...";
}
