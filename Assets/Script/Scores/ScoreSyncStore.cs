using System;
using System.Collections.Generic;
using System.Linq;
using YARG.Scores.Sync;

namespace YARG.Scores
{
    /// <summary>
    /// Connects the score sync merge core (<see cref="ScoreSyncMerge"/>) to a score database:
    /// reads every row as sync data, and merges a source file into it in one transaction.
    /// </summary>
    /// <remarks>
    /// Profiles are passed in rather than read from <c>PlayerContainer</c>, so this runs against
    /// any <see cref="ScoreDatabase"/>, including a copy opened outside the game. Creating the
    /// profiles a plan asks for is the caller's job.
    /// </remarks>
    public static class ScoreSyncStore
    {
        /// <summary>
        /// Reads every row of the database, with the given profiles alongside.
        /// </summary>
        public static ScoreSyncData Read(ScoreDatabase db, IEnumerable<SyncProfile> profiles)
        {
            var data = new ScoreSyncData();
            data.Profiles.AddRange(profiles);

            data.Players.AddRange(db.QueryAllPlayers().Select(p => new SyncPlayer
            {
                Id = p.Id,
                Name = p.Name,
            }));

            var games = new Dictionary<int, SyncGame>();
            foreach (var record in db.QueryAllScores().OrderBy(r => r.Id))
            {
                var game = ToSync(record);
                games.Add(record.Id, game);
                data.Games.Add(game);
            }

            foreach (var record in db.QueryAllPlayerScoreRecords())
            {
                // A player score whose game is gone can't be matched or re-linked on another PC
                if (games.TryGetValue(record.GameRecordId, out var game))
                {
                    game.PlayerScores.Add(ToSync(record));
                }
            }

            data.SectionCompletions.AddRange(db.QueryAllSectionCompletions().Select(ToSync));
            data.SectionProgress.AddRange(db.QueryAllSectionProgress().Select(ToSync));

            return data;
        }

        /// <summary>
        /// Plans and applies one source file against the database, all inside one transaction.
        /// If anything throws, nothing is written and the exception propagates.
        /// </summary>
        /// <returns>
        /// The applied plan. Its <see cref="ScoreSyncPlan.ProfilesToCreate"/> still have to be
        /// created by the caller.
        /// </returns>
        public static ScoreSyncPlan Merge(ScoreDatabase db, IEnumerable<SyncProfile> localProfiles,
            ScoreSyncData source)
        {
            ScoreSyncPlan plan = null;
            var profiles = localProfiles.ToList();

            db.RunInTransaction(() =>
            {
                // Read inside the transaction, so the plan matches what it is applied to
                var local = Read(db, profiles);
                plan = ScoreSyncMerge.Plan(local, source);
                Apply(db, plan);
            });

            return plan;
        }

        private static void Apply(ScoreDatabase db, ScoreSyncPlan plan)
        {
            db.InsertPlayerRecords(plan.PlayersToInsert.Select(p => new PlayerInfoRecord
            {
                Id = p.Id,
                Name = p.Name,
            }));

            foreach (var game in plan.GamesToInsert)
            {
                var record = ToRecord(game);
                db.InsertBandRecord(record);

                // The insert assigned the new auto-increment ID
                db.InsertSoloRecords(game.PlayerScores.Select(s => ToRecord(s, record.Id)).ToList());
            }

            db.InsertSectionCompletions(plan.SectionCompletionsToInsert.Select(ToRecord).ToList());

            foreach (var update in plan.SectionCompletionDateUpdates)
            {
                var c = update.Local;
                db.UpdateSectionCompletionDate(c.SongChecksum, c.PlayerId, c.Instrument, c.Difficulty,
                    c.HarmonyIndex, c.SectionIndex, new DateTime(update.FirstCompletedTicks));
            }

            foreach (var write in plan.SectionProgressWrites)
            {
                db.UpsertSectionProgress(ToRecord(write.Row));
            }
        }

        #region Mapping

        // Dates go through raw ticks both ways. sqlite-net stores DateTime as ticks and reads it
        // back as DateTimeKind.Unspecified, so new DateTime(ticks) matches what a local write
        // would have stored.

