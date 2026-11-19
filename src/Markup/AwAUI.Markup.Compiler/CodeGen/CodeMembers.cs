using System;
using System.Collections.Generic;
using System.Text;
using AwAUI.Markup.Compiler.Parsing;

namespace AwAUI.Markup.Compiler.CodeGen;

/// <summary>
/// <c>@code</c> 块成员声明的轻量扫描（不解析 C#）：找出 Props（public、非 static、public get 加 public set/init 的非索引器属性，
/// 名字不是 <c>Key</c>），类型为 <c>State&lt;T&gt;</c> 的字段/属性，以及手写的胶水方法重写。
/// 这是没有 Roslyn 时（命令行、测试）的退路；源生成器用 Roslyn 的符号得到同样的信息（含代码后置文件与基类），
/// 通过 <see cref="CSharpEmitOptions.ClassInfo"/> 传入。线性时间，任何输入都不抛异常。
/// </summary>
internal sealed class CodeMembers
{
    static readonly HashSet<string> Modifiers = new(StringComparer.Ordinal)
    {
        "public", "private", "protected", "internal", "static", "readonly", "override", "virtual", "new", "sealed",
        "abstract", "required", "partial", "unsafe", "extern", "async", "volatile", "const", "file", "ref", "fixed",
    };

    static readonly HashSet<string> TypeKeywords = new(StringComparer.Ordinal)
    {
        "class", "struct", "interface", "enum", "record", "delegate", "event", "operator", "implicit", "explicit",
    };

    public List<ComponentPropDeclaration> Props { get; } = new();

    /// <summary>类型为 <c>State&lt;T&gt;</c> 的字段与属性名。</summary>
    public HashSet<string> StateMembers { get; } = new(StringComparer.Ordinal);

    public bool DeclaresPropsEqual { get; private set; }
    public bool DeclaresAdoptProps { get; private set; }
    public bool DeclaresCanAdoptProps { get; private set; }

