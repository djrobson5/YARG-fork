using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using YARG.Core;
using YARG.Core.Game;
using YARG.Scores.Sync;

namespace YARG.ScoreSyncTests
{
    /// <summary>
    /// Row builders and an in-memory stand-in for the database layer's apply step.
    /// </summary>
    internal static class Fixtures
    {
        public static readonly byte[] SongA = { 0xA1, 0xA2, 0xA3, 0xA4 };
        public static readonly byte[] SongB = { 0xB1, 0xB2, 0xB3, 0xB4 };

        public static readonly long T0 = new DateTime(2026, 9, 1, 20, 0, 0).Ticks;

        public static SyncProfile Profile(Guid id, string name)
        {
            return new SyncProfile
            {
                Id = id,
                Name = name,
                GameMode = GameMode.FiveFretGuitar,
                CurrentInstrument = Instrument.FiveFretGuitar,
                CurrentDifficulty = Difficulty.Expert,
            };
        }

        public static SyncPlayer Player(Guid id, string name)
        {
            return new SyncPlayer { Id = id, Name = name };
        }

        public static SyncGame Game(byte[] song, long ticks, int bandScore, params SyncPlayerScore[] scores)
        {
            return new SyncGame
            {
                DateTicks = ticks,
                SongChecksum = song,
                GameVersion = "v0.15.0-test",
                SongName = "Song",
                SongArtist = "Artist",
                SongCharter = "Charter",
                ReplayFileName = $"replay-{ticks}.replay",
                ReplayChecksum = new byte[] { 1, 2, 3 },
                BandScore = bandScore,
                BandStars = StarAmount.Star5,
                SongSpeed = 1f,
                PlayerScores = scores.ToList(),
            };
        }

        public static SyncPlayerScore Score(Guid player, int score)
        {
            return new SyncPlayerScore
            {
                PlayerId = player,
                Instrument = Instrument.FiveFretGuitar,
                Difficulty = Difficulty.Expert,
                Score = score,
                Stars = StarAmount.Star5,
                NotesHit = 100,
                NotesMissed = 0,
                IsFc = true,
                Percent = 1f,
            };
        }

        public static SyncSectionCompletion Completion(Guid player, byte[] song, int index, int count, long ticks)
        {
            return new SyncSectionCompletion
            {
                SongChecksum = song,
                PlayerId = player,
                Instrument = Instrument.FiveFretGuitar,
                Difficulty = Difficulty.Expert,
                HarmonyIndex = 0,
                SectionIndex = index,
                SectionCount = count,
                FirstCompletedTicks = ticks,
            };
        }

        public static SyncSectionProgress Progress(Guid player, byte[] song, int sections, int completed, long ticks)
        {
            return new SyncSectionProgress
            {
                PlayerId = player,
                Instrument = Instrument.FiveFretGuitar,
                SongChecksum = song,
                Difficulty = Difficulty.Expert,
                HarmonyIndex = 0,
                SectionCount = sections,
                CompletedCount = completed,
                LastUpdatedTicks = ticks,
            };
        }

        public static ScoreSyncFile File(ScoreSyncData data, string deviceId = "LAPTOP-7b04d1e8")
        {
            return new ScoreSyncFile
            {
                Format = ScoreSyncFile.CURRENT_FORMAT,
                DeviceId = deviceId,
                DeviceName = "LAPTOP",
                ExportedAt = new DateTime(2026, 9, 22, 21, 14, 3, DateTimeKind.Utc),
                AppVersion = "v0.15.0-sectionfc.9",
                Profiles = data.Profiles,
                Players = data.Players,
                Games = data.Games,
                SectionCompletions = data.SectionCompletions,
                SectionProgress = data.SectionProgress,
            };
        }

        /// <summary>
        /// Applies a plan the way the database layer does, so tests can check the merged state
        /// and plan again against it.
        /// </summary>
        public static void Apply(ScoreSyncData local, ScoreSyncPlan plan)
        {
            local.Profiles.AddRange(plan.ProfilesToCreate.Select(p => new SyncProfile
            {
                Id = p.Id,
                Name = p.Name,
                GameMode = p.GameMode,
                CurrentInstrument = p.CurrentInstrument,
                CurrentDifficulty = p.CurrentDifficulty,
            }));
            local.Players.AddRange(plan.PlayersToInsert);
            local.Games.AddRange(plan.GamesToInsert);
            local.SectionCompletions.AddRange(plan.SectionCompletionsToInsert);

            foreach (var update in plan.SectionCompletionDateUpdates)
            {
                Assert.That(local.SectionCompletions, Does.Contain(update.Local));
                update.Local.FirstCompletedTicks = update.FirstCompletedTicks;
            }

            foreach (var write in plan.SectionProgressWrites)
            {
                var existing = local.SectionProgress.Where(p => SameProgressKey(p, write.Row)).ToList();
                Assert.That(existing, Has.Count.EqualTo(write.IsInsert ? 0 : 1));

                local.SectionProgress.RemoveAll(p => SameProgressKey(p, write.Row));
                local.SectionProgress.Add(write.Row);
            }
        }

        public static bool SameProgressKey(SyncSectionProgress a, SyncSectionProgress b)
        {
            return a.PlayerId == b.PlayerId && a.Instrument == b.Instrument && a.Difficulty == b.Difficulty
                && a.HarmonyIndex == b.HarmonyIndex && a.SongChecksum.SequenceEqual(b.SongChecksum);
        }

        /// <summary>
        /// A deep copy through the file format, so a source and a local set never share rows.
        /// </summary>
        public static ScoreSyncData Copy(ScoreSyncData data)
        {
            var bytes = ScoreSyncFileFormat.WriteToBytes(File(data));
            return ScoreSyncFileFormat.ReadFromBytes(bytes).File;
        }

        public static List<Guid> ScorePlayers(ScoreSyncData data)
        {
            return data.Games.SelectMany(g => g.PlayerScores).Select(s => s.PlayerId).ToList();
        }
    }
}
