<#
.SYNOPSIS
    Updates an installed YARG-fork ("Section FC") build from the fork's GitHub Releases.

.DESCRIPTION
    The fork ships as a bare .zip on GitHub Releases and is invisible to the YARC Launcher,
    so updating means download-and-unzip-over-the-old-folder by hand. This script does that
    dance safely:

        1. Lists https://api.github.com/repos/djrobson5/YARG-fork/releases and picks the
           newest "-sectionfc" release. (The releases are pre-releases, so /releases/latest
           returns nothing useful. And tags sort badly lexically -- "sectionfc.10" sorts
           before "sectionfc.9" -- so the trailing integer is parsed and compared as a
           number.)
        2. Works out which version is installed (see -CurrentVersion below).
        3. Downloads the Windows .zip asset to %LOCALAPPDATA%\YARG-fork-updates, checks the
           downloaded byte count against the asset's `size` field (the release publishes no
           checksum, so this is the only integrity signal available), and extracts it to a
           staging folder.
        4. Sanity-checks that YARG.exe and YARG_Data\ are at the staging root.
        5. Moves the current install's contents into <install>\..\backup\<old-tag>, copies
           staging over the install dir, and restores the backup if anything fails.
        6. Relaunches YARG.exe.

    The script never elevates. If the install directory is not writable by the current user
    (e.g. it was unzipped into C:\Program Files), it refuses to do anything and tells you to
    move the install somewhere under your profile. A self-updater that asks for admin is
    both a support liability and an antivirus red flag.

.PARAMETER InstallDir
    The folder containing YARG.exe. Defaults to the folder this script is sitting in, if
    that folder contains YARG.exe (i.e. drop this script next to YARG.exe and just run it).

.PARAMETER CurrentVersion
    The release tag that is currently installed, e.g. "v0.15.0-sectionfc.1". If omitted the
    script tries, in order:
        a) the .yarg-update-tag marker file this script writes after a successful update,
        b) the bundleVersion string baked into YARG_Data\globalgamemanagers -- CIBuild.cs sets
           PlayerSettings.bundleVersion to the release tag, so this is exactly what
           Application.version returns at runtime,
    and otherwise treats the install as an unknown version (which means an update is always
    offered).

    Two sources that look right but are not (both checked against v0.15.0-sectionfc.1):
    YARG.exe's version resource holds the Unity *editor* version, and version.txt holds a git
    description ("HEAD b4213 (51d52d8)") rather than the release tag -- and is bundled inside
    resources.assets, not shipped loose. See docs/release-build.md.

.PARAMETER CheckOnly
    Print the installed version and the latest release, then exit. Touches nothing.

.PARAMETER DryRun
    Do every read-only step (query, resolve install dir, writability probe) and print exactly
    what would happen, but download nothing and change nothing.

.PARAMETER Force
    Reinstall even when the installed version already matches the latest release. Also
    switches the "YARG is running" behaviour from "wait for it to exit" to "ask whether to
    kill it".

.PARAMETER NoLaunch
    Do not relaunch YARG.exe after a successful update.

.PARAMETER Repo
    owner/name of the GitHub repository to update from. Defaults to djrobson5/YARG-fork.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File .\update-yarg.ps1 -CheckOnly

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File .\update-yarg.ps1

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File .\update-yarg.ps1 -InstallDir "D:\Games\YARG-SectionFC" -NoLaunch

.NOTES
    Windows PowerShell 5.1 compatible (no ternary/null-coalescing operators).
    If Windows refuses to run the script, that is the machine's execution policy, not a
    problem with the file -- use the "powershell -ExecutionPolicy Bypass -File ..." form
    above rather than changing the machine-wide policy.
#>

