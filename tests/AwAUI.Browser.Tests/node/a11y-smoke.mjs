// 影子 ARIA DOM 的独立冒烟测试：不加载 WASM，直接导入 awaui-browser.js，装上 DOM 垫片与假的 [JSExport] 回调，
// 然后像 C# 桥那样调用 a11yAttach / a11yUpdate / a11yDetach，断言影子树结构、焦点、动作回传、live region 与清理。
//
// 用法：node tests/AwAUI.Browser.Tests/node/a11y-smoke.mjs
import { pathToFileURL } from 'node:url';
import { join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { installDomShim } from './dom-shim.mjs';

const here = fileURLToPath(new URL('.', import.meta.url));
const repo = resolve(here, '../../..');
const glue = await import(pathToFileURL(join(repo, 'src/Browser/AwAUI.Browser/wwwroot/awaui-browser.js')).href);

let failures = 0;
const check = (name, ok, detail = '') => {
    console.log(`${ok ? 'PASS' : 'FAIL'}  ${name}${detail ? `  (${detail})` : ''}`);
    if (!ok) failures++;
};
const tick = () => new Promise((r) => setTimeout(r, 0));

const shim = installDomShim({ dpr: 2 });
const { document, canvas } = shim;

const actions = [];
const focuses = [];
const callbacks = {
    OnA11yAction(handle, nodeId, kind, value) { actions.push({ handle, nodeId, kind, value }); },
    OnA11yFocus(handle, nodeId) { focuses.push({ handle, nodeId }); },
    OnActivation() { }, OnLifecycle() { }, OnFullscreen() { },
};
globalThis.getDotnetRuntime = () => ({
    getAssemblyExports: async () => ({ AwAUI: { Browser: { Interop: { BrowserCallbacks: callbacks } } } }),
});
await glue.init();

const attached = glue.attachCanvas(1, 'view', false, 0);
check('canvas attached', Array.isArray(attached) && attached.length >= 7, JSON.stringify(attached));
check('a11y attach', glue.a11yAttach(1) === true);

const container = document.body.children.find((el) => el.getAttribute('data-awaui-a11y') === '1');
check('container exists', !!container);
check('container hidden with opacity, not display/visibility/aria-hidden',
    container.style.position === 'absolute' && container.style.opacity === '0' &&
    container.style.pointerEvents === 'none' && container.style.display === undefined &&
    container.style.visibility === undefined && container.getAttribute('aria-hidden') === null,
    `${container.style.position} opacity=${container.style.opacity}`);

const find = (root, id) => {
    if (!root) return null;
    if (root.getAttribute && root.getAttribute('data-awaui-id') === id) return root;
    for (const child of root.children || []) {
        const hit = find(child, id);
        if (hit) return hit;
    }
    return null;
};

const model = (liveName) => ({
    v: 1, sv: 1, r: 'root',
    n: [
        { i: 'root', r: 'application', b: [0, 0, 640, 440], c: ['btn', 'txt', 'slider', 'live'] },
        { i: 'btn', r: 'button', nm: '关闭', st: ['focused'], a: [0, 1, 2], b: [10, 20, 80, 30] },
        { i: 'txt', r: 'textbox', nm: '搜索', v: 'abc', a: [1, 3], b: [10, 60, 120, 24] },
        { i: 'slider', r: 'slider', nm: '音量', v: '5', a: [1, 4, 5], b: [10, 90, 120, 20] },
        { i: 'live', r: 'status', nm: liveName, lv: 1, b: [10, 120, 120, 20] },
    ],
});

check('a11y update', glue.a11yUpdate(1, JSON.stringify(model('已保存'))) === true);

const rootEl = find(container, 'root');
const btn = find(container, 'btn');
const txt = find(container, 'txt');
const slider = find(container, 'slider');
const live = find(container, 'live');
check('nodes created and linked', !!rootEl && !!btn && !!txt && !!slider && !!live && rootEl.children.length === 4,
    `root children=${rootEl?.children.length}`);
check('root role mapped to application', rootEl.getAttribute('role') === 'application');
check('button name and focusability', btn.getAttribute('role') === 'button' && btn.textContent === '关闭' && btn.getAttribute('tabindex') === '-1');
check('bounds applied', btn.style.left === '10px' && btn.style.top === '20px' && btn.style.width === '80px' && btn.style.height === '30px',
    `${btn.style.left},${btn.style.top} ${btn.style.width}x${btn.style.height}`);
check('textbox is editable and shows value',
    txt.getAttribute('contenteditable') === 'true' && txt.textContent === 'abc' && txt.getAttribute('aria-label') === '搜索');
check('slider value exposed', slider.getAttribute('aria-valuetext') === '5' && slider.getAttribute('aria-valuenow') === '5');
check('live region role / aria-live', live.getAttribute('role') === 'status' && live.getAttribute('aria-live') === 'polite' &&
    live.getAttribute('aria-atomic') === 'true' && live.textContent === '已保存', live.getAttribute('role'));

// 点击 → Invoke
actions.length = 0;
btn.fire('click');
check('click invokes', actions.length === 1 && actions[0].nodeId === 'btn' && actions[0].kind === 0, JSON.stringify(actions));

// 键盘 → Increment / Invoke
actions.length = 0;
slider.fire('keydown', { key: 'ArrowUp' });
check('ArrowUp increments', actions.length === 1 && actions[0].nodeId === 'slider' && actions[0].kind === 4, JSON.stringify(actions));

// contenteditable 输入 → SetValue
actions.length = 0;
txt.textContent = 'xyz';
txt.fire('input');
check('contenteditable input sets value', actions.length === 1 && actions[0].nodeId === 'txt' && actions[0].kind === 3 &&
    actions[0].value === 'xyz', JSON.stringify(actions));

// 没有声明动作的节点不产生动作
actions.length = 0;
rootEl.fire('click');
check('node without actions is ignored', actions.length === 0);

// DOM 焦点 → C# Focus
btn.focus();
check('DOM focus reports node', focuses.length === 1 && focuses[0].nodeId === 'btn' && document.activeElement === btn, JSON.stringify(focuses));
check('aria-activedescendant points at focused node', container.getAttribute('aria-activedescendant') === btn.getAttribute('id'),
    container.getAttribute('aria-activedescendant'));

// live region：文本变化先清空再写
check('live update', glue.a11yUpdate(1, JSON.stringify(model('已更新'))) === true);
check('live text cleared before rewrite', live.textContent === '', `"${live.textContent}"`);
await tick();
check('live text rewritten', live.textContent === '已更新', `"${live.textContent}"`);

// 复用节点 id：元素对象不变
check('node element reused across updates', find(container, 'btn') === btn);

// 移除节点
const smaller = { v: 1, sv: 2, r: 'root', n: [{ i: 'root', r: 'application', b: [0, 0, 640, 440], c: [] }] };
check('a11y update removes stale nodes', glue.a11yUpdate(1, JSON.stringify(smaller)) === true && find(container, 'btn') === null && rootEl.children.length === 0);

// 非法模型
check('invalid json rejected', glue.a11yUpdate(1, '{not json') === false);
check('unknown format version rejected', glue.a11yUpdate(1, JSON.stringify({ v: 99, n: [] })) === false);

// 清理
glue.a11yDetach(1);
check('detach removes container and listeners',
    document.body.children.indexOf(container) < 0 && document.body.children.every((el) => el.getAttribute('data-awaui-a11y') !== '1'));

// 环境变量关闭
try {
    process.env.AWAUI_BROWSER_A11Y = '0';
    check('AWAUI_BROWSER_A11Y=0 disables attach', glue.a11yAttach(1) === false);
} finally {
    delete process.env.AWAUI_BROWSER_A11Y;
}
check('a11y re-enabled after environment reset', glue.a11yAttach(1) === true);
glue.a11yDetach(1);

// 窗口 detach 顺带清理影子树
check('second attach', glue.a11yAttach(1) === true);
const container2 = document.body.children.find((el) => el.getAttribute('data-awaui-a11y') === '1');
check('attach recreated container', !!container2);
glue.detach(1, false);
check('window detach removes shadow tree', document.body.children.indexOf(container2) < 0);

console.log(failures === 0 ? '# all a11y checks passed' : `# ${failures} a11y check(s) FAILED`);
process.exit(failures === 0 ? 0 : 1);
