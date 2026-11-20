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

    /// <summary>已登记的样式表（快照，按登记顺序）。</summary>
    public static IReadOnlyList<AppStyleSheet> Registered => Volatile.Read(ref s_sheets);

    /// <summary>登记数；管线据此判断是否有新的登记。</summary>
    public static int Count => Volatile.Read(ref s_sheets).Length;

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
            return sheet;
        }
    }
}

/// <summary><see cref="AppStyleSheets"/> 中的一项：CSS 文本与（按需解析一次的）样式表。</summary>
public sealed class AppStyleSheet
{
    readonly object _gate = new();
    StyleSheet? _sheet;

    internal AppStyleSheet(string text, string sourceName)
    {
        Text = text;
        SourceName = sourceName;
    }

    /// <summary>CSS 文本。</summary>
    public string Text { get; }

    /// <summary>来源名（诊断与 DevTools 显示），如 <c>tailwind:Styles/app.css</c>。</summary>
    public string SourceName { get; }

    /// <summary>解析后的样式表（第一次读取时解析，之后共享同一个对象；语法诊断见 <see cref="StyleSheet.Diagnostics"/>）。</summary>
    public StyleSheet StyleSheet
    {
        get
        {
            var s = Volatile.Read(ref _sheet);
            if (s is not null) return s;
            lock (_gate)
            {
                if (_sheet is null) Volatile.Write(ref _sheet, StyleSheet.Parse(Text, SourceName));
                return _sheet!;
            }
        }
    }

    public override string ToString() => SourceName;
}
