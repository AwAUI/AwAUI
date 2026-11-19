using System;
using System.Collections.Generic;
using AwAUI.Graphics.Text;
using AwAUI.Unicode;

namespace AwAUI.Text;

/// <summary>
/// 文本引擎：持有字体管理器、整形器与全局的词级整形缓存，是段落布局（<see cref="ParagraphBuilder"/> / <see cref="TextLayout"/>）的入口。
/// 可整体替换（design/19「测试钩子」）：例如 <see cref="Testing.TestTextEngine"/> 使用固定宽度的方块字体，让布局与控件测试不依赖真实字体。
/// 线程安全：缓存内部加锁，整形可在任意线程执行。
/// </summary>
public abstract class TextEngine
{
    static readonly string[] s_defaultFamilies = { "sans-serif" };

    readonly ShapeCache _cache;

    /// <param name="shapeCacheEntries">词级整形缓存的最大条目数（0 关闭缓存）。</param>
    protected TextEngine(int shapeCacheEntries = ShapeCache.DefaultMaxEntries)
    {
        _cache = new ShapeCache(Math.Max(0, shapeCacheEntries), ShapeCache.DefaultMaxChars);
    }

    /// <summary>字体匹配与回退。</summary>
    public abstract FontManager Fonts { get; }

    /// <summary>整形器。</summary>
    public abstract TextShaper Shaper { get; }

    /// <summary>文本样式没有指定字体族时使用的族名列表（默认 "sans-serif"，由 <see cref="FontCollection.SetGenericFamily"/> 解析）。</summary>
    public virtual IReadOnlyList<string> DefaultFontFamilies => s_defaultFamilies;

    /// <summary>创建段落构建器。</summary>
    public ParagraphBuilder CreateParagraphBuilder(ParagraphStyle style) => new(style, this);

    /// <summary>便捷方法：用单一样式排版一段文本并按 <paramref name="maxWidth"/> 断行。</summary>
    public TextLayout CreateLayout(string text, ParagraphStyle style, float maxWidth = float.PositiveInfinity)
    {
        if (text is null) throw new ArgumentNullException(nameof(text));
        var b = new ParagraphBuilder(style, this);
        b.AddText(text);
        var layout = b.Build();
        layout.Layout(maxWidth);
        return layout;
    }

    /// <summary>词级整形缓存的统计。</summary>
    public ShapeCacheStatistics ShapeCacheStatistics => _cache.GetStatistics();

    /// <summary>清空整形缓存（字体集合变化后调用）。</summary>
    public void ClearCaches() => _cache.Clear();

    /// <summary>
    /// 整形一个词（两个断行机会之间、同一 run 内的文本），结果按「文本 + 字型 + 字号 + 方向 + 文字 + 语言 + 轴取值 + 特性设置 + 连写上下文」缓存。
    /// 返回的 <see cref="ShapedRun.Clusters"/> 相对于 <paramref name="word"/> 的起点。过长的词不缓存。
    /// <paramref name="before"/> / <paramref name="after"/> 是词前后的文本（阿拉伯式连写的文字才需要，最多各 5 个码点）：
    /// 词被样式变化切开时（例如给一个字母换颜色），连写形式仍取决于相邻的字符。上下文只在可能影响连写时传给整形器并参与缓存键。
    /// </summary>
    internal ShapedRun ShapeWord(ReadOnlyMemory<char> word, FontFace face, float size, bool rightToLeft, Script script,
        string? language, FontVariation variation, FontFeature[]? features,
        ReadOnlySpan<char> before = default, ReadOnlySpan<char> after = default)
    {
        int context = before.IsEmpty && after.IsEmpty ? 0 : Shaping.ArabicShaping.ContextKey(before, after);
        bool cacheable = word.Length <= ShapeCache.MaxWordLength;
        var key = new ShapeKey(word, face, size, rightToLeft, script, language, variation, features, context);
        if (cacheable && _cache.TryGet(key, out var cached)) return cached;
        ShapedRun run;
        if (context == 0)
        {
            run = Shaper.Shape(new ShapeRequest(word.Span, face, size)
            {
                RightToLeft = rightToLeft,
                Script = script,
                Language = language,
                Variation = variation,
                Features = features,
            });
        }
        else
        {
            // 上下文 + 词 + 上下文拼成一段，整形范围只含词本身；簇值移回相对于词的起点
            var buffer = new char[before.Length + word.Length + after.Length];
            before.CopyTo(buffer);
            word.Span.CopyTo(buffer.AsSpan(before.Length));
            after.CopyTo(buffer.AsSpan(before.Length + word.Length));
            var shaped = Shaper.Shape(new ShapeRequest(buffer, new TextRange(before.Length, before.Length + word.Length), face, size)
            {
                RightToLeft = rightToLeft,
                Script = script,
                Language = language,
                Variation = variation,
                Features = features,
            });
            var clusters = shaped.Clusters.ToArray();
            for (int i = 0; i < clusters.Length; i++) clusters[i] -= before.Length;
            run = new ShapedRun(shaped.Face, shaped.Size, new TextRange(0, word.Length), shaped.RightToLeft, shaped.Glyphs.ToArray(), clusters,
                shaped.Advances.ToArray(), shaped.Offsets.ToArray(), shaped.Flags.ToArray(), owned: true);
        }
        if (cacheable) _cache.Add(key.Detach(), run);
        return run;
    }

