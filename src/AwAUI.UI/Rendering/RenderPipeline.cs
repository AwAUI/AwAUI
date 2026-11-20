using System;
using System.Collections.Generic;
using System.Diagnostics;
using AwAUI.Css;
using AwAUI.Css.Cascade;
using AwAUI.Css.Values;
using AwAUI.Graphics;
using AwAUI.Layout;
using AwAUI.Text;

namespace AwAUI.UI;

/// <summary>一帧中帧管线各阶段的耗时与工作量（毫秒为墙钟时间，用 <see cref="Stopwatch"/> 测得）。</summary>
public readonly struct FrameTimings
{
    public FrameTimings(double styleMs, double layoutMs, double paintMs, int styledElements, int paragraphsBuilt, bool layoutRan, bool repainted, int commandCount)
        : this(styleMs, layoutMs, paintMs, styledElements, paragraphsBuilt, layoutRan, repainted, commandCount, 0, 0)
    {
    }

    /// <param name="recordedElements">本帧重新录制的元素（录制单元）数。</param>
    /// <param name="reusedElements">本帧直接复用缓存 DisplayList 的录制单元数（只在合成时平移）。</param>
    public FrameTimings(double styleMs, double layoutMs, double paintMs, int styledElements, int paragraphsBuilt, bool layoutRan, bool repainted, int commandCount,
        int recordedElements, int reusedElements)
    {
        RecordedElements = recordedElements;
        ReusedElements = reusedElements;
        StyleMs = styleMs;
        LayoutMs = layoutMs;
        PaintMs = paintMs;
        StyledElements = styledElements;
        ParagraphsBuilt = paragraphsBuilt;
        LayoutRan = layoutRan;
        Repainted = repainted;
        CommandCount = commandCount;
    }

    /// <summary>样式阶段（级联与差异）。</summary>
    public double StyleMs { get; }

    /// <summary>布局阶段（同步布局树、重建段落、布局计算、像素对齐与绝对几何）。</summary>
    public double LayoutMs { get; }

    /// <summary>绘制阶段（录制 DisplayList 与命中测试表）。</summary>
    public double PaintMs { get; }

    /// <summary>本帧重新计算了样式的元素数。</summary>
    public int StyledElements { get; }

    /// <summary>本帧重建的段落数。</summary>
    public int ParagraphsBuilt { get; }

    /// <summary>本帧是否执行了布局计算（布局树或视口有变化）。</summary>
    public bool LayoutRan { get; }

    /// <summary>本帧是否重录了 DisplayList（为 false 时 <see cref="RenderPipeline.Frame"/> 沿用上一帧）。</summary>
    public bool Repainted { get; }

    /// <summary>帧 DisplayList 的命令数（只计顶层列表；嵌入的元素列表各自计数）。</summary>
    public int CommandCount { get; }

    /// <summary>本帧重新录制 DisplayList 的元素数（增量绘制：只有绘制变化或局部几何变化的元素及其祖先单元重录）。</summary>
    public int RecordedElements { get; }

    /// <summary>本帧复用缓存 DisplayList 的元素数（子树没有变化，或只是整体移动了位置）。</summary>
    public int ReusedElements { get; }

    public override string ToString() =>
        FormattableString.Invariant($"style {StyleMs:F2} ms ({StyledElements} el), layout {LayoutMs:F2} ms ({ParagraphsBuilt} para{(LayoutRan ? "" : ", cached")}), paint {PaintMs:F2} ms ({CommandCount} cmds, {RecordedElements} recorded / {ReusedElements} reused{(Repainted ? "" : ", frame reused")})");
}

