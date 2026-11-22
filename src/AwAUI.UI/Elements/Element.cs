using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;

namespace AwAUI.UI;

/// <summary>
/// 由框架维护、供伪类匹配的元素状态。位定义与 <c>AwAUI.Css.Selectors.PseudoStates</c> 保持一致，
/// 样式集成时可直接按位转换。
/// </summary>
[Flags]
public enum PseudoStates
{
    None = 0,
    Hover = 1 << 0,
    Active = 1 << 1,
    Focus = 1 << 2,
    FocusVisible = 1 << 3,
    FocusWithin = 1 << 4,
    Disabled = 1 << 5,
    Checked = 1 << 6,
    PlaceholderShown = 1 << 7,
}

/// <summary>元素的脏标记：帧管线的样式、布局、绘制、语义（无障碍）阶段只处理带标记的元素。</summary>
[Flags]
public enum ElementDirtyFlags : byte
{
    None = 0,

    /// <summary>选择器输入变化（id、class、属性、伪状态、内联样式、子元素结构），需要重算样式。</summary>
    Style = 1 << 0,

    /// <summary>需要重新布局（子元素结构、文本、固有尺寸变化）。</summary>
    Layout = 1 << 1,

    /// <summary>需要重录 DisplayList。</summary>
    Paint = 1 << 2,

    /// <summary>无障碍语义（角色、名称、Tab 顺序）变化。</summary>
    Semantics = 1 << 3,

    All = Style | Layout | Paint | Semantics,
}

/// <summary>
/// 保留的元素（相当于 DOM 节点）。由协调器创建与修改，应用代码通过 <see cref="Ref{T}"/> 获得它做命令式操作。
/// 只能在所属 <see cref="UIRoot"/> 的 UI 线程上访问。
/// <para>
/// 成员命名刻意与 <c>AwAUI.Css.Selectors.StyleElement</c> 对齐（<see cref="Tag"/>、<see cref="Id"/>、<see cref="Classes"/>、
/// <see cref="HasClass"/>、<see cref="GetAttribute"/>、<see cref="States"/>、<see cref="IsEmpty"/>、<see cref="CanBeDisabled"/>、
/// <see cref="GetChildIndex"/>、<see cref="GetChildIndexFromEnd"/>），样式集成时由一个薄适配器（或改为派生）接入选择器匹配。
/// </para>
/// </summary>
public class Element
{
    static long s_nextId;

    static readonly List<Element> s_noChildren = new(0);

    readonly string _tag;
    string? _id;
    string[] _classes = Array.Empty<string>();
    string _classText = "";
    AttributeMap? _attributes;
    string[]? _parts;
    string? _inlineStyle;
    string? _scope;

    // 运行时覆盖（SetStyleOverride / SetAttributeOverride）：叠加在节点给出的值之上，协调器的写入不会冲掉它们
    List<KeyValuePair<string, string>>? _styleOverrides;
    string? _effectiveStyle;
    AttributeMap? _attributeOverrides;

    internal Element? _parent;
    internal List<Element> _children = s_noChildren;
    internal int _index;
    internal int _elementIndex;
    List<Element>? _elementChildren;
    bool _indicesValid = true;

    PseudoStates _states;
    PseudoStates _forcedStates;
    ElementDirtyFlags _dirty = ElementDirtyFlags.All;
    ElementDirtyFlags _descendantDirty;
    Element? _invalidatedBy;
    string? _invalidatedByProperty;

    Role _role;
    string? _ariaLabel;
    int? _tabIndex;
    internal Delegate?[]? _handlers;
    List<GestureRecognizer>? _gestureRecognizers;

    internal UIRoot? _root;
    internal Component? _owner;
    internal long _syncStamp;

    /// <summary>帧管线（样式 → 布局 → 绘制）为本元素保存的数据：样式视图、计算样式、布局节点、段落、绘制结果。</summary>
    internal RenderBox? Render;

    public Element(string tag)
    {
        _tag = tag ?? throw new ArgumentNullException(nameof(tag));
        UniqueId = Interlocked.Increment(ref s_nextId);
    }

    // ------------------------------------------------------------ 身份

    /// <summary>元素类型名，CSS 类型选择器匹配它（<c>div</c>、<c>text</c>、<c>button</c> ...；文本节点为 <c>#text</c>）。</summary>
    public string Tag => _tag;

    /// <summary>进程内唯一、稳定的数字标识，供 DevTools 引用元素。</summary>
    public long UniqueId { get; }

    public string? Id => _id;

    /// <summary>class 列表（无重复，保持书写顺序）。</summary>
    public IReadOnlyList<string> Classes => _classes;

    /// <summary>class 的规范化文本（空格分隔）。</summary>
    public string ClassName => _classText;

    public bool HasClass(string name)
    {
        var c = _classes;
        for (int i = 0; i < c.Length; i++)
            if (string.Equals(c[i], name, StringComparison.Ordinal)) return true;
        return false;
    }

    /// <summary>
    /// 内联样式的声明文本：节点的 <c>Style</c> 文本，其后接上 <see cref="SetStyleOverride"/> 设置的运行时覆盖（因此覆盖在级联中胜出）。
    /// </summary>
    public string? InlineStyle => _styleOverrides is null ? _inlineStyle : _effectiveStyle;

    /// <summary>作用域样式标记；作为一个值为空串的属性对 CSS 可见。</summary>
    public string? Scope => _scope;

    /// <summary>自定义属性（不含反射到属性的内建字段）。</summary>
    public IReadOnlyList<KeyValuePair<string, string>> Attributes
    {
        get
        {
            if (_attributeOverrides is null)
                return (IReadOnlyList<KeyValuePair<string, string>>?)_attributes ?? Array.Empty<KeyValuePair<string, string>>();
            // 有运行时覆盖：节点属性按位置替换为覆盖值，其余覆盖接在后面
            var merged = new AttributeMap(_attributes);
            foreach (var kv in _attributeOverrides) merged[kv.Key] = kv.Value;
            return merged;
        }
    }

    /// <summary>
    /// 属性值（CSS 属性选择器使用）：自定义属性优先，然后是内建字段的反射（<c>id</c>、<c>class</c>、<c>role</c>、
    /// <c>aria-label</c>、<c>tabindex</c>，以及各元素类型自己的属性如 <c>src</c>、<c>disabled</c>）。不存在时返回 null。
    /// </summary>
    public string? GetAttribute(string name)
    {
        var v = _attributeOverrides?[name] ?? _attributes?[name];
        if (v is not null) return v;
        if (_scope is not null && string.Equals(name, _scope, StringComparison.Ordinal)) return "";
        return GetReflectedAttribute(ToLowerAscii(name));
    }

