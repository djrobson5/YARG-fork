# Fork handoff

Written 2026-09-02 at the end of the first session, updated 2026-09-03 (slice 5, the upstream `dev` merge, then the roadmap features). Read this first.

The doc now covers the whole fork, not just Section FC:

- **Section FC** — the sections **State** and **What remains** below, plus `docs/section-fc-design.md`.
- **The four roadmap features** — **Roadmap work, 2026-09-03**, plus `docs/roadmap.md` (research) and the three design docs it points at.
- **Rewind to section** — **Rewind to section, 2026-09-10/11**, plus `docs/rewind-design.md` (locked design) and the wayfinder map, issue #2 on the fork.
- **Fork-wide** — **Workflow that worked**, **Environment gotchas** and **Nightly tracking** apply to everything.

## Section FC state

Branch `feature/section-fc`, now based on upstream's nightly `dev` rather than `master` (see **Nightly tracking** below). Nothing pushed since the merge. Working tree clean. Commits, oldest first:

| Commit | Content |
|---|---|
| `da79c59b` | CLAUDE.md with model dispatch rules |
| `6c71e333` | Design doc and headless build notes |
| `70f70159` | Slice 1: per-section scan, `SectionCompletions` table, end-of-song hook |
| `cc4e3074` | Slice 2: results-screen row, strip, Section FC tag, violet colorizer slot |
| `af4e7cf8` | Slice 2 fix: strip inset to match stat rows |
| `a5f02838` | Slice 3: fraction in the library pill, `SectionProgress` summary table, cache |
| `7b7459b5` | Slice 4: in-game section strip with live percent, draggable, per-highway width |
| `e09f9924` | Slice 4 follow-up: binary-search note mapping, width stabilization |
| `16027f49` | Docs: dotnet build compile check |
| `74a8af3a` | Section FC slice 5: settings toggles |
| `0b1f0a8e` | Merge of upstream `dev` (212 commits) into the feature branch |
| _this commit_ | Nightly-tracking notes in this document |

All five slices were verified by the user in the editor: solo run, two-player run, bot run with "Save Scores with Bots" on.

## Section FC — what remains

Slice 5 is done. It shipped two settings:

- `TrackSectionCompletion` (Song Manager > Music Library, master switch): off means no scan, no rows, no UI anywhere; existing rows are kept.
- `ShowSectionStrip` (Graphics > HUD): hides the in-game strip only.

Both are read at song start. The master toggle's callback invalidates `ScoreContainer`'s section cache and calls `MusicLibraryMenu.SetReload(Partial)`.

**Known low-severity items left open:**

a. With the strip off, its empty root stays registered with `DraggableHudManager`, so HUD edit mode can select an invisible outline above the track.
b. Toggles flipped from the pause menu apply on the next song only, and turning the master off mid-song leaves the strip drawing though no credit is recorded.
c. `SetReload(Partial)` can downgrade a pending Full reload if flipped right after a rescan, same as the existing `AllowDuplicateSongs` behavior.

**Optional follow-ups the user has not requested:** a vocals HUD surface (the miss and hit hooks already exist on `VocalsPlayer`), a sidebar per-section checklist (deferred in the design doc), letting section credit ignore bots (a change to the slice 1 eligibility rule; today it mirrors the high-score rule), and the ease-duration/no-animation setting.

## Roadmap work, 2026-09-03

All four roadmap features were worked in the order `docs/roadmap.md` recommended (1, 4, 2, 3).
Research for all four is in `docs/roadmap.md`; the locked decisions and slice plans are in
`docs/updater-design.md`, `docs/delete-song-design.md` and `docs/sp-path-design.md`.

Commits on `feature/section-fc` since `2b4a11c5` ("Roadmap: add the in-game updater as feature 4"),
oldest first:

| Commit | Content |
|---|---|
| `d6f12a84` | Feature 1 + 4 slice 1: `tools/import-scores.ps1` and `tools/update-yarg.ps1` |
| `3f52029c` | Updater slice 2: Check for Updates button |
| `3d34ce9a` | Updater slice 3: download and stage |
| `edf0fed9` | Delete songs slices 1-3: popup item and confirm dialog |
| `9a02fb20` | Delete songs slices 4-5: in-place removal, dirty flag, playlist pruning |
| `2d1c7759` | SP path slices 1-2: design doc, harness, scoring model |
| `dd49dab2` | SP path slice 3: the optimizer |
| `c11ad2d2` | SP path slices 4-6: plumbing, rendering, settings |
| `e7a1da7e` | This handoff, updated for the roadmap work |
| `088d5016` | Dim the SP path only when the Star Power state diverges |