[CmdletBinding()]
param(
    [string] $InstallDir,
    [string] $CurrentVersion,
    [switch] $CheckOnly,
    [switch] $DryRun,
    [switch] $Force,
    [switch] $NoLaunch,
    [string] $Repo = 'djrobson5/YARG-fork'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2.0

# ---------------------------------------------------------------------------------------
# Constants
# ---------------------------------------------------------------------------------------

# GitHub rejects unauthenticated API calls without a User-Agent.
$UserAgent = 'YARG-fork-updater'

# Only releases whose tag contains this are considered ours. Upstream-shaped tags, or any
# other release someone drops on the fork, are ignored.
$TagFilter = '-sectionfc'

# The workflow names the asset "YARG-SectionFC_<tag>-Windows-x64.zip"
# (.github/workflows/build-windows.yml, "[Setup] Resolve version" step).
$AssetPattern = '^YARG-SectionFC_.*-Windows-x64\.zip$'

# Marker file written into the install dir after a successful update, so the next run knows
# which tag is installed without having to trust the executable's version resource.
$MarkerFileName = '.yarg-update-tag'

$WorkRoot = Join-Path $env:LOCALAPPDATA 'YARG-fork-updates'

# ---------------------------------------------------------------------------------------
# Output helpers
# ---------------------------------------------------------------------------------------

function Write-Step { param([string] $Message) Write-Host "==> $Message" -ForegroundColor Cyan }
function Write-Info { param([string] $Message) Write-Host "    $Message" }
function Write-Ok   { param([string] $Message) Write-Host "    $Message" -ForegroundColor Green }
function Write-Warn { param([string] $Message) Write-Host "    $Message" -ForegroundColor Yellow }

# Terminates the script with a readable message instead of a PowerShell stack trace.
function Fail {
    param([string] $Message)
    Write-Host ""
    Write-Host "ERROR: $Message" -ForegroundColor Red
    Write-Host ""
    exit 1
}

# Anything that throws without going through Fail (an unforeseen IO error, a broken pipe)
# still ends up as a one-line message rather than a wall of red.
trap {
    Write-Host ""
    Write-Host "ERROR: $($_.Exception.Message)" -ForegroundColor Red
    Write-Host "  at $($_.InvocationInfo.ScriptLineNumber): $($_.InvocationInfo.Line.Trim())" -ForegroundColor DarkGray
    Write-Host ""
    exit 1
}

# ---------------------------------------------------------------------------------------
# Version helpers
# ---------------------------------------------------------------------------------------

# Pulls the trailing integer out of a tag: "v0.15.0-sectionfc.12" -> 12.
# Returns $null when the tag has no trailing integer, in which case the caller falls back to
# a plain string comparison.
function Get-TagOrdinal {
    param([string] $Tag)

    if ([string]::IsNullOrWhiteSpace($Tag)) { return $null }

    $m = [regex]::Match($Tag, '(\d+)\s*$')
    if (-not $m.Success) { return $null }

    $value = 0
    if ([int]::TryParse($m.Groups[1].Value, [ref] $value)) { return $value }
    return $null
}

function Test-LooksLikeForkTag {
    param([string] $Value)

    if ([string]::IsNullOrWhiteSpace($Value)) { return $false }
    return $Value -like "*$TagFilter*"
}

# ---------------------------------------------------------------------------------------
# GitHub
# ---------------------------------------------------------------------------------------

# Returns the newest "-sectionfc" release, chosen by the trailing integer of its tag rather
# than by GitHub's ordering or a lexical sort.
function Get-LatestForkRelease {
    param([string] $Repository)

    $url = "https://api.github.com/repos/$Repository/releases"
    Write-Info "GET $url"

    # TLS 1.2 is not the default on stock Windows PowerShell 5.1 and api.github.com requires it.
    try {
        [Net.ServicePointManager]::SecurityProtocol =
            [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
    } catch {
        # Older .NET without Tls12 in the enum -- nothing useful to do, let the call fail below.
    }

    try {
        $releases = Invoke-RestMethod -Uri $url -Headers @{ 'User-Agent' = $UserAgent } -TimeoutSec 30
    } catch {
        Fail ("Could not reach the GitHub API: " + $_.Exception.Message +
              "`n    (Unauthenticated API calls are rate limited to 60/hour per IP.)")
    }

    if ($null -eq $releases -or @($releases).Count -eq 0) {
        Fail "The GitHub API returned no releases for $Repository."
    }

    $candidates = @($releases | Where-Object { Test-LooksLikeForkTag $_.tag_name })
    if ($candidates.Count -eq 0) {
        Fail "No release on $Repository has a tag containing '$TagFilter'."
    }

    # Sort by the trailing integer descending; releases with no trailing integer sort last but
    # are still usable as a fallback.
    $ranked = $candidates |
        Sort-Object -Property `
            @{ Expression = { $ord = Get-TagOrdinal $_.tag_name; if ($null -eq $ord) { -1 } else { $ord } }; Descending = $true },
            @{ Expression = { $_.created_at }; Descending = $true }

    return @($ranked)[0]
}

function Get-WindowsAsset {
    param($Release)

    $assets = @()
    if ($Release.PSObject.Properties.Name -contains 'assets' -and $null -ne $Release.assets) {
        $assets = @($Release.assets)
    }

    $match = @($assets | Where-Object { $_.name -match $AssetPattern })
    if ($match.Count -eq 0) {
        # Be forgiving if the workflow's naming ever changes: any single .zip will do.
        $match = @($assets | Where-Object { $_.name -like '*.zip' })
    }

    if ($match.Count -eq 0) {
        Fail "Release $($Release.tag_name) has no Windows .zip asset."
    }

    return $match[0]
}

# ---------------------------------------------------------------------------------------
# Install directory
# ---------------------------------------------------------------------------------------

function Resolve-InstallDir {
    param([string] $Requested)

    if (-not [string]::IsNullOrWhiteSpace($Requested)) {
        if (-not (Test-Path -LiteralPath $Requested -PathType Container)) {
            Fail "-InstallDir '$Requested' does not exist."
        }
        $resolved = (Resolve-Path -LiteralPath $Requested).ProviderPath
        if (-not (Test-Path -LiteralPath (Join-Path $resolved 'YARG.exe') -PathType Leaf)) {
            Fail "-InstallDir '$resolved' does not contain YARG.exe."
        }
        return $resolved
    }

    # Default: the folder this script lives in, if that is an install.
    $scriptDir = $PSScriptRoot
    if ([string]::IsNullOrWhiteSpace($scriptDir)) {
        $scriptDir = Split-Path -Parent $MyInvocation.PSCommandPath
    }

    if (-not [string]::IsNullOrWhiteSpace($scriptDir) -and
        (Test-Path -LiteralPath (Join-Path $scriptDir 'YARG.exe') -PathType Leaf)) {
        return (Resolve-Path -LiteralPath $scriptDir).ProviderPath
    }

    Fail ("Could not work out where YARG is installed.`n" +
          "    This script defaults to its own folder, but '$scriptDir' contains no YARG.exe.`n" +
          "    Either copy update-yarg.ps1 next to YARG.exe, or pass the install folder:`n" +
          "        powershell -ExecutionPolicy Bypass -File .\update-yarg.ps1 -InstallDir ""D:\Games\YARG-SectionFC""")
}

# Works out which release tag is installed. Returns $null if it cannot be determined.
function Get-InstalledTag {
    param([string] $Dir)

    # a) The marker this script writes after a successful update. Most trustworthy, because
    #    we wrote it ourselves.
    $marker = Join-Path $Dir $MarkerFileName
    if (Test-Path -LiteralPath $marker -PathType Leaf) {
        $tag = (Get-Content -LiteralPath $marker -Raw -ErrorAction SilentlyContinue)
        if ($null -ne $tag) { $tag = $tag.Trim() }
        if (Test-LooksLikeForkTag $tag) {
            Write-Info "Installed version read from $MarkerFileName."
            return $tag
        }
    }

    # b) The bundleVersion baked into the player.
    #
    #    CIBuild.ApplyVersionOverride sets PlayerSettings.bundleVersion to the release tag, and
    #    Unity stores that string in YARG_Data\globalgamemanagers -- it is what
    #    Application.version returns at runtime. Verified against
    #    v0.15.0-sectionfc.1: the file (~300 KB) contains the literal "v0.15.0-sectionfc.1".
    #
    #    Note that neither of the two more obvious sources works:
    #      * YARG.exe's version resource carries the *Unity editor* version
    #        ("6000.3.5f2 (3fa8bc678cb0)" / "6000.3.5.4171964"), not bundleVersion.
    #      * Assets/Resources/version.txt holds a git description ("HEAD b4213 (51d52d8)"),
    #        not the release tag -- and it is bundled into resources.assets rather than
    #        shipped as a loose file, so there is nothing to read anyway.
    $ggm = Join-Path $Dir 'YARG_Data\globalgamemanagers'
    if (Test-Path -LiteralPath $ggm -PathType Leaf) {
        try {
            $bytes = [System.IO.File]::ReadAllBytes($ggm)
            $text = [System.Text.Encoding]::ASCII.GetString($bytes)
            # Bounded, and stops at the NUL that terminates the serialised string.
            $m = [regex]::Match($text, 'v[0-9][^\x00]{0,40}-sectionfc[^\x00]{0,20}')
            if ($m.Success) {
                Write-Info "Installed version read from YARG_Data\globalgamemanagers (Application.version)."
                return $m.Value.Trim()
            }
        } catch {
            # Unreadable or unexpected format is not fatal; fall through to "unknown".
        }
    }

    return $null
}

# Probes writability by actually creating and deleting a file, because ACL inspection lies
# (virtualisation, inherited denies, read-only media).
function Test-DirectoryWritable {
    param([string] $Dir)

    $probe = Join-Path $Dir ('.yarg-update-write-probe-' + [guid]::NewGuid().ToString('N'))
    try {
        [System.IO.File]::WriteAllText($probe, 'probe')
        Remove-Item -LiteralPath $probe -Force
        return $true
    } catch {
        if (Test-Path -LiteralPath $probe) {
            Remove-Item -LiteralPath $probe -Force -ErrorAction SilentlyContinue
        }
        return $false
    }
}

# ---------------------------------------------------------------------------------------
# Running-process handling
# ---------------------------------------------------------------------------------------

function Get-RunningYargProcesses {
    param([string] $Dir)

    $exe = [System.IO.Path]::Combine($Dir, 'YARG.exe')

    $procs = @()
    foreach ($p in @(Get-Process -Name 'YARG' -ErrorAction SilentlyContinue)) {
        $path = $null
        try { $path = $p.Path } catch { $path = $null }   # Access denied on other users' processes.
        if ($null -ne $path -and $path -ieq $exe) { $procs += $p }
    }
    return $procs
}

function Wait-ForYargToExit {
    param([string] $Dir)

    # @() because a function returning an empty array yields $null, and Set-StrictMode makes
    # .Count on $null a hard error.
    $procs = @(Get-RunningYargProcesses -Dir $Dir)
    if ($procs.Count -eq 0) { return }

    if ($Force) {
        Write-Warn "YARG is running from this install (PID $($procs.Id -join ', '))."
        $answer = Read-Host "    Close it now? Unsaved progress will be lost. [y/N]"
        if ($answer -match '^(y|yes)$') {
            foreach ($p in $procs) {
                try { $p.CloseMainWindow() | Out-Null } catch { }
            }
            # Give it a moment to shut down cleanly before insisting.
            foreach ($p in $procs) {
                if (-not $p.WaitForExit(10000)) {
                    Write-Warn "PID $($p.Id) did not close; terminating."
                    try { $p.Kill() } catch { }
                    $p.WaitForExit(10000) | Out-Null
                }
            }
            return
        }
    }

    Write-Warn "YARG is running from this install (PID $($procs.Id -join ', ')). Waiting for it to exit..."
    Write-Warn "Press Ctrl+C to abort."
    while (@(Get-RunningYargProcesses -Dir $Dir).Count -gt 0) {
        Start-Sleep -Seconds 2
    }
    Write-Ok "YARG has exited."
}

# ---------------------------------------------------------------------------------------
# Download / extract / stage
# ---------------------------------------------------------------------------------------

function Save-ReleaseAsset {
    param($Asset, [string] $Destination)

    # A previous run may already have fetched this exact asset. Trust it only if the byte count
    # matches, which is the same check a fresh download gets.
    if (Test-Path -LiteralPath $Destination -PathType Leaf) {
        if ((Get-Item -LiteralPath $Destination).Length -eq $Asset.size) {
            Write-Ok "Reusing the already-downloaded $($Asset.name) (size matches)."
            return
        }
        Write-Info "Discarding a partial/stale copy of $($Asset.name)."
        Remove-Item -LiteralPath $Destination -Force
    }

    Write-Info "Downloading $($Asset.name) ($([math]::Round($Asset.size / 1MB, 1)) MB)..."
    Write-Info "  from $($Asset.browser_download_url)"

    # Invoke-WebRequest streams to -OutFile. Its progress bar is very slow in PS 5.1 for large
    # files, so it is suppressed for the duration of the download.
    $previousProgress = $ProgressPreference
    $ProgressPreference = 'SilentlyContinue'
    try {
        Invoke-WebRequest -Uri $Asset.browser_download_url `
                          -Headers @{ 'User-Agent' = $UserAgent } `
                          -OutFile $Destination `
                          -UseBasicParsing `
                          -TimeoutSec 1800
    } catch {
        Fail "Download failed: $($_.Exception.Message)"
    } finally {
        $ProgressPreference = $previousProgress
    }

    # The release publishes no checksum, so the asset's declared size is the only integrity
    # signal there is. It at least catches a truncated download.
    $actual = (Get-Item -LiteralPath $Destination).Length
    if ($actual -ne $Asset.size) {
        Remove-Item -LiteralPath $Destination -Force -ErrorAction SilentlyContinue
        Fail "Downloaded $actual bytes but the release says the asset is $($Asset.size) bytes. Aborting."
    }

    Write-Ok "Downloaded $actual bytes; size matches the release metadata."
}

function Expand-ToStaging {
    param([string] $ZipPath, [string] $StagingDir)

    if (Test-Path -LiteralPath $StagingDir) {
        Remove-Item -LiteralPath $StagingDir -Recurse -Force
    }
    New-Item -ItemType Directory -Path $StagingDir -Force | Out-Null

    Write-Info "Extracting to $StagingDir ..."
    # [IO.Compression.ZipFile] rather than Expand-Archive: the latter is dramatically slower on
    # an archive with this many files.
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    try {
        [System.IO.Compression.ZipFile]::ExtractToDirectory($ZipPath, $StagingDir)
    } catch {
        Fail "Extraction failed (the download may be corrupt): $($_.Exception.Message)"
    }

    # The workflow zips from inside build/StandaloneWindows64, so YARG.exe and YARG_Data are at
    # the archive root. If that ever changes, catch it here rather than after clobbering the
    # install.
    $exe = Join-Path $StagingDir 'YARG.exe'
    $data = Join-Path $StagingDir 'YARG_Data'
    if (-not (Test-Path -LiteralPath $exe -PathType Leaf)) {
        Fail "Staged build has no YARG.exe at its root ($StagingDir). Refusing to install it."
    }
    if (-not (Test-Path -LiteralPath $data -PathType Container)) {
        Fail "Staged build has no YARG_Data folder at its root ($StagingDir). Refusing to install it."
    }

    Write-Ok "Staging tree looks like a YARG build (YARG.exe + YARG_Data present)."
}

# ---------------------------------------------------------------------------------------
# Apply
# ---------------------------------------------------------------------------------------

# Moves everything in $InstallPath into $BackupPath. Returns the list of moved leaf names so a
# restore can put them back.
function Move-InstallToBackup {
    param([string] $InstallPath, [string] $BackupPath)

    if (Test-Path -LiteralPath $BackupPath) {
        Remove-Item -LiteralPath $BackupPath -Recurse -Force
    }
    New-Item -ItemType Directory -Path $BackupPath -Force | Out-Null

    $moved = @()
    foreach ($item in @(Get-ChildItem -LiteralPath $InstallPath -Force)) {
        # Never move this script out from under itself if it is running from the install dir.
        if ($item.Name -ieq 'update-yarg.ps1') { continue }

        Move-Item -LiteralPath $item.FullName -Destination (Join-Path $BackupPath $item.Name) -Force
        $moved += $item.Name
    }
    return $moved
}

function Restore-Backup {
    param([string] $InstallPath, [string] $BackupPath)

    Write-Warn "Restoring the previous install from $BackupPath ..."

    # Clear whatever half-copied mess is in the install dir first.
    foreach ($item in @(Get-ChildItem -LiteralPath $InstallPath -Force)) {
        if ($item.Name -ieq 'update-yarg.ps1') { continue }
        Remove-Item -LiteralPath $item.FullName -Recurse -Force -ErrorAction SilentlyContinue
    }

    foreach ($item in @(Get-ChildItem -LiteralPath $BackupPath -Force)) {
        Move-Item -LiteralPath $item.FullName -Destination (Join-Path $InstallPath $item.Name) -Force
    }

    Remove-Item -LiteralPath $BackupPath -Recurse -Force -ErrorAction SilentlyContinue
    Write-Ok "Previous install restored. Nothing was lost."
}

function Copy-StagingOverInstall {
    param([string] $StagingDir, [string] $InstallPath)

    foreach ($item in @(Get-ChildItem -LiteralPath $StagingDir -Force)) {
        Copy-Item -LiteralPath $item.FullName -Destination $InstallPath -Recurse -Force
    }
}

# ---------------------------------------------------------------------------------------
# Main
# ---------------------------------------------------------------------------------------

Write-Host ""
Write-Host "YARG fork updater ($Repo)" -ForegroundColor White
Write-Host ""

Write-Step "Checking GitHub for the latest -sectionfc release"
$release = Get-LatestForkRelease -Repository $Repo
$latestTag = $release.tag_name
$asset = Get-WindowsAsset -Release $release
Write-Ok "Latest release: $latestTag  ($($asset.name), $($asset.size) bytes)"
Write-Info "Release page: $($release.html_url)"

Write-Step "Locating the install"
$installPath = Resolve-InstallDir -Requested $InstallDir
Write-Ok "Install directory: $installPath"

$installedTag = $CurrentVersion
if ([string]::IsNullOrWhiteSpace($installedTag)) {
    $installedTag = Get-InstalledTag -Dir $installPath
} else {
    Write-Info "Installed version supplied with -CurrentVersion."
}

if ([string]::IsNullOrWhiteSpace($installedTag)) {
    Write-Warn "Installed version: unknown (no $MarkerFileName, and YARG.exe carries no -sectionfc version)."
    Write-Warn "An update will be offered. Pass -CurrentVersion if you know which build this is."
} else {
    Write-Ok "Installed version: $installedTag"
}

# Compare by the trailing integer where possible, falling back to a string comparison.
$installedOrdinal = Get-TagOrdinal $installedTag
$latestOrdinal = Get-TagOrdinal $latestTag

$upToDate = $false
if (-not [string]::IsNullOrWhiteSpace($installedTag)) {
    if ($null -ne $installedOrdinal -and $null -ne $latestOrdinal) {
        $upToDate = ($installedOrdinal -ge $latestOrdinal)
    } else {
        $upToDate = ($installedTag -eq $latestTag)
    }
}

Write-Host ""
if ($upToDate) {
    if ($installedTag -eq $latestTag) {
        Write-Host "You are up to date ($installedTag)." -ForegroundColor Green
    } else {
        Write-Host "Installed build ($installedTag) is newer than the latest release ($latestTag)." -ForegroundColor Green
    }
} else {
    $fromLabel = $installedTag
    if ([string]::IsNullOrWhiteSpace($fromLabel)) { $fromLabel = '(unknown)' }
    Write-Host "An update is available: $fromLabel -> $latestTag" -ForegroundColor Yellow
}
Write-Host ""

if ($CheckOnly) {
    Write-Info "-CheckOnly: nothing else to do."
    exit 0
}

if ($upToDate -and -not $Force) {
    Write-Info "Nothing to do. Pass -Force to reinstall anyway."
    exit 0
}

Write-Step "Checking the install directory is writable"
if (-not (Test-DirectoryWritable -Dir $installPath)) {
    Fail ("'$installPath' is not writable by the current user.`n" +
          "    This updater deliberately never asks for administrator rights.`n" +
          "    Move the install somewhere under your own profile (e.g. %LOCALAPPDATA%\YARG-SectionFC)`n" +
          "    and run this script again.")
}
Write-Ok "Writable."

# Backups live beside the install rather than inside it, so the copy step can wipe the install
# dir without touching them.
$backupRoot = Join-Path (Split-Path -Parent $installPath) 'backup'
$backupLabel = $installedTag
if ([string]::IsNullOrWhiteSpace($backupLabel)) { $backupLabel = 'unknown' }
$backupLabel = ($backupLabel -replace '[\\/:*?"<>|]', '_')
$backupPath = Join-Path $backupRoot $backupLabel

$zipPath = Join-Path $WorkRoot $asset.name
$stagingPath = Join-Path (Join-Path $WorkRoot 'staging') $latestTag

if ($DryRun) {
    Write-Step "Dry run -- nothing will be downloaded or changed"
    Write-Info "Would download : $($asset.browser_download_url)"
    Write-Info "             to : $zipPath"
    Write-Info "Would stage to  : $stagingPath"
    Write-Info "Would back up   : $installPath -> $backupPath"
    Write-Info "Would install   : $stagingPath -> $installPath"
    if ($NoLaunch) {
        Write-Info "Would not relaunch (-NoLaunch)."
    } else {
        Write-Info "Would relaunch  : $(Join-Path $installPath 'YARG.exe')"
    }
    exit 0
}

Write-Step "Downloading"
New-Item -ItemType Directory -Path $WorkRoot -Force | Out-Null
Save-ReleaseAsset -Asset $asset -Destination $zipPath

Write-Step "Staging"
Expand-ToStaging -ZipPath $zipPath -StagingDir $stagingPath

Write-Step "Making sure YARG is not running"
Wait-ForYargToExit -Dir $installPath

Write-Step "Backing up the current install"
New-Item -ItemType Directory -Path $backupRoot -Force | Out-Null
$movedNames = Move-InstallToBackup -InstallPath $installPath -BackupPath $backupPath
Write-Ok "Moved $($movedNames.Count) item(s) to $backupPath"

Write-Step "Installing $latestTag"
try {
    Copy-StagingOverInstall -StagingDir $stagingPath -InstallPath $installPath

    # Re-verify at the destination -- a copy that half-succeeded and did not throw would
    # otherwise go unnoticed.
    if (-not (Test-Path -LiteralPath (Join-Path $installPath 'YARG.exe') -PathType Leaf)) {
        throw "YARG.exe is missing from the install directory after the copy."
    }
    if (-not (Test-Path -LiteralPath (Join-Path $installPath 'YARG_Data') -PathType Container)) {
        throw "YARG_Data is missing from the install directory after the copy."
    }
} catch {
    Write-Warn "Install failed: $($_.Exception.Message)"
    try {
        Restore-Backup -InstallPath $installPath -BackupPath $backupPath
    } catch {
        Write-Warn "RESTORE ALSO FAILED: $($_.Exception.Message)"
        Write-Warn "Your previous install is still intact at: $backupPath"
        Write-Warn "Copy its contents back into $installPath by hand."
    }
    Fail "Update aborted; the install was rolled back."
}

# Record what we just installed so the next run knows, regardless of what the .exe says.
Set-Content -LiteralPath (Join-Path $installPath $MarkerFileName) -Value $latestTag -Encoding ASCII
Write-Ok "Installed $latestTag."
Write-Info "Previous version kept at $backupPath -- delete it once the new build has launched cleanly."

# The staged copy has served its purpose; the .zip is kept so a re-run does not re-download.
Remove-Item -LiteralPath $stagingPath -Recurse -Force -ErrorAction SilentlyContinue

if ($NoLaunch) {
    Write-Host ""
    Write-Info "-NoLaunch: not starting YARG."
} else {
    Write-Step "Relaunching YARG"
    Start-Process -FilePath (Join-Path $installPath 'YARG.exe') -WorkingDirectory $installPath
}

Write-Host ""
Write-Host "Done." -ForegroundColor Green
exit 0
