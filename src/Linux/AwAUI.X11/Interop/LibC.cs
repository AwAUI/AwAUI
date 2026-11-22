using System;
using System.Runtime.InteropServices;

namespace AwAUI.X11.Interop;

/// <summary>
/// glibc 的裸互操作：自唤醒管道、poll、单调时钟、System V 共享内存（MIT-SHM）、setlocale（XIM 需要）。
/// 常量取 Linux 通用值（x86_64 / arm64 相同）。显式加载 libc.so.6，因为 "libc.so" 在 glibc 上是链接脚本而非 ELF。
/// </summary>
internal static unsafe class LibC
{
    const string Lib = "libc.so.6";

    [DllImport(Lib, ExactSpelling = true, SetLastError = true)] public static extern int pipe2(int* fds, int flags);
    [DllImport(Lib, ExactSpelling = true, SetLastError = true)] public static extern int poll(PollFd* fds, ulong nfds, int timeout);
    [DllImport(Lib, ExactSpelling = true, SetLastError = true)] public static extern IntPtr read(int fd, void* buffer, UIntPtr count);
    [DllImport(Lib, ExactSpelling = true, SetLastError = true)] public static extern IntPtr write(int fd, void* buffer, UIntPtr count);
    [DllImport(Lib, ExactSpelling = true)] public static extern int close(int fd);
    [DllImport(Lib, ExactSpelling = true)] public static extern int clock_gettime(int clockId, Timespec* ts);
    [DllImport(Lib, ExactSpelling = true)] public static extern IntPtr setlocale(int category, byte* locale);
    /// <summary>
    /// size_t mbrtowc(wchar_t*, const char*, size_t, mbstate_t*)：按当前 LC_CTYPE 区域设置把一个多字节字符转换为宽字符。
    /// Linux 上 wchar_t 是 4 字节的 UTF-32；返回 (size_t)-1 表示无效序列、(size_t)-2 表示末尾不完整序列，0 表示遇到 NUL。
    /// </summary>
    [DllImport(Lib, ExactSpelling = true)] public static extern UIntPtr mbrtowc(uint* pwc, byte* s, UIntPtr n, byte* state);

    [DllImport(Lib, ExactSpelling = true, SetLastError = true)] public static extern int shmget(int key, UIntPtr size, int flags);
    [DllImport(Lib, ExactSpelling = true, SetLastError = true)] public static extern IntPtr shmat(int id, IntPtr address, int flags);
    [DllImport(Lib, ExactSpelling = true)] public static extern int shmdt(IntPtr address);
    [DllImport(Lib, ExactSpelling = true)] public static extern int shmctl(int id, int cmd, void* buffer);

    public const int O_NONBLOCK = 0x800;     // 04000
    public const int O_CLOEXEC = 0x80000;    // 02000000
    public const short POLLIN = 0x1;
    public const int CLOCK_MONOTONIC = 1;
    public const int LC_CTYPE = 0;           // glibc
    public const int IPC_PRIVATE = 0;
    public const int IPC_CREAT = 0x200;      // 01000
    public const int IPC_RMID = 0;

    /// <summary>CLOCK_MONOTONIC，单位 100 ns tick。</summary>
    public static long MonotonicTicks()
    {
        Timespec ts;
        clock_gettime(CLOCK_MONOTONIC, &ts);
        return ts.tv_sec * TimeSpan.TicksPerSecond + ts.tv_nsec / 100;
    }
}
