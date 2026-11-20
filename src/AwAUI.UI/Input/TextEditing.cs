using System;
using System.Globalization;
using System.Text;

namespace AwAUI.UI;

/// <summary>
/// 与布局无关的文本编辑辅助：字素簇与词边界（近似）、单行文本清洗、密码掩码与显示下标的换算。
/// <para>
/// 编辑命令可能在两帧之间连续到达（布局还没有更新），所以边界不依赖段落布局：字素簇按扩展字素簇规则的常用子集近似
/// （组合记号、ZWJ 序列、变体选择符、肤色修饰、标签序列、区域指示符对、CRLF），词按字符类别（空白 / 词字符 / 标点）划分。
/// 向后查找的回溯步数有上限，长文本上每次操作的代价有界。
/// </para>
/// </summary>
internal static class TextEditing
{
    /// <summary>向后回溯组合序列的最大码点数（防止病态输入让一次退格变成 O(n)）。</summary>
    const int MaxClusterLookback = 64;

    /// <summary>把下标限制到 [0, Length] 并移出代理对中间。</summary>
    public static int Snap(string s, int offset)
    {
        if (offset <= 0) return 0;
        if (offset >= s.Length) return s.Length;
        if (char.IsLowSurrogate(s[offset]) && char.IsHighSurrogate(s[offset - 1])) offset--;
        return offset;
    }

