using System;
using System.Collections.Generic;
using AwAUI.Css;
using AwAUI.Css.Values;
using AwAUI.Graphics;
using AwAUI.Text;

namespace AwAUI.UI;

public sealed partial class RenderPipeline
{
    /// <summary>命中测试表的一项：按绘制顺序记录，越靠后越在上层。</summary>
    readonly struct HitEntry
    {
        public HitEntry(Element element, in RoundRect shape, Rect clip, in RoundRect clipShape, bool pointerEventsNone, byte scrollbarAxis = 0)
        {
            Element = element;
            Shape = shape;
            Clip = clip;
            ClipShape = clipShape;
            PointerEventsNone = pointerEventsNone;
            ScrollbarAxis = scrollbarAxis;
        }

        /// <summary>滚动条命中项：1 横向、2 纵向；0 为元素本身。命中滚动条时 <see cref="HitTestCore"/> 仍返回滚动容器。</summary>
        public byte ScrollbarAxis { get; }

        /// <summary><c>pointer-events: none</c>：常规命中测试跳过它，DevTools 的拾取（忽略 pointer-events）仍可选中。</summary>
        public bool PointerEventsNone { get; }

        public Element Element { get; }
        public RoundRect Shape { get; }

        /// <summary>全部祖先裁剪的轴对齐交集。</summary>
        public Rect Clip { get; }

        /// <summary>最内层的圆角裁剪（没有时为空）；外层圆角裁剪只按矩形近似。</summary>
        public RoundRect ClipShape { get; }
    }

    /// <summary>
    /// 录制单元的最大嵌套深度。更深的元素直接录进最近的外层单元：既限制光栅器重放嵌套 DisplayList 的递归深度，
    /// 也让极深的树不会产生同样多层的小列表。
    /// </summary>
    const int MaxUnitDepth = 24;

    /// <summary>逃逸检测（<see cref="RenderBox.ClipEscape"/>）沿祖先链的总步数上限；超出时所有层叠上下文按「逐块裁剪」处理（正确但不缓存）。</summary>
    const int MaxEscapeSteps = 4_000_000;

    readonly List<HitEntry> _hits = new();
    readonly List<Element> _clipChain = new();
    readonly List<Element> _escapePath = new();
    readonly List<RenderBox> _positionedScratch = new();
    readonly List<Element> _recordedElements = new();

    /// <summary>当前录制目标；为 null 时只遍历（重建命中测试表、记录图片可见性），不录制。</summary>
    DrawingContext? _dc;
    Rect _hitClip;
    RoundRect _hitClipShape;
    int _suppress;

    /// <summary>当前录制单元所在的层叠上下文：它的外部裁剪链在合成时已经施加，单元内部只施加位于它之内的裁剪。</summary>
    Element? _clipScope;

    int _unitDepth;
    int _recordedUnits;
    int _reusedUnits;

    /// <summary>当前（下一次）绘制轮次。失效标记 <see cref="RenderBox.DirtySerial"/> 与缓存 <see cref="RenderBox.CacheSerial"/> 都以它计。</summary>
    long _paintSerial = 1;

    /// <summary>缓存世代：缩放变化、<see cref="InvalidateAll"/> 等全局变化时加一，所有元素缓存随之作废。</summary>
    int _cacheEpoch = 1;

    /// <summary>层叠结构（定位、z-index、opacity、元素树结构）的版本；变化后重新收集层叠项并重新做逃逸检测。</summary>
    long _layeringSerial;
    bool _escapeDirty = true;
    bool _escapeEverywhere;

    // 损伤跟踪（逻辑像素）：本轮所有变化的旧、新墨迹之并
    Rect _damage;
    bool _damageAll = true;
    readonly List<RenderBox> _damageBoxes = new();
    Rect? _lastDamage;

    static readonly Rect s_noClip = new(-1e9f, -1e9f, 2e9f, 2e9f);

    /// <summary>
    /// 最近一次重录相对上一帧的损伤区域（逻辑像素，已与视口求交，可能为空矩形表示像素没有变化）；为 null 表示整帧都要重画。
    /// 只在 <see cref="FrameVersion"/> 恰好比上次光栅化时大 1 时可用于局部光栅化（<see cref="UIHost"/> 据此给软件后端传 <see cref="RenderOptions.Damage"/>）。
    /// </summary>
    public Rect? LastDamage => _lastDamage;

    /// <summary>最近一次绘制中重新录制了 DisplayList 的元素（录制单元），按录制完成顺序。DevTools 的重绘闪烁与测试使用。</summary>
    public IReadOnlyList<Element> LastRecordedElements => _recordedElements;

