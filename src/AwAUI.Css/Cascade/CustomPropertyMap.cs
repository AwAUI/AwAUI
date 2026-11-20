using System;
using System.Collections.Generic;
using AwAUI.Css.Syntax;
using AwAUI.Css.Values;

namespace AwAUI.Css.Cascade;

/// <summary>
/// 自定义属性（<c>--*</c>）的计算值映射：持久化结构，子元素共享父元素的映射并只记录差异（design/16）。
/// 值是已完成 <c>var()</c> 替换的组件值；被移除（guaranteed-invalid）的属性以 null 差异记录。
/// 链长超过上限时压平，查找开销有界。
/// </summary>
public sealed class CustomPropertyMap
{
    const int MaxChainDepth = 8;

    /// <summary>空映射（初始样式）。</summary>
    public static readonly CustomPropertyMap Empty = new(null, new Dictionary<string, CustomPropertyValue?>(StringComparer.Ordinal), 0);

    readonly CustomPropertyMap? _parent;
    readonly Dictionary<string, CustomPropertyValue?> _own;
    readonly int _depth;

    CustomPropertyMap(CustomPropertyMap? parent, Dictionary<string, CustomPropertyValue?> own, int depth)
    {
        _parent = parent;
        _own = own;
        _depth = depth;
    }

    /// <summary>
    /// 本元素上取了非初始值的「不继承」已注册属性（<c>@property … inherits: false</c>）：子元素的映射要把它们重置为初始值。
    /// 大多数元素为 null。
    /// </summary>
    internal string[]? NonInheritedNames { get; private set; }

    /// <summary>不带父链的映射（已注册属性的初始值等）。</summary>
    internal static CustomPropertyMap CreateFlat(Dictionary<string, CustomPropertyValue?> values)
        => values.Count == 0 ? Empty : new CustomPropertyMap(null, new Dictionary<string, CustomPropertyValue?>(values, StringComparer.Ordinal), 0);

    /// <summary>与 <see cref="Create"/> 相同，并记录本元素设置的不继承已注册属性。</summary>
    internal static CustomPropertyMap Create(CustomPropertyMap parent, Dictionary<string, CustomPropertyValue?> changes, string[]? nonInherited)
    {
        if (changes.Count == 0 && nonInherited is null) return parent;
        var m = changes.Count == 0 ? new CustomPropertyMap(parent, new Dictionary<string, CustomPropertyValue?>(StringComparer.Ordinal), parent._depth + 1) : Create(parent, changes);
        if (ReferenceEquals(m, parent)) m = new CustomPropertyMap(parent, new Dictionary<string, CustomPropertyValue?>(StringComparer.Ordinal), parent._depth + 1);
        m.NonInheritedNames = nonInherited;
        return m;
    }

    /// <summary>在 <paramref name="parent"/> 上应用差异（值为 null 表示移除）。差异为空时返回 parent 本身。</summary>
    internal static CustomPropertyMap Create(CustomPropertyMap parent, Dictionary<string, CustomPropertyValue?> changes)
    {
        if (changes.Count == 0) return parent;
        if (parent._depth + 1 < MaxChainDepth)
            return new CustomPropertyMap(parent, new Dictionary<string, CustomPropertyValue?>(changes, StringComparer.Ordinal), parent._depth + 1);
        // 压平：把整条链合并成一个字典
        var flat = new Dictionary<string, CustomPropertyValue?>(StringComparer.Ordinal);
        foreach (var kv in parent.Enumerate()) flat[kv.Key] = kv.Value;
        foreach (var kv in changes)
        {
            if (kv.Value is null) flat.Remove(kv.Key);
            else flat[kv.Key] = kv.Value;
        }
        return new CustomPropertyMap(null, flat, 0);
    }

