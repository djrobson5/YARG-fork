using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Cysharp.Threading.Tasks;
using UnityEngine;
using YARG.Core.Logging;
using YARG.Helpers;
using YARG.Menu.Persistent;
using YARG.Menu.Settings;
using YARG.Scores.Sync;
using YARG.Settings;

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
    /// Runs export and import against the sync folder (docs/score-sync-design.md): Sync Now, the
    /// export after a recorded score, and the import at startup.
    /// </summary>
    /// <remarks>
    /// Called on the main thread. The database and profile work stays there, since it goes
    /// through <see cref="ScoreContainer"/>; everything that touches the sync folder runs on the
    /// thread pool, because opening another PC's file can mean a slow cloud download. One run at
    /// a time: a run waits for the one in flight to finish.
    /// </remarks>
    public static class ScoreSyncRunner
    {
        private static readonly SemaphoreSlim _gate = new(1, 1);
        private static readonly object _deviceLock = new();
        private static ScoreSyncDevice _device;

        /// <summary>An export is waiting for the gate. Main thread only.</summary>
        private static bool _exportQueued;

        private static string DataDirectory => PathHelper.PersistentDataPath;

        public static ScoreSyncDevice Device
        {
            get
            {
                lock (_deviceLock)
                {
                    return _device ??= ScoreSyncDevice.LoadOrCreate(DataDirectory, Environment.MachineName);
                }
            }
        }

        /// <summary>A run is in flight, for the status line.</summary>
        public static bool IsRunning { get; private set; }

        /// <summary>A Sync Now is waiting or running, so another press does nothing.</summary>
        public static bool IsSyncNowPending { get; private set; }

        /// <summary>
        /// Whether the automatic triggers run: Windows only, a provider chosen and a folder set.
        /// </summary>
        public static bool IsEnabled =>
            Application.platform is RuntimePlatform.WindowsPlayer or RuntimePlatform.WindowsEditor
            && !SettingsManager.Settings.SyncProvider.Value.IsOff
            && !string.IsNullOrWhiteSpace(SettingsManager.Settings.SyncFolder.Value);

        /// <summary>
        /// Exports, then imports: what Sync Now does. Never throws.
        /// </summary>
        public static async UniTask<ScoreSyncRunResult> SyncNow(string syncRoot)
        {
            IsSyncNowPending = true;
            try
            {
                await Enter();
                try
                {
                    var result = new ScoreSyncRunResult();
                    if (!await CheckRoot(syncRoot, result))
                    {
                        return Finish(result);
                    }

                    try
                    {
                        result.ExportedFile = await WriteExport(syncRoot);
                    }
                    catch (Exception e)
                    {
                        YargLogger.LogException(e, "Failed to export scores for sync.");
                        result.Error = $"Couldn't write this PC's scores to the sync folder: {e.Message}";
                        return Finish(result);
                    }

                    await ImportInto(syncRoot, result, waitOutsideGameplay: false);
                    return Finish(result);
                }
                catch (Exception e)
                {
                    YargLogger.LogException(e, "Score sync failed.");
                    return Finish(new ScoreSyncRunResult { Error = $"Score sync failed: {e.Message}" });
                }
                finally
                {
                    Exit();
                }
            }
            finally
            {
                IsSyncNowPending = false;
            }
        }

        /// <summary>
        /// Exports this PC's scores in the background, after a song recorded one. Requests made
        /// while an export waits to start are covered by that export; a request made while one
        /// runs exports once more at the end.
        /// </summary>
        public static void RequestExport()
        {
            if (!IsEnabled || _exportQueued)
            {
                return;
            }

            _exportQueued = true;
            ExportInBackground().Forget();
        }

        /// <summary>
        /// Imports every other PC's changed export in the background. Anything added is announced
        /// in a toast; a failure is only a status message.
        /// </summary>
        public static void ImportAtStartup()
        {
            if (!IsEnabled)
            {
                return;
            }

            ImportInBackground().Forget();
        }

        private static async UniTaskVoid ExportInBackground()
        {
            await Enter();
            // Scores recorded from here on are not in this export, so they queue another
            _exportQueued = false;
            try
            {
                string syncRoot = SettingsManager.Settings.SyncFolder.Value;
                var result = new ScoreSyncRunResult();
                if (await CheckRoot(syncRoot, result))
                {
                    try
                    {
                        await WriteExport(syncRoot);
                    }
                    catch (Exception e)
                    {
                        YargLogger.LogException(e, "Failed to export scores for sync.");
                        result.Error = $"Couldn't write this PC's scores to the sync folder: {e.Message}";
                    }
                }

                Finish(result);
            }
            catch (Exception e)
            {
                YargLogger.LogException(e, "Score sync export failed.");
            }
            finally
            {
                Exit();
            }
        }

        private static async UniTaskVoid ImportInBackground()
        {
            await Enter();
            try
            {
                string syncRoot = SettingsManager.Settings.SyncFolder.Value;
                var result = new ScoreSyncRunResult();
                if (await CheckRoot(syncRoot, result))
                {
                    await ImportInto(syncRoot, result, waitOutsideGameplay: true);
                }

                Finish(result);

                foreach (var import in result.Imports.Where(i => i.Succeeded))
                {
                    string toast = ScoreSyncStatus.ImportToast(import.DeviceName, import.GamesAdded,
                        import.SectionRecordsAdded, import.CreatedProfileNames);
                    if (toast is not null)
                    {
                        ToastManager.ToastSuccess(toast);
                    }
                }
            }
            catch (Exception e)
            {
                YargLogger.LogException(e, "Score sync import at startup failed.");
            }
            finally
            {
                Exit();
            }
        }

        private static async UniTask Enter()
        {
            await _gate.WaitAsync();
            await UniTask.SwitchToMainThread();
            IsRunning = true;
            RefreshSettingsStatus();
        }

        private static void Exit()
        {
            IsRunning = false;
            _gate.Release();
            RefreshSettingsStatus();
        }

        private static void RefreshSettingsStatus()
        {
            if (SettingsMenu.Instance != null)
            {
                SettingsMenu.Instance.OnSettingChanged();
            }
        }

        private static async UniTask<bool> CheckRoot(string syncRoot, ScoreSyncRunResult result)
        {
            if (string.IsNullOrWhiteSpace(syncRoot))
            {
                result.Error = "No sync folder is set.";
                return false;
            }

            // A cloud drive that is still starting up can be slow to answer
            if (!await UniTask.RunOnThreadPool(() => Directory.Exists(syncRoot)))
            {
                result.Error = $"The sync folder doesn't exist: {syncRoot}";
                return false;
            }

            return true;
        }

        private static async UniTask<string> WriteExport(string syncRoot)
        {
            // The database and profile list are read here on the main thread; the file is
            // serialized and written on the thread pool
            var data = ScoreContainer.GetSyncExportData();
            string appVersion = GlobalVariables.Instance.CurrentVersion;

            return await UniTask.RunOnThreadPool(() =>
            {
                var device = Device;
                var file = new ScoreSyncFile
                {
                    Format = ScoreSyncFile.CURRENT_FORMAT,
                    DeviceId = device.DeviceId,
                    DeviceName = device.MachineName,
                    ExportedAt = DateTime.UtcNow,
                    AppVersion = appVersion,
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
            });
        }

        private readonly struct PendingImport
        {
            public readonly ScoreSyncFolder.SourceFile Source;
            public readonly ScoreSyncFile File;

            public PendingImport(ScoreSyncFolder.SourceFile source, ScoreSyncFile file)
            {
                Source = source;
                File = file;
            }
        }

        private static async UniTask ImportInto(string syncRoot, ScoreSyncRunResult result, bool waitOutsideGameplay)
        {
            var (state, pending) = await UniTask.RunOnThreadPool(() => ReadSources(syncRoot, result));
            if (state is null)
            {
                return;
            }

            // Merging holds the main thread for a moment, which a song in progress would feel
            if (pending.Count > 0 && waitOutsideGameplay)
            {
                await UniTask.WaitUntil(() => GlobalVariables.Instance.CurrentScene != SceneIndex.Gameplay);
            }

            foreach (var item in pending)
            {
                var import = ScoreContainer.ImportSyncFile(item.File);
                result.Imports.Add(import);
                if (import.Succeeded)
                {
                    state.RecordImported(item.Source, item.File);
                }
                else
                {
                    result.SkippedFiles.Add($"{item.Source.FileName}: {import.Error}");
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

        /// <summary>
        /// Lists and reads the other PCs' files: the part that can wait on a cloud download.
        /// Returns a null state when the folder could not be listed.
        /// </summary>
        private static (ScoreSyncState, List<PendingImport>) ReadSources(string syncRoot, ScoreSyncRunResult result)
        {
            var device = Device;
            var state = ScoreSyncState.Load(DataDirectory);
            var pending = new List<PendingImport>();

            List<ScoreSyncFolder.SourceFile> sources;
            try
            {
                sources = ScoreSyncFolder.ListSources(syncRoot, device.ExportFileName);
            }
            catch (Exception e)
            {
                result.Error = $"Couldn't list the sync folder: {e.Message}";
                return (null, pending);
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

                pending.Add(new PendingImport(source, file));
            }

            return (state, pending);
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
