namespace AwAUI.DevTools;

/// <summary>
/// 面板自己的样式表（只加到面板窗口的宿主上，与被检查应用的样式完全隔离，design/06「与被调试 UI 的隔离」）。
/// 配色参照浏览器 DevTools 的浅色主题；行高与 <see cref="InspectorModel.RowHeight"/> 一致。
/// </summary>
internal static class PanelStyles
{
    public const string Css = """
        root {
          font-family: sans-serif;
          font-size: 12px;
          color: #1f2328;
          background-color: #ffffff;
        }
        .dt { display: flex; flex-direction: column; height: 100%; }

        /* 工具栏 */
        .dt-toolbar {
          display: flex; align-items: center; gap: 2px;
          height: 32px; padding: 0 6px; flex-shrink: 0;
          background-color: #f6f8fa; border-bottom: 1px solid #d0d7de;
        }
        .dt-tool {
          height: 24px; padding: 0 9px; border-radius: 5px; font-size: 12px;
          border: 1px solid transparent; background-color: transparent; color: #424a53;
        }
        .dt-tool:hover { background-color: #eaeef2; }
        .dt-tool.on { background-color: #ddf4ff; border-color: #80ccff; color: #0969da; }
        .dt-pick-icon {
          width: 12px; height: 12px; margin-right: 6px; border: 1.5px solid currentColor; border-radius: 2px;
        }
        .dt-sep { width: 1px; height: 18px; margin: 0 6px; background-color: #d0d7de; }
        .dt-title { font-weight: 600; color: #1f2328; padding: 0 4px; }
        .dt-spacer { flex: 1; }
        .dt-conn { display: flex; align-items: center; color: #57606a; padding: 0 6px; }
        .dt-dot { width: 8px; height: 8px; border-radius: 4px; margin-right: 6px; background-color: #8c959f; }
        .dt-dot.ok { background-color: #2da44e; }

        .dt-main { flex: 1; display: flex; min-height: 0; }

        /* 元素树（虚拟化：只为可见行建元素） */
        .dt-left { flex: 11; min-width: 0; display: flex; flex-direction: column; position: relative; border-right: 1px solid #d0d7de; }
        .dt-tree {
          flex: 1; min-height: 0; position: relative; overflow: hidden;
          background-color: #ffffff;
        }

        /* 搜索框与结果列表 */
        .dt-search {
          position: relative; display: flex; align-items: center; gap: 4px; height: 28px; padding: 0 6px; flex-shrink: 0;
          background-color: #f6f8fa; border-bottom: 1px solid #d0d7de;
        }
        .dt-search-input {
          flex: 1; min-width: 0; height: 20px; padding: 0 5px; border-radius: 4px; font-size: 12px;
          border: 1px solid #d0d7de; background-color: #ffffff; color: #1f2328;
        }
        .dt-search-clear {
          width: 20px; height: 20px; padding: 0; font-size: 14px; line-height: 18px; border-radius: 4px;
          border: 1px solid transparent; background-color: transparent; color: #57606a;
        }
        .dt-search-clear:hover { background-color: #eaeef2; }
        .dt-search-results {
          position: absolute; left: 6px; right: 6px; top: 28px; max-height: 280px; overflow: hidden; z-index: 20;
          border: 1px solid #d0d7de; border-radius: 4px; background-color: #ffffff;
        }
        .dt-search-result {
          display: flex; align-items: center; gap: 6px; width: 100%; height: 20px; padding: 0 6px; text-align: left;
          border: none; border-radius: 0; background-color: transparent; font-size: 12px;
        }
        .dt-search-result:hover { background-color: #ddf4ff; }
        .dt-search-match { width: 58px; flex-shrink: 0; color: #8250df; font-family: monospace; font-size: 11px; }
        .dt-search-label { flex: 1; min-width: 0; overflow: hidden; white-space: nowrap; color: #1f2328; font-family: monospace; }
        .dt-search-note { padding: 6px 8px; color: #8c959f; }
        .dt-rows { position: absolute; left: 0; right: 10px; }
        .dt-row {
          display: flex; align-items: center; height: 20px; padding-right: 8px;
          white-space: nowrap; overflow: hidden; cursor: default;
        }
        .dt-row:hover { background-color: #f0f6ff; }
        .dt-row.selected { background-color: #cfe5fc; }
        .dt-twisty { width: 14px; height: 20px; display: flex; align-items: center; justify-content: center; flex-shrink: 0; }
        .dt-arrow-right {
          width: 0; height: 0;
          border-left: 5px solid #6e7781; border-top: 4px solid transparent; border-bottom: 4px solid transparent;
        }
        .dt-arrow-down {
          width: 0; height: 0;
          border-top: 5px solid #6e7781; border-left: 4px solid transparent; border-right: 4px solid transparent;
        }
        .dt-label { font-family: monospace; font-size: 12px; white-space: pre; color: #1f2328; }
        .t-tag { color: #881280; }
        .t-attr { color: #994500; }
        .t-val { color: #1a1aa6; }
        .t-text { color: #1f2328; }
        .t-dim { color: #8c959f; }
        .t-comp { color: #8250df; }
        .dt-empty { padding: 12px; color: #8c959f; }

        .dt-scrollbar { position: absolute; top: 0; right: 0; bottom: 0; width: 10px; background-color: #f6f8fa; }
        .dt-thumb { position: absolute; left: 2px; right: 2px; border-radius: 3px; background-color: #c4cbd3; }
        .dt-thumb:hover { background-color: #a8b1bb; }

        /* 右侧：页签 + 内容 + 盒模型 */
        .dt-side { flex: 9; min-width: 0; display: flex; flex-direction: column; background-color: #ffffff; }
        .dt-tabs {
          display: flex; align-items: flex-end; height: 28px; padding: 0 6px; flex-shrink: 0;
          border-bottom: 1px solid #d0d7de; background-color: #f6f8fa;
        }
        .dt-tab {
          height: 27px; padding: 0 7px; display: flex; align-items: center; color: #57606a;
          border-bottom: 2px solid transparent; cursor: default; font-size: 11px; white-space: nowrap;
        }
        .dt-tab:hover { color: #1f2328; background-color: #eaeef2; }
        .dt-tab.on { color: #1f2328; border-bottom-color: #0969da; }
        .dt-tab-spacer { flex: 1; }
        .dt-mini {
          height: 20px; padding: 0 8px; margin-bottom: 3px; font-size: 11px; border-radius: 4px;
          border: 1px solid #d0d7de; background-color: #ffffff; color: #424a53;
        }
        .dt-scroll { flex: 1; min-height: 0; position: relative; overflow: hidden; }
        .dt-scroll-content { position: absolute; left: 0; right: 0; }
        .dt-note { padding: 10px 12px; color: #8c959f; }
        .dt-error { margin: 6px 8px; padding: 6px 8px; border-radius: 4px; color: #cf222e; background-color: #ffebe9; border: 1px solid #ffcecb; }

        /* 样式规则 */
        .dt-section-title {
          padding: 5px 10px; font-size: 11px; color: #57606a; background-color: #f6f8fa;
          border-bottom: 1px solid #eaeef2;
        }
        .dt-rule { padding: 5px 0 6px 0; border-bottom: 1px solid #eaeef2; font-family: monospace; }
        .dt-rule-head { display: flex; align-items: center; padding: 0 10px 0 10px; height: 18px; }
        .dt-selector { color: #1f2328; white-space: pre; }
        .dt-selector-dim { color: #8c959f; white-space: pre; }
        .dt-source { margin-left: auto; padding-left: 8px; color: #57606a; font-size: 11px; white-space: nowrap; font-family: sans-serif; }
        .dt-brace { padding: 0 10px; height: 18px; color: #1f2328; white-space: pre; }
        .dt-decl { position: relative; display: flex; align-items: center; height: 18px; padding-left: 12px; white-space: pre; }
        .dt-decl:hover { background-color: #f6f8fa; }
        .dt-check {
          width: 10px; height: 10px; margin-right: 6px; flex-shrink: 0; border-radius: 2px;
          border: 1px solid #afb8c1; background-color: #ffffff;
        }
        .dt-check.on { background-color: #0969da; border-color: #0969da; }
        .dt-prop { color: #c80000; white-space: pre; }
        .dt-colon { color: #1f2328; white-space: pre; }
        .dt-value { color: #1f2328; white-space: pre; padding: 0 1px; border-radius: 2px; }
        .dt-value:hover { background-color: #ddf4ff; }
        .dt-swatch { width: 10px; height: 10px; margin-right: 4px; border: 1px solid #8c959f; flex-shrink: 0; }
        .dt-decl.off .dt-prop, .dt-decl.off .dt-value, .dt-decl.off .dt-colon { color: #a5adb6; }
        .dt-decl.bad .dt-prop, .dt-decl.bad .dt-value { color: #a5adb6; }
        .dt-warn { margin-left: 6px; color: #bf8700; font-family: sans-serif; font-size: 11px; white-space: nowrap; }
        .dt-strike { position: absolute; top: 9px; height: 1px; background-color: #8c959f; }
        .dt-edit {
          min-width: 60px; height: 18px; padding: 0 3px; border-radius: 2px; font-family: monospace; font-size: 12px;
          border: 1px solid #0969da; background-color: #ffffff;
        }
        .dt-add { height: 18px; padding-left: 30px; color: #8c959f; font-family: sans-serif; font-size: 11px; display: flex; align-items: center; }
        .dt-add:hover { color: #0969da; }

        /* 强制伪状态 */
        .dt-pseudo-bar {
          display: flex; flex-direction: column; padding: 5px 10px; border-bottom: 1px solid #eaeef2; background-color: #fafbfc;
        }
        .dt-pseudo-head { display: flex; align-items: center; margin-bottom: 4px; }
        .dt-pseudo-title { font-size: 11px; color: #57606a; }
        .dt-pseudo-clear { font-size: 11px; color: #0969da; }
        .dt-pseudo-clear:hover { text-decoration: underline; }
        .dt-pseudo-list { display: flex; flex-wrap: wrap; gap: 4px; }
        .dt-pseudo {
          height: 18px; padding: 0 6px; border-radius: 3px; font-family: monospace; font-size: 11px;
          border: 1px solid #d0d7de; background-color: #ffffff; color: #424a53;
        }
        .dt-pseudo:hover { background-color: #eaeef2; }
        .dt-pseudo.on { background-color: #ddf4ff; border-color: #80ccff; color: #0969da; }

        /* 属性编辑 */
        .dt-attr-row { display: flex; align-items: center; height: 20px; padding: 0 10px; white-space: nowrap; font-family: monospace; }
        .dt-attr-row:hover { background-color: #f6f8fa; }
        .dt-attr-name { color: #994500; }
        .dt-attr-eq { padding: 0 2px; color: #1f2328; }
        .dt-attr-value { padding: 0 2px; border-radius: 2px; color: #1a1aa6; }
        .dt-attr-value:hover { background-color: #ddf4ff; }
        .dt-attr-remove {
          width: 16px; height: 16px; margin-left: 6px; padding: 0; font-size: 12px; line-height: 14px;
          border: none; border-radius: 3px; background-color: transparent; color: #8c959f;
        }
        .dt-attr-remove:hover { color: #cf222e; background-color: #ffebe9; }
        .dt-attr-edit {
          min-width: 80px; height: 18px; padding: 0 3px; border-radius: 2px; font-family: monospace; font-size: 12px;
          border: 1px solid #0969da; background-color: #ffffff;
        }
        .dt-attr-add { display: flex; align-items: center; height: 18px; padding-left: 10px; font-size: 11px; color: #8c959f; }
        .dt-attr-add:hover { color: #0969da; }

        /* 计算值 */
        .dt-computed-row { display: flex; height: 19px; align-items: center; padding: 0 10px; font-family: monospace; white-space: pre; }
        .dt-computed-row:hover { background-color: #f6f8fa; }
        .dt-computed-name { color: #c80000; width: 46%; flex-shrink: 0; overflow: hidden; }
        .dt-computed-value { color: #1f2328; flex: 1; overflow: hidden; }
        .dt-computed-origin { color: #8c959f; font-family: sans-serif; font-size: 11px; padding-left: 6px; }

        /* 事件日志 */
        .dt-log-row { display: flex; height: 18px; align-items: center; padding: 0 8px; white-space: pre; overflow: hidden; font-family: monospace; font-size: 11px; border-bottom: 1px solid #f6f8fa; }
        .dt-log-method { color: #0969da; width: 190px; flex-shrink: 0; overflow: hidden; }
        .dt-log-detail { color: #57606a; overflow: hidden; }

        /* 盒模型图 */
        .dt-boxpane { flex-shrink: 0; height: 196px; border-top: 1px solid #d0d7de; display: flex; align-items: center; justify-content: center; background-color: #ffffff; }
        .dt-box { position: relative; display: flex; flex-direction: column; align-items: stretch; font-size: 11px; color: #1f2328; }
        .dt-box-margin { background-color: #f9cc9d; border: 1px dashed #333333; padding: 0 10px; }
        .dt-box-border { background-color: #fbdda0; border: 1px solid #000000; padding: 0 10px; }
        .dt-box-padding { background-color: #c3d08b; border: 1px dashed #333333; padding: 0 10px; }
        .dt-box-content { background-color: #8cb6c0; border: 1px solid #808080; height: 24px; min-width: 96px; display: flex; align-items: center; justify-content: center; padding: 0 8px; }
        .dt-box-row { display: flex; align-items: center; justify-content: center; height: 18px; }
        .dt-box-mid { display: flex; align-items: center; }
        .dt-box-side { width: 22px; text-align: center; flex-shrink: 0; }
        .dt-box-label { position: absolute; left: 4px; top: 2px; font-size: 10px; color: #333333; }
        .dt-box-wrap { position: relative; }

        /* 样式表编辑 */
        .dt-sheet-list { border-bottom: 1px solid #eaeef2; }
        .dt-sheet-row {
          display: flex; align-items: center; gap: 6px; width: 100%; height: 20px; padding: 0 10px; text-align: left;
          border: none; border-radius: 0; background-color: transparent; font-size: 12px;
        }
        .dt-sheet-row:hover { background-color: #f6f8fa; }
        .dt-sheet-row.on { background-color: #ddf4ff; }
        .dt-sheet-name { flex: 1; min-width: 0; overflow: hidden; white-space: nowrap; color: #1f2328; font-family: monospace; }
        .dt-sheet-origin { color: #8250df; font-size: 10px; white-space: nowrap; }
        .dt-sheet-badge {
          padding: 0 4px; border-radius: 3px; color: #57606a; background-color: #eaeef2;
          font-size: 10px; white-space: nowrap;
        }
        .dt-sheet-editor-pane { display: flex; flex-direction: column; gap: 6px; padding: 6px 8px; }
        .dt-sheet-editor-title { color: #57606a; font-size: 11px; white-space: nowrap; overflow: hidden; }
        .dt-sheet-editor {
          align-self: stretch; width: auto; height: 240px; padding: 4px 6px; border-radius: 4px; resize: none;
          border: 1px solid #d0d7de; background-color: #ffffff; color: #1f2328;
          font-family: monospace; font-size: 12px; line-height: 16px;
        }
        .dt-sheet-editor:focus { border-color: #0969da; }
        .dt-sheet-actions { display: flex; align-items: center; gap: 8px; }
        .dt-sheet-apply {
          height: 22px; padding: 0 10px; border-radius: 4px; font-size: 12px;
          border: 1px solid #1f883d; background-color: #1f883d; color: #ffffff;
        }
        .dt-sheet-apply:hover { background-color: #1a7f37; }
        .dt-sheet-apply:disabled { border-color: #d0d7de; background-color: #eaeef2; color: #8c959f; }
        .dt-sheet-dirty { color: #bf8700; font-size: 11px; }
        .dt-sheet-note { color: #8c959f; font-size: 11px; }
        .dt-sheet-diagnostics { display: flex; flex-direction: column; gap: 2px; }
        .dt-sheet-diag { display: flex; align-items: flex-start; gap: 6px; padding: 3px 6px; border-radius: 3px; font-size: 11px; white-space: pre-wrap; }
        .dt-sheet-diag.error { color: #cf222e; background-color: #ffebe9; }
        .dt-sheet-diag.warning { color: #9a6700; background-color: #fff8c5; }
        .dt-sheet-diag.info { color: #0969da; background-color: #ddf4ff; }
        .dt-sheet-diag-severity { font-weight: 600; text-transform: uppercase; font-size: 10px; flex-shrink: 0; }
        .dt-sheet-diag-code { font-family: monospace; flex-shrink: 0; }
        .dt-sheet-diag-pos { font-family: monospace; flex-shrink: 0; color: #57606a; }
        .dt-sheet-diag-message { flex: 1; min-width: 0; }

        /* 组件实例 */
        .dt-component-list { padding: 2px 0; }
        .dt-component-row {
          display: flex; align-items: center; width: 100%; height: 20px; padding-right: 8px; text-align: left;
          border: none; border-radius: 0; background-color: transparent; white-space: nowrap; overflow: hidden;
        }
        .dt-component-row:hover { background-color: #f0f6ff; }
        .dt-component-row.on { background-color: #cfe5fc; }
        .dt-component-name { color: #8250df; font-family: monospace; font-size: 12px; }
        .dt-component-node { color: #57606a; font-family: monospace; font-size: 11px; overflow: hidden; }
        .dt-components-detail { padding: 4px 10px; color: #1f2328; font-family: monospace; font-size: 11px; white-space: nowrap; overflow: hidden; }

        /* 状态栏 */
        .dt-status {
          display: flex; align-items: center; gap: 12px; height: 22px; padding: 0 8px; flex-shrink: 0;
          border-top: 1px solid #d0d7de; background-color: #f6f8fa; color: #57606a; font-size: 11px; white-space: nowrap; overflow: hidden;
        }
        .dt-crumb { color: #1f2328; font-family: monospace; }
        """;
}
