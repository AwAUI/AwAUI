"""
生成 AwATest-*.otf / AwATest-Layout.ttf：本仓库自制的小型测试字体（CC0，见 README.md）。

  python3 -m venv /tmp/ft && /tmp/ft/bin/pip install fonttools
  /tmp/ft/bin/python -I build_test_fonts.py <输出目录>

CFF / CFF2 表由本脚本的最小写入器直接生成字节（不经 fontTools 的 CFF 编译器），以便精确控制要测试的结构：
宽度参数、hstemhm / vstemhm / hintmask / cntrmask、局部与全局子程序（含偏置与三层嵌套）、全部路径操作符、
flex / hflex / hflex1 / flex1、已废弃的算术与存储操作符、endchar 的 seac 组合、CID 键字体的 FDArray + FDSelect（格式 0 与 3）、
CFF2 的 blend 与 vsindex。期望轮廓由 fontTools 独立解码得到（见 tests/AwAUI.Text.UnitTests/Expected/dump_test_fonts.py）。
GSUB / GPOS 由 feaLib 编译，上下文查找的格式 1 / 2 / 3 与非链式上下文由 otTables 手工构造。
"""
import struct, sys, os
from fontTools.fontBuilder import FontBuilder
from fontTools.ttLib import newTable
from fontTools.ttLib.tables import otTables as ot
from fontTools.feaLib.builder import addOpenTypeFeaturesFromString
from fontTools.pens.ttGlyphPen import TTGlyphPen

OUT = sys.argv[1] if len(sys.argv) > 1 else '.'

# ---------------------------------------------------------------- CFF 写入器

OPS = {'hstem': 1, 'vstem': 3, 'vmoveto': 4, 'rlineto': 5, 'hlineto': 6, 'vlineto': 7, 'rrcurveto': 8, 'callsubr': 10,
       'return': 11, 'endchar': 14, 'vsindex': 15, 'blend': 16, 'hstemhm': 18, 'hintmask': 19, 'cntrmask': 20, 'rmoveto': 21,
       'hmoveto': 22, 'vstemhm': 23, 'rcurveline': 24, 'rlinecurve': 25, 'vvcurveto': 26, 'hhcurveto': 27, 'callgsubr': 29,
       'vhcurveto': 30, 'hvcurveto': 31,
       'and': 1203, 'or': 1204, 'not': 1205, 'abs': 1209, 'add': 1210, 'sub': 1211, 'div': 1212, 'neg': 1214, 'eq': 1215,
       'drop': 1218, 'put': 1220, 'get': 1221, 'ifelse': 1222, 'random': 1223, 'mul': 1224, 'sqrt': 1226, 'dup': 1227,
       'exch': 1228, 'index': 1229, 'roll': 1230, 'hflex': 1234, 'flex': 1235, 'hflex1': 1236, 'flex1': 1237}


def cs_num(v):
    if isinstance(v, float) and v != int(v):
        return bytes([255]) + struct.pack('>i', int(round(v * 65536)))
    v = int(v)
    if -107 <= v <= 107: return bytes([v + 139])
    if 108 <= v <= 1131: v -= 108; return bytes([(v >> 8) + 247, v & 0xFF])
    if -1131 <= v <= -108: v = -v - 108; return bytes([(v >> 8) + 251, v & 0xFF])
    return bytes([28]) + struct.pack('>h', v)


def charstring(prog):
    out = b''
    for t in prog:
        if isinstance(t, bytes): out += t                      # hintmask 的掩码字节
        elif isinstance(t, str):
            op = OPS[t]
            out += bytes([12, op - 1200]) if op >= 1200 else bytes([op])
        else: out += cs_num(t)
    return out


def dict_int(v):
    return bytes([29]) + struct.pack('>i', v)                 # 偏移一律用 5 字节，避免迭代布局


def dict_small(v):
    if -107 <= v <= 107: return bytes([v + 139])
    return dict_int(v)


def dict_op(op):
    return bytes([12, op - 1200]) if op >= 1200 else bytes([op])


def index(items, cff2=False):
    count = len(items)
    if count == 0: return struct.pack('>I' if cff2 else '>H', 0)
    offs = [1]
    for it in items: offs.append(offs[-1] + len(it))
    off_size = 1 if offs[-1] < 0x100 else 2 if offs[-1] < 0x10000 else 4
    fmt = {1: '>B', 2: '>H', 4: '>I'}[off_size]
    head = struct.pack('>I' if cff2 else '>H', count) + bytes([off_size])
    return head + b''.join(struct.pack(fmt, o) for o in offs) + b''.join(items)


