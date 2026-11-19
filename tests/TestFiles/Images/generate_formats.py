#!/usr/bin/env python3
"""生成 GIF / WebP / BMP / ICO 解码测试图片及参考解码结果。

依赖：Python 3 + Pillow（自带 libwebp 1.6）+ numpy；Homebrew 的 libwebp 工具（cwebp / img2webp / gif2webp / webpmux）、
giflib（libgif.dylib，经 ctypes 调用）与 ffmpeg（带 libvpx）。用法：在仓库根目录运行
`python3 tests/TestFiles/Images/generate_formats.py [gif|webp|bmp|ico|perf ...]`（不带参数时全部重新生成）。

参考结果的来源：
- GIF：giflib 的 DGifSlurp 解出每帧索引，再按浏览器语义（画布初始透明、处置 2 清为透明、处置 3 恢复）
  在本脚本里合成；GIF 文件本身由本脚本自带的 LZW 编码器、Pillow 或 ffmpeg 写出。
- WebP：Pillow 内置 libwebp 的 WebPAnimDecoder（非预乘 RGBA，默认 fancy 上采样），静态图与 dwebp -pam 交叉核对。
- BMP：Pillow 解码（16 位通道 Pillow 用 v*255/max 向下取整，测试改用位复制公式，见 README）。
- ICO / CUR：Pillow 解码最大的一项。
参考图存为 *.ref.png（RGBA）；动画存为 *.frames.png（所有帧自上而下拼接）。
"""
import ctypes
import os
import struct
import subprocess
import sys
import tempfile

import numpy as np
from PIL import Image

ROOT = os.path.dirname(os.path.abspath(__file__))
GOLDEN = os.path.join(ROOT, "..", "Golden")
BIN = os.environ.get("WEBP_BIN", "/opt/homebrew/bin")
GIFLIB = os.environ.get("GIFLIB", "/opt/homebrew/lib/libgif.dylib")
TMP = tempfile.mkdtemp()
rng = np.random.default_rng(0)


def out(sub, name):
    d = os.path.join(ROOT, sub)
    os.makedirs(d, exist_ok=True)
    return os.path.join(d, name)


def golden(name, crop=None, size=None):
    im = Image.open(os.path.join(GOLDEN, name + ".png")).convert("RGBA")
    if crop:
        im = im.crop(crop)
    if size:
        im = im.resize(size, Image.LANCZOS)
    return im


def photo(box, size=None):
    im = Image.open(os.path.join(ROOT, "perf-1280x880.jpg")).convert("RGB").crop(box)
    if size:
        im = im.resize(size, Image.LANCZOS)
    return im


def save_ref(arr_or_img, path):
    im = arr_or_img if isinstance(arr_or_img, Image.Image) else Image.fromarray(arr_or_img, "RGBA")
    im.convert("RGBA").save(path, optimize=True)


def save_strip(frames, path):
    save_ref(np.concatenate(frames, axis=0), path)


def tmp(name):
    return os.path.join(TMP, name)


def run(*args):
    subprocess.run([str(a) for a in args], check=True, capture_output=True)


# ====================================================================== GIF


class BitWriter:
    def __init__(self):
        self.acc = 0
        self.n = 0
        self.out = bytearray()

    def write(self, code, width):
        self.acc |= code << self.n
        self.n += width
        while self.n >= 8:
            self.out.append(self.acc & 0xFF)
            self.acc >>= 8
            self.n -= 8

    def flush(self):
        if self.n:
            self.out.append(self.acc & 0xFF)
        self.acc = self.n = 0
        return bytes(self.out)


def lzw_encode(indices, min_size, clear_when_full=True, clear_interval=None, eoi=True):
    """GIF 变体的 LZW。按解码器的视角维护码宽，clear_when_full=False 时测试「延迟清表」。"""
    clear, end = 1 << min_size, (1 << min_size) + 1
    w = BitWriter()
    width = min_size + 1

    def reset():
        return {}, end + 1, min_size + 1, end + 1, 0

    table, enc_next, width, dec_next, emitted = reset()
    w.write(clear, width)
    prefix = None
    since_clear = 0
    for k in indices:
        if prefix is None:
            prefix = k
            continue
        key = (prefix, k)
        if key in table and not (clear_interval and since_clear >= clear_interval):
            prefix = table[key]
            continue
        # 输出 prefix
        w.write(prefix, width)
        emitted += 1
        since_clear += 1
        if emitted >= 2 and dec_next < 4096:
            dec_next += 1
            if dec_next == (1 << width) and width < 12:
                width += 1
        if enc_next < 4096:
            table[key] = enc_next
            enc_next += 1
        elif clear_when_full:
            w.write(clear, width)
            table, enc_next, width, dec_next, emitted = reset()
            since_clear = 0
        if clear_interval and since_clear >= clear_interval:
            w.write(clear, width)
            table, enc_next, width, dec_next, emitted = reset()
            since_clear = 0
        prefix = k
    if prefix is not None:
        w.write(prefix, width)
        emitted += 1
        if emitted >= 2 and dec_next < 4096:
            dec_next += 1
            if dec_next == (1 << width) and width < 12:
                width += 1
    if eoi:
        w.write(end, width)
    return w.flush()


def sub_blocks(data, block=255):
    res = bytearray()
    for i in range(0, len(data), block):
        chunk = data[i:i + block]
        res.append(len(chunk))
        res += chunk
    res.append(0)
    return bytes(res)


def color_table(colors, size_bits):
    n = 1 << (size_bits + 1)
    t = bytearray()
    for i in range(n):
        c = colors[i] if i < len(colors) else (0, 0, 0)
        t += bytes(c[:3])
    return bytes(t)


def interlace_order(h):
    rows = []
    for start, step in ((0, 8), (4, 8), (2, 4), (1, 2)):
        rows += list(range(start, h, step))
    return rows


