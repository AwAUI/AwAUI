using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using AwAUI.Css;
using AwAUI.Css.Values;
using AwAUI.Graphics;
using AwAUI.Platform;
using AwAUI.Text;

namespace AwAUI.UI;

/// <summary>
/// 只读文本选择（design/05「文本选择」）：非编辑元素（<see cref="Text"/>、<see cref="Span"/>、裸文本）也能用鼠标拖选，
/// 选中后按主修饰键 + C 复制。选择以「段落 + 字符偏移」表达，可跨段落；<c>user-select: none</c> 的片段不参与
/// （行号、按钮文字等），<c>user-select: all</c> 的片段按下即整段选中。
/// <para>
/// 与 <see cref="InputElement"/> 的选区相互独立：输入框沿用自身的选区、剪贴板与输入法路径；本类只处理其余文本。
/// 选中区域在段落绘制时画在字形之下，选择变化只重绘涉及的元素。
/// </para>
/// </summary>
public sealed class TextSelection
{
    const int MultiClickMilliseconds = 500;
    const float MultiClickDistance = 4;

    readonly UIRoot _root;
    readonly List<Fragment> _fragments = new();
    readonly List<Element> _walk = new();
    readonly Dictionary<Element, (int Start, int End)> _ranges = new();
    readonly List<Element> _oldRangeElements = new();

    Element? _anchorElement;
    Element? _focusElement;
    int _anchorOffset;
    int _focusOffset;

    long _version;
    long _rangesVersion = -1;
    long _emptyCheckedFrame = -1;

    bool _dragging;
    long _dragPointer = long.MinValue;
    Element? _captureElement;
    int _dragMode;
    Element? _dragElement;
    int _dragWordStart;
    int _dragWordEnd;

    Element? _lastClickElement;
    Point _lastClickPosition;
    TimeSpan _lastClickTime;
    int _clickCount;

    internal TextSelection(UIRoot root) => _root = root;

    /// <summary>是否有非空的文本选区。</summary>
    public bool HasSelection => _anchorElement is not null && _focusElement is not null
        && (!ReferenceEquals(_anchorElement, _focusElement) || _anchorOffset != _focusOffset);

    /// <summary>Selection version; incremented on every selection change. The accessibility semantic tree rebuilds when it changes.</summary>
    public long SelectionVersion => _version;

    /// <summary>
    /// Gets the selection of a paragraph element relative to its text (clamped to the current text length).
    /// Returns false when there is no selection or the element is not part of it. Used for accessibility
    /// TextPattern mapping; input controls expose their own selection through <see cref="InputElement"/>.
    /// </summary>
    public bool TryGetSelectionRange(Element element, out int start, out int end)
    {
        if (element is null)
        {
            start = end = 0;
            return false;
        }
        return TryGetRange(element, out start, out end);
    }

    /// <summary>选中的文本。同一行内的片段直接相连，不同行之间插入换行。</summary>
    public string SelectedText
    {
        get
        {
            CollectFragments();
            if (_anchorElement is null || _focusElement is null) return "";
            int ai = IndexOf(_anchorElement), fi = IndexOf(_focusElement);
            if (ai < 0 || fi < 0) return "";
            int ao = Clamp(_anchorOffset, 0, _fragments[ai].Text.Length);
            int fo = Clamp(_focusOffset, 0, _fragments[fi].Text.Length);
            if (ai > fi || (ai == fi && ao > fo))
            {
                (ai, fi) = (fi, ai);
                (ao, fo) = (fo, ao);
            }

            var sb = new StringBuilder();
            int lastContributed = -1;
            for (int i = ai; i <= fi; i++)
            {
                var fragment = _fragments[i];
                int start = i == ai ? ao : 0;
                int end = i == fi ? fo : fragment.Text.Length;
                if (start >= end) continue;
                if (lastContributed >= 0 && !OnSameLine(_fragments[lastContributed], fragment)) sb.Append('\n');
                sb.Append(fragment.Text, start, end - start);
                lastContributed = i;
            }
            return sb.ToString();
        }
    }

