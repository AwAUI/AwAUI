// AwAUI.Browser 的 JS 胶水（ES 模块）。由 C# 经 JSHost.ImportAsync 以模块名 "AwAUI.Browser" 加载，
// 导出的函数对应 Interop/BrowserInterop.cs 的 [JSImport]；DOM 事件经 Interop/BrowserCallbacks.cs 的 [JSExport] 回到 C#。
//
// 约定（与 Translation/DomProtocol.cs 保持一致）：
//   指针事件种类  0 down, 1 up, 2 move, 3 enter, 4 leave, 5 cancel
//   指针设备      0 mouse, 1 touch, 2 pen
//   修饰键位      1 shift, 2 ctrl, 4 alt, 8 meta, 16 AltGraph（只用于键盘策略：Windows 上 AltGr 报告为 Ctrl+Alt）
//   attachCanvas  成功 [CSS 宽, CSS 高, DPR, 设备宽, 设备高, alpha 0/1, 可见 0/1]；找不到 null；ATTACH_ALREADY / ATTACH_NO_CONTEXT 时 [错误码]
//   组合事件      0 start, 1 update, 2 end
// 跨边界只传数字与短字符串；坐标、按键、DPR 的换算都在 C# 中完成（可在普通 .NET 上单元测试）。

const POINTER_DOWN = 0, POINTER_UP = 1, POINTER_MOVE = 2, POINTER_ENTER = 3, POINTER_LEAVE = 4, POINTER_CANCEL = 5;
const COMPOSITION_START = 0, COMPOSITION_UPDATE = 1, COMPOSITION_END = 2;
const FLAG_PREVENT_CONTEXT_MENU = 1, FLAG_COALESCED = 2, FLAG_PAUSE_OFFSCREEN = 4;
const ATTACH_ALREADY = -1, ATTACH_NO_CONTEXT = -2;

let runtime = null;
let cb = null; // C# 的 BrowserCallbacks
const windows = new Map(); // 句柄 → 窗口的 DOM 状态
let canvasCounter = 0;

// ------------------------------------------------------------------ 初始化与调度

let drainScheduled = false;
let drainChannel = null;

export async function init() {
    runtime = globalThis.getDotnetRuntime(0);
    const exports = await runtime.getAssemblyExports('AwAUI.Browser');
    cb = exports.AwAUI.Browser.Interop.BrowserCallbacks;
    drainChannel = new MessageChannel();
    drainChannel.port1.onmessage = () => { drainScheduled = false; cb.Drain(); };
    // Node 下不让这个端口单独维持进程存活
    drainChannel.port1.unref?.();
    drainChannel.port2.unref?.();
    document.addEventListener('visibilitychange', () => { for (const w of windows.values()) updateVisibility(w); });
}

export function scheduleDrain() {
    if (drainScheduled) return;
    drainScheduled = true;
    drainChannel.port2.postMessage(0);
}

export function now() { return performance.now(); }

export function setTitle(title) { document.title = title; }

// ------------------------------------------------------------------ canvas 附加 / 分离

export function createCanvas(cssWidth, cssHeight) {
    const canvas = document.createElement('canvas');
    canvas.id = `awaui-canvas-${++canvasCounter}`;
    canvas.style.display = 'block';
    canvas.style.width = `${cssWidth}px`;
    canvas.style.height = `${cssHeight}px`;
    document.body.appendChild(canvas);
    return canvas.id;
}

const modifiers = (e) => (e.shiftKey ? 1 : 0) | (e.ctrlKey ? 2 : 0) | (e.altKey ? 4 : 0) | (e.metaKey ? 8 : 0)
    | (typeof e.getModifierState === 'function' && e.getModifierState('AltGraph') ? 16 : 0);
const deviceCode = (t) => (t === 'touch' ? 1 : t === 'pen' ? 2 : 0);

