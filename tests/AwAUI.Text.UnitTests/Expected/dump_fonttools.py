# 用 fontTools 生成字体解析的期望值（交叉校验用）。
# 用法：python3 -I dump_fonttools.py <字体文件> <输出文件>
# 需要 fontTools（pip install fonttools，建议装在临时虚拟环境中）。
import sys
from fontTools.ttLib import TTFont

font_path, out_path = sys.argv[1], sys.argv[2]
f = TTFont(font_path)
glyf = f["glyf"]
order = f.getGlyphOrder()
lines = []
w = lines.append
import fontTools
w("# fontTools %s, %s" % (fontTools.version, font_path.split("/")[-1]))

head, hhea, os2, post = f["head"], f["hhea"], f["OS/2"], f["post"]
w("head %d %d %d %d %d %d %d" % (head.unitsPerEm, head.xMin, head.yMin, head.xMax, head.yMax, head.macStyle, head.indexToLocFormat))
w("hhea %d %d %d %d %d" % (hhea.ascent, hhea.descent, hhea.lineGap, hhea.advanceWidthMax, hhea.numberOfHMetrics))
w("maxp %d" % f["maxp"].numGlyphs)
w("os2 %d %d %d %d %d %d %d %d %d %d %d %d %d" % (
    os2.version, os2.usWeightClass, os2.usWidthClass, os2.fsSelection,
    os2.sTypoAscender, os2.sTypoDescender, os2.sTypoLineGap, os2.usWinAscent, os2.usWinDescent,
    getattr(os2, "sxHeight", 0), getattr(os2, "sCapHeight", 0), os2.yStrikeoutSize, os2.yStrikeoutPosition))
w("post %r %d %d %d" % (float(post.italicAngle), post.underlinePosition, post.underlineThickness, post.isFixedPitch))
names = f["name"]
for nid in (1, 2, 4, 6, 16, 17):
    rec = names.getName(nid, 3, 1, 0x409) or names.getName(nid, 1, 0, 0)
    if rec is not None:
        w("name %d %s" % (nid, rec.toUnicode()))

cmap = f.getBestCmap()
for cp in sorted(cmap):
    w("cmap %d %d" % (cp, f.getGlyphID(cmap[cp])))

hmtx = f["hmtx"]
for gid, name in enumerate(order):
    adv, lsb = hmtx[name]
    w("hmtx %d %d %d" % (gid, adv, lsb))

for gid, name in enumerate(order):
    g = glyf[name]
    coords, endPts, flags = g.getCoordinates(glyf)
    n = len(coords)
    on = sum(1 for fl in flags if fl & 1)
    sx = sum(int(round(x)) for x, y in coords)
    sy = sum(int(round(y)) for x, y in coords)
    chk = 0
    for i, (x, y) in enumerate(coords):
        chk = (chk + (i + 1) * (int(round(x)) * 31 + int(round(y)) * 17)) % 2147483647
    comp = 1 if g.isComposite() else 0
    bbox = (g.xMin, g.yMin, g.xMax, g.yMax) if hasattr(g, "xMin") else (0, 0, 0, 0)
    w("glyf %d %d %d %d %d %d %d %d %d %d %d %d" % (gid, n, len(endPts), on, sx, sy, chk, comp, *bbox))

if "kern" in f:
    for t in f["kern"].kernTables:
        if t.format != 0:
            continue
        for (l, r), v in sorted(t.kernTable.items(), key=lambda kv: (f.getGlyphID(kv[0][0]), f.getGlyphID(kv[0][1]))):
            w("kern %d %d %d" % (f.getGlyphID(l), f.getGlyphID(r), v))

with open(out_path, "w", encoding="utf-8") as fp:
    fp.write("\n".join(lines) + "\n")
