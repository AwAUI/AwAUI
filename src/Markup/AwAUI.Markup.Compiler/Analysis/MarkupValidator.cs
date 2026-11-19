using System;
using System.Collections.Generic;
using System.Globalization;
using AwAUI.Markup.Compiler.Parsing;
using AwAUI.Markup.Compiler.Syntax;

namespace AwAUI.Markup.Compiler.Analysis;

/// <summary>
/// 在语法树上做不需要组件目录（Roslyn）即可完成的检查：
/// AW4002 未知内建元素、AW4003 未知属性/事件、AW4004 属性值转换、AW4007 指令属性与重复指令、
/// AW4008 循环内缺少 @key、AW4009 @bind、AW4011 文件名。组件属性的类别检查需要 <c>ComponentCatalog</c>，不在此处。
/// </summary>
public static class MarkupValidator
{
    static readonly HashSet<string> CSharpKeywords = new(StringComparer.Ordinal)
    {
        "abstract", "as", "base", "bool", "break", "byte", "case", "catch", "char", "checked", "class", "const",
        "continue", "decimal", "default", "delegate", "do", "double", "else", "enum", "event", "explicit", "extern",
        "false", "finally", "fixed", "float", "for", "foreach", "goto", "if", "implicit", "in", "int", "interface",
        "internal", "is", "lock", "long", "namespace", "new", "null", "object", "operator", "out", "override",
        "params", "private", "protected", "public", "readonly", "ref", "return", "sbyte", "sealed", "short",
        "sizeof", "stackalloc", "static", "string", "struct", "switch", "this", "throw", "true", "try", "typeof",
        "uint", "ulong", "unchecked", "unsafe", "ushort", "using", "virtual", "void", "volatile", "while",
    };

    /// <summary>对语法树做检查，返回新增的诊断（不含 <see cref="MarkupSyntaxTree.Diagnostics"/>）。</summary>
    public static IReadOnlyList<MarkupDiagnostic> Validate(MarkupSyntaxTree tree)
    {
        var bag = new DiagnosticBag(tree.Source);
        ValidateClassName(tree.Source.FilePath, bag);
        ValidateDirectives(tree.Root, bag);
        Walk(tree.Root, bag);
        return bag.Items;
    }

    /// <summary>文件名（去掉 .awaui）是否是合法的 C# 类名。</summary>
    public static bool IsValidClassName(string name)
    {
        if (name.Length == 0 || !MarkupParser.IsIdentStart(name[0])) return false;
        for (int i = 1; i < name.Length; i++)
            if (!MarkupParser.IsIdentPart(name[i])) return false;
        return !CSharpKeywords.Contains(name);
    }

    /// <summary>由文件路径得到类名（文件名去掉 <c>.awaui</c> 扩展名）。</summary>
    public static string ClassNameFromPath(string filePath)
    {
        int slash = Math.Max(filePath.LastIndexOf('/'), filePath.LastIndexOf('\\'));
        string file = filePath.Substring(slash + 1);
        return file.EndsWith(".awaui", StringComparison.OrdinalIgnoreCase) ? file.Substring(0, file.Length - 6) : file;
    }

    static void ValidateClassName(string? filePath, DiagnosticBag bag)
    {
        if (string.IsNullOrEmpty(filePath)) return;
        string name = ClassNameFromPath(filePath!);
        if (!IsValidClassName(name))
            bag.Error(DiagnosticCodes.InvalidClassName, new TextSpan(0, 0), $"文件名 {name} 不是合法的 C# 类名（组件类名取自文件名）");
    }

