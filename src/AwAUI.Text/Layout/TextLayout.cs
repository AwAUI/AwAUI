using System;
using System.Collections.Generic;
using AwAUI.Graphics;
using AwAUI.Graphics.Text;
using AwAUI.Unicode;

namespace AwAUI.Text;

/// <summary>
/// 段落排版结果（design/19「段落布局」）。由 <see cref="ParagraphBuilder.Build"/> 创建：此时已完成分段（双向层级、文字、样式、字体回退）、
/// 整形（词级缓存）与断行机会的计算；<see cref="Layout"/> 只做断行、行内重排、对齐与截断，可用不同宽度反复调用而不重新整形。
/// <para>
/// 文本偏移一律是 UTF-16 索引（相对于构建时追加的文本），返回的位置落在字素簇边界上。坐标为逻辑像素，原点在布局盒左上角。
/// 布局（<see cref="Layout"/>）在 UI 线程进行；完成后的对象可在其他线程只读使用（绘制、命中测试）。
/// </para>
/// </summary>
public sealed partial class TextLayout
{
    const char Zwsp = '\u200B';
    const float Epsilon = 1e-3f;

    [Flags]
    enum CharFlags : byte
    {
        None = 0,
        /// <summary>被空白折叠移除（工作文本中替换为 ZWSP，零宽、不绘制）。</summary>
        Removed = 1,
        Placeholder = 2,
    }

    [Flags]
    enum UnitFlags : byte
    {
        None = 0,
        /// <summary>零宽且不加字距：折叠掉的空白、控制字符、强制换行符。</summary>
        Removed = 1,
        /// <summary>以强制换行符结束。</summary>
        HardBreak = 2,
        /// <summary>词分隔符（加词间距，两端对齐的扩展点）。</summary>
        Space = 4,
        /// <summary>位于行尾时悬挂（不计入行宽）。</summary>
        Hang = 8,
        Placeholder = 16,
        /// <summary>东亚宽字符（两端对齐时字符之间也可扩展）。</summary>
        Cjk = 32,
    }

    sealed class StyleInfo
    {
        public TextStyle Style = null!;
        public FontQuery Query;
        public FontFace Primary = null!;
        public Paint Paint = null!;
        public float Size;
        public float LetterSpacing;
        public float WordSpacing;
        public LineHeight LineHeight;
        public FontFeature[]? Features;
    }

    struct Item
    {
        public int Start, End;
        public byte Level;
        public Script Script;
        public int Style;
        public FontFace? Face;
        public GlyphRunFlags Synthetic;
        public int Placeholder;     // -1：文本
        public float Above, Below;  // 含半行距
    }

    struct Piece
    {
        public int Start, End, Item;
        public ShapedRun? Run;
    }

    struct Unit
    {
        public int Start, End, Piece, GlyphStart, GlyphCount;
        public float Advance;
        public UnitFlags Flags;
    }

    struct Segment
    {
        public int UnitStart, UnitEnd, HangStart;
        public float Width, Hang;
        public bool Mandatory;
    }

    readonly TextEngine _engine;
    readonly ParagraphStyle _style;
    readonly string _source;
    readonly char[] _text;          // 工作文本：大小写变换与空白折叠之后（长度与原文相同）
    readonly char[] _shapeText;     // 整形文本：控制符 → ZWSP、制表符 → 空格、RTL 中的镜像字符
    readonly byte[] _levels;
    readonly int[] _paraStarts;
    readonly byte[] _paraLevels;
    readonly StyleInfo[] _styles;
    readonly StyleInfo _strutStyle;
    readonly float _strutAbove, _strutBelow, _strutXHeight;
    readonly PlaceholderRun[] _placeholders;
    readonly byte[] _breaks;        // 每个位置之前：0 不可断，1 可断，2 强制
    readonly Item[] _items;
    readonly Piece[] _pieces;
    readonly Unit[] _units;
    readonly int[] _unitOfChar;
    readonly Segment[] _segments;

    internal TextLayout(TextEngine engine, ParagraphStyle style, string text, StyleSpan[] spans, PlaceholderRun[] placeholders)
    {
        _engine = engine;
        _style = style;
        _source = text;
        _placeholders = placeholders;
        _spanList = spans;
        int n = text.Length;
        _text = text.ToCharArray();

        // 样式表（按对象引用去重）
        var styleIndex = new Dictionary<TextStyle, int>(ReferenceEqualityComparer<TextStyle>.Instance);
        var styles = new List<StyleInfo>();
        _strutStyle = CreateStyleInfo(style.TextStyle);
        var spanStyles = new int[spans.Length];
        for (int i = 0; i < spans.Length; i++)
        {
            if (!styleIndex.TryGetValue(spans[i].Style, out int idx))
            {
                idx = styles.Count;
                styles.Add(ReferenceEquals(spans[i].Style, style.TextStyle) ? _strutStyle : CreateStyleInfo(spans[i].Style));
                styleIndex.Add(spans[i].Style, idx);
            }
            spanStyles[i] = idx;
        }
        if (styles.Count == 0) styles.Add(_strutStyle);
        _styles = styles.ToArray();
        var charStyle = new int[n];
        for (int i = 0; i < spans.Length; i++)
            for (int j = spans[i].Start; j < spans[i].End; j++) charStyle[j] = spanStyles[i];

        ComputeVerticalMetrics(_strutStyle, _strutStyle.Primary, out _strutAbove, out _strutBelow);
        _strutXHeight = _strutStyle.Primary.Metrics.XHeight * _strutStyle.Primary.Metrics.GetScale(_strutStyle.Size);

        var flags = new CharFlags[n];
        foreach (var p in placeholders) flags[p.Offset] |= CharFlags.Placeholder;

        ApplyTextTransform(charStyle, flags);
        ProcessWhiteSpace(flags);

        var graphemes = ComputeGraphemes();
        _breaks = ComputeBreaks(graphemes, flags);
        _levels = new byte[n];
        ResolveBidi(out _paraStarts, out _paraLevels);
        _shapeText = BuildShapeText(flags);
        _items = Itemize(graphemes, charStyle, flags);
        _unitOfChar = new int[n];
        BuildPiecesAndUnits(graphemes, out _pieces, out _units);
        _segments = BuildSegments();
    }