    /// <summary>查找自定义属性的计算值；未定义或无效返回 false。</summary>
    public bool TryGetValue(string name, out CustomPropertyValue value)
    {
        for (var m = this; m is not null; m = m._parent)
        {
            if (m._own.TryGetValue(name, out var v))
            {
                value = v!;
                return v is not null;
            }
        }
        value = null!;
        return false;
    }

    /// <summary>取值的 CSS 文本；未定义返回 null。</summary>
    public string? GetText(string name) => TryGetValue(name, out var v) ? v.ToString() : null;

    /// <summary>全部有效的自定义属性（按名称排序，DevTools 的 getVariables）。</summary>
    public IReadOnlyList<KeyValuePair<string, CustomPropertyValue>> Enumerate()
    {
        var seen = new Dictionary<string, CustomPropertyValue?>(StringComparer.Ordinal);
        for (var m = this; m is not null; m = m._parent)
            foreach (var kv in m._own)
                if (!seen.ContainsKey(kv.Key)) seen.Add(kv.Key, kv.Value);
        var list = new List<KeyValuePair<string, CustomPropertyValue>>(seen.Count);
        foreach (var kv in seen)
            if (kv.Value is not null) list.Add(new KeyValuePair<string, CustomPropertyValue>(kv.Key, kv.Value));
        list.Sort((a, b) => string.CompareOrdinal(a.Key, b.Key));
        return list;
    }

    /// <summary>有效属性个数。</summary>
    public int Count => Enumerate().Count;
}

/// <summary>
/// <c>var()</c> / <c>env()</c> 替换（CSS Variables 1）：逐元素解析自定义属性的依赖，检测循环（循环中的属性都变为无效），
/// 并限制替换结果的规模与嵌套深度，防止「十亿笑声」式的指数膨胀与栈溢出。
/// </summary>
internal sealed class VariableResolver
{
    /// <summary>替换结果的最大组件值总数（递归计数）。</summary>
    public const int MaxTokens = 65536;

    /// <summary>替换过程（自定义属性引用链 + 组件嵌套）的最大递归深度。</summary>
    public const int MaxDepth = 512;

    /// <summary>
    /// 一个元素上全部替换（所有自定义属性与普通属性）合计的组件值上限：大量自定义属性各自接近
    /// <see cref="MaxTokens"/> 时，总内存也保持有界。超出后其余引用都按无效处理。
    /// </summary>
    public const int MaxTokensPerElement = 1 << 20;

    enum State : byte { Unresolved, InProgress, Resolved }

    sealed class Entry
    {
        public CssValue Declared = null!;
        public State State;
        public bool InCycle;
        public CustomPropertyValue? Result;
    }

    readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    readonly List<Entry> _stack = new();
    CustomPropertyMap _parent = CustomPropertyMap.Empty;
    CssEnvironment _environment = null!;
    CustomPropertyRegistry _registry = CustomPropertyRegistry.Empty;
    StyleComputer? _computer;
    int _depth;
    int _elementBudget;
    bool _ownMap;

    /// <summary>本元素的计算中是否用到了 env()。</summary>
    public bool UsedEnvironment { get; private set; }

    public void Begin(CustomPropertyMap parent, CssEnvironment environment) => Begin(parent, environment, CustomPropertyRegistry.Empty, null);

    /// <summary>
    /// 开始一个元素。已注册属性（<paramref name="registry"/>）：不继承的取初始值而不是父值；带类型的在替换后按语法校验并计算
    /// （长度换算用 <paramref name="computer"/> 当前的字体大小，见 <see cref="StyleEngine"/> 的计算顺序）。
    /// </summary>
    public void Begin(CustomPropertyMap parent, CssEnvironment environment, CustomPropertyRegistry registry, StyleComputer? computer)
        => Begin(parent, environment, registry, computer, ownMap: false);

