using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using AwAUI.Platform;
using AwAUI.Text;

namespace AwAUI.UI;

// 输入框的编辑：输入法客户端的接入、编辑键、剪贴板、指针定位与选择。
public sealed partial class InputManager
{
    TextInputMethod? _textInput;
    InputTextClient? _activeClient;

    // 最近一次通知平台时的状态（只在变化时调用 NotifyStateChanged）
    bool _notifyPending;
    TextInputRange _notifiedSelection;
    TextInputRange? _notifiedComposition;
    Rect _notifiedCaret;
    int _notifiedLength;

    // 指针选择
    InputElement? _dragInput;
    long _dragPointer = long.MinValue;
    int _dragMode;
    int _dragAnchorStart, _dragAnchorEnd;
    InputElement? _lastClickInput;
    TimeSpan _lastClickTime;
    Point _lastClickPosition;
    int _clickCount;

    /// <summary>多击（双击选词、三击全选）的最大间隔与位移。</summary>
    static readonly TimeSpan MultiClickInterval = TimeSpan.FromMilliseconds(500);
    const float MultiClickDistance = 4;

    /// <summary>预编辑文本的长度上限（UTF-16 码元）；更长的部分被截断，不切开代理对。</summary>
    public const int MaxCompositionLength = 4096;

    /// <summary>
    /// 窗口的输入法桥（<see cref="WindowImpl.TextInput"/>，宿主设置）。焦点所在的输入框作为客户端交给它；
    /// 为 null 时输入框只经 <see cref="DispatchTextInput"/> 接收文本。
    /// </summary>
    public TextInputMethod? TextInputMethod
    {
        get => _textInput;
        set
        {
            if (ReferenceEquals(_textInput, value)) return;
            _textInput?.SetClient(null);
            _textInput = value;
            _activeClient = null;
            if (value is not null && _root.FocusManager.FocusedElement is InputElement input) AttachClient(input);
        }
    }

    /// <summary>剪贴板（宿主设置为 <see cref="WindowImpl.Clipboard"/>）；为 null 时复制、剪切、粘贴快捷键不做任何事。</summary>
    public Clipboard? Clipboard { get; set; }

    /// <summary>当前交给输入法桥的输入框（诊断与测试用）。</summary>
    public InputElement? TextInputTarget => _activeClient?.Element;

    // ------------------------------------------------------------ 焦点与客户端

    /// <summary>焦点进入 <paramref name="element"/>（焦点事件派发之前）。</summary>
    internal void OnFocusEntered(Element element)
    {
        RevealFocus(element);
        if (element is not InputElement input) return;
        // 键盘 / 程序聚焦：插入点在末尾（保留已有的非空选区，例如程序先 SelectAll 再聚焦）
        if (input.SelectionStart == input.SelectionEnd) input.SetSelectionCore(input.Value.Length, input.Value.Length);
        AttachClient(input);
    }

    void AttachClient(InputElement input)
    {
        _activeClient = input.TextInputClient;
        _notifyPending = true;
        if (_textInput is null) return;
        _textInput.SetClient(_activeClient);
        if (!input.ReadOnly && !input.IsDisabled) _textInput.Show();
    }

    void DetachClient()
    {
        _activeClient = null;
        if (_textInput is null) return;
        _textInput.SetClient(null);
        _textInput.Hide();
    }

    void OnInputFocusLeaving(InputElement input)
    {
        // 与浏览器一致：失去焦点时按原样提交预编辑文本
        if (input.IsComposing) FinishComposition(input, commit: true, notifyPlatform: true);
        if (ReferenceEquals(_activeClient?.Element, input)) DetachClient();
        if (ReferenceEquals(_dragInput, input)) _dragInput = null;
    }

    void OnTextSubtreeRemoved(Element removed)
    {
        if (_activeClient is { } c && removed.IsInclusiveAncestorOf(c.Element))
        {
            // 焦点随子树一起静默消失（不派发事件）：预编辑文本也静默丢弃，元素以后被重新插入时不会显示过期的组合
            if (c.Element.IsComposing)
            {
                int at = c.Element.CompositionStart;
                c.Element.SetCompositionState("", 0, 0, 0);
                c.Element.SetSelectionCore(at, at);
            }
            DetachClient();
        }
        if (_dragInput is not null && removed.IsInclusiveAncestorOf(_dragInput)) _dragInput = null;
        if (_lastClickInput is not null && removed.IsInclusiveAncestorOf(_lastClickInput)) _lastClickInput = null;
    }

