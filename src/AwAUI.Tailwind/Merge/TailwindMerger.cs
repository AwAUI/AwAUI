using System;
using System.Collections.Generic;
using System.Text;
using AwAUI.Tailwind.Merge;

namespace AwAUI.Tailwind;

/// <summary>
/// 扩展 <see cref="TailwindMerger"/> 的默认配置：给主题刻度加值、登记自定义工具的类组与冲突关系（对应 tailwind-merge 的 <c>extendTailwindMerge</c>）。
/// </summary>
public sealed class TailwindMergeOptions
{
    internal readonly MergeConfigBuilder Builder = DefaultMergeConfig.Create();

    /// <summary>类名前缀（与 <c>@import "tailwindcss" prefix(tw)</c> 对应）；带前缀的类名才参与合并。</summary>
    public string? Prefix
    {
        get => Builder.Prefix;
        set => Builder.Prefix = value;
    }

    /// <summary>结果缓存的容量（默认 500，与 tailwind-merge 相同；0 表示不缓存）。</summary>
    public int CacheSize { get; set; } = 500;

    /// <summary>给主题刻度（<c>color</c>、<c>text</c>、<c>radius</c>、<c>shadow</c> ...）追加具名值，例如自定义的 <c>text-2xs</c>。</summary>
    public TailwindMergeOptions AddThemeValues(string themeKey, params string[] values)
    {
        var defs = new Def[values.Length];
        for (int i = 0; i < values.Length; i++) defs[i] = values[i];
        Builder.Theme(themeKey, defs);
        return this;
    }

    /// <summary>登记一个由固定类名组成的类组（同组的类名互相覆盖），例如自定义 <c>@utility</c>。</summary>
    public TailwindMergeOptions AddClassGroup(string groupId, params string[] classNames)
    {
        var defs = new Def[classNames.Length];
        for (int i = 0; i < classNames.Length; i++) defs[i] = classNames[i];
        Builder.Group(groupId, defs);
        return this;
    }

    /// <summary>登记冲突：<paramref name="groupId"/> 出现时覆盖之前的 <paramref name="conflictingGroupIds"/>（如 <c>p</c> 覆盖 <c>px</c>）。</summary>
    public TailwindMergeOptions AddConflict(string groupId, params string[] conflictingGroupIds)
    {
        Builder.Conflict(groupId, conflictingGroupIds);
        return this;
    }
}

/// <summary>可与 <see cref="TailwindMerge.Cn(ClassValue[])"/> 一起使用的条件类名（<c>clsx</c> 风格）。</summary>
public readonly struct ClassValue
{
    /// <summary>类名文本；null 或空串被忽略。</summary>
    public readonly string? Value;

    ClassValue(string? value) => Value = value;

    /// <summary>字符串直接作为类名。</summary>
    public static implicit operator ClassValue(string? value) => new(value);

    /// <summary>条件成立时才加入。</summary>
    public static ClassValue When(bool condition, string? value) => new(condition ? value : null);
}

/// <summary>
/// tailwind-merge v3 语义的类名合并：后出现的类覆盖先出现的冲突类（<c>"px-2 py-1 p-3"</c> → <c>"p-3"</c>）。
/// 变体、<c>!</c> 与后缀修饰符（<c>/50</c>、<c>text-sm/6</c>）分别对待；任意值与任意属性按类组判定。
/// </summary>
/// <remarks>线程安全：配置构造后只读，结果缓存内部加锁。</remarks>
public sealed class TailwindMerger
{
    sealed class ClassPart
    {
        public Dictionary<string, ClassPart>? Next;
        public List<KeyValuePair<string, Func<string, bool>>>? Validators;
        public string? GroupId;
    }

    readonly ClassPart _root = new();
    readonly Dictionary<string, string[]> _conflicts;
    readonly Dictionary<string, string[]> _modifierConflicts;
    readonly HashSet<string> _postfixLookups;
    readonly Dictionary<string, int> _orderSensitive = new(StringComparer.Ordinal);
    readonly string? _prefix;
    readonly int _cacheSize;
    readonly object _cacheGate = new();
    Dictionary<string, string> _cache = new(StringComparer.Ordinal);
    Dictionary<string, string> _previousCache = new(StringComparer.Ordinal);
    Dictionary<(string, string), string> _pairCache = new();

    /// <summary>默认配置（与 tailwind-merge v3.7 的 <c>twMerge</c> 相同）。</summary>
    public static TailwindMerger Default { get; } = new(new TailwindMergeOptions());

