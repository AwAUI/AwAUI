using System;
using System.Text;

namespace AwAUI.Diagnostics.Json;

/// <summary>UTF-8 校验与解码辅助（JSON 读取器与 WebSocket 文本帧共用）。</summary>
internal static class Utf8Util
{
    /// <summary>严格的 UTF-8 编码（不输出 BOM，遇到无效字节抛异常）。</summary>
    public static readonly UTF8Encoding Strict = new(false, true);

    /// <summary>
    /// 校验从 <paramref name="i"/> 开始的一个多字节序列（首字节 ≥ 0x80）。合法时返回序列长度，否则返回 0。
    /// 拒绝过长编码、代理区码点（U+D800–DFFF）与超过 U+10FFFF 的码点（RFC 3629）。
    /// </summary>
    public static int ValidateSequence(ReadOnlySpan<byte> s, int i)
    {
        byte b0 = s[i];
        int need;
        int min, max = 0xBF;
        if (b0 >= 0xC2 && b0 <= 0xDF) { need = 1; min = 0x80; }
        else if (b0 == 0xE0) { need = 2; min = 0xA0; }
        else if (b0 >= 0xE1 && b0 <= 0xEC) { need = 2; min = 0x80; }
        else if (b0 == 0xED) { need = 2; min = 0x80; max = 0x9F; }
        else if (b0 >= 0xEE && b0 <= 0xEF) { need = 2; min = 0x80; }
        else if (b0 == 0xF0) { need = 3; min = 0x90; }
        else if (b0 >= 0xF1 && b0 <= 0xF3) { need = 3; min = 0x80; }
        else if (b0 == 0xF4) { need = 3; min = 0x80; max = 0x8F; }
        else return 0;
        if (i + need >= s.Length) return 0; // 序列被截断
        byte b1 = s[i + 1];
        if (b1 < min || b1 > max) return 0;
        for (int k = 2; k <= need; k++)
        {
            byte b = s[i + k];
            if (b < 0x80 || b > 0xBF) return 0;
        }
        return need + 1;
    }

    /// <summary>返回第一个无效字节的位置；全部合法时返回 -1。</summary>
    public static int IndexOfInvalid(ReadOnlySpan<byte> s)
    {
        int i = 0;
        while (i < s.Length)
        {
            if (s[i] < 0x80) { i++; continue; }
            int n = ValidateSequence(s, i);
            if (n == 0) return i;
            i += n;
        }
        return -1;
    }

    public static unsafe string GetString(ReadOnlySpan<byte> utf8)
    {
        if (utf8.IsEmpty) return string.Empty;
        fixed (byte* p = utf8) return Strict.GetString(p, utf8.Length);
    }

    /// <summary>把 UTF-8 解码到 <paramref name="dest"/>，返回写入的字符数。</summary>
    public static unsafe int GetChars(ReadOnlySpan<byte> utf8, Span<char> dest)
    {
        if (utf8.IsEmpty) return 0;
        fixed (byte* p = utf8)
        fixed (char* c = dest)
            return Strict.GetChars(p, utf8.Length, c, dest.Length);
    }
}
