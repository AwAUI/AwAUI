using System;

namespace AwAUI.Text.OpenType;

/// <summary>'name' 表：按平台优先级挑出英文名称。</summary>
internal static class NameTable
{
    public const int Family = 1;
    public const int Subfamily = 2;
    public const int FullName = 4;
    public const int PostScriptName = 6;
    public const int TypographicFamily = 16;
    public const int TypographicSubfamily = 17;

    /// <summary>
    /// 读取指定 nameID 的字符串。优先级：Windows Unicode 美式英语 → Windows Unicode 其他语言 →
    /// Unicode 平台 → Mac Roman 英语。找不到时返回 null。
    /// </summary>
    public static string? Get(ReadOnlySpan<byte> s, int nameId)
    {
        if (s.Length < 6) return null;
        int count = Be.U16(s, 2);
        int storage = Be.U16(s, 4);
        int bestScore = -1, bestOffset = 0, bestLength = 0;
        bool bestUtf16 = false;
        for (int i = 0; i < count; i++)
        {
            int rec = 6 + i * 12;
            if (rec + 12 > s.Length) break;
            if (Be.U16(s, rec + 6) != nameId) continue;
            int platform = Be.U16(s, rec), encoding = Be.U16(s, rec + 2), language = Be.U16(s, rec + 4);
            int length = Be.U16(s, rec + 8), offset = storage + Be.U16(s, rec + 10);
            if (offset + length > s.Length) continue;

            int score;
            bool utf16;
            if (platform == 3 && (encoding == 1 || encoding == 10 || encoding == 0))
            {
                score = language == 0x0409 ? 40 : 30;
                utf16 = true;
            }
            else if (platform == 0)
            {
                score = 20;
                utf16 = true;
            }
            else if (platform == 1 && encoding == 0)
            {
                score = language == 0 ? 10 : 5;
                utf16 = false;
            }
            else continue;

            if (score > bestScore)
            {
                bestScore = score;
                bestOffset = offset;
                bestLength = length;
                bestUtf16 = utf16;
            }
        }
        if (bestScore < 0) return null;
        var bytes = s.Slice(bestOffset, bestLength);
        return bestUtf16 ? DecodeUtf16BE(bytes) : MacRoman.Decode(bytes);
    }

    public static string DecodeUtf16BE(ReadOnlySpan<byte> bytes)
    {
        var chars = new char[bytes.Length / 2];
        for (int i = 0; i < chars.Length; i++) chars[i] = (char)((bytes[i * 2] << 8) | bytes[i * 2 + 1]);
        return new string(chars);
    }
}
