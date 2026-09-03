# Section FC handoff

Written 2026-09-02 at the end of the first session, updated 2026-09-03 (twice: slice 5, then the upstream `dev` merge). Read this, then `docs/section-fc-design.md`, before doing anything.

## State

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

## What remains

Slice 5 is done. It shipped two settings:

- `TrackSectionCompletion` (Song Manager > Music Library, master switch): off means no scan, no rows, no UI anywhere; existing rows are kept.
- `ShowSectionStrip` (Graphics > HUD): hides the in-game strip only.

Both are read at song start. The master toggle's callback invalidates `ScoreContainer`'s section cache and calls `MusicLibraryMenu.SetReload(Partial)`.

**Known low-severity items left open:**

a. With the strip off, its empty root stays registered with `DraggableHudManager`, so HUD edit mode can select an invisible outline above the track.
b. Toggles flipped from the pause menu apply on the next song only, and turning the master off mid-song leaves the strip drawing though no credit is recorded.
c. `SetReload(Partial)` can downgrade a pending Full reload if flipped right after a rescan, same as the existing `AllowDuplicateSongs` behavior.

**Optional follow-ups the user has not requested:** a vocals HUD surface (the miss and hit hooks already exist on `VocalsPlayer`), a sidebar per-section checklist (deferred in the design doc), letting section credit ignore bots (a change to the slice 1 eligibility rule; today it mirrors the high-score rule), and the ease-duration/no-animation setting.

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
