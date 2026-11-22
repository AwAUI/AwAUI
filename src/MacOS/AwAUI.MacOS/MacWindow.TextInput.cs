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
    bool _commandHandled;     // 这一键由 doCommandBySelector: 处理（取消组合、组合内移动等），不再交给框架
    bool _markedText;         // 输入法有进行中的预编辑文本（密码框的客户端不保存它，但仍据此判断提交）
    readonly StringBuilder _keyText = new();

    /// <summary>放弃平台侧组合状态（见 <see cref="MacTextInput"/>）：密码框的预编辑不进入客户端文档，只能在这里清除。</summary>
    internal void ClearMarkedText() => _markedText = false;

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
        _commandHandled = false;
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
        // 输入法处理的按键不交给框架（与浏览器的 isComposing 一致）。组合在这一键结束时才结束的
        // 提交 / 换行命令不再算「输入法处理的按键」，照常交给框架；取消与组合内移动已由命令处理。
        if (_commandHandled || _imeChanged || (composingBefore && Ime.Client?.Composition is not null)) return;
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

    /// <summary>
    /// <c>doCommandBySelector:</c>：输入法（以及 Cocoa 的文本键绑定）要求客户端执行的编辑命令。参数是命令的 SEL。
    /// 只实现与组合相关的语义，其余命令保持空实现以避免 NSResponder 的提示音：
    /// <list type="bullet">
    /// <item><c>cancelOperation:</c>：丢弃预编辑文本，不提交（<see cref="ITextInputClient.SetComposition"/> 的空文本约定）。</item>
    /// <item><c>insertNewline:</c> / <c>insertTab:</c>：结束组合并提交预编辑文本；有组合时这一键由命令消费，
    /// 没有组合时照常交给框架（单行框回车提交、Tab 切换焦点）。</item>
    /// <item><c>moveLeft:</c> / <c>moveRight:</c> / <c>moveUp:</c> / <c>moveDown:</c>：组合期间移动客户端的选区
    /// （平台没有文本布局，只按 <see cref="MacInputLogic.MoveCompositionSelection"/> 移动一个位置）；没有组合时留给框架的按键处理。</item>
    /// </list>
    /// </summary>
    internal void HandleCommand(IntPtr command)
    {
        if (command == IntPtr.Zero || DiscardingMarkedText) return;
        var client = Ime.Client;
        if (client is null) return;

        if (command == Sel("cancelOperation:"))
        {
            bool composing = client.Composition is not null || _markedText;
            _markedText = false;
            if (!composing) return; // 没有组合：Escape 照常交给框架
            client.SetComposition("", default);
            if (_inKeyDown) _commandHandled = true;
            return;
        }

        if (command == Sel("insertNewline:") || command == Sel("insertTab:"))
        {
            if (client.Composition is { } composed)
            {
                client.Commit(client.GetText(composed));
                _markedText = false;
                if (_inKeyDown) _commandHandled = true; // 这一键用于结束组合，不再触发提交 / 焦点移动
                return;
            }
            if (_markedText)
            {
                // 密码框的预编辑不进入客户端文档，无法按原样提交；结束组合状态并消费这一键，避免提交表单
                _markedText = false;
                if (_inKeyDown) _commandHandled = true;
            }
            return; // 没有组合：按键照常交给框架
        }

        bool? forward = command == Sel("moveRight:") || command == Sel("moveDown:") ? true
            : command == Sel("moveLeft:") || command == Sel("moveUp:") ? false
            : null;
        if (forward is not { } direction || client.Composition is not { } composition) return;
        client.Selection = MacInputLogic.MoveCompositionSelection(client.Selection.Clamp(client.Length), composition, direction);
        if (_inKeyDown) _commandHandled = true;
    }

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
        if (client.Composition is not null || _markedText)
        {
            // 输入法提交：替换预编辑文本（密码框没有登记的预编辑，Commit 直接插入到当前选区）
            _markedText = false;
            if (_inKeyDown) _imeChanged = true;
            client.Commit(text);
            return;
        }
        if (IsValid(replacement))
        {
            // 密码框不登记预编辑文本（客户端遮罩预编辑），输入法的替换范围对应文档中不存在的文本；
            // 候选确认的文本按客户端语义插入到当前选区，而不是替换一个错误的范围。
            if (client.Options.IsPassword) client.Commit(text);
            else client.ReplaceRange(ToRange(replacement).Clamp(client.Length), text);
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
        if (client is null) return;
        var text = TextOf(obj) ?? "";
        _markedText = text.Length > 0; // 空文本表示取消组合
        if (_inKeyDown) _imeChanged = true;
        // 密码框：客户端不保存预编辑文本（预编辑由 UI 层遮罩），输入法的替换范围在文档中不存在；
        // 不把 replacement 当成选区、也不调用 SetComposition，候选确认的文本在 InsertText 里经 Commit 插入。
        if (client.Options.IsPassword) return;
        if (client.Composition is null && IsValid(replacement))
            client.Selection = ToRange(replacement).Clamp(client.Length); // 重新转换：预编辑替换这段已提交的文本
        // selected 是预编辑文本内的范围（相对于预编辑起点），原样交给客户端，不退化到末尾光标
        var sel = IsValid(selected) ? ToRange(selected) : TextInputRange.Caret(text.Length);
        client.SetComposition(text, sel);
    }

    internal void UnmarkText()
    {
        _markedText = false;
        if (DiscardingMarkedText) return;
        var client = Ime.Client;
        if (client?.Composition is not { } comp) return;
        client.Commit(client.GetText(comp)); // 按原样提交预编辑文本
    }

    // 密码框的预编辑不进入客户端文档（UI 层遮罩），所以 markedRange / hasMarkedText 对它报告「没有组合」；
    // 输入法的提交仍经 insertText 到达，并按 _markedText 判断为组合提交（见 InsertText）。
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

    /// <summary>屏幕坐标（AppKit 全局坐标，原点在主屏左下）→ 客户区逻辑坐标（y 向下）。</summary>
    internal Point ScreenToClient(NSPoint point)
    {
        var inWindow = SendRect(_window, Sel("convertRectFromScreen:"), new NSRect(point.X, point.Y, 0, 0));
        var local = SendRect(_view, Sel("convertRect:fromView:"), inWindow, IntPtr.Zero);
        return new Point((float)local.Origin.X, (float)(ClientSize.Height - local.Origin.Y));
    }

    /// <summary>
    /// <c>characterIndexForPoint:</c>：屏幕坐标 → 最近的插入点（文档下标）。平台桥没有字形测量与行布局，插入点矩形由客户端的
    /// <see cref="ITextInputClient.GetTextBounds"/> 提供，<see cref="MacInputLogic.CharacterIndex"/> 对下标做二分并比较前后两点；
    /// 因此折行处与从右到左文本只是按矩形顺序的近似。没有客户端（焦点不在可编辑元素上）时返回 <see cref="Callbacks.NotFound"/>。
    /// </summary>
    internal ulong CharacterIndex(NSPoint point)
    {
        var client = Ime.Client;
        if (client is null || _closed) return Callbacks.NotFound;
        return (ulong)MacInputLogic.CharacterIndex(client, ScreenToClient(point));
    }

    internal static IntPtr EmptyArray() => Send(Class("NSArray"), Sel("array"));
}
