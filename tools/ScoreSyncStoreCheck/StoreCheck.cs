using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json.Linq;
using YARG.Core;
using YARG.Scores;
using YARG.Scores.Sync;

// The database-level check for score sync (docs/score-sync-design.md, slice 2). It runs inside
// the headless editor, so it uses the game's real sqlite-net and native SQLite. See the README
// next to this file for the command and the data it expects.
public static class ScoreSyncStoreCheck
{
    /// <param name="root">
    /// A folder holding nightly/ and dev/, each with a COPY of a scores.db and profiles.json.
    /// The check writes only to root/work, which it recreates on every run.
    /// </param>
    public static string Run(string root)
    {
        var real = root;
        var work = Path.Combine(root, "work");
        if (Directory.Exists(work)) Directory.Delete(work, true);
        Directory.CreateDirectory(work);

        var log = new StringBuilder();
        int failures = 0;
        void Check(bool ok, string what) { log.AppendLine($"[{(ok ? "ok" : "FAIL")}] {what}"); if (!ok) failures++; }

        List<SyncProfile> Profiles(string set) => JArray.Parse(File.ReadAllText(Path.Combine(real, set, "profiles.json")))
            .Where(p => !(bool) p["IsBot"])
            .Select(p => new SyncProfile { Id = Guid.Parse((string) p["Id"]), Name = (string) p["Name"],
                GameMode = (GameMode) (int) p["GameMode"], CurrentInstrument = (Instrument) (int) p["CurrentInstrument"],
                CurrentDifficulty = (Difficulty) (int) p["CurrentDifficulty"] }).ToList();

        string Copy(string set, string name) { var p = Path.Combine(work, name); File.Copy(Path.Combine(real, set, "scores.db"), p); return p; }

        ScoreSyncFile Export(ScoreDatabase db, List<SyncProfile> profiles, string device)
        {
            var d = ScoreSyncStore.Read(db, profiles);
            var f = new ScoreSyncFile { Format = ScoreSyncFile.CURRENT_FORMAT, DeviceId = device, DeviceName = device,
                ExportedAt = DateTime.UtcNow, AppVersion = "eval", Profiles = d.Profiles, Players = d.Players, Games = d.Games,
                SectionCompletions = d.SectionCompletions, SectionProgress = d.SectionProgress };
            var r = ScoreSyncFileFormat.ReadFromBytes(ScoreSyncFileFormat.WriteToBytes(f));
            if (r.Status != SyncReadStatus.Ok) throw new Exception(r.Error);
            return r.File;
        }

        string Counts(ScoreSyncData d) => $"{d.Games.Count} games, {d.Games.Sum(g => g.PlayerScores.Count)} scores, {d.Players.Count} players, "
            + $"{d.SectionCompletions.Count} completions, {d.SectionProgress.Count} progress";

        string ProgressRow(SyncSectionProgress p) =>
            $"{p.PlayerId}|{Convert.ToBase64String(p.SongChecksum)}|{p.SectionCount}|{p.CompletedCount}|{p.LastUpdatedTicks}";

        var nightlyProfiles = Profiles("nightly");
        var devProfiles = Profiles("dev");

        var nightlyDb = new ScoreDatabase(Copy("nightly", "nightly.db"));
        var devDb = new ScoreDatabase(Copy("dev", "dev.db"));
        try
        {
            int Raw(ScoreDatabase db, string table) => db._db.ExecuteScalar<int>($"SELECT COUNT(*) FROM {table}");
            int nGames = Raw(nightlyDb, "GameRecords"), nScores = Raw(nightlyDb, "PlayerScores");
            int nDone = Raw(nightlyDb, "SectionCompletions"), nProgress = Raw(nightlyDb, "SectionProgress");
            int dGames = Raw(devDb, "GameRecords"), dScores = Raw(devDb, "PlayerScores");
            int dDone = Raw(devDb, "SectionCompletions"), dProgress = Raw(devDb, "SectionProgress");

            var nightlyFile = Export(nightlyDb, nightlyProfiles, "nightly");
            var devFile = Export(devDb, devProfiles, "dev");
            log.AppendLine("nightly: " + Counts(nightlyFile));
            log.AppendLine("dev:     " + Counts(devFile));
            Check(nightlyFile.Games.Count == nGames && nightlyFile.Games.Sum(g => g.PlayerScores.Count) == nScores, "nightly read matches the raw row counts");
            Check(devFile.Games.Count == dGames && devFile.SectionCompletions.Count == dDone && devFile.SectionProgress.Count == dProgress, "dev read matches the raw row counts");

            var self = ScoreSyncStore.Merge(nightlyDb, nightlyProfiles, nightlyFile);
            Check(!self.HasChanges, "nightly importing its own export writes nothing");

            var plan = ScoreSyncStore.Merge(nightlyDb, nightlyProfiles, devFile);
            log.AppendLine($"nightly <- dev: +{plan.GamesToInsert.Count} games, +{plan.PlayerScoresToInsert} scores, +{plan.SectionCompletionsToInsert.Count} completions, "
                + $"{plan.SectionProgressWrites.Count} progress writes, create [{string.Join(", ", plan.ProfilesToCreate.Select(p => p.Name))}]");
            var nightlyAfter = ScoreSyncStore.Read(nightlyDb, nightlyProfiles);
            log.AppendLine("nightly after: " + Counts(nightlyAfter));
            Check(nightlyAfter.Games.Count == nGames + dGames && nightlyAfter.Games.Sum(g => g.PlayerScores.Count) == nScores + dScores, "nightly now holds the sum of both sets (the fixtures share no games)");
            Check(nightlyAfter.SectionCompletions.Count == nDone + dDone && nightlyAfter.SectionProgress.Count == nProgress + dProgress, "nightly now holds dev's section data");
            var nightlyWithCreated = nightlyProfiles.Concat(plan.ProfilesToCreate).ToList();
            Check(!ScoreSyncStore.Merge(nightlyDb, nightlyWithCreated, devFile).HasChanges, "second import into the real database writes nothing");

            var srcGame = devFile.Games.OrderBy(g => g.DateTicks).First();
            var dbGame = nightlyAfter.Games.Single(g => g.DateTicks == srcGame.DateTicks && g.SongChecksum.SequenceEqual(srcGame.SongChecksum));
            Check(dbGame.BandScore == srcGame.BandScore && dbGame.ReplayFileName == srcGame.ReplayFileName && dbGame.SongName == srcGame.SongName,
                $"inserted game keeps its exact ticks and fields ({srcGame.SongName})");
            var srcDone = devFile.SectionCompletions.First();
            Check(nightlyAfter.SectionCompletions.Any(c => c.FirstCompletedTicks == srcDone.FirstCompletedTicks && c.SectionIndex == srcDone.SectionIndex),
                "inserted section completion keeps its exact date");
            var raw = nightlyDb._db.ExecuteScalar<long>("SELECT Date FROM GameRecords ORDER BY Id DESC LIMIT 1");
            Check(nightlyAfter.Games.Any(g => g.DateTicks == raw), "raw Date column holds ticks");

            plan = ScoreSyncStore.Merge(devDb, devProfiles, nightlyFile);
            log.AppendLine($"dev <- nightly: +{plan.GamesToInsert.Count} games, +{plan.PlayerScoresToInsert} scores, create [{string.Join(", ", plan.ProfilesToCreate.Select(p => p.Name))}], "
                + $"remaps {plan.PlayerIdMap.Count(kv => kv.Key != kv.Value)}");
            var devAfter = ScoreSyncStore.Read(devDb, devProfiles);
            var a = nightlyAfter.Games.Select(ScoreSyncMerge.GameKey).OrderBy(k => k).ToList();
            var b = devAfter.Games.Select(ScoreSyncMerge.GameKey).OrderBy(k => k).ToList();
            Check(a.SequenceEqual(b), $"both databases hold the same {a.Count} games");
            Check(devAfter.Games.Sum(g => g.PlayerScores.Count) == nScores + dScores, "dev now holds every player score");
            Check(nightlyAfter.SectionProgress.Select(ProgressRow).OrderBy(x => x)
                .SequenceEqual(devAfter.SectionProgress.Select(ProgressRow).OrderBy(x => x)), "section progress rows are identical on both");
        }
        finally
        {
            nightlyDb.Dispose();
            devDb.Dispose();
        }

        var failDb = new ScoreDatabase(Copy("nightly", "rollback.db"));
        try
        {
            var devSrc = new ScoreDatabase(Copy("dev", "dev-src.db"));
            ScoreSyncFile devFile;
            try { devFile = Export(devSrc, devProfiles, "dev"); } finally { devSrc.Dispose(); }

            int gamesBefore = failDb._db.ExecuteScalar<int>("SELECT COUNT(*) FROM GameRecords");
            int playersBefore = failDb._db.ExecuteScalar<int>("SELECT COUNT(*) FROM Players");
            // Games and players are written before completions, so this aborts mid-apply
            failDb._db.Execute("CREATE TRIGGER sync_fail BEFORE INSERT ON SectionCompletions BEGIN SELECT RAISE(ABORT, 'forced'); END");
            bool threw = false;
            try { ScoreSyncStore.Merge(failDb, nightlyProfiles, devFile); }
            catch (Exception e) { threw = true; log.AppendLine("  forced failure: " + e.Message); }
            Check(threw, "forced failure propagates");
            Check(failDb._db.ExecuteScalar<int>("SELECT COUNT(*) FROM GameRecords") == gamesBefore
                && failDb._db.ExecuteScalar<int>("SELECT COUNT(*) FROM Players") == playersBefore, "failed merge rolled back every row");
            failDb._db.Execute("DROP TRIGGER sync_fail");
            Check(ScoreSyncStore.Merge(failDb, nightlyProfiles, devFile).GamesToInsert.Count == devFile.Games.Count, "the same import succeeds afterwards");
        }
        finally
        {
            failDb.Dispose();
        }

        log.AppendLine(failures == 0 ? "ALL OK" : $"{failures} FAILURES");
        return log.ToString();
    }
}