    /// <summary>清除选区（会重绘原来选中的段落）。</summary>
    public void Clear()
    {
        EndDrag();
        if (_anchorElement is null && _focusElement is null) return;
        EnsureRanges();
        _oldRangeElements.Clear();
        _oldRangeElements.AddRange(_ranges.Keys);

        _anchorElement = _focusElement = null;
        _anchorOffset = _focusOffset = 0;
        _version++;
        EnsureRanges();
        RepaintChanged();
    }

    /// <summary>选中根下全部可选择文本。</summary>
    public void SelectAll()
    {
        CollectFragments();
        if (_fragments.Count == 0)
        {
            Clear();
            return;
        }
        EnsureRanges();
        _oldRangeElements.Clear();
        _oldRangeElements.AddRange(_ranges.Keys);

        _anchorElement = _fragments[0].Element;
        _anchorOffset = 0;
        _focusElement = _fragments[_fragments.Count - 1].Element;
        _focusOffset = _fragments[_fragments.Count - 1].Text.Length;
        _version++;
        EnsureRanges();
        RepaintChanged();
    }

    /// <summary>键盘默认动作（主修饰键 + C / A、Esc）；返回是否已处理。</summary>
    internal bool HandleKey(in RawKeyEvent raw, string? logicalKey)
    {
        if (raw.Type != RawKeyEventType.Down) return false;
        if (raw.Key == PhysicalKey.Escape)
        {
            if (!HasSelection) return false;
            Clear();
            return true;
        }
        if (!Shortcuts.IsPrimaryShortcut(raw.Modifiers)) return false;
        switch (Shortcuts.ShortcutKey(raw.Key, logicalKey))
        {
            case PhysicalKey.KeyA:
                SelectAll();
                return true;
            case PhysicalKey.KeyC:
                if (!HasSelection) return false;
                Copy(_root.Input.Clipboard);
                return true;
            default:
                return false;
        }
    }

    void Copy(Clipboard? clipboard)
    {
        if (clipboard is null) return;
        var task = clipboard.SetTextAsync(SelectedText);
        if (task.IsCompleted)
        {
            _ = task.Exception;
            return;
        }
        task.ContinueWith(static t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously);
    }

    // ------------------------------------------------------------ 指针

    /// <summary>指针按下（调用方已确认目标不是可获得焦点的控件）：开始选择、Shift 扩展或清除。</summary>
    internal void PointerDown(in RawPointerEvent raw, Element target)
    {
        if (raw.Device == PointerType.Touch) return; // 触摸的拖选（长按 + 手柄）尚未实现
        if (raw.Device == PointerType.Mouse && raw.ChangedButton != PointerButtons.Left) return;
        if (_root.Pipeline is not RenderPipeline pipeline)
        {
            Clear();
            return;
        }

        EndDrag();
        EnsureRanges();
        _oldRangeElements.Clear();
        _oldRangeElements.AddRange(_ranges.Keys);

        if (!TryResolve(target, raw.Position, forStart: true, out var fragment, out int offset))
        {
            Clear();
            return;
        }

        int clickCount = NextClickCount(raw, fragment.Element);
        bool shift = (raw.Modifiers & KeyModifiers.Shift) != 0;
        if (shift && _anchorElement is not null)
        {
            _focusElement = fragment.Element;
            _focusOffset = offset;
            _dragMode = 1;
        }
        else if (pipeline.GetUsedUserSelect(fragment.Element) == CssUserSelect.All)
        {
            // user-select: all：按下即选中整段（再拖动由 PointerMove 继续扩展）
            _anchorElement = _focusElement = fragment.Element;
            _anchorOffset = 0;
            _focusOffset = fragment.Text.Length;
            _dragMode = 3;
            _dragElement = fragment.Element;
        }
        else
        {
            switch (clickCount)
            {
                case 2:
                {
                    var (ws, we) = TextEditing.WordAt(fragment.Text, offset);
                    _anchorElement = _focusElement = fragment.Element;
                    _anchorOffset = ws;
                    _focusOffset = we;
                    _dragMode = 2;
                    _dragElement = fragment.Element;
                    _dragWordStart = ws;
                    _dragWordEnd = we;
                    break;
                }
                case 3:
                    _anchorElement = _focusElement = fragment.Element;
                    _anchorOffset = 0;
                    _focusOffset = fragment.Text.Length;
                    _dragMode = 3;
                    break;
                default:
                    _anchorElement = _focusElement = fragment.Element;
                    _anchorOffset = _focusOffset = offset;
                    _dragMode = 1;
                    break;
            }
        }

        _version++;
        EnsureRanges();
        RepaintChanged();

        _dragging = true;
        _dragPointer = raw.PointerId;
        _captureElement = fragment.Element;
        if (fragment.Element.IsAttached) _root.Input.SetCapture(raw.PointerId, fragment.Element);
    }

