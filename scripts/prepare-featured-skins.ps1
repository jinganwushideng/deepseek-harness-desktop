param([switch]$Force)

$ErrorActionPreference = "Stop"
$projectRoot = Split-Path -Parent $PSScriptRoot
$destination = Join-Path $projectRoot "DeepSeekHarnessDesktop\Assets\FeaturedSkins"
$tempRoot = [System.IO.Path]::GetFullPath([System.IO.Path]::GetTempPath())
$working = Join-Path $tempRoot ("dsh-desktop-featured-skins-{0}" -f [guid]::NewGuid().ToString("N"))

$deepWhaleSource = "https://github.com/GGBond2424648901/deep-whale-day-night-theme/releases/download/v0.1.1/deep-whale-day-night-theme-v0.1.1.zip"
$deepWhaleSha256 = "A4781EE62F7DEC2F2EA82B1B500D2785FBCF526A74FF6A6ACDF2AC481C7DCB80"
$packages = @(
    @{ Name = "dshthemes-ui-0.2.0.tgz"; Url = "https://registry.npmjs.org/@dshthemes/ui/-/ui-0.2.0.tgz"; Sha512 = "286580EBDC543FC0C80FDFE1547AAC41EFE3A0E036F740A42BF868A8C091449FBF443602DDDFC29ECD17BE0285AAE0745B49EF361155C46957DC002A3B046376" },
    @{ Name = "dshthemes-core-0.2.0.tgz"; Url = "https://registry.npmjs.org/@dshthemes/core/-/core-0.2.0.tgz"; Sha512 = "1042965E44896EC32AB1ECB2B9C383402C157BFAE80854DCDAB955D559EE8467BBBA67D4BE0AEB22EF69FBE1E9E9C022501CC0174C831B74B35DB0DBEDE0AAE8" },
    @{ Name = "clsx-2.1.1.tgz"; Url = "https://registry.npmjs.org/clsx/-/clsx-2.1.1.tgz"; Sha512 = "7989B441606D52B0566561B4777F3A386030D7A67DF793E2395A3607B6E35926C779D1A5E5ED1959AABAE6438681448D7AC1080E407D2126D383F24AF5D84264" }
)

function Get-VerifiedDownload([string]$Url, [string]$Path, [string]$Algorithm, [string]$ExpectedHash) {
    if ((Test-Path -LiteralPath $Path -PathType Leaf) -and -not $Force) {
        if ((Get-FileHash -LiteralPath $Path -Algorithm $Algorithm).Hash -eq $ExpectedHash) { return }
    }
    $partial = "$Path.download"
    try {
        Invoke-WebRequest -Uri $Url -OutFile $partial -UseBasicParsing
        $actual = (Get-FileHash -LiteralPath $partial -Algorithm $Algorithm).Hash
        if ($actual -ne $ExpectedHash) { throw "Featured skin hash mismatch: $Url`nExpected $ExpectedHash`nActual $actual" }
        Move-Item -LiteralPath $partial -Destination $Path -Force
    } finally {
        if (Test-Path -LiteralPath $partial) { Remove-Item -LiteralPath $partial -Force }
    }
}

New-Item -ItemType Directory -Path $destination -Force | Out-Null
New-Item -ItemType Directory -Path $working -Force | Out-Null
try {
    foreach ($package in $packages) {
        Get-VerifiedDownload $package.Url (Join-Path $destination $package.Name) "SHA512" $package.Sha512
    }

    $upstreamZip = Join-Path $working "deep-whale-upstream.zip"
    Get-VerifiedDownload $deepWhaleSource $upstreamZip "SHA256" $deepWhaleSha256
    $expanded = Join-Path $working "expanded"
    Expand-Archive -LiteralPath $upstreamZip -DestinationPath $expanded
    $manifest = Get-ChildItem -LiteralPath $expanded -Filter package.json -Recurse | Select-Object -First 1
    if ($null -eq $manifest) { throw "Deep Whale release is missing package.json." }
    $sourceRoot = Split-Path -Parent $manifest.FullName
    $staged = Join-Path $working "install"
    New-Item -ItemType Directory -Path $staged | Out-Null
    foreach ($name in @("package.json", "cordis.patch.yml", "skin.json", "LICENSE", "NOTICE", "README.md")) {
        $source = Join-Path $sourceRoot $name
        if (-not (Test-Path -LiteralPath $source)) { throw "Deep Whale release is missing $name." }
        Copy-Item -LiteralPath $source -Destination (Join-Path $staged $name)
    }
    foreach ($name in @("lib", "preview")) {
        $source = Join-Path $sourceRoot $name
        if (-not (Test-Path -LiteralPath $source -PathType Container)) { throw "Deep Whale release is missing the $name directory." }
        Copy-Item -LiteralPath $source -Destination (Join-Path $staged $name) -Recurse
    }
    $installZip = Join-Path $destination "deep-whale-day-night-theme-v0.1.1.install.zip"
    if (Test-Path -LiteralPath $installZip) { Remove-Item -LiteralPath $installZip -Force }
    Compress-Archive -Path (Join-Path $staged "*") -DestinationPath $installZip -CompressionLevel Optimal
    Write-Host "Featured skin payloads are ready: $destination"
} finally {
    $resolvedWorking = [System.IO.Path]::GetFullPath($working)
    if ($resolvedWorking.StartsWith($tempRoot, [System.StringComparison]::OrdinalIgnoreCase) -and (Test-Path -LiteralPath $resolvedWorking)) {
        Remove-Item -LiteralPath $resolvedWorking -Recurse -Force
    }
}