def build_cff1(name, programs, local_subrs, global_subrs, charset_sids, strings, cid=None):
    """cid = None：名字键；否则 (fd_of_glyph 列表, 每个 FD 的局部子程序列表, fdselect 格式)。"""
    n = len(programs)
    header = bytes([1, 0, 4, 4])
    name_index = index([name.encode()])
    string_index = index([s.encode() for s in strings])
    gsubr_index = index([charstring(p) for p in global_subrs])
    charstrings = index([charstring(p) for p in programs])

    def layout(top_dict):
        top_index = index([top_dict])
        base = len(header) + len(name_index) + len(top_index) + len(string_index) + len(gsubr_index)
        return base

    # 先用占位偏移计算 Top DICT 长度（偏移都是 5 字节整数，长度不随取值变化）
    def make_top(o):
        d = b''
        if cid:
            d += dict_small(391) + dict_small(392) + dict_small(0) + dict_op(1230)   # ROS：Adobe Identity 0（自定义字符串 391、392）
        d += dict_int(o['charset']) + dict_op(15)
        d += dict_int(o['charstrings']) + dict_op(17)
        if cid:
            d += dict_int(o['fdarray']) + dict_op(1236)
            d += dict_int(o['fdselect']) + dict_op(1237)
            d += dict_int(n) + dict_op(1234)   # CIDCount
        else:
            d += dict_int(o['private_size']) + dict_int(o['private']) + dict_op(18)
        return d

    dummy = dict(charset=0, charstrings=0, fdarray=0, fdselect=0, private=0, private_size=0)
    pos = layout(make_top(dummy))
    o = {}
    # charset 格式 0：每个字形（除 .notdef）一个 SID / CID
    charset = bytes([0]) + b''.join(struct.pack('>H', s) for s in charset_sids)
    o['charset'] = pos; pos += len(charset)
    fdselect = b''
    if cid:
        fd_of, fd_subrs, fmt = cid
        if fmt == 0: fdselect = bytes([0]) + bytes(fd_of)
        else:
            ranges = []
            for g, fd in enumerate(fd_of):
                if not ranges or ranges[-1][1] != fd: ranges.append((g, fd))
            fdselect = bytes([3]) + struct.pack('>H', len(ranges)) + b''.join(struct.pack('>HB', g, fd) for g, fd in ranges) + struct.pack('>H', n)
        o['fdselect'] = pos; pos += len(fdselect)
    o['charstrings'] = pos; pos += len(charstrings)

    tail = b''
    if cid:
        # FDArray：每个 FD 一个只含 Private 的字典；Private 之后紧跟其局部子程序
        nfd = len(fd_subrs)
        fd_dict_len = len(dict_int(0) + dict_int(0) + dict_op(18))
        fd_index_len = len(index([b'x' * fd_dict_len] * nfd))
        o['fdarray'] = pos
        p = pos + fd_index_len
        fds, privs = [], b''
        for subrs in fd_subrs:
            priv_len = len(dict_int(0) + dict_op(19))
            priv = dict_int(priv_len) + dict_op(19)           # Subrs 紧跟在 Private DICT 之后
            fds.append(dict_int(priv_len) + dict_int(p) + dict_op(18))
            body = priv + index([charstring(s) for s in subrs])
            privs += body
            p += len(body)
        tail = index(fds) + privs
    else:
        priv_len = len(dict_int(0) + dict_op(19))
        o['private'] = pos
        o['private_size'] = priv_len
        tail = dict_int(priv_len) + dict_op(19) + index([charstring(s) for s in local_subrs])
    top = make_top(o)
    data = header + name_index + index([top]) + string_index + gsubr_index + charset + fdselect + charstrings + tail
    return data


def build_cff2(programs, local_subrs, global_subrs, region_counts):
    header_len = 5
    def make_top(o):
        return (dict_int(o['charstrings']) + dict_op(17) + dict_int(o['fdarray']) + dict_op(1236)
                + dict_int(o['varstore']) + dict_op(24))
    top_len = len(make_top(dict(charstrings=0, fdarray=0, varstore=0)))
    gsubrs = index([charstring(p) for p in global_subrs], True)
    pos = header_len + top_len + len(gsubrs)
    o = {}
    # VarStore：2 字节长度 + ItemVariationStore（一个轴，三个区域）
    axis_count, regions = 1, [(0, 1, 1), (-1, -1, 0), (0, 0.5, 1)]
    rl = struct.pack('>HH', axis_count, len(regions)) + b''.join(struct.pack('>hhh', *(int(v * 16384) for v in r)) for r in regions)
    ivds = [struct.pack('>HHH', 0, 0, rc) + b''.join(struct.pack('>H', i) for i in range(rc)) for rc in region_counts]
    ivs_head = 2 + 4 + 2 + 4 * len(ivds)
    offs, p = [], ivs_head
    for d in ivds: offs.append(p); p += len(d)
    rl_off = p
    ivs = struct.pack('>HIH', 1, rl_off, len(ivds)) + b''.join(struct.pack('>I', x) for x in offs) + b''.join(ivds) + rl
    varstore = struct.pack('>H', len(ivs)) + ivs
    o['varstore'] = pos; pos += len(varstore)
    charstrings = index([charstring(p) for p in programs], True)
    o['charstrings'] = pos; pos += len(charstrings)
    priv_len = len(dict_int(0) + dict_op(19))
    fd = dict_int(priv_len) + dict_int(0) + dict_op(18)
    fd_index_len = len(index([fd], True))
    o['fdarray'] = pos
    priv_pos = pos + fd_index_len
    fd = dict_int(priv_len) + dict_int(priv_pos) + dict_op(18)
    tail = index([fd], True) + dict_int(priv_len) + dict_op(19) + index([charstring(s) for s in local_subrs], True)
    top = make_top(o)
    return bytes([2, 0, 5]) + struct.pack('>H', len(top)) + top + gsubrs + varstore + charstrings + tail


# ---------------------------------------------------------------- 字形程序（CFF1）

