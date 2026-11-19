using System;

namespace AwAUI.Unicode;

/// <summary>段落基础方向。</summary>
public enum BidiDirection : byte
{
    /// <summary>从左到右（段落层级 0）。</summary>
    LeftToRight = 0,
    /// <summary>从右到左（段落层级 1）。</summary>
    RightToLeft = 1,
    /// <summary>按 P2/P3 由第一个强字符决定；没有强字符时从左到右。</summary>
    Auto = 2,
}

/// <summary>
/// UAX #9 Unicode 双向算法：段落层级解析（P2–P3、X1–X10、W1–W7、N0–N2、I1–I2）、行内层级重置（L1）与视觉重排（L2）。
/// 层级按 UTF-16 代码单元给出（代理对两半层级相同）。被 X9 移除的字符（BN 与显式嵌入/覆盖控制符）取前一个字符的层级。
/// 临时缓冲按线程缓存复用（不超过 64K 码位的文本），稳定状态下不分配。
/// </summary>
public static class BidiAlgorithm
{
    /// <summary>最大显式嵌入深度（max_depth）。</summary>
    public const int MaxDepth = 125;

    const byte L = (byte)BidiClass.L, R = (byte)BidiClass.R, AL = (byte)BidiClass.AL, EN = (byte)BidiClass.EN;
    const byte ES = (byte)BidiClass.ES, ET = (byte)BidiClass.ET, AN = (byte)BidiClass.AN, CS = (byte)BidiClass.CS;
    const byte NSM = (byte)BidiClass.NSM, BN = (byte)BidiClass.BN, B = (byte)BidiClass.B, S = (byte)BidiClass.S;
    const byte WS = (byte)BidiClass.WS, ON = (byte)BidiClass.ON, LRE = (byte)BidiClass.LRE, LRO = (byte)BidiClass.LRO;
    const byte RLE = (byte)BidiClass.RLE, RLO = (byte)BidiClass.RLO, PDF = (byte)BidiClass.PDF, LRI = (byte)BidiClass.LRI;
    const byte RLI = (byte)BidiClass.RLI, FSI = (byte)BidiClass.FSI, PDI = (byte)BidiClass.PDI;

    static bool IsIsolateInitiator(byte t) => t == LRI || t == RLI || t == FSI;

    static bool IsRemovedByX9(byte t) => t == BN || t == LRE || t == RLE || t == LRO || t == RLO || t == PDF;

    static byte ClassOf(int cp) => (byte)(UnicodeTables.Bidi(cp) & UnicodeTables.BidiClassMask);

    /// <summary>
    /// 按 P2/P3 求段落层级：跳过隔离段，第一个强字符为 L 时返回 0，R/AL 时返回 1；没有强字符时返回 <paramref name="defaultLevel"/>。
    /// </summary>
    public static int GetParagraphLevel(ReadOnlySpan<char> text, int defaultLevel = 0)
    {
        int depth = 0;
        for (int i = 0; i < text.Length;)
        {
            int cp = CodePoint.Decode(text, i, out int len);
            i += len;
            byte t = ClassOf(cp);
            if (IsIsolateInitiator(t)) depth++;
            else if (t == PDI) { if (depth > 0) depth--; }
            else if (t == B) break;
            else if (depth == 0)
            {
                if (t == L) return 0;
                if (t == R || t == AL) return 1;
            }
        }
        return defaultLevel;
    }