/// <summary>
/// 标准帧管线（design/05「帧内流程」的 Style → Layout → Paint 部分）：把元素树经 CSS 级联、布局引擎与盒绘制器变成一帧
/// <see cref="DisplayList"/>，并维护命中测试表。设置到 <see cref="UIRoot.Pipeline"/> 后由 <see cref="UIRoot.RunFrame"/> 调用；
/// 不引用任何渲染后端（design/01 规则 1），由宿主把 <see cref="Frame"/> 交给 <see cref="RenderTarget"/>。
/// <para>
/// 每个阶段只处理脏集合：
/// <list type="bullet">
/// <item><b>样式</b>：只重算带 Style 标记的元素；class / id / 属性 / 伪状态的变化按 <see cref="StyleInvalidation"/> 判断是否需要
/// 连带子树或兄弟；继承分组或自定义属性变化时子元素重算。新旧计算样式经 <see cref="ComputedStyle.Diff"/> 转成布局 / 绘制脏标记。</item>
/// <item><b>布局</b>：只同步带 Layout / Paint 标记的路径（结构变化重建布局子节点列表、样式变化重新映射 <see cref="LayoutStyle"/>、
/// 文本变化重建段落）；布局引擎自身按节点缓存，干净子树不重算。</item>
/// <item><b>绘制</b>：没有任何布局或绘制变化时沿用上一帧的 DisplayList（<see cref="FrameVersion"/> 不变，宿主据此跳过光栅化）；
/// 否则重新合成帧：元素级 DisplayList 缓存（design/18「不提升为层的元素通过 DrawDisplayList 嵌入」）只重录绘制变化或局部几何变化的元素
/// 及包含它们的外层单元，只移动了位置的子树在合成时平移；同时给出相对上一帧的损伤区域 <see cref="LastDamage"/>。</item>
/// </list>
/// </para>
/// <para>只能在 UI 线程上使用；一个实例服务一个 <see cref="UIRoot"/>。</para>
/// </summary>
public sealed partial class RenderPipeline : FramePipeline
{
    readonly StyleEngine _engine;
    readonly LayoutEngine _layoutEngine = new();
    readonly StyleInvalidation _invalidation = new();
    readonly TextStyleMapper _textStyles = new();
    readonly Dictionary<string, CssInlineStyle> _inlineStyles = new(StringComparer.Ordinal);
    readonly PipelineObserver _observer;
    readonly HitTester _hitTester;
    readonly List<CssDiagnostic> _inlineDiagnostics = new();
    const int MaxInlineStyleCacheEntries = 2048;

    UIRoot? _root;
    Size _viewport = new(800, 600);
    float _scale = 1;
    bool _invalidationDirty = true;
    bool _fullRestyle = true;
    bool _viewportChanged = true;
    bool _forceRepaint = true;
    DisplayList _frame = DisplayList.Empty;
    long _frameVersion;

    /// <param name="textEngine">文本引擎（字体、整形、段落布局）。由应用的组合根选择，测试可传 <c>TestTextEngine</c>。</param>
    /// <param name="environment">CSS 环境（媒体特性等）；为 null 时新建。视口尺寸由 <see cref="SetViewport"/> 维护。</param>
    /// <param name="includeDefaultStyleSheet">是否加入 <see cref="DefaultStyleSheet"/>（UserAgent 来源）。</param>
    public RenderPipeline(TextEngine textEngine, CssEnvironment? environment = null, bool includeDefaultStyleSheet = true)
    {
        TextEngine = textEngine ?? throw new ArgumentNullException(nameof(textEngine));
        _engine = new StyleEngine(environment);
        if (includeDefaultStyleSheet) _engine.AddStyleSheet(DefaultStyleSheet.Sheet, CascadeOrigin.UserAgent);
        _observer = new PipelineObserver(this);
        _hitTester = new GeneratedAwareHitTester(this);
    }

    /// <summary>文本引擎。</summary>
    public TextEngine TextEngine { get; }

    /// <summary>CSS 级联引擎（只读访问；增删样式表请用 <see cref="AddStyleSheet(StyleSheet, CascadeOrigin)"/>，以便重算样式）。</summary>
    public StyleEngine StyleEngine => _engine;

    /// <summary>视口（客户区）尺寸，逻辑像素。</summary>
    public Size Viewport => _viewport;

    /// <summary>逻辑像素到设备像素的缩放（DPI）。布局结果按它对齐到设备像素。</summary>
    public float Scale => _scale;

    /// <summary>最近一帧的 DisplayList（逻辑像素坐标）。还没有执行过帧时为空列表。</summary>
    public DisplayList Frame => _frame;

    /// <summary>每次重录 DisplayList 加一；宿主比较它决定是否需要重新光栅化。</summary>
    public long FrameVersion => _frameVersion;

    /// <summary>最近一帧各阶段的耗时。</summary>
    public FrameTimings LastTimings { get; private set; }

    /// <summary>基于最近一帧布局与绘制顺序的命中测试；挂接时设置到 <see cref="InputManager.HitTester"/>。</summary>
    public HitTester HitTester => _hitTester;

    /// <summary>客户区坐标处最上层的可命中元素（遵循 <c>pointer-events</c>、<c>visibility</c>、溢出裁剪与层叠顺序）。</summary>
    public Element? HitTest(Point position) => _hitTester.HitTest(position);

    /// <summary>
    /// 命中测试；<paramref name="ignorePointerEvents"/> 为 true 时也考虑 <c>pointer-events: none</c> 的元素（DevTools 拾取，与浏览器的检查器一致）。
    /// </summary>
    public Element? HitTest(Point position, bool ignorePointerEvents) => MapGeneratedHit(HitTestCore(position, ignorePointerEvents));

