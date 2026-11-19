using System;
#if NET9_0_OR_GREATER
using System.Diagnostics.CodeAnalysis;
#endif

namespace AwAUI.Diagnostics;

/// <summary>
/// 诊断总开关 <c>AwAUI.Diagnostics.Enabled</c>（design/06）。进程启动后只读取一次 <see cref="AppContext"/> 开关。
/// </summary>
/// <remarks>
/// <para>未设置开关时默认为开启（与 .NET 自身的特性开关惯例一致）。应用在 Release 构建中关闭它：</para>
/// <code>
/// &lt;ItemGroup&gt;
///   &lt;RuntimeHostConfigurationOption Include="AwAUI.Diagnostics.Enabled" Value="false" Trim="true" /&gt;
/// &lt;/ItemGroup&gt;
/// </code>
/// <para>
/// 裁剪：net8.0 产物内嵌 <c>ILLink.Substitutions.xml</c>，开关为 false 时 ILLink 把 <see cref="IsEnabled"/> 的方法体替换为常量
/// <c>false</c>；net9+ 产物另带 <c>[FeatureSwitchDefinition]</c>，ILLink/NativeAOT 直接把属性当常量折叠。于是
/// <c>if (DiagnosticHub.IsEnabled(...))</c> 后面的探针代码、DevTools Agent 与传输层都会被当作死代码裁剪掉。
/// netstandard2.0 / .NET Framework 不做裁剪，开关仍在运行时生效。
/// </para>
/// </remarks>
public static class DiagnosticsFeature
{
    /// <summary>特性开关名。</summary>
    public const string SwitchName = "AwAUI.Diagnostics.Enabled";

    /// <summary>诊断功能是否被允许（静态只读，JIT 在分层编译后视为常量）。</summary>
#if NET9_0_OR_GREATER
    [FeatureSwitchDefinition(SwitchName)]
#endif
    public static bool IsEnabled { get; } = AppContext.TryGetSwitch(SwitchName, out bool enabled) ? enabled : true;
}
