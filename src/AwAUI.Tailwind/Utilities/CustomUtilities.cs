using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using AwAUI.Tailwind.Ast;
using AwAUI.Tailwind.Candidates;
using AwAUI.Tailwind.Internal;

namespace AwAUI.Tailwind.Compilation;

/// <summary><c>@utility name { ... }</c> 与 <c>@utility name-* { ... --value(...) ... }</c>（移植自 Tailwind <c>createCssUtility</c>）。</summary>
internal static class CustomUtilities
{
    static readonly string[] s_bareTypes = { "number", "integer", "ratio", "percentage" };
    static readonly Regex s_leadingIdent = new("^-?[a-z][a-zA-Z0-9_-]*", RegexOptions.CultureInvariant);

    /// <summary>返回注册动作；名称非法时返回 null。</summary>
    public static Action<DesignSystem>? Create(AstNode node)
    {
        string name = Js.Unescape(node.Params);
        if (IsValidFunctionalUtilityName(name)) return ds => RegisterFunctional(name, node, ds);
        if (IsValidStaticUtilityName(name)) return ds => ds.Utilities.Static(name, () => AstNode.CloneList(node.Nodes));
        return null;
    }

    static void RegisterFunctional(string name, AstNode node, DesignSystem ds)
    {
        // 规范化 --value(...) / --modifier(...) 的参数写法（--color-*、--text-*--line-height 等）
        AstNode.Walk(node.Nodes, (n, _) =>
        {
            if (n.Kind != AstKind.Declaration || n.Value == null) return WalkAction<AstNode>.Continue;
            if (n.Value.IndexOf("--value(", StringComparison.Ordinal) < 0 && n.Value.IndexOf("--modifier(", StringComparison.Ordinal) < 0) return WalkAction<AstNode>.Continue;
            var ast = ValueParser.Parse(n.Value);
            ValueParser.Walk(ast, (fn, _) =>
            {
                if (fn.Kind != ValueKind.Function) return WalkAction<ValueNode>.Continue;
                if (fn.Value != "--value" && fn.Value != "--modifier") return WalkAction<ValueNode>.Continue;
                var args = Js.Segment(ValueParser.ToCss(fn.Nodes!), ',');
                for (int i = 0; i < args.Count; i++)
                {
                    string arg = args[i].Trim();
                    if (arg.StartsWith("--default(", StringComparison.Ordinal))
                    {
                        args[i] = arg;
                        continue;
                    }
                    arg = arg.Replace("\\*", "*");
                    arg = Regex.Replace(arg, @"--(.*?)\s--(.*?)", "--$1-*--$2");
                    arg = Regex.Replace(arg, @"\s+", "");
                    arg = Regex.Replace(arg, @"(-\*){2,}", "-*");
                    if (arg.Length > 1 && arg[0] == '-' && arg[1] == '-' && arg.IndexOf('(') < 0 && arg.IndexOf("-*", StringComparison.Ordinal) < 0) arg += "-*";
                    args[i] = arg;
                }
                fn.Nodes = ValueParser.Parse(string.Join(",", args));
                return WalkAction<ValueNode>.Continue;
            });
            n.Value = ValueParser.ToCss(ast);
            return WalkAction<AstNode>.Continue;
        });

        string root = name.Substring(0, name.Length - 2);
        ds.Utilities.Functional(root, candidate =>
        {
            var copy = node.Clone();
            var value = candidate.Value;
            var modifier = candidate.Modifier;
            bool usedValueFn = false, resolvedValue = false, usedModifierFn = false, resolvedModifier = false;
            bool isRatio = false;
            var valueDecls = new List<KeyValuePair<AstNode, AstNode>>();
            AstNode.Walk(new List<AstNode> { copy }, (decl, ctx) =>
            {
                var parent = ctx.Parent;
                if (parent == null || (parent.Kind != AstKind.Rule && parent.Kind != AstKind.AtRule) || decl.Kind != AstKind.Declaration || decl.Value == null) return WalkAction<AstNode>.Continue;
                bool remove = false;
                var ast = ValueParser.Parse(decl.Value);
                ValueParser.Walk(ast, (fn, _) =>
                {
                    if (fn.Kind != ValueKind.Function) return WalkAction<ValueNode>.Continue;
                    if (fn.Value == "--value")
                    {
                        usedValueFn = true;
                        var r = ResolveValueFunction(value == null ? null : new Arg(value), fn, ds);
                        if (r == null)
                        {
                            remove = true;
                            return WalkAction<ValueNode>.Stop;
                        }
                        resolvedValue = true;
                        if (r.Value.Ratio) isRatio = true;
                        else valueDecls.Add(new KeyValuePair<AstNode, AstNode>(decl, parent));
                        return WalkAction<ValueNode>.ReplaceSkip(r.Value.Nodes);
                    }
                    if (fn.Value == "--modifier")
                    {
                        usedModifierFn = true;
                        var r = ResolveValueFunction(modifier == null ? null : new Arg(modifier), fn, ds);
                        if (r == null)
                        {
                            remove = true;
                            return WalkAction<ValueNode>.Stop;
                        }
                        resolvedModifier = true;
                        return WalkAction<ValueNode>.ReplaceSkip(r.Value.Nodes);
                    }
                    return WalkAction<ValueNode>.Continue;
                });
                if (remove) return WalkAction<AstNode>.ReplaceSkip(new List<AstNode>());
                decl.Value = ValueParser.ToCss(ast);
                return WalkAction<AstNode>.Continue;
            });

            if (!usedValueFn || !resolvedValue) return null;
            if (usedModifierFn && !resolvedModifier && modifier != null) return null;
            if (isRatio && resolvedModifier) return null;
            if (modifier != null && !isRatio && !resolvedModifier) return null;
            if (isRatio)
            {
                foreach (var kv in valueDecls)
                {
                    int idx = kv.Value.Nodes.IndexOf(kv.Key);
                    if (idx != -1) kv.Value.Nodes.RemoveAt(idx);
                }
            }
            return copy.Nodes;
        });
    }