    /// <summary>
    /// 解析段落的嵌入层级（至 I2，不含按行的 L1）。<paramref name="levels"/> 长度须不小于文本长度。
    /// 文本中若含段落分隔符（B），各段独立解析。返回第一段的段落层级。
    /// </summary>
    public static int ResolveLevels(ReadOnlySpan<char> text, BidiDirection direction, Span<byte> levels)
    {
        if (levels.Length < text.Length) throw new ArgumentException("levels is shorter than text.", nameof(levels));
        if (text.IsEmpty) return direction == BidiDirection.RightToLeft ? 1 : 0;
        int n = 0;
        for (int i = 0; i < text.Length; n++)
        {
            CodePoint.Decode(text, i, out int len);
            i += len;
        }
        var w = Workspace.Rent(n);
        try
        {
            int k = 0;
            for (int i = 0; i < text.Length; k++)
            {
                int cp = CodePoint.Decode(text, i, out int len);
                w.CodePoints[k] = cp;
                w.Original[k] = ClassOf(cp);
                i += len;
            }
            int firstLevel = -1;
            int start = 0;
            for (int p = 0; p < n; p++)
            {
                if (w.Original[p] != B) continue;
                // CR LF 作为一个段落分隔
                if (w.CodePoints[p] == '\r' && p + 1 < n && w.CodePoints[p + 1] == '\n') p++;
                int lvl = ResolveParagraph(w, start, p + 1, direction);
                if (firstLevel < 0) firstLevel = lvl;
                start = p + 1;
            }
            if (start < n)
            {
                int lvl = ResolveParagraph(w, start, n, direction);
                if (firstLevel < 0) firstLevel = lvl;
            }
            // 码位层级 → UTF-16 层级
            k = 0;
            for (int i = 0; i < text.Length; k++)
            {
                CodePoint.Decode(text, i, out int len);
                levels[i] = w.Levels[k];
                if (len == 2) levels[i + 1] = w.Levels[k];
                i += len;
            }
            return firstLevel;
        }
        finally
        {
            w.Return();
        }
    }

    /// <summary>
    /// L1：对一行重置层级。段分隔符（S）、段落分隔符（B），以及位于它们之前或行尾的空白/隔离控制符/被移除的格式字符序列，
    /// 层级重置为段落层级。<paramref name="line"/> 与 <paramref name="levels"/> 是同一行的文本与层级（UTF-16 对齐）。
    /// </summary>
    public static void ResetWhitespaceLevels(ReadOnlySpan<char> line, int paragraphLevel, Span<byte> levels)
    {
        if (levels.Length < line.Length) throw new ArgumentException("levels is shorter than line.", nameof(levels));
        int trailingStart = -1; // 当前空白序列的起点
        for (int i = 0; i < line.Length;)
        {
            int cp = CodePoint.Decode(line, i, out int len);
            byte t = ClassOf(cp);
            if (t == S || t == B)
            {
                int from = trailingStart >= 0 ? trailingStart : i;
                for (int j = from; j < i + len; j++) levels[j] = (byte)paragraphLevel;
                trailingStart = -1;
            }
            else if (t == WS || IsIsolateInitiator(t) || t == PDI || IsRemovedByX9(t))
            {
                if (trailingStart < 0) trailingStart = i;
            }
            else trailingStart = -1;
            i += len;
        }
        if (trailingStart >= 0)
            for (int j = trailingStart; j < line.Length; j++) levels[j] = (byte)paragraphLevel;
    }

    /// <summary>
    /// L2：由一行（或一行中各 run）的层级求视觉顺序。<paramref name="visualToLogical"/>[i] 是第 i 个视觉位置上的逻辑下标。
    /// </summary>
    public static void ReorderVisual(ReadOnlySpan<byte> levels, Span<int> visualToLogical)
    {
        int n = levels.Length;
        if (visualToLogical.Length < n) throw new ArgumentException("visualToLogical is shorter than levels.", nameof(visualToLogical));
        int max = 0, minOdd = int.MaxValue;
        for (int i = 0; i < n; i++)
        {
            visualToLogical[i] = i;
            int l = levels[i];
            if (l > max) max = l;
            if ((l & 1) != 0 && l < minOdd) minOdd = l;
        }
        for (int lvl = max; lvl >= minOdd && lvl > 0; lvl--)
        {
            int i = 0;
            while (i < n)
            {
                if (levels[visualToLogical[i]] < lvl) { i++; continue; }
                int j = i;
                while (j < n && levels[visualToLogical[j]] >= lvl) j++;
                visualToLogical.Slice(i, j - i).Reverse();
                i = j;
            }
        }
    }

    // ------------------------------------------------------------------
    // 段落解析
    // ------------------------------------------------------------------

