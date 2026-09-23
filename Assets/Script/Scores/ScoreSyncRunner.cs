using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using YARG.Core.Logging;
using YARG.Helpers;
using YARG.Scores.Sync;

namespace YARG.Scores
{
    /// <summary>
    /// What one pass over the sync folder did, for the Sync Now dialog and the status line.
    /// </summary>
    public class ScoreSyncRunResult
    {
        /// <summary>Set when the pass could not run at all (no folder, export failed).</summary>
        public string Error;

        public string ExportedFile;

        public List<ScoreSyncImportResult> Imports = new();

        /// <summary>Files not imported this time, with the reason. Unchanged files are not listed.</summary>
        public List<string> SkippedFiles = new();

        public int UnchangedFiles;

        /// <summary>The PCs whose files had nothing new, for the Sync Now dialog.</summary>
        public List<string> UnchangedDevices = new();

        public string Summary()
        {
            if (Error is not null)
            {
                return Error;
            }

            int games = Imports.Sum(i => i.GamesAdded);
            int sections = Imports.Sum(i => i.SectionRecordsAdded);
            var created = Imports.SelectMany(i => i.CreatedProfileNames).ToList();

            string summary = games == 0 && sections == 0 && created.Count == 0
                ? "Up to date"
                : $"Added {games} scores and {sections} section records";
            if (created.Count > 0)
            {
                summary += $"; new profiles: {string.Join(", ", created)}";
            }

            if (SkippedFiles.Count > 0)
            {
                summary += $"; {SkippedFiles.Count} file(s) skipped";
            }

            return summary;
        }

        /// <summary>
        /// The Sync Now dialog: one line per other PC, then created profiles and skipped files.
        /// </summary>
        public string DialogMessage()
        {
            if (Error is not null)
            {
                return Error;
            }

            var lines = new List<string> { "This PC's scores were written to the sync folder.", string.Empty };

            foreach (var import in Imports.Where(i => i.Succeeded))
            {
                lines.Add(import.GamesAdded == 0 && import.SectionRecordsAdded == 0
                    ? $"{import.DeviceName}: nothing new"
                    : $"{import.DeviceName}: {import.GamesAdded} scores, {import.SectionRecordsAdded} section records");
            }

            foreach (string device in UnchangedDevices)
            {
                lines.Add($"{device}: up to date");
            }

            if (lines.Count == 2 && SkippedFiles.Count == 0)
            {
                lines.Add("No other PCs have synced to this folder yet.");
            }

            var created = Imports.SelectMany(i => i.CreatedProfileNames).ToList();
            if (created.Count > 0)
            {
                string label = created.Count == 1 ? "New profile" : "New profiles";
                lines.Add($"{label}: {string.Join(", ", created)} (set up bindings before playing)");
            }

            foreach (string skipped in SkippedFiles)
            {
                lines.Add($"Skipped {skipped}");
            }

            return string.Join("\n", lines);
        }
    }

    /// <summary>
    /// Runs export and import against a sync root (docs/score-sync-design.md, slice 3). The
    /// provider, folder setting and triggers come later (slices 4 and 5).
    /// </summary>
    /// <remarks>
    /// Main-thread calls, since they go through <see cref="ScoreContainer"/>. Slice 5 moves the
    /// file I/O off the main thread.
    /// </remarks>
    public static class ScoreSyncRunner
    {
        private static string DataDirectory => PathHelper.PersistentDataPath;

        public static ScoreSyncDevice Device => ScoreSyncDevice.LoadOrCreate(DataDirectory, Environment.MachineName);

        /// <summary>
        /// Exports, then imports: what Sync Now does.
        /// </summary>
        public static ScoreSyncRunResult SyncNow(string syncRoot)
        {
            var result = new ScoreSyncRunResult();
            if (!CheckRoot(syncRoot, result))
            {
                return Finish(result);
            }

            try
            {
                result.ExportedFile = WriteExport(syncRoot);
            }
            catch (Exception e)
            {
                YargLogger.LogException(e, "Failed to export scores for sync.");
                result.Error = $"Couldn't write this PC's scores to the sync folder: {e.Message}";
                return Finish(result);
            }

            ImportInto(syncRoot, result);
            return Finish(result);
        }

        /// <summary>
        /// Writes this PC's export file. Returns an error message, or null on success.
        /// </summary>
        public static string Export(string syncRoot)
        {
            var result = new ScoreSyncRunResult();
            if (!CheckRoot(syncRoot, result))
            {
                return result.Error;
            }

            try
            {
                WriteExport(syncRoot);
                return null;
            }
            catch (Exception e)
            {
                YargLogger.LogException(e, "Failed to export scores for sync.");
                return $"Couldn't write this PC's scores to the sync folder: {e.Message}";
            }
        }