B = 107   # 子程序偏置（数量 < 1240）
GLYPHS = ['.notdef', 'space', 'A', 'V', 'T', 'o', 'f', 'i', 'l', 'f_i', 'f_f_l', 'acute', 'Aacute', 'uni0301', 'Q', 'X']
WIDTHS = [500, 250, 620, 600, 560, 540, 320, 260, 260, 560, 820, 300, 620, 0, 700, 600]

LOCAL = [
    [20, 0, 'rmoveto', 290, 700, 290, -700, -60, 0, -230, 580, -230, -580, 'rlineto', 'return'],   # 0：A 的外形
    [-B + 2, 'callsubr', 'return'],                                                                # 1：→ 局部 2
    [-B + 0, 'callgsubr', 'return'],                                                               # 2：→ 全局 0
]
GLOBAL = [
    [100, 600, 'rmoveto', 120, 120, 'rlineto', -20, 0, -100, -100, 'rlineto', 'return'],         # 0：重音
    [180, 260, 'rmoveto', 260, 'hlineto', 60, 'vlineto', -260, 'hlineto', 'return'],             # 1：A 的横笔
]

PROGRAMS = [
    # .notdef：宽度参数 + rlineto
    [500, 50, 0, 'rmoveto', 400, 0, 0, 700, -400, 0, 'rlineto', 'endchar'],
    # space：只有宽度
    [250, 'endchar'],
    # A：hstemhm / vstemhm + hintmask（隐含 vstem）+ 局部与全局子程序
    [620, 0, 50, 650, 50, 'hstemhm', 20, 60, 'vstemhm', 540, 60, 'hintmask', b'\xF0', -B + 0, 'callsubr',
     'hintmask', b'\xC0', -B + 1, 'callgsubr', 'endchar'],
    # V：rlinecurve + rcurveline
    [0, 700, 'rmoveto', 60, 0, 240, -600, 0, 0, 0, 0, 0, 0, 'rlinecurve', 20, 200, 20, 200, 20, 200, 60, 0, 'rcurveline',
     10, 20, 30, 40, 50, 60, 'rrcurveto', 'endchar'],
    # T：hlineto / vlineto + cntrmask
    [0, 10, 600, 10, 'vstem', 'cntrmask', b'\xC0', 10, 640, 'rmoveto', 540, 60, -540, 'hlineto',
     240, -640, 'rmoveto', 60, 'hlineto', 640, 'vlineto', -60, 'hlineto', 'endchar'],
    # o：hvcurveto / vhcurveto（含最后的额外参数）+ hhcurveto / vvcurveto（含开头的奇数参数）
    [40, 250, 'rmoveto', -140, 110, -110, 130, 130, 110, 110, 140, 140, -110, 110, -130, -130, -110, -110, -140, 'hvcurveto',
     60, 0, 'rmoveto', 100, 70, 70, 80, 80, 70, -70, -100, 'vhcurveto',
     -10, -80, -70, -60, -80, 'hhcurveto', 5, -70, -10, 50, 60, 'vvcurveto', 'endchar'],
    # f
    [320, 40, 0, 'rmoveto', 70, 'hlineto', 450, 'vlineto', 100, 'hlineto', 60, 'vlineto', -100, 'hlineto',
     40, 30, 30, 40, 40, 'hvcurveto', 60, 'vlineto', -100, -30, -50, -60, 'hvcurveto', -110, 'vlineto', -50, 'hlineto',
     -60, 'vlineto', 50, 'hlineto', 'endchar'],
    # i：局部 1 → 局部 2 → 全局 0 的三层子程序嵌套
    [260, 90, 0, 'rmoveto', 70, 0, 0, 480, -70, 0, 'rlineto', -B + 1, 'callsubr', 'endchar'],
    # l
    [90, 0, 'rmoveto', 70, 700, -70, 'hlineto', 'endchar'],
    # f_i
    [560, 40, 0, 'rmoveto', 70, 450, 280, -450, 70, 510, -420, 160, -70, 'hlineto', 'endchar'],
    # f_f_l
    [820, 40, 0, 'rmoveto', 70, 450, 200, -450, 70, 450, 200, -450, 70, 700, -540, 'hlineto', 'endchar'],
    # acute
    [300, -B + 0, 'callgsubr', 'endchar'],
    # Aacute：seac（adx ady bchar=65 'A' achar=194 'acute'）
    [620, 160, 120, 65, 194, 'endchar'],
    # uni0301：组合重音（零宽）
    [0, -B + 0, 'callgsubr', 'endchar'],
    # Q：flex / hflex / hflex1 / flex1
    [700, 50, 300, 'rmoveto', 50, 50, 50, 50, 50, 0, 50, 0, 50, -50, 50, -50, 50, 'flex',
     50, 50, 20, 50, 50, 50, 50, 'hflex', 40, 10, 40, 30, 40, 40, 40, -20, 40, 'hflex1',
     -50, -40, -50, -40, -50, -10, -50, 10, -50, 40, -50, 'flex1', 'endchar'],
    # X：算术 / 存储 / 条件操作符（结果在 rmoveto 之前留下 (100, 50)）
    [600, 30, 70, 'add', 2, 'mul', 4, 'div', 'dup', 'drop', 0, 'put', 0, 'get',          # 50
     7, 'neg', 'abs', 7, 'eq', 1, 'and', 0, 'or', 'not', 'not', 'drop',               # 栈上仍是 50
     9, 'sqrt', 3, 'sub', 'drop', 100, 'exch',                                         # 100 50
     5, 6, 2, 1, 'roll', 'drop', 'drop',                                              # 100 50
     1, 2, 3, 4, 'ifelse', 'drop', 1, 'index', 1, 'index', 'drop', 'drop',              # 100 50
     'rmoveto', 400, 0, 0, 400, -400, 0, 'rlineto', 'endchar'],
]

