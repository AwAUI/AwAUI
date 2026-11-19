using System;
using System.Collections.Generic;
using AwAUI.Css.Syntax;

namespace AwAUI.Css.Values;

/// <summary>可动画性。</summary>
public enum CssAnimatable : byte
{
    /// <summary>不可动画。</summary>
    No,
    /// <summary>离散：在进度 50% 处切换。</summary>
    Discrete,
    /// <summary>按计算值插值。</summary>
    ByComputedValue,
}

/// <summary>
/// 计算样式的差异：变化影响帧管线的哪些阶段（design/16「影响阶段」，design/05 的脏标记表）。
/// </summary>
[Flags]
public enum StyleDifference
{
    None = 0,
    /// <summary>—：不产生视觉变化，只更新行为状态（如 <c>cursor</c>）。</summary>
    Behavior = 1 << 0,
    /// <summary>C：只改变合成层属性（如 <c>opacity</c>）。</summary>
    Composite = 1 << 1,
    /// <summary>P：只改变外观，重录该元素的 DisplayList。</summary>
    Paint = 1 << 2,
    /// <summary>L：改变盒子尺寸或位置，重排。</summary>
    Layout = 1 << 3,
    /// <summary>S：改变盒树结构或层叠上下文，重建布局对象。</summary>
    Structure = 1 << 4,
    /// <summary>自定义属性发生变化（本身不影响绘制，但依赖它的后代需要重算样式）。</summary>
    CustomProperties = 1 << 5,
}

/// <summary>属性元数据（由 Properties.json 生成），供级联、DevTools 与编译期校验使用。</summary>
public sealed class CssProperty
{
    internal CssProperty(CssPropertyId id, string name, bool inherited, CssAnimatable animatable, StyleDifference affects,
        CssPropertyGroup? group, int phase, string? initial, CssPropertyId[] longhands)
    {
        Id = id;
        Name = name;
        Inherited = inherited;
        Animatable = animatable;
        Affects = affects;
        Group = group;
        Phase = phase;
        InitialText = initial;
        LonghandIds = longhands;
    }

    public CssPropertyId Id { get; }

    /// <summary>属性名（ASCII 小写）。</summary>
    public string Name { get; }

    public bool IsShorthand => (int)Id >= CssProperties.LonghandCount;

    /// <summary>是否继承（简写为 false）。</summary>
    public bool Inherited { get; }

    public CssAnimatable Animatable { get; }

    /// <summary>值变化时影响的阶段；简写为 None。</summary>
    public StyleDifference Affects { get; }

    /// <summary>所在的 <see cref="ComputedStyle"/> 分组；简写为 null。</summary>
    public CssPropertyGroup? Group { get; }

    /// <summary>实现分期（design/16）。</summary>
    public int Phase { get; }

    /// <summary>初始值的 CSS 文本；简写为 null。</summary>
    public string? InitialText { get; }

    /// <summary>简写展开后的长属性；长属性为空。</summary>
    public IReadOnlyList<CssPropertyId> Longhands => LonghandIds;

    internal CssPropertyId[] LonghandIds { get; }

    public override string ToString() => Name;
}

/// <summary>属性表：按 ID 或名称查找属性元数据，以及未支持属性的提示。</summary>
public static partial class CssProperties
{
    static readonly Dictionary<string, CssProperty> s_byName;
    static Dictionary<string, int>? s_plannedByName;

    // 字段初始化器分布在多个分部文件中，跨文件的初始化顺序不确定；依赖 s_all 的索引放在静态构造函数里
    static CssProperties() => s_byName = BuildNameIndex();

    static Dictionary<string, CssProperty> BuildNameIndex()
    {
        var map = new Dictionary<string, CssProperty>(s_all.Length, StringComparer.Ordinal);
        foreach (var p in s_all) map.Add(p.Name, p);
        return map;
    }

    /// <summary>全部属性（长属性在前），下标即 <see cref="CssPropertyId"/>。</summary>
    public static IReadOnlyList<CssProperty> All => s_all;

    public static CssProperty Get(CssPropertyId id) => s_all[(int)id];

