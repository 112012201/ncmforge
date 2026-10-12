# Signs the Windows release assets with a local Certum certificate, repackages, and replaces them on
# the GitHub release — plus the .exe installer, which is rebuilt from the signed tree.
#
# Why this runs locally rather than in CI
# ---------------------------------------
# Certum's SimplySign keeps the private key in their cloud HSM and exposes it only through a signed-in
# desktop client. Since June 2023 the CA/Browser Forum requires exactly that for a publicly-trusted
# code-signing certificate, so it cannot be exported into a .pfx for a GitHub-hosted runner. Signing
# here, on the machine where SimplySign is signed in, is the supported route.
#
# Two phases, so a failed upload does not mean re-signing:
#
#   -Prepare   download the release archives, sign the binaries, rebuild the archives and the
#              installer, and leave everything in -WorkDir. No network writes.
#   -Upload    replace the release assets with what -Prepare produced.
#
# With neither switch it does both.
#
# Usage:
#   # find your thumbprint first
#   Get-ChildItem Cert:\CurrentUser\My | Where-Object { $_.HasPrivateKey } | Format-List Subject, Thumbprint
#
#   powershell -ExecutionPolicy Bypass -File scripts/sign-and-upload.ps1 `
#       -Tag v1.2.4153-rc2 -Thumbprint ABC123... -Token <github-pat> -Proxy http://127.0.0.1:7897

param(
    # Release tag whose Windows assets should be replaced, e.g. v1.2.4153-rc2.
    [Parameter(Mandatory = $true)]
    [string] $Tag,

    # Certificate thumbprint from the user store; SimplySign's, normally.
    [string] $Thumbprint,

    # A .pfx instead, for an internal or test certificate.
    [string] $PfxPath,
    [string] $PfxPassword,

    [Parameter(Mandatory = $true)]
    [string] $Token,

    [string] $Repo = '112012201/ncmforge',
    [string] $WorkDir,
    [string] $Proxy,

    # Architectures to process; must match the release's existing assets.
    [string[]] $Architectures = @('x64', 'x86', 'arm64'),

    [switch] $Prepare,
    [switch] $Upload,
    [switch] $SkipInstaller
)

$ErrorActionPreference = 'Stop'
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

$repoRoot = Split-Path -Parent $PSScriptRoot
$doPrepare = $Prepare -or (-not $Prepare -and -not $Upload)
$doUpload = $Upload -or (-not $Prepare -and -not $Upload)

if (-not $WorkDir) { $WorkDir = Join-Path $env:TEMP "ncmforge-sign-$Tag" }
New-Item -ItemType Directory -Force -Path $WorkDir | Out-Null

$headers = @{
    Authorization          = "Bearer $Token"
    Accept                 = 'application/vnd.github+json'
    'User-Agent'           = 'NcmForge'
    'X-GitHub-Api-Version' = '2022-11-28'
}

function Invoke-GitHub {
    param([string] $Uri, [string] $Method = 'GET', [hashtable] $ExtraHeaders = @{})
    $all = $headers.Clone()
    foreach ($k in $ExtraHeaders.Keys) { $all[$k] = $ExtraHeaders[$k] }
    return Invoke-RestMethod -Uri $Uri -Method $Method -Headers $all -TimeoutSec 120
}

function Get-CurlProxyArgs {
    # uploads.github.com is frequently unreachable directly on a proxied machine.
    if ($Proxy) { return @('--proxy', $Proxy) }
    if ($env:HTTPS_PROXY) { return @('--proxy', $env:HTTPS_PROXY) }
    return @()
}

$release = Invoke-GitHub "https://api.github.com/repos/$Repo/releases/tags/$Tag"
Write-Host "release $Tag has $($release.assets.Count) assets"

# --- prepare ------------------------------------------------------------------
$prepared = @()

