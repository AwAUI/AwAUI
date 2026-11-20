// 在 Node 下运行 WASM 探针的基准。
// 用法：node bench.mjs <publish>/wwwroot [--no-jiterpreter] [--no-simd] [--warmup N] [--frames N] [--filter name] [--tier Scalar|Vector|Vector128] [--series] [--dump <dir>] [--micro [--iterations N]]
import { pathToFileURL } from 'node:url';
import { writeFileSync, mkdirSync } from 'node:fs';
import { join, resolve } from 'node:path';

const argv = process.argv.slice(2);
const root = resolve(argv[0] ?? '.');
const flag = (name) => argv.includes(name);
const opt = (name, fallback) => { const i = argv.indexOf(name); return i >= 0 && i + 1 < argv.length ? argv[i + 1] : fallback; };

const runtimeOptions = [];
if (flag('--no-jiterpreter')) {
    // 关闭 jiterpreter 的三个部分：热循环 trace、解释器入口、间接调用
    runtimeOptions.push('--no-jiterpreter-traces-enabled', '--no-jiterpreter-interp-entry-enabled', '--no-jiterpreter-jit-call-enabled');
}

const env = {};
if (flag('--no-simd')) {
    // 运行时包里的 dotnet.native.wasm 是预编译的（带 SIMD），不装 wasm-tools 无法重链接成无 SIMD 版本（NETSDK1147）。
    // 这里退而求其次：关掉解释器的 SIMD 内建函数与 jiterpreter 的 SIMD 生成，近似「托管代码不用 WASM SIMD」。
    env.MONO_INTERPRETER_OPTIONS = '-simd';
    runtimeOptions.push('--no-jiterpreter-simd-enabled');
}

const { dotnet } = await import(pathToFileURL(join(root, '_framework', 'dotnet.js')).href);
const t0 = performance.now();
const runtime = await dotnet
    .withConfig({ runtimeOptions })
    .withEnvironmentVariables(env)
    .withDiagnosticTracing(false)
    .create();
const startupMs = performance.now() - t0;
const config = runtime.getConfig();
console.log(`# node ${process.version}, startup ${startupMs.toFixed(0)} ms, runtimeOptions=[${runtimeOptions.join(' ')}] env=${JSON.stringify(env)}`);

const benchArgs = flag('--micro') ? ['micro', '--iterations', opt('--iterations', '2000000')] : ['bench', '--warmup', opt('--warmup', '10'), '--frames', opt('--frames', '30')];
if (opt('--filter')) benchArgs.push('--filter', opt('--filter'));
if (flag('--series')) benchArgs.push('--series');
if (opt('--tier')) benchArgs.push('--tier', opt('--tier'));
await runtime.runMain(config.mainAssemblyName, benchArgs);

const dumpDir = opt('--dump');
if (dumpDir) {
    // 固定时间渲染一帧场景，把原始 RGBA 写盘，用于与原生输出逐像素比对
    mkdirSync(dumpDir, { recursive: true });
    const exports = await runtime.getAssemblyExports(config.mainAssemblyName);
    for (const scale of [1, 2]) {
        const ptr = exports.WasmProbe.CanvasHost.RenderReference(scale, 0.25);
        const len = 640 * scale * 440 * scale * 4;
        const heap = runtime.localHeapViewU8();
        writeFileSync(join(dumpDir, `scene@${scale}x.rgba`), heap.subarray(ptr, ptr + len));
    }
    console.log(`# dumped reference frames to ${dumpDir}`);
}
process.exit(0);