    /// <summary>
    /// 绘制阶段：按 CSS 2.1 附录 E 的简化层叠顺序录制。每个层叠上下文内依次是：自身背景与边框 → z 为负的子上下文 →
    /// 常规流内容（按树序，每个元素先背景边框后内容，溢出裁剪作用于其后代）→ z 为 0 / auto 的定位元素与子上下文（树序）→
    /// z 为正的子上下文。层叠上下文由根、z-index 不为 auto 的定位元素或 flex 项、opacity &lt; 1 的元素、fixed / sticky 建立；
    /// opacity 用离屏图层实现。定位元素脱离树序绘制时按其包含块链重新施加祖先的溢出裁剪（绝对定位元素不受包含块之外的祖先裁剪，
    /// fixed 不受任何祖先裁剪）。
    /// <para>
    /// <b>增量录制</b>：每个元素（以及每个层叠上下文的内部）是一个录制单元，DisplayList 按自身边框盒原点的局部坐标缓存，
    /// 由外层单元以「平移 + <see cref="DrawingContext.DrawDisplayList"/>」合成。单元在以下情况重录：自身或后代有绘制脏标记、
    /// 子树内任何元素的局部几何或相对父元素的偏移变化、剔除结果可能不同、全局世代变化。只移动了位置的子树不重录，
    /// 只是外层单元重新合成它。命中测试表每次绘制都重建（对缓存的子树只遍历、不录制）。
    /// </para>
    /// </summary>
    void PaintPass(UIRoot root, bool geometryChanged)
    {
        var rootEl = root.RootElement;
        var dc = new DrawingContext(4096);
        _dc = dc;
        _hits.Clear();
        _recordedElements.Clear();
        _hitClip = s_noClip;
        _hitClipShape = default;
        _suppress = 0;
        _unitDepth = 0;
        _recordedUnits = _reusedUnits = 0;
        _clipScope = rootEl;
        _cull = new Rect(0, 0, Math.Max(1, _viewport.Width), Math.Max(1, _viewport.Height));
        if (_escapeDirty) ComputeClipEscapes(rootEl);

        if (!_incremental) _damageAll = true;
        InvalidatePaintDirtyCaches(rootEl);
        bool fullInk = geometryChanged || _damageAll;
        if (!_damageAll) CollectDamageBefore(rootEl);
        if (fullInk) ComputeInk(rootEl, dirtyOnly: false);
        else ComputeInk(rootEl, dirtyOnly: true);
        foreach (var b in _damageBoxes) AddDamage(b.Ink);
        try
        {
            var rootBox = rootEl.Render;
            if (rootBox?.Style is { } rs && rootBox.HasBox)
            {
                // 根元素的背景铺满画布（CSS 的画布背景传播）
                var bg = rs.ResolveColor(rs.BackgroundColor);
                if (!bg.IsTransparent) dc.FillRect(new Rect(0, 0, _viewport.Width, _viewport.Height), bg);
                PaintStackingContext(rootBox, isRoot: true);
            }
        }
        finally
        {
            _dc = null;
            _clipScope = null;
        }
        _frame = dc.Finish();
        _frameVersion++;
        _forceRepaint = false;
        if (_damageAll)
        {
            _lastDamage = null;
        }
        else
        {
            var d = _damage.Intersect(_cull);
            _lastDamage = d.Width > 0 && d.Height > 0 ? d : Rect.Empty;
        }
        _damageAll = false;
        _damage = Rect.Empty;
        _damageBoxes.Clear();
        _paintSerial++;
        _root?.ImagesOrNull?.AfterPaint();
    }

    Rect _cull;
    readonly Stack<(Element Element, bool Exit)> _inkStack = new();

    /// <summary>子树的墨迹完全在视口之外时不必录制（命中测试也不可能命中视口外的点）。</summary>
    bool Culled(RenderBox b) => !b.Ink.Intersects(_cull);

    /// <summary>
    /// 自底向上算出每个盒的墨迹范围（显式栈，后序）。<paramref name="dirtyOnly"/> 时只重算带绘制脏标记的路径
    /// （没有布局变化的帧：其余盒的墨迹不变）。
    /// </summary>
    void ComputeInk(Element rootEl, bool dirtyOnly)
    {
        var stack = _inkStack;
        stack.Clear();
        stack.Push((rootEl, false));
        while (stack.Count > 0)
        {
            var (e, exit) = stack.Pop();
            var b = e.Render;
            if (b is null) continue;
            if (!exit)
            {
                if (!b.HasBox || b.Kind == BoxKind.Inline)
                {
                    b.Ink = default;
                    continue;
                }
                stack.Push((e, true));
                if (b.Kind == BoxKind.Container)
                {
                    var ch = e.Children;
                    for (int i = 0; i < ch.Count; i++)
                    {
                        var c = ch[i];
                        if (dirtyOnly && ((c.DirtyFlags | c.DescendantDirtyFlags) & ElementDirtyFlags.Paint) == 0) continue;
                        stack.Push((c, false));
                    }
                }
                continue;
            }
            var ink = OwnInk(b);
            if (b.Kind == BoxKind.Container)
            {
                var ch = e.Children;
                for (int i = 0; i < ch.Count; i++)
                    if (ch[i].Render is { HasBox: true } cb) ink = Union(ink, cb.Ink);
            }
            b.Ink = ink;
        }
    }

    static Rect Union(Rect a, Rect b) => a.Width <= 0 && a.Height <= 0 ? b : b.Width <= 0 && b.Height <= 0 ? a
        : Rect.FromLTRB(Math.Min(a.Left, b.Left), Math.Min(a.Top, b.Top), Math.Max(a.Right, b.Right), Math.Max(a.Bottom, b.Bottom));

    static bool ContainsRect(Rect outer, Rect inner) =>
        inner.Left >= outer.Left && inner.Top >= outer.Top && inner.Right <= outer.Right && inner.Bottom <= outer.Bottom;

    /// <summary>字形墨迹相对字号的估计上限（纵向，含上伸与下伸部件）：行高小于它时字形伸出行盒。</summary>
    const float GlyphExtentEm = 1.6f;

    /// <summary>字形向行内方向悬垂的估计上限（斜体、负侧边距），按字号计。</summary>
    const float GlyphOverhangEm = 0.3f;

