using System;
using System.Collections.Generic;
using AwAUI.Graphics.Text;
using AwAUI.Text.OpenType;
using AwAUI.Unicode;

namespace AwAUI.Text;

/// <summary>
/// 内存中的字体集合：从字节或文件注册字型，按 CSS Fonts 4 §5.2 的算法匹配，并在已注册的字型中做码点回退。
/// 不枚举系统字体（那属于平台层）。线程安全。
/// </summary>
public sealed class FontCollection : FontManager
{
    sealed class Entry
    {
        public Entry(FontFace face, FontDescriptor descriptor, string[] familyNames, int order)
        {
            Face = face;
            Descriptor = descriptor;
            FamilyNames = familyNames;
            Order = order;
        }

        public FontFace Face { get; }
        public FontDescriptor Descriptor { get; }
        /// <summary>可用于匹配的族名（排版族名、旧式族名）。</summary>
        public string[] FamilyNames { get; }
        public int Order { get; }
    }

    readonly object _lock = new();
    readonly List<Entry> _entries = new();
    readonly List<string> _familyNames = new();
    readonly HashSet<string> _familySet = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, List<Entry>> _byFamily = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, string[]> _generic = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<(int, int, FontStyle, FontStretch, string), FontFace?> _fallbackCache = new();
    const int MaxFallbackCache = 4096;

    public override IReadOnlyList<string> FamilyNames
    {
        get
        {
            lock (_lock) return _familyNames.ToArray();
        }
    }

    /// <summary>注册的字型总数。</summary>
    public int Count
    {
        get
        {
            lock (_lock) return _entries.Count;
        }
    }

    /// <summary>
    /// 把通用族名（sans-serif、serif、monospace、system-ui 等）或任意别名映射到具体的族名列表。
    /// 传入空列表取消映射。
    /// </summary>
    public void SetGenericFamily(string name, params string[] families)
    {
        if (name is null) throw new ArgumentNullException(nameof(name));
        if (families is null) throw new ArgumentNullException(nameof(families));
        lock (_lock)
        {
            if (families.Length == 0) _generic.Remove(name);
            else _generic[name] = (string[])families.Clone();
            _fallbackCache.Clear();
        }
    }

    public override FontRegistration Register(FontSource source, in FontDescriptor? overrideDescriptor = null)
    {
        if (source is null) throw new ArgumentNullException(nameof(source));
        var data = source.GetData();
        IReadOnlyList<OpenTypeFace> faces = source.FaceIndex < 0
            ? OpenTypeFace.LoadAll(data)
            : new[] { OpenTypeFace.Load(data, source.FaceIndex) };
        var array = new FontFace[faces.Count];
        for (int i = 0; i < array.Length; i++) array[i] = faces[i];
        return Add(source, array, overrideDescriptor);
    }

    /// <summary>从字节注册（.ttf / .otf / .ttc 中的全部字型）。</summary>
    public FontRegistration Register(ReadOnlyMemory<byte> data, in FontDescriptor? overrideDescriptor = null) =>
        Register(FontSource.FromMemory(data), overrideDescriptor);

    /// <summary>从文件注册。</summary>
    public FontRegistration RegisterFile(string path, in FontDescriptor? overrideDescriptor = null) =>
        Register(FontSource.FromFile(path), overrideDescriptor);

    /// <summary>注册一个已有的字型对象（例如自定义的 <see cref="FontFace"/> 实现）。</summary>
    public FontRegistration Register(FontFace face, in FontDescriptor? overrideDescriptor = null)
    {
        if (face is null) throw new ArgumentNullException(nameof(face));
        return Add(null, new[] { face }, overrideDescriptor);
    }

    FontRegistration Add(FontSource? source, FontFace[] faces, FontDescriptor? overrideDescriptor)
    {
        var descriptors = new FontDescriptor[faces.Length];
        lock (_lock)
        {
            for (int i = 0; i < faces.Length; i++)
            {
                var face = faces[i];
                var d = overrideDescriptor ?? face.Descriptor;
                descriptors[i] = d;
                var names = new List<string>(2);
                if (d.FamilyName.Length > 0) names.Add(d.FamilyName);
                // 没有覆盖描述符时，旧式族名（nameID 1）也能匹配，例如「Helvetica Light」
                if (overrideDescriptor is null && face is OpenTypeFace ot && ot.Names.Family is { Length: > 0 } legacy
                    && !names.Exists(n => string.Equals(n, legacy, StringComparison.OrdinalIgnoreCase)))
                    names.Add(legacy);
                var entry = new Entry(face, d, names.ToArray(), _entries.Count);
                _entries.Add(entry);
                foreach (var n in entry.FamilyNames)
                {
                    if (!_byFamily.TryGetValue(n, out var list)) _byFamily.Add(n, list = new List<Entry>());
                    list.Add(entry);
                }
                if (d.FamilyName.Length > 0 && _familySet.Add(d.FamilyName)) _familyNames.Add(d.FamilyName);
            }
            _fallbackCache.Clear();
        }
        return new FontRegistration(source, faces, descriptors);
    }

    public override FontFace? Match(in FontQuery query)
    {
        lock (_lock)
        {
            foreach (var family in ExpandFamilies(query.Families))
                if (_byFamily.TryGetValue(family, out var list) && list.Count > 0)
                    return SelectBest(list, query)?.Face;
            return null;
        }
    }

