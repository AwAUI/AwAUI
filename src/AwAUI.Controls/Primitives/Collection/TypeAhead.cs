using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using AwAUI.Platform;

namespace AwAUI.Controls.Primitives;

/// <summary>
/// 类型前缀查找（Radix 的 typeahead）：连续键入的字符累积成搜索串，距上次按键超过 <see cref="ResetDelay"/>（默认 1 秒）后清空；
/// 从当前项开始向后查找文本以搜索串开头的项（回绕）。搜索串全是同一个字符时（如连按 <c>a a a</c>）按单字符处理，
/// 并跳过当前项，于是在所有以该字符开头的项之间循环。匹配不区分大小写与变音符号（<c>é</c> 匹配 <c>e</c>）。
/// <para>
/// 与 Radix 的差别：按索引而不是按文本值排除当前项，文本重复的项（两个「Apple」）也能依次到达。
/// 时间由调用方传入（事件时间戳），便于测试；搜索串最长 <see cref="MaxSearchLength"/> 个字符，超出的键入被忽略。
/// </para>
/// </summary>
public sealed class TypeAhead
{
    /// <summary>搜索串的长度上限（UTF-16 单元）。</summary>
    public const int MaxSearchLength = 256;

    string _search = "";
    TimeSpan _last;

    /// <summary>搜索串的清空延迟。</summary>
    public TimeSpan ResetDelay { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>当前搜索串（未过期时）。</summary>
    public string Search => _search;

    /// <summary>
    /// 是否正处于类型查找中（搜索串非空且未过期）。此时空格键应当作为搜索字符而不是激活键（Radix Select / Menu 的行为）。
    /// </summary>
    public bool IsTypingAhead(TimeSpan now) => _search.Length > 0 && now - _last < ResetDelay;

    /// <summary>清空搜索串。</summary>
    public void Reset() => _search = "";

    /// <summary>按键是否应进入类型查找：恰好一个字符（一个码点），且没有按 Ctrl / Alt / Meta。</summary>
    public static bool IsTypeAheadKey(string? key, KeyModifiers modifiers)
    {
        if (key is null || key.Length == 0 || key.Length > 2) return false;
        if ((modifiers & (KeyModifiers.Control | KeyModifiers.Alt | KeyModifiers.Meta)) != 0) return false;
        if (key.Length == 2 && !char.IsSurrogatePair(key[0], key[1])) return false;
        if (key.Length == 1 && (char.IsControl(key[0]) || char.IsSurrogate(key[0]))) return false;
        return true;
    }

    /// <summary>把键入追加到搜索串（过期则先清空），返回新的搜索串。</summary>
    public string Append(string key, TimeSpan timestamp)
    {
        if (key is null) throw new ArgumentNullException(nameof(key));
        if (!IsTypingAhead(timestamp)) _search = "";
        _last = timestamp;
        if (_search.Length + key.Length <= MaxSearchLength) _search += key;
        return _search;
    }

    /// <summary>
    /// 处理一次键入并返回应高亮 / 聚焦的项索引；没有匹配或匹配的就是当前项时返回 -1。
    /// <paramref name="textValues"/> 为 null 的项不参与；<paramref name="isEnabled"/> 为 false 的项（禁用）被跳过。
    /// </summary>
    public int HandleKey(string key, TimeSpan timestamp, IReadOnlyList<string?> textValues, int currentIndex, Func<int, bool>? isEnabled = null)
    {
        var search = Append(key, timestamp);
        return GetNextMatch(textValues, search, currentIndex, isEnabled);
    }

    /// <summary>
    /// 在集合上处理一次键入。<typeparamref name="TKey"/> 为值类型时 <paramref name="currentKey"/> 的 <c>default</c> 是一个合法的键：
    /// 没有当前项时请用不带它的重载。
    /// </summary>
    public CollectionItem<TKey>? HandleKey<TKey>(string key, TimeSpan timestamp, ItemCollection<TKey> items, TKey? currentKey) where TKey : notnull
    {
        if (items is null) throw new ArgumentNullException(nameof(items));
        var search = Append(key, timestamp);
        return items.FindNextMatch(search, currentKey);
    }

    /// <summary>在集合上处理一次键入（没有当前项）。</summary>
    public CollectionItem<TKey>? HandleKey<TKey>(string key, TimeSpan timestamp, ItemCollection<TKey> items) where TKey : notnull
    {
        if (items is null) throw new ArgumentNullException(nameof(items));
        var search = Append(key, timestamp);
        return items.FindNextMatch(search);
    }

    /// <summary>
    /// Radix 的 <c>getNextMatch</c>（按索引）：从 <paramref name="currentIndex"/> 开始回绕查找；单字符搜索时跳过当前项。
    /// </summary>
    public static int GetNextMatch(IReadOnlyList<string?> values, string search, int currentIndex, Func<int, bool>? isEnabled = null)
    {
        if (values is null) throw new ArgumentNullException(nameof(values));
        var normalized = new string?[values.Count];
        for (int i = 0; i < normalized.Length; i++) normalized[i] = values[i] is { } v ? Normalize(v) : null;
        return GetNextMatchNormalized(normalized, NormalizeSearch(search), currentIndex,
            i => normalized[i] is not null && (isEnabled is null || isEnabled(i)));
    }

    internal static int GetNextMatchNormalized(IReadOnlyList<string?> normalizedValues, string normalizedSearch, int currentIndex, Func<int, bool> eligible)
    {
        int n = normalizedValues.Count;
        if (n == 0 || normalizedSearch.Length == 0) return -1;
        if (currentIndex < 0 || currentIndex >= n || !eligible(currentIndex)) currentIndex = -1;
        bool single = CodePointCount(normalizedSearch) == 1;
        int start = Math.Max(currentIndex, 0);
        for (int k = 0; k < n; k++)
        {
            int i = (start + k) % n;
            if (single && i == currentIndex) continue;
            if (!eligible(i)) continue;
            var v = normalizedValues[i];
            if (v is not null && v.StartsWith(normalizedSearch, StringComparison.Ordinal))
                return i == currentIndex ? -1 : i;
        }
        return -1;
    }

    /// <summary>规范化搜索串，并把「同一字符重复」折叠成单个字符。</summary>
    internal static string NormalizeSearch(string search)
    {
        var s = Normalize(search ?? "");
        if (s.Length == 0) return s;
        int firstLen = char.IsHighSurrogate(s[0]) && s.Length > 1 && char.IsLowSurrogate(s[1]) ? 2 : 1;
        if (s.Length <= firstLen) return s;
        for (int i = firstLen; i < s.Length; i += firstLen)
        {
            if (i + firstLen > s.Length || string.CompareOrdinal(s, i, s, 0, firstLen) != 0) return s;
        }
        return s.Substring(0, firstLen);
    }

    /// <summary>
    /// 匹配用的规范化：Unicode 兼容分解（全角 → 半角等）、去掉不占位的组合附加符号（变音符号）、按不变区域转小写。
    /// 含孤立代理项等无法规范化的输入时退回只转小写（不抛出）。
    /// </summary>
    public static string Normalize(string text)
    {
        if (text is null) throw new ArgumentNullException(nameof(text));
        if (text.Length == 0) return text;
        string decomposed;
        try
        {
            decomposed = text.Normalize(NormalizationForm.FormKD);
        }
        catch (ArgumentException)
        {
            return text.ToLowerInvariant();
        }
        var sb = new StringBuilder(decomposed.Length);
        foreach (char c in decomposed)
        {
            var cat = CharUnicodeInfo.GetUnicodeCategory(c);
            // 只去掉不占位的附加符号（Mn / Me）；印度系文字的占位元音符号（Mc）保留，否则会改变字义
            if (cat is UnicodeCategory.NonSpacingMark or UnicodeCategory.EnclosingMark) continue;
            sb.Append(c);
        }
        return sb.ToString().ToLowerInvariant();
    }

    static int CodePointCount(string s)
    {
        int n = 0;
        for (int i = 0; i < s.Length; i++)
        {
            if (char.IsHighSurrogate(s[i]) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1])) i++;
            n++;
        }
        return n;
    }
}
