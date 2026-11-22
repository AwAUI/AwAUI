using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using AwAUI.Diagnostics.Client;
using AwAUI.Diagnostics.Json;
using AwAUI.Diagnostics.Transport;
using AwAUI.Threading;

namespace AwAUI.DevTools.Tests;

/// <summary>
/// 组件页 / 样式表页测试用的最小协议桩：一条内存传输按请求方法返回预设结果，并可主动推送 <c>Css</c> 事件。
/// Harness 的公共入口只覆盖连接到真实 Agent 的场景；推送外部 <c>styleSheetChanged</c> 需要这里。
/// </summary>
internal sealed class StubTransport : DevToolsTransport
{
    public readonly List<StubSheet> Sheets = new();
    public readonly List<string> Methods = new();

    /// <summary>模拟服务端在处理 <c>setStyleSheetText</c> 时先推送 <c>styleSheetChanged</c> 再返回响应。</summary>
    public bool EmitStyleSheetChangedOnApply;

    /// <summary><c>Tree.getDocument</c> 返回的根节点；null 时返回一个空 div。</summary>
    public JsonObject? RootJson;

    /// <summary>让 <c>getStyleSheets</c> / <c>getStyleSheetText</c> 返回协议错误（验证前端的错误显示）。</summary>
    public bool FailSheetList;
    public bool FailSheetText;

    public override bool IsConnected => true;

    public StubSheet? Find(string sheetId) => Sheets.Find(s => s.Id == sheetId);

    public override Task SendAsync(ReadOnlyMemory<byte> message)
    {
        var request = JsonValue.Parse(message.Span) as JsonObject ?? new JsonObject();
        string method = request.GetStringOrDefault("method") ?? "";
        Methods.Add(method);
        var p = request["params"] as JsonObject ?? new JsonObject();
        long id = request["id"] is JsonNumber n ? n.GetInt64() : 0;
        switch (method)
        {
            case "App.hello":
                Respond(id, new JsonObject
                {
                    ["app"] = new JsonObject { ["name"] = "Stub" },
                    ["targets"] = new JsonArray { new JsonObject { ["id"] = "t1" } },
                });
                break;
            case "Css.getStyleSheets":
            {
                if (FailSheetList)
                {
                    RespondError(id, -32603, "Style sheets are unavailable.");
                    break;
                }
                var arr = new JsonArray();
                foreach (var s in Sheets) arr.Add(SheetJson(s));
                Respond(id, new JsonObject { ["sheets"] = arr });
                break;
            }
            case "Css.getStyleSheetText":
            {
                string sheetId = p.GetStringOrDefault("sheetId") ?? "";
                if (FailSheetText)
                {
                    RespondError(id, -32603, "Style sheet text is unavailable.");
                    break;
                }
                if (Find(sheetId) is not { } sheet)
                {
                    RespondError(id, -32602, "Style sheet '" + sheetId + "' not found.");
                    break;
                }
                Respond(id, new JsonObject { ["text"] = sheet.Text });
                break;
            }
            case "Css.setStyleSheetText":
            {
                string sheetId = p.GetStringOrDefault("sheetId") ?? "";
                string text = p.GetStringOrDefault("text") ?? "";
                if (Find(sheetId) is not { } sheet)
                {
                    RespondError(id, -32602, "Style sheet '" + sheetId + "' not found.");
                    break;
                }
                if (!sheet.Editable)
                {
                    RespondError(id, -32004, "The style sheet is read-only.");
                    break;
                }
                sheet.Text = text;
                if (EmitStyleSheetChangedOnApply)
                    Emit("Css.styleSheetChanged", new JsonObject { ["sheetId"] = sheetId });
                var diagnostics = new JsonArray();
                if (text.IndexOf("bogus", StringComparison.Ordinal) >= 0)
                    diagnostics.Add(new JsonObject
                    {
                        ["severity"] = "warning",
                        ["message"] = "Unknown property 'bogus'.",
                        ["code"] = "AW2002",
                        ["line"] = 1,
                        ["column"] = 6,
                    });
                Respond(id, new JsonObject { ["diagnostics"] = diagnostics });
                break;
            }
            case "Tree.getDocument":
                Respond(id, new JsonObject
                {
                    ["root"] = RootJson ?? new JsonObject { ["id"] = 1, ["kind"] = "element", ["tag"] = "div", ["childCount"] = 0 },
                });
                break;
            default:
                Respond(id, new JsonObject());
                break;
        }
        return Task.CompletedTask;
    }

    /// <summary>推送一条事件（参数形状与 Agent 一致，带 target）。</summary>
    public void Emit(string method, JsonObject parameters) =>
        OnReceived(new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["method"] = method,
            ["params"] = parameters,
            ["target"] = "t1",
        }.ToUtf8Bytes());

    void Respond(long id, JsonObject result) =>
        OnReceived(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = result }.ToUtf8Bytes());

    void RespondError(long id, int code, string message) =>
        OnReceived(new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id,
            ["error"] = new JsonObject { ["code"] = code, ["message"] = message },
        }.ToUtf8Bytes());

    static JsonObject SheetJson(StubSheet s)
    {
        var o = new JsonObject { ["sheetId"] = s.Id, ["origin"] = s.Origin, ["disabled"] = s.Disabled, ["editable"] = s.Editable };
        if (s.SourceUrl is not null) o["sourceUrl"] = s.SourceUrl;
        if (s.Title is not null) o["title"] = s.Title;
        if (s.OwnerNodeId != 0) o["ownerNodeId"] = s.OwnerNodeId;
        return o;
    }
}

/// <summary>样式表桩的一项。</summary>
internal sealed class StubSheet
{
    public StubSheet(string id, string text)
    {
        Id = id;
        Text = text;
    }

    public readonly string Id;
    public string Text;
    public string Origin { get; set; } = "author";
    public string? SourceUrl { get; set; }
    public string? Title { get; set; }
    public bool Disabled { get; set; }
    public bool Editable { get; set; } = true;
    public long OwnerNodeId { get; set; }
}

/// <summary>同步执行 <see cref="IDispatcher.Post"/> 的调度器：桩传输的响应在发送栈上直接完成。</summary>
internal sealed class ImmediateDispatcher : IDispatcher
{
    public bool CheckAccess() => true;

    public void Post(Action action) => action();
}

/// <summary>用桩传输驱动的 <see cref="InspectorModel"/>，用于不便经真实 Agent 触发的场景。</summary>
internal static class FrontendTestUtil
{
    public static InspectorModel CreateModel(StubTransport transport, out DevToolsClient client)
    {
        client = new DevToolsClient(transport);
        return new InspectorModel(client, new ImmediateDispatcher(), "t1", null);
    }
}
