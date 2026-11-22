using System;
using System.Collections.Generic;
using System.Text;

namespace AwAUI.UI.Accessibility;

/// <summary>
/// 语义节点状态（design/10「语义树」的状态列）。取值是位标记，可同时表示
/// 「可聚焦且已选中」等组合；未知/不适用的位为 0。
/// </summary>
[Flags]
public enum AccessibilityState
{
    None = 0,

    /// <summary>禁用。</summary>
    Disabled = 1 << 0,

    /// <summary>已选中（复选框、单选、开关）。</summary>
    Checked = 1 << 1,

    /// <summary>混合状态（三态复选框）。</summary>
    Mixed = 1 << 2,

    /// <summary>已选中（列表项、页签、选项）。</summary>
    Selected = 1 << 3,

    /// <summary>已展开。</summary>
    Expanded = 1 << 4,

    /// <summary>按下（切换按钮）。</summary>
    Pressed = 1 << 5,

    /// <summary>值无效。</summary>
    Invalid = 1 << 6,

    /// <summary>忙碌。</summary>
    Busy = 1 << 7,

    /// <summary>隐藏（<c>aria-hidden</c>；通常整棵子树被剔除，仅根等特殊情况保留）。</summary>
    Hidden = 1 << 8,

    /// <summary>持有焦点。</summary>
    Focused = 1 << 9,

    /// <summary>必填。</summary>
    Required = 1 << 10,

    /// <summary>只读。</summary>
    ReadOnly = 1 << 11,

    /// <summary>多选。</summary>
    MultiSelectable = 1 << 12,

    /// <summary>实时区域。</summary>
    LiveRegion = 1 << 13,
}

/// <summary>
/// 语义节点上可用的动作。动作通过元素现有的 API（焦点管理、点击派发、输入框值、滚动控制器）执行，
/// 不重新实现控件行为。
/// </summary>
[Flags]
public enum AccessibilityAction
{
    None = 0,

    /// <summary>激活（<c>click</c>）。</summary>
    Invoke = 1 << 0,

    /// <summary>聚焦。</summary>
    Focus = 1 << 1,

    /// <summary>切换。</summary>
    Toggle = 1 << 2,

    /// <summary>设置可编辑控件的值。</summary>
    SetValue = 1 << 3,

    /// <summary>增加一步。</summary>
    Increment = 1 << 4,

    /// <summary>减少一步。</summary>
    Decrement = 1 << 5,

    /// <summary>滚动到可见。</summary>
    ScrollIntoView = 1 << 6,

    /// <summary>展开。</summary>
    Expand = 1 << 7,

    /// <summary>折叠。</summary>
    Collapse = 1 << 8,
}

/// <summary>实时区域的播报优先级。</summary>
public enum AccessibilityLiveRegion
{
    Off,
    Polite,
    Assertive,
}

/// <summary>Modifier keys required by an access key (mnemonic).</summary>
[Flags]
public enum AccessKeyModifiers
{
    /// <summary>No modifier required.</summary>
    None = 0,

    /// <summary>Alt (Windows-style <c>&amp;X</c> mnemonics).</summary>
    Alt = 1 << 0,

    /// <summary>Ctrl.</summary>
    Ctrl = 1 << 1,

    /// <summary>Shift.</summary>
    Shift = 1 << 2,

    /// <summary>Meta / Command / Windows key.</summary>
    Meta = 1 << 3,
}

/// <summary>
/// TextPattern semantics for read-only static text (design/10): the text value, the read-only flag and
/// the selection range. Editable controls use <see cref="AccessibilityTextInfo"/> instead, which also
/// carries IME composition state.
/// </summary>
public sealed class AccessibilityTextPatternInfo
{
    internal AccessibilityTextPatternInfo(string value, int selectionStart, int selectionEnd)
    {
        Value = value;
        SelectionStart = selectionStart;
        SelectionEnd = selectionEnd;
        int start = Math.Max(0, Math.Min(selectionStart, value.Length));
        int end = Math.Max(start, Math.Min(selectionEnd, value.Length));
        SelectedText = end > start ? value.Substring(start, end - start) : "";
    }

    /// <summary>The full text (including text from child nodes).</summary>
    public string Value { get; }

    /// <summary>Static text is always read-only.</summary>
    public bool IsReadOnly => true;

    /// <summary>Selection start (UTF-16 index, at most <see cref="SelectionEnd"/>).</summary>
    public int SelectionStart { get; }

    /// <summary>Selection end (exclusive).</summary>
    public int SelectionEnd { get; }

    /// <summary>The selected text; empty when there is no selection.</summary>
    public string SelectedText { get; }

    /// <summary>Whether there is a non-empty selection.</summary>
    public bool HasSelection => SelectionEnd > SelectionStart;
}

