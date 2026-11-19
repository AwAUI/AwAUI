using System;
using System.Collections.Generic;

namespace AwAUI.Layout;

/// <summary>
/// 布局树节点。UI 元素树中每个元素持有一个（design/05「布局」）：保存输入样式、有序子节点、可选的叶子测量，
/// 以及上一次布局的结果与缓存。
/// <para>
/// 结构或样式变化时自动标脏；叶子内容（文本、图片）变化时由持有者调用 <see cref="MarkDirty"/>。
/// 脏标记沿父链上传到根，布局时干净子树按约束命中缓存，不会重新测量。
/// </para>
/// <para>非线程安全：一棵树只能在一个线程上修改与布局。</para>
/// </summary>
public class LayoutNode
{
    LayoutStyle _style = LayoutStyle.Default;  // 默认样式不引用包含块尺寸，_basisFlags 初值 0 与之一致
    LayoutNode? _parent;
    readonly List<LayoutNode> _children = new();
    LayoutMeasure? _measure;

    // ---- 脏标记与缓存（由 LayoutEngine 维护） ----
    internal bool _isDirty = true;
    internal bool _hidden;
    internal bool _escValid, _escAbs, _escFixed;
    internal CacheEntry[]? _cache;
    internal int _cacheCount, _cacheNext;
    internal bool _hasFinal;
    internal LayoutInput _finalInput;
    internal LayoutOutput _finalOutput;
    internal MeasureConstraints _mk0, _mk1;
    internal Size _mv0, _mv1;
    internal int _mCount;

    // ---- 样式是否引用包含块尺寸（百分比/calc/fit-content）；不引用时缓存键忽略对应的包含块尺寸 ----
    internal const byte UsesParentWidth = 1, UsesParentHeight = 2;
    internal byte _basisFlags;

    static bool Pct(DimensionKind k) => k is DimensionKind.Percent or DimensionKind.Calc;

    static byte ComputeBasisFlags(LayoutStyle s)
    {
        bool w = Pct(s.Width.Kind) || Pct(s.MinWidth.Kind) || Pct(s.MaxWidth.Kind) ||
                 s.Width.Kind == DimensionKind.FitContent || s.MinWidth.Kind == DimensionKind.FitContent ||
                 s.MaxWidth.Kind == DimensionKind.FitContent ||
                 Pct(s.Padding.Left.Kind) || Pct(s.Padding.Right.Kind) || Pct(s.Padding.Top.Kind) || Pct(s.Padding.Bottom.Kind) ||
                 Pct(s.Margin.Left.Kind) || Pct(s.Margin.Right.Kind) || Pct(s.Margin.Top.Kind) || Pct(s.Margin.Bottom.Kind);
        bool h = Pct(s.Height.Kind) || Pct(s.MinHeight.Kind) || Pct(s.MaxHeight.Kind);
        // 宽高比会把一个轴的百分比传递到另一个轴
        if (s.AspectRatio is not null && (w || h)) w = h = true;
        return (byte)((w ? UsesParentWidth : 0) | (h ? UsesParentHeight : 0));
    }

    // ---- 结果 ----
    internal float _x, _y, _w, _h;
    internal float _baseline = float.NaN;
    internal float _scrollW, _scrollH;
    internal BoxEdges _margin, _padding, _border;

    // ---- 绝对定位的静态位置：相对盒父节点边框盒的矩形 + 每轴对齐（0 起始、1 居中、2 末端） ----
    internal float _staticX, _staticY, _staticW, _staticH;
    internal byte _staticAlignX, _staticAlignY;

    // ---- 绝对定位的网格子节点：网格容器为包含块时的网格区域（网格容器边框盒坐标），由网格的完整布局写入 ----
    internal LayoutNode? _gridCbOwner;
    internal float _gridCbX, _gridCbY, _gridCbW, _gridCbH;

    // ---- 像素对齐结果 ----
    internal float _snapX, _snapY, _snapW, _snapH;

    public LayoutNode() { }

    public LayoutNode(LayoutStyle style)
    {
        _style = style ?? throw new ArgumentNullException(nameof(style));
        _basisFlags = ComputeBasisFlags(style);
    }

    /// <summary>持有者（通常是 UI 元素）的反向引用，引擎不使用。</summary>
    public object? Owner { get; set; }

    /// <summary>输入样式。设置为不相等的样式时标脏。</summary>
    public LayoutStyle Style
    {
        get => _style;
        set
        {
            if (value is null) throw new ArgumentNullException(nameof(value));
            if (ReferenceEquals(value, _style)) return;
            bool changed = !value.Equals(_style);
            _style = value;
            _basisFlags = ComputeBasisFlags(value);
            if (changed) MarkDirty();
        }
    }

    /// <summary>叶子测量。非 null 时节点按叶子处理，子节点被忽略（布局结果为零）。</summary>
    public LayoutMeasure? Measure
    {
        get => _measure;
        set
        {
            if (ReferenceEquals(value, _measure)) return;
            _measure = value;
            MarkDirty();
        }
    }

    public LayoutNode? Parent => _parent;
    public int ChildCount => _children.Count;
    public LayoutNode GetChild(int index) => _children[index];
    internal List<LayoutNode> ChildList => _children;

    public void AddChild(LayoutNode child) => InsertChild(_children.Count, child);

