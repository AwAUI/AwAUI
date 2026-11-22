using System.Collections.Generic;
using AwAUI.Css;
using AwAUI.Css.Cascade;

namespace AwAUI.UI;

/// <summary>应用级（程序集登记的）样式表的接入，见 <see cref="AppStyleSheets"/>。</summary>
public sealed partial class RenderPipeline
{
    /// <summary>一张已处理过的登记项：级联句柄与已加入的版本。</summary>
    sealed class AppliedAppSheet
    {
        public AppliedAppSheet(AppStyleSheet sheet) => Sheet = sheet;

        public AppStyleSheet Sheet { get; }

        /// <summary>已加入级联的样式表；尚未加入时为 null。</summary>
        public CascadeStyleSheet? Cascade;

        /// <summary>加入时的 <see cref="AppStyleSheet.Version"/>。</summary>
        public int Version = -1;
    }

    readonly List<AppliedAppSheet> _appSheets = new();
    long _appSheetsRevision = -1;

    /// <summary>
    /// 是否在帧开始时把 <see cref="AppStyleSheets"/> 中的登记项（构建期生成的 Tailwind 样式表等）加入级联，默认 true。
    /// 在第一帧之前设为 false 才能完全不加入；已加入的不会移除。
    /// </summary>
    public bool IncludeAppStyleSheets { get; set; } = true;

    /// <summary>
    /// 把尚未加入的应用样式表按登记顺序加入（Author 来源），并替换内容变化过的样式表（<see cref="AppStyleSheet.Update"/>）。
    /// 每帧开始与加入组件样式表之前调用：<see cref="AppStyleSheets.Revision"/> 与登记数都没有变化时只做两次比较。
    /// 变化过的表先 <see cref="RemoveStyleSheet"/> 旧句柄、再加入新解析的样式表，因此级联里不会留下重复的旧规则。
    /// 解析与级联的诊断（不支持的属性、选择器 ...）报告到 <see cref="UIRoot.Diagnostic"/>，与组件样式表相同：
    /// 构建期生成的 CSS 里 AwAUI 不支持的部分不会悄悄失效。
    /// </summary>
    void AddPendingAppStyleSheets(UIRoot? root)
    {
        if (!IncludeAppStyleSheets) return;
        var list = AppStyleSheets.Registered;
        long revision = AppStyleSheets.Revision;
        if (revision == _appSheetsRevision && list.Count == _appSheets.Count) return;
        for (int i = 0; i < list.Count; i++)
        {
            var app = list[i];
            AppliedAppSheet state;
            if (i < _appSheets.Count && ReferenceEquals(_appSheets[i].Sheet, app))
            {
                state = _appSheets[i];
                int version = app.Version;
                if (state.Version == version) continue;
                // 先移除旧句柄再加入新解析的表：内容变化时级联中只保留一张表
                if (state.Cascade is not null) RemoveStyleSheet(state.Cascade);
            }
            else
            {
                state = new AppliedAppSheet(app);
                _appSheets.Add(state);
            }
            var parsed = app.StyleSheet;
            var added = AddStyleSheet(parsed);
            state.Cascade = added;
            state.Version = app.Version;
            if (root is null) continue;
            foreach (var d in parsed.Diagnostics) ReportAppSheetDiagnostic(root, app, d);
            foreach (var d in added.Diagnostics) ReportAppSheetDiagnostic(root, app, d);
        }
        _appSheetsRevision = revision;
    }

    static void ReportAppSheetDiagnostic(UIRoot root, AppStyleSheet sheet, AwAUI.Css.CssDiagnostic d) =>
        root.ReportDiagnostic(new UIDiagnostic(d.Code,
            d.Severity == AwAUI.Css.CssDiagnosticSeverity.Error ? DiagnosticSeverity.Error : DiagnosticSeverity.Warning,
            "应用样式表（" + sheet.SourceName + "）：" + d.Message, null));
}
