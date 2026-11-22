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
// AppLifecycle 的数值（与 AwAUI.Platform.AppLifecycle 一致）
const LIFECYCLE_ACTIVE = 1, LIFECYCLE_INACTIVE = 2, LIFECYCLE_BACKGROUND = 3, LIFECYCLE_FOREGROUND = 4;
// BrowserStorageCodec 的字段 / 记录分隔符
const STORAGE_FIELD = '\u001f', STORAGE_RECORD = '\n';
// 与 BrowserClipboard.MaxImagePixels 一致：16M 像素（≈64 MB RGBA）
const MAX_IMAGE_PIXELS = 16 * 1024 * 1024;
// 影子 ARIA DOM：JSON 格式与动作 / 实时区域编号（与 Translation/BrowserAriaCodec.cs 一致）
const A11Y_FORMAT_VERSION = 1;
const A11Y_ACTION_INVOKE = 0, A11Y_ACTION_FOCUS = 1, A11Y_ACTION_TOGGLE = 2, A11Y_ACTION_SET_VALUE = 3,
    A11Y_ACTION_INCREMENT = 4, A11Y_ACTION_DECREMENT = 5, A11Y_ACTION_SCROLL_INTO_VIEW = 6,
    A11Y_ACTION_EXPAND = 7, A11Y_ACTION_COLLAPSE = 8;
const A11Y_LIVE_OFF = 0, A11Y_LIVE_POLITE = 1, A11Y_LIVE_ASSERTIVE = 2;

let runtime = null;
let cb = null; // C# 的 BrowserCallbacks
const windows = new Map(); // 句柄 → 窗口的 DOM 状态
let canvasCounter = 0;
let screensWatching = false;
let resolutionQuery = null, resolutionListener = null;
const storageFiles = new Map(); // 存储句柄 → { kind:'r'|'rw'|'d', file?, handle?, name, size, type, writable }
let storageCounter = 0;
const stagedImages = new Map(); // 图片 id → { width, height, data: Uint8ClampedArray }
let stagedImageCounter = 0;

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
    installGlobalListeners();
}

function documentHasFocus() {
    try { return typeof document.hasFocus === 'function' ? !!document.hasFocus() : true; } catch { return true; }
}

// 窗口级激活、页面生命周期与全屏都是页面（而不是某个 canvas）的全局事件，分发给所有窗口。
function installGlobalListeners() {
    if (typeof document === 'undefined') return;
    const activation = (active) => { for (const w of windows.values()) cb.OnActivation(w.handle, active); };
    const lifecycle = (state) => { for (const w of windows.values()) cb.OnLifecycle(w.handle, state); };
    if (typeof globalThis.addEventListener === 'function') {
        globalThis.addEventListener('focus', () => { activation(true); lifecycle(LIFECYCLE_ACTIVE); });
        globalThis.addEventListener('blur', () => { activation(false); lifecycle(LIFECYCLE_INACTIVE); });
        globalThis.addEventListener('pagehide', () => { activation(false); lifecycle(LIFECYCLE_BACKGROUND); });
        globalThis.addEventListener('pageshow', () => { activation(documentHasFocus()); lifecycle(LIFECYCLE_FOREGROUND); });
    }
    document.addEventListener('visibilitychange', () => {
        const visible = document.visibilityState !== 'hidden';
        for (const w of windows.values()) updateVisibility(w);
        activation(visible && documentHasFocus());
        lifecycle(visible ? LIFECYCLE_FOREGROUND : LIFECYCLE_BACKGROUND);
    });
    document.addEventListener('fullscreenchange', () => {
        const fullscreen = !!document.fullscreenElement;
        for (const w of windows.values()) cb.OnFullscreen(w.handle, fullscreen);
    });
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
        // 代理文本框的软键盘提示：enabled 由 Show / Hide 控制，mode / lang / enterKeyHint 来自客户端的 TextInputOptions
        textInput: { enabled: false, mode: 'text', lang: null, enterKeyHint: null },
        resizeObserver: null, intersectionObserver: null, dprQuery: null,
        a11y: null,
    };
    windows.set(handle, w);

    // 浏览器不再自行处理 canvas 上的平移 / 缩放 / 选择文本：手势与滚动由框架实现（design/09 touch-action）
    canvas.style.touchAction = 'none';
    canvas.style.userSelect = 'none';
    canvas.style.webkitUserSelect = 'none';
    canvas.style.webkitTapHighlightColor = 'transparent';
    canvas.style.outline = 'none';

    w.input = createProxyInput(w, signal);
    applyTextInput(w);
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
    disposeA11y(w);
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

// ------------------------------------------------------------------ 窗口状态、焦点、安全区

export function hasFocus() { return documentHasFocus(); }

export function isFullscreen() {
    try { return !!document.fullscreenElement; } catch { return false; }
}

// 浏览器只有 Normal / FullScreen。requestFullscreen 需要用户手势，失败时抛出的错误在这里变成 false。
export async function requestFullscreen(handle) {
    const w = windows.get(handle);
    if (!w) return false;
    try {
        if (document.fullscreenElement) return true;
        const target = w.canvas;
        if (typeof target.requestFullscreen !== 'function') return false;
        await target.requestFullscreen({ navigationUI: 'hide' });
        return true;
    } catch { return false; }
}

