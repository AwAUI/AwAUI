using System.Numerics;

namespace AwAUI.UI;

/// <summary>滚动命令的来源：滚动管理器会区分这些路径，并据此调用 <see cref="NestedScrollConnection"/> 的钩子。</summary>
public enum ScrollSource : byte
{
    /// <summary>滚轮与触控板（精确增量与系统惯性相位都归入此类）。</summary>
    Wheel,

    /// <summary>触摸拖动与松手后的惯性。</summary>
    Touch,

    /// <summary>键盘（方向键、PageUp / PageDown、Home / End、空格）。</summary>
    Keyboard,

    /// <summary>滚动条：点击轨道翻页、拖动滑块（拖动只移动偏移，不调用 pre / post scroll 钩子）。</summary>
    Scrollbar,

    /// <summary>程序滚动（<see cref="ScrollController.ScrollTo"/>、<see cref="ScrollController.ScrollIntoView"/>、<see cref="ScrollController.JumpTo"/>）。
    /// 这些命令是显式的，不调用嵌套滚动的钩子。</summary>
    Programmatic,
}

/// <summary>
/// 嵌套滚动的联动钩子（design/21「嵌套滚动」）：挂在子滚动控制器上（<see cref="ScrollController.NestedConnection"/>），
/// 在滚动管理器应用一次用户滚动的前后拿到位移 / 速度，用于折叠标题栏、下拉刷新、底部抽屉这类联动。
/// <para>
/// 所有钩子都在 UI 线程上调用，输入的 <see cref="Vector2"/> 以「偏移增量」为单位（逻辑像素、逻辑像素每秒；
/// 正值 = 偏移增大，即内容向上 / 在 LTR 中向左移动）。钩子返回的结果是它消费掉的部分：
/// <list type="bullet">
/// <item><see cref="OnPreScroll"/>：子滚动器消费之前，联结可以先拿走一部分；剩下的才交给子滚动器。</item>
/// <item><see cref="OnPostScroll"/>：子滚动器消费之后调用，返回联结额外消费的部分（只用于联动，不会自动滚动某个父容器）。</item>
/// <item><see cref="OnPreFling"/>：建立惯性之前调用，返回联结消费掉的速度部分。</item>
/// <item><see cref="OnPostFling"/>：惯性建立之后调用，返回联结额外消费的速度部分。</item>
/// </list>
/// 非有限值、反向或超出可用量的返回值会被忽略（按「未消费」处理）。钩子抛出的异常被报告为事件处理器错误，滚动继续。
/// </para>
/// </summary>
public abstract class NestedScrollConnection
{
    /// <summary>子滚动器消费之前：父级可以先拿走一部分（如先折叠标题栏）。<paramref name="available"/> 是本次可用的位移。</summary>
    public virtual Vector2 OnPreScroll(Vector2 available, ScrollSource source) => default;

    /// <summary>子滚动器消费之后：<paramref name="consumed"/> 是子滚动器实际消费的位移，<paramref name="available"/> 是原始可用位移。返回联结额外消费的部分。</summary>
    public virtual Vector2 OnPostScroll(Vector2 consumed, Vector2 available, ScrollSource source) => default;

    /// <summary>建立惯性之前：<paramref name="velocity"/> 是松手速度；返回联结消费掉的速度部分。</summary>
    public virtual Vector2 OnPreFling(Vector2 velocity) => default;

    /// <summary>惯性建立之后：<paramref name="consumed"/> 是交给惯性的速度，<paramref name="available"/> 是原始松手速度；返回联结额外消费的部分。</summary>
    public virtual Vector2 OnPostFling(Vector2 consumed, Vector2 available) => default;
}
