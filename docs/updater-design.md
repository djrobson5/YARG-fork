# In-game updater

## What it is

The fork ships as a bare `.zip` on GitHub Releases and is invisible to the YARC Launcher.
The updater lets a running build notice that a newer `-sectionfc` release exists, tell the
user about it, download it and apply it.

Background research, the API shape, and the risk list live in
`docs/roadmap.md` → "Feature 4 — In-game updater from the fork's GitHub Releases".
This document records only the decisions that are now locked, and the plan they imply.

## Decisions (locked 2026-09-03, after a UI mockup review)

| Question | Decision |
|---|---|
| Trigger | **Manual only.** A "Check for Updates" button in Settings. No check on launch. An automatic check may come later behind a toggle (slice 5). |
| Placement | Settings → **General** tab, in a new `Updates` section header at the **top** of the tab, above `Calibration`. The section contains the single button row and nothing else. |
| Button wording | "Check for Updates". |
| Dialog titles | "Up to Date", "Update Available", "Could Not Check for Updates". |
| Field labels | "Installed" and "Latest". |
| Dialog buttons | `DialogManager.ShowDialog` already force-adds a red "Close" button first (`Assets/Script/Menu/Persistent/DialogManager.cs:248`), so every dialog gets one for free. The Update Available dialog adds one more button, **"Open Release Page"**, which calls `Application.OpenURL(html_url)` on the release. |
| Build description | Up to Date and the error dialogs also show `GlobalVariables.Instance.CurrentVersion` (the git description, e.g. `HEAD b4213 (51d52d8)`) on a line under the tag, because when there is no update the exact build is the useful fact. Update Available shows only the two tags — the git description is noise next to a concrete "you are on X, Y exists". |
| Version compared | `Application.version` against the release `tag_name`. **Not** `CurrentVersion`, which is a git description and is useless to compare (`roadmap.md`, Feature 4, "What exists today"). |
| Which releases count | Only releases whose `tag_name` matches the `-sectionfc` pattern. Everything else on the repo is ignored. |
| Picking "latest" | Parse the **trailing integer** after `-sectionfc.` and take the numeric maximum. Not lexical sort (`...sectionfc.10` sorts before `...sectionfc.9`) and not blind trust in GitHub's ordering. |
| Retries | **Never retry.** One request per button press. |
| Caching | An answer GitHub actually gave (up to date, update available, no releases) is cached for the process lifetime; a second press re-shows it without a second request. `Failed` and `RateLimited` are **not** cached, so a later press can succeed once the connection returns or the rate-limit window resets. A request already in flight is shared, so a second press joins it rather than opening a second connection. |
| Row visibility | The whole `Updates` section (header **and** button row) is hidden when `Application.version` is not a release tag — i.e. the editor and any non-CI build, where there is nothing meaningful to compare — and when `GlobalVariables.OfflineMode` is set (matching how `SongSources.LoadSources()` suppresses its own fetch). |
| Rate limiting | HTTP 403 and 429 get their own message ("GitHub is rate limiting… try again in an hour"). A 200 with no `-sectionfc` release gets its own too ("No fork releases were found on GitHub."), since that is not a network problem. Every other failure gets one generic could-not-check message. |
| Failure visibility | Failures are **shown**, not swallowed. This is the opposite of the `SongSources` precedent, and deliberately so: the user pressed a button and is owed an answer. |
| Backup retention | When the apply step lands (slice 4), the previous install is kept in a backup folder **until the next update replaces it** — not deleted after one successful launch. |
| Downgrade | Not offered, ever. The score DB is forward-only (see Feature 1 in the roadmap). |

### Slice 3 decisions (2026-09-03)