    /// <summary>属性名按 ASCII 大小写不敏感：已是小写（选择器匹配中的常见情况）时不分配。</summary>
    static string ToLowerAscii(string name)
    {
        for (int i = 0; i < name.Length; i++)
        {
            char c = name[i];
            if (c >= 'A' && c <= 'Z') return name.ToLowerInvariant();
        }
        return name;
    }

    /// <summary>派生类型反射自身字段为属性；<paramref name="name"/> 已转为小写。</summary>
    protected virtual string? GetReflectedAttribute(string name)
    {
        switch (name)
        {
            case "id": return _id;
            case "class": return _classes.Length == 0 ? null : _classText;
            case "style": return _inlineStyle;
            case "role": return _role == Role.None ? null : RoleNames.Get(_role);
            case "aria-label": return _ariaLabel;
            case "tabindex": return _tabIndex?.ToString(System.Globalization.CultureInfo.InvariantCulture);
            default: return null;
        }
    }

    /// <summary>作为组件部件暴露的名称（<c>part</c> 属性，空白分隔），供 <c>::part()</c> 使用。</summary>
    public IReadOnlyList<string> Parts
    {
        get
        {
            if (_parts is not null) return _parts;
            var text = _attributes?["part"];
            _parts = string.IsNullOrWhiteSpace(text)
                ? Array.Empty<string>()
                : new ClassValue(text).Split();
            return _parts;
        }
    }

    /// <summary>创建本元素的组件（即产出它的 <c>Build()</c> 所属组件），供 DevTools 跳转。根元素为 null。</summary>
    public Component? Owner => _owner;

    /// <summary>所属的 UI 根；未挂到树上时为 null。</summary>
    public UIRoot? Root => _root;

    public bool IsAttached => _root is not null;

    /// <summary>是否为纯文本节点（<see cref="TextNodeElement"/>）。</summary>
    public virtual bool IsTextNode => false;

    // ------------------------------------------------------------ 树结构

    public Element? Parent => _parent;

    /// <summary>全部子节点（含文本节点）。</summary>
    public IReadOnlyList<Element> Children => _children;

    public int ChildCount => _children.Count;

    public Element? FirstChild => _children.Count == 0 ? null : _children[0];

    public Element? LastChild => _children.Count == 0 ? null : _children[_children.Count - 1];

    /// <summary>在父节点全部子节点中的 0 基位置；无父节点时为 -1。</summary>
    public int IndexInParent
    {
        get
        {
            if (_parent is null) return -1;
            _parent.EnsureIndices();
            return _index;
        }
    }

    public Element? PreviousSibling
    {
        get
        {
            var p = _parent;
            if (p is null) return null;
            p.EnsureIndices();
            return _index > 0 ? p._children[_index - 1] : null;
        }
    }

    public Element? NextSibling
    {
        get
        {
            var p = _parent;
            if (p is null) return null;
            p.EnsureIndices();
            return _index + 1 < p._children.Count ? p._children[_index + 1] : null;
        }
    }

    /// <summary>前一个元素兄弟（跳过文本节点），O(1)。</summary>
    public Element? PreviousElementSibling
    {
        get
        {
            var p = _parent;
            if (p is null) return null;
            p.EnsureIndices();
            if (_elementIndex < 0) return ScanElementSibling(-1);
            return _elementIndex > 0 ? p._elementChildren![_elementIndex - 1] : null;
        }
    }

    /// <summary>后一个元素兄弟（跳过文本节点），O(1)。</summary>
    public Element? NextElementSibling
    {
        get
        {
            var p = _parent;
            if (p is null) return null;
            p.EnsureIndices();
            if (_elementIndex < 0) return ScanElementSibling(1);
            var list = p._elementChildren!;
            return _elementIndex + 1 < list.Count ? list[_elementIndex + 1] : null;
        }
    }

    Element? ScanElementSibling(int step)
    {
        var list = _parent!._children;
        for (int i = _index + step; i >= 0 && i < list.Count; i += step)
            if (!list[i].IsTextNode) return list[i];
        return null;
    }

    /// <summary>在元素兄弟（不含文本节点）中的 1 基位置（<c>:nth-child</c>）；文本节点或无父节点时为 0。</summary>
    public int GetChildIndex()
    {
        var p = _parent;
        if (p is null || IsTextNode) return 0;
        p.EnsureIndices();
        return _elementIndex + 1;
    }

    /// <summary>从末尾数起的 1 基位置（<c>:nth-last-child</c>）。</summary>
    public int GetChildIndexFromEnd()
    {
        var p = _parent;
        if (p is null || IsTextNode) return 0;
        p.EnsureIndices();
        return p._elementChildren!.Count - _elementIndex;
    }

    /// <summary>元素子节点（不含文本节点）。</summary>
    public IReadOnlyList<Element> ElementChildren
    {
        get
        {
            EnsureIndices();
            return _elementChildren ?? s_noChildren;
        }
    }

    void EnsureIndices()
    {
        if (_indicesValid) return;
        var children = _children;
        var elems = _elementChildren;
        if (elems is null) _elementChildren = elems = new List<Element>(children.Count);
        else elems.Clear();
        for (int i = 0; i < children.Count; i++)
        {
            var c = children[i];
            c._index = i;
            if (c.IsTextNode)
            {
                c._elementIndex = -1;
            }
            else
            {
                c._elementIndex = elems.Count;
                elems.Add(c);
            }
        }
        _indicesValid = true;
    }

    /// <summary><c>:empty</c>：没有元素子节点，也没有非空文本。</summary>
    public virtual bool IsEmpty
    {
        get
        {
            var children = _children;
            for (int i = 0; i < children.Count; i++)
            {
                var c = children[i];
                if (!c.IsTextNode || ((TextNodeElement)c).Text.Length > 0) return false;
            }
            return true;
        }
    }

    /// <summary>是否为祖先（或自身）。</summary>
    public bool IsInclusiveAncestorOf(Element? other)
    {
        for (var e = other; e is not null; e = e._parent)
            if (ReferenceEquals(e, this)) return true;
        return false;
    }

    /// <summary>
    /// 文本内容（DOM <c>textContent</c>）：按树序拼接子树中所有文本节点与 <see cref="TextElement.Content"/>。
    /// </summary>
    public string TextContent
    {
        get
        {
            var sb = new StringBuilder();
            AppendText(this, sb);
            return sb.ToString();
        }
    }

    static void AppendText(Element root, StringBuilder sb)
    {
        // 显式栈，避免深树递归
        var stack = new Stack<(Element, int)>();
        stack.Push((root, -1));
        while (stack.Count > 0)
        {
            var (e, i) = stack.Pop();
            if (i < 0)
            {
                if (e is TextNodeElement t) { sb.Append(t.Text); continue; }
                if (e is TextElement te && te.Content is not null) sb.Append(te.Content);
                i = 0;
            }
            if (i < e._children.Count)
            {
                stack.Push((e, i + 1));
                stack.Push((e._children[i], -1));
            }
        }
    }