    /// <summary>指针移动：拖动时把插入点移到指针处的文本位置。</summary>
    internal void PointerMove(in RawPointerEvent raw)
    {
        if (!_dragging || raw.PointerId != _dragPointer) return;
        if (raw.Device == PointerType.Mouse && raw.Buttons == PointerButtons.None)
        {
            EndDrag();
            return;
        }
        if (_root.Pipeline is not RenderPipeline pipeline || _anchorElement is null) return;
        var hit = pipeline.HitTest(raw.Position);
        if (!TryResolve(hit, raw.Position, forStart: false, out var fragment, out int offset)) return;

        int anchorOffset = _anchorOffset;
        int focusOffset = offset;
        Element anchorElement = _anchorElement;
        Element focusElement = fragment.Element;
        if (_dragMode == 2 && ReferenceEquals(fragment.Element, _dragElement))
        {
            // 双击后拖动：按词扩展（与输入框一致）
            if (offset < _dragWordStart)
            {
                focusElement = anchorElement = fragment.Element;
                anchorOffset = _dragWordEnd;
                focusOffset = TextEditing.WordAt(fragment.Text, offset).Start;
            }
            else
            {
                focusOffset = Math.Max(_dragWordEnd, TextEditing.WordAt(fragment.Text, Math.Max(0, offset - 1)).End);
            }
        }
        else if (_dragMode == 3 && ReferenceEquals(fragment.Element, _dragElement))
        {
            // 三击后拖动：整段成为选区
            anchorOffset = 0;
            focusOffset = fragment.Text.Length;
        }

        if (ReferenceEquals(_anchorElement, anchorElement) && _focusElement == focusElement
            && _anchorOffset == anchorOffset && _focusOffset == focusOffset) return;

        EnsureRanges();
        _oldRangeElements.Clear();
        _oldRangeElements.AddRange(_ranges.Keys);

        _anchorElement = anchorElement;
        _anchorOffset = anchorOffset;
        _focusElement = focusElement;
        _focusOffset = focusOffset;
        _version++;
        EnsureRanges();
        RepaintChanged();
    }

    internal void PointerUp(long pointerId)
    {
        if (_dragging && pointerId == _dragPointer) EndDrag();
    }

    internal void OnSubtreeRemoved(Element removed)
    {
        if (_anchorElement is not null && removed.IsInclusiveAncestorOf(_anchorElement)
            || _focusElement is not null && removed.IsInclusiveAncestorOf(_focusElement))
            Clear();
    }

    /// <summary>选中段落的内容变化：偏移失效，按浏览器的做法取消选区。</summary>
    internal void OnTextChanged(Element changed)
    {
        if (_anchorElement is null || _focusElement is null) return;
        if (_anchorElement.IsInclusiveAncestorOf(changed) || _focusElement.IsInclusiveAncestorOf(changed))
            Clear();
    }

    void EndDrag()
    {
        if (_captureElement is not null && _dragPointer != long.MinValue)
            _root.Input.ReleaseCapture(_dragPointer, _captureElement);
        _captureElement = null;
        _dragging = false;
        _dragPointer = long.MinValue;
        _dragMode = 0;
        _dragElement = null;
    }

