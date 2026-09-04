# Fork handoff

Written 2026-09-02 at the end of the first session, updated 2026-09-03 (slice 5, the upstream `dev` merge, then the roadmap features). Read this first.

The doc now covers the whole fork, not just Section FC:

- **Section FC** — the sections **State** and **What remains** below, plus `docs/section-fc-design.md`.
- **The four roadmap features** — **Roadmap work, 2026-09-03**, plus `docs/roadmap.md` (research) and the three design docs it points at.
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

### Feature 4 — in-game updater: slices 1-4 done, 5 remains

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

**Verification gap.** In the editor `Application.version` is `0.1.0`, so the in-game pieces hide
themselves and cannot be exercised — none of slices 2, 3 and 4 has been seen working in the game.
They need a CI release build (`.github/workflows/build-windows.yml`; the pipeline is proven, see
`docs/release-build.md`) and then a run of the packaged `.exe`; slice 4 needs **two** releases, one
to install and one to update to. The slice 4 helper script itself *was* tested outside Unity (dead
PID, live PID, and a forced copy failure exercising the restore path, all with spaces in the
paths). `tools/update-yarg.ps1`'s copy-over-and-relaunch step is still **untested** — it has never
been pointed at a real install; writing the helper did expose one bug in it, since fixed (it kept a
backup per tag rather than exactly one).

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

1. **Cut two release builds** (`v0.15.0-sectionfc.N` and `N+1`) and exercise updater slices 2-4
   against the packaged `.exe`, following `docs/updater-design.md` → "Slice 4 implemented" →
   "Manual test procedure". Slice 5 (automatic check behind a toggle) only after that.

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
