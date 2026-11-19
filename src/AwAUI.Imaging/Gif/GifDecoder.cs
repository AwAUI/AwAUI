using System;
using System.Collections.Generic;
using System.IO;

namespace AwAUI.Imaging.Gif;

/// <summary>GIF 中一帧（图像描述符 + 之前的图形控制扩展）。</summary>
internal sealed class GifFrame
{
    public int X, Y, Width, Height;
    public bool Interlaced;
    /// <summary>颜色表在文件中的偏移与项数（局部表优先，否则全局表；没有则为 0 项）。</summary>
    public int PaletteOffset, PaletteCount;
    public int TransparentIndex = -1;
    public int DelayCentiseconds;
    public int Disposal;
    public int LzwMinCodeSize;
    /// <summary>第一个数据子块长度字节的偏移。</summary>
    public int DataOffset;
    /// <summary>LZW 数据的字节数（不含子块长度字节），用于工作量估算。</summary>
    public int DataLength;
}

/// <summary>
/// GIF 容器解析。语义按浏览器：画布为逻辑屏幕与第一帧范围的并（与 Chrome 一致）、画布初始全透明、
/// 处置方式 2 清为透明、处置方式 3 恢复、4–7 视为 0；越界的帧被裁剪。
/// 文件在帧数据中途截断时保留已解出的像素；缺少结束符或在帧之间截断时，已完整解析的帧照常使用。
/// </summary>
internal static class GifParser
{
    public static bool HasSignature(ReadOnlySpan<byte> h) =>
        h.Length >= 6 && h[0] == 'G' && h[1] == 'I' && h[2] == 'F' && h[3] == '8' && (h[4] == '7' || h[4] == '9') && h[5] == 'a';