Nothing here is pushed: ten commits ahead of `fork/feature/section-fc`, and the commit carrying this
doc update will be the eleventh. **Policy: the fork never modifies the `YARG.Core` submodule.** Fixes that
would naturally belong there are done from the main repo instead — see the `PreviewContext.Loop`
mixer leak in `docs/delete-song-design.md`, worked around by calling `Dispose()` from
`StopPreviewAsync`.

### Feature 1 — import scores from an official install: parked

`tools/import-scores.ps1` is written and ready. Nothing else is needed; there is no Unity code.

**Parked.** The user's other machine runs the official nightly, and the fork's CI build defines
`YARG_NIGHTLY_BUILD`, so it reads the same `%USERPROFILE%\AppData\LocalLow\YARC\YARG\nightly`
folder; installing the fork there picks up scores and profiles automatically, so no import is
needed. `tools/import-scores.ps1` stays for the stable-install (release folder) case.

Decisions already locked, so the next session does not re-litigate them:

- **Copy/overwrite**, not merge. The imported database replaces the local one.
- **Both targets**: import into the nightly folder (`…\YARG\nightly`, what the fork's packaged
  builds read) *and* the dev folder (`…\YARG\dev`, what the editor reads).
- **Source profiles replace local profiles.** `profiles.json` comes over wholesale, because the
  score rows key off the source machine's profile GUIDs and would otherwise orphan.

### Feature 4 — in-game updater: slices 1-4 done and user-verified on packaged builds 2026-09-04; slice 5 optional

Done:

1. `tools/update-yarg.ps1` — the standalone script.
2. Check-only: `UpdateChecker`, the Settings → General → Updates button, three dialogs, strings.
3. Download + verify + stage into `PathHelper.PersistentDataPath/updates/staging/<tag>`.
   Nothing is written to the install directory yet.
4. **Apply** (2026-09-04). `Assets/Script/Song/UpdateInstaller.cs`: writability probe, never any
   elevation, and a helper `.cmd` (a C# `const`, written to `updates/apply-<tag>.cmd` so it lives
   outside the install) that waits on the PID, moves the install to `<install>/../backup/<old-tag>`
   — exactly one backup is kept — copies staging over, relaunches and deletes itself, restoring
   the old build if the copy fails. The Update Ready dialog gained an **Install and Restart**
   button; the game shows an Installing dialog and quits. Windows packaged builds only. Full
   record, including the manual test procedure, in `docs/updater-design.md` → "Slice 4
   implemented".

Remaining:

5. **Optional automatic check** behind a toggle, plus a "latest build" line by the version
   watermark.

**Verified end to end on packaged builds, 2026-09-04.** The user ran the full check → download →
stage → install flow on real CI-built `.exe`s: `v0.15.0-sectionfc.2` (no apply step yet) found and
staged the latest release; `v0.15.0-sectionfc.3` was installed by hand, then Install and Restart
updated it to `v0.15.0-sectionfc.4`. Releases `.2`/`.3`/`.4` came from CI runs 33913612079,
33915794938 and 33917288074; `.3` and `.4` were built from commit `6bf7e105`. Full record in
`docs/updater-design.md` → "2026-09-04: verified end to end on packaged builds". The slice 4
helper script itself was separately tested outside Unity (dead PID, live PID, and a forced copy
failure exercising the restore path, all with spaces in the paths); `tools/update-yarg.ps1`'s own
copy-over-and-relaunch step is still untested against a real install, if that remains true.

### Feature 2 — delete songs: done and verified

All five slices are implemented, and the user **verified them in the editor on 2026-09-03**.

The entry point is gated behind a settings toggle: **Settings → Debug → Show Advanced Music Library
Options**. With it off, no Delete Song item appears in the music library popup.

Details, the risk list and the file map are in `docs/delete-song-design.md`. Risk 1 (a ghost entry
if `SongCacheDirty` fails to persist) is mitigated, not eliminated; it can only be tested by
deleting, restarting the game, and checking the library — not by watching the UI.

### Feature 3 — Star Power path: computing correctly in the editor, **visuals redesigned 2026-09-04**

All six slices are implemented. `dotnet build Assembly-CSharp.csproj` is green and the harness is
green: **49 tests**, run with

