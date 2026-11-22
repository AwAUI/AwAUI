using System;
using System.Collections.Generic;
using AwAUI.Css.Values;

namespace AwAUI.UI;

/// <summary>
/// 手势识别器（design/21「手势识别器」）：观察同一次按下的后续指针事件，在竞技场里宣告胜出或退出。
/// <para>
/// 识别器由 <see cref="Element.AddGestureRecognizer"/> 挂到元素上；一次按下时，命中路径（由内到外）上接受该指针的识别器
/// 按「内层优先」入场。识别器在 <see cref="HandleEvent"/> 中通过 <see cref="ArenaEntry"/> 宣告 <c>Accept / Reject / Hold / Release</c>。
/// </para>
/// <para>
/// 识别器实例只跟踪一个指针序列；多指识别器（捏合 / 旋转）改用 <see cref="UsesAllPointers"/> 钩子，不进入单指针竞技场。
/// 状态在每次新的按下时由竞技场重置。
/// </para>
/// </summary>
public abstract class GestureRecognizer
{
    readonly HashSet<GestureRecognizer> _requireFailureOf = new();
    readonly HashSet<GestureRecognizer> _simultaneousWith = new();

    /// <summary>挂载的元素；尚未加入任何元素时为 null。</summary>
    public Element? Target { get; internal set; }

    /// <summary>状态机。由识别器自己在 <see cref="HandleEvent"/> / <see cref="OnAccepted"/> / <see cref="OnRejected"/> 中推进。</summary>
    public GestureState State { get; protected set; } = GestureState.Possible;

    /// <summary>
    /// 本识别器是否参与这次按下（可按指针类型、按键、<c>touch-action</c> 过滤）。默认接受全部指针。
    /// </summary>
    protected internal virtual bool AcceptsPointer(in PointerEvent down) => true;

    /// <summary>处理一个指针事件；通过 <paramref name="entry"/> 宣告胜出、退出或延后裁决。</summary>
    protected internal abstract void HandleEvent(in PointerEvent e, ArenaEntry entry);

    /// <summary>
    /// 是否观察全部指针（捏合 / 旋转这类多指识别器）。为 true 时识别器不进入单指针竞技场，
    /// 而是由 <see cref="GestureArenaManager"/> 在同时有至少两个指针落在它的命中路径上时，
    /// 把 <see cref="MultiPointerEvent"/> 交给 <see cref="HandleMultiPointer"/>。
    /// 单指针识别器的竞技场规则与指针捕获行为因此不受第二根手指影响。
    /// </summary>
    internal virtual bool UsesAllPointers => false;

    /// <summary>多指事件（识别器 <see cref="UsesAllPointers"/> 为 true 时由竞技场管理器调用）。</summary>
    internal virtual void HandleMultiPointer(in MultiPointerEvent e) { }

    /// <summary>竞技场裁决本识别器胜出。默认把状态从 <see cref="GestureState.Possible"/> 推进到 <see cref="GestureState.Began"/>。</summary>
    protected internal virtual void OnAccepted(long pointerId)
    {
        if (State == GestureState.Possible) State = GestureState.Began;
    }

    /// <summary>竞技场裁决本识别器失败（其它识别器胜出或被指针取消）。默认进入 <see cref="GestureState.Failed"/>。</summary>
    protected internal virtual void OnRejected(long pointerId)
    {
        if (State is GestureState.Possible or GestureState.Began or GestureState.Changed) State = GestureState.Failed;
    }

    /// <summary>等 <paramref name="other"/> 失败后才能胜出（例如单击等双击失败）。</summary>
    public void RequireFailureOf(GestureRecognizer other)
    {
        if (other is null) throw new ArgumentNullException(nameof(other));
        if (ReferenceEquals(other, this)) throw new ArgumentException("识别器不能要求自己失败。", nameof(other));
        _requireFailureOf.Add(other);
    }

    /// <summary>允许与 <paramref name="other"/> 同时生效（例如捏合 + 旋转）；两者组成一个队，整队共同胜负。</summary>
    public void AllowSimultaneousWith(GestureRecognizer other)
    {
        if (other is null) throw new ArgumentNullException(nameof(other));
        if (ReferenceEquals(other, this)) throw new ArgumentException("识别器不能与自身同时生效。", nameof(other));
        _simultaneousWith.Add(other);
    }

    internal bool RequiresFailureOf(GestureRecognizer other) => _requireFailureOf.Contains(other);

    internal bool AllowsSimultaneousWith(GestureRecognizer other) => _simultaneousWith.Contains(other);

    /// <summary>本次按下时命中元素的计算 <c>touch-action</c>（竞技场在入场前写入）。</summary>
    internal CssTouchAction TouchAction { get; set; } = CssTouchAction.Auto;

    /// <summary>
    /// 本次指针序列使用的手势参数：竞技场在按下时从命中元素的组件子树读取最近的 <see cref="GestureSettings"/>
    /// （<see cref="Context.Provide{T}"/>）并写入；没有提供时为 <see cref="GestureSettings.Default"/>。
    /// 只在本次序列内有效，下一次按下重新求值。
    /// </summary>
    internal GestureSettings Settings { get; set; } = GestureSettings.Default;

    /// <summary>
    /// 命中元素的 <c>touch-action</c> 是否允许本识别器入场。默认允许（点按、长按不是 CSS 默认手势）；
    /// <see cref="PanGestureRecognizer"/> 会拒绝 <c>none</c> 并据此锁定轴向。
    /// </summary>
    internal virtual bool AllowsTouchAction(CssTouchAction action) => true;

    /// <summary>新的指针序列开始：把状态复位为 <see cref="GestureState.Possible"/>。</summary>
    internal virtual void Reset() => State = GestureState.Possible;
}
