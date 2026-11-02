<#
.SYNOPSIS
    Release check: build, tests, publish (portable + Inno Setup) and a content check of the results.

.EXAMPLE
    pwsh -File build\verify.ps1
    pwsh -File build\verify.ps1 -SkipInstaller
#>
[CmdletBinding()]
param(
    [switch] $SkipInstaller
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$root = Split-Path -Parent $PSScriptRoot
[xml] $props = Get-Content -Raw -Path (Join-Path $root 'Directory.Build.props')
$version = $props.SelectSingleNode('//Version').InnerText.Trim()
$artifacts = Join-Path $root 'artifacts'
$failed = [System.Collections.Generic.List[string]]::new()

function Step([string] $title, [scriptblock] $action) {
    Write-Host "`n== $title" -ForegroundColor Cyan
    & $action
    if ($LASTEXITCODE -ne 0) { throw "$title failed (exit $LASTEXITCODE)." }
}

function Expect([string] $path, [string] $what) {
    if (Test-Path $path) { Write-Host "  ok  $what" -ForegroundColor Green }
    else { Write-Host "  --  $what missing: $path" -ForegroundColor Red; $failed.Add($what) }
}

Step 'Build (Release)' { dotnet build (Join-Path $root 'WEBit.slnx') -c Release }
Step 'Tests' { dotnet test (Join-Path $root 'WEBit.slnx') -c Release --no-build }

Step 'Publish' { & (Join-Path $PSScriptRoot 'publish.ps1') -SkipInstaller:$SkipInstaller }

Write-Host "`n== Content" -ForegroundColor Cyan
$publish = Join-Path $artifacts 'publish\WEBit'
Expect (Join-Path $publish 'WEBit.exe') 'WEBit.exe'
Expect (Join-Path $publish 'WEBit.Service.exe') 'WEBit.Service.exe'
Expect (Join-Path $publish 'bin\caddy\WEBit-Caddy.exe') 'bundled Caddy'
Expect (Join-Path $publish 'licenses\LICENSE.md') 'license files'
Expect (Join-Path $publish 'licenses\third-party\MARIADB.md') 'third-party notices'
Expect (Join-Path $publish 'licenses\third-party\CADDY-MODULES.md') 'Caddy Go module notices'
if ((Test-Path (Join-Path $publish 'licenses\third-party\CADDY-MODULES.md')) -and
    -not (Select-String -Path (Join-Path $publish 'licenses\third-party\CADDY-MODULES.md') -Pattern '^\| `' -Quiet)) {
    Write-Host '  --  CADDY-MODULES.md has no module list (build-info not run)' -ForegroundColor Red
    $failed.Add('Caddy module list')
}
foreach ($notBundled in 'bin\php', 'bin\mailpit', 'bin\node', 'bin\mariadb', 'bin\python') {
    if (Test-Path (Join-Path $publish $notBundled)) {
        Write-Host "  --  $notBundled must not be bundled (downloaded in the app)" -ForegroundColor Red
        $failed.Add($notBundled)
    }
}

$zip = Join-Path $artifacts "webit_v$($version)_portable.zip"
Expect $zip 'portable ZIP'
if (Test-Path $zip) {
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive = [System.IO.Compression.ZipFile]::OpenRead($zip)
    try {
        $names = $archive.Entries | ForEach-Object { $_.FullName -replace '\\', '/' }
        if ($names -notcontains 'WEBit/WEBit.portable') { Write-Host '  --  portable marker missing in ZIP' -ForegroundColor Red; $failed.Add('portable marker') }
        else { Write-Host '  ok  portable marker' -ForegroundColor Green }
    }
    finally { $archive.Dispose() }
}

if (-not $SkipInstaller) {
    Expect (Join-Path $artifacts "webit_v$($version)_setup.exe") 'Inno Setup installer'
}

if ($failed.Count -gt 0) { throw "Release check failed: $($failed -join ', ')" }
Write-Host "`nWEBit $($version): release check passed." -ForegroundColor Green
