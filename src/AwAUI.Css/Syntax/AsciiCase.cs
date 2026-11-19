using System;
using System.Collections.Generic;

namespace AwAUI.Css.Syntax;

/// <summary>
/// ASCII 大小写不敏感的比较（Selectors 4 / CSS 的「ASCII case-insensitive」）：只折叠 A–Z，
/// 不像 <see cref="StringComparison.OrdinalIgnoreCase"/> 那样折叠 é/É 等非 ASCII 字母。
/// </summary>
internal static class AsciiCase
{
    static char Lower(char c) => c >= 'A' && c <= 'Z' ? (char)(c + 32) : c;

    /// <summary>两个任意字符串是否 ASCII 大小写不敏感相等。</summary>
    public static bool EqualsIgnoreCase(string? a, string? b)
    {
        if (ReferenceEquals(a, b)) return true;
        if (a is null || b is null || a.Length != b.Length) return false;
        return RegionEquals(a, 0, b, 0, a.Length, ignoreCase: true);
    }

    /// <summary>比较 <paramref name="a"/>[aStart..] 与 <paramref name="b"/>[bStart..] 的 <paramref name="length"/> 个字符。</summary>
    public static bool RegionEquals(string a, int aStart, string b, int bStart, int length, bool ignoreCase)
    {
        if (!ignoreCase) return string.CompareOrdinal(a, aStart, b, bStart, length) == 0;
        for (int i = 0; i < length; i++)
        {
            char x = a[aStart + i], y = b[bStart + i];
            if (x != y && Lower(x) != Lower(y)) return false;
        }
        return true;
    }

    public static bool StartsWith(string s, string prefix, bool ignoreCase)
        => s.Length >= prefix.Length && RegionEquals(s, 0, prefix, 0, prefix.Length, ignoreCase);

    public static bool EndsWith(string s, string suffix, bool ignoreCase)
        => s.Length >= suffix.Length && RegionEquals(s, s.Length - suffix.Length, suffix, 0, suffix.Length, ignoreCase);

    public static bool Contains(string s, string value, bool ignoreCase)
    {
        if (!ignoreCase) return s.IndexOf(value, StringComparison.Ordinal) >= 0;
        for (int i = 0; i + value.Length <= s.Length; i++)
            if (RegionEquals(s, i, value, 0, value.Length, ignoreCase: true)) return true;
        return false;
    }

    /// <summary>ASCII 大小写不敏感的字符串比较器（用于按类型名分桶）。</summary>
    public static readonly IEqualityComparer<string> Comparer = new AsciiComparer();

    sealed class AsciiComparer : IEqualityComparer<string>
    {
        public bool Equals(string? x, string? y) => EqualsIgnoreCase(x, y);

        public int GetHashCode(string obj)
        {
            // FNV-1a，先折叠 ASCII 大写
            unchecked
            {
                uint h = 2166136261;
                foreach (char c in obj) h = (h ^ Lower(c)) * 16777619;
                return (int)h;
            }
        }
    }
}