    /// <summary>构建时追加的原文。</summary>
    public string Text => _source;

    public ParagraphStyle Style => _style;

    /// <summary>占位盒数量。</summary>
    public int PlaceholderCount => _placeholders.Length;

    readonly StyleSpan[] _spanList;

    /// <summary>
    /// 偏移 <paramref name="offset"/> 处字符的文本样式（构建时 <see cref="ParagraphBuilder.PushStyle"/> 给出的实例）；
    /// 不在任何样式片段内（或越界）时为段落的默认样式 <see cref="ParagraphStyle.TextStyle"/>。
    /// </summary>
    public TextStyle GetStyleAt(int offset)
    {
        foreach (var span in _spanList)
            if (offset >= span.Start && offset < span.End) return span.Style;
        return _style.TextStyle;
    }

    // ------------------------------------------------------------------ 样式

    StyleInfo CreateStyleInfo(TextStyle s)
    {
        var families = s.FontFamilies is { Count: > 0 } f ? f : _engine.DefaultFontFamilies;
        var query = new FontQuery(families, s.FontWeight, s.FontStyle, s.FontStretch);
        var fonts = _engine.Fonts;
        var primary = fonts.Match(query);
        if (primary is null)
        {
            primary = fonts.Match(new FontQuery(_engine.DefaultFontFamilies, s.FontWeight, s.FontStyle, s.FontStretch));
            if (primary is null)
            {
                var names = fonts.FamilyNames;
                if (names.Count > 0) primary = fonts.Match(new FontQuery(names[0], s.FontWeight, s.FontStyle, s.FontStretch));
            }
        }
        float letterSpacing = TextStyle.Sanitize(s.LetterSpacing, -TextStyle.MaxFontSize, TextStyle.MaxFontSize);
        // CSS Text 3 §8.2：字母间距不为 0 时不应用可选连字（与浏览器相同，关闭 liga / clig / dlig / calt）；
        // 否则连字画成一个字形而它的各个字素单元分别加上间距。放在样式特性之前，显式设置的特性仍然优先（后出现的覆盖先出现的）
        int implicitCount = letterSpacing != 0 ? s_noOptionalLigatures.Length : 0;
        int explicitCount = s.Features?.Count ?? 0;
        FontFeature[]? features = null;
        if (implicitCount + explicitCount > 0)
        {
            features = new FontFeature[implicitCount + explicitCount];
            for (int i = 0; i < implicitCount; i++) features[i] = s_noOptionalLigatures[i];
            for (int i = 0; i < explicitCount; i++) features[implicitCount + i] = new FontFeature(s.Features![i].Tag, s.Features[i].Value);
        }
        return new StyleInfo
        {
            Style = s,
            Query = query,
            Primary = primary ?? NullFontFace.Instance,
            Paint = s.Foreground is { } brush ? new Paint(brush) : new Paint(s.Color),
            Size = s.EffectiveFontSize,
            LetterSpacing = letterSpacing,
            WordSpacing = TextStyle.Sanitize(s.WordSpacing, -TextStyle.MaxFontSize, TextStyle.MaxFontSize),
            LineHeight = s.LineHeight ?? _style.LineHeight,
            Features = features,
        };
    }

    static readonly FontFeature[] s_noOptionalLigatures =
    {
        new("liga", 0), new("clig", 0), new("dlig", 0), new("calt", 0),
    };

    /// <summary>CSS 行内盒的竖直范围：上升 + 下降 + 行间距（normal），或指定行高，按半行距分配到上下。</summary>
    static void ComputeVerticalMetrics(StyleInfo style, FontFace face, out float above, out float below)
    {
        var m = face.Metrics;
        float scale = m.GetScale(style.Size);
        float a = m.Ascender * scale, d = -m.Descender * scale, gap = Math.Max(0, m.LineGap) * scale;
        float lh = style.LineHeight.Resolve(style.Size, a + d + gap);
        float half = (lh - (a + d)) / 2;
        above = a + half;
        below = d + half;
    }

    // ------------------------------------------------------------------ 大小写变换

    void ApplyTextTransform(int[] charStyle, CharFlags[] flags)
    {
        bool any = false, capitalize = false;
        foreach (var s in _styles)
        {
            any |= s.Style.TextTransform != TextTransform.None;
            capitalize |= s.Style.TextTransform == TextTransform.Capitalize;
        }
        if (!any) return;
        var t = _text;
        for (int i = 0; i < t.Length;)
        {
            int cp = CodePoint.Decode(t, i, out int len);
            var tr = _styles[charStyle[i]].Style.TextTransform;
            if ((flags[i] & CharFlags.Placeholder) == 0 && (tr == TextTransform.Uppercase || tr == TextTransform.Lowercase))
                Replace(i, len, tr == TextTransform.Uppercase ? UnicodeData.ToUpper(cp) : UnicodeData.ToLower(cp));
            i += len;
        }
        if (!capitalize) return;
        var words = TextSegmenter.EnumerateWords(_source.AsSpan());
        while (words.MoveNext())
        {
            var w = words.Current;
            if (!w.IsWordLike) continue;
            for (int i = w.Start; i < w.End;)
            {
                int cp = CodePoint.Decode(t, i, out int len);
                if (IsLetter(UnicodeData.GetGeneralCategory(cp)))
                {
                    if (_styles[charStyle[i]].Style.TextTransform == TextTransform.Capitalize) Replace(i, len, UnicodeData.ToTitle(cp));
                    break;
                }
                i += len;
            }
        }

        void Replace(int index, int length, int mapped)
        {
            // 只做不改变 UTF-16 长度的映射，保证偏移与原文一一对应
            if (CodePoint.Utf16Length(mapped) == length) CodePoint.Encode(mapped, t.AsSpan(index, length));
        }
    }

