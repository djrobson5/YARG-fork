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
   No downloading, nothing written to disk. **← this document's slice**
3. **Download + verify + stage.** Download to `PathHelper.PersistentDataPath/updates`,
   verify the length against the asset's `size` field, extract to `updates/staging/<tag>`,
   sanity-check that `YARG.exe` and `YARG_Data/` are at the staging root. Still no writes to
   the install directory.
4. **Apply.** Writability probe on `PathHelper.ExecutablePath` first; **no elevation, ever**.
   Helper `.cmd` that waits for the PID, moves the install into `backup/<old-tag>`, copies
   staging over, relaunches, deletes itself. `Application.Quit()`. Windows only
   (`#if UNITY_STANDALONE_WIN`); other platforms degrade to "Open Release Page".
5. **Optional automatic check** behind a `ToggleSetting`, plus a "latest build" line near the
   version watermark.

## Files

| Concern | Touch point |
|---|---|
| Fetch + compare | `Assets/Script/Song/UpdateChecker.cs` (new) |
| Button method | `Assets/Script/Settings/SettingsManager.Settings.cs` |
| Tab wiring | `Assets/Script/Settings/SettingsManager.cs` (General tab) |
| Header visibility | `Assets/Script/Settings/Metadata/HeaderMetadata.cs` |
| Strings | `Assets/StreamingAssets/lang/en-US.json` |
