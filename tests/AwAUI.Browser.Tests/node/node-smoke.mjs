// 不需要浏览器的冒烟测试与测量：在 Node 中装上 dom-shim.mjs，加载 BrowserDemo 的发布产物（同一个 main.js、
// 同一个 awaui-browser.js 胶水、同一个 .NET WASM 运行时，解释器 + jiterpreter），向垫片派发合成的 DOM 事件并断言 C# 侧状态。
// 它验证的是「胶水 ↔ [JSImport]/[JSExport] ↔ BrowserWindow ↔ 应用」整条接线与 WASM 下的耗时；
// 浏览器自己的行为（事件顺序、ResizeObserver、putImageData 的真实开销）由 browser-smoke.mjs 在真实 Chromium 中验证。
//
// 用法：node node-smoke.mjs <publish>/wwwroot [--bench-frames 30] [--no-tests]
import { pathToFileURL } from 'node:url';
import { join, resolve } from 'node:path';
import { installDomShim } from './dom-shim.mjs';

const argv = process.argv.slice(2);
const opt = (name, fallback) => { const i = argv.indexOf(name); return i >= 0 && i + 1 < argv.length ? argv[i + 1] : fallback; };
const root = resolve(argv[0] ?? '.');
const benchFrames = Number(opt('--bench-frames', '30'));
const tick = () => new Promise((r) => setTimeout(r, 0)); // 让 MessageChannel 宏任务与 Promise 跑完

let failures = 0;
const check = (name, ok, detail = '') => {
    console.log(`${ok ? 'PASS' : 'FAIL'}  ${name}${detail ? `  (${detail})` : ''}`);
    if (!ok) failures++;
};

const shim = installDomShim({ dpr: 2 });
const { canvas, document } = shim;
const t0 = performance.now();
await import(pathToFileURL(join(root, 'main.js')).href);
console.log(`# node ${process.version}, startup ${(performance.now() - t0).toFixed(0)} ms`);
const probe = globalThis.awauiDemo.probe;
const state = () => JSON.parse(probe.State());
const textarea = document.querySelector('textarea');

const pointer = (type, x, y, extra = {}) => canvas.fire(type, {
    pointerId: 1, pointerType: 'mouse', clientX: canvas.rect.left + x, clientY: canvas.rect.top + y,
    button: -1, buttons: 0, pressure: 0, shiftKey: false, ctrlKey: false, altKey: false, metaKey: false, ...extra,
});

