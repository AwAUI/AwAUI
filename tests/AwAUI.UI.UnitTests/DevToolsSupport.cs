using System.Text;
using AwAUI.Diagnostics.Json;
using AwAUI.Diagnostics.Protocol;
using AwAUI.Diagnostics.Transport;

namespace AwAUI.UI.UnitTests;

/// <summary>
/// 进程内的 DevTools 协议客户端（测试用前端）。在 UI 线程上调用：进程内传输同步投递，检查接缝的方法在 UI 线程上直接执行，
/// 因此 <see cref="Call"/> 返回时响应已经到达；事件按到达顺序保存在 <see cref="Messages"/> 中。
/// </summary>
sealed class DevToolsClient
{
    readonly InProcTransport _transport;
    readonly List<JsonObject> _messages = new();
    readonly object _gate = new();
    long _id;

    public DevToolsClient(UIDevTools devTools)
    {
        var (agentSide, clientSide) = InProcTransport.CreatePair();
        devTools.Connect(agentSide);
        _transport = clientSide;
        clientSide.Received += m =>
        {
            var o = JsonValue.Parse(m.Span).AsObject();
            lock (_gate) _messages.Add(o);
        };
    }

    public List<JsonObject> Messages { get { lock (_gate) return new List<JsonObject>(_messages); } }

    public void Clear() { lock (_gate) _messages.Clear(); }

    /// <summary>名为 <paramref name="method"/> 的事件的参数，按到达顺序。</summary>
    public List<JsonObject> Events(string method)
    {
        var list = new List<JsonObject>();
        foreach (var m in Messages)
            if (m["method"] is JsonString s && s.Value == method && !m.ContainsKey("id")) list.Add(m["params"]!.AsObject());
        return list;
    }

    /// <summary>发送请求并返回完整的响应对象（含 result 或 error）。</summary>
    public JsonObject Raw(string method, JsonObject? parameters = null)
    {
        long id = ++_id;
        var req = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method };
        if (parameters is not null) req["params"] = parameters;
        _transport.SendAsync(req.ToUtf8Bytes()).GetAwaiter().GetResult();
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (true)
        {
            foreach (var m in Messages)
                if (m["id"] is JsonNumber n && n.GetInt64() == id && !m.ContainsKey("method")) return m;
            if (DateTime.UtcNow > deadline) throw new TimeoutException("No response to " + method + ". Got: " + string.Join("\n", Messages.Select(x => x.ToJsonString())));
            Thread.Sleep(1);
        }
    }

    /// <summary>发送请求，断言成功并返回 result。</summary>
    public JsonValue Call(string method, JsonObject? parameters = null)
    {
        var r = Raw(method, parameters);
        Assert.True(r["error"] is null, method + " failed: " + r.ToJsonString());
        return r["result"]!;
    }

    /// <summary>发送请求，断言失败并返回错误码。</summary>
    public long Fail(string method, JsonObject? parameters = null)
    {
        var r = Raw(method, parameters);
        Assert.True(r["error"] is not null, method + " should fail: " + r.ToJsonString());
        return r["error"]!["code"]!.GetInt64();
    }

    public static JsonObject Node(long id) => new() { ["nodeId"] = id };
}

/// <summary>前端视角的元素树镜像：由 getDocument 建立，按 childrenChanged / childMoved 事件增量维护（与真实树逐项比较）。</summary>
sealed class TreeMirror
{
    public readonly Dictionary<long, List<long>> Children = new();
    public readonly Dictionary<long, string> Labels = new();

    public void Load(JsonValue node)
    {
        var stack = new Stack<JsonValue>();
        stack.Push(node);
        while (stack.Count > 0)
        {
            var n = stack.Pop();
            long id = n["id"]!.GetInt64();
            Labels[id] = Label(n);
            if (n["children"] is JsonArray ch)
            {
                var list = new List<long>();
                foreach (var c in ch)
                {
                    list.Add(c["id"]!.GetInt64());
                    stack.Push(c);
                }
                Children[id] = list;
            }
        }
    }

    static string Label(JsonValue n) =>
        n["kind"]!.GetString() == "text" ? "\"" + n["text"]!.GetString() + "\"" : n["tag"]!.GetString() + (n["elementId"] is JsonString s ? "#" + s.Value : "");

    public void Apply(JsonObject message)
    {
        var method = message["method"]!.GetString();
        var p = message["params"]!;
        if (method == "Tree.childrenChanged")
        {
            long parent = p["parentId"]!.GetInt64();
            var list = Children[parent];
            foreach (var r in p["removed"]!.AsArray()) list.Remove(r.GetInt64());
            foreach (var ins in p["inserted"]!.AsArray())
            {
                var node = ins["node"]!;
                long id = node["id"]!.GetInt64();
                Load(node);
                Insert(list, id, ins["beforeId"]);
            }
        }
        else if (method == "Tree.childMoved")
        {
            var list = Children[p["parentId"]!.GetInt64()];
            long id = p["nodeId"]!.GetInt64();
            list.Remove(id);
            Insert(list, id, p["beforeId"]);
        }
    }

    static void Insert(List<long> list, long id, JsonValue? before)
    {
        if (before is null)
        {
            list.Add(id);
            return;
        }
        int i = list.IndexOf(before.GetInt64());
        Assert.True(i >= 0, "beforeId " + before.GetInt64() + " is not a known child");
        list.Insert(i, id);
    }
}