# 名字键字体的 charset：标准字符串的 SID 或自定义字符串（391 起）
STD_SID = {'space': 1, 'A': 34, 'V': 55, 'T': 53, 'o': 80, 'f': 71, 'i': 74, 'l': 77, 'acute': 125, 'Aacute': 159, 'Q': 50, 'X': 57}
CUSTOM = ['f_i', 'f_f_l', 'uni0301']


def cff1_charset():
    sids = []
    for g in GLYPHS[1:]:
        sids.append(STD_SID[g] if g in STD_SID else 391 + CUSTOM.index(g))
    return sids


def base_font(glyphs, widths, cmap, cff_bytes=None, tag='CFF ', glyf=None):
    fb = FontBuilder(1000, isTTF=glyf is not None)
    fb.setupGlyphOrder(glyphs)
    fb.setupCharacterMap(cmap)
    if glyf is not None: fb.setupGlyf(glyf)
    else:
        # 先放一个占位 CFF，随后替换为本脚本写出的字节
        from fontTools.misc.psCharStrings import T2CharString
        fb.setupCFF('AwATest', {}, {g: T2CharString(program=['endchar']) for g in glyphs}, {})
    metrics = {g: (w, 0) for g, w in zip(glyphs, widths)}
    fb.setupHorizontalMetrics(metrics)
    fb.setupHorizontalHeader(ascent=800, descent=-200)
    fb.setupNameTable({'familyName': 'AwATest', 'styleName': 'Regular'})
    fb.setupOS2(sTypoAscender=800, sTypoDescender=-200, usWinAscent=800, usWinDescent=200)
    fb.setupPost()
    font = fb.font
    if cff_bytes is not None:
        # 原样写入本脚本生成的字节（不经 fontTools 重新编译，保留 FDSelect 格式、子程序与提示结构）
        from fontTools.ttLib.tables.DefaultTable import DefaultTable
        if 'CFF ' in font: del font['CFF ']
        t = DefaultTable(tag)
        t.data = cff_bytes
        font[tag] = t
        if tag == 'CFF2':
            font.sfntVersion = 'OTTO'
            font['maxp'].tableVersion = 0x00005000
    return font


LATIN_CMAP = {0x20: 'space', 0x41: 'A', 0x56: 'V', 0x54: 'T', 0x6F: 'o', 0x66: 'f', 0x69: 'i', 0x6C: 'l', 0xC1: 'Aacute',
              0x301: 'uni0301', 0x51: 'Q', 0x58: 'X', 0xB4: 'acute'}

LATIN_FEA = '''
languagesystem DFLT dflt;
languagesystem latn dflt;
markClass uni0301 <anchor 150 600> @TOP;
table GDEF { GlyphClassDef [A V T o f i l Aacute Q X acute], [f_i f_f_l], [uni0301], ; } GDEF;
feature liga { sub f f l by f_f_l; sub f i by f_i; } liga;
feature kern {
  pos A V -80;
  pos T o -60;
  @L = [V Q];
  @R = [A o];
  pos @L @R -30;
} kern;
feature mark { pos base [A o V] <anchor 300 700> mark @TOP; } mark;
'''


def write(font, name):
    path = os.path.join(OUT, name)
    # fontTools 的 Type 2 解释器不支持算术操作符（字形 X），保存时不重算边界框
    font.recalcBBoxes = False
    font.save(path)
    print(path, os.path.getsize(path))


def build_cff_fonts():
    data = build_cff1('AwATest-Cff', PROGRAMS, LOCAL, GLOBAL, cff1_charset(), CUSTOM)
    font = base_font(GLYPHS, WIDTHS, LATIN_CMAP, data)
    addOpenTypeFeaturesFromString(font, LATIN_FEA)
    write(font, 'AwATest-Cff.otf')

    # CID 键：同样的程序，字形名 cidNNNNN；两个 FD 的局部子程序不同（FD1 的子程序 0 把 A 外形右移 20）
    cid_glyphs = ['.notdef'] + ['cid%05d' % i for i in range(1, len(GLYPHS))]
    local1 = [list(LOCAL[0]), LOCAL[1], LOCAL[2]]
    local1[0] = [40, 0, 'rmoveto'] + LOCAL[0][3:]
    progs = [p for p in PROGRAMS]
    progs[12] = [620, 0, 0, 'rmoveto', 10, 'hlineto', 'endchar']   # CID 字体不支持 seac：换成普通字形
    fd_of = [0] * 6 + [1] * (len(GLYPHS) - 6)
    fd_of[2] = 1   # A 用 FD1 的子程序（区间：0-1 FD0, 2 FD1, 3-5 FD0, 6- FD1）
    cmap = {cp: cid_glyphs[GLYPHS.index(g)] for cp, g in LATIN_CMAP.items()}
    for fmt, name in ((3, 'AwATest-Cid.otf'), (0, 'AwATest-Cid0.otf')):
        data = build_cff1('AwATest-Cid', progs, None, GLOBAL, list(range(1, len(GLYPHS))), ['Adobe', 'Identity'],
                          cid=(fd_of, [LOCAL, local1], fmt))
        font = base_font(cid_glyphs, WIDTHS, cmap, data)
        write(font, name)