export function attachCanvas(handle, canvasId, transparent, flags) {
    const canvas = document.getElementById(canvasId);
    if (!canvas || String(canvas.tagName).toUpperCase() !== 'CANVAS') return null;
    for (const other of windows.values()) if (other.canvas === canvas) return [ATTACH_ALREADY];
    let ctx = null;
    try {
        // 已有 WebGL / bitmaprenderer 上下文时返回 null；transferControlToOffscreen 之后抛 InvalidStateError
        ctx = canvas.getContext('2d', { alpha: !!transparent });
    } catch { /* 视为取不到 */ }
    if (!ctx) return [ATTACH_NO_CONTEXT];
    // 页面先以别的 alpha 选项取过 2D 上下文时，getContext 返回已有的上下文而忽略新选项：以实际值为准
    const alpha = typeof ctx.getContextAttributes === 'function' ? ctx.getContextAttributes().alpha !== false : !!transparent;

    const abort = new AbortController();
    const signal = abort.signal;
    const w = {
        handle, canvas, ctx, abort, input: null,
        raf: 0, image: null, imageBuffer: null, imagePtr: 0,
        cssWidth: 0, cssHeight: 0, dpr: globalThis.devicePixelRatio || 1, deviceWidth: NaN, deviceHeight: NaN,
        pageVisible: document.visibilityState !== 'hidden', intersecting: true, visible: true,
        composing: false, resetting: false, afterReset: false, caret: { x: 0, y: 0, w: 1, h: 16 },
        resizeObserver: null, intersectionObserver: null, dprQuery: null,
    };
    windows.set(handle, w);

    // 浏览器不再自行处理 canvas 上的平移 / 缩放 / 选择文本：手势与滚动由框架实现（design/09 touch-action）
    canvas.style.touchAction = 'none';
    canvas.style.userSelect = 'none';
    canvas.style.webkitUserSelect = 'none';
    canvas.style.webkitTapHighlightColor = 'transparent';
    canvas.style.outline = 'none';

    w.input = createProxyInput(w, signal);
    attachPointer(w, flags, signal);
    attachResize(w);
    if (flags & FLAG_PAUSE_OFFSCREEN && typeof IntersectionObserver === 'function') {
        w.intersectionObserver = new IntersectionObserver((entries) => {
            const last = entries[entries.length - 1];
            w.intersecting = !!last.isIntersecting;
            updateVisibility(w);
        });
        w.intersectionObserver.observe(canvas);
    }
    if (flags & FLAG_PREVENT_CONTEXT_MENU) canvas.addEventListener('contextmenu', (e) => e.preventDefault(), { signal });

    // 初始尺寸：ResizeObserver 的第一次回调要到下一次布局之后，这里先同步读一次
    const r = canvas.getBoundingClientRect();
    w.cssWidth = r.width;
    w.cssHeight = r.height;
    w.visible = w.pageVisible;
    return [w.cssWidth, w.cssHeight, w.dpr, NaN, NaN, alpha ? 1 : 0, w.visible ? 1 : 0];
}

export function detach(handle, removeCanvas) {
    const w = windows.get(handle);
    if (!w) return;
    windows.delete(handle);
    if (w.raf) cancelAnimationFrame(w.raf);
    w.abort.abort();
    w.resizeObserver?.disconnect();
    w.intersectionObserver?.disconnect();
    w.dprQuery?.removeEventListener('change', w.dprListener);
    w.input.remove();
    w.image = null;
    if (removeCanvas) w.canvas.remove();
}

// ------------------------------------------------------------------ 帧节拍与呈现

export function requestFrame(handle) {
    const w = windows.get(handle);
    if (!w || w.raf) return;
    w.raf = requestAnimationFrame((t) => {
        w.raf = 0;
        cb.OnFrame(handle, t);
    });
}

export function cancelFrame(handle) {
    const w = windows.get(handle);
    if (!w || !w.raf) return;
    cancelAnimationFrame(w.raf);
    w.raf = 0;
}