    bool IsActive(InputElement e) => ReferenceEquals(_activeClient?.Element, e);

    /// <summary>值被程序改变而放弃了组合：让平台也放弃。</summary>
    internal void OnCompositionDiscarded(InputElement e)
    {
        if (IsActive(e)) _textInput?.ResetComposition();
    }

    /// <summary>输入类型变化（如切换为密码框）：重新交给输入法桥以应用新选项。</summary>
    internal void OnTextInputOptionsChanged(InputElement e)
    {
        if (!IsActive(e) || _textInput is null) return;
        if (e.IsComposing) FinishComposition(e, commit: true, notifyPlatform: true);
        _textInput.SetClient(null);
        _textInput.SetClient(e.TextInputClient);
        _notifyPending = true;
    }

    /// <summary>
    /// 每帧（布局与绘制之后）由宿主调用：客户端的选区、组合或插入点矩形变化时通知平台（输入法据此移动候选窗）。
    /// </summary>
    public void AfterFrame()
    {
        if (_activeClient is not { } c || _textInput is not { } tim) return;
        if (!c.Element.IsAttached) return;
        var sel = c.Selection;
        var comp = c.Composition;
        var caret = c.GetCaretRect();
        int len = c.Length;
        if (!_notifyPending && sel == _notifiedSelection && comp == _notifiedComposition && caret == _notifiedCaret && len == _notifiedLength) return;
        _notifyPending = false;
        _notifiedSelection = sel;
        _notifiedComposition = comp;
        _notifiedCaret = caret;
        _notifiedLength = len;
        tim.NotifyStateChanged();
    }

    // ------------------------------------------------------------ 编辑操作（都在派发入口之内调用）

    /// <summary>结束组合：<paramref name="commit"/> 为 true 时预编辑文本按原样成为值的一部分（派发 <c>input</c>），否则丢弃。</summary>
    internal void FinishComposition(InputElement e, bool commit, bool notifyPlatform = true)
    {
        if (!e.IsComposing) return;
        string text = e.CompositionText;
        int start = e.CompositionStart;
        e.SetCompositionState("", 0, 0, 0);
        if (commit && !e.ReadOnly && !e.IsDisabled) ReplaceValueRange(e, start, start, text, keepComposition: false);
        else e.SetSelectionCore(start, start);
        if (notifyPlatform && IsActive(e)) _textInput?.ResetComposition();
    }

    /// <summary>输入法设置预编辑文本（空文本即取消组合）。组合开始时先删除选中的文本。</summary>
    internal void UpdateComposition(InputElement e, string text, int selStart, int selEnd)
    {
        text = Truncate(text, MaxCompositionLength);
        if (text.Length == 0)
        {
            if (!e.IsComposing) return;
            int at = e.CompositionStart;
            e.SetCompositionState("", 0, 0, 0);
            e.SetSelectionCore(at, at);
            return;
        }
        int start;
        if (e.IsComposing) start = e.CompositionStart;
        else
        {
            if (e.SelectionStart != e.SelectionEnd) ReplaceValueRange(e, e.SelectionStart, e.SelectionEnd, "", keepComposition: false);
            start = e.SelectionStart;
        }
        if (!e.IsAttached) return;
        e.SetSelectionCore(start, start);
        e.SetCompositionState(text, start, selStart, selEnd);
    }

    /// <summary>提交文本：替换预编辑文本（没有组合时替换选区），派发 <c>input</c>。</summary>
    internal void CommitText(InputElement e, string text)
    {
        if (e.IsComposing)
        {
            int start = e.CompositionStart;
            e.SetCompositionState("", 0, 0, 0);
            ReplaceValueRange(e, start, start, text, keepComposition: false);
            return;
        }
        ReplaceValueRange(e, e.SelectionStart, e.SelectionEnd, text, keepComposition: false);
    }

