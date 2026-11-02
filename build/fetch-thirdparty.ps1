<#
.SYNOPSIS
    Downloads the bundled third-party binaries (Caddy) from their official GitHub releases and
    verifies them (SHA-256 asset digest of the GitHub API, fallback: release checksum file).

.DESCRIPTION
    PHP, Mailpit and the Visual C++ runtime are not bundled: the app downloads them on demand
    (Modules → Download), from the same official sources and with the same checks.

.EXAMPLE
    pwsh -File build\fetch-thirdparty.ps1
    pwsh -File build\fetch-thirdparty.ps1 -Force
    pwsh -File build\fetch-thirdparty.ps1 -Only caddy
    pwsh -File build\fetch-thirdparty.ps1 -NoticesOnly

    After fetching, the Go modules compiled into caddy.exe are read with `caddy build-info` and listed in
    licenses/third-party/CADDY-MODULES.md (MIT/BSD/Apache notices of the bundled binary).
#>
[CmdletBinding()]
param(
    [switch] $Force,
    [string[]] $Only,
    [switch] $NoticesOnly
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

$root = Split-Path -Parent $PSScriptRoot
$manifest = Get-Content -Raw -Path (Join-Path $PSScriptRoot 'thirdparty.json') | ConvertFrom-Json
$work = Join-Path ([IO.Path]::GetTempPath()) ('webit-fetch-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $work | Out-Null
$headers = @{ 'User-Agent' = 'WEBit-build'; 'Accept' = 'application/vnd.github+json' }
# CI: authenticated API calls (higher rate limit).
if ($env:GITHUB_TOKEN) { $headers['Authorization'] = "Bearer $($env:GITHUB_TOKEN)" }

function Get-Prop($object, [string] $name, $default = $null) {
    if ($object.PSObject.Properties.Name -contains $name) { return $object.$name }
    return $default
}

function Test-Current([string] $target, [string] $version) {
    $marker = Join-Path $target '.version'
    return (-not $Force) -and (Test-Path $marker) -and ((Get-Content $marker -Raw).Trim() -eq $version)
}

function Save-Marker([string] $target, [string] $version) {
    Set-Content -Path (Join-Path $target '.version') -Value $version -Encoding ascii
}

function Assert-Hash([string] $file, [string] $algorithm, [string] $expected) {
    $actual = (Get-FileHash -Path $file -Algorithm $algorithm).Hash.ToLowerInvariant()
    if ($actual -ne $expected.ToLowerInvariant()) {
        throw "Checksum mismatch for $(Split-Path -Leaf $file)!`n expected: $expected`n actual:   $actual"
    }
}

function Copy-Files([string] $extract, [string] $target, $files) {
    New-Item -ItemType Directory -Path $target -Force | Out-Null
    if (@($files) -contains '*') {
        # Whole archive; keep .gitkeep.
        Get-ChildItem -Path $target -Force | Where-Object { $_.Name -ne '.gitkeep' } | Remove-Item -Recurse -Force
        Copy-Item -Path (Join-Path $extract '*') -Destination $target -Recurse -Force
        return
    }

    foreach ($file in $files) {
        $source = Get-ChildItem -Path $extract -Recurse -File -Filter $file | Select-Object -First 1
        if ($source) { Copy-Item -Path $source.FullName -Destination (Join-Path $target $file) -Force }
    }
}

function Get-GitHubHash($component, $release, $asset) {
    # 1) GitHub asset digest ("sha256:<hex>")
    $item = $release.assets | Where-Object { $_.name -eq $asset } | Select-Object -First 1
    if (-not $item) { throw "Asset $asset not found in release $($release.tag_name)." }
    if ($item.PSObject.Properties.Name -contains 'digest' -and $item.digest -match '^sha256:([0-9a-fA-F]{64})$') {
        return @{ Algorithm = 'SHA256'; Hash = $Matches[1].ToLowerInvariant(); Url = $item.browser_download_url }
    }

    # 2) Checksum file of the release
    $sumsName = $component.checksums.Replace('{version}', $component.version)
    $sumsItem = $release.assets | Where-Object { $_.name -eq $sumsName } | Select-Object -First 1
    if (-not $sumsItem) { throw "No digest and no checksum file for $asset." }
    $sumsFile = Join-Path $work "$($component.id)-checksums.txt"
    Invoke-WebRequest -Uri $sumsItem.browser_download_url -OutFile $sumsFile -UseBasicParsing -Headers $headers
    $line = Get-Content $sumsFile | Where-Object { $_ -match "\s\*?$([Regex]::Escape($asset))$" } | Select-Object -First 1
    if (-not $line) { throw "No checksum for $asset in $sumsName." }
    $hash = ($line -split '\s+')[0].ToLowerInvariant()
    $algorithm = switch ($hash.Length) { 128 { 'SHA512' } 64 { 'SHA256' } default { throw "Unknown checksum format: $hash" } }
    return @{ Algorithm = $algorithm; Hash = $hash; Url = $item.browser_download_url }
}

function Install-GitHub($component) {
    $version = $component.version
    $target = Join-Path $root $component.target
    if (Test-Current $target $version) {
        Write-Host "[$($component.name)] $version already present (-Force to refresh)." -ForegroundColor DarkGray
        return
    }

    $tag = $component.tag.Replace('{version}', $version)
    $asset = $component.asset.Replace('{version}', $version)
    Write-Host "[$($component.name)] $tag · $asset" -ForegroundColor Cyan
    $release = Invoke-RestMethod -Uri "https://api.github.com/repos/$($component.repository)/releases/tags/$tag" -Headers $headers
    $expected = Get-GitHubHash $component $release $asset

    $zip = Join-Path $work $asset
    Invoke-WebRequest -Uri $expected.Url -OutFile $zip -UseBasicParsing -Headers @{ 'User-Agent' = 'WEBit-build' }
    Assert-Hash $zip $expected.Algorithm $expected.Hash
    Write-Host "[$($component.name)] $($expected.Algorithm) verified" -ForegroundColor Green

    $extract = Join-Path $work $component.id
    Expand-Archive -Path $zip -DestinationPath $extract -Force
    Copy-Files $extract $target $component.files
    Save-Marker $target $version
    Write-Host "[$($component.name)] $version -> $($component.target)" -ForegroundColor Green
}

# Go modules compiled into caddy.exe (their MIT/BSD/Apache notices travel with the binary).
function Write-CaddyModules($component) {
    $exe = Join-Path (Join-Path $root $component.target) 'caddy.exe'
    $out = Join-Path $root 'licenses/third-party/CADDY-MODULES.md'
    if (-not (Test-Path $exe)) { return }
    if ((-not $Force) -and (Test-Path $out) -and ((Get-Item $out).LastWriteTimeUtc -ge (Get-Item $exe).LastWriteTimeUtc) -and
        (Select-String -Path $out -Pattern '^\| `' -Quiet)) {
        return
    }

    $info = & $exe build-info 2>$null
    if ($LASTEXITCODE -ne 0 -or -not $info) {
        Write-Warning "[$($component.name)] build-info failed – $out not updated."
        return
    }

    $lines = [Collections.Generic.List[string]]::new()
    $lines.Add('# Caddy – compiled-in Go modules')
    $lines.Add('')
    $lines.Add("Generated by ``build/fetch-thirdparty.ps1`` from ``WEBit-Caddy.exe build-info`` (Caddy $($component.version)).")
    $lines.Add('The modules are part of the unmodified official Caddy binary. Each module''s license and copyright notice')
    $lines.Add('is linked (pkg.go.dev, tab "Licenses"); the common license texts are in this folder:')
    $lines.Add('[LICENSE-APACHE-2.0.md](LICENSE-APACHE-2.0.md), [LICENSE-MIT.md](LICENSE-MIT.md), [LICENSE-BSD-2-CLAUSE.md](LICENSE-BSD-2-CLAUSE.md),')
    $lines.Add('[LICENSE-BSD-3-CLAUSE.md](LICENSE-BSD-3-CLAUSE.md), [LICENSE-ISC.md](LICENSE-ISC.md); other licenses are linked per module.')
    $lines.Add('')
    $go = $info | Where-Object { $_ -match '^go\d' } | Select-Object -First 1
    if ($go) { $lines.Add("Go toolchain: $($go.Trim()) (BSD-3-Clause, https://go.dev/LICENSE)"); $lines.Add('') }
    $lines.Add('| Module | Version | License and notice |')
    $lines.Add('| --- | --- | --- |')
    $count = 0
    foreach ($line in $info) {
        $parts = $line.Trim() -split '\s+'
        if ($parts.Count -ge 3 -and $parts[0] -eq 'dep') {
            $lines.Add("| ``$($parts[1])`` | $($parts[2]) | https://pkg.go.dev/$($parts[1])@$($parts[2])?tab=licenses |")
            $count++
        }
        elseif ($parts.Count -ge 3 -and $parts[0] -eq '=>' -and $lines.Count -gt 0) {
            # Replaced module: the replacement is what is compiled in.
            $lines[$lines.Count - 1] = "| ``$($parts[1])`` | $($parts[2]) | https://pkg.go.dev/$($parts[1])@$($parts[2])?tab=licenses |"
        }
    }

    Set-Content -Path $out -Value $lines -Encoding utf8
    Write-Host "[$($component.name)] $count Go modules -> licenses/third-party/CADDY-MODULES.md" -ForegroundColor Green
}

try {
    foreach ($component in $manifest.components) {
        if ($Only -and ($Only -notcontains $component.id)) { continue }

        if (-not $NoticesOnly) {
            switch (Get-Prop $component 'provider' 'github') {
                'github' { Install-GitHub $component }
                default { throw "Unknown provider for $($component.id)." }
            }
        }

        if ($component.id -eq 'caddy') { Write-CaddyModules $component }
    }
}
finally {
    Remove-Item -Recurse -Force $work -ErrorAction SilentlyContinue
}