    int NextClickCount(in RawPointerEvent raw, Element element)
    {
        bool repeat = ReferenceEquals(_lastClickElement, element) && raw.Timestamp > TimeSpan.Zero && _lastClickTime > TimeSpan.Zero
            && raw.Timestamp - _lastClickTime >= TimeSpan.Zero
            && raw.Timestamp - _lastClickTime <= TimeSpan.FromMilliseconds(MultiClickMilliseconds)
            && Math.Abs(raw.Position.X - _lastClickPosition.X) <= MultiClickDistance
            && Math.Abs(raw.Position.Y - _lastClickPosition.Y) <= MultiClickDistance;
        _clickCount = repeat ? Math.Min(_clickCount + 1, 3) : 1;
        _lastClickElement = element;
        _lastClickTime = raw.Timestamp;
        _lastClickPosition = raw.Position;
        return _clickCount;
    }

    // ------------------------------------------------------------ 片段与命中

    readonly struct Fragment
    {
        public Fragment(Element element, RenderBox box, TextLayout layout)
        {
            Element = element;
            Box = box;
            Layout = layout;
            Text = layout.Text;
        }

        public readonly Element Element;
        public readonly RenderBox Box;
        public readonly TextLayout Layout;
        public readonly string Text;
    }

    /// <summary>帧管线绘制段落之前取本元素的选区（相对段落文本的偏移，已按当前文本长度截断）。</summary>
    internal bool TryGetRange(Element element, out int start, out int end)
    {
        start = end = 0;
        if (!HasSelection) return false;
        EnsureRanges();
        if (!_ranges.TryGetValue(element, out var range)) return false;
        int length = element.Render?.Paragraph?.Layout.Text.Length ?? 0;
        start = Clamp(range.Start, 0, length);
        end = Clamp(range.End, start, length);
        return start < end;
    }

    void EnsureRanges()
    {
        if (_rangesVersion == _version)
        {
            // 选区还在，但所有片段都暂时不可见（display:none 的子树）：每帧再试一次，重新可见时恢复高亮；
            // 同一帧内只重算一次，避免每个段落各遍历一遍整棵树
            if (_ranges.Count > 0 || _anchorElement is null || _emptyCheckedFrame == _root.FrameNumber) return;
            _emptyCheckedFrame = _root.FrameNumber;
        }
        _rangesVersion = _version;
        _ranges.Clear();
        if (!HasSelection) return;

        CollectFragments();
        int ai = IndexOf(_anchorElement!), fi = IndexOf(_focusElement!);
        if (ai < 0 || fi < 0) return;
        int ao = Clamp(_anchorOffset, 0, _fragments[ai].Text.Length);
        int fo = Clamp(_focusOffset, 0, _fragments[fi].Text.Length);
        if (ai > fi || (ai == fi && ao > fo))
        {
            (ai, fi) = (fi, ai);
            (ao, fo) = (fo, ao);
        }
        for (int i = ai; i <= fi; i++)
        {
            int start = i == ai ? ao : 0;
            int end = i == fi ? fo : _fragments[i].Text.Length;
            if (start < end) _ranges[_fragments[i].Element] = (start, end);
        }
    }

    int IndexOf(Element element)
    {
        for (int i = 0; i < _fragments.Count; i++)
            if (ReferenceEquals(_fragments[i].Element, element)) return i;
        return -1;
    }

    static bool OnSameLine(in Fragment a, in Fragment b)
    {
        float at = a.Box.ContentY, ab = at + a.Box.ContentHeight;
        float bt = b.Box.ContentY, bb = bt + b.Box.ContentHeight;
        return bt < ab - 0.5f && bb > at + 0.5f;
    }

    /// <summary>收集根下可选择、已布局的段落（文档顺序）。</summary>
    void CollectFragments()
    {
        _fragments.Clear();
        if (_root.Pipeline is not RenderPipeline pipeline) return;
        _walk.Clear();
        _walk.Add(_root.RootElement);
        while (_walk.Count > 0)
        {
            var e = _walk[_walk.Count - 1];
            _walk.RemoveAt(_walk.Count - 1);
            if (!e.IsAttached) continue;
            var box = e.Render;
            if (box is { HasBox: true, Paragraph: { IsEmpty: false } para }
                && e is not InputElement && e is not GeneratedContentElement
                && box.Style is { } style && style.Display != CssDisplay.None && style.Visibility == CssVisibility.Visible)
            {
                var access = pipeline.GetUsedUserSelect(e);
                if (access is CssUserSelect.Text or CssUserSelect.All)
                {
                    _fragments.Add(new Fragment(e, box, para.Layout));
                    continue; // 段落的后代是行内 run，没有独立的盒
                }
            }
            var children = e.Children;
            for (int i = children.Count - 1; i >= 0; i--) _walk.Add(children[i]);
        }
    }