    Rect OwnInk(RenderBox b)
    {
        var r = b.Border;
        var s = b.Kind == BoxKind.AnonymousText ? b.Element.Parent?.Render?.Style : b.Style;
        if (s is null) return r;
        float grow = 0;
        if (!s.BoxShadow.IsNone)
        {
            foreach (var sh in s.BoxShadow.Shadows)
                if (!sh.Inset) grow = Math.Max(grow, Math.Abs(sh.OffsetX) + Math.Abs(sh.OffsetY) + sh.Blur + Math.Max(0, sh.Spread));
        }
        if (s.OutlineStyle is not (CssLineStyle.None or CssLineStyle.Hidden))
            grow = Math.Max(grow, Math.Abs(s.OutlineOffset.Resolve(0)) + s.OutlineWidth);
        if (!(grow >= 0) || float.IsInfinity(grow)) grow = 1e6f;
        if (grow > 0) r = r.Inflate(grow + 1);
        if (b.Paragraph is { IsEmpty: false } para)
        {
            // 段落文本可能溢出内容盒（nowrap、不可断的长词）
            var tl = para.Layout;
            tl.Layout(b.ContentWidth + TextParagraph.WidthSlack);
            // 对齐方式可能让溢出的行向任一侧伸出：两侧都按溢出量外扩
            float over = Math.Max(0, tl.Width - b.ContentWidth);
            // 字形可能伸出行盒：line-height 小于字体内容区（约 1–1.5 em）时上下各伸出差值的一半；斜体与负侧边距向两侧悬垂
            float em = para.MaxFontSize;
            float minLine = float.PositiveInfinity;
            int lines = tl.LineCount;
            for (int i = 0; i < lines; i++) minLine = Math.Min(minLine, tl.GetLine(i).Height);
            if (float.IsInfinity(minLine)) minLine = 0;
            float glyphY = Math.Max(0, (GlyphExtentEm * em - minLine) / 2);
            float glyphX = GlyphOverhangEm * em;
            if (!(glyphY < 1e6f)) glyphY = 1e6f;
            if (!(glyphX < 1e6f)) glyphX = 1e6f;
            r = Union(r, new Rect(b.ContentX - over - glyphX - 1, b.ContentY - glyphY - 1, b.ContentWidth + 2 * (over + glyphX) + 2,
                Math.Max(tl.Height, b.ContentHeight) + 2 * glyphY + 2));
        }
        return r;
    }

    // ------------------------------------------------------------ 损伤

    void AddDamage(Rect r)
    {
        if (r.Width <= 0 || r.Height <= 0) return;
        _damage = Union(_damage, r);
    }

    /// <summary>几何变化或自身绘制变化：旧墨迹现在计入损伤，新墨迹在本轮墨迹重算后计入。</summary>
    void DamageBox(RenderBox b)
    {
        if (_damageAll || b.DamageSerial == _paintSerial) return;
        b.DamageSerial = _paintSerial;
        AddDamage(b.Ink);
        _damageBoxes.Add(b);
    }

    /// <summary>沿绘制脏路径找出自身有变化的盒（段落连同其行内后代的变化），记录旧墨迹；其墨迹已覆盖后代，不再深入。</summary>
    void CollectDamageBefore(Element rootEl)
    {
        if ((rootEl.DirtyFlags & ElementDirtyFlags.Paint) != 0)
        {
            // 根的背景铺满画布
            _damageAll = true;
            return;
        }
        var stack = _layoutStack;
        stack.Clear();
        stack.Push(rootEl);
        while (stack.Count > 0)
        {
            var e = stack.Pop();
            var b = e.Render;
            if (b is null) continue;
            bool self = (e.DirtyFlags & ElementDirtyFlags.Paint) != 0
                        || (b.Kind is BoxKind.Paragraph or BoxKind.Replaced && (e.DescendantDirtyFlags & ElementDirtyFlags.Paint) != 0);
            if (self && !ReferenceEquals(e, rootEl))
            {
                DamageBox(b);
                continue;
            }
            var ch = e.Children;
            for (int i = 0; i < ch.Count; i++)
                if (((ch[i].DirtyFlags | ch[i].DescendantDirtyFlags) & ElementDirtyFlags.Paint) != 0) stack.Push(ch[i]);
        }
    }

    /// <summary>
    /// 把本轮的绘制脏标记转成持久的缓存失效（<see cref="RenderBox.DirtySerial"/>）：脏标记在帧末清除，而本轮没有录制的元素
    /// （被剔除、位于 opacity:0 之下、所在上下文逃逸或嵌套超过 <see cref="MaxUnitDepth"/> 而直接录进外层单元）的旧缓存
    /// 必须在它下次被合成时仍然判定为失效。只沿绘制脏路径走。
    /// </summary>
    void InvalidatePaintDirtyCaches(Element rootEl)
    {
        if (((rootEl.DirtyFlags | rootEl.DescendantDirtyFlags) & ElementDirtyFlags.Paint) == 0) return;
        var stack = _layoutStack;
        stack.Clear();
        stack.Push(rootEl);
        while (stack.Count > 0)
        {
            var e = stack.Pop();
            if (e.Render is { } b) b.DirtySerial = _paintSerial;
            if ((e.DescendantDirtyFlags & ElementDirtyFlags.Paint) == 0) continue;
            var ch = e.Children;
            for (int i = 0; i < ch.Count; i++)
                if (((ch[i].DirtyFlags | ch[i].DescendantDirtyFlags) & ElementDirtyFlags.Paint) != 0) stack.Push(ch[i]);
        }
    }

