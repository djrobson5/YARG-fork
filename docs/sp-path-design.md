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

Undecided, deferred to a mockup interview before slice 4: marker style, visibility
window, score-gain labels, coexistence with the section strip, settings placement.

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
- **Ordering:** `RunEngineLoop` runs `UpdateStarPower()` *before* `UpdateHitLogic()` (`BaseEngine.cs:400-405`). So a note hit in the same engine loop as the activation **is** doubled. The optimizer's contract is therefore "activate on note N" ⇒ *N is the first note scored under SP*.
- **Boundary rule (to be confirmed by the harness):** a note whose scoring tick maps to a measure tick `>= E` is **not** doubled, because the release runs first in the loop at `StarPowerEndTime`. Marked **unverified** until the harness pins it; a half-open `[activation, E)` interval is the model's assumption.

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
Transitions: ≤ ~10 per state after candidate pruning. **Well under a millisecond**, fully
hidden behind the loading screen.

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
    public int ProjectedScore { get; }        // TotalScore of a perfect run following this path
    public int GreedyScore { get; }           // same run under the bot's naive policy, for the "gain" label
}

public readonly struct Activation
{
    public int  NoteIndex;            // index into TrackPlayer.Notes
    public uint ActivationTick;       // quarter tick of that note
    public uint EndMeasureTick;       // E
    public double ActivationTime;     // MeasureTickToTime / note.Time, for rendering
    public double EndTime;            // FindMinTimeForMeasureTick(E) — once, here, not in a loop
    public int  MeterAtActivation;    // 2..4 quarter-bars, for the divergence check
    public int  ScoreGain;            // points this window adds over not activating
}
```

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

### 4.3 The three cursor-reset sites

An `_spPathIndex` spawn cursor must be reset everywhere `BeatlineIndex` is, in
`Assets/Script/Gameplay/Player/TrackPlayer.cs`:

| Line | Method | Also needs |
|---|---|---|
| `108` | `Initialize` | initial value |
| `397` | `ResetPracticeSection()` | cursor reset only |
| `969` | `SetPracticeSection(uint, uint)` | **cursor reset *and* a full recompute** — this method rebuilds `NoteTrack` from a tick range (`:954-965`) and calls `CreateEngine()` again (`:974`), so the old plan's note indices are meaningless |
| `990` | `SetReplayTime(double)` | cursor reset only |

The spawn loop itself copies `UpdateBeatlines` (`TrackPlayer.cs:561-596`): a `while` over
the ordered activation list against `time + SpawnTimeOffset`, taking from a pool with
`TakeWithoutEnabling()`.

Practice mode is also where SP itself can be switched off —
`Assets/Script/Gameplay/PracticeManager.cs:213` calls
`player.BaseEngine.AllowStarPower(allowPracticeSP)`, which strips `NoteFlags.StarPower`
from the notes (`BaseEngine.Generic.cs:424-447`). When SP is disallowed the path must be
cleared, not recomputed.

### 4.4 Reading live SP state to dim markers

`BasePlayer.BaseStats` (`Assets/Script/Gameplay/Player/BasePlayer.cs:61`, forwarding to
`BaseEngine.BaseStats` at `:59`) exposes everything needed, all public:
`StarPowerTickAmount`, `IsStarPowerActive`, `StarPowerActivationCount`
(`BaseStats.cs:126,141,156`), plus `BaseEngine.StarPowerTickPosition` and
`StarPowerTickEndPosition` (`BaseEngine.cs:116,124`).

Divergence rule: the plan is stale from the first moment the player's meter at an upcoming
activation cannot match `Activation.MeterAtActivation`. Cheapest sufficient check, evaluated
per frame against the next pending activation:

- `BaseStats.StarPowerActivationCount` exceeds the number of plan activations already passed → the player activated off-plan; dim everything from here.
- at the activation's note, `StarPowerTickAmount / TicksPerQuarterSpBar < MeterAtActivation` → the player cannot follow it; dim.
- any missed note (`TrackPlayer.OnNoteMissed`, already hooked for the section strip per `docs/section-fc-design.md` slice 4) → full-combo assumption broken; dim everything.

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
`Assets/StreamingAssets/lang/en-US.json`. Placement is an open question for the mockup.

---

## 5. Rendering sketch

Deliberately thin — the visual decisions are deferred to the mockup interview.

- **Copy `BeatlineElement`** (`Assets/Script/Gameplay/Visuals/TrackElements/BeatlineElement.cs`, 66 lines): a `TrackElement<TrackPlayer>` that overrides `ElementTime`, `InitializeElement()`, `UpdateElement()`, `HideElement()`, and scales/tints a single `MeshRenderer`.
- **Prefab template:** `Assets/Prefabs/Gameplay/Visual/TrackElements/Beatline.prefab` — root → `Parent` → `Mesh`, `Quad.fbx` rotated X+90°, `localPosition (0, 0.002, 0)` to dodge z-fighting, `localScale (2, 0.05, 1)` where X = 2 = `TrackPlayer.TRACK_WIDTH`. Pooled from a `Beatline Pool` on `Assets/Prefabs/Gameplay/Visual/BaseVisual.prefab`. (Prefab internals **unverified** in this pass — taken from the roadmap's visuals research.)
- **Positioning is free:** `TrackElement.UpdateElementPosition()` (`TrackElement.cs:42-61`) does the whole time → z conversion, and returns the element to the pool past `REMOVE_POINT`. `GetZPositionAtTime` (`:32-40`) is the same formula for a second anchor point (an SP-window end marker).
- **Curvature and fade come for free** via `Assets/Script/Gameplay/Visuals/HighwayCameraRendering.cs`'s global `_YargCurveFactors` / `_YargFadeParams`, as long as the material stays off the `FadeExclude` layer.
- **For a region** rather than a point, the existing patterns are `TrackEffectElement.RescaleForZ()` and `LaneElement.SetTimeRange(start, end)` (used at `TrackPlayer.cs:943`) — both cheaper than a new `TrackEffectType`.
- **The section strip is the wrong rendering precedent** (`Assets/Script/Gameplay/HUD/SectionStrip.cs` is runtime uGUI under a `HorizontalLayoutGroup`) but the right data-flow precedent, as used throughout §4.

---

## 6. Slices

Each ends at a state that can be verified without the next one.

| # | Scope | Ends at | Effort |
|---|---|---|---|
| **1** | **Harness skeleton.** `Tools/SpPathTests/` (net8.0, NUnit) building against `YARG.Core.csproj`; one test that loads `drawntotheflame.mid`, runs a bot engine with the *stock* policy, and asserts a known `TotalScore`. No optimizer yet. | `dotnet test` green locally; the golden number recorded in the test. Proves the whole verification story before any model is written. | **S** |
| **2** | **Scoring model, no SP.** `Assets/Script/Gameplay/SpPath/` (Unity-free): duplicate constants, prefix-sum score table, `ProjectPerfectScore()` with no activations. | Test asserts the projection equals a bot run with SP suppressed (`AllowStarPower(false)`, `BaseEngine.Generic.cs:424`), exactly. This is where every rounding rule in §1 gets pinned. | **M** |
| **3** | **SP model + DP.** Window arithmetic, the five-state meter, the DP, `StarPowerPath`. | Test asserts a scripted-activation bot run reproduces `ProjectedScore` exactly for the optimizer's own path, **and** for three hand-picked suboptimal paths (so the model is right, not just self-consistent). Second test: optimizer ≥ stock greedy bot. Add `dotnet test` to CI. | **L** |
| **4** | **Plumbing + logging.** `InitializeStarPowerPaths()`, `BasePlayer.SetStarPowerPath`, the four cursor sites, the practice recompute, the band gate, the divergence check. No visuals — log the plan and the live dim state. | Play a song, read the log: plan present in single-player, absent in a band run, recomputed on a practice-section change, dims on a dropped note. | **M** |
| **5** | **Mockup + rendering.** Interview on the five deferred questions, then the pooled highway element. | Markers on the highway. | **M** |
| **6** | **Settings toggle + copy.** Three files, plus the band-run explanation text. | Toggle works; overlay off by default or on, per the interview. | **S** |

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

**Slice 3 must add a synthetic fixture** — a small hand-authored `.mid` (or a chart built
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
doubled?) are still **unverified** — that is slice 3's job, and §1.5's boundary rule with it.

**Not done here:** the measure-tick conversion and prefix sums from §2.1 (slice 3 needs them,
slice 2 does not), and the CI step from §3.2.

### Risks specific to this plan

1. **Model drift** — the whole reason slices 1–3 are ordered harness-first. `BaseEngine.Generic.cs` is actively changed upstream. The CI test is the guard; a merge that breaks it must block.
2. **The Unity-free constraint on the optimizer** is load-bearing for §3. One stray `UnityEngine` using and the test project stops compiling. Worth a comment at the top of every file in `Assets/Script/Gameplay/SpPath/`.
3. **The `net10.0` / SDK 8 mismatch** means the fork cannot run the existing YARG.Core tests. If upstream retargets, revisit whether the harness should just live there after all — but that would still modify the submodule.
4. **Sustain burst boundary** (§1.4) and the **SP-end boundary** (§1.5) are the two places the model will first be wrong. Slice 3's suboptimal-path tests should be chosen to straddle both.
5. **"Optimal" is a claim** that assumes full combo and no whammy. Dimming handles the first; the second needs UI copy.
6. **CHOpt disagreement is not evidence of a bug** — YARG's bar is 8 measures, CHOpt's is 32 flat beats.

### Open questions for the mockup interview

1. **Marker style** — a band at the activation point, a shaded region spanning the SP window, or both (start + end markers)?
2. **Visibility window** — always drawn, or only within N seconds of the activation point?
3. **Score-gain labels** — show `+12,400` next to each marker, or keep it purely spatial?
4. **Coexistence with the section strip** — both want the top of the highway. Coexist, or mutually exclusive?
5. **Whammy disclosure** — does the UI state that the path assumes no whammy, and where?
6. **Settings placement** — Graphics → HUD next to `ShowSectionStrip`, and per-instrument or global?
7. **Dim styling** — what "diverged" looks like: alpha, desaturation, or outline-only?
