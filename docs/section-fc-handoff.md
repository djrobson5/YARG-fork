# Section FC handoff

Written 2026-09-02 at the end of the first session, updated 2026-09-03. Read this, then `docs/section-fc-design.md`, before doing anything.

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
| `16027f49` | Docs: dotnet build compile check |
| `74a8af3a` | Section FC slice 5: settings toggles |

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