        private static SyncGame ToSync(GameRecord r)
        {
            return new SyncGame
            {
                DateTicks = r.Date.Ticks,
                SongChecksum = r.SongChecksum,
                GameVersion = r.GameVersion,
                SongName = r.SongName,
                SongArtist = r.SongArtist,
                SongCharter = r.SongCharter,
                ReplayFileName = r.ReplayFileName,
                ReplayChecksum = r.ReplayChecksum,
                BandScore = r.BandScore,
                BandStars = r.BandStars,
                SongSpeed = r.SongSpeed,
                PlayedWithReplay = r.PlayedWithReplay,
                HasBots = r.HasBots,
            };
        }

        private static GameRecord ToRecord(SyncGame g)
        {
            return new GameRecord
            {
                Date = new DateTime(g.DateTicks),
                SongChecksum = g.SongChecksum,
                GameVersion = g.GameVersion,
                SongName = g.SongName,
                SongArtist = g.SongArtist,
                SongCharter = g.SongCharter,
                ReplayFileName = g.ReplayFileName,
                ReplayChecksum = g.ReplayChecksum,
                BandScore = g.BandScore,
                BandStars = g.BandStars,
                SongSpeed = g.SongSpeed,
                PlayedWithReplay = g.PlayedWithReplay,
                HasBots = g.HasBots,
            };
        }

        private static SyncPlayerScore ToSync(PlayerScoreRecord r)
        {
            return new SyncPlayerScore
            {
                PlayerId = r.PlayerId,
                Instrument = r.Instrument,
                Difficulty = r.Difficulty,
                EnginePresetId = r.EnginePresetId,
                Score = r.Score,
                Stars = r.Stars,
                NotesHit = r.NotesHit,
                NotesMissed = r.NotesMissed,
                IsFc = r.IsFc,
                IsReplay = r.IsReplay,
                Percent = r.Percent,
            };
        }

        private static PlayerScoreRecord ToRecord(SyncPlayerScore s, int gameRecordId)
        {
            return new PlayerScoreRecord
            {
                GameRecordId = gameRecordId,
                PlayerId = s.PlayerId,
                Instrument = s.Instrument,
                Difficulty = s.Difficulty,
                EnginePresetId = s.EnginePresetId,
                Score = s.Score,
                Stars = s.Stars,
                NotesHit = s.NotesHit,
                NotesMissed = s.NotesMissed,
                IsFc = s.IsFc,
                IsReplay = s.IsReplay,
                Percent = s.Percent,
            };
        }

        private static SyncSectionCompletion ToSync(SectionCompletionRecord r)
        {
            return new SyncSectionCompletion
            {
                SongChecksum = r.SongChecksum,
                PlayerId = r.PlayerId,
                Instrument = r.Instrument,
                Difficulty = r.Difficulty,
                HarmonyIndex = r.HarmonyIndex,
                SectionIndex = r.SectionIndex,
                SectionCount = r.SectionCount,
                FirstCompletedTicks = r.FirstCompletedDate.Ticks,
            };
        }

        private static SectionCompletionRecord ToRecord(SyncSectionCompletion c)
        {
            return new SectionCompletionRecord
            {
                SongChecksum = c.SongChecksum,
                PlayerId = c.PlayerId,
                Instrument = c.Instrument,
                Difficulty = c.Difficulty,
                HarmonyIndex = c.HarmonyIndex,
                SectionIndex = c.SectionIndex,
                SectionCount = c.SectionCount,
                FirstCompletedDate = new DateTime(c.FirstCompletedTicks),
            };
        }

        private static SyncSectionProgress ToSync(SectionProgressRecord r)
        {
            return new SyncSectionProgress
            {
                PlayerId = r.PlayerId,
                Instrument = r.Instrument,
                SongChecksum = r.SongChecksum,
                Difficulty = r.Difficulty,
                HarmonyIndex = r.HarmonyIndex,
                SectionCount = r.SectionCount,
                CompletedCount = r.CompletedCount,
                LastUpdatedTicks = r.LastUpdated.Ticks,
            };
        }

        private static SectionProgressRecord ToRecord(SyncSectionProgress p)
        {
            return new SectionProgressRecord
            {
                PlayerId = p.PlayerId,
                Instrument = p.Instrument,
                SongChecksum = p.SongChecksum,
                Difficulty = p.Difficulty,
                HarmonyIndex = p.HarmonyIndex,
                SectionCount = p.SectionCount,
                CompletedCount = p.CompletedCount,
                LastUpdated = new DateTime(p.LastUpdatedTicks),
            };
        }

        #endregion
    }
}