    // ------------------------------------------------------------ 状态

    /// <summary>当前伪状态（含 DevTools 强制的状态）。</summary>
    public PseudoStates States => _states | _forcedStates;

    /// <summary>DevTools 强制的伪状态（<c>:hover</c> 等），与真实状态合并。</summary>
    public PseudoStates ForcedStates
    {
        get => _forcedStates;
        set
        {
            if (_forcedStates == value) return;
            var old = States;
            _forcedStates = value;
            OnStatesChanged(old);
        }
    }

    internal PseudoStates RealStates => _states;

    internal void SetState(PseudoStates state, bool on)
    {
        var next = on ? _states | state : _states & ~state;
        if (next == _states) return;
        var old = States;
        _states = next;
        if (old != States) OnStatesChanged(old);
    }

    void OnStatesChanged(PseudoStates old)
    {
        MarkDirty(ElementDirtyFlags.Style);
        _root?.NotifyPseudoStatesChanged(this, old);
    }

    /// <summary>是否处于禁用状态。</summary>
    public bool IsDisabled => (_states & PseudoStates.Disabled) != 0;

    /// <summary>该元素能否被禁用（决定 <c>:enabled</c> 是否可能匹配）。</summary>
    public virtual bool CanBeDisabled => false;

    // ------------------------------------------------------------ 无障碍与焦点

    public Role Role => _role;

    public string? AriaLabel => _ariaLabel;

    public int? TabIndex => _tabIndex;

    /// <summary>未指定 TabIndex 时是否默认可聚焦（按钮、输入框）。</summary>
    public virtual bool IsFocusableByDefault => false;

    /// <summary>
    /// 自身或任一祖先带 HTML <c>inert</c> 属性：整棵子树不可聚焦、不参与 Tab 顺序
    /// （用于过渡期间仍挂载的退出内容；<c>pointer-events</c> 等交互屏蔽由使用者自行处理）。
    /// </summary>
    public bool IsInert
    {
        get
        {
            for (var current = this; current is not null; current = current.Parent)
                if (current.GetAttribute("inert") is not null) return true;
            return false;
        }
    }

    /// <summary>当前能否获得焦点：已挂载、未禁用、且有 TabIndex 或默认可聚焦。</summary>
    public bool IsFocusable => _root is not null && !IsDisabled && !IsTextNode && !IsInert && (_tabIndex.HasValue || IsFocusableByDefault);

    /// <summary>是否参与 Tab 顺序（可聚焦且 TabIndex 不为负）。</summary>
    public bool IsTabStop => IsFocusable && (_tabIndex ?? 0) >= 0;

    public bool IsFocused => _root is not null && ReferenceEquals(_root.FocusManager.FocusedElement, this);

    /// <summary>以程序方式聚焦本元素；不可聚焦时返回 false。</summary>
    public bool Focus() => _root is not null && _root.FocusManager.Focus(this, FocusOrigin.Programmatic);

    /// <summary>
    /// 按选项聚焦本元素（DOM 的 <c>focus(options)</c>）：<see cref="FocusOptions.PreventScroll"/> 不把它滚动到可见，
    /// <see cref="FocusOptions.SelectText"/> 聚焦输入框后全选（Radix 的 <c>focus({ select: true })</c>）。不可聚焦时返回 false。
    /// </summary>
    public bool Focus(FocusOptions options) => _root is not null && _root.FocusManager.Focus(this, options);

    /// <summary>若本元素持有焦点则移除焦点。</summary>
    public void Blur()
    {
        if (IsFocused) _root!.FocusManager.ClearFocus();
    }

    /// <summary>
    /// 是否被渲染：已挂载，且自身与祖先的计算样式都不是 <c>display: none</c>。根没有帧管线时，已挂载即视为渲染；
    /// 有帧管线但还没经过样式阶段（本帧新插入、尚未布局）的元素返回 false——需要可见性的逻辑应在
    /// <see cref="UIRoot.LayoutCompleted"/> / <see cref="UIRoot.RequestAfterLayout"/> 中判断。文本节点看父元素。
    /// </summary>
    public bool IsRendered
    {
        get
        {
            var root = _root;
            if (root is null) return false;
            if (root.Pipeline is not RenderPipeline) return true;
            for (var e = IsTextNode ? _parent : this; e is not null; e = e._parent)
            {
                var s = e.Render?.Style;
                if (s is null || s.Display == AwAUI.Css.Values.CssDisplay.None) return false;
            }
            return true;
        }
    }

    /// <summary>是否可见：<see cref="IsRendered"/>，且计算样式的 <c>visibility</c> 为 visible（没有帧管线时等于 <see cref="IsRendered"/>）。</summary>
    public bool IsVisible
    {
        get
        {
            if (!IsRendered) return false;
            var e = IsTextNode ? _parent : this;
            var s = e?.Render?.Style;
            return s is null || s.Visibility == AwAUI.Css.Values.CssVisibility.Visible;
        }
    }

    // ------------------------------------------------------------ 指针捕获

    /// <summary>捕获指针：此后该指针的事件都派发给本元素，直到抬起或释放。</summary>
    public void SetPointerCapture(long pointerId) => _root?.Input.SetCapture(pointerId, this);

    public void ReleasePointerCapture(long pointerId) => _root?.Input.ReleaseCapture(pointerId, this);

    public bool HasPointerCapture(long pointerId) => _root is not null && ReferenceEquals(_root.Input.GetCapture(pointerId), this);

    // ------------------------------------------------------------ 手势识别器（design/21）

    /// <summary>挂在本元素上的手势识别器（按添加顺序；一次按下时沿途收集，内层优先入场）。</summary>
    public IReadOnlyList<GestureRecognizer> GestureRecognizers =>
        (IReadOnlyList<GestureRecognizer>?)_gestureRecognizers ?? Array.Empty<GestureRecognizer>();

    /// <summary>挂上一个手势识别器。识别器只能属于一个元素；重复挂到同一元素被忽略。</summary>
    public void AddGestureRecognizer(GestureRecognizer recognizer)
    {
        if (recognizer is null) throw new ArgumentNullException(nameof(recognizer));
        if (recognizer.Target is { } existing && !ReferenceEquals(existing, this))
            throw new InvalidOperationException("识别器已属于另一个元素。");
        _gestureRecognizers ??= new List<GestureRecognizer>();
        if (_gestureRecognizers.Contains(recognizer)) return;
        recognizer.Target = this;
        _gestureRecognizers.Add(recognizer);
    }

