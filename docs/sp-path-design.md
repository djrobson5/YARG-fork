# Optimal Star Power path overlay

## What it is

An optional highway overlay that marks where a perfect player should activate Star Power
to maximise score — the thing CHOpt computes for Clone Hero, computed for YARG's own
scoring model and drawn in-game.

Background research, the approach comparison and the risk list live in
`docs/roadmap.md` → "Feature 3 — Mark optimal Star Power activation points on the
highway". This document records the locked decisions, the exact scoring model the
optimizer has to replicate, the verification story, and the plan they imply.

Every claim below is backed by a `file:line`, or explicitly marked **unverified**.
Line numbers are against the tree at `feature/section-fc` @ `2b4a11c5`.

## Decisions (locked 2026-09-03)

| Question | Decision |
|---|---|
| Where the optimizer lives | **Unity-side, `Assets/Script`.** The `YARG.Core` submodule is never modified. Scoring constants are duplicated, with a permanent bot-run verification test as the drift guard. |
| Recompute on deviation | **No.** One static perfect-play reference computed at song load. Markers **dim** once the player's actual SP state diverges from the plan; they are never recomputed mid-song. |
| Assist status | **Not an assist.** The overlay does not invalidate high scores and does not invalidate Section FC credit. |
| Instruments | **5-fret guitar/bass first.** Drums later (different activation model). Vocals never. |
| SP model | **Phrases only.** No whammy gain. Full combo assumed. No squeezes. Activation exactly on a note. |
| Multiplayer | **Single player only.** Hidden whenever the run has 2+ human players, with the pause menu / setting text explaining why (band SP is coupled across players — `YARG.Core/YARG.Core/Engine/EngineManager.Band.cs:17`, `AwardUnisonBonus` at `BaseEngine.cs:637`). |

The rendering and UI questions that were deferred to a mockup interview were settled on
2026-09-03 and are locked in "Locked UI decisions" below.

---

## 1. The scoring model to replicate (5-fret)

### 1.1 Constants that must be duplicated

All of these are `protected const` or `protected readonly` on `BaseEngine`, so nothing in
`Assets/Script` can read them. Each one is re-declared in the optimizer and pinned by the
verification test.

| Constant | Value | Source |
|---|---|---|
| `POINTS_PER_NOTE` | `50` | `YARG.Core/YARG.Core/Engine/BaseEngine.cs:11` |
| `POINTS_PER_PRO_NOTE` | `60` (`POINTS_PER_NOTE + 10`) | `BaseEngine.cs:12` — not used by 5-fret; listed so a later pro-guitar/keys extension does not rediscover it |
| `POINTS_PER_BEAT` | `25` | `BaseEngine.cs:13` |
| `STAR_POWER_MAX_MEASURES` | `8` | `BaseEngine.cs:17` |
| `SUSTAIN_BURST_FRACTION` | `4` | `BaseEngine.cs:20` |
| `TicksPerQuarterSpBar` | `SyncTrack.MeasureResolution * 2` (2 measures) | `BaseEngine.cs:168` |
| `TicksPerHalfSpBar` | `TicksPerQuarterSpBar * 2` (4 measures) | `BaseEngine.cs:169` |
| `TicksPerFullSpBar` | `TicksPerQuarterSpBar * 4` (8 measures) | `BaseEngine.cs:170` |
| `TicksPerSustainPoint` | `SyncTrack.Resolution / (double) POINTS_PER_BEAT` | `BaseEngine.Generic.cs:106` |
| `SustainBurstThreshold` | `SyncTrack.Resolution / SUSTAIN_BURST_FRACTION` | `BaseEngine.Generic.cs:107` |
| Solo bonus rate | `100` points per solo note | `BaseEngine.Generic.cs:1196`, `:1315` |
| Solo bonus floor | rounded down to a multiple of `50` | `BaseEngine.Generic.cs:1199` |
| Solo bonus cutoff | `< 0.6` hit ratio pays nothing; above that scaled by `clamp((pct - 0.6)/0.4, 0, 1)` | `BaseEngine.Generic.cs:1186-1194` |

Read from the live engine/profile, **never** hardcoded:

| Value | Where to read it | Note |
|---|---|---|
| `MaxMultiplier` | `BaseEngineParameters.cs:13`, via `player.BaseEngine.BaseParameters.MaxMultiplier` | `EnginePreset.Instruments.cs:17-18` sets `DEFAULT_MAX_MULTIPLIER = 4`, `BASS_MAX_MULTIPLIER = 6`; picked at `:149`. **Bass genuinely reaches 12x under SP.** |
| `NoStarPowerOverlap` | `Guitar/GuitarEngineParameters.cs:15,32` | Default is `false` (Clone Hero-like: phrases collected during SP still count). Consumed at `Guitar/GuitarEngine.cs:259-261`. |
| `SongSpeed` | `BaseEngineParameters.cs:25`, set by `BaseEngine.SetSpeed` (`BaseEngine.cs:599-605`) | Scales only the hit window and the whammy timer. **It does not touch ticks, points or SP drain, so it cannot change the optimal path.** Modelled as a no-op. |
| Note track after modifiers | `Assets/Script/Gameplay/Player/TrackPlayer.cs:238` (`player.Profile.ApplyModifiers`) | The optimizer must run on the post-modifier `NoteTrack`, not the raw chart. |

### 1.2 Note scoring

`Guitar/GuitarEngine.cs:335-340`:

```csharp
int notePoints = POINTS_PER_NOTE * (1 + note.ChildNotes.Count);
```

Guitar constructs with `isChordSeparate: false` (`Guitar/GuitarEngine.cs:52`), so
`GetNumberOfNotes` returns 1 per chord (`BaseEngine.cs:186-189`) — a chord is one combo
step but pays `50 × chord size`.

Order inside `HitNote` matters (`Guitar/GuitarEngine.cs:257-276`): SP is awarded first,
then `IncrementCombo()`, then `UpdateMultiplier()`, **then** `AddScore(note)`. So a note
is scored at the multiplier its own combo increment produced.

`AddScore(int)` (`BaseEngine.Generic.cs:781-806`):

```csharp
int scoreMultiplier = score * EngineStats.ScoreMultiplier;
EngineStats.CommittedScore += scoreMultiplier;
```

The `scoreMultiplier / 2` integer division at `:793` only splits the **`StarPowerScore`
stat**; it does not affect `CommittedScore`. Same for the `sustainPoints /= 2` at `:921`,
which only feeds the `SustainScore` stat. **Correction to the roadmap's framing:** the
"SustainPoints must include the multiplier, but NOT the star power multiplier" comment
(`BaseEngine.Generic.cs:917`) describes a *stat breakdown*, not the real score. Sustains
are fully SP-doubled in `TotalScore`. The optimizer only needs `TotalScore`
(`BaseStats.cs:34` = `CommittedScore + PendingScore + SoloBonuses + CodaBonuses`), so
neither halving is modelled.

### 1.3 Multiplier progression

`BaseEngine.cs:447-457`:

```csharp
BaseStats.ScoreMultiplier = Math.Min((BaseStats.Combo / 10) + 1, BaseParameters.MaxMultiplier);
if (BaseStats.IsStarPowerActive) BaseStats.ScoreMultiplier *= 2;
```

Combo counts chords, not notes. Under the full-combo assumption the pre-SP multiplier is a
pure function of note index: `min(i/10 + 1, MaxMultiplier)` where `i` is the number of
combo steps taken *before* this note — exactly what `CalculateChartScores` already does at
`Guitar/GuitarEngine.cs:414`. That means **the entire base-score curve is a prefix sum over
the note list, computable once.** SP only ever doubles a contiguous run of it.

### 1.4 Sustain scoring — the easily-missed part

`CalculateSustainPoints` (`BaseEngine.Generic.cs:1354-1364`) accumulates raw points at
`(scoreTick - BaseTick) / TicksPerSustainPoint`, and `RebaseSustains`
(`:1249-1271`, triggered by every multiplier change via `BaseEngine.cs:456` and
`Guitar/GuitarEngine.cs:342-357`) folds the accrued amount into `BaseScore` without
applying any multiplier. **Rebasing is score-neutral**; it exists to keep the on-screen
`PendingScore` smooth.

The whole sustain is therefore committed **once**, at its burst, with the multiplier
current at that instant (`BaseEngine.Generic.cs:898-912`):

```csharp
double finalScore = CalculateSustainPoints(ref sustain, sustainTick);   // sustainTick == note.TickEnd
var points = (int) Math.Ceiling(finalScore);
AddScore(points);
```

The commit tick is:

- **Long sustain** (`TickLength >= SustainBurstThreshold`): `note.TickEnd - SustainBurstThreshold`, i.e. a quarter-beat early (`:857-864`). The engine explicitly queues an update at that time (`:156-161`), so the commit lands there exactly.
- **Short sustain** (`TickLength < SustainBurstThreshold`): burst is true as soon as `CurrentTick >= note.Tick` (`:859`), so it commits on the first engine update at or after the note.

Either way `sustainTick = note.TickEnd` (`:868`), so the point total is always the full
`Math.Ceiling(TickLength / TicksPerSustainPoint)`.

**Consequence for the DP:** a sustain crossing the end of a Star Power window is scored
entirely by whether its *burst tick* falls inside the window, not by how much of the
sustain overlapped it. A long sustain that starts one tick before SP ends is fully
un-doubled; one that ends a quarter-beat after SP ends is fully doubled. This is a genuine
discontinuity and a place a naive implementation silently diverges.

Disjoint chords count each child sustain separately, with combo incremented once per
distinct child tick (`Guitar/GuitarEngine.cs:424-439`).

### 1.5 Star Power gain, drain and window

- **Gain:** completing a phrase awards exactly `TicksPerQuarterSpBar` — `AwardStarPower` (`BaseEngine.Generic.cs:1158-1163`) → `GainStarPower` (`BaseEngine.cs:522`). Four phrases fill the bar. Clamped to `TicksPerFullSpBar` at `BaseEngine.cs:532-535`.
- **Unison bonus:** a phrase that is part of a *unison* pays a **second** `TicksPerQuarterSpBar`. `AwardStarPower` raises `OnStarPowerPhraseHit` right after gaining the first quarter (`BaseEngine.Generic.cs:1158-1163`); `EngineManager.OnStarPowerPhraseHit` (`EngineManager.UnisonEvent.cs:336-360`) finds the matching `UnisonEvent`, and once every participant has cleared it sends `AwardUnisonBonus` to each (`BaseEngine.cs:637-641`), which is another `GainStarPower(TicksPerQuarterSpBar)`. So the order is *phrase, clamp, unison, clamp* — two clamped quarter-bar gains, which lands on the same number as one clamped half-bar gain, and while Star Power is active it runs the window-extension line twice. Modelled since 2026-09-04; see "Unison bonuses, modelled" below.
- **Credited at the note carrying `IsStarPowerEnd`** (`Guitar/GuitarEngine.cs:263-267`), *not* at `Phrase.TickEnd`. `PhraseType.StarPower` is documented as visual-only; the authority is `NoteFlags.StarPower` on the note.
- **Overlap:** with `NoStarPowerOverlap == true`, a phrase hit while SP is already active is stripped instead of awarded (`Guitar/GuitarEngine.cs:259-261`). Default false.
- **Drain:** `CalculateStarPowerDrain(measureTick, lastMeasureTick) => measureTick - lastMeasureTick` (`BaseEngine.Generic.cs:1073-1076`) — 1:1 with measure ticks. A full bar always lasts exactly 8 measures of chart time, tempo-independent and **meter-aware**. CHOpt's flat-beat model diverges on any chart with a time-signature change, so CHOpt is a cross-check, never an oracle.
- **Position:** `StarPowerTickPosition = SyncTrack.QuarterTickToMeasureTick(CurrentTick)` (`BaseEngine.Generic.cs:996`, `SyncTrack.cs:325`).
- **Window end:** `StarPowerTickEndPosition = StarPowerTickPosition + BaseStats.StarPowerTickAmount` (`BaseEngine.cs:555`). This is a **pure function** — no simulation needed. Gaining a phrase while active re-runs the same line (`BaseEngine.cs:543-547`), so the end extends by exactly one quarter bar, subject to the full-bar clamp:

  ```
  E ← min(E + TicksPerQuarterSpBar, m + TicksPerFullSpBar)      // m = measure tick of the phrase-end note
  ```

- **Release:** when the amount reaches 0 (`BaseEngine.Generic.cs:1029-1032`), at the update queued for `StarPowerEndTime` (`:286-290`).
- **Activation:** `if (IsStarPowerInputActive && CanStarPowerActivate) ActivateStarPower();` (`BaseEngine.Generic.cs:1034-1037`), with `CanStarPowerActivate => StarPowerTickAmount >= TicksPerHalfSpBar` (`BaseEngine.cs:44`). **Half a bar minimum.**
- **Ordering:** `RunEngineLoop` runs `UpdateStarPower()` *before* `UpdateHitLogic()` (`BaseEngine.cs:400-405`). So a note hit in the same engine loop as the activation **is** doubled. The optimizer's contract is therefore "activate on note N" ⇒ *N is the first note scored under SP*, and the activation runs at `CurrentTick == Notes[N].Tick`. Confirmed in slice 3; see the "Settled activation semantics" table below for the harness detail this forced.
- **Boundary rule — confirmed in slice 3.** A note whose scoring tick maps to a measure tick `>= E` is **not** doubled, because the release runs first in the loop at `StarPowerEndTime`. The window is the half-open measure-tick interval `[m, E)`, and both directions are asserted: every doubled award is inside it, and every award inside it is doubled (`SpSemanticsTests.ActivateAtNoteN_DoublesFromNInclusiveToEExclusive`).

### 1.6 Things that are constants under full combo, and are therefore ignored

