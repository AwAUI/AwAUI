using System;
using System.Runtime.InteropServices;

namespace AwAUI.Win32.Interop;

/// <summary>advapi32 的裸互操作（读取浅色 / 深色外观的用户设置）。</summary>
internal static unsafe class Advapi32
{
    const string Lib = "advapi32.dll";

    /// <summary>HKEY_CURRENT_USER（预定义句柄，32 位时符号扩展为 0x80000001）。</summary>
    public static readonly IntPtr HKEY_CURRENT_USER = new(unchecked((int)0x80000001));

    public const int KEY_READ = 0x20019;
    public const uint REG_DWORD = 4;

    [DllImport(Lib, ExactSpelling = true)]
    public static extern int RegOpenKeyExW(IntPtr key, char* subKey, uint options, int samDesired, out IntPtr result);

    [DllImport(Lib, ExactSpelling = true)]
    public static extern int RegQueryValueExW(IntPtr key, char* valueName, IntPtr reserved, out uint type, byte* data, ref uint dataSize);

    [DllImport(Lib, ExactSpelling = true)]
    public static extern int RegCloseKey(IntPtr key);
}
