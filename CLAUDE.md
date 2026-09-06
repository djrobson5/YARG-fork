# YARG fork

Personal fork of [YARG](https://github.com/YARC-Official/YARG) (Yet Another Rhythm Game, Unity/C#) for building a custom feature.

## Model dispatch rules

These rules apply to every session in this repo.

- **Fable orchestrates only.** The top-level session plans, coordinates, reviews results, and talks to the user. It does not do research or write code directly.
- **Opus does research and coding.** Spawn an Agent with `model: "opus"` for any codebase exploration, design investigation, or code change.
- **Fable is the fallback.** If an Opus agent reports it is stuck or produces poor results after a reasonable attempt, spawn a `subagent_type: "fork"` agent (which runs on Fable) to take over that task.
- **Sonnet does rote work.** Spawn an Agent with `model: "sonnet"` for deterministic tasks: git actions (clone, branch, commit, push, status), file moves, running builds or test commands, and similar mechanical steps.

When in doubt about which model fits, prefer the cheaper one and escalate on failure.

## Repo notes

- Upstream: `https://github.com/YARC-Official/YARG.git`, cloned recursively. The `YARG.Core` engine submodule lives at the repo root (`YARG.Core/`), not under `Assets/Plugins`.
- Unity project. Check `ProjectSettings/ProjectVersion.txt` for the required editor version before building.
- Feature spec and locked design decisions: `docs/section-fc-design.md`. Session handoff with current state and next steps: `docs/section-fc-handoff.md`. Running list of open items: `docs/open-items.md`. Read all three before touching section-completion code.
- The four roadmap features have their own locked designs: `docs/updater-design.md` (in-game updater), `docs/delete-song-design.md` (delete songs), `docs/sp-path-design.md` (Star Power path); the Star Power model has a headless harness, run with `dotnet test tools/SpPathTests/SpPathTests.csproj` (49 tests).
- The fork never modifies the `YARG.Core` submodule; fixes that would belong there are worked around from the main repo.

## Building headlessly

Unity 6000.3.5f2 is at `C:\Program Files\Unity\Hub\Editor\6000.3.5f2\Editor\Unity.exe`. Compile check:

```
Unity.exe -batchmode -nographics -quit -projectPath <repo> -logFile <log>
```

### Fast compile check (editor may stay open)

Agents should use this after every C# edit; it takes ~8 seconds and does not touch the Unity editor:

```
cd <repo>
dotnet restore Assembly-CSharp.csproj -nologo -v q
dotnet build Assembly-CSharp.csproj --no-restore -nologo -v q -p:UseSharedCompilation=false
```

Green means `Build succeeded` with no `error CS` lines. The restore is required once per clone because the csproj is SDK-style; it fetches nothing. Output lands in the gitignored `Temp/`.

**After pulling upstream, the generated csprojs are stale** (they carry explicit `<Compile Include>` lists) until Unity next compiles, so `dotnet build` reports `CS2001 could not be found` for deleted files and misses added ones. Don't hand-edit them: copy `Assembly-CSharp`, `Assembly-CSharp-firstpass` and `YARG.Core.Package` to `*.Check.csproj`, replace each one's `Assets\Script\...` / `YARG.Core\YARG.Core\...` compile entries with a `**\*.cs` glob (excluding `Editor`, `.artifacts\**\*.cs` under `YARG.Core\YARG.Core\.artifacts` — leftover build output from `dotnet test` on SpPathTests, causes CS0579 duplicate TargetFrameworkAttribute — and `Assets\Plugins\ZString-Ext\**\*.cs`, which has its own asmdef/csproj and causes CS0121 ambiguous calls), repoint their `ProjectReference`s at the `.Check` copies, add `<Reference>`s for any new `Assets\Packages\*` DLLs, build the check project, then delete the copies and `Temp/obj/*Check*`. `Assembly-CSharp.csproj` also compiles two files outside `Assets\Script` (`Assets\Packages\sqlite-net.1.6.292\content\SQLite*.cs`), which must be kept as explicit entries alongside the glob. This only checks the main runtime assembly (`Assembly-CSharp`), so editor-only assemblies, prefab/scene serialization, and shaders still need a real Unity compile before merging. The `.csproj` files are Unity-generated; if they are missing, regenerate them from Unity Preferences > External Tools.

Exit code 0 and no `error CS` lines means green. Gotchas:

- NuGet packages (DryWetMidi, ZString, sqlite-net, etc.) restore into the gitignored `Assets/Packages/` via NuGetForUnity, which only runs after a successful compile. On a fresh clone, batchmode deadlocks with hundreds of missing-type errors; bootstrap by unpacking the `.nupkg` files from `Assets/packages.config` into `Assets/Packages/<Id>.<Version>/` once, after which the plugin maintains them.
- Unity's VS Code integration rewrites `.vscode/settings.json` on open. Revert it before committing.
- Unity also rewrites `dotnet.defaultSolution` in `.vscode/settings.json` (to `YARG-fork.slnx`) when VS Code is the external editor. Revert with `git checkout -- .vscode/settings.json`.
- Make sure no song or library preview is playing before focusing the editor to trigger a recompile after a large pull. A BASS audio callback firing during the domain unload deadlocked the editor once (log stops at `Begin MonoManager ReloadAssembly` with a NullReferenceException in a DSP callback). Recovery: kill Unity and relaunch; nothing on disk is affected.
