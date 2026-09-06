<#
.SYNOPSIS
    Import YARG score history (and profiles) from another machine's official
    install into this fork's local data folders.

.DESCRIPTION
    Copies scores\scores.db and profiles\profiles.json (and optionally
    scores\replays\) from a source folder -- either a copy of the official
    <release|nightly> data folder, or a zip of it -- into one or more of this
    fork's local target folders (nightly, dev). This is a straight overwrite:
    the source profiles.json REPLACES the target's profiles.json, and
    scores.db is replaced wholesale (not merged). Existing target files are
    backed up first.

    See docs/roadmap.md "Feature 1 -- Import scores" for the full design
    rationale (why profiles.json has to travel with scores.db, why a plain
    copy is sufficient, what is and isn't imported).

.PARAMETER Source
    Path to the folder copied from the other machine (containing
    scores\scores.db and profiles\profiles.json), or a .zip of that folder.

.PARAMETER Targets
    Which local data folders to import into. Defaults to both 'nightly' and
    'dev'. Maps to <DataRoot>\<target>.

.PARAMETER IncludeReplays
    Also copy scores\replays\ from the source. Off by default -- replay
    folders can be hundreds of megabytes.

.PARAMETER DryRun
    Print the plan (what would be copied, where, and what would be backed
    up) without writing anything.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File tools\import-scores.ps1 -Source C:\Temp\other-machine-nightly

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File tools\import-scores.ps1 -Source C:\Temp\nightly.zip -IncludeReplays

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File tools\import-scores.ps1 -Source C:\Temp\other-machine-nightly -Targets dev -DryRun
#>

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$Source,

    [string[]]$Targets = @('nightly', 'dev'),

    [switch]$IncludeReplays,

    [switch]$DryRun,

    # Hidden/test hook: overrides the root that <target> folders live under.
    # Defaults to the real YARG data root. Point this at a scratch folder to
    # test the script without touching real data.
    [string]$DataRoot = "$env:USERPROFILE\AppData\LocalLow\YARC\YARG"
)

$ErrorActionPreference = 'Stop'

function Write-Section($text) {
    Write-Host ''
    Write-Host "== $text ==" -ForegroundColor Cyan
}

function Write-Warn2($text) {
    Write-Host "WARNING: $text" -ForegroundColor Yellow
}

# ---------------------------------------------------------------------------
# 1. Refuse to run while YARG.exe is up -- writing under a live game folder
#    is how you corrupt scores.db mid-write.
# ---------------------------------------------------------------------------
$running = Get-Process -Name 'YARG' -ErrorAction SilentlyContinue
if ($running) {
    Write-Host "YARG.exe is currently running (PID $($running.Id -join ', ')). Close it before importing." -ForegroundColor Red
    exit 1
}

# ---------------------------------------------------------------------------
# 2. Resolve the source: a folder, or a zip to extract first.
# ---------------------------------------------------------------------------
if (-not (Test-Path -LiteralPath $Source)) {
    Write-Host "Source path not found: $Source" -ForegroundColor Red
    exit 1
}

$sourceItem = Get-Item -LiteralPath $Source
$sourceFolder = $null

if ($sourceItem.PSIsContainer) {
    $sourceFolder = $sourceItem.FullName
}
elseif ($sourceItem.Extension -ieq '.zip') {
    $extractRoot = Join-Path $env:LOCALAPPDATA 'YARG-fork-updates\import'
    $extractDir = Join-Path $extractRoot ("$([System.IO.Path]::GetFileNameWithoutExtension($sourceItem.Name))-$(Get-Date -Format 'yyyyMMdd-HHmmss')")

    Write-Section "Extracting zip"
    Write-Host "Source zip: $($sourceItem.FullName)"
    Write-Host "Extract to: $extractDir"

    if (-not $DryRun) {
        New-Item -ItemType Directory -Force -Path $extractDir | Out-Null
        Expand-Archive -LiteralPath $sourceItem.FullName -DestinationPath $extractDir -Force
    }

    $sourceFolder = $extractDir
}
else {
    Write-Host "Source must be a folder or a .zip file. Got: $($sourceItem.FullName)" -ForegroundColor Red
    exit 1
}

# When doing a dry run against a zip, there's nothing extracted to inspect
# yet, so we can't validate its contents further. Say so and stop cleanly.
if ($DryRun -and $sourceItem.Extension -ieq '.zip') {
    Write-Section "Dry run"
    Write-Host "Would extract '$($sourceItem.FullName)' to '$sourceFolder', then locate scores\scores.db and profiles\profiles.json inside it."
    Write-Host "Re-run without -DryRun (or point -Source at an already-extracted folder) to see the full plan."
    exit 0
}

