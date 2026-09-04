# Delete songs from the in-game library

## What it is

A "Delete Song" item in the music library's More Options popup that removes the selected
song's files from disk and takes it out of the library, without leaving the game.

Background research — the entry→disk mapping, the song-cache constraints, the CON problem,
and the approach comparison — lives in `docs/roadmap.md` → "Feature 2 — Delete songs from
the in-game library". This document records only the decisions that are now locked, and the
plan they imply.

## Decisions (locked 2026-09-03)

| Question | Decision |
|---|---|
| Trash vs permanent | **Recycle Bin on Windows, permanent everywhere else.** The confirm dialog states which one will happen on the running platform, so the wording is never a lie. `FOF_ALLOWUNDO` is best-effort — Windows still deletes outright on drives with no Recycle Bin, when the bin is disabled for that drive, or when the item is over quota — so the Windows wording is hedged ("sent to the Recycle Bin where possible"). |
| Failed recycle | **Never escalated to a permanent delete.** If `SHFileOperation` returns non-zero or sets `fAnyOperationsAborted`, the delete reports failure and the user gets the "Could not delete" toast. The user agreed to a recoverable delete; silently upgrading it to a destructive one is worse than doing nothing. |
| Path guards | Before the confirm dialog, the song's `ActualLocation` is checked against `SettingsManager.Settings.SongFolders`. If it **equals** a configured folder, a message dialog refuses and points at Settings > Song Manager. If it is **not a strict descendant** of any configured folder (a hand-edited `songs.dta` can put `..` in an ExCON sub-name), the delete is refused with a logged warning and the Failed toast. |
| CON packs | The item is **shown greyed out**, not hidden. Clicking it opens a message dialog explaining that the song lives inside a pack file that the game cannot edit, and that deleting it would destroy every song in the pack. Hiding it would leave the user wondering why the option is missing on one song and not another. |
| ExCON subfolders | Deletable, with the dialog warning that the pack's `songs.dta` still lists the song, so the next full scan will log it as a bad song until the `.dta` is edited by hand. |
| Placement | The More Options popup's advanced block (behind `ShowAdvancedMusicLibraryOptions`), as the **last** item — after "View Song Folder" and "Copy Song Checksum". Last because it is the only destructive entry in the menu and should not sit under the cursor's resting position. |
| Label | "Delete Song". |
| Batch or one at a time | **One at a time.** No pending-deletion queue (approach (C) in the roadmap). |
| Confirm | `ConfirmDeleteDialog` with **the song name as the confirm text** — the user types the title before the Delete button does anything, the same bar the profile delete sets. |
| Dialog body | States trash-vs-permanent for this platform; states that **scores and section progress are kept**; shows the path that will be removed on a dimmed `<alpha=#80>` line so the user can see exactly what is about to go. |
| Scores / section FC | **Kept.** They are keyed by checksum, there is no deletion path in `Assets/Script/Scores/` at all, and re-adding the song later restores everything for free. Matches the profile-delete dialog's own "play history will remain" promise. |
| Success feedback | A toast: `Moved "<song>" to the Recycle Bin.` on Windows, `Deleted "<song>".` elsewhere. No dialog — the user just confirmed one. |
| Library refresh | The library updates in place; the on-disk song cache is reconciled by a **full song scan on next launch**. |
| Playlists / favourites | The song is **pruned from every playlist and the favourites list** as part of the delete, rather than left as a dead hash for a later cleanup pass. |
| Preview audio | The library preview **must be stopped and awaited** before any file is touched — the preview stream holds the audio file open and the delete fails on Windows otherwise. Stopping the *running* preview is not enough: an in-flight `PreviewContext.Create` is inside `LoadPreviewAudio` with nothing assigned to `_previewContext` yet, so that task is tracked in a field and awaited too. |
| Queued song | If the deleted song is `GlobalVariables.State.CurrentSong`, that field is cleared. |

## Approach

Approach **(B)** from the roadmap:

1. Send the song's `ActualLocation` to the Recycle Bin (Windows) or delete it permanently.
2. Remove the entry from the in-memory song containers.
3. Persist a **dirty flag** that forces `SongContainer.RunRefresh(quick: false)` on next
   launch, so the quick scan cannot resurrect the song as a ghost entry.

