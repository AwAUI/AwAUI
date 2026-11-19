# 图片解码测试数据

本目录下的文件都由脚本生成，可以重新生成；参考结果来自独立的参考解码器，不是被测代码的输出。

| 位置 | 生成脚本 | 参考解码器 |
|---|---|---|
| `*.jpg` / `*.ref.png`（本目录根部） | `generate.py` | libjpeg-turbo 的 `djpeg -dct int` |
| `gif/` | `generate_formats.py gif` | giflib 5（`DGifSlurp` 解出索引）+ 脚本按浏览器语义合成 |
| `webp/` | `generate_formats.py webp` | libwebp 1.6（Pillow 的 `WebPAnimDecoder`；静态图另与 `dwebp -pam` 逐字节核对） |
| `bmp/` | `generate_formats.py bmp` | Pillow；16 位、2 位与 RLE 跳转的期望值由脚本直接算出（见下） |
| `ico/` | `generate_formats.py ico` | Pillow 逐项解码；CUR 的期望值即源像素 |
| `*/perf-*` | `generate_formats.py perf` | 同上，参考结果只存所有帧 RGBA 的 SHA-256（`*.sha256`） |

依赖：Python 3、Pillow（自带 libwebp）、numpy，以及 Homebrew 的 `cwebp` / `img2webp` / `gif2webp` / `webpmux`、
giflib（`libgif.dylib`，经 ctypes 调用）、ffmpeg（含 libvpx）。在仓库根目录运行
`python3 tests/TestFiles/Images/generate_formats.py`（可只给 `gif`、`webp`、`bmp`、`ico`、`perf` 中的若干节）。
每节使用独立的随机种子，单独重跑某一节不会改变其他节的文件。

参考图的格式：静态图为 `名称.ref.png`（8 位 RGBA、非预乘）；动画为 `名称.frames.png`，所有帧（整张画布，处置与混合已生效）
自上而下拼接。测试按非预乘 RGBA 逐字节比较，GIF / BMP / WebP（含有损）均要求完全一致。

## 源图

- `perf-1280x880.jpg` 的裁剪（照片内容）与 `tests/TestFiles/Golden/*.png`（界面渲染结果）；金样图都不透明，
  需要透明度的用例叠加了脚本生成的 alpha（水平渐变 + 两块全透明区域）。
- 噪声、渐变、调色板图由 numpy 按固定种子生成。

## GIF（`gif/`）

- `photo-256*.gif`：256 色全局调色板，逐行与隔行扫描。`shapes-4-transparent.gif`：局部颜色表 + 透明色。
  `bilevel-87a.gif`：GIF87a、2 色。
- `deferred-clear.gif`：码表满 4096 项后不发清表码继续编码（延迟清表）；`frequent-clear-no-eoi.gif`：每 50 个码字清表、
  缺少 EOI、最小码长 8。以上由脚本自带的 LZW 编码器写出，giflib 能正确解码即说明编码合法。
- `anim-disposal.gif`：处置方式 1/2/3、帧偏移、局部表、透明色、超出画布的帧、NETSCAPE 循环 2 次。
- `anim-truncated-frame.gif`：最后一帧只给出一半像素（giflib 拒绝，期望值由脚本直接合成：已解出的像素照常绘制）。
- `pillow-anim.gif`、`ffmpeg-anim.gif`：Pillow 与 ffmpeg（`palettegen` / `paletteuse`，透明差分 + 偏移）写出的动画。
- `perf-480x320-anim.gif`：24 帧，ffmpeg 只存变化的矩形；性能测试用。

## WebP（`webp/`）

- `ll-*`：无损（VP8L），cwebp 的不同 `-m` / `-z` / `-near_lossless` / `-exact`，调色板变换的 1/2/4/8 位打包，1×1、200×3 等尺寸。
- `lossy-*`：有损（VP8），不同质量、简单 / 普通环路滤波、锐度、分段数、`-sharp_yuv`、非 16 倍数尺寸。
  `lossy-libvpx-{4,8}parts.webp` 由 ffmpeg 的 libvpx 编码（4 / 8 个系数分区、环路滤波的参考帧与模式增量），
  再由脚本包进 RIFF 容器。
- `lossy-alpha-*`：ALPH 块。cwebp 只会选择无压缩或 VP8L 压缩 + 水平滤波，`lossy-alpha-raw-{horizontal,vertical,gradient}.webp`
  的 ALPH 块由脚本按 libwebp 的滤波规则手工构造，覆盖三种预测滤波。
- `lossy-vp8x-metadata.webp`：webpmux 加入 ICCP 与 EXIF 块的扩展格式。
- `anim-*`：img2webp（无损 / 有损 / 混合）、gif2webp，以及 webpmux 手工组帧的 `anim-mux-dispose-blend.webp`
  （显式偏移、处置到背景、混合与不混合、半透明与全透明帧）。
- `perf-1280x880-lossless.webp`（金样图拼成的界面截图）与 `perf-1280x880-lossy.webp`（`perf-1280x880.jpg`，q75）：性能测试用。

## BMP（`bmp/`）

由脚本按格式规范直接写出：24 位（自下而上 / 自上而下）、32 位 BI_RGB（第 4 字节为 0）、V5 头 BI_BITFIELDS + alpha、
V4 头的非常规掩码顺序（期望值同 `rgba32-v5.ref.png`）、16 位 5-5-5 与 BITFIELDS 5-6-5、8/4/2/1 位调色板、
`biClrUsed` = 7、OS/2 1.x 核心头、RLE8、RLE4、带增量跳转与提前结束的 RLE8。

不足 8 位的通道按位复制扩展（`v << 3 | v >> 2`，与 Firefox 一致）；Pillow 用 `v * 255 / 31` 向下取整，
两者最多差 1，因此 `rgb555` / `rgb565` 的期望值由脚本按位复制计算。Pillow 不支持 2 位 BMP，
RLE 中未写到的像素 Pillow 填 0 号颜色而浏览器为透明，这两种的期望值也由脚本直接给出。

## ICO / CUR（`ico/`）

- `bmp-entries.ico`：32 / 8 / 4 / 1 / 24 位 BMP 项（含 AND 掩码），每项一个参考图 `bmp-entries.WxH-BPP.ref.png`。
- `png-entries.ico`：Pillow 写出的 PNG 项（16、32、256，256 在目录中记为 0）。
- `pointer.cur`：两个 32 位 BMP 项与热点 (3,4)、(1,2)。
