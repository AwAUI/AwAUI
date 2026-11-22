namespace AwAUI.UI;

/// <summary>
/// 手势识别器的状态（design/21「手势识别器」）：
/// <see cref="Possible"/> → <see cref="Began"/> → <see cref="Changed"/>* → <see cref="Ended"/> | <see cref="Cancelled"/>；
/// 未胜出或中途放弃时为 <see cref="Failed"/>。
/// </summary>
public enum GestureState : byte
{
    /// <summary>候选：已按下，尚未开始（也未失败）。</summary>
    Possible,

    /// <summary>已胜出并开始识别。</summary>
    Began,

    /// <summary>识别中且本次事件带来了变化（拖动、缩放 ...）。</summary>
    Changed,

    /// <summary>正常结束（抬起）。</summary>
    Ended,

    /// <summary>被取消（<c>pointercancel</c>、系统手势接管 ...）。</summary>
    Cancelled,

    /// <summary>失败：退出了竞技场，或有其它识别器胜出。</summary>
    Failed,
}
