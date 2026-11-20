using System.Collections.Generic;
using AwAUI.Css.Syntax;

namespace AwAUI.Css.Values;

/// <summary>在组件值列表上前进的游标，自动跳过空白（只在需要感知空白处使用 <see cref="PeekRaw"/>）。</summary>
internal struct ValueReader
{
    readonly IReadOnlyList<CssComponentValue> _list;
    int _i;

    public ValueReader(IReadOnlyList<CssComponentValue> list)
    {
        _list = list;
        _i = 0;
    }

    /// <summary>当前位置，可保存后赋回以回溯。</summary>
    public int Position
    {
        readonly get => _i;
        set => _i = value;
    }

    public void SkipWhitespace()
    {
        while (_i < _list.Count && _list[_i] is CssToken { Type: CssTokenType.Whitespace }) _i++;
    }

    /// <summary>跳过空白后是否已到末尾。</summary>
    public bool AtEnd
    {
        get
        {
            SkipWhitespace();
            return _i >= _list.Count;
        }
    }

    /// <summary>跳过空白后查看下一个组件值。</summary>
    public CssComponentValue? Peek()
    {
        SkipWhitespace();
        return _i < _list.Count ? _list[_i] : null;
    }

    /// <summary>不跳过空白，查看当前位置的组件值。</summary>
    public readonly CssComponentValue? PeekRaw() => _i < _list.Count ? _list[_i] : null;

    /// <summary>跳过空白后取下一个组件值。</summary>
    public CssComponentValue? Next()
    {
        SkipWhitespace();
        return _i < _list.Count ? _list[_i++] : null;
    }

    /// <summary>按绝对下标取组件值（不移动位置）。</summary>
    public readonly CssComponentValue? PeekAt(int index) => index < _list.Count ? _list[index] : null;

    /// <summary>不跳过空白，前进一个组件值。</summary>
    public void Advance() => _i++;

    public bool TryComma()
    {
        if (Peek() is CssToken { Type: CssTokenType.Comma })
        {
            _i++;
            return true;
        }
        return false;
    }

    public bool TryDelim(char c)
    {
        if (Peek() is CssToken t && t.IsDelim(c))
        {
            _i++;
            return true;
        }
        return false;
    }

    /// <summary>下一个值是否为与 <paramref name="lower"/> 匹配（ASCII 大小写不敏感）的 ident，是则消费。</summary>
    public bool TryIdent(string lower)
    {
        if (Peek() is CssToken t && t.IsIdent(lower))
        {
            _i++;
            return true;
        }
        return false;
    }

    /// <summary>下一个值若为 ident，返回其 ASCII 小写形式（不消费）。</summary>
    public string? PeekIdentLower()
        => Peek() is CssToken { Type: CssTokenType.Ident } t ? CssSerializer.ToAsciiLower(t.Value) : null;
}
