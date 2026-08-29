$ErrorActionPreference = "Stop"
$projectRoot = Split-Path -Parent $PSScriptRoot
[xml]$props = Get-Content -LiteralPath (Join-Path $projectRoot "Directory.Build.props") -Raw
$version = [string]$props.Project.PropertyGroup.DesktopVersion
if ($version -notmatch '^\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?$') { throw "Directory.Build.props 中的 DesktopVersion 无效：$version" }

$checks = @(
    @{ Path = "Installer\DeepSeekHarnessDesktop.nsi"; Pattern = "!define APP_VERSION `"$([regex]::Escape($version))`"" },
    @{ Path = "README.md"; Pattern = "DeepSeek-Harness-Desktop-Setup-$([regex]::Escape($version))\.exe" },
    @{ Path = "README.en.md"; Pattern = "DeepSeek-Harness-Desktop-Setup-$([regex]::Escape($version))\.exe" }
)
foreach ($check in $checks) {
    $path = Join-Path $projectRoot $check.Path
    if ((Get-Content -LiteralPath $path -Raw) -notmatch $check.Pattern) { throw "版本未同步：$($check.Path) 应为 $version" }
}
Write-Host "Desktop version synchronized: $version"