    /// <summary>候选值或修饰符的统一视图。</summary>
    sealed class Arg
    {
        public readonly bool IsArbitrary;
        public readonly string Value;
        public readonly string? Fraction;
        public readonly bool HasFraction;
        public readonly string? DataType;

        public Arg(CandidateValue v)
        {
            IsArbitrary = v.IsArbitrary;
            Value = v.Value;
            Fraction = v.Fraction;
            HasFraction = !v.IsArbitrary;
            DataType = v.DataType;
        }

        public Arg(CandidateModifier m)
        {
            IsArbitrary = m.IsArbitrary;
            Value = m.Value;
        }
    }

    static (List<ValueNode> Nodes, bool Ratio)? ResolveValueFunction(Arg? value, ValueNode fn, DesignSystem ds)
    {
        if (value == null)
        {
            foreach (var arg in fn.Nodes!)
            {
                if (arg.Kind == ValueKind.Function && arg.Value == "--default") return (arg.Nodes!, false);
            }
            return null;
        }
        foreach (var arg in fn.Nodes!)
        {
            if (!value.IsArbitrary && arg.Kind == ValueKind.Word && arg.Value.Length >= 2 && (arg.Value[0] == '\'' || arg.Value[0] == '"') && arg.Value[arg.Value.Length - 1] == arg.Value[0] && arg.Value.Slice(1, -1) == value.Value)
                return (ValueParser.Parse(value.Value), false);

            if (!value.IsArbitrary && arg.Kind == ValueKind.Word && arg.Value.Length > 1 && arg.Value[0] == '-' && arg.Value[1] == '-')
            {
                string themeKey = arg.Value;
                if (themeKey.EndsWith("-*", StringComparison.Ordinal))
                {
                    themeKey = themeKey.Substring(0, themeKey.Length - 2);
                    string? resolved = ds.Theme.Resolve(value.Value, new[] { themeKey });
                    if (resolved != null) return (ValueParser.Parse(resolved), false);
                }
                else
                {
                    var nested = themeKey.Split(new[] { "-*" }, StringSplitOptions.None);
                    if (nested.Length <= 1) continue;
                    var keys = new[] { nested[0] };
                    var rest = new string[nested.Length - 1];
                    Array.Copy(nested, 1, rest, 0, rest.Length);
                    var resolved = ds.Theme.ResolveWith(value.Value, keys, rest);
                    if (resolved != null && resolved.Value.Extra.TryGetValue(rest[rest.Length - 1], out var nestedValue) && nestedValue.Length > 0)
                        return (ValueParser.Parse(nestedValue), false);
                }
                continue;
            }

            if (!value.IsArbitrary && arg.Kind == ValueKind.Word)
            {
                if (Array.IndexOf(s_bareTypes, arg.Value) < 0) continue;
                string? v = arg.Value == "ratio" && value.HasFraction ? value.Fraction : value.Value;
                if (string.IsNullOrEmpty(v)) continue;
                string? type = DataTypes.Infer(v!, new[] { arg.Value });
                if (type == null) continue;
                if (type == "ratio")
                {
                    var parts = Js.Segment(v!, '/');
                    if (parts.Count < 2 || !Js.IsPositiveInteger(Js.Number(parts[0])) || !Js.IsPositiveInteger(Js.Number(parts[1]))) continue;
                }
                else
                {
                    if (type == "number" && !Js.IsMultipleOfQuarter(v!)) continue;
                    if (type == "percentage" && !Js.IsPositiveInteger(v!.Slice(0, -1))) continue;
                }
                if (type == "ratio")
                {
                    var parts = Js.Segment(v!, '/');
                    return (ValueParser.Parse(parts[0].Trim() + " / " + parts[1].Trim()), true);
                }
                return (ValueParser.Parse(v!), false);
            }

            if (value.IsArbitrary && arg.Kind == ValueKind.Word && arg.Value.Length >= 2 && arg.Value[0] == '[' && arg.Value[arg.Value.Length - 1] == ']')
            {
                string dataType = arg.Value.Slice(1, -1);
                if (dataType == "*") return (ValueParser.Parse(value.Value), false);
                if (value.DataType != null && value.DataType != dataType) continue;
                if (value.DataType != null) return (ValueParser.Parse(value.Value), false);
                if (DataTypes.Infer(value.Value, new[] { dataType }) != null) return (ValueParser.Parse(value.Value), false);
            }
        }
        return null;
    }