    public static AnimatedImage Parse(byte[] data)
    {
        if (!HasSignature(data)) throw new InvalidDataException("Not a GIF file.");
        if (data.Length < 13) throw new InvalidDataException("Truncated GIF header.");
        int screenW = data[6] | data[7] << 8, screenH = data[8] | data[9] << 8;
        int packed = data[10];
        int pos = 13;
        int globalOffset = 0, globalCount = 0;
        if ((packed & 0x80) != 0)
        {
            globalCount = 2 << (packed & 7);
            globalOffset = pos;
            pos += globalCount * 3;
            if (pos > data.Length) throw new InvalidDataException("Truncated GIF global color table.");
        }

        var frames = new List<GifFrame>();
        int loopCount = 1;
        int transparent = -1, delay = 0, disposal = 0;
        long work = 0;
        while (pos < data.Length)
        {
            int introducer = data[pos++];
            if (introducer == 0x3B) break;                 // 结束符
            if (introducer == 0x21)
            {
                if (pos >= data.Length) break;
                int label = data[pos++];
                if (label == 0xF9 && pos < data.Length && data[pos] >= 4 && pos + 4 < data.Length)
                {
                    int p = data[pos + 1];
                    disposal = (p >> 2) & 7;
                    if (disposal > 3) disposal = 0;
                    delay = data[pos + 2] | data[pos + 3] << 8;
                    transparent = (p & 1) != 0 ? data[pos + 4] : -1;
                }
                else if (label == 0xFF && pos + 16 <= data.Length && data[pos] == 11
                         && (Matches(data, pos + 1, "NETSCAPE2.0") || Matches(data, pos + 1, "ANIMEXTS1.0"))
                         && data[pos + 12] >= 3 && data[pos + 13] == 1)
                {
                    int n = data[pos + 14] | data[pos + 15] << 8;
                    loopCount = n == 0 ? 0 : n + 1;
                }
                if (!SkipSubBlocks(data, ref pos, out _)) break;
                continue;
            }
            if (introducer != 0x2C) break;                 // 未知块：其后的内容无法解析，按文件结束处理

            if (pos + 9 > data.Length) break;
            var f = new GifFrame
            {
                X = data[pos] | data[pos + 1] << 8,
                Y = data[pos + 2] | data[pos + 3] << 8,
                Width = data[pos + 4] | data[pos + 5] << 8,
                Height = data[pos + 6] | data[pos + 7] << 8,
                TransparentIndex = transparent,
                DelayCentiseconds = delay,
                Disposal = disposal,
            };
            int ip = data[pos + 8];
            f.Interlaced = (ip & 0x40) != 0;
            pos += 9;
            if ((ip & 0x80) != 0)
            {
                f.PaletteCount = 2 << (ip & 7);
                f.PaletteOffset = pos;
                pos += f.PaletteCount * 3;
                if (pos > data.Length) break;
            }
            else
            {
                f.PaletteOffset = globalOffset;
                f.PaletteCount = globalCount;
            }
            if (pos >= data.Length) break;
            f.LzwMinCodeSize = data[pos++];
            f.DataOffset = pos;
            bool complete = SkipSubBlocks(data, ref pos, out f.DataLength);
            if (f.LzwMinCodeSize < 1 || f.LzwMinCodeSize > 8)
                throw new InvalidDataException($"Invalid GIF LZW minimum code size {f.LzwMinCodeSize}.");
            if (f.PaletteCount == 0) throw new InvalidDataException("GIF frame has no color table.");
            frames.Add(f);
            transparent = -1;
            delay = 0;
            disposal = 0;
            if (!complete) break;
        }
        if (frames.Count == 0) throw new InvalidDataException("GIF contains no image.");

        // 与 Chrome（Skia / Wuffs）一致：画布扩大到能容纳第一帧（逻辑屏幕为 0 或小于第一帧的常见错误文件），
        // 之后各帧超出画布的部分被裁剪
        screenW = Math.Max(1, Math.Max(screenW, frames[0].X + frames[0].Width));
        screenH = Math.Max(1, Math.Max(screenH, frames[0].Y + frames[0].Height));
        DecodeLimits.CheckDimensions(screenW, screenH, "GIF");
        // 工作量：画布 + 各帧裁剪后的面积（处置、保存恢复区域都与之成正比）+ LZW 产出的像素（受数据量限制）
        work = (long)screenW * screenH;
        var infos = new AnimationFrame[frames.Count];
        for (int i = 0; i < frames.Count; i++)
        {
            var f = frames[i];
            var bounds = new PixelRect(f.X, f.Y, f.Width, f.Height);
            var clipped = bounds.Intersect(new PixelRect(0, 0, screenW, screenH));
            work += 2L * clipped.Width * clipped.Height;
            var disp = f.Disposal switch { 2 => FrameDisposal.Background, 3 => FrameDisposal.Previous, _ => FrameDisposal.None };
            infos[i] = new AnimationFrame(i, bounds, TimeSpan.FromMilliseconds(f.DelayCentiseconds * 10), disp, FrameBlending.Over);
        }
        DecodeLimits.CheckBudget(work, data.Length, "GIF");
        return new AnimatedImage(new PixelSize(screenW, screenH), loopCount, infos, new GifCompositor(data, frames, new PixelSize(screenW, screenH)));
    }

    static bool Matches(byte[] data, int pos, string text)
    {
        for (int i = 0; i < text.Length; i++)
            if (data[pos + i] != text[i]) return false;
        return true;
    }

    /// <summary>跳过一串数据子块（含长度为 0 的终止块）。返回 false 表示数据在中途截断。</summary>
    static bool SkipSubBlocks(byte[] data, ref int pos, out int payload)
    {
        payload = 0;
        while (pos < data.Length)
        {
            int n = data[pos++];
            if (n == 0) return true;
            if (pos + n > data.Length)
            {
                payload += data.Length - pos;
                pos = data.Length;
                return false;
            }
            payload += n;
            pos += n;
        }
        return false;
    }
}

/// <summary>GIF 帧的合成：处置在下一帧开始时生效；LZW 解出的像素按行直接写入画布（裁剪到画布范围）。</summary>
internal sealed class GifCompositor : AnimationCompositor
{
    readonly byte[] _data;
    readonly List<GifFrame> _frames;
    readonly LzwDecoder _lzw = new();
    readonly uint[] _palette = new uint[256];
    byte[] _saved = Array.Empty<byte>();
    PixelRect _savedRect;

    public GifCompositor(byte[] data, List<GifFrame> frames, PixelSize size) : base(size)
    {
        _data = data;
        _frames = frames;
    }

