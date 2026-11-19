using System;
using System.Collections.Generic;
using AwAUI.Diagnostics.Json;

namespace AwAUI.Diagnostics.Protocol;

/// <summary>调试命令参数类型。</summary>
public enum ConsoleParameterType
{
    /// <summary>字符串。</summary>
    String,
    /// <summary>64 位整数。</summary>
    Integer,
    /// <summary>双精度数。</summary>
    Number,
    /// <summary>布尔。</summary>
    Boolean,
    /// <summary>任意 JSON 值，原样交给处理器。</summary>
    Json,
}

/// <summary>调试命令的一个参数声明。</summary>
public sealed class ConsoleParameter
{
    public ConsoleParameter(string name, ConsoleParameterType type, bool optional = false, string? description = null)
    {
        Name = name ?? throw new ArgumentNullException(nameof(name));
        Type = type;
        Optional = optional;
        Description = description;
    }

    public string Name { get; }
    public ConsoleParameterType Type { get; }
    public bool Optional { get; }
    public string? Description { get; }

    internal static string TypeName(ConsoleParameterType t) => t switch
    {
        ConsoleParameterType.String => "string",
        ConsoleParameterType.Integer => "integer",
        ConsoleParameterType.Number => "number",
        ConsoleParameterType.Boolean => "boolean",
        _ => "json",
    };
}

/// <summary>
/// 具名调试命令（design/06：AOT 平台无法 eval，用命令注册表替代）。参数在调用前按声明校验类型，
/// 处理器拿到的是已校验的 <see cref="ConsoleArguments"/>。返回值作为 <c>Console.invoke</c> 的 <c>result</c> 字符串。
/// </summary>
public sealed class ConsoleCommand
{
    public ConsoleCommand(string name, string description, IReadOnlyList<ConsoleParameter> parameters, Func<ConsoleArguments, string?> handler)
    {
        if (string.IsNullOrEmpty(name)) throw new ArgumentException("Command name is required.", nameof(name));
        Name = name;
        Description = description ?? string.Empty;
        Parameters = parameters ?? Array.Empty<ConsoleParameter>();
        Handler = handler ?? throw new ArgumentNullException(nameof(handler));
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var p in Parameters)
            if (!seen.Add(p.Name)) throw new ArgumentException($"Duplicate parameter '{p.Name}'.", nameof(parameters));
    }

    public string Name { get; }
    public string Description { get; }
    public IReadOnlyList<ConsoleParameter> Parameters { get; }
    public Func<ConsoleArguments, string?> Handler { get; }

    internal JsonObject ToJson()
    {
        var ps = new JsonArray();
        foreach (var p in Parameters)
        {
            var o = new JsonObject { ["name"] = p.Name, ["type"] = ConsoleParameter.TypeName(p.Type), ["optional"] = p.Optional };
            if (p.Description != null) o["description"] = p.Description;
            ps.Add(o);
        }
        return new JsonObject { ["name"] = Name, ["description"] = Description, ["parameters"] = ps };
    }
}

/// <summary>已按声明校验过的命令参数。</summary>
public sealed class ConsoleArguments
{
    readonly Dictionary<string, JsonValue> _values;

    internal ConsoleArguments(ConsoleCommand command, Dictionary<string, JsonValue> values)
    {
        Command = command;
        _values = values;
    }

    public ConsoleCommand Command { get; }

    /// <summary>参数是否提供了（非 null 的）值。</summary>
    public bool Has(string name) => _values.ContainsKey(name);

    public string GetString(string name, string? fallback = null) =>
        _values.TryGetValue(name, out var v) ? v.GetString() : fallback ?? throw Missing(name);

    public long GetInt64(string name, long? fallback = null) =>
        _values.TryGetValue(name, out var v) ? v.GetInt64() : fallback ?? throw Missing(name);

    public double GetDouble(string name, double? fallback = null) =>
        _values.TryGetValue(name, out var v) ? v.GetDouble() : fallback ?? throw Missing(name);

    public bool GetBoolean(string name, bool? fallback = null) =>
        _values.TryGetValue(name, out var v) ? v.GetBoolean() : fallback ?? throw Missing(name);

    /// <summary>取原始 JSON 值；未提供时返回 null。</summary>
    public JsonValue? GetJson(string name) => _values.TryGetValue(name, out var v) ? v : null;

    static ArgumentException Missing(string name) => new($"Argument '{name}' was not provided.", nameof(name));
}
