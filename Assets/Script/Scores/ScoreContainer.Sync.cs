using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using YARG.Core;
using YARG.Core.Game;
using YARG.Core.Logging;
using YARG.Player;
using YARG.Scores.Sync;
using YARG.Song;

namespace YARG.Scores
{
    /// <summary>
    /// What one imported sync file changed, for the toast and the Sync Now dialog.
    /// </summary>
    public class ScoreSyncImportResult
    {
        public string DeviceName;

        /// <summary>Null when the import succeeded.</summary>
        public string Error;

        public int GamesAdded;
        public int PlayerScoresAdded;
        public int GamesAlreadyPresent;
        public int SectionRecordsAdded;
        public int ProfilesMatchedById;
        public int ProfilesMatchedByName;

        /// <summary>Names of the profiles the import created, for the toast.</summary>
        public List<string> CreatedProfileNames = new();

        public bool Succeeded => Error is null;

        public bool AddedAnything => GamesAdded > 0 || SectionRecordsAdded > 0 || CreatedProfileNames.Count > 0;
    }

    public static partial class ScoreContainer
    {
        private const string PRE_SYNC_BACKUP_SUFFIX = ".pre-sync.bak";

        /// <summary>
        /// Every row of the score database plus the profiles, as the payload of this PC's
        /// export file (docs/score-sync-design.md).
        /// </summary>
        /// <remarks>
        /// Call on the main thread: it reads the live database connection and the profile list.
        /// Serializing and writing the result can happen anywhere.
        /// </remarks>
        public static ScoreSyncData GetSyncExportData()
        {
            return ScoreSyncStore.Read(_db, GetSyncProfiles(includeUnloaded: false));
        }

        /// <summary>
        /// Merges one other PC's export into this PC's scores and profiles. Never throws; a
        /// failure leaves the database as it was and is reported in the result.
        /// </summary>
        /// <remarks>
        /// Call on the main thread: it creates profiles and refreshes the score caches.
        /// </remarks>
        public static ScoreSyncImportResult ImportSyncFile(ScoreSyncFile file)
        {
            var result = new ScoreSyncImportResult { DeviceName = file.DeviceName };

            ScoreSyncPlan plan;
            try
            {
                BackUpBeforeFirstSync();
                plan = ScoreSyncStore.Merge(_db, GetSyncProfiles(includeUnloaded: true), file);
            }
            catch (Exception e)
            {
                YargLogger.LogException(e, $"Failed to import scores from {file.DeviceName}.");
                result.Error = e.Message;
                return result;
            }

            result.GamesAdded = plan.GamesToInsert.Count;
            result.PlayerScoresAdded = plan.PlayerScoresToInsert;
            result.GamesAlreadyPresent = plan.GamesSkipped;
            result.SectionRecordsAdded = plan.SectionCompletionsToInsert.Count;
            result.ProfilesMatchedById = plan.Profiles.Count(p => p.Match == ProfileMatch.ById);
            result.ProfilesMatchedByName = plan.Profiles.Count(p => p.Match == ProfileMatch.ByName);

            // The scores are committed at this point. If creating a profile fails, its scores
            // stay under its ID, and the next import tries again
            try
            {
                foreach (var profile in plan.ProfilesToCreate)
                {
                    // Only a bot can already hold the ID, since bots are left out of the sync
                    if (PlayerContainer.GetProfileById(profile.Id) is not null)
                    {
                        continue;
                    }

                    if (PlayerContainer.AddProfile(CreateProfile(profile)))
                    {
                        result.CreatedProfileNames.Add(profile.Name);
                    }
                }

                if (result.CreatedProfileNames.Count > 0)
                {
                    PlayerContainer.SaveProfiles();
                }
            }
            catch (Exception e)
            {
                YargLogger.LogException(e, $"Failed to create profiles from {file.DeviceName}.");
                result.Error = e.Message;
            }

            if (plan.HasChanges)
            {
                FetchBandHighScores();
                InvalidateScoreCache();
                SongContainer.InvalidateStarsCache();
            }

            YargLogger.LogFormatInfo("Imported scores from {0}: {1} games added, {2} already present, "
                + "{3} section records added, {4} profiles created.", file.DeviceName, result.GamesAdded,
                result.GamesAlreadyPresent, result.SectionRecordsAdded, result.CreatedProfileNames.Count);

            return result;
        }

        /// <summary>
        /// The profiles that take part in a sync. Bots never do: their scores are never saved,
        /// and a bot sent to another PC would arrive as a human profile.
        /// </summary>
        /// <param name="includeUnloaded">
        /// Also include the profiles this version could not load, by ID and name only. An import
        /// must see them, so it never creates a second profile with an ID that is already taken.
        /// </param>
        private static List<SyncProfile> GetSyncProfiles(bool includeUnloaded)
        {
            var profiles = PlayerContainer.Profiles
                .Where(p => !p.IsBot)
                .Select(p => new SyncProfile
                {
                    Id = p.Id,
                    Name = p.Name,
                    GameMode = p.GameMode,
                    CurrentInstrument = p.CurrentInstrument,
                    CurrentDifficulty = p.CurrentDifficulty,
                })
                .ToList();

            if (includeUnloaded)
            {
                foreach (var unloaded in PlayerContainer.UnloadedProfiles)
                {
                    if (Guid.TryParse((string) unloaded.Record?["Id"], out var id))
                    {
                        profiles.Add(new SyncProfile { Id = id, Name = unloaded.Name });
                    }
                }
            }

            return profiles;
        }

        private static YargProfile CreateProfile(SyncProfile source)
        {
            // The same defaults as a profile made from the profile list
            var profile = new YargProfile(source.Id)
            {
                Name = source.Name,
                NoteSpeed = 5,
                HighwayLength = 1,
                GameMode = source.GameMode,
                CurrentInstrument = source.CurrentInstrument,
                CurrentDifficulty = source.CurrentDifficulty,
            };

            profile.PreferredInstrument = profile.HasValidInstrument
                ? profile.CurrentInstrument
                : profile.GameMode.PossibleInstruments()[0];

            return profile;
        }

        /// <summary>
        /// Copies <c>scores.db</c> and <c>profiles.json</c> aside before this PC's first import.
        /// An existing backup is never replaced, so it always holds the state from before any
        /// sync.
        /// </summary>
        private static void BackUpBeforeFirstSync()
        {
            string scoresBackup = _scoreDatabaseFile + PRE_SYNC_BACKUP_SUFFIX;
            if (!File.Exists(scoresBackup))
            {
                // No transaction is open here, so the file on disk is complete
                File.Copy(_scoreDatabaseFile, scoresBackup);
                YargLogger.LogFormatInfo("Backed up the score database to {0} before the first sync.", scoresBackup);
            }

            string profilesFile = Path.Combine(PlayerContainer.ProfilesDirectory, "profiles.json");
            string profilesBackup = profilesFile + PRE_SYNC_BACKUP_SUFFIX;
            if (File.Exists(profilesFile) && !File.Exists(profilesBackup))
            {
                File.Copy(profilesFile, profilesBackup);
            }
        }
    }
}
