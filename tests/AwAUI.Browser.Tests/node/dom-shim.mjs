// Node 下运行 AwAUI.Browser 用的最小 DOM / canvas 垫片：只实现 awaui-browser.js 与 BrowserDemo 的 main.js 用到的部分。
// 刻意不定义 globalThis.window：.NET 运行时的 dotnet.js 用 `typeof window` 判断是否在浏览器中，定义了会走错加载路径。
// requestAnimationFrame 由测试手动推进（flushFrames），以便精确断言「空闲时不挂回调」。

class FakeEvent {
    constructor(type, init = {}) {
        Object.assign(this, { bubbles: false, cancelable: true, timeStamp: performance.now() }, init);
        this.type = type;
        this.defaultPrevented = false;
    }
    preventDefault() { if (this.cancelable) this.defaultPrevented = true; }
    stopPropagation() {}
}

class FakeEventTarget {
    constructor() { this._listeners = new Map(); }
    addEventListener(type, fn, opts) {
        const signal = opts && typeof opts === 'object' ? opts.signal : undefined;
        if (signal?.aborted) return;
        if (!this._listeners.has(type)) this._listeners.set(type, []);
        const entry = { fn };
        this._listeners.get(type).push(entry);
        signal?.addEventListener('abort', () => this.removeEventListener(type, fn));
    }
    removeEventListener(type, fn) {
        const list = this._listeners.get(type);
        if (!list) return;
        const i = list.findIndex((e) => e.fn === fn);
        if (i >= 0) list.splice(i, 1);
    }
    dispatchEvent(e) {
        e.target ??= this;
        e.currentTarget = this;
        for (const { fn } of [...(this._listeners.get(e.type) ?? [])]) fn.call(this, e);
        return !e.defaultPrevented;
    }
    /** 测试用：构造并派发事件，返回事件对象（可检查 defaultPrevented）。 */
    fire(type, init) {
        const e = new FakeEvent(type, init);
        this.dispatchEvent(e);
        return e;
    }
    listenerCount(type) { return this._listeners.get(type)?.length ?? 0; }
}

class FakeElement extends FakeEventTarget {
    constructor(doc, tagName) {
        super();
        this.ownerDocument = doc;
        this.tagName = tagName.toUpperCase();
        this.style = {};
        this.attributes = new Map();
        this.children = [];
        this.parentNode = null;
        this.id = '';
        this.textContent = '';
        this.innerHTML = '';
        this.scrollTop = 0;
        this.scrollHeight = 0;
        this.tabIndex = 0;
        this.rect = { left: 0, top: 0, width: 0, height: 0 };
        this.checked = false;
    }
    setAttribute(n, v) { this.attributes.set(n, String(v)); if (n === 'id') this.id = String(v); }
    getAttribute(n) { return this.attributes.has(n) ? this.attributes.get(n) : null; }
    removeAttribute(n) { this.attributes.delete(n); if (n === 'id') this.id = ''; }
    appendChild(c) {
        // 与真实 DOM 一致：已经挂在别处（或同一父节点）时先摘下来再追加，避免重复。
        if (c.parentNode) c.parentNode.removeChild(c);
        c.parentNode = this;
        this.children.push(c);
        return c;
    }
    removeChild(c) {
        const i = this.children.indexOf(c);
        if (i >= 0) this.children.splice(i, 1);
        if (c.parentNode === this) c.parentNode = null;
        return c;
    }
    remove() {
        if (!this.parentNode) return;
        const list = this.parentNode.children;
        list.splice(list.indexOf(this), 1);
        this.parentNode = null;
    }
    getBoundingClientRect() {
        let r = this.rect;
        // 没有显式布局的元素按 style 的 px 尺寸（BrowserPlatform.CreateWindow 新建的 canvas）
        if (r.width === 0 && r.height === 0 && /px$/.test(this.style.width ?? '')) r = { left: 0, top: 0, width: parseFloat(this.style.width), height: parseFloat(this.style.height) };
        return { left: r.left, top: r.top, x: r.left, y: r.top, width: r.width, height: r.height, right: r.left + r.width, bottom: r.top + r.height };
    }
    focus() {
        const doc = this.ownerDocument;
        if (doc.activeElement === this) return;
        const prev = doc.activeElement;
        doc.activeElement = this;
        prev?.fire('blur');
        this.fire('focus');
    }
    blur() {
        const doc = this.ownerDocument;
        if (doc.activeElement !== this) return;
        doc.activeElement = doc.body;
        this.fire('blur');
    }
}

class FakeTextArea extends FakeElement {
    constructor(doc) {
        super(doc, 'textarea');
        this.value = '';
        this.selectionStart = 0;
        this.selectionEnd = 0;
    }
}

/** 2D 上下文：putImageData 真的复制像素（模拟浏览器的那一次拷贝，并可回读校验）。 */
class FakeContext2D {
    constructor(canvas, options) {
        this.canvas = canvas;
        this.options = options;
        this.backing = null;
        this.puts = 0;
    }
    // 与浏览器一致：报告上下文创建时的 alpha（之后再以别的选项 getContext 不会改变它）
    getContextAttributes() { return { alpha: this.options?.alpha !== false }; }
    putImageData(image, dx, dy) {
        const c = this.canvas;
        if (image.width !== c.width || image.height !== c.height || dx !== 0 || dy !== 0) throw new Error('shim: unexpected putImageData geometry');
        const n = c.width * c.height * 4;
        if (!this.backing || this.backing.length !== n) this.backing = new Uint8ClampedArray(n);
        this.backing.set(image.data);
        this.puts++;
    }
    getImageData(x, y, w, h) {
        const c = this.canvas, out = new Uint8ClampedArray(w * h * 4);
        if (this.backing) for (let j = 0; j < h; j++) out.set(this.backing.subarray(((y + j) * c.width + x) * 4, ((y + j) * c.width + x + w) * 4), j * w * 4);
        return { data: out, width: w, height: h };
    }
}

