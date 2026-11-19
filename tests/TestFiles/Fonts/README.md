# 测试字体

文本栈（`tests/AwAUI.Text.UnitTests`）使用的字体。全部为 SIL Open Font License 1.1，许可证文本与字体放在一起。
字体文件按原样提交，不做任何修改（OFL 允许再分发；修改后须改名，因此这里只用原版）。

| 文件 | 来源（固定到提交） | 许可证 | SHA-256 | 用途 |
|---|---|---|---|---|
| `Abel-Regular.ttf` | https://raw.githubusercontent.com/google/fonts/3b99d83d2625944fc0b8bd328d793fa819b92381/ofl/abel/Abel-Regular.ttf | `Abel-OFL.txt`（https://raw.githubusercontent.com/google/fonts/90abd17b4f97671435798b6147b698aa9087612f/ofl/abel/OFL.txt） | `8809dcad25318225052f88333e208c5aad4adcb7b2c934c135735ec19aa410b4` | 35 KB；带传统 `kern` 表（格式 0，748 对），upem 2048，cmap 格式 4 + Mac 格式 0 |
| `NotoSans-Regular.ttf` | https://raw.githubusercontent.com/notofonts/notofonts.github.io/28b15b4b43b7bed62b5cf6e6b0b5ff5846270535/fonts/NotoSans/unhinted/ttf/NotoSans-Regular.ttf | `NotoSans-OFL.txt`（https://raw.githubusercontent.com/notofonts/latin-greek-cyrillic/4bc63d7ebca1faed49c6c685f380ba0abc2c1941/OFL.txt） | `f3961a9cde016d41a4879aecda1474d3a36d6bf54fa0e4643de029cc2248b0e8` | 静态实例（unhinted）；3884 个字形，1621 个复合字形，cmap 格式 4 + 12，OS/2 v4，upem 1000 |

两者都是 `glyf` 轮廓（非 CFF）。

## 自制测试字体（AwATest-*）

由本仓库的 `build_test_fonts.py`（fontTools 4.68）生成，用于 CFF / CFF2 轮廓与 OpenType 布局（GSUB / GPOS）的单元测试。
它们是为测试专门构造的方块与简单几何形状，不是可用的字体；作者放弃全部权利（CC0 1.0，https://creativecommons.org/publicdomain/zero/1.0/）。

| 文件 | 内容 |
|---|---|
| `AwATest-Cff.otf` | 名字键 CFF：宽度参数、hstemhm / vstemhm / hintmask / cntrmask、局部与全局子程序（三层嵌套）、全部路径操作符、flex 族、算术 / 存储操作符（字形 X）、seac（Aacute）；GSUB liga、GPOS kern（格式 1 + 2）与 mark |
| `AwATest-Cid.otf` / `AwATest-Cid0.otf` | CID 键 CFF：两个 FD，各自的局部子程序，FDSelect 格式 3 / 格式 0 |
| `AwATest-Cff2.otf` | CFF2：blend（vsindex 0 / 1，区域数 1 / 2）、提示、局部与全局子程序、没有 endchar |
| `AwATest-Hostile.otf` | 恶意 CFF：自递归子程序、4^9 次展开、栈溢出、截断的 hintmask、缺失字形的 seac、子程序之外的 return |
| `AwATest-Layout.ttf` | 方块字形的 TrueType：GSUB 类型 1–8（上下文 / 链式上下文的格式 1–3 由 otTables 手工构造）、GPOS 类型 1–9、查找标志（IgnoreMarks、MarkAttachmentType、UseMarkFilteringSet、RightToLeft）、阿拉伯 init / medi / fina / rlig / curs / mark / mkmk、希伯来标记、TRK 语言系统 |

重新生成字体与期望数据（`Expected/AwATest-outlines.txt` 由 fontTools 解码，`Expected/AwATest-shaping.txt` 由 HarfBuzz 15.1 的 `hb-shape --font-funcs=ot` 生成）：

```sh
/tmp/ft/bin/python -I build_test_fonts.py .
cd ../../AwAUI.Text.UnitTests/Expected && /tmp/ft/bin/python -I dump_test_fonts.py ../../TestFiles/Fonts
```

期望值：`tests/AwAUI.Text.UnitTests/Expected/*.txt` 由同目录的 `dump_fonttools.py` 用 fontTools 4.68.0 生成，
包括全局表、全部 cmap 映射、全部 hmtx、每个字形的点数 / 轮廓数 / 坐标校验和 / 边界框，以及全部 kern 对。
更换或新增字体后重新生成：

```sh
python3 -m venv /tmp/ft && /tmp/ft/bin/pip install fonttools
cd tests/AwAUI.Text.UnitTests/Expected
/tmp/ft/bin/python -I dump_fonttools.py ../../TestFiles/Fonts/Abel-Regular.ttf Abel-Regular.txt
/tmp/ft/bin/python -I dump_fonttools.py ../../TestFiles/Fonts/NotoSans-Regular.ttf NotoSans-Regular.txt
```

不要提交操作系统自带的字体。依赖 macOS 系统字体（`/System/Library/Fonts/Helvetica.ttc` 等）的测试在文件不存在时自动跳过。