    public override void Reset() => Canvas.AsSpan().Clear();

    PixelRect Clip(GifFrame f) => new PixelRect(f.X, f.Y, f.Width, f.Height).Intersect(new PixelRect(0, 0, CanvasSize.Width, CanvasSize.Height));

    public override void Compose(int index)
    {
        var canvas = Canvas;
        int stride = CanvasSize.Width * 4;
        if (index > 0)
        {
            var prev = _frames[index - 1];
            var r = Clip(prev);
            if (prev.Disposal == 2)
            {
                for (int y = r.Y; y < r.Bottom; y++) canvas.AsSpan(y * stride + r.X * 4, r.Width * 4).Clear();
            }
            else if (prev.Disposal == 3 && !_savedRect.IsEmpty)
            {
                for (int y = 0; y < _savedRect.Height; y++)
                    _saved.AsSpan(y * _savedRect.Width * 4, _savedRect.Width * 4)
                        .CopyTo(canvas.AsSpan((_savedRect.Y + y) * stride + _savedRect.X * 4));
            }
        }

        var f = _frames[index];
        var clip = Clip(f);
        _savedRect = default;
        if (f.Disposal == 3 && !clip.IsEmpty)
        {
            int n = clip.Width * clip.Height * 4;
            if (_saved.Length < n) _saved = new byte[n];
            for (int y = 0; y < clip.Height; y++)
                canvas.AsSpan((clip.Y + y) * stride + clip.X * 4, clip.Width * 4).CopyTo(_saved.AsSpan(y * clip.Width * 4));
            _savedRect = clip;
        }
        if (f.Width == 0 || f.Height == 0) return;

        // 调色板：表外的索引为不透明黑色
        for (int i = 0; i < 256; i++)
        {
            if (i < f.PaletteCount)
            {
                int o = f.PaletteOffset + i * 3;
                _palette[i] = (uint)(_data[o] | _data[o + 1] << 8 | _data[o + 2] << 16 | 0xFF << 24);
            }
            else _palette[i] = 0xFF000000;
        }
        _lzw.Decode(_data, f, canvas, CanvasSize, _palette);
    }
}

/// <summary>
/// GIF 变体的 LZW 解码（最多 4096 项、码宽至多 12 位、支持延迟清表）。每个码字最多产出 4095 个像素，
/// 输出总数截止于帧面积，因此耗时与数据量和帧面积成线性关系。无效码字抛出 <see cref="InvalidDataException"/>；
/// 数据提前结束（缺少 EOI、子块截断）时保留已解出的像素。
/// </summary>
internal sealed class LzwDecoder
{
    const int MaxCodes = 4096;
    readonly ushort[] _prefix = new ushort[MaxCodes];
    readonly byte[] _suffix = new byte[MaxCodes];
    readonly ushort[] _length = new ushort[MaxCodes];
    readonly byte[] _first = new byte[MaxCodes];
    readonly byte[] _string = new byte[MaxCodes];
    byte[] _row = Array.Empty<byte>();

    // 输出状态
    byte[] _canvas = Array.Empty<byte>();
    uint[] _palette = Array.Empty<uint>();
    GifFrame _frame = null!;
    int _canvasW, _canvasH, _x, _rowIndex, _pass, _y;
    bool _done;

    public void Decode(byte[] data, GifFrame frame, byte[] canvas, PixelSize canvasSize, uint[] palette)
    {
        try
        {
            DecodeCore(data, frame, canvas, canvasSize, palette);
        }
        finally
        {
            // 数据提前结束：已解出的半行也要画出
            if (!_done && _x > 0) WriteRow(_x);
        }
    }

