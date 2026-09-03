# Roadmap

Feasibility research for four features the user is considering after Section FC.
Nothing here is implemented. Written 2026-09-03 against `feature/section-fc` (post
upstream `dev` merge). Every claim is either backed by a file path plus symbol name
or explicitly marked unverified.

Read `docs/section-fc-design.md` and `docs/section-fc-handoff.md` first for the
conventions this fork already follows (slice-by-slice delivery, mockup-then-interview
for any new UI, Opus implements and reviews, Sonnet commits).

---

## Feature 1 — Import scores from an official YARG install

### Goal

The user has a YARG score history on **another Windows machine** (an official build).
They want that history visible in this fork: high scores in the music library, the
history menu, star ratings, play counts.

### What exists today

**Where the data lives.** `Assets/Script/Helpers/PathHelper.cs` `Init()` picks one
subfolder of Unity's `Application.persistentDataPath`
(`%USERPROFILE%\AppData\LocalLow\YARC\YARG`) based on compile defines:

| Build | Define | Folder |
|---|---|---|
| Unity editor, test build | `UNITY_EDITOR` / `YARG_TEST_BUILD` | `…\YARG\dev` |
| Nightly | `YARG_NIGHTLY_BUILD` | `…\YARG\nightly` |
| Official stable release | (none) | `…\YARG\release` |

`CommandLineArgs.PersistentDataPath` overrides all of it, which is a useful escape
hatch for testing an import without touching the live folder.

**This fork's own builds are nightly-flavoured.** `Assets/Editor/Build/CIBuild.cs`
defines `NIGHTLY_DEFINE = "YARG_NIGHTLY_BUILD"`, and `.github/workflows/build-windows.yml`
says so in its release notes. So the fork's packaged build reads and writes
`…\YARG\nightly`, while day-to-day editor work writes `…\YARG\dev`. **These are two
different databases** and any import plan has to name which one is the target.

**The score store.** `Assets/Script/Scores/ScoreContainer.cs` `Init()`:

- `ScoreDirectory` = `<PersistentDataPath>\scores`
- `_scoreDatabaseFile` = `<ScoreDirectory>\scores.db`
- `ScoreReplayDirectory` = `<ScoreDirectory>\replays`

`Assets/Script/Scores/ScoreDatabase.cs` is a thin sqlite-net wrapper. Its constructor
opens the file and calls `CreateTable<T>()` for five types:

| Table | Record class | Origin |
|---|---|---|
| `GameRecords` | `GameRecord.cs` | upstream |
| `PlayerScores` | `PlayerScoreRecord.cs` | upstream |
| `Players` | `PlayerInfoRecord.cs` | upstream |
| `SectionCompletions` | `SectionCompletionRecord.cs` | **this fork** |
| `SectionProgress` | `SectionProgressRecord.cs` | **this fork** |

It then `_db.Execute`s two extra covering indexes on `PlayerScores`
(`IX_PlayerScores_PlayerInstrumentReplayGameRecord`,
`IX_PlayerScores_GameRecordPlayerInstrumentReplayDifficulty`).

`PlayCountRecord.cs` is **not** a table — it is a query projection used by
`QueryPlayerMostPlayedSongs`. There is no stored play count to merge; play counts are
derived by counting `GameRecords` rows.

**Schema versioning: there is none, and it does not matter.** No `PRAGMA user_version`,
no migration table, no `ALTER` statements in `ScoreDatabase.cs`. sqlite-net does the
work: `SQLiteConnection.CreateTable` (vendored at
`Assets/Packages/sqlite-net.1.6.292/content/SQLite.cs`, line ~543) compares the class's
columns against `PRAGMA table_info` and calls `MigrateTable` (line ~817), which issues
`ALTER TABLE … ADD COLUMN` for anything missing. **Opening an older official `scores.db`
with this fork therefore upgrades it in place**: missing columns are added as NULL, and
the two Section tables are created empty.

