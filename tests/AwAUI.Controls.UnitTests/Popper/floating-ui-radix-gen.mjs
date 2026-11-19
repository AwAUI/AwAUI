// 生成 floating-ui-radix-cases.txt：按 Radix Popper（@radix-ui/react-popper 1.2）的中间件组合运行 @floating-ui/core 1.7.5，
// 作为 PopperLayout.Compute 的参考输出。偏重小锚点（比箭头还小）、贴边、RTL、显式碰撞边界与裁剪祖先。
// 重新生成（仓库外的临时目录）：npm install @floating-ui/core@1.7.5 && node floating-ui-radix-gen.mjs 800 > floating-ui-radix-cases.txt
import { computePosition, offset, flip, shift, limitShift, size, arrow, hide } from '@floating-ui/core';

let seed = Number(process.argv[3] || 20261119);
function rnd() { seed |= 0; seed = seed + 0x6D2B79F5 | 0; let t = Math.imul(seed ^ seed >>> 15, 1 | seed); t = t + Math.imul(t ^ t >>> 7, 61 | t) ^ t; return ((t ^ t >>> 14) >>> 0) / 4294967296; }
const ri = (a, b) => a + Math.floor(rnd() * (b - a + 1));
const rq = (a, b) => ri(a, b) + (rnd() < 0.3 ? ri(0, 3) / 4 : 0);
const pick = arr => arr[ri(0, arr.length - 1)];
function inter(a, b) {
  const left = Math.max(a.x, b.x), top = Math.max(a.y, b.y), right = Math.min(a.x + a.width, b.x + b.width), bottom = Math.min(a.y + a.height, b.y + b.height);
  return { x: left, y: top, width: right - left, height: bottom - top };
}
const R = r => r ? `${r.x}:${r.y}:${r.width}:${r.height}` : '_';

// Radix 的 transformOrigin 中间件（原样移植）
const transformOrigin = (options) => ({
  name: 'transformOrigin', options,
  fn(data) {
    const { placement, rects, middlewareData } = data;
    const cannotCenterArrow = middlewareData.arrow?.centerOffset !== 0;
    const isArrowHidden = cannotCenterArrow;
    const arrowWidth = isArrowHidden ? 0 : options.arrowWidth;
    const arrowHeight = isArrowHidden ? 0 : options.arrowHeight;
    const [placedSide, placedAlign] = getSideAndAlignFromPlacement(placement);
    const noArrowAlign = { start: '0%', center: '50%', end: '100%' }[placedAlign];
    const arrowXCenter = (middlewareData.arrow?.x ?? 0) + arrowWidth / 2;
    const arrowYCenter = (middlewareData.arrow?.y ?? 0) + arrowHeight / 2;
    let x = '', y = '';
    if (placedSide === 'bottom') { x = isArrowHidden ? noArrowAlign : `${arrowXCenter}px`; y = `${-arrowHeight}px`; }
    else if (placedSide === 'top') { x = isArrowHidden ? noArrowAlign : `${arrowXCenter}px`; y = `${rects.floating.height + arrowHeight}px`; }
    else if (placedSide === 'right') { x = `${-arrowHeight}px`; y = isArrowHidden ? noArrowAlign : `${arrowYCenter}px`; }
    else if (placedSide === 'left') { x = `${rects.floating.width + arrowHeight}px`; y = isArrowHidden ? noArrowAlign : `${arrowYCenter}px`; }
    return { data: { x, y } };
  },
});
function getSideAndAlignFromPlacement(placement) { const [side, align = 'center'] = placement.split('-'); return [side, align]; }

