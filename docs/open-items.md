# Open Items

Running list of known issues and possible follow-ups for the fork. Last updated 2026-09-04. Remove items when done; note the commit.

Feature research lives in `docs/roadmap.md`; the state of each feature is in
`docs/section-fc-handoff.md` → "Roadmap work, 2026-09-03".

## Branch state and next steps

The branch is fully pushed to `fork/feature/section-fc` as of 2026-09-04 (head `c4441462` plus
this commit). Next steps in order: (1) install `v0.15.0-sectionfc.4` on the user's other (nightly)
machine and confirm scores appear, (2) periodic merge of upstream `dev`, (3) optional updater
slice 5.

## Parked

- **Feature 1, score import.** The user's other machine runs the official nightly, and the fork's
  CI build defines `YARG_NIGHTLY_BUILD`, so it reads the same
  `%USERPROFILE%\AppData\LocalLow\YARC\YARG\nightly` folder; installing the fork there picks up
  scores and profiles automatically, so no import is needed. `tools/import-scores.ps1` stays for
  the stable-install (release folder) case.

## Unfinished features

- **Feature 4, updater — slices 1-4 done and user-verified on packaged builds 2026-09-04; slice 5
  optional.** Slice 4 (apply) landed on 2026-09-04: `Assets/Script/Song/UpdateInstaller.cs` plus an
  Install and Restart button on the Update Ready dialog. Slice 5 is the optional automatic check
  behind a toggle plus a "latest build" line by the version watermark.

## Needs verification

- **`tools/update-yarg.ps1`'s own copy-over-and-relaunch step is still untested against a real
  install**, if that remains true per `docs/updater-design.md`. The in-game helper path is verified
  (see "Done, lightly verified" below).
- **Feature 2, delete songs — risk 1 stays open by nature.** If `SongCacheDirty` fails to persist,
  a deleted song can come back unplayable after a quick scan on the next launch. Only a
  delete-then-restart test exercises it; the UI cannot show it.

## Done, lightly verified

- **Feature 4, updater — verified end to end on packaged builds, 2026-09-04.** Slices 1-4 done and
  user-verified: `v0.15.0-sectionfc.2` found and staged the latest release; `v0.15.0-sectionfc.3`
  was installed by hand, then Install and Restart updated it to `v0.15.0-sectionfc.4`. Releases
  `.2`/`.3`/`.4` came from CI runs 33913612079, 33915794938 and 33917288074; `.3` and `.4` were
  built from commit `6bf7e105`. Slice 5 remains optional and unimplemented.
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
- Downloaded update zips accumulate under `nightly/updates` (roughly 130 MB per version) and are never pruned; periodically clear old ones by hand.
- The updater helper deletes any folder named `backup` beside the install when it makes a new one, so don't keep anything else there under that name.
