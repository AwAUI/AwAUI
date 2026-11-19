using System.Collections.Generic;
using AwAUI.Tailwind.Compilation;
using AwAUI.Tailwind.Internal;
using AwAUI.Tailwind.Variants;

namespace AwAUI.Tailwind.Candidates;

/// <summary>候选类名与变体的解析（移植自 Tailwind <c>candidate.ts</c>）。</summary>
internal static class CandidateParser
{
    static bool IsNamedValue(string s)
    {
        if (s.Length == 0) return false;
        foreach (char c in s)
        {
            if (!((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '_' || c == '.' || c == '%' || c == '-')) return false;
        }
        return true;
    }

    /// <summary>解析一个候选类名；可能有多种解析（不同的工具根），按 Tailwind 的顺序全部返回。</summary>
    public static List<Candidate> Parse(string input, DesignSystem ds)
    {
        var results = new List<Candidate>();
        if (input.Length == 0 || input.Length > Limits.MaxCandidateLength) return results;
        var rawVariants = Js.Segment(input, ':');
        if (ds.Theme.Prefix != null)
        {
            if (rawVariants.Count == 1 || rawVariants[0] != ds.Theme.Prefix) return results;
            rawVariants.RemoveAt(0);
        }
        if (rawVariants.Count - 1 > Limits.MaxVariants) return results;
        string baseName = rawVariants[rawVariants.Count - 1];
        rawVariants.RemoveAt(rawVariants.Count - 1);

        var parsedVariants = new List<Variant>();
        for (int i = rawVariants.Count - 1; i >= 0; --i)
        {
            var v = ds.ParseVariant(rawVariants[i]);
            if (v == null) return results;
            parsedVariants.Add(v);
        }

        bool important = false;
        if (baseName.Length > 0 && baseName[baseName.Length - 1] == '!')
        {
            important = true;
            baseName = baseName.Substring(0, baseName.Length - 1);
        }
        else if (baseName.Length > 0 && baseName[0] == '!')
        {
            important = true;
            baseName = baseName.Substring(1);
        }
        if (baseName.Length == 0) return results;

        if (ds.Utilities.Has(baseName, UtilityKind.Static) && baseName.IndexOf('[') < 0)
        {
            results.Add(new Candidate { Kind = CandidateKind.Static, Root = baseName, Variants = parsedVariants, Important = important, Raw = input });
        }

        var slashParts = Js.Segment(baseName, '/');
        if (slashParts.Count > 2) return results;
        string baseWithoutModifier = slashParts[0];
        string? modifierSegment = slashParts.Count > 1 ? slashParts[1] : null;
        CandidateModifier? parsedModifier = modifierSegment == null ? null : ParseModifier(modifierSegment);
        if (modifierSegment != null && parsedModifier == null) return results;

        if (baseWithoutModifier.Length > 0 && baseWithoutModifier[0] == '[')
        {
            // 任意属性 [prop:value]
            if (baseWithoutModifier[baseWithoutModifier.Length - 1] != ']') return results;
            char code = baseWithoutModifier.CharAt(1);
            if (code != '-' && !(code >= 'a' && code <= 'z')) return results;
            string inner = baseWithoutModifier.Substring(1, baseWithoutModifier.Length - 2);
            int idx = inner.IndexOf(':');
            if (idx == -1 || idx == 0 || idx == inner.Length - 1) return results;
            string property = inner.Substring(0, idx);
            string? value = ArbitraryValue.DecodeChecked(inner.Substring(idx + 1));
            if (value == null) return results;
            results.Add(new Candidate
            {
                Kind = CandidateKind.Arbitrary,
                Property = property,
                ArbitraryValue = value,
                Modifier = parsedModifier,
                Variants = parsedVariants,
                Important = important,
                Raw = input,
            });
            return results;
        }

        List<(string Root, string? Value)> roots;
        if (baseWithoutModifier.Length > 0 && baseWithoutModifier[baseWithoutModifier.Length - 1] == ']')
        {
            int idx = baseWithoutModifier.IndexOf("-[", System.StringComparison.Ordinal);
            if (idx == -1) return results;
            string root = baseWithoutModifier.Substring(0, idx);
            if (!ds.Utilities.Has(root, UtilityKind.Functional)) return results;
            roots = new List<(string, string?)> { (root, baseWithoutModifier.Substring(idx + 1)) };
        }
        else if (baseWithoutModifier.Length > 0 && baseWithoutModifier[baseWithoutModifier.Length - 1] == ')')
        {
            int idx = baseWithoutModifier.IndexOf("-(", System.StringComparison.Ordinal);
            if (idx == -1) return results;
            string root = baseWithoutModifier.Substring(0, idx);
            if (!ds.Utilities.Has(root, UtilityKind.Functional)) return results;
            string value = baseWithoutModifier.Slice(idx + 2, -1);
            var parts = Js.Segment(value, ':');
            string? dataType = null;
            if (parts.Count == 2)
            {
                dataType = parts[0];
                value = parts[1];
            }
            if (value.Length < 2 || value[0] != '-' || value[1] != '-' || !ArbitraryValue.IsValid(value)) return results;
            roots = new List<(string, string?)> { (root, dataType == null ? "[var(" + value + ")]" : "[" + dataType + ":var(" + value + ")]") };
        }
        else
        {
            roots = FindRoots(baseWithoutModifier, r => ds.Utilities.Has(r, UtilityKind.Functional));
        }

        foreach (var (root, value) in roots)
        {
            var candidate = new Candidate
            {
                Kind = CandidateKind.Functional,
                Root = root,
                Modifier = parsedModifier,
                Variants = parsedVariants,
                Important = important,
                Raw = input,
            };
            if (value == null)
            {
                results.Add(candidate);
                continue;
            }
            int startOfArbitrary = value.IndexOf('[');
            if (startOfArbitrary != -1)
            {
                if (value[value.Length - 1] != ']') return results;
                string? arbitrary = ArbitraryValue.DecodeChecked(value.Slice(startOfArbitrary + 1, -1));
                if (arbitrary == null) continue;
                string? typehint = null;
                for (int i = 0; i < arbitrary.Length; i++)
                {
                    char c = arbitrary[i];
                    if (c == ':')
                    {
                        typehint = arbitrary.Substring(0, i);
                        arbitrary = arbitrary.Substring(i + 1);
                        break;
                    }
                    if (!(c == '-' || (c >= 'a' && c <= 'z'))) break;
                }
                if (arbitrary.Length == 0 || arbitrary.Trim().Length == 0 || typehint == "") continue;
                candidate.Value = new CandidateValue { IsArbitrary = true, DataType = typehint, Value = arbitrary };
            }
            else
            {
                string? fraction = modifierSegment == null || (candidate.Modifier != null && candidate.Modifier.IsArbitrary) ? null : value + "/" + modifierSegment;
                if (!IsNamedValue(value)) continue;
                candidate.Value = new CandidateValue { IsArbitrary = false, Value = value, Fraction = fraction };
            }
            results.Add(candidate);
        }
        return results;
    }

    /// <summary>解析修饰符：<c>[..]</c>、<c>(--x)</c> 或具名。</summary>
    public static CandidateModifier? ParseModifier(string modifier)
    {
        if (modifier.Length >= 2 && modifier[0] == '[' && modifier[modifier.Length - 1] == ']')
        {
            string? arbitrary = ArbitraryValue.DecodeChecked(modifier.Slice(1, -1));
            if (arbitrary == null || arbitrary.Length == 0 || arbitrary.Trim().Length == 0) return null;
            return new CandidateModifier { IsArbitrary = true, Value = arbitrary };
        }
        if (modifier.Length >= 2 && modifier[0] == '(' && modifier[modifier.Length - 1] == ')')
        {
            modifier = modifier.Slice(1, -1);
            if (modifier.Length < 2 || modifier[0] != '-' || modifier[1] != '-' || !ArbitraryValue.IsValid(modifier)) return null;
            modifier = "var(" + modifier + ")";
            return new CandidateModifier { IsArbitrary = true, Value = ArbitraryValue.Decode(modifier) };
        }
        return IsNamedValue(modifier) ? new CandidateModifier { IsArbitrary = false, Value = modifier } : null;
    }

    /// <summary>解析变体（结果由 <see cref="DesignSystem"/> 缓存）。</summary>
    public static Variant? ParseVariant(string variant, DesignSystem ds, int depth = 0)
    {
        if (depth > Limits.MaxCompoundDepth || variant.Length == 0) return null;
        if (variant[0] == '[' && variant[variant.Length - 1] == ']')
        {
            // 任意变体：[&_svg]、[@media(...)]
            if (variant.Length > 1 && variant[1] == '@' && variant.IndexOf('&') >= 0) return null;
            string? selector = ArbitraryValue.DecodeChecked(variant.Slice(1, -1));
            if (selector == null || selector.Length == 0 || selector.Trim().Length == 0) return null;
            bool relative = selector[0] == '>' || selector[0] == '+' || selector[0] == '~';
            if (!relative && selector[0] != '@' && selector.IndexOf('&') < 0) selector = "&:is(" + selector + ")";
            return new Variant { Kind = VariantKind.Arbitrary, Selector = selector, Relative = relative };
        }

        var slashParts = Js.Segment(variant, '/');
        if (slashParts.Count > 2) return null;
        string variantWithoutModifier = slashParts[0];
        string? modifier = slashParts.Count > 1 ? slashParts[1] : null;

        foreach (var (root, valueRaw) in FindRoots(variantWithoutModifier, r => ds.Variants.Has(r)))
        {
            string? value = valueRaw;
            switch (ds.Variants.Kind(root))
            {
                case VariantKind.Static:
                    if (value != null || modifier != null) return null;
                    return new Variant { Kind = VariantKind.Static, Root = root };
                case VariantKind.Functional:
                {
                    CandidateModifier? parsedModifier = modifier == null ? null : ParseModifier(modifier);
                    if (modifier != null && parsedModifier == null) return null;
                    if (value == null) return new Variant { Kind = VariantKind.Functional, Root = root, Modifier = parsedModifier };
                    if (value[value.Length - 1] == ']')
                    {
                        if (value[0] != '[') continue;
                        string? arbitrary = ArbitraryValue.DecodeChecked(value.Slice(1, -1));
                        if (arbitrary == null || arbitrary.Length == 0 || arbitrary.Trim().Length == 0) return null;
                        return new Variant { Kind = VariantKind.Functional, Root = root, Modifier = parsedModifier, Value = new CandidateValue { IsArbitrary = true, Value = arbitrary } };
                    }
                    if (value[value.Length - 1] == ')')
                    {
                        if (value[0] != '(') continue;
                        string? arbitrary = ArbitraryValue.DecodeChecked(value.Slice(1, -1));
                        if (arbitrary == null || arbitrary.Length < 2 || arbitrary.Trim().Length == 0 || arbitrary[0] != '-' || arbitrary[1] != '-') return null;
                        return new Variant { Kind = VariantKind.Functional, Root = root, Modifier = parsedModifier, Value = new CandidateValue { IsArbitrary = true, Value = "var(" + arbitrary + ")" } };
                    }
                    if (!IsNamedValue(value)) continue;
                    return new Variant { Kind = VariantKind.Functional, Root = root, Modifier = parsedModifier, Value = new CandidateValue { IsArbitrary = false, Value = value } };
                }
                case VariantKind.Compound:
                {
                    if (value == null) return null;
                    string? mod = modifier;
                    if (mod != null && (root == "not" || root == "has" || root == "in"))
                    {
                        value = value + "/" + mod;
                        mod = null;
                    }
                    var inner = ds.ParseVariant(value, depth + 1);
                    if (inner == null || !ds.Variants.CompoundsWith(root, inner)) return null;
                    CandidateModifier? parsedModifier = mod == null ? null : ParseModifier(mod);
                    if (mod != null && parsedModifier == null) return null;
                    return new Variant { Kind = VariantKind.Compound, Root = root, Modifier = parsedModifier, Inner = inner };
                }
            }
        }
        return null;
    }

    /// <summary>
    /// 从右往左在 <c>-</c> 处切分，找出注册过的根（<c>bg-red-500</c> → <c>bg</c> + <c>red-500</c>）。
    /// <c>@</c> 开头的容器查询变体特殊处理（<c>@md</c> → <c>@</c> + <c>md</c>）。
    /// </summary>
    public static List<(string Root, string? Value)> FindRoots(string input, System.Func<string, bool> exists)
    {
        var list = new List<(string, string?)>();
        if (exists(input)) list.Add((input, null));
        int idx = input.LastIndexOf('-');
        while (idx > 0)
        {
            string maybeRoot = input.Substring(0, idx);
            if (exists(maybeRoot))
            {
                string rest = input.Substring(idx + 1);
                if (rest.Length == 0 || (maybeRoot == "@" && exists("@") && input[idx] == '-')) break;
                list.Add((maybeRoot, rest));
            }
            idx = input.LastIndexOf('-', idx - 1);
        }
        if (input.Length > 0 && input[0] == '@' && exists("@")) list.Add(("@", input.Substring(1)));
        return list;
    }
}