export async function exitFullscreen() {
    try {
        if (document.fullscreenElement) await document.exitFullscreen();
        return true;
    } catch { return false; }
}

// 聚焦代理文本框（键盘 / 输入法都从它进入），并尽力把页面窗口提到前台。
export function activateWindow(handle) {
    const w = windows.get(handle);
    if (!w) return;
    focusInput(w);
    try { if (typeof globalThis.focus === 'function') globalThis.focus(); } catch { /* 页面焦点由浏览器控制 */ }
}

// 浏览器无法移动页面中的 canvas：启动位置 / 坐标只作为滚动页面的尽力而为；最小 / 最大尺寸映射为 CSS 限制。
export function applyWindowOptions(handle, startupLocation, hasPosition, x, y, fullscreen, minWidth, minHeight, maxWidth, maxHeight) {
    const w = windows.get(handle);
    if (!w) return;
    const style = w.canvas.style;
    if (minWidth >= 0) style.minWidth = `${minWidth}px`;
    if (minHeight >= 0) style.minHeight = `${minHeight}px`;
    if (maxWidth >= 0) style.maxWidth = `${maxWidth}px`;
    if (maxHeight >= 0) style.maxHeight = `${maxHeight}px`;
    try {
        if (startupLocation === 1 || startupLocation === 2) {
            // CenterScreen / CenterOwner：把 canvas 滚到视口中央
            if (typeof w.canvas.scrollIntoView === 'function') w.canvas.scrollIntoView({ block: 'center', inline: 'center' });
        } else if (hasPosition) {
            // Manual + Position：让 canvas 的左上角落在视口 (x, y)（页面不能滚动时无效果）
            const r = w.canvas.getBoundingClientRect();
            if (typeof globalThis.scrollBy === 'function') globalThis.scrollBy(r.left - x, r.top - y);
        }
    } catch { /* 页面布局由宿主控制 */ }
    if (fullscreen) requestFullscreen(handle);
}

// env(safe-area-inset-*) 只在有刘海 / 圆角 / 软键盘的移动浏览器上非零；用隐藏元素读计算样式，失败时为零。
export function safeArea(handle) {
    if (!windows.has(handle) || typeof document.createElement !== 'function') return [0, 0, 0, 0];
    let probe = null;
    try {
        probe = document.createElement('div');
        const s = probe.style;
        s.position = 'fixed';
        s.left = '0'; s.top = '0'; s.width = '0'; s.height = '0';
        s.visibility = 'hidden'; s.pointerEvents = 'none';
        s.paddingTop = 'env(safe-area-inset-top)';
        s.paddingRight = 'env(safe-area-inset-right)';
        s.paddingBottom = 'env(safe-area-inset-bottom)';
        s.paddingLeft = 'env(safe-area-inset-left)';
        document.body.appendChild(probe);
        const cs = getComputedStyle(probe);
        const value = (name) => { const v = parseFloat(cs[name]); return Number.isFinite(v) ? Math.max(0, v) : 0; };
        return [value('paddingLeft'), value('paddingTop'), value('paddingRight'), value('paddingBottom')];
    } catch {
        return [0, 0, 0, 0];
    } finally {
        if (probe) probe.remove();
    }
}

// ------------------------------------------------------------------ 显示器

export function screensInfo() {
    try {
        const s = globalThis.screen;
        if (!s) return '';
        const width = Number(s.width) || 0, height = Number(s.height) || 0;
        const availWidth = Number(s.availWidth) || width, availHeight = Number(s.availHeight) || height;
        const availLeft = Number.isFinite(s.availLeft) ? s.availLeft : 0;
        const availTop = Number.isFinite(s.availTop) ? s.availTop : 0;
        const dpr = Number(globalThis.devicePixelRatio) || 1;
        const type = s.orientation && s.orientation.type ? String(s.orientation.type) : '';
        return [type, width, height, availWidth, availHeight, availLeft, availTop, dpr].join('|');
    } catch { return ''; }
}

// 分辨率媒体查询每次变化后按新的 devicePixelRatio 重新登记（与 CanvasMetrics 的做法一致）。
export function watchScreens() {
    if (screensWatching || typeof document === 'undefined') return;
    screensWatching = true;
    const notify = () => { cb.OnScreensChanged(); };
    if (typeof globalThis.addEventListener === 'function') {
        globalThis.addEventListener('resize', notify);
        globalThis.addEventListener('orientationchange', notify);
    }
    if (typeof globalThis.matchMedia === 'function') {
        const watch = () => {
            if (resolutionQuery) resolutionQuery.removeEventListener('change', resolutionListener);
            const dpr = Number(globalThis.devicePixelRatio) || 1;
            resolutionQuery = globalThis.matchMedia(`(resolution: ${dpr}dppx)`);
            resolutionListener = () => { notify(); watch(); };
            resolutionQuery.addEventListener('change', resolutionListener);
        };
        watch();
    }
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
        // 与 input 事件一致：读代理文本框的选区，输入法在组合中移动光标时框架才能画出正确的预编辑光标
        cb.OnComposition(h, COMPOSITION_UPDATE, e.data || '', input.selectionStart | 0, input.selectionEnd | 0);
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
    if (!w) return;
    w.textInput.enabled = !!enabled;
    applyTextInput(w);
}

