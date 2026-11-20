using System;

namespace AwAUI.Platform;

/// <summary>系统的减少动态效果偏好，经 <see cref="WindowImpl.GetFeature{T}"/> 获取；在 UI 线程上读取并订阅。</summary>
public interface IReducedMotionPreference
{
    bool PrefersReducedMotion { get; }

    /// <summary>偏好发生变化时在 UI 线程上报告新值。</summary>
    event Action<bool>? PrefersReducedMotionChanged;
}
