import fs from 'node:fs';
import path from 'node:path';
import { __unstable__loadDesignSystem } from '../node_modules/tailwindcss/dist/lib.mjs';
const root = path.resolve(path.dirname(new URL(import.meta.url).pathname), '..');
async function loadStylesheet(id, base) {
  const p = id === 'tailwindcss' ? path.join(root, 'node_modules/tailwindcss/index.css') : path.resolve(base, id);
  return { path: p, base: path.dirname(p), content: fs.readFileSync(p, 'utf8') };
}
const ds = await __unstable__loadDesignSystem('@import "tailwindcss";', { base: root, loadStylesheet });
const list = ds.getClassList().map(([n]) => n);
const variants = ds.getVariants();
const vnames = [];
for (const v of variants) {
  if (v.values.length === 0) vnames.push(v.name);
  else for (const val of v.values.slice(0, 3)) vnames.push(v.name + (v.hasDash ? '-' : '') + val);
}
fs.writeFileSync(process.argv[2], list.join('\n') + '\n');
// variant x sample utility
const vc = vnames.map(v => v + ':flex').concat(vnames.slice(0, 200).map(v => v + ':bg-red-500/50'));
fs.writeFileSync(process.argv[3], vc.join('\n') + '\n');
console.log(list.length, vnames.length);
