# Open Items

Running list of known issues and possible follow-ups for the fork. Last updated 2026-09-03. Remove items when done; note the commit.

Feature research lives in `docs/roadmap.md`; the state of each feature is in
`docs/section-fc-handoff.md` → "Roadmap work, 2026-09-03".

## Branch state and next steps

`feature/section-fc` is **ten commits ahead of `fork/feature/section-fc` and nothing is pushed**
(the commit carrying this update makes eleven). In order: (1) SP path editor verification,
(2) push the branch, (3) score import once the user's files arrive, (4) cut a release build to
exercise updater slices 2-3, then updater slice 4 (apply).

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
- **Feature 3, SP path — computes in the editor; the visuals were redesigned on 2026-09-04 and
  nothing about the new ones has been seen running.** A real run logged
  `SP path (FiveFretGuitar): 4 activation(s), first at tick 45000 (54.612s ...)` plus the divergence
  line, so the optimizer, plumbing and gating work. The original thin orange band was never
  identifiable — same thickness as a beat line, same colour as everything Star Power around it — so
  it was replaced (design doc: "Visual redesign, 2026-09-04"): a green ring on the activation note
  plus a beat-long green band with rail caps and a lead-in tick on the highway, a steady green wash
  over the strike line at the activation moment (skipped when `ReduceFlashingLights` is on), and a
  code-built `ACTIVATE IN n` chip in `TrackView`'s top band. Colour is the drum Star Power
  activation green (`#52FF00` / `#005400`), not Star Power orange; the highway preset's
  `StarPowerColor` is now ignored. **Amended 2026-09-04 at the user's instruction:** nothing dims
  any more — the dimmed marker state, grey ring and `OFF PLAN` chip are gone, the glow follows the
  activation window alone, and the cue stays bright for the whole song; divergence detection is
  kept as a log-only diagnostic. The activation note itself is now recoloured green as the one
  guaranteed-visible part of the cue. **The user has since confirmed the band, the green note and
  the chip all render, so the temporary `SP path: TEMPORARY ...` logs were deleted, and the cue is
  now player-configurable:** four settings in Graphics → HUD after `ShowStarPowerPath` and greyed
  out with it — `StarPowerPathColor` (colour picker, default `#52FF00`, drives every surface),
  `StarPowerPathChipLeadIn` (1–8 s, step 0.5, default 3), `StarPowerPathChipHold` (0–3 s, step
  0.25, default 0.75) and `StarPowerPathFretGlow` (default on, still overridden by
  `ReduceFlashingLights`), all read once per path so pause-menu changes land next song.
  **Next: the user tests the redesigned visuals.** Highest risks, in order: the runtime-cloned quad
  geometry (rotation convention, z-fighting), `RemovePointOffset = 2f` on a band centred on the
  activation, the ring lining up with the notes under lefty flip, the code-built uGUI chip rendering
  inside `Top Elements` with a borrowed font, and the strike line glow reading as a glow. Unison bonuses are now modelled by the
  optimizer (design doc: "Unison bonuses, modelled"), so the markers land where the meter really
  fills; harness stays green (49 tests, `dotnet test tools/SpPathTests/SpPathTests.csproj`). Manual test steps are in
  `docs/sp-path-design.md` → "Manual test steps (redesigned visuals)".
- **Feature 2, delete songs — risk 1 stays open by nature.** If `SongCacheDirty` fails to persist,
  a deleted song can come back unplayable after a quick scan on the next launch. Only a
  delete-then-restart test exercises it; the UI cannot show it.

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
