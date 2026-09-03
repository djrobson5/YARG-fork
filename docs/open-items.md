# Open Items

Running list of known issues and possible follow-ups for the Section FC fork. Last updated 2026-09-03. Remove items when done; note the commit.

## Known low-severity issues

- Hidden strip still registered with the HUD editor: with `ShowSectionStrip` off, the strip's empty root GameObject stays in `DraggableHudManager`'s element list, so HUD edit mode can select an invisible outline above the track. Fix needs an unregister call whose timing against the manager's `Start()` must be verified in the editor.
- Pause-menu toggles apply on the next song only. Turning `TrackSectionCompletion` off mid-song leaves the live strip drawing though no credit is recorded at song end.
- `TrackSectionCompletionCallback` calls `MusicLibraryMenu.SetReload(Partial)` unconditionally, which can downgrade a pending Full reload if flipped right after a rescan. Same behavior as upstream's `AllowDuplicateSongs`.

## Optional follow-ups (not requested)

- Vocals HUD surface for section progress; miss and hit hooks already exist on `VocalsPlayer`.
- Sidebar per-section checklist (deferred in `docs/section-fc-design.md`).
- Let section credit ignore bots (changes the slice 1 eligibility rule, which today mirrors the high-score rule).
- Ease-duration or no-animation setting for the strip (`_easeDuration` is a serialized field on `SectionStrip`).

## Maintenance

- Merge upstream `dev` into `feature/section-fc` every week or two; routine in `docs/section-fc-handoff.md` (Nightly tracking).
- Disable the Crowdin and label-conflicts workflows in the fork's Actions tab; they exist on the default branch and fail without upstream's secrets.
- If the `Library` cache in `build-windows.yml` never saves (GitHub's 10 GB cap), remove the cache step to save time.
- Record the outcome of the first release build (run 33794133823, `v0.15.0-sectionfc.1`) in `docs/release-build.md` and adjust the workflow for anything it revealed.