    /// <summary>加入应用样式表（默认 Author 来源）。下一帧全量重算样式。</summary>
    public CascadeStyleSheet AddStyleSheet(StyleSheet sheet, CascadeOrigin origin = CascadeOrigin.Author)
    {
        var cs = _engine.AddStyleSheet(sheet, origin);
        StyleSheetsChanged();
        StyleSheetAdded?.Invoke(cs);
        return cs;
    }

    /// <summary>解析 CSS 文本并加入（Author 来源）。解析诊断见返回对象的 <see cref="CascadeStyleSheet.Diagnostics"/> 与 <see cref="StyleSheet.Diagnostics"/>。</summary>
    public CascadeStyleSheet AddStyleSheet(string css, string? sourceName = null) => AddStyleSheet(StyleSheet.Parse(css, sourceName));

    /// <summary>移除样式表（热重载、DevTools 编辑）。</summary>
    public bool RemoveStyleSheet(CascadeStyleSheet sheet)
    {
        if (!_engine.RemoveStyleSheet(sheet)) return false;
        StyleSheetsChanged();
        StyleSheetRemoved?.Invoke(sheet);
        return true;
    }

    /// <summary>
    /// 原位替换样式表（沿用其来源与级联位置），下一帧全量重算样式。DevTools 编辑规则、热重载使用。<paramref name="sheet"/> 未注册时返回 null。
    /// </summary>
    public CascadeStyleSheet? ReplaceStyleSheet(CascadeStyleSheet sheet, StyleSheet replacement)
    {
        var cs = _engine.ReplaceStyleSheet(sheet, replacement);
        if (cs is null) return null;
        StyleSheetsChanged();
        StyleSheetReplaced?.Invoke(sheet, cs);
        return cs;
    }

    /// <summary>样式表被 <see cref="ReplaceStyleSheet"/> 原位替换（旧, 新）。DevTools 据此更新样式表 id 的映射。</summary>
    internal event Action<CascadeStyleSheet, CascadeStyleSheet>? StyleSheetReplaced;

    /// <summary>样式表被加入（含组件样式表）。</summary>
    internal event Action<CascadeStyleSheet>? StyleSheetAdded;

    /// <summary>样式表被移除。</summary>
    internal event Action<CascadeStyleSheet>? StyleSheetRemoved;

    /// <summary>元素的计算样式在样式阶段换成了新对象（DevTools 推送 <c>Css.computedStyleInvalidated</c>）。</summary>
    internal Action<Element>? ComputedStyleChanged;

    // ------------------------------------------------------------ 组件样式表

    UIRoot? _componentSheetsRoot;
    int _componentSheetsAdded;

    public override void OnComponentStyleSheet(UIRoot root, ComponentStyleSheet sheet)
    {
        if (root is null) throw new ArgumentNullException(nameof(root));
        AddPendingComponentSheets(root);
    }

    /// <summary>把根上尚未加入的组件样式表按登记顺序加入（Author 来源）。解析诊断报告到 <see cref="UIRoot.Diagnostic"/>。</summary>
    void AddPendingComponentSheets(UIRoot root)
    {
        if (!ReferenceEquals(_componentSheetsRoot, root))
        {
            _componentSheetsRoot = root;
            _componentSheetsAdded = 0;
        }
        // 应用样式表（Tailwind 等）先于组件样式表：它的 @layer 声明（theme, base, components, utilities）要先确立层顺序
        AddPendingAppStyleSheets(root);
        var list = root.ComponentStyleSheets;
        while (_componentSheetsAdded < list.Count)
        {
            var cs = list[_componentSheetsAdded++];
            var parsed = StyleSheet.Parse(cs.Text, cs.SourceName);
            var added = AddStyleSheet(parsed);
            foreach (var d in parsed.Diagnostics) ReportSheetDiagnostic(root, cs, d);
            foreach (var d in added.Diagnostics) ReportSheetDiagnostic(root, cs, d);
        }
    }

    static void ReportSheetDiagnostic(UIRoot root, ComponentStyleSheet sheet, CssDiagnostic d) =>
        root.ReportDiagnostic(new UIDiagnostic(d.Code,
            d.Severity == CssDiagnosticSeverity.Error ? DiagnosticSeverity.Error : DiagnosticSeverity.Warning,
            "组件样式（" + sheet.SourceName + "）：" + d.Message, null));

    void StyleSheetsChanged()
    {
        _invalidationDirty = true;
        _fullRestyle = true;
        _root?.RootElement.MarkDirty(ElementDirtyFlags.Style);
    }