def build_cff2_font():
    glyphs = ['.notdef', 'space', 'A', 'o', 'H']
    widths = [500, 250, 620, 540, 640]
    progs = [
        [50, 0, 'rmoveto', 400, 0, 0, 700, -400, 0, 'rlineto'],
        [],
        # blend：两个坐标各带 1 个区域增量（vsindex 默认 0：1 个区域）
        [20, 0, 5, 0, 2, 'blend', 'rmoveto', 290, 700, 30, -10, 2, 'blend', 290, -700, 'rlineto', -B + 0, 'callsubr'],
        # vsindex 1（2 个区域）：每个值带 2 个增量
        [1, 'vsindex', 40, 250, 10, 20, 0, 0, 2, 'blend', 'rmoveto', -140, 110, -110, 130, 130, 110, 110, 140, 140, -110, 110, -130,
         -130, -110, -110, -140, 'hvcurveto'],
        # 提示 + hintmask + 全局子程序；CFF2 没有宽度参数与 endchar
        [0, 50, 600, 50, 'hstemhm', 40, 60, 'vstemhm', 'hintmask', b'\xE0', -B + 0, 'callgsubr'],
    ]
    local = [[200, 300, 'rmoveto', 200, 50, -200, 'hlineto']]
    glob = [[40, 0, 'rmoveto', 60, 700, -60, 'hlineto', 440, -700, 'rmoveto', 60, 700, -60, 'hlineto']]
    data = build_cff2(progs, local, glob, [1, 2])
    cmap = {0x20: 'space', 0x41: 'A', 0x6F: 'o', 0x48: 'H'}
    font = base_font(glyphs, widths, cmap, data, tag='CFF2')
    from fontTools.ttLib.tables._f_v_a_r import Axis
    fvar = newTable('fvar')
    ax = Axis(); ax.axisTag = 'wght'; ax.minValue, ax.defaultValue, ax.maxValue = 100, 400, 900; ax.axisNameID = 256
    font['name'].setName('Weight', 256, 3, 1, 0x409)
    fvar.axes = [ax]; fvar.instances = []
    font['fvar'] = fvar
    write(font, 'AwATest-Cff2.otf')


# ---------------------------------------------------------------- 布局测试字体（TrueType 方块字形）

LAYOUT_GLYPHS = {
    # 名字: (前进量, 宽, 高, 下沉)
    '.notdef': (500, 400, 700, 0), 'space': (250, 0, 0, 0),
    'a': (500, 400, 500, 0), 'a.alt1': (520, 420, 520, 0), 'a.alt2': (540, 440, 540, 0), 'b': (500, 400, 700, 0),
    'c': (480, 380, 500, 0), 'c.alt': (490, 390, 480, 0), 'd': (510, 410, 700, 0), 'e': (470, 370, 500, 0),
    'f': (300, 260, 700, 0), 'i': (240, 120, 650, 0), 'f_f_i': (760, 720, 700, 0), 'x': (460, 400, 500, 0),
    'x.ctx1': (461, 400, 500, 0), 'x.ctx2': (462, 400, 500, 0), 'x.ctx3': (463, 400, 500, 0), 'y': (450, 400, 500, 200),
    'A': (620, 580, 700, 0), 'Adieresis': (620, 580, 860, 0), 'T': (560, 540, 700, 0), 'o': (520, 440, 500, 0),
    'dieresiscomb': (0, 200, 100, -700), 'acutecomb': (0, 120, 160, -700), 'dotbelowcomb': (0, 80, 80, 150),
    'gravecomb': (0, 120, 160, -720),
    # 阿拉伯
    'beh': (600, 560, 300, 0), 'beh.init': (420, 420, 300, 0), 'beh.medi': (400, 400, 300, 0), 'beh.fina': (580, 560, 300, 0),
    'alef': (220, 120, 750, 0), 'alef.fina': (240, 140, 750, 0), 'lam': (500, 460, 800, 0), 'lam.init': (300, 300, 800, 0),
    'lam.medi': (280, 280, 800, 0), 'lam.fina': (520, 480, 800, 0), 'meem': (480, 440, 300, 250), 'meem.init': (460, 420, 300, 0),
    'meem.medi': (440, 420, 300, 0), 'meem.fina': (470, 440, 300, 250), 'lam_alef': (560, 520, 800, 0),
    'lam_alef.fina': (580, 540, 800, 0), 'fatha': (0, 200, 80, -500), 'shadda': (0, 220, 120, -480), 'kasra': (0, 200, 80, 200),
    'tatweel': (300, 300, 60, 0), 'zwj': (0, 0, 0, 0), 'zwnj': (0, 0, 0, 0),
    # 希伯来
    'alefhebrew': (600, 560, 600, 0), 'qamatshebrew': (0, 200, 80, 150),
}

