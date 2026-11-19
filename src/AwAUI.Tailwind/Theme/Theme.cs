using System;
using System.Collections.Generic;
using AwAUI.Tailwind.Ast;
using AwAUI.Tailwind.Internal;

namespace AwAUI.Tailwind.Theming;

/// <summary>主题变量选项（与 Tailwind 的 <c>ThemeOptions</c> 位一致）。</summary>
[Flags]
internal enum ThemeOptions
{
    None = 0,
    /// <summary><c>@theme inline</c>：工具类直接内联值而不是引用变量。</summary>
    Inline = 1 << 0,
    /// <summary><c>@theme reference</c>：不输出变量，引用时带回退值。</summary>
    Reference = 1 << 1,
    /// <summary><c>@theme default</c>：默认主题，可被用户值覆盖。</summary>
    Default = 1 << 2,
    /// <summary><c>@theme static</c>：即使未使用也输出。</summary>
    Static = 1 << 3,
    /// <summary>已被工具类使用（决定是否输出）。</summary>
    Used = 1 << 4,
}

internal sealed class ThemeEntry
{
    public string Key = "";
    public string Value = "";
    public ThemeOptions Options;
}

/// <summary>
/// 主题变量表（移植自 Tailwind <c>theme.ts</c>）。键为反转义后的自定义属性名，保持插入顺序。
/// </summary>
internal sealed class Theme
{
    readonly Dictionary<string, LinkedListNode<ThemeEntry>> _map = new(StringComparer.Ordinal);
    readonly LinkedList<ThemeEntry> _order = new();
    readonly List<AstNode> _keyframes = new();

    public string? Prefix;

    /// <summary>这些命名空间下有更具体的子命名空间，解析时需跳过（<c>--font</c> 不应匹配 <c>--font-weight-*</c>）。</summary>
    static readonly Dictionary<string, string[]> s_ignoredNamespaces = new(StringComparer.Ordinal)
    {
        ["--font"] = new[] { "--font-weight", "--font-size" },
        ["--inset"] = new[] { "--inset-shadow", "--inset-ring" },
        ["--text"] = new[] { "--text-color", "--text-decoration-color", "--text-decoration-thickness", "--text-indent", "--text-shadow", "--text-underline-offset" },
        ["--grid-column"] = new[] { "--grid-column-start", "--grid-column-end" },
        ["--grid-row"] = new[] { "--grid-row-start", "--grid-row-end" },
    };

    static bool IsIgnoredThemeKey(string themeKey, string ns)
    {
        if (!s_ignoredNamespaces.TryGetValue(ns, out var subs)) return false;
        foreach (var s in subs)
        {
            if (themeKey == s || themeKey.StartsWith(s + "-", StringComparison.Ordinal)) return true;
        }
        return false;
    }

    public int Count => _map.Count;

    public void Add(string key, string value, ThemeOptions options = ThemeOptions.None)
    {
        if (key.EndsWith("-*", StringComparison.Ordinal))
        {
            if (value != "initial") throw new TailwindException($"Invalid theme value `{value}` for namespace `{key}`");
            if (key == "--*") Clear();
            else ClearNamespace(key.Substring(0, key.Length - 2), ThemeOptions.None);
        }
        if ((options & ThemeOptions.Default) != 0)
        {
            if (_map.TryGetValue(key, out var existing) && (existing.Value.Options & ThemeOptions.Default) == 0) return;
        }
        if (value == "initial")
        {
            Remove(key);
            return;
        }
        if (_map.TryGetValue(key, out var node))
        {
            node.Value.Value = value;
            node.Value.Options = options;
        }
        else
        {
            var entry = new ThemeEntry { Key = key, Value = value, Options = options };
            _map[key] = _order.AddLast(entry);
        }
    }

    void Clear()
    {
        _map.Clear();
        _order.Clear();
    }

    void Remove(string key)
    {
        if (_map.TryGetValue(key, out var node))
        {
            _order.Remove(node);
            _map.Remove(key);
        }
    }

    public void ClearNamespace(string ns, ThemeOptions clearOptions)
    {
        s_ignoredNamespaces.TryGetValue(ns, out var ignored);
        var toRemove = new List<string>();
        foreach (var e in _order)
        {
            if (!e.Key.StartsWith(ns, StringComparison.Ordinal)) continue;
            if (clearOptions != ThemeOptions.None && (e.Options & clearOptions) != clearOptions) continue;
            bool skip = false;
            if (ignored != null)
            {
                foreach (var s in ignored)
                {
                    if (e.Key.StartsWith(s, StringComparison.Ordinal))
                    {
                        skip = true;
                        break;
                    }
                }
            }
            if (!skip) toRemove.Add(e.Key);
        }
        foreach (var k in toRemove) Remove(k);
    }

    public string? Get(string key) => _map.TryGetValue(key, out var n) ? n.Value.Value : null;

    public string? Get(IEnumerable<string> keys)
    {
        foreach (var k in keys)
        {
            if (_map.TryGetValue(k, out var n)) return n.Value.Value;
        }
        return null;
    }

