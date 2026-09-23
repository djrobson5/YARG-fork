# Score sync between PCs

## What it is

The user plays on more than one Windows PC and wants each PC's scores to show up on the others.
The fork already has a one-way, overwrite-only import (`tools/import-scores.ps1`, parked in
`docs/roadmap.md` Feature 1). This replaces that need with a two-way merge that runs inside the
game and travels through a cloud drive the user already has: OneDrive or Google Drive.

The game never talks to Microsoft or Google. Each provider's Windows desktop app keeps a local
folder in sync with the cloud, and the game only reads and writes files in that folder.

## Decisions (locked 2026-09-22)

| Question | Decision |
|---|---|
| Devices | Windows PCs only. Nothing here is built or tested for other platforms; the settings section is hidden outside `UNITY_STANDALONE_WIN`. |
| Transport | **Sync folder.** The OneDrive or Google Drive for desktop app must be installed on every PC. No sign-in inside YARG, no app registration with either provider, no tokens. |
| Provider choice | A setting: `Off` (default), `OneDrive`, `Google Drive`, `Custom Folder`. Picking a provider auto-detects its folder; the folder can always be overridden with Browse. `Custom Folder` covers any other sync tool (Dropbox, a NAS share, a USB stick). |
| Trigger | **Automatic and manual.** Export after every song that records a score; import at startup; a **Sync Now** button in Settings that does both; a status line with the last sync time and result. |
| Replays | **Not synced.** Scores only. A synced game record keeps its `ReplayFileName`; the history screen already reports a missing replay file gracefully (`ReplayViewType.cs`, "The replay for this song does not exist!"). |
| Unknown profiles | **Match by ID, then by name, else create.** A source profile whose ID exists locally is the same player. Otherwise a local profile with the same name (trimmed, case-insensitive) is the same player, and the source ID is remapped to the local one. Otherwise the profile is created locally **with the source's ID**, so later syncs match it by ID. No prompt; the import toast names each created profile. |
| Merge direction | Always a **union**. Nothing local is ever deleted or overwritten by an import. Deleting a score on one PC does not delete it on the others. |
| YARG.Core | Untouched, as with every fork feature. |
| Account in use | The user syncs through their work/school (UO) OneDrive, knowingly. Moving to another account later is only a folder change: every export is the PC's full history, so the first export into a new folder carries everything, and nothing lives only in the old one. |

## Mechanism

### One file per PC

The sync folder holds a subfolder, `YARG Score Sync`, with **one file per PC**:

```
<sync root>/YARG Score Sync/
    DESKTOP-GAMING-3f9a1c2e.yargsync
    LAPTOP-7b04d1e8.yargsync
```

Each PC writes only its own file and reads everyone else's. Two PCs therefore never write the
same file, so the sync clients never have a conflict to resolve, and nothing depends on either
client's conflict handling.

The live `scores.db` is **never** placed in the sync folder. SQLite files under a sync client
get torn copies and conflict duplicates, and a lock held by the game would stall the client.

### Device identity

`<PersistentDataPath>/score-sync-device.json` holds a GUID generated on first use and the
machine name at that time. The file name is `<MachineName>-<first 8 hex of GUID>.yargsync`; the
GUID part keeps two PCs with the same machine name apart. The identity file is local and never
synced.

### Export file format

A gzip-compressed JSON document:

```
{
  "Format": 1,
  "DeviceId": "...", "DeviceName": "DESKTOP-GAMING",
  "ExportedAt": "2026-09-22T21:14:03Z", "AppVersion": "v0.15.0-sectionfc.9",
  "Profiles":            [ { "Id", "Name", "GameMode", "CurrentInstrument", "CurrentDifficulty" } ],
  "Players":             [ PlayerInfoRecord ],
  "Games":               [ GameRecord + its PlayerScoreRecords, nested ],
  "SectionCompletions":  [ SectionCompletionRecord ],
  "SectionProgress":     [ SectionProgressRecord ]
}
```