    /// <summary>
    /// 将指针位置解析为片段与偏移。<paramref name="forStart"/> 为 true 时遵循 <c>user-select: none</c>（不从不允许的元素开始选择）；
    /// 拖动时总是取最近的文本片段（可以拖出原始段落、越过行号与内边距）。
    /// </summary>
    bool TryResolve(Element? hit, Point position, bool forStart, out Fragment fragment, out int offset)
    {
        fragment = default;
        offset = 0;
        CollectFragments();
        if (_fragments.Count == 0) return false;
        var pipeline = (RenderPipeline)_root.Pipeline!;

        if (hit is not null)
        {
            if (hit.IsTextNode && hit.Parent is { } parent) hit = parent;
            if (hit is InputElement || AncestorInput(hit) is not null) return false;
            if (forStart && pipeline.GetUsedUserSelect(hit) == CssUserSelect.None) return false;
            for (var e = hit; e is not null; e = e.Parent)
            {
                for (int i = 0; i < _fragments.Count; i++)
                {
                    if (!ReferenceEquals(_fragments[i].Element, e)) continue;
                    fragment = _fragments[i];
                    offset = OffsetAt(fragment, position);
                    return true;
                }
            }
        }

        int best = Nearest(position);
        if (best < 0) return false;
        fragment = _fragments[best];
        // 按在非文本元素上（按钮似的 Div、图标）：只有当它包含最近的文本片段时才允许从空白处开始选择。
        // 否则不抢指针捕获 —— 否则随后的 click 会被合成到相邻的文本元素上，目标控件永远收不到点击。
        if (hit is not null && !ReferenceEquals(fragment.Element, hit) && !hit.IsInclusiveAncestorOf(fragment.Element))
            return false;
        offset = OffsetAt(fragment, position);
        return true;
    }

    static InputElement? AncestorInput(Element element)
    {
        for (var e = element; e is not null; e = e.Parent)
            if (e is InputElement input) return input;
        return null;
    }

    static int OffsetAt(in Fragment fragment, Point position)
    {
        var local = new Point(position.X - fragment.Box.ContentX, position.Y - fragment.Box.ContentY);
        var tp = fragment.Layout.HitTest(local);
        return Clamp(tp.Offset, 0, fragment.Text.Length);
    }

    /// <summary>离点最近的片段（按到内容盒的欧氏距离）。</summary>
    int Nearest(Point position)
    {
        int best = -1;
        float bestDistance = float.PositiveInfinity;
        for (int i = 0; i < _fragments.Count; i++)
        {
            var b = _fragments[i].Box;
            float dx = position.X < b.ContentX ? b.ContentX - position.X
                : position.X > b.ContentX + b.ContentWidth ? position.X - (b.ContentX + b.ContentWidth) : 0;
            float dy = position.Y < b.ContentY ? b.ContentY - position.Y
                : position.Y > b.ContentY + b.ContentHeight ? position.Y - (b.ContentY + b.ContentHeight) : 0;
            float distance = dx * dx + dy * dy;
            if (distance >= bestDistance) continue;
            bestDistance = distance;
            best = i;
        }
        return best;
    }

    void RepaintChanged()
    {
        // 原来与新选中的片段集合可能重叠：重复标脏无副作用（标记是幂等的）
        foreach (var element in _oldRangeElements) MarkSelectionDirty(element);
        foreach (var element in _ranges.Keys) MarkSelectionDirty(element);
        _oldRangeElements.Clear();
    }

    static void MarkSelectionDirty(Element element)
    {
        element.MarkDirty(ElementDirtyFlags.Paint);
        // 匿名文本由父元素的录制单元绘制（容器遍历子节点调用 PaintParagraph）
        if (element.IsTextNode && element.Parent is { } parent) parent.MarkDirty(ElementDirtyFlags.Paint);
    }

    static int Clamp(int value, int min, int max) => value < min ? min : value > max ? max : value;
}