- **Solo bonuses.** `EndSolo` (`BaseEngine.Generic.cs:1181-1204`) pays `100 × NotesHit × multiplier`, floored to 50, into `SoloBonuses`. Not combo-scaled, not SP-scaled. Under full combo this is a fixed offset for every path.
- **Coda / BRE bonuses.** `AwardCodaBonus` (`BaseEngine.cs:643-652`) adds `CurrentCodaBonus` to `CodaBonuses`; BRE notes are excluded from scoring and from `TotalNotes` (`BaseEngine.Generic.cs:76-98`, `Guitar/GuitarEngine.cs:407-409`). Also a fixed offset. **Unverified** that no coda path is SP-multiplied; the harness compares totals, so a mismatch would surface there.
- **Band bonus.** `BandBonusScore` / `BandBonusMultiplier` (`BaseStats.cs:90`) only matter in multiplayer, which the feature does not support.
- **Star thresholds.** `PopulateStarScoreThresholds` uses `Math.Floor` (`BaseEngine.Generic.cs:126`); irrelevant to path choice, but the optimizer must not accidentally report stars.
- **Whammy.** `CalculateStarPowerGain` (`BaseEngine.Generic.cs:1059-1070`) with `GAIN_FACTOR = 32/30` and a `tickRemainder` carried between frames. **Explicitly out of the model.** The UI must say so (open question 5).

---

## 2. The optimizer

### 2.1 Precomputation

Walk the post-modifier note list once, mirroring `Guitar/GuitarEngine.CalculateChartScores`
(`:399-444`), and build parallel arrays over combo-step index `i`:

