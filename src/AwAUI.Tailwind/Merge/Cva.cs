using System;
using System.Collections.Generic;
using System.Text;

namespace AwAUI.Tailwind;

/// <summary>复合变体：所有条件同时满足时追加 <see cref="Class"/>（class-variance-authority 的 <c>compoundVariants</c>）。</summary>
public sealed class CompoundVariant
{
    /// <summary>创建复合变体。条件的值可以有多个（任一匹配即可），与 cva 的数组条件相同。</summary>
    public CompoundVariant(IReadOnlyDictionary<string, string[]> conditions, string @class)
    {
        Conditions = conditions ?? throw new ArgumentNullException(nameof(conditions));
        Class = @class;
    }

    /// <summary>变体名 → 允许的取值。</summary>
    public IReadOnlyDictionary<string, string[]> Conditions { get; }

    /// <summary>追加的类名。</summary>
    public string Class { get; }
}

/// <summary>
/// class-variance-authority 的移植（字符串键版本）：<c>base</c> + 每个变体选中的类 + 满足条件的复合变体类 + 调用方的类，
/// 最后经 <see cref="TailwindMerge.Cn(string?)"/> 消解冲突（相当于 shadcn 的 <c>cn(buttonVariants({ variant, size, className }))</c>）。
/// </summary>
/// <remarks>不可变、线程安全；结果按「选中的变体组合」缓存，相同组合不重复拼接与合并。</remarks>
public sealed class Cva
{
    readonly string? _base;
    readonly KeyValuePair<string, IReadOnlyDictionary<string, string>>[] _variants;
    readonly IReadOnlyList<CompoundVariant> _compounds;
    readonly IReadOnlyDictionary<string, string> _defaults;
    readonly Dictionary<string, string> _cache = new(StringComparer.Ordinal);
    readonly object _gate = new();

    /// <summary>创建变体表。</summary>
    /// <param name="base">总是包含的类。</param>
    /// <param name="variants">变体名 → (取值 → 类)。</param>
    /// <param name="compoundVariants">复合变体。</param>
    /// <param name="defaultVariants">未指定时使用的取值。</param>
    public Cva(string? @base, IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>>? variants = null, IReadOnlyList<CompoundVariant>? compoundVariants = null, IReadOnlyDictionary<string, string>? defaultVariants = null)
    {
        _base = @base;
        var list = new List<KeyValuePair<string, IReadOnlyDictionary<string, string>>>();
        if (variants != null)
        {
            foreach (var kv in variants) list.Add(kv);
        }
        _variants = list.ToArray();
        _compounds = compoundVariants ?? Array.Empty<CompoundVariant>();
        _defaults = defaultVariants ?? new Dictionary<string, string>();
    }

    /// <summary>
    /// 计算类名。<paramref name="props"/> 中值为 null 的变体表示「显式不选」（cva 中的 <c>null</c>）；缺省的变体用默认值。
    /// 布尔变体用 <c>"true"</c> / <c>"false"</c> 作为取值。
    /// </summary>
    public string Invoke(IReadOnlyDictionary<string, string?>? props = null, string? className = null)
    {
        string key = CacheKey(props);
        string? cached;
        lock (_gate)
        {
            _cache.TryGetValue(key, out cached);
        }
        if (cached == null)
        {
            cached = TailwindMerge.Cn(Compose(props));
            lock (_gate)
            {
                if (_cache.Count < 4096) _cache[key] = cached;
            }
        }
        return string.IsNullOrEmpty(className) ? cached : TailwindMerge.Cn(cached, className);
    }

    string CacheKey(IReadOnlyDictionary<string, string?>? props)
    {
        if (props == null || props.Count == 0) return "";
        var sb = new StringBuilder();
        foreach (var kv in _variants)
        {
            sb.Append(props.TryGetValue(kv.Key, out var v) ? (v == null ? "\u0001" : v) : "\u0002").Append('\0');
        }
        // 复合变体可以引用不在 variants 里的键
        foreach (var c in _compounds)
        {
            foreach (var cond in c.Conditions)
            {
                if (props.TryGetValue(cond.Key, out var v)) sb.Append(cond.Key).Append('=').Append(v ?? "\u0001").Append('\0');
            }
        }
        return sb.ToString();
    }

    string Compose(IReadOnlyDictionary<string, string?>? props)
    {
        var sb = new StringBuilder();
        void Add(string? s)
        {
            if (string.IsNullOrEmpty(s)) return;
            if (sb.Length > 0) sb.Append(' ');
            sb.Append(s);
        }
        Add(_base);
        foreach (var kv in _variants)
        {
            string? selected;
            if (props != null && props.TryGetValue(kv.Key, out var given))
            {
                if (given == null) continue;
                // cva：空串视为未指定，回退到默认值
                if (given.Length == 0) _defaults.TryGetValue(kv.Key, out selected);
                else selected = given;
            }
            else _defaults.TryGetValue(kv.Key, out selected);
            if (selected != null && kv.Value.TryGetValue(selected, out var cls)) Add(cls);
        }
        foreach (var c in _compounds)
        {
            bool match = true;
            foreach (var cond in c.Conditions)
            {
                // 与 cva 相同：显式给出的 null 不回退到默认值（只有缺省的键才用默认值）
                string? actual = null;
                if (props == null || !props.TryGetValue(cond.Key, out actual))
                {
                    if (!_defaults.TryGetValue(cond.Key, out actual)) actual = null;
                }
                if (actual == null || Array.IndexOf(cond.Value, actual) < 0)
                {
                    match = false;
                    break;
                }
            }
            if (match) Add(c.Class);
        }
        return sb.ToString();
    }
}

