using System;
using AwAUI.Unicode;

namespace AwAUI.Text.Shaping;

/// <summary>
/// 阿拉伯式连写（HarfBuzz hb-ot-shaper-arabic 的状态机）：按 Unicode Joining_Type（与叙利亚文的 Alaph / Dalath_Rish 连写组）
/// 为每个字符确定 isol / fina / fin2 / fin3 / medi / med2 / init 形式；透明字符（组合标记等）不打断连写，ZWJ 为 C 类（引起连写），ZWNJ 为 U 类。
/// 字体没有这些特性时（例如只有 AAT 'morx' 的 Geeza Pro），按 Unicode 阿拉伯表现形式（FB50–FDFF、FE70–FEFF）经 cmap 回退，并合成 lam-alef 连字。
/// </summary>
internal static class ArabicShaping
{
    public const byte Isol = 0, Fina = 1, Fin2 = 2, Fin3 = 3, Medi = 4, Med2 = 5, Init = 6, None = 7;
    public const int ActionCount = 8;

    /// <summary>s_arabicFeatures 中的下标与动作值相同。</summary>
    public static int ActionForFeatureIndex(int i) => i;

    // 列：U, L, R, D, ALAPH, DALATH_RISH；每项 (前一字符动作, 当前字符动作, 下一状态)
    static readonly byte[,,] s_table =
    {
        { { None, None, 0 }, { None, Isol, 2 }, { None, Isol, 1 }, { None, Isol, 2 }, { None, Isol, 1 }, { None, Isol, 6 } },
        { { None, None, 0 }, { None, Isol, 2 }, { None, Isol, 1 }, { None, Isol, 2 }, { None, Fin2, 5 }, { None, Isol, 6 } },
        { { None, None, 0 }, { None, Isol, 2 }, { Init, Fina, 1 }, { Init, Fina, 3 }, { Init, Fina, 4 }, { Init, Fina, 6 } },
        { { None, None, 0 }, { None, Isol, 2 }, { Medi, Fina, 1 }, { Medi, Fina, 3 }, { Medi, Fina, 4 }, { Medi, Fina, 6 } },
        { { None, None, 0 }, { None, Isol, 2 }, { Med2, Isol, 1 }, { Med2, Isol, 2 }, { Med2, Fin2, 5 }, { Med2, Isol, 6 } },
        { { None, None, 0 }, { None, Isol, 2 }, { Isol, Isol, 1 }, { Isol, Isol, 2 }, { Isol, Fin2, 5 }, { Isol, Isol, 6 } },
        { { None, None, 0 }, { None, Isol, 2 }, { None, Isol, 1 }, { None, Isol, 2 }, { None, Fin3, 5 }, { None, Isol, 6 } },
    };

    const int ColT = -1;

    /// <summary>状态机的列；透明字符返回 −1。</summary>
    static int Column(int cp)
    {
        switch (UnicodeData.GetJoiningType(cp))
        {
            case JoiningType.Transparent: return ColT;
            case JoiningType.LeftJoining: return 1;
            case JoiningType.RightJoining:
                var g = UnicodeData.GetJoiningGroup(cp);
                if (g == JoiningGroup.Alaph) return 4;
                if (g == JoiningGroup.DalathRish) return 5;
                return 2;
            case JoiningType.DualJoining:
            case JoiningType.JoinCausing:
                return 3;
            default:
                return 0;
        }
    }

    /// <summary>
    /// 计算每个字形的连写动作并设置掩码。<paramref name="before"/> / <paramref name="after"/> 是整形范围之前 / 之后的上下文文本
    /// （逻辑顺序），用于跨越整形边界的连写判断。
    /// </summary>
    public static void SetupMasks(GlyphBuffer buffer, uint[] masks, ReadOnlySpan<char> before, ReadOnlySpan<char> after)
    {
        var info = buffer.Info;
        int count = buffer.Len;
        int prev = -1, state = 0;
        // 前文：取最近一个非透明字符确定初始状态（最多看 ContextLength 个码点，与 ContextKey 一致）
        for (int i = before.Length, k = 0; i > 0 && k < ContextLength; k++)
        {
            int cp = CodePoint.DecodeBefore(before, i, out int len);
            i -= len;
            int col = Column(cp);
            if (col == ColT) continue;
            state = s_table[state, col, 2];
            break;
        }
        for (int i = 0; i < count; i++)
        {
            int col = Column(info[i].CodePoint);
            if (col == ColT)
            {
                info[i].JoiningAction = None;
                continue;
            }
            byte prevAction = s_table[state, col, 0];
            if (prevAction != None && prev >= 0) info[prev].JoiningAction = prevAction;
            info[i].JoiningAction = s_table[state, col, 1];
            prev = i;
            state = s_table[state, col, 2];
        }
        for (int i = 0, k = 0; i < after.Length && k < ContextLength; k++)
        {
            int cp = CodePoint.Decode(after, i, out int len);
            i += len;
            int col = Column(cp);
            if (col == ColT) continue;
            byte prevAction = s_table[state, col, 0];
            if (prevAction != None && prev >= 0) info[prev].JoiningAction = prevAction;
            break;
        }
        for (int i = 0; i < count; i++)
        {
            byte a = info[i].JoiningAction;
            if (a < masks.Length) info[i].Mask |= masks[a];
        }
    }