const lines = [];
const N = Number(process.argv[2] || 800);
for (let n = 0; n < N; n++) {
  const W = ri(200, 900), H = ri(200, 900);
  const vp = { x: 0, y: 0, width: W, height: H };
  const small = rnd() < 0.5;
  const aw = small ? ri(0, 24) : rq(0, 260), ah = small ? ri(0, 24) : rq(0, 160);
  // 一半场景贴近视口边缘
  const edge = rnd() < 0.5;
  const ax = edge ? pick([ri(-30, 30), ri(W - 60, W + 10)]) : rq(-40, W);
  const ay = edge ? pick([ri(-30, 30), ri(H - 60, H + 10)]) : rq(-40, H);
  const anchor = { x: ax, y: ay, width: aw, height: ah };
  const content = { width: rq(0, 360), height: rq(0, 360) };
  const side = pick(['top', 'right', 'bottom', 'left']);
  const align = pick(['start', 'center', 'end']);
  const sideOffset = rnd() < 0.6 ? ri(-4, 16) : 0;
  const alignOffset = rnd() < 0.4 ? ri(-20, 20) : 0;
  const avoidCollisions = rnd() < 0.85;
  const sticky = rnd() < 0.75 ? 'partial' : 'always';
  const hideWhenDetached = rnd() < 0.4;
  const arrowPadding = rnd() < 0.5 ? ri(0, 16) : 0;
  const hasArrow = rnd() < 0.6;
  const arrowW = hasArrow ? ri(4, 24) : 0, arrowH = hasArrow ? ri(2, 12) : 0;
  const padUniform = rnd() < 0.6;
  const cp = padUniform ? ri(0, 16) : { top: ri(0, 16), right: ri(0, 16), bottom: ri(0, 16), left: ri(0, 16) };
  const boundary = rnd() < 0.2 ? { x: ri(0, W / 3), y: ri(0, H / 3), width: ri(100, W), height: ri(100, H) } : null;
  const rtl = rnd() < 0.3;
  const fclip = rnd() < 0.2 ? { x: ri(-50, W / 2), y: ri(-50, H / 2), width: ri(50, W), height: ri(50, H) } : null;
  const rclip = rnd() < 0.4 ? { x: ri(-50, W / 2), y: ri(-50, H / 2), width: ri(50, W), height: ri(50, H) } : null;

  const placement = side + (align !== 'center' ? '-' + align : '');
  const collisionPadding = typeof cp === 'number' ? cp : { top: 0, right: 0, bottom: 0, left: 0, ...cp };
  const detectOverflowOptions = { padding: collisionPadding, boundary: boundary ? [boundary] : [], altBoundary: !!boundary };
  const arrowEl = { tag: 'arrow', width: arrowW, height: arrowH };
  let avail = null;
  const mws = [
    offset({ mainAxis: sideOffset + arrowH, alignmentAxis: alignOffset }),
    avoidCollisions && shift({ mainAxis: true, crossAxis: false, limiter: sticky === 'partial' ? limitShift() : undefined, ...detectOverflowOptions }),
    avoidCollisions && flip({ ...detectOverflowOptions }),
    size({ ...detectOverflowOptions, apply: ({ availableWidth, availableHeight }) => { avail = { w: availableWidth, h: availableHeight }; } }),
    hasArrow && arrow({ element: arrowEl, padding: arrowPadding }),
    transformOrigin({ arrowWidth: arrowW, arrowHeight: arrowH }),
    hideWhenDetached && hide({ strategy: 'referenceHidden', ...detectOverflowOptions }),
  ];
  const platform = {
    getElementRects: () => ({ reference: { ...anchor }, floating: { x: 0, y: 0, width: content.width, height: content.height } }),
    getDimensions: el => el.tag === 'arrow' ? { width: el.width, height: el.height } : { width: content.width, height: content.height },
    getClippingRect: ({ element, boundary: b }) => {
      // Radix 传的是元素数组；空数组 = 没有显式边界（与 DOM 平台把 [] 与裁剪祖先合并的结果相同：只有祖先）
      const list = Array.isArray(b) ? b : (b && b !== 'clippingAncestors' ? [b] : []);
      if (list.length > 0) return list.reduce((acc, r) => inter(acc, r), vp);
      const c = element.tag === 'floating' ? fclip : rclip;
      return c ? inter(vp, c) : { ...vp };
    },
    isRTL: () => rtl,
  };
  const res = await computePosition({ tag: 'reference' }, { tag: 'floating' }, { placement, middleware: mws, platform, strategy: 'fixed' });
  const d = res.middlewareData;
  const cannotCenterArrow = d.arrow?.centerOffset !== 0;
  const input = [R(vp), R(anchor), `${content.width}:${content.height}`, side, align, sideOffset, alignOffset, +avoidCollisions, sticky[0],
    +hideWhenDetached, arrowPadding, hasArrow ? `${arrowW}:${arrowH}` : '_', typeof cp === 'number' ? `${cp}` : `${cp.top}:${cp.right}:${cp.bottom}:${cp.left}`,
    R(boundary), +rtl, R(fclip), R(rclip)];
  const out = [res.x, res.y, res.placement, `${avail.w}:${avail.h}`, d.arrow ? `${d.arrow.x ?? '_'}:${d.arrow.y ?? '_'}` : '_', +cannotCenterArrow,
    hideWhenDetached ? +!!d.hide?.referenceHidden : '_', `${d.transformOrigin.x}|${d.transformOrigin.y}`];
  lines.push([...input, '=>', ...out].join(' '));
}
console.log('# 由 @floating-ui/core 1.7.5 + Radix Popper 的中间件组合生成（floating-ui-radix-gen.mjs）：输入 => 输出，见 PopperLayoutDifferentialTests');
console.log(lines.join('\n'));
