using System;
using AwAUI.Platform;

namespace AwAUI.MacOS;

/// <summary>macOS 输入桥中与 AppKit 无关的纯逻辑（NSTextInputClient 命令与 <c>characterIndexForPoint:</c> 的下标换算）。</summary>
internal static class MacInputLogic
{
    /// <summary>
    /// 组合期间的方向键命令（<c>moveLeft:</c> / <c>moveRight:</c> / <c>moveUp:</c> / <c>moveDown:</c>）：
    /// 在预编辑文本内移动插入点 / 折叠选区。范围是文档中的绝对下标，结果总在 <paramref name="composition"/> 之内，由客户端再按组合内偏移处理。
    /// <para>
    /// 平台桥没有文本布局，无法做真正的按行移动；<paramref name="forward"/> 为 false 时向左 / 向上，为 true 时向右 / 向下，
    /// 两者都只移动一个 UTF-16 码元（与 <see cref="ITextInputClient.Selection"/> 的语义一致，客户端负责限制范围）。
    /// 没有组合时方向键由框架的按键处理（<see cref="IWindowEvents.OnKey"/>）移动光标，不经过这里。
    /// </para>
    /// </summary>
    public static TextInputRange MoveCompositionSelection(TextInputRange selection, TextInputRange composition, bool forward)
    {
        int start = Math.Max(selection.Start, composition.Start);
        int end = Math.Min(selection.End, composition.End);
        // 选区与组合不相交（例如客户端在组合期间改了选区）：落在组合的靠外一端
        if (end < start) start = end = Math.Min(Math.Max(selection.Start, composition.Start), composition.End);
        if (start != end)
        {
            // 有选区时折叠到移动方向的一端，与不带 Shift 的方向键一致
            int collapsed = forward ? end : start;
            return new TextInputRange(collapsed, collapsed);
        }
        int caret = forward ? Math.Min(composition.End, end + 1) : Math.Max(composition.Start, start - 1);
        return new TextInputRange(caret, caret);
    }

    /// <summary>
    /// <c>characterIndexForPoint:</c> 的文档下标：用客户端给出的插入点矩形（<see cref="ITextInputClient.GetTextBounds"/>）对下标做二分，
    /// 返回离 <paramref name="point"/> 最近的插入点。
    /// <para>
    /// 文本布局完全由客户端提供，平台桥不做字形测量：折行处、双向文本与尚未布局（矩形全为空）时只按矩形顺序近似。
    /// </para>
    /// </summary>
    public static int CharacterIndex(ITextInputClient client, Point point)
    {
        int length = Math.Max(0, client.Length);
        if (length == 0) return 0;

        // 插入点按下标顺序沿行推进（行从上到下、行内从左到右），对 (行, x) 做二分
        int lo = 0, hi = length;
        while (lo < hi)
        {
            int mid = lo + (hi - lo) / 2;
            if (CompareCaret(client.GetTextBounds(TextInputRange.Caret(mid)), point) < 0) lo = mid + 1;
            else hi = mid;
        }
        if (lo > 0)
        {
            // 前后两个插入点里取更近的一个（点在行尾右侧时取上一行末尾而不是下一行行首）
            float previous = Distance(client.GetTextBounds(TextInputRange.Caret(lo - 1)), point);
            float current = Distance(client.GetTextBounds(TextInputRange.Caret(lo)), point);
            if (previous <= current) return lo - 1;
        }
        return lo;
    }

    /// <summary>插入点矩形与点的比较：点在该行的垂直范围之前为 1，之后为 -1，同一行按 x 比较。</summary>
    static int CompareCaret(Rect caret, Point point)
    {
        if (!caret.IsFinite) return 0;
        // 未布局时高度可能为 0：给一个最小行高，避免所有点都落到「之后」
        float height = Math.Max(caret.Height, 1f);
        if (point.Y < caret.Y) return 1;
        if (point.Y > caret.Y + height) return -1;
        return caret.X.CompareTo(point.X);
    }

    /// <summary>点到插入点矩形的曼哈顿距离（矩形内部为 0）。</summary>
    static float Distance(Rect caret, Point point)
    {
        float dx = point.X < caret.X ? caret.X - point.X : point.X > caret.Right ? point.X - caret.Right : 0f;
        float dy = point.Y < caret.Y ? caret.Y - point.Y : point.Y > caret.Bottom ? point.Y - caret.Bottom : 0f;
        return dx + dy;
    }
}