    /// <summary>整形范围前后的上下文最多看多少个码点（与 HarfBuzz 的 HB_BUFFER_CONTEXT_LENGTH 相同）。</summary>
    public const int ContextLength = 5;

    /// <summary>
    /// 上下文对连写结果的影响，压缩为一个整数（供整形缓存作键）：前文中最近的非透明字符与后文中最近的非透明字符的状态机列。
    /// 两侧都不能连写（没有字符、U 类）时返回 0，表示上下文不影响结果、可以不传。各侧只看 <see cref="ContextLength"/> 个码点。
    /// </summary>
    public static int ContextKey(ReadOnlySpan<char> before, ReadOnlySpan<char> after)
    {
        int colBefore = 0, colAfter = 0;
        int k = 0;
        for (int i = before.Length; i > 0 && k < ContextLength; k++)
        {
            int cp = CodePoint.DecodeBefore(before, i, out int len);
            i -= len;
            int col = Column(cp);
            if (col == ColT) continue;
            colBefore = col;
            break;
        }
        k = 0;
        for (int i = 0; i < after.Length && k < ContextLength; k++)
        {
            int cp = CodePoint.Decode(after, i, out int len);
            i += len;
            int col = Column(cp);
            if (col == ColT) continue;
            colAfter = col;
            break;
        }
        return colBefore * 8 + colAfter;
    }