```
dotnet test tools/SpPathTests/SpPathTests.csproj
```

CI runs the same suite via `.github/workflows/sp-path-tests.yml`. `Assets/Script/Gameplay/SpPath/`
is deliberately Unity-free so the harness can compile it; keep it that way (one stray
`using UnityEngine` breaks the test project). The 2026-09-04 redesign did not touch it.

**Editor status, end of 2026-09-03.** The user ran a song with a human profile and the setting on.
The log carried
`SP path (FiveFretGuitar): 4 activation(s), first at tick 45000 (54.612s ...)` and the divergence
line, so the optimizer, the plumbing and the gating all work in a real run. **No marker was ever
seen rendering.** That run drove the dim-rule change in `088d5016`: the path now dims **only when
the Star Power state diverges** (missed Star Power phrase, off-plan activation, planned activation
not taken); ordinary missed notes and dropped sustains no longer dim it.

**Visual redesign, 2026-09-04.** The marker was diagnosed as unidentifiable by construction, not
merely un-rendered: it was a beatline-thickness band in Star Power orange, sitting next to the Star
Power notes, the Star Power phrase region and the Star Power bar. A mockup interview settled a
replacement (Option D), recorded in `docs/sp-path-design.md` → "Visual redesign, 2026-09-04", which
supersedes the UI rows of that document's §5.1:

- **Colour is the drum Star Power activation green** — trim `#52FF00`, body `#005400`, from
  `Assets/Art/Materials/Gameplay/Track/Effects/DrumSPActivationTrim.mat`. The highway preset's
  `StarPowerColor` is ignored for the marker now, and the near-black fallback that existed for it is
  gone.
- **Highway cue at the activation note**: a bright green ring around the note(s) to hit, a
  beat-long full-width green band with brighter rail caps at the highway edges, and a tick on the
  beat before. Beat timing comes from `SyncTrack.Beatlines` in the Unity layer.
- **A steady green wash over the strike line** while the activation is inside the grace window,
  skipped entirely when `ReduceFlashingLights` is on.
- **A code-built `ACTIVATE IN n` / `ACTIVATE` chip** in `TrackView`'s top element container (the
  solo box's band), visible only through the lead-in and the grace window, hidden whenever the solo
  box is up. Strings live at `Gameplay.StarPowerPath.*` in `en-US.json`.
- **Still no prefab, material, scene or shader asset edited.** Every piece is a runtime clone of
  `Beatline.prefab`'s quad, so the highway curve/fade shaders keep applying. `SpPathChip` is built
  from code into the existing container.

**Amended 2026-09-04, at the user's instruction** (`docs/sp-path-design.md` → "The dim states are
gone, and the activation note is recoloured"):

- **Nothing dims, ever.** The dimmed marker state, the grey ring and the `OFF PLAN` chip text are
  removed, and the strike line glow now depends on the activation window alone. The path is shown
  at full brightness for the whole song whatever the player does, because it is information for
  the *next* run.
- **Divergence detection survives as a log-only diagnostic.** `SpPathDiverged` is still set and the
  detailed divergence/phrase-strip log lines are unchanged; nothing visual reads the flag.
- **The activation note itself is recoloured** to the same `#52FF00` green with an emission boost,
  via `INoteElement.IsStarPowerPathActivation` (set for the whole chord in `TrackPlayer.SpawnNote`)
  and `FiveFretGuitarNoteElement.TryApplyStarPowerPathColor()`. It is the one part of the cue that
  is guaranteed to be on screen, since everything else is built at runtime. The green releases when
  the note is hit or missed.
- **The two temporary `SP path: TEMPORARY ...` log families are gone (2026-09-04).** The user
  confirmed the band, the green note and the chip all render, so `LogSpawnDiagnostics` and the
  per-glow-show line were deleted; the compute-time, spawn and divergence lines stay.
- **The false-positive divergence is fixed, and unison bonuses are modelled (2026-09-04).** A
  stripped Star Power phrase is now recorded and logged, never acted on; the verdict moved to a
  meter check at each planned activation (`TrackPlayer.CheckStarPowerPathMeter`, third cursor
  `_spPlanMeterIndex`). The root cause was the model not counting unison bonuses, which
  `BaseEngine.AwardUnisonBonus` pays on every unison phrase and which a single-player run is
  awarded in full. `SpScoreModel` now takes an optional `IReadOnlyList<SpUnisonPhrase>`
  (`FiveFretGuitarPlayer.GetUnisonPhrases()` reads `EngineContainer.UnisonPhrases`, the very list
  the engine awards against; a null container costs the plan its bonuses, not the overlay), so
  a unison phrase end banks two quarter bars and extends an open window twice. See
  `docs/sp-path-design.md` → "Divergence, corrected" and "Unison bonuses, modelled".
