using System;
using System.Diagnostics;
using System.Runtime.InteropServices.JavaScript;
using System.Runtime.Versioning;
using AwAUI;
using AwAUI.Graphics;
using AwAUI.Render;
using RenderDemo;

namespace WasmProbe;

/// <summary>
/// 浏览器宿主的 JSExport 边界。帧缓冲是 <see cref="Bitmap"/> 的非托管内存，即 WASM 线性内存中的一段；
/// JS 端直接在 HEAPU8 上建 Uint8ClampedArray 视图交给 putImageData，跨边界零拷贝（只有 putImageData 本身一次拷贝）。
/// </summary>
[SupportedOSPlatform("browser")]
public static partial class CanvasHost
{
    static readonly SoftwareRenderBackend Backend = new();
    static readonly DemoScene Scene = new();
    static BitmapRenderTarget? _target;
    static double _lastRecordMs;

    /// <summary>按设备像素尺寸（重新）创建帧缓冲，返回其在线性内存中的地址。</summary>
    [JSExport]
    public static int Resize(int pixelWidth, int pixelHeight, double scale)
    {
        _target?.Dispose();
        _target = (BitmapRenderTarget)Backend.CreateOffscreenTarget(new PixelSize(pixelWidth, pixelHeight), scale);
        return FramebufferAddress();
    }

    /// <summary>帧缓冲地址（RGBA8888，预乘；场景背景不透明，所以与 ImageData 期望的非预乘一致）。</summary>
    [JSExport]
    public static int FramebufferAddress() => _target is null ? 0 : (int)_target.Bitmap.Address;

    /// <summary>渲染一帧，返回录制 + 光栅化耗时（毫秒）。</summary>
    [JSExport]
    public static double RenderFrame(double timeSeconds)
    {
        if (_target is null) return 0;
        var t0 = Stopwatch.GetTimestamp();
        Scene.Time = timeSeconds;
        var logical = new Size((float)(_target.PixelSize.Width / _target.Scale), (float)(_target.PixelSize.Height / _target.Scale));
        var list = Scene.Build(logical);
        _lastRecordMs = (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;
        _target.Render(list, new RenderOptions(Color.Transparent));
        return (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;
    }

    /// <summary>上一帧中录制 DisplayList 的耗时（毫秒）。</summary>
    [JSExport]
    public static double LastRecordMs() => _lastRecordMs;

    /// <summary>指针事件（逻辑坐标）。kind：0 移动，1 按下，2 抬起，3 离开。</summary>
    [JSExport]
    public static void Pointer(int kind, double x, double y)
    {
        switch (kind)
        {
            case 3: Scene.Pointer = null; break;
            case 1: Scene.Pressed = true; Scene.Pointer = new Point((float)x, (float)y); break;
            case 2:
                if (Scene.Hovered == "primary") Scene.Clicks++;
                Scene.Pressed = false;
                break;
            default: Scene.Pointer = new Point((float)x, (float)y); break;
        }
        Scene.UpdateHover();
    }

    /// <summary>以固定时间渲染一帧场景到独立缓冲（用于与原生输出逐像素对照），返回地址。</summary>
    [JSExport]
    public static int RenderReference(int scale, double timeSeconds)
    {
        var target = Bench.RenderScene(scale, timeSeconds);
        _references[scale & 3]?.Dispose();
        _references[scale & 3] = target;
        return (int)target.Bitmap.Address;
    }

    static readonly BitmapRenderTarget?[] _references = new BitmapRenderTarget?[4];
}
