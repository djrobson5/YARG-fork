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

- Upstream: `https://github.com/YARC-Official/YARG.git`, cloned recursively (submodules matter).
- Unity project. Check `ProjectSettings/ProjectVersion.txt` for the required editor version before building.
