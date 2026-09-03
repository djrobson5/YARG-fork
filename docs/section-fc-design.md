# Section FC (cumulative section completion)

## What it is

A parallel stat, separate from the normal score. For each song, instrument, and difficulty, track which named chart sections the player has ever hit 100% of the notes in, across any number of separate full-song runs. When every section has been perfected at least once, the song is "Section FC'd".

Example: run 1 perfects the intro but drops the solo. Run 2 drops the intro but perfects the solo. After run 2, both sections are marked complete.

## Decisions (locked 2026-09-02)

| Question | Decision |
|---|---|
| Notes before the first section marker | Fold into section index 0, matching practice mode's `FindSectionAtTime` fallback. |
| Overstrums / overhits | Ignored. A section is perfected when every note in it is hit. |
| Vocals | A vocal section is perfected when every phrase in it passed (no `OnNoteMissed`). Matches YARG's vocal FC definition. |
| Practice mode | Does not count. Only full-song runs earn credit. |
| Replays | Never count, neither playback nor backfill. Live play only. |
| Eligibility | Same gate as high scores: `ScoreContainer.IsBandScoreValid` and `IsSoloScoreValid` (no bots, speed >= 1.0, no invalidating modifiers). |
| Big Rock Ending notes | Excluded from section totals, same as `EngineStats.TotalNotes`. |
| Section identity | Keyed by index in `SongChart.Sections`, never by name (names repeat within a song). Song identity is the existing `SongChecksum`, so a chart edit naturally invalidates old rows. |
| Empty sections | A section with no notes on the player's instrument is never perfected and gets no row. The denominator is the count of sections that have at least one note ("applicable" sections), not `Sections.Count`. If a player has no applicable sections, nothing is written or logged. |
| Harmony parts | `Profile.HarmonyIndex` is part of the key, so HARM1 and HARM2 never merge into one completion set. It is 0 for every non-harmony instrument. |
| Playing alongside a replay | `GlobalVariables.State.PlayingWithReplay` runs earn no credit for any player, matching the replays-never-count decision. |
| Songs without section markers | Counted normally. `SongChart.PostProcessSections` gives such charts 10 auto-generated percent buckets ("0% - 10%" ... "90% - 100%"), and those are used as-is. |

## UI surfaces (in priority order)

1. Results screen: a per-player score card line "Sections perfected this run: N of M", plus a tag when the run completes the set.
2. Library row: a "9/12" fraction beside the percent pill in `InstrumentDifficultyView`, distinct color at full completion.
3. In-game HUD: marker showing whether the current section is already perfected or still needed.

Sidebar checklist is deferred.

## Implementation slices

1. **Core + storage, log only.** Scan function, SQLite table, end-of-song hook. Verified by log output and DB inspection.
2. Results screen line and tag.
3. Library row fraction.
4. In-game HUD marker.
5. Settings toggle.

## Mechanism

Compute post-song in `GameManager.RecordScores`. For each eligible human player, do one merged linear walk of `Chart.Sections` against the player's `NoteTrack.Notes` (both tick-sorted). Per section, `total += engine.GetNumberOfNotes(note)`. The hit count follows the same chord semantics: when the engine counts a chord as one note, the chord counts as hit only if `note.WasFullyHit()`; when the engine treats chords as separate notes, each sub-note is counted individually on its own `WasHit`. Skip `IsBigRockEnding`. Use tick, half-open `[Tick, TickEnd)`; sections are contiguous by construction, so one advancing cursor suffices. Notes with tick below `Sections[0].Tick` go to index 0.

Vocals: walk phrases instead of notes; a phrase counts as hit if `WasHit`.

Persist one row per (SongChecksum, PlayerId, Instrument, Difficulty, HarmonyIndex, SectionIndex) that reached 100%, with first-completed date and the applicable section count at time of write. Insert-only; never delete on a later imperfect run. The rows are written at the same point as `ScoreContainer.RecordScore`, so section rows and score rows are saved together or not at all.

Research notes with file/line references: see the two reports produced 2026-09-02 (`research-sections-hits.md`, `research-scores-ui.md`) in the session scratchpad; key touch points are summarized below.

| Concern | Touch point |
|---|---|
| Section class | `YARG.Core/YARG.Core/Chart/Events/Section.cs`; list at `SongChart.Sections` |
| Hit flags | `Note.WasHit`, `Note.WasFullyHit()` in `YARG.Core/YARG.Core/Chart/Notes/Note.cs` |
| Chord counting | `BaseEngine.GetNumberOfNotes` / `TreatChordAsSeparate` |
| DB | `Assets/Script/Scores/ScoreDatabase.cs` (sqlite-net, `CreateTable<T>()`, no migrations) |
| Public API | `Assets/Script/Scores/ScoreContainer.cs` (static partial; add a `ScoreContainer.Sections.cs` part) |
| Hook | `Assets/Script/Gameplay/GameManager.cs` `RecordScores` |
| Section display names | `Assets/Script/Helpers/PracticeSectionHelper.ParseSectionName` |

## Slice 2 decisions (locked 2026-09-02)

Mockup: https://claude.ai/code/artifact/9fccb142-6aed-4378-96c6-98f07ea4da85 (variant 2 chosen, plus variant 3 for the completed state).

