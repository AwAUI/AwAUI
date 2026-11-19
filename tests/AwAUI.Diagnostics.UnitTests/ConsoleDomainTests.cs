using AwAUI.Diagnostics.Json;
using AwAUI.Diagnostics.Protocol;

namespace AwAUI.Diagnostics.UnitTests;

public class ConsoleDomainTests
{
    [Fact]
    public async Task Enable_replays_buffer_after_response_then_streams()
    {
        var (agent, client, _) = RpcClient.Create(new DevToolsAgentOptions { ConsoleBufferSize = 2 });
        agent.Console.Log(LogLevel.Debug, "dropped");
        agent.Console.Log(LogLevel.Info, "one");
        agent.Console.Log(LogLevel.Warning, "two", sourceFile: "App.awaui", sourceLine: 12, componentId: 7);
        Assert.Empty(client.Messages);

        var resp = await client.CallAsync("Console.enable");
        Rpc.Result(resp);
        var msgs = client.Messages;
        Assert.Same(resp, msgs[0]);    // 先响应，再补发
        var entries = client.Events("Console.entry");
        Assert.Equal(new[] { "one", "two" }, entries.Select(e => e["params"]!["message"]!.GetString()).ToArray());
        var two = entries[1]["params"]!;
        Assert.Equal("warning", two["level"]!.GetString());
        Assert.Equal("App.awaui", two["source"]!["file"]!.GetString());
        Assert.Equal(12, two["source"]!["line"]!.GetInt64());
        Assert.Equal(7, two["componentId"]!.GetInt64());
        Assert.True(two["timestamp"]!.GetDouble() > 1.6e12);

        agent.Console.Log(LogLevel.Error, "three", new InvalidOperationException("bad"));
        var three = client.Events("Console.entry").Last()["params"]!;
        Assert.Equal("error", three["level"]!.GetString());
        Assert.Contains("InvalidOperationException", three["exception"]!.GetString());

        Rpc.Result(await client.CallAsync("Console.disable"));
        agent.Console.Log(LogLevel.Info, "four");
        Assert.Equal(3, client.Events("Console.entry").Count);

        Rpc.Result(await client.CallAsync("Console.clear"));
        client.ClearMessages();
        Rpc.Result(await client.CallAsync("Console.enable"));
        Assert.Empty(client.Events("Console.entry"));
    }

    [Fact]
    public async Task Logging_from_another_thread_during_enable_is_neither_duplicated_nor_reordered()
    {
        var (agent, client, _) = RpcClient.Create(new DevToolsAgentOptions { ConsoleBufferSize = 64 });
        using var stop = new CancellationTokenSource();
        int paused = 1;
        long iterations = 0;
        long logged = 0;
        // 专用线程：全量并行跑测试时线程池可能被占满，日志线程在开启窗口内得不到调度
        var logger = Task.Factory.StartNew(() =>
        {
            while (!stop.IsCancellationRequested)
            {
                if (Volatile.Read(ref paused) == 0) agent.Console.Log(LogLevel.Info, Interlocked.Increment(ref logged).ToString(System.Globalization.CultureInfo.InvariantCulture));
                else Thread.Yield();
                Interlocked.Increment(ref iterations);
            }
        }, TaskCreationOptions.LongRunning);
        async Task PauseAsync()
        {
            // 置位后再等日志线程完整跑完两轮：此后没有进行中的 Log（它的投递已同步完成）
            Volatile.Write(ref paused, 1);
            long start = Interlocked.Read(ref iterations);
            while (Interlocked.Read(ref iterations) < start + 2) await Task.Yield();
        }
        try
        {
            long floor = 0;    // 上一轮暂停时已写的条数：更早的条目若迟到（上一轮停用前已通过检查），不计入本轮
            for (int round = 0; round < 30; round++)
            {
                client.ClearMessages();
                Volatile.Write(ref paused, 0);
                await Task.Delay(1);
                var resp = await client.CallAsync("Console.enable");
                // 等到确实收到本轮的实时推送再停用。停用时仍在发件队列中、尚未送达的条目会被丢弃（协议约定，见 design/20），
                // 因此不能假设「开启期间写入的条目」一定送达；本测试检验的是送达部分的顺序与不重复。
                long seenFloor = floor;
                var deadline = DateTime.UtcNow.AddSeconds(10);
                while (DateTime.UtcNow < deadline && !client.Messages.Any(m =>
                           m["method"] is JsonString { Value: "Console.entry" } &&
                           long.Parse(m["params"]!["message"]!.GetString(), System.Globalization.CultureInfo.InvariantCulture) > seenFloor))
                    await Task.Delay(1);
                Rpc.Result(await client.CallAsync("Console.disable"));
                await PauseAsync();
                Rpc.Result(await client.CallAsync("App.getInfo"));    // 屏障：此前进入收件队列的消息都已投递

                var msgs = client.Messages;
                int enableAt = msgs.FindIndex(m => ReferenceEquals(m, resp));
                var numbers = new List<long>();
                for (int i = 0; i < msgs.Count; i++)
                {
                    if (msgs[i]["method"] is not JsonString { Value: "Console.entry" }) continue;
                    long n = long.Parse(msgs[i]["params"]!["message"]!.GetString(), System.Globalization.CultureInfo.InvariantCulture);
                    if (n <= floor) continue;
                    Assert.True(i > enableAt, $"round {round}: Console.entry {n} arrived before the enable response");
                    numbers.Add(n);
                }
                floor = Interlocked.Read(ref logged);
                Assert.NotEmpty(numbers);
                // 补发 + 实时推送是一段连续、无重复、递增的序列
                for (int i = 1; i < numbers.Count; i++)
                    Assert.True(numbers[i] == numbers[i - 1] + 1, $"round {round}: {numbers[i - 1]} followed by {numbers[i]}");
            }
        }
        finally
        {
            stop.Cancel();
            await logger;
        }
    }

