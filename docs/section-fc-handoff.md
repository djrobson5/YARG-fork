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

Nothing here is pushed. **Policy: the fork never modifies the `YARG.Core` submodule.** Fixes that
would naturally belong there are done from the main repo instead — see the `PreviewContext.Loop`
mixer leak in `docs/delete-song-design.md`, worked around by calling `Dispose()` from
`StopPreviewAsync`.

### Feature 1 — import scores from an official install: blocked on the user

`tools/import-scores.ps1` is written and ready. Nothing else is needed; there is no Unity code.

**Blocked on the user producing `scores.db` and `profiles.json` from the other machine.** Until
those files exist there is nothing to run it against and nothing to verify.

Decisions already locked, so the next session does not re-litigate them:

- **Copy/overwrite**, not merge. The imported database replaces the local one.
- **Both targets**: import into the nightly folder (`…\YARG\nightly`, what the fork's packaged
  builds read) *and* the dev folder (`…\YARG\dev`, what the editor reads).
- **Source profiles replace local profiles.** `profiles.json` comes over wholesale, because the
  score rows key off the source machine's profile GUIDs and would otherwise orphan.

### Feature 4 — in-game updater: slices 1-3 done, 4-5 remain

Done:

1. `tools/update-yarg.ps1` — the standalone script.
2. Check-only: `UpdateChecker`, the Settings → General → Updates button, three dialogs, strings.
3. Download + verify + stage into `PathHelper.PersistentDataPath/updates/staging/<tag>`.
   Nothing is written to the install directory yet.

Remaining:

4. **Apply.** Writability probe, no elevation ever, a helper `.cmd` that waits on the PID, moves
   the install to `backup/<old-tag>`, copies staging over, relaunches and deletes itself.
   Windows only.
5. **Optional automatic check** behind a toggle, plus a "latest build" line by the version
   watermark.

**Verification gap.** In the editor `Application.version` is `0.1.0`, so the in-game pieces hide
themselves and cannot be exercised — none of slices 2 and 3 has been seen working. They need a CI
release build (`.github/workflows/build-windows.yml`; the pipeline is proven, see
`docs/release-build.md`) and then a run of the packaged `.exe`. `tools/update-yarg.ps1`'s
copy-over-and-relaunch step is likewise **untested** — it has never been pointed at a real install.

### Feature 2 — delete songs: done and verified

All five slices are implemented, and the user **verified them in the editor on 2026-09-03**.

The entry point is gated behind a settings toggle: **Settings → Debug → Show Advanced Music Library
Options**. With it off, no Delete Song item appears in the music library popup.

Details, the risk list and the file map are in `docs/delete-song-design.md`. Risk 1 (a ghost entry
if `SongCacheDirty` fails to persist) is mitigated, not eliminated; it can only be tested by
deleting, restarting the game, and checking the library — not by watching the UI.

### Feature 3 — Star Power path: done in code, **not yet editor-verified**

All six slices are implemented. `dotnet build Assembly-CSharp.csproj` is green and the harness is
green: **35 tests**, run with

```
dotnet test tools/SpPathTests/SpPathTests.csproj
```

CI runs the same suite via `.github/workflows/sp-path-tests.yml`. `Assets/Script/Gameplay/SpPath/`
is deliberately Unity-free so the harness can compile it; keep it that way (one stray
`using UnityEngine` breaks the test project).

**Nothing Unity-side has been through a real Unity compile or a real frame** — `dotnet build` only
covers `Assembly-CSharp`, so the runtime pool, the prefab work, the shaders and the settings row are
all unproven. Highest-risk unverified items, in order (the full list is in `docs/sp-path-design.md`
→ "What still needs verifying in the editor"):

1. **The runtime pool.** `Instantiate` under an inactive parent, `DestroyImmediate` of the
   `BeatlineElement` and `AddComponent` producing a working poolable — and the markers landing at
   the right place on the highway from a copy of the beatline pool's local transform.
2. **`GetComponentInParent<TrackPlayer>()` in `TrackElement.GameplayAwake`.** Prewarmed clones are
   inactive, so `Awake` is deferred to the first `EnableFromPool`; watch for a null `Player` on the
   very first marker.
3. **The z-fight lift** — marker mesh local `y = 0.003` against the beatline quad's `0.002`; does
   1 mm read as "on top" or as a gap?
4. **The colour** reading as Star Power orange (`#FF9800`) through the highway curve/fade shaders,
   and the dimmed `0.25` alpha still being visible.
5. **The settings row** rendering with its new copy, and the toggle surviving a settings save/load.

Manual test steps (from `docs/sp-path-design.md`):

1. Settings → Graphics → HUD: **Show Star Power Path** exists right after **Show Section Strip**,
   defaults to off, description mentions full combo, no whammy, single player. Turn it on.
2. Play a 5-fret guitar or bass song alone. The log carries one
   `SP path (FiveFretGuitar): N activation(s), first at tick … projected …` line, and orange bands
   appear on the highway at those notes.
3. Miss a note → `SP path: diverged — a note was missed`, and every marker on screen drops to a
   faint orange for the rest of the song.
4. Restart, activate Star Power somewhere unmarked → `Star Power was activated off-plan`.
5. Restart, let a marker go by → after ~0.25 s, `a planned activation was not taken`.
6. Restart, drop a sustain without breaking combo → `a sustain was dropped`, combo meter still full.
7. Same song with a second **human** player: no markers,
   `SP path: skipped, 2 human player(s) in this run`. A **bot** second player instead: markers
   return.
8. Drums or vocals with the setting on: no markers and no log line at all.
9. Practice mode: `SP path: skipped, practice mode`, no markers in any section.
10. Replay playback: `SP path: skipped, replay playback`, no markers.

**Exclusions are deliberate**, not bugs: no path in practice mode, none during replay playback, and
none in a band run with more than one human player (bots do not count). Only 5-fret guitar and bass
compute a path; drums and vocals do not override `RecomputeStarPowerPath`. The path also assumes a
**full combo and no whammy** — that disclaimer is in the setting's description.

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
