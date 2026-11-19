// usage: node ref.mjs <input.css> <candidates.txt> <out-per-candidate.txt> [full-out.css]
import fs from 'node:fs';
import path from 'node:path';
import { compile, __unstable__loadDesignSystem, Polyfills } from '../node_modules/tailwindcss/dist/lib.mjs';
const root = path.resolve(path.dirname(new URL(import.meta.url).pathname), '..');
async function loadStylesheet(id, base) {
  let p;
  if (id === 'tailwindcss') p = path.join(root, 'node_modules/tailwindcss/index.css');
  else if (id === 'tailwindcss/theme.css' || id === 'tailwindcss/theme') p = path.join(root, 'node_modules/tailwindcss/theme.css');
  else if (id === 'tailwindcss/preflight.css') p = path.join(root, 'node_modules/tailwindcss/preflight.css');
  else if (id === 'tailwindcss/utilities.css') p = path.join(root, 'node_modules/tailwindcss/utilities.css');
  else if (id === 'tw-animate-css') p = path.join(root, 'node_modules/tw-animate-css/dist/tw-animate.css');
  else if (id === 'shadcn/tailwind.css') p = path.join(root, 'pkg/package/dist/tailwind.css');
  else p = path.resolve(base, id);
  return { path: p, base: path.dirname(p), content: fs.readFileSync(p, 'utf8') };
}
const [,, inCss, candFile, outFile, fullOut] = process.argv;
const css = fs.readFileSync(inCss, 'utf8');
const cands = fs.readFileSync(candFile, 'utf8').split('\n').map(s => s.trim()).filter(Boolean);
const ds = await __unstable__loadDesignSystem(css, { base: path.dirname(inCss), loadStylesheet });
const res = ds.candidatesToCss(cands);
let out = [];
for (let i = 0; i < cands.length; i++) {
  out.push('### ' + cands[i]);
  out.push(res[i] == null ? '(none)' : res[i].trimEnd());
}
fs.writeFileSync(outFile, out.join('\n') + '\n');
if (fullOut) {
  const c = await compile(css, { base: path.dirname(inCss), loadStylesheet, polyfills: Polyfills.None });
  fs.writeFileSync(fullOut, c.build(cands));
}