// 客户端 TextInputOptions 的软键盘提示（C# 的 BrowserTextInputHints.SetTextInputOptions）。桌面浏览器只表达
// lang / inputmode / enterkeyhint；自动更正、候选与敏感内容没有对应的 DOM 属性，由 C# 侧决定忽略。
export function setTextInputOptions(handle, lang, inputMode, enterKeyHint) {
    const w = windows.get(handle);
    if (!w) return;
    w.textInput.lang = lang || null;
    w.textInput.mode = inputMode || 'text';
    w.textInput.enterKeyHint = enterKeyHint || null;
    applyTextInput(w);
}

// inputmode 同时受 Show / Hide（enabled）与客户端内容类型（mode）控制：不可编辑时总是 none，避免软键盘弹出。
function applyTextInput(w) {
    const input = w.input, ti = w.textInput;
    input.setAttribute('inputmode', ti.enabled ? ti.mode : 'none');
    if (ti.lang) input.setAttribute('lang', ti.lang);
    else input.removeAttribute('lang');
    if (ti.enterKeyHint) input.setAttribute('enterkeyhint', ti.enterKeyHint);
    else input.removeAttribute('enterkeyhint');
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

// ------------------------------------------------------------------ 影子 ARIA DOM

// canvas 对读屏器不可见：在 canvas 上方维护一棵透明、不接收指针事件的 DOM 树，节点与语义树一一对应。
// 容器只能用 opacity:0 + pointer-events:none 隐藏；display:none / visibility:hidden / aria-hidden 会让读屏器看不到它。
// 所有来自框架的字符串只经 textContent / setAttribute 写入，不拼 HTML。
function a11yDisabled() {
    try {
        if (globalThis.AWAUI_BROWSER_A11Y === 0 || globalThis.AWAUI_BROWSER_A11Y === '0') return true;
        const env = globalThis.process && globalThis.process.env;
        if (env && env.AWAUI_BROWSER_A11Y === '0') return true;
    } catch { /* 没有环境信息时按启用处理 */ }
    return false;
}

function a11yIdPart(value) {
    // encodeURIComponent 遇到孤立代理项会抛 URIError；先替换再转义。
    let safe = '';
    for (let i = 0; i < value.length; i++) {
        const c = value.charCodeAt(i);
        if (c >= 0xD800 && c <= 0xDBFF) {
            const d = i + 1 < value.length ? value.charCodeAt(i + 1) : 0;
            if (d >= 0xDC00 && d <= 0xDFFF) { safe += value[i] + value[i + 1]; i++; continue; }
            safe += '�';
        } else if (c >= 0xDC00 && c <= 0xDFFF) {
            safe += '�';
        } else {
            safe += value[i];
        }
    }
    return encodeURIComponent(safe);
}

function a11yDomId(handle, id) {
    return 'awaui-a11y-' + handle + '-' + a11yIdPart(String(id));
}

function setA11yAttr(el, name, value) {
    if (value == null || value === '') {
        if (el.getAttribute(name) != null) el.removeAttribute(name);
        return;
    }
    const text = String(value);
    if (el.getAttribute(name) !== text) el.setAttribute(name, text);
}

function setA11yText(el, text) {
    if ((el.textContent ?? '') !== text) el.textContent = text;
}

function a11yNumber(value, min) {
    const n = Number(value);
    if (!Number.isFinite(n)) return 0;
    return Math.max(min == null ? -1e7 : min, Math.min(1e7, n));
}

function a11yInside(container, element) {
    for (let node = element; node; node = node.parentNode) if (node === container) return true;
    return false;
}

export function a11yAttach(handle) {
    const w = windows.get(handle);
    if (!w || a11yDisabled() || typeof document === 'undefined' || !document.body) return false;
    if (w.a11y) return true;

    const container = document.createElement('div');
    container.setAttribute('data-awaui-a11y', String(handle));
    const s = container.style;
    s.position = 'absolute';
    s.opacity = '0';
    s.pointerEvents = 'none';
    s.overflow = 'hidden';
    s.zIndex = '2147483647';
    s.margin = '0';
    s.padding = '0';
    s.border = '0';
    document.body.appendChild(container);

    const abort = new AbortController();
    const a = {
        container, abort, nodes: new Map(), focusedId: null, internalFocus: false,
        structure: null, resizeObserver: null,
    };
    w.a11y = a;

    const reposition = () => {
        try {
            const r = w.canvas.getBoundingClientRect();
            const sx = Number(globalThis.scrollX ?? globalThis.pageXOffset ?? 0) || 0;
            const sy = Number(globalThis.scrollY ?? globalThis.pageYOffset ?? 0) || 0;
            s.left = `${r.left + sx}px`;
            s.top = `${r.top + sy}px`;
            s.width = `${Math.max(0, r.width)}px`;
            s.height = `${Math.max(0, r.height)}px`;
        } catch { /* 页面布局不可读时保持上一次的位置 */ }
    };
    a.reposition = reposition;
    reposition();

    if (typeof globalThis.addEventListener === 'function') {
        globalThis.addEventListener('scroll', reposition, { capture: true, passive: true, signal: abort.signal });
        globalThis.addEventListener('resize', reposition, { passive: true, signal: abort.signal });
    } else if (typeof document.addEventListener === 'function') {
        document.addEventListener('scroll', reposition, { capture: true, passive: true, signal: abort.signal });
    }
    if (typeof ResizeObserver === 'function') {
        try {
            a.resizeObserver = new ResizeObserver(reposition);
            a.resizeObserver.observe(w.canvas);
        } catch { a.resizeObserver = null; }
    }
    return true;
}

export function a11yUpdate(handle, json) {
    const w = windows.get(handle);
    if (!w || !w.a11y) return false;
    let model;
    try { model = JSON.parse(json); } catch { return false; }
    if (!model || typeof model !== 'object' || !Array.isArray(model.n)) return false;
    if (model.v != null && model.v !== A11Y_FORMAT_VERSION) return false;
    applyA11yModel(w, model);
    return true;
}

export function a11yDetach(handle) {
    const w = windows.get(handle);
    if (w) disposeA11y(w);
}

function disposeA11y(w) {
    const a = w.a11y;
    if (!a) return;
    w.a11y = null;
    a.abort.abort();
    if (a.resizeObserver) { try { a.resizeObserver.disconnect(); } catch { /* 已断开 */ } }
    for (const el of a.nodes.values()) { try { el.remove(); } catch { /* 已移除 */ } }
    a.nodes.clear();
    try { a.container.remove(); } catch { /* 页面已卸载 */ }
}

function createA11yNode(w, id) {
    const el = document.createElement('div');
    el.__awauiId = id;
    el.setAttribute('id', a11yDomId(w.handle, id));
    el.setAttribute('data-awaui-id', id);
    const s = el.style;
    s.position = 'absolute';
    s.margin = '0';
    s.padding = '0';
    s.border = '0';
    s.pointerEvents = 'none';
    s.overflow = 'visible';
    s.whiteSpace = 'nowrap';

    const a = w.a11y;
    const signal = a.abort.signal;
    el.addEventListener('click', (e) => {
        if (e && typeof e.preventDefault === 'function') e.preventDefault();
        performA11yAction(w, el, [A11Y_ACTION_INVOKE, A11Y_ACTION_TOGGLE, A11Y_ACTION_EXPAND]);
    }, { signal });
    el.addEventListener('keydown', (e) => {
        const key = e && e.key ? e.key : '';
        let codes;
        if (key === 'Enter') codes = [A11Y_ACTION_INVOKE, A11Y_ACTION_TOGGLE];
        else if (key === ' ' || key === 'Spacebar') codes = [A11Y_ACTION_TOGGLE, A11Y_ACTION_INVOKE];
        else if (key === 'ArrowUp') codes = [A11Y_ACTION_INCREMENT];
        else if (key === 'ArrowDown') codes = [A11Y_ACTION_DECREMENT];
        else if (key === 'ArrowRight') codes = [A11Y_ACTION_EXPAND];
        else if (key === 'ArrowLeft') codes = [A11Y_ACTION_COLLAPSE];
        else return;
        if (performA11yAction(w, el, codes) && e && typeof e.preventDefault === 'function') e.preventDefault();
    }, { signal });
    el.addEventListener('focus', () => {
        if (a.internalFocus) return;
        if (typeof el.__awauiId === 'string') cb.OnA11yFocus(w.handle, el.__awauiId);
    }, { signal });
    el.addEventListener('input', () => {
        if (a.internalFocus) return;
        if (el.getAttribute('contenteditable') !== 'true') return;
        if (typeof el.__awauiId === 'string') cb.OnA11yAction(w.handle, el.__awauiId, A11Y_ACTION_SET_VALUE, el.textContent ?? '');
    }, { signal });
    return el;
}

function performA11yAction(w, el, codes) {
    const actions = el.__awauiActions;
    if (!Array.isArray(actions)) return false;
    for (const code of codes) {
        if (actions.indexOf(code) >= 0) {
            cb.OnA11yAction(w.handle, el.__awauiId, code, null);
            return true;
        }
    }
    return false;
}

function updateA11yLiveText(el, text) {
    if (el.__awauiLiveText === text) return;
    const initial = el.__awauiLiveText === undefined;
    el.__awauiLiveText = text;
    if (initial) { setA11yText(el, text); return; }
    // 先清空再写，读屏器才会把同样的文本当成一次新变更；首次建立不播报。
    setA11yText(el, '');
    const write = () => { if (el.__awauiLiveText === text) setA11yText(el, text); };
    if (typeof setTimeout === 'function') setTimeout(write, 0); else write();
}

function updateA11yNode(w, el, node) {
    const actions = Array.isArray(node.a)
        ? node.a.filter((code) => Number.isInteger(code) && code >= A11Y_ACTION_INVOKE && code <= A11Y_ACTION_COLLAPSE)
        : [];
    el.__awauiActions = actions;

    const children = Array.isArray(node.c) ? node.c.filter((id) => typeof id === 'string' && id.length > 0) : [];
    const leaf = children.length === 0;
    const name = typeof node.nm === 'string' ? node.nm : null;
    const description = typeof node.d === 'string' ? node.d : null;
    const value = typeof node.v === 'string' ? node.v : null;
    const state = new Set(Array.isArray(node.st) ? node.st.filter((s) => typeof s === 'string') : []);

    let role = typeof node.r === 'string' && node.r.length > 0 && node.r.length <= 64 ? node.r : 'group';
    const live = node.lv === A11Y_LIVE_POLITE ? 'polite' : node.lv === A11Y_LIVE_ASSERTIVE ? 'assertive' : null;
    if (live && (role === 'group' || role === 'generic' || role === 'application' || role === 'paragraph')) {
        role = node.lv === A11Y_LIVE_ASSERTIVE ? 'alert' : 'status';
    }
    setA11yAttr(el, 'role', role);

    const editable = (role === 'textbox' || role === 'searchbox' || role === 'spinbutton')
        && actions.indexOf(A11Y_ACTION_SET_VALUE) >= 0;
    setA11yAttr(el, 'contenteditable', editable ? 'true' : null);
    setA11yAttr(el, 'spellcheck', editable ? 'false' : null);

    setA11yAttr(el, 'aria-label', name);
    setA11yAttr(el, 'aria-description', description);
    if (!editable) {
        if (value != null) {
            setA11yAttr(el, 'aria-valuetext', value);
            setA11yAttr(el, 'aria-valuenow', /^-?\d+(?:\.\d+)?$/.test(value) ? value : null);
        } else {
            setA11yAttr(el, 'aria-valuetext', null);
            setA11yAttr(el, 'aria-valuenow', null);
        }
    } else {
        setA11yAttr(el, 'aria-valuetext', null);
        setA11yAttr(el, 'aria-valuenow', null);
    }

    // 名称 / 值：叶子用 textContent 暴露可读文本；有子节点时用 aria-label，避免写入文本会清空子树。
    if (leaf && !editable && !live) setA11yText(el, name != null ? name : (value != null ? value : ''));
    if (leaf && editable) setA11yText(el, value != null ? value : '');
    if (!leaf) setA11yAttr(el, 'aria-label', name);

    if (live) {
        setA11yAttr(el, 'aria-live', live);
        setA11yAttr(el, 'aria-atomic', 'true');
        if (leaf) updateA11yLiveText(el, name != null ? name : (value != null ? value : ''));
    } else {
        setA11yAttr(el, 'aria-live', null);
        setA11yAttr(el, 'aria-atomic', null);
        el.__awauiLiveText = null;
    }

    const canExpand = actions.indexOf(A11Y_ACTION_EXPAND) >= 0 || actions.indexOf(A11Y_ACTION_COLLAPSE) >= 0;
    const checkable = role === 'checkbox' || role === 'radio' || role === 'switch'
        || role === 'menuitemcheckbox' || role === 'menuitemradio';
    const toggleButton = !checkable && actions.indexOf(A11Y_ACTION_TOGGLE) >= 0;

    setA11yAttr(el, 'aria-disabled', state.has('disabled') ? 'true' : null);
    setA11yAttr(el, 'aria-checked', checkable
        ? (state.has('mixed') ? 'mixed' : state.has('checked') ? 'true' : 'false')
        : null);
    setA11yAttr(el, 'aria-selected', state.has('selected') ? 'true' : null);
    setA11yAttr(el, 'aria-expanded', state.has('expanded') ? 'true' : canExpand ? 'false' : null);
    setA11yAttr(el, 'aria-pressed', toggleButton ? (state.has('pressed') ? 'true' : 'false') : null);
    setA11yAttr(el, 'aria-invalid', state.has('invalid') ? 'true' : null);
    setA11yAttr(el, 'aria-busy', state.has('busy') ? 'true' : null);
    setA11yAttr(el, 'aria-required', state.has('required') ? 'true' : null);
    setA11yAttr(el, 'aria-readonly', state.has('readonly') ? 'true' : null);
    setA11yAttr(el, 'aria-multiselectable', state.has('multiselectable') ? 'true' : null);
    setA11yAttr(el, 'aria-hidden', state.has('hidden') ? 'true' : null);
    setA11yAttr(el, 'data-awaui-focused', state.has('focused') ? 'true' : null);

    if (Number.isInteger(node.lvl)) setA11yAttr(el, 'aria-level', String(node.lvl));
    else setA11yAttr(el, 'aria-level', null);
    setA11yAttr(el, 'aria-posinset', Number.isInteger(node.ps) ? String(node.ps) : null);
    setA11yAttr(el, 'aria-setsize', Number.isInteger(node.ss) ? String(node.ss) : null);
    setA11yAttr(el, 'aria-haspopup', typeof node.hp === 'string' ? node.hp : null);
    setA11yAttr(el, 'aria-modal', node.md === 1 ? 'true' : null);
    setA11yAttr(el, 'aria-keyshortcuts', typeof node.ks === 'string' ? node.ks : null);

    const refs = (key) => {
        const list = Array.isArray(node[key]) ? node[key].filter((id) => typeof id === 'string' && id.length > 0) : [];
        return list.length > 0 ? list.map((id) => a11yDomId(w.handle, id)).join(' ') : null;
    };
    setA11yAttr(el, 'aria-labelledby', refs('lb'));
    setA11yAttr(el, 'aria-controls', refs('ct'));
    setA11yAttr(el, 'aria-describedby', refs('db'));
    setA11yAttr(el, 'aria-activedescendant',
        typeof node.ad === 'string' && node.ad.length > 0 ? a11yDomId(w.handle, node.ad) : null);

    // contenteditable 自身可聚焦，必须显式 tabindex=-1 才不会进入 Tab 顺序（键盘输入仍由代理文本框接收）。
    setA11yAttr(el, 'tabindex', (editable || actions.indexOf(A11Y_ACTION_FOCUS) >= 0) ? '-1' : null);

    const bounds = Array.isArray(node.b) ? node.b : [0, 0, 0, 0];
    el.style.left = `${a11yNumber(bounds[0])}px`;
    el.style.top = `${a11yNumber(bounds[1])}px`;
    el.style.width = `${a11yNumber(bounds[2], 0)}px`;
    el.style.height = `${a11yNumber(bounds[3], 0)}px`;
    el.__awauiNode = node;
}

function applyA11yModel(w, model) {
    const a = w.a11y;
    if (!a) return;
    if (typeof a.reposition === 'function') a.reposition();

    const valid = [];
    const seen = new Set();
    for (const node of model.n) {
        if (!node || typeof node !== 'object') continue;
        const id = node.i;
        if (typeof id !== 'string' || id.length === 0 || id.length > 4096 || seen.has(id)) continue;
        seen.add(id);
        valid.push(node);
    }

    // 按 id 复用元素，移除已消失的节点。
    for (const [id, el] of a.nodes) {
        if (!seen.has(id)) {
            a.nodes.delete(id);
            try { el.remove(); } catch { /* 已移除 */ }
        }
    }
    for (const node of valid) {
        let el = a.nodes.get(node.i);
        if (!el) {
            el = createA11yNode(w, node.i);
            a.nodes.set(node.i, el);
        }
        updateA11yNode(w, el, node);
    }

    // 父子链接：只在结构变化时重排，避免仅焦点变化时移动获得焦点的元素。
    const parentOf = new Map();
    for (const node of valid) {
        if (!Array.isArray(node.c)) continue;
        for (const child of node.c) {
            if (typeof child === 'string' && seen.has(child) && !parentOf.has(child)) parentOf.set(child, node.i);
        }
    }
    const structure = valid.map((node) => node.i + '|' + (Array.isArray(node.c) ? node.c.join(',') : '')).join(';');
    const reorder = structure !== a.structure;
    // 重排会移出 / 重新插入获得焦点的节点，浏览器可能因此失焦；记住重排前焦点是否在影子树内。
    const focusWasInside = a11yInside(a.container, document.activeElement);
    if (reorder) {
        // 从「叶子」变成「容器」的节点可能残留直接文本；子元素会在下面重新挂回，所以可以安全清空。
        for (const node of valid) {
            if (Array.isArray(node.c) && node.c.length > 0) {
                const el = a.nodes.get(node.i);
                if (el) setA11yText(el, '');
            }
        }
    }
    for (const node of valid) {
        const el = a.nodes.get(node.i);
        const parentId = parentOf.get(node.i);
        const target = parentId && a.nodes.has(parentId) ? a.nodes.get(parentId) : a.container;
        if (reorder || el.parentNode !== target) target.appendChild(el);
    }
    a.structure = structure;

    // 焦点同步：框架焦点 → DOM 焦点只在 DOM 焦点已经位于影子树内时进行，避免抢走代理文本框 / 页面焦点。
    let focused = null;
    for (const node of valid) {
        if (Array.isArray(node.st) && node.st.indexOf('focused') >= 0) {
            focused = a.nodes.get(node.i) || null;
            break;
        }
    }
    a.focusedId = focused ? focused.__awauiId : null;
    setA11yAttr(a.container, 'aria-activedescendant', focused ? focused.getAttribute('id') : null);
    if (focused && focused !== document.activeElement && (focusWasInside || a11yInside(a.container, document.activeElement))) {
        a.internalFocus = true;
        try { focused.focus({ preventScroll: true }); }
        catch { try { focused.focus(); } catch { /* 不可聚焦 */ } }
        finally { a.internalFocus = false; }
    }
}

// ------------------------------------------------------------------ 剪贴板

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

// ------------------------------------------------------------------ 富剪贴板（HTML / 图片）

export async function clipboardReadHtml() {
    try {
        const n = globalThis.navigator;
        if (!n || !n.clipboard || typeof n.clipboard.read !== 'function') return null;
        const items = await n.clipboard.read();
        for (const item of items) {
            if (!item.types || item.types.indexOf('text/html') < 0) continue;
            const blob = await item.getType('text/html');
            return await blob.text();
        }
        return null;
    } catch { return null; }
}

export async function clipboardWriteHtml(html) {
    try {
        if (html == null || typeof ClipboardItem === 'undefined') return false;
        const n = globalThis.navigator;
        if (!n || !n.clipboard || typeof n.clipboard.write !== 'function') return false;
        let plain = '';
        try {
            const div = document.createElement('div');
            div.innerHTML = html;
            plain = div.textContent || '';
        } catch { plain = ''; }
        const item = new ClipboardItem({
            'text/html': new Blob([html], { type: 'text/html' }),
            'text/plain': new Blob([plain], { type: 'text/plain' }),
        });
        await n.clipboard.write([item]);
        return true;
    } catch { return false; }
}

// 剪贴板图片：读到的像素先暂存在 JS，C# 再过 clipboardTakeImage 复制到自己的缓冲；
// 写入时 C# 把非预乘 RGBA 暂存到这里，再编码 PNG 交给 ClipboardItem。
export async function clipboardReadImageInfo() {
    try {
        const n = globalThis.navigator;
        if (!n || !n.clipboard || typeof n.clipboard.read !== 'function') return null;
        const items = await n.clipboard.read();
        for (const item of items) {
            const type = item.types && item.types.find((t) => t.indexOf('image/') === 0);
            if (!type) continue;
            const blob = await item.getType(type);
            const info = await decodeImageBlob(blob);
            if (!info) return null;
            const id = ++stagedImageCounter;
            stagedImages.set(id, info);
            return `${info.width},${info.height},${id}`;
        }
        return null;
    } catch { return null; }
}

async function decodeImageBlob(blob) {
    try {
        if (typeof createImageBitmap !== 'function') return null;
        const bitmap = await createImageBitmap(blob);
        try {
            const width = bitmap.width | 0, height = bitmap.height | 0;
            if (width <= 0 || height <= 0) return null;
            if (width * height > MAX_IMAGE_PIXELS) return null;
            const canvas = createScratchCanvas(width, height);
            const ctx = canvas.getContext('2d', { willReadFrequently: true });
            if (!ctx) return null;
            ctx.drawImage(bitmap, 0, 0);
            // getImageData 是非预乘 RGBA；Bitmap.FromRgbaUnpremultiplied 接受这种格式
            return { width, height, data: ctx.getImageData(0, 0, width, height).data };
        } finally {
            if (bitmap.close) bitmap.close();
        }
    } catch { return null; }
}

function createScratchCanvas(width, height) {
    if (typeof OffscreenCanvas === 'function') return new OffscreenCanvas(width, height);
    const canvas = document.createElement('canvas');
    canvas.width = width;
    canvas.height = height;
    return canvas;
}

export function clipboardDiscardImage(id) { stagedImages.delete(id); }

export function clipboardTakeImage(id, address, length) {
    const image = stagedImages.get(id);
    if (!image) return false;
    stagedImages.delete(id);
    try {
        const expected = image.width * image.height * 4;
        if (length !== expected) return false;
        const heap = runtime.localHeapViewU8();
        if (address < 0 || address + expected > heap.length) return false;
        heap.set(image.data, address);
        return true;
    } catch { return false; }
}

export function clipboardStageImage(width, height, address, length) {
    try {
        const expected = width * height * 4;
        if (width <= 0 || height <= 0 || length !== expected || width * height > MAX_IMAGE_PIXELS) return 0;
        const heap = runtime.localHeapViewU8();
        if (address < 0 || address + expected > heap.length) return 0;
        // 同步复制：C# 返回后即可解除对托管缓冲的固定
        const data = new Uint8ClampedArray(heap.buffer, address, expected).slice();
        const id = ++stagedImageCounter;
        stagedImages.set(id, { width, height, data });
        return id;
    } catch { return 0; }
}

export async function clipboardWriteStagedImage(id) {
    const image = stagedImages.get(id);
    stagedImages.delete(id);
    if (!image) return false;
    try {
        if (typeof ClipboardItem === 'undefined') return false;
        const n = globalThis.navigator;
        if (!n || !n.clipboard || typeof n.clipboard.write !== 'function') return false;
        const canvas = createScratchCanvas(image.width, image.height);
        const ctx = canvas.getContext('2d', { willReadFrequently: true });
        if (!ctx) return false;
        ctx.putImageData(new ImageData(image.data, image.width, image.height), 0, 0);
        const blob = typeof canvas.convertToBlob === 'function'
            ? await canvas.convertToBlob({ type: 'image/png' })
            : await new Promise((resolve) => canvas.toBlob(resolve, 'image/png'));
        if (!blob) return false;
        await n.clipboard.write([new ClipboardItem({ 'image/png': blob })]);
        return true;
    } catch { return false; }
}

// ------------------------------------------------------------------ Launcher

export function launcherOpen(url) {
    try {
        if (typeof document === 'undefined' || typeof URL !== 'function') return false;
        const target = new URL(String(url), document.baseURI || 'https://localhost/');
        // 与 C# 的 BrowserUriPolicy 相同的白名单（防御纵深）
        if (target.protocol !== 'http:' && target.protocol !== 'https:' && target.protocol !== 'mailto:') return false;
        if (typeof globalThis.open !== 'function') return false;
        // noopener,noreferrer：新页面拿不到 opener，也不发送 Referer
        const opened = globalThis.open(target.href, '_blank', 'noopener,noreferrer');
        if (opened) return true;
        // noopener 让 window.open 在成功时也返回 null；以 transient activation 区分被拦截的常见情况。
        const activation = globalThis.navigator && globalThis.navigator.userActivation;
        return !activation || activation.isActive !== false;
    } catch { return false; }
}

// ------------------------------------------------------------------ 文件选择器 / 保存

function encodeStorageEntry(id, name, size, type, writable) {
    return [id, encodeURIComponent(name || ''), size, encodeURIComponent(type || ''), writable ? 1 : 0].join(STORAGE_FIELD);
}

export async function storageOpenFiles(accept, multiple) {
    try {
        const files = await pickFiles(accept, multiple);
        const lines = [];
        for (const file of files) {
            const size = typeof file.size === 'number' ? file.size : -1;
            const type = file.type || '';
            const id = ++storageCounter;
            storageFiles.set(id, { kind: 'r', file, name: file.name || 'file', size, type, writable: false });
            lines.push(encodeStorageEntry(id, file.name, size, type, false));
        }
        return lines.join(STORAGE_RECORD);
    } catch { return ''; }
}

function pickFiles(accept, multiple) {
    if (typeof document === 'undefined') return Promise.resolve([]);
    return new Promise((resolve) => {
        const input = document.createElement('input');
        input.type = 'file';
        input.style.display = 'none';
        if (accept) input.accept = accept;
        input.multiple = !!multiple;
        let done = false;
        const read = () => {
            const list = input.files, files = [];
            if (list) for (let i = 0; i < list.length; i++) files.push(list[i]);
            return files;
        };
        const onFocus = () => setTimeout(() => { if (!done) finish(read()); }, 300);
        const finish = (files) => {
            if (done) return;
            done = true;
            if (typeof globalThis.removeEventListener === 'function') globalThis.removeEventListener('focus', onFocus);
            input.remove();
            resolve(files || []);
        };
        input.addEventListener('change', () => finish(read()));
        input.addEventListener('cancel', () => finish([]));
        // 没有 cancel 事件的浏览器：对话框关闭后窗口重新获得焦点，稍等再读取
        if (typeof globalThis.addEventListener === 'function') globalThis.addEventListener('focus', onFocus);
        document.body.appendChild(input);
        try { input.click(); } catch { finish([]); }
    });
}

export async function storageSaveFile(suggestedName) {
    try {
        if (typeof globalThis.showSaveFilePicker === 'function') {
            try {
                const handle = await globalThis.showSaveFilePicker({ suggestedName: suggestedName || 'download' });
                const name = handle.name || suggestedName || 'download';
                const id = ++storageCounter;
                storageFiles.set(id, { kind: 'rw', handle, name, size: -1, type: '', writable: true });
                return encodeStorageEntry(id, name, -1, '', true);
            } catch (e) {
                if (e && e.name === 'AbortError') return ''; // 用户取消
                // SecurityError / NotAllowedError（例如没有用户手势）→ 下载回退
            }
        }
        const name = suggestedName || 'download';
        const id = ++storageCounter;
        storageFiles.set(id, { kind: 'd', name, size: -1, type: '', writable: true });
        return encodeStorageEntry(id, name, -1, '', true);
    } catch { return ''; }
}

export async function storageFileSize(handle) {
    const entry = storageFiles.get(handle);
    if (!entry) return -1;
    if (typeof entry.size === 'number' && entry.size >= 0) return entry.size;
    try {
        let file = entry.file;
        if (!file && entry.handle && typeof entry.handle.getFile === 'function') file = await entry.handle.getFile();
        if (file && typeof file.size === 'number') { entry.size = file.size; return file.size; }
    } catch { /* 写之前的新文件可能还不存在 */ }
    return -1;
}

export async function storageReadFileInto(handle, address, length) {
    const entry = storageFiles.get(handle);
    if (!entry || length < 0) return false;
    try {
        let file = entry.file;
        if (!file && entry.handle && typeof entry.handle.getFile === 'function') file = await entry.handle.getFile();
        if (!file || typeof file.arrayBuffer !== 'function') return false;
        const buffer = await file.arrayBuffer();
        const size = buffer.byteLength;
        if (size > length) return false;
        // 等待期间 .NET 堆可能增长：重新取得当前线性内存视图，地址（偏移）仍然有效
        const heap = runtime.localHeapViewU8();
        if (address < 0 || address + size > heap.length) return false;
        heap.set(new Uint8Array(buffer), address);
        return true;
    } catch { return false; }
}

export async function storageFinishWrite(handle, address, length) {
    const entry = storageFiles.get(handle);
    if (!entry || !entry.writable || length < 0) return false;
    try {
        const heap = runtime.localHeapViewU8();
        if (address < 0 || address + length > heap.length) return false;
        // 同步复制后再 await：之后的内存增长不会影响这份快照
        const bytes = new Uint8Array(heap.buffer, address, length).slice();
        if (entry.kind === 'rw' && entry.handle && typeof entry.handle.createWritable === 'function') {
            const writable = await entry.handle.createWritable();
            await writable.write(bytes);
            await writable.close();
            entry.size = length;
            return true;
        }
        if (entry.kind === 'd') {
            const blob = new Blob([bytes], { type: entry.type || 'application/octet-stream' });
            const url = URL.createObjectURL(blob);
            const anchor = document.createElement('a');
            anchor.href = url;
            anchor.download = entry.name || 'download';
            anchor.style.display = 'none';
            document.body.appendChild(anchor);
            anchor.click();
            anchor.remove();
            setTimeout(() => URL.revokeObjectURL(url), 0);
            entry.size = length;
            return true;
        }
        return false;
    } catch { return false; }
}