    /// <summary>设置视口尺寸与缩放（窗口尺寸或 DPI 变化）。下一帧重新布局；媒体查询与视口单位随之重算。</summary>
    public void SetViewport(Size size, float scale)
    {
        if (!(scale > 0) || float.IsInfinity(scale)) scale = 1;
        float w = Sanitize(size.Width), h = Sanitize(size.Height);
        if (w == _viewport.Width && h == _viewport.Height && scale == _scale) return;
        bool sizeChanged = w != _viewport.Width || h != _viewport.Height;
        // 缩放变化：边框宽度的设备像素对齐随之变化，所有盒都要重新映射布局样式（DPI 变化很少发生）
        if (scale != _scale)
        {
            _forceParagraphRebuild = true;
            _cacheEpoch++;
        }
        _damageAll = true;
        _viewport = new Size(w, h);
        _scale = scale;
        _viewportChanged = true;
        var media = _engine.Environment.Media;
        media.Width = w;
        media.Height = h;
        media.Resolution = scale;
        if (_engine.EnvironmentChanged()) _fullRestyle = true;
        else if (sizeChanged) _viewportRestyle = true;
        _root?.RootElement.MarkDirty(ElementDirtyFlags.Style | ElementDirtyFlags.Layout | ElementDirtyFlags.Paint);
    }

    bool _viewportRestyle;

    static float Sanitize(float v) => float.IsNaN(v) || v < 0 ? 0 : Math.Min(v, 1e6f);

    /// <summary>强制下一帧全量重算样式、重新布局并重绘（例如字体注册变化后）。</summary>
    public void InvalidateAll()
    {
        _fullRestyle = true;
        _forceRepaint = true;
        _textStyles.Clear();
        _forceParagraphRebuild = true;
        _cacheEpoch++;
        _damageAll = true;
        LayeringChanged();
        _root?.RootElement.MarkDirty(ElementDirtyFlags.All);
    }

    bool _forceParagraphRebuild;

    // ------------------------------------------------------------ 帧

    /// <summary>每帧最多触发 <see cref="UIRoot.LayoutCompleted"/> 的轮数（每轮之后重算样式与布局）。</summary>
    public const int MaxLayoutCompletedRounds = 3;

    public override void Run(UIRoot root)
    {
        if (root is null) throw new ArgumentNullException(nameof(root));
        Attach(root);
        AddPendingAppStyleSheets(root);
        PruneGeneratedContent();
        // 滚动动画、惯性与回弹按帧时钟推进（偏移在布局阶段之后施加到几何上）
        LastFrameScrolled = false;
        _scrollManager?.BeginFrame();
        var sw = Stopwatch.StartNew();
        // CSS 动画与过渡：读取帧时钟，推进只有时间变化的元素（Animation/RenderPipeline.Animation.cs）
        BeginAnimationFrame(root);
        int styled = StylePass(root);
        double styleMs = sw.Elapsed.TotalMilliseconds;
        sw.Restart();
        int paragraphs = LayoutPass(root, out bool layoutRan, out bool geometryChanged);
        // 容器查询：查询容器的尺寸由布局给出，依赖它的元素在布局之后重算样式并再布局（有上限，RenderPipeline.Containers.cs）。
        // 不只在本帧布局过时检查：上一帧布局完成事件之后的再布局也可能改变了容器尺寸（只比较登记的容器，开销与容器数成正比）
        paragraphs += ContainerQueryPasses(root, ref styled, ref layoutRan, ref geometryChanged);
        // 布局完成（UIRoot.LayoutCompleted）：处理器（浮层定位、自动聚焦）改变的样式在本帧内重算并布局，最多若干轮。
        // 处理器没有改变任何样式（也没有新的一次性回调）时不再重复：稳定状态下每帧只触发一次、没有额外的样式与布局遍历
        for (int round = 0; round < MaxLayoutCompletedRounds && root.HasLayoutCompletedWork; round++)
        {
            // Leave one callback round to observe the new geometry and defer any further builds.
            bool rebuilt = root.RunLayoutCallbacksAndRebuild(allowRebuild: round + 1 < MaxLayoutCompletedRounds);
            bool restyle = ((root.RootElement.DirtyFlags | root.RootElement.DescendantDirtyFlags) & ElementDirtyFlags.Style) != 0;
            if (!rebuilt && !restyle && !root.HasPendingAfterLayout) break;
            if (!rebuilt && !restyle) continue;
            if (restyle) styled += StylePass(root);
            paragraphs += LayoutPass(root, out bool lr, out bool gc);
            layoutRan |= lr;
            geometryChanged |= gc;
            if (rebuilt) paragraphs += ContainerQueryPasses(root, ref styled, ref layoutRan, ref geometryChanged);
        }
        double layoutMs = sw.Elapsed.TotalMilliseconds;
        sw.Restart();
        bool repaint = geometryChanged || _forceRepaint || _scrollRepaint
                       || (root.RootElement.DirtyFlags | root.RootElement.DescendantDirtyFlags) != ElementDirtyFlags.None;
        if (repaint) PaintPass(root, geometryChanged || _forceRepaint || _scrollInkFull);
        else
        {
            _recordedUnits = _reusedUnits = 0;
            _recordedElements.Clear();
        }
        _scrollRepaint = _scrollInkFull = false;
        ClearAllFlags(root.RootElement);
        // 动画事件与下一帧的请求（继续采样进行中的动画，或为延迟阶段安排定时器）
        EndAnimationFrame(root);
        double paintMs = sw.Elapsed.TotalMilliseconds;
        LastTimings = new FrameTimings(styleMs, layoutMs, paintMs, styled, paragraphs, layoutRan, repaint, _frame.CommandCount, _recordedUnits, _reusedUnits);
        // 每帧最多一次的 scroll / scrollend（处理器可能写 State，下一帧生效）
        _scrollManager?.EndFrame();
    }