    /// <summary>
    /// 回退顺序：查询中的族名 → 其余已注册的族（按注册顺序）。每个族内只考虑含有该码点的字型，再按 CSS 算法选择风格最接近者。
    /// P1 不使用 <paramref name="locale"/>（按文字与语言选择默认字体在后续阶段实现）。
    /// </summary>
    public override FontFace? Fallback(int codePoint, in FontQuery baseQuery, string? locale)
    {
        lock (_lock)
        {
            var key = (codePoint, baseQuery.Weight.Value, baseQuery.Style, baseQuery.Stretch, string.Join("\u0001", baseQuery.Families));
            if (_fallbackCache.TryGetValue(key, out var cached)) return cached;

            FontFace? result = null;
            var visited = new HashSet<Entry>();
            var candidates = new List<Entry>();
            foreach (var family in ExpandFamilies(baseQuery.Families))
            {
                if (!_byFamily.TryGetValue(family, out var list)) continue;
                result = BestWithGlyph(list, codePoint, baseQuery, candidates, visited);
                if (result is not null) break;
            }
            if (result is null)
                foreach (var family in _familyNames)
                {
                    result = BestWithGlyph(_byFamily[family], codePoint, baseQuery, candidates, visited);
                    if (result is not null) break;
                }

            if (_fallbackCache.Count >= MaxFallbackCache) _fallbackCache.Clear();
            _fallbackCache[key] = result;
            return result;
        }
    }

    static FontFace? BestWithGlyph(List<Entry> family, int codePoint, in FontQuery query, List<Entry> scratch, HashSet<Entry> visited)
    {
        scratch.Clear();
        foreach (var e in family)
            if (visited.Add(e) && e.Face.TryGetGlyph(codePoint, out _))   // 已检查过的字型不再重复检查
                scratch.Add(e);
        return scratch.Count == 0 ? null : SelectBest(scratch, query)?.Face;
    }

    IEnumerable<string> ExpandFamilies(IReadOnlyList<string> families)
    {
        foreach (var f in families)
        {
            if (f is null) continue;
            var name = f.Trim();
            if (_generic.TryGetValue(name, out var mapped))
                foreach (var m in mapped) yield return m;
            else yield return name;
        }
    }

    // ---------------------------------------------------------------- CSS Fonts 4 §5.2 字体匹配

    static Entry? SelectBest(List<Entry> faces, in FontQuery query)
    {
        if (faces.Count == 0) return null;
        // 1. 字宽
        float desired = query.Stretch.ToPercentage();
        float? chosenStretch = null;
        foreach (var e in faces)
        {
            float s = e.Descriptor.Stretch.ToPercentage();
            if (chosenStretch is not { } c || StretchBetter(s, c, desired)) chosenStretch = s;
        }
        var set = faces.FindAll(e => e.Descriptor.Stretch.ToPercentage() == chosenStretch);

        // 2. 风格
        var styles = query.Style switch
        {
            FontStyle.Italic => new[] { FontStyle.Italic, FontStyle.Oblique, FontStyle.Normal },
            FontStyle.Oblique => new[] { FontStyle.Oblique, FontStyle.Italic, FontStyle.Normal },
            _ => new[] { FontStyle.Normal, FontStyle.Oblique, FontStyle.Italic },
        };
        foreach (var st in styles)
        {
            var subset = set.FindAll(e => e.Descriptor.Style == st);
            if (subset.Count > 0)
            {
                set = subset;
                break;
            }
        }

        // 3. 字重；完全相同的描述符取最后注册的（与 @font-face 一致）
        int w = query.Weight.Value;
        Entry? best = null;
        foreach (var e in set)
        {
            if (best is null) best = e;
            else
            {
                int a = e.Descriptor.Weight.Value, b = best.Descriptor.Weight.Value;
                if (a == b ? e.Order > best.Order : WeightBetter(a, b, w)) best = e;
            }
        }
        return best;
    }

    /// <summary>字宽 <paramref name="a"/> 是否比 <paramref name="b"/> 更接近期望值（按 CSS 的方向优先规则）。</summary>
    static bool StretchBetter(float a, float b, float desired)
    {
        if (a == b) return false;
        return Rank(a) < Rank(b);

        float Rank(float s)
        {
            // 期望 ≤ 100%：先更窄（由近到远），再更宽；期望 > 100%：先更宽，再更窄
            if (desired <= 100) return s <= desired ? desired - s : 1000 + s - desired;
            return s >= desired ? s - desired : 1000 + desired - s;
        }
    }

    /// <summary>字重 <paramref name="a"/> 是否比 <paramref name="b"/> 更接近期望值 <paramref name="w"/>。</summary>
    static bool WeightBetter(int a, int b, int w) => WeightRank(a, w) < WeightRank(b, w);

    static int WeightRank(int x, int w)
    {
        if (w >= 400 && w <= 500)
        {
            // 先 [w, 500] 升序，再 < w 降序，再 > 500 升序
            if (x >= w && x <= 500) return x - w;
            if (x < w) return 2000 + (w - x);
            return 4000 + (x - 500);
        }
        if (w < 400)
            // 先 ≤ w 降序，再 > w 升序
            return x <= w ? w - x : 2000 + (x - w);
        // w > 500：先 ≥ w 升序，再 < w 降序
        return x >= w ? x - w : 2000 + (w - x);
    }
}