    /// <summary>移除手势识别器；返回是否确实移除了。已入场（进行中）的识别器不受影响，下一次按下起不再入场。</summary>
    public bool RemoveGestureRecognizer(GestureRecognizer recognizer)
    {
        if (recognizer is null) throw new ArgumentNullException(nameof(recognizer));
        if (_gestureRecognizers is null || !_gestureRecognizers.Remove(recognizer)) return false;
        if (ReferenceEquals(recognizer.Target, this)) recognizer.Target = null;
        return true;
    }

    /// <summary>移除全部手势识别器。</summary>
    public void ClearGestureRecognizers()
    {
        if (_gestureRecognizers is null) return;
        for (int i = 0; i < _gestureRecognizers.Count; i++)
            if (ReferenceEquals(_gestureRecognizers[i].Target, this)) _gestureRecognizers[i].Target = null;
        _gestureRecognizers.Clear();
    }

    // ------------------------------------------------------------ 样式与布局结果（由帧管线写入）

    /// <summary>
    /// 计算样式（CSS 级联结果）。由 <see cref="RenderPipeline"/> 在样式阶段写入；尚未经过样式阶段、或没有帧管线时为 null。
    /// 文本节点没有自己的计算样式，返回父元素的。
    /// </summary>
    public AwAUI.Css.ComputedStyle? ComputedStyle => Render?.Style;

    /// <summary>边框盒在客户区中的矩形（逻辑像素，已对齐到设备像素）。布局之前为空矩形。</summary>
    public Rect BoundingRect { get; internal set; }

    public Rect GetBoundingRect() => BoundingRect;

    /// <summary>
    /// 边框盒在窗口客户区中的矩形（逻辑像素，DOM 的 <c>getBoundingClientRect()</c>）：已计入所有祖先滚动容器的偏移，
    /// fixed 定位的元素不随滚动移动。在 <see cref="UIRoot.LayoutCompleted"/> 中读取得到本帧的布局结果。
    /// <para>
    /// 计入自身与祖先的 CSS 变换（<c>transform / translate / scale / rotate</c>，<see cref="RenderPipeline.GetTransformedBounds"/>）：
    /// 结果是变换后的轴对齐包围盒，与浏览器相同。需要未变换的布局尺寸（Floating UI 中浮层的 <c>offsetWidth</c>）时用 <see cref="BoundingRect"/>。
    /// 没有帧管线时等于 <see cref="BoundingRect"/>。
    /// </para>
    /// </summary>
    public Rect GetBoundingClientRect() =>
        _root?.Pipeline is RenderPipeline pipeline && Render is not null ? pipeline.GetTransformedBounds(this) : BoundingRect;

    /// <summary>
    /// 裁剪本元素的矩形（窗口坐标）：视口与沿包含块链各个裁剪溢出（<c>overflow</c> 不为 visible）的祖先的内边距盒之交，
    /// 内边距盒先经 CSS 变换映射到窗口，再取轴对齐的交集。fixed 定位的元素只受视口裁剪；absolute 跳过非定位的祖先。
    /// 根没有帧管线时返回空矩形；完全被裁掉时返回空矩形（宽高为 0）。
    /// </summary>
    public Rect GetClippingRect()
    {
        var root = _root;
        if (root?.Pipeline is not RenderPipeline pipeline) return Rect.Empty;
        var viewport = pipeline.Viewport;
        var clip = new Rect(0, 0, viewport.Width, viewport.Height);
        var cur = IsTextNode ? _parent : this;
        int guard = 0;
        while (cur is not null && guard++ < 100_000)
        {
            var cs = cur.Render?.Style;
            if (cs is null || cs.Position == AwAUI.Css.Values.CssPosition.Fixed) break;
            var next = cur._parent;
            if (cs.Position == AwAUI.Css.Values.CssPosition.Absolute)
            {
                // 包含块：最近的定位祖先（根为初始包含块）
                while (next is not null && next._parent is not null && (next.Render?.Style is not { } ns || ns.Position == AwAUI.Css.Values.CssPosition.Static))
                    next = next._parent;
            }
            if (next is null || next._parent is null) break; // 根即视口
            if (next.Render is { HasBox: true, Style: { } s } && s.Display != AwAUI.Css.Values.CssDisplay.Contents
                && (s.OverflowX != AwAUI.Css.Values.CssOverflow.Visible || s.OverflowY != AwAUI.Css.Values.CssOverflow.Visible))
            {
                clip = Intersect(clip, pipeline.ScrollViewportRect(next).TransformBounds(pipeline.GetElementTransform(next)));
            }
            cur = next;
        }
        return clip;
    }

    static Rect Intersect(Rect a, Rect b)
    {
        float l = Math.Max(a.Left, b.Left), t = Math.Max(a.Top, b.Top);
        float r = Math.Min(a.Right, b.Right), btm = Math.Min(a.Bottom, b.Bottom);
        return r > l && btm > t ? Rect.FromLTRB(l, t, r, btm) : new Rect(l, t, 0, 0);
    }

    // ------------------------------------------------------------ 文档顺序

    /// <summary>
    /// <paramref name="other"/> 相对本元素的位置（DOM 的 <c>compareDocumentPosition</c>）：<see cref="DocumentPosition.Preceding"/> 表示它在本元素之前
    /// （树序，先序遍历），<see cref="DocumentPosition.Contains"/> 表示它是本元素的祖先（同时为 Preceding），<see cref="DocumentPosition.ContainedBy"/>
    /// 表示它是后代（同时为 Following）；不在同一棵树时为 <see cref="DocumentPosition.Disconnected"/> 加上一个稳定的前后关系。同一元素返回 None。
    /// 覆盖层（Portal）中的元素在树序上位于全部常规内容之后。O(深度)。
    /// </summary>
    public DocumentPosition CompareDocumentPosition(Element other)
    {
        if (other is null) throw new ArgumentNullException(nameof(other));
        if (ReferenceEquals(this, other)) return DocumentPosition.None;
        var mine = new List<Element>();
        var theirs = new List<Element>();
        for (var e = this; e is not null; e = e._parent) mine.Add(e);
        for (var e = other; e is not null; e = e._parent) theirs.Add(e);
        if (!ReferenceEquals(mine[mine.Count - 1], theirs[theirs.Count - 1]))
        {
            var top = mine[mine.Count - 1].UniqueId < theirs[theirs.Count - 1].UniqueId ? DocumentPosition.Following : DocumentPosition.Preceding;
            return DocumentPosition.Disconnected | top;
        }
        // 自根向下找到分叉点
        int i = mine.Count - 1, j = theirs.Count - 1;
        while (i >= 0 && j >= 0 && ReferenceEquals(mine[i], theirs[j]))
        {
            i--;
            j--;
        }
        if (j < 0) return DocumentPosition.Contains | DocumentPosition.Preceding;   // other 是本元素的祖先
        if (i < 0) return DocumentPosition.ContainedBy | DocumentPosition.Following; // other 是后代
        return theirs[j].IndexInParent < mine[i].IndexInParent ? DocumentPosition.Preceding : DocumentPosition.Following;
    }

