using AwAUI.Css.Media;

namespace AwAUI.UI;

public sealed partial class RenderPipeline
{
    /// <summary>同步宿主的系统偏好；仅当媒体查询的匹配结果变化时重算样式。</summary>
    internal bool SetPrefersReducedMotion(bool value)
    {
        var media = _engine.Environment.Media;
        if (media.PrefersReducedMotion == value) return false;
        media.PrefersReducedMotion = value;
        if (!_engine.EnvironmentChanged()) return false;
        StyleSheetsChanged();
        return true;
    }

    /// <summary>同步宿主的系统外观（<c>prefers-color-scheme</c>）；仅当媒体查询的匹配结果变化时重算样式。</summary>
    internal bool SetPrefersDark(bool value)
    {
        var media = _engine.Environment.Media;
        var scheme = value ? MediaColorScheme.Dark : MediaColorScheme.Light;
        if (media.ColorScheme == scheme) return false;
        media.ColorScheme = scheme;
        if (!_engine.EnvironmentChanged()) return false;
        StyleSheetsChanged();
        return true;
    }
}