    /// <summary>把值中的 [start, end) 替换为 <paramref name="text"/>（单行清洗），插入点移到新文本之后；值变化时派发 <c>input</c>。</summary>
    internal void ReplaceValueRange(InputElement e, int start, int end, string text, bool keepComposition)
    {
        text = TextEditing.SanitizeSingleLine(text);
        string v = e.Value;
        start = TextEditing.Snap(v, start);
        end = TextEditing.Snap(v, end);
        if (end < start) (start, end) = (end, start);
        // 结果长度受剪贴板上限约束（Clipboard.MaxTextLength），防止重复粘贴无界增长
        if ((long)v.Length - (end - start) + text.Length > Platform.Clipboard.MaxTextLength)
            text = Truncate(text, Math.Max(0, Platform.Clipboard.MaxTextLength - (v.Length - (end - start))));
        string nv = string.Concat(v.Substring(0, start), text, v.Substring(end));
        int caret = start + text.Length;
        bool changed;
        if (keepComposition && e.IsComposing)
        {
            string comp = e.CompositionText;
            int cs = e.CompositionStart, ss = e.CompositionSelectionStart, se = e.CompositionSelectionEnd;
            if (cs >= end) cs += text.Length - (end - start);
            else if (cs > start) cs = caret;
            changed = e.SetEditState(nv, cs, cs);
            e.SetCompositionState(comp, cs, ss, se);
        }
        else
        {
            changed = e.SetEditState(nv, caret, caret);
        }
        if (changed && e.IsAttached) Dispatch(e, new InputEvent("input", e.Value), EventKind.Input, null);
    }

    static string Truncate(string s, int max)
    {
        if (s.Length <= max) return s;
        int n = max;
        if (n > 0 && char.IsHighSurrogate(s[n - 1])) n--;
        return s.Substring(0, n);
    }

    // ------------------------------------------------------------ 编辑键