    /// <summary>树序比较（排序用）：<paramref name="a"/> 在 <paramref name="b"/> 之前（含祖先在后代之前）返回负数，相同返回 0，之后返回正数。</summary>
    public static int CompareTreeOrder(Element a, Element b)
    {
        if (a is null) throw new ArgumentNullException(nameof(a));
        if (b is null) throw new ArgumentNullException(nameof(b));
        if (ReferenceEquals(a, b)) return 0;
        return (a.CompareDocumentPosition(b) & DocumentPosition.Following) != 0 ? -1 : 1;
    }

    // ------------------------------------------------------------ 滚动

    internal ScrollController? _scroll;
    bool _scrollFromNode;

    /// <summary>
    /// 本元素作为滚动容器的控制器（design/21）：节点上指定了 <c>ScrollController</c> 时是它，否则按需创建。
    /// 只有 <c>overflow</c> 不为 visible / clip 的元素（以及根元素，即视口）真正滚动；其他元素的控制器保持偏移 0、内容尺寸 0。
    /// </summary>
    public ScrollController ScrollController
    {
        get
        {
            if (_scroll is null)
            {
                _scroll = new ScrollController();
                _scroll.Attach(this);
            }
            return _scroll;
        }
    }

    /// <summary>
    /// 最近的滚动容器祖先的控制器（不含自身；根元素即视口总是滚动容器）。没有帧管线或还没有计算样式时返回根元素的控制器。
    /// 浮层据此知道锚点随哪个容器滚动。
    /// </summary>
    public ScrollController? GetScrollContainer()
    {
        if (_root is null) return null;
        for (var e = _parent; e is not null; e = e._parent)
            if (e._parent is null || RenderPipeline.ScrollAxes(e) != 0) return e.ScrollController;
        return null;
    }

    /// <summary>
    /// 协调器写入节点上的控制器：换成新的控制器时，未用过的新控制器接过当前的滚动位置。
    /// <para>
    /// 已施加到几何上的偏移与视口 / 内容度量属于元素（几何是按它们算的），总是由新控制器接过：只平移子树的滚动帧按「已施加的偏移」
    /// 判断要不要重新平移，度量决定能不能滚动。控制器从别的元素移过来时，那个元素换上一个新控制器，同样接过它自己的几何状态。
    /// </para>
    /// </summary>
    internal void SetScrollController(ScrollController? controller)
    {
        if (controller is null)
        {
            if (!_scrollFromNode) return;
            _scrollFromNode = false;
            var old = _scroll;
            if (old is null) return;
            // 节点不再指定控制器：换回元素自己的控制器，位置保留
            var own = new ScrollController();
            own.Attach(this);
            CopyScrollState(old, own);
            old.Detach();
            _scroll = own;
            return;
        }
        _scrollFromNode = true;
        if (ReferenceEquals(_scroll, controller)) return;
        var previous = _scroll;
        bool fresh = !controller._everAttached && !controller._explicitOffset;
        // 先从原来的元素上摘下（那个元素在 DetachScrollController 中保留自己的几何状态）
        controller.Attach(this);
        if (previous is not null)
        {
            if (fresh) CopyScrollState(previous, controller);
            else
            {
                CopyGeometryState(previous, controller);
                // 自带的位置按本元素的范围钳制（不等下一次布局）
                controller._x = controller.Clamp(controller._x, 0);
                controller._y = controller.Clamp(controller._y, 1);
            }
            previous.Detach();
        }
        else
        {
            // 元素还没有控制器：几何上没有施加任何偏移，度量等下一次布局
            controller._appliedX = controller._appliedY = 0;
            controller._hasMetrics = false;
        }
        _scroll = controller;
        controller.Manager?.OffsetChanged(controller);
    }

    static void CopyScrollState(ScrollController from, ScrollController to)
    {
        to._x = from._x;
        to._y = from._y;
        to._reportedX = from._reportedX;
        to._reportedY = from._reportedY;
        CopyGeometryState(from, to);
    }

    /// <summary>元素的几何状态：已施加的偏移与度量。</summary>
    static void CopyGeometryState(ScrollController from, ScrollController to)
    {
        to._appliedX = from._appliedX;
        to._appliedY = from._appliedY;
        to._viewportW = from._viewportW;
        to._viewportH = from._viewportH;
        to._contentW = from._contentW;
        to._contentH = from._contentH;
        to._hasMetrics = from._hasMetrics;
    }

    /// <summary>
    /// 控制器被附着到别的元素：本元素换上一个新控制器（位置 0），接过几何状态，下一帧把几何平移回位置 0。
    /// </summary>
    internal void DetachScrollController(ScrollController controller)
    {
        if (!ReferenceEquals(_scroll, controller)) return;
        _scrollFromNode = false;
        var own = new ScrollController();
        own.Attach(this);
        CopyGeometryState(controller, own);
        _scroll = own;
        own.Manager?.OffsetChanged(own);
        MarkDirty(ElementDirtyFlags.Paint);
    }

    // ------------------------------------------------------------ 运行时覆盖

    /// <summary>
    /// 设置一个运行时样式声明（DOM 的 <c>element.style.setProperty</c>）：接在节点的 <c>Style</c> 之后，因此在内联样式中胜出；
    /// 组件重建写入节点样式时保留。<paramref name="value"/> 为 null 或空时移除。用于在 <see cref="UIRoot.LayoutCompleted"/> 中命令式地定位浮层
    /// （<c>left / top</c>、<c>--aw-popper-*</c> 变量），不需要重建组件。只在值变化时打样式脏标记。
    /// <para><paramref name="property"/> 必须是属性名或自定义属性名（字母、数字、<c>-</c>、<c>_</c>）；<paramref name="value"/> 不能包含 <c>; { }</c>。</para>
    /// </summary>
    public void SetStyleOverride(string property, string? value)
    {
        ValidateOverrideName(property, nameof(property));
        if (value is not null && value.Trim().Length == 0) value = null;
        if (value is not null)
        {
            if (value.Length > MaxOverrideValueLength) throw new ArgumentException("样式值过长。", nameof(value));
            if (value.IndexOfAny(s_forbiddenStyleChars) >= 0) throw new ArgumentException("样式值不能包含 ; { }。", nameof(value));
            if (!IsSelfContainedValue(value)) throw new ArgumentException("样式值的引号或括号不配对，或含有注释。", nameof(value));
        }
        var list = _styleOverrides;
        int index = -1;
        if (list is not null)
        {
            for (int i = 0; i < list.Count; i++)
                if (string.Equals(list[i].Key, property, StringComparison.Ordinal)) { index = i; break; }
        }
        if (value is null)
        {
            if (index < 0) return;
            list!.RemoveAt(index);
            if (list.Count == 0) _styleOverrides = null;
        }
        else
        {
            if (index >= 0 && string.Equals(list![index].Value, value, StringComparison.Ordinal)) return;
            list ??= _styleOverrides = new List<KeyValuePair<string, string>>();
            if (list.Count >= MaxOverrides && index < 0) throw new InvalidOperationException("运行时样式覆盖过多。");
            var kv = new KeyValuePair<string, string>(property, value);
            if (index >= 0) list[index] = kv;
            else list.Add(kv);
        }
        _effectiveStyle = _styleOverrides is null ? null : ComposeStyle();
        MarkDirty(ElementDirtyFlags.Style, "style");
        _root?.NotifyInlineStyleChanged(this);
    }

