#!/usr/bin/env python3
"""生成 JPEG 解码测试图片及参考解码结果。

依赖：Python 3 + Pillow + numpy，libjpeg-turbo 3.x 的 cjpeg / djpeg / libturbojpeg（Homebrew）。
参考解码统一用 `djpeg -dct int`（islow，默认平滑上采样），结果保存为 *.ref.png（RGB 或灰度）。
用法：在仓库根目录运行 `python3 tests/TestFiles/Images/generate.py`（全部重新生成）
或 `python3 tests/TestFiles/Images/generate.py edge`（只生成小尺寸边界用例）。
"""
import ctypes
import io
import os
import subprocess
import sys
import tempfile

import numpy as np
from PIL import Image

ROOT = os.path.dirname(os.path.abspath(__file__))
GOLDEN = os.path.join(ROOT, "..", "Golden")
BIN = os.environ.get("JPEG_BIN", "/opt/homebrew/bin")
LIB = os.environ.get("TURBOJPEG_LIB", "/opt/homebrew/lib/libturbojpeg.dylib")
TMP = tempfile.mkdtemp()


def golden_rgb(name, crop=None):
    im = Image.open(os.path.join(GOLDEN, name + ".png")).convert("RGBA")
    bg = Image.new("RGBA", im.size, (255, 255, 255, 255))
    im = Image.alpha_composite(bg, im).convert("RGB")
    if crop:
        im = im.crop(crop)
    return im


def cjpeg(src_img, out_name, *args):
    src = os.path.join(TMP, out_name + ".ppm")
    src_img.save(src)
    out = os.path.join(ROOT, out_name)
    subprocess.run([os.path.join(BIN, "cjpeg"), *args, "-outfile", out, src], check=True)
    return out


def reference(jpg_name):
    """用 djpeg（islow）解码，存为 PNG。"""
    jpg = os.path.join(ROOT, jpg_name)
    data = subprocess.run([os.path.join(BIN, "djpeg"), "-dct", "int", "-pnm", jpg], check=True, capture_output=True).stdout
    im = Image.open(io.BytesIO(data))
    im.save(os.path.join(ROOT, jpg_name[:-4] + ".ref.png"), optimize=True)


def make_ycck(src_img, out_name, quality=85):
    """经 TurboJPEG 以 TJPF_CMYK 压缩，得到 YCCK（Adobe transform=2）的 JPEG。"""
    tj = ctypes.CDLL(LIB)
    tj.tjInitCompress.restype = ctypes.c_void_p
    tj.tjCompress2.argtypes = [ctypes.c_void_p, ctypes.c_char_p, ctypes.c_int, ctypes.c_int, ctypes.c_int, ctypes.c_int,
                               ctypes.POINTER(ctypes.c_void_p), ctypes.POINTER(ctypes.c_ulong), ctypes.c_int, ctypes.c_int, ctypes.c_int]
    tj.tjFree.argtypes = [ctypes.c_void_p]
    tj.tjDestroy.argtypes = [ctypes.c_void_p]
    rgb = np.asarray(src_img, dtype=np.float64)
    k = rgb.max(axis=2)  # Adobe 反相存储：K' = 255 - 墨量
    safe = np.where(k == 0, 1, k)
    cmy = np.where(k[..., None] == 0, 0, rgb * 255.0 / safe[..., None])
    cmyk = np.concatenate([cmy, k[..., None]], axis=2).round().clip(0, 255).astype(np.uint8)
    h, w = cmyk.shape[:2]
    handle = tj.tjInitCompress()
    buf = ctypes.c_void_p()
    size = ctypes.c_ulong(0)
    TJPF_CMYK, TJSAMP_420 = 11, 2
    rc = tj.tjCompress2(handle, cmyk.tobytes(), w, w * 4, h, TJPF_CMYK, ctypes.byref(buf), ctypes.byref(size), TJSAMP_420, quality, 0)
    if rc != 0:
        raise RuntimeError("tjCompress2 failed")
    data = ctypes.string_at(buf, size.value)
    tj.tjFree(buf)
    tj.tjDestroy(handle)
    with open(os.path.join(ROOT, out_name), "wb") as f:
        f.write(data)


