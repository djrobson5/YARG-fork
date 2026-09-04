# Open Items

Running list of known issues and possible follow-ups for the fork. Last updated 2026-09-04. Remove items when done; note the commit.

Feature research lives in `docs/roadmap.md`; the state of each feature is in
`docs/section-fc-handoff.md` → "Roadmap work, 2026-09-03".

## Branch state and next steps

The branch is fully pushed to `fork/feature/section-fc` as of 2026-09-04 (head `c4441462` plus
this commit). Next steps in order: (1) cut two release builds and exercise updater slices 2-4
against the packaged `.exe` (`docs/updater-design.md` → "Slice 4 implemented" → "Manual test
procedure"), (2) periodic merge of upstream `dev`.

## Parked

- **Feature 1, score import.** The user's other machine runs the official nightly, and the fork's
  CI build defines `YARG_NIGHTLY_BUILD`, so it reads the same
  `%USERPROFILE%\AppData\LocalLow\YARC\YARG\nightly` folder; installing the fork there picks up
  scores and profiles automatically, so no import is needed. `tools/import-scores.ps1` stays for
  the stable-install (release folder) case.

## Unfinished features

- **Feature 4, updater — slice 5 only.** Slice 4 (apply) landed on 2026-09-04:
  `Assets/Script/Song/UpdateInstaller.cs` plus an Install and Restart button on the Update Ready
  dialog. Slice 5 is the optional automatic check behind a toggle plus a "latest build" line by the
  version watermark.

## Needs verification

- **Feature 4, updater — nothing in-game has ever been seen running.** In the editor
  `Application.version` is `0.1.0`, so the Check for Updates button, the download/stage flow and the
  Install and Restart button all hide themselves (`UpdateInstaller.IsSupported` is additionally
  false under `UNITY_EDITOR`). Cut **two** CI release builds and exercise slices 2-4 against the
  packaged `.exe`, following `docs/updater-design.md` → "Slice 4 implemented" → "Manual test
  procedure" — including the non-writable case (an install under `C:\Program Files` must show
  "Could Not Install", raise no UAC prompt and change nothing).
- **`tools/update-yarg.ps1`'s copy-over-and-relaunch step is untested** — it has never been pointed
  at a real install. Its backup step was corrected on 2026-09-04 (it kept one backup per tag instead
  of exactly one); that change is untested for the same reason.
- **Feature 2, delete songs — risk 1 stays open by nature.** If `SongCacheDirty` fails to persist,
  a deleted song can come back unplayable after a quick scan on the next launch. Only a
  delete-then-restart test exercises it; the UI cannot show it.

## Done, lightly verified

- **Feature 3, SP path — verified by the user in the editor on 2026-09-04.** The green activation
  notes, the highway band, the countdown chip, and the four Graphics → HUD settings (colour picker,
  chip lead-in, chip hold, fret glow toggle) all work. Unison bonuses are modelled by the optimizer;
  harness stays green (49 tests, `dotnet test tools/SpPathTests/SpPathTests.csproj`). Remaining
  unverified: a second human player, drums/vocals, practice mode and replay exclusions have not been
  re-checked since the redesign (they are gated in code, not visuals).

## Not a bug

- **Three caught `NullReferenceException`s at settings load** (`SettingContainer` setters →
  `RefreshSongs` → `RequestContainerRefresh` → `GetSongLengthSort`) are stock upstream behaviour,
  confirmed by blame. Caught and harmless; ignore them in editor logs.

## Policy

- **The fork never modifies the `YARG.Core` submodule.** Anything that would belong there is worked
  around from the main repo instead (e.g. the `PreviewContext.Loop` mixer leak, disposed from
  `StopPreviewAsync`).

## Known low-severity issues (Section FC)

- Hidden strip still registered with the HUD editor: with `ShowSectionStrip` off, the strip's empty root GameObject stays in `DraggableHudManager`'s element list, so HUD edit mode can select an invisible outline above the track. Fix needs an unregister call whose timing against the manager's `Start()` must be verified in the editor.
- Pause-menu toggles apply on the next song only. Turning `TrackSectionCompletion` off mid-song leaves the live strip drawing though no credit is recorded at song end.
- `TrackSectionCompletionCallback` calls `MusicLibraryMenu.SetReload(Partial)` unconditionally, which can downgrade a pending Full reload if flipped right after a rescan. Same behavior as upstream's `AllowDuplicateSongs`.

## Optional follow-ups (not requested)

- Vocals HUD surface for section progress; miss and hit hooks already exist on `VocalsPlayer`.
- Sidebar per-section checklist (deferred in `docs/section-fc-design.md`).
- Let section credit ignore bots (changes the slice 1 eligibility rule, which today mirrors the high-score rule).
- Ease-duration or no-animation setting for the strip (`_easeDuration` is a serialized field on `SectionStrip`).

## Maintenance

- Merge upstream `dev` into `feature/section-fc` every week or two; routine in `docs/section-fc-handoff.md` (Nightly tracking).
- Disable the Crowdin and label-conflicts workflows in the fork's Actions tab; they exist on the default branch and fail without upstream's secrets.
- If the `Library` cache in `build-windows.yml` never saves (GitHub's 10 GB cap), remove the cache step to save time.
- The release pipeline is proven as of `v0.15.0-sectionfc.1` (run 33797702460); outcome recorded in `docs/release-build.md`. The `Library` cache did save on that run (~1.28 GB, well under GitHub's 10 GB cap), so the "cache never saves" concern above did not materialize — but it was a cold-cache first save, so watch whether it still saves once the cache grows over repeated runs.