// 帧缓冲位于 WASM 线性内存：直接在 HEAPU8 的 ArrayBuffer 上建 ImageData，不经托管 → JS 复制。
// 内存增长后旧 ArrayBuffer 被分离，需要按新的 buffer 重建视图；平时复用同一个 ImageData。
// 注意：多线程 WASM 的内存是 SharedArrayBuffer，ImageData 不接受，届时需要先复制（或改用 WebGL 上传）。
export function present(handle, address, width, height) {
    const w = windows.get(handle);
    if (!w) return 0;
    const t0 = performance.now();
    const buffer = runtime.localHeapViewU8().buffer;
    if (w.image === null || w.imageBuffer !== buffer || w.imagePtr !== address || w.image.width !== width || w.image.height !== height) {
        w.image = new ImageData(new Uint8ClampedArray(buffer, address, width * height * 4), width, height);
        w.imageBuffer = buffer;
        w.imagePtr = address;
    }
    const canvas = w.canvas;
    // 只在呈现新尺寸的帧时才改 canvas 尺寸（改尺寸会清空 canvas），缩放窗口时不闪白
    if (canvas.width !== width) canvas.width = width;
    if (canvas.height !== height) canvas.height = height;
    w.ctx.putImageData(w.image, 0, 0);
    return performance.now() - t0;
}

export function setCursor(handle, css) {
    const w = windows.get(handle);
    if (w) w.canvas.style.cursor = css;
}

// ------------------------------------------------------------------ 尺寸、DPR、可见性

function attachResize(w) {
    const report = () => cb.OnResize(w.handle, w.cssWidth, w.cssHeight, w.dpr, w.deviceWidth, w.deviceHeight);
    if (typeof ResizeObserver === 'function') {
        w.resizeObserver = new ResizeObserver((entries) => {
            const e = entries[entries.length - 1];
            const box = e.contentBoxSize && e.contentBoxSize[0];
            w.cssWidth = box ? box.inlineSize : e.contentRect.width;
            w.cssHeight = box ? box.blockSize : e.contentRect.height;
            const dev = e.devicePixelContentBoxSize && e.devicePixelContentBoxSize[0];
            w.deviceWidth = dev ? dev.inlineSize : NaN;
            w.deviceHeight = dev ? dev.blockSize : NaN;
            w.dpr = globalThis.devicePixelRatio || 1;
            report();
            repositionInput(w);
        });
        try {
            // 设备像素内容盒与合成器的像素网格精确对齐；Safari 不支持该选项时抛出 TypeError
            w.resizeObserver.observe(w.canvas, { box: 'device-pixel-content-box' });
        } catch {
            w.resizeObserver.observe(w.canvas, { box: 'content-box' });
        }
    }
    // 拖到另一块屏幕或缩放页面时 devicePixelRatio 变化：resolution 媒体查询每次变化后按新值重新登记
    const watchDpr = () => {
        if (typeof matchMedia !== 'function') return;
        w.dprQuery?.removeEventListener('change', w.dprListener);
        w.dprQuery = matchMedia(`(resolution: ${globalThis.devicePixelRatio || 1}dppx)`);
        w.dprListener = () => {
            if (!windows.has(w.handle)) return;
            w.dpr = globalThis.devicePixelRatio || 1;
            // 不支持 device-pixel-content-box 的浏览器在 DPR 变化时不会触发 ResizeObserver；设备尺寸作废，按 CSS × DPR 计算
            w.deviceWidth = NaN;
            w.deviceHeight = NaN;
            report();
            watchDpr();
        };
        w.dprQuery.addEventListener('change', w.dprListener);
    };
    watchDpr();
}

function updateVisibility(w) {
    w.pageVisible = document.visibilityState !== 'hidden';
    const visible = w.pageVisible && w.intersecting;
    if (visible === w.visible) return;
    w.visible = visible;
    cb.OnVisibility(w.handle, visible);
}

// ------------------------------------------------------------------ 指针与滚轮