    public void InsertChild(int index, LayoutNode child)
    {
        if (child is null) throw new ArgumentNullException(nameof(child));
        if (child._parent is not null) throw new InvalidOperationException("节点已有父节点，请先移除。");
        for (var p = this; p is not null; p = p._parent)
            if (ReferenceEquals(p, child)) throw new InvalidOperationException("不能把祖先节点加为子节点。");
        _children.Insert(index, child);
        child._parent = this;
        MarkDirty();
    }

    public void RemoveChildAt(int index)
    {
        var child = _children[index];
        _children.RemoveAt(index);
        child._parent = null;
        MarkDirty();
    }

    public bool RemoveChild(LayoutNode child)
    {
        int i = _children.IndexOf(child);
        if (i < 0) return false;
        RemoveChildAt(i);
        return true;
    }

    public void ClearChildren()
    {
        if (_children.Count == 0) return;
        foreach (var c in _children) c._parent = null;
        _children.Clear();
        MarkDirty();
    }

    /// <summary>上次布局后是否有影响本节点的变化。</summary>
    public bool IsDirty => _isDirty;

    /// <summary>
    /// 使本节点及所有祖先的布局缓存失效（叶子内容变化时调用）。代价为树深度；同级兄弟的缓存不受影响。
    /// </summary>
    public void MarkDirty()
    {
        _mCount = 0;
        for (var n = this; n is not null; n = n._parent)
        {
            n._isDirty = true;
            n._cacheCount = 0;
            n._hasFinal = false;
            n._escValid = false;
        }
    }

    // ---------------- 布局结果 ----------------

    /// <summary>边框盒左上角相对父节点边框盒的 x（父节点为 display:contents 时，contents 节点自身位于 (0,0)）。</summary>
    public float X => _x;
    /// <summary>边框盒左上角相对父节点边框盒的 y。</summary>
    public float Y => _y;
    /// <summary>边框盒宽度。</summary>
    public float Width => _w;
    /// <summary>边框盒高度。</summary>
    public float Height => _h;
    /// <summary>边框盒（相对父节点边框盒）。</summary>
    public Rect BorderBox => new(_x, _y, _w, _h);
    /// <summary>已解析的外边距（含 auto 外边距分到的空间）。</summary>
    public BoxEdges Margin => _margin;
    public BoxEdges Padding => _padding;
    public BoxEdges Border => _border;

    /// <summary>内边距盒，相对自身边框盒。</summary>
    public Rect PaddingBox => new(_border.Left, _border.Top,
        Math.Max(0, _w - _border.Horizontal), Math.Max(0, _h - _border.Vertical));

    /// <summary>内容盒，相对自身边框盒。</summary>
    public Rect ContentBox => new(_border.Left + _padding.Left, _border.Top + _padding.Top,
        Math.Max(0, _w - _border.Horizontal - _padding.Horizontal), Math.Max(0, _h - _border.Vertical - _padding.Vertical));

    /// <summary>
    /// 可滚动溢出区域尺寸：从内边距盒左上角量起，覆盖内边距盒与所有子孙盒（溢出可见的子节点连同其溢出）
    /// 以及末端内边距。不小于内边距盒。
    /// </summary>
    public Size ScrollSize => new(_scrollW, _scrollH);

    /// <summary>第一条基线到边框盒顶部的距离；没有基线时为 NaN。</summary>
    public float Baseline => _baseline;

    /// <summary><see cref="LayoutEngine.RoundToPixels"/> 之后的边框盒（相对父节点对齐后的边框盒）。</summary>
    public Rect SnappedBorderBox => new(_snapX, _snapY, _snapW, _snapH);

    internal void ZeroLayout()
    {
        _x = _y = _w = _h = 0;
        _scrollW = _scrollH = 0;
        _baseline = float.NaN;
        _margin = _padding = _border = default;
    }

    // ---------------- 缓存 ----------------

    internal const int CacheSize = 6;

    internal bool TryGetCached(in LayoutInput input, out LayoutOutput output)
    {
        if (_hasFinal && _finalInput.SameKey(input))
        {
            output = _finalOutput;
            return true;
        }
        var cache = _cache;
        for (int i = 0; i < _cacheCount; i++)
        {
            if (cache![i].Input.SameKey(input))
            {
                output = cache[i].Output;
                return true;
            }
        }
        output = default;
        return false;
    }

    internal void StoreCache(in LayoutInput input, in LayoutOutput output)
    {
        _cache ??= new CacheEntry[CacheSize];
        int slot;
        if (_cacheCount < CacheSize) slot = _cacheCount++;
        else
        {
            slot = _cacheNext;
            _cacheNext = (_cacheNext + 1) % CacheSize;
        }
        _cache[slot] = new CacheEntry { Input = input, Output = output };
    }

    internal bool TryGetMeasured(in MeasureConstraints c, out Size size)
    {
        if (_mCount > 0 && _mk0.Equals(c)) { size = _mv0; return true; }
        if (_mCount > 1 && _mk1.Equals(c)) { size = _mv1; return true; }
        size = default;
        return false;
    }

    internal void StoreMeasured(in MeasureConstraints c, Size size)
    {
        // 最近使用的放在 0 号槽
        _mk1 = _mk0; _mv1 = _mv0;
        _mk0 = c; _mv0 = size;
        if (_mCount < 2) _mCount++;
    }

    public override string ToString() =>
        FormattableString.Invariant($"LayoutNode {_style.Display} [{_x}, {_y}, {_w}x{_h}]");
}
