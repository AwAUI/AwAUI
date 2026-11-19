// 真实浏览器中的端到端冒烟测试与测量：用 python3 http.server 在本机托管 BrowserDemo 的发布目录，
// 启动无头 Chromium（或 Edge / Chrome 的 --headless=new），经 DevTools 协议注入真实的输入事件
// （鼠标、滚轮、触摸、键盘、输入法组合），再读取 C# 侧状态（BrowserDemo 的 DemoProbe）断言。
//
// 用法：node browser-smoke.mjs <publish>/wwwroot [--browser <可执行文件>] [--scale 2] [--screenshot out.png]
//                             [--bench-frames 60] [--no-tests]
// 退出码：0 全部通过，1 有失败。
import { existsSync, readdirSync, writeFileSync } from 'node:fs';
import { homedir } from 'node:os';
import { join, resolve } from 'node:path';
import { startStaticServer, launchBrowser, openPage, delay } from './cdp.mjs';

const argv = process.argv.slice(2);
const opt = (name, fallback) => { const i = argv.indexOf(name); return i >= 0 && i + 1 < argv.length ? argv[i + 1] : fallback; };
const flag = (name) => argv.includes(name);
const root = resolve(argv[0] ?? '.');
const scale = Number(opt('--scale', '2'));
const benchFrames = Number(opt('--bench-frames', '60'));

function findBrowser() {
    const explicit = opt('--browser');
    if (explicit) return explicit;
    const cache = join(homedir(), 'Library/Caches/ms-playwright');
    if (existsSync(cache)) {
        for (const d of readdirSync(cache).filter((d) => d.startsWith('chromium_headless_shell')).sort().reverse()) {
            for (const sub of ['chrome-headless-shell-mac-arm64', 'chrome-headless-shell-mac-x64', 'chrome-headless-shell-linux64']) {
                const exe = join(cache, d, sub, 'chrome-headless-shell');
                if (existsSync(exe)) return exe;
            }
        }
    }
    for (const exe of ['/Applications/Google Chrome.app/Contents/MacOS/Google Chrome', '/Applications/Microsoft Edge.app/Contents/MacOS/Microsoft Edge',
        '/usr/bin/chromium', '/usr/bin/google-chrome']) if (existsSync(exe)) return exe;
    throw new Error('no Chromium-based browser found; pass --browser');
}

let failures = 0;
const check = (name, ok, detail = '') => {
    console.log(`${ok ? 'PASS' : 'FAIL'}  ${name}${detail ? `  (${detail})` : ''}`);
    if (!ok) failures++;
};

