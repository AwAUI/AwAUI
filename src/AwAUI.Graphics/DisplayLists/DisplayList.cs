using System;
using System.Collections.Generic;

namespace AwAUI.Graphics;

/// <summary>
/// 冻结后的绘制命令流（design/13）。不可变、可跨线程读取。
/// 由 <see cref="DrawingContext"/> 录制产生。
/// </summary>
public sealed class DisplayList
{
    /// <summary>格式版本：2 = 第九批起 PushLayer 追加滤镜字段、新增 DrawBackdrop（操作码 38），见 design/13。</summary>
    public const int FormatVersion = 2;

    readonly byte[] _commands;
    readonly object[] _resources;

    internal DisplayList(byte[] commands, object[] resources, Rect bounds, int commandCount)
    {
        _commands = commands;
        _resources = resources;
        Bounds = bounds;
        CommandCount = commandCount;
        ContentHash = ComputeHash(commands, resources);
    }

    /// <summary>保守包围盒（局部坐标），含描边、阴影外扩。</summary>
    public Rect Bounds { get; }
    public int CommandCount { get; }
    public bool IsEmpty => CommandCount == 0;

    /// <summary>
    /// 内容哈希：命令字节 + 资源标识（位图另含录制时的 <see cref="Bitmap.Version"/>）。用于缓存与 DevTools diff。
    /// 哈希在构造时求出：位图在录制之后再被改写（<see cref="Bitmap.Invalidate"/>，例如动画图片复用同一位图）不会改变它，
    /// 所以按 ContentHash 缓存渲染结果的代码（保留层等）还必须比较所引用位图的当前版本。
    /// </summary>
    public ulong ContentHash { get; }

    public ReadOnlySpan<byte> CommandBytes => _commands;
    public IReadOnlyList<object> Resources => _resources;

    public DisplayListReader GetReader() => new(_commands, _resources);

    public static DisplayList Empty { get; } = new(Array.Empty<byte>(), Array.Empty<object>(), Rect.Empty, 0);

    static ulong ComputeHash(byte[] bytes, object[] resources)
    {
        ulong h = 14695981039346656037UL;
        unchecked
        {
            foreach (var b in bytes) h = (h ^ b) * 1099511628211UL;
            foreach (var r in resources)
            {
                ulong v = r is DisplayList child ? child.ContentHash : (ulong)System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(r);
                // 位图按对象引用，像素在渲染时读取：录制时的内容版本也进入哈希（同一位图在不同版本下录制的列表哈希不同）
                if (r is Bitmap bitmap) v ^= (ulong)bitmap.Version * 0x9E3779B97F4A7C15UL;
                h = (h ^ v) * 1099511628211UL;
            }
        }
        return h;
    }
}
