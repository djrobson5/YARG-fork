# ScoreSyncStoreCheck: score sync against real databases and folders

The database-level check for score sync (`docs/score-sync-design.md`, slice 2). It drives
`ScoreSyncStore` inside the headless editor, so it runs the game's own sqlite-net and native
SQLite, over copies of two real YARG data sets. `tools/ScoreSyncTests` covers the merge rules;
this covers the mapping to database rows, the transaction and rollback, and exact date round
trips.

## Fixture data

Make a folder anywhere outside the repo holding two sets, each a **copy** (never the live files):

```
<data root>/nightly/scores.db     <data root>/nightly/profiles.json
<data root>/dev/scores.db         <data root>/dev/profiles.json
```

On the dev PC these come from `%USERPROFILE%\AppData\LocalLow\YARC\YARG\nightly\{scores,profiles}\`
and `...\dev\{scores,profiles}\`, copied while YARG is closed. The two sets must share no
games; the check expects the merged counts to be the sums. The check writes only to
`<data root>/work/`, which it recreates on every run.

## Running it

With the headless editor up (see CLAUDE.md):

```
unity command run_script --file tools/ScoreSyncStoreCheck/StoreCheck.cs --entry ScoreSyncStoreCheck.Run --args '["<data root>"]' --timeout 150
```

It returns a log ending in `ALL OK` or `N FAILURES`. It covers:
- reads matching the raw row counts;
- self-import writing nothing;
- each set imported into the other, with a second import writing nothing;
- exact ticks and fields surviving the round trip;
- both databases converging;
- a failure forced mid-apply (a trigger on `SectionCompletions`) rolling back every row.

## FolderCheck.cs: the sync folders (slice 3)

```
unity command run_script --file tools/ScoreSyncStoreCheck/FolderCheck.cs --entry ScoreSyncFolderCheck.Run --timeout 150
```

It prints what OneDrive and Google Drive detection finds on this PC; compare that with the
accounts actually signed in. Then, in each detected root, it runs a write, list, read,
unchanged-skip and re-export cycle, using a small synthetic export in a throwaway
`YARG Score Sync Check` folder, and deletes that folder. It never touches real scores. The
sync clients do upload the check folder briefly.

These folders are not projects: the files compile only inside the editor, through `run_script`.