LAYOUT_CMAP = {0x20: 'space', 0x61: 'a', 0x62: 'b', 0x63: 'c', 0x64: 'd', 0x65: 'e', 0x66: 'f', 0x69: 'i', 0x78: 'x', 0x79: 'y',
               0x41: 'A', 0xC4: 'Adieresis', 0x54: 'T', 0x6F: 'o', 0x308: 'dieresiscomb', 0x301: 'acutecomb', 0x323: 'dotbelowcomb',
               0x300: 'gravecomb',
               0x628: 'beh', 0x627: 'alef', 0x644: 'lam', 0x645: 'meem', 0x64E: 'fatha', 0x651: 'shadda', 0x650: 'kasra',
               0x640: 'tatweel', 0x200D: 'zwj', 0x200C: 'zwnj', 0x5D0: 'alefhebrew', 0x5B8: 'qamatshebrew'}

LAYOUT_FEA = '''
languagesystem DFLT dflt;
languagesystem latn dflt;
languagesystem latn TRK;
languagesystem arab dflt;
languagesystem hebr dflt;

@MARKS = [dieresiscomb acutecomb dotbelowcomb gravecomb fatha shadda kasra qamatshebrew];
@TOPMARKS = [dieresiscomb acutecomb gravecomb fatha shadda];
@BOTTOMMARKS = [dotbelowcomb kasra qamatshebrew];
table GDEF {
  GlyphClassDef [a a.alt1 a.alt2 b c c.alt d e f i x x.ctx1 x.ctx2 x.ctx3 y A Adieresis T o beh beh.init beh.medi beh.fina alef alef.fina
                 lam lam.init lam.medi lam.fina meem meem.init meem.medi meem.fina tatweel alefhebrew space],
                [f_f_i lam_alef lam_alef.fina], @MARKS, ;
} GDEF;

markClass [dieresiscomb acutecomb gravecomb] <anchor 100 0> @LTOP;
markClass [dotbelowcomb] <anchor 40 0> @LBOT;
markClass [fatha shadda] <anchor 100 0> @ATOP;
markClass [kasra] <anchor 100 0> @ABOT;
markClass [qamatshebrew] <anchor 100 0> @HBOT;

# ---- GSUB
lookup CCMP_MULT { sub Adieresis by A dieresiscomb; } CCMP_MULT;          # 类型 2
feature ccmp { lookup CCMP_MULT; } ccmp;

feature locl { script latn; language TRK exclude_dflt; sub x by x.ctx3; } locl;

feature salt { sub a from [a.alt1 a.alt2]; } salt;                         # 类型 3

lookup LIGA useExtension {                                                 # 类型 4 经扩展（类型 7）
  lookupflag IgnoreMarks;
  sub f f i by f_f_i;
} LIGA;
feature liga { lookup LIGA; } liga;

lookup SINGLE_C { sub c by c.alt; } SINGLE_C;                              # 类型 1 格式 1
lookup SINGLE_X { sub x by x.ctx1; sub y by x.ctx2; } SINGLE_X;            # 类型 1 格式 2
feature calt {
  sub b c' lookup SINGLE_C d;                                              # 类型 6 格式 3（有回溯与前瞻）
} calt;

feature rclt {
  reversesub [a b] x' [d e] by x.ctx3;                                     # 类型 8
} rclt;

# 阿拉伯
feature init { sub beh by beh.init; sub lam by lam.init; sub meem by meem.init; } init;
feature medi { sub beh by beh.medi; sub lam by lam.medi; sub meem by meem.medi; } medi;
feature fina { sub beh by beh.fina; sub alef by alef.fina; sub lam by lam.fina; sub meem by meem.fina; } fina;
feature rlig {
  lookupflag IgnoreMarks;
  sub lam.init alef.fina by lam_alef;
  sub lam.medi alef.fina by lam_alef.fina;
} rlig;

# ---- GPOS
lookup SINGLEPOS { pos e <10 20 30 0>; pos [c c.alt] <-5 0 -10 0>; } SINGLEPOS;   # 类型 1 格式 1 / 2
feature dist { lookup SINGLEPOS; } dist;

feature kern {
  pos T o -70;                                                             # 类型 2 格式 1
  pos space T -50;                                                         # 跨词字距（空格之后是断行机会）
  pos A T -40;
  @K1 = [a b];
  @K2 = [d e x];
  pos @K1 @K2 -25;                                                         # 类型 2 格式 2
  pos o <0 0 -15 0> A <5 0 5 0>;                                           # 第二个值记录
} kern;

lookup KERN_O { pos o -33; } KERN_O;
feature kern {
  pos T o' lookup KERN_O T;                                                # 类型 8（链式上下文定位）
} kern;

feature mark {
  lookupflag UseMarkFilteringSet @TOPMARKS;
  pos base [a b c d e x y A T o] <anchor 250 650> mark @LTOP;
  pos base [beh beh.init beh.medi beh.fina meem meem.init meem.medi meem.fina] <anchor 300 400> mark @ATOP;
  pos ligature lam_alef <anchor 400 900> mark @ATOP ligComponent <anchor 100 900> mark @ATOP;   # 类型 5
} mark;
feature mark {
  lookupflag 0;
  pos base [a b c d e x y A T o] <anchor 200 -20> mark @LBOT;
  pos base [beh beh.init beh.medi beh.fina meem meem.init meem.medi meem.fina] <anchor 300 -50> mark @ABOT;
  pos base [alefhebrew] <anchor 300 -40> mark @HBOT;
} mark;

feature mkmk {
  lookupflag MarkAttachmentType @TOPMARKS;
  pos mark [dieresiscomb acutecomb gravecomb] <anchor 100 120> mark @LTOP;   # 类型 6
  pos mark [shadda] <anchor 100 150> mark @ATOP;
} mkmk;

feature curs {
  lookupflag RightToLeft IgnoreMarks;
  pos cursive beh.init <anchor NULL> <anchor 0 100>;                       # 类型 3
  pos cursive beh.medi <anchor 400 120> <anchor 0 80>;
  pos cursive beh.fina <anchor 560 60> <anchor NULL>;
  pos cursive meem.init <anchor NULL> <anchor 0 50>;
  pos cursive meem.medi <anchor 440 70> <anchor 0 60>;
  pos cursive meem.fina <anchor 440 40> <anchor NULL>;
} curs;
'''


