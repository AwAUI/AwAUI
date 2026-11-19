using System;
using System.Threading.Tasks;
using AwAUI.Platform;

namespace AwAUI.Headless;

/// <summary>
/// 无窗口平台的输入法桥：完全由测试脚本驱动。<see cref="Compose(string, int)"/> / <see cref="CommitComposition"/> /
/// <see cref="CancelComposition"/> 模拟输入法的组合序列，记录框架的通知（<see cref="StateChangedCount"/>、<see cref="LastCaretRect"/>），
/// 便于端到端测试文本编辑元素作为 <see cref="ITextInputClient"/> 的行为。
/// 与真实平台一样，密码框（<see cref="TextInputOptions.IsPassword"/>）关闭输入法组合。
/// </summary>
public sealed class HeadlessTextInput : TextInputMethod
{
    readonly HeadlessWindow _window;

    internal HeadlessTextInput(HeadlessWindow window) => _window = window;

    /// <summary>平台侧是否有进行中的组合（最近一次 <see cref="Compose(string, int)"/> 之后、提交 / 取消 / 重置之前）。</summary>
    public bool IsComposing { get; private set; }

    /// <summary>软键盘是否显示（<see cref="TextInputMethod.Show"/> / <see cref="TextInputMethod.Hide"/>）。</summary>
    public bool IsKeyboardShown { get; private set; }

    /// <summary>当前客户端是否允许输入法组合（有客户端且不是密码框）。</summary>
    public bool IsImeEnabled => Client is { } c && !c.Options.IsPassword;

    /// <summary>框架调用 <see cref="NotifyStateChanged"/> 的次数。</summary>
    public int StateChangedCount { get; private set; }

    /// <summary>框架调用 <see cref="ResetComposition"/> 的次数。</summary>
    public int ResetCount { get; private set; }

    /// <summary>客户端切换的次数（含切换为 null）。</summary>
    public int ClientChangedCount { get; private set; }

    /// <summary>最近一次 <see cref="NotifyStateChanged"/> 时向客户端查询到的插入点矩形（候选窗会定位到这里）。</summary>
    public Rect? LastCaretRect { get; private set; }

    protected override void OnClientChanged(ITextInputClient? oldClient, ITextInputClient? newClient)
    {
        ClientChangedCount++;
        IsComposing = false;
        if (newClient is null) IsKeyboardShown = false;
    }

    public override void NotifyStateChanged()
    {
        StateChangedCount++;
        if (Client is { } c) LastCaretRect = c.GetCaretRect();
    }

    public override void ResetComposition()
    {
        ResetCount++;
        IsComposing = false;
    }

    public override void Show() => IsKeyboardShown = Client is not null;

    public override void Hide() => IsKeyboardShown = false;

    ITextInputClient RequireClient()
    {
        if (_window.IsClosed) throw new ObjectDisposedException(nameof(HeadlessWindow));
        return Client ?? throw new InvalidOperationException("没有获得焦点的可编辑元素（TextInputMethod.Client 为 null）。");
    }

    ITextInputClient RequireIme()
    {
        var c = RequireClient();
        if (c.Options.IsPassword) throw new InvalidOperationException("密码框关闭了输入法组合。");
        return c;
    }

    /// <summary>输入法组合开始或更新：预编辑文本为 <paramref name="text"/>，光标在其中的 <paramref name="caret"/> 处（默认在末尾）。</summary>
    public void Compose(string text, int caret = -1)
    {
        if (text is null) throw new ArgumentNullException(nameof(text));
        if (caret < 0) caret = text.Length;
        Compose(text, caret, caret);
    }

    /// <summary>输入法组合开始或更新，预编辑文本内的选区为 [<paramref name="selectionStart"/>, <paramref name="selectionEnd"/>)（如正在转换的文节）。</summary>
    public void Compose(string text, int selectionStart, int selectionEnd)
    {
        if (text is null) throw new ArgumentNullException(nameof(text));
        var c = RequireIme();
        IsComposing = text.Length > 0;
        c.SetComposition(text, new TextInputRange(selectionStart, selectionEnd));
    }

    /// <summary>输入法提交 <paramref name="text"/>（替换预编辑文本）并结束组合。</summary>
    public void CommitComposition(string text)
    {
        if (text is null) throw new ArgumentNullException(nameof(text));
        var c = RequireClient();
        IsComposing = false;
        c.Commit(text);
    }

    /// <summary>输入法取消组合（Esc）：预编辑文本被移除，不提交任何内容。</summary>
    public void CancelComposition()
    {
        var c = RequireClient();
        IsComposing = false;
        c.SetComposition(string.Empty, default);
    }

    /// <summary>模拟移动端自动更正 / 删除周围文本：替换客户端文档中的范围。</summary>
    public void ReplaceRange(TextInputRange range, string text)
    {
        if (text is null) throw new ArgumentNullException(nameof(text));
        RequireClient().ReplaceRange(range, text);
    }

    /// <summary>平台送达一段已提交的文本：有客户端时交给它（与真实平台相同），否则经 <see cref="IWindowEvents.OnTextInput"/>。</summary>
    internal void DeliverText(string text)
    {
        if (Client is { } c) c.Commit(text);
        else _window.Events?.OnTextInput(text);
    }
}

/// <summary>
/// 内存中的剪贴板（进程内、与系统剪贴板无关）。读写立即完成；<see cref="DenyAccess"/> 可模拟被拒绝的访问（如 Web 上缺少权限）。
/// </summary>
public sealed class HeadlessClipboard : Clipboard
{
    /// <summary>当前内容；null 表示空。</summary>
    public string? Text { get; set; }

    /// <summary>为 true 时读返回 null、写返回 false。</summary>
    public bool DenyAccess { get; set; }

    /// <summary>读取与写入的次数（测试检查）。</summary>
    public int ReadCount { get; private set; }

    /// <inheritdoc cref="ReadCount"/>
    public int WriteCount { get; private set; }

    public override Task<string?> GetTextAsync()
    {
        ReadCount++;
        return Task.FromResult(DenyAccess || string.IsNullOrEmpty(Text) ? null : Text);
    }

    public override Task<bool> SetTextAsync(string text)
    {
        if (text is null) throw new ArgumentNullException(nameof(text));
        WriteCount++;
        if (DenyAccess) return Task.FromResult(false);
        Text = LimitLength(text);
        return Task.FromResult(true);
    }
}
