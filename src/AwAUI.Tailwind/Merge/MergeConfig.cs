using System;
using System.Collections.Generic;

namespace AwAUI.Tailwind.Merge;

internal enum DefKind : byte
{
    Literal,
    Validator,
    Theme,
    Object,
}

/// <summary>类组定义项：字面量、校验器、主题引用或嵌套对象（tailwind-merge 的 <c>ClassDefinition</c>）。</summary>
internal readonly struct Def
{
    public readonly DefKind Kind;
    public readonly string? Text;
    public readonly Func<string, bool>? Validator;
    public readonly (string Key, Def[] Defs)[]? Entries;

    Def(DefKind kind, string? text, Func<string, bool>? validator, (string, Def[])[]? entries)
    {
        Kind = kind;
        Text = text;
        Validator = validator;
        Entries = entries;
    }

    public static implicit operator Def(string literal) => new(DefKind.Literal, literal, null, null);

    public static implicit operator Def(Func<string, bool> validator) => new(DefKind.Validator, null, validator, null);

    public static Def Theme(string key) => new(DefKind.Theme, key, null, null);

    public static Def Object((string Key, Def[] Defs)[] entries) => new(DefKind.Object, null, null, entries);
}

/// <summary>合并配置（类组、主题刻度、冲突关系）。</summary>
internal sealed class MergeConfigBuilder
{
    public readonly Dictionary<string, List<Def>> Themes = new(StringComparer.Ordinal);
    public readonly List<KeyValuePair<string, List<Def>>> Groups = new();
    public readonly Dictionary<string, List<string>> Conflicts = new(StringComparer.Ordinal);
    public readonly Dictionary<string, List<string>> ModifierConflicts = new(StringComparer.Ordinal);
    public readonly HashSet<string> PostfixLookups = new(StringComparer.Ordinal);
    public readonly List<string> OrderSensitiveModifiers = new();
    public string? Prefix;

    public void Theme(string key, Def[] defs)
    {
        if (!Themes.TryGetValue(key, out var list))
        {
            list = new List<Def>();
            Themes[key] = list;
        }
        list.AddRange(defs);
    }

    public void Group(string id, Def[] defs)
    {
        foreach (var kv in Groups)
        {
            if (kv.Key == id)
            {
                kv.Value.AddRange(defs);
                return;
            }
        }
        Groups.Add(new KeyValuePair<string, List<Def>>(id, new List<Def>(defs)));
    }

    public void Conflict(string id, params string[] groups)
    {
        if (!Conflicts.TryGetValue(id, out var list))
        {
            list = new List<string>();
            Conflicts[id] = list;
        }
        list.AddRange(groups);
    }

    public void ModifierConflict(string id, params string[] groups)
    {
        if (!ModifierConflicts.TryGetValue(id, out var list))
        {
            list = new List<string>();
            ModifierConflicts[id] = list;
        }
        list.AddRange(groups);
    }

    public void PostfixLookup(params string[] groups)
    {
        foreach (var g in groups) PostfixLookups.Add(g);
    }

    public void OrderSensitive(params string[] modifiers) => OrderSensitiveModifiers.AddRange(modifiers);
}

/// <summary>tailwind-merge 的默认配置（数据见生成的 <c>DefaultMergeConfig.g.cs</c>）。</summary>
internal static partial class DefaultMergeConfig
{
    public static MergeConfigBuilder Create()
    {
        var c = new MergeConfigBuilder();
        Populate(c);
        return c;
    }

    static Def[] G(params Def[] defs) => defs;

    static Def O(params (string Key, Def[] Defs)[] entries) => Def.Object(entries);

    static Def T(string key) => Def.Theme(key);
}
