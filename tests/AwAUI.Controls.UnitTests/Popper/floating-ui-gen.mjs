// 生成 floating-ui-cases.txt：Floating UI 的参考输出（差分测试 FloatingUiDifferentialTests 的数据）。
// 重新生成（在仓库外的临时目录中）：npm install @floating-ui/core@1.7.5 && node floating-ui-gen.mjs 1500 > floating-ui-cases.txt
// 构建与测试不依赖 Node；这个脚本只用于更新数据。
import { computePosition, offset, flip, shift, limitShift, size, arrow, hide, autoPlacement } from '@floating-ui/core';

// 确定性伪随机（mulberry32）
let seed = 12345;
function rnd() { seed |= 0; seed = seed + 0x6D2B79F5 | 0; let t = Math.imul(seed ^ seed >>> 15, 1 | seed); t = t + Math.imul(t ^ t >>> 7, 61 | t) ^ t; return ((t ^ t >>> 14) >>> 0) / 4294967296; }
const ri = (a, b) => a + Math.floor(rnd() * (b - a + 1));
const pick = arr => arr[ri(0, arr.length - 1)];
const PL = ['top','top-start','top-end','right','right-start','right-end','bottom','bottom-start','bottom-end','left','left-start','left-end'];

function inter(a, b) {
  const left = Math.max(a.x, b.x), top = Math.max(a.y, b.y), right = Math.min(a.x + a.width, b.x + b.width), bottom = Math.min(a.y + a.height, b.y + b.height);
  return { x: left, y: top, width: right - left, height: bottom - top };
}
const R = r => r ? `${r.x}:${r.y}:${r.width}:${r.height}` : '_';