    sealed class Workspace
    {
        public readonly int Capacity;
        public readonly int[] CodePoints;
        public readonly byte[] Original;      // 原始 Bidi_Class
        public readonly byte[] Types;         // 工作中的类别
        public readonly byte[] Levels;
        public readonly byte[] ExplicitLevels; // X1–X8 得到的层级（求 sos/eos 用，之后不再修改）
        public readonly int[] MatchingPdi;    // 隔离起始符 → 匹配的 PDI（-1 表示无）
        public readonly int[] MatchingInit;   // PDI → 匹配的隔离起始符（-1 表示无）
        public readonly int[] Kept;           // 本段落中未被 X9 移除的码位下标
        public readonly int[] KeptIndex;      // 码位下标 → 在 Kept 中的位置（被移除为 -1）
        public readonly int[] RunStartAt;     // 码位下标 → 以它开头的层级 run 编号（-1 表示不是）
        public readonly int[] RunFirst;       // run → Kept 中的起点
        public readonly int[] RunEnd;         // run → Kept 中的终点（不含）
        public readonly bool[] RunContinues;  // run 以匹配的 PDI 开头、已并入前面的隔离 run 序列
        public readonly int[] Sequence;       // 当前隔离 run 序列的码位下标
        public readonly int[] Stack;          // BD9 / BD16 的临时栈与括号对

        [ThreadStatic] static Workspace? s_cached;
        const int MaxCachedCapacity = 1 << 16;

        Workspace(int capacity)
        {
            Capacity = capacity;
            CodePoints = new int[capacity];
            Original = new byte[capacity];
            Types = new byte[capacity];
            Levels = new byte[capacity];
            ExplicitLevels = new byte[capacity];
            MatchingPdi = new int[capacity];
            MatchingInit = new int[capacity];
            Kept = new int[capacity];
            KeptIndex = new int[capacity];
            RunStartAt = new int[capacity];
            RunFirst = new int[capacity + 1];
            RunEnd = new int[capacity + 1];
            RunContinues = new bool[capacity + 1];
            Sequence = new int[capacity];
            Stack = new int[capacity + 2 * 64 + 2];
        }

        public static Workspace Rent(int n)
        {
            var w = s_cached;
            if (w != null && w.Capacity >= n)
            {
                s_cached = null;
                return w;
            }
            return new Workspace(Math.Max(n, 256));
        }

        public void Return()
        {
            if (Capacity <= MaxCachedCapacity && (s_cached == null || s_cached.Capacity < Capacity)) s_cached = this;
        }
    }

    /// <summary>解析码位区间 [start, end) 构成的一个段落，返回段落层级。</summary>
    static int ResolveParagraph(Workspace w, int start, int end, BidiDirection direction)
    {
        var orig = w.Original;
        var levels = w.Levels;

        // BD9：匹配隔离起始符与 PDI
        var stack = w.Stack;
        int sp = 0;
        for (int i = start; i < end; i++)
        {
            w.MatchingPdi[i] = -1;
            w.MatchingInit[i] = -1;
            byte t = orig[i];
            if (IsIsolateInitiator(t)) stack[sp++] = i;
            else if (t == PDI && sp > 0)
            {
                int opener = stack[--sp];
                w.MatchingPdi[opener] = i;
                w.MatchingInit[i] = opener;
            }
        }

        int paragraphLevel = direction switch
        {
            BidiDirection.LeftToRight => 0,
            BidiDirection.RightToLeft => 1,
            _ => FirstStrongLevel(w, start, end, 0),
        };

        ResolveExplicit(w, start, end, paragraphLevel);
        Array.Copy(levels, start, w.ExplicitLevels, start, end - start);

        // X9：收集未移除的字符
        int keptCount = 0;
        for (int i = start; i < end; i++)
        {
            if (IsRemovedByX9(orig[i])) { w.KeptIndex[i] = -1; continue; }
            w.KeptIndex[i] = keptCount;
            w.Kept[keptCount++] = i;
        }

        // BD7：层级 run
        int runCount = 0;
        for (int k = 0; k < keptCount;)
        {
            int lvl = levels[w.Kept[k]];
            int j = k + 1;
            while (j < keptCount && levels[w.Kept[j]] == lvl) j++;
            w.RunFirst[runCount] = k;
            w.RunEnd[runCount] = j;
            runCount++;
            k = j;
        }
        for (int i = start; i < end; i++) w.RunStartAt[i] = -1;
        for (int r = 0; r < runCount; r++) w.RunStartAt[w.Kept[w.RunFirst[r]]] = r;
        for (int r = 0; r < runCount; r++)
        {
            // 以匹配的 PDI 开头、且其隔离起始符位于上一层级 run 末尾的 run，并入起始符所在的序列
            int first = w.Kept[w.RunFirst[r]];
            int init = orig[first] == PDI ? w.MatchingInit[first] : -1;
            w.RunContinues[r] = init >= 0 && w.KeptIndex[init] >= 0 && (w.KeptIndex[init] + 1 == keptCount || levels[w.Kept[w.KeptIndex[init] + 1]] != levels[init]);
        }

        // BD13 / X10：隔离 run 序列，逐个解析
        for (int r = 0; r < runCount; r++)
        {
            if (w.RunContinues[r]) continue;
            int len = 0;
            int run = r;
            while (true)
            {
                for (int k = w.RunFirst[run]; k < w.RunEnd[run]; k++) w.Sequence[len++] = w.Kept[k];
                int last = w.Kept[w.RunEnd[run] - 1];
                if (!IsIsolateInitiator(orig[last])) break;
                int pdi = w.MatchingPdi[last];
                if (pdi < 0 || w.RunStartAt[pdi] < 0 || !w.RunContinues[w.RunStartAt[pdi]]) break;
                run = w.RunStartAt[pdi];
            }
            ResolveSequence(w, len, keptCount, paragraphLevel);
        }

        // 被 X9 移除的字符取前一个字符的层级
        int prevLevel = paragraphLevel;
        for (int i = start; i < end; i++)
        {
            if (w.KeptIndex[i] < 0) levels[i] = (byte)prevLevel;
            else prevLevel = levels[i];
        }
        return paragraphLevel;
    }

