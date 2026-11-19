using AwAUI.Diagnostics.Json;

namespace AwAUI.Diagnostics.Protocol;

/// <summary>
/// App 域（design/20「会话」）：<c>App.hello</c> 握手（版本协商、配对令牌）与 <c>App.getInfo</c>。
/// </summary>
/// <remarks>
/// App.hello 的检查顺序：参数 → 配对令牌（不符返回 -32006）→ 协议版本（格式错误或主版本不同返回 -32602，
/// <c>data.protocolVersion</c> 给出本端版本，客户端据此提示升级）。主版本不同时，错误响应发出后 Agent 断开该连接
/// （design/20「主版本不同拒绝连接」；进程内传输没有可断开的连接，会话保持未配对）。
/// 成功后会话被授权，次版本取双方较小者。失败的 hello 不改变会话已有的状态。
/// </remarks>
public sealed class AppDevToolsDomain : DevToolsDomain
{
    internal AppDevToolsDomain() : base("App") { }

    protected override void RegisterMethods(DevToolsMethodTable methods)
    {
        methods.Add("hello", Hello);
        methods.Add("getInfo", ctx => ctx.Agent.Options.AppInfo.ToJson());
    }

    static JsonValue Hello(DevToolsCallContext ctx)
    {
        var agent = ctx.Agent;
        string client = ctx.GetString("client");
        string versionText = ctx.GetString("protocolVersion");
        string? token = ctx.GetOptionalString("token");

        string? expected = agent.Options.PairingToken;
        if (expected != null && (token is null || !FixedTimeEquals(token, expected)))
            throw new DevToolsProtocolException(DevToolsErrorCodes.Unauthorized, "Unauthorized: invalid or missing pairing token.");

        var server = agent.ProtocolVersion;
        if (!ProtocolVersion.TryParse(versionText, out var clientVersion))
            throw DevToolsProtocolException.InvalidParams("Parameter 'protocolVersion' must have the form 'major.minor'.",
                new JsonObject { ["parameter"] = "protocolVersion", ["protocolVersion"] = server.ToString() });
        if (!server.TryNegotiate(clientVersion, out var negotiated))
        {
            // design/20：主版本不同拒绝连接 —— 先回错误（让前端能提示升级），随后断开
            ctx.DisconnectReason = "Incompatible DevTools protocol version";
            throw DevToolsProtocolException.InvalidParams($"Unsupported protocol version {clientVersion}; this agent speaks {server}.",
                new JsonObject { ["parameter"] = "protocolVersion", ["protocolVersion"] = server.ToString() });
        }

        var session = ctx.Session;
        session.IsAuthorized = true;
        session.HasHandshake = true;
        session.ClientName = client;
        session.ProtocolVersion = negotiated;

        var targets = new JsonArray();
        foreach (var t in agent.Targets) targets.Add(t.ToJson());
        var domains = new JsonArray();
        foreach (var d in agent.Domains) domains.Add(d.Name);

        return new JsonObject
        {
            ["protocolVersion"] = negotiated.ToString(),
            ["app"] = agent.Options.AppInfo.ToJson(),
            ["targets"] = targets,
            ["domains"] = domains,
        };
    }

    static bool FixedTimeEquals(string a, string b)
    {
        int diff = a.Length ^ b.Length;
        int n = System.Math.Max(a.Length, b.Length);
        for (int i = 0; i < n; i++)
        {
            char x = i < a.Length ? a[i] : '\0';
            char y = i < b.Length ? b[i] : '\0';
            diff |= x ^ y;
        }
        return diff == 0;
    }
}