    /// <summary>布局阶段报告的几何变化：<paramref name="self"/> 为局部几何（尺寸、内容盒）变化，否则为相对父元素的偏移变化。</summary>
    void OnGeometryChanged(RenderBox b, bool self)
    {
        DamageBox(b);
        var target = self ? b : b.Element.Parent?.Render;
        if (target is not null) InvalidateCache(target);
    }

    /// <summary>作废一个元素及其全部祖先的缓存（祖先的 DisplayList 引用了它的列表或包含它的录制内容）。</summary>
    void InvalidateCache(RenderBox b)
    {
        for (RenderBox? x = b; x is not null; x = x.Element.Parent?.Render)
        {
            if (x.DirtySerial == _paintSerial) break;
            x.DirtySerial = _paintSerial;
        }
    }

    /// <summary>元素子树被移除：旧墨迹计入损伤。</summary>
    void OnSubtreeRemoved(Element child)
    {
        if (child.Render is { } b && !_damageAll) AddDamage(b.Ink);
        _layeringSerial++;
        _escapeDirty = true;
    }

    void LayeringChanged()
    {
        _layeringSerial++;
        _escapeDirty = true;
    }

    // ------------------------------------------------------------ 层叠

    static bool IsPositioned(ComputedStyle s) => s.Position != CssPosition.Static;

    static bool ClipsOverflow(ComputedStyle s) => s.OverflowX != CssOverflow.Visible || s.OverflowY != CssOverflow.Visible;

    /// <summary>z-index 只对定位元素与 flex / grid 项生效；不生效或为 auto 时返回 null。</summary>
    static int? ZIndexOf(RenderBox b)
    {
        var s = b.Style!;
        if (s.ZIndex.IsAuto) return null;
        if (IsPositioned(s)) return s.ZIndex.Value;
        var parent = b.Element.Parent?.Render?.Style;
        // flex 与 grid 项目都接受 z-index
        if (parent is not null && parent.Display is CssDisplay.Flex or CssDisplay.InlineFlex or CssDisplay.Grid or CssDisplay.InlineGrid)
            return s.ZIndex.Value;
        return null;
    }

    /// <summary>根、z-index 生效的元素、opacity &lt; 1，以及 fixed / sticky 定位（与浏览器一致，即使 z-index 为 auto）建立层叠上下文。</summary>
    static bool IsStackingContext(RenderBox b) =>
        b.Element.Parent is null || ZIndexOf(b) is not null || b.Style!.Opacity < 1f
        || b.Style.Position is CssPosition.Fixed or CssPosition.Sticky;

    /// <summary>是否脱离常规流的树序、由所在层叠上下文统一安排绘制顺序。</summary>
    static bool IsLayered(RenderBox b) =>
        b.Kind is BoxKind.Container or BoxKind.Paragraph or BoxKind.Replaced && b.Style is { } s && (IsPositioned(s) || IsStackingContext(b));

    /// <summary>样式变化是否影响层叠结构（层叠项的收集与排序）。</summary>
    static bool AffectsLayering(ComputedStyle? a, ComputedStyle b) =>
        a is null || !a.ZIndex.Equals(b.ZIndex) || (a.Opacity < 1f) != (b.Opacity < 1f) || a.Position != b.Position || a.Display != b.Display
        || ClipsOverflow(a) != ClipsOverflow(b) || a.Order != b.Order;

    /// <summary>
    /// 逃逸检测（M1 评审遗留的边界情况）：对每个绝对 / 固定定位的元素 B，沿祖先链走到它的包含块 L（固定定位走到根）；
    /// 路径上严格位于 B 与 L 之间的层叠上下文 M，若 M 与 L 之间（不含两端）有溢出裁剪的祖先，则 B 不受该祖先裁剪，
    /// 而 M 的外部裁剪链却包含它：M 标记为 <see cref="RenderBox.ClipEscape"/>，绘制时不把外部裁剪链整体施加于 M。
    /// 总步数有上限（<see cref="MaxEscapeSteps"/>），超出时退化为所有上下文都逐块裁剪。
    /// </summary>
    void ComputeClipEscapes(Element rootEl)
    {
        _escapeDirty = false;
        _escapeEverywhere = false;
        var positioned = _positionedScratch;
        positioned.Clear();
        var stack = _layoutStack;
        stack.Clear();
        stack.Push(rootEl);
        while (stack.Count > 0)
        {
            var e = stack.Pop();
            var b = e.Render;
            if (b is null) continue;
            b.ClipEscape = false;
            if (!b.HasBox || b.Style is null || b.Kind is BoxKind.Inline or BoxKind.AnonymousText) continue;
            if (b.Style.Position is CssPosition.Absolute or CssPosition.Fixed) positioned.Add(b);
            if (b.Kind != BoxKind.Container) continue;
            var ch = e.Children;
            for (int i = 0; i < ch.Count; i++) stack.Push(ch[i]);
        }
        long steps = 0;
        var path = _escapePath;
        foreach (var b in positioned)
        {
            bool fixedPos = b.Style!.Position == CssPosition.Fixed;
            path.Clear();
            for (var p = b.Element.Parent; p is not null; p = p.Parent)
            {
                if (!fixedPos && p.Render?.Style is { } ps && ps.Position != CssPosition.Static) break;
                path.Add(p);
                if (++steps > MaxEscapeSteps)
                {
                    _escapeEverywhere = true;
                    positioned.Clear();
                    path.Clear();
                    return;
                }
            }
            bool clipAbove = false;
            for (int i = path.Count - 1; i >= 0; i--)
            {
                var mb = path[i].Render;
                if (mb is not { HasBox: true, Style: { } ms }) continue;
                if (clipAbove && mb.Element.Parent is not null && IsStackingContext(mb)) mb.ClipEscape = true;
                if (ClipsOverflow(ms) && ms.Display != CssDisplay.Contents) clipAbove = true;
            }
        }
        positioned.Clear();
        path.Clear();
    }