def gif_file(width, height, frames, global_colors=None, loop=None, bg=0, version=b"GIF89a"):
    """frames: dict(x, y, w, h, idx (h×w uint8), colors=None(局部表), delay, disposal, transparent, interlace,
    min_size, clear_when_full, eoi)"""
    b = bytearray(version)
    packed = 0
    gbits = 0
    if global_colors:
        gbits = max(0, (len(global_colors) - 1).bit_length() - 1)
        packed = 0x80 | (7 << 4) | gbits
    b += struct.pack("<HHBBB", width, height, packed, bg, 0)
    if global_colors:
        b += color_table(global_colors, gbits)
    if loop is not None:
        b += b"\x21\xFF\x0BNETSCAPE2.0" + struct.pack("<BBHB", 3, 1, loop, 0)
    b += b"\x21\xFE" + sub_blocks(b"AwAUI test image")  # 注释扩展，解码器应跳过
    for f in frames:
        if f.get("gce", True):
            tflag = 1 if f.get("transparent") is not None else 0
            p = (f.get("disposal", 0) << 2) | tflag
            b += b"\x21\xF9\x04" + struct.pack("<BHB", p, f.get("delay", 10), f.get("transparent") or 0) + b"\x00"
        idx = np.asarray(f["idx"], dtype=np.uint8)
        h, w = idx.shape
        lbits = 0
        packed = 0
        if f.get("colors"):
            lbits = max(0, (len(f["colors"]) - 1).bit_length() - 1)
            packed = 0x80 | lbits
        if f.get("interlace"):
            packed |= 0x40
        b += b"\x2C" + struct.pack("<HHHHB", f["x"], f["y"], w, h, packed)
        if f.get("colors"):
            b += color_table(f["colors"], lbits)
        rows = interlace_order(h) if f.get("interlace") else range(h)
        data = [int(v) for r in rows for v in idx[r]]
        if "truncate" in f:
            data = data[:f["truncate"]]
        ms = f.get("min_size", max(2, int(max(data or [0])).bit_length()))
        b.append(ms)
        b += sub_blocks(lzw_encode(data, ms, f.get("clear_when_full", True), f.get("clear_interval"), f.get("eoi", True)))
    b += b"\x3B"
    return bytes(b)


class GifColorType(ctypes.Structure):
    _fields_ = [("Red", ctypes.c_ubyte), ("Green", ctypes.c_ubyte), ("Blue", ctypes.c_ubyte)]


class ColorMapObject(ctypes.Structure):
    _fields_ = [("ColorCount", ctypes.c_int), ("BitsPerPixel", ctypes.c_int), ("SortFlag", ctypes.c_bool),
                ("Colors", ctypes.POINTER(GifColorType))]


class GifImageDesc(ctypes.Structure):
    _fields_ = [("Left", ctypes.c_int), ("Top", ctypes.c_int), ("Width", ctypes.c_int), ("Height", ctypes.c_int),
                ("Interlace", ctypes.c_bool), ("ColorMap", ctypes.POINTER(ColorMapObject))]


class ExtensionBlock(ctypes.Structure):
    _fields_ = [("ByteCount", ctypes.c_int), ("Bytes", ctypes.POINTER(ctypes.c_ubyte)), ("Function", ctypes.c_int)]


class SavedImage(ctypes.Structure):
    _fields_ = [("ImageDesc", GifImageDesc), ("RasterBits", ctypes.POINTER(ctypes.c_ubyte)),
                ("ExtensionBlockCount", ctypes.c_int), ("ExtensionBlocks", ctypes.POINTER(ExtensionBlock))]


class GifFileType(ctypes.Structure):
    _fields_ = [("SWidth", ctypes.c_int), ("SHeight", ctypes.c_int), ("SColorResolution", ctypes.c_int),
                ("SBackGroundColor", ctypes.c_int), ("AspectByte", ctypes.c_ubyte),
                ("SColorMap", ctypes.POINTER(ColorMapObject)), ("ImageCount", ctypes.c_int),
                ("Image", GifImageDesc), ("SavedImages", ctypes.POINTER(SavedImage)),
                ("ExtensionBlockCount", ctypes.c_int), ("ExtensionBlocks", ctypes.POINTER(ExtensionBlock)),
                ("Error", ctypes.c_int), ("UserData", ctypes.c_void_p), ("Private", ctypes.c_void_p)]


class GraphicsControlBlock(ctypes.Structure):
    _fields_ = [("DisposalMode", ctypes.c_int), ("UserInputFlag", ctypes.c_bool), ("DelayTime", ctypes.c_int),
                ("TransparentColor", ctypes.c_int)]


_gif = None


def giflib():
    global _gif
    if _gif is None:
        _gif = ctypes.CDLL(GIFLIB)
        _gif.DGifOpenFileName.restype = ctypes.POINTER(GifFileType)
        _gif.DGifOpenFileName.argtypes = [ctypes.c_char_p, ctypes.POINTER(ctypes.c_int)]
        _gif.DGifSlurp.argtypes = [ctypes.POINTER(GifFileType)]
        _gif.DGifSavedExtensionToGCB.argtypes = [ctypes.POINTER(GifFileType), ctypes.c_int,
                                                 ctypes.POINTER(GraphicsControlBlock)]
        _gif.DGifCloseFile.argtypes = [ctypes.POINTER(GifFileType), ctypes.POINTER(ctypes.c_int)]
    return _gif


def gif_reference(path):
    """giflib 解出索引 + 浏览器语义合成。返回 (帧列表, 每帧 (延迟 cs, 处置))。"""
    lib = giflib()
    err = ctypes.c_int()
    g = lib.DGifOpenFileName(path.encode(), ctypes.byref(err))
    assert g, err.value
    if lib.DGifSlurp(g) != 1:
        raise RuntimeError(f"giflib failed on {path}: {g.contents.Error}")
    gf = g.contents
    W, H = gf.SWidth, gf.SHeight
    canvas = np.zeros((H, W, 4), np.uint8)
    frames, meta = [], []
    for i in range(gf.ImageCount):
        si = gf.SavedImages[i]
        d = si.ImageDesc
        gcb = GraphicsControlBlock(0, False, 0, -1)
        lib.DGifSavedExtensionToGCB(g, i, ctypes.byref(gcb))
        cmap = d.ColorMap if d.ColorMap else gf.SColorMap
        pal = np.zeros((256, 4), np.uint8)
        pal[:, 3] = 255
        for c in range(cmap.contents.ColorCount):
            col = cmap.contents.Colors[c]
            pal[c, :3] = (col.Red, col.Green, col.Blue)
        raster = np.ctypeslib.as_array(si.RasterBits, shape=(d.Height * d.Width,)).reshape(d.Height, d.Width).copy()
        x0, y0 = d.Left, d.Top
        x1, y1 = min(W, x0 + d.Width), min(H, y0 + d.Height)
        saved = canvas.copy()
        if x1 > x0 and y1 > y0:
            sub = raster[:y1 - y0, :x1 - x0]
            mask = np.ones(sub.shape, bool) if gcb.TransparentColor < 0 else (sub != gcb.TransparentColor)
            region = canvas[y0:y1, x0:x1]
            region[mask] = pal[sub[mask]]
        frames.append(canvas.copy())
        meta.append((gcb.DelayTime, gcb.DisposalMode))
        if gcb.DisposalMode == 2 and x1 > x0 and y1 > y0:
            canvas[y0:y1, x0:x1] = 0
        elif gcb.DisposalMode == 3:
            canvas = saved
    lib.DGifCloseFile(g, ctypes.byref(err))
    return frames, meta


