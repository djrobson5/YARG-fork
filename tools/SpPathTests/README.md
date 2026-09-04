# SpPathTests — verification harness for the optimal Star Power path

The drift guard for `Assets/Script/Gameplay/SpPath/`. It runs YARG's real headless engine over a
real chart and asserts that the fork's duplicated scoring model reproduces the engine's score
exactly. See `docs/sp-path-design.md` §3 for why the harness lives here rather than in
`YARG.Core.UnitTests`.

## Running it

```sh
dotnet test tools/SpPathTests/SpPathTests.csproj -nologo -v q
```

Roughly 5 seconds cold, 1 second warm. It needs no Unity licence and no `Assets/Packages/`
bootstrap — just the .NET SDK.

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
- **The chart** is `YARG.Core/YARG.Core.UnitTests/Engine/Test Charts/drawntotheflame.mid`, read
  through `SongChart.FromMidi`. Read-only.
- **Bot runs are step-size independent.** The engine queues its own updates at note times,
  sustain burst times and the Star Power end, so 1/30 s and 1/240 s produce identical scores.
  Verified across both instruments before the goldens were recorded.

## Golden numbers

`drawntotheflame.mid`, Expert, stock default engine preset, full combo (1269 guitar notes /
1176 bass notes, both 100% hit):

| Run | `TotalScore` |
|---|---|
| Guitar, stock greedy bot policy | **376,558** |
| Guitar, Star Power suppressed | **317,774** (289,774 committed + 28,000 solo) |
| Bass, Star Power suppressed | **389,279** (no solo on this chart's bass) |

`MaxMultiplier` is 4 for guitar and 6 for bass (`EnginePreset.Instruments.cs:17-18`).
