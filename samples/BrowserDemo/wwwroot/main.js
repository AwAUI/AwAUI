// BrowserDemo 的页面脚本：注册 C# 用到的页面回调（模块名 "BrowserDemo"），然后运行 Main。
// 不用 dotnet.run()：它在 Main 返回后会退出运行时，而这里 Main 只做初始化，之后由浏览器事件驱动。
import { dotnet } from './_framework/dotnet.js';

const $ = (id) => document.getElementById(id);
const logLines = [];
const escape = (s) => s.replace(/[&<>]/g, (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;' })[c]);

const page = {
    log(line) {
        logLines.push(line);
        if (logLines.length > 200) logLines.shift();
        const el = $('log');
        el.textContent = logLines.join('\n');
        el.scrollTop = el.scrollHeight;
    },
    setStats(text) { $('stats').textContent = text; },
    setEcho(committed, composition, selStart, selEnd) {
        let html = escape(committed);
        if (composition) {
            const a = escape(composition.slice(0, selStart)), b = escape(composition.slice(selStart, selEnd)), c = escape(composition.slice(selEnd));
            html += `<span class="comp">${a}<span class="caret"></span>${b}${c}</span>`;
        }
        $('echo').innerHTML = html || '&nbsp;';
    },
};

const runtime = await dotnet.create();
runtime.setModuleImports('BrowserDemo', page);
const config = runtime.getConfig();
await runtime.runMain(config.mainAssemblyName, []);

// 冒烟测试与测量脚本经 globalThis.awauiDemo 访问 DemoProbe
const exports = await runtime.getAssemblyExports(config.mainAssemblyName);
globalThis.awauiDemo = { probe: exports.BrowserDemo.DemoProbe, runtime };
$('animate').addEventListener('change', (e) => exports.BrowserDemo.DemoProbe.SetAnimating(e.target.checked));
