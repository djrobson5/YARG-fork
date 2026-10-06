using System;
using System.IO;
using System.Linq;
using System.Text;
using YARG.Core;
using YARG.Core.Game;
using YARG.Scores;
using YARG.Scores.Sync;

// The live folder check for score sync (docs/score-sync-design.md, slice 3), run inside the
// headless editor on the PC under test. It prints what provider detection finds on this PC, then
// runs a write / list / read / skip / re-export cycle in a throwaway "YARG Score Sync Check"
// folder inside each detected root, with a small synthetic export, and deletes that folder.
// See README.md for the command. Compare the printed detection with what the PC actually has.
public static class ScoreSyncFolderCheck
{
    public static string Run()
    {
        var log = new StringBuilder();
        int failures = 0;
        void Check(bool ok, string what) { log.AppendLine($"[{(ok ? "ok" : "FAIL")}] {what}"); if (!ok) failures++; }

        var oneDrive = ScoreSyncProviderProbe.Detect(ScoreSyncProvider.OneDrive);
        var google = ScoreSyncProviderProbe.Detect(ScoreSyncProvider.GoogleDrive);
        log.AppendLine("OneDrive: " + (oneDrive.Count == 0 ? "(none)" : string.Join(" | ", oneDrive.Select(c => $"{c.Label} -> {c.Path}"))));
        log.AppendLine("Google:   " + (google.Count == 0 ? "(none)" : string.Join(" | ", google.Select(c => $"{c.Label} -> {c.Path}"))));
        Check(oneDrive.Concat(google).All(c => Directory.Exists(c.Path)), "every detected root exists");
        Check(ScoreSyncProviderProbe.Detect(ScoreSyncProvider.CustomFolder).Count == 0, "Custom Folder detects nothing");

        foreach (var candidate in new[] { oneDrive.FirstOrDefault(), google.FirstOrDefault() }.Where(c => c != null))
        {
            // A throwaway root inside the real sync root; everything under it is deleted below
            string root = Path.Combine(candidate.Path, "YARG Score Sync Check");
            log.AppendLine($"== {root}");
            try
            {
                var device = new ScoreSyncDevice { Id = Guid.NewGuid(), MachineName = "CHECK-PC" };
                var id = Guid.NewGuid();
                var file = new ScoreSyncFile
                {
                    Format = ScoreSyncFile.CURRENT_FORMAT, DeviceId = device.DeviceId, DeviceName = device.MachineName,
                    ExportedAt = DateTime.UtcNow, AppVersion = "check",
                };
                file.Profiles.Add(new SyncProfile { Id = id, Name = "Check", GameMode = GameMode.FiveFretGuitar,
                    CurrentInstrument = Instrument.FiveFretGuitar, CurrentDifficulty = Difficulty.Expert });
                file.Games.Add(new SyncGame { DateTicks = DateTime.Now.Ticks, SongChecksum = new byte[] { 1, 2, 3 }, BandScore = 42,
                    PlayerScores = { new SyncPlayerScore { PlayerId = id, Instrument = Instrument.FiveFretGuitar, Difficulty = Difficulty.Expert, Score = 42 } } });

                var path = ScoreSyncFolder.WriteExport(root, device.ExportFileName, file);
                Check(File.Exists(path) && !File.Exists(path + ".tmp"), "export written, no temp left");

                // As another PC sees it
                var sources = ScoreSyncFolder.ListSources(root, "SOMEONE-ELSE-00000000.yargsync");
                Check(sources.Count == 1 && sources[0].FileName == device.ExportFileName, $"listed as {sources.FirstOrDefault()?.FileName}");
                var read = ScoreSyncFolder.ReadSource(sources[0].Path);
                Check(read.Status == SourceReadStatus.Ok && read.File.Games.Single().BandScore == 42, $"read back ({read.Status} {read.Error})");

                var state = new ScoreSyncState();
                state.RecordImported(sources[0], read.File);
                Check(state.IsUnchanged(ScoreSyncFolder.ListSources(root, "x.yargsync")[0]), "unchanged file is skipped by listing alone");

                file.ExportedAt = file.ExportedAt.AddSeconds(1);
                file.Games[0].BandScore = 43;
                ScoreSyncFolder.WriteExport(root, device.ExportFileName, file);
                var again = ScoreSyncFolder.ListSources(root, "x.yargsync")[0];
                Check(!state.IsUnchanged(again) && ScoreSyncFolder.ReadSource(again.Path).File.Games[0].BandScore == 43,
                    "re-export replaces the file and is seen as changed");
                Check(ScoreSyncFolder.ListSources(root, device.ExportFileName).Count == 0, "own file is left out");
            }
            catch (Exception e)
            {
                Check(false, $"threw {e.GetType().Name}: {e.Message}");
            }
            finally
            {
                if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
                Check(!Directory.Exists(root), "check folder removed");
            }
        }

        log.AppendLine(failures == 0 ? "ALL OK" : $"{failures} FAILURES");
        return log.ToString();
    }
}