    void Attach(UIRoot root)
    {
        if (ReferenceEquals(_root, root)) return;
        if (_root is not null)
        {
            _root.RemoveObserver(_observer);
            if (ReferenceEquals(_root.Input.HitTester, _hitTester)) _root.Input.HitTester = null;
            if (ReferenceEquals(_root.Input.Scrolling, _scrollManager)) _root.Input.Scrolling = null;
        }
        if (_generated.Count > 0) _pruneGenerated = true;
        _root = root;
        root.AddObserver(_observer);
        root.Input.HitTester = _hitTester;
        Scroll.Attach(root);
        root.Input.Scrolling = Scroll;
        AddPendingComponentSheets(root);
        _fullRestyle = true;
        _forceRepaint = true;
        _viewportChanged = true;
        _cacheEpoch++;
        _damageAll = true;
        LayeringChanged();
    }

    /// <summary>帧末清除剩余的脏标记（段落内部元素的布局 / 绘制标记、语义标记等）。只沿脏路径走。</summary>
    static void ClearAllFlags(Element rootElement)
    {
        if ((rootElement.DirtyFlags | rootElement.DescendantDirtyFlags) == ElementDirtyFlags.None) return;
        var stack = new Stack<Element>();
        stack.Push(rootElement);
        while (stack.Count > 0)
        {
            var e = stack.Pop();
            bool descend = e.DescendantDirtyFlags != ElementDirtyFlags.None;
            e.ClearDirty(ElementDirtyFlags.All);
            if (!descend) continue;
            var ch = e.Children;
            for (int i = ch.Count - 1; i >= 0; i--)
            {
                var c = ch[i];
                if ((c.DirtyFlags | c.DescendantDirtyFlags) != ElementDirtyFlags.None) stack.Push(c);
            }
        }
    }

    // ------------------------------------------------------------ 内联样式

    /// <summary>按文本缓存解析后的内联样式（同样的 <c>Style</c> 文本只解析一次）。诊断报告到 <see cref="UIRoot.Diagnostic"/>。</summary>
    internal CssInlineStyle GetInlineStyle(string text, Element element)
    {
        if (_inlineStyles.TryGetValue(text, out var style)) return style;
        _inlineDiagnostics.Clear();
        style = CssInlineStyle.Parse(text, _inlineDiagnostics);
        if (_inlineStyles.Count >= MaxInlineStyleCacheEntries) _inlineStyles.Clear();
        _inlineStyles[text] = style;
        if (_inlineDiagnostics.Count > 0 && _root is not null)
        {
            foreach (var d in _inlineDiagnostics)
                _root.ReportDiagnostic(new UIDiagnostic(d.Code,
                    d.Severity == CssDiagnosticSeverity.Error ? DiagnosticSeverity.Error : DiagnosticSeverity.Warning,
                    "内联样式（" + element + "）：" + d.Message, element.Owner));
        }
        return style;
    }

    // ------------------------------------------------------------ 样式

    struct StyleWork
    {
        public Element Element;
        public ComputedStyle? Parent;
        /// <summary>0：只在自身有标记时重算；1：重算自身；2：重算整棵子树。</summary>
        public byte Mode;
    }

    readonly Stack<StyleWork> _styleStack = new();