    /// <summary>按名称（ASCII 大小写不敏感）查找已支持的属性。</summary>
    public static bool TryGet(string name, out CssProperty property)
    {
        if (name is null) throw new ArgumentNullException(nameof(name));
        if (s_byName.TryGetValue(name, out property!)) return true;
        return s_byName.TryGetValue(CssSerializer.ToAsciiLower(name), out property!);
    }

    /// <summary>属性是否在清单中计划于后续某期支持；返回期号。</summary>
    public static bool TryGetPlannedPhase(string name, out int phase)
    {
        var map = s_plannedByName;
        if (map is null)
        {
            map = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var (n, p) in s_planned) map[n] = p;
            s_plannedByName = map;
        }
        return map.TryGetValue(CssSerializer.ToAsciiLower(name), out phase);
    }

    /// <summary>明确不支持的属性及其替代写法。</summary>
    public static bool TryGetUnsupportedHint(string name, out string hint)
    {
        var lower = CssSerializer.ToAsciiLower(name);
        foreach (var (n, h) in s_unsupported)
        {
            if (n == lower)
            {
                hint = h;
                return true;
            }
        }
        hint = string.Empty;
        return false;
    }

    /// <summary>与 <paramref name="name"/> 编辑距离最近的已支持属性名（距离不超过 2 或长度的三分之一）；没有则返回 null。</summary>
    public static string? SuggestName(string name)
    {
        if (name is null) throw new ArgumentNullException(nameof(name));
        // 名称过长时不做建议：编辑距离是 O(n·m)，攻击者可控的超长名称不应放大开销
        if (name.Length == 0 || name.Length > 64) return null;
        var lower = CssSerializer.ToAsciiLower(name);
        string? best = null;
        int bestDistance = int.MaxValue;
        int limit = Math.Max(2, lower.Length / 3);
        Span<int> row = stackalloc int[66];
        foreach (var p in s_all)
        {
            if (Math.Abs(p.Name.Length - lower.Length) > limit || p.Name.Length > 64) continue;
            int d = EditDistance(lower, p.Name, row, limit);
            if (d < bestDistance)
            {
                bestDistance = d;
                best = p.Name;
            }
        }
        return bestDistance <= limit ? best : null;
    }

    /// <summary>Levenshtein 距离（两行滚动），超过 <paramref name="limit"/> 时提前返回。</summary>
    internal static int EditDistance(string a, string b, Span<int> scratch, int limit)
    {
        var prev = scratch.Slice(0, b.Length + 1);
        Span<int> cur = stackalloc int[b.Length + 1];
        for (int j = 0; j <= b.Length; j++) prev[j] = j;
        for (int i = 1; i <= a.Length; i++)
        {
            cur[0] = i;
            int rowMin = cur[0];
            for (int j = 1; j <= b.Length; j++)
            {
                int cost = a[i - 1] == b[j - 1] ? 0 : 1;
                int v = Math.Min(Math.Min(prev[j] + 1, cur[j - 1] + 1), prev[j - 1] + cost);
                cur[j] = v;
                if (v < rowMin) rowMin = v;
            }
            if (rowMin > limit) return int.MaxValue;
            cur.CopyTo(prev);
        }
        return prev[b.Length];
    }
}

/// <summary>关键字表：名称（ASCII 小写）↔ 枚举值。</summary>
internal sealed class KeywordTable
{
    readonly Dictionary<string, int> _lookup;

    public KeywordTable(string[] names)
    {
        Names = names;
        _lookup = new Dictionary<string, int>(names.Length, StringComparer.Ordinal);
        for (int i = 0; i < names.Length; i++) _lookup.Add(names[i], i);
    }

    /// <summary>带旧别名（legacy value alias）的关键字表：别名（ASCII 小写）在解析时换成对应的关键字，计算值与序列化都是目标关键字。</summary>
    public KeywordTable(string[] names, (string Alias, int Value)[] aliases) : this(names)
    {
        foreach (var (alias, value) in aliases) _lookup.Add(alias, value);
    }

    public string[] Names { get; }

    public string NameOf(int value) => (uint)value < (uint)Names.Length ? Names[value] : string.Empty;

    /// <summary>按 ASCII 大小写不敏感查找。</summary>
    public bool TryGet(string ident, out int value)
    {
        if (_lookup.TryGetValue(ident, out value)) return true;
        return _lookup.TryGetValue(CssSerializer.ToAsciiLower(ident), out value);
    }
}
