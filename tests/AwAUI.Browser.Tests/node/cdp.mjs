// 极简的 Chrome DevTools Protocol 客户端（Node 22+ 自带 WebSocket，无需安装任何包）与本地静态服务器 / 浏览器进程管理。
import { spawn } from 'node:child_process';
import { mkdtempSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';

/** 用 python3 -m http.server 在 127.0.0.1 的随机端口上托管 root（只监听本机）。 */
export async function startStaticServer(root) {
    const proc = spawn('python3', ['-u', '-m', 'http.server', '0', '--bind', '127.0.0.1', '--directory', root], { stdio: ['ignore', 'pipe', 'pipe'] });
    const port = await new Promise((resolve, reject) => {
        let out = '';
        const onData = (d) => {
            out += d;
            const m = /port (\d+)/.exec(out);
            if (m) resolve(Number(m[1]));
        };
        proc.stdout.on('data', onData);
        proc.stderr.on('data', () => { /* 访问日志 */ });
        proc.on('exit', (code) => reject(new Error(`http.server exited (${code}): ${out}`)));
        setTimeout(() => reject(new Error('http.server did not start')), 10000);
    });
    return { url: `http://127.0.0.1:${port}/`, close: () => proc.kill() };
}

/** 启动无头浏览器（chrome-headless-shell 或 Chrome/Edge 的 --headless=new），返回 DevTools 端点。 */
export async function launchBrowser(executable, { scale = 2, width = 1100, height = 760, extraArgs = [] } = {}) {
    const profile = mkdtempSync(join(tmpdir(), 'awaui-cdp-'));
    const isShell = /headless[-_]shell/.test(executable);
    const args = [
        isShell ? '--headless' : '--headless=new',
        '--remote-debugging-port=0', `--user-data-dir=${profile}`, '--no-first-run', '--no-default-browser-check',
        `--force-device-scale-factor=${scale}`, `--window-size=${width},${height}`, '--disable-extensions',
        ...extraArgs, 'about:blank',
    ];
    const proc = spawn(executable, args, { stdio: ['ignore', 'ignore', 'pipe'] });
    const wsUrl = await new Promise((resolve, reject) => {
        let err = '';
        proc.stderr.on('data', (d) => {
            err += d;
            const m = /DevTools listening on (ws:\/\/\S+)/.exec(err);
            if (m) resolve(m[1]);
        });
        proc.on('exit', (code) => reject(new Error(`browser exited (${code}): ${err.slice(0, 2000)}`)));
        setTimeout(() => reject(new Error('browser did not start')), 20000);
    });
    const httpBase = wsUrl.replace(/^ws:/, 'http:').replace(/\/devtools\/browser\/.*/, '');
    return {
        httpBase,
        close: () => { proc.kill(); setTimeout(() => rmSync(profile, { recursive: true, force: true }), 500); },
    };
}

export class CdpSession {
    constructor(ws) {
        this.ws = ws;
        this.nextId = 1;
        this.pending = new Map();
        this.listeners = new Map();
        ws.addEventListener('message', (ev) => {
            const msg = JSON.parse(ev.data);
            if (msg.id !== undefined) {
                const p = this.pending.get(msg.id);
                if (!p) return;
                this.pending.delete(msg.id);
                if (msg.error) p.reject(new Error(`${p.method}: ${msg.error.message}`));
                else p.resolve(msg.result);
            } else {
                for (const fn of this.listeners.get(msg.method) ?? []) fn(msg.params);
            }
        });
    }

    static async connect(url) {
        const ws = new WebSocket(url);
        await new Promise((resolve, reject) => { ws.onopen = resolve; ws.onerror = () => reject(new Error(`cannot connect ${url}`)); });
        return new CdpSession(ws);
    }

    send(method, params = {}) {
        const id = this.nextId++;
        this.ws.send(JSON.stringify({ id, method, params }));
        return new Promise((resolve, reject) => this.pending.set(id, { resolve, reject, method }));
    }

    on(method, fn) {
        if (!this.listeners.has(method)) this.listeners.set(method, []);
        this.listeners.get(method).push(fn);
    }

    /** 在页面中求值并按值返回；表达式抛出时抛出。 */
    async eval(expression) {
        const r = await this.send('Runtime.evaluate', { expression, returnByValue: true, awaitPromise: true });
        if (r.exceptionDetails) throw new Error(`eval failed: ${r.exceptionDetails.exception?.description ?? r.exceptionDetails.text}`);
        return r.result.value;
    }

    close() { this.ws.close(); }
}

/** 打开一个新页面并连接到它。 */
export async function openPage(httpBase) {
    const res = await fetch(`${httpBase}/json/new?about:blank`, { method: 'PUT' });
    const target = await res.json();
    return CdpSession.connect(target.webSocketDebuggerUrl);
}

export const delay = (ms) => new Promise((r) => setTimeout(r, ms));