    static bool IsValidFunctionalUtilityName(string name)
    {
        if (!name.EndsWith("-*", StringComparison.Ordinal)) return false;
        name = name.Substring(0, name.Length - 2);
        var m = s_leadingIdent.Match(name);
        return m.Success && m.Index == 0 && m.Length == name.Length;
    }

    /// <summary>静态工具名：标识符，可带 <c>/</c>、<c>.5</c>、<c>%</c>（Tailwind <c>isValidStaticUtilityName</c>）。</summary>
    static bool IsValidStaticUtilityName(string name)
    {
        var m = s_leadingIdent.Match(name);
        if (!m.Success || m.Index != 0) return false;
        string head = m.Value;
        string rest = name.Substring(head.Length);
        if (rest.Length == 0 && head.EndsWith("-", StringComparison.Ordinal)) return false;
        if (rest.Length == 0) return true;
        bool seenSlash = false;
        for (int i = 0; i < rest.Length; i++)
        {
            char c = rest[i];
            switch (c)
            {
                case '%':
                {
                    if (i != rest.Length - 1) return false;
                    char prev = i > 0 ? rest[i - 1] : head[head.Length - 1];
                    if (prev < '0' || prev > '9') return false;
                    break;
                }
                case '/':
                    if (i == rest.Length - 1 || seenSlash) return false;
                    seenSlash = true;
                    break;
                case '.':
                {
                    char prev = i > 0 ? rest[i - 1] : head[head.Length - 1];
                    if (prev < '0' || prev > '9') return false;
                    char next = i + 1 < rest.Length ? rest[i + 1] : '\0';
                    if (next < '0' || next > '9') return false;
                    break;
                }
                case '_':
                case '-':
                    continue;
                default:
                    if ((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9')) continue;
                    return false;
            }
        }
        return true;
    }
}