    /// <summary>
    /// <paramref name="ownMap"/> 为 true 时 <paramref name="parent"/> 是元素自己已计算的映射（动画关键帧在元素的计算样式上求值）：
    /// 未声明的属性（含不继承的已注册属性）直接取映射中的值，不重置为初始值。
    /// </summary>
    public void Begin(CustomPropertyMap parent, CssEnvironment environment, CustomPropertyRegistry registry, StyleComputer? computer, bool ownMap)
    {
        _ownMap = ownMap;
        _entries.Clear();
        _stack.Clear();
        _parent = parent;
        _environment = environment;
        _registry = registry;
        _computer = computer;
        _depth = 0;
        _elementBudget = MaxTokensPerElement;
        UsedEnvironment = false;
    }

    /// <summary>登记本元素上胜出的自定义属性声明（值为 <see cref="CustomPropertyValue"/> 或全局关键字）。</summary>
    public void Declare(string name, CssValue value) => _entries[name] = new Entry { Declared = value };

    public bool HasDeclarations => _entries.Count > 0;

    /// <summary>是否需要构建新映射：有声明，或父元素设置了不继承的已注册属性（本元素要重置它们）。</summary>
    public bool NeedsMap => _entries.Count > 0 || (!_ownMap && _parent.NonInheritedNames is not null);

    /// <summary>解析全部声明并得到本元素的映射（无差异时与父映射为同一实例）。</summary>
    public CustomPropertyMap BuildMap(Action<string>? onInvalid = null)
    {
        if (!NeedsMap) return _parent;
        var changes = new Dictionary<string, CustomPropertyValue?>(StringComparer.Ordinal);
        List<string>? nonInherited = null;
        foreach (var kv in _entries)
        {
            var e = kv.Value;
            _registry.TryGet(kv.Key, out var reg);
            var v = Resolve(kv.Key, e);
            if (e.Declared is CssWideKeywordValue && reg is null)
            {
                // initial → guaranteed-invalid；inherit / unset（自定义属性可继承）与父值相同，无需记录
                if (((CssWideKeywordValue)e.Declared).Keyword == Values.CssWideKeyword.Initial && _parent.TryGetValue(kv.Key, out _)) changes[kv.Key] = null;
                continue;
            }
            if (v is null && e.Declared is not CssWideKeywordValue) onInvalid?.Invoke(kv.Key);
            if (reg is not null && !reg.Inherits && !ReferenceEquals(v, reg.InitialValue)) (nonInherited ??= new List<string>()).Add(kv.Key);
            if (v is null)
            {
                if (_parent.TryGetValue(kv.Key, out _)) changes[kv.Key] = null;
                continue;
            }
            if (_parent.TryGetValue(kv.Key, out var pv) && ReferenceEquals(pv, v)) continue;
            changes[kv.Key] = v;
        }
        // 父元素设置的不继承属性：本元素没有声明时回到初始值
        if (!_ownMap && _parent.NonInheritedNames is { } parentNames)
        {
            foreach (var name in parentNames)
            {
                if (_entries.ContainsKey(name) || !_registry.TryGet(name, out var reg)) continue;
                changes[name] = reg.InitialValue;
            }
        }
        return CustomPropertyMap.Create(_parent, changes, nonInherited?.ToArray());
    }

