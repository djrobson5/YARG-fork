# SpPathTests — verification harness for the optimal Star Power path

The drift guard for `Assets/Script/Gameplay/SpPath/`. It runs YARG's real headless engine over real
charts and asserts that the fork's duplicated scoring and Star Power models reproduce the engine's
score exactly. See `docs/sp-path-design.md` §3 for why the harness lives here rather than in
`YARG.Core.UnitTests`.

## Running it

```sh
dotnet test tools/SpPathTests/SpPathTests.csproj -nologo -v q
```

Roughly 5 seconds cold, 2 seconds warm. It needs no Unity licence and no `Assets/Packages/`
bootstrap — just the .NET SDK. CI runs the same command from
`.github/workflows/sp-path-tests.yml`.

## Notes

- **net8.0.** The local SDK is 8.0.424; the submodule's own `YARG.Core.UnitTests` targets
  `net10.0` and cannot be built on this machine. `YARG.Core.csproj` itself is `netstandard2.1`,
  so it is referenceable from net8.0 unchanged — the submodule is never modified.
- **`nuget.config`.** The machine-level NuGet config on the dev box has no package sources at
  all, so this directory pins `nuget.org` itself. It does `<clear />` first and then adds
  `nuget.org` as the only source, so restores here ignore any machine- or user-level feeds and
  resolve reproducibly. Scoped to this directory: nothing about the Unity project's
  NuGetForUnity setup changes.
- **The model is compiled by link.** `SpPathTests.csproj` has
  `<Compile Include="..\..\Assets\Script\Gameplay\SpPath\**\*.cs" />`, so **every file under
  `Assets/Script/Gameplay/SpPath/` must stay free of `UnityEngine`**. One stray `using
  UnityEngine;` and this project stops compiling. Anything Unity-shaped (settings gate,
  per-player storage, rendering) belongs in a different folder.
- **`SpPathTests.csproj` is hand-written**, unlike every other `.csproj` in the repo, which is why
  the root `.gitignore` carries an explicit `!tools/SpPathTests/*.csproj` exception.
- **The chart** is `YARG.Core/YARG.Core.UnitTests/Engine/Test Charts/drawntotheflame.mid`, read
  through `SongChart.FromMidi`. Read-only.
- **Bot runs are step-size independent.** The engine queues its own updates at note times,
  sustain burst times and the Star Power end, so 1/30 s and 1/240 s produce identical scores.
  Verified across both instruments before the goldens were recorded.
- **Scripted activations go through `UpdateStarPower`, not `UpdateBot`.** See the class comment on
  `ScriptedBotGuitarEngine`: "activate at note N" has to mean the activation runs on the engine
  pass that then hits note N, or the window starts at the wrong tick.

## Golden numbers

`drawntotheflame.mid`, Expert, stock default engine preset, full combo (1269 guitar notes /
1176 bass notes, both 100% hit):

| Run | Guitar | Bass |
|---|---|---|
| Star Power suppressed | **317,774** (289,774 committed + 28,000 solo) | **389,279** (no solo on this chart's bass) |
| Stock greedy bot policy | **376,558** | **465,083** |
| Optimizer (`SpPathOptimizer`) | **392,750** | **484,979** |

`MaxMultiplier` is 4 for guitar and 6 for bass (`EnginePreset.Instruments.cs:17-18`).

The synthetic fixture (`SyntheticChart.cs`, built as a MIDI in memory) scores **30,692** with no
Star Power, **53,327** under the stock bot and **55,204** under the optimizer, on Expert guitar.

## What the two fixtures cover

`drawntotheflame.mid` is a single-tempo, single-4/4 chart with no disjoint chords, no open notes,
no BRE and no sustain shorter than the burst threshold. It therefore cannot distinguish YARG's
measure-based Star Power bar from a flat-beat one, and several scoring branches never run on it.

`SyntheticChart` exists for exactly those branches: a 4/4 → 3/4 → 4/4 meter change, a tempo change,
a disjoint chord with unequal sustains, a sub-burst-threshold sustain, extended sustains, an open
note, a BRE with and without a coda, and Star Power phrases spread across the meter change.
`SyntheticChartTests.Fixture_CoversTheBranchesTheRealChartDoesNot` asserts the coverage itself, so a
parser change that silently drops one of them fails loudly instead of quietly testing nothing.
