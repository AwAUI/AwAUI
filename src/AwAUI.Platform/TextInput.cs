using System;
using System.Collections.Generic;

namespace AwAUI.Platform;

/// <summary>
/// 文本输入客户端文档中的范围 [Start, End)，UTF-16 码元下标，总是 Start ≤ End（构造时规范化，负值取 0）。
/// 与 <c>AwAUI.Text.TextRange</c> 分开命名：平台层不引用文本栈，而 UI 代码常同时用到两者。
/// </summary>
public readonly struct TextInputRange : IEquatable<TextInputRange>
{
    public TextInputRange(int start, int end)
    {
        if (start < 0) start = 0;
        if (end < 0) end = 0;
        if (end < start) (start, end) = (end, start);
        Start = start;
        End = end;
    }

    /// <summary>位于 <paramref name="offset"/> 的空范围（插入点）。</summary>
    public static TextInputRange Caret(int offset) => new(offset, offset);

    public int Start { get; }
    public int End { get; }
    public int Length => End - Start;
    public bool IsEmpty => Start == End;

    /// <summary>限制到 [0, <paramref name="length"/>]。</summary>
    public TextInputRange Clamp(int length) =>
        new(Math.Min(Start, Math.Max(0, length)), Math.Min(End, Math.Max(0, length)));

    public bool Equals(TextInputRange other) => Start == other.Start && End == other.End;
    public override bool Equals(object? obj) => obj is TextInputRange r && Equals(r);
    public override int GetHashCode() => (Start * 397) ^ End;
    public static bool operator ==(TextInputRange a, TextInputRange b) => a.Equals(b);
    public static bool operator !=(TextInputRange a, TextInputRange b) => !a.Equals(b);
    public override string ToString() => "[" + Start + ", " + End + ")";
}

/// <summary>可编辑内容的类型：决定软键盘布局，以及密码框关闭输入法与自动更正。</summary>
public enum TextInputContentType : byte
{
    Text,
    Password,
    Number,
    Email,
    Url,
    Search,
    Phone,
}

/// <summary>软键盘回车键的样式（提示，桌面后端忽略）。与 HTML 的 <c>enterkeyhint</c> 对应。</summary>
public enum TextInputReturnKeyType : byte
{
    /// <summary>平台默认（浏览器不设置 enterkeyhint）。</summary>
    Default,
    Done,
    Go,
    Next,

    /// <summary>换行（HTML 的 <c>enterkeyhint="enter"</c>）。</summary>
    Return,

    Search,
    Send,
}

/// <summary>自动大写策略（提示，桌面后端忽略）。与 HTML 的 <c>autocapitalize</c> 对应。</summary>
public enum TextInputAutoCapitalization : byte
{
    None,
    Sentences,
    Words,
    Characters,
}

