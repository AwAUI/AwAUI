using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace AwAUI.Diagnostics;

/// <summary>
/// 对象 ↔ 会话内稳定整数 ID 的映射（design/06、design/20 的 NodeId 约定）。
/// 只持有弱引用，不延长对象寿命；ID 单调递增、从 1 开始，<b>永不复用</b>，对象被回收或显式移除后旧 ID 永远解析失败。
/// 线程安全。
/// </summary>
public sealed class ObjectRegistry
{
    sealed class IdBox
    {
        public readonly long Id;
        public IdBox(long id) => Id = id;
    }

    readonly object _gate = new();
    readonly ConditionalWeakTable<object, IdBox> _ids = new();
    readonly Dictionary<long, WeakReference> _objects = new();
    long _nextId;
    int _addsSincePurge;

    /// <summary>当前登记的条目数（包含已被回收但尚未清理的条目）。</summary>
    public int Count { get { lock (_gate) return _objects.Count; } }

    /// <summary>取对象的 ID，首次出现时分配新 ID。</summary>
    public long GetOrAddId(object obj)
    {
        if (obj is null) throw new ArgumentNullException(nameof(obj));
        lock (_gate)
        {
            if (_ids.TryGetValue(obj, out var box)) return box.Id;
            long id = ++_nextId;
            _ids.Add(obj, new IdBox(id));
            _objects[id] = new WeakReference(obj);
            if (++_addsSincePurge >= 1024 && _objects.Count >= 1024) PurgeLocked();
            return id;
        }
    }

    /// <summary>对象已有 ID 时返回 true；不会分配新 ID。</summary>
    public bool TryGetId(object obj, out long id)
    {
        if (obj is null) throw new ArgumentNullException(nameof(obj));
        lock (_gate)
        {
            if (_ids.TryGetValue(obj, out var box)) { id = box.Id; return true; }
        }
        id = 0;
        return false;
    }

    /// <summary>按 ID 取回对象；对象已回收或已移除时返回 false。</summary>
    public bool TryGetObject(long id, out object? obj)
    {
        lock (_gate)
        {
            if (_objects.TryGetValue(id, out var wr))
            {
                obj = wr.Target;
                if (obj != null) return true;
                _objects.Remove(id);
            }
        }
        obj = null;
        return false;
    }

    /// <summary>按 ID 取回指定类型的对象。</summary>
    public bool TryGetObject<T>(long id, out T? obj) where T : class
    {
        if (TryGetObject(id, out object? o) && o is T t) { obj = t; return true; }
        obj = null;
        return false;
    }

    /// <summary>显式移除（例如节点从树上移除时）。之后该对象若再登记，会得到一个新的 ID。</summary>
    public bool Remove(object obj)
    {
        if (obj is null) throw new ArgumentNullException(nameof(obj));
        lock (_gate)
        {
            if (!_ids.TryGetValue(obj, out var box)) return false;
            _ids.Remove(obj);
            _objects.Remove(box.Id);
            return true;
        }
    }

    /// <summary>清理已被回收对象的条目，返回清理数量。</summary>
    public int Purge()
    {
        lock (_gate) return PurgeLocked();
    }

    int PurgeLocked()
    {
        _addsSincePurge = 0;
        List<long>? dead = null;
        foreach (var kv in _objects)
            if (!kv.Value.IsAlive) (dead ??= new List<long>()).Add(kv.Key);
        if (dead is null) return 0;
        foreach (var id in dead) _objects.Remove(id);
        return dead.Count;
    }
}
