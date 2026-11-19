using System;
using System.Text;
using AwAUI.MacOS.Interop;
using AwAUI.Platform;
using static AwAUI.MacOS.Interop.ObjC;

namespace AwAUI.MacOS;

// NSTextInputClient：键盘、输入法与剪贴板（design/04「IME」）
public sealed partial class MacWindow
{
    MacTextInput? _textInput;
    bool _inKeyDown;          // 正在把 keyDown: 交给输入上下文
    bool _imeChanged;         // 这一键期间输入法设置了预编辑文本
    readonly StringBuilder _keyText = new();

    /// <summary>正在调用 discardMarkedText（见 <see cref="MacTextInput"/>）：期间的输入法回调被忽略。</summary>
    internal bool DiscardingMarkedText;

    /// <summary>输入法桥（NSTextInputClient）。</summary>
    public override TextInputMethod TextInput => _textInput ??= new MacTextInput(this);

    /// <summary>平台剪贴板（NSPasteboard）。</summary>
    public override Clipboard Clipboard => _platform.Clipboard;

    MacTextInput Ime => (MacTextInput)TextInput;

    /// <summary>视图的 NSTextInputContext（视图遵循 NSTextInputClient 时由 AppKit 提供）；没有时为 0。</summary>
    internal IntPtr InputContext => _closed ? IntPtr.Zero : Send(_view, Sel("inputContext"));

    /// <summary>NSView*（测试探针用）。</summary>
    internal IntPtr ViewHandle => _view;

    void HandleKeyEvent(IntPtr e, bool down, KeyModifiers mods, TimeSpan time)
    {
        var events = Events;
        if (events == null) return;
        ushort code = (ushort)SendULong(e, Sel("keyCode"));
        var key = KeyMap.Get(code);
        bool repeat = down && SendByte(e, Sel("isARepeat")) != 0;
        string? characters = GetString(Send(e, Sel("characters")), KeyValues.MaxLength);
        string? ignoring = GetString(Send(e, Sel("charactersIgnoringModifiers")), KeyValues.MaxLength);
        string logical = KeyMap.LogicalKey(key, characters, ignoring, mods);
        var raw = new RawKeyEvent(down ? RawKeyEventType.Down : RawKeyEventType.Up, key, mods, repeat, code, time, logical);

        var client = Ime.Client;
        var context = InputContext;
        if (!down || client is null || !Ime.UsesInputMethod || context == IntPtr.Zero)
        {
            // 不经输入法：按键事件，然后按键的字符作为文本（Ctrl / ⌘ 组合是快捷键，不产生文本）
            events.OnKey(raw);
            if (!down || _closed || (mods & (KeyModifiers.Meta | KeyModifiers.Control)) != 0) return;
            var text = GetString(Send(e, Sel("characters")), Clipboard.MaxTextLength);
            if (!string.IsNullOrEmpty(text) && IsPrintable(text!)) DeliverText(text!);
            return;
        }

        // 经输入法：先让输入上下文解释按键（同步回调 setMarkedText / insertText / doCommandBySelector），再决定这一键属于谁
        bool composingBefore = client.Composition is not null;
        _keyText.Clear();
        _imeChanged = false;
        _inKeyDown = true;
        try
        {
            SendByte(context, Sel("handleEvent:"), e);
        }
        finally
        {
            _inKeyDown = false;
        }
        if (_closed) return;
        if (composingBefore || _imeChanged) return; // 输入法处理的按键不交给框架（与浏览器的 isComposing 一致）
        events.OnKey(raw);
        if (_closed || _keyText.Length == 0) return;
        if ((mods & (KeyModifiers.Meta | KeyModifiers.Control)) != 0) return;
        var typed = _keyText.ToString();
        _keyText.Clear();
        if (IsPrintable(typed)) DeliverText(typed);
    }

    /// <summary>已提交的文本：有客户端时交给它，否则经 OnTextInput。</summary>
    void DeliverText(string text)
    {
        if (Ime.Client is { } c) c.Commit(text);
        else Events?.OnTextInput(text);
    }

    static string? TextOf(IntPtr obj)
    {
        if (obj == IntPtr.Zero) return null;
        // NSAttributedString 或 NSString
        if (SendByte(obj, Sel("isKindOfClass:"), Class("NSAttributedString")) != 0) obj = Send(obj, Sel("string"));
        return GetString(obj, Clipboard.MaxTextLength);
    }

    static bool IsValid(NSRange r) => r.Location != Callbacks.NotFound && r.Location <= int.MaxValue && r.Length <= int.MaxValue;

    static TextInputRange ToRange(NSRange r) =>
        new((int)Math.Min(r.Location, int.MaxValue), (int)Math.Min(r.Location + Math.Min(r.Length, int.MaxValue), int.MaxValue));

    // ---------------------------------------------------------------- NSTextInputClient 回调

