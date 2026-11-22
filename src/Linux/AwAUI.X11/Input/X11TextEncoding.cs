using System;
using System.Text;

namespace AwAUI.X11;

// 本目录（Input/）只含与 Xlib 无关的纯逻辑：由测试以源码链接方式在任何系统上编译运行（含 net48）。

/// <summary>
/// XIM 预编辑文本的解码（纯逻辑，不依赖 Xlib）。
/// <para>
/// <c>XIMText.encoding_is_wchar</c> 为 0 时，<c>string</c> 是当前 <c>LC_CTYPE</c> 区域设置的多字节编码
/// （例如 zh_CN.GB18030 下是 GBK / GB18030），<b>不能假定为 UTF-8</b>。调用方用 C 库的 <c>mbrtowc</c> 按同一区域设置
/// 逐字符转换（见 <see cref="MultibyteDecoder"/>），这里负责码点 → UTF-16 以及无效序列的回退。
/// </para>
/// 宽字符（Linux 上 <c>wchar_t</c> 为 4 字节的 UTF-32）同样经 <see cref="FromUtf32(uint*, int)"/> 解码。
/// </summary>
internal static unsafe class X11TextEncoding
{
    /// <summary>无效码点的替换字符 U+FFFD。</summary>
    public const uint Replacement = 0xFFFD;

    /// <summary>
    /// 多字节 → 码点的逐字符转换器：把 <paramref name="source"/> 的前 <paramref name="remaining"/> 个字节中第一个字符
    /// 写入 <paramref name="codePoint"/>，返回消耗的字节数（&gt; 0）；返回 0 表示遇到 NUL（文本结束），
    /// 返回 -1 表示无效序列、-2 表示末尾不完整序列（调用方写入一个替换字符并前进一步）。
    /// <paramref name="state"/> 是 16 字节的零初始化 <c>mbstate_t</c> 缓冲，供有状态编码使用。
    /// </summary>
    public delegate int MultibyteDecoder(byte* state, byte* source, int remaining, uint* codePoint);

    /// <summary>
    /// wchar_t（UTF-32）数组 → UTF-16，遇到 NUL 或达到 <paramref name="count"/> 为止；
    /// 代理区与超出 U+10FFFF 的值替换为 U+FFFD。
    /// </summary>
    public static string FromUtf32(uint* codePoints, int count)
    {
        if (codePoints == null || count <= 0) return string.Empty;
        var sb = new StringBuilder(count);
        for (int i = 0; i < count; i++)
        {
            uint cp = codePoints[i];
            if (cp == 0) break;
            Append(sb, cp);
        }
        return sb.ToString();
    }

    /// <summary>
    /// 逐字符转换多字节文本。转换失败的字节各写入一个 U+FFFD 后继续（不中断整段文本），因此输出总是合法的 UTF-16；
    /// 无法确定区域设置编码时也绝不会把原始字节当作 UTF-8 解释。
    /// </summary>
    public static string FromMultibyte(byte* bytes, int byteCount, MultibyteDecoder decode)
    {
        if (bytes == null || byteCount <= 0) return string.Empty;
        if (decode == null) throw new ArgumentNullException(nameof(decode));

        var sb = new StringBuilder(Math.Min(byteCount, 4096));
        byte* state = stackalloc byte[16];
        for (int i = 0; i < 16; i++) state[i] = 0;

        int offset = 0;
        while (offset < byteCount)
        {
            uint cp = Replacement;
            int consumed = decode(state, bytes + offset, byteCount - offset, &cp);
            if (consumed == 0) break; // NUL：文本结束
            if (consumed < 0)
            {
                // 无效 / 不完整序列：用一个替换字符代替一个字节，继续解码后面的文本；
                // 清零转换状态，避免有状态编码（如 ISO-2022）把错误状态带到后续字节
                sb.Append('�');
                for (int i = 0; i < 16; i++) state[i] = 0;
                offset++;
                continue;
            }
            if (consumed > byteCount - offset) consumed = byteCount - offset; // 防御：转换器报告的字节数不超过输入
            if (cp == 0) break;
            Append(sb, cp);
            offset += consumed;
        }
        return sb.ToString();
    }

    /// <summary>是否为 Unicode 标量值（排除代理区与超出 U+10FFFF 的值）。</summary>
    public static bool IsScalar(uint cp) => cp <= 0x10FFFF && (cp < 0xD800 || cp > 0xDFFF);

    static void Append(StringBuilder sb, uint cp)
    {
        if (IsScalar(cp)) sb.Append(char.ConvertFromUtf32((int)cp));
        else sb.Append('�');
    }
}