const lines = [];
const N = Number(process.argv[2] || 1500);
for (let n = 0; n < N; n++) {
  const W = ri(200, 1200), H = ri(200, 1200);
  const vp = { x: 0, y: 0, width: W, height: H };
  const refRect = { x: ri(-80, W + 20), y: ri(-80, H + 20), width: ri(0, 300), height: ri(0, 200) };
  const fl = { width: ri(0, 400), height: ri(0, 400) };
  const origFl = { ...fl };
  const placement = pick(PL);
  const rtl = rnd() < 0.25;
  const fclip = rnd() < 0.25 ? { x: ri(-50, W / 2), y: ri(-50, H / 2), width: ri(50, W), height: ri(50, H) } : null;
  const rclip = rnd() < 0.4 ? { x: ri(-50, W / 2), y: ri(-50, H / 2), width: ri(50, W), height: ri(50, H) } : null;
  const reference = { tag: 'reference' };
  const floating = { tag: 'floating', ...fl };
  const arrowEl = { tag: 'arrow', width: ri(4, 20), height: ri(4, 12) };
  let lastSize = null;
  const specs = [];
  const mws = [];
  if (rnd() < 0.7) {
    const m = ri(-10, 24), c = ri(-10, 10), a = rnd() < 0.4 ? ri(-15, 15) : null;
    mws.push(offset(a === null ? { mainAxis: m, crossAxis: c } : { mainAxis: m, crossAxis: c, alignmentAxis: a }));
    specs.push(`offset,${m},${c},${a === null ? '_' : a}`);
  }
  const useAuto = rnd() < 0.2;
  const shiftFirst = rnd() < 0.5;
  const doShift = rnd() < 0.7;
  const shiftSpec = () => {
    const main = rnd() < 0.85, cross = rnd() < 0.3, lim = rnd() < 0.5, lo = ri(0, 20), lc = ri(0, 10), lm = rnd() < 0.85, lx = rnd() < 0.7, pad = ri(0, 12);
    const bnd = rnd() < 0.15 ? { x: ri(0, W / 3), y: ri(0, H / 3), width: ri(100, W), height: ri(100, H) } : null;
    const opts = { mainAxis: main, crossAxis: cross, padding: pad };
    if (bnd) opts.boundary = bnd;
    if (lim) opts.limiter = limitShift({ offset: { mainAxis: lo, crossAxis: lc }, mainAxis: lm, crossAxis: lx });
    mws.push(shift(opts));
    specs.push(`shift,${+main},${+cross},${+lim},${lo},${lc},${+lm},${+lx},${pad},${R(bnd)}`);
  };
  if (doShift && shiftFirst) shiftSpec();
  if (useAuto) {
    const cross = rnd() < 0.5, al = pick([null, 'start', 'end']), aa = rnd() < 0.7, pad = ri(0, 10);
    const allowed = rnd() < 0.3 ? PL.filter(() => rnd() < 0.5) : null;
    const opts = { crossAxis: cross, autoAlignment: aa, padding: pad };
    if (al !== null) opts.alignment = al;
    if (allowed && allowed.length) opts.allowedPlacements = allowed;
    mws.push(autoPlacement(opts));
    specs.push(`auto,${+cross},${al ?? '_'},${+aa},${pad},${allowed && allowed.length ? allowed.join('+') : '_'}`);
  } else if (rnd() < 0.8) {
    const main = rnd() < 0.9, cross = pick([false, true, 'alignment']), fasd = pick(['none', 'start', 'end']), fs = pick(['bestFit', 'initialPlacement']), fa = rnd() < 0.8, pad = ri(0, 12), alt = rnd() < 0.2;
    const fb = rnd() < 0.2 ? PL.filter(() => rnd() < 0.3) : null;
    const opts = { mainAxis: main, crossAxis: cross, fallbackAxisSideDirection: fasd, fallbackStrategy: fs, flipAlignment: fa, padding: pad, altBoundary: alt };
    if (fb && fb.length) opts.fallbackPlacements = fb;
    mws.push(flip(opts));
    specs.push(`flip,${+main},${cross === false ? 0 : cross === true ? 1 : 2},${['none','start','end'].indexOf(fasd)},${fs === 'bestFit' ? 0 : 1},${+fa},${pad},${+alt},${fb && fb.length ? fb.join('+') : '_'}`);
  }
  if (doShift && !shiftFirst) shiftSpec();
  if (rnd() < 0.5) {
    const pad = ri(0, 10), shrink = rnd() < 0.5;
    mws.push(size({ padding: pad, apply: ({ availableWidth, availableHeight }) => {
      lastSize = { w: availableWidth, h: availableHeight };
      if (shrink) floating.height = Math.max(0, Math.min(origFl.height, Math.floor(availableHeight)));
    } }));
    specs.push(`size,${pad},${+shrink}`);
  }
  if (rnd() < 0.5) {
    const pad = ri(0, 12);
    mws.push(arrow({ element: arrowEl, padding: pad }));
    specs.push(`arrow,${arrowEl.width},${arrowEl.height},${pad}`);
  }
  if (rnd() < 0.5) {
    const strat = pick(['referenceHidden', 'escaped']), pad = ri(0, 8);
    mws.push(hide({ strategy: strat, padding: pad }));
    specs.push(`hide,${strat === 'referenceHidden' ? 'r' : 'e'},${pad}`);
  }
  const platform = {
    getElementRects: ({ reference: r, floating: f }) => ({ reference: { ...refRect }, floating: { x: 0, y: 0, width: floating.width, height: floating.height } }),
    getDimensions: el => el.tag === 'arrow' ? { width: el.width, height: el.height } : { width: floating.width, height: floating.height },
    getClippingRect: ({ element, boundary, rootBoundary }) => {
      let root = vp;
      if (boundary && boundary !== 'clippingAncestors') return inter(root, boundary);
      const c = element.tag === 'floating' ? fclip : rclip;
      return c ? inter(root, c) : { ...root };
    },
    isRTL: () => rtl,
  };
  const res = await computePosition(reference, floating, { placement, middleware: mws, platform });
  const d = res.middlewareData;
  const out = [
    res.x, res.y, res.placement,
    d.offset ? `${d.offset.x}:${d.offset.y}:${d.offset.placement}` : '_',
    d.shift ? `${d.shift.x}:${d.shift.y}:${+d.shift.enabled.x}:${+d.shift.enabled.y}` : '_',
    d.flip ? `${d.flip.index ?? 0}` : '_',
    d.arrow ? `${d.arrow.x ?? '_'}:${d.arrow.y ?? '_'}:${d.arrow.centerOffset}:${d.arrow.alignmentOffset ?? '_'}` : '_',
    d.hide ? `${d.hide.referenceHidden === undefined ? '_' : +d.hide.referenceHidden}:${d.hide.escaped === undefined ? '_' : +d.hide.escaped}` : '_',
    lastSize ? `${lastSize.w}:${lastSize.h}` : '_',
    `${floating.width}:${floating.height}`,
  ];
  lines.push([R(vp), R(refRect), `${origFl.width}:${origFl.height}`, placement, +rtl, R(fclip), R(rclip), specs.join(';') || '_', '=>', ...out].join(' '));
}
console.log('# 由 @floating-ui/core 1.7.5 生成（scratchpad 中的 gen.mjs，mulberry32 种子 12345）：输入 => 输出，见 FloatingUiDifferentialTests');
console.log(lines.join('\n'));