        /// <summary>
        /// Imports every other PC's export that changed since it was last imported.
        /// </summary>
        public static ScoreSyncRunResult ImportAll(string syncRoot)
        {
            var result = new ScoreSyncRunResult();
            if (CheckRoot(syncRoot, result))
            {
                ImportInto(syncRoot, result);
            }

            return Finish(result);
        }

        private static bool CheckRoot(string syncRoot, ScoreSyncRunResult result)
        {
            if (string.IsNullOrWhiteSpace(syncRoot))
            {
                result.Error = "No sync folder is set.";
                return false;
            }

            if (!Directory.Exists(syncRoot))
            {
                result.Error = $"The sync folder doesn't exist: {syncRoot}";
                return false;
            }

            return true;
        }

        private static string WriteExport(string syncRoot)
        {
            var device = Device;
            var data = ScoreContainer.GetSyncExportData();
            var file = new ScoreSyncFile
            {
                Format = ScoreSyncFile.CURRENT_FORMAT,
                DeviceId = device.DeviceId,
                DeviceName = device.MachineName,
                ExportedAt = DateTime.UtcNow,
                AppVersion = GlobalVariables.Instance.CurrentVersion,
                Profiles = data.Profiles,
                Players = data.Players,
                Games = data.Games,
                SectionCompletions = data.SectionCompletions,
                SectionProgress = data.SectionProgress,
            };

            string path = ScoreSyncFolder.WriteExport(syncRoot, device.ExportFileName, file);

            var state = ScoreSyncState.Load(DataDirectory);
            state.LastExportUtc = file.ExportedAt;
            state.Save(DataDirectory);

            YargLogger.LogInfo($"Exported {file.Games.Count} games for score sync to {path}.");
            return path;
        }

        private static void ImportInto(string syncRoot, ScoreSyncRunResult result)
        {
            var device = Device;
            var state = ScoreSyncState.Load(DataDirectory);

            List<ScoreSyncFolder.SourceFile> sources;
            try
            {
                sources = ScoreSyncFolder.ListSources(syncRoot, device.ExportFileName);
            }
            catch (Exception e)
            {
                result.Error = $"Couldn't list the sync folder: {e.Message}";
                return;
            }

            foreach (var source in sources)
            {
                if (state.IsUnchanged(source))
                {
                    result.UnchangedFiles++;
                    result.UnchangedDevices.Add(ScoreSyncStatus.DeviceNameFromFileName(source.FileName));
                    continue;
                }

                var read = ScoreSyncFolder.ReadSource(source.Path);
                if (read.Status != SourceReadStatus.Ok)
                {
                    // Not recorded in the state, so it is tried again next time
                    YargLogger.LogWarning($"Skipped score sync file {source.FileName}: {read.Error}");
                    result.SkippedFiles.Add($"{source.FileName}: {read.Error}");
                    continue;
                }

                var file = read.File;
                if (file.DeviceId == device.DeviceId)
                {
                    // This PC's own export under another name, e.g. a sync client's conflict copy
                    state.RecordImported(source, file);
                    continue;
                }

                if (state.AlreadyImported(file))
                {
                    state.RecordImported(source, file);
                    result.UnchangedFiles++;
                    result.UnchangedDevices.Add(file.DeviceName);
                    continue;
                }

                var import = ScoreContainer.ImportSyncFile(file);
                result.Imports.Add(import);
                if (import.Succeeded)
                {
                    state.RecordImported(source, file);
                }
                else
                {
                    result.SkippedFiles.Add($"{source.FileName}: {import.Error}");
                }
            }

            state.LastSyncUtc = DateTime.UtcNow;
            state.LastResult = result.Summary();
            state.LastResultUtc = state.LastSyncUtc;
            try
            {
                state.Save(DataDirectory);
            }
            catch (Exception e)
            {
                // Costs a re-read next time, nothing else
                YargLogger.LogException(e, "Failed to save the score sync state.");
            }
        }

        private static ScoreSyncRunResult Finish(ScoreSyncRunResult result)
        {
            if (result.Error is not null)
            {
                try
                {
                    var state = ScoreSyncState.Load(DataDirectory);
                    state.LastResult = result.Error;
                    state.LastResultUtc = DateTime.UtcNow;
                    state.Save(DataDirectory);
                }
                catch (Exception)
                {
                    // The status line just shows the older result
                }
            }

            return result;
        }
    }
}
