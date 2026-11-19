using System;
using System.Threading.Tasks;
using AwAUI.UI;

namespace AwAUI.Controls.Primitives;

/// <summary>
/// <see cref="Virtualizer"/> 与 <see cref="ScrollController"/>（design/21）之间的胶水：只用滚动控制器的公开 API。
/// 宿主组件在 <see cref="ScrollController.Scrolled"/> 中调用 <see cref="Sync"/> 并在返回 true 时重建。
/// </summary>
public static class VirtualizerScrolling
{
    /// <summary>
    /// 把滚动控制器的偏移与视口尺寸（按 <see cref="VirtualizerOptions.Horizontal"/> 取轴）写入虚拟化器；渲染范围因此变化时返回 true。
    /// </summary>
    public static bool Sync(Virtualizer virtualizer, ScrollController controller)
    {
        if (virtualizer is null) throw new ArgumentNullException(nameof(virtualizer));
        if (controller is null) throw new ArgumentNullException(nameof(controller));
        var before = virtualizer.GetRange();
        bool horizontal = virtualizer.Options.Horizontal;
        virtualizer.ScrollOffset = horizontal ? controller.Offset.X : controller.Offset.Y;
        virtualizer.ViewportSize = horizontal ? controller.ViewportSize.Width : controller.ViewportSize.Height;
        var after = virtualizer.GetRange();
        return before.OverscanStartIndex != after.OverscanStartIndex || before.OverscanEndIndex != after.OverscanEndIndex
            || before.StartIndex != after.StartIndex;
    }

    /// <summary>
    /// 施加 <see cref="Virtualizer.Measure"/> 返回的锚定调整量（立即跳转，不打断语义上的位置）。
    /// <see cref="ScrollController.JumpTo"/> 按控制器当前的内容尺寸钳制：须在滚动内容（尺寸为新的 <see cref="Virtualizer.TotalSize"/>）重新布局、控制器的内容尺寸更新之后再调用，
    /// 否则靠近末尾时（上方的项变高）调整量会被旧的最大偏移截掉。
    /// </summary>
    public static void ApplyAdjustment(Virtualizer virtualizer, ScrollController controller, double delta)
    {
        if (virtualizer is null) throw new ArgumentNullException(nameof(virtualizer));
        if (controller is null) throw new ArgumentNullException(nameof(controller));
        if (delta == 0 || double.IsNaN(delta)) return;
        var o = controller.Offset;
        controller.JumpTo(virtualizer.Options.Horizontal ? new Point(o.X + (float)delta, o.Y) : new Point(o.X, o.Y + (float)delta));
    }

    /// <summary>滚动到第 <paramref name="index"/> 项（默认平滑，见 <see cref="ScrollController.ScrollTo"/>）。</summary>
    public static Task ScrollToIndex(Virtualizer virtualizer, ScrollController controller, int index,
        ScrollAlignment alignment = ScrollAlignment.Nearest, ScrollAnimation? animation = null)
    {
        if (virtualizer is null) throw new ArgumentNullException(nameof(virtualizer));
        if (controller is null) throw new ArgumentNullException(nameof(controller));
        float target = (float)virtualizer.GetOffsetForIndex(index, alignment);
        var o = controller.Offset;
        return controller.ScrollTo(virtualizer.Options.Horizontal ? new Point(target, o.Y) : new Point(o.X, target), animation);
    }
}