    /// <summary>两个分别整形的词在边界处相邻时的额外间距（逻辑像素），见 <see cref="TextShaper.GetBoundaryKerning"/>。</summary>
    internal float GetBoundaryKerning(FontFace face, float size, bool rightToLeft, Script script, string? language,
        FontVariation variation, FontFeature[]? features, ushort first, ushort second)
    {
        float k = Shaper.GetBoundaryKerning(new ShapeRequest(ReadOnlySpan<char>.Empty, face, size)
        {
            RightToLeft = rightToLeft,
            Script = script,
            Language = language,
            Variation = variation,
            Features = features,
        }, first, second);
        return float.IsNaN(k) || float.IsInfinity(k) ? 0 : k;
    }
}

/// <summary>默认文本引擎：给定的字体管理器 + 整形器（默认 <see cref="OpenTypeShaper"/>）。</summary>
public sealed class DefaultTextEngine : TextEngine
{
    readonly IReadOnlyList<string>? _defaultFamilies;

    public DefaultTextEngine(FontManager fonts, TextShaper? shaper = null, IReadOnlyList<string>? defaultFontFamilies = null,
        int shapeCacheEntries = ShapeCache.DefaultMaxEntries)
        : base(shapeCacheEntries)
    {
        Fonts = fonts ?? throw new ArgumentNullException(nameof(fonts));
        Shaper = shaper ?? OpenTypeShaper.Instance;
        _defaultFamilies = defaultFontFamilies;
    }

    public override FontManager Fonts { get; }
    public override TextShaper Shaper { get; }
    public override IReadOnlyList<string> DefaultFontFamilies => _defaultFamilies ?? base.DefaultFontFamilies;
}

/// <summary>整形缓存统计。</summary>
public readonly struct ShapeCacheStatistics
{
    internal ShapeCacheStatistics(long hits, long misses, int count, long chars)
    {
        Hits = hits;
        Misses = misses;
        Count = count;
        Characters = chars;
    }

    public long Hits { get; }
    public long Misses { get; }
    public int Count { get; }
    /// <summary>缓存中词文本的 UTF-16 码元总数。</summary>
    public long Characters { get; }

    public override string ToString() => $"{Count} entries, {Characters} chars, {Hits} hits, {Misses} misses";
}