    CustomPropertyValue? Resolve(string name, Entry e)
    {
        switch (e.State)
        {
            case State.Resolved:
                return e.Result;
            case State.InProgress:
            {
                // 循环：栈上从该属性到栈顶的所有属性都在循环中
                for (int i = _stack.Count - 1; i >= 0; i--)
                {
                    _stack[i].InCycle = true;
                    if (ReferenceEquals(_stack[i], e)) break;
                }
                return null;
            }
        }
        _registry.TryGet(name, out var reg);
        if (e.Declared is not CustomPropertyValue cv)
        {
            e.State = State.Resolved;
            var keyword = ((CssWideKeywordValue)e.Declared).Keyword;
            if (reg is not null)
            {
                // 已注册属性：initial 取初始值；unset / revert 类按是否继承
                bool inherit = keyword == Values.CssWideKeyword.Inherit || (keyword != Values.CssWideKeyword.Initial && reg.Inherits);
                e.Result = inherit ? ParentValue(name) : reg.InitialValue;
                return e.Result;
            }
            e.Result = keyword == Values.CssWideKeyword.Initial ? null
                : _parent.TryGetValue(name, out var inherited) ? inherited : null;
            return e.Result;
        }
        if (!cv.HasReferences)
        {
            e.State = State.Resolved;
            e.Result = reg is null || reg.Syntax.IsUniversal ? cv : Typed(cv, reg);
            return e.Result;
        }
        if (_depth >= MaxDepth) return null;
        e.State = State.InProgress;
        _stack.Add(e);
        _depth++;
        int budget = MaxTokens;
        var list = Substitute(cv.Tokens, ref budget);
        _depth--;
        _stack.RemoveAt(_stack.Count - 1);
        e.State = State.Resolved;
        e.Result = e.InCycle || list is null ? null : new CustomPropertyValue(list, hasReferences: false);
        if (reg is not null)
        {
            // 计算值时无效的已注册属性按 unset 处理：继承的取父值，不继承的取初始值
            e.Result = e.Result is null ? (reg.Inherits ? ParentValue(name) : reg.InitialValue)
                : reg.Syntax.IsUniversal ? e.Result : Typed(e.Result, reg);
        }
        return e.Result;
    }

    /// <summary>父元素上的值（父映射中没有时为初始值）。</summary>
    CustomPropertyValue? ParentValue(string name)
        => _parent.TryGetValue(name, out var v) ? v : _registry.TryGet(name, out var reg) ? reg.InitialValue : null;

    /// <summary>按注册的语法校验并计算；不符合时按 unset 处理。</summary>
    CustomPropertyValue? Typed(CustomPropertyValue v, RegisteredCustomProperty reg)
    {
        var ctx = _computer?.CurrentContext ?? new LengthContext(16, 16, _environment);
        var computed = reg.Syntax.Compute(v.Tokens, ctx);
        if (computed is null) return reg.Inherits ? ParentValue(reg.Name) : reg.InitialValue;
        return ReferenceEquals(computed, v.Tokens) ? v : new CustomPropertyValue(computed, hasReferences: false);
    }

    /// <summary>查找 <c>var(--name)</c> 的值：本元素声明的先解析，否则取继承来的映射。</summary>
    CustomPropertyValue? Lookup(string name)
    {
        if (_entries.TryGetValue(name, out var e)) return Resolve(name, e);
        // 不继承的已注册属性：本元素没有声明时为初始值
        if (_registry.TryGet(name, out var reg) && !reg.Inherits && !_ownMap) return reg.InitialValue;
        return _parent.TryGetValue(name, out var v) ? v : reg?.InitialValue;
    }

    /// <summary>替换组件值列表中的全部 var()/env()；任何引用无效且无回退时返回 null（计算值时无效）。</summary>
    public IReadOnlyList<CssComponentValue>? Substitute(IReadOnlyList<CssComponentValue> list, ref int budget)
    {
        if (++_depth > MaxDepth)
        {
            _depth--;
            return null;
        }
        try
        {
            var result = new List<CssComponentValue>(list.Count);
            for (int i = 0; i < list.Count; i++)
            {
                if (--budget < 0 || --_elementBudget < 0) return null;
                var c = list[i];
                switch (c)
                {
                    case CssFunction f when Values.PropertyParsers.IsReferenceFunction(f.Name):
                    {
                        if (!SubstituteReference(f, result, ref budget)) return null;
                        break;
                    }
                    case CssFunction f:
                    {
                        var args = Substitute(f.Arguments, ref budget);
                        if (args is null) return null;
                        result.Add(ReferenceEquals(args, f.Arguments) ? f : new CssFunction(f.Name, args, f.Range, f.IsClosed));
                        break;
                    }
                    case CssSimpleBlock b:
                    {
                        var vals = Substitute(b.Values, ref budget);
                        if (vals is null) return null;
                        result.Add(new CssSimpleBlock(b.Open, vals, b.Range, b.IsClosed));
                        break;
                    }
                    default:
                        result.Add(c);
                        break;
                }
            }
            return result;
        }
        finally
        {
            _depth--;
        }
    }

