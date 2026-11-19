// usage: node merge-cases.mjs <pool1.txt> <pool2.txt> <out.txt>
import fs from 'node:fs';
import { twMerge } from '../pkg/tailwind-merge-3.7.0/package/dist/bundle-mjs.mjs';
const pool = [...new Set([fs.readFileSync(process.argv[2],'utf8'), fs.readFileSync(process.argv[3],'utf8')].join('\n').split('\n').map(s=>s.trim()).filter(Boolean))];
let seed = 12345;
function rnd(n) { seed = (seed * 1103515245 + 12345) & 0x7fffffff; return seed % n; }
const byHead = new Map();
for (const c of pool) { const base = c.split(':').pop().replace(/^[-!]/,''); const head = base.split('-')[0]; if (!byHead.has(head)) byHead.set(head, []); byHead.get(head).push(c); }
const heads = [...byHead.keys()];
const cases = [];
const hand = [
 'px-2 py-1 p-3', 'p-3 px-2', 'px-2 p-3 py-1', 'text-sm text-red-500', 'text-red-500 text-blue-500', 'text-sm/6 leading-7', 'leading-7 text-sm/6', 'text-sm leading-7 text-lg',
 'hover:bg-red-500 bg-blue-500 hover:bg-green-500', 'md:hover:p-2 hover:md:p-4', 'bg-red-500/50 bg-blue-500', '!p-2 p-4', 'p-4! !p-2', 'border-2 border-red-500 border-4',
 'rounded-md rounded-t-lg rounded-lg', 'inset-0 top-2', 'top-2 inset-0', 'grid-cols-2 grid-cols-[1fr_auto]', 'w-4 size-8', 'size-8 w-4', 'h-9 has-[>svg]:px-3 px-4 has-[>svg]:px-2',
 'shadow-xs shadow-none', 'shadow-lg shadow-red-500', 'ring-2 ring-ring/50 ring-[3px]', 'outline-none outline-hidden', 'focus-visible:ring-[3px] focus-visible:ring-4',
 '[mask-type:luminance] [mask-type:alpha]', '[--x:1] [--x:2]', 'data-[state=open]:bg-red-500 data-[state=open]:bg-blue-500', 'group-hover:flex group-hover:hidden',
 '[&_svg]:size-4 [&_svg]:size-3', 'gap-2 gap-x-4', 'gap-x-4 gap-2', 'font-bold font-[600]', 'font-sans font-mono', 'tracking-tight tracking-[0.1em]',
 'translate-x-2 -translate-x-1/2', 'scale-95 scale-x-110', 'transition transition-all', 'duration-200 duration-[1s]', 'animate-spin animate-none',
 'bg-linear-to-r bg-radial', 'from-red-500 from-10%', 'line-clamp-2 overflow-auto', 'overflow-auto line-clamp-2', 'block flex', 'hidden md:flex flex', 'sr-only not-sr-only',
 'touch-pan-x touch-none', 'touch-none touch-pan-x touch-pinch-zoom', '@container @container/main', 'container mx-auto', 'stroke-2 stroke-red-500', 'fill-red-500 fill-none',
 'bg-(--a) bg-(--b)', 'p-(--a) px-(--b)', 'text-(length:--x) text-(--c)', 'mt-2 -mt-4', 'z-10 z-[999]', 'opacity-50 opacity-[.67]', 'dark:text-white dark:text-black',
 'before:content-[""] before:content-none', 'aria-invalid:border-red-500 aria-invalid:border-transparent', 'not-first:mt-2 not-first:mt-4', '*:p-2 *:p-4', 'file:border-0 file:border',
 'cursor-pointer cursor-[url(a.cur),_pointer]', 'col-span-2 col-start-1', 'row-span-2 row-end-3', 'justify-start justify-center', 'items-center items-start', 'self-auto self-end',
 'decoration-2 decoration-red-500 decoration-4', 'underline no-underline', 'whitespace-nowrap whitespace-normal', 'text-ellipsis truncate', 'truncate text-clip',
];
for (const h of hand) cases.push(h);
for (let i = 0; i < 1200; i++) {
  const n = 2 + rnd(5);
  const parts = [];
  const h = heads[rnd(heads.length)];
  for (let k = 0; k < n; k++) {
    const group = rnd(3) === 0 ? byHead.get(heads[rnd(heads.length)]) : byHead.get(h);
    parts.push(group[rnd(group.length)]);
  }
  cases.push(parts.join(' '));
}
const out = cases.map(c => c + '\t' + twMerge(c));
fs.writeFileSync(process.argv[4], out.join('\n') + '\n');
console.log(cases.length);
