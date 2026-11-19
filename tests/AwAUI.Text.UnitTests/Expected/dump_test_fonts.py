"""
为自制测试字体（tests/TestFiles/Fonts/AwATest-*）生成期望数据：

  AwATest-outlines.txt  每个 CFF / CFF2 字形的轮廓（fontTools 4.68 独立解码，seac 组件展开，坐标保留 1 位小数，y 向上）
  AwATest-shaping.txt   整形期望（HarfBuzz 15.1 的 hb-shape，--font-funcs=ot；字形 ID、簇、累加后的 x / y 位置与总前进量）

  python3 -m venv /tmp/ft && /tmp/ft/bin/pip install fonttools
  cd tests/AwAUI.Text.UnitTests/Expected && /tmp/ft/bin/python -I dump_test_fonts.py ../../TestFiles/Fonts

hb-shape 需要在 PATH 中（例如 Homebrew 的 harfbuzz）。用例避开规范分解 / 组合会改变结果的文本
（OpenTypeShaper 只做组合标记的规范排序，不做分解与组合，见其文档）。用例避开规范分解 / 组合会改变结果的文本
（OpenTypeShaper 只做组合标记的规范排序，不做分解与组合，见其文档）。字形 X 使用 fontTools 不支持的算术操作符，在轮廓文件中缺省，由测试直接断言。
"""
import json, os, subprocess, sys, urllib.parse
from fontTools.ttLib import TTFont
from fontTools.pens.recordingPen import DecomposingRecordingPen

FONTS = sys.argv[1] if len(sys.argv) > 1 else '../../TestFiles/Fonts'


def num(v):
    r = round(v + 0.0, 1)
    s = ('%.1f' % r).rstrip('0').rstrip('.')
    return '0' if s in ('-0', '') else s


def outlines(out):
    for name in ('AwATest-Cff.otf', 'AwATest-Cid.otf', 'AwATest-Cid0.otf', 'AwATest-Cff2.otf'):
        font = TTFont(os.path.join(FONTS, name))
        gs = font.getGlyphSet()
        for gid, g in enumerate(font.getGlyphOrder()):
            pen = DecomposingRecordingPen(gs)
            try:
                gs[g].draw(pen)
            except NotImplementedError:
                continue
            parts = []
            for op, a in pen.value:
                if op == 'moveTo': parts.append('M %s %s' % (num(a[0][0]), num(a[0][1])))
                elif op == 'lineTo': parts.append('L %s %s' % (num(a[0][0]), num(a[0][1])))
                elif op == 'curveTo': parts.append('C ' + ' '.join('%s %s' % (num(x), num(y)) for x, y in a))
                elif op in ('closePath', 'endPath'): parts.append('Z')
            out.write('%s\t%d\t%s\n' % (name, gid, ' '.join(parts)))


# (字体, 文字, 语言, 方向, 特性, 文本)
CASES = [
    ('AwATest-Cff.otf', 'Latn', '', 'ltr', '', 'AVATAR To fi ffl'),
    ('AwATest-Cff.otf', 'Latn', '', 'ltr', '-liga,-kern', 'AVATAR To fi ffl'),
    ('AwATest-Cff.otf', 'Latn', '', 'ltr', '', 'V\u0301o\u0301 QA Vo'),
    ('AwATest-Layout.ttf', 'Latn', '', 'ltr', '', 'Ä ffi f́fi a b c d'),
    ('AwATest-Layout.ttf', 'Latn', '', 'ltr', 'salt=1', 'aba'),
    ('AwATest-Layout.ttf', 'Latn', '', 'ltr', 'salt=2', 'aba'),
    ('AwATest-Layout.ttf', 'Latn', '', 'ltr', 'salt[1:2]=2', 'aaa'),
    ('AwATest-Layout.ttf', 'Latn', '', 'ltr', '', 'bcd xy ce cc dcd exe axd'),
    ('AwATest-Layout.ttf', 'Latn', '', 'ltr', '-calt', 'bcd xy ce cc dcd exe'),
    ('AwATest-Layout.ttf', 'Latn', 'tr', 'ltr', '', 'x axd'),
    ('AwATest-Layout.ttf', 'Latn', '', 'ltr', '', 'ToT oo oA AT ad be ex ce'),
    ('AwATest-Layout.ttf', 'Latn', '', 'ltr', '-kern,-dist', 'ToT oo oA AT ad be'),
    ('AwATest-Layout.ttf', 'Latn', '', 'ltr', '', 'á̀ x̣́ o\u0323\u0308\u0301 T\u0301'),
    ('AwATest-Layout.ttf', 'Latn', '', 'ltr', 'kern[2:5]=0', 'ad ad ad'),
    ('AwATest-Layout.ttf', 'Arab', '', 'rtl', '', 'ببب مبم'),
    ('AwATest-Layout.ttf', 'Arab', '', 'rtl', '', 'لا بلا لَا'),
    ('AwATest-Layout.ttf', 'Arab', '', 'rtl', '', 'بَّبِ بَ'),
    ('AwATest-Layout.ttf', 'Arab', '', 'rtl', '', 'ب‍ ‍ب ب‌ب ـبـ'),
    ('AwATest-Layout.ttf', 'Arab', '', 'rtl', '-curs', 'ببب ممم'),
    ('AwATest-Layout.ttf', 'Hebr', '', 'rtl', '', 'אָא'),
    # 组合标记的规范排序（按修正后的组合类）：下方标记排到上方标记之前；阿拉伯 fatha + shadda 排成 shadda + fatha
    ('AwATest-Layout.ttf', 'Latn', '', 'ltr', '', 'o\u0308\u0301\u0323 x\u0301\u0323'),
    ('AwATest-Layout.ttf', 'Arab', '', 'rtl', '', '\u0628\u064E\u0651\u0628 \u0628\u0651\u064E\u0628'),
]


def shaping(out):
    for font, script, lang, d, feats, text in CASES:
        args = ['hb-shape', '--font-funcs=ot', '--output-format=json', '--no-glyph-names', '--script=' + script, '--direction=' + d]
        if lang: args.append('--language=' + lang)
        if feats: args.append('--features=' + feats)
        args += ['--', os.path.join(FONTS, font), text]
        glyphs = json.loads(subprocess.run(args, capture_output=True, text=True, check=True).stdout)
        x, parts = 0, []
        for g in glyphs:
            parts.append('%d=%d@%d,%d' % (g['g'], g['cl'], x + g['dx'], g['dy']))
            x += g['ax']
        out.write('\t'.join([font, script, lang, d, feats, urllib.parse.quote(text), ' '.join(parts) + ' |' + str(x)]) + '\n')


with open('AwATest-outlines.txt', 'w') as o: outlines(o)
with open('AwATest-shaping.txt', 'w') as o: shaping(o)
