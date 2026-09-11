# Rewind to section during play

## What it is

From the pause menu, mid-song, the player picks a section they have already entered and returns to it. Song time jumps back to that section's start, their state is restored to what it was when they first entered it on the surviving timeline, and a short lead-in of audio plus highway plus countdown runs before judging resumes. Everything after the target is discarded and played again.

Domain terms (section, rewind target, section rewind, surviving timeline, lead-in, rewound run) are defined once in `CONTEXT.md` and used here without redefinition.

The decision record is the map issue, [Map: Rewind to section during play](https://github.com/djrobson5/YARG-fork/issues/2), with one grilling or research ticket per cluster (issues 3 through 8). Every decision below is locked; this document records them rather than reopening them. Code is cited by file path and member name, not line number.

## Scope and non-goals

v1 is **single player only**: the pause row is hidden whenever the run has more than one player, and hidden when the lone player is a bot. Out of scope: whole-band rewind with two or more local players (v2 if ever); forward skipping to sections the player has not reached; persisting the rewind badge on the stored score record; any change inside the `YARG.Core` submodule; replay format extensions beyond what a truncated input timeline already gives, including a rewound marker in the replay file or a sidecar.

## Pause row and picker (locked 2026-09-10, issue 4)

Mockup: `docs/prototypes/rewind-mockup.html` on branch `research/rewind-ui-mockup` (`?variant=A|B|C|D`, D chosen). Reviewer artifact: https://claude.ai/code/artifact/2cd523ab-fd26-4b65-8d83-47228dd0e5b9

| Question | Decision |
|---|---|
| Entry point | A `REWIND TO SECTION` row in the pause list after Restart: Resume, Restart, Rewind to Section, Practice, Save Replay, Settings, Back to Library. Same `GenericPauseOption` row, 58 px, RedHatDisplay-Black 40 px, `#3EB4FF46` selection wash. |
| Picker shape | Not a submenu. Confirming opens the section list in the pause page's empty 960-wide `Graphics Container` on the right; the pause list stays visible on the left at about 45% white, the Rewind row keeping its wash. Focus moves to the pane; Red/Back returns it without rewinding. |
| Navigation plumbing | Both list and pane are `NavigationGroup`s; opening the pane swaps the active group rather than pushing a `PauseMenuObject`, since the pane lives on the same page. Prior art: the `QuickSettings` third-level `Sub Settings Container`. The pane is a new prefab, and `Graphics Container` is empty in every pause prefab today, so nothing conflicts. |
| Ordering | Whole chart in chart order, Intro first. Rewind targets at full strength; sections ahead of the player dimmed (the practice picker's 15% white) and skipped by the cursor. Markerless charts list their ten auto-generated buckets. |
| Cursor start | On the current section, so one Confirm restarts the section in progress. Up walks back. |
| Row content | Result glyph at left in section-strip colours (clean `#AD7AFF`, dropped `#E05265`, in progress neutral ring `#B9BEE0`, unplayed `#262941`), section name via `PracticeSectionHelper.ParseSectionName` in RedHatDisplay-Black 40 px UpperCase, a cyan `CURRENT` chip on the current section only, start time `m:ss` right-aligned in a dimmer weight. |
| Scrolling | Practice-picker behaviour: selected row stays vertically centred, data slides through fixed slots, soft-mask fades top and bottom. No grouping, no scroll view, however long the chart. |
| Confirmation | None. Confirm on a row rewinds immediately. Help bar: Green `CONFIRM`, Red `BACK`, `UP`, `DOWN`. A quiet caption at the pane bottom states the lead-in ("Resumes N s before the section with a countdown"). |

Rejected: the submenu in the 750 column (loses the pause list, adds a Back row), the timeline scrubber (hides section names, needs Left/Right), newest-first ordering, a second confirm line, a `MODIFIERS` row icon, a bottom tag, and a rewind count.

## Lead-in (locked 2026-09-10, issue 5)

**Setting: Rewind Lead-In.** General > Gameplay, next to Practice Restart Delay, not advanced-only. `SliderSetting`, default **2 s**, range **0.5 to 5 s**, step **0.5**. The value is in **real seconds**: like `PracticeRestartDelay` and unlike the 1-second unpause rewind of `GameManager.RewindAndResume` (which is song seconds), the seek goes back `leadIn * SongSpeed` chart seconds, so wall-clock settle time is constant at any speed.

| Question | Decision |
|---|---|
| Transition | Hard cut, no scrub. The highway snaps to `sectionStart - leadIn` and the existing rewind SFX plays once. The unpause path's 0.5 s DOTween scrub is not used, because the jump can span minutes. |
| Audio | Full mix from `sectionStart - leadIn`, no fade-in, no stem muting, as in practice mode. If the window crosses time zero, song time goes negative and the mixer schedules silence until zero (`BassSong.SetPosition_Internal`, the normal pre-roll). No clamping. |
| Engine | Frozen for the whole lead-in via the existing `GameManager.Rewinding` gate, holding the restored section-start state. Judged notes before the marker do not respawn (see the HUD table); only a sustain crossing the boundary is shown, in held state. Inputs arriving during the window are **dropped**, not queued for resume the way `SendInputsOnResume` queues them on the unpause path. The engine goes live at the marker. |
| Countdown | Reuse the wait-countdown widget (`CountdownDisplay`: ring plus seconds, per track, vocal index 0), driven by the rewind and **forced on** regardless of the player's Countdown Display setting, counting to the **section marker** rather than the first note. If the first note is more than 9 s past the marker, the engine's own wait countdown takes over as usual. The widget's built-in 1.5 s early-hide and its Disabled style are both bypassed for lead-ins (issue 8), so the countdown runs all the way to the marker at every lead-in length. |
| Pause during the lead-in | Resume restarts the full lead-in from the same target; state is still frozen at section start, so nothing is lost. It is not a 1-second unpause rewind and does not count toward pause-abuse invalidation. One narrow exception found in implementation: `RestartLeadIn` goes through `SetSongTime`, which calls `UnisonDisplay.ResetState`, so a unison phrase straddling the marker loses its restored notes-hit count. The rewind itself is exact; only a pause taken inside the window does this. |

## What the player sees when it lands (locked 2026-09-10, issue 8)

Principle: **state-bearing readouts are exact, one-shot transients are skipped.** The re-simulation runs under the existing seek-suppression flag (`GameManager.IsSeekingReplay` or a rewind equivalent) so hit and miss SFX, haptics, camera punches and Star Power award/ready SFX do not re-fire while the recorded inputs replay. A fade with the rewind SFX covers the swap: everything is reset while hidden, and it fades back in at lead-in start. Per-element rules are tabulated below.

Settled in implementation (`GameManager.RewindToSectionBehindFade`, `RewindFadeOverlay`): a full-screen black plate on its own screen-space overlay canvas at the top of the sorting order, so it covers the highway, the HUD and the venue alike. **0.15 s out, 0.15 s in**, 0.3 s of fade in total, both smoothstepped - at that length a linear alpha ramp on a black plate reads as a shutter edge at each end - and driven off unscaled time, because the fade-out runs while the pause menu still holds `Time.timeScale` at zero. The order is: the picker closes, the pause menus are popped, `SfxSample.Rewind` plays once, the plate goes to black, the whole rebuild runs behind it, and the plate clears over the first 0.15 s of the lead-in. Audio is already playing under that fade-in, which is deliberate: the lead-in is seconds long and the countdown wants the music under it. The menus come down at the start of the fade rather than behind it, because the pause list has the navigation group back the moment the picker closes; the two pause bindings (Escape and `MenuAction.Start`) are gated on `GameManager.IsRewindFading` for the same reason.

**The fade does hold for the video handshake, but only when the handshake actually holds the run.** A song-source video answers `BackgroundManager.SetTime` a frame or more later; with Wait For Song Video on, the seek calls `GameManager.OverridePause()` and the run is frozen until the answer arrives, so fading in on a timer would show a still highway. `_videoSeeking` alone is raised for every in-range song-source seek and is therefore the wrong flag - with the setting off the run plays on underneath it, and holding on it would leave a black plate over a live song. The plate waits on the narrower `BackgroundManager.IsHoldingForVideoSeek`, latched beside the `OverridePause()` call and cleared with the answer, **capped at 0.5 s** so a video that never answers cannot leave the screen black. A pause taken inside the lead-in does **not** fade again: `RestartLeadIn` only moves the clock back inside a window the player is already looking at, and a plate on every resume would read as a stutter.

## Score page badge

A `REWOUND` pill in the score card's Engine Settings strip, beside `DEFAULT ENGINE` and `MODIFIERS ACTIVE`, styled as a `ColoredPillElement` with an amber tint. No modifier icon, no bottom-tag change, no rewind count. It is **session-only**: nothing is persisted on the score record, so a history entry or a saved replay of a rewound run is indistinguishable from a normal run.

Settled in implementation. The colour pair is read off the shape the existing engine-preset presets already use - the hue at 10% for the fill, the same hue at 50% for the outline, and a pale tint of it for the text - with the game's orange as the hue: fill and outline **`#FF8413`** at `0.098` / `0.502` alpha, text **`#FFB061`**, which is the `#FFB020` family member that sits in that scheme. The mockup's heavier `0.75` outline was dropped so the new pill does not shout louder than the two beside it. The preset is appended to `ColoredPillPreset` as `Rewound` rather than slotted in, because a pill's preset is stored as its index into the colour arrays on `Assets/Prefabs/Menu/Common/ColoredPillElement.prefab`.

**The strip needs no font or spacing change.** Measured in the editor at the authored 16 px: `DEFAULT ENGINE` 148.9 px, `MODIFIERS ACTIVE` 157.4 px, `REWOUND` 98.0 px, plus two 10 px gaps, is 424.3 px in a 430-wide strip. The one combination that does not fit is a custom engine preset (`CUSTOM ENGINE PRESET`, 201.8 px) with non-engine modifiers *and* a rewind, at 477 px; the horizontal layout group compresses the pills toward their minimum there and each pill's existing `Mask` clips its own text, so the row stays a row. Taking the font down for every card to buy that case was judged the worse trade.

## Replays (locked 2026-09-10, issue 6)

| Question | Decision |
|---|---|
| Content | The **surviving timeline only**. At each rewind the per-player input log (`BasePlayer._replayInputs`) is truncated at the rewind point and `GameManager.PauseInfo` is truncated at the same song time, which also drops the pause that opened the rewind menu. |
| Verification | The file is an ordinary single-timeline replay. `ReplayAnalyzer` re-simulates the same input list on a fresh engine and compares against the saved live stats, which come from the fresh engine the rewind constructed, so verification passes with no special casing. `ReplayLength` stays `InputTime` at save. |
| Marker | None, anywhere. `ReplayInfo`, `ReplayData`, `ReplayFrame` and the `Modifier` flags are `YARG.Core`-owned and hashed whole, so the file cannot carry one without a submodule edit; a sidecar was rejected as drift-prone. |
| Save availability | Save Replay stays available at all times, including after a rewind and while paused inside the lead-in; it saves the surviving timeline as it stands. End-of-song auto-save is unchanged. |

## Restore mechanism

A section rewind **constructs a fresh engine and re-simulates the truncated input log into it**, modelled on the replay seek path: `ReplayController.SetReplayTime` -> `GameManager.SetSongTime` -> `EngineManager.ResetState` -> `BasePlayer.SetReplayTime` (which calls `BaseEngine.ProcessUpToTime`) -> `TrackPlayer.ResetVisuals` -> `TrackView.ForceReset`. There are no snapshot types in `YARG.Core` and the fork does not add any.

**A fresh engine is mandatory.** `BaseEngine.Reset()` never clears the Star Power position block or `BaseTimeInStarPower`; `GuitarEngine.Reset()` leaves `InputButtonMask` / `LastButtonMask` / `StandardButtonHeld` set; the private `_scheduledUpdates` list is never cleared; and `KeysEngine.Reset` and `BaseEngine.Generic.Reset` do not clear `ActiveSustains`. Re-simulating on a reused engine diverges for real (guitar at 180 s: `NotesHit` 702 vs 700, `Overstrums` 0 vs 1, `TotalScore` 104,479 vs 104,082; `TimeInStarPower` roughly doubled on drums and vocals). On a fresh engine `ProcessUpToTime` reproduces every gameplay-visible stat exactly on drums and vocals, and everything on guitar except `SustainScore`, which wobbles by 3 to 6 points out of ~460,000 and does the same in a pure live control run, so it is update-cadence sensitivity in sustain scoring, not a replay defect. This is also a live bug in today's replay scrubbing, filed as [issue 10](https://github.com/djrobson5/YARG-fork/issues/10); the fork works around it rather than editing the submodule. The practice path already rebuilds `Engine = CreateEngine()` on section change, and construction costs well under a millisecond.

**Cost is not a concern.** A full reset-and-replay of a 12-minute dense Expert chart is about 30 ms on guitar, 7 ms on drums, 9 ms on vocals, linear in replayed inputs at roughly 1 to 2 us each. A section target is proportionally cheaper: one dropped frame inside a menu action that already does an audio seek.

### Ordered seek checklist

Inherited from the existing seek path: the song time move and audio seek (`GameManager.SetSongTime`), including negative time; `EngineManager.ResetState`, which also refills the rock meter; note pools, the track effects overlay (`TrackPlayer.ResetTrackEffectOverlay`), solo and BRE boxes, text notifications and countdown force-reset; Star Power path cursors via `TrackPlayer.ResetStarPowerPathCursors` (`SpPathIndex`, `_spPlan*Index`, `_spHudIndex`, `_spPhrasesLost`, `SpPathDiverged`); lyric bar, unison display, beat events, camera cuts and venue characters.

What the rewind must add, in order:

1. **Fresh engine wiring.** Every event subscription made in the player's `CreateEngine` must be re-established on the new engine, and `EngineManager` repointed at it. The replay viewer never had to do this because it reuses its engine.
2. **Seek suppression during re-sim.** `GameManager.IsSeekingReplay` is load-bearing: `TrackPlayer.OnNoteHit` / `OnNoteMissed` gate SFX, haptics, stem muting and the FC banner on it. Without an equivalent flag the rewind fires thousands of hit sounds.
3. **Lead-in freeze with inputs dropped.** Hold the engine under `GameManager.Rewinding` from the landing until song time reaches the marker, discarding inputs received in the window rather than queueing them.
4. **Button-state resend at the marker.** Feed the current physical button state to the engine as the existing resume path does, so a sustain crossing the boundary keeps ticking if the frets are held and drops under normal rules if not.
5. **Truncation.** `ProcessUpToTime` returns the consumed input index (the value `SetReplayTime` stores in `_replayInputIndex`); truncate `_replayInputs` there and truncate `GameManager.PauseInfo` at the same song time.
6. **Section strip rewind.** `SectionStripState` is fed from `TrackPlayer.OnNoteHit` / `OnNoteMissed`, which the engine re-fires during `ProcessUpToTime`. `OnNoteMissed` is idempotent, but `OnNoteHit` does `_blockHits[block] += count`, so every earlier block's progress would roughly double without a deliberate rewind, and `_sectionCursor` never regresses. End-of-song credit is safe either way: `SectionCompletionScanner` reads `Note.WasHit`, which `Reset()` clears and re-simulation re-establishes.
7. **Invalidation bypass.** `GameManager.InvalidateScores` calls `player.SetSectionState(null)`, so the rewind must bypass `CheckForRewindInvalidation` and handle section state deliberately rather than inheriting that side effect.

Smaller traps from the research: `ProcessUpToTime` enqueues into `InputQueue` directly and leaves `LastQueuedInputTime` at `double.MinValue`, disarming the out-of-order guard for the first input after a seek (harmless today); the recorded input log is monotonic only because `BasePlayer.OnGameInput` runs `QueueInput` before `_replayInputs.Add`, so any future path that appends without `QueueInput` silently breaks live/replay parity. The monotonic-time assertion is not a blocker: `UpdateTimeVariables` logs "Time cannot go backwards!" through `YargLogger.FailFormat` without throwing, and `Reset()` sets `CurrentTime = double.MinValue` first. The trap is touching the engine at the rewound time *before* `Reset()`.

## Fork-owned pieces that need new code

Roughly in dependency order, for later sessions to slice from:

1. Fresh-engine construction plus subscription and `EngineManager` re-pointing.
2. Rewind-time seek suppression flag, input-log and `PauseInfo` truncation.
3. `SectionStripState` rewind (rebuild via `Create` + `SetSectionState` with no DB re-query, or a `RewindTo(index)` that regresses the cursor).
4. Backwards seek on `LightManager` and `StageManager` (a `ResetTime` that rescans from zero, as `CameraManager.ResetTime` does). Also fixes the replay viewer.
5. `VocalTrack` seek modelled on its practice reset (`VocalTrack.ResetPracticeSection`): pools, lyric container, talkie pool, pitch range, cursors rescanned to the lead-in time; plus resetting `VocalsPlayer._phraseIndex`.
6. Countdown widget driven by the rewind, with the Disabled style and the early-hide bypassed for lead-ins.
7. Respawn of a sustain crossing the section boundary, in held state.
8. Reset of the latched Star Power trim overlay and the `ChangeStarPowerStatus` reverb counter, and a guard on deploy/release SFX during re-sim.
9. Clearing latched view state: `TrackView._isSoloActive` / `_isUnisonActive` / `_isCodaActive`, and `TrackPlayer._unisonStartIndex` / `_unisonEndIndex` / `_breIndex` / `CurrentCoda`.
10. Rewind row and picker pane prefabs on the quick-play pause menu, plus the same row on `FailPause` and `SetlistPause`.
11. Rewind SFX plus the ~0.3 s fade.
12. `REWOUND` pill on the score card.
13. The Rewind Lead-In setting.

## Interactions with existing fork features

| Feature | Interaction |
|---|---|
| Section FC | Rewinding resets section-FC status for the target section and everything after it; earlier sections keep theirs. The rewind row's eligibility reuses the existing gate rather than inventing a second one. |
| Star Power path | Cursors reset and `SpPathDiverged` cleared (already on the seek path); **no recompute**, since `SpPathOptimizer.Optimize` is a pure function of the chart and the plan survives a rewind. Markers respawn from the target. |
| Pause-abuse invalidation | A rewind resume uses the lead-in instead of the 1-second unpause rewind and does not count toward the pause count, so `CheckForRewindInvalidation` is bypassed on that path and the opening pause is dropped from `PauseInfo`. |
| Scores | A rewound run is a **normal high score**. The four existing invalidation sites (resume-after-fail and no-fail in `GameManager`, too-many-pauses in `CheckForRewindInvalidation`, auto-calibration in `AutoCalibrator`) are untouched, and `ScoreContainer.IsBandScoreValid` / `IsSoloScoreValid` keep their current meaning. |

## Rule table: eligibility and edge cases (locked 2026-09-10, issue 7)

| Situation | Rule |
|---|---|
| Quick play | Available. |
| Setlist | Available in every song; each song is its own run and rewinds never cross songs. The setlist pause prefab gets the row too. |
| Practice mode, replay playback | Never offered. Prefab-authored pause menus, so absent naturally. |
| Multiplayer, bot | Hidden with more than one player, and hidden when the lone player is a bot. |
| Fail menu | Offered. The run stays a normal high score, unlike the existing resume-after-fail which invalidates. |
| Rock meter | Full after every rewind: the engine manager resets it on any seek and the fork does not track it separately. |
| Song speed | No restriction. Recorded input times are already song-time, so re-sim needs no conversion; sub-1.0 speed is already not a valid high score. |
| Song ended | Offered for as long as the pause menu can open, including the end-delay tail after the last note. Once the end-of-song flow starts, nothing is offered. |
| Cap | None, per run or per section. |
| Current section | A valid target (restart-section); the cursor starts there. |
| First section | Rewinds to song start, since it owns the intro. Matches section-FC's index-0 folding; negative song time is already accepted. |
| Section starting inside a BRE/coda | Not a target: dimmed and skipped like an unplayed section, because coda lane timers and the success latch cannot be re-entered cleanly without a `YARG.Core` edit. Sections before the BRE are fine; the fresh engine rebuilds the coda. |
| Star Power active at the target's start | Normal target. SP resumes at the marker with its remaining duration intact, since the engine is frozen through the lead-in and nothing drains. |
| Target boundary inside a solo | Normal target. The solo continues with the restored notes-hit count and the bonus is awarded at solo end from the surviving timeline. |
| Sustain crossing the boundary | Re-sim rebuilds the held sustain; at the marker the current physical button state is fed to the engine. Held frets keep it ticking, otherwise it drops under normal rules. |
| Keys sustains | The reason the fresh engine is mandatory: `KeysEngine.Reset` and the generic reset do not clear `ActiveSustains`. |

## Rule table: HUD and highway elements (locked 2026-09-10, issue 8)

| Element | Rule |
|---|---|
| Combo meter, multiplier, streak, score box, stars | Per-frame from the fresh engine; self-heal. Stars already unwind downward. Nothing to add. |
| SP bar, sunburst, track SP mode | Per-frame; self-heal. Add: reset the latched SP-trim overlay and the `ChangeStarPowerStatus` reverb counter, and guard deploy/release SFX during re-sim. |
| Section strip | Sections before the target keep their status, with one exception: a note whose tick is before the marker but whose hit input fell after it loses that input to the truncation, so it is missed on the replay and drops the block it belongs to. That is correct - the strip follows the surviving timeline, and agrees with `Note.WasHit` and the saved replay. The target and everything after go back to the unplayed look. The highlight sits on the target from the moment the rewind lands, through the lead-in. No rewind marker on the strip. Needs the new `SectionStripState` rewind. |
| Notes on the highway | Seek behaviour accepted: judged notes before the marker do not respawn (hit notes gone, missed notes absent). Exception: a sustain crossing the boundary respawns in held state. |
| Track effects (solo, unison, SP phrase, drum fill) | Already rebuilt by `ResetTrackEffectOverlay`. |
| Solo box, BRE box, text notifications, countdown | Force-reset on the seek path already. Add: clear the latched `TrackView` and `TrackPlayer` flags listed above. The solo box re-shows with the restored notes-hit count at the marker. |
| Lead-in countdown | `CountdownDisplay` driven every frame with (leadIn, sectionStart); its 1.5 s early-hide and the Disabled style are both bypassed for lead-ins. |
| Lyric bar, unison display, beat events, camera cuts, venue characters | Already seek with `GameManager.SetSongTime`. |
| Background video | Seek to lead-in start via `BackgroundManager.SetTime`, song-source videos only. A hitch from the video handshake is accepted. |
| Venue lights and stage cues | Add a backwards seek to `LightManager` and `StageManager`, rescanning from zero. Also fixes the replay viewer. |
| Vocals | In v1. Mic inputs are in the input log and re-sim exactly. Add the `VocalTrack` seek and reset `VocalsPlayer._phraseIndex`. Accepted in implementation: a vocal note straddling the landing does not return until the next element, and there is no vocals equivalent of the held-note respawn the highway gets. |
| Star Power path | Cursors reset, `SpPathDiverged` cleared, no recompute. Markers respawn from the target. |
| One-shot notifications (Hot Start, Bass Groove, new high score) | Shown once per run, never re-shown. No flags reset. |
| Drums | Nothing extra; call `ResetLastHitTimes` for tidiness. |

## Open questions

None. Everything the tickets left to implementation has been settled and recorded above.

## Research assets and prior art

- `research/rewind-resim`: the determinism and cost harness, `tools/RewindSimBench/` (`Program.cs`, `LongChart.cs`, `InputSynth.cs`, `Snapshot.cs`). Run with `dotnet run -c Release --project tools/RewindSimBench`. Not referenced by any Unity asmdef; `YARG.Core` untouched. Neither the section strip nor the SP path cursors are reachable from it (both live in `Assembly-CSharp`), so those findings are read from code. The chart is synthetic (no BRE, coda, unisons, lanes or trills) and keys, pro-keys, six-fret and elite drums were not measured.
- `research/rewind-ui-mockup`: `docs/prototypes/rewind-mockup.html`, four variants of the pause row, picker and score badge, built from real prefab values.
- Map issue: https://github.com/djrobson5/YARG-fork/issues/2, with tickets 3 through 8 linked from it.
- Bug: https://github.com/djrobson5/YARG-fork/issues/10, the incomplete `BaseEngine.Reset()` that makes the fresh engine mandatory and that also affects today's replay scrubbing.
- Upstream prior art: the 1-second unpause rewind (`GameManager.RewindAndResume`, `SongRunner.RewindAndResume`, `BasePlayer.Rewind`); replay seeking (`ReplayController.SetReplayTime`, `BasePlayer.SetReplayTime`, `BaseEngine.ProcessUpToTime`); practice section rebuild (`PracticeManager`, `TrackPlayer.SetPracticeSection` / `ResetPracticeSection`); pause menus as `PauseMenuObject` children of `PauseMenuManager`.