    static void ValidateDirectives(DocumentSyntax doc, DiagnosticBag bag)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in doc.Items)
        {
            if (item is not DirectiveSyntax d) continue;
            string? key = d.DirectiveKind switch
            {
                DirectiveKind.Namespace => "namespace",
                DirectiveKind.Inherits => "inherits",
                DirectiveKind.TypeParam => "typeparam " + FirstWord(d.Value),
                _ => null,
            };
            if (key != null && !seen.Add(key))
                bag.Error(DiagnosticCodes.InvalidDirective, d.NameSpan, $"重复的指令 @{d.Name}");
        }
    }

    static string FirstWord(string s)
    {
        int i = 0;
        while (i < s.Length && MarkupParser.IsIdentPart(s[i])) i++;
        return s.Substring(0, i);
    }

    /// <param name="inSvg">是否位于内联 <c>&lt;svg&gt;</c> 的子树中：其中的小写元素是 SVG 元素（不限于内建元素表），未知属性按 SVG 属性接受。</param>
    static void Walk(SyntaxNode node, DiagnosticBag bag, bool inSvg = false)
    {
        switch (node)
        {
            case ElementSyntax e:
                ValidateElement(e, bag, inSvg);
                if (e.ElementKind == ElementKind.Intrinsic && e.Name == "svg") inSvg = true;
                break;
            case LoopSyntax loop:
                CheckLoopKeys(loop.Body.Nodes, bag, loop.Keyword);
                break;
        }
        foreach (var child in node.ChildNodes())
            Walk(child, bag, inSvg);
    }

    // ---------------------------------------------------------------- AW4008

    static void CheckLoopKeys(IReadOnlyList<SyntaxNode> roots, DiagnosticBag bag, string keyword)
    {
        foreach (var n in roots)
        {
            switch (n)
            {
                case ElementSyntax e:
                    if (e.FindAttribute("@key") == null)
                        bag.Warning(DiagnosticCodes.MissingKeyInLoop, e.NameSpan,
                            $"@{keyword} 循环内的 <{e.Name}> 缺少 @key，列表变化时无法稳定地协调");
                    break;
                case IfSyntax i:
                    foreach (var b in i.Branches) CheckLoopKeys(b.Body.Nodes, bag, keyword);
                    break;
                case SwitchSyntax s:
                    foreach (var sec in s.Sections) CheckLoopKeys(sec.Nodes, bag, keyword);
                    break;
                case StatementBlockSyntax sb:
                    CheckLoopKeys(sb.Body.Nodes, bag, keyword);
                    break;
            }
        }
    }

    // ---------------------------------------------------------------- 元素与属性

    static void ValidateElement(ElementSyntax e, DiagnosticBag bag, bool inSvg)
    {
        bool intrinsic = e.ElementKind == ElementKind.Intrinsic;
        // 内联 svg（根元素与子树）：SVG 的元素与属性远多于内建表，未知的不报错；已知属性仍做类型检查
        bool svgContent = intrinsic && (inSvg || e.Name == "svg");
        if (intrinsic && !svgContent && !IntrinsicElements.IsIntrinsic(e.Name))
        {
            string? near = Nearest(e.Name, IntrinsicElements.Names);
            string hint = near != null
                ? $"，是否是 <{near}>？"
                : "；组件名应以大写字母开头";
            bag.Error(DiagnosticCodes.UnknownElement, e.NameSpan, $"未知的内建元素 <{e.Name}>{hint}");
            intrinsic = false; // 未知元素上不再逐个报告属性
        }

        foreach (var a in e.Attributes)
        {
            if (a.IsDirective) ValidateDirectiveAttribute(e, a, intrinsic, bag);
            else if (intrinsic) ValidateIntrinsicAttribute(e, a, bag, lenient: svgContent);
        }
        ValidateBind(e, intrinsic, bag);
    }

    static void ValidateDirectiveAttribute(ElementSyntax e, AttributeSyntax a, bool intrinsic, DiagnosticBag bag)
    {
        string name = a.Name;
        if (name == "bind" || name.StartsWith("bind-", StringComparison.Ordinal)) return; // ValidateBind
        bool isEvent = name.Length > 2 && name.StartsWith("on", StringComparison.Ordinal);
        if (!isEvent && name != "key" && name != "ref" && name != "attributes")
        {
            bag.Error(DiagnosticCodes.InvalidDirective, a.NameSpan, $"未知的指令属性 @{name}");
            return;
        }
        if (a.Parameter != null)
            bag.Error(DiagnosticCodes.InvalidDirective, a.NameSpan, $"指令属性 @{name} 不支持参数 :{a.Parameter}");
        if (isEvent && intrinsic && !IntrinsicElements.IsKnownEvent(name.Substring(2)))
        {
            string? near = Nearest(name, IntrinsicElements.EventAttributeNames());
            bag.Error(DiagnosticCodes.UnknownAttribute, a.NameSpan,
                $"<{e.Name}> 上未知的事件 @{name}" + (near != null ? $"，是否是 @{near}？" : ""));
        }
        if (a.Value == null || (a.Value.Parts.Count == 0))
            bag.Error(DiagnosticCodes.InvalidAttributeValue, a.NameSpan, $"@{name} 需要一个 C# 表达式作为值");
    }

    static void ValidateIntrinsicAttribute(ElementSyntax e, AttributeSyntax a, DiagnosticBag bag, bool lenient = false)
    {
        if (!IntrinsicElements.TryGetAttribute(e.Name, a.Name, out var info) || info == null)
        {
            if (lenient) return;
            string hint;
            if (a.Name.StartsWith("on", StringComparison.Ordinal) && IntrinsicElements.IsKnownEvent(a.Name.Substring(2)))
            {
                hint = $"；事件处理器请写作 @{a.Name}";
            }
            else
            {
                string? near = Nearest(a.Name, IntrinsicElements.AttributeNames(e.Name));
                hint = near != null ? $"，是否是 {near}？" : "";
            }
            bag.Error(DiagnosticCodes.UnknownAttribute, a.NameSpan, $"<{e.Name}> 上未知的属性 {a.Name}{hint}");
            return;
        }

        var v = a.Value;
        if (v == null)
        {
            // 布尔属性，以及空串是有效关键字的枚举属性（spellcheck），可以不写值
            if (info.Type != AttributeValueType.Boolean && !(info.Type == AttributeValueType.Enum && AllowsEmpty(info)))
                bag.Error(DiagnosticCodes.InvalidAttributeValue, a.NameSpan, $"属性 {a.Name} 需要值");
            return;
        }
        string? literal = v.LiteralValue;
        if (literal == null)
        {
            // 含表达式：字符串类型做拼接；其他类型要求整个值是单一表达式（忽略两侧空白）
            if (info.Type != AttributeValueType.String && !IsSingleExpression(v))
                bag.Error(DiagnosticCodes.InvalidAttributeValue, v.Span,
                    $"属性 {a.Name} 的类型是 {TypeName(info)}，含表达式时整个值必须是单一表达式");
            return;
        }
        string? problem = CheckLiteral(a.Name, literal, info);
        if (problem != null)
            bag.Error(DiagnosticCodes.InvalidAttributeValue, v.Span, problem);
    }

    static bool IsSingleExpression(AttributeValueSyntax v)
    {
        int exprs = 0;
        foreach (var p in v.Parts)
        {
            if (p is ExpressionSyntax) exprs++;
            else if (p is AttributeLiteralSyntax l && l.Value.Trim().Length > 0) return false;
        }
        return exprs == 1;
    }

    static string? CheckLiteral(string name, string literal, IntrinsicAttributeInfo info)
    {
        string t = literal.Trim();
        switch (info.Type)
        {
            case AttributeValueType.Integer:
                return int.TryParse(t, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out _)
                    ? null : $"属性 {name} 需要整数，\"{literal}\" 无法转换";
            case AttributeValueType.Number:
                return double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out double d) && !double.IsNaN(d) && !double.IsInfinity(d)
                    ? null : $"属性 {name} 需要数值，\"{literal}\" 无法转换";
            case AttributeValueType.Boolean:
                return t.Length == 0 || t == "true" || t == "false" || t == name
                    ? null : $"属性 {name} 需要布尔值（true / false），\"{literal}\" 无法转换";
            case AttributeValueType.Enum:
                foreach (var ev in info.EnumValues)
                    if (ev == t) return null;
                return $"属性 {name} 的取值应为 {EnumList(info)} 之一，\"{literal}\" 无效";
            default:
                return null;
        }
    }

    static bool AllowsEmpty(IntrinsicAttributeInfo info)
    {
        foreach (var ev in info.EnumValues)
            if (ev.Length == 0) return true;
        return false;
    }

    /// <summary>枚举取值的列表文本；空串写作 <c>""</c>。</summary>
    static string EnumList(IntrinsicAttributeInfo info)
    {
        var parts = new string[info.EnumValues.Count];
        for (int i = 0; i < parts.Length; i++) parts[i] = info.EnumValues[i].Length == 0 ? "\"\"" : info.EnumValues[i];
        return string.Join(" / ", parts);
    }

    static string TypeName(IntrinsicAttributeInfo info) => info.Type switch
    {
        AttributeValueType.Integer => "整数",
        AttributeValueType.Number => "数值",
        AttributeValueType.Boolean => "布尔",
        AttributeValueType.Enum => "枚举",
        _ => "字符串",
    };

    // ---------------------------------------------------------------- AW4009

    static void ValidateBind(ElementSyntax e, bool intrinsic, DiagnosticBag bag)
    {
        AttributeSyntax? bind = null, bindEvent = null;
        var bindProps = new List<AttributeSyntax>();
        foreach (var a in e.Attributes)
        {
            if (!a.IsDirective) continue;
            if (a.Name == "bind")
            {
                if (a.Parameter == null) bind = a;
                else if (a.Parameter == "event") bindEvent = a;
                else bag.Error(DiagnosticCodes.InvalidBind, a.NameSpan, $"不支持的 @bind 参数 :{a.Parameter}（只支持 :event）");
            }
            else if (a.Name.StartsWith("bind-", StringComparison.Ordinal))
            {
                if (a.Parameter == null) bindProps.Add(a);
                else if (a.Parameter != "event") bag.Error(DiagnosticCodes.InvalidBind, a.NameSpan, $"不支持的 @{a.Name} 参数 :{a.Parameter}");
            }
        }

        bool isComponent = e.ElementKind == ElementKind.Component;
        if (bind != null)
        {
            if (isComponent)
            {
                bag.Error(DiagnosticCodes.InvalidBind, bind.NameSpan, $"组件 <{e.Name}> 上请用 @bind-属性名（如 @bind-Value）");
            }
            else if (intrinsic && e.Name != "input" && e.Name != "textarea")
            {
                bag.Error(DiagnosticCodes.InvalidBind, bind.NameSpan, $"<{e.Name}> 不支持 @bind（只有 input 与 textarea 支持）");
            }
            else if (intrinsic)
            {
                string evt = "input";
                if (bindEvent != null)
                {
                    string? ev = bindEvent.Value?.LiteralValue?.Trim();
                    if (ev == "change") evt = "change";
                    else if (ev != "input")
                        bag.Error(DiagnosticCodes.InvalidBind, bindEvent.Value?.Span ?? bindEvent.NameSpan, "@bind:event 的值应为 \"input\" 或 \"change\"");
                }
                var value = e.FindAttribute("value");
                if (value != null)
                    bag.Error(DiagnosticCodes.InvalidBind, value.NameSpan, "@bind 已经提供 value，不能再显式写 value");
                var handler = e.FindAttribute("@on" + evt);
                if (handler != null)
                    bag.Error(DiagnosticCodes.InvalidBind, handler.NameSpan, $"@bind 已经处理 on{evt} 事件，不能再显式写 @on{evt}");
            }
            CheckBindTarget(bind, bag);
        }
        else if (bindEvent != null)
        {
            bag.Error(DiagnosticCodes.InvalidBind, bindEvent.NameSpan, "@bind:event 需要与 @bind 一起使用");
        }

        Dictionary<string, AttributeSyntax>? byName = null; // 很多 @bind-X 时避免逐个线性查找
        foreach (var bp in bindProps)
        {
            string prop = bp.Name.Substring(5);
            if (!isComponent)
            {
                bag.Error(DiagnosticCodes.InvalidBind, bp.NameSpan, $"内建元素 <{e.Name}> 不支持 @{bp.Name}，请用 @bind");
            }
            else if (prop.Length == 0)
            {
                bag.Error(DiagnosticCodes.InvalidBind, bp.NameSpan, "@bind- 之后缺少属性名");
            }
            else
            {
                byName ??= IndexAttributes(e);
                foreach (var conflict in new[] { prop, prop + "Changed", "@" + prop + "Changed" })
                {
                    byName.TryGetValue(conflict, out var c);
                    if (c != null)
                        bag.Error(DiagnosticCodes.InvalidBind, c.NameSpan, $"@{bp.Name} 已经提供 {conflict.TrimStart('@')}，不能再显式写");
                }
            }
            CheckBindTarget(bp, bag);
        }
    }

    /// <summary>按完整名称索引元素的属性（重复时取第一个，与 <see cref="ElementSyntax.FindAttribute"/> 一致）。</summary>
    static Dictionary<string, AttributeSyntax> IndexAttributes(ElementSyntax e)
    {
        var map = new Dictionary<string, AttributeSyntax>(StringComparer.Ordinal);
        foreach (var a in e.Attributes)
            if (!map.ContainsKey(a.FullName)) map.Add(a.FullName, a);
        return map;
    }

    static void CheckBindTarget(AttributeSyntax bind, DiagnosticBag bag)
    {
        var v = bind.Value;
        if (v == null || v.Parts.Count == 0)
        {
            bag.Error(DiagnosticCodes.InvalidBind, bind.NameSpan, $"{bind.FullName} 需要绑定目标（State<T> 或可赋值的字段、属性、索引器）");
            return;
        }
        string? code = v.SingleExpression?.Code ?? v.LiteralText;
        if (code == null || !IsAssignable(code))
            bag.Error(DiagnosticCodes.InvalidBind, v.Span,
                $"{bind.FullName} 的目标必须是 State<T> 或可赋值的字段、属性、索引器");
    }

    /// <summary>语法上是否像一个可赋值的表达式：标识符后接 <c>.成员</c>、<c>[索引]</c>、<c>!</c>，不以调用结尾。</summary>
    internal static bool IsAssignable(string code)
    {
        string s = code.Trim();
        while (s.Length >= 2 && s[0] == '(' && CSharpScanner.FindClose(s, 0) == s.Length - 1)
            s = s.Substring(1, s.Length - 2).Trim();
        int i = 0;
        if (i < s.Length && s[i] == '@') i++;
        if (i >= s.Length || !MarkupParser.IsIdentStart(s[i])) return false;
        int identStart = i;
        while (i < s.Length && MarkupParser.IsIdentPart(s[i])) i++;
        string first = s.Substring(identStart, i - identStart);
        if (first == "true" || first == "false" || first == "null" || first == "new" || first == "default" || first == "typeof")
            return false;
        bool onlyThis = first == "this";
        while (i < s.Length)
        {
            char c = s[i];
            if (c == '.' && i + 1 < s.Length && MarkupParser.IsIdentStart(s[i + 1]))
            {
                i++;
                while (i < s.Length && MarkupParser.IsIdentPart(s[i])) i++;
                onlyThis = false;
            }
            else if (c == '[')
            {
                int close = CSharpScanner.FindClose(s, i);
                if (close < 0) return false;
                i = close + 1;
                onlyThis = false;
            }
            else if (c == '!' && i + 1 < s.Length && (s[i + 1] == '.' || s[i + 1] == '['))
            {
                i++;
            }
            else
            {
                return false;
            }
        }
        return !onlyThis;
    }

    // ---------------------------------------------------------------- 最接近的名称

    internal static string? Nearest(string name, IEnumerable<string> candidates)
    {
        string? best = null;
        int bestDistance = int.MaxValue;
        int limit = name.Length <= 3 ? 1 : name.Length <= 6 ? 2 : 3;
        string lower = name.ToLowerInvariant();
        foreach (var c in candidates)
        {
            // 长度差超过上限的候选不可能足够接近：跳过，超长名称不必付出 O(长度 × 候选长度) 的代价
            if (Math.Abs(c.Length - name.Length) > limit) continue;
            int d = Distance(lower, c.ToLowerInvariant());
            if (d < bestDistance)
            {
                bestDistance = d;
                best = c;
            }
        }
        return bestDistance <= limit ? best : null;
    }

    /// <summary>编辑距离（含相邻换位，Damerau 的 OSA 变体）。</summary>
    static int Distance(string a, string b)
    {
        var d = new int[a.Length + 1, b.Length + 1];
        for (int i = 0; i <= a.Length; i++) d[i, 0] = i;
        for (int j = 0; j <= b.Length; j++) d[0, j] = j;
        for (int i = 1; i <= a.Length; i++)
        {
            for (int j = 1; j <= b.Length; j++)
            {
                int cost = a[i - 1] == b[j - 1] ? 0 : 1;
                int v = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + cost);
                if (i > 1 && j > 1 && a[i - 1] == b[j - 2] && a[i - 2] == b[j - 1])
                    v = Math.Min(v, d[i - 2, j - 2] + 1);
                d[i, j] = v;
            }
        }
        return d[a.Length, b.Length];
    }
}