    /// <summary>读取 <see cref="SetStyleOverride"/> 设置的值；没有时返回 null。</summary>
    public string? GetStyleOverride(string property)
    {
        var list = _styleOverrides;
        if (list is null || property is null) return null;
        foreach (var kv in list)
            if (string.Equals(kv.Key, property, StringComparison.Ordinal)) return kv.Value;
        return null;
    }

    /// <summary>移除全部运行时样式覆盖。</summary>
    public void ClearStyleOverrides()
    {
        if (_styleOverrides is null) return;
        _styleOverrides = null;
        _effectiveStyle = null;
        MarkDirty(ElementDirtyFlags.Style, "style");
        _root?.NotifyInlineStyleChanged(this);
    }

    /// <summary>
    /// 设置一个运行时属性（如浮层实际方位 <c>data-side / data-align</c>）：优先于节点的同名属性，组件重建时保留；<paramref name="value"/> 为 null 时移除覆盖。
    /// 只在值变化时打样式脏标记并通知观察者。
    /// </summary>
    public void SetAttributeOverride(string name, string? value)
    {
        ValidateOverrideName(name, nameof(name));
        if (value is not null && value.Length > MaxOverrideValueLength) throw new ArgumentException("属性值过长。", nameof(value));
        var old = GetAttribute(name);
        var map = _attributeOverrides;
        if (value is null)
        {
            if (map is null || !map.Contains(name)) return;
            map[name] = null;
            if (map.Count == 0) _attributeOverrides = null;
        }
        else
        {
            if (map is not null && string.Equals(map[name], value, StringComparison.Ordinal)) return;
            map ??= _attributeOverrides = new AttributeMap();
            if (map.Count >= MaxOverrides && !map.Contains(name)) throw new InvalidOperationException("运行时属性覆盖过多。");
            map[name] = value;
        }
        if (!string.Equals(old, GetAttribute(name), StringComparison.Ordinal)) AttributeChanged(name, old);
    }

    /// <summary>读取 <see cref="SetAttributeOverride"/> 设置的值；没有时返回 null。</summary>
    public string? GetAttributeOverride(string name) => name is null ? null : _attributeOverrides?[name];

    const int MaxOverrides = 256;
    const int MaxOverrideValueLength = 4096;
    static readonly char[] s_forbiddenStyleChars = { ';', '{', '}' };
    static readonly char[] s_structuralStyleChars = { '"', '\'', '(', ')', '[', ']', '\\', '/' };

    /// <summary>
    /// 值是否自成一体：引号与括号配对、没有注释开头、没有悬空的转义。否则它会吞掉拼接在其后的声明
    /// （未闭合的字符串、<c>(</c> 块或注释一直延续到文本末尾），使其他覆盖失效。
    /// </summary>
    static bool IsSelfContainedValue(string v)
    {
        if (v.IndexOfAny(s_structuralStyleChars) < 0) return true; // 常见情况（长度、颜色、关键字）：不分配
        var open = new Stack<char>();
        char quote = '\0';
        for (int i = 0; i < v.Length; i++)
        {
            char c = v[i];
            if (quote != '\0')
            {
                if (c == '\\') i++;
                else if (c == quote) quote = '\0';
                else if (c is '\n' or '\r' or '\f') return false;
                continue;
            }
            switch (c)
            {
                case '\\':
                    if (++i >= v.Length) return false;
                    break;
                case '"' or '\'':
                    quote = c;
                    break;
                case '(':
                    open.Push(')');
                    break;
                case '[':
                    open.Push(']');
                    break;
                case ')' or ']':
                    if (open.Count == 0 || open.Pop() != c) return false;
                    break;
                case '/':
                    if (i + 1 < v.Length && v[i + 1] == '*') return false;
                    break;
            }
        }
        return quote == '\0' && open.Count == 0;
    }