    static bool IsLetter(GeneralCategory gc) => gc <= GeneralCategory.OtherLetter;

    static bool IsLetterOrNumber(GeneralCategory gc) => gc <= GeneralCategory.OtherLetter || (gc >= GeneralCategory.DecimalNumber && gc <= GeneralCategory.OtherNumber);

    // ------------------------------------------------------------------ 空白处理（CSS Text 3 §4.1.1 第一阶段）

    static bool IsSegmentBreak(char c) => c == '\n' || c == '\r';

    void ProcessWhiteSpace(CharFlags[] flags)
    {
        if (!_style.CollapsesSpaces) return;
        var t = _text;
        int n = t.Length;
        bool preserveNewlines = _style.PreservesNewlines;
        for (int i = 0; i < n; i++)
            if (t[i] == '\t') t[i] = ' ';

        // 1. 换行符前后的空格移除
        for (int i = 0; i < n; i++)
        {
            if (!IsSegmentBreak(t[i])) continue;
            for (int j = i - 1; j >= 0 && t[j] == ' '; j--) flags[j] |= CharFlags.Removed;
            int k = i + 1;
            for (; k < n && t[k] == ' '; k++) flags[k] |= CharFlags.Removed;
        }

        // 2. normal / nowrap：连续的换行折叠为一个，再转换为空格（两侧都是东亚宽字符时移除）
        if (!preserveNewlines)
        {
            int prevKept = -1;          // 上一个未移除的字符
            for (int i = 0; i < n; i++)
            {
                if ((flags[i] & CharFlags.Removed) != 0) continue;
                if (IsSegmentBreak(t[i]))
                {
                    if (prevKept >= 0 && IsSegmentBreak(_source[prevKept]))
                        flags[i] |= CharFlags.Removed;
                    else
                    {
                        int next = i + 1;
                        while (next < n && ((flags[next] & CharFlags.Removed) != 0 || IsSegmentBreak(t[next]))) next++;
                        if (prevKept >= 0 && next < n && IsEastAsianNonHangul(CodePoint.DecodeBefore(t, prevKept + 1, out _))
                            && IsEastAsianNonHangul(CodePoint.Decode(t, next)))
                            flags[i] |= CharFlags.Removed;
                        else
                        {
                            t[i] = ' ';
                            prevKept = i;
                        }
                    }
                    continue;
                }
                prevKept = i;
            }
        }

        // 3. 连续的可折叠空格只保留第一个；段落开头（以及强制换行之后）的空格移除
        bool afterSpace = true;
        for (int i = 0; i < n; i++)
        {
            if ((flags[i] & CharFlags.Removed) != 0) continue;
            char c = t[i];
            if (c == ' ' && (flags[i] & CharFlags.Placeholder) == 0)
            {
                if (afterSpace) flags[i] |= CharFlags.Removed;
                afterSpace = true;
            }
            else afterSpace = c < 0x80 ? c is '\n' or '\r' or '\u000B' or '\u000C' : IsHardBreakChar(c);
        }

        for (int i = 0; i < n; i++)
            if ((flags[i] & CharFlags.Removed) != 0) t[i] = Zwsp;
    }

    static bool IsEastAsianNonHangul(int cp)
    {
        var w = UnicodeData.GetEastAsianWidth(cp);
        if (w != EastAsianWidth.Wide && w != EastAsianWidth.Fullwidth && w != EastAsianWidth.Halfwidth) return false;
        return UnicodeData.GetScript(cp) != Script.Hangul;
    }

    /// <summary>UAX #14 的强制换行类别 BK / CR / LF / NL。</summary>
    static bool IsHardBreakChar(int cp)
    {
        var c = UnicodeData.GetLineBreakClass(cp);
        return c == LineBreakClass.BK || c == LineBreakClass.CR || c == LineBreakClass.LF || c == LineBreakClass.NL;
    }

    // ------------------------------------------------------------------ 字素簇与断行机会

    /// <summary>
    /// 字素簇边界（升序，首项 0，末项为文本长度；空文本只有 0）。
    /// 占位盒是原子行内盒：它的前后总是边界（否则前置字符 Prepend 或其后的组合标记会把 U+FFFC 并入文本簇，占位盒随之丢失）。
    /// </summary>
    int[] ComputeGraphemes()
    {
        var list = new List<int>(_text.Length + 1);
        var e = TextSegmenter.EnumerateGraphemes(_text);
        while (e.MoveNext()) list.Add(e.Current.Start);
        list.Add(_text.Length);
        if (_placeholders.Length == 0) return list.ToArray();

        // 与占位盒两侧的边界（同样升序）归并去重
        var merged = new List<int>(list.Count + 2 * _placeholders.Length);
        int i = 0, k = 0, extra = 2 * _placeholders.Length;
        while (i < list.Count || k < extra)
        {
            int v;
            if (k >= extra) v = list[i++];
            else
            {
                int pv = _placeholders[k >> 1].Offset + (k & 1);
                if (i < list.Count && list[i] <= pv) v = list[i++];
                else
                {
                    v = pv;
                    k++;
                }
            }
            if (merged.Count == 0 || merged[merged.Count - 1] < v) merged.Add(v);
        }
        return merged.ToArray();
    }

