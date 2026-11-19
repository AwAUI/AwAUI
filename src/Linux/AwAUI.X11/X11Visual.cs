using System;
using AwAUI.Graphics;
using AwAUI.X11.Interop;
using static AwAUI.X11.Interop.Xlib;

namespace AwAUI.X11;

/// <summary>
/// 窗口使用的视觉（visual）与像素格式。优先 24/32 位 TrueColor；按通道掩码判断能否让渲染器直接写入
/// （<see cref="PixelFormat.Bgra8888"/> 或 <see cref="PixelFormat.Rgba8888"/>，32 bpp），否则呈现时逐像素转换。
/// </summary>
internal sealed unsafe class X11Visual
{
    public IntPtr Visual { get; private set; }
    public int Depth { get; private set; }
    public ulong Colormap { get; private set; }

    /// <summary>是否为了非默认视觉而创建了自己的颜色表（销毁时需释放）。</summary>
    public bool OwnsColormap { get; private set; }

    public ulong RedMask { get; private set; }
    public ulong GreenMask { get; private set; }
    public ulong BlueMask { get; private set; }

    /// <summary>服务器上该深度的 ZPixmap 每像素位数（16 / 24 / 32）。</summary>
    public int BitsPerPixel { get; private set; }

    /// <summary>渲染器可直接写入的格式；为 null 时需要转换（<see cref="Convert"/>）。</summary>
    public PixelFormat? DirectFormat { get; private set; }

    X11Visual() { }

    public static X11Visual Choose(IntPtr display, int screen, ulong root)
    {
        var v = new X11Visual();
        var defaultVisual = XDefaultVisual(display, screen);
        XVisualInfo info = default;
        bool found = false;

        // 1. 默认视觉若为 24/32 位 TrueColor 则直接使用（无需自建颜色表）
        if (TryGetInfo(display, screen, defaultVisual, out var def) && def.@class == TrueColor && (def.depth == 24 || def.depth == 32))
        {
            info = def;
            found = true;
        }
        // 2. 否则找一个 24 位 TrueColor 视觉
        else if (XMatchVisualInfo(display, screen, 24, TrueColor, &info) != 0)
        {
            found = true;
        }
        // 3. 退而求其次：默认视觉是低深度 TrueColor（15/16 位），呈现时转换
        else if (def.visual != IntPtr.Zero && def.@class == TrueColor && def.depth >= 15)
        {
            info = def;
            found = true;
        }
        if (!found)
            throw new PlatformNotSupportedException("AwAUI.X11 requires a TrueColor visual (depth 15, 16, 24 or 32); this X server offers none.");

        v.Visual = info.visual;
        v.Depth = info.depth;
        v.RedMask = info.red_mask;
        v.GreenMask = info.green_mask;
        v.BlueMask = info.blue_mask;
        if (info.visual == defaultVisual)
        {
            v.Colormap = XDefaultColormap(display, screen);
        }
        else
        {
            // 非默认视觉的窗口必须带同一视觉的颜色表，否则 XCreateWindow 报 BadMatch
            v.Colormap = XCreateColormap(display, root, info.visual, AllocNone);
            v.OwnsColormap = true;
        }

        v.BitsPerPixel = FindBitsPerPixel(display, v.Depth);
        if (v.BitsPerPixel != 16 && v.BitsPerPixel != 32)
            throw new PlatformNotSupportedException($"AwAUI.X11 does not support {v.BitsPerPixel} bits per pixel for depth {v.Depth}.");

        if (v.BitsPerPixel == 32 && v.GreenMask == 0x00FF00)
        {
            // 以小端 32 位字解释：字节 [B,G,R,A] → 0xAARRGGBB，红在 0xFF0000
            if (v.RedMask == 0xFF0000 && v.BlueMask == 0x0000FF) v.DirectFormat = PixelFormat.Bgra8888;
            else if (v.RedMask == 0x0000FF && v.BlueMask == 0xFF0000) v.DirectFormat = PixelFormat.Rgba8888;
        }
        return v;
    }

    /// <summary>释放为非默认视觉创建的颜色表。</summary>
    public void Free(IntPtr display)
    {
        if (!OwnsColormap || Colormap == 0) return;
        XFreeColormap(display, Colormap);
        Colormap = 0;
        OwnsColormap = false;
    }

    static bool TryGetInfo(IntPtr display, int screen, IntPtr visual, out XVisualInfo info)
    {
        info = default;
        if (visual == IntPtr.Zero) return false;
        var template = new XVisualInfo { visualid = XVisualIDFromVisual(visual), screen = screen };
        int count = 0;
        var list = XGetVisualInfo(display, VisualIDMask | VisualScreenMask, &template, &count);
        if (list == null) return false;
        if (count > 0) info = list[0];
        XFree(list);
        return count > 0;
    }

    static int FindBitsPerPixel(IntPtr display, int depth)
    {
        int count = 0;
        var formats = XListPixmapFormats(display, &count);
        int bpp = depth > 16 ? 32 : 16;
        if (formats == null) return bpp;
        for (int i = 0; i < count; i++)
            if (formats[i].depth == depth) { bpp = formats[i].bits_per_pixel; break; }
        XFree(formats);
        return bpp;
    }

    // ------------------------------------------------------------ 像素转换（非直写格式）

    /// <summary>
    /// 把 Bgra8888 源（预乘，alpha 直接丢弃，窗口不透明）转换为该视觉的像素，按 LSBFirst 字节序写入目标。
    /// 只在掩码不是标准 8 位通道（例如 16 位 565）时使用。
    /// </summary>
    public void Convert(byte* src, int srcRowBytes, byte* dst, int dstRowBytes, int width, int height)
    {
        Channel(RedMask, out int rs, out int rb);
        Channel(GreenMask, out int gs, out int gb);
        Channel(BlueMask, out int bs, out int bb);
        bool wide = BitsPerPixel == 32;
        for (int y = 0; y < height; y++)
        {
            byte* s = src + (long)y * srcRowBytes;
            byte* d = dst + (long)y * dstRowBytes;
            for (int x = 0; x < width; x++, s += 4)
            {
                uint p = (Scale(s[2], rb) << rs) | (Scale(s[1], gb) << gs) | (Scale(s[0], bb) << bs);
                if (wide)
                {
                    d[0] = (byte)p; d[1] = (byte)(p >> 8); d[2] = (byte)(p >> 16); d[3] = (byte)(p >> 24);
                    d += 4;
                }
                else
                {
                    d[0] = (byte)p; d[1] = (byte)(p >> 8);
                    d += 2;
                }
            }
        }
    }

    static uint Scale(byte value, int bits) => bits >= 8 ? (uint)value << (bits - 8) : (uint)value >> (8 - bits);

    static void Channel(ulong mask, out int shift, out int bits)
    {
        shift = 0;
        bits = 0;
        if (mask == 0) return;
        while ((mask & 1) == 0) { mask >>= 1; shift++; }
        while ((mask & 1) != 0) { mask >>= 1; bits++; }
    }
}