    // ------------------------------------------------------------ 录制单元

    /// <summary>缓存的 DisplayList 能否原样复用（只在合成时平移）。</summary>
    bool CanReuse(RenderBox b, bool isContext)
    {
        if (b.Cache is null || b.CacheIsContext != isContext || b.CacheEpoch != _cacheEpoch || b.DirtySerial > b.CacheSerial) return false;
        var e = b.Element;
        if (((e.DirtyFlags | e.DescendantDirtyFlags) & ElementDirtyFlags.Paint) != 0) return false;
        // 剔除：录制时没有剔除任何内容且现在也完全可见，或者相对视口的位置没有变化
        if (b.CacheComplete && ContainsRect(_cull, b.Ink)) return true;
        return b.CacheCullLocal.Equals(LocalCull(b));
    }

    Rect LocalCull(RenderBox b) => new(_cull.X - b.Border.X, _cull.Y - b.Border.Y, _cull.Width, _cull.Height);

    /// <summary>当前能否把元素作为独立的录制单元（缓存其 DisplayList）。</summary>
    bool CanBeUnit() => _incremental && _dc is not null && _suppress == 0 && _unitDepth < MaxUnitDepth;

    bool _incremental = true;

    /// <summary>
    /// 是否增量绘制（默认开启）：元素级 DisplayList 缓存与损伤跟踪。关闭时每次重绘都整帧平铺录制、<see cref="LastDamage"/> 为 null
    /// （对照测试与性能比较用）。切换后下一帧整帧重录。
    /// </summary>
    public bool IncrementalPaint
    {
        get => _incremental;
        set
        {
            if (_incremental == value) return;
            _incremental = value;
            _cacheEpoch++;
            _damageAll = true;
            _forceRepaint = true;
            _root?.RootElement.MarkDirty(ElementDirtyFlags.Paint);
        }
    }

    /// <summary>
    /// 把一个录制单元合成进当前录制：缓存有效时直接引用（并只遍历子树以重建命中测试表），否则录制新的局部 DisplayList。
    /// </summary>
    void PaintUnit(RenderBox b, bool isContext)
    {
        var parent = _dc!;
        if (CanReuse(b, isContext))
        {
            Compose(parent, b);
            _reusedUnits++;
            _dc = null;
            try
            {
                PaintBody(b, isContext);
            }
            finally
            {
                _dc = parent;
            }
            return;
        }
        var child = new DrawingContext(256);
        child.Translate(-b.Border.X, -b.Border.Y);
        _dc = child;
        _unitDepth++;
        DisplayList list;
        try
        {
            PaintBody(b, isContext);
        }
        finally
        {
            _unitDepth--;
            _dc = parent;
            list = child.Finish();
        }
        b.Cache = list;
        b.CacheIsContext = isContext;
        b.CacheSerial = _paintSerial;
        b.CacheEpoch = _cacheEpoch;
        b.CacheCullLocal = LocalCull(b);
        b.CacheComplete = ContainsRect(_cull, b.Ink);
        _recordedUnits++;
        _recordedElements.Add(b.Element);
        Compose(parent, b);
    }

    static void Compose(DrawingContext dc, RenderBox b)
    {
        var list = b.Cache!;
        if (list.IsEmpty) return;
        dc.Save();
        dc.Translate(b.Border.X, b.Border.Y);
        dc.DrawDisplayList(list);
        dc.Restore();
    }

    void PaintBody(RenderBox b, bool isContext)
    {
        if (isContext) PaintContextBody(b, isRoot: false, escape: false);
        else PaintElementBody(b);
    }

    // ------------------------------------------------------------ 层叠上下文

    void PaintStackingContext(RenderBox ctx, bool isRoot)
    {
        if (!StackGuard.HasSufficientStack() || (!isRoot && Culled(ctx))) return;
        bool escape = !isRoot && (_escapeEverywhere || ctx.ClipEscape);
        if (isRoot || escape)
        {
            PaintContextBody(ctx, isRoot, escape);
            return;
        }
        var savedClip = _hitClip;
        var savedShape = _hitClipShape;
        _dc?.Save();
        ApplyClipChain(ctx);
        if (CanBeUnit() && ctx.Style!.Opacity > 0) PaintUnit(ctx, isContext: true);
        else PaintContextBody(ctx, isRoot: false, escape: false);
        _dc?.Restore();
        _hitClip = savedClip;
        _hitClipShape = savedShape;
    }

