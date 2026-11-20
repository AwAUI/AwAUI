// 浏览器宿主：每个 requestAnimationFrame 调用一次 C# 渲染，然后把 WASM 堆上的帧缓冲直接 putImageData。
// ?scale=1 或 ?scale=2 可覆盖 devicePixelRatio。
import { dotnet } from './_framework/dotnet.js';

const runtime = await dotnet.create();
const exports = await runtime.getAssemblyExports(runtime.getConfig().mainAssemblyName);
const host = exports.WasmProbe.CanvasHost;

const canvas = document.getElementById('view');
const hud = document.getElementById('hud');
const ctx = canvas.getContext('2d', { alpha: false });
const query = new URLSearchParams(location.search);
const scale = Number(query.get('scale')) || window.devicePixelRatio || 1;
const logicalW = 640, logicalH = 440;
const w = Math.round(logicalW * scale), h = Math.round(logicalH * scale);
canvas.width = w;
canvas.height = h;
let ptr = host.Resize(w, h, scale);

let image = null, imageBuffer = null;
function frameImage() {
    // 内存增长后 HEAPU8 会换成新的 ArrayBuffer，视图需要重建；平时复用同一个 ImageData（零拷贝视图）
    const heap = runtime.localHeapViewU8();
    if (image === null || imageBuffer !== heap.buffer) {
        imageBuffer = heap.buffer;
        image = new ImageData(new Uint8ClampedArray(heap.buffer, ptr, w * h * 4), w, h);
    }
    return image;
}

const toLogical = (e) => { const r = canvas.getBoundingClientRect(); return [(e.clientX - r.left) * logicalW / r.width, (e.clientY - r.top) * logicalH / r.height]; };
canvas.addEventListener('pointermove', (e) => host.Pointer(0, ...toLogical(e)));
canvas.addEventListener('pointerdown', (e) => host.Pointer(1, ...toLogical(e)));
canvas.addEventListener('pointerup', (e) => host.Pointer(2, ...toLogical(e)));
canvas.addEventListener('pointerleave', () => host.Pointer(3, 0, 0));

let frames = 0, renderSum = 0, blitSum = 0, last = performance.now();
function tick(now) {
    const renderMs = host.RenderFrame(now / 1000);
    const b0 = performance.now();
    ctx.putImageData(frameImage(), 0, 0);
    blitSum += performance.now() - b0;
    renderSum += renderMs;
    frames++;
    if (now - last >= 1000) {
        hud.textContent = `${w}x${h} px @${scale}x — ${(frames * 1000 / (now - last)).toFixed(0)} fps, ` +
            `C# ${(renderSum / frames).toFixed(1)} ms/frame (record ${host.LastRecordMs().toFixed(2)} ms), ` +
            `putImageData ${(blitSum / frames).toFixed(2)} ms`;
        frames = 0; renderSum = 0; blitSum = 0; last = now;
    }
    requestAnimationFrame(tick);
}
requestAnimationFrame(tick);
