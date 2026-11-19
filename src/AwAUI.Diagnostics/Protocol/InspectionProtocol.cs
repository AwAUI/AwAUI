using System;
using System.Collections.Generic;
using AwAUI.Diagnostics.Inspection;
using AwAUI.Diagnostics.Json;

namespace AwAUI.Diagnostics.Protocol;

/// <summary>Tree / Css / Layout / Overlay 域共用的参数解析、错误与序列化。</summary>
internal static class InspectionProtocol
{
    /// <summary>JavaScript 能精确表示的最大整数：节点 id 不得超过它。</summary>
    public const long MaxSafeId = (1L << 53) - 1;

    /// <summary>
    /// 登记一个在目标 UI 线程上运行的方法，并把接缝抛出的约定异常映射为协议错误：
    /// <see cref="FormatException"/> → -32005，<see cref="NotSupportedException"/> → -32004。
    /// </summary>
    public static void Add(DevToolsMethodTable methods, string name, DevToolsMethodHandler handler, bool requiresEnable = false) =>
        methods.Add(name, ctx => Guard(handler, ctx), requiresEnable, runOnTargetThread: true);

    static JsonValue? Guard(DevToolsMethodHandler handler, DevToolsCallContext ctx)
    {
        try
        {
            return handler(ctx);
        }
        catch (FormatException ex)
        {
            throw new DevToolsProtocolException(DevToolsErrorCodes.ValueParseError, ex.Message);
        }
        catch (NotSupportedException ex)
        {
            throw new DevToolsProtocolException(DevToolsErrorCodes.NotSupported, ex.Message);
        }
    }

    /// <summary>
    /// 需要 enable 的方法在 UI 线程上真正执行时再确认一次：请求在投递期间，会话可能已停用该域或断开
    /// （停用时的撤销已经执行过，此时再修改叠加层或伪状态就没有人撤销了）。
    /// </summary>
    public static void RequireStillEnabled(DevToolsCallContext ctx, DevToolsDomain domain)
    {
        if (!ctx.Session.IsDomainEnabled(domain)) throw DomainNotEnabled(domain);
    }

    public static DevToolsProtocolException DomainNotEnabled(DevToolsDomain domain) =>
        new(DevToolsErrorCodes.DomainNotEnabled, $"Domain '{domain.Name}' is not enabled.", new JsonObject { ["domain"] = domain.Name });

    public static DevToolsTarget RequireTarget(DevToolsCallContext ctx) =>
        ctx.Target ?? throw new DevToolsProtocolException(DevToolsErrorCodes.TargetNotFound, "No target is registered.");

    public static InspectableTree RequireTree(DevToolsCallContext ctx)
    {
        var t = RequireTarget(ctx);
        return t.Tree ?? throw NotSupported(t, "an inspectable tree");
    }

    public static StyleInspector RequireStyles(DevToolsCallContext ctx)
    {
        var t = RequireTarget(ctx);
        return t.Styles ?? throw NotSupported(t, "a style inspector");
    }

    public static OverlayHost RequireOverlay(DevToolsCallContext ctx)
    {
        var t = RequireTarget(ctx);
        return t.Overlay ?? throw NotSupported(t, "an overlay");
    }

    static DevToolsProtocolException NotSupported(DevToolsTarget t, string what) =>
        new(DevToolsErrorCodes.NotSupported, $"Target '{t.Id}' has no {what}.", new JsonObject { ["target"] = t.Id });

    public static DevToolsProtocolException NodeNotFound(long nodeId) =>
        new(DevToolsErrorCodes.NodeNotFound, "Node not found", new JsonObject { ["nodeId"] = nodeId });

    // ---- 序列化 ----

    /// <summary>浮点坐标：非有限值无法写成 JSON，写为 0。</summary>
    public static JsonValue Number(float v) => float.IsNaN(v) || float.IsInfinity(v) ? JsonValue.Create(0L) : JsonValue.Create((double)v);

    public static JsonArray QuadJson(Quad q) => new()
    {
        Number(q.P1.X), Number(q.P1.Y), Number(q.P2.X), Number(q.P2.Y),
        Number(q.P3.X), Number(q.P3.Y), Number(q.P4.X), Number(q.P4.Y),
    };

    public static JsonObject SourceJson(SourceLocation s) => new()
    {
        ["file"] = s.File ?? string.Empty,
        ["line"] = s.Line,
        ["column"] = s.Column,
    };

    public static JsonArray StringArray(IReadOnlyList<string>? items)
    {
        var a = new JsonArray();
        if (items != null)
            foreach (var s in items) a.Add(s ?? string.Empty);
        return a;
    }

    /// <summary>
    /// design/20 <c>Node</c>：<c>{ id, parentId?, kind, tag?, elementId?, classes?, attributes?, text?, textTruncated?,
    /// componentId?, componentName?, pseudoStates?, childCount, source? }</c>（不含 children，由调用方附加）。
    /// </summary>
    public static JsonObject NodeJson(InspectableNodeInfo n, int maxTextLength)
    {
        ValidateId(n.Id);
        var o = new JsonObject { ["id"] = n.Id };
        if (n.ParentId != 0) o["parentId"] = n.ParentId;
        o["kind"] = n.Kind == InspectableNodeKind.Text ? "text" : "element";
        if (n.Tag != null) o["tag"] = n.Tag;
        if (n.ElementId != null) o["elementId"] = n.ElementId;
        if (n.Classes is { Count: > 0 }) o["classes"] = StringArray(n.Classes);
        if (n.Attributes is { Count: > 0 } attrs)
        {
            var a = new JsonObject();
            foreach (var kv in attrs)
                if (kv.Key != null) a[kv.Key] = kv.Value ?? string.Empty;
            o["attributes"] = a;
        }
        if (n.Text != null)
        {
            o["text"] = Truncate(n.Text, maxTextLength, out bool truncated);
            if (truncated) o["textTruncated"] = true;
        }
        if (n.ComponentId != 0) o["componentId"] = n.ComponentId;
        if (n.ComponentName != null) o["componentName"] = n.ComponentName;
        if (n.PseudoStates is { Count: > 0 }) o["pseudoStates"] = StringArray(n.PseudoStates);
        o["childCount"] = Math.Max(0, n.ChildCount);
        if (n.Source is { } src) o["source"] = SourceJson(src);
        return o;
    }

    /// <summary>截断到 <paramref name="max"/> 个 UTF-16 单元，不拆开代理对。</summary>
    public static string Truncate(string text, int max, out bool truncated)
    {
        max = Math.Max(0, max);
        truncated = text.Length > max;
        if (!truncated) return text;
        int n = max;
        if (n > 0 && char.IsHighSurrogate(text[n - 1])) n--;
        return text.Substring(0, n);
    }

    public static void ValidateId(long id)
    {
        if (id <= 0 || id > MaxSafeId)
            throw new InvalidOperationException($"Inspectable node ids must be in [1, 2^53-1]; got {id}.");
    }
}
