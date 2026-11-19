namespace AwAUI.Diagnostics;

/// <summary>
/// 探针类别（design/06「探针」表）。值是位索引，最多 64 个；0–31 留给框架，32–63 给应用自定义（<see cref="Custom"/>）。
/// </summary>
public enum DiagnosticCategory
{
    /// <summary>元素树增删改。</summary>
    Tree = 0,
    /// <summary>CSS 级联、规则匹配。</summary>
    Css = 1,
    /// <summary>组件重建。</summary>
    Components = 2,
    /// <summary>布局。</summary>
    Layout = 3,
    /// <summary>绘制与重绘原因。</summary>
    Paint = 4,
    /// <summary>层树与合成。</summary>
    Compositor = 5,
    /// <summary>帧调度与各阶段耗时。</summary>
    Frame = 6,
    /// <summary>输入路由与手势竞技场。</summary>
    Input = 7,
    /// <summary>渲染后端（批次、图集、降级）。</summary>
    Backend = 8,
    /// <summary>文本整形与字体回退。</summary>
    Text = 9,
    /// <summary>响应式状态。</summary>
    State = 10,
    /// <summary>无障碍语义树。</summary>
    Accessibility = 11,
    /// <summary>资源（图片、字体、内存）。</summary>
    Resources = 12,
    /// <summary>日志（Console 域的数据源）。</summary>
    Log = 13,

    /// <summary>应用自定义类别的起点：<c>Custom + n</c>，n ∈ [0, 31]。</summary>
    Custom = 32,
}
