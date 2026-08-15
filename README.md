<div align="center">
  <img src="DeepSeekHarnessDesktop/Assets/WhaleGirl-v2.png" width="104" alt="DeepSeek Harness Desktop mascot">
  <h1>DeepSeek Harness Desktop</h1>
  <p>DeepSeek Harness 的 Windows 桌面壳与本地管理中心</p>
</div>

[English](README.en.md) · [下载预发布版](https://github.com/jinganwushideng/deepseek-harness-desktop/releases/tag/v1.1.1)

> [!IMPORTANT]
> 本项目是非官方社区项目，与 DeepSeek 没有隶属或背书关系。DeepSeek、DeepSeek Harness 及相关名称归其各自权利人所有。

![应用预览](docs/images/preview.png)

## 功能

- 启动时自动运行本机 DeepSeek Harness，并在 WebView2 中直接打开 Web UI。
- 自包含 .NET、Node.js 24、pnpm 与 Harness 离线运行时，首次启动无需下载运行时。
- 深浅色桌面壳、系统托盘、回复完成 Windows 通知及服务器崩溃恢复。
- 服务器、更新、插件、Skill、日志、诊断、数据与加密备份管理。
- 自动检查桌面壳 GitHub Release 与 Harness npm 版本；仅提示不静默安装，可在设置中关闭。关闭更新提示后会记住该版本，未来更高版本仍会再次提醒。
- 官方插件与用户插件分离；用户 CLI/Skill 包存放在独立的 `launcher-packages` 目录。
- 独立皮肤中心完整显示已验证的社区皮肤并支持搜索，不与普通插件混排；可选择让桌面壳跟随皮肤的明暗和语义颜色，并自动修复低对比度、纯白强调色等不可读组合。
- 安装包离线内置“鲸鱼娘昼夜工坊”和“DeepSeek Harness Themes”两套精选皮肤；首次运行可分别选择启用、保留但关闭或删除，默认均不启用且最多只启用一套。
- 自动插件仓库每日分页扫描 npm/GitHub、依赖与代码特征并校验 Harness 清单；主动寻找中文 README，中文介绍优先，支持一键下载安装。
- 仓库卡片优先使用项目 README 预览图；无图时使用内置鲸鱼娘占位图。图片按首屏、预取批次和按需加载分级缓存，简介列表使用回收虚拟化。
- 官方下载地址遵循系统代理；网络故障时自动切换国内可用镜像，镜像强制直连。
- 服务仅监听 `127.0.0.1`，不会主动开放到局域网。

## 安装

1. 从 [Releases](https://github.com/jinganwushideng/deepseek-harness-desktop/releases) 下载 `DeepSeek-Harness-Desktop-Setup-1.1.1.exe`。
2. 运行安装程序。它按当前用户安装到 `%LOCALAPPDATA%\Programs\DeepSeek Harness Desktop`，不需要管理员权限。
3. 首次启动选择工作目录、DSH_HOME 和端口，然后按需设置模型 API Key。

安装包尚未代码签名，Windows SmartScreen 可能提示“未知发布者”。请从本仓库 Release 下载并对照 `SHA256SUMS.txt` 校验。

### 系统要求

- Windows 10 2004 或更新版本 / Windows 11，x64。
- Microsoft Edge WebView2 Runtime。Windows 11 和正常更新的 Windows 10 通常已包含。
- 使用模型、在线更新或安装网络插件时需要网络连接。

不需要另行安装 .NET、Node.js、npm 或 pnpm。

## 数据与隐私

- Harness 数据默认位于 `%USERPROFILE%\.dsh`，也可在首次设置时选择其他 DSH_HOME。
- API Key 不写入 `launcher.json`，界面不会回显密钥明文，日志会进行敏感值脱敏。
- 卸载默认保留 DSH_HOME、`launcher.json` 和备份，避免删除会话、凭据或用户插件。
- WebView2 浏览器数据与 Harness 数据分开存放。

## 从源码构建

需要 .NET 10 SDK、PowerShell 和 NSIS 3.12。运行时种子不进入 Git 历史，会从对应 Release 下载并校验。

```powershell
git clone https://github.com/jinganwushideng/deepseek-harness-desktop.git
cd deepseek-harness-desktop

.\scripts\prepare-runtime.ps1
.\scripts\prepare-featured-skins.ps1
dotnet test .\DeepSeekHarnessDesktop.Tests\DeepSeekHarnessDesktop.Tests.csproj -c Release
.\Installer\build-installer.ps1
```

仅编译或运行测试时不需要 `runtime.seed.zip` 或精选皮肤离线包；生成可离线首次启动的应用和安装包时，构建脚本会准备并校验这些文件。内置皮肤仍遵循各自上游许可证，详见第三方许可说明。

插件目录位于 `catalog/plugin-index.json`，由 `.github/workflows/plugin-catalog.yml` 每日调用 `scripts/update-plugin-catalog.mjs` 自动更新。目录验证只确认包具有 Harness 插件清单，不代表安全审计；安装前仍应核对来源和脚本提示。

## 参与贡献

提交 Issue 或 Pull Request 前请阅读 [CONTRIBUTING.md](CONTRIBUTING.md)。安全问题请按 [SECURITY.md](SECURITY.md) 私下报告。

## 许可证

桌面壳源码和已声明的项目资产采用 [MIT License](LICENSE)。内嵌运行时及依赖保留各自许可，详见 [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md) 和 [ASSETS.md](ASSETS.md)。
