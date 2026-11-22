using System;
using System.Collections.Generic;
using AwAUI.Css.Values;
using AwAUI.Platform;

namespace AwAUI.UI;

/// <summary>
/// 识别器在竞技场中的席位：<see cref="GestureRecognizer.HandleEvent"/> 通过它宣告裁决结果。
/// 默认（未初始化）实例上的调用是空操作。
/// </summary>
public readonly struct ArenaEntry
{
    readonly GestureArena? _arena;

    internal ArenaEntry(GestureArena arena, GestureRecognizer recognizer)
    {
        _arena = arena;
        Recognizer = recognizer;
    }

    internal GestureRecognizer? Recognizer { get; }

    /// <summary>宣告胜出：其余未退出的识别器（同时生效的队友除外）收到 <see cref="GestureRecognizer.OnRejected"/>。</summary>
    public void Accept() => _arena?.Accept(Recognizer!);

    /// <summary>主动退出竞技场。</summary>
    public void Reject() => _arena?.Reject(Recognizer!);

    /// <summary>请求延后裁决（等待超时，例如双击的第二次按下）；有时钟时在 <see cref="GestureArena.DefaultHoldTimeout"/> 后自动胜出。</summary>
    public void Hold() => _arena?.Hold(Recognizer!);

    /// <summary>取消 <see cref="Hold"/>；若指针已抬起且没有其它被延后的识别器，立即按入场顺序裁决。</summary>
    public void Release() => _arena?.Release(Recognizer!);
}

/// <summary>
/// 一次按下对应一个竞技场：管理命中路径上入场的识别器，实现 design/21「竞技场规则」1-6。
/// <list type="number">
/// <item>按下时由 <see cref="GestureArenaManager"/> 按命中路径（内到外）加入接受该指针的识别器。</item>
/// <item>任一识别器 <see cref="Accept"/> 时它胜出，其余收到 <see cref="GestureRecognizer.OnRejected"/>（同时生效的队除外）。</item>
/// <item>只剩一个未退出的识别器时自动胜出（有 <see cref="Hold"/> 时延后）。</item>
/// <item>抬起时仍无人胜出且无人延后：按入场顺序第一个未退出者胜出。</item>
/// <item><see cref="GestureRecognizer.AllowSimultaneousWith"/> 的识别器组成一个队，整队共同胜负。</item>
/// <item>取消：全部取消。</item>
/// </list>
/// </summary>
internal sealed class GestureArena
{
    /// <summary><see cref="ArenaEntry.Hold"/> 后多久自动裁决（毫秒）；取双击间隔的常见值。</summary>
    internal static readonly TimeSpan DefaultHoldTimeout = TimeSpan.FromMilliseconds(300);

    readonly GestureArenaManager _owner;
    readonly List<Member> _members = new();
    readonly Dictionary<GestureRecognizer, Member> _byRecognizer = new();
    bool _resolved;
    bool _pointerUp;

    internal GestureArena(GestureArenaManager owner, long pointerId)
    {
        _owner = owner;
        PointerId = pointerId;
    }

    internal long PointerId { get; }

    internal bool IsResolved => _resolved;

    internal bool IsEmpty => _members.Count == 0;

    internal bool Contains(GestureRecognizer recognizer) => _byRecognizer.ContainsKey(recognizer);

    /// <summary>把识别器加入竞技场（保持入场顺序）；重复加入被忽略。</summary>
    internal void Add(GestureRecognizer recognizer)
    {
        if (_byRecognizer.ContainsKey(recognizer)) return;
        var member = new Member(recognizer);
        _members.Add(member);
        _byRecognizer.Add(recognizer, member);
    }

    /// <summary>入场完毕：只剩一个接受者时自动胜出（design/21 规则 3）。</summary>
    internal void Close() => TryResolveSoleSurvivor();

    /// <summary>把事件交给全部未退出的识别器（按入场顺序）。</summary>
    internal void Dispatch(in PointerEvent e)
    {
        for (int i = 0; i < _members.Count; i++)
        {
            var member = _members[i];
            if (!member.Alive) continue;
            member.Recognizer.HandleEvent(e, new ArenaEntry(this, member.Recognizer));
        }
    }

    /// <summary>抬起：先按规则 4 裁决（无人延后时），再把事件交给胜者 / 被延后的识别器。</summary>
    internal void PointerUp(in PointerEvent e)
    {
        ResolveFallback();
        Dispatch(e);
        _pointerUp = true;
        if (_resolved || !AnyHeld()) _owner.Remove(this);
    }