/// <summary>
/// 可编辑控件的文本信息（design/10「文本信息」）：值、选区与输入法组合状态。
/// 直接映射 <see cref="InputElement"/> 的公共文本 API；非可编辑控件为 null。
/// </summary>
public sealed class AccessibilityTextInfo
{
    internal AccessibilityTextInfo(InputElement input)
    {
        Value = input.Value;
        SelectionStart = input.SelectionStart;
        SelectionEnd = input.SelectionEnd;
        SelectionAnchor = input.SelectionAnchor;
        SelectionFocus = input.SelectionFocus;
        SelectedText = input.SelectedText;
        IsComposing = input.IsComposing;
        CompositionText = input.CompositionText;
        CompositionStart = input.CompositionStart;
        CompositionSelectionStart = input.CompositionSelectionStart;
        CompositionSelectionEnd = input.CompositionSelectionEnd;
    }

    /// <summary>已提交的文本（不含预编辑文本）。</summary>
    public string Value { get; }

    /// <summary>选区起点（≤ <see cref="SelectionEnd"/>）。</summary>
    public int SelectionStart { get; }

    /// <summary>选区终点。</summary>
    public int SelectionEnd { get; }

    /// <summary>选区的锚点。</summary>
    public int SelectionAnchor { get; }

    /// <summary>选区的焦点（插入点所在的一端）。</summary>
    public int SelectionFocus { get; }

    /// <summary>选中的文本；密码框为空串。</summary>
    public string SelectedText { get; }

    /// <summary>输入法组合是否进行中。</summary>
    public bool IsComposing { get; }

    /// <summary>预编辑文本。</summary>
    public string CompositionText { get; }

    /// <summary>预编辑文本插入在值中的位置。</summary>
    public int CompositionStart { get; }

    /// <summary>预编辑文本内的选区起点。</summary>
    public int CompositionSelectionStart { get; }

    /// <summary>预编辑文本内的选区终点。</summary>
    public int CompositionSelectionEnd { get; }
}

/// <summary>
/// 语义树节点（design/10）。由 <see cref="AccessibilityTree"/> 按需从元素树构建；
/// 每次构建都是快照，元素树再次变化后应重新读取 <see cref="AccessibilityTree.Root"/>。
/// </summary>
public sealed class AccessibilityNode
{
    readonly List<AccessibilityNode> _children = new();

    internal AccessibilityNode(string id, Element element, AccessibilityTree tree)
    {
        Id = id;
        Element = element;
        Tree = tree;
    }

    /// <summary>稳定标识：元素显式 <c>id</c>，否则由元素唯一序号生成（<c>el-123</c>）。</summary>
    public string Id { get; }

    /// <summary>产生该节点的元素。</summary>
    public Element Element { get; }

    internal AccessibilityTree Tree { get; }

    /// <summary>ARIA 角色名（<c>button</c>、<c>checkbox</c>、<c>listitem</c> ...）。</summary>
    public string Role { get; internal set; } = "";

    /// <summary>可访问名称，按 ARIA 的名称计算规则得出。</summary>
    public string? Name { get; internal set; }

    /// <summary>描述。</summary>
    public string? Description { get; internal set; }

    /// <summary>当前值（输入框、滑块、进度条等）。</summary>
    public string? Value { get; internal set; }

    /// <summary>状态标记。</summary>
    public AccessibilityState State { get; internal set; }

    /// <summary>可用动作。</summary>
    public AccessibilityAction Actions { get; internal set; }

    /// <summary>屏幕（窗口客户区）坐标下的边界，已计入滚动与 CSS 变换。</summary>
    public Rect Bounds { get; internal set; }

    /// <summary>父节点；根为 null。</summary>
    public AccessibilityNode? Parent { get; internal set; }

    /// <summary>子节点，按树序。</summary>
    public IReadOnlyList<AccessibilityNode> Children => _children;

    /// <summary><c>aria-labelledby</c> 引用的节点标识。</summary>
    public IReadOnlyList<string> LabelledBy { get; internal set; } = Array.Empty<string>();

    /// <summary><c>aria-controls</c> 引用的节点标识。</summary>
    public IReadOnlyList<string> Controls { get; internal set; } = Array.Empty<string>();

    /// <summary><c>aria-describedby</c> 引用的节点标识。</summary>
    public IReadOnlyList<string> DescribedBy { get; internal set; } = Array.Empty<string>();

    /// <summary>实时区域的播报优先级。</summary>
    public AccessibilityLiveRegion LiveRegion { get; internal set; }

    /// <summary>标题层级（1–6），非标题为 null。</summary>
    public int? HeadingLevel { get; internal set; }

    /// <summary>虚拟化列表中的位置（1 基，<c>aria-posinset</c>）。</summary>
    public int? PosInSet { get; internal set; }