    int StylePass(UIRoot root)
    {
        if (_invalidationDirty)
        {
            _invalidation.Rebuild(_engine);
            _invalidationDirty = false;
        }
        var rootEl = root.RootElement;
        if (_viewportRestyle)
        {
            _viewportRestyle = false;
            if (!_fullRestyle) MarkViewportDependent(rootEl);
        }
        bool full = _fullRestyle;
        _fullRestyle = false;
        if (!full && ((rootEl.DirtyFlags | rootEl.DescendantDirtyFlags) & ElementDirtyFlags.Style) == 0) return 0;

        int count = 0;
        var stack = _styleStack;
        stack.Clear();
        stack.Push(new StyleWork { Element = rootEl, Parent = null, Mode = full ? (byte)2 : (byte)0 });
        // 一次重算批次：:has() 的结果在批次内按 (选择器, 元素) 缓存
        var batch = _engine.BeginRecalcBatch();
        try
        {
            while (stack.Count > 0)
            {
                var w = stack.Pop();
                var e = w.Element;
                var box = RenderBox.Get(e);
                if (e.IsTextNode)
                {
                    UpdateTextNodeStyle(e, box, w.Parent);
                    e.ClearDirty(ElementDirtyFlags.Style);
                    continue;
                }

                byte childMode = w.Mode == 2 || box.RestyleSubtree ? (byte)2 : (byte)0;
                bool changed = false;
                bool self = w.Mode != 0 || box.RestyleSubtree || box.Style is null || (e.DirtyFlags & ElementDirtyFlags.Style) != 0;
                if (self)
                {
                    count++;
                    var old = box.Style;
                    ComputedStyle next;
                    try
                    {
                        next = ResolveStyle(e, box, w.Parent);
                    }
                    catch (InsufficientExecutionStackException ex)
                    {
                        // 选择器匹配在极深的树上可能耗尽栈：保留旧样式（或继承父样式），报告后继续
                        root.ReportDiagnostic(new UIDiagnostic("AW0001", DiagnosticSeverity.Error, "样式计算失败：" + ex.Message, e.Owner));
                        next = old ?? w.Parent ?? ComputedStyle.Initial;
                    }
                    box.StyledClasses = e.Classes;
                    if (!ReferenceEquals(old, next))
                    {
                        changed = true;
                        box.Style = next;
                        ComputedStyleChanged?.Invoke(e);
                        var diff = ComputedStyle.Diff(old, next);
                        ApplyStyleDifference(e, box, diff, old, next);
                        if (AffectsLayering(old, next)) LayeringChanged();
                        if (old is null || InheritedChanged(old, next)) childMode = Math.Max(childMode, (byte)1);
                        // 祖先的 display:none 切换会停止或重启后代的动画，包括暂停和延迟的动画。
                        if (old is not null && (old.Display == CssDisplay.None) != (next.Display == CssDisplay.None)) childMode = 2;
                        if (old is not null && DecorationChanged(old, next)) MarkDecoratedText(e);
                    }
                    // 伪元素（::before / ::after / ::placeholder / ::selection）随原始元素一起重算（RenderPipeline.Pseudo.cs）
                    UpdatePseudoElements(e, box);
                }
                box.RestyleSubtree = false;
                byte directMode = childMode;
                if (box.RestyleChildren) directMode = Math.Max(directMode, box.RestyleChildSubtrees ? (byte)2 : (byte)1);
                box.RestyleChildren = box.RestyleChildSubtrees = false;
                bool descend = directMode != 0 || changed || (e.DescendantDirtyFlags & ElementDirtyFlags.Style) != 0;
                e.ClearDirty(ElementDirtyFlags.Style);
                if (!descend) continue;
                var ch = e.Children;
                var parentStyle = box.Style;
                for (int i = ch.Count - 1; i >= 0; i--)
                {
                    var c = ch[i];
                    // 文本节点共享父元素的样式对象：父样式换了实例就要同步（便宜，只比较几个分组）
                    if (c.IsTextNode && changed)
                    {
                        stack.Push(new StyleWork { Element = c, Parent = parentStyle, Mode = directMode });
                        continue;
                    }
                    // 子元素对非继承属性用了 inherit：父样式换了实例（可能只是非继承分组变化）就要重算它
                    if (directMode == 0 && changed && c.Render?.Style is { } cs && (cs.Flags & ComputedStyleFlags.InheritsNonInherited) != 0)
                    {
                        stack.Push(new StyleWork { Element = c, Parent = parentStyle, Mode = 1 });
                        continue;
                    }
                    if (directMode == 0 && ((c.DirtyFlags | c.DescendantDirtyFlags) & ElementDirtyFlags.Style) == 0
                        && c.Render is { } cb && (cb.Style is not null || c.IsTextNode) && !cb.RestyleSubtree && !cb.RestyleChildren)
                        continue;
                    stack.Push(new StyleWork { Element = c, Parent = parentStyle, Mode = directMode });
                }
            }
        }
        finally
        {
            batch.Dispose();
        }
        return count;
    }

    static bool InheritedChanged(ComputedStyle a, ComputedStyle b) =>
        !ReferenceEquals(a.FontGroup, b.FontGroup) || !ReferenceEquals(a.TextGroup, b.TextGroup)
        || !ReferenceEquals(a.InteractionGroup, b.InteractionGroup) || !ReferenceEquals(a.CustomProperties, b.CustomProperties);

