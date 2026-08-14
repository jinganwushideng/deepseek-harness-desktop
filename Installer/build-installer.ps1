param(
    [string]$Version = "1.0.0",
    [string]$MakeNsisPath = ""
)

$ErrorActionPreference = "Stop"
$projectRoot = Split-Path -Parent $PSScriptRoot
$appProject = Join-Path $projectRoot "DeepSeekHarnessDesktop\DeepSeekHarnessDesktop.csproj"
$dotnet = Join-Path $projectRoot ".dotnet\dotnet.exe"
$publishDir = Join-Path $projectRoot "artifacts\publish\win-x64"
$outputDir = Join-Path $projectRoot "Release"
$script = Join-Path $PSScriptRoot "DeepSeekHarnessDesktop.nsi"
$runtimeSeed = Join-Path $projectRoot "DeepSeekHarnessDesktop\Assets\runtime.seed.zip"
$prepareRuntime = Join-Path $projectRoot "scripts\prepare-runtime.ps1"

if (-not (Test-Path -LiteralPath $dotnet)) { $dotnet = (Get-Command dotnet -ErrorAction Stop).Source }
if ([string]::IsNullOrWhiteSpace($MakeNsisPath)) {
    $candidates = @(
        (Join-Path $projectRoot "_tools\nsis\Bin\makensis.exe"),
        (Join-Path $projectRoot "_tools\nsis\makensis.exe"),
        "$env:ProgramFiles\NSIS\makensis.exe",
        "${env:ProgramFiles(x86)}\NSIS\makensis.exe"
    )
    $MakeNsisPath = $candidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
}
if ([string]::IsNullOrWhiteSpace($MakeNsisPath) -or -not (Test-Path -LiteralPath $MakeNsisPath)) {
    throw "未找到 NSIS makensis.exe。请安装 NSIS 3.12，或通过 -MakeNsisPath 指定路径。"
}

if (-not (Test-Path -LiteralPath $runtimeSeed)) {
    if (-not (Test-Path -LiteralPath $prepareRuntime)) {
        throw "缺少 runtime.seed.zip，也未找到 scripts\prepare-runtime.ps1。"
    }
    & $prepareRuntime
}
if (-not (Test-Path -LiteralPath $runtimeSeed)) {
    throw "离线运行时准备完成后仍未找到 runtime.seed.zip。"
}

New-Item -ItemType Directory -Path $publishDir -Force | Out-Null
New-Item -ItemType Directory -Path $outputDir -Force | Out-Null

& $dotnet publish $appProject -c Release -r win-x64 --self-contained true -o $publishDir
if ($LASTEXITCODE -ne 0) { throw "桌面壳发布失败，退出代码 $LASTEXITCODE。" }

$appExe = Join-Path $publishDir "DeepSeekHarnessDesktop.exe"
if (-not (Test-Path -LiteralPath $appExe)) { throw "发布结果中缺少 DeepSeekHarnessDesktop.exe。" }

& $MakeNsisPath "/INPUTCHARSET" "UTF8" "/DAPP_VERSION=$Version" "/DPUBLISH_DIR=$publishDir" "/DOUTPUT_DIR=$outputDir" $script
if ($LASTEXITCODE -ne 0) { throw "NSIS 构建失败，退出代码 $LASTEXITCODE。" }

$installer = Join-Path $outputDir "DeepSeek-Harness-Desktop-Setup-$Version.exe"
if (-not (Test-Path -LiteralPath $installer)) { throw "未生成预期的安装包。" }

$item = Get-Item -LiteralPath $installer
$hash = Get-FileHash -LiteralPath $installer -Algorithm SHA256
[pscustomobject]@{
    Installer = $item.FullName
    SizeMiB = [math]::Round($item.Length / 1MB, 2)
    SHA256 = $hash.Hash
}