def box_glyph(w, h, sink):
    """宽 w、高 h 的矩形；sink > 0 伸到基线以下，sink < 0 整体抬高（上方标记）。"""
    pen = TTGlyphPen(None)
    if w > 0 and h != 0:
        x0, y0 = 20, -sink
        pen.moveTo((x0, y0)); pen.lineTo((x0, y0 + h)); pen.lineTo((x0 + w, y0 + h)); pen.lineTo((x0 + w, y0)); pen.closePath()
    return pen.glyph()


def add_context_lookups(font):
    """手工构造 GSUB 类型 5（格式 1、2、3）与类型 6 格式 1、2，GPOS 类型 7 格式 3；加入 'calt' / 'kern' 特性。"""
    gsub = font['GSUB'].table
    gpos = font['GPOS'].table
    names = {g: g for g in font.getGlyphOrder()}

    def find_lookup(table, first_glyph_pred):
        for i, l in enumerate(table.LookupList.Lookup):
            if first_glyph_pred(l): return i
        raise KeyError

    single_c = find_lookup(gsub, lambda l: l.LookupType == 1 and getattr(l.SubTable[0], 'mapping', {}).get('c') == 'c.alt' and len(l.SubTable[0].mapping) == 1)
    single_x = find_lookup(gsub, lambda l: l.LookupType == 1 and getattr(l.SubTable[0], 'mapping', {}).get('x') == 'x.ctx1')

    def rec(cls, seq, lookup):
        r = getattr(ot, cls)(); r.SequenceIndex = seq; r.LookupListIndex = lookup; return r

    def coverage(glyphs):
        c = ot.Coverage(); c.glyphs = sorted(glyphs, key=font.getGlyphID); return c

    def classdef(m):
        c = ot.ClassDef(); c.classDefs = dict(m); return c

    def new_lookup(table, ltype, subtables):
        l = ot.Lookup(); l.LookupType = ltype; l.LookupFlag = 0; l.SubTable = subtables; l.SubTableCount = len(subtables)
        table.LookupList.Lookup.append(l); table.LookupList.LookupCount = len(table.LookupList.Lookup)
        return len(table.LookupList.Lookup) - 1

    # 类型 5 格式 1："x y" → x 用 SINGLE_X（x→x.ctx1），y 用 SINGLE_X（y→x.ctx2）
    st = ot.ContextSubst(); st.Format = 1; st.Coverage = coverage(['x'])
    rs = ot.SubRuleSet(); r = ot.SubRule(); r.Input = ['y']; r.GlyphCount = 2
    r.SubstLookupRecord = [rec('SubstLookupRecord', 0, single_x), rec('SubstLookupRecord', 1, single_x)]; r.SubstCount = 2
    rs.SubRule = [r]; rs.SubRuleCount = 1
    st.SubRuleSet = [rs]; st.SubRuleSetCount = 1
    l5f1 = new_lookup(gsub, 5, [st])

    # 类型 5 格式 2：类别 1 = {c}，类别 2 = {e}；"c e" 中的 c → c.alt
    st = ot.ContextSubst(); st.Format = 2; st.Coverage = coverage(['c']); st.ClassDef = classdef({'c': 1, 'e': 2})
    r = ot.SubClassRule(); r.Class = [2]; r.GlyphCount = 2; r.SubstLookupRecord = [rec('SubstLookupRecord', 0, single_c)]; r.SubstCount = 1
    s1 = ot.SubClassSet(); s1.SubClassRule = [r]; s1.SubClassRuleCount = 1
    st.SubClassSet = [None, s1]; st.SubClassSetCount = 2
    l5f2 = new_lookup(gsub, 5, [st])

    # 类型 5 格式 3："c c" 中的第二个 c → c.alt
    st = ot.ContextSubst(); st.Format = 3; st.Coverage = [coverage(['c']), coverage(['c'])]; st.GlyphCount = 2
    st.SubstLookupRecord = [rec('SubstLookupRecord', 1, single_c)]; st.SubstCount = 1
    l5f3 = new_lookup(gsub, 5, [st])

    # 类型 6 格式 1：回溯 e，输入 x，前瞻 e → x.ctx1
    st = ot.ChainContextSubst(); st.Format = 1; st.Coverage = coverage(['x'])
    r = ot.ChainSubRule(); r.Backtrack = ['e']; r.BacktrackGlyphCount = 1; r.Input = []; r.InputGlyphCount = 1
    r.LookAhead = ['e']; r.LookAheadGlyphCount = 1; r.SubstLookupRecord = [rec('SubstLookupRecord', 0, single_x)]; r.SubstCount = 1
    rs = ot.ChainSubRuleSet(); rs.ChainSubRule = [r]; rs.ChainSubRuleCount = 1
    st.ChainSubRuleSet = [rs]; st.ChainSubRuleSetCount = 1
    l6f1 = new_lookup(gsub, 6, [st])

    # 类型 6 格式 2：回溯类别 1 = {d}，输入类别 1 = {c}，前瞻类别 1 = {d}："d c d" → c.alt
    st = ot.ChainContextSubst(); st.Format = 2; st.Coverage = coverage(['c'])
    st.BacktrackClassDef = classdef({'d': 1}); st.InputClassDef = classdef({'c': 1}); st.LookAheadClassDef = classdef({'d': 1})
    r = ot.ChainSubClassRule(); r.Backtrack = [1]; r.BacktrackGlyphCount = 1; r.Input = []; r.InputGlyphCount = 1
    r.LookAhead = [1]; r.LookAheadGlyphCount = 1; r.SubstLookupRecord = [rec('SubstLookupRecord', 0, single_c)]; r.SubstCount = 1
    s1 = ot.ChainSubClassSet(); s1.ChainSubClassRule = [r]; s1.ChainSubClassRuleCount = 1
    st.ChainSubClassSet = [None, s1]; st.ChainSubClassSetCount = 2
    l6f2 = new_lookup(gsub, 6, [st])

    # GPOS 类型 7 格式 3："o o" 中的第一个 o 加 KERN_O（-33）
    kern_o = find_lookup(gpos, lambda l: l.LookupType == 1 and l.SubTable[0].Coverage.glyphs == ['o'])
    st = ot.ContextPos(); st.Format = 3; st.Coverage = [coverage(['o']), coverage(['o'])]; st.GlyphCount = 2
    st.PosLookupRecord = [rec('PosLookupRecord', 0, kern_o)]; st.PosCount = 1
    l7f3 = new_lookup(gpos, 7, [st])

    for table, tag, idx in ((gsub, 'calt', [l5f1, l5f2, l5f3, l6f1, l6f2]), (gpos, 'kern', [l7f3])):
        for fr in table.FeatureList.FeatureRecord:
            if fr.FeatureTag == tag:
                fr.Feature.LookupListIndex = sorted(set(fr.Feature.LookupListIndex + idx))
                fr.Feature.LookupCount = len(fr.Feature.LookupListIndex)