# ---------------------------------------------------------------------------
# 3. Locate the files we care about inside the source folder.
#
#    Accept either the <release|nightly> folder itself, or a parent that
#    directly contains scores\ and profiles\ -- we look for scores.db
#    anywhere at depth <= 2 under the given folder so both
#    "<source>\scores\scores.db" and a zip that unpacked with an extra
#    top-level directory both work.
# ---------------------------------------------------------------------------
function Find-DataFile($root, $relativeName) {
    $direct = Join-Path $root $relativeName
    if (Test-Path -LiteralPath $direct) {
        return (Get-Item -LiteralPath $direct).FullName
    }
    # Search one level deeper (handles a zip that contains a single wrapping folder).
    $found = Get-ChildItem -LiteralPath $root -Directory -ErrorAction SilentlyContinue |
        ForEach-Object {
            $candidate = Join-Path $_.FullName $relativeName
            if (Test-Path -LiteralPath $candidate) { $candidate }
        } | Select-Object -First 1
    return $found
}

$scoresDbPath = Find-DataFile -root $sourceFolder -relativeName 'scores\scores.db'
$profilesJsonPath = Find-DataFile -root $sourceFolder -relativeName 'profiles\profiles.json'
$replaysDirPath = Find-DataFile -root $sourceFolder -relativeName 'scores\replays'

Write-Section "Source contents"
Write-Host "Source folder : $sourceFolder"

if (-not $scoresDbPath) {
    Write-Host "Could not find scores\scores.db under the source folder. Nothing to import." -ForegroundColor Red
    exit 1
}
Write-Host "scores.db     : $scoresDbPath"

if (-not $profilesJsonPath) {
    Write-Warn2 "profiles\profiles.json was NOT found in the source."
    Write-Warn2 "Without it, imported scores keep the source machine's player GUIDs, which won't match any local profile."
    Write-Warn2 "Result: the scores will appear in the history menu but NOT on the music library rows/high-score pill for any local profile."
    Write-Warn2 "See docs/roadmap.md 'Feature 1 -- Import scores' for details."
}
else {
    Write-Host "profiles.json : $profilesJsonPath"
}

if ($IncludeReplays) {
    if ($replaysDirPath) {
        $replayCount = (Get-ChildItem -LiteralPath $replaysDirPath -File -Recurse -ErrorAction SilentlyContinue).Count
        Write-Host "replays\      : $replaysDirPath ($replayCount files)"
    }
    else {
        Write-Warn2 "-IncludeReplays was passed but scores\replays\ was not found in the source. Skipping replays."
    }
}

# ---------------------------------------------------------------------------
# 4. Build the target list and validate/prepare each one.
# ---------------------------------------------------------------------------
Write-Section "Targets"

$plan = @()

foreach ($target in $Targets) {
    $targetRoot = Join-Path $DataRoot $target
    $targetScoresDir = Join-Path $targetRoot 'scores'
    $targetProfilesDir = Join-Path $targetRoot 'profiles'
    $targetScoresDb = Join-Path $targetScoresDir 'scores.db'
    $targetProfilesJson = Join-Path $targetProfilesDir 'profiles.json'
    $targetReplaysDir = Join-Path $targetScoresDir 'replays'

    $existingScoresDb = Test-Path -LiteralPath $targetScoresDb
    $existingProfilesJson = Test-Path -LiteralPath $targetProfilesJson
    $existingReplays = ($IncludeReplays -and (Test-Path -LiteralPath $targetReplaysDir))

    $plan += [PSCustomObject]@{
        Target              = $target
        TargetRoot          = $targetRoot
        TargetScoresDir     = $targetScoresDir
        TargetProfilesDir   = $targetProfilesDir
        TargetScoresDb      = $targetScoresDb
        TargetProfilesJson  = $targetProfilesJson
        TargetReplaysDir    = $targetReplaysDir
        HadExistingScoresDb = $existingScoresDb
        HadExistingProfiles = $existingProfilesJson
        HadExistingReplays  = $existingReplays
    }

    Write-Host "$target -> $targetRoot"
    Write-Host "    existing scores.db     : $existingScoresDb"
    Write-Host "    existing profiles.json : $existingProfilesJson"
    if ($IncludeReplays) {
        Write-Host "    existing replays\       : $existingReplays"
    }
}

