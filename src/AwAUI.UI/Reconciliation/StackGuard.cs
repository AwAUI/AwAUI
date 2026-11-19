using System.Runtime.CompilerServices;

namespace AwAUI.UI;

/// <summary>
/// 递归协调的栈深度保护：描述树可能来自数据（极深的嵌套），栈溢出会直接终止进程且无法捕获，
/// 因此在每层递归前检查剩余栈空间，不足时把该子树作为错误报告而不是继续递归。
/// </summary>
internal static class StackGuard
{
    /// <summary>当前线程的剩余栈空间是否足以继续递归一层协调。检查本身是一次栈指针比较，开销可忽略。</summary>
    public static bool HasSufficientStack()
    {
#if NETSTANDARD2_0 || NETFRAMEWORK
        try
        {
            RuntimeHelpers.EnsureSufficientExecutionStack();
            return true;
        }
        catch (System.InsufficientExecutionStackException)
        {
            return false;
        }
#else
        return RuntimeHelpers.TryEnsureSufficientExecutionStack();
#endif
    }

    public static System.InsufficientExecutionStackException CreateException() =>
        new("界面描述树嵌套过深，剩余栈空间不足以继续协调：该子树没有被挂载/更新。请减少嵌套层级（例如对数据驱动的树使用虚拟化或扁平化渲染）。");
}