def build_layout_font():
    glyphs = list(LAYOUT_GLYPHS)
    glyf = {g: box_glyph(v[1], v[2], v[3]) for g, v in LAYOUT_GLYPHS.items()}
    font = base_font(glyphs, [v[0] for v in LAYOUT_GLYPHS.values()], LAYOUT_CMAP, glyf=glyf)
    addOpenTypeFeaturesFromString(font, LAYOUT_FEA)
    add_context_lookups(font)
    write(font, 'AwATest-Layout.ttf')


def build_hostile_font():
    """恶意构造的 CFF：自递归子程序、指数级展开、栈溢出、截断的 hintmask、缺失字形的 seac、没有 moveto 的 lineto。"""
    glyphs = ['.notdef', 'g1', 'g2', 'g3', 'g4', 'g5', 'g6', 'g7']
    local = [
        [-B + 0, 'callsubr', 'return'],                                          # 0：调用自己
    ]
    # 1..10：每层调用下一层 4 次（4^9 次展开）
    for level in range(1, 11):
        nxt = level + 1
        local.append(([-B + nxt, 'callsubr'] * 4 if level < 10 else [10, 10, 'rlineto']) + ['return'])
    progs = [
        [50, 0, 'rmoveto', 400, 0, 0, 700, -400, 0, 'rlineto', 'endchar'],
        [0, 0, 'rmoveto', -B + 0, 'callsubr', 'endchar'],
        [0, 0, 'rmoveto', -B + 1, 'callsubr', 'endchar'],
        list(range(49)) + ['rlineto', 'endchar'],
        [0, 10] * 30 + ['hstemhm', 'hintmask'],                                  # 掩码字节被截断
        [0, 0, 65, 66, 'endchar'],                                               # seac：标准编码 'A'、'B' 不在 charset 中
        [100, 100, 'rlineto', 50, 0, 'rlineto', 'endchar'],                      # 没有 moveto（宽松：从原点开始）
        [0, 0, 'rmoveto', 10, 'return'],                                         # 子程序之外的 return
    ]
    data = build_cff1('AwATest-Hostile', progs, local, [], [391 + i for i in range(len(glyphs) - 1)], glyphs[1:])
    font = base_font(glyphs, [500] * len(glyphs), {0x31 + i: g for i, g in enumerate(glyphs[1:])}, data)
    write(font, 'AwATest-Hostile.otf')


if __name__ == '__main__':
    build_cff_fonts()
    build_hostile_font()
    build_cff2_font()
    build_layout_font()
