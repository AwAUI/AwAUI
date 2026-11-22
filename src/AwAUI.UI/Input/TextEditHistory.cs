using System;
using System.Collections.Generic;

namespace AwAUI.UI;

/// <summary>
/// 输入框的撤销 / 重做历史：保存每次编辑后的值、选区与插入点，连续输入或连续删除合并为一个编辑组。
/// <para>
/// 浏览器的行为：撤销恢复编辑前的值与选区，重做恢复编辑后的值；程序方式改写值（<see cref="InputElement.SetValue"/>）会清空历史。
/// 历史按元素保存，条目数与总长度都有上限（长文本上逐次编辑不会无界增长）。
/// </para>
/// </summary>
internal sealed class TextEditHistory
{
    /// <summary>编辑组的种类，用于合并连续编辑。</summary>
    internal enum EditKind : byte
    {
        /// <summary>在空选区插入文本（键入、输入法提交）。</summary>
        Insert,

        /// <summary>删除文本（退格、删除）。</summary>
        Delete,

        /// <summary>替换选区：总是单独成组。</summary>
        Other,
    }

    readonly struct Snapshot
    {
        public Snapshot(string text, int anchor, int focus)
        {
            Text = text;
            Anchor = anchor;
            Focus = focus;
        }

        public string Text { get; }

        public int Anchor { get; }

        public int Focus { get; }
    }

    /// <summary>保留的编辑组上限；更早的组被丢弃。</summary>
    public const int MaxEntries = 100;

    readonly List<Snapshot> _states = new();
    int _index;
    EditKind _lastKind = EditKind.Other;
    int _lastEditEnd = -1;

    public TextEditHistory(string text, int anchor, int focus) => Reset(text, anchor, focus);

    /// <summary>用新的初始状态清空历史（程序方式改写值）。</summary>
    public void Reset(string text, int anchor, int focus)
    {
        _states.Clear();
        _states.Add(new Snapshot(text, anchor, focus));
        _index = 0;
        _lastKind = EditKind.Other;
        _lastEditEnd = -1;
    }

    /// <summary>光标移动、点击或焦点变化后不再与上一次编辑合并。</summary>
    public void BreakCoalescing()
    {
        _lastKind = EditKind.Other;
        _lastEditEnd = -1;
    }

    /// <summary>
    /// 记录一次编辑：<paramref name="text"/> / <paramref name="anchor"/> / <paramref name="focus"/> 是编辑后的状态，
    /// <paramref name="caretBefore"/> / <paramref name="caretAfter"/> 是编辑前后的插入点。
    /// 插入点紧接上一次编辑（连续键入或连续删除）时合并到同一组。
    /// </summary>
    public void Record(string text, int anchor, int focus, EditKind kind, int caretBefore, int caretAfter)
    {
        bool merge = kind != EditKind.Other && _index == _states.Count - 1
            && kind == _lastKind && caretBefore == _lastEditEnd;
        if (merge)
        {
            _states[_index] = new Snapshot(text, anchor, focus);
        }
        else
        {
            if (_index < _states.Count - 1) _states.RemoveRange(_index + 1, _states.Count - _index - 1);
            _states.Add(new Snapshot(text, anchor, focus));
            _index = _states.Count - 1;
            if (_states.Count > MaxEntries)
            {
                _states.RemoveAt(0);
                _index--;
            }
        }
        _lastKind = kind;
        _lastEditEnd = caretAfter;
    }

    /// <summary>回到上一个编辑组的状态。</summary>
    public bool TryUndo(out string text, out int anchor, out int focus)
    {
        if (_index <= 0)
        {
            (text, anchor, focus) = ("", 0, 0);
            return false;
        }
        _index--;
        var state = _states[_index];
        (text, anchor, focus) = (state.Text, state.Anchor, state.Focus);
        BreakCoalescing();
        return true;
    }

    /// <summary>重新应用被撤销的编辑组。</summary>
    public bool TryRedo(out string text, out int anchor, out int focus)
    {
        if (_index >= _states.Count - 1)
        {
            (text, anchor, focus) = ("", 0, 0);
            return false;
        }
        _index++;
        var state = _states[_index];
        (text, anchor, focus) = (state.Text, state.Anchor, state.Focus);
        BreakCoalescing();
        return true;
    }

    /// <summary>是否有可撤销 / 可重做的编辑组（诊断与测试用）。</summary>
    public bool CanUndo => _index > 0;

    /// <inheritdoc cref="CanUndo"/>
    public bool CanRedo => _index < _states.Count - 1;
}
