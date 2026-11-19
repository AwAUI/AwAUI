using System;

namespace AwAUI.Text.OpenType;

/// <summary>OpenType 四字节标签（表名、特性名、文字与语言标签）的工具与常用常量。</summary>
public static class OpenTypeTag
{
    /// <summary>把最多 4 个 ASCII 字符的字符串编码为标签，不足 4 个时以空格补齐。</summary>
    public static uint Make(string tag)
    {
        if (tag is null) throw new ArgumentNullException(nameof(tag));
        if (tag.Length > 4) throw new ArgumentException("A tag has at most four characters.", nameof(tag));
        uint v = 0;
        for (int i = 0; i < 4; i++)
        {
            char c = i < tag.Length ? tag[i] : ' ';
            if (c > 0x7E || c < 0x20) throw new ArgumentException("A tag consists of printable ASCII characters.", nameof(tag));
            v = (v << 8) | c;
        }
        return v;
    }

    /// <summary>把标签解码为 4 个字符的字符串。</summary>
    public static string ToString(uint tag)
    {
        var chars = new char[4];
        for (int i = 0; i < 4; i++)
        {
            byte b = (byte)(tag >> (24 - i * 8));
            chars[i] = b >= 0x20 && b <= 0x7E ? (char)b : '?';
        }
        return new string(chars);
    }

    public const uint Head = 0x68656164;   // 'head'
    public const uint Hhea = 0x68686561;   // 'hhea'
    public const uint Maxp = 0x6D617870;   // 'maxp'
    public const uint Hmtx = 0x686D7478;   // 'hmtx'
    public const uint Cmap = 0x636D6170;   // 'cmap'
    public const uint Loca = 0x6C6F6361;   // 'loca'
    public const uint Glyf = 0x676C7966;   // 'glyf'
    public const uint Name = 0x6E616D65;   // 'name'
    public const uint OS2 = 0x4F532F32;    // 'OS/2'
    public const uint Post = 0x706F7374;   // 'post'
    public const uint Kern = 0x6B65726E;   // 'kern'
    public const uint Cff = 0x43464620;    // 'CFF '
    public const uint Cff2 = 0x43464632;   // 'CFF2'
    public const uint Gsub = 0x47535542;   // 'GSUB'
    public const uint Gpos = 0x47504F53;   // 'GPOS'
    public const uint Gdef = 0x47444546;   // 'GDEF'
    public const uint Fvar = 0x66766172;   // 'fvar'
    public const uint Sbix = 0x73626978;   // 'sbix'
    public const uint Cbdt = 0x43424454;   // 'CBDT'
    public const uint Colr = 0x434F4C52;   // 'COLR'
}