    static void ValidateOverrideName(string name, string paramName)
    {
        if (name is null) throw new ArgumentNullException(paramName);
        if (name.Length == 0 || name.Length > 256) throw new ArgumentException("名称为空或过长。", paramName);
        foreach (char c in name)
        {
            bool ok = (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '-' || c == '_';
            if (!ok) throw new ArgumentException("名称只能包含字母、数字、- 与 _：" + name, paramName);
        }
    }

    string ComposeStyle()
    {
        var sb = new StringBuilder();
        var node = _inlineStyle;
        if (!string.IsNullOrWhiteSpace(node))
        {
            sb.Append(node!.TrimEnd());
            if (sb[sb.Length - 1] != ';') sb.Append(';');
        }
        foreach (var kv in _styleOverrides!)
        {
            if (sb.Length > 0) sb.Append(' ');
            sb.Append(kv.Key).Append(": ").Append(kv.Value).Append(';');
        }
        return sb.ToString();
    }

    // ------------------------------------------------------------ 脏标记

    public ElementDirtyFlags DirtyFlags => _dirty;

    /// <summary>后代中存在的脏标记（用于只沿脏路径遍历）。</summary>
    public ElementDirtyFlags DescendantDirtyFlags => _descendantDirty;

    /// <summary>
    /// 最近一次把本元素（或其后代）标脏的来源元素；没有记录时为 null。这是 UI 侧的近似：只跟踪经
    /// <see cref="MarkDirty(ElementDirtyFlags)"/> 传播的最近来源，不是布局引擎内部的失效原因。
    /// </summary>
    internal Element? InvalidatedBy => _invalidatedBy;

    /// <summary>最近一次标脏来源上的属性名（<c>id</c>、<c>class</c>、<c>style</c>、自定义属性名等）；未知时为 null。含义见 <see cref="InvalidatedBy"/>。</summary>
    internal string? InvalidatedByProperty => _invalidatedByProperty;

    /// <summary>打脏标记并向祖先传播「后代脏」，同时请求一帧。</summary>
    public void MarkDirty(ElementDirtyFlags flags) => MarkDirty(flags, this, null);

    /// <summary>打脏标记并记录来源属性（本元素即来源，同时更新祖先的 <c>invalidatedBy</c>）。</summary>
    internal void MarkDirty(ElementDirtyFlags flags, string property) => MarkDirty(flags, this, property);

    void MarkDirty(ElementDirtyFlags flags, Element source, string? property)
    {
        if (flags == ElementDirtyFlags.None) return;
        _dirty |= flags;
        // 不分配对象：只保存来源引用与属性名字符串（内建属性名是常量，自定义属性名由调用方持有）。
        _invalidatedBy = source;
        _invalidatedByProperty = property;
        for (var p = _parent; p is not null; p = p._parent)
        {
            // 「最近一次」语义：即使祖先已经有这些标记，也要把来源更新为本次的来源。
            // 脏标记本身只需传播到第一个已经全量标记的祖先（它的祖先必然也已标记），
            // 但来源要继续更新到根，否则同一帧内第二次失效时上层祖先会保留较旧的来源。
            p._invalidatedBy = source;
            p._invalidatedByProperty = property;
            p._descendantDirty |= flags;
        }
        _root?.OnElementDirty();
    }

    /// <summary>帧管线处理完某一阶段后清除标记（本元素与后代标记分别清除）。</summary>
    public void ClearDirty(ElementDirtyFlags flags, bool includeDescendantFlags = true)
    {
        _dirty &= ~flags;
        if (includeDescendantFlags) _descendantDirty &= ~flags;
        if ((flags & (ElementDirtyFlags.Style | ElementDirtyFlags.Layout)) != 0
            && ((_dirty | _descendantDirty) & (ElementDirtyFlags.Style | ElementDirtyFlags.Layout)) == 0)
        {
            // invalidatedBy 报告到本帧的样式与布局阶段都处理掉该失效为止（帧末的 ClearAllFlags 也会清除）；
            // 只清掉样式、布局仍待处理时保留来源，之后没有可报告的失效来源，直到下一次 MarkDirty。
            _invalidatedBy = null;
            _invalidatedByProperty = null;
        }
    }

    // ------------------------------------------------------------ 协调器写入（只在变化时打标记与通知）

    internal void SetId(string? id)
    {
        if (string.Equals(_id, id, StringComparison.Ordinal)) return;
        var old = _id;
        _id = id;
        MarkDirty(ElementDirtyFlags.Style, "id");
        _root?.NotifyIdChanged(this, old);
    }

    internal void SetClasses(ClassValue value)
    {
        string text = value.Text;
        if (string.Equals(_classText, text, StringComparison.Ordinal)) return;
        _classText = text;
        _classes = value.Split();
        MarkDirty(ElementDirtyFlags.Style, "class");
        _root?.NotifyClassesChanged(this);
    }

    internal void SetInlineStyle(string? text)
    {
        if (string.Equals(_inlineStyle, text, StringComparison.Ordinal)) return;
        _inlineStyle = text;
        if (_styleOverrides is not null) _effectiveStyle = ComposeStyle();
        MarkDirty(ElementDirtyFlags.Style, "style");
        _root?.NotifyInlineStyleChanged(this);
    }

    internal void SetScope(string? scope)
    {
        if (string.Equals(_scope, scope, StringComparison.Ordinal)) return;
        _scope = scope;
        MarkDirty(ElementDirtyFlags.Style, "scope");
    }

    internal void SetRole(Role role)
    {
        if (_role == role) return;
        _role = role;
        PropertyChanged("role", ElementDirtyFlags.Style | ElementDirtyFlags.Semantics);
    }

    internal void SetAriaLabel(string? label)
    {
        if (string.Equals(_ariaLabel, label, StringComparison.Ordinal)) return;
        _ariaLabel = label;
        PropertyChanged("aria-label", ElementDirtyFlags.Style | ElementDirtyFlags.Semantics);
    }

    internal void SetTabIndex(int? tabIndex)
    {
        if (_tabIndex == tabIndex) return;
        _tabIndex = tabIndex;
        PropertyChanged("tabindex", ElementDirtyFlags.Style | ElementDirtyFlags.Semantics);
    }

    /// <summary>反射为属性的字段变化：打标记并通知观察者。</summary>
    private protected void PropertyChanged(string name, ElementDirtyFlags flags)
    {
        MarkDirty(flags, name);
        _root?.NotifyPropertyChanged(this, name);
    }

    /// <summary>整体替换自定义属性（复制一份，描述对象之后被修改也不影响元素），逐项报告变化。O(属性数)。</summary>
    internal void SetAttributes(AttributeMap? map)
    {
        var old = _attributes;
        if (AttributeMap.ContentEquals(old, map)) return;
        _attributes = map is null || map.Count == 0 ? null : new AttributeMap(map);
        if (old is not null)
        {
            foreach (var kv in old)
                if (map is null || !map.Contains(kv.Key)) AttributeChanged(kv.Key, kv.Value);
        }
        if (map is not null)
        {
            foreach (var kv in map)
            {
                var prev = old?[kv.Key];
                if (!string.Equals(prev, kv.Value, StringComparison.Ordinal)) AttributeChanged(kv.Key, prev);
            }
        }
    }

    void AttributeChanged(string name, string? oldValue)
    {
        if (string.Equals(name, "part", StringComparison.OrdinalIgnoreCase)) _parts = null;
        MarkDirty(ElementDirtyFlags.Style, name);
        _root?.NotifyAttributeChanged(this, name, oldValue);
    }

    internal void SetHandlers(Delegate?[]? handlers)
    {
        if (handlers is null)
        {
            if (_handlers is not null) Array.Clear(_handlers, 0, _handlers.Length);
            return;
        }
        _handlers ??= new Delegate?[(int)EventKind.Count];
        Array.Copy(handlers, _handlers, handlers.Length);
    }

    internal Delegate? GetHandler(EventKind kind) => _handlers?[(int)kind];

    // ------------------------------------------------------------ 子节点（协调器的宿主同步）

    /// <summary>用新的子节点顺序替换（由协调器在算出最少移动后调用）。</summary>
    internal void ReplaceChildren(List<Element> children)
    {
        _children = children;
        _indicesValid = false;
    }

    internal List<Element> MutableChildren => _children;

    internal void InvalidateIndices() => _indicesValid = false;

    /// <summary>把子树挂到 / 摘离根（显式栈）。</summary>
    internal static void SetRootRecursive(Element top, UIRoot? root)
    {
        if (ReferenceEquals(top._root, root)) return;
        var stack = new Stack<Element>();
        stack.Push(top);
        while (stack.Count > 0)
        {
            var e = stack.Pop();
            e._root = root;
            var ch = e._children;
            for (int i = 0; i < ch.Count; i++)
                if (!ReferenceEquals(ch[i]._root, root)) stack.Push(ch[i]);
        }
    }

    /// <summary>
    /// 不经协调器插入子元素（覆盖层的层元素）：<paramref name="index"/> 为 -1 时追加。挂在根上时设置根并通知观察者。
    /// 只用于没有任何实例记录（Fiber）管理其子节点的宿主（覆盖层根），或宿主同步会保留它的位置（根元素上的覆盖层根）。
    /// </summary>
    internal void InsertChildDirect(Element child, int index = -1)
    {
        if (child._parent is not null) throw new InvalidOperationException("元素 " + child + " 已有父节点。");
        if (ReferenceEquals(_children, s_noChildren)) _children = new List<Element>();
        if (index < 0 || index > _children.Count) index = _children.Count;
        _children.Insert(index, child);
        child._parent = this;
        _indicesValid = false;
        if (_root is not null) SetRootRecursive(child, _root);
        child.MarkInsertedDirty();
        _root?.NotifyChildInserted(this, child);
        MarkDirty(ElementDirtyFlags.Style | ElementDirtyFlags.Layout | ElementDirtyFlags.Paint);
    }

    /// <summary>不经协调器移除子元素（与 <see cref="InsertChildDirect"/> 配对）。</summary>
    internal bool RemoveChildDirect(Element child)
    {
        if (!ReferenceEquals(child._parent, this)) return false;
        int index = _children.IndexOf(child);
        if (index < 0) return false;
        _children.RemoveAt(index);
        child._parent = null;
        _indicesValid = false;
        var root = _root;
        if (root is not null) root.OnSubtreeRemoved(this, child);
        SetRootRecursive(child, null);
        root?.NotifyChildRemoved(this, child);
        MarkDirty(ElementDirtyFlags.Style | ElementDirtyFlags.Layout | ElementDirtyFlags.Paint);
        return true;
    }

    /// <summary>新插入的子树需要完整的样式、布局与绘制。</summary>
    internal void MarkInsertedDirty()
    {
        _dirty = ElementDirtyFlags.All;
        _descendantDirty = _children.Count > 0 ? ElementDirtyFlags.All : ElementDirtyFlags.None;
        MarkDirty(ElementDirtyFlags.All);
    }

    public override string ToString()
    {
        var sb = new StringBuilder(_tag);
        if (_id is not null) sb.Append('#').Append(_id);
        foreach (var c in _classes) sb.Append('.').Append(c);
        return sb.ToString();
    }
}

/// <summary><see cref="Element.CompareDocumentPosition"/> 的结果（位与 DOM 的 <c>Node.DOCUMENT_POSITION_*</c> 相同）。</summary>
[Flags]
public enum DocumentPosition
{
    /// <summary>同一元素。</summary>
    None = 0,

