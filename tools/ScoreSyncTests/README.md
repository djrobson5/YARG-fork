# ScoreSyncTests — merge core for score sync between PCs

Tests for `Assets/Script/Scores/Sync/`, slice 1 of `docs/score-sync-design.md`: the export file
reader/writer and the merge rules, as pure C# over in-memory rows.

## Running it

```sh
dotnet test tools/ScoreSyncTests/ScoreSyncTests.csproj -nologo -v q
```

Under a second warm. Just the .NET SDK; no Unity licence, no `Assets/Packages/` bootstrap. CI
runs the same command from `.github/workflows/score-sync-tests.yml`.

## Notes

- **Compiled by link.** Every file under `Assets/Script/Scores/Sync/` is compiled into this
  project, so those files must stay free of `UnityEngine` and of sqlite-net (`using SQLite;`).
  The database layer (slice 2) maps the `GameRecord`/`PlayerScoreRecord`/section records to
  and from the `Sync*` types and lives elsewhere.
- **Same setup as `tools/SpPathTests`:** net8.0, `YARG.Core` by project reference (for
  `Instrument`, `Difficulty`, `GameMode`, `StarAmount`, and Newtonsoft.Json transitively),
  implicit usings off so a missing `using` fails here as it would in Unity, a local
  `nuget.config` pinning nuget.org, and a `.gitignore` exception for the hand-written csproj.
- **`Fixtures.Apply`** is an in-memory stand-in for slice 2's apply step. The idempotence and
  transitivity tests plan, apply, and plan again, so the real apply must write exactly what the
  plan says: inserts as listed, completion dates updated on the given local rows, and
  progress rows inserted or replaced by key.
