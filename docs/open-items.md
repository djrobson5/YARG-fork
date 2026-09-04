# Open Items

Running list of known issues and possible follow-ups for the fork. Last updated 2026-09-03. Remove items when done; note the commit.

Feature research lives in `docs/roadmap.md`; the state of each feature is in
`docs/section-fc-handoff.md` → "Roadmap work, 2026-09-03".

## Blocked

- **Feature 1, score import.** `tools/import-scores.ps1` is ready and needs nothing further.
  Waiting on the user to bring `scores.db` and `profiles.json` over from the other machine.
  Decided: copy/overwrite rather than merge, import into both the nightly and the dev folders, and
  the source `profiles.json` replaces the local one.

## Unfinished features

- **Feature 4, updater — slices 4 and 5.** Slice 4 is apply (writability probe, no elevation, a
  helper `.cmd` that waits on the PID, backs the install up to `backup/<old-tag>`, copies staging
  over, relaunches, deletes itself; Windows only). Slice 5 is the optional automatic check behind a
  toggle plus a "latest build" line by the version watermark.

## Needs verification

- **Feature 4, updater — nothing in-game has ever been seen running.** In the editor
  `Application.version` is `0.1.0`, so the Check for Updates button and the download/stage flow hide
  themselves. Cut a CI release build and exercise slices 2 and 3 against the packaged `.exe`.
- **`tools/update-yarg.ps1`'s copy-over-and-relaunch step is untested** — it has never been pointed
  at a real install.
- **Feature 3, SP path — not editor-verified.** All six slices are implemented and 35 harness tests
  pass (`dotnet test tools/SpPathTests/SpPathTests.csproj`, also run by
  `.github/workflows/sp-path-tests.yml`), but no Unity compile and no real frame. Highest risks:
  the hand-built runtime pool, `GetComponentInParent<TrackPlayer>()` on a prewarmed clone, the
  0.003 z-lift over the beatline quad, the orange reading correctly through the highway shaders,
  and the settings row. The ten manual test steps are in
  `docs/section-fc-handoff.md` and `docs/sp-path-design.md`.
- **Feature 2, delete songs — risk 1 stays open by nature.** If `SongCacheDirty` fails to persist,
  a deleted song can come back unplayable after a quick scan on the next launch. Only a
  delete-then-restart test exercises it; the UI cannot show it.

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