    /// <summary>
    /// 每个位置之前的断行机会（0 不可断，1 可断，2 强制）。可选断点在去掉折叠字符（工作文本中的 ZWSP）的文本上计算再映射回原文：
    /// 否则 ZWSP（类别 ZW）会在原本不可断开处制造断点，例如东亚字符之间被移除的换行符后面紧跟避头标点「。」或 keep-all 的表意文字。
    /// </summary>
    byte[] ComputeBreaks(int[] graphemes, CharFlags[] flags)
    {
        var t = _text;
        int n = t.Length;
        var raw = new byte[n + 1];
        int removedCount = 0;
        for (int i = 0; i < n; i++)
            if ((flags[i] & CharFlags.Removed) != 0) removedCount++;
        // 强制断点总是取自原文（它们只出现在保留下来的换行符之后，折叠字符不影响）
        var e = TextSegmenter.EnumerateLineBreaks(t, _style.LineBreak);
        while (e.MoveNext())
        {
            var o = e.Current;
            if (o.IsMandatory) raw[o.Position] = 2;
            else if (removedCount == 0) raw[o.Position] = 1;
        }
        if (removedCount > 0)
        {
            // 可选断点取自压缩文本。折叠字符都是 BMP 的空白 / 换行，删去它们不会拆开代理对
            int m = n - removedCount;
            var kept = new char[m];
            var map = new int[m];   // 压缩文本中的位置 → 原文位置：断点落在下一个保留字符之前，折叠字符归上一行
            for (int i = 0, j = 0; i < n; i++)
            {
                if ((flags[i] & CharFlags.Removed) != 0) continue;
                kept[j] = t[i];
                map[j++] = i;
            }
            var ek = TextSegmenter.EnumerateLineBreaks(kept, _style.LineBreak);
            while (ek.MoveNext())
            {
                var o = ek.Current;
                if (!o.IsMandatory && o.Position < m && raw[map[o.Position]] == 0) raw[map[o.Position]] = 1;
            }
        }

        var breaks = new byte[n + 1];
        // 只保留字素簇边界上的机会（强制断点若落在簇内则推迟到簇尾）
        int gi = 0;
        byte pendingMandatory = 0;
        for (int p = 1; p <= n; p++)
        {
            while (gi < graphemes.Length && graphemes[gi] < p) gi++;
            bool boundary = gi < graphemes.Length && graphemes[gi] == p;
            if (raw[p] == 2) pendingMandatory = 2;
            if (!boundary) continue;
            breaks[p] = Math.Max(raw[p], pendingMandatory);
            pendingMandatory = 0;
        }

        var wb = _style.WordBreak;
        bool breakSpaces = _style.WhiteSpace == WhiteSpace.BreakSpaces;
        if (wb != WordBreak.Normal || breakSpaces)
        {
            // before：p 之前最后一个未折叠的码点（按位置递增维护，整体线性）
            int before = -1, scanned = 0;
            for (int g = 1; g < graphemes.Length - 1; g++)
            {
                int p = graphemes[g];
                while (scanned < p)
                {
                    int cp = CodePoint.Decode(t, scanned, out int len);
                    if ((flags[scanned] & CharFlags.Removed) == 0) before = cp;
                    scanned += len;
                }
                // 折叠字符之前不是断点（断点已映射到其后的保留字符之前）
                if (breaks[p] == 2 || before < 0 || (flags[p] & CharFlags.Removed) != 0) continue;
                int after = CodePoint.Decode(t, p);
                if (breakSpaces && IsHangableSpace(before))
                {
                    breaks[p] = 1;
                    continue;
                }
                bool letters = IsLetterOrNumber(UnicodeData.GetGeneralCategory(before)) && IsLetterOrNumber(UnicodeData.GetGeneralCategory(after));
                if (wb == WordBreak.BreakAll && letters) breaks[p] = 1;
                else if (wb == WordBreak.KeepAll && letters) breaks[p] = 0;
            }
        }
        return breaks;
    }

    // ------------------------------------------------------------------ 双向

    void ResolveBidi(out int[] paraStarts, out byte[] paraLevels)
    {
        var t = _text;
        int n = t.Length;
        var direction = _style.Direction switch
        {
            ParagraphDirection.RightToLeft => BidiDirection.RightToLeft,
            ParagraphDirection.LeftToRight => BidiDirection.LeftToRight,
            _ => BidiDirection.Auto,
        };
        bool needed = direction == BidiDirection.RightToLeft;
        for (int i = 0; i < n && !needed;)
        {
            int cp = CodePoint.Decode(t, i, out int len);
            i += len;
            if (cp < 0x0590) continue;   // 拉丁、希腊、西里尔等都是强 L 或中性
            switch (UnicodeData.GetBidiClass(cp))
            {
                case BidiClass.R:
                case BidiClass.AL:
                case BidiClass.AN:
                case BidiClass.RLE:
                case BidiClass.RLO:
                case BidiClass.RLI:
                case BidiClass.LRE:
                case BidiClass.LRO:
                case BidiClass.LRI:
                case BidiClass.FSI:
                    needed = true;
                    break;
            }
        }
        if (needed) BidiAlgorithm.ResolveLevels(t, direction, _levels);

        // 双向段落：以段落分隔符（B）结束
        var starts = new List<int> { 0 };
        for (int i = 0; i < n;)
        {
            int cp = CodePoint.Decode(t, i, out int len);
            i += len;
            if (cp < 0x80 ? cp is not ('\n' or '\r' or 0x1C or 0x1D or 0x1E) : UnicodeData.GetBidiClass(cp) != BidiClass.B) continue;
            if (cp == '\r' && i < n && t[i] == '\n') i++;
            if (i < n) starts.Add(i);
        }
        paraStarts = starts.ToArray();
        paraLevels = new byte[paraStarts.Length];
        for (int p = 0; p < paraStarts.Length; p++)
        {
            paraLevels[p] = direction switch
            {
                BidiDirection.RightToLeft => 1,
                BidiDirection.LeftToRight => 0,
                _ when !needed => 0,
                _ => (byte)BidiAlgorithm.GetParagraphLevel(
                    t.AsSpan(paraStarts[p], (p + 1 < paraStarts.Length ? paraStarts[p + 1] : n) - paraStarts[p]), 0),
            };
        }
    }

