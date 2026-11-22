# 发布 NuGet

仓库使用 **NuGet.org Trusted Publishing**（GitHub OIDC）发布，不再保存长期 API key。
工作流：[`publish.yml`](workflows/publish.yml)。

## 一次性配置

### 1. 在 nuget.org 添加 Trusted Publishing 策略

登录 nuget.org → 右上角用户名 → **Trusted Publishing** → **Add a new policy**，填写：

| 字段 | 值 |
|---|---|
| Policy Owner | 拥有这些包的账号或组织（必须与包的 owner 一致） |
| Repository Owner | `AwAUI` |
| Repository | `AwAUI` |
| Workflow File | `publish.yml`（只写文件名，不带 `.github/workflows/`） |
| Environment | 留空 |

如果策略里可以配置 scope / 包匹配模式，请设为 `*` 或至少包含 `AwAUI.*`，否则首次发布新包会被拒绝。
工作流不使用 GitHub Environment，因此策略里的 Environment 必须留空。

### 2. 设置仓库变量

```sh
gh variable set NUGET_USER --repo AwAUI/AwAUI --body "<你的 nuget.org 用户名>"
```

`NUGET_USER` 是 nuget.org 的**账号名（profile name）**，不是邮箱。
工作流运行时 `NuGet/login@v1` 会把 GitHub OIDC token 换成 1 小时有效的临时 API key；
仓库里不需要、也不应该添加 `NUGET_API_KEY` secret。

## 发布

推荐通过 GitHub Release：

1. 打 tag，例如 `v0.1.0` 或 `v0.2.0-preview.1`；
2. 以该 tag 创建并发布 Release；
3. `Publish NuGet` 工作流会打包并推送到 nuget.org。

也可以手动触发：

- Actions → **Publish NuGet** → Run workflow，填写 `version`；
- 勾选 `dry_run` 只打包并上传 artifact，不推送。

版本号会覆盖 `Directory.Build.props` 里的默认 `Version`，所以 tag/输入必须是
`MAJOR.MINOR.PATCH`（可带 `-preview.1` 之类的预发布后缀）。

## 包清单

`dotnet pack AwAUI.slnx` 会打包所有 `IsPackable != false` 的 `src/` 项目，包括：

- 运行时：`AwAUI.Foundation`、`AwAUI.Graphics`、`AwAUI.Imaging`、`AwAUI.Svg`、
  `AwAUI.Platform`、`AwAUI.Css`、`AwAUI.Layout`、`AwAUI.Text`、`AwAUI.Unicode`、
  `AwAUI.Diagnostics`、`AwAUI.UI`、`AwAUI.Render`、`AwAUI.Tailwind`
- 桌面与图形后端：`AwAUI.MacOS`、`AwAUI.Win32`、`AwAUI.X11`、`AwAUI.Headless`、
  `AwAUI.Rhi.Metal`
- 浏览器：`AwAUI.Browser`（`net10.0-browser`）
- 组件与工具：`AwAUI.Controls`、`AwAUI.Controls.DataGrid`、`AwAUI.Mvvm`、
  `AwAUI.Themes.Fluent`、`AwAUI.Shadcn`、`AwAUI.Icons.Lucide`、`AwAUI.Markup`
- 开发工具：`AwAUI.DevTools`

暂不发布：

- `AwAUI.Desktop`：程序集在构建期只链接打包机所在平台的后端，单包无法跨平台，等元包拆分或改为运行时选择后再发布；
- `AwAUI.Markup.Compiler`：源码以链接方式编入 `AwAUI.Markup` 分析器包；
- `src/tools/*`、`tests/*`、`samples/*`：不对外发布。

## 包元数据

公共元数据在 [`build/Packaging.props`](../build/Packaging.props)：

- 图标：仓库根 `favicon.png` 的 128×128 版本 `build/package-icon.png`；
- README：项目自带 `README.md` 时打包它，否则打包仓库根 `README.md`；
- 符号包：`.snupkg` 与 `.nupkg` 一起推送；
- 仓库地址、项目地址、标签、输出目录 `artifacts/packages`。

本地验证：

```sh
dotnet pack src/AwAUI.Foundation/AwAUI.Foundation.csproj -c Release
unzip -l artifacts/packages/AwAUI.Foundation.0.1.0.nupkg
```

## 待办

当前除 `AwAUI.Icons.Lucide` 外，包没有声明项目许可证（`PackageLicenseExpression` / `PackageLicenseFile`）。
正式发布前应确定仓库许可证并补上，否则 nuget.org 会显示 “License: not specified”。
