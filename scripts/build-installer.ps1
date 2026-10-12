# Builds the Windows .exe installer around an already-published build, and optionally signs it.
#
# Used by the release workflow for each Windows architecture and by scripts/sign-and-upload.ps1 for
# the local Certum/SimplySign flow, so both produce byte-identical installers from one definition
# (installer/ncmforge.iss).
#
# Usage:
#   powershell -ExecutionPolicy Bypass -File scripts/build-installer.ps1 `
#       -StageDir dist/win-x64 -Version 1.2.4153-rc2 -Arch x64 -OutputDir dist

param(
    # Folder holding NcmForge.exe and cli\ncmforge.exe. Made absolute, because the .iss requires it.
    [Parameter(Mandatory = $true)]
    [string] $StageDir,

    [Parameter(Mandatory = $true)]
    [string] $Version,

    # x64 | x86 | arm64
    [Parameter(Mandatory = $true)]
    [ValidateSet('x64', 'x86', 'arm64')]
    [string] $Arch,

    [string] $OutputDir = 'dist',

    # Optional: sign the setup with the same certificate settings as sign-windows.ps1.
    [string] $Thumbprint,
    [string] $PfxPath,
    [string] $PfxPassword,

    [string] $SignToolPath
)

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot

function Find-Iscc {
    $onPath = Get-Command ISCC.exe -ErrorAction SilentlyContinue
    if ($onPath) { return $onPath.Source }

    $candidates = @(
        "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
        "$env:ProgramFiles\Inno Setup 6\ISCC.exe",
        "${env:ProgramFiles(x86)}\Inno Setup 5\ISCC.exe",
        "$env:ProgramFiles\Inno Setup 5\ISCC.exe"
    )
    foreach ($candidate in $candidates) {
        if ($candidate -and (Test-Path $candidate)) { return $candidate }
    }
    return $null
}

$iscc = Find-Iscc
if (-not $iscc) {
    throw 'ISCC.exe was not found. Install Inno Setup 6 (the windows-latest runner image includes it).'
}

$stage = (Resolve-Path $StageDir).Path
$output = $OutputDir
if (-not [IO.Path]::IsPathRooted($output)) { $output = Join-Path $repoRoot $output }
New-Item -ItemType Directory -Force -Path $output | Out-Null

Write-Host "building the $Arch installer for $Version"
Write-Host "  stage  : $stage"
Write-Host "  output : $output"
Write-Host "  iscc   : $iscc"

& $iscc (Join-Path $repoRoot 'installer\ncmforge.iss') `
    "/DAppVersion=$Version" `
    "/DStageDir=$stage" `
    "/DAppArch=$Arch" `
    "/O$output"

if ($LASTEXITCODE -ne 0) {
    throw "ISCC failed with exit code $LASTEXITCODE"
}

$setup = Join-Path $output "NcmForge-$Version-win-$Arch-setup.exe"
if (-not (Test-Path $setup)) {
    throw "ISCC reported success but $setup does not exist"
}

Write-Host ("built {0} ({1:N1} MB)" -f (Split-Path -Leaf $setup), ((Get-Item $setup).Length / 1MB))

# The installer is what SmartScreen inspects, so it is signed too when a certificate is available.
if ($Thumbprint -or $PfxPath) {
    $signArgs = @{ Path = $setup }
    if ($SignToolPath) { $signArgs.SignToolPath = $SignToolPath }
    if ($Thumbprint) { $signArgs.Thumbprint = $Thumbprint }
    if ($PfxPath) { $signArgs.PfxPath = $PfxPath; $signArgs.PfxPassword = $PfxPassword }

    & (Join-Path $PSScriptRoot 'sign-windows.ps1') @signArgs
    if ($LASTEXITCODE -ne 0) { throw "signing the installer failed with exit code $LASTEXITCODE" }
}

Write-Host "installer ready: $setup"