    byte ParagraphLevelAt(int offset)
    {
        int i = Array.BinarySearch(_paraStarts, offset);
        if (i < 0) i = ~i - 1;
        return _paraLevels[Math.Max(0, i)];
    }

    /// <summary>段落基础方向是否为从右到左（第一个双向段落）。</summary>
    public bool IsRightToLeft => _paraLevels[0] == 1;

    // ------------------------------------------------------------------ 整形文本

    char[] BuildShapeText(CharFlags[] flags)
    {
        var s = (char[])_text.Clone();
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (c == '\t') s[i] = ' ';
            else if (c < 0x20 || (c >= 0x7F && c <= 0x9F) || c == '\u2028' || c == '\u2029') s[i] = Zwsp;
            else if ((_levels[i] & 1) != 0 && (flags[i] & CharFlags.Placeholder) == 0 && !char.IsSurrogate(c) && UnicodeData.IsBidiMirrored(c))
            {
                // UAX #9 L4：从右到左的镜像字符
                int m = UnicodeData.GetBidiMirroringGlyph(c);
                if (m < 0x10000 && !char.IsSurrogate((char)m)) s[i] = (char)m;
            }
        }
        return s;
    }

    // ------------------------------------------------------------------ 分段

    Item[] Itemize(int[] graphemes, int[] charStyle, CharFlags[] flags)
    {
        var items = new List<Item>();
        var t = _text;
        int placeholderCursor = 0;
        Item cur = default;
        bool open = false;
        FontFace? prevFace = null;
        int prevStyle = -1;
        var memo = new CoverageMemo();
        for (int g = 0; g + 1 < graphemes.Length; g++)
        {
            int s = graphemes[g], e = graphemes[g + 1];
            int style = charStyle[s];
            byte level = _levels[s];
            if ((flags[s] & CharFlags.Placeholder) != 0)
            {
                while (placeholderCursor < _placeholders.Length && _placeholders[placeholderCursor].Offset < s) placeholderCursor++;
                if (open) items.Add(cur);
                items.Add(new Item { Start = s, End = e, Level = level, Script = Script.Common, Style = style, Placeholder = placeholderCursor });
                open = false;
                continue;
            }

            var info = _styles[style];
            var clusterScript = ClusterScript(t, s, e, out int firstReal);
            var face = ChooseFace(t, s, e, style, info, style == prevStyle ? prevFace : null, memo);
            prevFace = face;
            prevStyle = style;

            bool split = !open || cur.Style != style || cur.Level != level || !ReferenceEquals(cur.Face, face);
            if (!split && clusterScript != Script.Common)
            {
                if (cur.Script == Script.Common) cur.Script = clusterScript;
                else if (cur.Script != clusterScript && !UnicodeData.HasScript(firstReal, cur.Script)) split = true;
            }
            if (split)
            {
                if (open) items.Add(cur);
                cur = new Item { Start = s, End = e, Level = level, Script = clusterScript, Style = style, Face = face, Placeholder = -1 };
                open = true;
            }
            else cur.End = e;
        }
        if (open) items.Add(cur);

        var array = items.ToArray();
        for (int i = 0; i < array.Length; i++)
        {
            ref var it = ref array[i];
            if (it.Placeholder >= 0) continue;
            var info = _styles[it.Style];
            ComputeVerticalMetrics(info, it.Face!, out it.Above, out it.Below);
            // vertical-align 的基线偏移：行内盒整体上移 / 下移，行盒的上、下延伸随之变化
            float shift = TextStyle.Sanitize(info.Style.BaselineShift, -TextStyle.MaxFontSize, TextStyle.MaxFontSize);
            if (shift != 0)
            {
                it.Above += shift;
                it.Below -= shift;
            }
            var d = it.Face!.Descriptor;
            if (info.Style.FontWeight.Value >= 600 && d.Weight.Value < 600) it.Synthetic |= GlyphRunFlags.SyntheticBold;
            if (info.Style.FontStyle != FontStyle.Normal && d.Style == FontStyle.Normal) it.Synthetic |= GlyphRunFlags.SyntheticOblique;
        }
        return array;
    }

    /// <summary>簇的文字：第一个不是 Common / Inherited / Unknown 的码点的文字；没有时为 Common。</summary>
    static Script ClusterScript(char[] t, int s, int e, out int firstReal)
    {
        for (int i = s; i < e;)
        {
            int cp = CodePoint.Decode(t, i, out int len);
            i += len;
            var sc = UnicodeData.GetScript(cp);
            if (sc != Script.Common && sc != Script.Inherited && sc != Script.Unknown)
            {
                firstReal = cp;
                return sc;
            }
        }
        firstReal = 0;
        return Script.Common;
    }

    /// <summary>
    /// 为一个字素簇选择字型：主字型 → 前一个簇的（回退）字型 → <see cref="FontManager.Fallback"/>；整个簇使用同一个字型。
    /// 没有需要字形的码点（零宽、控制字符）时沿用前一个簇的字型，避免无谓地切分 run。
    /// </summary>
    FontFace ChooseFace(char[] t, int s, int e, int style, StyleInfo info, FontFace? prev, CoverageMemo memo)
    {
        var primary = info.Primary;
        int first = -1;
        if (Covers(primary, t, s, e, ref first, memo)) return first < 0 && prev != null ? prev : primary;
        if (prev != null && !ReferenceEquals(prev, primary) && Covers(prev, t, s, e, ref first, memo)) return prev;
        // 回退结果按（样式、码点）记忆：缺字的长文本不必为每个簇都查询字体管理器
        long key = ((long)style << 21) | (uint)first;
        if (!memo.Fallbacks.TryGetValue(key, out var fallback))
            memo.Fallbacks[key] = fallback = _engine.Fonts.Fallback(first, info.Query, info.Style.Locale);
        return fallback ?? primary;
    }

    static bool Covers(FontFace face, char[] t, int s, int e, ref int firstSignificant, CoverageMemo? memo = null)
    {
        for (int i = s; i < e;)
        {
            int cp = CodePoint.Decode(t, i, out int len);
            i += len;
            if (!IsSignificant(cp)) continue;
            if (firstSignificant < 0) firstSignificant = cp;
            if (!(memo?.HasGlyph(face, cp) ?? face.TryGetGlyph(cp, out _))) return false;
        }
        return true;
    }

    /// <summary>分段时的字形覆盖查询缓存（直接映射，按码点低位索引）：同一段落中的字符大量重复，避免反复查 cmap。</summary>
    sealed class CoverageMemo
    {
        const int Size = 512;
        readonly int[] _cp = new int[Size];
        readonly FontFace?[] _face = new FontFace?[Size];
        readonly bool[] _has = new bool[Size];

        public readonly Dictionary<long, FontFace?> Fallbacks = new();

        public bool HasGlyph(FontFace face, int cp)
        {
            int slot = cp & (Size - 1);
            if (_cp[slot] == cp && ReferenceEquals(_face[slot], face)) return _has[slot];
            bool has = face.TryGetGlyph(cp, out _);
            _cp[slot] = cp;
            _face[slot] = face;
            _has[slot] = has;
            return has;
        }
    }

    /// <summary>需要字形的码点：不是默认可忽略字符、控制符或换行符。</summary>
    static bool IsSignificant(int cp)
    {
        if (cp < 0x20 || (cp >= 0x7F && cp <= 0x9F) || cp == 0x2028 || cp == 0x2029) return false;
        if ((uint)(cp - 0xD800) <= 0x7FF) return true;
        return !BasicTextShaper.IsDefaultIgnorable(cp);
    }

    // ------------------------------------------------------------------ 整形与字素单元

    void BuildPiecesAndUnits(int[] graphemes, out Piece[] piecesOut, out Unit[] unitsOut)
    {
        var pieces = new List<Piece>(_items.Length * 2);
        var units = new Unit[Math.Max(0, graphemes.Length - 1)];
        int unitCount = 0;
        int gi = 0;
        var shapeMemory = new ReadOnlyMemory<char>(_shapeText);
        for (int ii = 0; ii < _items.Length; ii++)
        {
            ref readonly var item = ref _items[ii];
            int p = item.Start;
            bool joining = item.Placeholder < 0 && Shaping.ShapePlan.KindOf(item.Script) == Shaping.ScriptShaperKind.Arabic;
            while (p < item.End)
            {
                int q = p + 1;
                if (item.Placeholder < 0)
                    while (q < item.End && _breaks[q] == 0) q++;
                else q = item.End;
                var piece = new Piece { Start = p, End = q, Item = ii };
                var info = _styles[item.Style];
                if (item.Placeholder < 0)
                {
                    // 阿拉伯式连写的文字：词可能被样式变化或断行机会切开，连写形式取决于两侧相邻的字符
                    ReadOnlySpan<char> before = default, after = default;
                    if (joining)
                    {
                        before = ContextBefore(p);
                        after = ContextAfter(q);
                    }
                    piece.Run = _engine.ShapeWord(shapeMemory.Slice(p, q - p), item.Face!, info.Size, (item.Level & 1) != 0,
                        item.Script, info.Style.Locale, info.Style.Variation, info.Features, before, after);
                }
                int pieceIndex = pieces.Count;
                pieces.Add(piece);

                // 字素单元
                int firstUnit = unitCount;
                while (gi + 1 < graphemes.Length && graphemes[gi] < q)
                {
                    int us = graphemes[gi], ue = graphemes[gi + 1];
                    units[unitCount] = new Unit { Start = us, End = ue, Piece = pieceIndex };
                    for (int c = us; c < ue; c++) _unitOfChar[c] = unitCount;
                    unitCount++;
                    gi++;
                }

                // 字形 → 单元。簇值在逻辑顺序中单调（OpenType 整形器按单调字素簇合并）；遇到不单调的簇值（重排）时
                // 并入前一个单元，保证每个单元的字形在整形结果中连续
                if (piece.Run is { } run)
                {
                    var clusters = run.Clusters;
                    var advances = run.Advances;
                    bool rtl = run.RightToLeft;
                    int prevUnit = -1;
                    for (int j = 0; j < clusters.Length; j++)
                    {
                        int c = p + clusters[j];
                        if (c < p) c = p;
                        else if (c >= q) c = q - 1;
                        int ui = _unitOfChar[c];
                        if (prevUnit >= 0 && (rtl ? ui > prevUnit : ui < prevUnit)) ui = prevUnit;
                        prevUnit = ui;
                        ref var u = ref units[ui];
                        if (u.GlyphCount == 0) u.GlyphStart = j;
                        u.GlyphCount = j - u.GlyphStart + 1;
                        u.Advance += advances[j].X;
                    }
                    DistributeLigatures(units, firstUnit, unitCount, rtl);
                }

                for (int k = firstUnit; k < unitCount; k++) FinishUnit(ref units[k], item, info);
                if (pieceIndex > 0 && firstUnit > 0) KernAcrossPieces(pieces, pieceIndex, units, firstUnit, item, info);
                p = q;
            }
        }
        piecesOut = pieces.ToArray();
        unitsOut = units;
    }

    /// <summary>
    /// 整形上下文：<paramref name="index"/> 之前最多 <see cref="Shaping.ArabicShaping.ContextLength"/> 个码点（不拆开代理对）。
    /// 遇到整形文本中被替换为 ZWSP 的字符（控制符、换行、折叠的空白）即停止：ZWSP 的连写类型是透明，不能让连写越过换行。
    /// </summary>
    ReadOnlySpan<char> ContextBefore(int index)
    {
        var t = _shapeText;
        int i = index;
        for (int k = 0; k < Shaping.ArabicShaping.ContextLength && i > 0 && !IsReplacedChar(i - 1); k++)
        {
            CodePoint.DecodeBefore(t, i, out int len);
            i -= len;
        }
        return new ReadOnlySpan<char>(t, i, index - i);
    }

    /// <summary>
    /// 整形上下文：<paramref name="index"/> 起最多 <see cref="Shaping.ArabicShaping.ContextLength"/> 个码点，规则同 <see cref="ContextBefore"/>；
    /// 词本身以被替换的字符结束时（强制断行在换行符之后）没有后文。
    /// </summary>
    ReadOnlySpan<char> ContextAfter(int index)
    {
        var t = _shapeText;
        if (index > 0 && IsReplacedChar(index - 1)) return default;
        int i = index;
        for (int k = 0; k < Shaping.ArabicShaping.ContextLength && i < t.Length && !IsReplacedChar(i); k++)
        {
            CodePoint.Decode(t, i, out int len);
            i += len;
        }
        return new ReadOnlySpan<char>(t, index, i - index);
    }

    bool IsReplacedChar(int i) => _shapeText[i] == Zwsp && _source[i] != Zwsp;

    /// <summary>
    /// 一个字形覆盖多个字素单元（连字，如 "ffi"）时，把它的前进量平均分给这些单元，使连字内部也有光标位置与命中区域（与浏览器相同）。
    /// 字形归属于组内视觉上最左的单元：从左到右时是第一个，从右到左时是逻辑上最后一个（绘制时单元从左向右排列）。
    /// </summary>
    static void DistributeLigatures(Unit[] units, int first, int end, bool rtl)
    {
        int k = first;
        while (k < end && units[k].GlyphCount == 0) k++;
        while (k < end)
        {
            int m = k + 1;
            while (m < end && units[m].GlyphCount == 0) m++;
            int g = m - k;
            if (g > 1)
            {
                float total = units[k].Advance;
                float each = total / g;
                for (int i = k; i < m; i++) units[i].Advance = each;
                if (rtl)
                {
                    units[m - 1].GlyphStart = units[k].GlyphStart;
                    units[m - 1].GlyphCount = units[k].GlyphCount;
                    units[k].GlyphCount = 0;
                }
            }
            k = m;
        }
    }

    /// <summary>
    /// 跨词字距：词分别整形（并经缓存复用），相邻两个词在同一 run 中时，把边界两侧字形之间的成对调整（GPOS 'kern' 或 'kern' 表）
    /// 加到视觉上左侧的单元（只处理从左到右；见 <see cref="TextShaper.GetBoundaryKerning"/>）。
    /// </summary>
    void KernAcrossPieces(List<Piece> pieces, int pieceIndex, Unit[] units, int firstUnit, in Item item, StyleInfo info)
    {
        if ((item.Level & 1) != 0) return;
        var prev = pieces[pieceIndex - 1];
        var cur = pieces[pieceIndex];
        if (prev.Item != cur.Item || prev.Run is not { } a || cur.Run is not { } b) return;
        int ia = a.Count - 1;
        while (ia >= 0 && (a.Flags[ia] & GlyphFlags.Hidden) != 0) ia--;
        int ib = 0;
        while (ib < b.Count && (b.Flags[ib] & GlyphFlags.Hidden) != 0) ib++;
        if (ia < 0 || ib >= b.Count) return;
        ref var last = ref units[firstUnit - 1];
        if ((last.Flags & UnitFlags.Removed) != 0) return;
        float k = _engine.GetBoundaryKerning(item.Face!, info.Size, false, item.Script, info.Style.Locale, info.Style.Variation, info.Features,
            a.Glyphs[ia], b.Glyphs[ib]);
        if (k != 0) last.Advance += k;
    }

    void FinishUnit(ref Unit u, in Item item, StyleInfo info)
    {
        var t = _text;
        int cp = CodePoint.Decode(t, u.Start);
        int last = CodePoint.DecodeBefore(t, u.End, out _);
        UnitFlags f = 0;
        if (item.Placeholder >= 0)
        {
            u.Advance = _placeholders[item.Placeholder].Style.Width;
            u.Flags = UnitFlags.Placeholder;
            return;
        }
        if (cp > 0x20 && cp < 0x7F && u.End - u.Start == 1)
        {
            // 可打印 ASCII：没有任何特殊标志
            u.Flags = 0;
            u.Advance += info.LetterSpacing;
            return;
        }
        if (IsHardBreakChar(last)) f |= UnitFlags.HardBreak | UnitFlags.Removed;
        else if (cp == Zwsp && _source[u.Start] != Zwsp) f |= UnitFlags.Removed;          // 折叠掉的空白
        else if (cp < 0x20 && cp != '\t' || cp >= 0x7F && cp <= 0x9F) f |= UnitFlags.Removed;   // 控制字符

        var ws = _style.WhiteSpace;
        if ((f & UnitFlags.Removed) != 0) f |= UnitFlags.Hang;
        else
        {
            if (ws != WhiteSpace.Pre && ws != WhiteSpace.BreakSpaces && IsHangableSpace(cp)) f |= UnitFlags.Hang;
            if (IsWordSeparator(cp)) f |= UnitFlags.Space;
            else
            {
                var w = UnicodeData.GetEastAsianWidth(cp);
                if ((w == EastAsianWidth.Wide || w == EastAsianWidth.Fullwidth) && UnicodeData.GetGeneralCategory(cp) != GeneralCategory.SpaceSeparator)
                    f |= UnitFlags.Cjk;
            }
        }
        u.Flags = f;
        if ((f & UnitFlags.Removed) != 0)
            u.Advance = 0;
        else
        {
            // 保留的制表符（不折叠空白时）：tab-size 个空格宽（整形时按空格整形）或给定长度
            if (cp == '\t' && !_style.CollapsesSpaces)
                u.Advance = _style.TabSizeIsLength ? TextStyle.Sanitize(_style.TabSize, 0, MaxFiniteWidth)
                    : u.Advance * TextStyle.Sanitize(_style.TabSize, 0, 1000);
            u.Advance += info.LetterSpacing;
            if ((f & UnitFlags.Space) != 0) u.Advance += info.WordSpacing;
        }
    }

    /// <summary>行尾可悬挂的空白：制表符与非不换行的空格分隔符（CSS Text 3 §4.1.3）。</summary>
    static bool IsHangableSpace(int cp) =>
        cp == '\t' || (UnicodeData.GetGeneralCategory(cp) == GeneralCategory.SpaceSeparator && cp != 0xA0 && cp != 0x2007 && cp != 0x202F);

    /// <summary>CSS 词分隔符（word-spacing 与两端对齐作用于它们）。</summary>
    static bool IsWordSeparator(int cp) =>
        cp == ' ' || cp == 0xA0 || cp == 0x1361 || cp == 0x10100 || cp == 0x10101 || cp == 0x1039F || cp == 0x1091F;

    Segment[] BuildSegments()
    {
        var list = new List<Segment>();
        int start = 0;
        float width = 0;
        for (int u = 0; u < _units.Length; u++)
        {
            width += _units[u].Advance;
            byte b = _breaks[_units[u].End];
            if (b == 0 && u + 1 < _units.Length) continue;
            int hangStart = u + 1;
            float hang = 0;
            while (hangStart > start && (_units[hangStart - 1].Flags & UnitFlags.Hang) != 0)
            {
                hangStart--;
                hang += _units[hangStart].Advance;
            }
            list.Add(new Segment { UnitStart = start, UnitEnd = u + 1, HangStart = hangStart, Width = width, Hang = hang, Mandatory = b == 2 || (_units[u].Flags & UnitFlags.HardBreak) != 0 });
            start = u + 1;
            width = 0;
        }
        return list.ToArray();
    }

    double[]? _prefix;

    /// <summary>单元前进量的区间和（前缀和，O(1)；首次调用时建立）。</summary>
    float PrefixSum(int from, int to)
    {
        if (_prefix is null)
        {
            var p = new double[_units.Length + 1];
            for (int i = 0; i < _units.Length; i++) p[i + 1] = p[i] + _units[i].Advance;
            _prefix = p;
        }
        return (float)(_prefix[to] - _prefix[from]);
    }

    float SumAdvance(int from, int to)
    {
        float w = 0;
        for (int u = from; u < to; u++) w += _units[u].Advance;
        return w;
    }

    // ------------------------------------------------------------------ 固有宽度

    float _minIntrinsic = float.NaN, _maxIntrinsic = float.NaN;

    /// <summary>最小固有宽度（min-content）：最宽的不可断开片段（不含其后悬挂的空白）。不触发断行。</summary>
    public float MinIntrinsicWidth
    {
        get
        {
            if (float.IsNaN(_minIntrinsic))
            {
                float m = 0;
                if (!_style.Wraps) m = MaxIntrinsicWidth;
                else if (_style.OverflowWrap == OverflowWrap.Anywhere)
                {
                    foreach (var u in _units)
                        if ((u.Flags & UnitFlags.Hang) == 0) m = Math.Max(m, u.Advance);
                }
                else
                    foreach (var s in _segments) m = Math.Max(m, s.Width - s.Hang);
                _minIntrinsic = m + IntrinsicIndent;
            }
            return _minIntrinsic;
        }
    }

    /// <summary>固有宽度计入的缩进：只计长度部分的正缩进（百分比相对的可用宽度此时未知）。</summary>
    float IntrinsicIndent => Math.Max(0, TextStyle.Sanitize(_style.TextIndent, 0, MaxFiniteWidth));

    /// <summary>最大固有宽度（max-content）：不自动换行时最宽一行的宽度。不触发断行。</summary>
    public float MaxIntrinsicWidth
    {
        get
        {
            if (float.IsNaN(_maxIntrinsic))
            {
                float m = 0, w = 0;
                foreach (var s in _segments)
                {
                    if (s.Mandatory)
                    {
                        m = Math.Max(m, w + s.Width - s.Hang);
                        w = 0;
                    }
                    else w += s.Width;
                }
                _maxIntrinsic = Math.Max(m, w) + IntrinsicIndent;
            }
            return _maxIntrinsic;
        }
    }
}

/// <summary>按对象引用比较（netstandard2.0 没有 System.Collections.Generic.ReferenceEqualityComparer）。</summary>
sealed class ReferenceEqualityComparer<T> : IEqualityComparer<T> where T : class
{
    public static readonly ReferenceEqualityComparer<T> Instance = new();
    public bool Equals(T? x, T? y) => ReferenceEquals(x, y);
    public int GetHashCode(T obj) => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj);
}
