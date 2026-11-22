using System;

namespace AwAUI.Platform;

/// <summary>
/// 系统的浅色 / 深色外观偏好（CSS 的 <c>prefers-color-scheme</c>），经 <see cref="WindowImpl.GetFeature{T}"/> 获取；
/// 在 UI 线程上读取并订阅。
/// </summary>
public interface IAppearancePreference
{
    /// <summary>系统当前是否为深色外观。</summary>
    bool PrefersDark { get; }

    /// <summary>系统外观变化时在 UI 线程上报告新值。</summary>
    event Action<bool>? PrefersDarkChanged;
}