    /// <summary>用扩展后的配置创建合并器。</summary>
    public TailwindMerger(TailwindMergeOptions options)
    {
        if (options == null) throw new ArgumentNullException(nameof(options));
        var b = options.Builder;
        _prefix = b.Prefix;
        _cacheSize = Math.Max(0, options.CacheSize);
        foreach (var g in b.Groups)
        {
            foreach (var def in g.Value) Process(def, _root, g.Key, b.Themes, 0);
        }
        _conflicts = ToArrays(b.Conflicts);
        _modifierConflicts = ToArrays(b.ModifierConflicts);
        _postfixLookups = new HashSet<string>(b.PostfixLookups, StringComparer.Ordinal);
        for (int i = 0; i < b.OrderSensitiveModifiers.Count; i++) _orderSensitive[b.OrderSensitiveModifiers[i]] = i;
    }

    static Dictionary<string, string[]> ToArrays(Dictionary<string, List<string>> d)
    {
        var r = new Dictionary<string, string[]>(StringComparer.Ordinal);
        foreach (var kv in d) r[kv.Key] = kv.Value.ToArray();
        return r;
    }

    void Process(Def def, ClassPart part, string groupId, Dictionary<string, List<Def>> themes, int depth)
    {
        if (depth > 32) return;
        switch (def.Kind)
        {
            case DefKind.Literal:
                (def.Text!.Length == 0 ? part : GetPart(part, def.Text)).GroupId = groupId;
                break;
            case DefKind.Validator:
                (part.Validators ??= new List<KeyValuePair<string, Func<string, bool>>>()).Add(new KeyValuePair<string, Func<string, bool>>(groupId, def.Validator!));
                break;
            case DefKind.Theme:
                if (themes.TryGetValue(def.Text!, out var themeDefs))
                {
                    foreach (var t in themeDefs) Process(t, part, groupId, themes, depth + 1);
                }
                break;
            case DefKind.Object:
                foreach (var (key, defs) in def.Entries!)
                {
                    var target = GetPart(part, key);
                    foreach (var d in defs) Process(d, target, groupId, themes, depth + 1);
                }
                break;
        }
    }

    static ClassPart GetPart(ClassPart part, string path)
    {
        var current = part;
        foreach (var p in path.Split('-'))
        {
            current.Next ??= new Dictionary<string, ClassPart>(StringComparer.Ordinal);
            if (!current.Next.TryGetValue(p, out var next))
            {
                next = new ClassPart();
                current.Next[p] = next;
            }
            current = next;
        }
        return current;
    }

    // ------------------------------------------------------------------
    // 公开 API
    // ------------------------------------------------------------------

    /// <summary>合并一个以空白分隔的类名列表。</summary>
    public string Merge(string? classList)
    {
        if (string.IsNullOrEmpty(classList)) return "";
        if (_cacheSize == 0) return MergeCore(classList!);
        lock (_cacheGate)
        {
            if (_cache.TryGetValue(classList!, out var hit)) return hit;
            if (_previousCache.TryGetValue(classList!, out hit))
            {
                Store(classList!, hit);
                return hit;
            }
        }
        string result = MergeCore(classList!);
        lock (_cacheGate) Store(classList!, result);
        return result;
    }

    /// <summary>两段类名合并（组件默认类 + 调用方的 <c>Class</c>），命中缓存时不分配。</summary>
    public string Merge(string? first, string? second)
    {
        if (string.IsNullOrEmpty(first)) return Merge(second);
        if (string.IsNullOrEmpty(second)) return Merge(first);
        if (_cacheSize > 0)
        {
            lock (_cacheGate)
            {
                if (_pairCache.TryGetValue((first!, second!), out var hit)) return hit;
            }
        }
        string result = Merge(first + " " + second);
        if (_cacheSize > 0)
        {
            lock (_cacheGate)
            {
                if (_pairCache.Count >= _cacheSize) _pairCache = new Dictionary<(string, string), string>();
                _pairCache[(first!, second!)] = result;
            }
        }
        return result;
    }

    void Store(string key, string value)
    {
        _cache[key] = value;
        if (_cache.Count > _cacheSize)
        {
            // 与 tailwind-merge 的 LRU 相同的两代缓存：满了就把当前代降为上一代
            _previousCache = _cache;
            _cache = new Dictionary<string, string>(StringComparer.Ordinal);
        }
    }

