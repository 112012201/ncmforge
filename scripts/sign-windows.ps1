# Signs the Windows binaries with a real code-signing certificate.
#
# Nothing here can conjure a certificate: a publicly-trusted one has to come from a CA (Certum, in
# this project's case) and, since June 2023, the CA/Browser Forum requires its private key to live in
# hardware or a cloud HSM rather than in a plain file. So this script does the mechanical part —
# locate signtool, sign, timestamp, verify — and takes the key material from wherever you have it.
#
# Three ways to supply a certificate, in the order Certum customers usually have them:
#
#   1. -Thumbprint   The certificate is already in your user store. This is how Certum's SimplySign
#                    Desktop works: sign in, and the certificate appears in the store with its key
#                    held in their cloud. Nothing secret touches this machine or CI.
#                    Best for: signing on your own PC.
#
#   2. -PfxPath      A .pfx/.p12 file plus its password. A publicly-trusted Certum certificate cannot
#                    be exported to one, so this is for an internal or test certificate — or for a
#                    self-hosted runner where SimplySign is installed.
#                    Best for: CI with a self-hosted runner.
#
#   3. Neither       The script reports that signing was skipped and exits 0, so an unsigned build
#                    still succeeds. That is what a fork or a pull request will do.
#
# Azure Trusted Signing is the CI-friendly alternative worth knowing about: it keeps the key in
# Microsoft's HSM, is driven entirely by a service principal, and needs no runner-local certificate.
# It is not wired up here because this project signs with Certum.
#
# Usage:
#   # sign an already-published folder
#   powershell -ExecutionPolicy Bypass -File scripts/sign-windows.ps1 -Path dist/win-x64/NcmForge.Gui -Thumbprint ABC123...
#
#   # sign specific files with a pfx
#   powershell -ExecutionPolicy Bypass -File scripts/sign-windows.ps1 -Path publish/NcmForge.exe -PfxPath cert.pfx -PfxPassword hunter2
#
#   # check whether a file is signed, and by whom
#   powershell -ExecutionPolicy Bypass -File scripts/sign-windows.ps1 -Path NcmForge.exe -VerifyOnly

param(
    # A folder (signed recursively) or one or more files.
    [Parameter(Mandatory = $true)]
    [string[]] $Path,

    # Certificate thumbprint from the user or machine store, e.g. SimplySign's.
    [string] $Thumbprint,

    # A .pfx / .p12 and its password.
    [string] $PfxPath,
    [string] $PfxPassword,

    # Certum's RFC 3161 timestamp service. Timestamping is what keeps a signature valid after the
    # certificate expires — without it, every signature dies with the certificate.
    [string] $TimestampUrl = 'http://time.certum.pl',

    # Which files to sign. NcmForge ships one .exe per frontend plus a few hundred runtime dlls that
    # are already signed by Microsoft, so signing the launchers is what matters.
    [string[]] $Include = @('NcmForge.exe', 'ncmforge.exe'),

    # Just report the current signatures and sign nothing.
    [switch] $VerifyOnly
)

$ErrorActionPreference = 'Stop'

function Find-SignTool {
    $onPath = Get-Command signtool.exe -ErrorAction SilentlyContinue
    if ($onPath) { return $onPath.Source }

    # The Windows SDK does not put signtool on PATH; take the newest one installed.
    $roots = @(
        "${env:ProgramFiles(x86)}\Windows Kits\10\bin",
        "$env:ProgramFiles\Windows Kits\10\bin"
    ) | Where-Object { $_ -and (Test-Path $_) }

    foreach ($root in $roots) {
        $candidate = Get-ChildItem $root -Recurse -Filter signtool.exe -ErrorAction SilentlyContinue |
            Where-Object { $_.FullName -match '\\x64\\' } |
            Sort-Object FullName -Descending |
            Select-Object -First 1
        if ($candidate) { return $candidate.FullName }
    }

    return $null
}

function Resolve-Targets {
    $files = @()
    foreach ($item in $Path) {
        if (Test-Path $item -PathType Container) {
            foreach ($pattern in $Include) {
                $files += Get-ChildItem $item -Recurse -Filter $pattern -File -ErrorAction SilentlyContinue
            }
        }
        elseif (Test-Path $item -PathType Leaf) {
            $files += Get-Item $item
        }
        else {
            Write-Warning "not found, skipping: $item"
        }
    }
    return $files | Sort-Object FullName -Unique
}

$signtool = Find-SignTool
if (-not $signtool) {
    Write-Warning 'signtool.exe was not found. Install the Windows SDK, or run this on a windows-latest runner where it ships with the image.'
    exit 0
}

$targets = Resolve-Targets
if (-not $targets -or $targets.Count -eq 0) {
    Write-Warning "nothing to sign under: $($Path -join ', ')"
    exit 0
}

if ($VerifyOnly) {
    Write-Host "verifying $($targets.Count) file(s) with $signtool"
    $unsigned = 0
    foreach ($file in $targets) {
        & $signtool verify /pa /v $file.FullName *> $null
        if ($LASTEXITCODE -eq 0) {
            Write-Host ("  signed   {0}" -f $file.Name)
        }
        else {
            Write-Host ("  UNSIGNED {0}" -f $file.Name)
            $unsigned++
        }
    }
    Write-Host "$unsigned of $($targets.Count) are unsigned"
    exit 0
}

# --- signing -----------------------------------------------------------------
$signArguments = @('sign', '/fd', 'SHA256', '/tr', $TimestampUrl, '/td', 'SHA256', '/v')

if ($Thumbprint) {
    # /sha1 picks the certificate; /sm would look in the machine store instead of the user's.
    $signArguments += @('/sha1', ($Thumbprint -replace '\s', ''))
    Write-Host "signing with the certificate $Thumbprint from the store"
}
elseif ($PfxPath) {
    if (-not (Test-Path $PfxPath)) { throw "pfx not found: $PfxPath" }
    $signArguments += @('/f', (Resolve-Path $PfxPath).Path)
    if ($PfxPassword) { $signArguments += @('/p', $PfxPassword) }
    Write-Host "signing with the certificate file $PfxPath"
}
else {
    Write-Host 'No certificate supplied (-Thumbprint or -PfxPath), so nothing was signed.'
    Write-Host 'An unsigned build is still a valid build; this is the expected path for forks and pull requests.'
    exit 0
}

Write-Host ("timestamping via {0}" -f $TimestampUrl)
Write-Host ""

$failed = 0
foreach ($file in $targets) {
    Write-Host ("signing {0}" -f $file.Name)
    & $signtool @signArguments $file.FullName
    if ($LASTEXITCODE -ne 0) {
        Write-Warning ("signtool failed for {0} with exit code {1}" -f $file.Name, $LASTEXITCODE)
        $failed++
        continue
    }

    # A signature that cannot be verified is worse than none: it looks trustworthy and is not.
    & $signtool verify /pa $file.FullName *> $null
    if ($LASTEXITCODE -ne 0) {
        Write-Warning ("{0} did not verify after signing" -f $file.Name)
        $failed++
    }
}

Write-Host ""
if ($failed -gt 0) {
    throw "$failed file(s) could not be signed or verified"
}

Write-Host "signed and verified $($targets.Count) file(s)"
