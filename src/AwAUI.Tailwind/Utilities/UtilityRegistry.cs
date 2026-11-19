using System;
using System.Collections.Generic;
using AwAUI.Tailwind.Ast;
using AwAUI.Tailwind.Candidates;

namespace AwAUI.Tailwind.Compilation;

internal enum UtilityKind : byte
{
    Static,
    Functional,
}

/// <summary>工具的生成函数：返回声明 / 嵌套规则；返回 null 表示该候选不由此工具处理。</summary>
internal delegate List<AstNode>? UtilityCompileFn(Candidate candidate);

internal sealed class UtilityEntry
{
    public UtilityKind Kind;
    public UtilityCompileFn CompileFn = null!;
}

/// <summary>工具注册表（移植自 Tailwind <c>Utilities</c>）。同一根可以同时注册静态与函数式实现。</summary>
internal sealed class UtilityRegistry
{
    readonly Dictionary<string, List<UtilityEntry>> _utilities = new(StringComparer.Ordinal);

    public void Static(string name, Func<List<AstNode>> compileFn)
    {
        GetOrCreate(name).Add(new UtilityEntry { Kind = UtilityKind.Static, CompileFn = _ => compileFn() });
    }

    public void Functional(string name, UtilityCompileFn compileFn)
    {
        GetOrCreate(name).Add(new UtilityEntry { Kind = UtilityKind.Functional, CompileFn = compileFn });
    }

    List<UtilityEntry> GetOrCreate(string name)
    {
        if (!_utilities.TryGetValue(name, out var list))
        {
            list = new List<UtilityEntry>(1);
            _utilities[name] = list;
        }
        return list;
    }

    public bool Has(string name, UtilityKind kind)
    {
        if (!_utilities.TryGetValue(name, out var list)) return false;
        foreach (var e in list)
        {
            if (e.Kind == kind) return true;
        }
        return false;
    }

    public List<UtilityEntry> Get(string name) => _utilities.TryGetValue(name, out var list) ? list : s_empty;

    public IEnumerable<string> Keys(UtilityKind kind)
    {
        foreach (var kv in _utilities)
        {
            foreach (var e in kv.Value)
            {
                if (e.Kind == kind)
                {
                    yield return kv.Key;
                    break;
                }
            }
        }
    }

    static readonly List<UtilityEntry> s_empty = new(0);
}