    /// <summary>不经缓存的合并（移植自 tailwind-merge <c>mergeClassList</c>）。</summary>
    string MergeCore(string classList)
    {
        var classNames = SplitClasses(classList);
        var inConflict = new HashSet<string>(StringComparer.Ordinal);
        var kept = new List<string>(classNames.Count);
        for (int index = classNames.Count - 1; index >= 0; index--)
        {
            string original = classNames[index];
            var parsed = ParseClassName(original);
            if (parsed.IsExternal)
            {
                kept.Add(original);
                continue;
            }
            bool hasPostfix = parsed.PostfixPosition > 0;
            string? groupId;
            if (hasPostfix)
            {
                groupId = GetClassGroupId(parsed.BaseClassName.Substring(0, parsed.PostfixPosition));
                string? withPostfix = groupId != null && _postfixLookups.Contains(groupId) ? GetClassGroupId(parsed.BaseClassName) : null;
                if (withPostfix != null && withPostfix != groupId)
                {
                    groupId = withPostfix;
                    hasPostfix = false;
                }
            }
            else groupId = GetClassGroupId(parsed.BaseClassName);

            if (groupId == null)
            {
                if (!hasPostfix)
                {
                    kept.Add(original);
                    continue;
                }
                groupId = GetClassGroupId(parsed.BaseClassName);
                if (groupId == null)
                {
                    kept.Add(original);
                    continue;
                }
                hasPostfix = false;
            }

            string variantModifier = parsed.Modifiers.Count == 0 ? "" : parsed.Modifiers.Count == 1 ? parsed.Modifiers[0] : string.Join(":", SortModifiers(parsed.Modifiers));
            string modifierId = parsed.HasImportant ? variantModifier + "!" : variantModifier;
            string classId = modifierId + groupId;
            if (inConflict.Contains(classId)) continue;
            inConflict.Add(classId);
            foreach (var g in GetConflictingGroups(groupId, hasPostfix)) inConflict.Add(modifierId + g);
            kept.Add(original);
        }
        if (kept.Count == classNames.Count && kept.Count == 1) return kept[0];
        var sb = new StringBuilder(classList.Length);
        for (int i = kept.Count - 1; i >= 0; i--)
        {
            if (sb.Length > 0) sb.Append(' ');
            sb.Append(kept[i]);
        }
        return sb.ToString();
    }

    static List<string> SplitClasses(string s)
    {
        var list = new List<string>();
        int i = 0;
        while (i < s.Length)
        {
            while (i < s.Length && char.IsWhiteSpace(s[i])) i++;
            int start = i;
            while (i < s.Length && !char.IsWhiteSpace(s[i])) i++;
            if (i > start) list.Add(s.Substring(start, i - start));
        }
        return list;
    }

    string[] GetConflictingGroups(string groupId, bool hasPostfix)
    {
        _conflicts.TryGetValue(groupId, out var baseConflicts);
        if (hasPostfix && _modifierConflicts.TryGetValue(groupId, out var modifierConflicts))
        {
            if (baseConflicts == null) return modifierConflicts;
            var merged = new string[baseConflicts.Length + modifierConflicts.Length];
            baseConflicts.CopyTo(merged, 0);
            modifierConflicts.CopyTo(merged, baseConflicts.Length);
            return merged;
        }
        return baseConflicts ?? Array.Empty<string>();
    }

    struct ParsedClassName
    {
        public List<string> Modifiers;
        public bool HasImportant;
        public string BaseClassName;
        public int PostfixPosition;
        public bool IsExternal;
    }

    ParsedClassName ParseClassName(string className)
    {
        if (_prefix != null)
        {
            string full = _prefix + ":";
            if (!className.StartsWith(full, StringComparison.Ordinal)) return new ParsedClassName { Modifiers = new List<string>(), BaseClassName = className, IsExternal = true };
            className = className.Substring(full.Length);
        }
        // 超长类名不可能是 Tailwind 工具类；直接当作外部类，避免正则校验器在敌意输入上退化
        if (className.Length > CandidateScanner.MaxCandidateLength) return new ParsedClassName { Modifiers = new List<string>(), BaseClassName = className, IsExternal = true };
        var modifiers = new List<string>();
        int bracketDepth = 0, parenDepth = 0, modifierStart = 0, postfix = -1;
        for (int i = 0; i < className.Length; i++)
        {
            char c = className[i];
            if (bracketDepth == 0 && parenDepth == 0)
            {
                if (c == ':')
                {
                    modifiers.Add(className.Substring(modifierStart, i - modifierStart));
                    modifierStart = i + 1;
                    continue;
                }
                if (c == '/')
                {
                    postfix = i;
                    continue;
                }
            }
            if (c == '[') bracketDepth++;
            else if (c == ']') bracketDepth--;
            else if (c == '(') parenDepth++;
            else if (c == ')') parenDepth--;
        }
        string withImportant = modifiers.Count == 0 ? className : className.Substring(modifierStart);
        string baseName = withImportant;
        bool important = false;
        if (withImportant.EndsWith("!", StringComparison.Ordinal))
        {
            baseName = withImportant.Substring(0, withImportant.Length - 1);
            important = true;
        }
        else if (withImportant.StartsWith("!", StringComparison.Ordinal))
        {
            baseName = withImportant.Substring(1);
            important = true;
        }
        int postfixPosition = postfix > 0 && postfix > modifierStart ? postfix - modifierStart : 0;
        return new ParsedClassName { Modifiers = modifiers, HasImportant = important, BaseClassName = baseName, PostfixPosition = postfixPosition };
    }