    /// <summary>取消：事件交给全部未退出者（识别器据此复位并派发取消回调），随后全部退出。</summary>
    internal void PointerCancel(in PointerEvent e)
    {
        Dispatch(e);
        _resolved = true;
        for (int i = 0; i < _members.Count; i++)
        {
            var member = _members[i];
            if (!member.Alive) continue;
            member.Alive = false;
            CancelHold(member);
        }
        _owner.Remove(this);
    }

    internal void Accept(GestureRecognizer recognizer)
    {
        if (_resolved || !_byRecognizer.TryGetValue(recognizer, out var member) || !member.Alive) return;
        // RequireFailureOf：对方仍存活时不能胜出，等它失败后再裁决
        if (RequiresAliveFailure(member))
        {
            member.PendingAccept = true;
            return;
        }
        ResolveInFavorOf(member);
    }

    internal void Reject(GestureRecognizer recognizer)
    {
        if (_resolved || !_byRecognizer.TryGetValue(recognizer, out var member) || !member.Alive) return;
        RejectMember(member);
        // 退出的识别器可能正是别人等待失败的：先看有没有可裁决的等待者
        for (int i = 0; i < _members.Count; i++)
        {
            var pending = _members[i];
            if (pending.Alive && pending.PendingAccept && !RequiresAliveFailure(pending))
            {
                ResolveInFavorOf(pending);
                return;
            }
        }
        TryResolveSoleSurvivor();
    }

    internal void Hold(GestureRecognizer recognizer)
    {
        if (_resolved || !_byRecognizer.TryGetValue(recognizer, out var member) || !member.Alive || member.Held) return;
        member.Held = true;
        if (_owner.Clock is not { } clock) return;
        member.HoldTimer = clock.Schedule(DefaultHoldTimeout, () =>
        {
            member.HoldTimer = null;
            if (_resolved || !member.Alive) return;
            member.Held = false;
            if (RequiresAliveFailure(member))
            {
                // 超时仍未等到对方失败：本候选退出，其余继续裁决
                Reject(member.Recognizer);
                if (_pointerUp && !_resolved) ResolveFallback();
            }
            else
            {
                ResolveInFavorOf(member);
            }
            if (_pointerUp) _owner.Remove(this);
        });
    }

    internal void Release(GestureRecognizer recognizer)
    {
        if (!_byRecognizer.TryGetValue(recognizer, out var member)) return;
        member.Held = false;
        CancelHold(member);
        if (_resolved || !_pointerUp || AnyHeld()) return;
        ResolveFallback();
        if (_resolved) _owner.Remove(this);
    }

    // ------------------------------------------------------------ 裁决

    void ResolveInFavorOf(Member winner)
    {
        if (_resolved) return;
        _resolved = true;
        var team = CollectTeam(winner);
        for (int i = 0; i < _members.Count; i++)
        {
            var member = _members[i];
            if (!member.Alive) continue;
            CancelHold(member);
            if (team.Contains(member))
            {
                member.Recognizer.OnAccepted(PointerId);
            }
            else
            {
                member.Alive = false;
                member.Recognizer.OnRejected(PointerId);
            }
        }
    }

    /// <summary>规则 4：按入场顺序第一个未退出者胜出；有延后（Hold）时等待。</summary>
    void ResolveFallback()
    {
        if (_resolved) return;
        Member? first = null;
        for (int i = 0; i < _members.Count; i++)
        {
            var member = _members[i];
            if (!member.Alive) continue;
            first = member;
            break;
        }
        if (first is null)
        {
            _resolved = true;
            return;
        }
        if (AnyHeld()) return;
        ResolveInFavorOf(first);
    }

    /// <summary>规则 3：只剩一个未退出者且无人延后时自动胜出。</summary>
    void TryResolveSoleSurvivor()
    {
        if (_resolved) return;
        Member? sole = null;
        int alive = 0;
        bool held = false;
        for (int i = 0; i < _members.Count; i++)
        {
            var member = _members[i];
            if (!member.Alive) continue;
            alive++;
            sole = member;
            held |= member.Held;
        }
        if (alive == 0)
        {
            _resolved = true;
            return;
        }
        if (alive == 1 && !held) ResolveInFavorOf(sole!);
    }

    void RejectMember(Member member)
    {
        member.Alive = false;
        CancelHold(member);
        member.Recognizer.OnRejected(PointerId);
    }

    bool RequiresAliveFailure(Member member)
    {
        for (int i = 0; i < _members.Count; i++)
        {
            var other = _members[i];
            if (!other.Alive || ReferenceEquals(other, member)) continue;
            if (member.Recognizer.RequiresFailureOf(other.Recognizer)) return true;
        }
        return false;
    }

    bool AnyHeld()
    {
        for (int i = 0; i < _members.Count; i++)
            if (_members[i].Alive && _members[i].Held) return true;
        return false;
    }