| Question | Decision |
|---|---|
| Progress UI | **No new prefab.** `DialogManager.ShowMessage` returns the `MessageDialog`, and `MessageDialog.Message` is a public `TextMeshProUGUI` — so the progress readout is that one text field rewritten in place from the `IProgress<float>` callback (which UniTask raises on the player loop, so touching TMP from it is safe). A dedicated progress dialog prefab is not worth a scene change for one screen. |
| Cancelling | The forced red **Close** button is the cancel affordance. The progress callback checks `dialog == null \|\| !dialog.IsOpen` each frame and cancels the `CancellationTokenSource` when the dialog has gone; UniTask's cancellation path calls `UnityWebRequest.Abort()`, and `removeFileOnAbort` deletes the partial file. A cancel shows **no** dialog — the user closed one, they do not want another. |
| Cancelling *during extraction* | `ZipFile.ExtractToDirectory` cannot be interrupted, so the token is **not** passed to `RunOnThreadPool`. The main thread instead polls (`await UniTask.Yield()`) while the extraction task is pending and returns `Cancelled` the moment the token trips; the extraction is left to run to completion in the background and its output folder is deleted once it lets go of the files. So a cancel is immediate from the user's side but the disk work is not actually stopped. |
| Concurrency | One download at a time. `UpdateDownloader` keeps a `_inFlight` preserved task, mirroring `UpdateChecker`; a second `DownloadAndStage` joins it (ignoring its own progress/token), and the Settings button early-returns on `UpdateDownloader.IsDownloading` so two progress callbacks never fight over one message field. |
| "Extracting" wording | The same `IProgress<float>`, reported as exactly `1` before extraction starts. The UI switches wording at 100% rather than needing a second callback or a phase enum. |
| Download timeout | **None.** `UnityWebRequest.timeout` is a wall clock on the whole transfer rather than an idle timeout, so any finite value fails a slow connection on a 130 MB asset. Slice 2's 10-second timeout stays on the (tiny) API call. |
| Extraction threading | On a thread pool thread via `UniTask.RunOnThreadPool`. Extracting ~130 MB on the main thread would freeze the menu for seconds and look like a hang. |
| Reusing a download | A `.zip` already in `updates/` is reused when its length matches the release's `size` — the same check a fresh download gets — and deleted otherwise. Copied from `Save-ReleaseAsset` in `tools/update-yarg.ps1`. The `.zip` is kept after staging so a repeat press does not re-download. |
| Asset selection | `^YARG-SectionFC_.*-Windows-x64\.zip$`, falling back to the release's **single** `.zip` if there is exactly one. The PowerShell script's fallback takes the first of any number of zips; requiring exactly one is stricter and avoids silently grabbing a macOS build. |
| Non-Windows | The asset fields are only populated under `#if UNITY_STANDALONE_WIN`, so the Download Update button never appears elsewhere and the dialog degrades to Open Release Page — matching how slice 4's apply step will be gated. |
| Where "installing isn't available yet" is said | On the **Update Ready** dialog, in plain wording pointing at `tools\update-yarg.ps1` and the release page. There is no point staging a build and saying nothing about what to do with it. |

### Slice 4 decisions (2026-09-04)

