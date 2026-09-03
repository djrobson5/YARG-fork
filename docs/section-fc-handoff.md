# Section FC handoff

Written 2026-09-02 at the end of the first session. Read this, then `docs/section-fc-design.md`, before doing anything.

## State

Branch `feature/section-fc`, nothing pushed. Working tree clean. Commits, oldest first:

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

All four slices were verified by the user in the editor: solo run, two-player run, bot run with "Save Scores with Bots" on.

## What remains

**Slice 5, settings toggle.** No mockup needed. Proposed scope, not yet agreed with the user:

1. Master toggle `TrackSectionCompletion` (default on): when off, no scan, no rows, no UI anywhere.
2. `ShowSectionStrip` (default on): hides the in-game strip only.
3. Optionally expose the strip ease duration or a "no animation" toggle; no existing HUD motion setting exists.

Where: `SettingContainer` in `Assets/Script/Settings/SettingsManager.Settings.cs` near `HighScoreInfo`, registered in `DisplayedSettingsTabs` in `SettingsManager.cs` under the Music Library header (or Gameplay for the strip), plus `Settings.<Name>.Name` and `.Description` strings in `Assets/StreamingAssets/lang/en-US.json`. Read the toggle via `SettingsManager.Settings.<Name>.Value` at the gate sites: `GameManager.ScanSectionCompletions`, `GameManager.InitializeSectionStripStates`, and `SongViewType.FetchSectionProgress`.

**Optional follow-ups the user has not requested:** a vocals HUD surface (the miss and hit hooks already exist on `VocalsPlayer`), a sidebar per-section checklist (deferred in the design doc), and letting section credit ignore bots (a change to the slice 1 eligibility rule; today it mirrors the high-score rule).

## Workflow that worked

- Fable orchestrates; Opus does research, implementation, and review; Sonnet does git. See `CLAUDE.md`.
- Every slice: Opus implements, Opus reviews, Opus applies fixes, user verifies in the editor, Sonnet commits. Reviews caught real bugs every time; do not skip them.
- Any new UI: Opus builds an HTML mockup from the real prefab values, the orchestrator publishes it as an artifact and interviews the user with `AskUserQuestion`, then decisions are appended to the design doc before implementation.
- The user reports visual bugs with editor screenshots. Forward the image path to the fixing agent.

## Environment gotchas

- While the user has the Unity editor open, batchmode compiles cannot run. Agents validate by reading, and the user recompiles by clicking into Unity. When the editor is closed, the headless command in `CLAUDE.md` works and takes about a minute.
- Unity rewrites `.vscode/settings.json` on focus. Revert before committing; the commit agents already do this.
- Unity may rewrite hand-authored prefab YAML on save (trailing spaces, `m_EditorClassIdentifier`). Expect diff noise, not breakage.
- Section rows and summary rows only exist for runs made after their slice landed. Songs played earlier show no fraction until the next valid run.
- Scratchpad artifacts from this session (research reports and the three mockups) live under the session's temp directory and may be gone; the mockup artifacts are linked from the design doc.