| Question | Decision |
|---|---|
| Layout | Stat row plus a segmented strip: one block per applicable section, colored "perfected this run", "perfected earlier", "still missing". |
| Row wording | `Sections perfected · 9 / 12 · +3` (cumulative fraction, then this run's newly perfected count). Label styled like the existing stat labels. |
| Tag priority | "Section FC" beats Full Combo, High Score, and Cleared. Bot and Replay still win over it. |
| Accent | A new, distinct hue added to the `ScoreCardColorizer` arrays (border, header text, colored text, tag text), in the violet family so it reads differently from the gold Full Combo. Do not hardcode colors in scripts; read them from the colorizer. |
| Data flow | Per-player results ride in `PlayerScoreCard` (`ScoreScreenContainer.cs`): applicable count, cumulative completed count after this run, newly perfected indices, and a per-section state list for the strip. Populated in `GameManager.EndSong` from the same scan used for persistence, so the card never re-queries the DB. |
| Denominator when no rows exist | Comes from the scan's applicable count, never from the DB. |
| Tag trigger | Only the run that closes the set shows the tag and the violet card (`ClosedSetThisRun`). The cumulative `IsSectionFullCombo` stays available for non-tag displays, so a completed song does not re-tag on every later run. |
| `+N` suffix | Omitted entirely when nothing was newly perfected; the row is just `9 / 12`. |
| Truthfulness | The card only shows section progress that was actually persisted. `RecordScores` reports whether it wrote, and `EndSong` clears `PlayerScoreCard.Sections` when it did not. |

## Slice 3 decisions (locked 2026-09-02)

Mockup: https://claude.ai/code/artifact/8a15aac4-f3d8-41fe-90a1-946c909e8755 (variant 1 chosen).

| Question | Decision |
|---|---|
| Placement | Fraction inside the existing instrument/difficulty pill, after the percent: `97% · 9/12`. Widen the pill as needed; no new prefab. |
| Visibility | Same rule as the percent: shown whenever the pill shows, in all `HighScoreInfo` modes. No extra gate. |
| No-rows state | Show `0/12` from the first valid run. Requires a per-(SongChecksum, PlayerId, Instrument, Difficulty, HarmonyIndex) summary row storing the applicable section count and the cumulative completed count, upserted on every valid run alongside the score. |
| Completion color | Violet matching the score card accent (colored text 0.678, 0.478, 1.0). A plain FC percent stays gold. |
| Difficulty shown | The fraction follows the difficulty of the high-score record the pill already displays, so the two figures always describe the same chart. |
| Loading | One bulk query per (player, instrument) cached in `ScoreContainer`, invalidated with the existing score cache, mirroring `FetchHighScores`. Never per-row DB reads. |
| Multiplayer rows | With two or more human players the row shows the band score and no pill; no fraction is shown there. |
| Missing summary row | The fraction disappears (no `·` segment at all) when the difficulty of the player's best score, or their current harmony part, has no summary row — a consequence of following the high-score difficulty rather than the profile's. The pill still shows the percent. |
| Pre-existing scores | Scores recorded before this feature shipped have no summary row, so they show no fraction until the player finishes another valid run on that chart. |

## Slice 4 decisions (locked 2026-09-02)

Mockup: https://claude.ai/code/artifact/9664aa50-7347-42ca-a66b-39a08a2d451a (variant 2 chosen).

| Question | Decision |
|---|---|
| Marker | Per-player mini-strip of all applicable sections above the highway's far end, one block per section, the current section enlarged with its display name. Equal-width blocks. |
| States | Perfected earlier (dim, quiet, no label), Needed (missing tone), Clean so far this run (violet), Dropped this run (red #E05265, label DROPPED). A dropped section is one where any applicable note was missed this run. |
| Current-section label | The section name, then a live percent of the section's notes hit so far while it is Needed or Clean — floored, so it only reads 100% once the last note is hit. The denominator is the scan's `NotesTotal` for that section, so it carries the engine's chord semantics. A miss replaces the percent with DROPPED for the rest of the run; a section perfected earlier shows its name alone. |
| Current-section detection | Advancing cursor on `SongTime >= section.TimeEnd`, same pattern as `PracticeHud`. Sections not applicable to the player's instrument are skipped in the strip but the cursor still walks the full list. |
| Live drop detection | Hook the player's note-missed path (`TrackPlayer.OnNoteMissed`; vocals phrase miss) and map the note tick to a section index. |
| Pre-run state | Loaded at song start from `ScoreContainer.GetCompletedSections` for the player's song/profile/instrument/difficulty/harmony. Ineligible runs (bot, replay, practice, playing-with-replay, invalid speed/modifiers) hide the strip. |
| Visibility | On by default. Draggable in single-player HUD edit mode under element name `sectionStrip`, following `DraggableHudElement`. Slice 5 adds the off switch. |
| Motion | One 150 ms ease when the current block changes; respect any existing reduced-motion or HUD animation settings. No other animation. |
| Placement | Its own container in `TrackView.prefab`, pinned above `Top Elements` so it never collides with the solo box or streak text. Scales with `ScaleContainer`. |
| Vocals | No strip in this slice; state is not built for vocals players. The miss hook exists for a later vocals HUD surface. |