    internal void InsertText(IntPtr obj, NSRange replacement)
    {
        if (DiscardingMarkedText) return;
        var text = TextOf(obj);
        if (text is null) return;
        var client = Ime.Client;
        if (client is null)
        {
            if (text.Length > 0 && IsPrintable(text)) Events?.OnTextInput(text);
            return;
        }
        if (client.Composition is not null)
        {
            // 输入法提交：替换预编辑文本
            if (_inKeyDown) _imeChanged = true;
            client.Commit(text);
            return;
        }
        if (IsValid(replacement))
        {
            client.ReplaceRange(ToRange(replacement).Clamp(client.Length), text);
            return;
        }
        if (_inKeyDown)
        {
            // 普通按键产生的字符：等按键事件交给框架之后再提交（框架先看到 keydown）
            if (_keyText.Length + text.Length <= Clipboard.MaxTextLength) _keyText.Append(text);
            return;
        }
        client.Commit(text);
    }

    internal void SetMarkedText(IntPtr obj, NSRange selected, NSRange replacement)
    {
        if (DiscardingMarkedText) return;
        var client = Ime.Client;
        var text = TextOf(obj) ?? "";
        if (client is null) return;
        if (_inKeyDown) _imeChanged = true;
        if (client.Composition is null && IsValid(replacement))
            client.Selection = ToRange(replacement).Clamp(client.Length); // 重新转换：预编辑替换这段已提交的文本
        var sel = IsValid(selected) ? ToRange(selected) : TextInputRange.Caret(text.Length);
        client.SetComposition(text, sel);
    }

    internal void UnmarkText()
    {
        if (DiscardingMarkedText) return;
        var client = Ime.Client;
        if (client?.Composition is not { } comp) return;
        client.Commit(client.GetText(comp)); // 按原样提交预编辑文本
    }

    internal bool HasMarkedText => Ime.Client?.Composition is not null;

    internal NSRange MarkedRange => Ime.Client?.Composition is { } c
        ? new NSRange { Location = (ulong)c.Start, Length = (ulong)c.Length }
        : new NSRange { Location = Callbacks.NotFound, Length = 0 };

    internal NSRange SelectedRange => Ime.Client is { } client
        ? new NSRange { Location = (ulong)client.Selection.Start, Length = (ulong)client.Selection.Length }
        : new NSRange { Location = Callbacks.NotFound, Length = 0 };

    /// <summary>输入法查询文档片段（重新转换、上下文预测）。返回自动释放的 NSAttributedString；片段长度有上限。</summary>
    internal unsafe IntPtr AttributedSubstring(NSRange range, IntPtr actual)
    {
        var client = Ime.Client;
        if (client is null || !IsValid(range)) return IntPtr.Zero;
        var r = ToRange(range).Clamp(client.Length);
        if (r.Length > MaxQueryLength) r = new TextInputRange(r.Start, r.Start + MaxQueryLength);
        if (actual != IntPtr.Zero) *(NSRange*)actual = new NSRange { Location = (ulong)r.Start, Length = (ulong)r.Length };
        var s = NewString(client.GetText(r));
        var attributed = Send(Send(Class("NSAttributedString"), Sel("alloc")), Sel("initWithString:"), s);
        Release(s);
        return Send(attributed, Sel("autorelease"));
    }

    /// <summary>输入法一次查询的文本长度上限（防止对超长文档的整段复制）。</summary>
    const int MaxQueryLength = 1 << 16;

    /// <summary>
    /// 范围第一行的屏幕矩形（候选窗定位）：客户区逻辑坐标（y 向下）→ 视图坐标（y 向上）→ 窗口坐标 → 屏幕坐标。
    /// </summary>
    internal unsafe NSRect FirstRect(NSRange range, IntPtr actual)
    {
        var client = Ime.Client;
        if (client is null || _closed) return default;
        var r = IsValid(range) ? ToRange(range).Clamp(client.Length) : client.Selection;
        if (actual != IntPtr.Zero) *(NSRange*)actual = new NSRange { Location = (ulong)r.Start, Length = (ulong)r.Length };
        var rect = client.GetTextBounds(r);
        return ClientToScreen(rect);
    }

    /// <summary>客户区逻辑矩形 → 屏幕坐标（AppKit 全局坐标，原点在主屏左下）。</summary>
    internal NSRect ClientToScreen(Rect rect)
    {
        double height = ClientSize.Height;
        var local = new NSRect(rect.X, height - rect.Y - rect.Height, Math.Max(0, rect.Width), Math.Max(0, rect.Height));
        var inWindow = SendRect(_view, Sel("convertRect:toView:"), local, IntPtr.Zero);
        return SendRect(_window, Sel("convertRectToScreen:"), inWindow);
    }

    internal static IntPtr EmptyArray() => Send(Class("NSArray"), Sel("array"));
}