const server = await startStaticServer(root);
const browserExe = findBrowser();
const browser = await launchBrowser(browserExe, { scale });
let page;
try {
    page = await openPage(browser.httpBase);
    const consoleLines = [];
    page.on('Runtime.consoleAPICalled', (p) => consoleLines.push(p.args.map((a) => a.value ?? a.description).join(' ')));
    page.on('Runtime.exceptionThrown', (p) => consoleLines.push('EXCEPTION ' + (p.exceptionDetails.exception?.description ?? p.exceptionDetails.text)));
    await page.send('Runtime.enable');
    await page.send('Page.enable');
    const version = await page.send('Browser.getVersion');
    console.log(`# browser: ${version.product} (${browserExe})`);
    console.log(`# serving ${root} at ${server.url}`);

    // 记录胶水登记的 resolution 媒体查询，供 DPR 变化测试派发事件
    await page.send('Page.addScriptToEvaluateOnNewDocument', { source: `(() => {
        const mm = globalThis.matchMedia.bind(globalThis); globalThis.__awauiMql = [];
        globalThis.matchMedia = (q) => { const m = mm(q); if (q.includes('resolution')) globalThis.__awauiMql.push(m); return m; };
    })()` });
    const t0 = performance.now();
    await page.send('Page.navigate', { url: server.url });
    let ready = false;
    for (let i = 0; i < 300 && !ready; i++) {
        await delay(100);
        ready = await page.eval('!!globalThis.awauiDemo').catch(() => false);
    }
    if (!ready) {
        console.log(consoleLines.join('\n'));
        throw new Error('demo did not start');
    }
    console.log(`# startup ${(performance.now() - t0).toFixed(0)} ms (navigate → Main returned)`);
    const state = async () => JSON.parse(await page.eval('awauiDemo.probe.State()'));
    const rect = await page.eval(`(() => { const r = document.getElementById('view').getBoundingClientRect(); return { x: r.left, y: r.top, w: r.width, h: r.height }; })()`);
    const at = (x, y) => ({ x: rect.x + x, y: rect.y + y }); // 逻辑坐标 → 页面坐标（canvas 未缩放时）
    const mouse = (type, p, extra = {}) => page.send('Input.dispatchMouseEvent', { type, x: p.x, y: p.y, button: 'none', ...extra });

    let s = await state();
    for (let i = 0; i < 50 && s.frames < 4; i++) { await delay(100); s = await state(); }
    const expectW = Math.round(640 * scale), expectH = Math.round(440 * scale);
    if (!flag('--no-tests')) {
        check('canvas metrics', s.clientWidth === 640 && s.clientHeight === 440 && s.scale === scale && s.pixelWidth === expectW && s.pixelHeight === expectH,
            `${s.clientWidth}x${s.clientHeight} @${s.scale} → ${s.pixelWidth}x${s.pixelHeight}`);
        const canvasSize = await page.eval(`(() => { const c = document.getElementById('view'); return [c.width, c.height]; })()`);
        check('canvas backing store = framebuffer', canvasSize[0] === expectW && canvasSize[1] === expectH, canvasSize.join('x'));
        check('frames flowing (rAF)', s.frames > 3 && s.presents > 3, `frames=${s.frames} presents=${s.presents}`);
        const pixel = await page.eval(`(() => { const c = document.getElementById('view'); const d = c.getContext('2d').getImageData(${Math.round(20 * scale)}, ${Math.round(20 * scale)}, 1, 1).data; return [...d]; })()`);
        check('canvas shows rendered background', pixel[3] === 255 && pixel[0] > 200 && pixel[2] > 200, pixel.join(','));

        // 悬停与点击（RenderDemo 的紫色按钮：卡片 420×300 居中，按钮在卡片右下）
        const button = at(110 + 420 - 24 - 60, 70 + 226 + 20);
        await mouse('mouseMoved', button);
        await delay(50);
        s = await state();
        check('hover via pointermove', s.hovered === 'primary', `hovered=${s.hovered}`);
        const cursor = await page.eval(`document.getElementById('view').style.cursor`);
        check('cursor style', cursor === 'pointer', cursor);
        await mouse('mousePressed', button, { button: 'left', buttons: 1, clickCount: 1 });
        await mouse('mouseReleased', button, { button: 'left', buttons: 0, clickCount: 1 });
        await delay(50);
        s = await state();
        check('click', s.clicks === 1, `clicks=${s.clicks}`);
        check('focus moves to proxy input on pointerdown', s.focused === true && s.focusChanges >= 1, `focused=${s.focused}`);

        // 拖动圆片（初始在 (580, 340)），拖出 canvas 后仍跟随（指针捕获）
        const chip0 = { x: s.chipX, y: s.chipY };
        await mouse('mouseMoved', at(chip0.x, chip0.y));
        await mouse('mousePressed', at(chip0.x, chip0.y), { button: 'left', buttons: 1, clickCount: 1 });
        // 拖动中的移动必须带 button: 'left'，否则 Chromium 视为按键已松开并释放指针捕获
        for (let i = 1; i <= 10; i++) await mouse('mouseMoved', at(chip0.x - 30 * i, chip0.y - 20 * i), { button: 'left', buttons: 1 });
        await mouse('mouseMoved', at(700, 470), { button: 'left', buttons: 1 }); // canvas 之外（右下方的页面区域）
        await delay(30);
        s = await state();
        check('drag follows pointer outside canvas (capture)', Math.abs(s.chipX - 700) < 0.6 && Math.abs(s.chipY - 470) < 0.6, `chip=(${s.chipX},${s.chipY})`);
        await mouse('mouseMoved', at(300, 200), { button: 'left', buttons: 1 });
        await mouse('mouseReleased', at(300, 200), { button: 'left', buttons: 0, clickCount: 1 });
        await mouse('mouseMoved', at(320, 220));
        await delay(30);
        s = await state();
        check('drag ends on release', Math.abs(s.chipX - 300) < 0.6 && Math.abs(s.chipY - 200) < 0.6, `chip=(${s.chipX},${s.chipY})`);

        // 滚轮：DOM deltaY=+120（向下）→ 平台约定 -120
        await mouse('mouseWheel', at(200, 200), { deltaX: 0, deltaY: 120 });
        await delay(50);
        s = await state();
        check('wheel (sign + pixel mode)', s.wheelEvents === 1 && s.wheelY === -120, `events=${s.wheelEvents} y=${s.wheelY}`);
        const scrollY = await page.eval('window.scrollY');
        check('wheel does not scroll page', scrollY === 0, `scrollY=${scrollY}`);

        // 触摸：两指按下 → 两个触摸点，抬起 → 0
        await page.send('Emulation.setTouchEmulationEnabled', { enabled: true, maxTouchPoints: 5 });
        const t1 = at(150, 150), t2 = at(250, 160);
        await page.send('Input.dispatchTouchEvent', { type: 'touchStart', touchPoints: [{ x: t1.x, y: t1.y, id: 1 }, { x: t2.x, y: t2.y, id: 2 }] });
        await page.send('Input.dispatchTouchEvent', { type: 'touchMove', touchPoints: [{ x: t1.x + 10, y: t1.y + 5, id: 1 }, { x: t2.x - 10, y: t2.y, id: 2 }] });
        await delay(50);
        s = await state();
        check('multi-touch pointers', s.touches === 2, `touches=${s.touches}`);
        await page.send('Input.dispatchTouchEvent', { type: 'touchEnd', touchPoints: [] });
        await delay(50);
        s = await state();
        check('touch release', s.touches === 0, `touches=${s.touches}`);
        await page.send('Emulation.setTouchEmulationEnabled', { enabled: false });

        // 文本框：点击获得焦点后键入
        const field = at(320, 440 - 36);
        await mouse('mouseMoved', field);
        await mouse('mousePressed', field, { button: 'left', buttons: 1, clickCount: 1 });
        await mouse('mouseReleased', field, { button: 'left', buttons: 0, clickCount: 1 });
        await delay(30);
        s = await state();
        check('text field focus', s.fieldFocused === true && s.focused === true, `field=${s.fieldFocused} focused=${s.focused}`);
        const inputMode = await page.eval(`document.querySelector('textarea').getAttribute('inputmode')`);
        check('soft keyboard enabled (inputmode)', inputMode === 'text', inputMode);
        const key = async (code, k, vk, text) => {
            await page.send('Input.dispatchKeyEvent', { type: text ? 'keyDown' : 'rawKeyDown', code, key: k, windowsVirtualKeyCode: vk, text });
            await page.send('Input.dispatchKeyEvent', { type: 'keyUp', code, key: k, windowsVirtualKeyCode: vk });
        };
        await key('KeyH', 'h', 72, 'h');
        await key('KeyI', 'i', 73, 'i');
        await delay(30);
        s = await state();
        check('typing → OnKey + OnTextInput', s.text === 'hi' && s.lastKey === 'KeyI' && s.lastKeyValue === 'i', `text="${s.text}" lastKey=${s.lastKey}/${s.lastKeyValue}`);
        const proxyValue = await page.eval(`document.querySelector('textarea').value`);
        check('proxy textarea stays empty', proxyValue === '', JSON.stringify(proxyValue));

        // 输入法：组合 "ni" → "nihao"（光标在中间）→ 提交 "你好"
        await page.send('Input.imeSetComposition', { text: 'ni', selectionStart: 2, selectionEnd: 2 });
        await delay(30);
        s = await state();
        check('IME composition start/update', s.composing === true && s.composition === 'ni' && s.compositionCaret === 2, `composing=${s.composing} "${s.composition}" caret=${s.compositionCaret}`);
        await page.send('Input.imeSetComposition', { text: 'nihao', selectionStart: 3, selectionEnd: 3 });
        await delay(30);
        s = await state();
        check('IME composition caret', s.composition === 'nihao' && s.compositionCaret === 3, `"${s.composition}" caret=${s.compositionCaret}`);
        await page.send('Input.insertText', { text: '你好' });
        await delay(50);
        s = await state();
        check('IME commit', s.composing === false && s.text === 'hi你好', `composing=${s.composing} text="${s.text}"`);
        const caretTop = await page.eval(`parseFloat(document.querySelector('textarea').style.top)`);
        check('proxy textarea follows caret', Math.abs(caretTop - (rect.y + 440 - 56 + 10)) < 2, `top=${caretTop}`);
        // 组合中按键不交给框架；Backspace 删除一个字符
        await key('Backspace', 'Backspace', 8);
        await delay(30);
        s = await state();
        check('Backspace (prevented, handled by app)', s.text === 'hi你' && s.lastKey === 'Backspace', `text="${s.text}"`);

        // 粘贴：没有订阅 Pasted 时作为文本输入
        await page.eval(`(() => { const t = document.querySelector('textarea'); const dt = new DataTransfer(); dt.setData('text/plain', 'P\\r\\nQ'); t.dispatchEvent(new ClipboardEvent('paste', { clipboardData: dt, bubbles: true, cancelable: true })); })()`);
        await delay(30);
        s = await state();
        check('paste → text input (CRLF → LF)', s.text === 'hi你P\nQ', JSON.stringify(s.text));

        // AltGr：Windows 上浏览器把 AltGr 报告为 Ctrl+Alt（CDP modifiers: 1 Alt, 2 Ctrl）。产生字符的组合不能 preventDefault，
        // 否则浏览器不派发 beforeinput，@ € { 等无法输入（审查发现的问题）；普通 Ctrl+S 仍被阻止、不产生文本
        await page.send('Input.dispatchKeyEvent', { type: 'keyDown', modifiers: 3, code: 'KeyQ', key: '@', windowsVirtualKeyCode: 81, text: '@' });
        await page.send('Input.dispatchKeyEvent', { type: 'keyUp', modifiers: 3, code: 'KeyQ', key: '@', windowsVirtualKeyCode: 81 });
        await page.send('Input.dispatchKeyEvent', { type: 'keyDown', modifiers: 2, code: 'KeyS', key: 's', windowsVirtualKeyCode: 83, text: 's' });
        await page.send('Input.dispatchKeyEvent', { type: 'keyUp', modifiers: 2, code: 'KeyS', key: 's', windowsVirtualKeyCode: 83 });
        await delay(30);
        s = await state();
        check('AltGr (Ctrl+Alt) character typed, Ctrl+S not', s.text === 'hi你P\nQ@', JSON.stringify(s.text));

        // 组合中焦点离开文本框（文本输入客户端被移除）：平台放弃组合（失焦再聚焦代理文本框），浏览器迟到的 compositionend 不再提交
        await page.send('Input.imeSetComposition', { text: 'zz', selectionStart: 2, selectionEnd: 2 });
        await delay(30);
        s = await state();
        const composingBeforeReset = s.composing;
        const outside = at(320, 440 - 120);
        await mouse('mousePressed', outside, { button: 'left', buttons: 1, clickCount: 1 });
        await mouse('mouseReleased', outside, { button: 'left', buttons: 0, clickCount: 1 });
        await delay(80);
        s = await state();
        check('composition discarded when the client goes away', composingBeforeReset === true && s.composing === false && s.text === 'hi你P\nQ@' && s.fieldFocused === false,
            `before=${composingBeforeReset} composing=${s.composing} field=${s.fieldFocused} text=${JSON.stringify(s.text)}`);
        // 重新聚焦文本框，后面的检查照旧
        await mouse('mousePressed', field, { button: 'left', buttons: 1, clickCount: 1 });
        await mouse('mouseReleased', field, { button: 'left', buttons: 0, clickCount: 1 });
        await delay(30);

        // 空闲：关闭动画后 rAF 停止，回调不再增长；输入后恰好再出一帧
        await page.eval('awauiDemo.probe.SetAnimating(false)');
        await delay(200);
        const idle0 = await state();
        await delay(500);
        const idle1 = await state();
        check('idle: no rAF while nothing requested', idle1.frameCallbacks === idle0.frameCallbacks && idle1.callbackPending === false,
            `callbacks ${idle0.frameCallbacks} → ${idle1.frameCallbacks}, pending=${idle1.callbackPending}`);
        await mouse('mouseMoved', at(100, 100));
        await delay(100);
        const idle2 = await state();
        check('input wakes exactly one frame', idle2.frames - idle1.frames === 1, `frames ${idle1.frames} → ${idle2.frames}`);

        // 尺寸变化：改 CSS 宽度 → ResizeObserver → OnResized + 重新出帧
        await page.eval(`document.getElementById('view').style.width = '500px'`);
        await delay(200);
        s = await state();
        check('ResizeObserver → resize', s.clientWidth === 500 && s.pixelWidth === Math.round(500 * scale) && s.resizes >= 1,
            `${s.clientWidth} → ${s.pixelWidth}px, resizes=${s.resizes}`);
        const cw = await page.eval(`document.getElementById('view').width`);
        check('canvas resized at present', cw === Math.round(500 * scale), `canvas.width=${cw}`);

        // DPR 变化（拖到另一块屏幕 / 页面缩放）。无头 Chromium 的 Emulation 改 deviceScaleFactor 时既不触发
        // resolution 媒体查询的 change，也不更新 device-pixel-content-box，所以这里改写 devicePixelRatio 并在胶水登记的
        // MediaQueryList 上派发 change：验证的是「监听 → 重新计算 → OnResized → 重新出帧」这条接线，不是浏览器何时触发事件。
        await page.eval(`(() => { globalThis.__dpr0 = globalThis.devicePixelRatio; Object.defineProperty(globalThis, 'devicePixelRatio', { get: () => 1, configurable: true }); globalThis.__awauiMql.at(-1).dispatchEvent(new Event('change')); })()`);
        await delay(200);
        s = await state();
        check('devicePixelRatio change (matchMedia path)', s.scale === 1 && s.pixelWidth === 500 && s.pixelHeight === 440, `@${s.scale} → ${s.pixelWidth}x${s.pixelHeight}`);
        const cw1 = await page.eval(`document.getElementById('view').width`);
        check('canvas backing store follows DPR', cw1 === 500, `canvas.width=${cw1}`);
        const mqlCount = await page.eval('globalThis.__awauiMql.length');
        check('media query re-armed for new DPR', mqlCount >= 2 && await page.eval('globalThis.__awauiMql.at(-1).media.includes("1dppx")'), `queries=${mqlCount}`);
        await page.eval(`(() => { Object.defineProperty(globalThis, 'devicePixelRatio', { get: () => globalThis.__dpr0, configurable: true }); globalThis.__awauiMql.at(-1).dispatchEvent(new Event('change')); })()`);
        await page.eval(`document.getElementById('view').style.width = '640px'`);
        await delay(300);
        s = await state();
        check('restored size and DPR', s.scale === scale && s.pixelWidth === expectW, `@${s.scale} → ${s.pixelWidth}`);

        // 页面隐藏 → 暂停出帧（即使应用仍在请求帧）；重新可见后恢复
        await page.eval('awauiDemo.probe.SetAnimating(true)');
        await delay(100);
        await page.eval(`(() => { Object.defineProperty(document, 'visibilityState', { get: () => 'hidden', configurable: true }); document.dispatchEvent(new Event('visibilitychange')); })()`);
        await delay(100);
        const h0 = await state();
        await delay(400);
        const h1 = await state();
        check('hidden page: frames paused', h0.visible === false && h1.frames === h0.frames && h1.callbackPending === false,
            `visible=${h0.visible} frames ${h0.frames} → ${h1.frames}`);
        await page.eval(`(() => { delete document.visibilityState; document.dispatchEvent(new Event('visibilitychange')); })()`);
        await delay(200);
        const h2 = await state();
        check('visible again: frames resume', h2.visible === true && h2.frames > h1.frames, `frames ${h1.frames} → ${h2.frames}`);

        // canvas 滚出视口 → IntersectionObserver 暂停
        await page.eval(`document.getElementById('view').style.marginTop = '4000px'`);
        await delay(300);
        const o0 = await state();
        await delay(300);
        const o1 = await state();
        check('offscreen canvas: frames paused', o0.visible === false && o1.frames === o0.frames, `visible=${o0.visible} frames ${o0.frames} → ${o1.frames}`);
        await page.eval(`document.getElementById('view').style.marginTop = ''`);
        await delay(300);
        const o2 = await state();
        check('back on screen: frames resume', o2.visible === true && o2.frames > o1.frames, `frames ${o1.frames} → ${o2.frames}`);
        await page.eval('awauiDemo.probe.SetAnimating(false)');

        // 额外窗口：CreateWindow 新建 canvas；附加透明 canvas（反预乘路径）；关闭后分离
        const newId = await page.eval(`awauiDemo.probe.TestWindow(null, false, 0, 0, 255, 1)`);
        const created = await page.eval(`(() => { const c = document.getElementById('${newId}'); return c ? [c.width, c.height, ...c.getContext('2d').getImageData(5, 5, 1, 1).data] : null; })()`);
        check('CreateWindow canvas', created && created[0] === Math.round(64 * scale) && created[1] === Math.round(48 * scale) && created[4] === 255 && created[5] === 255,
            JSON.stringify(created));
        await page.eval(`(() => { const c = document.createElement('canvas'); c.id = 'alpha'; c.style.cssText = 'display:block;width:10px;height:10px'; document.body.appendChild(c); })()`);
        await page.eval(`awauiDemo.probe.TestWindow('alpha', true, 255, 0, 0, 0.5)`);
        const red = await page.eval(`[...document.getElementById('alpha').getContext('2d').getImageData(1, 1, 1, 1).data]`);
        check('transparent canvas (unpremultiplied)', red[0] >= 254 && red[1] === 0 && Math.abs(red[3] - 128) <= 1, red.join(','));
        // 页面先以 alpha: true 取过 2D 上下文，再以不透明选项附加：getContext 返回已有的透明上下文，必须走反预乘
        await page.eval(`(() => { const c = document.createElement('canvas'); c.id = 'prealpha'; c.style.cssText = 'display:block;width:10px;height:10px'; document.body.appendChild(c); c.getContext('2d', { alpha: true }); })()`);
        await page.eval(`awauiDemo.probe.TestWindow('prealpha', false, 255, 0, 0, 0.5)`);
        const preRed = await page.eval(`[...document.getElementById('prealpha').getContext('2d').getImageData(1, 1, 1, 1).data]`);
        check('pre-existing alpha context (unpremultiplied)', preRed[0] >= 254 && preRed[1] === 0 && Math.abs(preRed[3] - 128) <= 1, preRed.join(','));
        const twice = await page.eval(`(() => { try { awauiDemo.probe.TestWindow('prealpha', false, 0, 0, 0, 1); return 'no error'; } catch (e) { return String(e.message); } })()`);
        check('attaching the same canvas twice throws', /already attached/.test(twice), twice);
        await page.eval(`(() => { const c = document.createElement('canvas'); c.id = 'glcanvas'; document.body.appendChild(c); c.getContext('webgl2') ?? c.getContext('webgl'); })()`);
        const noCtx = await page.eval(`(() => { try { awauiDemo.probe.TestWindow('glcanvas', false, 0, 0, 0, 1); return 'no error'; } catch (e) { return String(e.message); } })()`);
        check('WebGL canvas cannot be attached (no 2D context)', /2D context/.test(noCtx), noCtx);
        const remaining = await page.eval(`awauiDemo.probe.CloseTestWindows()`);
        const gone = await page.eval(`document.getElementById('${newId}') === null && document.querySelectorAll('textarea').length === 1`);
        await page.eval(`document.getElementById('prealpha').remove(); document.getElementById('glcanvas').remove()`);
        check('close detaches windows (canvas + proxy removed)', remaining === 1 && gone, `remaining=${remaining}`);

        // 异步剪贴板（无头浏览器需授予权限；不可用时只报告）
        await page.send('Browser.grantPermissions', { permissions: ['clipboardReadWrite', 'clipboardSanitizedWrite'], origin: server.url.replace(/\/$/, '') }).catch(() => {});
        const clip = await page.eval(`awauiDemo.probe.ClipboardRoundTrip('AwAUI 剪贴板')`);
        console.log(`# async clipboard round trip: ${JSON.stringify(clip)}`);
        check('async clipboard round trip or graceful failure', clip === 'AwAUI 剪贴板' || clip.startsWith('unavailable:'), clip);

        // 键盘焦点离开：blur
        await page.eval(`document.querySelector('textarea').blur()`);
        await delay(30);
        s = await state();
        check('blur → OnFocusChanged(false)', s.focused === false && s.fieldFocused === false, `focused=${s.focused}`);
    }

    // ------------------------------------------------------------ 测量
    await page.eval('awauiDemo.probe.SetAnimating(false)');
    await delay(100);
    s = await state();
    console.log(`# measuring at ${s.pixelWidth}x${s.pixelHeight} device px (@${s.scale}x)`);
    const bench = JSON.parse(await page.eval(`awauiDemo.probe.Benchmark(10, ${benchFrames})`));
    console.log(`# demo frame (sync loop, ${bench.frames} frames): total ${bench.total.toFixed(2)} ms (median ${bench.median.toFixed(2)}, min ${bench.min.toFixed(2)}) = ` +
        `record ${bench.record.toFixed(2)} + raster ${bench.raster.toFixed(2)} + present ${bench.present.toFixed(3)}`);

    // 呈现方式对比（同一个 1280×880 canvas）：
    //   A 零拷贝：缓存的 ImageData 直接指向 WASM 堆（本实现）
    //   B 每帧重建 ImageData 视图（不缓存，仍零拷贝）
    //   C 托管 → JS 复制：先把帧缓冲 slice 出来再 putImageData（不用共享视图时的做法）
    // 每次 put 之后用 1×1 的 getImageData 强制完成（否则 GPU canvas 的上传是异步的，测到的只是排队）；
    // 先把各方式都预热一遍再交替测三轮取最小值，避免先测的一方承担 canvas 加速模式切换等一次性开销。
    const cmp = await page.eval(`(() => {
        const W = ${expectW}, H = ${expectH}, len = W * H * 4, ptr = 1 << 20; // 任一段线性内存即可
        const c = document.createElement('canvas'); c.width = W; c.height = H;
        document.body.appendChild(c);
        const ctx = c.getContext('2d', { alpha: false });
        const heap = awauiDemo.runtime.localHeapViewU8();
        const cached = new ImageData(new Uint8ClampedArray(heap.buffer, ptr, len), W, H);
        const cases = {
            A_cachedView: () => ctx.putImageData(cached, 0, 0),
            B_newView: () => ctx.putImageData(new ImageData(new Uint8ClampedArray(heap.buffer, ptr, len), W, H), 0, 0),
            C_copyThenPut: () => ctx.putImageData(new ImageData(new Uint8ClampedArray(heap.slice(ptr, ptr + len).buffer), W, H), 0, 0),
            copyOnly: () => heap.slice(ptr, ptr + len),
        };
        const res = {};
        for (const fn of Object.values(cases)) for (let i = 0; i < 5; i++) { fn(); ctx.getImageData(0, 0, 1, 1); }
        for (let round = 0; round < 3; round++)
            for (const [name, fn] of Object.entries(cases)) {
                const t = performance.now();
                for (let i = 0; i < 30; i++) { fn(); ctx.getImageData(0, 0, 1, 1); }
                const ms = (performance.now() - t) / 30;
                res[name] = Math.min(res[name] ?? Infinity, ms);
            }
        c.remove();
        return res;
    })()`);
    console.log(`# present ${expectW}x${expectH} (put + forced flush, min of 3): A cached heap view ${cmp.A_cachedView.toFixed(3)} ms · B new view per frame ${cmp.B_newView.toFixed(3)} ms · ` +
        `C slice copy + put ${cmp.C_copyThenPut.toFixed(3)} ms (copy alone ${cmp.copyOnly.toFixed(3)} ms)`);

    // rAF 驱动的真实帧率
    await page.eval('awauiDemo.probe.SetAnimating(true)');
    const f0 = await state();
    const tw = performance.now();
    await delay(3000);
    const f1 = await state();
    const fps = (f1.frames - f0.frames) * 1000 / (performance.now() - tw);
    console.log(`# rAF loop: ${fps.toFixed(1)} fps, last frame ${f1.lastRenderMs.toFixed(1)} ms (record ${f1.lastRecordMs.toFixed(2)}, present ${f1.lastPresentMs.toFixed(3)})`);
    await page.eval('awauiDemo.probe.SetAnimating(false)');

    const shot = opt('--screenshot');
    if (shot) {
        const img = await page.send('Page.captureScreenshot', { format: 'png' });
        writeFileSync(shot, Buffer.from(img.data, 'base64'));
        console.log(`# screenshot → ${shot}`);
    }
    const errors = consoleLines.filter((l) => /EXCEPTION|error/i.test(l));
    if (!flag('--no-tests')) check('no page errors', errors.length === 0, errors.slice(0, 3).join(' | '));
} finally {
    page?.close();
    browser.close();
    server.close();
}
console.log(failures === 0 ? '# all browser checks passed' : `# ${failures} browser check(s) FAILED`);
process.exit(failures === 0 ? 0 : 1);