    /// <summary>输入框上的按下默认动作；返回 true 表示已处理（不再执行通用默认动作）。</summary>
    bool EditingKeyAction(InputElement input, in RawKeyEvent raw, string logicalKey)
    {
        var mods = raw.Modifiers;
        bool mac = Shortcuts.UseMacConventions;
        bool shift = (mods & KeyModifiers.Shift) != 0;
        bool editable = !input.IsDisabled && !input.ReadOnly;
        // 组合中的按键属于输入法（平台通常不会转来，防御性地忽略编辑键）
        bool composing = input.IsComposing;

        if (Shortcuts.IsPrimaryShortcut(mods))
        {
            switch (Shortcuts.ShortcutKey(raw.Key, logicalKey))
            {
                case PhysicalKey.KeyA:
                    if (!composing) input.SetSelectionCore(0, input.Value.Length);
                    return true;
                case PhysicalKey.KeyC:
                    if (!composing && !(_textInput?.HandlesClipboardShortcuts ?? false)) Copy(input, cut: false);
                    return true;
                case PhysicalKey.KeyX:
                    if (!composing && editable && !(_textInput?.HandlesClipboardShortcuts ?? false)) Copy(input, cut: true);
                    return true;
                case PhysicalKey.KeyV:
                    if (!composing && editable && !(_textInput?.HandlesClipboardShortcuts ?? false)) Paste(input);
                    return true;
            }
        }

        // 按词移动 / 删除：macOS 为 Option，其他平台为 Ctrl；macOS 上 Cmd 是到行首 / 行尾
        bool word = mac ? (mods & KeyModifiers.Alt) != 0 : (mods & KeyModifiers.Control) != 0;
        bool line = mac && (mods & KeyModifiers.Meta) != 0;
        string v = input.Value;
        int s = input.SelectionStart, e = input.SelectionEnd, focus = input.SelectionFocus, anchor = input.SelectionAnchor;
        switch (EditingKey(raw.Key, logicalKey))
        {
            case PhysicalKey.Enter:
            case PhysicalKey.NumpadEnter:
                if (composing) return true;
                if (!input.IsDisabled) Dispatch(input, new SubmitEvent(input.Value), EventKind.Submit, null);
                return true;
            case PhysicalKey.Backspace:
            case PhysicalKey.Delete:
            {
                if (composing || !editable) return true;
                bool back = EditingKey(raw.Key, logicalKey) == PhysicalKey.Backspace;
                if (s == e)
                {
                    if (back)
                        s = line ? 0 : word ? (input.IsPassword ? 0 : TextEditing.PreviousWordStart(v, s)) : TextEditing.PreviousGrapheme(v, s);
                    else
                        e = line ? v.Length : word ? (input.IsPassword ? v.Length : TextEditing.NextWordEnd(v, e)) : TextEditing.NextGrapheme(v, e);
                }
                if (s != e) ReplaceValueRange(input, s, e, "", keepComposition: false);
                return true;
            }
            case PhysicalKey.ArrowLeft:
            case PhysicalKey.ArrowRight:
            case PhysicalKey.Home:
            case PhysicalKey.End:
            case PhysicalKey.ArrowUp:
            case PhysicalKey.ArrowDown:
            {
                if (composing) return true;
                if ((mods & KeyModifiers.Control) != 0 && mac) return false; // macOS 的 Ctrl+方向键属于系统（调度中心）
                var nav = EditingKey(raw.Key, logicalKey);
                bool forward = nav is PhysicalKey.ArrowRight or PhysicalKey.End or PhysicalKey.ArrowDown;
                int target;
                if (nav is PhysicalKey.Home or PhysicalKey.End or PhysicalKey.ArrowUp or PhysicalKey.ArrowDown || line)
                    target = forward ? v.Length : 0;
                else if (!shift && s != e)
                    target = forward ? e : s; // 有选区时不带 Shift 的左右键折叠到选区一端
                else if (word)
                    target = input.IsPassword ? (forward ? v.Length : 0) : forward ? TextEditing.NextWordEnd(v, focus) : TextEditing.PreviousWordStart(v, focus);
                else
                    target = forward ? TextEditing.NextGrapheme(v, focus) : TextEditing.PreviousGrapheme(v, focus);
                if (shift) input.SetSelectionCore(anchor, target);
                else input.SetSelectionCore(target, target);
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// 编辑键按哪个键处理：一般是物理键；小键盘在 NumLock 关闭时产生导航与删除键（物理键仍是 Numpad7 等，逻辑键是 Home 等，
    /// 与 W3C 的 code / key 一致），这时按逻辑键处理。NumLock 打开时逻辑键是数字，不是编辑键。
    /// </summary>
    static PhysicalKey EditingKey(PhysicalKey physical, string logicalKey)
    {
        if (!PhysicalKeys.IsNumpad(physical) || physical == PhysicalKey.NumpadEnter) return physical;
        return logicalKey switch
        {
            "ArrowLeft" => PhysicalKey.ArrowLeft,
            "ArrowRight" => PhysicalKey.ArrowRight,
            "ArrowUp" => PhysicalKey.ArrowUp,
            "ArrowDown" => PhysicalKey.ArrowDown,
            "Home" => PhysicalKey.Home,
            "End" => PhysicalKey.End,
            "PageUp" => PhysicalKey.PageUp,
            "PageDown" => PhysicalKey.PageDown,
            "Delete" => PhysicalKey.Delete,
            "Insert" => PhysicalKey.Insert,
            _ => physical,
        };
    }

    // ------------------------------------------------------------ 剪贴板

    void Copy(InputElement input, bool cut)
    {
        if (input.IsPassword || input.SelectionStart == input.SelectionEnd) return;
        var clipboard = Clipboard;
        if (clipboard is null) return; // 没有剪贴板时剪切也不删除内容（避免丢失）
        string text = input.SelectedText;
        Task<bool> write;
        try
        {
            write = clipboard.SetTextAsync(text);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return; // 剪贴板不可用：剪切也不删除内容
        }
        Observe(write);
        // 已经知道写入失败（同步完成且为 false / 出错）时剪切不删除，否则选中的文本会丢失；
        // 还没完成的写入无法等待（与浏览器的剪切一致，照常删除）
        if (write.IsCompleted && (write.Status != TaskStatus.RanToCompletion || !write.Result)) return;
        if (cut) ReplaceValueRange(input, input.SelectionStart, input.SelectionEnd, "", keepComposition: false);
    }

    void Paste(InputElement input)
    {
        var clipboard = Clipboard;
        if (clipboard is null) return;
        Task<string?> task;
        try
        {
            task = clipboard.GetTextAsync();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return;
        }
        if (task.IsCompleted)
        {
            if (task.Status == TaskStatus.RanToCompletion && task.Result is { Length: > 0 } now) PasteInto(input, now);
            return;
        }
        var dispatcher = _root.Dispatcher;
        task.ContinueWith(t =>
        {
            if (t.Status != TaskStatus.RanToCompletion || t.Result is not { Length: > 0 } text) return;
            if (dispatcher is null) return;
            dispatcher.Post(() =>
            {
                // 读取期间焦点或元素可能已经变了：只粘贴到仍然获得焦点的同一个输入框
                if (!input.IsAttached || !ReferenceEquals(_root.FocusManager.FocusedElement, input)) return;
                _root.BeginDispatch();
                try
                {
                    PasteInto(input, text);
                }
                finally
                {
                    _root.EndDispatch();
                }
            });
        }, TaskScheduler.Default);
    }

    void PasteInto(InputElement input, string text)
    {
        if (input.IsDisabled || input.ReadOnly) return;
        if (input.IsComposing) FinishComposition(input, commit: true, notifyPlatform: true);
        ReplaceValueRange(input, input.SelectionStart, input.SelectionEnd, Platform.Clipboard.LimitLength(text), keepComposition: false);
    }

    static void Observe(Task task)
    {
        if (task.IsCompleted) { _ = task.Exception; return; }
        task.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously);
    }

    // ------------------------------------------------------------ 指针

    void TextPointerDown(InputElement input, in RawPointerEvent raw)
    {
        if (raw.Device == PointerType.Mouse && raw.ChangedButton != PointerButtons.Left) return;
        if (input.IsComposing) FinishComposition(input, commit: true, notifyPlatform: true);
        if (!input.IsAttached) return;
        // 多击：只按平台给出的真实时间戳判断（合成事件的时间戳为 0，不会被当成双击）
        var dt = raw.Timestamp - _lastClickTime;
        bool repeat = ReferenceEquals(_lastClickInput, input) && raw.Timestamp > TimeSpan.Zero && _lastClickTime > TimeSpan.Zero
            && dt >= TimeSpan.Zero && dt <= MultiClickInterval
            && Math.Abs(raw.Position.X - _lastClickPosition.X) <= MultiClickDistance && Math.Abs(raw.Position.Y - _lastClickPosition.Y) <= MultiClickDistance;
        _clickCount = repeat ? Math.Min(_clickCount + 1, 3) : 1;
        _lastClickInput = input;
        _lastClickTime = raw.Timestamp;
        _lastClickPosition = raw.Position;

        int offset = HitTestInput(input, raw.Position);
        if (offset < 0) offset = input.Value.Length;
        string v = input.Value;
        switch (_clickCount)
        {
            case 1:
                if ((raw.Modifiers & KeyModifiers.Shift) != 0) input.SetSelectionCore(input.SelectionAnchor, offset);
                else input.SetSelectionCore(offset, offset);
                _dragAnchorStart = _dragAnchorEnd = input.SelectionAnchor;
                break;
            case 2:
            {
                var (ws, we) = input.IsPassword ? (0, v.Length) : TextEditing.WordAt(v, offset);
                input.SetSelectionCore(ws, we);
                _dragAnchorStart = ws;
                _dragAnchorEnd = we;
                break;
            }
            default:
                input.SetSelectionCore(0, v.Length);
                break;
        }
        _dragInput = input;
        _dragPointer = raw.PointerId;
        _dragMode = _clickCount;
        SetCapture(raw.PointerId, input);
    }

    void TextPointerMove(in RawPointerEvent raw)
    {
        var input = _dragInput;
        if (input is null || raw.PointerId != _dragPointer) return;
        if (!input.IsAttached || !ReferenceEquals(_root.FocusManager.FocusedElement, input) || raw.Buttons == PointerButtons.None && raw.Device == PointerType.Mouse)
        {
            _dragInput = null;
            return;
        }
        int offset = HitTestInput(input, raw.Position);
        if (offset < 0) return;
        string v = input.Value;
        switch (_dragMode)
        {
            case 1:
                input.SetSelectionCore(_dragAnchorStart, offset);
                break;
            case 2:
                if (input.IsPassword) break;
                if (offset < _dragAnchorStart) input.SetSelectionCore(_dragAnchorEnd, TextEditing.WordAt(v, offset).Start);
                else input.SetSelectionCore(_dragAnchorStart, Math.Max(_dragAnchorEnd, TextEditing.WordAt(v, Math.Max(0, offset - 1)).End));
                break;
        }
    }

    void TextPointerUp(long pointerId)
    {
        if (pointerId == _dragPointer) _dragInput = null;
    }

    /// <summary>窗口坐标 → 输入框值中的下标；段落还没有按当前内容绘制过时返回 -1。</summary>
    internal int HitTestInput(InputElement input, Point position)
    {
        if (input.Value.Length == 0 && !input.IsComposing) return 0;
        if (!TryGetFreshLayout(input, out var tl, out var box, out var doc)) return -1;
        var local = new Point(position.X - box.Border.X - input.TextOriginLocal.X, position.Y - box.Border.Y - input.TextOriginLocal.Y);
        var tp = tl.HitTest(local);
        int docOffset = TextEditing.FromDisplay(doc, tp.Offset, input.IsPassword);
        return input.DocumentToValue(docOffset);
    }

    /// <summary>输入框段落的布局仍然对应当前内容（绘制时记录的显示文本与当前一致）。</summary>
    static bool TryGetFreshLayout(InputElement input, out TextLayout layout, out RenderBox box, out string document)
    {
        layout = null!;
        box = null!;
        document = input.DisplayText;
        if (!input.HasPaintedGeometry || input.Render is not { HasBox: true } b || b.Paragraph is not { } para) return false;
        string expected = input.IsPassword ? TextEditing.Mask(document) : document;
        if (expected.Length == 0 || !string.Equals(input.PaintedText, expected, StringComparison.Ordinal)
            || !string.Equals(para.Layout.Text, expected, StringComparison.Ordinal)) return false;
        layout = para.Layout;
        box = b;
        return true;
    }

    /// <summary>文档范围（含预编辑文本）第一行部分的窗口坐标矩形；布局过期时退回最近一次绘制的插入点或内容盒左缘。</summary>
    internal Rect GetTextBounds(InputElement input, TextInputRange range)
    {
        if (input.Render is not { HasBox: true } box) return default;
        if (TryGetFreshLayout(input, out var tl, out _, out var doc))
        {
            bool pw = input.IsPassword;
            int ds = TextEditing.ToDisplay(doc, range.Start, pw), de = TextEditing.ToDisplay(doc, range.End, pw);
            float ox = box.Border.X + input.TextOriginLocal.X, oy = box.Border.Y + input.TextOriginLocal.Y;
            Rect r;
            if (ds >= de)
            {
                r = tl.GetCaretRect(new TextPosition(ds, TextAffinity.Upstream));
                r = new Rect(r.X, r.Y, Math.Max(1, r.Width), r.Height);
            }
            else
            {
                var rects = new List<Rect>();
                tl.GetSelectionRects(new TextRange(ds, de), rects);
                r = rects.Count > 0 ? rects[0] : tl.GetCaretRect(new TextPosition(ds));
            }
            return new Rect(ox + r.X, oy + r.Y, r.Width, r.Height);
        }
        if (input.HasPaintedGeometry)
            return new Rect(box.Border.X + input.CaretLocal.X, box.Border.Y + input.CaretLocal.Y, Math.Max(1, input.CaretLocal.Width), input.CaretLocal.Height);
        return new Rect(box.ContentX, box.ContentY, 1, Math.Max(1, box.ContentHeight));
    }
}
