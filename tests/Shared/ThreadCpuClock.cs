// 共享测试工具：由需要它的测试项目以链接方式编译（见各 csproj）。
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace AwAUI.Tests.Shared;

/// <summary>
/// 当前线程已消耗的 CPU 时间（毫秒）。复杂度测试用它代替墙钟：机器超额订阅时，墙钟会把线程等待 CPU 的时间也算进去，
/// 而且越长的测量越难找到一段不被抢占的窗口（长测量被拉长的倍数远大于短测量），比值因此系统性偏大；线程 CPU 时间只计线程真正运行的时间。
/// </summary>
/// <remarks>
/// Linux / macOS 用 <c>clock_gettime(CLOCK_THREAD_CPUTIME_ID)</c>（纳秒级）；Windows 用 <c>GetThreadTimes</c>（按时钟中断累计，约 15.6 ms 粒度，
/// 由调用方的计时下限吸收）。都不可用时退回墙钟。
/// </remarks>
static class ThreadCpuClock
{
    /// <summary>是否真的在测线程 CPU 时间（否则是墙钟）。</summary>
    public static readonly bool IsThreadCpuTime = Probe();

    static readonly Stopwatch s_wall = Stopwatch.StartNew();

    public static double NowMs()
    {
        if (IsThreadCpuTime)
        {
            if (TryRead(out double ms)) return ms;
        }
        return s_wall.Elapsed.TotalMilliseconds;
    }

    static bool Probe()
    {
        try { return TryRead(out _); }
        catch (Exception) { return false; }    // 找不到入口点 / 库
    }

    static bool TryRead(out double ms)
    {
        ms = 0;
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            if (!GetThreadTimes(GetCurrentThread(), out _, out _, out long kernel, out long user)) return false;
            ms = (kernel + user) / 10_000.0;    // 100 ns 为单位
            return true;
        }
        int clock;
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux)) clock = 3;         // CLOCK_THREAD_CPUTIME_ID
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX)) clock = 16;     // _CLOCK_THREAD_CPUTIME_ID
        else return false;
        if (clock_gettime(clock, out Timespec ts) != 0) return false;
        ms = (long)ts.Seconds * 1000.0 + (long)ts.Nanoseconds / 1_000_000.0;
        return true;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct Timespec
    {
        public IntPtr Seconds;      // time_t
        public IntPtr Nanoseconds;  // long
    }

    [DllImport("libc", EntryPoint = "clock_gettime")]
    static extern int clock_gettime(int clockId, out Timespec ts);

    [DllImport("kernel32.dll")]
    static extern IntPtr GetCurrentThread();

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool GetThreadTimes(IntPtr thread, out long creationTime, out long exitTime, out long kernelTime, out long userTime);
}