    /// <summary>不在同一棵树中。</summary>
    Disconnected = 1,

    /// <summary>另一元素在本元素之前（树序）。</summary>
    Preceding = 2,

    /// <summary>另一元素在本元素之后。</summary>
    Following = 4,

    /// <summary>另一元素是本元素的祖先。</summary>
    Contains = 8,

    /// <summary>另一元素是本元素的后代。</summary>
    ContainedBy = 16,
}

/// <summary>纯文本节点（由 <see cref="NodeList"/> 中的字符串产生）。不参与 CSS 兄弟关系。</summary>
public sealed class TextNodeElement : Element
{
    string _text;

    public TextNodeElement(string text) : base("#text") => _text = text ?? "";

    public string Text => _text;

    public override bool IsTextNode => true;

    internal void SetText(string text)
    {
        if (string.Equals(_text, text, StringComparison.Ordinal)) return;
        _text = text;
        MarkDirty(ElementDirtyFlags.Layout | ElementDirtyFlags.Paint);
        Root?.NotifyTextChanged(this);
    }

    public override string ToString() => "\"" + _text + "\"";
}

/// <summary><c>text</c> 元素：段落容器，行内排版在其内部进行。</summary>
public sealed class TextElement : Element
{
    string? _content;

    public TextElement() : base("text") { }

    /// <summary>段首的纯文本内容。</summary>
    public string? Content => _content;

    public override bool IsEmpty => string.IsNullOrEmpty(_content) && base.IsEmpty;

    internal void SetContent(string? content)
    {
        if (string.Equals(_content, content, StringComparison.Ordinal)) return;
        _content = content;
        MarkDirty(ElementDirtyFlags.Layout | ElementDirtyFlags.Paint);
        Root?.NotifyTextChanged(this);
    }
}

/// <summary><c>button</c> 元素：默认可聚焦，回车/空格激活。</summary>
public sealed class ButtonElement : Element
{
    public ButtonElement() : base("button") { }

    public override bool IsFocusableByDefault => true;

    public override bool CanBeDisabled => true;

    protected override string? GetReflectedAttribute(string name) => name switch
    {
        "disabled" => IsDisabled ? "" : null,
        _ => base.GetReflectedAttribute(name),
    };

    internal void SetDisabled(bool disabled)
    {
        if (IsDisabled == disabled) return;
        SetState(PseudoStates.Disabled, disabled);
        PropertyChanged("disabled", ElementDirtyFlags.Semantics);
    }
}

static class RoleNames
{
    public static string Get(Role role) => role switch
    {
        Role.Generic => "generic",
        Role.Button => "button",
        Role.Link => "link",
        Role.Text => "text",
        Role.Image => "img",
        Role.TextBox => "textbox",
        Role.CheckBox => "checkbox",
        Role.Radio => "radio",
        Role.Switch => "switch",
        Role.Slider => "slider",
        Role.ProgressBar => "progressbar",
        Role.ComboBox => "combobox",
        Role.ListBox => "listbox",
        Role.Option => "option",
        Role.List => "list",
        Role.ListItem => "listitem",
        Role.Tree => "tree",
        Role.TreeItem => "treeitem",
        Role.Grid => "grid",
        Role.Row => "row",
        Role.Cell => "gridcell",
        Role.Tab => "tab",
        Role.TabList => "tablist",
        Role.TabPanel => "tabpanel",
        Role.Menu => "menu",
        Role.MenuBar => "menubar",
        Role.MenuItem => "menuitem",
        Role.Dialog => "dialog",
        Role.AlertDialog => "alertdialog",
        Role.Alert => "alert",
        Role.Status => "status",
        Role.Tooltip => "tooltip",
        Role.Toolbar => "toolbar",
        Role.Separator => "separator",
        Role.Group => "group",
        Role.Region => "region",
        Role.Navigation => "navigation",
        Role.Heading => "heading",
        Role.Presentation => "presentation",
        _ => "",
    };
}