/// <summary>整形缓存键。查找时引用调用方的字符（不分配），加入缓存前复制（<see cref="Detach"/>）。</summary>
readonly struct ShapeKey : IEquatable<ShapeKey>
{
    public ShapeKey(ReadOnlyMemory<char> text, FontFace face, float size, bool rtl, Script script, string? language, FontVariation variation,
        FontFeature[]? features = null, int context = 0)
    {
        Features = features is { Length: > 0 } ? features : null;
        Context = context;
        Text = text;
        Face = face;
        Size = size;
        Rtl = rtl;
        Script = script;
        Language = language;
        Variation = variation;
        Hash = HashCodeCompat.Combine(
            HashCodeCompat.Combine(HashText(text.Span), System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(face), size.GetHashCode()),
            HashCodeCompat.Combine((rtl ? 1 : 0) | ((int)script << 1), language is null ? 0 : StringComparer.Ordinal.GetHashCode(language), variation.GetHashCode()));
        if (Features is { } fs)
            foreach (var f in fs) Hash = HashCodeCompat.Combine(Hash, (int)f.Tag, (int)f.Value);
        if (context != 0) Hash = HashCodeCompat.Combine(Hash, context);
    }

    /// <summary>连写上下文（<see cref="Shaping.ArabicShaping.ContextKey"/>；0 表示上下文不影响结果）。</summary>
    public int Context { get; }

    /// <summary>特性设置（按内容比较；样式中的特性没有范围，Start / End 也参与比较）。</summary>
    public FontFeature[]? Features { get; }

    static bool FeaturesEqual(FontFeature[]? a, FontFeature[]? b)
    {
        if (ReferenceEquals(a, b)) return true;
        if (a is null || b is null || a.Length != b.Length) return false;
        for (int i = 0; i < a.Length; i++)
            if (a[i].Tag != b[i].Tag || a[i].Value != b[i].Value || a[i].Start != b[i].Start || a[i].End != b[i].End) return false;
        return true;
    }

#if !NET
    static readonly uint s_seed = (uint)Guid.NewGuid().GetHashCode() | 1;
#endif

    /// <summary>
    /// 词文本的哈希：按进程随机化（.NET 上为 Marvin，netstandard2.0 上为带随机种子的 FNV-1a）。
    /// 文本可能来自不可信输入；确定性的哈希可被构造出大量碰撞的词，让缓存的每次查找退化为线性比较（哈希洪泛）。
    /// </summary>
    static int HashText(ReadOnlySpan<char> text)
    {
#if NET
        return string.GetHashCode(text);
#else
        uint h = 2166136261 ^ s_seed;
        foreach (var c in text) h = (h ^ c) * 16777619;
        h ^= h >> 15;
        h *= 0x2C1B3C6D;
        h ^= h >> 12;
        return (int)h;
#endif
    }

    ShapeKey(in ShapeKey k, ReadOnlyMemory<char> text)
    {
        this = k;
        Text = text;
    }

    public ReadOnlyMemory<char> Text { get; }
    public FontFace Face { get; }
    public float Size { get; }
    public bool Rtl { get; }
    public Script Script { get; }
    public string? Language { get; }
    public FontVariation Variation { get; }
    public int Hash { get; }

    public ShapeKey Detach() => new(this, Text.ToArray());

    public bool Equals(ShapeKey o) =>
        Hash == o.Hash && ReferenceEquals(Face, o.Face) && Size.Equals(o.Size) && Rtl == o.Rtl && Script == o.Script
        && string.Equals(Language, o.Language, StringComparison.Ordinal) && Variation == o.Variation
        && Context == o.Context && FeaturesEqual(Features, o.Features) && Text.Span.SequenceEqual(o.Text.Span);

    public override bool Equals(object? obj) => obj is ShapeKey k && Equals(k);
    public override int GetHashCode() => Hash;
}

/// <summary>全局的词级整形缓存（LRU，按条目数与字符总数限界）。线程安全。</summary>
sealed class ShapeCache
{
    public const int DefaultMaxEntries = 16384;
    public const long DefaultMaxChars = 1 << 20;
    /// <summary>超过此长度的词不缓存（长的不可断开片段很少重复出现，且会让键比较变慢）。</summary>
    public const int MaxWordLength = 96;

    sealed class Node
    {
        public ShapeKey Key;
        public ShapedRun Value = null!;
        public Node? Prev, Next;
    }

    readonly object _lock = new();
    readonly Dictionary<ShapeKey, Node> _map = new();
    readonly int _maxEntries;
    readonly long _maxChars;
    Node? _head, _tail;   // head = 最近使用
    long _chars, _hits, _misses;

    public ShapeCache(int maxEntries, long maxChars)
    {
        _maxEntries = maxEntries;
        _maxChars = maxChars;
    }

    public bool TryGet(in ShapeKey key, out ShapedRun run)
    {
        lock (_lock)
        {
            if (_map.TryGetValue(key, out var node))
            {
                _hits++;
                MoveToFront(node);
                run = node.Value;
                return true;
            }
            _misses++;
            run = null!;
            return false;
        }
    }

    public void Add(in ShapeKey key, ShapedRun run)
    {
        if (_maxEntries == 0) return;
        lock (_lock)
        {
            if (_map.TryGetValue(key, out var existing))
            {
                existing.Value = run;
                MoveToFront(existing);
                return;
            }
            var node = new Node { Key = key, Value = run };
            _map.Add(key, node);
            _chars += key.Text.Length;
            node.Next = _head;
            if (_head != null) _head.Prev = node;
            _head = node;
            _tail ??= node;
            while ((_map.Count > _maxEntries || _chars > _maxChars) && _tail != null && _tail != node)
            {
                var victim = _tail;
                Unlink(victim);
                _map.Remove(victim.Key);
                _chars -= victim.Key.Text.Length;
            }
        }
    }

    public void Clear()
    {
        lock (_lock)
        {
            _map.Clear();
            _head = _tail = null;
            _chars = 0;
        }
    }

    public ShapeCacheStatistics GetStatistics()
    {
        lock (_lock) return new ShapeCacheStatistics(_hits, _misses, _map.Count, _chars);
    }

    void MoveToFront(Node node)
    {
        if (node == _head) return;
        Unlink(node);
        node.Next = _head;
        if (_head != null) _head.Prev = node;
        _head = node;
        _tail ??= node;
    }

    void Unlink(Node node)
    {
        if (node.Prev != null) node.Prev.Next = node.Next;
        else _head = node.Next;
        if (node.Next != null) node.Next.Prev = node.Prev;
        else _tail = node.Prev;
        node.Prev = node.Next = null;
    }
}