- **The cue is now player-configurable (2026-09-04).** Four settings in Graphics → HUD after
  `ShowStarPowerPath`, greyed out with it via `EditableWhen`: `StarPowerPathColor` (`ColorSetting`,
  default `#52FF00`, drives every surface including the derived band body and the chip),
  `StarPowerPathChipLeadIn` (slider, 1–8 s, step 0.5, default 3), `StarPowerPathChipHold` (slider,
  0–3 s, step 0.25, default 0.75) and `StarPowerPathFretGlow` (toggle, default on;
  `ReduceFlashingLights` still overrides it). Read once per path in
  `TrackPlayer.ReadStarPowerPathSettings()`, so pause-menu changes land on the next song. See
  `docs/sp-path-design.md` → "Player settings (2026-09-04)".

Files: `Assets/Script/Gameplay/Visuals/TrackElements/SpPathMarkerElement.cs` (rewritten),
`Assets/Script/Gameplay/HUD/SpPathChip.cs` (new), `Assets/Script/Gameplay/HUD/TrackView.cs`,
`Assets/Script/Gameplay/Player/TrackPlayer.cs`,
`Assets/Script/Gameplay/Player/FiveFretGuitarPlayer.cs`,
`Assets/Script/Gameplay/Visuals/TrackElements/NoteElement.cs`,
`Assets/Script/Gameplay/Visuals/TrackElements/NoteGroup.cs`,
`Assets/Script/Gameplay/Visuals/TrackElements/Guitar/FiveFretGuitarNoteElement.cs`,
`Assets/StreamingAssets/lang/en-US.json`.

**Verified by the user in the editor on 2026-09-04.** The green activation notes, the highway
band, the countdown chip, and the four Graphics → HUD settings (colour picker, chip lead-in, chip
hold, fret glow toggle) all work. Unison bonuses are modelled by the optimizer. Remaining
unverified: a second human player, drums/vocals, practice mode and replay exclusions have not been
re-checked since the redesign (they are gated in code, not visuals).

**Exclusions are deliberate**, not bugs: no path in practice mode, none during replay playback, and
none in a band run with more than one human player (bots do not count). Only 5-fret guitar and bass
compute a path; drums and vocals do not override `RecomputeStarPowerPath`. The path also assumes a
**full combo and no whammy** — that disclaimer is in the setting's description.

### Suggested next steps, in order

1. Install `v0.15.0-sectionfc.4` on the user's other (nightly) machine and confirm scores appear.
2. Periodic merge of upstream `dev`.
3. Optional updater slice 5 (automatic check behind a toggle, plus a "latest build" line by the
   version watermark).

## Rewind to section, 2026-09-10/11

From the pause menu, mid-song, the player picks a section they have already entered and returns to
it. Locked design in `docs/rewind-design.md`; the decision record is the wayfinder map, issue #2 on
the fork, with grilling/research tickets #3-#8 and build slices #12-#19 under #11 (label
`build:rewind`).

### What shipped

Seven build slices on `feature/section-fc`, oldest first. Each slice's resolution comment on its
issue is the detailed record; the table is the index.

| Commit | Issue | Content |
|---|---|---|
| `5ac1ce45` | #12 | Slice 1: fresh engine built and re-simulated from the truncated input log on a debug rewind; seek-suppression flag; `_replayInputs` / `PauseInfo` truncation |
| `758282ed` | #13 | Slice 2: lead-in freeze with inputs dropped, forced countdown to the marker, the **Rewind Lead-In** setting (General > Gameplay, 0.5-5 s, default 2 s, real seconds) |
| `5584fa45` | #14 | Slice 3: the `REWIND TO SECTION` pause row and the section picker pane in the pause page's Graphics Container, on quick play, setlist and the fail menu |
| `e407061e` | #15 | Slice 4: `SectionStripState` rewind (no doubled block progress, cursor regresses) and fail-state clearing |
| `2d19ebba` | #16 | Slice 5: latched HUD and Star Power state cleared — `UnisonDisplay` re-keyed to the fresh engine ids, SP reverb/activation counters, solo/unison/coda/BRE latches, crossing sustains held |
| `76e2e955` | #17 | Slice 6: backwards seek for `LightManager` and `StageManager`, background video, and the `VocalTrack` rebuild plus `VocalsPlayer._phraseIndex` |
| `4d6eaa1e` | #18 | Slice 7: the 0.3 s rewind fade (0.15 s out / 0.15 s in, smoothstep, unscaled time), `SfxSample.Rewind` once, and the `REWOUND` pill on the score card |

