using System;
using System.Runtime.InteropServices;

namespace AwAUI.MacOS.Interop;

/// <summary>
/// CVTimeStamp 的前半部分（只读取到 videoRefreshPeriod 为止，经指针访问，不按值传递）。
/// 完整布局：version, videoTimeScale, videoTime, hostTime, rateScalar, videoRefreshPeriod, smpteTime(24B), flags, reserved。
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct CVTimeStampHead
{
    public uint Version;
    public int VideoTimeScale;
    public long VideoTime;
    public ulong HostTime;
    public double RateScalar;
    public long VideoRefreshPeriod;
}

/// <summary>CoreVideo 的 CVDisplayLink（macOS 14 之前的帧节拍来源）。所有签名均为 blittable。</summary>
internal static unsafe class CV
{
    const string Lib = "/System/Library/Frameworks/CoreVideo.framework/CoreVideo";

    [DllImport(Lib)] public static extern int CVDisplayLinkCreateWithActiveCGDisplays(IntPtr* link);
    [DllImport(Lib)] public static extern int CVDisplayLinkSetOutputCallback(IntPtr link, IntPtr callback, IntPtr userInfo);
    [DllImport(Lib)] public static extern int CVDisplayLinkSetCurrentCGDisplay(IntPtr link, uint displayId);
    [DllImport(Lib)] public static extern int CVDisplayLinkStart(IntPtr link);
    [DllImport(Lib)] public static extern int CVDisplayLinkStop(IntPtr link);
    [DllImport(Lib)] public static extern void CVDisplayLinkRelease(IntPtr link);
}

/// <summary>
/// 主机时间（mach_absolute_time，单位换算为秒）。CADisplayLink 的时间戳（CACurrentMediaTime）、
/// CVTimeStamp.hostTime 与 NSEvent.timestamp 都基于这个时基，因此帧时间与输入时间可以直接比较。
/// </summary>
internal static unsafe class HostTime
{
    const string LibSystem = "/usr/lib/libSystem.dylib";

    [StructLayout(LayoutKind.Sequential)]
    struct MachTimebaseInfo { public uint Numer, Denom; }

    [DllImport(LibSystem)] static extern ulong mach_absolute_time();
    [DllImport(LibSystem)] static extern int mach_timebase_info(MachTimebaseInfo* info);

    static readonly double s_secondsPerTick = Init();

    static double Init()
    {
        MachTimebaseInfo info;
        if (mach_timebase_info(&info) != 0 || info.Denom == 0) return 1e-9;
        return (double)info.Numer / info.Denom * 1e-9;
    }

    /// <summary>当前主机时间（秒）。</summary>
    public static double Now => mach_absolute_time() * s_secondsPerTick;

    /// <summary>把 mach 主机时间单位换算为秒。</summary>
    public static double ToSeconds(ulong hostTime) => hostTime * s_secondsPerTick;
}