- Every row in the local database is exported, **including rows that came from other PCs**. That
  makes the merge transitive (a third PC gets everything) and costs nothing, because the import
  deduplicates.
- Auto-increment IDs (`GameRecord.Id`, `PlayerScoreRecord.Id`, the section tables' `Id`) are
  local and are not used for matching. Player scores are nested under their game so the link
  survives without them.
- Profiles carry only identity plus the fields needed to make a usable profile. Bindings,
  presets and theme settings never travel: preset GUIDs may not exist on the other PC.
- `Format` is checked on read. A file with a higher format than this build understands is
  skipped with a status message asking the user to update this PC.
- Written to `<name>.yargsync.tmp` and then swapped in with `File.Replace`, so a sync client
  never uploads a half-written file.

### Merge rules

Run in **one SQLite transaction** per source file; a failure rolls back and leaves the database
exactly as it was.

| Data | Match key | Rule |
|---|---|---|
| Profiles | ID, then name | See "Unknown profiles" above. A created profile goes through `PlayerContainer.AddProfile` and `SaveProfiles`. |
| `Players` | player ID (after remap) | Insert if missing. Local name wins. |
| `GameRecords` + `PlayerScores` | `SongChecksum` + `Date` (exact ticks) + `BandScore` | If a local game matches, skip the whole game and its player scores. Otherwise insert the game, then its player scores with the new `GameRecordId` and remapped `PlayerId`. |
| `SectionCompletions` | checksum, player, instrument, difficulty, harmony, section index | Insert if missing. If present, keep the **earlier** `FirstCompletedDate`. |
| `SectionProgress` | its unique key | **Recomputed**, not copied: `CompletedCount` = distinct completed sections for the key after the completions merge, clamped to `SectionCount`. `SectionCount` and `LastUpdated` come from whichever row has the newer `LastUpdated`. |

Afterwards, clear the in-memory caches the merge made stale:
`ScoreContainer.InvalidateScoreCache()` and `ScoreContainer.InvalidateSectionProgressCache()`.

Record matching is idempotent: importing the same file twice changes nothing the second time.

### Skipping unchanged files

`<PersistentDataPath>/score-sync-state.json` records, per source file, its size and write time
as listed plus the `DeviceId` and `ExportedAt` inside it, at the last successful import. The
skip happens in two stages:

1. A file whose listed size and write time have not moved is not opened at all. Listing never
   downloads an on-demand or streamed file, so this stage is cheap and works offline.
2. A file that did move is read, and its merge is still skipped if that device's `ExportedAt`
   was already imported, for instance when a sync client only touched the file.

A file that fails to read or import is not recorded, so it is tried again next time. The state
also holds the last export time and the last result for the status line.

### Safety

- Before the **first** import on a PC, `scores.db` and `profiles.json` are copied to
  `*.pre-sync.bak` beside them. After that the backup is left alone, so it always holds the
  pre-sync state.
- The source file is untrusted input: parse errors, missing fields and implausible values skip
  that file (logged and shown in the status line) and never throw into the game.

### Timing

- **Export** runs on the thread pool after `RecordScores` returns, so it never delays the score
  screen. Only one export is in flight at a time; a second request while one runs sets a flag
  and exports once more at the end.
- **Import at startup** runs after the score database and profiles are loaded, asynchronously,
  so the main menu is not held up. With Files On-Demand (OneDrive) or streaming mode (Google
  Drive), opening the other PC's file can trigger a download, and that can be slow or fail
  offline. A failure is a status message, not a dialog.
- **Sync Now** exports, then imports, then shows a short result dialog: games added, section
  records added, profiles created or matched, files skipped and why.
- After an import that added anything, a toast says so ("Added 14 scores from DESKTOP-GAMING").
  If the import created profiles, the toast names them ("Added 14 scores from DESKTOP-GAMING,
  and a new profile: Riffmaster"; "new profiles: A, B" for several). Profiles are still created
  without asking (decided 2026-09-22); the toast is how an automatic startup import tells the
  user that a profile appeared, since only Sync Now shows a result dialog. A created profile
  has no bindings or presets, so it needs setting up before it is played on.

### Folder detection

- **OneDrive:** the signed-in accounts under `HKCU\Software\Microsoft\OneDrive\Accounts\*`
  (`Personal`, `Business1`, …). An account counts only when both `UserEmail` and `UserFolder`
  are set and the folder exists. One account: use it. Several: the Sync Folder row offers them by
  email. **Not** the `OneDrive` environment variable: tested 2026-09-22 on the dev laptop, it
  pointed at `C:\Users\<user>\OneDrive`, a leftover folder from a personal account that is no
  longer signed in (registry `Personal` key with empty `UserEmail`/`UserFolder`, folder not a
  sync root). The account actually syncing was a work/school account (`Business1`), which the
  environment variable does not name (`OneDriveCommercial` was empty). The registry rule
  alone picks the right account; verified in-editor on 2026-09-22. The live root's
  `ReparsePoint` attribute is **not** used: PowerShell shows it (cloud-files tag `0x9000701a`),
  but .NET's `File.GetAttributes` does not, because Windows hides cloud placeholders from
  processes that don't opt in.
- **Google Drive for desktop:** in streaming mode (the default) a virtual drive with a `My Drive`
  folder at its root. Detection scans ready drives for `<root>\My Drive`, drives labelled
  "Google Drive" first. It then checks `%USERPROFILE%\My Drive`, mirror mode's default folder.
  - **Streaming mode was verified 2026-09-22 on a fresh install:** `G:`, volume label "Google
    Drive", reported as FAT32, plain `My Drive` directory. Settings are in
    `HKCU\Software\Google\DriveFS`, but none of them name the mount point directly, so the
    drive scan is the detection.
  - **Mirror mode is unverified.** When nothing is found, the user picks the folder with Browse.
- Detection runs when the provider is chosen, and the result is stored as the folder setting.
  It is not re-detected on every launch, so an override sticks.
- A folder that does not exist or is not writable makes the status line say so. Nothing else
  is affected.

## UI

Settings → **General**, a `Score Sync` section directly below `Updates`. As with the
earlier features, this is mocked up from real prefab values and reviewed before slice 4.
Planned rows:

1. **Sync Provider**: dropdown with Off / OneDrive / Google Drive / Custom Folder.
2. **Sync Folder**: path and Browse (`FileExplorerHelper.OpenChooseFolder`), greyed when Off.
3. **Sync Now**: button, greyed when Off.
4. A status line: last sync time, and the last result or error.

## Slices

1. **Merge core, headless.** Export model, file reader/writer, merge rules as pure C# over
   in-memory rows, with a dotnet test project (`tools/ScoreSyncTests`, like `SpPathTests`):
   dedup, idempotence, profile ID/name/create, remap, section recompute, a newer-format file,
   corrupt input.
2. **Database wiring.** Export from and merge into the real `scores.db` inside one transaction;
   profile creation through `PlayerContainer`; cache invalidation; first-import backup.
3. **Folder layer.** Device identity, per-device file naming, atomic write, scanning other
   devices' files, the unchanged-file skip, provider detection (including the Google Drive check
   on a real install).
4. **Settings UI.** Mockup first, then the four rows.
5. **Automatic triggers.** Export after a recorded score, import at startup, the result toast
   (naming any profiles the import created).
6. **Release build and a two-PC test** on the user's machines.

## Slice 1 notes (done 2026-09-22)

Code in `Assets/Script/Scores/Sync/`, Unity-free and sqlite-free, compiled by link into
`tools/ScoreSyncTests` (74 tests at the time). What slice 1 settled that the sections above leave open:

- **Types.** `ScoreSyncData` holds the five row lists; `ScoreSyncFile` adds the header. Rows are
  `SyncProfile`, `SyncPlayer`, `SyncGame` (with nested `SyncPlayerScore`s),
  `SyncSectionCompletion`, `SyncSectionProgress`. Slice 2 maps the database records to and
  from these; the file format never depends on the sqlite-net classes.
- **Dates are ticks** in the file (`DateTicks`, `FirstCompletedTicks`, `LastUpdatedTicks`), so the
  exact-ticks game match cannot drift through a `DateTime` Kind round trip. `ExportedAt` stays
  an ISO UTC date.
- **Every field is required on read** (`[JsonObject(ItemRequired = Required.AllowNull)]`): a
  missing field rejects the file rather than reading as 0, which would silently change a match
  key. Unknown fields are ignored. A field added later needs a `Format` bump or an explicit
  optional marking.
- **`ScoreSyncFileFormat.Read`** returns `Ok`, `NewerFormat` (checked before binding, so a
  reshaped future file is still reported as newer) or `Invalid` with a message; it never throws.
- **`ScoreSyncMerge.Plan(local, source)`** is pure and returns a `ScoreSyncPlan`: profile
  resolutions, the source-to-local player ID map, rows to insert, completion date updates, and
  section progress writes (insert or update by key), all already on local player IDs. Slice 2
  loads the local rows inside the transaction, plans, and applies. `HasChanges` is false when
  a file adds nothing; the tests plan, apply in memory, and plan again to check idempotence and
  transitivity.
- **Rules the spec did not cover:**
  - A player ID the file references without a source profile (a profile deleted on the other
    PC, its scores kept) keeps its ID, or maps to a local profile of the same name. It never
    creates a profile. Its `Players` row is inserted if missing.
  - A source profile whose ID is only in the local `Players` table (profile deleted on this
    PC) has no local profile, so it follows "else create" and the profile comes back. Open
    question for the user; changing it is one condition in `ResolvePlayers`.
  - Name matching only considers profiles that existed on this PC before the merge. Two players
    the source keeps apart (two same-named profiles, or a deleted profile's scores next to a
    current profile of the same name) stay apart unless a local profile joins them. Found on
    the real nightly data, which has an old deleted "Les Paul" beside the current one.
- **Real-data run (2026-09-22).** A scratch harness loaded copies of this PC's `nightly`
  (550 games, 5 profiles, 6 players) and `dev` (6 games, 27 completions, 3 progress rows)
  data through Microsoft.Data.Sqlite into the core: lossless round trip, 66 KB export for the
  nightly set, no changes on self-import, 556 games on both sides after merging each way,
  idempotent second imports, and a fresh PC fully populated from one merged export. The
  database stores `Date` as ticks (`bigint`), GUIDs as text and checksums as blobs, which is
  what slice 2's mapping reads.
  - Section progress: a local row is recounted even when the source has no row for its key,
    if the merge added completions to it. On equal `LastUpdated`, the local row's
    `SectionCount` wins.

## Slice 2 notes (done 2026-09-22)

- **`ScoreSyncStore`** (`Assets/Script/Scores/ScoreSyncStore.cs`) maps the sqlite-net records
  to and from the `Sync*` types. `Read(db, profiles)` reads every row. `Merge(db, profiles,
  source)` reads, plans and applies inside one `RunInTransaction`, so the plan always matches
  the rows it is applied to. It takes profiles as a parameter and never touches
  `PlayerContainer`, which is what lets it run against a database copy outside the game.
  Games are inserted one by one so each player score gets its new `GameRecordId`. Dates cross
  as raw ticks both ways (sqlite-net stores `DateTime` as ticks).
- **`ScoreDatabase`** gained a "Score sync" region: `QueryAllPlayers`,
  `QueryAllSectionCompletions`, `QueryAllSectionProgress`, `InsertPlayerRecords` (never renames,
  unlike `InsertPlayerRecord`), and `UpdateSectionCompletionDate`.
- **`ScoreContainer.Sync.cs`** is what the game calls:
  - `GetSyncExportData()` returns the export payload.
  - `ImportSyncFile(file)` returns a `ScoreSyncImportResult` and never throws. It makes the
    first-import backups (`scores.db.pre-sync.bak`, `profiles.json.pre-sync.bak`, never
    replaced), merges, then creates profiles through `PlayerContainer.AddProfile` +
    `SaveProfiles`, then refreshes the band high scores, the score and section caches, and the
    stars cache. The result carries the counts for the dialog and `CreatedProfileNames` for the
    toast.
  - Both are main-thread calls, because they use the live connection, the profile list and the
    caches. Slice 5 does the serializing and file I/O off the main thread.
- **Profiles:**
  - Bots are left out of the sync entirely. Their scores are never saved, and a bot sent to
    another PC would arrive as a human.
  - Profiles this version could not load (`UnloadedProfiles`) count as existing IDs during an
    import, so a sync never creates a second profile with a taken ID.
  - A created profile gets the profile-list defaults (note speed 5, highway length 1) plus
    the source's game mode, instrument and difficulty.
  - Scores are committed before profiles are created. If profile creation fails, the scores
    stay under their IDs and the next import creates the profile again.
- **Checked** with `tools/ScoreSyncStoreCheck` (run inside the headless editor via
  `run_script`) against copies of this PC's nightly and dev data. Everything below passed:
  - reads match the raw row counts;
  - self-import writes nothing;
  - each set imported into the other, with a second import writing nothing;
  - exact ticks and fields;
  - both databases converge;
  - a failure forced mid-apply rolls back every row.

  A full 550-game import takes about 0.6 s.
- **Not yet exercised:** `ImportSyncFile` itself (backup files, `PlayerContainer` profile
  creation, cache refresh). It needs the running game, so it gets its first run in slice 5's
  in-game test.

## Slice 3 notes (done 2026-09-22)

- **Unity-free, in `Assets/Script/Scores/Sync/` and covered by `tools/ScoreSyncTests`** (97 tests):
  - `ScoreSyncDevice`: identity file, export file name, and machine names cleaned into valid
    file names. A corrupt identity file is replaced; that only changes the export file name,
    and the union merge makes the old file harmless.
  - `ScoreSyncState`: the two-stage skip described above.
  - `ScoreSyncFolder`: write the export atomically (`.tmp` + `File.Replace`, or a move when
    there is no file yet); list other PCs' files (only the exact `.yargsync` extension, own
    file left out); read one with every sharing flag. A missing, locked or offline file is
    `Unavailable`, distinct from `Invalid`.
  - `ScoreSyncProviders`: the detection rules, which take registry and drive data as input.