- `mult[i] = min(i/10 + 1, MaxMultiplier)` — the un-doubled multiplier for note `i`.
- `notePts[i] = 50 * (1 + ChildNotes.Count)`, awarded at measure tick `M(note.Tick)`.
- `sustainPts[i] = ceil(TickLength / TicksPerSustainPoint)` (plus each disjoint child's own ceil), awarded at measure tick `M(burstTick(i))`.
- `phraseEnd[i]` — true when the note carries `IsStarPowerEnd`.

Convert every quarter tick to a measure tick with `SyncTrack.QuarterTickToMeasureTick`
(`SyncTrack.cs:325`) and every measure tick back with `MeasureTickToTime`
(`SyncTrack.cs:375`). **Never** call `FindMinTimeForMeasureTick` (`SyncTrack.cs:385-455`)
in a loop — it is a ~100-iteration binary search whose predicate itself binary-searches
the tempo list. It is correct to use once per emitted marker, for display.

Because the doubling is a multiply-by-2 of an interval of these award events, define two
prefix sums over award events sorted by award measure tick:
`P[k] = Σ_{j<k} pts[j] * mult[j]`. Then the score of any SP window `[a, E)` is
`P_total + (P[idx(E)] - P[idx(a)])` — a single extra copy of the doubled interval. Every
window's value is O(1) after two binary searches.

### 2.2 State space

With phrases-only gain, the SP meter takes exactly five values — 0, ¼, ½, ¾, full — so
the "not active" state is `(note index, meter ∈ {0..4})`. The "active" state adds a window
end `E`, but `E` is fully determined by the activation point and the set of phrase-end
notes crossed since, all of which are chart-fixed and monotone: from an activation at note
`a` with meter `q`, replaying forward through the phrase ends yields a single deterministic
`E`. So an activation is a **closed-form transition**, not a search:

```
solve(i, q):                        # i = next note index, q = meter in quarter-bars, 0..4
    best = solve(nextPhraseEnd(i) → i', min(q+1, 4))       # collect the next phrase, don't activate
    if q >= 2:                                              # CanStarPowerActivate
        for each candidate activation note a >= i:
            (E, i_after, q_after) = simulateWindow(a, q)     # closed-form, walks phrase ends inside the window
            best = max(best, doubledGain(a, E) + solve(i_after, q_after))
```

Memoised on `(i, q)`. The inner activation loop is bounded by the notes between `i` and
the next state change, so the total work is O(notes × 5 × candidatesPerState). In practice
only notes at or after the tick where the meter last changed are legal activation points,
and only the ~4 SP windows per bar-fill matter; a straightforward bound is
**O(notes² / something small)** worst case but O(notes × 5) with the standard trick of
only considering activations at notes where the doubled-interval boundary changes.

Typical Expert 5-fret chart: 1,000–2,500 notes, 15–40 SP phrases. States: ≤ 12,500.
Transitions: ≤ ~10 per state after candidate pruning. Measured at **~2 ms** in a Debug build with
no pruning at all (see slice 3's progress note), fully hidden behind the loading screen.

**Tie-break policy: the earliest activation wins.** Two activation sets can score identically —
the doubled interval is worth the same in two places, and on a chart with even note density that
happens often. A human has to *hit* the marker, so among optimal paths the earliest activation is
the safest one: it leaves more chart behind it to absorb a late tap, and a marker already passed is
worse than one not yet reached. The DP implements it by taking the "activate here" branch on `>=`
rather than `>` (`SpPathOptimizer.cs`), which cannot introduce a pointless zero-gain window because
the activation note is itself inside `[m, E)` and so always pays at least `POINTS_PER_NOTE`. It
stays deterministic either way: the scan over `(i, q)` is in fixed order, so a chart always yields
the same path. Note that this policy can produce *more* activations than a later-biased one at the
same total score — on `drawntotheflame.mid` it takes seven windows where the later-biased tie-break
took six, for the same 392,750.

Exactness caveat: the DP is exact **for the modelled subset** (full combo, phrases only,
no squeezes, activation on a note). It is not exact for real play.

### 2.3 Mapping activation ticks to notes

An activation is emitted as the **note index** it fires on, not a bare tick, because:

- the rendering needs a `TrackElement`-compatible time anchored to something the player can see;
- the "dim when diverged" check needs to compare the player's meter at that note;
- the engine's own activation lands on a loop boundary, and the note is the only stable identifier across a practice-section rebuild.

### 2.4 Output type

```csharp
public sealed class StarPowerPath
{
    public IReadOnlyList<Activation> Activations { get; }
    public int ProjectedScore { get; }                  // TotalScore of a perfect run following this path
    public int ScoreGainOverNoActivations { get; }      // how much it beats never activating
}

public readonly struct Activation
{
    public readonly int    NoteIndex;              // index into TrackPlayer.Notes
    public readonly uint   ActivationTick;         // quarter tick of that note
    public readonly uint   ActivationMeasureTick;  // m — the window start, inclusive
    public readonly uint   EndMeasureTick;         // E — exclusive
    public readonly double ActivationTime;         // SyncTrack.TickToTime, for rendering
    public readonly double EndTime;                // FindMinTimeForMeasureTick(E) — once, here, not in a loop
    public readonly int    MeterAtActivation;      // 2..4 quarter bars, for the divergence check
    public readonly int    ScoreGain;              // points this window adds over not activating
    public readonly int    ScoringNoteIndex;       // index into ScoreModel.ScoringNotes (model bookkeeping)
}
```

Two differences from the sketch this section originally carried, both settled by slice 3: the
"gain" figure is `ScoreGainOverNoActivations` rather than a `GreedyScore` (the greedy bot's path is
not note-aligned, so it is not a number the path object can produce — see "Settled activation
semantics"), and `Activation` carries the measure ticks and the `ScoringNoteIndex` because the
window model and the renderer need different coordinate spaces.

Plain C#, no `UnityEngine` types — see §3.

---

## 3. Verification harness

### 3.1 What exists

| Asset | Location | Usable? |
|---|---|---|
| YARG.Core unit tests | `YARG.Core/YARG.Core.UnitTests/` (NUnit 4.5.1) | **Inside the submodule** — off limits under the locked decision. |
| Headless engine construction | `YARG.Core.UnitTests/Engine/EngineTester.cs:34-49` | The pattern to copy: `MidiFile.Read` → `SongChart.FromMidi` → `new YargFiveFretGuitarEngine(...)`. |
| Engine subclassing for test hooks | `YARG.Core.UnitTests/Engine/GuitarEngineTester.cs:68-80` | Exactly the trick needed — a sealed subclass reaching protected members. |
| Test chart | `YARG.Core/YARG.Core.UnitTests/Engine/Test Charts/drawntotheflame.mid` | Readable from outside the submodule; no modification needed. |
| Replay analyzer | `YARG.Core/YARG.Core/Replays/Analyzer/ReplayAnalyzer.cs` | Drives engines from recorded inputs; wrong shape here (needs a real replay file). |
| `ReplayCli` / `TestConsole` | `YARG.Core/ReplayCli/ReplayCli.csproj:5`, `TestConsole.csproj:5` — both `net8.0` | Proof that a plain .NET console/test project against `YARG.Core` builds and runs. |
| Unity Test Framework | **Absent.** `Packages/manifest.json` has no `com.unity.test-framework`, and there is no `Assets/Tests`. | Adding it means a package + asmdefs + a batchmode Unity run per test — minutes, not seconds. |

Two blocking facts:

1. `YARG.Core.UnitTests.csproj` targets **`net10.0`**, and the local SDK is **8.0.424** (`dotnet --list-sdks`). The existing test project does not build on this machine at all.
2. `YARG.Core` is `netstandard2.1` with `"noEngineReferences": true` on its asmdef, so it is genuinely Unity-free — but `Assembly-CSharp` is not, so a test project cannot reference the optimizer through the Unity assembly.

### 3.2 Recommended approach

**A fork-owned NUnit project at `Tools/SpPathTests/SpPathTests.csproj`, targeting `net8.0`,
that compiles the optimizer's source files by link and project-references `YARG.Core`.**

```xml
<TargetFramework>net8.0</TargetFramework>
<ItemGroup>
  <ProjectReference Include="..\..\YARG.Core\YARG.Core\YARG.Core.csproj" />
  <Compile Include="..\..\Assets\Script\Gameplay\SpPath\**\*.cs" LinkBase="SpPath" />
</ItemGroup>
```

This works only if the optimizer is **Unity-free C#** — no `UnityEngine`, no
`MonoBehaviour`, no `Debug.Log`, no `SettingsManager`. That is a design constraint on the
optimizer, and a good one anyway: it keeps the model next to the thing it models and
testable in 8 seconds. Everything Unity-shaped (settings gate, per-player storage,
rendering) lives in a separate file that consumes `StarPowerPath` and is never compiled
into the test project.

The test itself:

1. Load `drawntotheflame.mid` via `SongChart.FromMidi` (`EngineTester.cs:36-38`).
2. Build `GuitarEngineParameters` matching a real preset (`EnginePreset.Instruments.cs:149` for the bass/guitar `MaxMultiplier` split).
3. Run the optimizer on `chart.FiveFretGuitar.GetDifficulty(Expert)` → `StarPowerPath`.
4. Construct a `ScriptedActivationEngine : YargFiveFretGuitarEngine` with `isBot: true`, overriding `UpdateBot`:

   ```csharp
   protected override void UpdateBot(double time)
   {
       base.UpdateBot(time);                       // hits every note perfectly
       IsStarPowerInputActive = ShouldBeActiveAt(NoteIndex);   // replaces the naive toggle
   }
   ```

   This is legal because `UpdateBot` is `protected override` and not sealed
   (`Guitar/Engines/YargFiveFretGuitarEngine.cs:23`), and `IsStarPowerInputActive` is
   `{ get; protected set; }` (`BaseEngine.cs:89`). Feeding `GameInput`s is **not** an
   option: `BaseEngine.Update` skips `ProcessInputs` entirely when `IsBot`
   (`BaseEngine.cs:200-201`).
5. Step the engine to the end of the chart in fixed increments and assert
   `engine.EngineStats.TotalScore == path.ProjectedScore`, exactly.
6. A second test asserts the optimizer beats the stock bot policy
   (`IsStarPowerInputActive = CanStarPowerActivate && !IsStarPowerInputActive`,
   `YargFiveFretGuitarEngine.cs:30` — it fires the instant the bar hits 50%), i.e.
   `path.ProjectedScore >= greedyScore`. That is the feature's whole justification and it
   should be a regression test.

Why this beats the alternatives:

- **vs. adding tests to `YARG.Core.UnitTests`:** would modify the submodule (forbidden) and cannot be built by the local SDK.
- **vs. Unity EditMode tests:** needs a new package, asmdefs, and a Unity batchmode launch per run; CLAUDE.md's whole build story is built around *not* launching Unity.
- **vs. a replay-based check:** needs a recorded replay per scenario, and the replay format is versioned upstream.

**Making it permanent:** add a `dotnet test Tools/SpPathTests` step to
`.github/workflows/build-windows.yml` before the Unity build (or a small dedicated
workflow). It needs no Unity licence and no `Assets/Packages/` bootstrap, so it is the
cheapest possible CI gate. Agents run it locally with
`dotnet test Tools/SpPathTests/SpPathTests.csproj -nologo -v q`.

**Unverified:** that `dotnet test` on a project referencing the `netstandard2.1`
`YARG.Core.csproj` resolves `Melanchall.DryWetMidi.Nativeless` cleanly on this machine.
Slice 1 must prove this before anything else is written.

---

## 4. Plumbing

### 4.1 Where the path is computed

`Assets/Script/Gameplay/GameManager.Loading.cs:234-237` — immediately after
`CreatePlayers()`, adjacent to `InitializeSectionStripStates()`. The comment there ("Must
be after the players exist, since it reads their note tracks") applies verbatim: the
optimizer needs the post-`ApplyModifiers` `NoteTrack` (`TrackPlayer.cs:238`) and the live
`GuitarEngineParameters` off the constructed engine (`TrackPlayer.cs:245`). Main thread,
loading screen still up, sub-millisecond. Do **not** move it into `LoadChart()` — the
engine parameters do not exist yet there.

A `GameManager.InitializeStarPowerPaths()` mirroring `InitializeSectionStripStates()`
(`GameManager.cs:894-944`) is the shape to copy, gate for gate.

### 4.2 Per-player storage

Mirror the `SectionStripState` plumbing exactly (`Assets/Script/Gameplay/HUD/SectionStripState.cs`):

- `BasePlayer.StarPowerPath { get; private set; }` + `SetStarPowerPath(StarPowerPath)` + `protected virtual void OnStarPowerPathSet()`, alongside the existing `SectionState` at `BasePlayer.cs:224,233-243`.
- `TrackPlayer.OnStarPowerPathSet()` forwards to the highway, the way `OnSectionStateSet()` forwards to `TrackView` at `TrackPlayer.cs:141-144`.

### 4.3 The four cursor-reset sites

The `SpPathIndex` spawn cursor, the two divergence cursors and the divergence flag all reset
together in `TrackPlayer.ResetStarPowerPathCursors()`, called everywhere `BeatlineIndex = 0`
happens, in `Assets/Script/Gameplay/Player/TrackPlayer.cs` (line numbers as implemented):

| Line | Method | Also needs |
|---|---|---|
| `133` | `Initialize` | initial value |
| `579` | `ResetPracticeSection()` | cursor reset only |
| `1154` | `SetPracticeSection(uint, uint)` | **cursor reset *and* a full recompute** — this method rebuilds `NoteTrack` from a tick range (`:1140-1148`) and calls `CreateEngine()` again (`:1160`), so the old plan's note indices are meaningless |
| `1180` | `SetReplayTime(double)` | cursor reset only |

A fifth caller is `OnStarPowerPathSet()` (`:190`), which is the rebuild case.

The spawn loop itself copies `UpdateBeatlines` (`TrackPlayer.cs:745-780`): a `while` over
the ordered activation list against `time + SpawnTimeOffset`, taking from a pool with
`TakeWithoutEnabling()`.

Practice mode does not need any of this: the overlay is **off in practice entirely**, because
upstream swallows every Star Power input there — `FiveFretGuitarPlayer.InterceptInput`
(`FiveFretGuitarPlayer.cs:822-825`) returns `true` for `GuitarAction.StarPower` whenever
`GameManager.IsPractice`, so no path could ever be followed. That also makes the stripped-flag
question moot: `PracticeManager.cs:213` calling `player.BaseEngine.AllowStarPower(false)`
(which strips `NoteFlags.StarPower`, `BaseEngine.Generic.cs:424-447`) and the ordering of that
call against `SetPracticeSection` no longer matter to the overlay either way.

### 4.4 Reading live SP state to dim markers

`BasePlayer.BaseStats` (`Assets/Script/Gameplay/Player/BasePlayer.cs:61`, forwarding to
`BaseEngine.BaseStats` at `:59`) exposes everything needed, all public:
`StarPowerTickAmount`, `IsStarPowerActive`, `StarPowerActivationCount`
(`BaseStats.cs:126,141,156`), plus `BaseEngine.StarPowerTickPosition` and
`StarPowerTickEndPosition` (`BaseEngine.cs:116,124`).

Divergence rule: the plan is stale from the first moment the player's meter at an upcoming
activation cannot match `Activation.MeterAtActivation`. Cheapest sufficient check, evaluated
per frame against the next pending activation:

- a Star Power phrase is failed (`BaseEngine.OnStarPowerPhraseMissed`, raised from `StripStarPower`, `BaseEngine.Generic.cs:1155`) → the meter never gets that quarter bar, so every later activation the plan schedules is funded by Star Power the player no longer has; dim everything from here.
- `BaseStats.StarPowerActivationCount` exceeds the number of plan activations already passed → the player activated off-plan; dim everything from here.
- at the activation's note, `StarPowerTickAmount / TicksPerQuarterSpBar < MeterAtActivation` → the player cannot follow it; dim.

**Ordinary misses do not dim** (changed 2026-09-03, see "Divergence is Star Power state only" below).
The full-combo assumption is a *scoring* assumption — it makes the projected score an upper bound —
not a claim about which markers are still followable. A player who drops a note still has exactly
the Star Power the plan expects, so the markers are still the right places to activate.

Dim, never recompute — locked decision.

### 4.5 The band-run gate

Hide the overlay whenever the run has 2+ human players. The existing idiom is
`GameManager.cs:196` (`HasBots => _players.Any(p => !p.Player.SittingOut && p.Player.Profile.IsBot)`)
and the `.Where(player => !player.Player.Profile.IsBot)` filter at `GameManager.cs:718`;
the gate is the same predicate, counted:

```csharp
_players.Count(p => !p.Player.SittingOut && !p.Player.Profile.IsBot) > 1
```

Justification: `EngineManager.Band.cs:17` `BandMultiplier => Math.Max(_starpowerCount * 2, 1)`
and `BaseEngine.AwardUnisonBonus()` (`BaseEngine.cs:637-641`, a free
`TicksPerQuarterSpBar`) both couple SP across players, so a single-player path is not
merely approximate there — it is wrong. The pause-menu / setting copy says so.

### 4.6 Settings

Three files, as with every previous toggle (`docs/roadmap.md`, Feature 3, "Effort"):
`Assets/Script/Settings/SettingsManager.Settings.cs` (a `ToggleSetting`, next to
`ShowSectionStrip` at `:849`), `Assets/Script/Settings/SettingsManager.cs` (a `nameof(...)`
entry in the HUD `MetadataTab`, near `:238`), and
`Assets/StreamingAssets/lang/en-US.json`. Placement is locked: Graphics → HUD, immediately after
`ShowSectionStrip`, global rather than per-instrument, with the description stating that the path
assumes a full combo and no whammy.

---

## 5. Rendering sketch

The visual decisions are now locked — see "Locked UI decisions" immediately below. This section is
the mechanical shape they imply.

- **Copy `BeatlineElement`** (`Assets/Script/Gameplay/Visuals/TrackElements/BeatlineElement.cs`, 66 lines): a `TrackElement<TrackPlayer>` that overrides `ElementTime`, `InitializeElement()`, `UpdateElement()`, `HideElement()`, and scales/tints a single `MeshRenderer`.
- **Prefab template:** `Assets/Prefabs/Gameplay/Visual/TrackElements/Beatline.prefab` — root → `Parent` → `Mesh`, `Quad.fbx` rotated X+90°, `localPosition (0, 0.002, 0)` to dodge z-fighting, `localScale (2, 0.05, 1)` where X = 2 = `TrackPlayer.TRACK_WIDTH`. Pooled from a `Beatline Pool` on `Assets/Prefabs/Gameplay/Visual/BaseVisual.prefab`. (Prefab internals **unverified** in this pass — taken from the roadmap's visuals research.)
- **Positioning is free:** `TrackElement.UpdateElementPosition()` (`TrackElement.cs:42-61`) does the whole time → z conversion, and returns the element to the pool past `REMOVE_POINT`. (`GetZPositionAtTime` at `:32-40` would give a second anchor point, but the locked design has no end marker to anchor.)
- **Curvature and fade come for free** via `Assets/Script/Gameplay/Visuals/HighwayCameraRendering.cs`'s global `_YargCurveFactors` / `_YargFadeParams`, as long as the material stays off the `FadeExclude` layer.
- **Not needed:** the region machinery (`TrackEffectElement.RescaleForZ()`, `LaneElement.SetTimeRange(start, end)` at `TrackPlayer.cs:943`, a new `TrackEffectType`). The locked marker is a point, not a region.
- **The section strip is the wrong rendering precedent** (`Assets/Script/Gameplay/HUD/SectionStrip.cs` is runtime uGUI under a `HorizontalLayoutGroup`) but the right data-flow precedent, as used throughout §4.

### 5.1 Locked UI decisions

> **Superseded 2026-09-04.** Everything in this subsection about how the overlay *looks* — a
> single Star Power orange band, "point only", "always drawn", the whole-run alpha dim — was
> replaced by "Visual redesign, 2026-09-04" at the end of this document. The rows about the
> *setting* (placement, scope, copy), the band-run gate and the section strip coexisting still
> stand. The text is kept as the record of what was decided and why it did not survive first
> contact with the editor.

The mockup interview §5 deferred is **done (2026-09-03)**. These are locked; the trailing "open
questions" list below is kept only as the record of what was asked.

| Question | Decision |
|---|---|
| Marker style | **A single band across the highway at the activation note**, drawn beatline-style. Same geometry as `BeatlineElement` / `Beatline.prefab` — a full-width quad lying on the track. |
| Marker colour | **Star Power orange, `#FF9800`** — `HighwayPreset.StarPowerColor`, `Color.FromArgb(255, 255, 152, 0)` at `YARG.Core/YARG.Core/Game/Presets/HighwayPreset.cs:10`. Read from the preset, never hardcoded a second time. |
| Region or point | **Point only.** No shaded region spanning the window, and **no end marker** — the player acts at one instant, and a region competes visually with the SP fill on the highway. |
| Score-gain labels | **None.** The overlay stays purely spatial; `Activation.ScoreGain` is still computed and logged, but nothing is drawn. |
| Behaviour on deviation | Once the player's **Star Power state** leaves the plan — a failed Star Power phrase, an activation the plan does not call for, or a planned activation not taken — **all markers fade to a low alpha for the rest of the song** (the ones already on the highway included) and stay that way. Never recomputed (locked decision, §"Decisions"). An ordinary missed note is *not* a deviation: it costs points, not Star Power. **Revised 2026-09-03**, replacing "a dropped note, an early or late activation, or actual SP state differing from the plan's" — see "Divergence is Star Power state only". |
| Visibility window | **Always drawn.** Markers spawn from the pool exactly like beatlines, on the same `SpawnTimeOffset` cursor. |
| Setting | A single `ToggleSetting` in **Settings → Graphics → HUD, immediately after `ShowSectionStrip`**. |
| Setting scope | **Global, not per-instrument.** |
| Setting copy | The description states that the path **assumes a full combo and no whammy** — that is the whammy disclosure (open question 5), and it lives in the setting rather than the pause menu. |
| Band runs | With **2+ human players the markers are silently hidden** — no message, no marker. The gate is §4.5's predicate. |
| Section strip | **Coexists.** The strip is uGUI at the top of the screen; the markers are on the highway. They do not compete for space. |

Consequences worth stating, since they cut work out of §5:

- No `TrackEffectType`, no `LaneElement.SetTimeRange`, no `RescaleForZ` — a point marker needs none
  of the region machinery. `EndMeasureTick` / `EndTime` on `Activation` stay in the model (the
  divergence check and the logging use them) but nothing renders them.
- No text rendering on the highway, so no font, no billboarding, no localisation of a number.
- The dim state is one bool per player plus an alpha on the shared material instance, not per-marker
  state.

---

## 6. Slices

Each ends at a state that can be verified without the next one.

| # | Scope | Ends at | Effort |
|---|---|---|---|
| **1** | **Harness skeleton.** `Tools/SpPathTests/` (net8.0, NUnit) building against `YARG.Core.csproj`; one test that loads `drawntotheflame.mid`, runs a bot engine with the *stock* policy, and asserts a known `TotalScore`. No optimizer yet. | `dotnet test` green locally; the golden number recorded in the test. Proves the whole verification story before any model is written. | **S** |
| **2** | **Scoring model, no SP.** `Assets/Script/Gameplay/SpPath/` (Unity-free): duplicate constants, prefix-sum score table, `ProjectPerfectScore()` with no activations. | Test asserts the projection equals a bot run with SP suppressed (`AllowStarPower(false)`, `BaseEngine.Generic.cs:424`), exactly. This is where every rounding rule in §1 gets pinned. | **M** |
| **3** | **SP model + DP.** Window arithmetic, the five-state meter, the DP, `StarPowerPath`. | Test asserts a scripted-activation bot run reproduces `ProjectedScore` exactly for the optimizer's own path, **and** for three hand-picked suboptimal paths (so the model is right, not just self-consistent). Second test: optimizer ≥ stock greedy bot. Add `dotnet test` to CI. | **L** |
| **4** | **Plumbing, log-only.** `InitializeStarPowerPaths()` after `CreatePlayers()`, `BasePlayer.SetStarPowerPath` + `OnStarPowerPathSet()`, the four cursor-reset sites, the practice-section recompute and the `AllowStarPower(false)` clear, the band-run gate (§4.5), and the divergence check that flips the dim flag (§4.4). **No visuals at all** — the plan and every dim transition go to the log. | Play a song, read the log: plan present in single-player, absent in a band run, recomputed on a practice-section change, dim flag flips on a failed Star Power phrase or an off-plan activation. | **M** |
| **5** | **Rendering.** A pooled `TrackElement<TrackPlayer>` modelled on `BeatlineElement` + `Beatline.prefab`: one full-width quad at the activation note, tinted the `HighwayPreset` Star Power orange (`#FF9800`), spawned off the `_spPathIndex` cursor exactly as beatlines are, and dropped to low alpha once slice 4's dim flag is set. No region, no end marker, no label — see "Locked UI decisions". | Markers on the highway, at the right notes, fading as one when the run goes off-plan. | **M** |
| **6** | **Settings toggle + copy.** The `ToggleSetting` in `SettingsManager.Settings.cs` immediately after `ShowSectionStrip`, its `nameof(...)` entry in the HUD `MetadataTab`, and the `en-US.json` strings — description stating the path assumes a full combo and no whammy. Global, not per-instrument. | Toggle works; the band-run case stays silently hidden with the toggle on. | **S** |

Total: broadly the roadmap's **L**, weighted 15% harness / 45% model+DP / 20% plumbing /
20% rendering+settings.

### Progress — slices 1 and 2 done (2026-09-03)

Both landed at `tools/SpPathTests/` and `Assets/Script/Gameplay/SpPath/`. Run with:

```sh
dotnet test tools/SpPathTests/SpPathTests.csproj -nologo -v q
```

5 tests, all green, ~1 s warm.

**Path casing:** the tracked directory is lowercase `tools/`, not `Tools/` as written above.
Everything else in §3.2 held.

**The `net8.0` / `netstandard2.1` unknown is resolved.** `dotnet test` against
`YARG.Core.csproj` resolves `Melanchall.DryWetMidi.Nativeless` and the rest cleanly. One
wrinkle the doc did not anticipate: the machine-level NuGet config on this box has **no package
sources at all**, so the harness ships its own `tools/SpPathTests/nuget.config` pinning
nuget.org. Scoped to that directory, so NuGetForUnity is untouched.

**Golden numbers** — `drawntotheflame.mid`, Expert, stock default engine preset, full combo
(1269 guitar notes, 1176 bass notes, 100% hit on both):

| Run | `TotalScore` | Recorded in |
|---|---|---|
| Guitar, stock greedy bot policy | **376,558** | `GoldenScoreTests.DrawnToTheFlameGuitarGreedyBotScore` |
| Guitar, SP suppressed (`AllowStarPower(false)`) | **317,774** (289,774 committed + 28,000 solo) | `ScoreModelTests.DrawnToTheFlameGuitarNoSpScore` |
| Bass, SP suppressed | **389,279** (this chart's bass has no solo) | `ScoreModelTests.DrawnToTheFlameBassNoSpScore` |

The greedy run takes 10 activations from 20 phrases, so the chart has plenty of SP to path with.

**Bot runs are step-size independent.** Measured identical totals at 1/30, 1/60, 1/120 and
1/240 s on both instruments, because the engine queues its own updates at note times, sustain
burst times and the SP end. The harness steps at 1/120 s.

**Two corrections to §1 that the harness turned up:**

1. **§1.3's multiplier formula is off by one.** The doc says the pre-SP multiplier is
   `min(i/10 + 1, MaxMultiplier)` with `i` = combo steps *before* the note, "exactly what
   `CalculateChartScores` already does". `CalculateChartScores` does do that
   (`Guitar/GuitarEngine.cs:414`), but the **live engine does not**: `HitNote` calls
   `IncrementCombo()` *before* `UpdateMultiplier()` (`Guitar/GuitarEngine.cs:270-274`), and
   `UpdateMultiplier` reads `BaseStats.Combo` post-increment (`BaseEngine.cs:447`). So the note
   at combo index `i` (0-based) is scored at `min((i + 1)/10 + 1, MaxMultiplier)` — the 10th
   note of the song is already 2x, not the 11th. Consequence: `BaseScore` is **not** the same
   number as a full-combo no-SP `CommittedScore` (289,624 vs 289,774 on guitar; 389,029 vs
   389,279 on bass). `ScoreModelTests.EngineBaseScore_IsNotTheSameAsAFullComboRun` pins this so
   nobody "fixes" the model to agree with `BaseScore`.

2. **The multiplier at a sustain burst is not the note's multiplier.** §1.4 is right that the
   sustain commits once at its burst tick with "the multiplier current at that instant", but it
   is worth being explicit that this is generally *higher* than the multiplier the note itself
   was scored at, because a long sustain spans later notes. `UpdateSustains()` runs after
   `CheckForNoteHit()` in the same engine pass (`YargFiveFretGuitarEngine.cs:228-229`), so a
   sustain bursting on the same tick as a note uses the multiplier that note produced. The model
   resolves it by counting the notes at or before the burst tick.

**What the harness actually verified, and what it did not.** `ScoreModel` matched the engine to
the point on both instruments on its first run, but the fixture only exercises part of §1. The
distinction matters, because an unexercised rule is a *reading of the engine source*, not a
tested one.

*Verified by the harness on `drawntotheflame.mid` (guitar and bass, full combo, SP suppressed):*

- `POINTS_PER_NOTE * (1 + ChildNotes.Count)` — 50 per note, chords paying per child.
- Chords as a single combo step (`isChordSeparate: false`).
- The live multiplier rule `min((combo after increment)/10 + 1, MaxMultiplier)`, including the
  off-by-one against `CalculateChartScores` (correction 1 above), on both `MaxMultiplier` 4 and 6.
- Long-sustain scoring: `ceil(TickLength / TicksPerSustainPoint)` for the whole sustain,
  committed once at `TickEnd - SustainBurstThreshold`, at the multiplier current at that tick
  (correction 2 above). 60 sustains on guitar, all longer than the burst threshold.
- Rebasing being score-neutral, and `PendingScore` reaching zero by the end of the run.
- `100 x NoteCount` solo bonus, floored to 50, as a fixed offset (28,000 across the guitar solos).
- `TotalScore = CommittedScore + SoloBonuses` for a run with no SP and no coda.
- Bot runs being step-size independent (1/30, 1/60, 1/120, 1/240 s all identical).

*Read from the engine source, not yet exercised by any fixture.* These are §1 claims with a
`file:line` behind them and no test coverage — the fixture has zero instances of each:

- **The disjoint-chord rule** — one sustain per sustained child, combo incremented once
  (`Guitar/GuitarEngine.cs:424-439`). 0 disjoint chords in the fixture.
- **BRE exclusion** (`Guitar/GuitarEngine.cs:247`, `BaseEngine.Generic.cs:76-98`). 0 BRE notes.
  `ScoreModel` skips BRE notes *unconditionally* while the engine's skip is conditional on
  `CodaHasStarted`; see the divergence comment at `ScoreModel.cs`'s BRE branch.
- **The short-sustain burst rule** — a sustain shorter than `SustainBurstThreshold` committing at
  `note.Tick` rather than `TickEnd - threshold` (`BaseEngine.Generic.cs:857-864`). The fixture's
  shortest sustain is 600 ticks against a 120-tick threshold, so the branch never runs.
- **Extended and rebased sustains** — a sustain spanning later notes, and the `RebaseSustains`
  path a multiplier change during a sustain triggers (`BaseEngine.Generic.cs:1249-1271`). 0
  extended sustains; with `MaxMultiplier` reached early and no long overlaps, rebasing is barely
  touched.
- **Tempo and meter changes** — `QuarterTickToMeasureTick`, the meter-aware SP drain, and the
  claim that a full bar is 8 measures regardless of tempo (§1.5). The fixture is a **single 4/4
  tempo map**, so every tick conversion in it is linear and would also pass under a naive
  flat-beat model.
- **The SP bar tick constants** — `TicksPerQuarterSpBar = MeasureResolution * 2` and the half/full
  multiples (`BaseEngine.cs:168-170`). Slice 2 computes them but never spends SP, so no test
  reads them.
- **Open notes** (0 in the fixture) and everything in §1.5 about gain, drain, the window end and
  the activation boundary — all of slice 3.

*Fixture coverage — `drawntotheflame.mid`, Expert 5-fret guitar:*

| Property | Value | Covers |
|---|---|---|
| Scoring notes | 1,269 | note points, multiplier curve |
| Chords (notes with children) | 35 | `50 x (1 + ChildNotes.Count)`, one combo step |
| Disjoint chords | **0** | nothing — per-child sustain rule untested |
| Sustains | 60, shortest 600 ticks | long-sustain burst at `TickEnd - 120` |
| Sustains below the burst threshold (120 ticks) | **0** | nothing — short-sustain branch untested |
| Extended sustains | **0** | nothing |
| Open notes | **0** | nothing |
| BRE notes | **0** | nothing — both sides of the BRE divergence untested |
| Solo notes | 280 | `100 x NoteCount`, floor-to-50 |
| Star Power phrases (`IsStarPowerEnd` notes) | 20 | gain only; never spent in slice 2 |
| Tempo map | 1 tempo (200 BPM), 1 time signature (4/4) | nothing meter-dependent |
| `Resolution` / `MeasureResolution` | 480 / 1920 | `TicksPerSustainPoint`, burst threshold |

**Slice 3 must add a synthetic fixture** (done — see below) — a small hand-authored `.mid` (or a chart built
programmatically against `SongChart`) covering exactly the untested branches above: a disjoint
chord with sustained children, a sustain shorter than the burst threshold, an extended sustain
crossing a multiplier change, an open note, a BRE with and without a preceding coda, and at least
one tempo change and one time-signature change so the measure-tick conversion and the meter-aware
SP drain are actually exercised. Without it, slice 3's DP would be validated only on the one
chart whose sync track cannot distinguish YARG's measure-based SP bar from CHOpt's flat-beat one.

**What slice 2 shipped:**

- `Assets/Script/Gameplay/SpPath/ScoreEvent.cs` — a point award (tick, un-multiplied points,
  combo multiplier, note-vs-sustain kind).
- `Assets/Script/Gameplay/SpPath/ScoreModel.cs` — `ScoreModel.Build(track, syncTrack,
  maxMultiplier)` produces the ordered event list, `CommittedScore`, `SoloBonusTotal` and
  `ProjectPerfectScore()`. The duplicated constants live here. No `UnityEngine`.
- `tools/SpPathTests/` — `ChartFixtures`, `BotRunner`, `ScriptedBotGuitarEngine`,
  `GoldenScoreTests`, `ScoreModelTests`.

`ScriptedBotGuitarEngine` overrides `UpdateBot`, calls `base.UpdateBot(time)` and then sets
`IsStarPowerInputActive` from a set of note indices, exactly as §3.2 step 4 predicted. With an
empty set it reproduces the SP-suppressed score, which is the check that it really replaces the
greedy toggle rather than racing it. Its per-note activation *semantics* (does note N get
doubled?) were still **unverified** at the end of slice 2 — see "Progress — slice 3 done" below,
which settles them and rewrites the override point in the process.

**Not done here:** the measure-tick conversion and prefix sums from §2.1 (slice 3 needs them,
slice 2 does not), and the CI step from §3.2.

### Progress — slice 3 done (2026-09-03)

35 tests, all green, ~2 s warm:

```sh
dotnet test tools/SpPathTests/SpPathTests.csproj -nologo -v q
```

#### Settled activation semantics

All of §1.5 that was marked unverified is now pinned by `tools/SpPathTests/SpSemanticsTests.cs`,
run against a real engine on `drawntotheflame.mid`:

| Question | Answer, as measured |
|---|---|
| Which notes get doubled? | Exactly the awards whose **measure tick** lies in `[m, E)`, where `m = QuarterTickToMeasureTick(Notes[N].Tick)`. Asserted both ways: no doubled award outside the interval, no undoubled award inside it. |
| First doubled note | **N itself**, not N+1. The activation runs in `UpdateStarPower`, which precedes `UpdateHitLogic` in the same loop pass. |
| Last doubled note | The last one with measure tick `< E`. The first award at measure tick exactly `E` is **not** doubled — the half-open interval is real, not a convention. |
| How `E` relates to activation and meter | `E = m + StarPowerTickAmount`, with the amount always a whole number of quarter bars (`TicksPerQuarterSpBar = MeasureResolution * 2`). Measured: half bar → `E - m = 7680`, three quarters → `11520`, full → `15360`, at `MeasureResolution = 1920`. |
| Phrase completed while active | `E ← min(E + TicksPerQuarterSpBar, m_phrase + TicksPerFullSpBar)`. Verified on the window opened at note 238 (a full bar) crossing phrase end 264: `E` moved from 92160 to 96000. The repeated-extension case is exercised on the synthetic fixture, where the optimizer's single window runs from measure tick 10,080 to 40,800 on half a bar (7,680) — **six** extensions of a quarter bar each. |
| Meter after a window | Always **0**. A phrase collected while active extends the window instead of banking, and the release fires at amount 0. This is what makes `(note index, meter)` a sufficient DP state. |
| Held or pulsed? | **Pulsed is enough.** Raising the input for a single engine pass produces the full window; holding it across the whole window scores identically and does not re-activate, because `ActivateStarPower` returns early while already active (`BaseEngine.cs:483-486`). |
| Below half a bar | The request is silently ignored — no activation, no meter spent. |

**The harness change this forced.** `ScriptedBotGuitarEngine` now drives `IsStarPowerInputActive`
from an override of **`UpdateStarPower`**, not from `UpdateBot` as slice 2 had it. `UpdateBot` runs
*inside* `UpdateHitLogic`, i.e. after `UpdateStarPower` has already read the input, so an input
raised there is only acted on some later pass — in practice a bare frame tick a few milliseconds
before the next note, which puts the activation at the wrong tick and (because the meter is spent
from there) shifts `E`. Setting the input at the top of `UpdateStarPower`, gated on
`NoteIndex == N && CurrentTime >= Notes[N].Time`, makes the activation land on the engine's own
queued "Bot Note Time" update for note N. That is exactly what a human tapping SP on note N gets,
since a real player's input is drained before the loop runs.

Consequence worth knowing: **the stock greedy bot's path is not note-aligned.** It toggles the
input every bot tick (`YargFiveFretGuitarEngine.cs:30`) and so activates on whatever pass follows
the meter reaching half — a frame tick, e.g. measure tick 237853. Re-anchoring those activations to
notes and replaying them produces a *different* path (on guitar it starves the fourth activation of
meter outright), so `SpModel_MatchesTheStockGreedyBotsActualRun` checks the model against the greedy
run **as it happened**, using `SpScoreModel.WindowEndAt` on the engine's own activation ticks.

#### Model corrections and additions

1. **§2.1's prefix sums are over measure ticks, not quarter ticks.** `ScoreEvent` now carries
   `MeasureTick` alongside `Tick`, and `SpScoreModel` binary-searches on it. `QuarterTickToMeasureTick`
   is monotone, so the tick-ordered event list is measure-tick-ordered too; the constructor asserts
   this rather than assuming it.
2. **`NoStarPowerOverlap` is a model input, not an ignorable flag.** When true, a phrase hit while
   active is stripped (`Guitar/GuitarEngine.cs:259-261`) and windows never extend. It is a
   **required** argument on `SpScoreModel`'s constructor and on `SpPathOptimizer.Optimize` — no
   default — because the value a forgotten argument would pick (`false`) is exactly the common one,
   so the mistake would only ever show up on the presets where it matters. `SpScoreModel.FromParameters`
   and the `Optimize(track, syncTrack, GuitarEngineParameters)` overload are the call sites the game
   uses: both read `MaxMultiplier` and `NoStarPowerOverlap` straight off the live engine parameters
   (`TrackPlayer.cs:245`), so neither can be dropped on the way in. Both goldens run at the preset
   default, `false`.
3. **The DP is solved backwards, iteratively**, over `(note index, meter ∈ 0..4)` — no recursion, so
   no stack-depth concern on long charts. No candidate pruning: the activation transition is
   `O(window length)`, and the solve is **2.2 ms** on the 1,269-note guitar chart and **2.0 ms** on
   the 1,176-note bass chart — warm median of nine runs, **Debug** build (`dotnet test`'s default
   configuration; Release will be faster). `Optimizer_BeatsGreedy_AndItsProjectionIsReproducibleOnTheEngine`
   prints cold and warm figures and the build config on every run, so this number can be
   re-derived rather than trusted. That is well inside a loading screen, so exactness is not worth
   trading for pruning.
4. **`ScoreModel.ScoringNotes`** is new: the note list minus BRE notes, each with its quarter tick,
   its measure tick and whether it carries `IsStarPowerEnd`. Activations are indexed into this list
   internally and reported back as note-track indices.

#### New goldens

`drawntotheflame.mid`, Expert, stock default preset, full combo:

| Run | Guitar | Bass |
|---|---|---|
| No Star Power | 317,774 | 389,279 |
| Stock greedy bot | 376,558 | **465,083** |
| **Optimizer** | **392,750** (7 activations) | **484,979** (7 activations) |
| Optimizer's gain over greedy | +16,192 (+4.3%) | +19,896 (+4.3%) |

The optimizer takes fewer windows than greedy's ten, and spends them on dense, high-multiplier
passages — hoarding to three quarters or a full bar where that pays. The seven-window count is the
earliest-activation tie-break at work: the same 392,750 is reachable with six, and the DP prefers
the path whose markers come soonest.

Synthetic fixture (`tools/SpPathTests/SyntheticChart.cs`), Expert guitar, default preset:

| Run | Score |
|---|---|
| No Star Power | 30,692 |
| Stock greedy bot | 53,327 |
| Optimizer | 55,204 |

#### The synthetic fixture

Built as a MIDI **in memory** with DryWetMidi and handed straight to `SongChart.FromMidi`, so
nothing is written to disk and the submodule's charts are untouched. It closes every gap slice 2
listed:

| Branch slice 2 could not exercise | Now covered by |
|---|---|
| Time-signature change | 4/4 → 3/4 (at quarter tick 15360) → 4/4. `StarPowerBar_IsMeterAware_NotFlatBeat` pins the consequence: 1440 quarter ticks span 1440 measure ticks in 4/4 and **1920** in 3/4, so the SP bar drains 4/3 as fast per quarter tick there. A flat-beat model (CHOpt's) puts `E` somewhere else. |
| Tempo change | 120 → 180 BPM at quarter tick 9600, on both sides of which a full bar is still 8 measures. |
| Disjoint chord with unequal sustains | Green 960 ticks + yellow 480 at the same tick — one combo step, two sustains. |
| Sustain below the burst threshold | A 90-tick sustain against a 120-tick threshold. **This needs a `ParseSettings` override**: `Default_Midi`'s sustain cutoff is `Resolution / 3` = 160 (`MidReader.cs:122-124`), which is *above* the 120-tick burst threshold, so with the stock setting the short-sustain branch is unreachable by construction. The fixture sets `SustainCutoffThreshold = 60`, a value real charts can set from `song.ini` (`SongEntry.IniBase.cs:258`). |
| Extended sustain across a multiplier change | A 2400-tick sustain spanning ten later notes; seven extended sustains in total. |
| Open note | Phase Shift sysex (`PS\0`, Expert, `Guitar_Open`) bracketing one note. |
| BRE | Note 120 over the last measure, with a `[coda]` text event in the EVENTS track — and a second variant without it. |
| SP phrases across the meter change | Eight, one of which straddles the 4/4 → 3/4 boundary. |

Two of those rows are now asserted rather than assumed, because a fixture that quietly stops
containing the shape it was built for is worse than no fixture:

- `SpModel_MatchesTheEngine_AcrossTheMeterChange` asserts that at least one Star Power window
  actually **overlaps the 3/4 stretch**. Without it the meter-aware drain could go untested while
  the test kept passing.
- `ASustainStraddlingAWindowEdge_IsScoredByItsBurstTick` searches the fixture for a sustain whose
  **note is on one side of a window edge and whose burst is on the other**, asserts one exists, and
  then reproduces the containing run on the engine. This is §1.4's discontinuity — the place a model
  that thinks in "how much of the sustain overlapped SP" instead of "where did the burst land"
  diverges first (risk 4). Found at note 18, note outside and burst inside a `[6240, 25440)` window.

**The BRE TODO is resolved, and the divergence is real.** `ScoreModel` skips BRE notes
*unconditionally*; the engine's skip is conditional on `CodaHasStarted` (`Guitar/GuitarEngine.cs:247`).
`BigRockEnding_IsSkippedByBothSidesOnlyWhenACodaStartsIt` pins both sides: with a coda the two agree
exactly (30,692); without one the engine scores the eight BRE notes and counts them towards combo
(32,292) while the model does not move. Keeping the model's skip unconditional stays a deliberate
decision — a BRE with no coda is malformed charting, and modelling `CodaHasStarted` would mean
simulating the coda phrase — but it is now a *tested* decision rather than an unexamined one.

#### What slice 3 shipped

- `Assets/Script/Gameplay/SpPath/SpScoreModel.cs` — the window model: measure-tick prefix sums,
  `SimulateWindow(note, quarterBars)`, `WindowEndAt(measureTick, meterTicks)`, `MeterAfter`, and
  `ScoreForActivations` / `DoubledPointsForActivations` for an arbitrary activation list.
- `Assets/Script/Gameplay/SpPath/SpPathOptimizer.cs` — the DP, plus `StarPowerPath` and
  `Activation` (note index, activation tick, activation/end measure ticks, activation/end times,
  meter at activation, score gain).
- `Assets/Script/Gameplay/SpPath/ScoreEvent.cs`, `ScoreModel.cs` — `MeasureTick` on every event and
  the new `ScoringNotes` list.
- `tools/SpPathTests/` — `SpSemanticsTests`, `SpPathOptimizerTests` (with the brute-force check),
  `SyntheticChart`, `SyntheticChartTests`; `ScriptedBotGuitarEngine` rewritten as described above and
  extended with an award/window trace and a `useStockPolicy` mode.
- `tools/SpPathTests/SyntheticChart.Dense` — a second, denser synthetic chart, built for the
  brute-force check alone. The main fixture's optimum is one long window, so an exhaustive search
  over it never has to get the *interaction* of several windows right. The dense chart is four
  4-measure dense clusters (a cluster is exactly the span a half-bar window covers) separated by
  4-measure sparse stretches worth an eighth as much, with exactly two Star Power phrases in each
  sparse stretch and a dense lead-in to cap the multiplier first. Its optimum is therefore forced to
  be **four chained windows**, one per cluster, and eight phrases put the brute force's activation
  bound at exactly four — so the search has to chain all four to find it. Both agree at +12,800, and
  the engine reproduces the four-window projection.
- `.github/workflows/sp-path-tests.yml` — `dotnet test` on push and PR, submodules recursive, .NET 8,
  path-filtered to `Assets/Script/Gameplay/SpPath/**`, `tools/SpPathTests/**` and the `YARG.Core`
  submodule pointer. No Unity licence, no `Assets/Packages/` bootstrap.
- `.gitignore` — `!tools/SpPathTests/*.csproj`, so the hand-written harness project survives the
  blanket `*.csproj` rule for Unity-generated projects.

#### The brute-force bound

`BruteForce`'s activation cap is `floor(phrases / 2)`, which is exact rather than a guess: meter
comes only from phrases, one quarter bar each; every activation spends at least half a bar; and a
phrase collected while active extends the window instead of banking, so it can never fund a later
activation. The search also prunes the whole subtree under an illegal prefix. That prune is sound
because the legality of the k-th activation is a function of the activations *before* it only — the
meter available and whether an earlier window already swallowed it — so appending more activations
cannot rescue an illegal prefix. Sets that drop the offending index still get enumerated under the
sibling branches.

#### Still unverified after slice 3

- **The disjoint-chord *combo* rule.** The fixture's disjoint chord has two children on the same
  tick, so the "only increment combo if we haven't already seen a note in that tick" branch
  (`Guitar/GuitarEngine.cs:430-438`) is never made to increment twice. Model and engine agree at one
  combo step here, but a disjoint chord whose children sit on *different* ticks is untested.
- **`NoStarPowerOverlap == true` against a real engine.** No preset in the fork sets it, so no test
  can drive an engine with it on. Both of its consequences are now pinned as a *pure-model* test
  (`SyntheticChartTests.NoStarPowerOverlap_WindowsNeverExtend_AndSwallowedPhrasesDoNotBank`):
  windows never extend (checked over every (note, meter) pair on the synthetic chart, 428 of which
  *would* have extended with overlap allowed, so the assertion is not vacuous), and a phrase
  swallowed by a window does not bank meter for a later activation. What remains unverified is only
  that the engine behaves as `Guitar/GuitarEngine.cs:259-261` reads.
- **Whammy, squeezes, dropped notes.** Out of the model by design (§1.6).
- **Anything Unity-side**: editor-only assemblies, prefabs, rendering. Slice 4 territory.

### Risks specific to this plan

1. **Model drift** — the whole reason slices 1–3 are ordered harness-first. `BaseEngine.Generic.cs` is actively changed upstream. The CI test is the guard; a merge that breaks it must block.
2. **The Unity-free constraint on the optimizer** is load-bearing for §3. One stray `UnityEngine` using and the test project stops compiling. Worth a comment at the top of every file in `Assets/Script/Gameplay/SpPath/`.
3. **The `net10.0` / SDK 8 mismatch** means the fork cannot run the existing YARG.Core tests. If upstream retargets, revisit whether the harness should just live there after all — but that would still modify the submodule.
4. **Sustain burst boundary** (§1.4) and the **SP-end boundary** (§1.5) are the two places the model will first be wrong. Slice 3's suboptimal-path tests should be chosen to straddle both.
5. **"Optimal" is a claim** that assumes full combo and no whammy. Both are disclosed in the setting copy. Dimming does *not* handle the first — it tracks Star Power state, not score (see "Divergence is Star Power state only").
6. **CHOpt disagreement is not evidence of a bug** — YARG's bar is 8 measures, CHOpt's is 32 flat beats.

### Open questions for the mockup interview — answered 2026-09-03

All seven are settled in "Locked UI decisions" above. Kept here as the record of what was asked:

1. **Marker style** — a band at the activation point, a shaded region spanning the SP window, or both (start + end markers)?
2. **Visibility window** — always drawn, or only within N seconds of the activation point?
3. **Score-gain labels** — show `+12,400` next to each marker, or keep it purely spatial?
4. **Coexistence with the section strip** — both want the top of the highway. Coexist, or mutually exclusive?
5. **Whammy disclosure** — does the UI state that the path assumes no whammy, and where?
6. **Settings placement** — Graphics → HUD next to `ShowSectionStrip`, and per-instrument or global?
7. **Dim styling** — what "diverged" looks like: alpha, desaturation, or outline-only?

### Progress — slices 4, 5 and 6 done (2026-09-03)

Plumbing, rendering and the settings toggle all landed together. `dotnet build Assembly-CSharp.csproj`
is green and `dotnet test tools/SpPathTests/SpPathTests.csproj` still passes all 35 tests —
`Assets/Script/Gameplay/SpPath/` was not touched at all, so it stays Unity-free.

#### Slice 4 — plumbing

| Concern | Where it landed |
|---|---|
| Compute site | `GameManager.InitializeStarPowerPaths()` (`Assets/Script/Gameplay/GameManager.cs`), called from `GameManager.Loading.cs` immediately after `InitializeSectionStripStates()`, exactly as §4.1 specified. |
| Gates | `ShowStarPowerPath` off → nothing. Practice mode → nothing, logged (`SP path: skipped, practice mode`). Replay playback (`GlobalVariables.State.PlayingWithReplay`) → nothing, logged; individual replay players (`player.Player.IsReplay`) are skipped too, following the section strip's precedent. Human count `!= 1` → nothing, logged. Humans are counted as `_players.Count(p => !p.Player.SittingOut && !p.Player.Profile.IsBot)`, so **bots do not count** and playing alongside one still gets an overlay. |
| Per-player storage | `BasePlayer.StarPowerPath` / `StarPowerPathEnabled` / `SpPathDiverged`, with `EnableStarPowerPath()`, `virtual RecomputeStarPowerPath()`, `SetStarPowerPath(path)` and `virtual OnStarPowerPathSet()` — the `SectionStripState` shape, gate for gate. |
| The optimizer call | `FiveFretGuitarPlayer.RecomputeStarPowerPath()` — the only override, since 5-fret is the only modelled instrument. Calls `SpPathOptimizer.Optimize(NoteTrack, SyncTrack, EngineParams)`, so `MaxMultiplier` and `NoStarPowerOverlap` both come off the live engine parameters. Wrapped in a try/catch: the overlay is cosmetic and must not take a song down. |
| Practice excluded | The overlay is off in practice outright, at both ends: `InitializeStarPowerPaths` refuses to enable it, and `RecomputeStarPowerPath` clears the path when `GameManager.IsPractice` so a practice-section change cannot rebuild one. The reason is upstream's own: `FiveFretGuitarPlayer.InterceptInput` (`FiveFretGuitarPlayer.cs:822-825`) swallows every `GuitarAction.StarPower` input while `IsPractice`, so a path could never be followed there. The stripped-flag concern (`AllowStarPower` and its ordering against `SetPracticeSection`) is therefore moot. |
| Cursor resets | `TrackPlayer.ResetStarPowerPathCursors()` called at all four `BeatlineIndex = 0` sites: `Initialize`, `ResetPracticeSection`, `SetPracticeSection` and `SetReplayTime`, plus `OnStarPowerPathSet`. It clears `SpPathDiverged` along with the three cursors — the flag is derived from where they are, so rewinding them and leaving it set would leave the two disagreeing. `SetPracticeSection` additionally calls `RecomputeStarPowerPath()` after the engine is rebuilt. |
| Logging | Info level, at every path build: activation count, the first activation's tick/time/note index, `ProjectedScore`, `ScoreGainOverNoActivations` and the solve time. Divergence logs its reason and the song time once. |

**Divergence detection** is `TrackPlayer.UpdateStarPowerPathDivergence()`, run every frame from
`UpdateVisuals`, plus one engine-event hook. The per-frame part reads live stats rather than
subscribing to engine events, because the one number that says an activation happened at all —
`BaseStats.StarPowerActivationCount` — has no event of its own. **Three** ways to go off-plan:

1. A Star Power phrase was failed — `BaseEngine.OnStarPowerPhraseMissed`, raised from
   `StripStarPower` (`BaseEngine.Generic.cs:1155`) whenever a note inside a phrase is missed or
   overstrummed. Hooked in `TrackPlayer<TEngine, TNote>.OnStarPowerPhraseMissed(TNote)`, which the
   per-instrument players already subscribe to the engine (`FiveFretGuitarPlayer.cs:282`), so no
   new subscription is needed and drums/keys get it for free once they have paths. This is an event
   rather than a per-frame read because nothing in `BaseStats` says a phrase was *lost* —
   `StarPowerPhrasesHit` only counts the ones that landed, and the meter it would be compared
   against is spent by activations.
2. `StarPowerActivationCount` exceeds the number of plan activations whose grace window the song
   has *entered* → the player activated something the plan does not call for.
3. `StarPowerActivationCount` falls short of the number whose grace window the song has *left* →
   a planned activation went by untaken.

The two per-frame checks compare against `GameManager.SongTime`, not `InputTime`: the plan's
activation times are chart times on the same clock, and `InputTime` leads it by the calibration
offset, which would shift both grace windows by that offset.

##### Divergence is Star Power state only (revised 2026-09-03)

Slices 4–5 shipped two further triggers that are now **removed**: `!IsFc` (any missed note or
overstrum) and `FiveFretGuitarPlayer.OnSustainEnd` with `finished == false` (a dropped sustain).

The first editor run showed why. On the test song the optimizer's first activation is at
**54.6 s**, and a single missed note in the intro flipped the flag at **4.9 s** — so the overlay
was dim for fifty seconds before it had told the player anything, and for the whole rest of the
song. Every marker on a run with one early mistake is dim, which is every real run.

The distinction the old rule missed: the full-combo assumption is a **scoring** assumption. It is
what makes `ProjectedScore` an upper bound and what makes the DP's arithmetic exact. It is *not* a
statement about whether the marked notes are still the right places to activate. A player who drops
a note (or a sustain — sustains feed Star Power only through whammy, which is out of the model
entirely, §1.6) still arrives at every marker with exactly the meter the plan predicted, so the
plan is still followable and the markers still mean what they say. What genuinely invalidates them
is the **meter** diverging: a failed phrase removes a quarter bar the plan spent, an off-plan
activation spends one early, and a skipped activation leaves the meter high and the later windows
mis-timed. Those three are what remain.

The setting copy changed with it: it still discloses that the path assumes a full combo with no
whammy (that is the scoring caveat, and §1.6's whammy disclosure), but no longer says a miss dims
the markers.

**Considered and skipped:** a stronger per-frame check comparing the engine's meter
(`StarPowerTickAmount / TicksPerQuarterSpBar`) against the plan's `MeterAtActivation` at each
upcoming activation. It is strictly implied by the three triggers above under the modelled subset,
and getting it right means reproducing the window-extension arithmetic (§1.5) live against a meter
that moves mid-window, so it is neither cheap nor obviously correct. The three-trigger version is
what ships.

Two cursors (`_spPlanEarlyIndex`, `_spPlanLateIndex`) walk the activation list against
`ActivationTime -/+ SP_PATH_ACTIVATION_GRACE` (0.25 s), so a human tap near the marker is not read
as a deviation in either direction. A one-cursor version is wrong: without the early bound, an
on-time activation reads as "off-plan" for the whole grace window. The flag is set once and never
un-set within a run; only a rebuilt path (`SetStarPowerPath`) or a replay seek clears it.

#### Slice 5 — rendering, and the prefab decision

**No new prefab, and no prefab edits at all.** `SpPathMarkerElement.CreateRuntimePool(BeatlinePool)`
(`Assets/Script/Gameplay/Visuals/TrackElements/SpPathMarkerElement.cs`) builds the pool from code at
song load:

1. A new `GameObject` gets the beatline pool's parent and its exact local transform, so markers land
   in the same space beatlines do. It is created **inactive**, which is what keeps `Pool.Awake`'s
   prewarm from running before there is a prefab to prewarm from.
2. `Beatline.prefab` is instantiated under it (inactive parent means no `Awake` on a half-built
   object), its `BeatlineElement` removed with `DestroyImmediate` — a deferred `Destroy` would leave
   two `IPoolable`s on the template for a frame and `Pool.CreateNew` looks one up with
   `GetComponent` — and an `SpPathMarkerElement` added. The clone is then the new pool's prefab.
3. `Pool.ConfigureRuntime(prefab, prewarm, cap)` is the one addition to `Pool`
   (`Assets/Script/Gameplay/Pool.cs`): a code-created pool cannot otherwise set the serialized
   `_prewarmAmount` / `_objectCap`, and the serialized defaults (300 / 500) are absurd for ~7
   markers. Markers use 4 / 16.

This was chosen over copying `Beatline.prefab` to `SpPathMarker.prefab` with fresh GUIDs precisely
because a hand-written prefab cannot be verified without opening the editor, and over a serialized
field on the track player prefab because that is a prefab edit this pass cannot check either. The
cost is that the marker's `MeshRenderer` is found with `GetComponentInChildren` rather than
serialized.

The element itself mirrors `BeatlineElement`: `TrackElement<TrackPlayer>`, `ElementTime =>
ActivationRef.ActivationTime`, a `0.07` Y scale (the measure-line thickness), and the material
colour set from `Player.HighwayPreset.StarPowerColor.ToUnityColor()` — read from the preset by the
spawner, never hardcoded a second time. Alpha is `1.0` normally and `0.25` once
`Player.SpPathDiverged` is set; it is re-applied in `UpdateElement()` (guarded by the last applied
value) so markers **already on the highway** dim with the ones still to come. Spawning is
`TrackPlayer.UpdateStarPowerPathMarkers`, a copy of `UpdateBeatlines` over the `SpPathIndex` cursor
and the same `SpawnTimeOffset`.

#### Slice 6 — settings

`ShowStarPowerPath`, a `ToggleSetting` defaulting to **false**, in `SettingsManager.Settings.cs`
immediately after `ShowSectionStrip`, with its `nameof(...)` entry appended to the HUD `MetadataTab`
in `SettingsManager.cs` and `Name`/`Description` in `en-US.json`. The description states the
single-player and 5-fret limits and that the path **assumes a full combo with no whammy** — the
whammy disclosure of open question 5.

#### What still needs verifying in the editor

Nothing here has been through a Unity compile or a real frame; `dotnet build` covers only
`Assembly-CSharp`. In rough order of risk:

1. **The runtime pool.** That `Instantiate` under an inactive parent, `DestroyImmediate` of the
   `BeatlineElement`, and `AddComponent` produce a working poolable — and that the markers appear at
   the right place on the highway, i.e. that copying the beatline pool's local transform was enough.
2. **`GetComponentInParent<TrackPlayer>()` in `TrackElement.GameplayAwake`.** Prewarmed clones are
   inactive, so their `Awake` is deferred to the first `EnableFromPool`; that runs synchronously
   inside `SetActive(true)`, before `InitializeElement`, but it is an ordering worth watching for a
   null `Player` on the very first marker.
3. **The z-fight lift.** The marker mesh's local `y` is set to `0.003` in `InitializeElement`,
   just above the beatline quad's `0.002`, so a marker landing on a beat line is not coplanar
   with it. Worth confirming that 1 mm of highway space reads as "on top" and not as a gap.
4. **The colour actually reading as Star Power orange** through the highway's curve/fade shaders,
   and the dimmed 0.25 alpha still being visible.
5. **The settings row** rendering with its new copy, and the toggle surviving a settings save/load.

#### Manual test steps

1. Settings → Graphics → HUD: **Show Star Power Path** exists right after **Show Section Strip**,
   defaults to off, and its description mentions full combo, no whammy, and single player. Turn it
   on.
2. Play a 5-fret guitar or bass song alone. The log carries one
   `SP path (FiveFretGuitar): N activation(s), first at tick ... projected ...` line. Orange bands
   appear on the highway at those notes.
3. Miss a note that is **not** inside a Star Power phrase, and drop a sustain. Nothing dims, and
   nothing is logged — the plan is still followable.
4. Miss a note **inside** a Star Power phrase. The log carries
   `SP path: diverged — a Star Power phrase was missed`, and every marker — the ones on screen
   included — drops to a faint orange for the rest of the song.
5. Restart and instead activate Star Power somewhere the plan does not mark. The reason logged is
   `Star Power was activated off-plan`.
6. Restart and instead let a marker go by without activating. After ~0.25 s the reason logged is
   `a planned activation was not taken`.
7. Play the same song with a second **human** player: no markers, and the log says
   `SP path: skipped, 2 human player(s) in this run`. Add a **bot** instead of a human: markers come
   back.
8. Play drums or vocals with the setting on: no markers, no log line (the player simply does not
   override `RecomputeStarPowerPath`).
9. Enter practice mode: `SP path: skipped, practice mode`, and no markers appear for any section.
10. Play back a replay of the same song: `SP path: skipped, replay playback`, no markers.

---

## Visual redesign, 2026-09-04

**Supersedes the UI rows of "5.1 Locked UI decisions".** The slice-5 marker — a thin
beatline-thickness band in Star Power orange — was never visually identifiable in the editor.
Two reasons, and only one of them is a bug: it is the same thickness as a beat line, and it is
the same colour as the Star Power notes, the Star Power phrase region and the Star Power bar it
sits next to. A mockup interview (four options, Option D chosen) settled the replacement.

### The decisions

| # | Decision |
|---|---|
| 1 | **Direction D — split the cue in two.** A *spatial* cue on the highway at the activation note, and a *temporal* cue in the HUD. Neither alone is enough: the highway cue is useless while the note is beyond the spawn horizon, and the HUD cue cannot say "on *that* note". |
| 2 | **Colour: the drum Star Power activation green upstream already ships** — trim `#52FF00`, body `#005400`, the `_Color` and `_EmissionColor` of `Assets/Art/Materials/Gameplay/Track/Effects/DrumSPActivationTrim.mat`. **Not** Star Power orange, and the highway preset's `StarPowerColor` is now ignored for the marker outright. The colour is the substantive change: it is the one thing that makes the marker distinguishable from the four orange things beside it, and it borrows a hue the game already means "activate here" with. |
| 3 | **Extent: the activation instant plus a short lead-in of one to two beats**, taken from the chart's own beatlines — **not** the whole Star Power window. A region spanning the window competes with the SP fill and with solo/unison regions for the same highway. |
| 4 | **Highway cue.** The activation note(s) get a bright green ring; a short full-width band about one beat long (body green at `0.17` alpha) sits under them with brighter trim-green rail caps at the highway edges, so the marker has a footprint wider than one lane; a lead-in tick sits on the beat before. Louder than a beat line by construction — a beat line is one 0.05-thick quad, this is a beat-long band plus rails plus a ring. |
| 5 | **Fret glow.** When the activation moment arrives (inside the activation grace window) a **steady** green wash is drawn over the strike line. Steady, never strobing, and **skipped entirely** when `ReduceFlashingLights` is on. |
| 6 | **HUD chip.** A compact green-outlined chip in the top band — the same band as the solo box and the text notifications, above the far end of the highway and off the lanes — reading `ACTIVATE IN {n}` with a countdown in whole beats, `ACTIVATE` inside the grace window. **Its lead-in is decoupled from the highway tick's one beat** (revised 2026-09-04: the first editor test found a one-beat chip "comes and goes so quickly it's almost imperceptible"): the chip appears at the *earlier* of the measure line before the activation and 3.0 s before it, capped at 6.0 s so a slow chart cannot leave it up indefinitely; if the previous activation's window would overlap, the nearer activation takes over. It then holds past the grace window — `ACTIVATE` for 0.75 s on plan, `OFF PLAN` for 1.5 s once diverged — and hides. Visible at no other time; hidden whenever the solo box is showing. Localised. |
| 7 | **Diverged state** (the rule from `088d5016` is unchanged — only Star Power state divergence dims): band, rails, lead-in tick and fret glow all **off**; the ring desaturates to grey at `0.22` alpha; the chip switches to `OFF PLAN`. |
| 8 | **Settings: the same `ShowStarPowerPath` toggle**, Graphics → HUD. Its description was reworded (it said "markers … dim"; it now says the cue rings notes and counts down, and "fades"). Every exclusion stands: practice, replay, more than one human, non-5-fret. |

### How it is built

Still **no prefab, material, scene or shader asset touched.** Everything is cloned at runtime
from `Beatline.prefab`'s quad, which is the one piece of highway geometry the code can reach
without an editor pass and which already carries a material the curve/fade shaders understand.

| Piece | Where |
|---|---|
| Band, rails, lead-in tick, ring | `SpPathMarkerElement` (`Assets/Script/Gameplay/Visuals/TrackElements/SpPathMarkerElement.cs`). `BuildGeometry()` clones the beatline quad once per pooled object — the template quad becomes the band, plus two rails, a lead-in tick and `5 x 4` ring edges. `InitializeElement()` sizes and places them per spawn; `SetQuad` encodes the prefab's convention that a quad rotated `X+90` has its local Y along the highway and its local X across it. |
| Colours | `SpPathMarkerElement.ActivationTrimColor` / `ActivationBodyColor`, copied from the drum activation material. `ApplyColors()` is called every frame but writes only when the divergence flag moves. |
| Ring lanes | `TrackPlayer.GetActivationLaneXPositions(noteIndex, list)`, `virtual` and empty by default, overridden in `FiveFretGuitarPlayer`. Full-width and open-without-a-lane notes contribute nothing — the band already spans everything a ring could. |
| Lead-in and band length | `TrackPlayer.BuildStarPowerPathMarkerInfos()` + `FindLastBeatlineBefore()`, computed once when the path arrives, from `SyncTrack.Beatlines`. Deliberately in the Unity layer: `Assets/Script/Gameplay/SpPath/` stays Unity-free and knows nothing about rendering. |
| Fret glow | `SpPathMarkerElement.CreateStrikeLineGlow()` builds one quad at `STRIKE_LINE_POS`; `TrackPlayer.ShowStarPowerPathGlow()` toggles it. Built only when `ReduceFlashingLights` is off, so the setting is read once per song rather than per frame. |
| HUD chip | `SpPathChip` (`Assets/Script/Gameplay/HUD/SpPathChip.cs`), built from code by `SpPathChip.Create()` into `TrackView`'s `_topElementContainer` — so it inherits `ScaleContainer`'s scaling and the container's placement at the highway's far end with no `TrackView.prefab` edit. The font is borrowed from any `TextMeshProUGUI` already in the view (the solo box and the section strip both have one). The green border is a second, inset `Image` rather than an `Outline` component, which on a solid rect reads as a drop shadow. |
| Chip driving | `TrackView.SetStarPowerPathChip(show, text, diverged)`, re-evaluated from `UpdateTextNotificationStatus()` so a solo starting or ending always re-decides it; and `TrackPlayer.UpdateStarPowerPathHud()`, run per frame off the `_spHudIndex` cursor against `GameManager.SongTime`. |
| Strings | `Gameplay.StarPowerPath.{ActivateIn, ActivateNow, OffPlan}` in `Assets/StreamingAssets/lang/en-US.json`. |

The temporary diagnostic logging from the visibility investigation is gone. What remains is one
`SP path: N activation(s) to draw ...` line per path and one `SP path: spawned marker N/M at t=...`
line per spawn.

The near-black preset-colour fallback is **gone with it**: decision 2 ignores
`HighwayPreset.StarPowerColor` entirely, so there is no preset colour left to fall back from.
`SpPathMarkerElement.DefaultMarkerColor` survives as an alias of the trim green.

### What still needs verifying in the editor

Nothing below has been through a Unity compile or a real frame; `dotnet build` covers only
`Assembly-CSharp`. In rough order of risk:

1. **The runtime geometry.** That cloning the beatline quad 24 times per pooled marker produces
   the intended band + rails + ring + tick, that the `X+90` rotation convention is read the right
   way round (a band that comes out running *across* the highway instead of along it is the
   failure mode), and that the pieces do not z-fight with each other or with the beat lines they
   overlap. Heights are staged `0.0030 / 0.0034 / 0.0040` above the beatline quad's own `0.002`.
2. **`RemovePointOffset = 2f`.** The band is centred on the activation, so half of it is behind
   the strike line when the note lands. If the offset is too small the marker pops; too large and
   it lingers under the frets.
3. **The ring lining up with the notes.** `GetActivationLaneXPositions` duplicates
   `TrackElement.GetElementX`'s arithmetic; a lefty-flip run is the case to check, since the ring
   goes through `GetLanePosition` (which already accounts for highway ordering) and the note
   element does the same, but neither applies `LeftyFlipMultiplier`.
4. **The HUD chip.** That a code-built uGUI chip renders at all inside `Top Elements`, that the
   borrowed font asset is usable, that it does not collide with the streak text, and that it
   really disappears for the whole solo.
5. **The strike line glow** reading as a glow and not as a grey slab, given it is an unlit quad
   with no bloom of its own.
6. **The colour** surviving the curve/fade shaders, and the diverged grey ring still being
   visible at `0.22` alpha.
7. **The settings row** rendering with its new copy.

### Manual test steps (redesigned visuals)

1. Settings → Graphics → HUD: **Show Star Power Path** exists right after **Show Section Strip**,
   defaults to off, and its description mentions full combo, no whammy, and single player. Turn it
   on.
2. Play a 5-fret guitar or bass song alone. The log carries one
   `SP path (FiveFretGuitar): N activation(s) ...` line, one `SP path: N activation(s) to draw ...`
   line, and one `SP path: spawned marker k/N at t=...` line per marker as it comes over the horizon.
3. At each activation: a **green** band about one beat long crosses the highway with bright green
   rail caps at both edges, the note(s) to hit are ringed in the same green, and a tick sits on the
   beat before. Nothing about it is orange.
4. Several seconds out — at the measure line before the activation, or three seconds before it,
   whichever is earlier, and never more than six — a green-outlined **ACTIVATE IN 4** chip appears
   in the top band above the highway, counts down a whole beat at a time to **ACTIVATE IN 1**, and
   switches to **ACTIVATE** as the note arrives. On plan it holds **ACTIVATE** for 0.75 s past the
   grace window and hides; off plan it holds **OFF PLAN** for 1.5 s and hides. It is invisible at
   every other moment of the song.
5. As the activation note reaches the strike line, a steady green wash sits over the strike line.
   It does not blink. Turn **Reduce Flashing Lights** on and replay: the wash is gone, everything
   else is unchanged.
6. Trigger an activation whose lead-in overlaps a solo: the chip does not appear while the solo box
   is up, and comes back if the lead-in outlasts the solo.
7. Miss a note that is **not** inside a Star Power phrase, and drop a sustain. Nothing changes.
8. Miss a note **inside** a Star Power phrase. `SP path: diverged - a Star Power phrase was missed`
   is logged; every marker — the ones on screen included — loses its band, rails and tick, the ring
   goes faint grey, the strike line glow stops firing, and the chip reads **OFF PLAN**.
9. Restart and activate Star Power somewhere the plan does not mark ->
   `Star Power was activated off-plan`. Restart and let a marker go by -> after ~0.25 s,
   `a planned activation was not taken`.
10. The band-run, drums/vocals, practice and replay exclusions are unchanged; re-run steps 7-10 of
    the 2026-09-03 list above.

---

## Divergence, corrected — the phrase-missed false positive (2026-09-04)

**Symptom.** Two consecutive editor runs of *I Just Might (Rhythm Version)* (Expert 5-fret guitar,
single human player, 4 planned activations, first at 54.612 s) both logged

```
SP path: diverged — a Star Power phrase was missed. (FiveFretGuitar, 37.243s)
```

at ~37.2 s, seventeen seconds before the first marker, and the HUD chip sat on OFF PLAN for the
rest of the song. The player had lost no Star Power phrase they were aware of, and their bar was
*full* before 54.6 s — full enough that a further phrase was collected and wasted.

**Where the check lived.** `TrackPlayer.OnStarPowerPhraseMissed(TNote)` called
`SetStarPowerPathDiverged` directly, off the engine's `OnStarPowerPhraseMissed` event. That event
is raised from exactly one place, `BaseEngine.Generic.StripStarPower` (`:1155`), which is reached
from three:

| Site | Trigger |
|---|---|
| `Guitar/GuitarEngine.cs:322` | `MissNote` on a note carrying `NoteFlags.StarPower` |
| `Guitar/GuitarEngine.cs:193` | `Overstrum()`, when `Notes[NoteIndex]` is mid-phrase (phrase-start notes are exempted) |
| `Guitar/GuitarEngine.cs:261` | `HitNote` under `NoStarPowerOverlap`; no shipped preset sets the flag |

So the check was **event-driven, not tick-driven**. None of the classic false-positive causes
applied: it never evaluated a phrase at its tick end, never raced the back-end hit window, never
compared a model phrase count against `StarPowerPhrasesHit`, and never read `StarPowerAmount` with
a tolerance. The engine had already made its decision when the callback fired, and the third site
is unreachable here — the two count rules would have dimmed at the off-plan activation first, and
no activation had happened by 37.2 s.

**The actual defect is the inference, not the timing.** "A phrase was stripped" was treated as
"the plan is now unfundable". It is not, because the player's real meter is fed by a source the
optimizer does not model: **unison bonuses**. `BaseEngine.AwardUnisonBonus` (`BaseEngine.cs:637`)
hands out a free `TicksPerQuarterSpBar` whenever every participant clears a unison phrase, and
`EngineManager.UnisonEvent.OnStarPowerPhraseHit` fires it in single-player runs too — the same log
shows three of them (7.14 s, 18.64 s, 102.52 s) before and around the window in question. Each one
doubles that phrase's yield. That is also the whole explanation for the second symptom: the bar
being full and overflowing well before the first planned activation is what a plan that counts one
quarter bar per phrase looks like when the engine is paying two. The plan was not wrong about the
chart; it was funded more generously than it assumed.

**The fix.** The phrase-strip event is now *recorded and logged*, never acted on. The verdict moved
to §4.4's third bullet, which had never been implemented: at each activation's own note,

```
BaseStats.StarPowerTickAmount < Activation.MeterAtActivation × StarPowerPath.TicksPerQuarterSpBar
```

dims the overlay. `TrackPlayer.CheckStarPowerPathMeter` runs it off a third cursor
(`_spPlanMeterIndex`), at `ActivationTime` rather than at the edge of the grace window — the latest
moment a verdict is still useful, so a phrase landing in the final quarter-second still counts. It
is skipped while `IsStarPowerActive`, because the amount is then a draining window rather than a
bank (which is what it looks like when the player took this very activation early inside its grace
window). The two count rules — off-plan activation, planned activation not taken — are unchanged
and still evaluated first.

`StarPowerPath` gained two fields to make this possible without duplicating a constant a third
time: `TicksPerQuarterSpBar`, and `PhraseEndTicks` (diagnostics).

**Logging.** Every divergence line now carries the engine's phrase counters, meter in ticks and in
quarter bars, activation count, whammy ticks and total ticks earned, plus the plan's activation
count, modelled phrase count, all three cursors, and the next activation's note index, tick, time,
meter and measure-tick window. Phrase strips log their own line with the note's tick and time and
whether the engine reached `StripStarPower` through a missed note or an overstrum
(`note.WasMissed` separates them — `MissNote` sets the miss state before stripping, `Overstrum`
does not). `FiveFretGuitarPlayer.LogStarPowerPathPhrases` logs the model's phrase count and
phrase-end ticks next to the engine's `TotalStarPowerPhrases` at path-compute time.

**Audit of the model against the engine, for the record:**

- *Phrase set.* Both sides read `IsStarPowerEnd` off the same post-modifier note track — the model
  in `ScoreModel.Build`, the engine at `Guitar/GuitarEngine.cs:263-267` — so they agree by
  construction. `SpPathDivergenceTests.PhraseEndTicks_MatchTheEnginesPhraseCountAndTheChart` pins
  it against a live engine's `TotalStarPowerPhrases`.
- *Meter cap.* `SpScoreModel.MeterAfter` clamps at `MaxQuarterBars = 4`, so a phrase collected on a
  full bar is modelled as wasted, exactly as `GainStarPower`'s clamp (`BaseEngine.cs:532-535`)
  does it.
- *Activation points.* The DP's outer loop runs over **every** scoring note index, so it can
  activate anywhere; nothing quantises it or forces it past a phrase.
- *Award timing.* `MeterAtActivation × TicksPerQuarterSpBar` is asserted to equal the engine's
  banked `StarPowerTickAmount` at every activation of a scripted run following the path
  (`MeterAtActivation_IsTheAmountTheEngineHasBankedAtThatActivation`).

So the model is faithful to what it models. **The gap was the unison bonus, which it did not model
at all.** That gap is closed below; the meter rule this section introduced is what made the gap
harmless in the meantime, because a surplus meter can never fall short.

Harness: 39 tests (was 35), `dotnet test tools/SpPathTests/SpPathTests.csproj`.

---

## Unison bonuses, modelled (2026-09-04)

Closes the open item this fork carried since the divergence investigation above.

### What the engine does

| Step | Where |
|---|---|
| Hitting a phrase-end note gains a quarter bar and raises `OnStarPowerPhraseHit` | `AwardStarPower`, `BaseEngine.Generic.cs:1158-1163` |
| The container forwards it to the manager | `EngineContainer.OnStarPowerPhraseHit`, `EngineManager.cs:98-101` |
| The manager finds the `UnisonEvent` whose *own* phrase for this engine contains the hit time (`phrase.Time <= time <= phrase.TimeEnd`) and counts a success | `EngineManager.UnisonEvent.cs:336-360` |
| When `SuccessCount == ParticipantToPhrase.Count` every participant is sent `AwardUnisonBonus`, once (`unison.Awarded`) | `AwardStarPowerBonus`, `:362-380`; `UnisonEvent.Success`, `:122-140` |
| Which is a second `GainStarPower(TicksPerQuarterSpBar)` | `BaseEngine.cs:637-641` |

Two consequences the model depends on:

- **A single-player run is awarded every unison.** `ParticipantToPhrase` holds only the *registered*
  engines (`AddPlayerToUnisons`, `:158-200`), so with one player the one success is all of them. The
  unison *phrases* themselves come from the chart, not from who is playing
  (`GetUnisonPhrases`, `:232-330`: the player's Star Power sections that coincide, within a
  sixteenth minus a tick, with a section on a track from another instrument group). Pinned by
  `SpUnisonTests.ASinglePlayerRunReallyIsAwardedTheUnisonBonus`, which drives one registered engine
  and finds `TotalStarPowerTicks == (phrases + unisons) × quarter`.
- **Under `NoStarPowerOverlap` a phrase hit while active is stripped, not awarded**
  (`Guitar/GuitarEngine.cs:259-261`), so `OnStarPowerPhraseHit` never fires and there is no unison
  bonus either. The model's existing overlap branch already covers this.

### What the model does

`SpScoreModel` takes an optional `IReadOnlyList<SpUnisonPhrase>` — plain quarter-tick ranges, no
Unity and no `YARG.Core.Engine` types on the boundary. In the constructor it precomputes
`_quarterBarsGained[scoringNote]`: 0, 1 for a phrase end, 2 when that note's tick falls inside one
of the ranges (ends included, mirroring the engine's time test). Everything downstream reads that
array instead of `IsPhraseEnd`:

- `MeterAfter` adds `gained` and clamps once at `MaxQuarterBars`. Exact, because the engine's clamp
  is applied on *every* `GainStarPower` call and `min(min(m+1,4)+1,4) == min(m+2,4)`.
- `WalkWindowEnd` runs its extension step `gained` times, each step
  `E ← min(E + quarter, m + full)` with the same `m` (the phrase note), which is what the engine's
  `end = position + amount` gives when `GainStarPower` runs twice from the same position.

Nothing else changes: with no list (or an empty one) every gain is 1 and the model is bit-for-bit
what it was, which `SpUnisonTests.NoUnisonPhrases_LeavesThePathExactlyWhereItWas` asserts against
the existing golden.

`StarPowerPath` gained `UnisonPhraseEndTicks`, the subset of `PhraseEndTicks` the plan expects a
bonus on — diagnostics, and the compute-time log's unison count.

### Plumbing

`FiveFretGuitarPlayer.GetUnisonPhrases()` reads `EngineContainer.UnisonPhrases` — the very list the
award path matches against — and hands it to `SpPathOptimizer.Optimize`. The container is built in
`CreateEngine`, which runs before every `RecomputeStarPowerPath` call site, and a null container
costs the plan its bonuses rather than the whole overlay. **No change to `GameManager` or to
`YARG.Core` was needed.**

The compute-time log lines now carry the unison count, and `LogStarPowerPathPhrases` prints the
model's unison count next to the container's.

### Goldens

`drawntotheflame` **does have unisons** — 11 of its 20 guitar phrases coincide with bass phrases —
so the fixture now carries two optima, and both are exact:

| Golden | Value | Engine it is the optimum for |
|---|---|---|
| `SpPathOptimizerTests.DrawnToTheFlameGuitarOptimalScore` | 392,750 | An engine with **no** `EngineManager`, which awards no unison bonuses. Unchanged; every pre-existing test drives this engine. |
| `SpUnisonTests.DrawnToTheFlameGuitarUnisonOptimalScore` | **427,954** | An engine registered with an `EngineManager`, i.e. the game. 10 activations instead of 7, the first moving from 38.25 s to 24.0 s. |
| `GoldenScoreTests.DrawnToTheFlameGuitarGreedyBotScore` | 376,558 | Stock greedy bot, no `EngineManager`. Unchanged. |

The unison-aware plan is reproduced on a live engine *exactly* — meter at activation, both window
bounds and `TotalScore`, for all ten windows
(`SpUnisonTests.UnisonAwarePlan_MatchesALiveEngineThatAwardsUnisons`, which is the unison extension
of `SpPathDivergenceTests.MeterAtActivation_IsTheAmountTheEngineHasBankedAtThatActivation`; the
latter keeps testing the no-`EngineManager` engine).

### The fixture

`SyntheticChart.Dense.Load(withUnisons: true)` adds a `PART BASS` track carrying Star Power phrases
over the **first phrase of each block only**. It has to be a subset: a bass track mirroring *every*
guitar phrase produces no unisons at all, because `ChartExtensions.SpListIsDuplicate` drops a
candidate track whose section list matches an accepted one tick for tick. On that fixture the plan's
first activation moves from 16.0 s to 12.0 s and the projection from 30,750 to 31,550 — the
"markers are late" symptom, reproduced and fixed in miniature.

Harness: 49 tests (was 39), `dotnet test tools/SpPathTests/SpPathTests.csproj`.

### Still not modelled

Whammy (§1.6, deliberate) and coda/BRE bonuses. Both can only make the player's meter *exceed* the
plan's, never fall short.

---

## The dim states are gone, and the activation note is recoloured (2026-09-04)

**Locked by the user, superseding decision 7 of "Visual redesign, 2026-09-04" and the "Behaviour on
deviation" row of §5.1.**

### The path is always shown at full brightness

The overlay is *information about the next run*, not a live scoreboard. A plan that fades out the
moment the player drops a phrase is a plan they cannot read for the rest of the song, which is
exactly when they most want to see where the good activations were. So:

- `SpPathMarkerElement.ApplyColors()` has one state. The dimmed band/rail/tick removal, the grey
  ring (`DivergedRingColor`) and `DIVERGED_ALPHA` are deleted, and `UpdateElement()` no longer
  repaints per frame — the colours are written once per spawn.
- The strike line glow depends on the activation window only:
  `TrackPlayer.UpdateStarPowerPathHud()` calls `ShowStarPowerPathGlow(atActivation)`.
- The HUD chip has one state too: `ACTIVATE IN n`, then `ACTIVATE`, held `SP_CHIP_HOLD_ON_PLAN`
  (0.75 s) past the grace window. `SpPathChip.SetState(bool, string)` and
  `TrackView.SetStarPowerPathChip(bool, string)` lost their `diverged` parameter, and
  `SP_CHIP_HOLD_DIVERGED` and the `Gameplay.StarPowerPath.OffPlan` string are gone.

**The divergence detection stays, as a log-only diagnostic.** `UpdateStarPowerPathDivergence`,
`CheckStarPowerPathMeter`, `NoteStarPowerPhraseLost`, `SetStarPowerPathDiverged` and their detailed
log lines are unchanged — they are the cheapest way to check the model against the live engine, and
the meter rule from "Divergence, corrected" is worth keeping honest. `BasePlayer.SpPathDiverged` is
still set; **nothing visual reads it any more.**

### The activation note itself is green

The band, rails, ring, tick, glow and chip are all built at runtime out of cloned quads and
code-built uGUI, and none of them had been seen rendering. The note model, by contrast, is
unquestionably on screen. So the cue now also recolours the note it is about:

- `INoteElement.IsStarPowerPathActivation` (`NoteElement.cs`), implemented as an auto-property on
  `NoteElement<TNote, TPlayer>` so every instrument compiles.
- `TrackPlayer.SpawnNote` **always** assigns it — elements are pooled, so a stale `true` would leave
  an ordinary note green. The value comes from `TrackPlayer.SpawningActivationNote`, set by
  `UpdateNotes` for the whole chord from `IsStarPowerPathActivationNote(noteIndex)`, which reads the
  `_spActivationNoteIndices` set built alongside `_spMarkers`.
- `FiveFretGuitarNoteElement.TryApplyStarPowerPathColor()` short-circuits `UpdateColor()` to
  `SpPathMarkerElement.ActivationTrimColor` with `NoteGroup.BoostEmission(2.5f)` (a new method on
  `NoteGroup` that scales the emission colour `SetColorWithEmission` just wrote). Because it runs
  from `UpdateColor`, it is honoured on every repaint path — spawn, Star Power toggle, hit and miss
  — and it releases the colour as soon as `NoteRef.WasHit || NoteRef.WasMissed`, after which the
  normal colours apply.

### Temporary diagnostics — removed 2026-09-04

The two `SP path: TEMPORARY` log families added for the runtime-geometry visibility investigation
(`SpPathMarkerElement.LogSpawnDiagnostics()`, one line per spawned marker; and one line per glow
show in `TrackPlayer.ShowStarPowerPathGlow()`) are **gone.** The user confirmed the highway band,
the recoloured note and the chip all render, which is the question they existed to answer. The
permanent log lines stay: the optimizer's compute-time line, the per-marker spawn line, and the
whole divergence/phrase-strip family.

### Manual test steps (amended)

Steps 1-6 of "Manual test steps (redesigned visuals)" stand, with two additions and two rewrites:

- **3a.** The activation note(s) themselves are drawn in the same bright green as the ring and bloom
  slightly, from the moment they spawn until they are hit or missed. Every note of an activation
  chord is green, not just the lowest.
- **4a.** The chip only ever reads `ACTIVATE IN n` and `ACTIVATE`. There is no `OFF PLAN`.
- **8 (rewritten).** Miss a note **inside** a Star Power phrase.
  `SP path: Star Power phrase stripped ...` appears in the log, and possibly
  `SP path: diverged — ...` later. **Nothing on screen changes:** the markers, the green notes, the
  glow and the chip all stay at full brightness for the rest of the song.
- **9 (rewritten).** Activate Star Power somewhere the plan does not mark, or let a marker go by.
  The divergence line is logged; again, the cues stay bright and keep marking every remaining
  activation.


---

## Player settings (2026-09-04)

The cue's colour and the chip's timing are the two things a mockup cannot settle for everyone —
colour because the default green collides with some highway presets and with some kinds of colour
blindness, timing because how much warning is useful scales with the player. Both are now the
player's, along with a separate switch for the strike line glow, which is the one piece of the cue
that moves.

All four live in **Settings → Graphics → HUD**, immediately after `ShowStarPowerPath`, and all four
are wired to `EditableWhen = () => ShowStarPowerPath.Value`, so they grey out as a group whenever
the path itself is off (`MetadataTab.OnSettingChanged` re-applies `SetEditable` on every change, so
this happens live). Like every other Star Power path setting they are read once per path, in
`TrackPlayer.ReadStarPowerPathSettings()` called from `OnStarPowerPathSet`; a change made from the
pause menu takes effect on the next song, which the localised descriptions say outright.

| Setting | Type | Range / default | Consumed by |
| --- | --- | --- | --- |
| `StarPowerPathColor` | `ColorSetting` (the existing colour-picker row, no transparency) | default `#52FF00` | `SpPathMarkerElement.SetCueColor()` → `ActivationTrimColor` / `ActivationBodyColor`, read by the band, rails, ring, lead-in tick, strike line glow, `FiveFretGuitarNoteElement.TryApplyStarPowerPathColor()` and `SpPathChip`'s border, label and body |
| `StarPowerPathChipLeadIn` | `SliderSetting`, seconds | 1.0 – 8.0, step 0.5, default 3.0 | `TrackPlayer._spChipMinLeadIn` in `BuildStarPowerPathMarkerInfos()` |
| `StarPowerPathChipHold` | `SliderSetting`, seconds | 0.0 – 3.0, step 0.25, default 0.75 | `TrackPlayer._spChipHold`, behind `ChipHoldDuration` |
| `StarPowerPathFretGlow` | `ToggleSetting` | default on | the `CreateStrikeLineGlow` guard in `OnStarPowerPathSet` |

Details worth keeping straight:

- **The body tint is derived, not configured.** `SpPathMarkerElement.DeriveBodyColor()` takes the
  cue colour's hue and saturation at `BODY_VALUE_SCALE = 0.33` of its value, which reproduces the
  `#005400`-to-`#52FF00` relationship the redesign locked while working for any hue. The chip's
  label and body are derived the same way — a desaturated full-value tint and a near-black wash —
  so one colour drives eight surfaces.
- **The measure-line rule is unchanged.** The chip still appears at whichever is *earlier*, the
  measure line before the activation or the lead-in seconds, and the 6 s cap still applies —
  except that the cap becomes `max(6, lead-in)` when the player asks for more than six seconds,
  since capping below an explicit request would silently ignore the setting.
  (`SP_CHIP_DEFAULT_MAX_LEAD_IN`, `_spChipMaxLeadIn`.)
- **`ReduceFlashingLights` still wins.** The glow is never built when it is on, whatever
  `StarPowerPathFretGlow` says: an accessibility setting outranks a cosmetic one.
- **Slider stepping is new plumbing.** `SliderSetting` gained an optional `step` constructor
  parameter that snaps in `SetValue`, relative to `Min`, so the snap applies to the handle, the
  numeric field, the navigation-scheme increase/decrease entries and a value loaded from disk
  alike. `step` defaults to `0` (continuous), so every existing slider is untouched.
- **Serialisation round-trips with no new work.** `AbstractSettingConverter` writes
  `ISettingType.ValueAsObject` against `ValueType`, so the two sliders are floats, the toggle a
  bool and the colour a `Color` — exactly what `GraphicalSongProgressTint` already stores. A
  settings file written before these existed simply lacks the keys, and the property initialisers
  supply the defaults.