/// <summary>文本输入客户端的选项（design/04「IME」）。</summary>
/// <para>
/// 除内容类型与多行外，其余成员都是给移动端软键盘 / 浏览器代理文本框的<b>提示</b>：桌面后端可以忽略它们，
/// 但不能因此改变编辑行为。新成员以 init 属性追加，不改变构造函数与既有成员的默认值。
/// </para>
public readonly struct TextInputOptions : IEquatable<TextInputOptions>
{
    public TextInputOptions(TextInputContentType contentType = TextInputContentType.Text, bool multiline = false, bool autoCorrect = true)
    {
        ContentType = contentType;
        Multiline = multiline;
        AutoCorrect = autoCorrect && contentType != TextInputContentType.Password;
    }

    public TextInputContentType ContentType { get; }

    /// <summary>多行编辑（回车插入换行而不是提交）。</summary>
    public bool Multiline { get; }

    /// <summary>允许拼写检查与自动更正（密码框总是 false）。</summary>
    public bool AutoCorrect { get; }

    /// <summary>密码框：平台应关闭输入法组合（只接受直接输入的字符），不显示候选。</summary>
    public bool IsPassword => ContentType == TextInputContentType.Password;

    /// <summary>软键盘回车键的样式（提示）。</summary>
    public TextInputReturnKeyType ReturnKeyType { get; init; }

    /// <summary>自动大写的策略（提示）。</summary>
    public TextInputAutoCapitalization AutoCapitalization { get; init; }

    /// <summary>敏感内容（密码、个人信息）：平台不应记录，也不应显示建议或候选（提示）。</summary>
    public bool IsSensitive { get; init; }

    /// <summary>是否显示拼写建议 / 候选（提示；密码框与 <see cref="IsSensitive"/> 时平台应关闭）。</summary>
    public bool ShowSuggestions { get; init; } = true;

    /// <summary>输入语言的 BCP 47 标签，按偏好排序（提示；浏览器只使用首项作为代理文本框的 <c>lang</c>）。</summary>
    public IReadOnlyList<string>? LocaleHints { get; init; }

    public bool Equals(TextInputOptions o) =>
        ContentType == o.ContentType && Multiline == o.Multiline && AutoCorrect == o.AutoCorrect
        && ReturnKeyType == o.ReturnKeyType && AutoCapitalization == o.AutoCapitalization
        && IsSensitive == o.IsSensitive && ShowSuggestions == o.ShowSuggestions
        && LocaleHintsEqual(LocaleHints, o.LocaleHints);

    public override bool Equals(object? obj) => obj is TextInputOptions o && Equals(o);

    public override int GetHashCode()
    {
        int h = HashCodeCompat.Combine((int)ContentType, (int)ReturnKeyType);
        h = HashCodeCompat.Combine(h, (int)AutoCapitalization);
        h = HashCodeCompat.Combine(h, (Multiline ? 1 : 0) | (AutoCorrect ? 2 : 0) | (IsSensitive ? 4 : 0) | (ShowSuggestions ? 8 : 0));
        if (LocaleHints is { } hints)
        {
            h = HashCodeCompat.Combine(h, hints.Count);
            for (int i = 0; i < hints.Count; i++) h = HashCodeCompat.Combine(h, hints[i]?.GetHashCode() ?? 0);
        }
        return h;
    }

    static bool LocaleHintsEqual(IReadOnlyList<string>? a, IReadOnlyList<string>? b)
    {
        if (ReferenceEquals(a, b)) return true;
        if (a is null || b is null || a.Count != b.Count) return false;
        for (int i = 0; i < a.Count; i++)
            if (!string.Equals(a[i], b[i], StringComparison.Ordinal)) return false;
        return true;
    }
}

/// <summary>
/// 文本输入客户端：获得焦点的可编辑元素与平台输入法之间的契约（design/04「IME / 文本输入」），由框架（UI 层的文本编辑元素）实现。
/// 接口按最复杂的平台设计：macOS <c>NSTextInputClient</c>、Android <c>InputConnection</c>、iOS <c>UITextInput</c> 都要求平台能反向查询文档。
/// <para>
/// <b>文档模型</b>：客户端的「文档」是当前显示的文本，<b>包含</b>组合中的预编辑文本（与 macOS / Android 一致）；
/// <see cref="Selection"/>、<see cref="Composition"/> 与各方法的范围都是文档中的 UTF-16 下标。
/// </para>
/// <para>
/// <b>调用约定</b>：只在 UI 线程上调用。平台把输入法的结果翻译成这些调用：组合开始 / 更新 → <see cref="SetComposition"/>；
/// 提交（含普通按键产生的字符）→ <see cref="Commit"/>；取消 → 以空文本调用 <see cref="SetComposition"/>；
/// 自动更正、删除光标周围文本、粘贴 → <see cref="ReplaceRange"/>。客户端在内容、选区或光标位置变化后调用
/// <see cref="TextInputMethod.NotifyStateChanged"/>，以程序方式改变内容时调用 <see cref="TextInputMethod.ResetComposition"/>。
/// </para>
/// 框架自己实现的接口（与 <see cref="IWindowEvents"/> 相同），平台只调用不实现，因此保留为接口（design/11）。
/// </summary>
public interface ITextInputClient
{
    TextInputOptions Options { get; }

    /// <summary>文档长度（含预编辑文本）。</summary>
    int Length { get; }

    /// <summary>选区（空范围即插入点）。平台设置时客户端把它限制在文档内。</summary>
    TextInputRange Selection { get; set; }

    /// <summary>预编辑文本在文档中的范围；没有组合时为 null。</summary>
    TextInputRange? Composition { get; }

    /// <summary>取文档中的文本；范围被限制在文档内。</summary>
    string GetText(TextInputRange range);

