param(
    [string]$ReleaseTag = "",
    [string]$Destination = "",
    [string]$SourcePath = "",
    [string]$Sha256 = "C1134FE86042895B781090C50054F050817E05C59321FB6597E5F691C505C608",
    [switch]$Force
)

$ErrorActionPreference = "Stop"
$projectRoot = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($ReleaseTag)) {
    [xml]$props = Get-Content -LiteralPath (Join-Path $projectRoot "Directory.Build.props") -Raw
    $ReleaseTag = [string]$props.Project.PropertyGroup.RuntimeSeedReleaseTag
}
if ([string]::IsNullOrWhiteSpace($Destination)) {
    $Destination = Join-Path $projectRoot "DeepSeekHarnessDesktop\Assets\runtime.seed.zip"
}
$Destination = [System.IO.Path]::GetFullPath($Destination)
$destinationDirectory = Split-Path -Parent $Destination
$expectedHash = $Sha256.ToUpperInvariant()

function Test-RuntimeSeed([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { return $false }
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash -eq $expectedHash
}

if ((Test-RuntimeSeed $Destination) -and -not $Force) {
    Write-Host "runtime.seed.zip 已存在且校验通过。"
    return
}
if ((Test-Path -LiteralPath $Destination) -and -not $Force) {
    throw "现有 runtime.seed.zip 校验失败。确认文件可替换后使用 -Force。"
}

New-Item -ItemType Directory -Path $destinationDirectory -Force | Out-Null
$temporary = Join-Path $destinationDirectory ("runtime.seed.{0}.download" -f [guid]::NewGuid().ToString("N"))
try {
    if (-not [string]::IsNullOrWhiteSpace($SourcePath)) {
        $source = [System.IO.Path]::GetFullPath($SourcePath)
        if (-not (Test-Path -LiteralPath $source -PathType Leaf)) {
            throw "本地运行时种子不存在：$source"
        }
        Copy-Item -LiteralPath $source -Destination $temporary
    } else {
        $url = "https://github.com/jinganwushideng/deepseek-harness-desktop/releases/download/$ReleaseTag/runtime.seed.zip"
        Write-Host "正在从 $url 下载离线运行时…"
        Invoke-WebRequest -Uri $url -OutFile $temporary -UseBasicParsing
    }

    $actualHash = (Get-FileHash -LiteralPath $temporary -Algorithm SHA256).Hash
    if ($actualHash -ne $expectedHash) {
        throw "runtime.seed.zip 校验失败。期望 $expectedHash，实际 $actualHash。"
    }
    Move-Item -LiteralPath $temporary -Destination $Destination -Force
    Write-Host "离线运行时已准备完成：$Destination"
} finally {
    if (Test-Path -LiteralPath $temporary) {
        Remove-Item -LiteralPath $temporary -Force
    }
}
