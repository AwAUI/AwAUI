using System;
using AwAUI.Platform;

namespace AwAUI.UI;

/// <summary>
/// <c>input</c> 元素：单行文本编辑。
/// <list type="bullet">
/// <item>选区：锚点与焦点（UTF-16 下标，落在字素簇边界上）；空选区即插入点。键盘（方向、Home/End、Shift 扩展、按词移动）、
/// 指针（点击定位、拖动选择、双击选词、三击全选）与 <see cref="SetSelectionRange"/> 都改变它。</item>
/// <item>输入法：作为 <see cref="ITextInputClient"/> 接到窗口的 <see cref="TextInputMethod"/>（获得焦点时）；预编辑文本插在
/// <see cref="CompositionStart"/> 处、带下划线显示，不计入 <see cref="Value"/>，提交后才成为值的一部分并派发 <c>input</c>。</item>
/// <item>剪贴板：Ctrl/⌘ + C / X / V / A（逻辑键匹配，见 <see cref="Shortcuts"/>）。密码框不能复制、剪切。</item>
/// <item>值被程序改变（受控值）时插入点移到末尾并放弃进行中的组合。</item>
/// </list>
/// </summary>
public sealed class InputElement : Element
{
    string _value = "";
    string? _valueAtFocus;
    int _anchor, _focus;
    string _composition = "";
    int _compositionStart, _compositionSelStart, _compositionSelEnd;
    InputTextClient? _client;

    public InputElement() : base("input") => SetState(PseudoStates.PlaceholderShown, false);

    /// <summary>已提交的文本（不含预编辑文本）。</summary>
    public string Value => _value;

    public string? Placeholder { get; private set; }

    public string? InputType { get; private set; }

    public bool ReadOnly { get; private set; }

    /// <summary>是否为密码框（<c>type="password"</c>）：显示为圆点、关闭输入法组合、不能复制。</summary>
    public bool IsPassword => string.Equals(InputType, "password", StringComparison.OrdinalIgnoreCase);

    /// <summary>选区起点（≤ <see cref="SelectionEnd"/>，<see cref="Value"/> 中的下标）。</summary>
    public int SelectionStart => Math.Min(_anchor, _focus);

    /// <summary>选区终点。</summary>
    public int SelectionEnd => Math.Max(_anchor, _focus);

    /// <summary>选区的锚点（扩展选区时不动的一端）。</summary>
    public int SelectionAnchor => _anchor;

    /// <summary>选区的焦点（插入点所在的一端）。</summary>
    public int SelectionFocus => _focus;

    /// <summary>选中的文本（密码框返回空串）。</summary>
    public string SelectedText => IsPassword ? "" : _value.Substring(SelectionStart, SelectionEnd - SelectionStart);

    /// <summary>输入法正在组合（有预编辑文本）。</summary>
    public bool IsComposing => _composition.Length > 0;

    /// <summary>预编辑文本；没有组合时为空串。</summary>
    public string CompositionText => _composition;

    /// <summary>预编辑文本插入在 <see cref="Value"/> 中的位置。</summary>
    public int CompositionStart => _compositionStart;

    /// <summary>预编辑文本内输入法给出的光标 / 选区（相对预编辑文本起点）。</summary>
    public int CompositionSelectionStart => _compositionSelStart;

    /// <inheritdoc cref="CompositionSelectionStart"/>
    public int CompositionSelectionEnd => _compositionSelEnd;

    public override bool IsFocusableByDefault => true;

    public override bool CanBeDisabled => true;

    /// <summary>显示的文本：值，组合中时在 <see cref="CompositionStart"/> 处插入预编辑文本（即输入法客户端的「文档」）。</summary>
    internal string DisplayText => _composition.Length == 0 ? _value : _value.Insert(_compositionStart, _composition);

    /// <summary>本元素作为文本输入客户端（获得焦点时交给窗口的输入法桥）。</summary>
    internal InputTextClient TextInputClient => _client ??= new InputTextClient(this);

    internal InputTextClient? TextInputClientOrNull => _client;

    // ---------------- 绘制阶段记录的局部几何（相对边框盒原点，逻辑像素），供命中测试与候选窗定位

    /// <summary>文本水平滚动量（内容比内容盒宽时让插入点保持可见）。</summary>
    internal float ScrollX;

    /// <summary>段落原点与插入点矩形（相对边框盒原点）；<see cref="HasPaintedGeometry"/> 为 false 时尚未绘制过。</summary>
    internal Point TextOriginLocal;
    internal Rect CaretLocal;
    internal bool HasPaintedGeometry;

    /// <summary>绘制时段落的显示文本（命中测试据此判断布局是否仍然对应当前内容）。</summary>
    internal string? PaintedText;

    protected override string? GetReflectedAttribute(string name) => name switch
    {
        "value" => _value,
        "placeholder" => Placeholder,
        "type" => InputType ?? "text",
        "disabled" => IsDisabled ? "" : null,
        "readonly" => ReadOnly ? "" : null,
        "selectionStart" => SelectionStart.ToString(System.Globalization.CultureInfo.InvariantCulture),
        "selectionEnd" => SelectionEnd.ToString(System.Globalization.CultureInfo.InvariantCulture),
        _ => base.GetReflectedAttribute(name),
    };

    /// <summary>
    /// 设置选区（DOM <c>setSelectionRange</c>）：下标被限制在值内并对齐到代理对边界。<paramref name="anchor"/> 为锚点、
    /// <paramref name="focus"/> 为插入点所在的一端。组合中调用时先按原样提交预编辑文本。
    /// </summary>
    public void SetSelectionRange(int anchor, int focus)
    {
        if (IsComposing) Root?.Input.FinishComposition(this, commit: true);
        SetSelectionCore(anchor, focus);
    }