function attachPointer(w, flags, signal) {
    const canvas = w.canvas;
    const coalesced = (flags & FLAG_COALESCED) !== 0;
    const send = (e, kind, r) => cb.OnPointer(w.handle, kind, deviceCode(e.pointerType), e.pointerId,
        e.clientX - r.left, e.clientY - r.top, r.width, r.height, e.button, e.buttons, e.pressure, modifiers(e), e.timeStamp);

    const on = (type, kind) => canvas.addEventListener(type, (e) => {
        const r = canvas.getBoundingClientRect();
        if (kind === POINTER_DOWN) {
            // 指针捕获：拖出 canvas 后仍收到移动与抬起（触摸本来就隐式捕获）
            try { canvas.setPointerCapture(e.pointerId); } catch { /* 指针已失效 */ }
            // 阻止默认行为（选择文本、焦点转移），然后手动把键盘焦点交给代理文本框
            e.preventDefault();
            focusInput(w);
        }
        if (kind === POINTER_MOVE && coalesced && e.button === -1 && typeof e.getCoalescedEvents === 'function') {
            const list = e.getCoalescedEvents();
            if (list.length > 0) {
                for (const c of list) send(c, kind, r);
                return;
            }
        }
        send(e, kind, r);
    }, { signal });

    on('pointerdown', POINTER_DOWN);
    on('pointerup', POINTER_UP);
    on('pointermove', POINTER_MOVE);
    on('pointerenter', POINTER_ENTER);
    on('pointerleave', POINTER_LEAVE);
    on('pointercancel', POINTER_CANCEL);

    canvas.addEventListener('wheel', (e) => {
        e.preventDefault(); // 页面不随之滚动；Ctrl+滚轮（触控板捏合）也不缩放页面
        const r = canvas.getBoundingClientRect();
        const legacy = typeof e.wheelDeltaY === 'number' ? e.wheelDeltaY : NaN;
        cb.OnWheel(w.handle, e.clientX - r.left, e.clientY - r.top, r.width, r.height, e.deltaX, e.deltaY, e.deltaMode,
            legacy, e.buttons, modifiers(e), e.timeStamp);
    }, { passive: false, signal });
}

// ------------------------------------------------------------------ 键盘、文本、输入法、剪贴板

// 隐藏的代理文本框：键盘焦点实际在这里，浏览器才会派发 beforeinput 与输入法组合事件。
// 不能用 display:none / visibility:hidden（无法聚焦）；透明且不响应指针，跟随光标定位以便候选窗出现在正确位置。
function createProxyInput(w, signal) {
    const input = document.createElement('textarea');
    input.setAttribute('autocomplete', 'off');
    input.setAttribute('autocorrect', 'off');
    input.setAttribute('autocapitalize', 'off');
    input.setAttribute('spellcheck', 'false');
    input.setAttribute('aria-hidden', 'true');
    input.setAttribute('inputmode', 'none'); // 软键盘只在可编辑元素获得焦点时弹出（BrowserTextInput.Show）
    input.tabIndex = -1;
    const s = input.style;
    s.position = 'fixed';
    s.left = '0px';
    s.top = '0px';
    s.width = '1px';
    s.height = '16px';
    s.padding = '0';
    s.margin = '0';
    s.border = '0';
    s.outline = 'none';
    s.resize = 'none';
    s.overflow = 'hidden';
    s.whiteSpace = 'pre';
    s.opacity = '0';
    s.color = 'transparent';
    s.background = 'transparent';
    s.caretColor = 'transparent';
    s.pointerEvents = 'none';
    s.fontSize = '16px'; // iOS Safari 对小于 16px 的输入框聚焦时会缩放页面
    s.zIndex = '-1';
    document.body.appendChild(input);

    const h = w.handle;
    const clear = () => { if (!w.composing && input.value !== '') input.value = ''; };

    const key = (down) => (e) => {
        const composing = e.isComposing || e.keyCode === 229;
        if (cb.OnKey(h, down, e.code || '', e.key || '', e.keyCode | 0, modifiers(e), !!e.repeat, composing, e.timeStamp)) e.preventDefault();
    };
    input.addEventListener('keydown', key(true), { signal });
    input.addEventListener('keyup', key(false), { signal });

    input.addEventListener('beforeinput', (e) => {
        if (w.resetting) return;
        const data = e.data ?? (e.dataTransfer ? e.dataTransfer.getData('text/plain') : null);
        if (cb.OnBeforeInput(h, e.inputType || '', data, !!e.isComposing || w.composing)) e.preventDefault();
    }, { signal });

    // 后备：beforeinput 不可取消的浏览器会真的把字符写进文本框，在 input 中取出并清空
    input.addEventListener('input', (e) => {
        if (w.resetting) return;
        if (w.composing || e.isComposing) {
            cb.OnComposition(h, COMPOSITION_UPDATE, input.value, input.selectionStart | 0, input.selectionEnd | 0);
            return;
        }
        const t = e.inputType || '';
        if (t.includes('Composition')) { clear(); return; }
        if (input.value !== '') {
            const text = input.value;
            input.value = '';
            cb.OnTextInput(h, text);
        }
    }, { signal });

    input.addEventListener('compositionstart', (e) => {
        if (w.resetting) return;
        w.afterReset = false;
        clear();
        w.composing = true;
        cb.OnComposition(h, COMPOSITION_START, e.data || '', -1, -1);
    }, { signal });
    input.addEventListener('compositionupdate', (e) => {
        if (w.resetting) return;
        w.composing = true;
        cb.OnComposition(h, COMPOSITION_UPDATE, e.data || '', -1, -1);
    }, { signal });
    input.addEventListener('compositionend', (e) => {
        if (w.resetting) return;
        // 重置后浏览器迟到的 compositionend（没有新的 compositionstart）：预编辑已被框架处理过，不再提交
        if (w.afterReset && !w.composing) return;
        w.composing = false;
        cb.OnComposition(h, COMPOSITION_END, e.data || '', -1, -1);
        // Firefox / Safari 在 compositionend 之后还会派发 input：等它们过去再清空
        setTimeout(clear, 0);
    }, { signal });

    input.addEventListener('focus', () => { repositionInput(w); if (!w.resetting) cb.OnFocus(h, true); }, { signal });
    input.addEventListener('blur', () => { if (!w.resetting) cb.OnFocus(h, false); }, { signal });

    input.addEventListener('paste', (e) => {
        const text = e.clipboardData ? e.clipboardData.getData('text/plain') : '';
        e.preventDefault();
        if (text) cb.OnPaste(h, text);
    }, { signal });
    const copy = (cut) => (e) => {
        const text = cb.OnCopy(h, cut);
        if (text == null || !e.clipboardData) return;
        e.clipboardData.setData('text/plain', text);
        e.preventDefault();
    };
    input.addEventListener('copy', copy(false), { signal });
    input.addEventListener('cut', copy(true), { signal });
    return input;
}