    bool SubstituteReference(CssFunction f, List<CssComponentValue> result, ref int budget)
    {
        // 参数：名称 [, 回退]
        var args = f.Arguments;
        int i = 0;
        while (i < args.Count && args[i] is CssToken { Type: CssTokenType.Whitespace }) i++;
        if (i >= args.Count || args[i] is not CssToken { Type: CssTokenType.Ident } nameTok) return false;
        i++;
        while (i < args.Count && args[i] is CssToken { Type: CssTokenType.Whitespace }) i++;
        List<CssComponentValue>? fallback = null;
        if (i < args.Count)
        {
            if (args[i] is not CssToken { Type: CssTokenType.Comma }) return false;
            fallback = new List<CssComponentValue>();
            for (int k = i + 1; k < args.Count; k++) fallback.Add(args[k]);
            Trim(fallback);
        }
        bool isVar = Syntax.CssSerializer.EqualsIgnoreAsciiCase(f.Name, "var");
        if (isVar)
        {
            var v = Lookup(nameTok.Value);
            if (v is not null)
            {
                // 依赖图包含回退中的引用（CSS Variables 1 §2.3）：回退未被使用也要参与循环检测
                if (fallback is not null && _stack.Count > 0) VisitFallbackReferences(fallback, 0);
                budget -= v.Weight;
                _elementBudget -= v.Weight;
                if (budget < 0 || _elementBudget < 0) return false;
                result.AddRange(v.Tokens);
                return true;
            }
        }
        else
        {
            UsedEnvironment = true;
            if (_environment.TryGetEnvironmentVariable(Syntax.CssSerializer.ToAsciiLower(nameTok.Value), out var px))
            {
                result.Add(CssToken.CreateDimension(px, "px"));
                return true;
            }
        }
        if (fallback is null) return false;
        var sub = Substitute(fallback, ref budget);
        if (sub is null) return false;
        result.AddRange(sub);
        return true;
    }

    /// <summary>
    /// 解析未使用的回退中引用的、本元素声明的自定义属性（结果不用），使经由回退形成的循环被检测到。
    /// 每个属性的解析结果都被缓存，因此总开销与声明的规模成线性关系。
    /// </summary>
    void VisitFallbackReferences(IReadOnlyList<CssComponentValue> list, int depth)
    {
        if (depth > MaxDepth) return;
        for (int i = 0; i < list.Count; i++)
        {
            switch (list[i])
            {
                case CssFunction f:
                    if (Syntax.CssSerializer.EqualsIgnoreAsciiCase(f.Name, "var"))
                    {
                        var r = new ValueReader(f.Arguments);
                        if (r.Next() is CssToken { Type: CssTokenType.Ident } n && _entries.TryGetValue(n.Value, out var e)) Resolve(n.Value, e);
                    }
                    VisitFallbackReferences(f.Arguments, depth + 1);
                    break;
                case CssSimpleBlock b:
                    VisitFallbackReferences(b.Values, depth + 1);
                    break;
            }
        }
    }

    static void Trim(List<CssComponentValue> list)
    {
        while (list.Count > 0 && list[list.Count - 1] is CssToken { Type: CssTokenType.Whitespace }) list.RemoveAt(list.Count - 1);
        int n = 0;
        while (n < list.Count && list[n] is CssToken { Type: CssTokenType.Whitespace }) n++;
        if (n > 0) list.RemoveRange(0, n);
    }
}