| Question | Decision |
|---|---|
| Where the helper script lives | A **C# `const` string** in `UpdateInstaller`, not a StreamingAssets file. It is one screen long, it is only meaningful next to the C# that fills in its paths, and as a constant it cannot go missing from — or be edited inside — a shipped install. |
| Helper's own location on disk | `<PersistentDataPath>/updates/apply-<tag>.cmd`, i.e. **outside the install directory**, so the step that moves the install cannot move the running script. Its working directory is the updates folder for the same reason: a working directory inside the install would hold a handle on the folder being moved. |
| Waiting for the game | Poll `tasklist /FI "PID eq <pid>"` once a second for **two minutes**, then give up **without touching anything**. |
| Sleep primitive | `ping -n 2 127.0.0.1`, not `timeout`. `timeout` refuses to run when there is no console to read from, and the helper is started with `CreateNoWindow`. |
| External tools | `tasklist`, `find`, `ping`, `xcopy` and `robocopy` are called by **absolute path** (`%SystemRoot%\System32\…`). A `find` earlier on `PATH` (git's, for one) has different exit codes, and mistaking "still running" for "exited" would move an install out from under a live game. This was caught in testing, not theory. |
| Backup location and retention | `<install>/../backup/<old-tag>`, a **sibling** of the install so replacing the install cannot touch it. Exactly one backup: the helper deletes the whole `backup` folder before creating the new one. |
| Old tag | `Application.version` — the release tag in a CI build, which is exactly what `tools/update-yarg.ps1` reads back out of `YARG_Data\globalgamemanagers`. Sanitized to one path component; `unknown` when empty. |
| Rollback | If the copy fails or the copied tree has no `YARG.exe`/`YARG_Data`, the helper moves the backup's contents back, deletes the backup folder and relaunches the old build. Same guarantee as the PowerShell script's `Restore-Backup`. |
| Marker file | The helper writes `.yarg-update-tag` into the install, the same marker `tools/update-yarg.ps1` writes and reads, so the two updaters agree about what is installed. |
| Staged tree vs the `.zip` | Slice 3 leaves an **already-extracted tree** at `staging/<tag>` and keeps the `.zip` beside it in `updates/`. The apply step installs from the extracted tree and never re-reads the archive; it deletes `staging/<tag>` on success and leaves the `.zip` alone, so a re-run does not re-download 130 MB. |
| Writability | Probed at the moment the button is pressed by creating and deleting `.yarg-update-write-probe-<guid>` in the install directory — ACL inspection lies (virtualisation, inherited denies, read-only media). A failure shows the "move your install" dialog and **nothing is written**. **The updater never elevates.** |
| Where the button lives | On the **Update Ready** dialog, as a third button: `[Close] [Open Release Page] [Install and Restart]`. It only appears when `UpdateInstaller.IsSupported` — Windows, and **not** the editor, where `PathHelper.ExecutablePath` is the Unity project folder. Otherwise the dialog keeps slice 3's wording pointing at `tools\update-yarg.ps1`. |
| Dialog before quitting | Pressing Install and Restart shows a short **Installing Update** dialog, yields a frame so it actually paints, then starts the helper and calls `Application.Quit()`. There is no second confirmation: the Update Ready dialog already says exactly what the button will do. |
| If the helper cannot start | The game does **not** quit. The dialog is replaced with "Could Not Install", and nothing on disk has been touched. |

## UI

Settings → General, at the very top:

```
Updates
[ Check for Updates ]

Calibration
...
```

Dialogs (the leading red Close button is implicit in all three):

**Up to Date**
```
Installed   v0.15.0-sectionfc.1
            HEAD b4213 (51d52d8)

You are running the latest release.
                                    [Close]
```

**Update Available**
```
Installed   v0.15.0-sectionfc.1
Latest      v0.15.0-sectionfc.2

 [Close] [Open Release Page] [Download Update]
```

`Download Update` is Windows-only and only appears when the release actually carries a
downloadable asset. Pressing it replaces this dialog with the progress one below.

**Downloading Update** (message text rewritten in place, no new prefab)
```
Downloading v0.15.0-sectionfc.2… 45%
                                    [Close]
```
then, once the bytes are down and verified:
```
Downloaded v0.15.0-sectionfc.2.

Extracting…
                                    [Close]
```

Closing this dialog cancels the download.

**Update Ready**
```
The update has been downloaded and verified.

Install and Restart will close YARG, replace this
install with v0.15.0-sectionfc.2 and reopen it.
Your current build is kept in a backup folder next
to the install until the next update.

Staged  v0.15.0-sectionfc.2
        <staging path>
 [Close] [Open Release Page] [Install and Restart]
```

On a build that cannot replace itself (non-Windows, or the editor) the middle paragraph is
slice 3's instead — "run tools\update-yarg.ps1 or open the release page" — and the third
button is absent.

**Installing Update** (the dialog shown for the frame before the game quits)
```
Installing v0.15.0-sectionfc.2.

YARG will close now and reopen once the new build
is in place. This takes a few seconds; do not close
the window that appears.
                                    [Close]
```

**Could Not Install** — the install directory is not writable, or the helper would not start.
Nothing has been changed in either case.
```
YARG cannot write to its own install folder, and
this updater never asks for administrator rights.

Move the install somewhere under your user profile
(for example %LOCALAPPDATA%\YARG-SectionFC) and try
again, or run tools\update-yarg.ps1 from an account
that can write to <install path>.
                                    [Close]
```

**Could Not Download**
```
<per-status message>
                 [Close] [Open Release Page]
```

**Could Not Check for Updates**
```
<rate-limit message | generic message>

Installed   v0.15.0-sectionfc.1
            HEAD b4213 (51d52d8)
                                    [Close]
```

## Mechanism

`UpdateChecker` — a static class in `Assets/Script/Song/` next to `SongSources.cs`, the class
whose fetch it copies.

- `UnityWebRequest.Get("https://api.github.com/repos/djrobson5/YARG-fork/releases")`,
  `User-Agent: YARG` header, short timeout, `JArray.Parse(request.downloadHandler.text)` —
  the `SongSources.DownloadSources()` idiom verbatim
  (`Assets/Script/Song/SongSources.cs:190-210`).
- Filter to tags matching `-sectionfc.<int>`; pick the max trailing integer.
- Returns a small immutable result: installed tag, latest tag, release `html_url`, and a
  status enum `UpToDate | UpdateAvailable | NoReleases | RateLimited | Failed`.
- UniTask's awaiter throws `UnityWebRequestException` on protocol, connection and
  data-processing errors, so the failure branches live in a `catch`, not in a post-await
  check of `request.result`. The exception snapshots the response code and error text.
- The result is stored in a static field and returned on every subsequent call.

`UpdateDownloader` — a static class beside `UpdateChecker`, the C# port of the download /
verify / stage half of `tools/update-yarg.ps1`.

- `UpdateChecker` now also returns the latest release's Windows asset: `AssetName`,
  `AssetUrl` (`browser_download_url`) and `AssetSize` (`size`), picked by
  `^YARG-SectionFC_.*-Windows-x64\.zip$` with a fallback to the archive's *single* `.zip`
  if the workflow's naming ever changes. The asset list is only read under
  `#if UNITY_STANDALONE_WIN`; every other platform gets `HasDownloadableAsset == false`
  and degrades to the Open Release Page button.
- `UniTask<UpdateStageResult> DownloadAndStage(UpdateCheckResult, IProgress<float>, CancellationToken)`:
  1. Reuses an already-downloaded `.zip` whose length matches `AssetSize`; deletes a stale
     or partial one.
  2. Otherwise `UnityWebRequest` + `DownloadHandlerFile` (`removeFileOnAbort = true`) with
     **no timeout** — `UnityWebRequest.timeout` is a wall clock on the whole transfer, not
     an idle timeout, so any value at all is a size-and-connection-speed lottery on a
     ~130 MB asset. Progress comes from UniTask's `ToUniTask(progress)`, which reports
     `UnityWebRequestAsyncOperation.progress` once per frame.
  3. Verifies the file's length against `AssetSize`. The release publishes no checksum, so
     this is the only integrity signal there is. A mismatch deletes the file.
  4. `ZipFile.ExtractToDirectory`s into a temporary sibling folder `staging/<tag>.tmp`, on a
     thread-pool thread (`UniTask.RunOnThreadPool`) so the menu does not freeze for the
     several seconds a ~130 MB archive takes.
  5. Checks that `YARG.exe` and `YARG_Data/` are at the temp root before calling it staged,
     exactly as `Expand-ToStaging` does in the PowerShell script, and only then deletes the
     previous `staging/<tag>` and renames the temp into place. A failed or refused extract
     therefore leaves any previously staged build intact.
- `AssetName` is reduced to a single path component (`Path.GetFileName` plus invalid-char
  replacement, refusing `.`/`..`/empty) before it is joined onto `updates/`, so a hostile or
  malformed GitHub response cannot write outside the updates folder. Tags get the same
  treatment.
- `UnityWebRequestException` is caught as slice 2 catches it; `OperationCanceledException`
  is its own branch. Everything is logged through `YargLogger`.

### Paths

Everything the updater writes lives under `PathHelper.PersistentDataPath` — deliberately
*not* the install directory, so a failed update cannot corrupt a working install.

| | Path |
|---|---|
| Work root | `<PersistentDataPath>/updates` |
| Downloaded asset | `<PersistentDataPath>/updates/<asset name>` |
| Staging root | `<PersistentDataPath>/updates/staging` |
| Staged build | `<PersistentDataPath>/updates/staging/<tag>` |

The tag is path-sanitized before use as a folder name (no release tag needs it today; it is
belt and braces). The `.zip` is kept after a successful stage so a repeat press does not
re-download 130 MB.

### `UpdateDownloader.StageStatus`

| Status | Meaning | Dialog |
|---|---|---|
| `Staged` | Downloaded, verified, extracted, layout looks like a YARG build. | Update Ready |
| `NoAsset` | The release carried no Windows `.zip`, or its asset name was not usable as a file name. | Could Not Download |
| `DownloadFailed` | Offline, 404, or the download could not be read back. | Could Not Download |
| `SizeMismatch` | Downloaded length ≠ the release's `size`. File deleted. | Could Not Download |
| `ExtractFailed` | The archive's contents could not be read (`InvalidDataException`); it is corrupt. The `.zip` **is deleted**, so a right-sized-but-broken archive is not reused forever. | Could Not Download |
| `StageIoError` | The updates folder could not be written to — out of disk, denied, or a handle held on the staging tree. The `.zip` is kept; nothing is wrong with it. | Could Not Download |
| `InvalidLayout` | No `YARG.exe` and/or no `YARG_Data/` at the staging root. | Could Not Download |
| `Cancelled` | The user closed the progress dialog. | none — say nothing |

The Settings button is a plain `public async void CheckForUpdates()` on `SettingContainer`,
following `RemoveRemoteContent()` (`SettingsManager.Settings.cs:664`) and wired with
`new ButtonRowMetadata(nameof(Settings.CheckForUpdates), visibleWhen)` in
`SettingsManager.cs`. It awaits the checker and shows the matching dialog.

`HeaderMetadata` gains the `visibleWhen` predicate that `AbstractMetadata` already supports
but that header never exposed, so the `Updates` header can hide alongside its row instead of
dangling over an empty section.

## Slices

1. **`tools/update-yarg.ps1`.** Proves the API filter, the asset naming and the
   copy-over-and-relaunch dance outside Unity. Ships in the release notes as the interim
   answer. *(Owned separately; not part of this document's implementation.)*
2. **Check only.** `UpdateChecker`, the Settings button, the three dialogs, localization.
   No downloading, nothing written to disk. *(Done.)*
3. **Download + verify + stage.** Download to `PathHelper.PersistentDataPath/updates`,
   verify the length against the asset's `size` field, extract to `updates/staging/<tag>`,
   sanity-check that `YARG.exe` and `YARG_Data/` are at the staging root. Still no writes to
   the install directory. *(Done.)*
4. **Apply.** Writability probe on `PathHelper.ExecutablePath` first; **no elevation, ever**.
   Helper `.cmd` that waits for the PID, moves the install into `backup/<old-tag>`, copies
   staging over, relaunches, deletes itself. `Application.Quit()`. Windows only
   (`#if UNITY_STANDALONE_WIN`); other platforms degrade to "Open Release Page". *(Done.)*
5. **Optional automatic check** behind a `ToggleSetting`, plus a "latest build" line near the
   version watermark.

## Files

| Concern | Touch point |
|---|---|
| Fetch + compare + asset info | `Assets/Script/Song/UpdateChecker.cs` |
| Download + verify + stage | `Assets/Script/Song/UpdateDownloader.cs` |
| Writability probe, helper script, quit | `Assets/Script/Song/UpdateInstaller.cs` |
| Button methods and dialogs | `Assets/Script/Settings/SettingsManager.Settings.cs` |
| Tab wiring | `Assets/Script/Settings/SettingsManager.cs` (General tab) |
| Header visibility | `Assets/Script/Settings/Metadata/HeaderMetadata.cs` |
| Strings | `Assets/StreamingAssets/lang/en-US.json` |

---

## Slice 4 implemented (2026-09-04)

Apply is in. The Update Ready dialog can now replace the running install with the staged
build and restart into it, on Windows packaged builds only.

### File map

| File | Change |
|---|---|
| `Assets/Script/Song/UpdateInstaller.cs` | **New.** `IsSupported`, `InstallDirectory`, `BackupRoot`, `IsInstallWritable()`, `IsStagedBuildValid()`, `Apply(newTag, stagingPath)`, and the helper script as the `HELPER_TEMPLATE` constant. |
| `Assets/Script/Settings/SettingsManager.Settings.cs` | `ShowUpdateReadyDialog()` (extracted from `DownloadUpdate`, now adds the Install and Restart button) and `InstallUpdate()`. Both inside the existing `#if UNITY_STANDALONE_WIN` block. |
| `Assets/StreamingAssets/lang/en-US.json` | `Updates.InstallAndRestart`, `Updates.Ready.DescriptionManual`, `Updates.Installing.*`, `Updates.InstallFailed.*`; `Updates.Ready.Description` rewritten now that installing works. |
| `tools/update-yarg.ps1` | `Move-InstallToBackup` now clears the whole `backup` root rather than only `backup\<old-tag>`. See "Bug found in the PowerShell script" below. |

Nothing under `YARG.Core/` is touched.

### What the helper does

Written to `<PersistentDataPath>/updates/apply-<tag>.cmd` and started as
`cmd.exe /c ""<path>""` with `CreateNoWindow`, `UseShellExecute = false`, and the updates
folder as its working directory. It logs every step to `apply-<tag>.log` beside itself.

1. Poll `%SystemRoot%\System32\tasklist.exe /FI "PID eq <pid>" /NH` once a second until the
   game's PID is gone. After 120 tries it logs a timeout, deletes itself and stops **without
   having changed anything** — and, unlike every other exit, without relaunching, because the
   game it gave up waiting for is still running.
2. `rd /s /q` the whole `<install>/../backup` folder, then `md` `<install>/../backup/<old-tag>`.
   Exactly one backup is kept. If the old backup cannot be cleared, or the folder cannot be
   created, it aborts here, still having changed nothing.
3. `robocopy "<install>" "<backup>" /E /MOVE` the whole install into the backup, then recreates
   the (now deleted) install folder. Moving rather than copying is both cheap and leaves a
   complete working build behind. A robocopy exit code of 8 or more jumps to the restore path.
   **Not** a `dir /b /a` + `move` loop: `move` refuses hidden and system files ("The system
   cannot find the file specified.") and `for /f` over `dir /b` silently skips any name starting
   with `;`, so either one could leave part of the old build in the install — where step 4 would
   then bury it and the restore path delete it. This was caught in testing.
4. `xcopy "<staging>\*" "<install>\" /E /I /H /Y`.
5. Re-check that `YARG.exe` and `YARG_Data` are in the install directory *after* the copy — a
   half-succeeded copy that did not set an exit code would otherwise go unnoticed. Failure
   jumps to the restore path.
6. Write `<install>\.yarg-update-tag` with the new tag, matching the marker
   `tools/update-yarg.ps1` writes and reads.
7. `rd /s /q` the staging folder. The downloaded `.zip` in `updates/` is kept.
8. `start "" /D "<install>" "<install>\YARG.exe"`.
9. Delete itself with `(goto) 2>nul & del "%~f0"`.

**Restore path** (steps 3–5 failing): `robocopy "<backup>" "<install>" /E /MOVE` the backup's
contents back, delete the backup folder, relaunch the restored build, delete itself. The log
survives, so a failure is diagnosable afterwards.

`/PURGE` is added — so that a half-copied new build is cleared rather than mixed into the old
one — **only** when step 3 completed, which is the only case in which the backup is known to
hold the whole old install. If step 3 failed part way, the install still holds originals that
were never copied anywhere and purging would destroy them outright; the restore then merely
overwrites, and any leftover new file is preferred to a lost old one.

Every path is written as `set "VAR=value"` and used quoted, so install directories with spaces,
parentheses and ampersands work — `C:\...\Program Files (x86) & Co\YARG Install` was the fixture
the helper was tested against. The release tag is path-sanitized before it is substituted into
the script for the same reason: only the `-sectionfc.<n>` suffix of a tag is checked, so
everything before it is whatever GitHub said. `ping` is the sleep because `timeout` refuses to
run without a console, and the external tools are called by absolute path because a `find` or
`tasklist` earlier on `PATH` reports different exit codes — with git's `find` on `PATH` the wait
loop concluded "exited" for a PID that was still running.

### Writability, and what happens when the install is not writable

`UpdateInstaller.IsInstallWritable()` creates and deletes
`<install>\.yarg-update-write-probe-<guid>`. It is called twice: once by `InstallUpdate()`
before any dialog changes, so the failure message replaces the Update Ready dialog cleanly,
and once inside `Apply()` before the helper is written, so no caller can skip it.

On failure the Could Not Install dialog says to move the install under the user's profile or
to run `tools/update-yarg.ps1` from an account that can write there. **The updater never
elevates** — no `runas`, no `Verb = "runas"`, no manifest. Nothing is written, nothing is
moved, and the game does not quit.

### Bug found in the PowerShell script

Writing the helper's single-backup step exposed the equivalent step in
`tools/update-yarg.ps1` as wrong: `Move-InstallToBackup` deleted only `backup\<old-tag>`, so
updating v1 → v2 → v3 left `backup\v1` *and* `backup\v2` behind — one ~130 MB copy per tag
ever updated from, against the locked "keep exactly one backup" decision. It now clears the
backup root. That is the only change to the script.

### Manual test procedure (needs a packaged build)

None of this can be exercised in the editor: `UpdateChecker.IsReleaseBuild` hides the whole
Updates section when `Application.version` is not a release tag (it is `0.1.0` there), and
`UpdateInstaller.IsSupported` is false under `UNITY_EDITOR` regardless.

1. Cut `v0.15.0-sectionfc.N` and `v0.15.0-sectionfc.N+1` from CI
   (`docs/release-build.md` §2). Two releases are needed: one to install, one to update to.
2. Unzip `N` into a folder **under your profile** with a space in its path — e.g.
   `%LOCALAPPDATA%\YARG SectionFC\YARG` — to exercise the quoting.
3. Run `YARG.exe`. Settings → General → Updates → **Check for Updates** → "Update Available",
   `N` → `N+1`.
4. **Download Update.** Watch the percentage climb, then "Extracting…", then Update Ready.
5. **Install and Restart.** The Installing Update dialog appears, the game quits, and after a
   few seconds it relaunches by itself.
6. Verify:
   - Settings → General → Updates → Check for Updates now reports Up to Date at `N+1`
     (a fresh process, so nothing is cached from before the restart).
   - `%LOCALAPPDATA%\YARG SectionFC\backup\v0.15.0-sectionfc.N\` exists and holds the old
     build, and there is no second folder beside it.
   - `<install>\.yarg-update-tag` reads `v0.15.0-sectionfc.N+1`.
   - `<PersistentDataPath>\updates\staging\` is empty; the `.zip` is still in `updates\`.
   - `<PersistentDataPath>\updates\apply-<tag>.cmd` is **gone**, and
     `apply-<tag>.log` ends with "Installed v0.15.0-sectionfc.N+1".
7. **Non-writable case:** copy the same install into `C:\Program Files\YARG-SectionFC`, run it
   unelevated, and press Install and Restart on a staged build. Expect the Could Not Install
   dialog, no UAC prompt, no `backup` folder next to the install, and the game still running.

The helper itself was tested outside Unity by rendering `HELPER_TEMPLATE` against a fixture
install at `…\Program Files (x86) & Co\YARG Install` holding a hidden file, a file whose name
starts with `;`, and names with `&` and parentheses, and running it five ways: against a dead
PID (installs), against a live PID killed after 8 s (waits, then installs), against a live PID
never killed (times out at ~2 minutes, changes nothing, does not relaunch), with the staging
folder deleted after the render (copy fails → the old build comes back complete, the backup is
deleted, the build relaunches), and with a staged build missing `YARG_Data` plus an extra file
(post-copy check fails → restore purges the half-copied build and puts the old one back). All
five self-deleted. What that cannot cover is the game actually quitting on cue, the relaunch of
a real `YARG.exe`, and the hidden-window `Process.Start` from a packaged player.
