using AwAUI.Diagnostics.Json;

namespace AwAUI.DevTools.Tests;

/// <summary>
/// 样式表编辑页：经真实 Agent（Harness）验证列表 / 选择 / 文本加载 / Apply 往返与诊断显示，
/// 并用最小协议桩验证外部 <c>styleSheetChanged</c> 的刷新与未保存草稿的保护。
/// </summary>
public class StylesheetEditorTests
{
    [Fact]
    public void Opening_the_stylesheets_tab_lists_sheets_and_selecting_one_loads_its_text()
    {
        using var h = new Harness();
        h.Open();
        var model = h.Model;

        int before = model.RequestCount;
        model.SetTab(SideTab.Stylesheets);
        h.Settle();
        Assert.Equal(SideTab.Stylesheets, model.Tab);
        Assert.True(model.RequestCount > before, "opening the tab should request Css.getStyleSheets");
        Assert.NotNull(h.PanelById("dt-tab-stylesheets"));
        Assert.NotNull(h.PanelById("dt-sheets-count"));
        Assert.Null(model.StyleSheetsError);
        var sheets = Assert.IsType<List<StyleSheetEntry>>(model.StyleSheets);
        Assert.True(sheets.Count >= 2, "expected the app's style sheets; got " + sheets.Count);
        Assert.Contains(sheets, s => s.SourceUrl is not null);

        // 列表行的来源名 + origin / editable 标记都渲染在面板里
        var firstRow = h.PanelById("dt-sheet-0");
        Assert.NotNull(firstRow);
        Assert.NotNull(firstRow!.GetAttribute("data-sheet"));

        // 选中另一个样式表：发出 getStyleSheetText 并载入文本
        var target = sheets.Find(s => s.SheetId != model.SelectedSheetId)!;
        before = model.RequestCount;
        model.SelectStyleSheet(target.SheetId);
        h.Settle();
        Assert.Equal(target.SheetId, model.SelectedSheetId);
        Assert.True(model.RequestCount > before, "selecting a sheet should request Css.getStyleSheetText");
        Assert.False(string.IsNullOrEmpty(model.SheetText));
        Assert.False(model.SheetTextLoading);
        Assert.Null(model.SheetTextError);
        Assert.NotNull(h.PanelById("dt-sheet-editor"));
        Assert.False(model.SheetDirty);
    }

    [Fact]
    public void Applying_text_round_trips_and_changes_the_app_pixels()
    {
        using var h = new Harness();
        h.Open();
        var model = h.Model;
        model.SetTab(SideTab.Stylesheets);
        h.Settle();

        var counterSheet = model.StyleSheets!.Find(s => s.SourceUrl is { } url && url.EndsWith("Counter.css", StringComparison.Ordinal));
        if (counterSheet is null)
        {
            // app.css 与 Counter.css 都注册了：退回到第一张可编辑的样式表也不影响本测试的往返
            counterSheet = model.StyleSheets!.Find(s => s.Editable)!;
        }
        model.SelectStyleSheet(counterSheet.SheetId);
        h.Settle();
        string original = model.SheetText;
        var count = h.AppById("count");

        string draft = original + "\n#count { color: #ff0000; }";
        model.SetSheetDraft(draft);
        h.Settle();
        Assert.True(model.SheetDirty);
        Assert.NotNull(h.PanelById("dt-sheet-dirty"));

        int before = model.RequestCount;
        model.ApplyStyleSheet();
        h.Settle();

        Assert.True(model.RequestCount > before, "Apply should send Css.setStyleSheetText");
        Assert.False(model.SheetApplying);
        Assert.Null(model.SheetTextError);
        Assert.NotNull(model.SheetDiagnostics);
        Assert.DoesNotContain(model.SheetDiagnostics!, d => d.Severity == "error");
        Assert.Equal(draft, model.SheetText);
        Assert.Equal(draft, model.SheetDraft);
        Assert.False(model.SheetDirty);
        Assert.Equal(counterSheet.SheetId, model.SelectedSheetId);
        Assert.NotNull(h.PanelById("dt-sheet-no-diagnostics"));
        Assert.True(h.CountAppPixels(count.BoundingRect, Harness.IsRed) > 20, "the applied rule should turn the counter red");
    }

    [Fact]
    public void Diagnostics_are_shown_and_the_user_text_is_kept()
    {
        using var h = new Harness();
        h.Open();
        var model = h.Model;
        model.SetTab(SideTab.Stylesheets);
        h.Settle();
        var sheet = model.StyleSheets!.Find(s => s.Editable && s.SourceUrl is { } url && url.EndsWith("Counter.css", StringComparison.Ordinal))
                    ?? model.StyleSheets!.Find(s => s.Editable)!;
        model.SelectStyleSheet(sheet.SheetId);
        h.Settle();

        string draft = ".bad { bogus: 1; }";
        model.SetSheetDraft(draft);
        model.ApplyStyleSheet();
        h.Settle();

        Assert.False(model.SheetApplying);
        Assert.Null(model.SheetTextError);
        var diagnostics = Assert.IsType<List<StyleSheetDiagnostic>>(model.SheetDiagnostics);
        var warning = Assert.Single(diagnostics);
        Assert.Equal("warning", warning.Severity);
        Assert.Equal("AW2002", warning.Code);
        Assert.Equal(1, warning.Line);
        Assert.Equal(8, warning.Column);
        Assert.Contains("bogus", warning.Message);
        // 服务端接受了文本：基线更新，但编辑框里的用户文本一字未改
        Assert.Equal(draft, model.SheetDraft);
        Assert.Equal(draft, model.SheetText);
        Assert.False(model.SheetDirty);
        Assert.NotNull(h.PanelById("dt-sheet-diagnostics"));
        // 诊断行的 severity / 行列 / 消息都渲染出来
        var rendered = h.PanelById("dt-sheet-diagnostics")!;
        Assert.Contains("warning", rendered.TextContent);
        Assert.Contains("AW2002", rendered.TextContent);
        Assert.Contains("1:8", rendered.TextContent);
    }