# ---------------------------------------------------------------------------
# 5. Dry run: print the plan and stop.
# ---------------------------------------------------------------------------
if ($DryRun) {
    Write-Section "Dry run -- no files will be written"
    foreach ($t in $plan) {
        Write-Host ''
        Write-Host "Target: $($t.Target) ($($t.TargetRoot))"
        Write-Host "  Would ensure folders exist: $($t.TargetScoresDir), $($t.TargetProfilesDir)"
        if ($t.HadExistingScoresDb -or $t.HadExistingProfiles -or $t.HadExistingReplays) {
            Write-Host "  Would back up existing files to: $($t.TargetRoot)\import-backup-<timestamp>\"
        }
        Write-Host "  Would copy: $scoresDbPath -> $($t.TargetScoresDb)"
        if ($profilesJsonPath) {
            Write-Host "  Would copy: $profilesJsonPath -> $($t.TargetProfilesJson) (REPLACES local profiles.json)"
        }
        if ($IncludeReplays -and $replaysDirPath) {
            Write-Host "  Would copy: $replaysDirPath -> $($t.TargetReplaysDir)"
        }
    }
    Write-Host ''
    Write-Host "Dry run complete. Re-run without -DryRun to perform the import." -ForegroundColor Cyan
    exit 0
}

# ---------------------------------------------------------------------------
# 6. Real run: back up, then copy, per target.
# ---------------------------------------------------------------------------
$summary = @()

foreach ($t in $plan) {
    Write-Section "Importing into $($t.Target)"

    New-Item -ItemType Directory -Force -Path $t.TargetScoresDir | Out-Null
    New-Item -ItemType Directory -Force -Path $t.TargetProfilesDir | Out-Null

    $backupDir = $null
    $needsBackup = $t.HadExistingScoresDb -or $t.HadExistingProfiles -or $t.HadExistingReplays
    if ($needsBackup) {
        $backupDir = Join-Path $t.TargetRoot "import-backup-$(Get-Date -Format 'yyyyMMdd-HHmmss')"
        New-Item -ItemType Directory -Force -Path $backupDir | Out-Null

        if ($t.HadExistingScoresDb) {
            $backupScoresDir = Join-Path $backupDir 'scores'
            New-Item -ItemType Directory -Force -Path $backupScoresDir | Out-Null
            Copy-Item -LiteralPath $t.TargetScoresDb -Destination (Join-Path $backupScoresDir 'scores.db') -Force
        }
        if ($t.HadExistingProfiles) {
            $backupProfilesDir = Join-Path $backupDir 'profiles'
            New-Item -ItemType Directory -Force -Path $backupProfilesDir | Out-Null
            Copy-Item -LiteralPath $t.TargetProfilesJson -Destination (Join-Path $backupProfilesDir 'profiles.json') -Force
        }
        if ($t.HadExistingReplays) {
            $backupScoresDir = Join-Path $backupDir 'scores'
            New-Item -ItemType Directory -Force -Path $backupScoresDir | Out-Null
            Copy-Item -LiteralPath $t.TargetReplaysDir -Destination (Join-Path $backupScoresDir 'replays') -Recurse -Force
        }

        Write-Host "Backed up existing files to: $backupDir"
    }
    else {
        Write-Host "No existing scores.db / profiles.json / replays found -- nothing to back up."
    }

    Copy-Item -LiteralPath $scoresDbPath -Destination $t.TargetScoresDb -Force
    $copiedProfiles = $false
    if ($profilesJsonPath) {
        Copy-Item -LiteralPath $profilesJsonPath -Destination $t.TargetProfilesJson -Force
        $copiedProfiles = $true
    }

    $copiedReplays = $false
    if ($IncludeReplays -and $replaysDirPath) {
        if (Test-Path -LiteralPath $t.TargetReplaysDir) {
            Remove-Item -LiteralPath $t.TargetReplaysDir -Recurse -Force
        }
        Copy-Item -LiteralPath $replaysDirPath -Destination $t.TargetReplaysDir -Recurse -Force
        $copiedReplays = $true
    }

    $summary += [PSCustomObject]@{
        Target        = $t.Target
        ScoresDb      = 'copied'
        ProfilesJson  = if ($copiedProfiles) { 'copied (replaced local)' } else { 'SKIPPED (not in source)' }
        Replays       = if ($IncludeReplays) { if ($copiedReplays) { 'copied' } else { 'SKIPPED (not in source)' } } else { 'not requested' }
        BackupDir     = if ($backupDir) { $backupDir } else { '(none needed)' }
    }
}

# ---------------------------------------------------------------------------
# 7. Summary.
# ---------------------------------------------------------------------------
Write-Section "Summary"
$summary | Format-Table -AutoSize -Wrap

if (-not $profilesJsonPath) {
    Write-Host ''
    Write-Warn2 "Reminder: no profiles.json was imported. Imported scores may not appear in the music library until profiles are reconciled."
}

Write-Host ''
Write-Host "Done." -ForegroundColor Green