    /// <summary>码点 → (孤立, 词尾, 词首, 词中) 表现形式；0 表示没有。生成自 Unicode 字符数据库的 &lt;isolated&gt; 等兼容分解（FB50–FDFF、FE70–FEFF 中的单字符分解）。</summary>
    static ReadOnlySpan<ushort> Forms => new ushort[]
    {
        0x0621, 0xFE80, 0x0000, 0x0000, 0x0000,
        0x0622, 0xFE81, 0xFE82, 0x0000, 0x0000,
        0x0623, 0xFE83, 0xFE84, 0x0000, 0x0000,
        0x0624, 0xFE85, 0xFE86, 0x0000, 0x0000,
        0x0625, 0xFE87, 0xFE88, 0x0000, 0x0000,
        0x0626, 0xFE89, 0xFE8A, 0xFE8B, 0xFE8C,
        0x0627, 0xFE8D, 0xFE8E, 0x0000, 0x0000,
        0x0628, 0xFE8F, 0xFE90, 0xFE91, 0xFE92,
        0x0629, 0xFE93, 0xFE94, 0x0000, 0x0000,
        0x062A, 0xFE95, 0xFE96, 0xFE97, 0xFE98,
        0x062B, 0xFE99, 0xFE9A, 0xFE9B, 0xFE9C,
        0x062C, 0xFE9D, 0xFE9E, 0xFE9F, 0xFEA0,
        0x062D, 0xFEA1, 0xFEA2, 0xFEA3, 0xFEA4,
        0x062E, 0xFEA5, 0xFEA6, 0xFEA7, 0xFEA8,
        0x062F, 0xFEA9, 0xFEAA, 0x0000, 0x0000,
        0x0630, 0xFEAB, 0xFEAC, 0x0000, 0x0000,
        0x0631, 0xFEAD, 0xFEAE, 0x0000, 0x0000,
        0x0632, 0xFEAF, 0xFEB0, 0x0000, 0x0000,
        0x0633, 0xFEB1, 0xFEB2, 0xFEB3, 0xFEB4,
        0x0634, 0xFEB5, 0xFEB6, 0xFEB7, 0xFEB8,
        0x0635, 0xFEB9, 0xFEBA, 0xFEBB, 0xFEBC,
        0x0636, 0xFEBD, 0xFEBE, 0xFEBF, 0xFEC0,
        0x0637, 0xFEC1, 0xFEC2, 0xFEC3, 0xFEC4,
        0x0638, 0xFEC5, 0xFEC6, 0xFEC7, 0xFEC8,
        0x0639, 0xFEC9, 0xFECA, 0xFECB, 0xFECC,
        0x063A, 0xFECD, 0xFECE, 0xFECF, 0xFED0,
        0x0641, 0xFED1, 0xFED2, 0xFED3, 0xFED4,
        0x0642, 0xFED5, 0xFED6, 0xFED7, 0xFED8,
        0x0643, 0xFED9, 0xFEDA, 0xFEDB, 0xFEDC,
        0x0644, 0xFEDD, 0xFEDE, 0xFEDF, 0xFEE0,
        0x0645, 0xFEE1, 0xFEE2, 0xFEE3, 0xFEE4,
        0x0646, 0xFEE5, 0xFEE6, 0xFEE7, 0xFEE8,
        0x0647, 0xFEE9, 0xFEEA, 0xFEEB, 0xFEEC,
        0x0648, 0xFEED, 0xFEEE, 0x0000, 0x0000,
        0x0649, 0xFEEF, 0xFEF0, 0xFBE8, 0xFBE9,
        0x064A, 0xFEF1, 0xFEF2, 0xFEF3, 0xFEF4,
        0x0671, 0xFB50, 0xFB51, 0x0000, 0x0000,
        0x0677, 0xFBDD, 0x0000, 0x0000, 0x0000,
        0x0679, 0xFB66, 0xFB67, 0xFB68, 0xFB69,
        0x067A, 0xFB5E, 0xFB5F, 0xFB60, 0xFB61,
        0x067B, 0xFB52, 0xFB53, 0xFB54, 0xFB55,
        0x067E, 0xFB56, 0xFB57, 0xFB58, 0xFB59,
        0x067F, 0xFB62, 0xFB63, 0xFB64, 0xFB65,
        0x0680, 0xFB5A, 0xFB5B, 0xFB5C, 0xFB5D,
        0x0683, 0xFB76, 0xFB77, 0xFB78, 0xFB79,
        0x0684, 0xFB72, 0xFB73, 0xFB74, 0xFB75,
        0x0686, 0xFB7A, 0xFB7B, 0xFB7C, 0xFB7D,
        0x0687, 0xFB7E, 0xFB7F, 0xFB80, 0xFB81,
        0x0688, 0xFB88, 0xFB89, 0x0000, 0x0000,
        0x068C, 0xFB84, 0xFB85, 0x0000, 0x0000,
        0x068D, 0xFB82, 0xFB83, 0x0000, 0x0000,
        0x068E, 0xFB86, 0xFB87, 0x0000, 0x0000,
        0x0691, 0xFB8C, 0xFB8D, 0x0000, 0x0000,
        0x0698, 0xFB8A, 0xFB8B, 0x0000, 0x0000,
        0x06A4, 0xFB6A, 0xFB6B, 0xFB6C, 0xFB6D,
        0x06A6, 0xFB6E, 0xFB6F, 0xFB70, 0xFB71,
        0x06A9, 0xFB8E, 0xFB8F, 0xFB90, 0xFB91,
        0x06AD, 0xFBD3, 0xFBD4, 0xFBD5, 0xFBD6,
        0x06AF, 0xFB92, 0xFB93, 0xFB94, 0xFB95,
        0x06B1, 0xFB9A, 0xFB9B, 0xFB9C, 0xFB9D,
        0x06B3, 0xFB96, 0xFB97, 0xFB98, 0xFB99,
        0x06BA, 0xFB9E, 0xFB9F, 0x0000, 0x0000,
        0x06BB, 0xFBA0, 0xFBA1, 0xFBA2, 0xFBA3,
        0x06BE, 0xFBAA, 0xFBAB, 0xFBAC, 0xFBAD,
        0x06C0, 0xFBA4, 0xFBA5, 0x0000, 0x0000,
        0x06C1, 0xFBA6, 0xFBA7, 0xFBA8, 0xFBA9,
        0x06C5, 0xFBE0, 0xFBE1, 0x0000, 0x0000,
        0x06C6, 0xFBD9, 0xFBDA, 0x0000, 0x0000,
        0x06C7, 0xFBD7, 0xFBD8, 0x0000, 0x0000,
        0x06C8, 0xFBDB, 0xFBDC, 0x0000, 0x0000,
        0x06C9, 0xFBE2, 0xFBE3, 0x0000, 0x0000,
        0x06CB, 0xFBDE, 0xFBDF, 0x0000, 0x0000,
        0x06CC, 0xFBFC, 0xFBFD, 0xFBFE, 0xFBFF,
        0x06D0, 0xFBE4, 0xFBE5, 0xFBE6, 0xFBE7,
        0x06D2, 0xFBAE, 0xFBAF, 0x0000, 0x0000,
        0x06D3, 0xFBB0, 0xFBB1, 0x0000, 0x0000,
    };

    static int FindForms(int cp)
    {
        var f = Forms;
        int lo = 0, hi = f.Length / 5 - 1;
        while (lo <= hi)
        {
            int mid = (lo + hi) >> 1;
            int v = f[mid * 5];
            if (v < cp) lo = mid + 1;
            else if (v > cp) hi = mid - 1;
            else return mid * 5;
        }
        return -1;
    }

    /// <summary>动作对应的表现形式码点，没有时 0。</summary>
    public static int PresentationForm(int cp, byte action)
    {
        int p = FindForms(cp);
        if (p < 0) return 0;
        var f = Forms;
        return action switch
        {
            Isol => f[p + 1],
            Fina or Fin2 or Fin3 => f[p + 2],
            Init => f[p + 3],
            Medi or Med2 => f[p + 4],
            _ => 0,
        };
    }

    /// <summary>lam + alef 变体 → 连字（孤立 / 词尾），没有时 0。</summary>
    public static int LamAlef(int alef, bool final) => alef switch
    {
        0x0622 => final ? 0xFEF6 : 0xFEF5,
        0x0623 => final ? 0xFEF8 : 0xFEF7,
        0x0625 => final ? 0xFEFA : 0xFEF9,
        0x0627 => final ? 0xFEFC : 0xFEFB,
        _ => 0,
    };
}