if ($doPrepare) {
    if (-not $Thumbprint -and -not $PfxPath) {
        throw 'Supply -Thumbprint (SimplySign) or -PfxPath; nothing can be signed without a certificate.'
    }

    $signArgs = @{}
    if ($Thumbprint) { $signArgs.Thumbprint = $Thumbprint }
    if ($PfxPath) { $signArgs.PfxPath = $PfxPath; $signArgs.PfxPassword = $PfxPassword }

    foreach ($arch in $Architectures) {
        $rid = "win-$arch"
        $assetName = "NcmForge-$($Tag.TrimStart('v'))-$rid.zip"
        $asset = $release.assets | Where-Object { $_.name -eq $assetName } | Select-Object -First 1

        if (-not $asset) {
            Write-Warning "no $assetName on $Tag; skipping $arch"
            continue
        }

        $tree = Join-Path $WorkDir $rid
        Remove-Item -Recurse -Force $tree -ErrorAction SilentlyContinue
        New-Item -ItemType Directory -Force -Path $tree | Out-Null

        $zip = Join-Path $WorkDir $assetName
        Write-Host "downloading $assetName"
        & curl.exe -sSL @(Get-CurlProxyArgs) -o $zip $asset.browser_download_url
        if ($LASTEXITCODE -ne 0) { throw "downloading $assetName failed" }

        Expand-Archive -Path $zip -DestinationPath $tree -Force

        Write-Host "signing the $arch binaries"
        & (Join-Path $PSScriptRoot 'sign-windows.ps1') -Path $tree @signArgs
        if ($LASTEXITCODE -ne 0) { throw "signing $rid failed" }

        # Rezip from inside the tree so the archive keeps its original layout.
        $signedZip = Join-Path $WorkDir "$rid-signed.zip"
        Remove-Item $signedZip -ErrorAction SilentlyContinue
        Compress-Archive -Path (Join-Path $tree '*') -DestinationPath $signedZip
        $prepared += [pscustomobject]@{ Kind = 'zip'; Arch = $arch; Asset = $asset; Path = $signedZip }

        if (-not $SkipInstaller) {
            $setupName = "NcmForge-$($Tag.TrimStart('v'))-win-$arch-setup.exe"
            $setup = & (Join-Path $PSScriptRoot 'build-installer.ps1') `
                -StageDir $tree -Version $Tag.TrimStart('v') -Arch $arch -OutputDir $WorkDir `
                @signArgs -ErrorAction Stop |
                Select-Object -Last 1

            $setupPath = Join-Path $WorkDir $setupName
            if (-not (Test-Path $setupPath)) { throw "the installer was not produced at $setupPath" }

            # The installer only replaces an existing asset when there is one; a first release may
            # have no installer yet, in which case this simply adds it.
            $existing = $release.assets | Where-Object { $_.name -eq $setupName } | Select-Object -First 1
            $prepared += [pscustomobject]@{ Kind = 'setup'; Arch = $arch; Asset = $existing; Path = $setupPath }
        }
    }

    Write-Host ''
    Write-Host "prepared $($prepared.Count) file(s) in $WorkDir"
    foreach ($p in $prepared) { Write-Host ("  {0,-6} {1}" -f $p.Kind, (Split-Path -Leaf $p.Path)) }
}

# --- upload -------------------------------------------------------------------
if ($doUpload) {
    if (-not $doPrepare) {
        # Upload-only run: take whatever -Prepare left behind.
        foreach ($arch in $Architectures) {
            $signedZip = Join-Path $WorkDir "win-$arch-signed.zip"
            if (Test-Path $signedZip) {
                $name = "NcmForge-$($Tag.TrimStart('v'))-win-$arch.zip"
                $prepared += [pscustomobject]@{
                    Kind = 'zip'; Arch = $arch
                    Asset = $release.assets | Where-Object { $_.name -eq $name } | Select-Object -First 1
                    Path = $signedZip
                }
            }
            $setupName = "NcmForge-$($Tag.TrimStart('v'))-win-$arch-setup.exe"
            $setupPath = Join-Path $WorkDir $setupName
            if (Test-Path $setupPath) {
                $prepared += [pscustomobject]@{
                    Kind = 'setup'; Arch = $arch
                    Asset = $release.assets | Where-Object { $_.name -eq $setupName } | Select-Object -First 1
                    Path = $setupPath
                }
            }
        }
    }

    if ($prepared.Count -eq 0) { throw 'nothing to upload' }

    foreach ($item in $prepared) {
        $name = Split-Path -Leaf $item.Path

        # GitHub will not overwrite an asset, so the old one has to go first. Done immediately before
        # the upload so the window where the asset is missing stays as short as possible.
        if ($item.Asset) {
            Write-Host "replacing $name"
            Invoke-GitHub "https://api.github.com/repos/$Repo/releases/assets/$($item.Asset.id)" -Method DELETE | Out-Null
        }
        else {
            Write-Host "adding $name"
        }

        # Work out the upload endpoint. A name with characters GitHub rewrites (the version's ~
        # becomes .) is uploaded by id against the release, so the URL is built from the release.
        $uploadUrl = ($release.upload_url -replace '\{.*$', '') + "?name=$([uri]::EscapeDataString($name))"

        $curlArgs = @('-sS', '--retry', '5', '--retry-delay', '5', '-X', 'POST') + (Get-CurlProxyArgs) + @(
            '-H', "Authorization: Bearer $Token",
            '-H', 'Content-Type: application/octet-stream',
            '-H', 'User-Agent: NcmForge',
            '--data-binary', "@$($item.Path)",
            $uploadUrl
        )
        $response = & curl.exe @curlArgs
        if ($LASTEXITCODE -ne 0) { throw "uploading $name failed" }

        $parsed = $response | ConvertFrom-Json
        Write-Host ("  uploaded {0} ({1:N1} MB)" -f $parsed.name, ($parsed.size / 1MB))
    }

    Write-Host ''
    Write-Host "done. Verify with: scripts/sign-windows.ps1 -Path <extracted tree> -VerifyOnly"
}
