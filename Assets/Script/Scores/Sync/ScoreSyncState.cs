using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Newtonsoft.Json;

namespace YARG.Scores.Sync
{
    /// <summary>
    /// What this PC already imported, and the last outcome for the status line
    /// (docs/score-sync-design.md, "Skipping unchanged files"). Stored locally in
    /// <c>score-sync-state.json</c> and never synced.
    /// </summary>
    public class ScoreSyncState
    {
        public const string FILE_NAME = "score-sync-state.json";

        /// <summary>
        /// Per source file name, what it looked like when it was last imported successfully.
        /// </summary>
        public Dictionary<string, ImportedSource> Sources = new();

        public DateTime? LastExportUtc;
        public DateTime? LastSyncUtc;

        /// <summary>The last result or error, for the status line.</summary>
        public string LastResult;

        public class ImportedSource
        {
            /// <summary>File size and write time as listed: checked without opening the file.</summary>
            public long Length;
            public long LastWriteUtcTicks;

            public string   DeviceId;
            public DateTime ExportedAt;
        }

        /// <summary>
        /// True if the listed file looks exactly as it did at its last successful import, so it
        /// need not be opened. Listing a file does not download it, even under OneDrive Files
        /// On-Demand or Google Drive streaming, so this check is cheap and works offline.
        /// </summary>
        public bool IsUnchanged(ScoreSyncFolder.SourceFile source)
        {
            return Sources.TryGetValue(source.FileName, out var seen)
                && seen.Length == source.Length
                && seen.LastWriteUtcTicks == source.LastWriteUtc.Ticks;
        }

        /// <summary>
        /// True if this device's export was already imported, although the file itself changed
        /// on disk (a sync client touching it, or the same export arriving under a new name).
        /// </summary>
        public bool AlreadyImported(ScoreSyncFile file)
        {
            foreach (var seen in Sources.Values)
            {
                if (seen.DeviceId == file.DeviceId && seen.ExportedAt == file.ExportedAt)
                {
                    return true;
                }
            }

            return false;
        }

        public void RecordImported(ScoreSyncFolder.SourceFile source, ScoreSyncFile file)
        {
            Sources[source.FileName] = new ImportedSource
            {
                Length = source.Length,
                LastWriteUtcTicks = source.LastWriteUtc.Ticks,
                DeviceId = file.DeviceId,
                ExportedAt = file.ExportedAt,
            };
        }

        /// <summary>
        /// Loads the state. A missing or unreadable file gives an empty state, which only costs
        /// a re-read of every source file.
        /// </summary>
        public static ScoreSyncState Load(string directory)
        {
            string path = Path.Combine(directory, FILE_NAME);
            try
            {
                if (File.Exists(path))
                {
                    var state = JsonConvert.DeserializeObject<ScoreSyncState>(File.ReadAllText(path));
                    if (state is not null)
                    {
                        state.Sources ??= new Dictionary<string, ImportedSource>();
                        return state;
                    }
                }
            }
            catch (Exception)
            {
                // Fall through to a fresh state
            }

            return new ScoreSyncState();
        }

        public void Save(string directory)
        {
            Directory.CreateDirectory(directory);
            ScoreSyncFolder.WriteAllBytesAtomic(Path.Combine(directory, FILE_NAME),
                Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(this, Formatting.Indented)));
        }
    }
}