    void ApplyStyleDifference(Element e, RenderBox box, Css.Values.StyleDifference diff, ComputedStyle? old, ComputedStyle next)
    {
        const Css.Values.StyleDifference layout = Css.Values.StyleDifference.Structure | Css.Values.StyleDifference.Layout;
        const Css.Values.StyleDifference paint = Css.Values.StyleDifference.Paint | Css.Values.StyleDifference.Composite;
        if ((diff & layout) != 0)
        {
            box.LayoutStyleDirty = true;
            e.MarkDirty(ElementDirtyFlags.Layout | ElementDirtyFlags.Paint);
        }
        else if ((diff & paint) != 0)
        {
            // 只改了变换 / 不透明度 / 滤镜等合成属性：元素自己的录制单元仍然有效（见 RenderPipeline.Effects.cs）
            if (!OnCompositeOnlyChange(e, box, old, next)) e.MarkDirty(ElementDirtyFlags.Paint);
        }
    }

    /// <summary>文本节点没有自己的样式：与父元素共享计算样式，只在影响文本的部分变化时标脏。</summary>
    static void UpdateTextNodeStyle(Element e, RenderBox box, ComputedStyle? parent)
    {
        var old = box.Style;
        if (ReferenceEquals(old, parent)) return;
        box.Style = parent;
        if (old is null || parent is null)
        {
            e.MarkDirty(ElementDirtyFlags.Layout | ElementDirtyFlags.Paint);
            return;
        }
        bool layout = !ReferenceEquals(old.FontGroup, parent.FontGroup) && !old.FontGroup.Equals(parent.FontGroup);
        bool paint = false;
        if (!ReferenceEquals(old.TextGroup, parent.TextGroup))
        {
            var a = old.TextGroup;
            var b = parent.TextGroup;
            if (a.TextAlign != b.TextAlign || a.WhiteSpace != b.WhiteSpace || a.WordBreak != b.WordBreak || a.OverflowWrap != b.OverflowWrap
                || TextLayoutChanged(a, b))
                layout = true;
            else if (!a.Color.Equals(b.Color) || a.Visibility != b.Visibility || TextPaintChanged(a, b))
                paint = true;
        }
        // 文本装饰不继承但传播到后代的文本（父元素的装饰变化要重建段落），行数截断属于父元素的盒
        if (!ReferenceEquals(old.EffectsGroup, parent.EffectsGroup) && DecorationChanged(old, parent)) paint = true;
        if (old.WebkitLineClamp != parent.WebkitLineClamp || old.Display != parent.Display || old.WebkitBoxOrient != parent.WebkitBoxOrient) layout = true;
        if (old.TextOverflow != parent.TextOverflow || old.OverflowX != parent.OverflowX) layout = true;
        if (layout) e.MarkDirty(ElementDirtyFlags.Layout | ElementDirtyFlags.Paint);
        else if (paint) e.MarkDirty(ElementDirtyFlags.Paint);
    }

    /// <summary>视口尺寸变化但媒体查询结果未变：只重算用到视口单位的元素。</summary>
    static void MarkViewportDependent(Element rootEl)
    {
        var stack = new Stack<Element>();
        stack.Push(rootEl);
        while (stack.Count > 0)
        {
            var e = stack.Pop();
            if (e.Render?.Style is { } s && !e.IsTextNode && (s.Flags & (ComputedStyleFlags.DependsOnViewport | ComputedStyleFlags.DependsOnEnvironment)) != 0)
                e.MarkDirty(ElementDirtyFlags.Style);
            var ch = e.Children;
            for (int i = ch.Count - 1; i >= 0; i--) stack.Push(ch[i]);
        }
    }

    // ------------------------------------------------------------ 失效（元素树观察者）

    /// <summary>
    /// 把选择器输入的变化翻译成样式失效范围：只影响自身（元素的 Style 标记已由元素打上）、影响子树，或影响兄弟。
    /// </summary>
    sealed class PipelineObserver : ElementTreeObserver
    {
        readonly RenderPipeline _p;

        public PipelineObserver(RenderPipeline p) => _p = p;

        StyleInvalidation Inv => _p.EnsureInvalidation();

        void Deep(Element e)
        {
            if (e.Render is not { } box) return;
            box.RestyleSubtree = true;
            if (Inv.HasSiblingCombinator && e.Parent is { } parent)
            {
                var pb = RenderBox.Get(parent);
                pb.RestyleChildren = pb.RestyleChildSubtrees = true;
                parent.MarkDirty(ElementDirtyFlags.Style);
            }
        }

