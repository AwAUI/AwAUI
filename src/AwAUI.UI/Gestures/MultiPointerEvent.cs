using System;
using AwAUI.Platform;

namespace AwAUI.UI;

/// <summary>多指事件中的一个接触点（内部）：指针 id、客户区位置与指针类型。</summary>
internal readonly struct PointerContact
{
    internal PointerContact(long pointerId, Point position, PointerType pointerType)
    {
        PointerId = pointerId;
        Position = position;
        PointerType = pointerType;
    }

    internal long PointerId { get; }

    internal Point Position { get; }

    internal PointerType PointerType { get; }
}

/// <summary>
/// 交给多指识别器（<see cref="GestureRecognizer.UsesAllPointers"/>）的事件：
/// 两个接触点 + 引起本次变化的那一个。事件名与指针事件一致（<c>pointerdown</c> / <c>pointermove</c> / <c>pointerup</c> / <c>pointercancel</c>）。
/// </summary>
internal sealed class MultiPointerEvent : Event
{
    internal MultiPointerEvent(string type, PointerContact first, PointerContact second, PointerContact changed, TimeSpan timestamp)
        : base(type, bubbles: false)
    {
        First = first;
        Second = second;
        Changed = changed;
        Timestamp = timestamp;
    }

    /// <summary>先按下的指针。</summary>
    internal PointerContact First { get; }

    /// <summary>后按下的指针。</summary>
    internal PointerContact Second { get; }

    /// <summary>引起本次事件的指针（移动 / 抬起 / 取消的那一个）。</summary>
    internal PointerContact Changed { get; }

    /// <summary>两指中点（客户区坐标，逻辑像素）。</summary>
    internal Point Focus => new(
        (First.Position.X + Second.Position.X) * 0.5f,
        (First.Position.Y + Second.Position.Y) * 0.5f);
}