    /// <summary>
    /// 层叠上下文的内部。非逃逸时外部裁剪链已由调用方施加，内部以本上下文为裁剪作用域；
    /// 逃逸时（<see cref="RenderBox.ClipEscape"/>）自身的背景、内容与轮廓各自施加自身的裁剪链，层叠项各自施加它们的裁剪链。
    /// </summary>
    void PaintContextBody(RenderBox ctx, bool isRoot, bool escape)
    {
        var s = ctx.Style!;
        var savedScope = _clipScope;
        if (!escape) _clipScope = ctx.Element;
        float opacity = s.Opacity;
        bool layer = false;
        if (opacity <= 0) _suppress++;
        else if (opacity < 1)
        {
            if (_suppress == 0) _dc?.PushLayer(new LayerOptions(opacity));
            layer = true;
        }
        try
        {
            EnsureLayered(ctx);
            var negative = ctx.Negative;
            var zero = ctx.Zero;
            var positive = ctx.Positive;

            if (escape) PaintOwnPiece(ctx, 0, isRoot);
            else PaintDecorations(ctx, isRoot);
            if (negative is not null)
                foreach (var item in negative) PaintStackingContext(item.Box, false);
            if (escape) PaintOwnPiece(ctx, 1, isRoot);
            else PaintContent(ctx);
            if (zero is not null)
            {
                for (int i = 0; i < zero.Count; i++)
                {
                    var item = zero[i];
                    if (IsStackingContext(item)) PaintStackingContext(item, false);
                    else PaintPositioned(item);
                    // 滚动容器的定位后代画完之后再画它的滚动条（滚动条在其内容之上）
                    PaintDeferredScrollbars(ctx, zero, i);
                }
            }
            if (positive is not null)
                foreach (var item in positive) PaintStackingContext(item.Box, false);
            if (escape) PaintOwnPiece(ctx, 2, isRoot);
            else
            {
                PaintScrollbars(ctx);
                PaintOutline(ctx);
            }
        }
        finally
        {
            if (opacity <= 0) _suppress--;
            else if (layer && _suppress == 0) _dc?.PopLayer();
            _clipScope = savedScope;
        }
    }

    /// <summary>逃逸上下文自身的一块（0 背景边框、1 内容、2 轮廓），在自身的裁剪链下绘制。</summary>
    void PaintOwnPiece(RenderBox ctx, int piece, bool isRoot)
    {
        var savedClip = _hitClip;
        var savedShape = _hitClipShape;
        _dc?.Save();
        ApplyClipChain(ctx);
        if (piece == 0) PaintDecorations(ctx, isRoot);
        else if (piece == 1) PaintContent(ctx);
        else
        {
            PaintScrollbars(ctx);
            PaintOutline(ctx);
        }
        _dc?.Restore();
        _hitClip = savedClip;
        _hitClipShape = savedShape;
    }

    /// <summary>层叠项按 <see cref="_layeringSerial"/> 缓存在上下文上；层叠结构变化后重新收集。</summary>
    void EnsureLayered(RenderBox ctx)
    {
        if (ctx.LayerSerial == _layeringSerial) return;
        ctx.LayerSerial = _layeringSerial;
        ctx.Negative?.Clear();
        ctx.Zero?.Clear();
        ctx.Positive?.Clear();
        var negative = ctx.Negative;
        var zero = ctx.Zero;
        var positive = ctx.Positive;
        CollectLayered(ctx, ref negative, ref zero, ref positive);
        if (negative is not null) SortByZ(negative);
        if (positive is not null) SortByZ(positive);
        ctx.Negative = negative is { Count: > 0 } ? negative : null;
        ctx.Zero = zero is { Count: > 0 } ? zero : null;
        ctx.Positive = positive is { Count: > 0 } ? positive : null;
        if (ctx.Zero is { } z) MarkDeferredScrollbars(ctx, z);
    }

    static void SortByZ(List<(RenderBox Box, int Z, int Order)> list) =>
        list.Sort((a, b) => a.Z != b.Z ? a.Z.CompareTo(b.Z) : a.Order.CompareTo(b.Order));

    /// <summary>
    /// 收集属于 <paramref name="ctx"/> 的层叠项：子层叠上下文（不进入其内部）与 z 为 auto 的定位元素（进入其内部，
    /// 因为它的定位后代仍属于本上下文）。按绘制顺序（flex / grid 容器的子元素按 order 修正后的文档顺序，
    /// 见 <see cref="LayoutMapper.PaintOrder"/>）编号以便稳定排序，z 相同的项与常规流的绘制顺序一致。
    /// </summary>
    void CollectLayered(RenderBox ctx, ref List<(RenderBox, int, int)>? negative, ref List<RenderBox>? zero, ref List<(RenderBox, int, int)>? positive)
    {
        if (ctx.Kind != BoxKind.Container) return;
        var stack = new Stack<Element>();
        var scratch = RentOrderScratch();
        var ch0 = LayoutMapper.PaintOrder(ctx.Element, scratch);
        for (int i = ch0.Count - 1; i >= 0; i--) stack.Push(ch0[i]);
        int order = 0;
        while (stack.Count > 0)
        {
            var e = stack.Pop();
            var b = e.Render;
            if (b is null || !b.HasBox || b.Style is null || b.Kind is BoxKind.AnonymousText or BoxKind.Inline) continue;
            if (IsLayered(b))
            {
                if (IsStackingContext(b))
                {
                    int z = ZIndexOf(b) ?? 0;
                    if (z < 0) (negative ??= new()).Add((b, z, order++));
                    else if (z > 0) (positive ??= new()).Add((b, z, order++));
                    else (zero ??= new()).Add(b);
                    continue;
                }
                (zero ??= new()).Add(b);
            }
            if (b.Kind != BoxKind.Container) continue;
            var ch = LayoutMapper.PaintOrder(e, scratch);
            for (int i = ch.Count - 1; i >= 0; i--) stack.Push(ch[i]);
        }
        ReturnOrderScratch(scratch);
    }

