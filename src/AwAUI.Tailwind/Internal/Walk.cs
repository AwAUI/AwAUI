using System;
using System.Collections.Generic;

namespace AwAUI.Tailwind.Internal;

/// <summary>树遍历的动作（与 Tailwind 的 <c>WalkAction</c> 一致）。</summary>
internal enum WalkKind : byte
{
    Continue,
    Skip,
    Stop,
    Replace,
    ReplaceSkip,
    ReplaceStop,
}

/// <summary>遍历回调的返回值。</summary>
internal readonly struct WalkAction<T>
{
    public readonly WalkKind Kind;
    public readonly List<T>? Nodes;

    WalkAction(WalkKind kind, List<T>? nodes)
    {
        Kind = kind;
        Nodes = nodes;
    }

    public static WalkAction<T> Continue => default;
    public static WalkAction<T> Skip => new(WalkKind.Skip, null);
    public static WalkAction<T> Stop => new(WalkKind.Stop, null);
    public static WalkAction<T> Replace(List<T> nodes) => new(WalkKind.Replace, nodes);
    public static WalkAction<T> Replace(T node) => new(WalkKind.Replace, new List<T> { node });
    public static WalkAction<T> ReplaceSkip(List<T> nodes) => new(WalkKind.ReplaceSkip, nodes);
    public static WalkAction<T> ReplaceSkip(T node) => new(WalkKind.ReplaceSkip, new List<T> { node });
    public static WalkAction<T> ReplaceStop(List<T> nodes) => new(WalkKind.ReplaceStop, nodes);
    public static WalkAction<T> ReplaceStop(T node) => new(WalkKind.ReplaceStop, new List<T> { node });
}

/// <summary>遍历上下文：父节点、兄弟列表、下标、深度与路径。</summary>
internal sealed class WalkContext<T> where T : class
{
    internal Frame? Top;

    public T? Parent { get; internal set; }
    public List<T> Siblings { get; internal set; } = null!;
    public int Index { get; internal set; }
    public int Depth { get; internal set; }

    /// <summary>从根到父节点的路径（不含当前节点）。</summary>
    public List<T> Path()
    {
        var list = new List<T>();
        for (Frame? f = Top; f != null; f = f.Prev)
        {
            if (f.Parent != null) list.Add(f.Parent);
        }
        list.Reverse();
        return list;
    }

    internal sealed class Frame
    {
        public List<T> List = null!;
        public int Index;
        public T? Parent;
        public Frame? Prev;
    }
}

/// <summary>
/// 非递归的树遍历（移植自 Tailwind 的 <c>walk</c>）。显式栈避免深树导致的栈溢出；
/// 回调可返回替换 / 跳过 / 停止。
/// </summary>
internal static class Walker
{
    public static void Walk<T>(List<T> root, Func<T, List<T>?> children, Func<T, WalkContext<T>, WalkAction<T>> enter, Func<T, WalkContext<T>, WalkAction<T>>? exit = null)
        where T : class
    {
        var ctx = new WalkContext<T>();
        var top = new WalkContext<T>.Frame { List = root, Index = 0, Parent = null, Prev = null };
        ctx.Top = top;
        while (top != null)
        {
            ctx.Top = top;
            var list = top.List;
            int p = top.Index;
            if (p >= list.Count)
            {
                top = top.Prev;
                ctx.Depth -= 1;
                continue;
            }
            ctx.Parent = top.Parent;
            ctx.Siblings = list;
            if (p >= 0)
            {
                ctx.Index = p;
                T node = list[p];
                var action = enter(node, ctx);
                switch (action.Kind)
                {
                    case WalkKind.Continue:
                    {
                        var kids = children(node);
                        top.Index = ~p;
                        if (kids != null && kids.Count > 0)
                        {
                            ctx.Depth += 1;
                            top = new WalkContext<T>.Frame { List = kids, Index = 0, Parent = node, Prev = top };
                        }
                        continue;
                    }
                    case WalkKind.Stop:
                        return;
                    case WalkKind.Skip:
                        top.Index = ~p;
                        continue;
                    case WalkKind.Replace:
                        list.RemoveAt(p);
                        list.InsertRange(p, action.Nodes!);
                        continue;
                    case WalkKind.ReplaceStop:
                        list.RemoveAt(p);
                        list.InsertRange(p, action.Nodes!);
                        return;
                    case WalkKind.ReplaceSkip:
                        list.RemoveAt(p);
                        list.InsertRange(p, action.Nodes!);
                        top.Index += action.Nodes!.Count;
                        continue;
                }
            }
            int c = ~p;
            ctx.Index = c;
            if (exit == null)
            {
                top.Index = c + 1;
                continue;
            }
            T exiting = list[c];
            var ex = exit(exiting, ctx);
            switch (ex.Kind)
            {
                case WalkKind.Continue:
                case WalkKind.Skip:
                    top.Index = c + 1;
                    continue;
                case WalkKind.Stop:
                    return;
                case WalkKind.Replace:
                case WalkKind.ReplaceSkip:
                    list.RemoveAt(c);
                    list.InsertRange(c, ex.Nodes!);
                    top.Index = c + ex.Nodes!.Count;
                    continue;
                case WalkKind.ReplaceStop:
                    list.RemoveAt(c);
                    list.InsertRange(c, ex.Nodes!);
                    return;
            }
        }
    }
}
