using System.Runtime.CompilerServices;
using AwAUI.Graphics;
using AwAUI.Graphics.Commands;

namespace AwAUI.Render.Raster;

/// <summary>
/// 图层隔离的判定（软件后端与 GPU 后端共用）。<see cref="DrawOp.PushLayer"/> 开启一个隔离组（W3C Compositing「isolated group」）：
/// 组内内容先画在透明的离屏表面上，再以组的不透明度与混合模式合成回父表面。
/// <para>
/// 不透明度为 1 且以 SrcOver 合成的组，只有当组内（不进入更深的隔离组）全部以 SrcOver 绘制时才等价于直接画在父表面上，
/// 后端可以跳过离屏。组内一旦有读目标的混合模式（DstIn、DstOut、Clear、Src、Multiply……，包括子图层以非 SrcOver 合成），
/// 它必须作用于组自己的表面，而不是父表面上已有的内容，此时必须隔离。
/// </para>
/// <para>
/// 判定按 DisplayList 预先扫描一遍（只读命令头与绘制参数），结果按 DisplayList 对象缓存（不可变、可跨线程读取）。
/// 嵌套的 DisplayList 按其顶层是否含有非 SrcOver 的绘制参与判定，同样缓存，所以共享的子列表只扫描一次。
/// </para>
/// </summary>
internal static class LayerIsolation
{
    /// <summary>递归分析嵌套 DisplayList 的最大深度；更深时保守地认为需要隔离。</summary>
    const int MaxDepth = 256;

    /// <summary>一个 DisplayList 的分析结果。</summary>
    internal sealed class Summary
    {
        public Summary(bool[] needsIsolation, bool topLevelReadsDestination)
        {
            NeedsIsolation = needsIsolation;
            TopLevelReadsDestination = topLevelReadsDestination;
        }

        /// <summary>按出现顺序，每个 PushLayer 的组内是否有非 SrcOver 的绘制（直接子级，含子图层自身的合成模式）。</summary>
        public bool[] NeedsIsolation { get; }

        /// <summary>不在任何图层内的命令中是否有非 SrcOver 的绘制（嵌入它的组因此需要隔离）。</summary>
        public bool TopLevelReadsDestination { get; }

        /// <summary>第 <paramref name="index"/> 个 PushLayer 是否需要隔离（越界时保守地返回 true）。</summary>
        public bool Needs(int index) => (uint)index >= (uint)NeedsIsolation.Length || NeedsIsolation[index];
    }

    static readonly ConditionalWeakTable<DisplayList, Summary> s_cache = new();
    static readonly Summary s_conservative = new(System.Array.Empty<bool>(), true);

    public static Summary Analyze(DisplayList list) => Analyze(list, 0);

    static Summary Analyze(DisplayList list, int depth)
    {
        if (s_cache.TryGetValue(list, out var cached)) return cached;
        if (depth >= MaxDepth) return s_conservative;
        var summary = Scan(list, depth);
        try
        {
            s_cache.Add(list, summary);
        }
        catch (System.ArgumentException)
        {
            // 另一个线程同时完成了同一个列表的分析：结果相同，用已有的
            if (s_cache.TryGetValue(list, out cached)) return cached;
        }
        return summary;
    }

    /// <summary>后端实际使用的混合模式：不支持的非可分离模式按 SrcOver 绘制（并另行上报降级）。</summary>
    static bool ReadsDestination(BlendMode mode) => mode != BlendMode.SrcOver && Blender.IsSupported(mode);

    static Summary Scan(DisplayList list, int depth)
    {
        var needs = new System.Collections.Generic.List<bool>();
        // 打开的图层在 needs 中的下标；栈空表示顶层
        var open = new System.Collections.Generic.Stack<int>();
        bool topLevel = false;
        var reader = list.GetReader();

        void Mark()
        {
            if (open.Count > 0) needs[open.Peek()] = true;
            else topLevel = true;
        }

        while (reader.MoveNext())
        {
            BlendMode mode;
            switch (reader.Op)
            {
                case DrawOp.PushLayer:
                {
                    var c = reader.Read<PushLayerCmd>();
                    if (ReadsDestination(c.BlendMode)) Mark();
                    open.Push(needs.Count);
                    needs.Add(false);
                    continue;
                }
                case DrawOp.PopLayer:
                    if (open.Count > 0) open.Pop();
                    continue;
                case DrawOp.DrawDisplayList:
                {
                    var child = reader.GetResource<DisplayList>(reader.Read<DrawDisplayListCmd>().DisplayListRef);
                    if (Analyze(child, depth + 1).TopLevelReadsDestination) Mark();
                    continue;
                }
                case DrawOp.FillRect: mode = reader.Read<FillRectCmd>().Paint.BlendMode; break;
                case DrawOp.FillRoundRect: mode = reader.Read<FillRoundRectCmd>().Paint.BlendMode; break;
                case DrawOp.StrokeRect: mode = reader.Read<StrokeRectCmd>().Paint.BlendMode; break;
                case DrawOp.StrokeRoundRect: mode = reader.Read<StrokeRoundRectCmd>().Paint.BlendMode; break;
                case DrawOp.DrawLine: mode = reader.Read<DrawLineCmd>().Paint.BlendMode; break;
                case DrawOp.FillPath: mode = reader.Read<FillPathCmd>().Paint.BlendMode; break;
                case DrawOp.StrokePath: mode = reader.Read<StrokePathCmd>().Paint.BlendMode; break;
                case DrawOp.DrawGlyphRun: mode = reader.Read<DrawGlyphRunCmd>().Paint.BlendMode; break;
                default: continue;   // 状态命令、盒阴影（总是 SrcOver）、注释与未知命令
            }
            if (ReadsDestination(mode)) Mark();
        }
        return new Summary(needs.ToArray(), topLevel);
    }
}
