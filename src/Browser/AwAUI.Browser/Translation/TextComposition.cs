using System;

namespace AwAUI.Browser;

public enum TextCompositionKind : byte
{
    /// <summary>输入法开始组合（compositionstart）。</summary>
    Start,

    /// <summary>组合文本或其中的光标 / 选区变化。</summary>
    Update,

    /// <summary>组合结束：<see cref="TextCompositionEvent.Text"/> 是最终提交的文本，取消时为空串。提交的文本同时经 OnTextInput 送达。</summary>
    End,
}

/// <summary>输入法组合（预编辑）事件。</summary>
public readonly struct TextCompositionEvent : IEquatable<TextCompositionEvent>
{
    public TextCompositionEvent(TextCompositionKind kind, string text, int selectionStart, int selectionEnd)
    {
        Kind = kind;
        Text = text;
        SelectionStart = selectionStart;
        SelectionEnd = selectionEnd;
    }

    public TextCompositionKind Kind { get; }

    /// <summary>当前的组合文本（UTF-16）。</summary>
    public string Text { get; }

    /// <summary>组合文本内的光标 / 选区（UTF-16 下标，0 ≤ Start ≤ End ≤ Text.Length）。</summary>
    public int SelectionStart { get; }
    public int SelectionEnd { get; }

    public bool Equals(TextCompositionEvent o) =>
        Kind == o.Kind && string.Equals(Text, o.Text, StringComparison.Ordinal) && SelectionStart == o.SelectionStart && SelectionEnd == o.SelectionEnd;

    public override bool Equals(object? obj) => obj is TextCompositionEvent e && Equals(e);
    public override int GetHashCode() => HashCodeCompat.Combine((int)Kind, StringComparer.Ordinal.GetHashCode(Text ?? ""), SelectionStart, SelectionEnd);
    public override string ToString() => $"{Kind} \"{Text}\" [{SelectionStart},{SelectionEnd})";
}

/// <summary>组合跟踪器的输出。</summary>
internal interface ITextInputSink
{
    void OnComposition(in TextCompositionEvent e);
    void OnCommit(string text);
}

/// <summary>
/// 把代理 &lt;textarea&gt; 上的 composition* / input / beforeinput 事件整理成干净的「开始 → 更新… → 结束 + 提交」序列。
/// <para>
/// 各浏览器的事件顺序不同（Chrome：compositionupdate → input → compositionend；Firefox 在 compositionend 之后
/// 还有一个 input；Safari 会在 compositionend 之后补发 insertFromComposition），也可能缺少 compositionstart
/// （脚本合成的事件、部分移动端键盘）。这里只认 composition 事件与组合期间的 input，重复的状态被合并，
/// 缺失的开始事件会被补上。文本长度有上限，超长输入被截断（不切开代理对）。
/// </para>
/// </summary>
internal sealed class CompositionTracker
{
    /// <summary>组合文本的长度上限（UTF-16 码元）。</summary>
    public const int MaxCompositionLength = 4096;

    /// <summary>一次提交文本（含粘贴）的长度上限。</summary>
    public const int MaxCommitLength = 1 << 20;

    readonly ITextInputSink _sink;
    string _text = "";
    int _selStart, _selEnd;

    public CompositionTracker(ITextInputSink sink) => _sink = sink;

    public bool IsComposing { get; private set; }
    public string Text => _text;
    public int SelectionStart => _selStart;
    public int SelectionEnd => _selEnd;

    /// <summary>按 <see cref="DomProtocol"/> 的组合事件种类分派。selStart &lt; 0 表示光标在文本末尾。</summary>
    public void Handle(int kind, string? data, int selStart, int selEnd)
    {
        switch (kind)
        {
            case DomProtocol.CompositionStart: Start(); break;
            case DomProtocol.CompositionUpdate: Update(data, selStart, selEnd); break;
            case DomProtocol.CompositionEnd: End(data); break;
        }
    }

    public void Start()
    {
        if (IsComposing) return; // 重复的 compositionstart：保持当前组合
        IsComposing = true;
        _text = "";
        _selStart = _selEnd = 0;
        _sink.OnComposition(new TextCompositionEvent(TextCompositionKind.Start, "", 0, 0));
    }

    public void Update(string? data, int selStart, int selEnd)
    {
        if (!IsComposing) Start();
        string text = Truncate(data ?? "", MaxCompositionLength);
        int len = text.Length;
        if (selStart < 0) selStart = len;
        if (selEnd < 0) selEnd = selStart;
        if (selStart > len) selStart = len;
        if (selEnd > len) selEnd = len;
        if (selEnd < selStart) (selStart, selEnd) = (selEnd, selStart);
        if (string.Equals(text, _text, StringComparison.Ordinal) && selStart == _selStart && selEnd == _selEnd) return;
        _text = text;
        _selStart = selStart;
        _selEnd = selEnd;
        _sink.OnComposition(new TextCompositionEvent(TextCompositionKind.Update, text, selStart, selEnd));
    }

    /// <summary>组合结束。<paramref name="data"/> 为最终文本（取消时为空）；没有进行中的组合时只提交文本。</summary>
    public void End(string? data)
    {
        string text = Truncate(data ?? "", MaxCompositionLength);
        if (IsComposing)
        {
            IsComposing = false;
            _text = "";
            _selStart = _selEnd = 0;
            _sink.OnComposition(new TextCompositionEvent(TextCompositionKind.End, text, text.Length, text.Length));
        }
        if (text.Length > 0) _sink.OnCommit(text);
    }