def perf_image(w=1280, h=880):
    """程序化生成的「类照片」图：多倍频平滑噪声 + 黄金图片拼贴，避免提交第三方照片。"""
    rng = np.random.default_rng(1234)
    acc = np.zeros((h, w, 3))
    for octave, amp in ((8, 1.0), (32, 0.5), (128, 0.25), (320, 0.12)):
        small = rng.random((octave * h // w + 2, octave + 2, 3))
        layer = Image.fromarray((small * 255).astype(np.uint8)).resize((w, h), Image.BICUBIC)
        acc += np.asarray(layer, dtype=np.float64) * amp
    acc = (acc / 1.87).clip(0, 255).astype(np.uint8)
    canvas = Image.fromarray(acc)
    x = y = 0
    for name in ("shapes@2x", "gradients", "strokes", "transforms", "box-shadows", "clip-layers", "blend-modes", "fill-rules"):
        tile = golden_rgb(name)
        if x + tile.width > w:
            x, y = 0, y + 330
        canvas.paste(tile, (x + 20, y + 20))
        x += tile.width + 20
    return canvas


def main():
    shapes = golden_rgb("shapes")
    odd = golden_rgb("shapes@2x", crop=(13, 9, 13 + 237, 9 + 153))
    files = {
        "baseline-420-q90.jpg": (shapes, "-quality", "90"),
        "baseline-444-q85.jpg": (golden_rgb("gradients"), "-quality", "85", "-sample", "1x1"),
        "baseline-422-q80.jpg": (golden_rgb("strokes"), "-quality", "80", "-sample", "2x1"),
        "baseline-440-q85.jpg": (golden_rgb("clip-layers"), "-quality", "85", "-sample", "1x2"),
        "baseline-411-q85.jpg": (golden_rgb("transforms"), "-quality", "85", "-sample", "4x1"),
        "baseline-mixed-q85.jpg": (golden_rgb("box-shadows"), "-quality", "85", "-sample", "2x2,1x2,2x1"),
        "baseline-restart-rows.jpg": (golden_rgb("blend-modes"), "-quality", "75", "-restart", "1"),
        "baseline-restart-blocks-optimized.jpg": (golden_rgb("strokes"), "-quality", "95", "-restart", "7B", "-optimize"),
        "baseline-rgb.jpg": (golden_rgb("fill-rules"), "-quality", "85", "-rgb"),
        "gray.jpg": (golden_rgb("fill-rules"), "-quality", "80", "-grayscale"),
        "gray-progressive.jpg": (golden_rgb("gradients"), "-quality", "80", "-grayscale", "-progressive"),
        "progressive-420.jpg": (golden_rgb("box-shadows"), "-quality", "85", "-progressive"),
        "progressive-444-restart.jpg": (golden_rgb("transforms"), "-quality", "90", "-progressive", "-sample", "1x1", "-restart", "3B"),
        "progressive-422-q50.jpg": (golden_rgb("strokes"), "-quality", "50", "-progressive", "-sample", "2x1"),
        "progressive-440-q98.jpg": (golden_rgb("clip-layers"), "-quality", "98", "-progressive", "-sample", "1x2"),
        "odd-420.jpg": (odd, "-quality", "75"),
        "odd-422-progressive.jpg": (odd, "-quality", "85", "-sample", "2x1", "-progressive"),
        "odd-440.jpg": (odd, "-quality", "85", "-sample", "1x2"),
        "odd-444-progressive-restart.jpg": (odd, "-quality", "70", "-sample", "1x1", "-progressive", "-restart", "1"),
    }
    for name, (img, *args) in files.items():
        cjpeg(img, name, *args)
        reference(name)

    # Adobe CMYK（Pillow 写入反相 CMYK，transform=0）
    golden_rgb("gradients").convert("CMYK").save(os.path.join(ROOT, "cmyk.jpg"), quality=90)
    reference("cmyk.jpg")
    make_ycck(golden_rgb("shapes"), "ycck.jpg")
    reference("ycck.jpg")

    # EXIF 方向 = 6（仅元数据）
    exif = Image.Exif()
    exif[0x0112] = 6
    golden_rgb("fill-rules").save(os.path.join(ROOT, "exif-orientation-6.jpg"), quality=80, exif=exif.tobytes())

    # 不支持的编码：算术编码、12 位、无损
    tiny = golden_rgb("shapes", crop=(0, 0, 48, 32))
    cjpeg(tiny, "unsupported-arithmetic.jpg", "-arithmetic")
    cjpeg(tiny, "unsupported-12bit.jpg", "-precision", "12")
    cjpeg(tiny, "unsupported-lossless.jpg", "-lossless", "1")

    # 性能测试图（无参考图，测试中断言 baseline 与 progressive 解码逐像素相同）
    perf = perf_image()
    cjpeg(perf, "perf-1280x880.jpg", "-quality", "75")
    cjpeg(perf, "perf-1280x880-progressive.jpg", "-quality", "75", "-progressive")


PROGRESSIVE_SCRIPT = """
0,1,2: 0-0, 0, 2;
0: 1-9, 0, 1;
2: 1-63, 0, 0;
1: 1-63, 0, 2;
0,1,2: 0-0, 2, 1;
0: 10-63, 0, 1;
0,1,2: 0-0, 1, 0;
1: 1-63, 2, 1;
0: 1-63, 1, 0;
1: 1-63, 1, 0;
"""

SEQUENTIAL_SCRIPT = "0;\n1;\n2;\n"


def strip_dht(name, out_name):
    """去掉所有 DHT 段（模拟省略标准表的 Motion-JPEG 帧）；源文件必须用标准表编码（未 -optimize）。"""
    data = open(os.path.join(ROOT, name), "rb").read()
    out, i = bytearray(data[:2]), 2
    while True:
        assert data[i] == 0xFF
        marker = data[i + 1]
        length = data[i + 2] << 8 | data[i + 3]
        if marker == 0xDA:
            out += data[i:]
            break
        if marker != 0xC4:
            out += data[i:i + 2 + length]
        i += 2 + length
    with open(os.path.join(ROOT, out_name), "wb") as f:
        f.write(out)
    os.remove(os.path.join(ROOT, name))


def edge_cases():
    """小尺寸的边界用例（每个 < 2 KB）：窄色度平面、SOF1 16 位量化表、非交织扫描、自定义渐进脚本、3x/4x 采样、缺 DHT。"""
    src = golden_rgb("shapes", crop=(40, 30, 77, 59))  # 37x29

    rng = np.random.default_rng(42)

    def crop(w, h):
        # 随机彩色噪声：色度变化剧烈，平滑与复制两种上采样的差异一目了然
        return Image.fromarray(rng.integers(0, 256, (h, w, 3), dtype=np.uint8))

    script = os.path.join(TMP, "progressive.txt")
    with open(script, "w") as f:
        f.write(PROGRESSIVE_SCRIPT)
    seq = os.path.join(TMP, "sequential.txt")
    with open(seq, "w") as f:
        f.write(SEQUENTIAL_SCRIPT)
    files = {
        # 色度平面宽度 <= 2：libjpeg 不做平滑上采样，退化为复制
        "tiny-4x4-420.jpg": (crop(4, 4), "-sample", "2x2"),
        "tiny-3x3-420-progressive.jpg": (crop(3, 3), "-sample", "2x2", "-progressive"),
        "tiny-3x5-422.jpg": (crop(3, 5), "-sample", "2x1"),
        "tiny-4x17-420.jpg": (crop(4, 17), "-sample", "2x2"),
        "tiny-2x3-420.jpg": (crop(2, 3), "-sample", "2x2"),
        "tiny-5x5-420.jpg": (crop(5, 5), "-sample", "2x2"),
        # 量化值超过 255：SOF1 + 16 位 DQT
        "sof1-16bit-q3.jpg": (src, "-quality", "3"),
        # 顺序模式下每个分量单独一个非交织扫描
        "sequential-noninterleaved.jpg": (src, "-scans", seq),
        # 非常规渐进脚本：DC 逐次逼近、分段 AC 频带、交错的细化
        "progressive-custom-script.jpg": (src, "-scans", script),
        # 非 2 倍的整数采样比（int_upsample 复制）
        "sample-3x1.jpg": (src, "-sample", "3x1"),
        "sample-3x2.jpg": (src, "-sample", "3x2"),
        "sample-4x2.jpg": (src, "-sample", "4x2"),
    }
    for name, (img, *args) in files.items():
        cjpeg(img, name, *args)
        reference(name)

    cjpeg(src, "tmp-std-tables.jpg", "-quality", "80")
    strip_dht("tmp-std-tables.jpg", "missing-dht.jpg")
    reference("missing-dht.jpg")


if __name__ == "__main__":
    if sys.argv[1:] == ["edge"]:
        sys.exit(edge_cases())
    main()
    sys.exit(edge_cases())
