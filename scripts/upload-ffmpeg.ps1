# Uploads the AVS3-P3 ffmpeg build to a GitHub release (and optionally to GitHub Packages).
#
# The build is roughly 110 MB and is third-party GPLv3 software, so it is never committed: this
# script packages the folder you point it at and publishes it as a release asset, which is how
# NcmForge users are told to obtain it.
#
# Usage:
#   powershell -ExecutionPolicy Bypass -File scripts/upload-ffmpeg.ps1 `
#       -FfmpegDir "D:\ffmpeg-avs" -Tag v1.0.0-rc2 -Token $env:GITHUB_TOKEN
#
#   # also push it to GitHub Packages (ghcr.io) as an OCI artifact, if oras is installed:
#   ... -Packages
#
# Requires a token with Contents: Read and write. -Packages additionally needs Packages: Write.

param(
    [Parameter(Mandatory = $true)]
    [string] $FfmpegDir,

    # Release to attach to. The release must already exist.
    [Parameter(Mandatory = $true)]
    [string] $Tag,

    [string] $Owner = '112012201',
    [string] $Repository = 'ncmforge',

    # A fine-grained PAT, or $env:GITHUB_TOKEN.
    [string] $Token = $env:GITHUB_TOKEN,

    # Also publish to ghcr.io. Needs the `oras` CLI and a token with Packages: Write.
    [switch] $Packages,

    # Where the zip is written. Defaults to a temporary folder.
    [string] $WorkDir = (Join-Path ([System.IO.Path]::GetTempPath()) ("ncmforge-ffmpeg-" + [guid]::NewGuid().ToString('N').Substring(0, 8)))
)

$ErrorActionPreference = 'Stop'

if (-not $Token) { throw 'no token supplied: pass -Token or set GITHUB_TOKEN' }
if (-not (Test-Path $FfmpegDir)) { throw "ffmpeg folder not found: $FfmpegDir" }

$source = (Resolve-Path $FfmpegDir).Path
$assetName = 'NcmForge-ffmpeg-av3a-win-x64.zip'

# --- what goes in -----------------------------------------------------------
# Only the pieces a converter needs. ffplay is a player and is deliberately left out: it would add
# another 110 MB to every download for no benefit.
$required = @('ffmpeg.exe')
$optional = @('ffprobe.exe', 'COPYING.GPLv3.txt', 'NOTICE.txt', 'README.txt')

foreach ($name in $required) {
    if (-not (Test-Path (Join-Path $source $name))) {
        throw "$source does not contain $name - is this an ffmpeg build folder?"
    }
}

New-Item -ItemType Directory -Force -Path $WorkDir | Out-Null
$zipPath = Join-Path $WorkDir $assetName

if (Test-Path $zipPath) { Remove-Item $zipPath -Force }

# Stage into a clean folder so the zip has no stray files from the source directory.
$stage = Join-Path $WorkDir 'stage'
if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
New-Item -ItemType Directory -Force -Path $stage | Out-Null

$included = @()
foreach ($name in ($required + $optional)) {
    $file = Join-Path $source $name
    if (Test-Path $file) {
        Copy-Item $file $stage -Force
        $included += $name
    }
}

Write-Host "packaging from $source"
foreach ($name in $included) {
    $size = [math]::Round((Get-Item (Join-Path $stage $name)).Length / 1MB, 1)
    Write-Host ("  {0,-22} {1,7} MB" -f $name, $size)
}

if (-not (Test-Path (Join-Path $stage 'NOTICE.txt'))) {
    throw "NOTICE.txt is missing from $source - it carries the GPLv3 redistribution notice and must ship with the binary"
}

Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $zipPath -CompressionLevel Optimal

$zip = Get-Item $zipPath
Write-Host ("`nzip: {0} ({1:N1} MB)" -f $zip.FullName, ($zip.Length / 1MB))

$hash = (Get-FileHash $zipPath -Algorithm SHA256).Hash.ToLower()
Write-Host "sha256: $hash"

# --- upload to the release --------------------------------------------------
$headers = @{
    Authorization          = "Bearer $Token"
    Accept                 = 'application/vnd.github+json'
    'User-Agent'           = 'NcmForge-upload-ffmpeg'
    'X-GitHub-Api-Version' = '2022-11-28'
}

$apiBase = "https://api.github.com/repos/$Owner/$Repository"

Write-Host "`nresolving release $Tag ..."
$release = Invoke-RestMethod -Uri "$apiBase/releases/tags/$Tag" -Headers $headers
Write-Host "  release id $($release.id)"

# Replacing an existing asset means deleting it first: the upload endpoint refuses duplicates.
$existing = $release.assets | Where-Object { $_.name -eq $assetName }
if ($existing) {
    Write-Host "  removing the existing $assetName (id $($existing.id))"
    Invoke-RestMethod -Method Delete -Uri "$apiBase/releases/assets/$($existing.id)" -Headers $headers | Out-Null
}

Write-Host "uploading $assetName ..."
$uploadHeaders = $headers.Clone()
$uploadHeaders['Content-Type'] = 'application/zip'

$uploaded = Invoke-RestMethod `
    -Method Post `
    -Uri "https://uploads.github.com/repos/$Owner/$Repository/releases/$($release.id)/assets?name=$assetName" `
    -Headers $uploadHeaders `
    -InFile $zipPath

Write-Host ("  done: {0} ({1:N1} MB)" -f $uploaded.browser_download_url, ($uploaded.size / 1MB))
Write-Host ("  url : {0}" -f $uploaded.browser_download_url)

# --- optionally publish to GitHub Packages ----------------------------------
if ($Packages) {
    $oras = Get-Command oras -ErrorAction SilentlyContinue
    if (-not $oras) {
        Write-Warning 'oras is not installed, so the Packages push was skipped. Install it from https://oras.land/ and re-run with -Packages.'
    }
    else {
        # ghcr.io is the only GitHub Packages registry that takes an arbitrary file: the others are
        # language-specific (npm, NuGet, Maven, RubyGems) and a 110 MB .exe fits none of them.
        $image = "ghcr.io/$($Owner.ToLower())/$Repository/ffmpeg-av3a:$Tag"
        Write-Host "`npushing to GitHub Packages: $image"
        & oras push $image `
            --artifact-type application/vnd.ncmforge.ffmpeg.v1+zip `
            "$zipPath`:application/zip" `
            --annotation "org.opencontainers.image.source=https://github.com/$Owner/$Repository" `
            --annotation "org.opencontainers.image.version=$Tag"
        if ($LASTEXITCODE -ne 0) { throw "oras push failed with exit code $LASTEXITCODE" }
        Write-Host "  pull it back with: oras pull $image"
    }
}

Write-Host "`nremember: the bundled ffmpeg is GPLv3. Redistributing it requires the licence text and an offer of corresponding source - see THIRD-PARTY-NOTICES.md."
Write-Host "work folder kept at $WorkDir"