    [Fact]
    public async Task Hub_log_events_are_forwarded_while_enabled()
    {
        var (_, client, _) = RpcClient.Create();
        // 其他测试可能并行地启用了 Log 类别，所以只断言「启用后能收到」
        Rpc.Result(await client.CallAsync("Console.enable"));
        Assert.True(DiagnosticHub.IsEnabled(DiagnosticCategory.Log));
        string marker = "hub-" + Guid.NewGuid().ToString("N");
        if (DiagnosticHub.IsEnabled(DiagnosticCategory.Log))
            DiagnosticHub.Write(DiagnosticCategory.Log, new LogEvent(LogLevel.Info, marker, sourceFile: "x.cs", sourceLine: 3));
        var entry = await client.WaitForAsync(m => m["params"]?["message"] is JsonString s && s.Value == marker);
        Assert.Equal("x.cs", entry["params"]!["source"]!["file"]!.GetString());
    }

    static (DevToolsAgent, RpcClient) WithCommands()
    {
        var (agent, client, _) = RpcClient.Create();
        agent.Console.RegisterCommand("add", "Adds two integers", args => (args.GetInt64("a") + args.GetInt64("b", 10)).ToString(),
            new ConsoleParameter("a", ConsoleParameterType.Integer),
            new ConsoleParameter("b", ConsoleParameterType.Integer, optional: true, description: "defaults to 10"));
        agent.Console.RegisterCommand("describe", "Echoes typed args", args =>
                string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{args.GetString("s")}|{args.GetDouble("n")}|{args.GetBoolean("f")}|{args.GetJson("j")}"),
            new ConsoleParameter("s", ConsoleParameterType.String),
            new ConsoleParameter("n", ConsoleParameterType.Number),
            new ConsoleParameter("f", ConsoleParameterType.Boolean),
            new ConsoleParameter("j", ConsoleParameterType.Json, optional: true));
        agent.Console.RegisterCommand("void", "Returns nothing", _ => null);
        agent.Console.RegisterCommand("explode", "Throws", _ => throw new InvalidOperationException("kaboom"));
        agent.Console.RegisterCommand("missingNode", "Protocol error", _ => throw new DevToolsProtocolException(DevToolsErrorCodes.NodeNotFound, "gone"));
        return (agent, client);
    }

    [Fact]
    public async Task GetCommands_lists_typed_parameters()
    {
        var (_, client) = WithCommands();
        var cmds = Rpc.Result(await client.CallAsync("Console.getCommands"))["commands"]!.AsArray();
        var add = cmds.Single(c => c["name"]!.GetString() == "add");
        Assert.Equal("Adds two integers", add["description"]!.GetString());
        var ps = add["parameters"]!.AsArray();
        Assert.Equal("a", ps[0]["name"]!.GetString());
        Assert.Equal("integer", ps[0]["type"]!.GetString());
        Assert.False(ps[0]["optional"]!.GetBoolean());
        Assert.True(ps[1]["optional"]!.GetBoolean());
        Assert.Equal("json", cmds.Single(c => c["name"]!.GetString() == "describe")["parameters"]![3]["type"]!.GetString());
    }

