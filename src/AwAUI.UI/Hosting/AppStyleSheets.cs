using System;
using System.Collections.Generic;
using System.Threading;
using AwAUI.Css;

namespace AwAUI.UI;

/// <summary>
/// 程序集级的应用样式表登记处：构建期生成的样式表（例如 <c>.awaui</c> 生成器从 Tailwind 入口样式表生成的 <c>TailwindStyles</c>，design/23）
/// 在模块初始化时调用 <see cref="Register"/>，之后每个 <see cref="RenderPipeline"/> 在加入组件样式表之前与每帧开始时把尚未加入的登记项按登记顺序加入级联（Author 来源，
/// 位于宿主先前用 <see cref="UIHost.AddStyleSheet(StyleSheet)"/> 加入的样式表之后、组件样式表之前，因此 Tailwind 的
/// <c>@layer theme, base, components, utilities</c> 先确立层顺序，组件 <c>&lt;style&gt;</c> 里的 <c>@layer components</c> 排在 preflight 之上）。不需要它的管线设 <see cref="RenderPipeline.IncludeAppStyleSheets"/> 为 false。
/// <para>线程安全；同一来源名重复登记时以第一次为准（模块初始化器只运行一次，重复登记通常来自同名的入口）。样式表文本在第一次被管线使用时解析一次，之后共享。</para>
/// </summary>
public static class AppStyleSheets
{
    static readonly object s_gate = new();
    static AppStyleSheet[] s_sheets = Array.Empty<AppStyleSheet>();
    static long s_revision;

    /// <summary>已登记的样式表（快照，按登记顺序）。</summary>
    public static IReadOnlyList<AppStyleSheet> Registered => Volatile.Read(ref s_sheets);

    /// <summary>登记数；管线据此判断是否有新的登记。</summary>
    public static int Count => Volatile.Read(ref s_sheets).Length;

    /// <summary>
    /// 单调递增的修订号：<see cref="Register"/> 新增登记或任一 <see cref="AppStyleSheet.Update"/> 时加一。
    /// 渲染管线据此判断是否需要重新查看已登记的样式表（不需要逐项比较版本）。
    /// </summary>
    public static long Revision => Interlocked.Read(ref s_revision);

    /// <summary>
    /// 登记一张应用样式表。<paramref name="sourceName"/> 用于诊断与 DevTools 显示，也是去重的键：已有同名登记时忽略本次并返回已有的登记项。
    /// </summary>
    public static AppStyleSheet Register(string css, string sourceName)
    {
        if (css is null) throw new ArgumentNullException(nameof(css));
        if (sourceName is null) throw new ArgumentNullException(nameof(sourceName));
        lock (s_gate)
        {
            foreach (var s in s_sheets)
                if (string.Equals(s.SourceName, sourceName, StringComparison.Ordinal)) return s;
            var sheet = new AppStyleSheet(css, sourceName);
            var next = new AppStyleSheet[s_sheets.Length + 1];
            Array.Copy(s_sheets, next, s_sheets.Length);
            next[next.Length - 1] = sheet;
            Volatile.Write(ref s_sheets, next);
            BumpRevision();
            return sheet;
        }
    }

    internal static void BumpRevision() => Interlocked.Increment(ref s_revision);
}

/// <summary><see cref="AppStyleSheets"/> 中的一项：CSS 文本与（按需解析一次的）样式表；可用 <see cref="Update"/> 热替换。</summary>
public sealed class AppStyleSheet
{
    readonly object _gate = new();
    string _text;
    StyleSheet? _sheet;
    // 增量解析的上一份样式表与变化起点；只在 _sheet 尚未重新解析前有效
    StyleSheet? _previous;
    int _pendingFrom = -1;
    int _version;

    internal AppStyleSheet(string text, string sourceName)
    {
        _text = text;
        SourceName = sourceName;
    }

    /// <summary>CSS 文本（<see cref="Update"/> 之后为新的文本）。</summary>
    public string Text => Volatile.Read(ref _text);

    /// <summary>来源名（诊断与 DevTools 显示），如 <c>tailwind:Styles/app.css</c>。</summary>
    public string SourceName { get; }

    /// <summary>内容版本：每次 <see cref="Update"/> 加一。渲染管线据此判断已加入级联的样式表是否过期。</summary>
    public int Version => Volatile.Read(ref _version);

    /// <summary>解析后的样式表（第一次读取时解析，之后共享同一个对象；<see cref="Update"/> 后重新解析；语法诊断见 <see cref="StyleSheet.Diagnostics"/>）。</summary>
    public StyleSheet StyleSheet
    {
        get
        {
            var s = Volatile.Read(ref _sheet);
            if (s is not null) return s;
            lock (_gate)
            {
                s = _sheet;
                if (s is null)
                {
                    var previous = _previous;
                    s = previous is not null && _pendingFrom >= 0
                        ? StyleSheet.ParseIncremental(Volatile.Read(ref _text), SourceName, previous, _pendingFrom)
                        : StyleSheet.Parse(Volatile.Read(ref _text), SourceName);
                    _previous = null;
                    _pendingFrom = -1;
                    Volatile.Write(ref _sheet, s);
                }
                return s;
            }
        }
    }

    /// <summary>
    /// 替换 CSS 文本（运行时补全、热重载）：下一次读取 <see cref="StyleSheet"/> 时重新解析，<see cref="Version"/> 加一，
    /// <see cref="AppStyleSheets.Revision"/> 也随之变化，已加入级联的渲染管线会在下一帧用新样式表替换旧的。线程安全。
    /// </summary>
    public void Update(string css)
    {
        if (css is null) throw new ArgumentNullException(nameof(css));
        lock (_gate)
        {
            Volatile.Write(ref _text, css);
            Volatile.Write(ref _sheet, null);
            _previous = null;
            _pendingFrom = -1;
            Volatile.Write(ref _version, _version + 1);
        }
        AppStyleSheets.BumpRevision();
    }

    /// <summary>
    /// 增量替换 CSS 文本（运行时补全等已知变化位置的场景）：<paramref name="changedAt"/> 是第一个可能变化的顶层规则起点，
    /// 该点之前的新旧文本逐字符相同。下一次读取 <see cref="StyleSheet"/> 时复用上一份解析结果的前缀规则与诊断，
    /// 只重新解析尾部；其余语义与 <see cref="Update(string)"/> 相同（版本 +1、修订号变化、下一帧替换级联）。
    /// 如果位置越界或前缀实际不同，自动退回整段解析。
    /// </summary>
    public void Update(string css, int changedAt)
    {
        if (css is null) throw new ArgumentNullException(nameof(css));
        if (changedAt < 0 || changedAt > css.Length) throw new ArgumentOutOfRangeException(nameof(changedAt));
        lock (_gate)
        {
            // 上一次增量尚未被读取时无法叠加两个偏移，退化为整段解析
            _previous = _pendingFrom >= 0 ? null : _sheet;
            _pendingFrom = _previous is null ? -1 : changedAt;
            Volatile.Write(ref _text, css);
            Volatile.Write(ref _sheet, null);
            Volatile.Write(ref _version, _version + 1);
        }
        AppStyleSheets.BumpRevision();
    }

    public override string ToString() => SourceName;
}