- **Game side:**
  - `ScoreSyncProviderProbe` reads the registry and the drives. That code is compiled only on
    Windows, so it finds nothing elsewhere.
  - `ScoreSyncRunner` has `SyncNow(root)`, `Export(root)` and `ImportAll(root)` on the main
    thread. It returns a `ScoreSyncRunResult` whose `Summary()` feeds the status line.
  - A file carrying this PC's own `DeviceId` under another name (a sync client's conflict
    copy) is recorded and never imported.
- **Checked live** with `tools/ScoreSyncStoreCheck/FolderCheck.cs` in the headless editor on
  this PC:
  - detection found only the signed-in UO work/school OneDrive and `G:\My Drive`;
  - in both, write, list, read, unchanged-skip, re-export and own-file exclusion all work, and
    the check folder is removed afterwards.

  A standalone .NET run also confirmed `File.Move`, `File.Replace` and overwrite on both.
- **Not yet exercised:** `ScoreSyncRunner` end to end, since it needs the running game's
  `ScoreContainer`. Slice 5's in-game test covers it.

## Gates

- Fast compile check (CLAUDE.md) after every C# edit.
- `dotnet test tools/ScoreSyncTests/ScoreSyncTests.csproj` from slice 1 on.
- `dotnet test tools/SpPathTests/SpPathTests.csproj` stays green.
- From slice 2 on, `tools/ScoreSyncStoreCheck` in the headless editor (see its README) ends in
  `ALL OK`.
- The settings prefab work in slice 4 is verified structurally through the headless editor
  (`unity command eval_file`) and visually in the GUI editor.