/// <summary>强类型的单维变体表（枚举键），组合结果预先缓存，<see cref="Of"/> 命中缓存时不分配。</summary>
public sealed class Cva<T1> where T1 : struct, Enum
{
    readonly Func<T1, string> _compute;
    readonly Dictionary<T1, string> _cache = new();
    readonly object _gate = new();

    /// <summary>创建变体表；<paramref name="compound"/> 在条件成立时追加类。</summary>
    public Cva(string? @base, IReadOnlyDictionary<T1, string> first, IReadOnlyList<(Func<T1, bool> When, string Class)>? compound = null)
    {
        if (first == null) throw new ArgumentNullException(nameof(first));
        _compute = a =>
        {
            var parts = new List<string?> { @base, first.TryGetValue(a, out var s) ? s : null };
            if (compound != null)
            {
                foreach (var (when, cls) in compound)
                {
                    if (when(a)) parts.Add(cls);
                }
            }
            return TailwindMerge.Cn(Join(parts));
        };
    }

    /// <summary>选中的类（已合并）；<paramref name="className"/> 追加在最后并参与冲突消解。</summary>
    public string Of(T1 first, string? className = null)
    {
        string result;
        lock (_gate)
        {
            if (!_cache.TryGetValue(first, out result!))
            {
                result = _compute(first);
                _cache[first] = result;
            }
        }
        return string.IsNullOrEmpty(className) ? result : TailwindMerge.Cn(result, className);
    }

    internal static string Join(List<string?> parts)
    {
        var sb = new StringBuilder();
        foreach (var p in parts)
        {
            if (string.IsNullOrEmpty(p)) continue;
            if (sb.Length > 0) sb.Append(' ');
            sb.Append(p);
        }
        return sb.ToString();
    }
}

/// <summary>强类型的二维变体表（如 shadcn Button 的 <c>Variant</c> × <c>Size</c>）。</summary>
public sealed class Cva<T1, T2> where T1 : struct, Enum where T2 : struct, Enum
{
    readonly Func<T1, T2, string> _compute;
    readonly Dictionary<(T1, T2), string> _cache = new();
    readonly object _gate = new();

    /// <summary>创建变体表。</summary>
    /// <param name="base">总是包含的类。</param>
    /// <param name="first">第一维（如 variant）每个取值的类。</param>
    /// <param name="second">第二维（如 size）每个取值的类。</param>
    /// <param name="compound">复合变体：条件成立时追加类。</param>
    public Cva(string? @base, IReadOnlyDictionary<T1, string> first, IReadOnlyDictionary<T2, string> second, IReadOnlyList<(Func<T1, T2, bool> When, string Class)>? compound = null)
    {
        if (first == null) throw new ArgumentNullException(nameof(first));
        if (second == null) throw new ArgumentNullException(nameof(second));
        _compute = (a, b) =>
        {
            var parts = new List<string?> { @base, first.TryGetValue(a, out var s1) ? s1 : null, second.TryGetValue(b, out var s2) ? s2 : null };
            if (compound != null)
            {
                foreach (var (when, cls) in compound)
                {
                    if (when(a, b)) parts.Add(cls);
                }
            }
            return TailwindMerge.Cn(Cva<T1>.Join(parts));
        };
    }

    /// <summary>选中的类（已合并）；<paramref name="className"/> 追加在最后并参与冲突消解。</summary>
    public string Of(T1 first, T2 second, string? className = null)
    {
        string result;
        lock (_gate)
        {
            if (!_cache.TryGetValue((first, second), out result!))
            {
                result = _compute(first, second);
                _cache[(first, second)] = result;
            }
        }
        return string.IsNullOrEmpty(className) ? result : TailwindMerge.Cn(result, className);
    }
}

/// <summary>强类型的三维变体表。</summary>
public sealed class Cva<T1, T2, T3> where T1 : struct, Enum where T2 : struct, Enum where T3 : struct, Enum
{
    readonly Func<T1, T2, T3, string> _compute;
    readonly Dictionary<(T1, T2, T3), string> _cache = new();
    readonly object _gate = new();

    /// <summary>创建变体表。</summary>
    public Cva(string? @base, IReadOnlyDictionary<T1, string> first, IReadOnlyDictionary<T2, string> second, IReadOnlyDictionary<T3, string> third, IReadOnlyList<(Func<T1, T2, T3, bool> When, string Class)>? compound = null)
    {
        if (first == null) throw new ArgumentNullException(nameof(first));
        if (second == null) throw new ArgumentNullException(nameof(second));
        if (third == null) throw new ArgumentNullException(nameof(third));
        _compute = (a, b, c) =>
        {
            var parts = new List<string?>
            {
                @base, first.TryGetValue(a, out var s1) ? s1 : null, second.TryGetValue(b, out var s2) ? s2 : null, third.TryGetValue(c, out var s3) ? s3 : null,
            };
            if (compound != null)
            {
                foreach (var (when, cls) in compound)
                {
                    if (when(a, b, c)) parts.Add(cls);
                }
            }
            return TailwindMerge.Cn(Cva<T1>.Join(parts));
        };
    }

    /// <summary>选中的类（已合并）；<paramref name="className"/> 追加在最后并参与冲突消解。</summary>
    public string Of(T1 first, T2 second, T3 third, string? className = null)
    {
        string result;
        lock (_gate)
        {
            if (!_cache.TryGetValue((first, second, third), out result!))
            {
                result = _compute(first, second, third);
                _cache[(first, second, third)] = result;
            }
        }
        return string.IsNullOrEmpty(className) ? result : TailwindMerge.Cn(result, className);
    }
}