    /// <summary>插入点矩形（窗口客户区逻辑坐标），输入法据此定位候选窗。尚未布局时给出最接近的估计。</summary>
    Rect GetCaretRect();

    /// <summary><paramref name="range"/> 第一行部分的包围矩形（窗口客户区逻辑坐标）；空范围给出该位置的插入点矩形。</summary>
    Rect GetTextBounds(TextInputRange range);

    /// <summary>
    /// 设置（开始或更新）预编辑文本：替换当前的预编辑文本（没有组合时替换选区），<paramref name="selection"/> 是预编辑文本内的光标 / 选区
    /// （相对预编辑文本起点，越界时被限制）。<paramref name="text"/> 为空表示取消组合。
    /// </summary>
    void SetComposition(string text, TextInputRange selection);

    /// <summary>提交文本：替换预编辑文本（没有组合时替换选区）并结束组合，插入点移到提交的文本之后。</summary>
    void Commit(string text);

    /// <summary>
    /// 把文档中的 <paramref name="range"/> 替换为 <paramref name="text"/>（空文本即删除），插入点移到新文本之后。
    /// 与组合相交时先结束组合（预编辑文本按原样保留为已提交文本）。
    /// </summary>
    void ReplaceRange(TextInputRange range, string text);
}

/// <summary>
/// 平台的输入法桥（design/04）：每个窗口一个，经 <see cref="WindowImpl.TextInput"/> 取得。
/// 焦点进入可编辑元素时框架调用 <see cref="SetClient"/>，离开时以 null 调用；平台把输入法（以及有客户端时的全部文本输入）
/// 交给当前客户端，没有客户端时文本经 <see cref="IWindowEvents.OnTextInput"/> 送达，并关闭输入法组合。
/// 平台实现派生自它；以后可以追加带默认实现的成员而不破坏已有平台（design/11）。
/// </summary>
public abstract class TextInputMethod
{
    ITextInputClient? _client;

    /// <summary>当前客户端；没有获得焦点的可编辑元素时为 null。</summary>
    public ITextInputClient? Client => _client;

    /// <summary>
    /// 客户端变化（焦点进入 / 离开可编辑元素，或同一元素的输入选项变化）。在 <see cref="OnClientChanged"/> 之后触发，
    /// 此时 <see cref="Client"/> 已经是新客户端；平台后端据此把 <see cref="TextInputOptions"/> 的提示应用到底层输入法。
    /// </summary>
    public event Action<ITextInputClient?>? ClientChanged;

    /// <summary>
    /// 焦点进入（非 null）或离开（null）可编辑元素。切换客户端时平台放弃进行中的组合（框架在此之前已按需提交预编辑文本）。
    /// </summary>
    public void SetClient(ITextInputClient? client)
    {
        if (ReferenceEquals(_client, client)) return;
        var old = _client;
        _client = client;
        OnClientChanged(old, client);
        ClientChanged?.Invoke(client);
    }

    /// <summary>客户端变化：启用 / 关闭输入法、按 <see cref="ITextInputClient.Options"/> 配置、放弃旧组合。</summary>
    protected abstract void OnClientChanged(ITextInputClient? oldClient, ITextInputClient? newClient);

    /// <summary>客户端的内容、选区或插入点矩形变化（布局之后）：平台重新定位组合窗与候选窗。</summary>
    public virtual void NotifyStateChanged() { }

    /// <summary>放弃平台侧进行中的组合（客户端已自行处理了预编辑文本，例如以程序方式改了内容或已把它提交）。</summary>
    public virtual void ResetComposition() { }

    /// <summary>显示软键盘（移动端与 Web）；桌面上没有效果。</summary>
    public virtual void Show() { }

    /// <summary>收起软键盘。</summary>
    public virtual void Hide() { }

    /// <summary>
    /// 平台自己把剪贴板快捷键（Ctrl/⌘ + C / X / V）变成对客户端的操作（Web：浏览器派发 copy / cut / paste 事件，
    /// 由平台经 <see cref="ITextInputClient.GetText"/> / <see cref="ITextInputClient.ReplaceRange"/> 完成）。
    /// 为 true 时框架不再自行处理这些快捷键，避免重复粘贴。
    /// </summary>
    public virtual bool HandlesClipboardShortcuts => false;
}