    void CancelHold(Member member)
    {
        member.Held = false;
        member.HoldTimer?.Dispose();
        member.HoldTimer = null;
    }

    /// <summary>与胜者同时生效的队（沿 <see cref="GestureRecognizer.AllowSimultaneousWith"/> 双向连通）。</summary>
    HashSet<Member> CollectTeam(Member winner)
    {
        var team = new HashSet<Member> { winner };
        var queue = new Queue<Member>();
        queue.Enqueue(winner);
        while (queue.Count > 0)
        {
            var member = queue.Dequeue();
            for (int i = 0; i < _members.Count; i++)
            {
                var other = _members[i];
                if (!other.Alive || team.Contains(other)) continue;
                if (!Simultaneous(member, other)) continue;
                team.Add(other);
                queue.Enqueue(other);
            }
        }
        return team;
    }

    static bool Simultaneous(Member a, Member b) =>
        a.Recognizer.AllowsSimultaneousWith(b.Recognizer) || b.Recognizer.AllowsSimultaneousWith(a.Recognizer);

    sealed class Member
    {
        internal Member(GestureRecognizer recognizer) => Recognizer = recognizer;

        internal GestureRecognizer Recognizer { get; }

        /// <summary>仍在竞技场中（未退出、未失败、未取消）。</summary>
        internal bool Alive { get; set; } = true;

        /// <summary>请求延后裁决（<see cref="ArenaEntry.Hold"/>）。</summary>
        internal bool Held { get; set; }

        /// <summary>曾宣告胜出但因 <see cref="GestureRecognizer.RequireFailureOf"/> 被推迟。</summary>
        internal bool PendingAccept { get; set; }

        internal IDisposable? HoldTimer { get; set; }
    }
}

/// <summary>每个 <see cref="InputManager"/> 一个：按指针 id 管理竞技场，把指针事件路由到它们。</summary>
internal sealed class GestureArenaManager
{
    readonly InputManager _input;
    readonly Dictionary<long, GestureArena> _arenas = new();
    readonly Dictionary<long, PointerContact> _pointers = new();
    readonly Dictionary<GestureRecognizer, MultiTarget> _multi = new();

    internal GestureArenaManager(InputManager input) => _input = input;

    internal UIClock? Clock => _input.Clock;

    internal void Remove(GestureArena arena)
    {
        // 同一指针 id 可能已有新的竞技场（上一轮的 Hold 尚未裁决）：只移除自己
        if (_arenas.TryGetValue(arena.PointerId, out var current) && ReferenceEquals(current, arena))
            _arenas.Remove(arena.PointerId);
    }

    /// <summary>按下：沿命中路径（内到外）收集接受该指针且被 <c>touch-action</c> 允许的识别器。</summary>
    internal void PointerDown(long pointerId, Element? hit, in PointerEvent down)
    {
        _arenas.Remove(pointerId);
        if (hit is null || !ReferenceEquals(hit.Root, _input.Root)) return;
        _pointers[pointerId] = new PointerContact(pointerId, down.Position, down.PointerType);
        // 按子树覆盖手势参数：从命中元素的组件向上找最近的 GestureSettings（只影响本次序列）
        var settings = FindSettings(hit);
        GestureArena? arena = null;
        List<MultiTarget>? multi = null;
        for (var element = hit; element is not null; element = element.Parent)
        {
            var recognizers = element.GestureRecognizers;
            if (recognizers.Count == 0) continue;
            var touchAction = element.ComputedStyle?.TouchAction ?? CssTouchAction.Auto;
            for (int i = 0; i < recognizers.Count; i++)
            {
                var recognizer = recognizers[i];
                if (!recognizer.AcceptsPointer(down) || !recognizer.AllowsTouchAction(touchAction)) continue;
                if (recognizer.UsesAllPointers)
                {
                    var target = GetOrCreateMulti(recognizer);
                    if (!target.PointerIds.Contains(pointerId)) target.PointerIds.Add(pointerId);
                    (multi ??= new List<MultiTarget>()).Add(target);
                    continue;
                }
                // 一个识别器同时只跟踪一个指针序列：第二根手指落在同一元素上时不重置它，
                // 第一根手指上的竞技场（Tap / Pan ...）因此不受影响。
                if (IsInLiveArena(recognizer)) continue;
                recognizer.Settings = settings;
                arena ??= new GestureArena(this, pointerId);
                if (!arena.Contains(recognizer))
                {
                    recognizer.Reset();
                    recognizer.TouchAction = touchAction;
                    arena.Add(recognizer);
                }
            }
        }
        if (arena is not null && !arena.IsEmpty)
        {
            _arenas[pointerId] = arena;
            arena.Dispatch(down);
            arena.Close();
        }
        if (multi is null) return;
        for (int i = 0; i < multi.Count; i++)
        {
            var target = multi[i];
            if (target.PointerIds.Count < 2) continue;
            // 刚凑齐两指：开始一段新的多指手势（第三根手指不重置）
            if (target.PointerIds.Count == 2)
            {
                target.Recognizer.Reset();
                target.Recognizer.Settings = settings;
            }
            ForwardMulti(target, "pointerdown", pointerId, down.Position, down.PointerType, down.Timestamp);
        }
    }