    public ThemeEntry? GetEntry(string key) => _map.TryGetValue(key, out var n) ? n.Value : null;

    public bool HasDefault(string key) => (GetOptions(key) & ThemeOptions.Default) == ThemeOptions.Default;

    public ThemeOptions GetOptions(string key)
    {
        key = Js.Unescape(Unprefix(key));
        return _map.TryGetValue(key, out var n) ? n.Value.Options : ThemeOptions.None;
    }

    public IEnumerable<ThemeEntry> Entries() => _order;

    public string PrefixKey(string key) => Prefix != null ? "--" + Prefix + "-" + key.Substring(2) : key;

    string Unprefix(string key) => Prefix != null && key.Length > 3 + Prefix.Length ? "--" + key.Substring(3 + Prefix.Length) : key;

    string? ResolveKey(string? candidateValue, string[] themeKeys)
    {
        foreach (var ns in themeKeys)
        {
            string themeKey = candidateValue != null ? ns + "-" + candidateValue : ns;
            if (!_map.ContainsKey(themeKey))
            {
                if (candidateValue != null && candidateValue.IndexOf('.') >= 0)
                {
                    themeKey = ns + "-" + candidateValue.Replace('.', '_');
                    if (!_map.ContainsKey(themeKey)) continue;
                }
                else continue;
            }
            if (!IsIgnoredThemeKey(themeKey, ns)) return themeKey;
        }
        return null;
    }

    string VarReference(string themeKey)
    {
        var entry = _map[themeKey].Value;
        string? fallback = (entry.Options & ThemeOptions.Reference) != 0 ? entry.Value : null;
        string escaped = Js.Escape(PrefixKey(themeKey));
        return fallback != null && fallback.Length > 0 ? "var(" + escaped + ", " + fallback + ")" : "var(" + escaped + ")";
    }

    /// <summary>把变量标记为已使用；返回是否是首次标记。</summary>
    public bool MarkUsedVariable(string themeKey)
    {
        string key = Js.Unescape(Unprefix(themeKey));
        if (!_map.TryGetValue(key, out var n)) return false;
        bool wasUsed = (n.Value.Options & ThemeOptions.Used) != 0;
        n.Value.Options |= ThemeOptions.Used;
        return !wasUsed;
    }

    /// <summary>解析主题值：inline 主题返回值本身，否则返回 <c>var(--key)</c>。</summary>
    public string? Resolve(string? candidateValue, string[] themeKeys, ThemeOptions options = ThemeOptions.None)
    {
        string? themeKey = ResolveKey(candidateValue, themeKeys);
        if (themeKey == null) return null;
        var entry = _map[themeKey].Value;
        return ((options | entry.Options) & ThemeOptions.Inline) != 0 ? entry.Value : VarReference(themeKey);
    }

    /// <summary>返回原始值（不经 var() 包装）。</summary>
    public string? ResolveValue(string? candidateValue, string[] themeKeys)
    {
        string? themeKey = ResolveKey(candidateValue, themeKeys);
        return themeKey == null ? null : _map[themeKey].Value.Value;
    }

    /// <summary>解析主值以及若干嵌套键（例如 <c>--text-sm</c> 与 <c>--text-sm--line-height</c>）。</summary>
    public (string Value, Dictionary<string, string> Extra)? ResolveWith(string? candidateValue, string[] themeKeys, string[] nestedKeys)
    {
        string? themeKey = ResolveKey(candidateValue, themeKeys);
        if (themeKey == null) return null;
        var extra = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var name in nestedKeys)
        {
            string nestedKey = themeKey + name;
            if (_map.TryGetValue(nestedKey, out var n))
            {
                extra[name] = (n.Value.Options & ThemeOptions.Inline) != 0 ? n.Value.Value : VarReference(nestedKey);
            }
        }
        var entry = _map[themeKey].Value;
        return ((entry.Options & ThemeOptions.Inline) != 0 ? entry.Value : VarReference(themeKey), extra);
    }

    /// <summary>命名空间下的全部值；键 null 表示命名空间本身（<c>--breakpoint</c>）。</summary>
    public List<KeyValuePair<string?, string>> Namespace(string ns)
    {
        var result = new List<KeyValuePair<string?, string>>();
        string prefix = ns + "-";
        string prefix2 = prefix + "-";
        foreach (var e in _order)
        {
            if (e.Key == ns) result.Add(new KeyValuePair<string?, string>(null, e.Value));
            else if (e.Key.StartsWith(prefix2, StringComparison.Ordinal)) result.Add(new KeyValuePair<string?, string>(e.Key.Substring(ns.Length), e.Value));
            else if (e.Key.StartsWith(prefix, StringComparison.Ordinal)) result.Add(new KeyValuePair<string?, string>(e.Key.Substring(prefix.Length), e.Value));
        }
        return result;
    }

    public void AddKeyframes(AstNode keyframes) => _keyframes.Add(keyframes);

    public List<AstNode> GetKeyframes() => new(_keyframes);
}