    static int CodePointAt(string s, int i, out int size)
    {
        char c = s[i];
        if (char.IsHighSurrogate(c) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1]))
        {
            size = 2;
            return char.ConvertToUtf32(c, s[i + 1]);
        }
        size = 1;
        return c;
    }

    static int CodePointBefore(string s, int i, out int size)
    {
        char c = s[i - 1];
        if (char.IsLowSurrogate(c) && i >= 2 && char.IsHighSurrogate(s[i - 2]))
        {
            size = 2;
            return char.ConvertToUtf32(s[i - 2], c);
        }
        size = 1;
        return c;
    }

    static UnicodeCategory Category(int cp) =>
        cp < 0x10000 ? CharUnicodeInfo.GetUnicodeCategory((char)cp) : CharUnicodeInfo.GetUnicodeCategory(char.ConvertFromUtf32(cp), 0);

    /// <summary>附着在前一个字符上、不单独成簇的码点。</summary>
    static bool IsExtend(int cp)
    {
        if (cp >= 0x1F3FB && cp <= 0x1F3FF) return true;         // 肤色修饰
        if (cp >= 0xE0020 && cp <= 0xE007F) return true;         // 标签
        if (cp >= 0xFE00 && cp <= 0xFE0F) return true;           // 变体选择符
        if (cp == 0x200C) return true;                           // ZWNJ
        if (cp < 0x300) return false;
        var cat = Category(cp);
        return cat is UnicodeCategory.NonSpacingMark or UnicodeCategory.EnclosingMark or UnicodeCategory.SpacingCombiningMark;
    }

    static bool IsRegionalIndicator(int cp) => cp >= 0x1F1E6 && cp <= 0x1F1FF;

    const int Zwj = 0x200D;

    /// <summary>下一个字素簇边界（<paramref name="offset"/> 之后）。</summary>
    public static int NextGrapheme(string s, int offset)
    {
        int i = Snap(s, offset);
        if (i >= s.Length) return s.Length;
        if (s[i] == '\r') return i + 1 < s.Length && s[i + 1] == '\n' ? i + 2 : i + 1;
        if (s[i] == '\n') return i + 1;
        int cp = CodePointAt(s, i, out int size);
        int j = i + size;
        if (IsRegionalIndicator(cp) && j < s.Length && IsRegionalIndicator(CodePointAt(s, j, out int rs))) j += rs;
        while (j < s.Length)
        {
            int next = CodePointAt(s, j, out int ns);
            if (IsExtend(next)) { j += ns; continue; }
            if (next == Zwj)
            {
                j += ns;
                if (j < s.Length && !char.IsControl(s[j])) { CodePointAt(s, j, out int zs); j += zs; }
                continue;
            }
            break;
        }
        return j;
    }

    /// <summary>上一个字素簇边界（<paramref name="offset"/> 之前）。</summary>
    public static int PreviousGrapheme(string s, int offset)
    {
        int i = Snap(s, offset);
        if (i <= 0) return 0;
        if (s[i - 1] == '\n') return i >= 2 && s[i - 2] == '\r' ? i - 2 : i - 1;
        int cp = CodePointBefore(s, i, out int size);
        int j = i - size;
        int steps = 0;
        // 当前码点是附着类的：继续向前找到基字符
        while (j > 0 && steps++ < MaxClusterLookback)
        {
            if (IsExtend(cp) || cp == Zwj)
            {
                cp = CodePointBefore(s, j, out size);
                j -= size;
                continue;
            }
            // 基字符前面是 ZWJ：属于同一个 ZWJ 序列
            if (j > 0 && s[j - 1] == (char)Zwj && j >= 2)
            {
                j--;
                cp = CodePointBefore(s, j, out size);
                j -= size;
                continue;
            }
            break;
        }
        if (IsRegionalIndicator(cp))
        {
            // 区域指示符两两成对：数一下前面连续的个数决定是否与前一个配对
            int k = j, count = 0;
            while (k >= 2 && count < MaxClusterLookback && IsRegionalIndicator(CodePointBefore(s, k, out int rs))) { k -= rs; count++; }
            if (count % 2 == 1) j -= 2;
        }
        return j;
    }

    enum CharClass : byte { Space, Word, Ideograph, Punct }

    static CharClass Classify(int cp)
    {
        if (cp < 0x80)
        {
            if (cp == ' ' || cp == '\t' || cp == '\n' || cp == '\r') return CharClass.Space;
            if ((cp >= 'a' && cp <= 'z') || (cp >= 'A' && cp <= 'Z') || (cp >= '0' && cp <= '9') || cp == '_') return CharClass.Word;
            return CharClass.Punct;
        }
        // CJK 统一表意文字、平假名、片假名、谚文音节：按「每个字一个词」近似（没有词典切分）
        if ((cp >= 0x3040 && cp <= 0x30FF) || (cp >= 0x3400 && cp <= 0x9FFF) || (cp >= 0xAC00 && cp <= 0xD7AF)
            || (cp >= 0xF900 && cp <= 0xFAFF) || (cp >= 0x20000 && cp <= 0x3FFFF)) return CharClass.Ideograph;
        var cat = Category(cp);
        switch (cat)
        {
            case UnicodeCategory.SpaceSeparator:
            case UnicodeCategory.LineSeparator:
            case UnicodeCategory.ParagraphSeparator:
                return CharClass.Space;
            case UnicodeCategory.UppercaseLetter:
            case UnicodeCategory.LowercaseLetter:
            case UnicodeCategory.TitlecaseLetter:
            case UnicodeCategory.ModifierLetter:
            case UnicodeCategory.OtherLetter:
            case UnicodeCategory.DecimalDigitNumber:
            case UnicodeCategory.LetterNumber:
            case UnicodeCategory.OtherNumber:
            case UnicodeCategory.NonSpacingMark:
            case UnicodeCategory.SpacingCombiningMark:
            case UnicodeCategory.EnclosingMark:
            case UnicodeCategory.ConnectorPunctuation:
                return CharClass.Word;
            default:
                return CharClass.Punct;
        }
    }

    /// <summary>向前到下一个词尾：跳过空白，再跳过一串同类字符（表意文字一次一个）。</summary>
    public static int NextWordEnd(string s, int offset)
    {
        int i = Snap(s, offset);
        while (i < s.Length && Classify(CodePointAt(s, i, out _)) == CharClass.Space) i = NextGrapheme(s, i);
        if (i >= s.Length) return s.Length;
        var cls = Classify(CodePointAt(s, i, out _));
        i = NextGrapheme(s, i);
        if (cls == CharClass.Ideograph) return i;
        while (i < s.Length && Classify(CodePointAt(s, i, out _)) == cls) i = NextGrapheme(s, i);
        return i;
    }

    /// <summary>向后到上一个词首：跳过空白，再跳过一串同类字符（表意文字一次一个）。</summary>
    public static int PreviousWordStart(string s, int offset)
    {
        int i = Snap(s, offset);
        while (i > 0 && Classify(CodePointBefore(s, i, out _)) == CharClass.Space) i = PreviousGrapheme(s, i);
        if (i <= 0) return 0;
        var cls = Classify(CodePointBefore(s, i, out _));
        i = PreviousGrapheme(s, i);
        if (cls == CharClass.Ideograph) return i;
        while (i > 0 && Classify(CodePointBefore(s, i, out _)) == cls) i = PreviousGrapheme(s, i);
        return i;
    }

    /// <summary>包含 <paramref name="offset"/> 的词（双击选词）：同类字符的最大连续段；在空白上时选中这段空白。</summary>
    public static (int Start, int End) WordAt(string s, int offset)
    {
        int i = Snap(s, offset);
        if (s.Length == 0) return (0, 0);
        if (i >= s.Length) i = PreviousGrapheme(s, s.Length);
        var cls = Classify(CodePointAt(s, i, out _));
        int end = NextGrapheme(s, i);
        int start = i;
        if (cls == CharClass.Ideograph) return (start, end);
        int guard = 0;
        while (start > 0 && guard++ < 1 << 20 && Classify(CodePointBefore(s, start, out _)) == cls) start = PreviousGrapheme(s, start);
        while (end < s.Length && Classify(CodePointAt(s, end, out _)) == cls) end = NextGrapheme(s, end);
        return (start, end);
    }

    /// <summary>
    /// 单行输入框的值清洗（与 HTML 的 value sanitization 一致去掉换行；另去掉 Tab 以外的 C0 控制字符与 DEL、孤立代理）。
    /// </summary>
    public static string SanitizeSingleLine(string text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        int i = 0;
        while (i < text.Length && !Drop(text, i)) i++;
        if (i == text.Length) return text;
        var sb = new StringBuilder(text.Length);
        sb.Append(text, 0, i);
        for (; i < text.Length; i++)
            if (!Drop(text, i)) sb.Append(text[i]);
        return sb.ToString();
    }

    public static string SanitizeMultiline(string text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        int index = 0;
        while (index < text.Length && (text[index] == '\n' || !Drop(text, index))) index++;
        if (index == text.Length) return text;
        var result = new StringBuilder(text.Length);
        result.Append(text, 0, index);
        for (; index < text.Length; index++)
        {
            char c = text[index];
            if (c == '\r')
            {
                if (index + 1 < text.Length && text[index + 1] == '\n') index++;
                result.Append('\n');
            }
            else if (c == '\n' || !Drop(text, index)) result.Append(c);
        }
        return result.ToString();
    }

    static bool Drop(string s, int i)
    {
        char c = s[i];
        if (c < 0x20) return c != '\t';
        if (c == 0x7F) return true;
        if (char.IsHighSurrogate(c)) return i + 1 >= s.Length || !char.IsLowSurrogate(s[i + 1]);
        if (char.IsLowSurrogate(c)) return i == 0 || !char.IsHighSurrogate(s[i - 1]);
        return false;
    }

    public const char PasswordBullet = '•';

    /// <summary>密码掩码：每个码点一个圆点。</summary>
    public static string Mask(string s)
    {
        int n = 0;
        for (int i = 0; i < s.Length; i++)
            if (!(char.IsLowSurrogate(s[i]) && i > 0 && char.IsHighSurrogate(s[i - 1]))) n++;
        return new string(PasswordBullet, n);
    }

    /// <summary>文档下标 → 显示文本下标（密码框按码点计数，否则相同）。</summary>
    public static int ToDisplay(string document, int offset, bool password)
    {
        offset = Snap(document, offset);
        if (!password) return offset;
        int n = 0;
        for (int i = 0; i < offset; i++)
            if (!(char.IsLowSurrogate(document[i]) && i > 0 && char.IsHighSurrogate(document[i - 1]))) n++;
        return n;
    }

    /// <summary>显示文本下标 → 文档下标。</summary>
    public static int FromDisplay(string document, int displayOffset, bool password)
    {
        if (!password) return Snap(document, displayOffset);
        int i = 0, n = 0;
        while (i < document.Length && n < displayOffset)
        {
            i += char.IsHighSurrogate(document[i]) && i + 1 < document.Length && char.IsLowSurrogate(document[i + 1]) ? 2 : 1;
            n++;
        }
        return i;
    }
}