The full rescan is the thing being deferred: it is all-or-nothing and takes a loading screen
per delete on a large library, which is unacceptable for a repeated action. There is no
incremental scan and no way to remove one entry from `songcache.bin` short of a full scan,
so deferring is the only option that is both correct and fast.

## Slices

1. **Popup item + confirm + delete.** `Ini` and `Sng` only. Immediate full rescan
   (`MusicLibraryMenu.RefreshSongs()`), i.e. approach (A) — a strict subset of (B) whose
   correctness is trivially guaranteed. Permanent delete.
2. **`ExCON` support** with the `songs.dta` warning; **`CON` greyed out** with the
   explanatory message dialog.
3. **`FileDeleteHelper`** with the Windows Recycle Bin path: `SHFileOperationW` from
   `shell32` with `FO_DELETE | FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_SILENT |
   FOF_NOERRORUI`, under `#if UNITY_STANDALONE_WIN`, reporting failure (never a permanent
   delete) if the call returns non-zero; a plain delete everywhere else. `SHFILEOPSTRUCT`
   uses **default packing**, not `Pack = 1` — `shellapi.h` only packs the struct under
   `!_WIN64`, and Unity 6 Windows standalone is x64 only; an `IntPtr.Size != 8` guard falls
   back to a permanent delete rather than call the P/Invoke with the wrong layout.
   No `Microsoft.VisualBasic`
   reference — pulling in the VB runtime for `RecycleOption.SendToRecycleBin` is an
   IL2CPP stripping/AOT risk, and the P/Invoke needs no extra assembly. The gating pattern
   mirrors `Assets/Script/Helpers/FileExplorerHelper.cs`.
4. **In-memory removal + dirty flag**, replacing the per-delete full rescan of slices 1-3.
   Needs a `SongContainer.RemoveSong(SongEntry)` (the cache is private today) that matches
   on `ActualLocation`, **not** on hash — `SongCache.Entries` maps one hash to a *list*,
   because duplicate copies of the same chart in different folders share a checksum.
5. **Playlist pruning**: `RemoveSong` on every playlist and on
   `PlaylistContainer.FavoritesPlaylist`.

Slices 1-3 are implemented. 4 and 5 are not.

## Files

| Concern | Touch point |
|---|---|
| Trash / delete a path | `Assets/Script/Helpers/FileDeleteHelper.cs` |
| Popup item, confirm dialog, delete flow | `Assets/Script/Menu/MusicLibrary/PopupMenu.cs` |
| Greyed-out item support | `Assets/Script/Menu/MusicLibrary/PopupMenuItem.cs` |
| Stopping the preview before the delete, and disposing its context | `Assets/Script/Menu/MusicLibrary/MusicLibraryMenu.cs` |
| Strings | `Assets/StreamingAssets/lang/en-US.json` |

Song names and paths are inserted into TMP dialogs and toasts, so `<` is escaped
(`<noparse><</noparse>`) before it goes into any message. The `ConfirmDeleteDialog` compares
the typed text against the *raw* confirm string, so the name passed as the confirm text stays
unescaped while the copy in the message body is escaped.

The fork does not modify `YARG.Core`; the submodule tracks upstream unchanged, so every fix
has to live in the main repo.

That matters for one leak. `PreviewContext.Loop` disposes the mixer only on the cancellation
path, so a throw inside the loop leaves the song's audio files open forever. Rather than patch
the submodule, `StopPreviewAsync` calls `previewContext.Dispose()` after awaiting
`WaitForCompletionAsync()`. `PreviewContext.Dispose` is public and idempotent (guarded by its
`_disposed` flag), so this is a no-op on the normal path and reclaims the mixer on the failed
one.

## Risks

1. **Ghost entries from the quick scan.** The highest-probability defect once slice 4 lands.
   Test by deleting, restarting the game, and checking the library — not by watching the UI.
2. **CON mass-deletion.** Losing a 50-song pack while removing one song is the worst
   realistic outcome; the greyed item is the gate.
3. **macOS and Linux have no trash path** without native glue, so delete is permanent there.
   The dialog says so.
4. **Duplicate-hash entries.** Match on `ActualLocation` when removing from the cache.
5. **The preview holds files open.** A delete that runs while the preview is streaming the
   song fails on Windows with a sharing violation. The in-flight `PreviewContext.Create` is
   the subtle half of this and is awaited alongside the running preview.
6. **Irreversibility.** This is the only planned feature that can destroy user data.
