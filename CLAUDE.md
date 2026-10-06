# YARG fork

Personal fork of [YARG](https://github.com/YARC-Official/YARG) (Yet Another Rhythm Game, Unity/C#) for building a custom feature.

## Model workflow (Opus 5.5, 2026-09-22)

These rules apply to every session in this repo. They replace the Fable/Opus split: Fable is retired from the workflow for now, since Opus 5.5 out-benchmarks Fable 5.1 on coding, agentic and design-adjacent work at lower cost.

- **Opus 5.5 is the main model** for everything with judgment in it: design docs, architecture decisions, research, coding, review and integration, root-causing. Coding is done inline by default; there is no orchestrator/coder handoff.
- **Opus subagents are optional**, for genuinely independent parallel pieces or to keep large file sweeps out of the main context. Use the Agent tool with `subagent_type: "opus-medium"`, and re-dispatch a struggling piece as `"opus-high"` (user-level `~/.claude/agents/`; the Agent tool has no effort param, so effort comes from these definitions). Forks inherit the session's model and effort. Cap batches at 3 agents. Briefs are self-contained: files to touch, target behavior, gates to run (the dotnet fast check below; `dotnet test tools/SpPathTests/SpPathTests.csproj` for Star Power changes), and pointers to the relevant `docs/` design files.
- **Effort ladder: `medium` by default; escalate to `high` when the model struggles**: two failed attempts at green gates, thrashing (rewrites without convergence), or a failure that turns out to be a design problem. Escalation is per piece, not session-wide; drop back to medium once the piece is green. `xhigh`/`max` are not part of the ladder.
- **Sonnet does rote work.** Spawn an Agent with `model: "sonnet"` for anything with no judgment in it: WebFetch/WebSearch lookups, basic git actions (status, log, diff, branch listing, fetching a ref), running a build or test and reporting the output, file moves, and similar one-shot chores. Same batch cap and self-contained briefs as Opus. If the task turns out to need a decision (a merge conflict, an ambiguous diff, a failing build to interpret), the agent reports back and the piece moves up to Opus 5.5; Sonnet never decides. Never Sonnet for commits, pushes, releases, or any file edit; those stay with Opus 5.5.

### Prompting Opus 5 / 5.5

Invoke `/opus5-prompts` when writing an agent brief or any prompt that runs on Opus 5 or 5.5. Rules for this repo:

- **Don't add verification steps to prompts.** No "double-check", "re-verify" or "use a subagent to verify"; the model self-verifies, and these cause over-verification. Design docs still list their gates; what goes away is telling the model to verify inside a prompt.
- **Keep the subagent cap explicit in briefs.** Opus 5 over-delegates. Restate the batches-of-3 ceiling, plus: no subagents for verification, and no splitting one small job across several agents.
- **Review prompts are coverage-first.** Never "flag important issues" or "only high-severity"; Opus 5 obeys literally and drops real findings. Ask for everything with confidence and severity, and filter downstream.
- **Never ask an agent to write out its reasoning**; Opus 5.5 can refuse that as `reasoning_extraction`.

## Repo notes

- Upstream: `https://github.com/YARC-Official/YARG.git`, cloned recursively. The `YARG.Core` engine submodule lives at the repo root (`YARG.Core/`), not under `Assets/Plugins`.
- Unity project. Check `ProjectSettings/ProjectVersion.txt` for the required editor version before building.
- Feature spec and locked design decisions: `docs/section-fc-design.md`. Session handoff with current state and next steps: `docs/section-fc-handoff.md`. Running list of open items: `docs/open-items.md`. Read all three before touching section-completion code.
- The four roadmap features have their own locked designs: `docs/updater-design.md` (in-game updater), `docs/delete-song-design.md` (delete songs), `docs/sp-path-design.md` (Star Power path); the Star Power model has a headless harness, run with `dotnet test tools/SpPathTests/SpPathTests.csproj` (49 tests). The rewind-to-section feature's locked design is `docs/rewind-design.md`; its decision record is the wayfinder map, issue #2 on the fork.
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

### Persistent headless editor (Unity CLI + MCP)

Unity's beta `unity` CLI (1.0.0-beta.9, at `C:\Users\djrob\AppData\Local\Unity\bin\unity.exe`, on PATH) and its `unity-editor-mcp` server (registered user-scope in Claude Code) drive a running editor. The required `com.unity.pipeline` package already comes from upstream.

Check `Get-Process Unity` first and leave a GUI editor alone — the CLI attaches to whichever editor owns the project, and two can't own it at once. If none is running, launch one (no `-quit`):

```
Start-Process "C:\Program Files\Unity\Hub\Editor\6000.3.5f2\Editor\Unity.exe" -ArgumentList "-batchmode","-nographics","-projectPath","<repo>","-logFile","<scratch>\unity-headless.log"
```

It serves port 7800; `unity status` or `mcp__unity-editor-mcp__editor_status` confirms readiness. Use it for:

- `recompile` — sub-second full-assembly build, covering the editor assemblies the dotnet check misses.
- Structural prefab/scene verification: `find_assets`, `open_scene`, `get_serialized_fields`, `get_console_logs`, and `eval` (Roslyn C#, so `PrefabUtility.LoadPrefabContents` / `SerializedObject` sweeps find missing scripts and null refs).
- `run_tests`.

Prefer this over hand-editing `.prefab`/`.unity` YAML whenever an editor is reachable. Limits:

- `-nographics` has no GPU, so `screenshot` and `capture_game_view` fail; visual checks need the GUI editor.
- Compile errors boot the editor into Safe Mode and the CLI can't connect, so the dotnet fast check stays the first line.
- Batchmode opens no scene; call `open_scene` before any hierarchy query.
- `UnityEditor.SearchService.SceneSearch.GetHierarchyPath` doesn't exist in 6000.3.
- The headless run rewrites `.vscode/settings.json` (see the gotcha above).
