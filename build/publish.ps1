<#
.SYNOPSIS
    Builds the WEBit release (Caddy bundled; PHP, Mailpit and VC++ runtime are downloaded in the app): webit_v<version>_portable.zip (+ .sha256) and, if Inno Setup
    is installed, webit_v<version>_setup.exe.

.DESCRIPTION
    1. Fetches third-party components (fetch-thirdparty.ps1) unless -SkipFetch
    2. Publishes app and service self-contained into one folder
    3. Checks that every bundled component has a license file
    4. Packs the portable ZIP (with WEBit.portable marker) and writes the SHA-256
    5. Compiles build\installer\webit.iss with ISCC (skip with -SkipInstaller)

.EXAMPLE
    pwsh -File build\publish.ps1
    pwsh -File build\publish.ps1 -Runtime win-arm64 -SkipInstaller
#>
[CmdletBinding()]
param(
    [string] $Configuration = 'Release',
    [string] $Runtime = 'win-x64',
    [switch] $SkipFetch,
    [switch] $SkipInstaller
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$root = Split-Path -Parent $PSScriptRoot
[xml] $props = Get-Content -Raw -Path (Join-Path $root 'Directory.Build.props')
$versionNode = $props.SelectSingleNode('//Version')
if ($null -eq $versionNode) { throw 'Version not found in Directory.Build.props.' }
$version = $versionNode.InnerText.Trim()

$name = "webit_v$version"
$artifacts = Join-Path $root 'artifacts'
$publishDir = Join-Path $artifacts "publish\WEBit"
$portableRoot = Join-Path $artifacts "$($name)_portable"
$zip = Join-Path $artifacts "$($name)_portable.zip"

Write-Host "WEBit $version · $Configuration · $Runtime" -ForegroundColor Cyan

if (-not $SkipFetch) {
    & (Join-Path $PSScriptRoot 'fetch-thirdparty.ps1')
}
else {
    & (Join-Path $PSScriptRoot 'fetch-thirdparty.ps1') -NoticesOnly
}

foreach ($exe in @('runtime\caddy\caddy.exe')) {
    if (-not (Test-Path (Join-Path $root $exe))) {
        throw "$exe is missing – run build\fetch-thirdparty.ps1."
    }
}

# Every bundled component needs a license file.
$manifest = Get-Content -Raw -Path (Join-Path $PSScriptRoot 'thirdparty.json') | ConvertFrom-Json
foreach ($component in $manifest.components) {
    if (-not (Test-Path (Join-Path $root $component.licenseFile))) {
        throw "License file missing: $($component.licenseFile)"
    }
}

foreach ($dir in (Split-Path $publishDir), $portableRoot) {
    if (Test-Path $dir) { Remove-Item -Recurse -Force $dir }
}
New-Item -ItemType Directory -Path $publishDir -Force | Out-Null

$common = @('-c', $Configuration, '-r', $Runtime, '--self-contained', 'true', '-p:PublishReadyToRun=false', '-p:DebugType=none', '-o', $publishDir)
dotnet publish (Join-Path $root 'src\WEBit.Service\WEBit.Service.csproj') @common
if ($LASTEXITCODE -ne 0) { throw 'Publishing WEBit.Service failed.' }
dotnet publish (Join-Path $root 'src\WEBit.App\WEBit.App.csproj') @common
if ($LASTEXITCODE -ne 0) { throw 'Publishing WEBit.App failed.' }

# Portable ZIP: same files plus the marker (data in .\data).
$staging = Join-Path $portableRoot 'WEBit'
New-Item -ItemType Directory -Path $staging -Force | Out-Null
Copy-Item -Path (Join-Path $publishDir '*') -Destination $staging -Recurse
Set-Content -Path (Join-Path $staging 'WEBit.portable') -Value 'Portable mode: data is stored in .\data' -Encoding ascii

if (Test-Path $zip) { Remove-Item -Force $zip }
Compress-Archive -Path $staging -DestinationPath $zip -CompressionLevel Optimal
$hash = (Get-FileHash -Path $zip -Algorithm SHA256).Hash.ToLowerInvariant()
Set-Content -Path "$zip.sha256" -Value "$hash  $(Split-Path -Leaf $zip)" -Encoding ascii
Write-Host "Portable: $zip" -ForegroundColor Green
Write-Host "SHA-256:  $hash"

# Installer (Inno Setup 6).
if (-not $SkipInstaller) {
    $iscc = @(
        (Get-Command 'ISCC.exe' -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Source),
        "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
        "$env:ProgramFiles\Inno Setup 6\ISCC.exe",
        "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe"
    ) | Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1

    if ($null -eq $iscc) {
        Write-Warning 'Inno Setup 6 (ISCC.exe) not found – installer skipped. https://jrsoftware.org/isinfo.php'
    }
    else {
        & $iscc "/DAppVersion=$version" "/DSourceDir=$publishDir" "/DOutputDir=$artifacts" (Join-Path $PSScriptRoot 'installer\webit.iss')
        if ($LASTEXITCODE -ne 0) { throw 'Installer build failed.' }
        $setup = Join-Path $artifacts "$($name)_setup.exe"
        $setupHash = (Get-FileHash -Path $setup -Algorithm SHA256).Hash.ToLowerInvariant()
        Set-Content -Path "$setup.sha256" -Value "$setupHash  $(Split-Path -Leaf $setup)" -Encoding ascii
        Write-Host "Setup:    $setup" -ForegroundColor Green
        Write-Host "SHA-256:  $setupHash"
    }
}