    /// <summary>P2/P3：在 [start, end) 中找第一个强字符（跳过隔离段）。</summary>
    static int FirstStrongLevel(Workspace w, int start, int end, int defaultLevel)
    {
        for (int i = start; i < end; i++)
        {
            byte t = w.Original[i];
            if (t == L) return 0;
            if (t == R || t == AL) return 1;
            if (IsIsolateInitiator(t))
            {
                int pdi = w.MatchingPdi[i];
                if (pdi < 0) break;
                i = pdi;
            }
            else if (t == B) break;
        }
        return defaultLevel;
    }

    /// <summary>X1–X8：显式层级与覆盖。</summary>
    static void ResolveExplicit(Workspace w, int start, int end, int paragraphLevel)
    {
        var orig = w.Original;
        var types = w.Types;
        var levels = w.Levels;
        // 方向状态栈：每项 = 层级 | 覆盖(0 中性, 1 L, 2 R) << 8 | 隔离 << 10
        Span<int> stack = stackalloc int[MaxDepth + 2];
        int depth = 0;
        stack[depth++] = paragraphLevel;
        int overflowIsolates = 0, overflowEmbeddings = 0, validIsolates = 0;

        for (int i = start; i < end; i++)
        {
            byte t = orig[i];
            types[i] = t;
            int top = stack[depth - 1];
            int topLevel = top & 0xFF;
            int topOverride = (top >> 8) & 3;
            switch (t)
            {
                case RLE:
                case LRE:
                case RLO:
                case LRO:
                {
                    bool rtl = t == RLE || t == RLO;
                    int newLevel = rtl ? (topLevel + 1) | 1 : (topLevel + 2) & ~1;
                    levels[i] = (byte)topLevel;
                    if (newLevel <= MaxDepth && overflowIsolates == 0 && overflowEmbeddings == 0)
                    {
                        int ovr = t == RLO ? 2 : t == LRO ? 1 : 0;
                        stack[depth++] = newLevel | (ovr << 8);
                    }
                    else if (overflowIsolates == 0) overflowEmbeddings++;
                    break;
                }
                case RLI:
                case LRI:
                case FSI:
                {
                    levels[i] = (byte)topLevel;
                    if (topOverride != 0) types[i] = topOverride == 1 ? L : R;
                    bool rtl = t == RLI;
                    if (t == FSI)
                    {
                        int pdi = w.MatchingPdi[i];
                        rtl = FirstStrongLevel(w, i + 1, pdi < 0 ? end : pdi, 0) == 1;
                    }
                    int newLevel = rtl ? (topLevel + 1) | 1 : (topLevel + 2) & ~1;
                    if (newLevel <= MaxDepth && overflowIsolates == 0 && overflowEmbeddings == 0)
                    {
                        validIsolates++;
                        stack[depth++] = newLevel | (1 << 10);
                    }
                    else overflowIsolates++;
                    break;
                }
                case PDI:
                {
                    if (overflowIsolates > 0) overflowIsolates--;
                    else if (validIsolates > 0)
                    {
                        overflowEmbeddings = 0;
                        while ((stack[depth - 1] & (1 << 10)) == 0) depth--;
                        depth--;
                        validIsolates--;
                    }
                    top = stack[depth - 1];
                    levels[i] = (byte)(top & 0xFF);
                    int ovr = (top >> 8) & 3;
                    if (ovr != 0) types[i] = ovr == 1 ? L : R;
                    break;
                }
                case PDF:
                {
                    levels[i] = (byte)topLevel;
                    if (overflowIsolates > 0) { }
                    else if (overflowEmbeddings > 0) overflowEmbeddings--;
                    else if ((top & (1 << 10)) == 0 && depth >= 2) depth--;
                    break;
                }
                case B:
                    levels[i] = (byte)paragraphLevel;
                    break;
                case BN:
                    levels[i] = (byte)topLevel;
                    break;
                default:
                    levels[i] = (byte)topLevel;
                    if (topOverride != 0) types[i] = topOverride == 1 ? L : R;
                    break;
            }
        }
    }