    static JsonObject Invoke(string name, JsonObject? args = null)
    {
        var p = new JsonObject { ["name"] = name };
        if (args != null) p["args"] = args;
        return p;
    }

    [Fact]
    public async Task Invoke_runs_commands_with_validated_args()
    {
        var (_, client) = WithCommands();
        Assert.Equal("5", Rpc.Result(await client.CallAsync("Console.invoke", Invoke("add", new JsonObject { ["a"] = 2, ["b"] = 3 })))["result"]!.GetString());
        Assert.Equal("12", Rpc.Result(await client.CallAsync("Console.invoke", Invoke("add", new JsonObject { ["a"] = 2 })))["result"]!.GetString());
        Assert.Equal("12", Rpc.Result(await client.CallAsync("Console.invoke", Invoke("add", new JsonObject { ["a"] = 2, ["b"] = null })))["result"]!.GetString());
        Assert.Equal("x|1.5|True|{\"k\":[1]}", Rpc.Result(await client.CallAsync("Console.invoke",
            Invoke("describe", new JsonObject { ["s"] = "x", ["n"] = 1.5, ["f"] = true, ["j"] = new JsonObject { ["k"] = new JsonArray { 1 } } })))["result"]!.GetString());
        var empty = Rpc.Result(await client.CallAsync("Console.invoke", Invoke("void")));
        Assert.Null(empty["result"]);
    }

    [Fact]
    public async Task Invoke_rejects_bad_arguments()
    {
        var (_, client) = WithCommands();
        Rpc.Error(await client.CallAsync("Console.invoke", Invoke("nope")), DevToolsErrorCodes.InvalidParams);
        Rpc.Error(await client.CallAsync("Console.invoke", new JsonObject()), DevToolsErrorCodes.InvalidParams);
        Rpc.Error(await client.CallAsync("Console.invoke", Invoke("add")), DevToolsErrorCodes.InvalidParams);                                  // 缺 a
        Rpc.Error(await client.CallAsync("Console.invoke", Invoke("add", new JsonObject { ["a"] = "2" })), DevToolsErrorCodes.InvalidParams);  // 类型不符
        Rpc.Error(await client.CallAsync("Console.invoke", Invoke("add", new JsonObject { ["a"] = 2.5 })), DevToolsErrorCodes.InvalidParams);
        var unknown = Rpc.Error(await client.CallAsync("Console.invoke", Invoke("add", new JsonObject { ["a"] = 1, ["c"] = 1 })), DevToolsErrorCodes.InvalidParams);
        Assert.Equal("c", unknown["data"]!["argument"]!.GetString());
        var p = Invoke("add");
        p["args"] = new JsonArray { 1, 2 };
        Rpc.Error(await client.CallAsync("Console.invoke", p), DevToolsErrorCodes.InvalidParams);
        Rpc.Error(await client.CallAsync("Console.invoke", Invoke("describe", new JsonObject { ["s"] = "x", ["n"] = "1", ["f"] = true })), DevToolsErrorCodes.InvalidParams);
        Rpc.Error(await client.CallAsync("Console.invoke", Invoke("describe", new JsonObject { ["s"] = "x", ["n"] = 1, ["f"] = 1 })), DevToolsErrorCodes.InvalidParams);
    }

    [Fact]
    public async Task Invoke_maps_command_failures()
    {
        var (agent, client) = WithCommands();
        Assert.Contains("kaboom", Rpc.Error(await client.CallAsync("Console.invoke", Invoke("explode")), DevToolsErrorCodes.InternalError)["message"]!.GetString());
        Rpc.Error(await client.CallAsync("Console.invoke", Invoke("missingNode")), DevToolsErrorCodes.NodeNotFound);
        Assert.True(agent.Console.UnregisterCommand("explode"));
        Rpc.Error(await client.CallAsync("Console.invoke", Invoke("explode")), DevToolsErrorCodes.InvalidParams);
    }

    [Fact]
    public void Command_declaration_rejects_duplicate_parameters()
    {
        Assert.Throws<ArgumentException>(() => new ConsoleCommand("x", "", new[]
        {
            new ConsoleParameter("a", ConsoleParameterType.String),
            new ConsoleParameter("a", ConsoleParameterType.Integer),
        }, _ => null));
    }
}