if (!argv.includes('--no-tests')) {
    let s = state();
    check('initial metrics from attach', s.clientWidth === 640 && s.clientHeight === 440 && s.scale === 2 && s.pixelWidth === 1280 && s.pixelHeight === 880,
        `${s.clientWidth}x${s.clientHeight} @${s.scale} → ${s.pixelWidth}x${s.pixelHeight}`);
    check('canvas styled for touch', canvas.style.touchAction === 'none');
    check('proxy textarea created', !!textarea && textarea.getAttribute('inputmode') === 'none');
    check('ResizeObserver uses device-pixel-content-box', shim.resizeObservers[0]?.targets[0]?.box === 'device-pixel-content-box');

    // 第一帧：Main 请求了一帧
    check('one rAF pending after start', shim.rafQueue.size === 1);
    shim.flushFrames(1000);
    s = state();
    check('frame rendered and presented', s.frames === 1 && s.presents === 1 && canvas.width === 1280 && canvas.height === 880, `frames=${s.frames} canvas=${canvas.width}x${canvas.height}`);
    const px = canvas.getContext('2d').getImageData(40, 40, 1, 1).data;
    check('pixels reached the canvas', px[3] === 255 && px[0] > 200, [...px].join(','));
    check('animation keeps requesting frames', shim.rafQueue.size === 1);

    // 关闭动画：再出一帧后不再挂 rAF
    probe.SetAnimating(false);
    shim.flushFrames(1016);
    check('idle: no rAF pending', shim.rafQueue.size === 0 && state().callbackPending === false);

    // 悬停 → 只请求一帧
    pointer('pointermove', 446, 316);
    s = state();
    check('pointermove → hover', s.hovered === 'primary', `hovered=${s.hovered}`);
    check('cursor', canvas.style.cursor === 'pointer', canvas.style.cursor);
    check('input requests exactly one frame', shim.rafQueue.size === 1);
    pointer('pointermove', 447, 316);
    check('repeated requests coalesce', shim.rafQueue.size === 1);
    shim.flushFrames(1032);
    check('frame delivered, idle again', state().frames === 3 && shim.rafQueue.size === 0, `frames=${state().frames}`);

    // 点击：pointerdown 阻止默认行为、捕获指针、聚焦代理文本框
    const down = pointer('pointerdown', 446, 316, { button: 0, buttons: 1, pressure: 0.5 });
    check('pointerdown preventDefault + capture', down.defaultPrevented && canvas.hasPointerCapture(1));
    check('focus moved to proxy', document.activeElement === textarea && state().focused === true);
    pointer('pointerup', 446, 316, { button: 0, buttons: 0 });
    check('click', state().clicks === 1);

    // 合并事件：一次 pointermove 带 3 个采样
    const chip = { x: state().chipX, y: state().chipY };
    pointer('pointerdown', chip.x, chip.y, { button: 0, buttons: 1 });
    const samples = [1, 2, 3].map((i) => ({ pointerId: 1, pointerType: 'mouse', clientX: canvas.rect.left + chip.x - 10 * i, clientY: canvas.rect.top + chip.y, button: -1, buttons: 1, pressure: 0.5, timeStamp: 2000 + i }));
    pointer('pointermove', chip.x - 30, chip.y, { buttons: 1, getCoalescedEvents: () => samples });
    s = state();
    check('coalesced samples delivered (drag)', Math.abs(s.chipX - (chip.x - 30)) < 1e-3, `chip=${s.chipX}`);
    pointer('pointerup', chip.x - 30, chip.y, { button: 0, buttons: 0 });

    // CSS 缩放：canvas 显示为一半大小
    canvas.rect = { ...canvas.rect, width: 320, height: 220 };
    pointer('pointermove', 223, 158);
    check('CSS-transformed canvas coordinates', state().hovered === 'primary', `hovered=${state().hovered}`);
    canvas.rect = { ...canvas.rect, width: 640, height: 440 };

    // 触摸
    const touch = (type, id, x, y, buttons) => canvas.fire(type, { pointerId: id, pointerType: 'touch', clientX: 16 + x, clientY: 16 + y, button: type === 'pointermove' ? -1 : 0, buttons, pressure: buttons ? 0.6 : 0 });
    touch('pointerdown', 7, 100, 100, 1);
    touch('pointerdown', 8, 200, 100, 1);
    check('two touch pointers', state().touches === 2);
    touch('pointerup', 7, 100, 100, 0);
    touch('pointercancel', 8, 200, 100, 0);
    check('touch up / cancel', state().touches === 0);

    // 滚轮：deltaMode=1（按行）
    const wheel = canvas.fire('wheel', { clientX: 100, clientY: 100, deltaX: 0, deltaY: 3, deltaMode: 1, buttons: 0 });
    s = state();
    check('wheel line mode + preventDefault', wheel.defaultPrevented && s.wheelY === -120, `wheelY=${s.wheelY}`);

    // 文本框：点击后键入
    pointer('pointerdown', 320, 404, { button: 0, buttons: 1 });
    pointer('pointerup', 320, 404, { button: 0, buttons: 0 });
    check('text field focused + soft keyboard', state().fieldFocused && textarea.getAttribute('inputmode') === 'text');
    const key = (type, code, keyValue, extra = {}) => textarea.fire(type, { code, key: keyValue, keyCode: extra.keyCode ?? 0, repeat: false, isComposing: false, ...extra });
    const kd = key('keydown', 'KeyA', 'a', { keyCode: 65 });
    const bi = textarea.fire('beforeinput', { inputType: 'insertText', data: 'a', isComposing: false });
    key('keyup', 'KeyA', 'a', { keyCode: 65 });
    s = state();
    check('printable key not prevented, beforeinput prevented + committed', !kd.defaultPrevented && bi.defaultPrevented && s.text === 'a' && s.lastKey === 'KeyA',
        `kd=${kd.defaultPrevented} bi=${bi.defaultPrevented} text="${s.text}"`);
    const tab = key('keydown', 'Tab', 'Tab', { keyCode: 9 });
    check('Tab prevented', tab.defaultPrevented);
    // AltGr：Windows 上报告为 Ctrl+Alt；产生字符时不能阻止，否则 beforeinput 不派发（审查发现的问题）
    const altGr = key('keydown', 'KeyQ', '@', { keyCode: 81, ctrlKey: true, altKey: true });
    const altGraphState = key('keydown', 'KeyE', '€', { keyCode: 69, ctrlKey: true, getModifierState: (m) => m === 'AltGraph' });
    const ctrlS = key('keydown', 'KeyS', 's', { keyCode: 83, ctrlKey: true });
    check('AltGr (Ctrl+Alt / AltGraph) characters not prevented, Ctrl+S prevented', !altGr.defaultPrevented && !altGraphState.defaultPrevented && ctrlS.defaultPrevented,
        `altGr=${altGr.defaultPrevented} altGraph=${altGraphState.defaultPrevented} ctrlS=${ctrlS.defaultPrevented}`);
    const ctrlC = key('keydown', 'KeyC', 'c', { keyCode: 67, ctrlKey: true });
    check('Ctrl+C not prevented (browser fires copy)', !ctrlC.defaultPrevented);
    const copy = textarea.fire('copy', { clipboardData: { data: {}, setData(t, v) { this.data[t] = v; }, getData(t) { return this.data[t] ?? ''; } } });
    check('copy event served by CopyHandler', copy.defaultPrevented && copy.clipboardData.data['text/plain'] === 'a');

    // 后备路径：beforeinput 不可取消的浏览器
    textarea.value = 'xy';
    textarea.fire('input', { inputType: 'insertText', isComposing: false });
    check('input fallback commits and clears', state().text === 'axy' && textarea.value === '', `text="${state().text}"`);

    // 输入法：Chrome 的事件顺序，组合中的 keydown（keyCode 229）不交给框架也不阻止
    textarea.fire('compositionstart', { data: '' });
    const imeKey = key('keydown', 'KeyN', 'Process', { keyCode: 229, isComposing: true });
    textarea.fire('compositionupdate', { data: 'n' });
    textarea.value = 'n'; textarea.selectionStart = textarea.selectionEnd = 1;
    const ib = textarea.fire('beforeinput', { inputType: 'insertCompositionText', data: 'n', isComposing: true });
    textarea.fire('input', { inputType: 'insertCompositionText', isComposing: true });
    textarea.fire('compositionupdate', { data: 'nihao' });
    textarea.value = 'nihao'; textarea.selectionStart = textarea.selectionEnd = 2;
    textarea.fire('input', { inputType: 'insertCompositionText', isComposing: true });
    s = state();
    check('composition in progress', s.composing && s.composition === 'nihao' && s.compositionCaret === 2 && s.lastKey === 'KeyC', // 组合中的 KeyN（229）没有交给框架
        `"${s.composition}" caret=${s.compositionCaret} lastKey=${s.lastKey}`);
    check('IME keydown / composition beforeinput not prevented', !imeKey.defaultPrevented && !ib.defaultPrevented);
    textarea.fire('compositionend', { data: '你好' });
    textarea.fire('input', { inputType: 'insertCompositionText', isComposing: false }); // Firefox 在 compositionend 之后的 input
    await tick();
    s = state();
    check('composition committed once', !s.composing && s.text === 'axy你好' && textarea.value === '', `text="${s.text}" textarea="${textarea.value}"`);

    // 粘贴
    const paste = textarea.fire('paste', { clipboardData: { getData: () => 'Z' } });
    check('paste', paste.defaultPrevented && state().text === 'axy你好Z');

    // 尺寸：ResizeObserver（带设备像素盒）→ OnResized，canvas 在下一次呈现时改尺寸
    probe.SetAnimating(false);
    shim.flushFrames(5000);
    shim.resize(500.5, 300, { device: true });
    s = state();
    check('resize via device-pixel-content-box', s.clientWidth === 500.5 && s.pixelWidth === 1001 && s.pixelHeight === 600 && shim.rafQueue.size === 1,
        `${s.clientWidth} → ${s.pixelWidth}x${s.pixelHeight}`);
    check('canvas not resized before present', canvas.width === 1280);
    shim.flushFrames(5016);
    check('canvas resized at present', canvas.width === 1001 && canvas.height === 600, `${canvas.width}x${canvas.height}`);

    // DPR 变化（matchMedia resolution 查询）
    shim.setDpr(1.5);
    s = state();
    check('devicePixelRatio change', s.scale === 1.5 && s.pixelWidth === 751 && s.pixelHeight === 450, `@${s.scale} → ${s.pixelWidth}x${s.pixelHeight}`);
    check('resolution query re-armed', shim.mediaQueries.at(-1).media === '(resolution: 1.5dppx)');
    shim.flushFrames(5032);
    shim.resize(640, 440, { device: false });
    shim.setDpr(2);
    shim.flushFrames(5048);
    check('restored', state().pixelWidth === 1280 && canvas.width === 1280);

    // 可见性：页面隐藏 / canvas 滚出视口
    probe.SetAnimating(true);
    check('animating: rAF pending', shim.rafQueue.size === 1);
    shim.setVisibility('hidden');
    check('hidden page cancels rAF', shim.rafQueue.size === 0 && state().visible === false);
    shim.setVisibility('visible');
    check('visible again reschedules', shim.rafQueue.size === 1 && state().visible === true);
    shim.intersect(false);
    check('offscreen canvas cancels rAF', shim.rafQueue.size === 0);
    shim.intersect(true);
    check('onscreen reschedules', shim.rafQueue.size === 1);
    probe.SetAnimating(false);
    shim.flushFrames(6000);

    // 失焦
    textarea.blur();
    s = state();
    check('blur', s.focused === false && s.fieldFocused === false);

    // 额外窗口：CreateWindow 新建 canvas；附加透明 canvas（反预乘路径）；关闭后分离
    const bodyChildren = document.body.children.length;
    const newId = probe.TestWindow(null, false, 0, 0, 255, 1);
    const created = document.getElementById(newId);
    check('CreateWindow creates and sizes a canvas', !!created && created.width === 128 && created.height === 96 && document.title === 'AwAUI BrowserDemo',
        `${created?.width}x${created?.height} title=${document.title}`);
    const blue = created.getContext('2d').getImageData(5, 5, 1, 1).data;
    check('opaque window pixels', blue[2] === 255 && blue[3] === 255 && blue[0] === 0, [...blue].join(','));
    const alpha = document.register('alpha', document.createElement('canvas'));
    alpha.rect = { left: 0, top: 600, width: 10, height: 10 };
    probe.TestWindow('alpha', true, 255, 0, 0, 0.5);
    const red = alpha.getContext('2d').getImageData(1, 1, 1, 1).data;
    check('transparent canvas gets unpremultiplied pixels', alpha.getContext('2d').options.alpha === true && red[0] === 255 && Math.abs(red[3] - 128) <= 1,
        [...red].join(','));
    const remaining = probe.CloseTestWindows();
    check('close detaches windows', remaining === 1 && document.getElementById(newId) === null && alpha.listenerCount('pointerdown') === 0
        && document.body.children.length === bodyChildren + 1, `remaining=${remaining}`);
    let missing = null;
    try { probe.TestWindow('no-such-canvas', false, 0, 0, 0, 1); } catch (e) { missing = e; }
    check('AttachCanvas with unknown id throws ArgumentException', /No <canvas> element/.test(missing?.message ?? ''), missing?.message);

    // 页面先以 alpha: true 取过 2D 上下文，再以不透明选项附加：以实际上下文为准走反预乘（审查发现的问题）
    const pre = document.register('prealpha', document.createElement('canvas'));
    pre.rect = { left: 0, top: 620, width: 10, height: 10 };
    pre.getContext('2d', { alpha: true });
    probe.TestWindow('prealpha', false, 255, 0, 0, 0.5);
    const preRed = pre.getContext('2d').getImageData(1, 1, 1, 1).data;
    check('pre-existing alpha context gets unpremultiplied pixels', preRed[0] === 255 && Math.abs(preRed[3] - 128) <= 1, [...preRed].join(','));
    let twice = null;
    try { probe.TestWindow('prealpha', false, 0, 0, 0, 1); } catch (e) { twice = e; }
    check('attaching the same canvas twice throws', /already attached/.test(twice?.message ?? ''), twice?.message);
    const gl = document.register('glcanvas', document.createElement('canvas'));
    gl.getContext = () => null; // 已有 WebGL 上下文：getContext('2d') 返回 null
    let noCtx = null;
    try { probe.TestWindow('glcanvas', false, 0, 0, 0, 1); } catch (e) { noCtx = e; }
    check('canvas without a 2D context throws', /2D context/.test(noCtx?.message ?? ''), noCtx?.message);
    check('failed attaches leave no windows behind', probe.CloseTestWindows() === 1);

    // 异步剪贴板在 Node 中不存在：优雅地返回失败
    check('clipboard unavailable is graceful', (await probe.ClipboardRoundTrip('x')) === 'unavailable:write-failed');

    // 合成的恶意事件：NaN / 巨大数值 / 超长字符串不应抛出
    let threw = null;
    try {
        canvas.fire('pointermove', { pointerId: 2 ** 31 - 1, pointerType: 'pen', clientX: NaN, clientY: Infinity, button: 99, buttons: -1, pressure: NaN, timeStamp: -1 });
        canvas.fire('wheel', { clientX: 1e308, clientY: -1e308, deltaX: Infinity, deltaY: NaN, deltaMode: 9, buttons: 0 });
        textarea.fire('keydown', { code: 'K'.repeat(100000), key: 'x'.repeat(100000), keyCode: -5 });
        textarea.fire('compositionupdate', { data: '字'.repeat(50000) });
        textarea.fire('compositionend', { data: null });
        textarea.fire('beforeinput', { inputType: 'insertText', data: '\u0000\u0001' });
        shim.resize(1e300, -1);
        shim.resize(640, 440);
    } catch (e) { threw = e; }
    check('hostile synthetic events are harmless', threw === null, threw?.message ?? '');
    s = state();
    check('state still sane', s.pixelWidth === 1280 && Number.isFinite(s.chipX), `pixel=${s.pixelWidth}`);
}

// ------------------------------------------------------------ 测量（Node 下的解释器 + jiterpreter）
probe.SetAnimating(false);
shim.flushFrames();
const bench = JSON.parse(probe.Benchmark(5, benchFrames));
console.log(`# demo frame @ ${bench.pixelWidth}x${bench.pixelHeight} (Node, ${bench.frames} frames): total ${bench.total.toFixed(2)} ms ` +
    `(median ${bench.median.toFixed(2)}, min ${bench.min.toFixed(2)}) = record ${bench.record.toFixed(2)} + raster ${bench.raster.toFixed(2)} + ` +
    `present ${bench.present.toFixed(3)} (shim putImageData = one Uint8ClampedArray.set)`);

console.log(failures === 0 ? '# all node checks passed' : `# ${failures} node check(s) FAILED`);
process.exit(failures === 0 ? 0 : 1);