        public override void OnClassesChanged(Element element)
        {
            var old = element.Render?.StyledClasses;
            var now = element.Classes;
            var inv = Inv;
            if (inv.HasRelevantClassChange(old, now)) inv.InvalidateHas(element, inclusive: false);
            // ::before / ::after 的候选键可能变化：元素自身已标记样式失效，伪元素随之重算
            if (old is not null)
            {
                foreach (var c in old)
                    if (inv.IsDeepClass(c) && !element.HasClass(c)) { Deep(element); return; }
            }
            foreach (var c in now)
            {
                if (!inv.IsDeepClass(c)) continue;
                bool had = false;
                if (old is not null)
                    for (int i = 0; i < old.Count; i++)
                        if (string.Equals(old[i], c, StringComparison.Ordinal)) { had = true; break; }
                if (!had) { Deep(element); return; }
            }
        }

        public override void OnIdChanged(Element element, string? oldId)
        {
            var inv = Inv;
            if (inv.IsDeepId(oldId) || inv.IsDeepId(element.Id)) Deep(element);
            if (inv.Has.IsRelevantId(oldId) || inv.Has.IsRelevantId(element.Id)) inv.InvalidateHas(element, inclusive: false);
        }

        public override void OnAttributeChanged(Element element, string name, string? oldValue) => AttributeOrProperty(element, name);

        public override void OnPropertyChanged(Element element, string name) => AttributeOrProperty(element, name);

        void AttributeOrProperty(Element element, string name)
        {
            var inv = Inv;
            if (inv.IsDeepAttribute(name)) Deep(element);
            bool state = inv.UsesStatePseudoClasses && StyleInvalidation.IsStateAttribute(name);
            // 状态伪类（:invalid、:read-write ...）：属性或输入框的值变化后元素自身重算；用在非主体位置时连带子树
            if (state)
            {
                element.MarkDirty(ElementDirtyFlags.Style);
                if (inv.HasDeepStatePseudoClasses) Deep(element);
            }
            if ((name == "dir" || name == "lang") && inv.UsesDirOrLang && element.Render is { } box)
            {
                box.RestyleSubtree = true;
                element.MarkDirty(ElementDirtyFlags.Style);
            }
            if (inv.Has.IsRelevantAttribute(name) || (state && inv.Has.UsesStatePseudoClasses)) inv.InvalidateHas(element, inclusive: false);
        }

        public override void OnPseudoStatesChanged(Element element, PseudoStates oldStates)
        {
            var changed = oldStates ^ element.States;
            if ((changed & Inv.DeepStates) != 0) Deep(element);
            if ((changed & (PseudoStates)(int)Inv.Has.States) != 0) Inv.InvalidateHas(element, inclusive: false); // 两个枚举的位定义相同
            // 输入框的光标由绘制阶段按焦点画出：即使没有任何 :focus 规则（计算样式不变）也要重绘
            if (element is InputElement && (changed & PseudoStates.Focus) != 0) element.MarkDirty(ElementDirtyFlags.Paint);
        }

        public override void OnTextChanged(Element element)
        {
            if (Inv.Has.UsesStructural && (element.IsTextNode ? element.Parent : element) is { } host) Inv.InvalidateHas(host, inclusive: true);
            if (!Inv.UsesEmpty) return;
            var target = element.IsTextNode ? element.Parent : element;
            target?.MarkDirty(ElementDirtyFlags.Style);
        }

        void Structure(Element parent)
        {
            var inv = Inv;
            if (!inv.HasSiblingCombinator && !inv.HasStructuralPseudo) return;
            var pb = RenderBox.Get(parent);
            pb.RestyleChildren = true;
            if (inv.HasSiblingCombinator || inv.HasDeepStructuralPseudo) pb.RestyleChildSubtrees = true;
            parent.MarkDirty(ElementDirtyFlags.Style);
        }

        public override void OnChildInserted(Element parent, Element child)
        {
            _p.LayeringChanged();
            Structure(parent);
            if (Inv.Has.UsesHas) Inv.InvalidateHas(child, inclusive: false);
        }

        public override void OnChildRemoved(Element parent, Element child)
        {
            if (Inv.Has.UsesHas) Inv.InvalidateHas(parent, inclusive: true);
            _p.OnSubtreeRemoved(parent, child);
            if (_p._generated.Count > 0) _p._pruneGenerated = true;
            _p._scrollManager?.OnSubtreeRemoved(child);
            ReplacedContent.ReleaseSubtree(child);
            Structure(parent);
        }

        public override void OnChildMoved(Element parent, Element child)
        {
            _p.LayeringChanged();
            Structure(parent);
            if (Inv.Has.HasSiblingRelative || Inv.Has.UsesStructural) Inv.InvalidateHas(parent, inclusive: true);
        }
    }

    StyleInvalidation EnsureInvalidation()
    {
        if (_invalidationDirty)
        {
            _invalidation.Rebuild(_engine);
            _invalidationDirty = false;
        }
        return _invalidation;
    }
}