    [Fact]
    public void External_styleSheetChanged_refreshes_the_text_but_keeps_an_unsaved_draft()
    {
        var transport = new StubTransport();
        transport.Sheets.Add(new StubSheet("s1", ".a { color: red; }") { SourceUrl = "a.css" });
        var model = FrontendTestUtil.CreateModel(transport, out var client);
        try
        {
            model.Start();
            Assert.Equal("s1", model.SelectedSheetId);
            Assert.Equal(".a { color: red; }", model.SheetText);

            // 外部修改：列表与当前文本都刷新
            transport.Find("s1")!.Text = ".a { color: blue; }";
            int before = model.RequestCount;
            transport.Emit("Css.styleSheetChanged", new JsonObject { ["sheetId"] = "s1" });
            Assert.True(model.RequestCount > before, "the event should refresh the sheet list / text");
            Assert.Equal(".a { color: blue; }", model.SheetText);
            Assert.Equal(model.SheetText, model.SheetDraft);
            Assert.False(model.SheetExternalChange);

            // 有未保存的草稿：外部修改不覆盖编辑框
            model.SetSheetDraft("my unsaved draft");
            transport.Find("s1")!.Text = ".a { color: green; }";
            transport.Emit("Css.styleSheetChanged", new JsonObject { ["sheetId"] = "s1" });
            Assert.Equal("my unsaved draft", model.SheetDraft);
            Assert.True(model.SheetExternalChange);
        }
        finally
        {
            model.Dispose();
            client.Dispose();
        }
    }

    [Fact]
    public void Applying_keeps_the_draft_even_when_the_change_event_arrives_before_the_response()
    {
        var transport = new StubTransport { EmitStyleSheetChangedOnApply = true };
        transport.Sheets.Add(new StubSheet("s1", ".a { color: red; }"));
        var model = FrontendTestUtil.CreateModel(transport, out var client);
        try
        {
            model.Start();
            model.SetSheetDraft(".a { bogus: 1; }");
            model.ApplyStyleSheet();
            Assert.False(model.SheetApplying);
            Assert.Equal(".a { bogus: 1; }", model.SheetDraft);
            Assert.Equal(".a { bogus: 1; }", model.SheetText);
            Assert.Single(Assert.IsType<List<StyleSheetDiagnostic>>(model.SheetDiagnostics));
        }
        finally
        {
            model.Dispose();
            client.Dispose();
        }
    }

    [Fact]
    public void Sheet_list_and_text_load_errors_are_reported_without_throwing()
    {
        var transport = new StubTransport { FailSheetList = true };
        transport.Sheets.Add(new StubSheet("s1", "a { color: red; }") { SourceUrl = "a.css" });
        transport.Sheets.Add(new StubSheet("s2", "b { color: blue; }"));
        var model = FrontendTestUtil.CreateModel(transport, out var client);
        try
        {
            model.Start();
            Assert.NotNull(model.StyleSheetsError);
            Assert.Null(model.StyleSheets);

            transport.FailSheetList = false;
            model.ReloadStyleSheets();
            Assert.Null(model.StyleSheetsError);
            Assert.Equal("s1", model.SelectedSheetId);
            Assert.Equal("a { color: red; }", model.SheetText);

            transport.FailSheetText = true;
            model.SelectStyleSheet("s2");
            Assert.False(model.SheetTextLoading);
            Assert.NotNull(model.SheetTextError);
            Assert.Contains("unavailable", model.SheetTextError!);
            Assert.Equal("s2", model.SelectedSheetId);
            Assert.Equal("", model.SheetText);
        }
        finally
        {
            model.Dispose();
            client.Dispose();
        }
    }

    [Fact]
    public void Read_only_sheets_are_marked_and_refuse_to_apply()
    {
        var transport = new StubTransport();
        transport.Sheets.Add(new StubSheet("s1", ".a { color: red; }"));
        transport.Sheets.Add(new StubSheet("s2", "/* default */") { Origin = "default", Editable = false });
        var model = FrontendTestUtil.CreateModel(transport, out var client);
        try
        {
            model.Start();
            model.SelectStyleSheet("s2");
            var readOnly = model.StyleSheets!.Find(s => s.SheetId == "s2")!;
            Assert.False(readOnly.Editable);
            Assert.Equal("/* default */", model.SheetText);

            model.SetSheetDraft("changed");
            int requests = model.RequestCount;
            model.ApplyStyleSheet();
            Assert.Equal(requests, model.RequestCount);
            Assert.NotNull(model.SheetTextError);
            Assert.Contains("read-only", model.SheetTextError!);
            Assert.Equal("changed", model.SheetDraft);
        }
        finally
        {
            model.Dispose();
            client.Dispose();
        }
    }
}
