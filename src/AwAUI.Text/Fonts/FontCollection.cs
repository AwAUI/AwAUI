using System;
using System.Collections.Generic;
using System.IO;
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
    readonly Dictionary<(int, int, FontStyle, FontStretch, string, string), FontFace?> _fallbackCache = new();
    const int MaxFallbackCache = 4096;
    // 系统字体：族名 → 字型（首次解析后缓存）；文件路径 → 文件中的全部字型。
    // 全量索引只记录（文件、面）位置——族名从 name 表读取，字型在命中族名后才加载，
    // 否则 Windows 的 Fonts 目录（约 470 MB）会整个进入进程内存。
    readonly Dictionary<string, Entry[]?> _systemFamilies = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, FontFace[]> _systemFaces = new(StringComparer.Ordinal);
    readonly Dictionary<string, List<SystemFaceRef>> _systemIndex = new(StringComparer.OrdinalIgnoreCase);
    SystemFontProvider? _systemFonts;
    bool _systemIndexBuilt;

    /// <summary>系统字体索引中的一项：某个文件中的某个面。字型按需加载。</summary>
    readonly struct SystemFaceRef : IEquatable<SystemFaceRef>
    {
        public SystemFaceRef(string path, int faceIndex)
        {
            Path = path;
            FaceIndex = faceIndex;
        }

        public string Path { get; }
        public int FaceIndex { get; }

        public bool Equals(SystemFaceRef other) =>
            FaceIndex == other.FaceIndex && string.Equals(Path, other.Path, StringComparison.Ordinal);

        public override bool Equals(object? obj) => obj is SystemFaceRef other && Equals(other);

        public override int GetHashCode() => (StringComparer.Ordinal.GetHashCode(Path) * 397) ^ FaceIndex;
    }

    /// <summary>
    /// 系统字体提供者（design/19「字体回退」）：应用注册的字体找不到族名或缺少某个码点时，按语言环境回退到系统字体。
    /// 为 null（默认）时只使用已注册的字体。设置后清空回退缓存与系统字体索引。
    /// 平台后端没有系统字体（Web、Headless）时保持 null 或设为 <see cref="SystemFontProvider.Empty"/>。
    /// </summary>
    public SystemFontProvider? SystemFonts
    {
        get => _systemFonts;
        set
        {
            lock (_lock)
            {
                if (ReferenceEquals(_systemFonts, value)) return;
                _systemFonts = value;
                _systemFamilies.Clear();
                _systemFaces.Clear();
                _systemIndex.Clear();
                _systemIndexBuilt = false;
                _fallbackCache.Clear();
            }
        }
    }

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

    /// <summary>已加载的系统字体文件数（测试用：全量索引不应把整个系统字体目录读进内存）。</summary>
    internal int LoadedSystemFaceFiles
    {
        get
        {
            lock (_lock) return _systemFaces.Count;
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
            {
                if (_byFamily.TryGetValue(family, out var list) && list.Count > 0)
                    return SelectBest(list, query)?.Face;
                // 系统字体：族名（含未映射的通用族名解析出的默认族）按需加载
                var system = ResolveSystemFamily(family, query);
                if (system is not null) return system.Face;
            }
            // 指定的族名都不存在：退回系统默认族（与浏览器对未知 font-family 的处理一致）
            if (_systemFonts?.DefaultFamilyName is { } defaultFamily)
                return ResolveSystemFamily(defaultFamily, query)?.Face;
            return null;
        }
    }

    /// <summary>
    /// 回退顺序：查询中的族名 → 其余已注册的族（按注册顺序）→ 系统字体（按语言环境与文字选择的回退族名、平台默认族、全量扫描）。
    /// 每个族内只考虑含有该码点的字型，再按 CSS 算法选择风格最接近者。结果按（码点、语言环境、字重、风格、字宽、族名列表）缓存。
    /// 没有任何字型能显示该码点时触发 <see cref="FontManager.MissingGlyphs"/>。
    /// </summary>
    public override FontFace? Fallback(int codePoint, in FontQuery baseQuery, string? locale)
    {
        FontFace? result;
        lock (_lock)
        {
            var key = (codePoint, baseQuery.Weight.Value, baseQuery.Style, baseQuery.Stretch,
                string.Join("\u0001", baseQuery.Families), locale ?? string.Empty);
            if (_fallbackCache.TryGetValue(key, out var cached)) return cached;

            result = null;
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
            if (result is null) result = SystemFallback(codePoint, baseQuery, locale, visited, candidates);

            if (_fallbackCache.Count >= MaxFallbackCache) _fallbackCache.Clear();
            _fallbackCache[key] = result;
        }
        // 缺字：让应用有机会按需下载并注册字体（design/19「Web 平台没有系统字体」）；在锁外通知
        if (result is null) RaiseMissingGlyph(codePoint, locale);
        return result;
    }

    /// <summary>系统字体回退：语言环境 / 文字的回退族名 → 平台默认族 → 全量扫描。</summary>
    FontFace? SystemFallback(int codePoint, in FontQuery query, string? locale, HashSet<Entry> visited, List<Entry> candidates)
    {
        var provider = _systemFonts;
        if (provider is null || provider.FontFiles.Count == 0) return null;
        foreach (var family in provider.GetFallbackFamilies(codePoint, locale))
            if (ResolveSystemFamily(family, query) is { } entry && Covers(entry.Face, codePoint)) return entry.Face;
        if (provider.DefaultFamilyName is { } defaultFamily && ResolveSystemFamily(defaultFamily, query) is { } fallback
            && Covers(fallback.Face, codePoint))
            return fallback.Face;

        // 回退族名都不存在（例如精简的容器镜像）：逐个文件扫描 cmap。
        // 文件以内存映射打开，只触及 cmap 所在的页；没有命中的文件立即解除映射，
        // 不会为了报告缺字而把整个字体目录留在内存中。
        foreach (var path in provider.FontFiles)
        {
            bool cached = _systemFaces.TryGetValue(path, out var faces);
            IDisposable? owner = null;
            if (!cached) faces = ReadSystemFaces(path, out owner);
            var entries = new List<Entry>(faces!.Length);
            foreach (var face in faces) entries.Add(CreateEntry(face, _entries.Count + entries.Count));
            var hit = entries.Count > 0 ? BestWithGlyph(entries, codePoint, query, candidates, visited) : null;
            if (hit is null)
            {
                owner?.Dispose();
                continue;
            }
            if (!cached) _systemFaces[path] = faces;
            // 同一个族的其他风格可能在别的文件里（arial.ttf / arialbd.ttf）：按族名解析出风格最接近的字型
            return ResolveSystemFamily(hit.Descriptor.FamilyName, query) is { } best && Covers(best.Face, codePoint)
                ? best.Face
                : hit;
        }
        return null;

        static bool Covers(FontFace face, int cp) => face.TryGetGlyph(cp, out _);
    }

    /// <summary>
    /// 解析一个族名对应的系统字型：先按文件名猜测（少数文件），再查全量索引。
    /// 返回风格最接近 <paramref name="query"/> 的字型；没有该族时返回 null。
    /// </summary>
    Entry? ResolveSystemFamily(string family, in FontQuery query)
    {
        if (_systemFonts is null || string.IsNullOrWhiteSpace(family)) return null;
        var entries = _systemFamilies.TryGetValue(family, out var cached)
            ? cached
            : _systemFamilies[family] = LookupSystemFamily(family);
        return entries is { Length: > 0 } ? SelectBest(new List<Entry>(entries), query) : null;
    }

    Entry[]? LookupSystemFamily(string family)
    {
        // 通用族名没有映射时使用平台默认族
        if (!_generic.ContainsKey(family) && IsGenericFamily(family)) family = _systemFonts!.DefaultFamilyName ?? family;
        var normalized = NormalizeFamily(family);
        if (normalized.Length == 0) return null;

        // 1. 文件名提示：族名与文件名（去掉风格后缀）互相包含时只加载少数文件
        if (_systemIndex.Count == 0)
        {
            var hints = new List<Entry>();
            foreach (var path in _systemFonts!.FontFiles)
            {
                var file = NormalizeFamily(Path.GetFileNameWithoutExtension(path));
                if (!IsFileNameHint(file, normalized, family)) continue;
                foreach (var face in LoadSystemFaces(path))
                    if (MatchesFamily(face, family)) hints.Add(CreateEntry(face, _entries.Count + hints.Count));
            }
            if (hints.Count > 0) return hints.ToArray();
        }

        // 2. 全量索引（Windows 的系统字体文件名与族名无关，例如 msyh.ttc → Microsoft YaHei）。
        //    索引只记录位置，命中后再加载该族的字型。
        BuildSystemIndex();
        return _systemIndex.TryGetValue(family, out var refs) ? LoadSystemEntries(refs) : null;
    }

    /// <summary>加载索引中某个族的全部字型（文件按路径缓存，只会加载真正用到的族）。</summary>
    Entry[]? LoadSystemEntries(List<SystemFaceRef> refs)
    {
        var entries = new List<Entry>(refs.Count);
        foreach (var r in refs)
        {
            var faces = LoadSystemFaces(r.Path);
            if ((uint)r.FaceIndex >= (uint)faces.Length) continue;
            entries.Add(CreateEntry(faces[r.FaceIndex], _entries.Count + entries.Count));
        }
        return entries.Count > 0 ? entries.ToArray() : null;
    }

    static bool IsFileNameHint(string file, string normalized, string family)
    {
        if (file.Length < 4 || normalized.Length < 3) return false;
        if (file.Contains(normalized, StringComparison.Ordinal) || normalized.Contains(file, StringComparison.Ordinal)) return true;
        // 常见缩写：msyh.ttc → Microsoft YaHei，simsun.ttf → SimSun，malgun.ttf → Malgun Gothic
        return s_shortFileNames.TryGetValue(file, out var full)
            && NormalizeFamily(full).Equals(normalized, StringComparison.Ordinal);
    }

    static readonly Dictionary<string, string> s_shortFileNames = new(StringComparer.Ordinal)
    {
        ["msyh"] = "Microsoft YaHei", ["msyhbd"] = "Microsoft YaHei", ["msyhl"] = "Microsoft YaHei",
        ["msjh"] = "Microsoft JhengHei", ["msjhbd"] = "Microsoft JhengHei",
        ["simsun"] = "SimSun", ["simhei"] = "SimHei", ["simkai"] = "KaiTi", ["simfang"] = "FangSong",
        ["mingliu"] = "MingLiU", ["malgun"] = "Malgun Gothic", ["gulim"] = "Gulim", ["batang"] = "Batang",
        ["meiryo"] = "Meiryo", ["msgothic"] = "MS Gothic", ["yugoth"] = "Yu Gothic", ["segoeui"] = "Segoe UI",
        ["pingfang"] = "PingFang SC", ["hiraginosans"] = "Hiragino Sans",
        ["hiraginosansgb"] = "Hiragino Sans GB", ["applesdgothicneo"] = "Apple SD Gothic Neo",
        ["applegothic"] = "AppleGothic", ["arialunicode"] = "Arial Unicode MS",
    };

    void BuildSystemIndex()
    {
        if (_systemIndexBuilt || _systemFonts is null) return;
        _systemIndexBuilt = true;
        foreach (var path in _systemFonts.FontFiles)
        {
            var faces = OpenTypeNameIndex.ReadFamilyNames(path);
            for (int i = 0; i < faces.Count; i++)
                foreach (var name in faces[i])
                {
                    if (!_systemIndex.TryGetValue(name, out var list)) _systemIndex.Add(name, list = new List<SystemFaceRef>());
                    list.Add(new SystemFaceRef(path, i));
                }
        }
    }

    FontFace[] LoadSystemFaces(string path)
    {
        if (_systemFaces.TryGetValue(path, out var cached)) return cached;
        return _systemFaces[path] = ReadSystemFaces(path, out _);
    }

    /// <summary>
    /// 读取一个系统字体文件中的全部字型（内存映射，见 <see cref="FontFileData"/>）。
    /// <paramref name="owner"/> 非 null 时释放它即解除映射；字型被保留时不要释放。无法解析的文件返回空数组。
    /// </summary>
    static FontFace[] ReadSystemFaces(string path, out IDisposable? owner)
    {
        owner = null;
        try
        {
            var loaded = OpenTypeFace.LoadAll(FontFileData.Open(path, out owner));
            var faces = new FontFace[loaded.Count];
            for (int i = 0; i < faces.Length; i++) faces[i] = loaded[i];
            return faces;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidFontException
            or NotSupportedException or ArgumentOutOfRangeException)
        {
            owner?.Dispose();
            owner = null;
            return Array.Empty<FontFace>();
        }
    }

    static Entry CreateEntry(FontFace face, int order)
    {
        var d = face.Descriptor;
        var names = new List<string>(2);
        if (d.FamilyName.Length > 0) names.Add(d.FamilyName);
        if (face is OpenTypeFace ot && ot.Names.Family is { Length: > 0 } legacy
            && !names.Exists(n => string.Equals(n, legacy, StringComparison.OrdinalIgnoreCase)))
            names.Add(legacy);
        return new Entry(face, d, names.ToArray(), order);
    }

    static bool MatchesFamily(FontFace face, string family)
    {
        if (face.Descriptor.FamilyName.Equals(family, StringComparison.OrdinalIgnoreCase)) return true;
        return face is OpenTypeFace ot && ot.Names.Family is { Length: > 0 } legacy
            && legacy.Equals(family, StringComparison.OrdinalIgnoreCase);
    }

    static bool IsGenericFamily(string name) => name.Equals("sans-serif", StringComparison.OrdinalIgnoreCase)
        || name.Equals("system-ui", StringComparison.OrdinalIgnoreCase)
        || name.Equals("ui-sans-serif", StringComparison.OrdinalIgnoreCase)
        || name.Equals("serif", StringComparison.OrdinalIgnoreCase)
        || name.Equals("ui-serif", StringComparison.OrdinalIgnoreCase)
        || name.Equals("monospace", StringComparison.OrdinalIgnoreCase)
        || name.Equals("ui-monospace", StringComparison.OrdinalIgnoreCase)
        || name.Equals("cursive", StringComparison.OrdinalIgnoreCase)
        || name.Equals("fantasy", StringComparison.OrdinalIgnoreCase);

    /// <summary>族名 / 文件名归一化：只保留字母与数字并转小写，忽略空格、连字符与风格后缀。</summary>
    static string NormalizeFamily(string name)
    {
        var buffer = new char[name.Length];
        int n = 0;
        foreach (var c in name)
            if (char.IsLetterOrDigit(c)) buffer[n++] = char.ToLowerInvariant(c);
        return new string(buffer, 0, n);
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