    internal void PointerMove(in PointerEvent move)
    {
        if (_arenas.TryGetValue(move.PointerId, out var arena)) arena.Dispatch(move);
        if (!_pointers.ContainsKey(move.PointerId)) return;
        _pointers[move.PointerId] = new PointerContact(move.PointerId, move.Position, move.PointerType);
        ForwardMultiAll("pointermove", move.PointerId, move.Position, move.PointerType, move.Timestamp);
    }

    internal void PointerUp(in PointerEvent up)
    {
        if (_arenas.TryGetValue(up.PointerId, out var arena)) arena.PointerUp(up);
        ForwardMultiAll("pointerup", up.PointerId, up.Position, up.PointerType, up.Timestamp);
        RemovePointer(up.PointerId);
    }

    internal void PointerCancel(in PointerEvent cancel)
    {
        if (_arenas.TryGetValue(cancel.PointerId, out var arena)) arena.PointerCancel(cancel);
        ForwardMultiAll("pointercancel", cancel.PointerId, cancel.Position, cancel.PointerType, cancel.Timestamp);
        RemovePointer(cancel.PointerId);
    }

    MultiTarget GetOrCreateMulti(GestureRecognizer recognizer)
    {
        if (_multi.TryGetValue(recognizer, out var target)) return target;
        target = new MultiTarget(recognizer);
        _multi.Add(recognizer, target);
        return target;
    }

    bool IsInLiveArena(GestureRecognizer recognizer)
    {
        foreach (var arena in _arenas.Values)
            if (arena.Contains(recognizer)) return true;
        return false;
    }

    /// <summary>
    /// 命中元素所属组件子树里最近的 <see cref="GestureSettings"/>（<see cref="Context.Provide{T}"/> 提供）。
    /// 普通 C# 元素树没有组件上下文，回退到 <see cref="GestureSettings.Default"/>。
    /// </summary>
    static GestureSettings FindSettings(Element hit)
    {
        for (var c = hit.Owner; c is not null; c = c.ParentComponent)
            if (c.TryGetContext<GestureSettings>(out var settings)) return settings;
        return GestureSettings.Default;
    }

    void ForwardMultiAll(string type, long pointerId, Point position, PointerType pointerType, TimeSpan timestamp)
    {
        if (_multi.Count == 0) return;
        foreach (var target in _multi.Values)
            ForwardMulti(target, type, pointerId, position, pointerType, timestamp);
    }

    void ForwardMulti(MultiTarget target, string type, long pointerId, Point position, PointerType pointerType, TimeSpan timestamp)
    {
        if (target.PointerIds.Count < 2 || !target.PointerIds.Contains(pointerId)) return;
        long firstId = target.PointerIds[0], secondId = target.PointerIds[1];
        if (!_pointers.TryGetValue(firstId, out var first) || !_pointers.TryGetValue(secondId, out var second)) return;
        var changed = new PointerContact(pointerId, position, pointerType);
        // 按按下顺序给出两个点：先按下的总是 First，与事件到达顺序无关
        target.Recognizer.HandleMultiPointer(new MultiPointerEvent(type, first, second, changed, timestamp));
    }

    void RemovePointer(long pointerId)
    {
        _pointers.Remove(pointerId);
        if (_multi.Count == 0) return;
        List<GestureRecognizer>? empty = null;
        foreach (var kv in _multi)
        {
            if (!kv.Value.PointerIds.Remove(pointerId)) continue;
            if (kv.Value.PointerIds.Count == 0) (empty ??= new List<GestureRecognizer>()).Add(kv.Key);
        }
        if (empty is null) return;
        for (int i = 0; i < empty.Count; i++) _multi.Remove(empty[i]);
    }

    /// <summary>一个多指识别器当前跟踪的指针（按按下顺序）。</summary>
    sealed class MultiTarget
    {
        internal MultiTarget(GestureRecognizer recognizer) => Recognizer = recognizer;

        internal GestureRecognizer Recognizer { get; }

        internal List<long> PointerIds { get; } = new();
    }
}