def write_gif(name, data, strip=True):
    path = out("gif", name)
    with open(path, "wb") as f:
        f.write(data)
    finish_gif(path, strip)


def finish_gif(path, strip=True):
    frames, meta = gif_reference(path)
    base = path[:-4]
    if len(frames) == 1 or not strip:
        save_ref(frames[0], base + ".ref.png")
    else:
        save_strip(frames, base + ".frames.png")
    print(os.path.basename(path), os.path.getsize(path), "bytes", len(frames), "frames", meta)


def quantize(img, colors):
    im = img.convert("RGB").quantize(colors, method=Image.Quantize.MEDIANCUT, dither=Image.Dither.NONE)
    pal = im.getpalette()[:colors * 3]
    return np.asarray(im), [tuple(pal[i * 3:i * 3 + 3]) for i in range(len(pal) // 3)]


def gen_gif():
    global rng
    rng = np.random.default_rng(1)  # 每节独立的随机源，单独重跑某一节时结果不变
    # 1. 静态 8 位调色板（全局表），逐行与隔行
    idx, pal = quantize(photo((300, 200, 460, 320), (80, 60)), 256)
    write_gif("photo-256.gif", gif_file(80, 60, [dict(x=0, y=0, idx=idx, gce=False)], pal))
    write_gif("photo-256-interlaced.gif", gif_file(80, 60, [dict(x=0, y=0, idx=idx, interlace=True, gce=False)], pal))
    # 2. 小调色板与透明色，局部颜色表，GIF87a
    idx4, pal4 = quantize(golden("shapes", (0, 0, 120, 80)), 4)
    write_gif("shapes-4-transparent.gif", gif_file(120, 80, [dict(x=0, y=0, idx=idx4, colors=pal4, transparent=0)]))
    idx2 = (np.indices((13, 37)).sum(axis=0) % 3 == 0).astype(np.uint8)
    write_gif("bilevel-87a.gif", gif_file(37, 13, [dict(x=0, y=0, idx=idx2, gce=False)],
                                          [(255, 255, 255), (0, 0, 64)], version=b"GIF87a"))
    # 3. 延迟清表：表满 4096 后不发清表码继续编码
    big = (rng.integers(0, 2, (90, 120)) * 1 + (np.indices((90, 120))[1] // 30)).astype(np.uint8)
    pal8 = [(i * 30, 255 - i * 30, (i * 77) % 256) for i in range(8)]
    write_gif("deferred-clear.gif", gif_file(120, 90, [dict(x=0, y=0, idx=big, clear_when_full=False, min_size=3)], pal8))
    # 频繁清表 + 缺 EOI + 最小码长 8（只用到 2 种颜色）
    write_gif("frequent-clear-no-eoi.gif", gif_file(120, 90, [dict(x=0, y=0, idx=big, clear_interval=50, eoi=False,
                                                                   min_size=8)], pal8))
    # 4. 动画：三种处置方式、偏移、局部表、透明、循环次数
    W, H = 64, 48
    gpal = [(255, 255, 255), (255, 0, 0), (0, 160, 0), (0, 0, 255), (250, 200, 0), (0, 0, 0), (128, 0, 128),
            (0, 200, 200)]
    frames = []
    bgidx = np.full((H, W), 0, np.uint8)
    bgidx[::4, :] = 5
    frames.append(dict(x=0, y=0, idx=bgidx, delay=20, disposal=1))
    sq = np.full((16, 16), 1, np.uint8)
    sq[4:12, 4:12] = 7  # 7 = 透明
    frames.append(dict(x=5, y=6, idx=sq, delay=10, disposal=2, transparent=7))  # 处置 2：清为透明
    sq2 = np.full((20, 12), 2, np.uint8)
    frames.append(dict(x=30, y=10, idx=sq2, delay=5, disposal=3, interlace=True))  # 处置 3：恢复
    loc = np.indices((10, 30)).sum(axis=0).astype(np.uint8) % 4
    frames.append(dict(x=20, y=30, idx=loc, delay=0, disposal=0,
                       colors=[(10, 20, 30), (200, 100, 50), (60, 250, 120), (255, 255, 0)]))
    out_of_canvas = np.full((10, 10), 4, np.uint8)
    frames.append(dict(x=58, y=42, idx=out_of_canvas, delay=7, disposal=2))  # 超出画布部分被裁掉
    frames.append(dict(x=0, y=0, idx=np.full((3, 3), 3, np.uint8), delay=100, disposal=0, transparent=0))
    write_gif("anim-disposal.gif", gif_file(W, H, frames, gpal, loop=2))
    # 5. 截断：最后一帧的像素数据不完整（只给出一半像素）
    t = [dict(x=0, y=0, idx=bgidx, delay=10), dict(x=8, y=8, idx=np.full((20, 20), 2, np.uint8), truncate=150, delay=10)]
    path = out("gif", "anim-truncated-frame.gif")
    with open(path, "wb") as f:
        f.write(gif_file(W, H, t, gpal, loop=0))
    # giflib 拒绝不完整的帧，参考结果在这里直接合成：截断帧只画出前 150 个像素，其余保持不变
    pal = np.array([c + (255,) for c in gpal], np.uint8)
    f0 = pal[bgidx]
    f1 = f0.copy()
    flat = f1[8:28, 8:28].reshape(-1, 4)
    flat[:150] = pal[2]
    f1[8:28, 8:28] = flat.reshape(20, 20, 4)
    save_strip([f0, f1], path[:-4] + ".frames.png")
    print("anim-truncated-frame.gif", os.path.getsize(path))
    # 6. Pillow 与 ffmpeg 写出的动画
    pf = []
    for i in range(6):
        im = Image.new("RGB", (48, 32), (255, 255, 255))
        a = np.asarray(im).copy()
        a[4 + i * 3:14 + i * 3, 5 + i * 6:15 + i * 6] = (200, 30 * i, 90)
        a[20:28, :] = (0, 0, 40 * i)
        pf.append(Image.fromarray(a))
    p = out("gif", "pillow-anim.gif")
    pf[0].save(p, save_all=True, append_images=pf[1:], duration=[30, 40, 50, 60, 70, 80], loop=5, disposal=2,
               optimize=False)
    finish_gif(p)
    src = tmp("ff-src.mp4")
    run("ffmpeg", "-y", "-loglevel", "error", "-f", "lavfi", "-i", "testsrc2=size=64x40:rate=10", "-t", "0.8", src)
    p = out("gif", "ffmpeg-anim.gif")
    run("ffmpeg", "-y", "-loglevel", "error", "-i", src, "-vf",
        "split[a][b];[a]palettegen=max_colors=64:reserve_transparent=1[p];[b][p]paletteuse=diff_mode=rectangle",
        "-gifflags", "+transdiff+offsetting", p)
    finish_gif(p)


def rgba_sha256(frames):
    import hashlib
    h = hashlib.sha256()
    for f in frames:
        h.update(np.ascontiguousarray(f).tobytes())
    return h.hexdigest()


def gen_gif_perf():
    global rng
    rng = np.random.default_rng(5)  # 每节独立的随机源，单独重跑某一节时结果不变
    # 性能测试：480×320、24 帧、帧间只有部分区域变化（ffmpeg 只存变化的矩形）。参考结果只存 RGBA 的 SHA-256
    W, H, N = 480, 320, 24
    base = np.asarray(photo((100, 100, 580, 420)).convert("RGB"))
    for i in range(N):
        a = base.copy()
        cx = 40 + i * 16
        a[100:220, cx:cx + 80] = (255 - i * 8, 40, 30 + i * 9)
        Image.fromarray(a).save(tmp(f"perf{i:02d}.png"))
    p = out("gif", "perf-480x320-anim.gif")
    run("ffmpeg", "-y", "-loglevel", "error", "-framerate", "25", "-i", tmp("perf%02d.png"), "-vf",
        "split[a][b];[a]palettegen=stats_mode=full[p];[b][p]paletteuse=dither=none:diff_mode=rectangle",
        "-gifflags", "+transdiff+offsetting", p)
    frames, meta = gif_reference(p)
    with open(p[:-4] + ".sha256", "w") as f:
        f.write(rgba_sha256(frames) + "\n")
    print(os.path.basename(p), os.path.getsize(p), len(frames), "frames")


# ====================================================================== WebP


def pillow_webp_frames(path):
    im = Image.open(path)
    frames = []
    for i in range(getattr(im, "n_frames", 1)):
        im.seek(i)
        frames.append(np.asarray(im.convert("RGBA")).copy())
    return frames, im.info


def dwebp_rgba(path):
    data = subprocess.run([os.path.join(BIN, "dwebp"), "-quiet", "-pam", path, "-o", "-"], check=True,
                          capture_output=True).stdout
    end = data.index(b"ENDHDR\n") + 7
    fields = dict(line.split(b" ", 1) for line in data[:end].split(b"\n")[1:-2])
    w, h = int(fields[b"WIDTH"]), int(fields[b"HEIGHT"])
    return np.frombuffer(data[end:end + w * h * 4], np.uint8).reshape(h, w, 4)


def finish_webp(path):
    frames, info = pillow_webp_frames(path)
    base = path[:-5]
    if len(frames) == 1:
        ref = dwebp_rgba(path)
        assert (ref == frames[0]).all(), f"dwebp and Pillow disagree on {path}"
        save_ref(frames[0], base + ".ref.png")
    else:
        save_strip(frames, base + ".frames.png")
    print(os.path.basename(path), os.path.getsize(path), "bytes", len(frames), "frames",
          {k: v for k, v in info.items() if k in ("loop", "duration", "background")})


def src_png(img, name):
    p = tmp(name + ".png")
    img.save(p)
    return p


def cwebp(img, name, *args):
    p = out("webp", name)
    run(os.path.join(BIN, "cwebp"), "-quiet", *args, src_png(img, name), "-o", p)
    finish_webp(p)
    return p


def noise_rgba(w, h, alpha=True):
    a = rng.integers(0, 256, (h, w, 4), dtype=np.uint8)
    if not alpha:
        a[..., 3] = 255
    return Image.fromarray(a, "RGBA")


def gradient_rgba(w, h):
    y, x = np.indices((h, w))
    a = np.stack([(x * 255 // max(1, w - 1)), (y * 255 // max(1, h - 1)), ((x + y) * 3) % 256,
                  np.clip(255 - (x * 2), 0, 255)], axis=-1).astype(np.uint8)
    return Image.fromarray(a, "RGBA")


def with_alpha(img):
    """金样图都是不透明的：叠加一个水平渐变 + 两块全透明区域的 alpha。"""
    a = np.asarray(img.convert("RGBA")).copy()
    h, w = a.shape[:2]
    a[..., 3] = np.linspace(255, 20, w, dtype=np.float64)[None, :].astype(np.uint8)
    a[h // 3:h // 2, w // 5:w // 3, 3] = 0
    a[: h // 6, :, 3] = 255
    a[-h // 8:, -w // 4:, 3] = 0
    return Image.fromarray(a, "RGBA")


def alpha_filter(a, flt):
    """libwebp 的 alpha 预测滤波（编码方向）。"""
    h, w = a.shape
    res = np.zeros_like(a)
    for y in range(h):
        for x in range(w):
            if y == 0:
                pred = 0 if x == 0 else a[y, x - 1]
            elif x == 0:
                pred = a[y - 1, 0]
            elif flt == 1:
                pred = a[y, x - 1]
            elif flt == 2:
                pred = a[y - 1, x]
            else:
                pred = min(255, max(0, int(a[y, x - 1]) + int(a[y - 1, x]) - int(a[y - 1, x - 1])))
            res[y, x] = (int(a[y, x]) - int(pred)) % 256
    return res.astype(np.uint8)


def chunk(tag, payload):
    return tag + struct.pack("<I", len(payload)) + payload + (b"\0" if len(payload) % 2 else b"")


def webp_with_alpha(vp8_chunk, w, h, filtered, flt):
    vp8x = chunk(b"VP8X", bytes([0x10, 0, 0, 0]) + struct.pack("<I", w - 1)[:3] + struct.pack("<I", h - 1)[:3])
    alph = chunk(b"ALPH", bytes([flt << 2]) + filtered.tobytes())
    body = b"WEBP" + vp8x + alph + vp8_chunk
    return b"RIFF" + struct.pack("<I", len(body)) + body


def gen_webp():
    global rng
    rng = np.random.default_rng(2)  # 每节独立的随机源，单独重跑某一节时结果不变
    shapes = with_alpha(golden("shapes"))             # 240×160 RGBA
    ph = photo((500, 300, 661, 419))                  # 161×119：宽高都不是 16 的倍数
    # ---- 无损
    cwebp(shapes, "ll-shapes.webp", "-lossless")
    cwebp(shapes, "ll-shapes-exact-z9.webp", "-lossless", "-exact", "-z", "9")
    cwebp(ph, "ll-photo-m6.webp", "-lossless", "-m", "6", "-q", "100")
    cwebp(ph, "ll-photo-m0.webp", "-lossless", "-m", "0", "-q", "0")
    cwebp(ph, "ll-near-lossless.webp", "-lossless", "-near_lossless", "40")
    cwebp(noise_rgba(37, 29), "ll-noise-alpha.webp", "-lossless", "-exact")
    cwebp(gradient_rgba(200, 3), "ll-gradient-200x3.webp", "-lossless", "-exact")
    cwebp(noise_rgba(1, 1), "ll-1x1.webp", "-lossless", "-exact")
    for n in (2, 4, 16, 200):  # 调色板变换：每像素 1 / 2 / 4 / 8 位
        idx = rng.integers(0, n, (23, 45))
        pal = rng.integers(0, 256, (n, 4), dtype=np.uint8)
        pal[:, 3] = np.where(np.arange(n) % 3 == 0, 255, pal[:, 3])
        cwebp(Image.fromarray(pal[idx], "RGBA"), f"ll-palette-{n}.webp", "-lossless", "-exact")
    big_palette = golden("gradients")
    cwebp(big_palette, "ll-gradients-z9.webp", "-lossless", "-z", "9")
    # ---- 有损
    cwebp(ph, "lossy-q75.webp", "-q", "75")
    cwebp(ph, "lossy-q5-simple-filter.webp", "-q", "5", "-nostrong")
    cwebp(ph, "lossy-q95-sharp.webp", "-q", "95", "-sharpness", "7", "-f", "100", "-sharp_yuv")
    cwebp(ph, "lossy-nofilter-1seg.webp", "-q", "50", "-f", "0", "-segments", "1", "-sns", "0")
    cwebp(ph, "lossy-partitions.webp", "-q", "80", "-partition_limit", "100", "-segments", "4", "-sns", "100",
          "-pass", "3", "-m", "6")
    cwebp(photo((0, 0, 1280, 880), (33, 17)), "lossy-33x17.webp", "-q", "60")
    cwebp(photo((0, 0, 1280, 880), (1, 1)), "lossy-1x1.webp", "-q", "60")
    cwebp(gradient_rgba(64, 64).convert("RGB"), "lossy-gradient-q30.webp", "-q", "30", "-m", "0")
    # libvpx（经 ffmpeg）编码的 VP8 关键帧：多个 DCT 系数分区（-slices）、环路滤波的参考帧 / 模式增量
    for parts, q in ((4, 40), (8, 63)):
        src = src_png(photo((300, 100, 397, 175)), f"vpx{parts}")
        ivf = tmp(f"vpx{parts}.ivf")
        run("ffmpeg", "-y", "-loglevel", "error", "-i", src, "-c:v", "libvpx", "-slices", str(parts), "-qmin", str(q),
            "-qmax", str(q), "-b:v", "50k", "-sharpness", "3", "-frames:v", "1", "-f", "ivf", ivf)
        with open(ivf, "rb") as f:
            ivf_data = f.read()
        hdr_len = struct.unpack("<H", ivf_data[6:8])[0]
        size = struct.unpack("<I", ivf_data[hdr_len:hdr_len + 4])[0]
        frame = ivf_data[hdr_len + 12:hdr_len + 12 + size]
        body = b"WEBP" + chunk(b"VP8 ", frame)
        p = out("webp", f"lossy-libvpx-{parts}parts.webp")
        with open(p, "wb") as f:
            f.write(b"RIFF" + struct.pack("<I", len(body)) + body)
        finish_webp(p)
    # ---- 有损 + alpha（ALPH 块）
    cwebp(shapes, "lossy-alpha-lossless.webp", "-q", "70", "-alpha_method", "1", "-alpha_filter", "best")
    cwebp(shapes.crop((100, 40, 160, 80)), "lossy-alpha-raw.webp", "-q", "70", "-alpha_method", "0")
    cwebp(with_alpha(golden("box-shadows")), "lossy-alpha-q50.webp", "-q", "40", "-alpha_q", "50", "-alpha_filter", "fast")
    # alpha 滤波器：竖直（各列恒定）与梯度（行噪声 + 列噪声）由 -alpha_filter best 选中
    base = np.asarray(gradient_rgba(50, 31)).copy()
    cols = rng.integers(0, 120, 50)
    rows = rng.integers(0, 120, 31)
    base[..., 3] = cols[None, :]
    cwebp(Image.fromarray(base, "RGBA"), "lossy-alpha-filter-vertical.webp", "-q", "70", "-alpha_filter", "best")
    base[..., 3] = cols[None, :] + rows[:, None]
    cwebp(Image.fromarray(base, "RGBA"), "lossy-alpha-filter-gradient.webp", "-q", "70", "-alpha_filter", "best")
    # 手工构造 ALPH 块：不压缩（method 0）+ 水平 / 竖直 / 梯度滤波，参考结果仍由 libwebp 解出
    for flt, name in ((1, "horizontal"), (2, "vertical"), (3, "gradient")):
        src = gradient_rgba(45, 27)
        alpha = np.asarray(src)[..., 3].astype(np.int32)
        alpha = (alpha + rng.integers(0, 40, alpha.shape)) % 256
        tmpw = tmp("af.webp")
        run(os.path.join(BIN, "cwebp"), "-quiet", "-q", "70", src_png(src.convert("RGB"), "af"), "-o", tmpw)
        with open(tmpw, "rb") as f:
            vp8 = f.read()[12:]
        p = out("webp", f"lossy-alpha-raw-{name}.webp")
        with open(p, "wb") as f:
            f.write(webp_with_alpha(vp8, 45, 27, alpha_filter(alpha, flt), flt))
        finish_webp(p)
    # ---- 扩展容器：ICCP / EXIF / XMP 元数据块
    base = cwebp(ph.crop((0, 0, 40, 30)), "tmp-meta-base.webp", "-q", "80")
    icc = tmp("x.icc")
    with open(icc, "wb") as f:
        f.write(b"\0" * 128)
    exif = tmp("x.exif")
    with open(exif, "wb") as f:
        f.write(b"Exif\0\0MM\0*\0\0\0\x08\0\0")
    p1 = tmp("m1.webp")
    run(os.path.join(BIN, "webpmux"), "-set", "icc", icc, base, "-o", p1)
    p = out("webp", "lossy-vp8x-metadata.webp")
    run(os.path.join(BIN, "webpmux"), "-set", "exif", exif, p1, "-o", p)
    os.remove(base)
    for suffix in (".ref.png",):
        os.remove(base[:-5] + suffix)
    finish_webp(p)
    # ---- 动画
    frames = []
    for i in range(5):
        a = np.zeros((40, 56, 4), np.uint8)
        a[..., :] = (255, 255, 255, 255) if i == 0 else (0, 0, 0, 0)
        a[5 + i * 4:20 + i * 4, 3 + i * 8:18 + i * 8] = (200, 40 * i, 60, 255)
        a[25:35, 10:46, :] = (0, 120, 250, 90 + i * 30)  # 半透明，测试混合
        frames.append(src_png(Image.fromarray(a, "RGBA"), f"anim{i}"))
    p = out("webp", "anim-lossless.webp")
    run(os.path.join(BIN, "img2webp"), "-loop", "3", "-d", "80", *frames, "-o", p)
    finish_webp(p)
    p = out("webp", "anim-lossy.webp")
    run(os.path.join(BIN, "img2webp"), "-loop", "0", "-lossy", "-q", "60", "-d", "50", *frames, "-o", p)
    finish_webp(p)
    p = out("webp", "anim-mixed.webp")
    run(os.path.join(BIN, "img2webp"), "-mixed", "-d", "40", *frames, "-o", p)
    finish_webp(p)
    # webpmux 手工组帧：显式偏移、处置到背景、混合 / 不混合
    parts = []
    for i, (w, h, col) in enumerate([(56, 40, (255, 255, 255, 255)), (20, 16, (255, 0, 0, 128)),
                                     (30, 20, (0, 255, 0, 200)), (16, 16, (0, 0, 255, 60)),
                                     (24, 12, (40, 40, 40, 0))]):
        a = np.zeros((h, w, 4), np.uint8)
        a[...] = col
        a[h // 4:h // 2, w // 4:w // 2] = (col[2], col[0], col[1], 255)
        fp = tmp(f"mux{i}.webp")
        run(os.path.join(BIN, "cwebp"), "-quiet", "-lossless", "-exact", src_png(Image.fromarray(a, "RGBA"), f"mux{i}"),
            "-o", fp)
        parts.append(fp)
    p = out("webp", "anim-mux-dispose-blend.webp")
    run(os.path.join(BIN, "webpmux"),
        "-frame", parts[0], "+100+0+0+0-b",
        "-frame", parts[1], "+70+4+6+1+b",     # 处置到背景，混合
        "-frame", parts[2], "+60+10+8+0+b",    # 与上一帧处置后的画布混合
        "-frame", parts[3], "+50+30+20+1-b",   # 不混合（直接覆盖，含半透明）
        "-frame", parts[4], "+40+20+10+0+b",   # 全透明帧
        "-loop", "7", "-bgcolor", "255,10,20,30", "-o", p)
    finish_webp(p)
    gp = out("gif", "ffmpeg-anim.gif")
    p = out("webp", "anim-from-gif.webp")
    run(os.path.join(BIN, "gif2webp"), "-quiet", "-mixed", gp, "-o", p)
    finish_webp(p)


def gen_webp_perf():
    global rng
    rng = np.random.default_rng(6)  # 每节独立的随机源，单独重跑某一节时结果不变
    tiles = ["glyphs-paint@2x", "glyphs-sizes", "paragraph-align", "gradients", "box-shadows", "shapes@2x"]
    canvas = Image.new("RGBA", (1280, 880), (246, 247, 249, 255))
    x = y = 0
    rowh = 0
    for t in tiles * 2:
        im = golden(t)
        if x + im.width > 1280:
            x, y, rowh = 0, y + rowh, 0
        if y + im.height > 880:
            break
        canvas.alpha_composite(im, (x, y))
        x += im.width
        rowh = max(rowh, im.height)
    p = out("webp", "perf-1280x880-lossless.webp")
    run(os.path.join(BIN, "cwebp"), "-quiet", "-lossless", src_png(canvas, "perf-ui"), "-o", p)
    print(os.path.basename(p), os.path.getsize(p))
    p = out("webp", "perf-1280x880-lossy.webp")
    run(os.path.join(BIN, "cwebp"), "-quiet", "-q", "75", os.path.join(ROOT, "perf-1280x880.jpg"), "-o", p)
    print(os.path.basename(p), os.path.getsize(p))
    for name in ("perf-1280x880-lossless", "perf-1280x880-lossy"):
        p = out("webp", name + ".webp")
        frames, _ = pillow_webp_frames(p)
        with open(p[:-5] + ".sha256", "w") as f:
            f.write(rgba_sha256(frames) + "\n")


# ====================================================================== BMP


def bmp_file(w, h, bpp, pixels, palette=None, compression=0, header=40, masks=None, top_down=False,
             colors_used=None, alpha_mask=None):
    """pixels：已经按行打包好的像素数据（自下而上或 top_down）。"""
    pal = b""
    if palette is not None:
        for c in palette:
            pal += bytes([c[2], c[1], c[0]]) + (b"" if header == 12 else b"\0")
    hh = -h if top_down else h
    if header == 12:
        info = struct.pack("<IHHHH", 12, w, h, 1, bpp)
    else:
        info = struct.pack("<IiiHHIIiiII", header, w, hh, 1, bpp, compression, len(pixels), 2835, 2835,
                           colors_used if colors_used is not None else (len(palette) if palette else 0), 0)
        if header >= 52:
            info += struct.pack("<III", *masks)
        if header >= 56:
            info += struct.pack("<I", alpha_mask or 0)
        if header >= 108:
            info += b"BGRs" + b"\0" * 36 + b"\0" * 12
        if header >= 124:
            info += struct.pack("<IIII", 4, 0, 0, 0)
        assert len(info) == header, (len(info), header)
    extra = b""
    if header == 40 and masks is not None:
        extra = struct.pack("<III", *masks) + (struct.pack("<I", alpha_mask) if compression == 6 else b"")
    off = 14 + len(info) + len(extra) + len(pal)
    return b"BM" + struct.pack("<IHHI", off + len(pixels), 0, 0, off) + info + extra + pal + pixels


def pack_rows(rows, bpp, top_down=False):
    data = b""
    order = rows if top_down else rows[::-1]
    for r in order:
        if bpp >= 8:
            raw = bytes(r)
        else:
            per = 8 // bpp
            raw = bytearray()
            for i in range(0, len(r), per):
                v = 0
                for j in range(per):
                    v <<= bpp
                    if i + j < len(r):
                        v |= int(r[i + j])
                raw.append(v)
            raw = bytes(raw)
        data += raw + b"\0" * ((-len(raw)) % 4)
    return data


def rle8(rows, use_absolute=True):
    """BI_RLE8 编码（自下而上），含绝对模式与行尾、文件尾标记。"""
    out_b = bytearray()
    for r in rows[::-1]:
        r = [int(v) for v in r]
        i = 0
        while i < len(r):
            run_len = 1
            while i + run_len < len(r) and r[i + run_len] == r[i] and run_len < 255:
                run_len += 1
            if run_len >= 3 or not use_absolute:
                out_b += bytes([run_len, r[i]])
                i += run_len
            else:
                j = i
                while j < len(r) and j - i < 255:
                    if j + 2 < len(r) and r[j] == r[j + 1] == r[j + 2]:
                        break
                    j += 1
                n = j - i
                if n < 3:
                    for k in range(n):
                        out_b += bytes([1, r[i + k]])
                else:
                    out_b += bytes([0, n]) + bytes(r[i:j]) + (b"\0" if n % 2 else b"")
                i = j
        out_b += b"\0\0"
    out_b[-2:] = b"\0\1"
    return bytes(out_b)


def rle4(rows):
    out_b = bytearray()
    for r in rows[::-1]:
        r = [int(v) for v in r]
        i = 0
        while i < len(r):
            # 两色交替的游程：取 r[i], r[i+1] 交替出现的最长前缀
            a = r[i]
            b = r[i + 1] if i + 1 < len(r) else 0
            n = 1
            while i + n < len(r) and n < 255 and r[i + n] == (a if n % 2 == 0 else b):
                n += 1
            if n >= 4 or i + n >= len(r):
                out_b += bytes([n, (a << 4) | b])
                i += n
            else:
                m = min(len(r) - i, 12)
                nib = r[i:i + m] + [0] * (m % 2)
                bs = bytes((nib[k] << 4) | nib[k + 1] for k in range(0, len(nib), 2))
                out_b += bytes([0, m]) + bs + (b"\0" if len(bs) % 2 else b"")
                i += m
        out_b += b"\0\0"
    out_b[-2:] = b"\0\1"
    return bytes(out_b)


def finish_bmp(name, data, reference=True):
    p = out("bmp", name)
    with open(p, "wb") as f:
        f.write(data)
    if reference:
        im = Image.open(p)
        im.load()
        save_ref(im.convert("RGBA"), p[:-4] + ".ref.png")
    print(name, len(data), "bytes")


def gen_bmp():
    global rng
    rng = np.random.default_rng(3)  # 每节独立的随机源，单独重跑某一节时结果不变
    ph = np.asarray(photo((200, 400, 263, 441)))     # 63×41（行需补齐）
    h, w = ph.shape[:2]
    bgr = ph[..., ::-1]
    finish_bmp("rgb24.bmp", bmp_file(w, h, 24, pack_rows([r.reshape(-1) for r in bgr], 24)))
    finish_bmp("rgb24-topdown.bmp", bmp_file(w, h, 24, pack_rows([r.reshape(-1) for r in bgr], 24, True),
                                             top_down=True))
    x32 = np.concatenate([bgr, np.zeros((h, w, 1), np.uint8)], axis=2)
    finish_bmp("rgb32-x.bmp", bmp_file(w, h, 32, pack_rows([r.reshape(-1) for r in x32], 32)))
    # 32 位 BITFIELDS + alpha（V5 头）
    rgba = np.asarray(with_alpha(golden("shapes", (10, 10, 74, 58))))
    hh, ww = rgba.shape[:2]
    bgra = rgba[..., [2, 1, 0, 3]]
    finish_bmp("rgba32-v5.bmp", bmp_file(ww, hh, 32, pack_rows([r.reshape(-1) for r in bgra], 32), compression=3,
                                         header=124, masks=(0xFF0000, 0xFF00, 0xFF), alpha_mask=0xFF000000))
    # 32 位 BITFIELDS 不常见的掩码顺序（RGBA 字节序，V4 头）
    finish_bmp("rgba32-v4-swapped.bmp", bmp_file(ww, hh, 32, pack_rows([r.reshape(-1) for r in rgba], 32),
                                                 compression=3, header=108,
                                                 masks=(0xFF, 0xFF00, 0xFF0000), alpha_mask=0xFF000000), False)
    # 16 位 555 / 565
    p16 = ph.astype(np.uint16)
    v555 = ((p16[..., 0] >> 3) << 10) | ((p16[..., 1] >> 3) << 5) | (p16[..., 2] >> 3)
    finish_bmp("rgb555.bmp", bmp_file(w, h, 16, pack_rows([r.astype("<u2").tobytes() for r in v555], 8)), False)
    rep5 = lambda v: (v << 3) | (v >> 2)
    rep6 = lambda v: (v << 2) | (v >> 4)
    exp = np.stack([rep5(p16[..., 0] >> 3), rep5(p16[..., 1] >> 3), rep5(p16[..., 2] >> 3),
                    np.full(p16.shape[:2], 255)], axis=-1).astype(np.uint8)
    save_ref(exp, out("bmp", "rgb555.ref.png"))
    exp = np.stack([rep5(p16[..., 0] >> 3), rep6(p16[..., 1] >> 2), rep5(p16[..., 2] >> 3),
                    np.full(p16.shape[:2], 255)], axis=-1).astype(np.uint8)
    save_ref(exp, out("bmp", "rgb565.ref.png"))
    v565 = ((p16[..., 0] >> 3) << 11) | ((p16[..., 1] >> 2) << 5) | (p16[..., 2] >> 3)
    finish_bmp("rgb565.bmp", bmp_file(w, h, 16, pack_rows([r.astype("<u2").tobytes() for r in v565], 8),
                                      compression=3, masks=(0xF800, 0x07E0, 0x001F)), False)
    # 调色板：8 / 4 / 2 / 1 位
    for bits, n in ((8, 256), (4, 16), (1, 2)):
        idx, pal = quantize(Image.fromarray(ph), n)
        finish_bmp(f"pal{bits}.bmp", bmp_file(w, h, bits, pack_rows(list(idx), bits), palette=pal))
    idx, pal = quantize(Image.fromarray(ph), 7)
    finish_bmp("pal8-7colors.bmp", bmp_file(w, h, 8, pack_rows(list(idx), 8), palette=pal, colors_used=7))
    idx2, pal2 = quantize(Image.fromarray(ph), 4)
    finish_bmp("pal2.bmp", bmp_file(w, h, 2, pack_rows(list(idx2), 2), palette=pal2), False)
    p2 = np.array([c + (255,) for c in pal2], np.uint8)
    save_ref(p2[idx2], out("bmp", "pal2.ref.png"))
    # OS/2 1.x 核心头（12 字节，3 字节调色板项）
    idx, pal = quantize(Image.fromarray(ph), 16)
    finish_bmp("core-pal4.bmp", bmp_file(w, h, 4, pack_rows(list(idx), 4), palette=pal, header=12))
    # RLE8 / RLE4
    idx, pal = quantize(Image.fromarray(ph), 256)
    blocky = idx.copy()
    blocky[:, 20:50] = blocky[:, 20:21]
    finish_bmp("rle8.bmp", bmp_file(w, h, 8, rle8(list(blocky)), palette=pal, compression=1))
    idx, pal = quantize(Image.fromarray(ph), 16)
    blocky4 = idx.copy()
    blocky4[:, 30:60] = blocky4[:, 30:31]
    finish_bmp("rle4.bmp", bmp_file(w, h, 4, rle4(list(blocky4)), palette=pal, compression=2))
    # RLE8 增量跳转与提前结束：未写到的像素为透明（浏览器语义，Pillow 填 0 号颜色，不作参考）
    rows = bytearray()
    rows += bytes([4, 1]) + bytes([0, 2, 3, 2])     # 文件第 0 行：4 个 1 号色，然后增量跳到 (7, 2)
    rows += bytes([2, 2]) + b"\0\0"                  # 文件第 2 行：(7..8) = 2 号色，行尾
    rows += bytes([3, 3]) + b"\0\1"                  # 文件第 3 行：(0..2) = 3 号色，结束
    finish_bmp("rle8-delta.bmp", bmp_file(12, 5, 8, bytes(rows), palette=[(0, 0, 0), (255, 0, 0), (0, 255, 0),
                                                                         (0, 0, 255)], compression=1), False)
    exp = np.zeros((5, 12, 4), np.uint8)       # 自下而上：文件第 0 行是图像最后一行
    exp[4, 0:4] = (255, 0, 0, 255)
    exp[2, 7:9] = (0, 255, 0, 255)
    exp[1, 0:3] = (0, 0, 255, 255)
    save_ref(exp, out("bmp", "rle8-delta.ref.png"))


# ====================================================================== ICO / CUR


def ico_bmp_entry(rgba, bpp, palette=None, idx=None):
    """ICO 中的 BMP 项：BITMAPINFOHEADER（高度加倍）+ XOR 位图 + AND 掩码。"""
    h, w = rgba.shape[:2]
    if bpp == 32:
        xor = pack_rows([r[..., [2, 1, 0, 3]].reshape(-1) for r in rgba], 32)
    elif bpp == 24:
        xor = pack_rows([r[..., [2, 1, 0]].reshape(-1) for r in rgba], 24)
    else:
        xor = pack_rows(list(idx), bpp)
    andm = pack_rows([(r[..., 3] < 128).astype(np.uint8) for r in rgba], 1)
    pal = b""
    if palette is not None:
        for c in palette:
            pal += bytes([c[2], c[1], c[0], 0])
    info = struct.pack("<IiiHHIIiiII", 40, w, h * 2, 1, bpp, 0, len(xor) + len(andm), 0, 0,
                       len(palette) if palette else 0, 0)
    return info + pal + xor + andm


def ico_file(entries, kind=1):
    """entries: (w, h, bpp, colors, data, hotspot)"""
    head = struct.pack("<HHH", 0, kind, len(entries))
    off = 6 + 16 * len(entries)
    dirs = b""
    blobs = b""
    for w, h, bpp, colors, data, hot in entries:
        p1, p2 = (1, bpp) if kind == 1 else hot
        dirs += struct.pack("<BBBBHHII", w % 256, h % 256, colors % 256, 0, p1, p2, len(data), off + len(blobs))
        blobs += data
    return head + dirs + blobs


def gen_ico():
    global rng
    rng = np.random.default_rng(4)  # 每节独立的随机源，单独重跑某一节时结果不变
    base = with_alpha(golden("shapes", (0, 0, 160, 160)))
    entries = []
    refs = {}
    for size, bpp in ((16, 32), (32, 8), (48, 4), (24, 1), (20, 24)):
        rgba = np.asarray(base.resize((size, size), Image.LANCZOS)).copy()
        rgba[..., 3] = np.where(rgba[..., 3] >= 128, 255, 0) if bpp != 32 else rgba[..., 3]
        if bpp in (8, 4, 1):
            idx, pal = quantize(Image.fromarray(rgba[..., :3]), 1 << bpp)
            entries.append((size, size, bpp, len(pal) if bpp < 8 else 0, ico_bmp_entry(rgba, bpp, pal, idx), None))
        else:
            entries.append((size, size, bpp, 0, ico_bmp_entry(rgba, bpp), None))
    p = out("ico", "bmp-entries.ico")
    with open(p, "wb") as f:
        f.write(ico_file(entries))
    # 每一项单独存参考（用 Pillow 按尺寸取出）
    im = Image.open(p)
    for (w, h, bpp, *_rest) in entries:
        e = im.ico.getimage((w, h), bpp)
        save_ref(e.convert("RGBA"), p[:-4] + f".{w}x{h}-{bpp}.ref.png")
    print("bmp-entries.ico", os.path.getsize(p))
    # Pillow 写出的 PNG 项（含 256 尺寸，目录里记为 0）
    p = out("ico", "png-entries.ico")
    base.resize((256, 256)).save(p, sizes=[(16, 16), (32, 32), (256, 256)])
    im = Image.open(p)
    for s in (16, 32, 256):
        save_ref(im.ico.getimage((s, s)).convert("RGBA"), p[:-4] + f".{s}x{s}.ref.png")
    print("png-entries.ico", os.path.getsize(p))
    # CUR：两个 BMP 项与热点
    cur_entries = []
    for size, hot in ((32, (3, 4)), (16, (1, 2))):
        rgba = np.asarray(base.resize((size, size), Image.LANCZOS))
        cur_entries.append((size, size, 32, 0, ico_bmp_entry(rgba, 32), hot))
    p = out("ico", "pointer.cur")
    with open(p, "wb") as f:
        f.write(ico_file(cur_entries, kind=2))
    save_ref(np.asarray(base.resize((32, 32), Image.LANCZOS)), p[:-4] + ".32x32.ref.png")
    print("pointer.cur", os.path.getsize(p))


if __name__ == "__main__":
    which = sys.argv[1:] or ["gif", "webp", "bmp", "ico", "perf"]
    if "gif" in which:
        gen_gif()
    if "webp" in which:
        gen_webp()
    if "bmp" in which:
        gen_bmp()
    if "ico" in which:
        gen_ico()
    if "perf" in which:
        gen_gif_perf()
        gen_webp_perf()