    /// <summary>
    /// <see cref="LayoutMapper.PaintOrder"/> 的缓冲池：<see cref="PaintContent"/> 递归绘制子元素时外层的排序结果仍在使用，
    /// 每层各自租用一个列表（异常时未归还的列表只是交给 GC）。
    /// </summary>
    readonly Stack<List<Element>> _orderScratchPool = new();

    List<Element> RentOrderScratch() => _orderScratchPool.Count > 0 ? _orderScratchPool.Pop() : new List<Element>();

    void ReturnOrderScratch(List<Element> list)
    {
        list.Clear();
        _orderScratchPool.Push(list);
    }

    void PaintPositioned(RenderBox b)
    {
        if (Culled(b)) return;
        var savedClip = _hitClip;
        var savedShape = _hitClipShape;
        _dc?.Save();
        ApplyClipChain(b);
        PaintElement(b);
        _dc?.Restore();
        _hitClip = savedClip;
        _hitClipShape = savedShape;
    }

    // ------------------------------------------------------------ 元素

    /// <summary>常规流中的一个元素（或 z 为 auto 的非上下文定位元素）：作为录制单元，或直接录进当前单元。</summary>
    void PaintElement(RenderBox b)
    {
        if (!StackGuard.HasSufficientStack() || Culled(b)) return;
        if (CanBeUnit()) PaintUnit(b, isContext: false);
        else PaintElementBody(b);
    }

    /// <summary>背景边框 → 内容（段落文本、替换内容或子元素）→ 轮廓。</summary>
    void PaintElementBody(RenderBox b)
    {
        PaintDecorations(b, false);
        PaintContent(b);
        PaintScrollbars(b);
        PaintOutline(b);
    }

    void PaintDecorations(RenderBox b, bool isRoot)
    {
        var s = b.Style!;
        if (s.Display == CssDisplay.Contents) return;
        bool visible = s.Visibility == CssVisibility.Visible;
        if (visible && _suppress == 0 && _dc is { } dc) BoxPainter.PaintBox(dc, s, b.Border, _scale, skipBackgroundColor: isRoot);
        if (visible)
        {
            var shape = isRoot ? new RoundRect(new Rect(0, 0, _viewport.Width, _viewport.Height), 0) : BoxPainter.OuterShape(s, b.Border);
            _hits.Add(new HitEntry(b.Element, shape, _hitClip, _hitClipShape, s.PointerEvents == CssPointerEvents.None));
        }
    }

    void PaintOutline(RenderBox b)
    {
        var s = b.Style!;
        if (_suppress != 0 || _dc is null || s.Display == CssDisplay.Contents || s.Visibility != CssVisibility.Visible) return;
        if (s.OutlineStyle is CssLineStyle.None or CssLineStyle.Hidden) return;
        BoxPainter.PaintOutline(_dc, s, b.Border, _scale);
    }

    void PaintContent(RenderBox b)
    {
        var s = b.Style!;
        bool clip = ClipsOverflow(s) && s.Display != CssDisplay.Contents;
        var savedClip = _hitClip;
        var savedShape = _hitClipShape;
        if (clip)
        {
            _dc?.Save();
            ClipToPaddingBox(b);
        }
        if (b.Kind == BoxKind.Paragraph)
        {
            PaintParagraph(b, s);
        }
        else if (b.Kind == BoxKind.Replaced)
        {
            PaintReplaced(b, s);
        }
        else
        {
            // flex / grid 容器的子元素按 order 修正后的文档顺序绘制（CSS Display 3 §3），与布局排列项目的顺序一致；
            // 命中测试按绘制记录的逆序进行，所以随之一致
            var scratch = RentOrderScratch();
            var ch = LayoutMapper.PaintOrder(b.Element, scratch);
            for (int i = 0; i < ch.Count; i++)
            {
                var cb = ch[i].Render;
                if (cb is null || !cb.HasBox || cb.Style is null) continue;
                if (cb.Kind == BoxKind.AnonymousText)
                {
                    if (!Culled(cb)) PaintParagraph(cb, s);
                    continue;
                }
                if (IsLayered(cb)) continue;
                PaintElement(cb);
            }
            ReturnOrderScratch(scratch);
        }
        if (clip) _dc?.Restore();
        _hitClip = savedClip;
        _hitClipShape = savedShape;
    }

    void PaintParagraph(RenderBox b, ComputedStyle style)
    {
        var para = b.Paragraph;
        if (para is null || _suppress != 0 || _dc is null) return;
        if (style.Visibility != CssVisibility.Visible) return;
        if (b.Element is InputElement input)
        {
            // 输入框：选区、预编辑文本、插入点与水平滚动（Input/RenderPipeline.Input.cs）
            PaintInput(b, style, para, input);
            return;
        }
        var tl = para.Layout;
        var origin = new Point(Snap(b.ContentX), Snap(b.ContentY));
        if (!para.IsEmpty)
        {
            tl.Layout(b.ContentWidth + TextParagraph.WidthSlack);
            tl.Paint(_dc, origin);
        }
    }

