# DeepSeek Harness Desktop 安装包

在 PowerShell 中运行：

```powershell
.\..\scripts\prepare-runtime.ps1
.\..\scripts\prepare-featured-skins.ps1
.\build-installer.ps1
```

需要 .NET 10 SDK 和 NSIS 3.12。如果 NSIS 不在默认位置，可传入
`-MakeNsisPath` 指定 `makensis.exe`。最终安装包输出到 `Release`
目录。若本地缺少 `runtime.seed.zip`，构建脚本会从对应 GitHub
Release 下载并校验离线运行时。
构建脚本也会下载并校验两套固定版本的精选皮肤离线包；它们在首次
运行时默认保持关闭，用户可选择启用、保留关闭或删除。

安装包使用当前用户范围，默认安装到
`%LOCALAPPDATA%\Programs\DeepSeek Harness Desktop`，不需要管理员权限。