    List<string> SortModifiers(List<string> modifiers)
    {
        var result = new List<string>(modifiers.Count);
        var segment = new List<string>();
        foreach (var m in modifiers)
        {
            bool sensitive = (m.Length > 0 && m[0] == '[') || _orderSensitive.ContainsKey(m);
            if (sensitive)
            {
                if (segment.Count > 0)
                {
                    segment.Sort(StringComparer.Ordinal);
                    result.AddRange(segment);
                    segment.Clear();
                }
                result.Add(m);
            }
            else segment.Add(m);
        }
        if (segment.Count > 0)
        {
            segment.Sort(StringComparer.Ordinal);
            result.AddRange(segment);
        }
        return result;
    }

    string? GetClassGroupId(string className)
    {
        if (className.Length >= 2 && className[0] == '[' && className[className.Length - 1] == ']')
        {
            string content = className.Substring(1, className.Length - 2);
            int colon = content.IndexOf(':');
            if (colon <= 0) return null;
            return "arbitrary.." + content.Substring(0, colon);
        }
        var parts = className.Split('-');
        int start = parts[0].Length == 0 && parts.Length > 1 ? 1 : 0;
        return GetGroupRecursive(parts, start, _root);
    }

    static string? GetGroupRecursive(string[] parts, int start, ClassPart part)
    {
        // 深度受类名长度约束（每层至少消耗一个 '-' 分段）
        if (parts.Length - start == 0) return part.GroupId;
        if (part.Next != null && part.Next.TryGetValue(parts[start], out var next))
        {
            string? r = GetGroupRecursive(parts, start + 1, next);
            if (r != null) return r;
        }
        if (part.Validators == null) return null;
        string rest = start == 0 ? string.Join("-", parts) : string.Join("-", parts, start, parts.Length - start);
        foreach (var v in part.Validators)
        {
            if (v.Value(rest)) return v.Key;
        }
        return null;
    }
}

/// <summary>
/// 组件代码使用的类名工具：<c>Cn</c> 条件拼接并按 tailwind-merge 语义消解冲突（shadcn/ui 的 <c>cn()</c>）。
/// 在 <c>.awaui</c> 中用 <c>@using static AwAUI.Tailwind.TailwindMerge</c> 后直接写 <c>Cn(...)</c>。
/// </summary>
public static class TailwindMerge
{
    /// <summary>合并一个类名列表（默认配置）。</summary>
    public static string Cn(string? classes) => TailwindMerger.Default.Merge(classes);

    /// <summary>合并两段类名：组件默认类在前、调用方的类在后（后者覆盖前者）。命中缓存时不分配。</summary>
    public static string Cn(string? first, string? second) => TailwindMerger.Default.Merge(first, second);

    /// <summary>合并三段类名。</summary>
    public static string Cn(string? first, string? second, string? third)
    {
        if (string.IsNullOrEmpty(third)) return Cn(first, second);
        if (string.IsNullOrEmpty(first)) return Cn(second, third);
        if (string.IsNullOrEmpty(second)) return Cn(first, third);
        return TailwindMerger.Default.Merge(first + " " + second, third);
    }

    /// <summary>合并任意多段（<c>null</c> / 空串 / 条件不成立的 <see cref="ClassValue"/> 被跳过）。</summary>
    public static string Cn(params ClassValue[] values)
    {
        if (values == null || values.Length == 0) return "";
        var sb = new StringBuilder();
        foreach (var v in values)
        {
            if (string.IsNullOrEmpty(v.Value)) continue;
            if (sb.Length > 0) sb.Append(' ');
            sb.Append(v.Value);
        }
        return TailwindMerger.Default.Merge(sb.ToString());
    }
}