    /// <summary>替换元素（图片、SVG）：内容画在内容盒内，裁剪到内容盒。只遍历时也记录可见性（动画图片只在可见时推进）。</summary>
    void PaintReplaced(RenderBox b, ComputedStyle style)
    {
        var content = b.Replaced;
        if (content is null || style.Visibility != CssVisibility.Visible || _suppress != 0) return;
        var rect = new Rect(Snap(b.ContentX), Snap(b.ContentY), b.ContentWidth, b.ContentHeight);
        rect = Rect.FromLTRB(rect.Left, rect.Top, Snap(b.ContentX + b.ContentWidth), Snap(b.ContentY + b.ContentHeight));
        if (rect.Width <= 0 || rect.Height <= 0) return;
        content.NotePainted();
        if (_dc is null) return;
        content.Paint(_dc, rect, style, _scale);
    }

    float Snap(float v) => (float)Math.Floor(v * _scale + 0.5f) / _scale;

    /// <summary>裁剪到内边距盒（溢出裁剪作用于内边距盒，圆角取内缘半径）。</summary>
    void ClipToPaddingBox(RenderBox b)
    {
        var s = b.Style!;
        var outer = BoxPainter.OuterShape(s, b.Border);
        var inner = BoxPainter.InnerShape(outer, BoxPainter.GetBorderWidths(s, _scale));
        if (_dc is { } dc)
        {
            if (inner.IsRect) dc.ClipRect(inner.Rect);
            else dc.ClipRoundRect(inner);
        }
        _hitClip = _hitClip.Intersect(inner.Rect);
        if (_hitClip.IsEmpty) _hitClip = default;
        if (!inner.IsRect) _hitClipShape = inner;
    }

    /// <summary>
    /// 脱离树序绘制的元素：沿包含块链向上，收集会裁剪它的祖先（static/relative 的包含块链是父元素；absolute 是最近的定位祖先；
    /// fixed 是视口，不受祖先裁剪），从外到内施加。只收集到当前裁剪作用域 <see cref="_clipScope"/> 为止（含作用域自身的溢出裁剪）：
    /// 作用域之外的裁剪已在合成作用域时施加。链在作用域之上落地时（绝对定位跳出了作用域）也就此停止——
    /// 这种情况下作用域没有被它跳过的外部裁剪（否则作用域已标记为逃逸，见 <see cref="ComputeClipEscapes"/>）。
    /// </summary>
    void ApplyClipChain(RenderBox item)
    {
        var scope = _clipScope;
        var chain = _clipChain;
        chain.Clear();
        var cur = item.Element;
        while (true)
        {
            var cs = cur.Render?.Style;
            if (cs is null || cs.Position == CssPosition.Fixed) break;
            var next = cur.Parent;
            if (cs.Position == CssPosition.Absolute)
            {
                while (next is not null && (next.Render?.Style is not { } ns || ns.Position == CssPosition.Static))
                {
                    if (ReferenceEquals(next, scope))
                    {
                        next = null;
                        break;
                    }
                    next = next.Parent;
                }
            }
            if (next is null) break;
            if (next.Render is { HasBox: true, Style: { } nextStyle } && ClipsOverflow(nextStyle) && nextStyle.Display != CssDisplay.Contents)
                chain.Add(next);
            if (ReferenceEquals(next, scope)) break;
            cur = next;
        }
        for (int i = chain.Count - 1; i >= 0; i--) ClipToPaddingBox(chain[i].Render!);
        chain.Clear();
    }

    // ------------------------------------------------------------ 命中测试

    Element? HitTestCore(Point p, bool ignorePointerEvents = false)
    {
        for (int i = _hits.Count - 1; i >= 0; i--)
        {
            var h = _hits[i];
            if (h.PointerEventsNone && !ignorePointerEvents) continue;
            if (!h.Clip.Contains(p)) continue;
            if (!h.ClipShape.IsRect && !Contains(h.ClipShape, p)) continue;
            if (!Contains(h.Shape, p)) continue;
            if (!h.Element.IsAttached || !ReferenceEquals(h.Element.Root, _root)) continue;
            return h.Element;
        }
        return null;
    }

    /// <summary>点是否在圆角矩形内（角上按椭圆判断）。</summary>
    static bool Contains(in RoundRect rr, Point p)
    {
        var r = rr.Rect;
        if (!r.Contains(p)) return false;
        if (rr.IsRect) return true;
        return InCorner(p, r.Left + rr.TopLeft.Width, r.Top + rr.TopLeft.Height, rr.TopLeft, -1, -1)
               && InCorner(p, r.Right - rr.TopRight.Width, r.Top + rr.TopRight.Height, rr.TopRight, 1, -1)
               && InCorner(p, r.Right - rr.BottomRight.Width, r.Bottom - rr.BottomRight.Height, rr.BottomRight, 1, 1)
               && InCorner(p, r.Left + rr.BottomLeft.Width, r.Bottom - rr.BottomLeft.Height, rr.BottomLeft, -1, 1);
    }

    static bool InCorner(Point p, float cx, float cy, Size radius, int sx, int sy)
    {
        if (radius.Width <= 0 || radius.Height <= 0) return true;
        float dx = p.X - cx, dy = p.Y - cy;
        if (dx * sx <= 0 || dy * sy <= 0) return true; // 不在该角的象限内
        float nx = dx / radius.Width, ny = dy / radius.Height;
        return nx * nx + ny * ny <= 1;
    }

    sealed class PipelineHitTester : HitTester
    {
        readonly RenderPipeline _p;

        public PipelineHitTester(RenderPipeline p) => _p = p;

        public override Element? HitTest(Point position) => _p.HitTestCore(position);
    }
}