function focusInput(w) {
    if (document.activeElement !== w.input) w.input.focus({ preventScroll: true });
}

export function focus(handle) {
    const w = windows.get(handle);
    if (w) focusInput(w);
}

// 放弃进行中的输入法组合（框架已经自行提交或丢弃了预编辑文本）：浏览器没有直接的 API，失焦再聚焦会让输入法结束组合。
// 期间产生的 compositionend / input / focus / blur 都被忽略，避免把预编辑文本再提交一次。
export function resetComposition(handle) {
    const w = windows.get(handle);
    if (!w) return;
    w.resetting = true;
    try {
        const focused = document.activeElement === w.input;
        w.composing = false;
        if (focused) w.input.blur();
        w.input.value = '';
        if (focused) w.input.focus({ preventScroll: true });
    } finally {
        w.resetting = false;
    }
    w.afterReset = true;
    setTimeout(() => { w.afterReset = false; }, 0);
}

export function setTextInputEnabled(handle, enabled) {
    const w = windows.get(handle);
    if (w) w.input.setAttribute('inputmode', enabled ? 'text' : 'none');
}

export function setTextInputRect(handle, x, y, width, height) {
    const w = windows.get(handle);
    if (!w) return;
    w.caret = { x, y, w: width, h: height };
    repositionInput(w);
}

function repositionInput(w) {
    const r = w.canvas.getBoundingClientRect();
    const sx = w.cssWidth > 0 ? r.width / w.cssWidth : 1;
    const sy = w.cssHeight > 0 ? r.height / w.cssHeight : 1;
    const c = w.caret;
    const s = w.input.style;
    s.left = `${r.left + c.x * sx}px`;
    s.top = `${r.top + c.y * sy}px`;
    s.height = `${Math.max(1, c.h * sy)}px`;
}

export async function clipboardReadText() {
    try {
        return await navigator.clipboard.readText();
    } catch {
        return null;
    }
}

export async function clipboardWriteText(text) {
    try {
        await navigator.clipboard.writeText(text);
        return true;
    } catch {
        return false;
    }
}