    public static CodeMembers Scan(IEnumerable<string> codeBlocks)
    {
        var result = new CodeMembers();
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var code in codeBlocks) result.ScanBlock(code, names);
        return result;
    }

    void ScanBlock(string s, HashSet<string> propNames)
    {
        int i = 0;
        int memberStart = 0;
        // 本成员的头部（第一个顶层 { ( = ; 之前）之后是否已出现 = 或 =>（初始化器 / 表达式体：其中的 {} 不结束成员）
        bool initializer = false;
        int headerEnd = -1;
        char headerStop = '\0';
        int angle = 0;
        // HasIdentifierAfterModifiers 的增量状态：同一成员头部中反复出现的 ( 只扫描新增的部分（否则 ()()()… 为二次方）
        var probe = new IdentifierProbe();
        while (i < s.Length)
        {
            int skipped = CSharpScanner.SkipAtom(s, i);
            if (skipped != i)
            {
                if (memberStart == i && CSharpScanner.IsTriviaAtom(s, i)) memberStart = skipped; // 成员前的注释
                i = skipped;
                continue;
            }
            char c = s[i];
            if (memberStart == i && (char.IsWhiteSpace(c)))
            {
                memberStart = i + 1;
                i++;
                continue;
            }
            switch (c)
            {
                case '<' when headerEnd < 0:
                    angle++;
                    i++;
                    continue;
                case '>' when headerEnd < 0 && angle > 0:
                    angle--;
                    i++;
                    continue;
                case '[':
                {
                    int close = CSharpScanner.FindClose(s, i);
                    if (close < 0) return;
                    i = close + 1;
                    continue;
                }
                case '(':
                {
                    // 元组类型 (int, int) 出现在类型位置（尚无标识符）或泛型实参中时属于头部
                    if (headerEnd < 0 && angle == 0 && probe.HasIdentifierAfterModifiers(s, memberStart, i))
                    {
                        headerEnd = i;
                        headerStop = '(';
                    }
                    int close = CSharpScanner.FindClose(s, i);
                    if (close < 0) return;
                    i = close + 1;
                    continue;
                }
                case '=':
                    if (headerEnd < 0 && angle == 0)
                    {
                        headerEnd = i;
                        headerStop = '=';
                    }
                    if (headerEnd >= 0) initializer = true;
                    i++;
                    continue;
                case '{':
                {
                    int open = i;
                    if (headerEnd < 0)
                    {
                        headerEnd = i;
                        headerStop = '{';
                        angle = 0;
                    }
                    int close = CSharpScanner.FindClose(s, i);
                    if (close < 0) return;
                    int after = close + 1;
                    if (!initializer)
                    {
                        // 成员体（访问器块、方法体、嵌套类型）：其后若是 = 则为自动属性初始化器，继续到 ;
                        int j = SkipWhitespaceAndComments(s, after);
                        if (j < s.Length && s[j] == '=' && (j + 1 >= s.Length || s[j + 1] != '>'))
                        {
                            initializer = true;
                            i = j + 1;
                            if (headerStop == '{') Classify(s, memberStart, headerEnd, headerStop, open, close, propNames);
                            continue;
                        }
                        if (headerStop == '{' || headerStop == '(') Classify(s, memberStart, headerEnd, headerStop, open, close, propNames);
                        memberStart = after;
                        headerEnd = -1;
                        headerStop = '\0';
                        initializer = false;
                        angle = 0;
                    }
                    i = after;
                    continue;
                }
                case ';':
                    if (headerEnd < 0)
                    {
                        headerEnd = i;
                        headerStop = ';';
                    }
                    if (headerStop == '=' || headerStop == ';' || headerStop == '(') Classify(s, memberStart, headerEnd, headerStop, -1, -1, propNames);
                    memberStart = i + 1;
                    headerEnd = -1;
                    headerStop = '\0';
                    initializer = false;
                    angle = 0;
                    i++;
                    continue;
                default:
                    i++;
                    continue;
            }
        }
    }

    /// <summary>
    /// 对一个成员分类。<paramref name="headerStop"/>：<c>{</c> 属性 / 嵌套类型；<c>(</c> 方法；<c>=</c> / <c>;</c> 字段。
    /// 属性的访问器块是 [<paramref name="i0"/>, <paramref name="close"/>]。
    /// </summary>
    void Classify(string s, int start, int headerEnd, char headerStop, int i0, int close, HashSet<string> propNames)
    {
        var tokens = HeaderTokens(s, start, headerEnd, out int typeStart);
        if (tokens is null) return;
        var mods = tokens;
        foreach (var m in mods)
            if (TypeKeywords.Contains(m)) return;

        // 头部去掉修饰符后为「类型 名称」
        string rest = s.Substring(typeStart, headerEnd - typeStart).Trim();
        if (headerStop == '(')
        {
            // 方法：名称是 ( 之前的最后一个标识符（泛型方法去掉 <...>）
            string head = rest;
            int lt = head.IndexOf('<');
            string name = LastIdentifier(lt >= 0 && head.EndsWith(">", StringComparison.Ordinal) ? head.Substring(0, lt) : head);
            if (mods.Contains("override"))
            {
                switch (name)
                {
                    case "PropsEqual": DeclaresPropsEqual = true; break;
                    case "AdoptProps": DeclaresAdoptProps = true; break;
                    case "CanAdoptProps": DeclaresCanAdoptProps = true; break;
                }
            }
            return;
        }

        string memberName = LastIdentifier(rest);
        if (memberName.Length == 0 || memberName == "this") return;
        int nameAt = rest.Length - memberName.Length;
        string type = Normalize(rest.Substring(0, nameAt));
        if (type.Length == 0 || type.EndsWith(".", StringComparison.Ordinal)) return; // 没有类型，或显式接口实现
        if (memberName.StartsWith("@", StringComparison.Ordinal)) memberName = memberName.Substring(1);

        if (IsStateType(type)) StateMembers.Add(memberName);

        if (headerStop != '{' || i0 < 0) return;
        // 属性：检查访问器
        if (!mods.Contains("public") || mods.Contains("static")) return;
        if (memberName == "Key" || type.StartsWith("ref ", StringComparison.Ordinal)) return;
        AccessorInfo(s, i0 + 1, close, out bool publicGet, out bool publicSet, out bool publicInit);
        if (!publicGet || !(publicSet || publicInit)) return;
        if (!propNames.Add(memberName)) return;
        Props.Add(new ComponentPropDeclaration(memberName, type, isInitOnly: publicInit && !publicSet));
    }

    /// <summary>成员头部的修饰符（含特性之后的部分）；<paramref name="typeStart"/> 为第一个非修饰符的位置。</summary>
    static List<string>? HeaderTokens(string s, int start, int end, out int typeStart)
    {
        var mods = new List<string>();
        int i = start;
        typeStart = start;
        while (i < end)
        {
            i = SkipWhitespaceAndComments(s, i);
            if (i >= end) break;
            if (s[i] == '[')
            {
                int close = CSharpScanner.FindClose(s, i);
                if (close < 0 || close >= end) return null;
                i = close + 1;
                continue;
            }
            if (!MarkupParser.IsIdentStart(s[i])) break;
            int j = i;
            while (j < end && MarkupParser.IsIdentPart(s[j])) j++;
            string word = s.Substring(i, j - i);
            if (!Modifiers.Contains(word))
            {
                typeStart = i;
                // 类型关键字（class / event ...）也记入，供调用方排除
                if (TypeKeywords.Contains(word)) mods.Add(word);
                return mods;
            }
            mods.Add(word);
            i = j;
        }
        typeStart = Math.Min(i, end);
        return mods;
    }

    /// <summary>
    /// [memberStart, pos) 中，去掉修饰符与特性后是否已有标识符（即 ( 不是类型开头的元组）。
    /// 同一成员内 <c>pos</c> 只增不减：修饰符部分只解析一次（它止于第一个 ( 之前，之后不变），标识符只在新增的区间里找，总体线性。
    /// </summary>
    struct IdentifierProbe
    {
        int _memberStart;
        int _scanned;
        bool _known;
        bool _found;

        public bool HasIdentifierAfterModifiers(string s, int memberStart, int pos)
        {
            if (!_known || _memberStart != memberStart)
            {
                _known = true;
                _memberStart = memberStart;
                var mods = HeaderTokens(s, memberStart, pos, out int typeStart);
                _found = mods is null;
                _scanned = typeStart;
            }
            for (int i = _scanned; !_found && i < pos; i++)
                if (MarkupParser.IsIdentPart(s[i])) _found = true;
            if (pos > _scanned) _scanned = pos;
            return _found;
        }
    }

    static void AccessorInfo(string s, int start, int end, out bool publicGet, out bool publicSet, out bool publicInit)
    {
        publicGet = publicSet = publicInit = false;
        bool restricted = false;
        int i = start;
        while (i < end)
        {
            int skipped = CSharpScanner.SkipAtom(s, i);
            if (skipped != i)
            {
                i = skipped;
                continue;
            }
            char c = s[i];
            if (c == '{' || c == '(' || c == '[')
            {
                int close = CSharpScanner.FindClose(s, i);
                if (close < 0) return;
                i = close + 1;
                if (c == '{') restricted = false; // 访问器体结束
                continue;
            }
            if (c == ';')
            {
                restricted = false;
                i++;
                continue;
            }
            if (c == '=' && i + 1 < end && s[i + 1] == '>')
            {
                // 表达式体访问器：跳到 ;
                int semi = i + 2;
                while (semi < end)
                {
                    int sk = CSharpScanner.SkipAtom(s, semi);
                    if (sk != semi) { semi = sk; continue; }
                    if (s[semi] == ';') break;
                    if (s[semi] == '(' || s[semi] == '[' || s[semi] == '{')
                    {
                        int close = CSharpScanner.FindClose(s, semi);
                        if (close < 0) return;
                        semi = close + 1;
                        continue;
                    }
                    semi++;
                }
                i = semi;
                continue;
            }
            if (MarkupParser.IsIdentStart(c))
            {
                int j = i;
                while (j < end && MarkupParser.IsIdentPart(s[j])) j++;
                string word = s.Substring(i, j - i);
                switch (word)
                {
                    case "private":
                    case "protected":
                    case "internal":
                        restricted = true;
                        break;
                    case "get":
                        if (!restricted) publicGet = true;
                        break;
                    case "set":
                        if (!restricted) publicSet = true;
                        break;
                    case "init":
                        if (!restricted) publicInit = true;
                        break;
                }
                i = j;
                continue;
            }
            i++;
        }
    }

    static int SkipWhitespaceAndComments(string s, int i)
    {
        while (i < s.Length)
        {
            if (char.IsWhiteSpace(s[i]))
            {
                i++;
                continue;
            }
            int skipped = CSharpScanner.SkipAtom(s, i);
            if (skipped != i && CSharpScanner.IsTriviaAtom(s, i))
            {
                i = skipped;
                continue;
            }
            break;
        }
        return i;
    }

    static string LastIdentifier(string text)
    {
        int end = text.Length;
        while (end > 0 && char.IsWhiteSpace(text[end - 1])) end--;
        int start = end;
        while (start > 0 && MarkupParser.IsIdentPart(text[start - 1])) start--;
        if (start > 0 && text[start - 1] == '@') start--;
        if (start == end || (start < end && !MarkupParser.IsIdentStart(text[start]) && text[start] != '@')) return "";
        return text.Substring(start, end - start);
    }

    /// <summary>压缩空白；去掉注释以外的内容保持原样（类型文本在同一个类中仍然可解析）。</summary>
    static string Normalize(string type)
    {
        var sb = new StringBuilder(type.Length);
        bool space = false;
        foreach (char c in type)
        {
            if (char.IsWhiteSpace(c))
            {
                space = sb.Length > 0;
                continue;
            }
            if (space && c != '<' && c != '>' && c != ',' && c != '?' && c != '[' && c != ']' && sb[sb.Length - 1] != '<' && sb[sb.Length - 1] != ',')
                sb.Append(' ');
            else if (space && sb[sb.Length - 1] == ',')
                sb.Append(' ');
            space = false;
            sb.Append(c);
        }
        return sb.ToString();
    }

    /// <summary>类型文本是否是 <c>State&lt;…&gt;</c>（可带 <c>AwAUI.UI.</c> / <c>global::AwAUI.UI.</c> 限定，可空）。</summary>
    internal static bool IsStateType(string type)
    {
        string t = type;
        if (t.StartsWith("global::", StringComparison.Ordinal)) t = t.Substring(8);
        if (t.StartsWith("AwAUI.UI.", StringComparison.Ordinal)) t = t.Substring(9);
        return t.StartsWith("State<", StringComparison.Ordinal);
    }
}
