namespace AwAUI.Unicode;

/// <summary>
/// 断行严格程度：决定 UAX #14 中 CJ 类（Conditional Japanese Starter，小假名、长音符 ー、々 等）的解析方式，对应 CSS <c>line-break</c>。
/// </summary>
public enum LineBreakStrictness
{
    /// <summary>严格（UAX #14 默认，CSS <c>line-break: strict</c>）：CJ 解析为 NS，不能出现在行首。</summary>
    Strict = 0,

    /// <summary>
    /// 普通（CSS <c>line-break: normal</c> / <c>loose</c> 的 CJ 部分）：CJ 解析为 ID，前后都可断开。
    /// CSS loose 的其他放宽（如允许在 IN、部分 NS 前断开）不在此实现。
    /// </summary>
    Normal = 1,
}
