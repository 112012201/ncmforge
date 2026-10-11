# Publishes NcmForge for distribution.
#
# Two layouts are supported, because they trade off differently:
#
#   multi-file (default)  a folder of the executable plus its libraries. Starts faster, needs no
#                         %TEMP% write access, and keeps the ffmpeg alongside as a separate file.
#   -SingleFile           one self-contained .exe. Convenient to hand around, but it extracts its
#                         native libraries to %TEMP%\.net on first run, and bundling a 110 MB
#                         ffmpeg inside it produces an unwieldy single file.
#
# Trimming is deliberately NOT enabled: Avalonia resolves XAML types by reflection and the trimmer
# cannot see those references, which turns into runtime failures.
#
# Usage:
#   powershell -ExecutionPolicy Bypass -File scripts/publish.ps1
#   powershell -ExecutionPolicy Bypass -File scripts/publish.ps1 -FfmpegDir "D:\ffmpeg-avs"
#   powershell -ExecutionPolicy Bypass -File scripts/publish.ps1 -Rids linux-x64,osx-arm64
#
# -FfmpegDir copies a patched ffmpeg into <app>/tools/ffmpeg/ so the app finds it before anything
# on PATH. That is what makes Audio Vivid (AVS3-P3) decoding work out of the box. ffplay is never
# copied (it is a player, not a converter) and ffprobe only with -WithFfprobe.

param(
    [string[]] $Rids = @('win-x64'),
    [string]   $Configuration = 'Release',
    [string]   $OutputRoot = 'dist',

    # Folder holding ffmpeg.exe (and optionally ffprobe.exe + COPYING.GPLv3.txt) to bundle.
    [string]   $FfmpegDir,

    # ffprobe is not required by the converter; it costs another ~110 MB per app.
    [switch]   $WithFfprobe,

    # Bundle ffmpeg into the CLI as well as the GUI. Off by default so the CLI stays small.
    [switch]   $FfmpegInCli,

    # Produce one self-contained .exe per app instead of a folder.
    [switch]   $SingleFile
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot

$apps = @(
    # Folder names must differ by more than case: Windows and macOS filesystems are
    # case-insensitive, so "NcmForge" and "ncmforge" would be the same directory and the second
    # publish would overwrite the first.
    @{ Name = 'NcmForge.Gui'; Path = 'src/NcmForge.App/NcmForge.App.csproj'; GetsFfmpeg = $true },
    @{ Name = 'NcmForge.Cli'; Path = 'src/NcmForge.Cli/NcmForge.Cli.csproj'; GetsFfmpeg = $FfmpegInCli }
)

function Copy-Ffmpeg([string]$appDirectory) {
    if (-not $FfmpegDir) { return }

    $source = (Resolve-Path $FfmpegDir).Path
    if (-not (Test-Path (Join-Path $source 'ffmpeg.exe')) -and -not (Test-Path (Join-Path $source 'ffmpeg'))) {
        throw "no ffmpeg binary found in $source"
    }

    $target = Join-Path $appDirectory 'tools\ffmpeg'
    New-Item -ItemType Directory -Force -Path $target | Out-Null

    foreach ($name in @('ffmpeg.exe', 'ffmpeg', 'COPYING.GPLv3.txt', 'NOTICE.txt')) {
        $file = Join-Path $source $name
        if (Test-Path $file) { Copy-Item $file $target -Force }
    }

    if ($WithFfprobe) {
        foreach ($name in @('ffprobe.exe', 'ffprobe')) {
            $file = Join-Path $source $name
            if (Test-Path $file) { Copy-Item $file $target -Force }
        }
    }

    Write-Host "  bundled ffmpeg from $source"
}

foreach ($rid in $Rids) {
    foreach ($app in $apps) {
        $suffix = if ($SingleFile) { '-single' } else { '' }
        $out = Join-Path $repo (Join-Path $OutputRoot (Join-Path $rid ($app.Name + $suffix)))
        Write-Host "publishing $($app.Name) for $rid -> $out"

        $arguments = @(
            (Join-Path $repo $app.Path),
            '-c', $Configuration,
            '-r', $rid,
            '--self-contained', 'true',
            '-p:DebugType=none',
            '-o', $out
        )

        if ($SingleFile) {
            $arguments += '-p:PublishSingleFile=true'
            $arguments += '-p:IncludeNativeLibrariesForSelfExtract=true'
            # Native libraries have to come out of the bundle to be loaded, so the ffmpeg must sit
            # next to the exe rather than inside it.
            $arguments += '-p:EnableCompressionInSingleFile=false'
        }

        dotnet publish @arguments

        if ($LASTEXITCODE -ne 0) {
            throw "publish failed for $($app.Name) / $rid"
        }

        if ($app.GetsFfmpeg) {
            Copy-Ffmpeg $out
        }
    }
}

Write-Host ''
Write-Host 'done. artifacts:'
foreach ($rid in $Rids) {
    foreach ($app in $apps) {
        $suffix = if ($SingleFile) { '-single' } else { '' }
        $out = Join-Path $repo (Join-Path $OutputRoot (Join-Path $rid ($app.Name + $suffix)))
        if (-not (Test-Path $out)) { continue }

        $files = Get-ChildItem $out -Recurse -File
        $total = ($files | Measure-Object -Property Length -Sum).Sum
        $main = $files | Where-Object { $_.Name -in @('NcmForge.exe', 'ncmforge', 'ncmforge.exe', 'NcmForge') } | Select-Object -First 1
        $ffmpeg = Join-Path $out 'tools\ffmpeg\ffmpeg.exe'

        '{0,-40} {1,6:N0} files  {2,7:N1} MB total' -f $out.Replace("$repo\", ''), $files.Count, ($total / 1MB)
        if ($main) { '    {0,-36} {1,7:N1} MB' -f $main.Name, ($main.Length / 1MB) }
        if (Test-Path $ffmpeg) { '    {0,-36} {1,7:N1} MB' -f 'tools/ffmpeg/ffmpeg.exe', ((Get-Item $ffmpeg).Length / 1MB) }
    }
}
