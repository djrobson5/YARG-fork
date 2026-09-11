# Open Items

Running list of known issues and possible follow-ups for the fork. Last updated 2026-09-11. Remove items when done; note the commit.

Feature research lives in `docs/roadmap.md`; the state of each feature is in
`docs/section-fc-handoff.md` → "Roadmap work, 2026-09-03". Rewind to section is in
`docs/section-fc-handoff.md` → "Rewind to section, 2026-09-10/11", with the locked design in
`docs/rewind-design.md`.

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
- **Rewind to section — build slices #12-#18 landed 2026-09-10/11 (`5ac1ce45` … `4d6eaa1e`), slice 8
  (#19) open.** Slice 8 is end-to-end verification in the GUI editor plus a release build. Its
  pre-flight passed at `4d6eaa1e` (49/49 SP harness, `dotnet build` green, headless Unity compile
  clean with no console errors, EditMode 1/1, prefab sweep 0 missing scripts). What shipped and what
  was verified per slice: `docs/section-fc-handoff.md` → "Rewind to section, 2026-09-10/11".

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

## Known low-severity issues (Rewind to section)

Accepted for v1. Detail and the issue each came from are in `docs/section-fc-handoff.md` →
"Rewind to section, 2026-09-10/11" → "Known carry-overs".

- **Pause inside the lead-in resets a straddling unison phrase's count** (#16). Resume runs
  `RestartLeadIn` → `SetSongTime` → `UnisonDisplay.ResetState`, which zeroes the restored notes-hit
  count of a unison phrase straddling the marker. The rewind itself is exact.
- **Vocals: a note straddling the landing does not return until the next element** (#17); there is
  no vocals equivalent of the highway's held-note respawn.
- **The 1 s unpause rewind bypasses `GameManager.SetSongTime`** (#17), so the light and stage
  cursors stay ahead by up to 1 s after an ordinary unpause. Pre-existing upstream behaviour,
  surfaced by slice 6, deliberately left alone.
- **The marker seam** (#15). A note before the marker whose hit input fell after it loses that input
  to the truncation, so it is missed on the re-sim and drops the previous block. Correct by design
  (the strip follows the surviving timeline and agrees with `Note.WasHit` and the saved replay), but
  it can read as a lost section.
- **`CUSTOM ENGINE PRESET` plus two pills clips** (#18) in the score card's 430-wide Engine Settings
  strip. The three-pill normal case measures 424 px and fits; the custom-preset case is 477 px and
  each pill's `Mask` clips its own text. Shrinking the font on every card was judged the worse trade.
- **Pre-existing vocals practice-mode stalls** (#17), left alone: `ScrollingPhraseNoteTracker.Reset`
  and `VocalPercussionTrack.Initialize` omit the empty-first-phrase skip, and
  `ResetPracticeSection` does not return `_phraseLinePool`.
- **`BaseEngine.Reset()` is incomplete**, filed as fork issue
  [#10](https://github.com/djrobson5/YARG-fork/issues/10): the SP position block,
  `BaseTimeInStarPower`, the guitar button masks, `_scheduledUpdates` and `ActiveSustains` all
  survive a reset. It also affects today's replay scrubbing. Per fork policy it is worked around,
  not fixed: every rewind constructs a fresh engine.

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