    void DecodeCore(byte[] data, GifFrame frame, byte[] canvas, PixelSize canvasSize, uint[] palette)
    {
        _canvas = canvas;
        _palette = palette;
        _frame = frame;
        _canvasW = canvasSize.Width;
        _canvasH = canvasSize.Height;
        if (_row.Length < frame.Width) _row = new byte[frame.Width];
        _x = 0;
        _rowIndex = 0;
        _pass = 0;
        _y = 0;
        _done = false;

        int minSize = frame.LzwMinCodeSize;
        int clear = 1 << minSize, eoi = clear + 1;
        for (int i = 0; i < clear; i++)
        {
            _suffix[i] = (byte)i;
            _first[i] = (byte)i;
            _length[i] = 1;
        }
        int codeSize = minSize + 1, next = eoi + 1, prev = -1;

        // 子块位读取
        int pos = frame.DataOffset, blockEnd = pos;
        uint bits = 0;
        int nbits = 0;
        while (!_done)
        {
            while (nbits < codeSize)
            {
                if (pos >= blockEnd)
                {
                    if (pos >= data.Length) return;
                    int n = data[pos++];
                    if (n == 0) return;
                    blockEnd = Math.Min(data.Length, pos + n);
                    if (pos >= blockEnd) return;
                }
                bits |= (uint)data[pos++] << nbits;
                nbits += 8;
            }
            int code = (int)(bits & ((1u << codeSize) - 1));
            bits >>= codeSize;
            nbits -= codeSize;

            if (code == clear)
            {
                codeSize = minSize + 1;
                next = eoi + 1;
                prev = -1;
                continue;
            }
            if (code == eoi) return;
            if (prev < 0)
            {
                if (code >= clear) throw new InvalidDataException("Invalid GIF LZW code.");
                Emit(code);
                prev = code;
                continue;
            }
            int firstChar;
            if (code < next)
            {
                firstChar = _first[code];
                Emit(code);
            }
            else if (code == next && next < MaxCodes)
            {
                firstChar = _first[prev];
            }
            else throw new InvalidDataException("Invalid GIF LZW code.");

            if (next < MaxCodes)
            {
                _prefix[next] = (ushort)prev;
                _suffix[next] = (byte)firstChar;
                _first[next] = _first[prev];
                _length[next] = (ushort)(_length[prev] + 1);
                next++;
                if (code == next - 1) Emit(code);           // KwKwK：新加入的项就是本码字
                if (next == 1 << codeSize && codeSize < 12) codeSize++;
            }
            prev = code;
        }
    }

    /// <summary>输出码字对应的串。</summary>
    void Emit(int code)
    {
        int len = _length[code];
        int c = code;
        for (int i = len - 1; i >= 0; i--)
        {
            _string[i] = _suffix[c];
            c = _prefix[c];
        }
        int w = _frame.Width;
        int s = 0;
        while (s < len && !_done)
        {
            int n = Math.Min(len - s, w - _x);
            Buffer.BlockCopy(_string, s, _row, _x, n);
            _x += n;
            s += n;
            if (_x == w) FlushRow();
        }
    }

    void FlushRow()
    {
        var f = _frame;
        WriteRow(f.Width);
        _x = 0;
        _rowIndex++;
        if (_rowIndex >= f.Height)
        {
            _done = true;
            return;
        }
        if (!f.Interlaced)
        {
            _y = _rowIndex;
            return;
        }
        // 隔行扫描：四遍分别从第 0/4/2/1 行开始，步长 8/8/4/2
        _y += _pass switch { 0 or 1 => 8, 2 => 4, _ => 2 };
        while (_y >= f.Height && _pass < 3)
        {
            _pass++;
            _y = _pass switch { 1 => 4, 2 => 2, _ => 1 };
        }
    }

    /// <summary>把行缓冲的前 <paramref name="count"/> 个像素写入画布（裁剪到画布，跳过透明色）。</summary>
    void WriteRow(int count)
    {
        var f = _frame;
        int y = f.Y + _y;
        if (y < _canvasH)
        {
            int x0 = f.X, x1 = Math.Min(_canvasW, f.X + count);
            int t = f.TransparentIndex;
            int o = (y * _canvasW + x0) * 4;
            var canvas = _canvas;
            var palette = _palette;
            for (int x = x0, i = 0; x < x1; x++, i++, o += 4)
            {
                int idx = _row[i];
                if (idx == t) continue;
                uint c = palette[idx];
                canvas[o] = (byte)c;
                canvas[o + 1] = (byte)(c >> 8);
                canvas[o + 2] = (byte)(c >> 16);
                canvas[o + 3] = (byte)(c >> 24);
            }
        }
    }
}