    /// <summary>窗口关闭等场合放弃进行中的组合（不提交）。</summary>
    public void Reset()
    {
        if (!IsComposing) return;
        IsComposing = false;
        _text = "";
        _selStart = _selEnd = 0;
        _sink.OnComposition(new TextCompositionEvent(TextCompositionKind.End, "", 0, 0));
    }

    /// <summary>截断到 <paramref name="max"/> 个码元以内，不把代理对切开。</summary>
    public static string Truncate(string s, int max)
    {
        if (s.Length <= max) return s;
        int n = max;
        if (n > 0 && char.IsHighSurrogate(s[n - 1])) n--;
        return s.Substring(0, n);
    }
}

/// <summary>beforeinput 事件的处理决定。</summary>
internal enum BeforeInputAction : byte
{
    /// <summary>不干预（输入法组合中的编辑由 composition 事件处理）。</summary>
    PassThrough,

    /// <summary>阻止浏览器修改代理文本框（编辑由按键事件驱动）。</summary>
    Prevent,

    /// <summary>阻止并把 data 作为提交文本交给框架。</summary>
    PreventAndCommit,

    /// <summary>阻止并删除插入点前（选区非空时删除选区）：没有可转发 keydown 的移动端键盘的退格。只在有文本输入客户端时使用。</summary>
    PreventAndDeleteBackward,

    /// <summary>阻止并删除插入点后（向前删除）。只在有文本输入客户端时使用。</summary>
    PreventAndDeleteForward,

    /// <summary>阻止并合成一次回车按键（移动端键盘只以 insertParagraph / insertLineBreak 送出回车时）。只在有文本输入客户端时使用。</summary>
    PreventAndEnter,
}

internal static class TextInputFilter
{
    /// <summary>
    /// 按 InputEvent.inputType 分类。代理文本框始终保持为空：普通字符输入在 beforeinput 阶段取出 data 后阻止，
    /// 删除、换行、撤销等编辑操作阻止后由框架按键处理（Backspace、Enter 等的 keydown 已交给框架）。
    /// 粘贴由 paste 事件处理；输入法组合中的编辑不可取消，交给 composition 事件。
    /// </summary>
    /// <param name="inputType">InputEvent.inputType。</param>
    /// <param name="isComposing">输入法组合中。</param>
    /// <param name="hasClient">有获得焦点的文本输入客户端：桌面浏览器上退格、删除与回车的 keydown 已经交给框架并被阻止，
    /// 不会再产生 beforeinput；仍然到达的（keyCode 229 的移动端键盘）转成对客户端的编辑。</param>
    public static BeforeInputAction Classify(string? inputType, bool isComposing, bool hasClient = false)
    {
        if (isComposing) return BeforeInputAction.PassThrough;
        if (hasClient)
        {
            switch (inputType)
            {
                case "deleteContentBackward": return BeforeInputAction.PreventAndDeleteBackward;
                case "deleteContentForward": return BeforeInputAction.PreventAndDeleteForward;
                case "insertParagraph":
                case "insertLineBreak":
                    return BeforeInputAction.PreventAndEnter;
            }
        }
        switch (inputType)
        {
            case "insertCompositionText":
            case "deleteCompositionText":
            case "insertFromComposition":
            case "deleteByComposition":
                return BeforeInputAction.PassThrough;
            case "insertText":
            // 移动端键盘的自动更正 / 拼写替换：代理文本框始终为空，拿不到被替换的范围，按插入处理（近似）
            case "insertReplacementText":
                return BeforeInputAction.PreventAndCommit;
            default:
                return BeforeInputAction.Prevent;
        }
    }

    /// <summary>提交文本的清洗：去掉 C0 控制字符（制表符与换行除外，因此 CRLF 变为 LF）与 DEL，截断超长文本。</summary>
    public static string SanitizeCommit(string? text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        string s = CompositionTracker.Truncate(text!, CompositionTracker.MaxCommitLength);
        int i = 0;
        while (i < s.Length && !IsDropped(s[i])) i++;
        if (i == s.Length) return s;
        var sb = new System.Text.StringBuilder(s.Length);
        sb.Append(s, 0, i);
        for (; i < s.Length; i++)
            if (!IsDropped(s[i])) sb.Append(s[i]);
        return sb.ToString();
    }

    static bool IsDropped(char c) => (c < 0x20 && c != '\t' && c != '\n') || c == 0x7F;

    /// <summary>
    /// 退格 / 向前删除要删除的文档范围：选区非空时为选区，否则为插入点前 / 后的一个码点（不切开代理对；字素簇边界由客户端的布局决定，
    /// 这里只能按码点近似）。<paramref name="textAround"/> 是插入点前后的文本（取 <paramref name="caret"/> − 2 起的至多 4 个码元）。
    /// </summary>
    public static AwAUI.Platform.TextInputRange DeletionRange(AwAUI.Platform.TextInputRange selection, int length, bool backward,
        string textBefore, string textAfter)
    {
        if (!selection.IsEmpty) return selection;
        int caret = selection.Start;
        if (backward)
        {
            if (caret <= 0) return selection;
            int n = textBefore.Length >= 2 && char.IsLowSurrogate(textBefore[textBefore.Length - 1]) && char.IsHighSurrogate(textBefore[textBefore.Length - 2]) ? 2 : 1;
            return new AwAUI.Platform.TextInputRange(Math.Max(0, caret - n), caret);
        }
        if (caret >= length) return selection;
        int m = textAfter.Length >= 2 && char.IsHighSurrogate(textAfter[0]) && char.IsLowSurrogate(textAfter[1]) ? 2 : 1;
        return new AwAUI.Platform.TextInputRange(caret, Math.Min(length, caret + m));
    }
}
