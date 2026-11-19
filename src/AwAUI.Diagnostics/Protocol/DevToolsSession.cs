using System.Collections.Generic;
using AwAUI.Diagnostics.Transport;

namespace AwAUI.Diagnostics.Protocol;

/// <summary>一条传输上的会话状态：配对、协商出的版本、已启用的 Domain。对端重连时重置。</summary>
public sealed class DevToolsSession
{
    readonly HashSet<DevToolsDomain> _enabled = new();
    // 已启用且 enable 的响应已发出的 Domain：只有它们接收事件（「事件在启用它们的响应之后」）
    readonly HashSet<DevToolsDomain> _live = new();
    internal System.Action<System.ReadOnlyMemory<byte>>? OnReceivedHandler;
    internal System.Action? OnConnectionChanged;

    internal DevToolsSession(DevToolsAgent agent, DevToolsTransport transport)
    {
        Agent = agent;
        Transport = transport;
        ProtocolVersion = agent.ProtocolVersion;
    }

    public DevToolsAgent Agent { get; }
    public DevToolsTransport Transport { get; }

    /// <summary>是否已通过 App.hello 完成握手。</summary>
    public bool HasHandshake { get; internal set; }
    /// <summary>是否已授权（未配置配对令牌时始终为 true）。</summary>
    public bool IsAuthorized { get; internal set; }
    /// <summary>App.hello 中客户端自报的名字。</summary>
    public string? ClientName { get; internal set; }
    /// <summary>协商出的协议版本（握手前为 Agent 自身的版本）。</summary>
    public ProtocolVersion ProtocolVersion { get; internal set; }

    public bool IsDomainEnabled(DevToolsDomain domain)
    {
        lock (_enabled) return _enabled.Contains(domain);
    }

    public bool IsDomainEnabled(string name)
    {
        lock (_enabled)
            foreach (var d in _enabled)
                if (d.Name == name) return true;
        return false;
    }

    internal bool SetEnabled(DevToolsDomain domain, bool enabled)
    {
        lock (_enabled)
        {
            if (enabled) return _enabled.Add(domain);
            _live.Remove(domain);
            return _enabled.Remove(domain);
        }
    }

    /// <summary>
    /// 本会话是否接收 <paramref name="domain"/> 的事件：已启用，且 enable 的响应已经发出。
    /// 在 enable 被处理与其响应发出之间（其他线程上）产生的事件不发给本会话，保证事件不会先于启用它们的响应到达。
    /// </summary>
    internal bool IsDomainLive(DevToolsDomain domain)
    {
        lock (_enabled) return _live.Contains(domain);
    }

    /// <summary>enable 的响应已发出：开始接收事件（期间已被停用则不做任何事）。</summary>
    internal void SetLive(DevToolsDomain domain)
    {
        lock (_enabled)
            if (_enabled.Contains(domain)) _live.Add(domain);
    }

    internal DevToolsDomain[] TakeAllEnabled()
    {
        lock (_enabled)
        {
            var all = new DevToolsDomain[_enabled.Count];
            _enabled.CopyTo(all);
            _enabled.Clear();
            _live.Clear();
            return all;
        }
    }
}