Slices 4, 6 and 7 also fixed bugs that were not rewind-specific: the venue flashed dark on **every**
seek (light intensity zeroed by the state clear, so replay and practice were affected too), the
replay viewer's backwards scrub never moved the lights, and a strobe beat index was off by one
after `BeatEventHandler.Reset`.

### What was verified

Every slice was user-verified in the GUI editor before it was committed; the per-issue comments
list what was exercised. Across the seven: rewind out of a solo, a unison phrase and an SP deploy;
SP active at the target resuming with its remaining duration and reverb; restart-current-section;
first section; double rewind; fail-menu rewind; finish-song results strip and high score; replay
save after a rewind; song-source video with Wait For Song Video both on and off; alt-tab during the
fade; the `REWOUND` pill present on a rewound run and absent on a normal run, in history and in
replay playback.

Slice 8 (#19) pre-flight, 2026-09-11, at `4d6eaa1e`: SP path harness 49/49 passed;
`dotnet build Assembly-CSharp.csproj` green (0 errors, 10 pre-existing warnings); a full headless
Unity 6000.3.5f2 compile including the editor assemblies clean with **no** console errors at all
(not even the three stock settings-load `NullReferenceException`s, which need a domain reload to
fire); Unity EditMode tests 1/1 passed and there are no PlayMode tests; and a
`PrefabUtility.LoadPrefabContents` sweep over all 19 rewind-touched prefabs
(`Assets/Prefabs/Gameplay/HUD/Pause/*`, the score cards, `ColoredPillElement`) found **0 missing
scripts**. The only null serialized references are the three `GenericPause` rewind fields
(`_pauseListNavGroup`, `_rewindRowObject`, `_rewindPane`) on `PracticePause`, `QuickSettings` and
`ReplayPause` — the three pause prefabs that deliberately carry no Rewind row — and all three are
null-guarded in `GenericPause.cs`.

Reviews caught real bugs in every single slice; see each issue comment. Do not skip them.

### v1 limits, deliberate

- **Single player only.** The row is hidden with more than one player and hidden when the lone
  player is a bot. Never offered in practice mode or replay playback.
- **No persisted marker.** The `REWOUND` pill is session-only; nothing is written to the score
  record, so a history entry or a saved replay of a rewound run is indistinguishable from a normal
  run. The replay carries the surviving timeline only, and verifies as an ordinary single-timeline
  replay.
- **No `YARG.Core` edits.** Everything is worked around from the main repo, which is why a fresh
  engine is constructed on every rewind rather than reusing one.

### Known carry-overs

Narrow, all understood, none blocking. Also listed in `docs/open-items.md`.

- **Pause inside the lead-in resets a straddling unison phrase's count** (#16). Resuming runs
  `RestartLeadIn` → `SetSongTime` → `UnisonDisplay.ResetState`, which zeroes the restored notes-hit
  count of a unison phrase straddling the marker. The rewind itself is exact.
- **Vocals: a note straddling the landing does not return until the next element** (#17), and there
  is no vocals equivalent of the highway's held-note respawn.
- **The 1 s unpause rewind bypasses `GameManager.SetSongTime`** (#17), so the light and stage
  cursors stay ahead by up to 1 s after an ordinary unpause. Pre-existing upstream behaviour,
  surfaced by slice 6, left alone.
- **The marker seam** (#15). A note whose tick is before the marker but whose hit input fell after
  it loses that input to the truncation, so it is missed on the re-sim and drops the block it
  belongs to. Correct by design — the strip follows the surviving timeline and agrees with
  `Note.WasHit` and the saved replay — but it can look like a lost section.
- **`CUSTOM ENGINE PRESET` plus two pills clips** (#18). Three pills measure 424 px in the 430-wide
  Engine Settings strip, so the normal case fits; a custom engine preset (201.8 px) with non-engine
  modifiers *and* a rewind is 477 px, and each pill's existing `Mask` clips its own text. Taking the
  font down on every card to buy that one case was judged the worse trade.
- **Pre-existing vocals practice-mode stalls** (#17), left alone: `ScrollingPhraseNoteTracker.Reset`
  and `VocalPercussionTrack.Initialize` omit the empty-first-phrase skip, and
  `ResetPracticeSection` does not return `_phraseLinePool`.
- **`BaseEngine.Reset()` is incomplete**, filed upstream-shaped as fork issue
  [#10](https://github.com/djrobson5/YARG-fork/issues/10). It never clears the Star Power position
  block or `BaseTimeInStarPower`, `GuitarEngine.Reset()` leaves the button masks set,
  `_scheduledUpdates` is never cleared, and `KeysEngine.Reset` / `BaseEngine.Generic.Reset` do not
  clear `ActiveSustains`. This is also a live bug in today's replay scrubbing. It is *the* reason
  the fresh engine is mandatory; the fork works around it rather than editing the submodule.

### What remains

Slice 8 (#19) itself: end-to-end verification in the GUI editor and a release build. Nothing else
is planned for rewind; the carry-overs above are accepted for v1.

## Workflow that worked

- Fable orchestrates; Opus does research, implementation, and review; Sonnet does git. See `CLAUDE.md`.
- Every slice: Opus implements, Opus reviews, Opus applies fixes, user verifies in the editor, Sonnet commits. Reviews caught real bugs every time; do not skip them.
- Any new UI: Opus builds an HTML mockup from the real prefab values, the orchestrator publishes it as an artifact and interviews the user with `AskUserQuestion`, then decisions are appended to the design doc before implementation.
- The user reports visual bugs with editor screenshots. Forward the image path to the fixing agent.

## Environment gotchas

- While the Unity editor is open, batchmode compiles cannot run. Use the `dotnet build` check in `CLAUDE.md` instead (~8 s, runtime assembly only). The user still recompiles in Unity for the final check. VS Code's C# language server diagnostics (via the IDE integration) do not refresh on disk-side edits, so don't rely on them.
- Unity rewrites `.vscode/settings.json` on focus. Revert before committing; the commit agents already do this.
- Unity may rewrite hand-authored prefab YAML on save (trailing spaces, `m_EditorClassIdentifier`). Expect diff noise, not breakage.
- Section rows and summary rows only exist for runs made after their slice landed. Songs played earlier show no fraction until the next valid run.
- Scratchpad artifacts from this session (research reports and the three mockups) live under the session's temp directory and may be gone; the mockup artifacts are linked from the design doc.
- **Three caught `NullReferenceException`s at settings load are stock upstream behaviour, not a fork
  bug.** The chain is `SettingContainer` setters → `RefreshSongs` → `RequestContainerRefresh` →
  `GetSongLengthSort`, firing before the song container exists. Verified with `git blame`; they are
  caught and harmless. Ignore them when reading editor logs.
- Make sure no song or library preview is playing before focusing the editor to trigger a recompile after a large pull, since a BASS audio callback firing during the domain unload can deadlock the editor (recovery: kill Unity and relaunch, nothing on disk is affected).

## Nightly tracking

The feature now sits on top of upstream's nightly `dev` rather than the v0.15.0
stable tag.

### Branch layout

| Branch | Tracks | Role |
|---|---|---|
| `master` | `upstream/master` | Upstream stable (v0.15.0). Never merged into anything now; kept as a reference point. |
| `dev` | `upstream/dev` | Read-only mirror of the nightly branch. Never commit to it. |
| `feature/section-fc` | `fork/feature/section-fc` | The feature. Merges `dev` in; never rebased, so the history stays reviewable. |

### Refreshing

```
git fetch upstream --prune
git checkout dev && git merge --ff-only upstream/dev
git checkout feature/section-fc && git merge --no-ff dev
git submodule update --init --recursive
```

Merge, never rebase. `master` is a strict ancestor of `upstream/dev`, and the
`YARG.Core` gitlink moves forward cleanly (`3beb94e5` -> `4f1fa7a5`); the feature
has never touched the submodule.

### The first merge (2026-09-03)

Fifteen conflict hunks across nine files. Seven were adjacent additions where both
sides were kept. The real ones:

- **`ScoreCard.Initialize`** — dev added an `isReplay` parameter and moved
  `AverageMultiplier` from `PlayerScoreCard` onto `BaseStats`, read as
  `Stats.AverageMultiplier`. The final signature is dev's parameters plus our
  `sections`; our `averageMultiplier` parameter and field are gone.
- **`GameManager` `PlayerScoreCard` construction** — dev's `IsReplay` field plus
  our `Sections`; the `AverageMultiplier` assignment is deleted.
- **`SettingsManager.cs` tab list** — our `TrackSectionCompletion` entry stays a
  bare `nameof(...)` string; the neighbour dev added uses `FieldMetadata` only
  because it is advanced-only.
- **`en-US.json`** — both key sets kept under `ScoreScreen`, parsed to confirm
  valid JSON.

Two changes merged cleanly but were **semantically broken**, and both would have
compiled only by accident:

1. `GameManager` still assigned the deleted `PlayerScoreCard.AverageMultiplier`.
2. dev removed `using System.Linq;` from `SongViewType.cs`, which orphaned the
   slice-3 `PlayerContainer.Players.FirstOrDefault(...)` call in
   `FetchSectionProgress`.

Grep for removed members after every future merge; a clean auto-merge proves
nothing.

### Semantic review of dev's changes

| dev change | Effect on Section FC | Action |
|---|---|---|
| #1413 aggregate drums high scores | `GetHighScoreForInstruments` still returns one concrete `PlayerScoreRecord`, so its `Instrument`/`Difficulty` name the chart the player actually played. The section lookup key is unambiguous. | None. |
| #1565 / #1641 / #1590 score context | `FetchHighScores` now re-fetches whenever `ScoreContext` changes. `_sectionProgress` is assigned in the same block, so the fraction refreshes with the percent instead of being cached forever. | None; strictly better than before. `ScoreContext` does not carry `HarmonyIndex`, so switching HARM1/HARM2 without changing instrument leaves both the percent and the fraction stale — an upstream limitation we now share. |
| #1488 replay score screen | `ReplayViewType` builds its own `PlayerScoreCard[]` and never sets `Sections`, so it defaults to `null` and `ScoreCard.BuildSectionCompletion` hides the row, strip and tag. | None. |
| `6bd898a0` / `6546d115` no-fail and replay score saving | The new rule lives inside `YargPlayer.IsScoreValid`, which `InvalidateScores` clears. Section credit reads that through `IsBandScoreValid` / `IsSoloScoreValid`, so it follows the new high-score rule for free. `InvalidateScores` also still calls `SetSectionState(null)`, so the strip disappears the moment a run stops being eligible. | None. |
| #1545 solo/unison/coda notification suppression | New state on `TrackView` drives `_textNotifications` and the unison bar, both inside `Top Elements`. The strip lives in its own container pinned above that, and dev did not touch `TrackView.prefab`. `UpdateSectionStrip*`, `_highwayIndex` and `_highwayCount` all survived. | None, but see the verification list below. |

### NuGet bootstrap

`Assets/packages.config` on dev adds `ManagedBass.Asio` and `ManagedBass.Wasapi`
3.1.1. NuGetForUnity only restores after a successful compile, so `dotnet build`
fails on missing types until they exist. Unpack them by hand once:

```
curl -sL -o ManagedBass.Asio.nupkg https://www.nuget.org/api/v2/package/ManagedBass.Asio/3.1.1
```

then unzip `lib/` and the `.nuspec` into
`Assets/Packages/ManagedBass.Asio.3.1.1/`, mirroring the existing
`ManagedBass.Fx.3.1.1` layout. Same for `ManagedBass.Wasapi`.

### Compile checking across a large upstream merge

`Assembly-CSharp.csproj` and `YARG.Core.Package.csproj` are Unity-generated,
gitignored, and carry explicit `<Compile Include>` lists. After a merge that adds
and deletes upstream source files they are stale in both directions, so
`dotnet build` reports hundreds of errors that have nothing to do with the merge.

Do not hand-edit them. Instead copy both to `*.Check.csproj`, replace the explicit
compile lists with `Assets\Script\**\*.cs` and `YARG.Core\YARG.Core\**\*.cs`
globs, add `<Reference>` entries for the two new ManagedBass DLLs, build the copy,
and delete it afterwards. That reduced 8 upstream-only errors plus 4 missing-file
errors down to the single real one (the missing `using System.Linq;`), after which
the build was green. Unity regenerates the real csprojs on its next compile.