class FakeCanvas extends FakeElement {
    constructor(doc) {
        super(doc, 'canvas');
        this._width = 300;
        this._height = 150;
        this._ctx = null;
        this.captures = new Set();
        this.resizes = 0;
    }
    get width() { return this._width; }
    set width(v) { if (v !== this._width) { this._width = v; this.resizes++; } }
    get height() { return this._height; }
    set height(v) { if (v !== this._height) { this._height = v; this.resizes++; } }
    getContext(type, options) { return type === '2d' ? (this._ctx ??= new FakeContext2D(this, options)) : null; }
    setPointerCapture(id) { this.captures.add(id); }
    releasePointerCapture(id) { this.captures.delete(id); }
    hasPointerCapture(id) { return this.captures.has(id); }
}

class FakeDocument extends FakeEventTarget {
    constructor() {
        super();
        this.body = new FakeElement(this, 'body');
        this.activeElement = this.body;
        this.visibilityState = 'visible';
        this.title = '';
        this.byId = new Map();
    }
    createElement(tag) {
        const t = tag.toLowerCase();
        const el = t === 'canvas' ? new FakeCanvas(this) : t === 'textarea' ? new FakeTextArea(this) : new FakeElement(this, t);
        return el;
    }
    getElementById(id) {
        if (this.byId.has(id)) return this.byId.get(id);
        const find = (el) => { if (el.id === id) return el; for (const c of el.children) { const f = find(c); if (f) return f; } return null; };
        return find(this.body);
    }
    register(id, el) { el.id = id; this.byId.set(id, el); this.body.appendChild(el); return el; }
    querySelector(sel) {
        const find = (el) => { if (el.tagName.toLowerCase() === sel) return el; for (const c of el.children) { const f = find(c); if (f) return f; } return null; };
        return find(this.body);
    }
}

export function installDomShim({ dpr = 2, canvasWidth = 640, canvasHeight = 440 } = {}) {
    const document = new FakeDocument();
    const canvas = document.register('view', document.createElement('canvas'));
    canvas.rect = { left: 16, top: 16, width: canvasWidth, height: canvasHeight };
    for (const id of ['echo', 'stats', 'log']) document.register(id, document.createElement('div'));
    document.register('animate', document.createElement('input')).checked = true;

    const shim = { document, canvas, dpr, rafQueue: new Map(), rafId: 0, resizeObservers: [], intersectionObservers: [], mediaQueries: [] };

    globalThis.document = document;
    globalThis.devicePixelRatio = dpr;
    globalThis.ImageData = class ImageData {
        constructor(data, width, height) {
            if (!(data instanceof Uint8ClampedArray) || data.length !== width * height * 4) throw new Error('shim: bad ImageData');
            this.data = data; this.width = width; this.height = height;
        }
    };
    globalThis.requestAnimationFrame = (fn) => { const id = ++shim.rafId; shim.rafQueue.set(id, fn); return id; };
    globalThis.cancelAnimationFrame = (id) => { shim.rafQueue.delete(id); };
    globalThis.ResizeObserver = class {
        constructor(cb) { this.cb = cb; this.targets = []; shim.resizeObservers.push(this); }
        observe(t, opts) { this.targets.push({ t, box: opts?.box }); }
        disconnect() { this.targets = []; }
    };
    globalThis.IntersectionObserver = class {
        constructor(cb) { this.cb = cb; this.targets = []; shim.intersectionObservers.push(this); }
        observe(t) { this.targets.push(t); }
        disconnect() { this.targets = []; }
    };
    globalThis.matchMedia = (query) => {
        const mq = new FakeEventTarget();
        mq.media = query;
        mq.matches = true;
        shim.mediaQueries.push(mq);
        return mq;
    };

    /** 执行当前排队的全部 rAF 回调（一帧），返回执行的个数。 */
    shim.flushFrames = (time = performance.now()) => {
        const pending = [...shim.rafQueue.entries()];
        shim.rafQueue.clear();
        for (const [, fn] of pending) fn(time);
        return pending.length;
    };
    /** 模拟 ResizeObserver 回调（可带设备像素盒）。 */
    shim.resize = (width, height, { device = true } = {}) => {
        canvas.rect = { ...canvas.rect, width, height };
        const d = globalThis.devicePixelRatio;
        const entry = {
            target: canvas,
            contentRect: { width, height },
            contentBoxSize: [{ inlineSize: width, blockSize: height }],
            devicePixelContentBoxSize: device ? [{ inlineSize: Math.round(width * d), blockSize: Math.round(height * d) }] : undefined,
        };
        for (const ro of shim.resizeObservers) if (ro.targets.some((x) => x.t === canvas)) ro.cb([entry]);
    };
    shim.setDpr = (value) => {
        globalThis.devicePixelRatio = value;
        const live = shim.mediaQueries.filter((m) => m.listenerCount('change') > 0);
        for (const m of live) m.fire('change');
    };
    shim.intersect = (isIntersecting) => {
        for (const io of shim.intersectionObservers) if (io.targets.includes(canvas)) io.cb([{ target: canvas, isIntersecting }]);
    };
    shim.setVisibility = (state) => { document.visibilityState = state; document.fire('visibilitychange'); };
    return shim;
}