    /// <summary>虚拟化列表的总数（<c>aria-setsize</c>）。</summary>
    public int? SetSize { get; internal set; }

    /// <summary>Whether this node is static text (<c>Text</c>, paragraphs, etc.); <see cref="TextPattern"/> may then be non-null.</summary>
    public bool IsStaticText { get; internal set; }

    /// <summary>TextPattern semantics for read-only static text; null for other nodes.</summary>
    public AccessibilityTextPatternInfo? TextPattern { get; internal set; }

    /// <summary>Access key (single uppercase character); null when absent. Duplicate keys are exposed only on the first node.</summary>
    public string? AccessKey { get; internal set; }

    /// <summary>Modifiers required by <see cref="AccessKey"/>.</summary>
    public AccessKeyModifiers AccessKeyModifiers { get; internal set; }

    /// <summary>Node id referenced by <c>aria-activedescendant</c>; null when absent.</summary>
    public string? ActiveDescendant { get; internal set; }

    /// <summary>The <c>aria-haspopup</c> popup type (<c>menu</c>, <c>listbox</c>, <c>tree</c>, <c>grid</c>, <c>dialog</c>); null when absent.</summary>
    public string? HasPopup { get; internal set; }

    /// <summary>The <c>aria-current</c> token (<c>page</c>, <c>step</c>, <c>location</c>, <c>date</c>, <c>time</c>, <c>true</c>); null when absent.</summary>
    public string? Current { get; internal set; }

    /// <summary><c>aria-orientation</c> (<c>horizontal</c> / <c>vertical</c>); null when absent.</summary>
    public string? Orientation { get; internal set; }

    /// <summary><c>aria-level</c> (tree depth, heading level, etc.); null when absent. Heading levels are also exposed via <see cref="HeadingLevel"/>.</summary>
    public int? Level { get; internal set; }

    /// <summary><c>aria-valuemin</c> or the control's <c>min</c> attribute.</summary>
    public double? ValueMin { get; internal set; }

    /// <summary><c>aria-valuemax</c> or the control's <c>max</c> attribute.</summary>
    public double? ValueMax { get; internal set; }

    /// <summary>The control's <c>step</c> attribute (value-range increment).</summary>
    public double? ValueStep { get; internal set; }

    /// <summary>The raw <c>aria-keyshortcuts</c> value, exposed as an attribute only; no global shortcut is registered.</summary>
    public string? KeyShortcuts { get; internal set; }

    /// <summary><c>aria-modal</c>: whether a dialog is modal.</summary>
    public bool IsModal { get; internal set; }

    /// <summary><c>aria-expanded</c>; null when the attribute is absent.</summary>
    public bool? Expanded { get; internal set; }

    /// <summary><c>aria-pressed</c>; null when absent or <c>mixed</c> (see <see cref="State"/> for <c>mixed</c>).</summary>
    public bool? Pressed { get; internal set; }

    /// <summary><c>aria-selected</c>; null when the attribute is absent.</summary>
    public bool? Selected { get; internal set; }

    /// <summary><c>aria-checked</c>; null when absent or <c>mixed</c> (see <see cref="State"/> for <c>mixed</c>).</summary>
    public bool? Checked { get; internal set; }

    /// <summary><c>aria-busy</c>; null when the attribute is absent.</summary>
    public bool? Busy { get; internal set; }

    /// <summary>The <c>aria-invalid</c> token (<c>true</c>, <c>grammar</c>, <c>spelling</c>); null when not invalid.</summary>
    public string? Invalid { get; internal set; }

    /// <summary><c>aria-atomic</c>: whether a live region should be announced as a whole.</summary>
    public bool Atomic { get; internal set; }

    /// <summary>The raw <c>aria-relevant</c> value; null when absent.</summary>
    public string? Relevant { get; internal set; }

    /// <summary>可编辑控件的文本信息；非可编辑控件为 null。</summary>
    public AccessibilityTextInfo? Text { get; internal set; }

    internal void AddChild(AccessibilityNode child)
    {
        child.Parent = this;
        _children.Add(child);
    }

    /// <summary>
    /// 执行一个动作。动作必须列在 <see cref="Actions"/> 中；禁用状态下除滚动到可见外返回 false。
    /// 通过元素现有的行为执行（点击派发、焦点管理、输入框值、滚动控制器），不复制控件逻辑。
    /// </summary>
    public bool Perform(AccessibilityAction action, string? value = null) => Tree.Perform(this, action, value);

    public override string ToString()
    {
        var sb = new StringBuilder(Role);
        if (!string.IsNullOrEmpty(Name)) sb.Append(" \"").Append(Name).Append('"');
        if (Id.Length > 0) sb.Append(" #").Append(Id);
        return sb.ToString();
    }
}
