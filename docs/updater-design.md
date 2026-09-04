# In-game updater

## What it is

The fork ships as a bare `.zip` on GitHub Releases and is invisible to the YARC Launcher.
The updater lets a running build notice that a newer `-sectionfc` release exists, tell the
user about it, and (eventually) download and apply it.

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

Installing from inside the game is not available
yet; run tools\update-yarg.ps1 or open the release
page to install it.

Staged  v0.15.0-sectionfc.2
        <staging path>
                 [Close] [Open Release Page]
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
   (`#if UNITY_STANDALONE_WIN`); other platforms degrade to "Open Release Page".
5. **Optional automatic check** behind a `ToggleSetting`, plus a "latest build" line near the
   version watermark.

## Files

| Concern | Touch point |
|---|---|
| Fetch + compare + asset info | `Assets/Script/Song/UpdateChecker.cs` |
| Download + verify + stage | `Assets/Script/Song/UpdateDownloader.cs` |
| Button methods and dialogs | `Assets/Script/Settings/SettingsManager.Settings.cs` |
| Tab wiring | `Assets/Script/Settings/SettingsManager.cs` (General tab) |
| Header visibility | `Assets/Script/Settings/Metadata/HeaderMetadata.cs` |
| Strings | `Assets/StreamingAssets/lang/en-US.json` |