    /// <summary>选中全部文本。</summary>
    public void SelectAll() => SetSelectionRange(0, _value.Length);

    internal void SetSelectionCore(int anchor, int focus)
    {
        anchor = TextEditing.Snap(_value, anchor);
        focus = TextEditing.Snap(_value, focus);
        if (anchor == _anchor && focus == _focus) return;
        _anchor = anchor;
        _focus = focus;
        MarkDirty(ElementDirtyFlags.Paint);
    }

    /// <summary>编辑产生的新值与选区（不派发事件；由 <see cref="InputManager"/> 派发 <c>input</c>）。返回值是否变化。</summary>
    internal bool SetEditState(string value, int anchor, int focus)
    {
        bool changed = !string.Equals(_value, value, StringComparison.Ordinal);
        if (changed)
        {
            _value = value;
            UpdatePlaceholderShown();
            MarkDirty(ElementDirtyFlags.Layout | ElementDirtyFlags.Paint);
            Root?.NotifyPropertyChanged(this, "value");
        }
        SetSelectionCore(anchor, focus);
        return changed;
    }

    /// <summary>设置预编辑状态（<paramref name="text"/> 为空即结束组合）。</summary>
    internal void SetCompositionState(string text, int start, int selStart, int selEnd)
    {
        text ??= "";
        start = text.Length == 0 ? 0 : TextEditing.Snap(_value, start);
        selStart = Math.Max(0, Math.Min(selStart, text.Length));
        selEnd = Math.Max(selStart, Math.Min(selEnd, text.Length));
        if (string.Equals(_composition, text, StringComparison.Ordinal) && start == _compositionStart
            && selStart == _compositionSelStart && selEnd == _compositionSelEnd) return;
        _composition = text;
        _compositionStart = start;
        _compositionSelStart = text.Length == 0 ? 0 : selStart;
        _compositionSelEnd = text.Length == 0 ? 0 : selEnd;
        UpdatePlaceholderShown();
        MarkDirty(ElementDirtyFlags.Layout | ElementDirtyFlags.Paint);
    }

    /// <summary>值被程序（受控值）改变：插入点移到末尾，放弃进行中的组合。</summary>
    internal void SetValue(string value)
    {
        value ??= "";
        if (string.Equals(_value, value, StringComparison.Ordinal)) return;
        if (IsComposing)
        {
            SetCompositionState("", 0, 0, 0);
            Root?.Input.OnCompositionDiscarded(this);
        }
        SetEditState(value, value.Length, value.Length);
    }

    internal void SetPlaceholder(string? placeholder)
    {
        if (string.Equals(Placeholder, placeholder, StringComparison.Ordinal)) return;
        Placeholder = placeholder;
        UpdatePlaceholderShown();
        PropertyChanged("placeholder", ElementDirtyFlags.Style | ElementDirtyFlags.Layout | ElementDirtyFlags.Paint | ElementDirtyFlags.Semantics);
    }

    internal void SetInputType(string? type)
    {
        if (string.Equals(InputType, type, StringComparison.Ordinal)) return;
        InputType = type;
        // 类型影响显示（密码圆点）与输入法选项：重建段落，焦点所在时重新交给输入法桥
        PropertyChanged("type", ElementDirtyFlags.Style | ElementDirtyFlags.Layout | ElementDirtyFlags.Paint | ElementDirtyFlags.Semantics);
        Root?.Input.OnTextInputOptionsChanged(this);
    }

    internal void SetDisabled(bool disabled)
    {
        if (IsDisabled == disabled) return;
        SetState(PseudoStates.Disabled, disabled);
        PropertyChanged("disabled", ElementDirtyFlags.Semantics);
    }

    internal void SetReadOnly(bool readOnly)
    {
        if (ReadOnly == readOnly) return;
        ReadOnly = readOnly;
        // 只读输入框不画光标：需要重绘
        PropertyChanged("readonly", ElementDirtyFlags.Style | ElementDirtyFlags.Paint | ElementDirtyFlags.Semantics);
    }

    void UpdatePlaceholderShown() =>
        SetState(PseudoStates.PlaceholderShown, _value.Length == 0 && _composition.Length == 0 && !string.IsNullOrEmpty(Placeholder));

    /// <summary>获得焦点：记录值（失焦时比较以派发 change）。</summary>
    internal void OnFocused() => _valueAtFocus = _value;

    /// <summary>失去焦点时若内容变化则返回 true（触发 change）。</summary>
    internal bool TakeChangedSinceFocus()
    {
        bool changed = _valueAtFocus is not null && !string.Equals(_valueAtFocus, _value, StringComparison.Ordinal);
        _valueAtFocus = null;
        return changed;
    }

    /// <summary>
    /// 输入法客户端文档中的下标（含预编辑文本）→ 值中的下标：落在预编辑文本内的映射到它的起点，之后的减去预编辑文本长度。
    /// </summary>
    internal int DocumentToValue(int offset)
    {
        if (_composition.Length == 0 || offset <= _compositionStart) return Math.Min(Math.Max(0, offset), _value.Length);
        if (offset <= _compositionStart + _composition.Length) return _compositionStart;
        return Math.Min(offset - _composition.Length, _value.Length);
    }

    /// <summary>值中的下标 → 文档下标（在预编辑文本插入点之后的位置加上它的长度）。</summary>
    internal int ValueToDocument(int offset)
    {
        if (_composition.Length == 0 || offset < _compositionStart) return offset;
        if (offset == _compositionStart) return offset;
        return offset + _composition.Length;
    }
}