This was confirmed empirically against the three local databases on this machine
(read-only copies, via Python's `sqlite3`):

| DB | `GameRecords` schema drift | Rows |
|---|---|---|
| `release\scores\scores.db` | no `HasBots` column (older build) | 459 game / 508 player |
| `nightly\scores\scores.db` | has `HasBots` | 550 game / 599 player |
| `dev\scores\scores.db` (this fork's editor) | has `HasBots` + both Section tables | 4 game / 4 player |

`PlayerScoreRecord.Percent` is already `float?` with a documented "added afterwards, so
it is nullable" fallback in `GetPercent()`, so old rows without it degrade gracefully.
The one thing to watch is that sqlite-net's `MigrateTable` issues a bare
`ALTER TABLE … ADD COLUMN "HasBots" integer` with no default, and its row materializer
does `cols[i].SetValue(obj, ReadCol(...))` where `ReadCol` returns `null` for a NULL
column — setting `null` on a non-nullable `bool` property would throw. In practice this
is fine: the local `nightly` database contains all 459 pre-`HasBots` records and every
one of them reads back as integer `0`, not NULL (verified with `typeof(HasBots)` — 550
of 550 rows integer). So the exact release→nightly migration this import needs has
already happened cleanly on this machine. **Partially unverified:** the mechanism that
backfilled those zeros is not fully explained by the sqlite-net code, so spot-check the
history menu immediately after an import rather than assuming.

**Player identity is the real problem.** `PlayerScores.PlayerId` is a `Guid` that comes
from `YargProfile.Id`, persisted in
`<PersistentDataPath>\profiles\profiles.json` (`Assets/Script/Player/PlayerContainer.cs`,
`ProfilesDirectory` / `ProfilesPath`, with `.bak` and `.unloaded` siblings). GUIDs are
generated per profile, per install. The music library pill and every high-score query
key off the *active profile's* GUID (`ScoreContainer._currentPlayerId`,
`FetchHighScores(Guid playerId, …)`), so imported scores are invisible unless the
player's GUID matches.

The local folders demonstrate exactly this failure mode. `release\profiles.json` has
`Les Paul` = `643cf3e6-a047-48ec-a35f-f1d6444e14a1` with 419 scores. `nightly\profiles.json`
has a *different* `Les Paul` = `8d888b85-…`. The nightly database still contains the 419
rows attributed to `643cf3e6`, orphaned from any loadable profile — the `Players` table
keeps the display name, so the history menu still reads sensibly, but the library pill
for the current `Les Paul` shows nothing for those songs. This is precisely what an
import from another machine will produce if `profiles.json` is not brought along.

**Other identity keys are stable across installs:**

- `GameRecords.SongChecksum` / `SectionCompletions.SongChecksum` — content hash of the
  chart, identical anywhere the same chart file exists. Songs absent from the local
  library are handled: `GameRecord` stores `SongName` / `SongArtist` / `SongCharter`
  with the comment "Keep this information in case the user doesn't have the song", and
  `HistoryMenu.cs:119` guards on `SongContainer.SongsByHash.ContainsKey`.
- `PlayerScores.EnginePresetId` — a `Guid` written at
  `GameManager.cs:763` and **never read back anywhere in `Assets/Script`**. A preset ID
  referring to a custom preset that does not exist locally dangles harmlessly.
- `GameRecords.GameVersion` — written once in `ScoreContainer.RecordScore` and read
  nowhere. Purely informational.

**Replays.** `GameRecords.ReplayFileName` / `ReplayChecksum` point into
`<ScoreDirectory>\replays`. On this machine all 550 nightly records have a filename,
but only 105 replay files exist there — the database was clearly copied without the
folder at some point. Replays are large (the local `release\scores\replays` is **375 MB
across 2174 files**), so copying them is a separate, optional decision. A missing replay
file only breaks the "View Replay" action; scores display fine.
Note `ReplayContainer.ReplayDirectory` is a *different* folder
(`<PersistentDataPath>\importedReplays`) for manually-imported replays — do not confuse
the two.

**No section data can be imported.** `SectionCompletions` and `SectionProgress` only
exist in this fork, so an official database contributes zero rows to them. Imported
songs will show a score and no section fraction until the user plays them again here.
This matches the already-documented "pre-existing scores show no fraction" behaviour in
`docs/section-fc-design.md`.

**Insert API available for an in-game importer.** `ScoreDatabase` exposes
`InsertBandRecord(GameRecord)` (sets `record.Id` from the autoincrement),
`InsertSoloRecords(IEnumerable<PlayerScoreRecord>)`, `InsertPlayerRecord(Guid, string)`.
`ScoreContainer` also has cache-invalidation entry points already used by the settings
callbacks. `Assets/Script/Helpers/FileExplorerHelper.cs` provides
`OpenChooseFile(startingDir, extension, callback)` and `OpenChooseFolder(...)` on top of
SimpleFileBrowser, so an in-game file picker is free.

### Exactly what the user must copy from the other machine

From `%USERPROFILE%\AppData\LocalLow\YARC\YARG\<release|nightly>\` on the source machine:

| Path | Needed? | Why |
|---|---|---|
| `scores\scores.db` | **required** | the history itself |
| `profiles\profiles.json` | **strongly recommended** | the only way `PlayerId` GUIDs line up without a remap step; also brings profile names, instruments, presets |
| `profiles\bindings.json` | optional | controller bindings; only useful if the same hardware moves too |
| `scores\replays\` | optional, large | enables "View Replay" on imported records |
| `custom\enginePresets\` | optional | so `EnginePresetId` resolves to a real preset (cosmetic only; nothing reads it today) |

Have them zip the whole `<release>` folder if unsure — everything else in it (settings,
song cache, playlists) is harmless to have available and can be ignored.

**Profile identity, two ways:**

1. **Copy `profiles.json` too**, into the fork's target folder, *before* first launch of
   the fork against that folder. GUIDs match, everything just works. Cost: it replaces
   the fork's existing profiles. If the fork already has profiles worth keeping, the
   file is JSON — the two lists can be concatenated by hand or by script.
2. **Remap GUIDs.** Read the source `profiles.json`, present the user with
   "source profile *Les Paul* → local profile *Dustin*", and rewrite
   `PlayerScores.PlayerId` (and `Players.Id`) during the merge. More code, but survives
   the case where the user has already built up local history under a different profile.

### Approaches

| | (a) One-off offline script | (b) In-game "Import scores" button | (c) Startup auto-migration |
|---|---|---|---|
| Where | Python, run by an agent now against a copied `scores.db` | Settings → a button, `FileExplorerHelper.OpenChooseFile` picks the DB | `ScoreContainer.Init()`, merges from a sibling folder if the local DB is empty |
| Merge logic | full SQL control, easy to iterate and re-run | must be written in C# against `ScoreDatabase` | same as (b) plus a "have I already done this" flag |
| Profile remap | trivially scriptable, can prompt the user in the terminal | needs a profile-mapping dialog (`DialogManager`) | no UI, so it must guess — bad |
| Repeatable by the user later | no, needs an agent | yes | once only |
| Risk to the live DB | back up first; script never ships | ships to users; a bad merge is a support problem | ships, and fires unattended — worst blast radius |
| Effort | **S** | **M** | **M**, and mostly the wrong M |

**Simplest viable path, and probably sufficient:** if the fork's target folder has an
empty or throwaway score history, a **straight file copy** of `scores.db` +
`profiles.json` is the whole feature. sqlite-net migrates the schema on first open. No
code at all. This is worth confirming with the user before writing anything.

A merge is only needed if the user has played meaningfully in *both* installs.

**Merge semantics, if needed.** `GameRecords` has no uniqueness constraint, so
duplicates are possible and must be avoided by key rather than prevented by the schema.
Use `(Date, SongChecksum, BandScore)` — `Date` is a DateTime tick count and is
effectively unique per run. The merge is then:

1. For each source `GameRecords` row not already present, insert it (new autoincrement
   `Id`) and record `oldId → newId`.
2. Insert its `PlayerScores` rows with `GameRecordId` remapped, and `PlayerId` remapped
   if using strategy 2 above.
3. Upsert `Players` rows by GUID (name collisions are cosmetic).
4. Leave `SectionCompletions` / `SectionProgress` alone — no source rows exist.

There is deliberately **no "keep the higher score"** step: `GameRecords` is an append-only
log of individual runs, and high scores are *derived* (`QueryBandSongHighScore`,
`QueryPlayerSongHighScore`). Union the runs and the high score falls out correctly. Play
counts likewise, since they are `COUNT(*)` over the union. The only cost of over-inclusion
is a longer history list.

### Recommended approach

**(a), scoped as a copy-or-merge script**, with the copy shortcut checked first.

1. User zips the source folder and drops it somewhere local.
2. Decide the target: `…\YARG\nightly` if they play the fork's packaged build,
   `…\YARG\dev` if they play in the editor. Probably both, in which case run twice.
3. Back up the target `scores.db` (and `profiles.json`) to `.bak` first.
4. If the target history is negligible → copy `scores.db` and `profiles.json` across, done.
5. Otherwise run the merge script above, then launch once and verify the library shows
   the imported high scores for the active profile.

Defer (b) until the user actually wants to repeat this without an agent.

### Effort

**S** for the script path (a few hours including verification).
**M** for the in-game button (b): dialog, profile mapping UI, error handling, localization
strings, plus the usual mockup-and-interview cycle this fork uses for new UI.

### Risks

- **Overwriting live data.** Back up before any write. The fork's editor DB currently
  holds four Section FC verification runs; the packaged build shares `nightly\` with the
  *official* nightly build, so a careless copy there destroys real history.
- **The fork and the official nightly share `…\YARG\nightly`.** Installing both means
  they interleave into one database. The fork adds two tables the official build does
  not know about — harmless (sqlite-net ignores unknown tables) but worth knowing. If
  the user wants them separate, launch one with `--persistent-data-path`
  (`CommandLineArgs.PersistentDataPath`).
- **NULL booleans from pre-`HasBots` databases** — see the unverified note above.
- **Orphaned `PlayerId`s** if `profiles.json` is not copied. Scores exist but never show
  in the library. Silent, and easy to mistake for "the import failed".
- **Chart-hash misses.** If the other machine's copy of a song differs by a byte, the
  checksum differs and the score attaches to a song the local library does not have. It
  will show in the history menu but never on the library row. Nothing can be done about
  this; mention it so it is not read as a bug.

### Open questions for the user

1. Which target — the packaged fork build (`nightly`), the editor (`dev`), or both?
2. Does the target already hold score history worth preserving, or can it be overwritten?
3. Should the source `profiles.json` replace the fork's profiles, be merged into them,
   or should scores be remapped onto an existing local profile?
4. Copy the 375 MB-ish replay folder, or skip replays?
5. Is a one-time agent-run script enough, or is a repeatable in-game button wanted?

---

## Feature 2 — Delete songs from the in-game library

### Goal

From the music library, remove a song from disk and from the library, without leaving
the game.

### What exists today

Upstream does **not** have this. `gh` searches of `YARC-Official/YARG` issues and PRs
for "delete song", "remove song in:title", "delete chart in:title" and "recycle bin"
returned only playlist/favourite removal work (#1452 "Remove Dead Playlist Hashes",
#1079 favourites). There is no grep hit for song deletion anywhere under
`Assets/Script/Menu/MusicLibrary/`.

Most of the scaffolding a delete needs is already built:

**The menu hook.** `Assets/Script/Menu/MusicLibrary/PopupMenu.cs` is the "more options"
menu, opened from `MusicLibraryMenu.cs` (~line 429) on `MenuAction.Orange`
(`Menu.MusicLibrary.MoreOptions`). It has a `State` enum
(`Main` / `SortSelect` / `GoToSection` / `AddToPlaylist`) and builds entries with
`CreateItem(localizeKey, UnityAction)`. `CreateMainMenu()` already ends with a block
gated on `SettingsManager.Settings.ShowAdvancedMusicLibraryOptions` that casts
`viewType is SongViewType songViewType`, then switches on `song.SubType` for
"View Song Folder" and "Copy Song Checksum". A `DeleteSong` item belongs in exactly
that block — the per-entry-type dispatch a delete needs is already written there.
`PopupMenu.DeletePlaylist` plus the `CloseAfterDialog()` helper (awaits
`DialogManager.Instance.WaitUntilCurrentClosed()`, then restores the library's
navigation scheme) is the closest existing flow to copy.

**The confirm dialog.** `Assets/Script/Menu/Common/Dialogs/ConfirmDeleteDialog.cs`
requires the user to *type* a confirmation string before `DeleteAction` fires. Invoked
via `DialogManager.ShowConfirmDeleteDialog(additionalMessageText, deleteAction, confirmText)`
(`Assets/Script/Menu/Persistent/DialogManager.cs:196`). The canonical call site is
`Assets/Script/Menu/ProfileList/ProfileView.cs:185` — show, `await dialog.WaitUntilClosed()`,
act on a captured bool. Only two callers exist today (profile delete, cache clear), so
the type-to-confirm pattern is reserved for genuinely destructive actions. Passing
`confirmText: song.Name` fits perfectly.

**Entry → disk mapping.** `YARG.Core/YARG.Core/Song/Entries/SongEntry.cs` declares
`abstract EntryType SubType`, `SortBasedLocation`, `ActualLocation`.

| `SubType` | Class | `ActualLocation` is | Delete means |
|---|---|---|---|
| `Ini` | `UnpackedIniEntry` (`Entries/Ini/SongEntry.UnpackedIni.cs`) | the song folder | recursive directory delete — clean |
| `Sng` | `SngEntry` (`Entries/Ini/SongEntry.Sng.cs`) | the `.sng` file | single file delete — cleanest |
| `ExCON` | `UnpackedRBCONEntry` / `UnpackedRBPKGEntry` (`Entries/RBCON/SongEntry.UnpackedConsolePackage.cs`) | `Path.Combine(_root.FullName, _subName)`, a per-song subfolder | delete the subfolder; the parent `songs.dta` still lists it, so the next full scan logs a bad song |
| `CON` | `PackedRBCONEntry` (`Entries/RBCON/SongEntry.PackedRBCON.cs`) | `_root.FullName` — **the whole CON/pkg file**, often dozens of songs | **not possible per song** |

**The CON problem is the real design constraint.** Nothing in the repo writes or repacks
CON containers; `CacheHandler` and `PackedRBCONEntry` only read through `CONFileStream`.
Deleting `ActualLocation` for a `CON` entry destroys an entire Rock Band pack. Either
disable the item for `EntryType.CON`, or offer an explicit "delete the entire pack
(N songs)" with the count made loud in the dialog.

**The song cache is the sharp edge.** `Assets/Script/Song/SongContainer.cs` holds
`private static SongCache _songCache`, where `SongCache`
(`YARG.Core/YARG.Core/Song/Cache/SongCache.cs`) is essentially
`Dictionary<HashWrapper, List<SongEntry>> Entries` — a **list per hash**, because
duplicate copies of the same chart in different folders share a checksum.

- `SongContainer.RequestContainerRefresh()` (~line 1211) re-sorts and refills the
  containers from `_songCache` with no rescan. This is the in-memory removal hook.
- There is **no** API to remove a single entry — `_songCache` is private, so a
  `SongContainer.RemoveSong(SongEntry)` would have to be added there.
- `SongContainer.RunRefresh(bool quick, LoadingContext?)` (~line 150) is all-or-nothing:
  `CacheHandler.RunScan` builds a whole new cache, then `PlaylistContainer.ReplaceUpdatedSongHashes`,
  sort, `FillContainers()`, `MusicLibraryMenu.SetReload(Full)`, `SongSources.LoadSprites`.
  No incremental scan exists. `MusicLibraryMenu.RefreshSongs()` (~line 1322) is the
  "Scan Songs" popup item and runs the full version behind a loading screen.
- `YARG.Core/YARG.Core/Song/Cache/CacheHandler.cs` `QuickScan` is documented as
  "performing very few validation checks … for the sole purpose of speeding through to
  gameplay". It does not stat the files (the only `File.Exists` calls in the cache
  groups are in `UnpackedConsolePackageEntryGroup.cs` and `CONUpdateGroup.cs` — nothing
  covering unpacked-ini or `.sng`). `FullScan` validates, drops missing entries and
  re-serialises `songcache.bin` plus `badsongs.txt`. **There is no way to remove one
  entry from the on-disk cache short of a full scan.**

So: delete the files, refresh only in memory, restart → the quick scan resurrects the
song as a ghost entry that is selectable and unplayable. Any approach must either
rescan immediately, or persist a "cache dirty" flag that forces `RunRefresh(quick: false)`
on next launch.

**Dangling references.** Mostly benign:

- Playlists / favourites (`Assets/Script/Playlists/Playlist.cs`, `List<HashWrapper> SongHashes`,
  with `RemoveSong` / `ContainsSong`; favourites is `PlaylistContainer.FavoritesPlaylist`).
  `Playlist.ToList()` resolves against `SongContainer`, so deleted songs vanish silently
  but leave dead hashes on disk — exactly the rot upstream #1452 targets. Cheap fix: call
  `RemoveSong` on every playlist during the delete.
- Scores (`Assets/Script/Scores/`) are keyed by checksum and there is **no deletion path
  at all** — zero `Delete`/`Remove`/`DROP` hits in that folder. Keeping scores is the
  right call and matches the profile-delete dialog's own wording ("Play history will
  remain and can be accessed in the History tab"). Section FC rows likewise survive, and
  re-adding the song later restores the fraction for free.
- Replays are referenced from score records, not song folders. Leave them.
- Recent / most-played both `TryGetValue` against `SongContainer.SongsByHash` and skip
  misses.
- `GlobalVariables.State.CurrentSong` / `ShowSongs` (set in `SongViewType.PrimaryButtonClick`)
  is an edge case worth guarding if the queued song is the one deleted.

**Recycle Bin vs permanent delete.** No `Microsoft.VisualBasic` reference exists in any
asmdef or in `Assets/packages.config`. The fork builds **both** Windows
(`.github/workflows/build-windows.yml`, `StandaloneWindows64`) and macOS
(`build-release-mac.yml`, `StandaloneOSX`), so this is a genuine platform split. Pulling
in the VB runtime for `FileSystem.DeleteDirectory(..., RecycleOption.SendToRecycleBin)`
is an IL2CPP stripping/AOT risk; P/Invoking `SHFileOperation` from shell32 with
`FOF_ALLOWUNDO` needs no extra assembly. macOS has no managed equivalent without native
glue (`NSFileManager trashItemAtURL:`). The idiomatic gating pattern to mirror is
`Assets/Script/Helpers/FileExplorerHelper.cs:171` / `:188` — `#if UNITY_STANDALONE_WIN /
#elif UNITY_STANDALONE_OSX / #elif UNITY_STANDALONE_LINUX / #else` with graceful
degradation in the `#else`. A sibling `FileDeleteHelper.SendToTrashOrDelete(path)` would
read naturally.

### Approaches

| | (A) Permanent delete + immediate full rescan | (B) Trash where possible + in-memory removal + deferred rescan | (C) Mark-for-deletion batch queue |
|---|---|---|---|
| Flow | popup item → `ShowConfirmDeleteDialog` → delete `ActualLocation` → `await RefreshSongs()` | new `FileDeleteHelper` (SHFileOperation on Windows, plain delete elsewhere) → `SongContainer.RemoveSong` → `RequestContainerRefresh()` + `SetReload(Partial)` → dirty flag forces a full scan next launch | popup item toggles a pending flag drawn on the row; one "Apply deletions (N)" action deletes all, then one full rescan |
| New code | ~40 lines + a localization key | helper, `SongContainer.RemoveSong`, playlist pruning, persisted dirty flag | all of B plus row decoration in `SongView`/`SongViewType`, a pending set that survives sort/filter changes, extra nav entries |
| Feel | full loading screen per delete — brutal on a 10k-song library | instant | instant, one screen at the end |
| Recoverable | no | on Windows only | on Windows only, plus a review step before anything is destroyed |
| Main failure mode | none — disk and cache always agree | dirty flag fails → ghost entries | UI state bugs; destructive step is the safest of the three |
| Effort | **S** | **M** | **L** |

CON handling is orthogonal to all three and should be decided first. Disabling the item
for `EntryType.CON` is nearly free and honest; supporting it properly means repacking a
CON container, which nothing in the codebase does — **XL, and not recommended**.

### Recommended approach

**(B), with the item disabled for `EntryType.CON` and a loud "this removes N songs"
warning for `ExCON`.** If a conservative first slice is wanted, ship **(A)** — it is a
strict subset and its correctness is trivially guaranteed — then upgrade the refresh
strategy once the delete path itself is trusted. That maps cleanly onto the fork's
existing slice-by-slice habit:

1. Popup item, confirm dialog, delete for `Ini`/`Sng` only, immediate full rescan.
2. `ExCON` support with a count warning; `CON` explicitly disabled with an explanatory message.
3. `FileDeleteHelper` with the Windows recycle-bin path.
4. In-memory removal + deferred rescan, replacing the per-delete loading screen.
5. Playlist pruning and a settings gate.

### Effort

**S** for slice 1 alone. **M** for the recommended end state (B). **L** if the batch
queue (C) is wanted.

### Risks

1. **Ghost entries from the quick scan** — the highest-probability defect in anything
   that does not rescan immediately. Test it by deleting, restarting, and checking the
   library, not just by watching the UI update.
2. **CON mass-deletion.** A user removing one song and losing a 50-song pack is the worst
   realistic outcome. Gate it explicitly.
3. **macOS has no trash path**, so "delete" is permanent there unless native glue is
   written. The dialog wording has to be honest about the asymmetry.
4. **Duplicate-hash entries.** `SongCache.Entries` maps one hash to a *list*; a naive
   `Entries.Remove(hash)` removes every copy of the chart. Match on `ActualLocation`.
5. **Deleting the queued song** (`GlobalVariables.State.CurrentSong`).
6. Irreversibility in general — this is the only feature of the three that can destroy
   user data.

### Open questions for the user

1. Recycle Bin on Windows and permanent elsewhere, or permanent everywhere for
   consistency? (Recommended: recycle bin, with the dialog saying which it will do.)
2. `CON` packs: hide the item, show it disabled with a reason, or offer whole-pack
   deletion with a count?
3. Gate behind `ShowAdvancedMusicLibraryOptions` (where "View Song Folder" already
   lives), or give it its own setting, or always show it?
4. One-at-a-time, or the batch queue?
5. Should deleting a song also prune it from playlists and favourites, or leave the
   hashes for a later cleanup pass?
6. Keep scores and Section FC rows for a deleted song? (Recommended: yes — they are
   restored automatically if the song comes back, and there is no deletion API anyway.)

---

## Feature 3 — Mark optimal Star Power activation points on the highway

### Goal

A toggle that draws, on the highway, where a perfect player *should* activate Star
Power to maximise score — the thing CHOpt computes for Clone Hero, shown in-game.

### The domain

An SP path is a **discrete optimisation problem**. You are given a note sequence with
base values, a multiplier that climbs with combo, a set of SP phrases that each grant a
fixed amount of SP, and a rule that activating SP doubles the multiplier for as long as
the bar lasts. Choosing *when* to activate changes which notes get doubled and whether
later phrases are wasted (SP caps, so collecting a phrase while the bar is full throws
it away). The standard formulation is a dynamic program over "activation opportunities"
× "SP bar state", maximising total score. CHOpt additionally models whammy gain on
sustains and "squeezes" (activating a few milliseconds early or late to catch one more
note), which is where most of its complexity lives.

### What exists today

**Nothing.** Greps for `Optimal`, `Squeeze`, `CHOpt`, and SP-sense `Path` across
`YARG.Core` and `Assets/Script` find no existing concept. This is greenfield.

But the pieces the DP needs are unusually well exposed.

**SP is measured in measure ticks, and the constants are public.**
`YARG.Core/YARG.Core/Engine/BaseEngine.cs`:

```csharp
TicksPerQuarterSpBar = SyncTrack.MeasureResolution * 2;   // = 2 measures
TicksPerHalfSpBar    = TicksPerQuarterSpBar * 2;          // = 4 measures
TicksPerFullSpBar    = TicksPerQuarterSpBar * 4;          // = 8 measures
public bool CanStarPowerActivate => BaseStats.StarPowerTickAmount >= TicksPerHalfSpBar;
```

All three are `public readonly` fields, and `protected const int STAR_POWER_MAX_MEASURES = 8`
(`BaseEngine.cs:17`) states the same thing in words. `GainStarPower(uint ticks)` adds and
clamps at `TicksPerFullSpBar`; a completed SP phrase grants exactly `TicksPerQuarterSpBar`
via `AwardStarPower(TNoteType)` (`BaseEngine.Generic.cs:1158`), so **four phrases fill the
bar**. `UpdateStarPowerEnds()` sets
`StarPowerTickEndPosition = StarPowerTickPosition + BaseStats.StarPowerTickAmount` and
converts to a time with `SyncTrack.FindMinTimeForMeasureTick`. Drain is
`CalculateStarPowerDrain(measureTick, lastMeasureTick) => measureTick - lastMeasureTick`
(`BaseEngine.Generic.cs:1073`) — **1:1 with measure ticks**, so a full bar always lasts
exactly 8 measures of chart time regardless of tempo.

Two details that decide correctness and are easy to get wrong:

- SP is credited at the tick of the **note carrying `IsStarPowerEnd`**, not at
  `Phrase.TickEnd` (`GuitarEngine.cs:257-268`). `PhraseType.StarPower` is documented as
  "Mainly for visuals, notes are already marked directly as SP"; the authority is
  `NoteFlags.StarPower` / `IsStarPowerStart` / `IsStarPowerEnd` on `Note`.
- `GuitarEngineParameters.NoStarPowerOverlap` defaults **false**, so phrases collected
  while SP is already active do count (Clone Hero-like). The optimizer must read this from
  the actual parameters rather than assume.

Two consequences matter:

1. **Without whammy the SP bar has only five states** — 0, ¼, ½, ¾, full. That makes the
   DP state space trivially small.
2. **YARG's SP bar is eight *measures*, not thirty-two flat beats.** Drain is
   time-signature aware, unlike Clone Hero. So CHOpt output is *not* ground truth for
   YARG on any chart with meter changes — a useful cross-check, not an oracle.

**Scoring constants.** `BaseEngine.cs:11-13`:
`POINTS_PER_NOTE = 50`, `POINTS_PER_PRO_NOTE = 60`, `POINTS_PER_BEAT = 25`.
`GuitarEngine.AddScore(GuitarNote)` uses `POINTS_PER_NOTE * (1 + note.ChildNotes.Count)`.
Sustains: `TicksPerSustainPoint = SyncTrack.Resolution / (double) POINTS_PER_BEAT`
(`BaseEngine.Generic.cs:106`), scored through `CalculateSustainPoints`.
`UpdateMultiplier()` is `Math.Min((Combo / 10) + 1, BaseParameters.MaxMultiplier)`, then
`*= 2` when `IsStarPowerActive`. Note the comment at `BaseEngine.Generic.cs:917`:
"SustainPoints must include the multiplier, but NOT the star power multiplier" — a real
asymmetry the optimizer must reproduce. `AddScore(int)` also computes the SP component as
an **integer** `scoreMultiplier / 2`, and solo and coda bonuses are *not* SP-multiplied.

`MaxMultiplier` must be read from the parameters, never assumed:
`EnginePreset.Instruments.cs` sets `DEFAULT_MAX_MULTIPLIER = 4` but
`BASS_MAX_MULTIPLIER = 6`, so **bass genuinely reaches 12x under SP**.

**A pure perfect-run scorer already exists, and it is the structural template.**
`BaseEngine.Generic.cs:1296` declares
`protected abstract (int baseScore, int noteScore) CalculateChartScores();`, documented as
"the score if a player were to FC and hit all sustains fully", implemented for guitar at
`GuitarEngine.cs:399` and called from the base constructor. It walks the notes carrying the
combo ramp, skips `IsBigRockEnding`, adds `Math.Ceiling(note.TickLength / TicksPerSustainPoint)`
for sustains, and handles disjoint chords (each child sustain counted separately, combo
incremented once per tick). It deliberately excludes SP — **there is no "perfect run with
SP" function anywhere** — but the optimizer is essentially this function with an SP
decision layer on top.

**These constants are `protected const`, not public.** Anything living in
`Assets/Script` has to re-declare 50 / 60 / 25 and re-derive the multiplier rule. That
is model duplication and the single biggest maintenance risk in this feature.

**SP phrases.** `PhraseType.StarPower` in
`YARG.Core/YARG.Core/Chart/Events/Phrase.cs`, with the comment "Mainly for visuals,
notes are already marked directly as SP" — so the authoritative source is the per-note
SP flag, and `Phrases` is the convenient grouping. Ticks convert via `SyncTrack`
(`TimeToMeasureTick`, `MeasureTickToQuarterTick`, `FindMinTimeForMeasureTick`).
Performance trap: `FindMinTimeForMeasureTick` is a ~100-iteration binary search whose
predicate itself binary-searches the tempo list. Fine where the engine uses it (once per
SP-end recalculation); never put it in an optimizer inner loop — use `MeasureTickToTime`.

**Whammy.** `BaseEngine.Generic.cs:219-245` — an `EngineTimer` seeded from
`engineParameters.StarPowerWhammyBuffer`, gaining via
`CalculateStarPowerGain(maxWhammyTick, …, ref whammyTickRemainder)` with a fractional
remainder carried between frames. Modelling this exactly is meaningfully harder than
modelling phrases, and it is what turns a five-state bar into a continuous one.

**Drums activate differently.** 5-fret activates on a button press any time
`CanStarPowerActivate`. Drums activate by hitting a marked note:
`DrumNote.IsStarPowerActivator` (`DrumNoteFlags.StarPowerActivator`), consumed at
`DrumsEngine.cs:243` (`!activationAutoHit && note.IsStarPowerActivator &&
CanStarPowerActivate && IsActivationComplete(note)`). Which notes get the flag depends on
`StarPowerActivationType` (`YARG.Core/InstrumentEnums.cs:117`) — `Freestyle`,
`RightmostLane` (both marked `TODO: Implement`), `RightmostNote` (Clone Hero style),
`AllNotes` (old YARG style). So on drums the *set of legal activation points is finite
and chart-given*, which actually makes the DP easier, but it is a different problem
shape from 5-fret and shares no code. In practice a chart has **5-15 drum fills**, so an
exact optimizer over drums is close to trivial — which is a real argument for doing drums
first, against the intuition that 5-fret is the easier target.

**Offline scoring is genuinely available.** `YARG.Core/YARG.Core.csproj` targets
`netstandard2.1` with no Unity references. `YARG.Core/YARG.Core/Replays/Analyzer/ReplayAnalyzer.cs`
(`CreateEngine` / `RunFrames`), the `YARG.Core/ReplayCli/` project, and
`YARG.Core.UnitTests/Engine/{GuitarEngineTester,DrumEngineTester,KeysEngineTester}.cs`
all construct and drive engines headlessly today. `YARG.Core.asmdef` declares
`"noEngineReferences": true`, so the no-Unity property is enforced, not incidental.

This gives a **free correctness oracle**. `UpdateBot(double)` auto-hits every note, and
`IsStarPowerInputActive` is `{ get; protected set; }` — so a small `BaseEngine` subclass
(exactly what `GuitarEngineTester` already does) can inject activations at chosen ticks
instead of the bot's default policy, and the run's final score can be asserted equal to
the optimizer's projection. Note the stock bot's own SP policy is naive-greedy
(`IsStarPowerInputActive = CanStarPowerActivate && !IsStarPowerInputActive` — it fires the
instant the bar hits 50%) and it never whammies, so it is an oracle for *scoring*, not for
*optimality*. Beating that greedy policy is the whole point of the feature.

**Rendering has a ready-made template.** From the visuals research:

- `Assets/Script/Gameplay/Visuals/TrackElements/TrackElement.cs`
  `GetZPositionAtTime(double time)` is the tick/time → highway-z conversion. The formula
  is duplicated in three places (`GetZPositionAtTime`, an inlined copy in
  `UpdateElementPosition`, and a private `ZFromTime` at `TrackEffectElement.cs:634`);
  there is no shared helper.
- The beatline is the model to copy: prefab
  `Assets/Prefabs/Gameplay/Visual/TrackElements/Beatline.prefab` (root → `Parent` → `Mesh`,
  `Quad.fbx` rotated X+90°, localPosition `(0, 0.002, 0)` to dodge z-fighting, localScale
  `(2, 0.05, 1)` where X = 2 = `TrackPlayer.TRACK_WIDTH` — that is what makes it span the
  highway), material `Assets/Art/Materials/Gameplay/Track/Beatline.mat` →
  `Assets/Art/Shaders/Gameplay/Beatline.shadergraph`, pooled from a `Beatline Pool`
  GameObject on `Assets/Prefabs/Gameplay/Visual/BaseVisual.prefab`.
- **Highway curvature and fade are free.**
  `Assets/Script/Gameplay/Visuals/HighwayCameraRendering.cs` injects
  `_YargCurveFactors` / `_YargFadeParams` globally through patched URP includes plus a
  screen-space mask pass. The Beatline shader graph has no highway-specific nodes; any
  ordinary URP material bends and fades correctly as long as it stays off the
  `FadeExclude` layer. This removes what looked like the main rendering risk.
- Per-frame hook is `BaseElement.LateUpdate()` → `UpdateVisualElement()` →
  `UpdateElementPosition()` then `UpdateElement()`.
- For a *region* rather than a point, the two existing length-aware patterns are
  `TrackEffectElement.RescaleForZ()` (scales a plane by
  `(TimeEnd - Time) * NoteSpeed / 10f`) and `LaneElement.SetTimeRange(start, end)` —
  both cheaper than adding a new `TrackEffectType`.

**This fork's `SectionStrip` is the wrong rendering precedent but the right data-flow
precedent.** `Assets/Script/Gameplay/HUD/SectionStrip.cs` builds runtime uGUI `Image`
blocks under a `HorizontalLayoutGroup`, positioned by `TrackView.UpdateSectionStripWidth()`.
An SP marker should follow `BeatlineElement` instead. But the *plumbing* —
`GameManager.InitializeSectionStripStates()` → `BasePlayer.SetSectionState` →
`OnSectionStateSet()` → `TrackView`, with `SectionStripState` as a plain-C# state model —
is exactly the shape this feature needs, and it is already working code to copy.

**Where precomputation hooks in.** `Assets/Script/Persistent/LoadingScreen.cs` defines
`LoadingContext : IDisposable` with `Queue(UniTask, title, sub)`; queued
`UniTask.RunOnThreadPool` work starts eagerly and genuinely overlaps audio loading. Two
placements:

- If the optimizer needs engine parameters (it almost certainly does), call it right
  after `CreatePlayers()`, adjacent to `InitializeSectionStripStates()` at
  `Assets/Script/Gameplay/GameManager.Loading.cs:234-237` — main thread, loading screen
  still up. This is the fork's own precedent for an identically-shaped problem.
- If chart-only, append it to `LoadChart()`, which already runs on the thread pool inside
  a try/catch, and get the parallel slot for free.

**Compute cost.** With phrases-only gain, activation candidates are bounded by the note
count and the bar by five states, so the DP is O(notes × 5) with a cheap prefix-sum
score lookup — sub-millisecond for any realistic chart, and invisible behind the loading
screen. Whammy modelling is what would make it expensive, because the bar becomes
continuous and has to be discretised.

### Approaches

| | (A) Exact DP in `YARG.Core` | (B) Greedy heuristic in `Assets/Script` | (C) Import CHOpt output |
|---|---|---|---|
| Where | new file under `YARG.Core/YARG.Core/Engine/` — **requires modifying the submodule, which this fork has never done** | Unity-side; must re-declare `POINTS_PER_NOTE` etc. because they are `protected const` | a parser plus a file-picker; no optimizer at all |
| Correctness | exact for the modelled subset; verifiable against a bot run | "activate when full and the next phrase is far" — right most of the time, wrong exactly where it matters | exact for *Clone Hero's* model, not YARG's — meter-aware drain diverges |
| Model drift | low: sits next to `BaseEngine` and breaks loudly on a merge | **high**: silently diverges as upstream changes scoring | n/a, but permanently wrong on meter changes |
| Merge cost | the `YARG.Core` gitlink currently moves forward cleanly with no local commits; a fork-side change ends that | none | none |
| User effort | none | none | must run CHOpt per song, externally |
| Effort | **L** | **M** | **M** |

### Recommended approach

**(A), phrases-only, guitar/bass 5-fret, single player, drawn as a pooled
`BeatlineElement`-style band** — with the submodule question settled first.

MVP scope, deliberately narrow:

1. **Model:** SP gained only from completed phrases (no whammy). Full combo assumed. No
   squeezes. Activation exactly on a note.
2. **Instrument:** 5-fret guitar/bass only. Drums second (different activation model,
   finite candidate set — arguably easier once the framework exists). Vocals never.
3. **Output:** an ordered list of (activation tick, projected end tick) plus the
   projected total score.
4. **Verification:** bot run driven with those activations must reproduce the projected
   score exactly. Build this before the UI.
5. **UI:** one highway-spanning band at each activation tick, extending to where SP would
   run out. Ghost/dim when the player's actual SP state has diverged from the plan.

**On the submodule.** `docs/section-fc-design.md` explicitly records that this fork does
not modify `YARG.Core`, and `docs/section-fc-handoff.md` notes the gitlink advances
cleanly precisely because of that. Approach (A) ends that property. The alternatives are
to accept it (and start carrying submodule merges), or to put the optimizer in
`Assets/Script` and duplicate the scoring constants — buying merge simplicity at the
price of silent drift. **This is the first decision to make, and it is a policy decision,
not a technical one.**

### Effort

**L.** Roughly: optimizer + score model 40%, headless verification harness 15%, rendering
20%, plumbing (settings, practice-mode invalidation, per-player state) 25%. Add an
instrument beyond 5-fret and it is closer to **XL**.

Settings alone touch three files, which is easy to under-scope:
`SettingsManager.Settings.cs` (a `ToggleSetting` in `SettingsContainer`),
`SettingsManager.cs` (a `nameof(...)` entry in a `MetadataTab` — the HUD block is around
line 212), and `Assets/StreamingAssets/lang/en-US.json`
(`Settings.Settings.<Property>.Name` and `.Description`, plus any `Gameplay.*` runtime
strings read via `Localize.Key(...)`).

### Risks

1. **Model drift.** The optimizer duplicates `BaseEngine`'s scoring rules. `BaseEngine.Generic.cs`
   is actively changed upstream (the recent `dev` merge touched adjacent scoring code). A
   wrong path is worse than no path, because it is confidently wrong. The bot-run oracle
   is the mitigation and should be a permanent test, not a one-off check.
2. **The whammy gap.** On sustain-heavy charts a phrases-only path can be materially
   suboptimal, and an experienced player will notice. Either model whammy (expensive) or
   label the feature honestly ("assumes no whammy").
3. **Practice mode is a correctness trap.** `TrackPlayer<>.SetPracticeSection(uint, uint)`
   rebuilds `NoteTrack` and **calls `CreateEngine()` again**; `ResetPracticeSection()` and
   `SetReplayTime(double)` reset every spawn cursor. Any SP-plan cursor must be reset
   everywhere `BeatlineIndex` is (three sites in `TrackPlayer.cs`), *and the plan itself
   must be recomputed* because the note track differs.
4. **The submodule policy change** described above.
5. **CHOpt is not a reliable cross-check** because of the measure-based SP bar. Do not
   treat a disagreement as a bug in the optimizer.
6. **"Optimal" is a claim.** It assumes full combo. A player who drops a note is off-path
   immediately, and a marker that keeps insisting on a stale plan is actively misleading.
7. **Band mode breaks the model.** `Engine/EngineManager.Band.cs:17` defines
   `BandMultiplier => Math.Max(_starpowerCount * 2, 1)`, and `AwardUnisonBonus()` grants
   cross-player SP. In multiplayer, optimal paths are **coupled across players**, so a
   single-player path shown during a band run is simply wrong. The UI must suppress or
   caveat it.
8. **Config sensitivity.** `NoStarPowerOverlap`, `MaxMultiplier` (6 on bass),
   `StarPowerActivationType` (drums), `SongSpeed`, and `profile.ApplyModifiers` all change
   the answer. Read every one from the real profile and parameters.
9. **Rounding fidelity.** Integer `scoreMultiplier / 2`, `Math.Ceiling` on sustain ticks,
   `Math.Floor` on star thresholds, and the whammy `tickRemainder` carry mean a float
   reimplementation drifts from the engine in small amounts that compound over a song.
10. **Fairness.** An always-on overlay is arguably an assist. Whether it should invalidate
   score records is a design decision with architectural consequences (see below).

### Open questions for the user

1. **Modify the `YARG.Core` submodule, or duplicate the scoring model Unity-side?**
   Settle this first; it changes the architecture and the fork's maintenance story.
2. **Does the path recompute when the player deviates** (drops a note, activates early),
   or is it a static "perfect play" reference drawn once? Live recomputation is a much
   bigger feature and pushes the optimizer onto a background thread mid-song.
3. **Does the overlay count as an assist**, invalidating high scores and Section FC
   credit the way bots and low speed already do (`YargPlayer.IsScoreValid`)?
4. **Marker style:** a band at the activation point, a shaded region spanning the whole
   SP duration, or both (start marker + end marker)?
5. **Visibility:** always drawn, or only within N seconds of the activation point?
6. **Show the expected score gain** ("+12,400") next to each marker, or keep it purely
   spatial?
7. **Interaction with the existing section strip** — both want space near the top of the
   highway. Coexist, or make them mutually exclusive?
8. **Setting placement:** Graphics → HUD, next to `ShowSectionStrip`? And should it be
   per-instrument?
9. **MVP instrument confirmation.** 5-fret first, drums second, vocals never? Note the
   evidence points the other way: drums have only 5-15 chart-fixed activation candidates,
   so an exact drums optimizer is much easier than a 5-fret one. Worth reconsidering.
10. **Band runs:** hide the path entirely, or show a single-player approximation with a
    caveat? (See the band-mode risk above — a single-player path is genuinely wrong there.)
11. **Whammy disclosure:** should the UI state that the path assumes no whammy? Presenting
    a phrases-only path as "optimal" without qualification is the main trust risk.

---

## Feature 4 — In-game updater from the fork's GitHub Releases

### Goal

The fork ships as a bare `.zip` on GitHub Releases and is invisible to the YARC Launcher
(`docs/release-build.md` §3). Today updating means: notice a release exists, download,
unzip over the old folder by hand. The feature is to have the running build notice a
newer `-sectionfc` release, offer it, and apply it.

### What exists today

**The build already knows its own release tag.** `Assets/Editor/Build/CIBuild.cs:156-165`
reads `-version` / `-buildVersion` and assigns `PlayerSettings.bundleVersion = version`,
and `.github/workflows/build-windows.yml` passes the release tag. So in a CI build
`Application.version` is exactly `v0.15.0-sectionfc.N` — a string that can be compared
directly against a release tag with no parsing beyond the trailing integer.

**`GlobalVariables.CurrentVersion` is the wrong string for this.**
`Assets/Script/Persistent/GlobalVariables.cs:48` defaults it to `"v0.15"` and `LoadVersion()`
(line 195) replaces it with the contents of `Assets/Resources/version.txt`, which
`CIBuild.WriteVersionFile` writes from `LoadVersionFromGit()` — a git description like
`HEAD b4213 (51d52d8)`, per `docs/release-build.md`. That is what `DevWatermark.cs` and
`MainMenu.cs` display. It is useful to *show*, useless to *compare*. **Compare
`Application.version`; display `CurrentVersion` alongside it.**

**The GitHub API shape.** Our releases are published as **pre-releases** (the tag trigger
`v*-sectionfc*` always sets prerelease, per `docs/release-build.md` §2), and
`/releases/latest` excludes pre-releases by definition. So the updater must list
`https://api.github.com/repos/djrobson5/YARG-fork/releases`, filter to tags matching the
`-sectionfc` pattern, and take the first (the endpoint returns newest-first by creation
date). Each release object carries `tag_name`, `prerelease`, `body` (markdown release
notes), `html_url`, and an `assets` array with `name`, `browser_download_url` and
**`size`** — the last is the only integrity signal available, since the workflow publishes
no checksum. Unauthenticated requests are rate-limited to **60/hour per IP** and GitHub
requires a `User-Agent` header.

**The fetch idiom is already in the repo, twice, against this exact API.**
`Assets/Script/Song/SongSources.cs:139-142` and `Assets/Script/Song/Genrelizer.cs:177-180`
both hit `api.github.com/repos/...` for a version probe and then download a `.zip`.
`SongSources.DownloadSources()` is the template worth copying almost verbatim:

- `using var request = UnityWebRequest.Get(url); request.SetRequestHeader("User-Agent", "YARG"); request.timeout = 2; await request.SendWebRequest();`
- check `request.result == UnityWebRequest.Result.Success`, parse with `JArray.Parse(request.downloadHandler.text)` (Newtonsoft is already a dependency)
- download the zip, `await File.WriteAllBytesAsync(zipPath, request.downloadHandler.data)`
- `ZipFile.ExtractToDirectory(zipPath, folder)` — `System.IO.Compression` is already used
  here and in `Assets/Script/Settings/Customization/CustomContent.cs`
- everything wrapped in try/catch logging through `YargLogger.LogException`, failing silently

Two deviations are required. The 2-second timeout is right for a version probe and wrong
for a ~130 MB asset (`docs/release-build.md` records 129,623,695 bytes for
`v0.15.0-sectionfc.1`). And `downloadHandler.data` buffers the whole thing in memory —
use `DownloadHandlerFile` and poll `request.downloadProgress` for a progress readout.
`GlobalVariables.OfflineMode` (`GlobalVariables.cs:39`, set from a CLI arg) must suppress
the check entirely, exactly as `SongSources.LoadSources()` does.

**Settings buttons exist and are cheap.** There is no `ButtonSetting` type under
`Assets/Script/Settings/Types/`; the mechanism is instead a plain `public void` method on
`SettingsContainer` referenced by `ButtonRowMetadata`. The nearest model is
`Assets/Script/Settings/SettingsManager.Settings.cs:664` `RemoveRemoteContent()` — an
`async void` method that shows a dialog and does work — wired at
`Assets/Script/Settings/SettingsManager.cs:237` as
`new ButtonRowMetadata(nameof(Settings.RemoveRemoteContent))` inside the
`FileManagement` tab. A `CheckForUpdates()` button is the same three-line change plus
localization keys. `Settings.OpenExecutablePath()` (line 659) already proves
`PathHelper.ExecutablePath` is the install directory (`PathHelper.cs:128`,
`Directory.GetParent(Application.dataPath)`), so the apply step does not need to
rediscover it.

**Dialogs and toasts.** `Assets/Script/Menu/Persistent/DialogManager.cs` offers
`ShowMessage(title, message)` (line 58), `ShowConfirmDeleteDialog(...)` (line 196) — the
type-to-confirm variant, too heavy here — and `WaitUntilCurrentClosed()` (line 292).
There is **no progress dialog type** in `Assets/Script/Menu/Common/Dialogs/`; a download
readout means either a custom dialog prefab, reuse of `LoadingScreen`'s `LoadingContext`,
or simply a toast plus a message dialog when done.
`Assets/Script/Menu/Persistent/Toasts/ToastManager.cs` gives
`ToastInformation(text, onClick)` (line 109) with a **click callback**, which is exactly
the non-blocking "a new build is available — click to update" affordance.
`Assets/Script/Menu/Main/MainMenu.cs:134` (`Application.OpenURL("https://github.com/YARC-Official/YARG")`)
is the existing zero-risk fallback: point the user at the release page and let them do it.

**The apply step is the whole engineering problem.** Windows will not let a running
process's `.exe` be overwritten, so the update cannot be applied in-process. Sketch:

1. Download the asset to `Path.Combine(PathHelper.PersistentDataPath, "updates")` — a
   persistent, writable location that is *not* the install dir, so a failed update cannot
   corrupt the working install.
2. Verify the downloaded length equals the asset's `size` field before touching anything.
3. `ZipFile.ExtractToDirectory` into `updates/staging/<tag>`. Sanity-check that
   `YARG.exe` and `YARG_Data/` are at the staging root (they are at the archive root per
   `docs/release-build.md` §3).
4. Write a helper `.cmd` (or a `powershell -ExecutionPolicy Bypass -File` script) that:
   waits for the current PID to exit; renames the install dir's contents into a sibling
   `backup/<old-tag>` folder; copies staging over `PathHelper.ExecutablePath`; relaunches
   `YARG.exe`; deletes itself.
5. `Application.Quit()`.

Everything from step 4 on is Windows-only and must sit behind `#if UNITY_STANDALONE_WIN`.
The fork also builds macOS (`.github/workflows/build-release-mac.yml`), and the idiomatic
gating pattern to mirror is `Assets/Script/Helpers/FileExplorerHelper.cs:171`
(`#if UNITY_STANDALONE_WIN / #elif UNITY_STANDALONE_OSX / #else` with graceful
degradation). On any non-Windows build the button should degrade to "open the release
page".

**Write-permission detection matters more than it looks.** If the user unzipped into
`C:\Program Files\...`, the copy step fails midway with the old install already renamed —
the worst possible outcome. Probe writability first (create and delete a temp file in
`PathHelper.ExecutablePath`) and, if it fails, tell the user to move the install
somewhere under their profile. **Do not elevate.** A self-updater that requests admin is
both a support liability and an antivirus red flag, and it is not needed for the intended
install layout.

### Approaches

| | (a) Manual "Check for updates" button | (b) Automatic check on launch + toast | (c) Both, behind a toggle | (d) External PowerShell script |
|---|---|---|---|---|
| Trigger | user presses a button in Settings | one API call during startup, alongside `SongSources.LoadSources()` | (a) always, (b) gated on `ToggleSetting` | user runs `update-yarg.ps1` by hand |
| UI | button → message dialog with the new tag + notes → confirm → progress → quit/relaunch | `ToastManager.ToastInformation(..., onClick)` opening the same flow | both | none, terminal only |
| Rate limit | one call per press — never an issue | one call per launch; 60/hr is ample | same | n/a |
| Failure mode | visible; the user asked, so an error message is expected | must fail **silently** (`SongSources` precedent) or it becomes noise offline | same | user sees the error directly |
| Surprise factor | none | low, if the toast is dismissible and never auto-applies | none | none |
| New code | fetch + compare + apply + one button + localization | (a) plus a startup hook | (a)+(b) plus a setting | zero C# |
| Effort | **S–M** | **+S** on top of (a) | **+S** | **XS** |

(d) is a genuine baseline, not a joke: a ~30-line PowerShell script that queries the API,
downloads, unzips over the install and relaunches solves the user's actual problem today
with zero risk to the shipped build, and it is also a working prototype of the helper
script that (a) needs in step 4. It should be written first regardless of which
in-game path is chosen.

### Recommended approach

**(a), with (d) written first as a spike, and (b) deferred behind a toggle if wanted later.**

Slices, in the fork's usual rhythm:

1. **`update-yarg.ps1`** in `docs/` or a `tools/` folder. Proves the API filter, the
   asset naming and the copy-over-and-relaunch dance outside Unity, where iteration is
   seconds rather than a 40-minute CI build. Ship it in the release notes as the interim
   answer.
2. **Check-only.** `UpdateChecker` static class next to `SongSources.cs`: list releases,
   filter `-sectionfc`, compare `tag_name` to `Application.version`, return a small
   record. A Settings button that shows a `DialogManager.ShowMessage` with current tag,
   latest tag and a link. No downloading. Independently useful and completely safe.
3. **Download + verify + stage.** Progress via toast or `LoadingContext`; size check;
   staging-layout sanity check. Still no writes to the install dir.
4. **Apply.** Helper script, backup folder, `Application.Quit()`, relaunch. Gated on
   `UNITY_STANDALONE_WIN` and on the writability probe.
5. **Optional automatic check** behind a `ToggleSetting`, plus a "latest build" line near
   the version watermark.

Per the fork's mockup-then-interview workflow, the Settings entry and the update dialog
should be mocked up as an artifact from real values before any of slice 2's UI is built.

### Effort

**S** for slices 1–2 (the script plus a check-only button — a few hours).
**M** for the full end state through slice 4; the apply step and its failure modes are
most of the cost, and every change to it needs a real packaged build to test, which is a
25–100 minute CI round trip (`docs/release-build.md` §2).

### Risks

1. **Antivirus.** A game that downloads a zip, writes a `.cmd`/`.ps1` and relaunches
   itself is textbook dropper behaviour. Defender SmartScreen already flags unsigned
   YARG builds; this makes it worse. Prefer a plain `.cmd` over PowerShell (no
   ExecutionPolicy fight, less heuristic weight), and keep the script human-readable.
2. **Partial or corrupt downloads.** The release publishes no checksum, so `size` is the
   only check available. A truncated-but-right-size download is unlikely but a corrupt
   extraction is not — validate the staging tree before overwriting anything, and keep
   the backup folder until the next successful launch.
3. **A failed apply leaves no working install.** The backup folder is the mitigation and
   is not optional. The helper script should restore it on any copy failure.
4. **TLS on Mono.** The standalone backend is **Mono, not IL2CPP**
   (`docs/release-build.md` §2), and Mono has a history of certificate-validation
   problems against `api.github.com`. `SongSources` and `Genrelizer` already do exactly
   this in shipped builds, so it evidently works — but it is the first thing to check if
   the request fails only in the packaged build.
5. **Release notes are markdown.** `body` comes back with `##`, `*` and link syntax.
   `MessageDialog` is TMP; either strip markdown, render a truncated plain-text summary,
   or show only the tag and link to `html_url`.
6. **Rate limiting.** 60/hr unauthenticated per IP. Fine for (a); fine for (b) too, but
   never retry in a loop, and cache the result for the session.
7. **No downgrade path.** The score DB is forward-only: sqlite-net's `MigrateTable` adds
   columns but never removes them (see Feature 1), so an older build opening a newer
   `scores.db` sees unknown columns. In practice sqlite-net ignores them, but this is not
   a supported direction — the backup folder is a rollback for the *binaries*, not for
   the data. Do not offer in-app downgrade.
8. **Version comparison is string-shaped.** `v0.15.0-sectionfc.10` sorts before
   `v0.15.0-sectionfc.9` lexically. Parse the trailing integer, or trust GitHub's
   newest-first ordering and merely check "different from mine".
9. **The `nightly` data folder is shared with official YARG nightlies** (Feature 1). An
   updater that only replaces the fork's binaries does not touch that, but it is worth
   remembering that "update" here never means "migrate data".

### Open questions for the user

1. **Automatic check on launch, or manual button only?** (Recommended: manual first; add
   automatic behind a toggle once the flow is trusted.)
2. **Keep a backup of the previous version**, and if so for how long — until the next
   update, or deleted after one successful launch?
3. **Filter to `-sectionfc` pre-releases only**, or show any release on the fork?
4. **Where in Settings?** `FileManagement` (next to `OpenExecutablePath`) is the closest
   fit; `General` is more discoverable; a `Debug`-tab placement is the most conservative.
5. **A "latest build" indicator near the version watermark** (`DevWatermark.cs`), or keep
   the whole feature inside Settings?
6. **Is the PowerShell script alone enough?** It solves the problem today with zero risk
   to the shipped build, and the in-game version is convenience on top.
7. **How much of the release body to show** — full notes, first paragraph, or just the
   tag plus a link?

---

## Suggested order

**1. Feature 1 — import scores. Do this first.**

It is the smallest (S, possibly zero code), it is the only one whose value is immediate
and permanent, and it is the only one blocked on the *user* rather than on
implementation — they have to fetch files from another machine, so starting the request
early costs nothing. It also has no dependency on anything else, and getting the fork's
library populated with real history makes every other feature easier to evaluate in the
editor. There is a mild ordering argument too: importing scores *before* implementing
song deletion means the library is fully populated when the delete path is tested, so
the "does the cache resurrect it" question gets exercised against a realistic library.

**2. Feature 4 — in-game updater. Do this second.**

It is small (S for the useful half), entirely standalone, and it is the only feature that
makes *every later release* easier to get into the user's hands — right now each new
build is a manual download-and-unzip, and features 2 and 3 will produce several of them.
Doing it early compounds; doing it last wastes the compounding. The first slice is a
standalone PowerShell script with no Unity code at all, so it can be finished and
delivered before any editor work starts, and the check-only in-game slice after it is
safe by construction (it reads an API and shows a dialog — it cannot break anything).
The one genuine cost is that testing the *apply* step requires real packaged builds, so
that slice should be timed alongside a release that was going to happen anyway.

**3. Feature 2 — delete songs. Do this third.**

Effort is S-to-M and almost every piece already exists (`PopupMenu`, `ConfirmDeleteDialog`,
`ActualLocation`, `RequestContainerRefresh`). It slices naturally into five small
increments that each end at a verifiable state, which is exactly the rhythm Section FC
established. It is also the one feature with a real chance of destroying user data, which
argues for doing it while the surrounding work is small and the review attention is
undivided — not alongside an XL feature.

The one caveat: it is the *least* interesting of the four. If the user's motivation
matters more than risk-adjusted ordering, swapping it with the SP path is defensible.

**4. Feature 3 — optimal SP path. Do this last.**

L bordering on XL, and the only one that needs a policy decision before a line of code
(the submodule question). It benefits from being last for two concrete reasons. First,
its data-flow plumbing is a near-copy of the section strip's, and doing the two smaller
features first keeps that code fresh and stable. Second, it is the only feature whose
correctness cannot be eyeballed — it needs a headless verification harness — so it wants
a stretch of undivided attention rather than being interleaved.

If the user wants to start it sooner, the honest split is: the **optimizer plus its
bot-run verification harness** is a self-contained piece of work that produces no UI and
can be built and validated entirely headlessly. That could run in parallel with Feature 2
without either blocking the other. The rendering and settings half should still come
last.

### Dependencies at a glance

| | Blocks | Blocked by |
|---|---|---|
| 1 Import scores | nothing | the user retrieving files from the other machine |
| 4 In-game updater | nothing | nothing (the apply slice wants a real packaged build to test against) |
| 2 Delete songs | nothing | nothing |
| 3 SP path | nothing | a decision on modifying `YARG.Core`; a decision on whether the overlay invalidates scores |

None of the four depend on each other technically. The order above is value-per-effort
plus risk containment, not a dependency chain.
