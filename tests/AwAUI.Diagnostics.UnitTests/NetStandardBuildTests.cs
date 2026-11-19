using System.Reflection;
using System.Runtime.Loader;
using System.Runtime.Versioning;
using System.Text;
using AwAUI.Diagnostics.Json;

namespace AwAUI.Diagnostics.UnitTests;

/// <summary>
/// 在 net10/net11 测试进程里加载库的 netstandard2.0 产物（独立的 AssemblyLoadContext），执行 .NET Framework 专用的代码路径：
/// double 解析 / "R" 格式化回退、polyfill、Utf8Formatter，以及一次完整的进程内 JSON-RPC 往返。
/// 这不能替代真正的 .NET Framework 运行（net48 测试目标只在 Windows 上运行），但能覆盖 netstandard2.0 条件编译分支。
/// </summary>
public class NetStandardBuildTests
{
    sealed class IsolatedContext : AssemblyLoadContext
    {
        readonly string _dir;

        public IsolatedContext(string dir) : base("AwAUI.Diagnostics.netstandard2.0", isCollectible: true) => _dir = dir;

        protected override Assembly? Load(AssemblyName name)
        {
            // 只隔离 AwAUI 自己的程序集；框架程序集回落到默认上下文（与 .NET Framework 上的统一行为一致）
            string path = Path.Combine(_dir, name.Name + ".dll");
            return name.Name!.StartsWith("AwAUI.", StringComparison.Ordinal) && File.Exists(path) ? LoadFromAssemblyPath(path) : null;
        }
    }

    static Assembly Load(out IsolatedContext context)
    {
        string path = typeof(NetStandardBuildTests).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .Single(a => a.Key == "AwAUI.Diagnostics.NetStandardPath").Value!;
        Assert.True(File.Exists(path), "netstandard2.0 build not found: " + path);
        context = new IsolatedContext(Path.GetDirectoryName(path)!);
        var asm = context.LoadFromAssemblyPath(path);
        Assert.Equal(".NETStandard,Version=v2.0", asm.GetCustomAttribute<TargetFrameworkAttribute>()!.FrameworkName);
        Assert.NotSame(typeof(JsonValue).Assembly, asm);
        return asm;
    }

    static object? Call(object target, string method, params object?[] args)
    {
        var types = args.Select(a => a?.GetType() ?? typeof(object)).ToArray();
        var m = target.GetType().GetMethods().Single(x => x.Name == method && x.GetParameters().Length == args.Length &&
            x.GetParameters().Select(p => p.ParameterType).Zip(types).All(t => t.Second == typeof(object) || t.First.IsAssignableFrom(t.Second)));
        return m.Invoke(target, args);
    }

    [Fact]
    public void Number_parsing_and_formatting_fallbacks()
    {
        var asm = Load(out var context);
        try
        {
            var valueType = asm.GetType("AwAUI.Diagnostics.Json.JsonValue", true)!;
            var optionsType = asm.GetType("AwAUI.Diagnostics.Json.JsonReaderOptions", true)!;
            var parse = valueType.GetMethod("Parse", new[] { typeof(string), optionsType })!;
            var doc = parse.Invoke(null, new[] { "[1.5e3,-0.1,1e400,-1e400,12345678901234567890,5e-324,42]", Activator.CreateInstance(optionsType) })!;
            var item = valueType.GetProperty("Item", new[] { typeof(int) })!;
            double D(int i) => (double)Call(item.GetValue(doc, new object[] { i })!, "GetDouble")!;
            Assert.Equal(1500.0, D(0));
            Assert.Equal(-0.1, D(1));
            Assert.Equal(double.PositiveInfinity, D(2));
            Assert.Equal(double.NegativeInfinity, D(3));
            Assert.Equal(12345678901234567890.0, D(4));
            Assert.Equal(double.Epsilon, D(5));
            Assert.Equal(42.0, D(6));

            // JsonWriter.WriteNumber(double) 在 netstandard2.0 上走 ToString("R")，WriteNumber(long) 走 Utf8Formatter
            var buffer = Activator.CreateInstance(asm.GetType("AwAUI.Diagnostics.Json.JsonBufferWriter", true)!, 256)!;
            var writer = Activator.CreateInstance(asm.GetType("AwAUI.Diagnostics.Json.JsonWriter", true)!, buffer)!;
            Call(writer, "WriteStartArray");
            double[] doubles = { 0.1, 1e21, -1.5e-7, 123.0, double.MaxValue, 1.0 / 3 };
            foreach (var d in doubles) Call(writer, "WriteNumber", d);
            Call(writer, "WriteNumber", long.MinValue);
            Call(writer, "WriteEndArray");
            string text = Encoding.UTF8.GetString((byte[])Call(buffer, "ToArray")!);
            var back = JsonValue.Parse(text).AsArray();    // 用 net10 的解析器读回：格式合法且往返精确
            for (int i = 0; i < doubles.Length; i++) Assert.Equal(doubles[i], back[i].GetDouble());
            Assert.Equal(long.MinValue, back[doubles.Length].GetInt64());
        }
        finally
        {
            context.Unload();
        }
    }

    [Fact]
    public void In_proc_json_rpc_round_trip()
    {
        var asm = Load(out var context);
        try
        {
            var agent = Activator.CreateInstance(asm.GetType("AwAUI.Diagnostics.Protocol.DevToolsAgent", true)!, new object?[] { null })!;
            var pair = asm.GetType("AwAUI.Diagnostics.Transport.InProcTransport", true)!.GetMethod("CreatePair")!.Invoke(null, null)!;
            object agentEnd = pair.GetType().GetField("Item1")!.GetValue(pair)!;
            object clientEnd = pair.GetType().GetField("Item2")!.GetValue(pair)!;
            Call(agent, "Attach", agentEnd);

            var received = new List<JsonObject>();
            clientEnd.GetType().GetEvent("Received")!.AddEventHandler(clientEnd,
                new Action<ReadOnlyMemory<byte>>(m => received.Add(JsonValue.Parse(m.Span).AsObject())));
            void Send(string json) => ((Task)Call(clientEnd, "SendAsync", new ReadOnlyMemory<byte>(Encoding.UTF8.GetBytes(json)))!).GetAwaiter().GetResult();

            Send("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"App.hello\",\"params\":{\"client\":\"ns20\",\"protocolVersion\":\"1.0\"}}");
            Assert.Equal("1.0", Rpc.Result(received.Single())["protocolVersion"]!.GetString());
            Send("{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"Console.enable\"}");
            Rpc.Result(received[1]);

            var console = agent.GetType().GetProperty("Console")!.GetValue(agent)!;
            var levelType = asm.GetType("AwAUI.Diagnostics.LogLevel", true)!;
            var log = console.GetType().GetMethod("Log")!;
            log.Invoke(console, new object?[] { Enum.Parse(levelType, "Warning"), "from netstandard2.0 — 你好", null, "a.cs", 7, 99L });
            var entry = received.Last();
            Assert.Equal("Console.entry", entry["method"]!.GetString());
            Assert.Equal("from netstandard2.0 — 你好", entry["params"]!["message"]!.GetString());
            Assert.Equal("warning", entry["params"]!["level"]!.GetString());
            Assert.Equal(99, entry["params"]!["componentId"]!.GetInt64());
            Assert.True(entry["params"]!["timestamp"]!.GetDouble() > 1.6e12);

            Send("{\"jsonrpc\":\"2.0\",\"id\":3,\"method\":\"Nope\",\"params\":[1,2]}");
            Rpc.Error(received.Last(), -32602);
        }
        finally
        {
            context.Unload();
        }
    }
}