    static byte StrongDirection(byte t) => t == L ? L : (t == R || t == AL || t == EN || t == AN) ? R : ON;

    static bool IsNeutralOrIsolate(byte t) => t == B || t == S || t == WS || t == ON || t == LRI || t == RLI || t == FSI || t == PDI;

    /// <summary>对一个隔离 run 序列应用 W1–W7、N0–N2、I1–I2。序列存放在 w.Sequence[0..len)。</summary>
    static void ResolveSequence(Workspace w, int len, int keptCount, int paragraphLevel)
    {
        var seq = w.Sequence;
        var types = w.Types;
        var levels = w.Levels;
        var orig = w.Original;
        int first = seq[0], last = seq[len - 1];
        int level = levels[first];

        // sos / eos（使用 X1–X8 的层级）
        var explicitLevels = w.ExplicitLevels;
        int firstKept = w.KeptIndex[first];
        int prevLevel = firstKept > 0 ? explicitLevels[w.Kept[firstKept - 1]] : paragraphLevel;
        byte sos = (Math.Max(prevLevel, level) & 1) != 0 ? R : L;
        int nextLevel = paragraphLevel;
        int lastKept = w.KeptIndex[last];
        if (!IsIsolateInitiator(orig[last]) && lastKept + 1 < keptCount) nextLevel = explicitLevels[w.Kept[lastKept + 1]];
        byte eos = (Math.Max(nextLevel, level) & 1) != 0 ? R : L;
        byte embedding = (level & 1) != 0 ? R : L;

        // W1
        byte prevType = sos;
        for (int k = 0; k < len; k++)
        {
            int p = seq[k];
            if (types[p] == NSM) types[p] = prevType;
            byte t = types[p];
            prevType = IsIsolateInitiator(t) || t == PDI ? ON : t;
        }
        // W2 + W3
        byte lastStrong = sos;
        for (int k = 0; k < len; k++)
        {
            int p = seq[k];
            byte t = types[p];
            if (t == EN) { if (lastStrong == AL) types[p] = AN; }
            else if (t == L || t == R) lastStrong = t;
            else if (t == AL) { lastStrong = AL; types[p] = R; }
        }
        // W4
        for (int k = 1; k + 1 < len; k++)
        {
            int p = seq[k];
            byte t = types[p];
            if (t != ES && t != CS) continue;
            byte before = types[seq[k - 1]], after = types[seq[k + 1]];
            if (t == ES && before == EN && after == EN) types[p] = EN;
            else if (t == CS && before == after && (before == EN || before == AN)) types[p] = before;
        }
        // W5
        for (int k = 0; k < len;)
        {
            if (types[seq[k]] != ET) { k++; continue; }
            int j = k;
            while (j < len && types[seq[j]] == ET) j++;
            bool adjacentEn = (k > 0 && types[seq[k - 1]] == EN) || (j < len && types[seq[j]] == EN);
            if (adjacentEn)
                for (int m = k; m < j; m++) types[seq[m]] = EN;
            k = j;
        }
        // W6 + W7
        lastStrong = sos;
        for (int k = 0; k < len; k++)
        {
            int p = seq[k];
            byte t = types[p];
            if (t == ES || t == ET || t == CS) types[p] = ON;
            else if (t == EN) { if (lastStrong == L) types[p] = L; }
            else if (t == L || t == R) lastStrong = t;
        }

        // N0
        ResolveBrackets(w, len, sos, embedding);

        // N1 + N2
        for (int k = 0; k < len;)
        {
            if (!IsNeutralOrIsolate(types[seq[k]])) { k++; continue; }
            int j = k;
            while (j < len && IsNeutralOrIsolate(types[seq[j]])) j++;
            byte leading = k == 0 ? sos : StrongDirection(types[seq[k - 1]]);
            byte trailing = j == len ? eos : StrongDirection(types[seq[j]]);
            byte resolved = leading == trailing && leading != ON ? leading : embedding;
            for (int m = k; m < j; m++) types[seq[m]] = resolved;
            k = j;
        }

        // I1 + I2
        for (int k = 0; k < len; k++)
        {
            int p = seq[k];
            byte t = types[p];
            int lv = levels[p];
            if ((lv & 1) == 0)
            {
                if (t == R) lv += 1;
                else if (t == AN || t == EN) lv += 2;
            }
            else if (t == L || t == EN || t == AN) lv += 1;
            levels[p] = (byte)lv;
        }
    }

