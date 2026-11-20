namespace AwAUI.UI;

/// <summary>应用级（程序集登记的）样式表的接入，见 <see cref="AppStyleSheets"/>。</summary>
public sealed partial class RenderPipeline
{
    int _appSheetsAdded;

    /// <summary>
    /// 是否在帧开始时把 <see cref="AppStyleSheets"/> 中的登记项（构建期生成的 Tailwind 样式表等）加入级联，默认 true。
    /// 在第一帧之前设为 false 才能完全不加入；已加入的不会移除。
    /// </summary>
    public bool IncludeAppStyleSheets { get; set; } = true;

    /// <summary>
    /// 把尚未加入的应用样式表按登记顺序加入（Author 来源）。每帧开始与加入组件样式表之前调用，没有新登记时只做一次整数比较。
    /// 解析与级联的诊断（不支持的属性、选择器 ...）报告到 <see cref="UIRoot.Diagnostic"/>，与组件样式表相同：构建期生成的 CSS 里 AwAUI 不支持的部分不会悄悄失效。
    /// </summary>
    void AddPendingAppStyleSheets(UIRoot? root)
    {
        if (!IncludeAppStyleSheets) return;
        var list = AppStyleSheets.Registered;
        while (_appSheetsAdded < list.Count)
        {
            var app = list[_appSheetsAdded++];
            var parsed = app.StyleSheet;
            var added = AddStyleSheet(parsed);
            if (root is null) continue;
            foreach (var d in parsed.Diagnostics) ReportAppSheetDiagnostic(root, app, d);
            foreach (var d in added.Diagnostics) ReportAppSheetDiagnostic(root, app, d);
        }
    }

    static void ReportAppSheetDiagnostic(UIRoot root, AppStyleSheet sheet, AwAUI.Css.CssDiagnostic d) =>
        root.ReportDiagnostic(new UIDiagnostic(d.Code,
            d.Severity == AwAUI.Css.CssDiagnosticSeverity.Error ? DiagnosticSeverity.Error : DiagnosticSeverity.Warning,
            "应用样式表（" + sheet.SourceName + "）：" + d.Message, null));
}