    /// <summary>N0：成对括号（BD16 配对，按开括号位置顺序处理）。</summary>
    static void ResolveBrackets(Workspace w, int len, byte sos, byte embedding)
    {
        var seq = w.Sequence;
        var types = w.Types;
        var cps = w.CodePoints;
        // 栈：每项两个 int（配对括号的规范化码位, 序列下标）；配对结果放在栈数组的后半部分
        var stack = w.Stack;
        const int StackLimit = 63;
        int pairBase = 2 * StackLimit + 2;
        int sp = 0, pairCount = 0;
        int pairCapacity = (stack.Length - pairBase) / 2;
        for (int k = 0; k < len; k++)
        {
            int p = seq[k];
            if (types[p] != ON) continue;
            var bpt = UnicodeData.GetBidiPairedBracketType(cps[p]);
            if (bpt == BidiPairedBracketType.Open)
            {
                if (sp == StackLimit) { pairCount = 0; break; }
                stack[sp * 2] = Canonical(UnicodeData.GetBidiPairedBracket(cps[p]));
                stack[sp * 2 + 1] = k;
                sp++;
            }
            else if (bpt == BidiPairedBracketType.Close)
            {
                int c = Canonical(cps[p]);
                for (int e = sp - 1; e >= 0; e--)
                {
                    if (stack[e * 2] != c) continue;
                    if (pairCount < pairCapacity)
                    {
                        // 按开括号位置插入排序
                        int open = stack[e * 2 + 1];
                        int pos = pairCount;
                        while (pos > 0 && stack[pairBase + (pos - 1) * 2] > open)
                        {
                            stack[pairBase + pos * 2] = stack[pairBase + (pos - 1) * 2];
                            stack[pairBase + pos * 2 + 1] = stack[pairBase + (pos - 1) * 2 + 1];
                            pos--;
                        }
                        stack[pairBase + pos * 2] = open;
                        stack[pairBase + pos * 2 + 1] = k;
                        pairCount++;
                    }
                    sp = e;
                    break;
                }
            }
        }

        byte opposite = embedding == L ? R : L;
        for (int i = 0; i < pairCount; i++)
        {
            int open = stack[pairBase + i * 2], close = stack[pairBase + i * 2 + 1];
            bool foundEmbedding = false, foundOpposite = false;
            for (int k = open + 1; k < close; k++)
            {
                byte d = StrongDirection(types[seq[k]]);
                if (d == embedding) { foundEmbedding = true; break; }
                if (d == opposite) foundOpposite = true;
            }
            byte resolved;
            if (foundEmbedding) resolved = embedding;
            else if (foundOpposite)
            {
                byte context = sos;
                for (int k = open - 1; k >= 0; k--)
                {
                    byte d = StrongDirection(types[seq[k]]);
                    if (d != ON) { context = d; break; }
                }
                resolved = context == opposite ? opposite : embedding;
            }
            else continue;
            SetBracket(w, len, open, resolved);
            SetBracket(w, len, close, resolved);
        }
    }

    static void SetBracket(Workspace w, int len, int k, byte type)
    {
        var seq = w.Sequence;
        w.Types[seq[k]] = type;
        // 紧随其后、原始类别为 NSM 的字符随括号改变
        for (int m = k + 1; m < len && w.Original[seq[m]] == NSM; m++) w.Types[seq[m]] = type;
    }

    static int Canonical(int cp) => cp == 0x2329 ? 0x3008 : cp == 0x232A ? 0x3009 : cp;
}
