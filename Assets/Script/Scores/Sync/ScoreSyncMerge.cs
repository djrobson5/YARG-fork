using System;
using System.Collections.Generic;
using System.Linq;
using YARG.Core;

namespace YARG.Scores.Sync
{
    public enum ProfileMatch
    {
        /// <summary>A local profile has the source profile's ID.</summary>
        ById,

        /// <summary>A local profile has the same name; the source ID is remapped to it.</summary>
        ByName,

        /// <summary>No match; the profile is created locally with the source's ID.</summary>
        Created,
    }

    public class ProfileResolution
    {
        public SyncProfile  Source;
        public ProfileMatch Match;
        public Guid         LocalId;
    }

    public class SectionCompletionDateUpdate
    {
        /// <summary>The existing local row, already remapped to local player IDs.</summary>
        public SyncSectionCompletion Local;

        public long FirstCompletedTicks;
    }

    public class SectionProgressWrite
    {
        /// <summary>
        /// The row as it should end up. Its key identifies the local row to update.
        /// </summary>
        public SyncSectionProgress Row;

        /// <summary>True when no local row exists for the key yet.</summary>
        public bool IsInsert;
    }

    /// <summary>
    /// Everything one source file adds to the local database. Applying it is the database
    /// layer's job and happens in one transaction.
    /// </summary>
    /// <remarks>
    /// Every player ID in the plan is already a local ID.
    /// </remarks>
    public class ScoreSyncPlan
    {
        public List<ProfileResolution> Profiles = new();

        /// <summary>Source player ID to local player ID, for every ID the file references.</summary>
        public Dictionary<Guid, Guid> PlayerIdMap = new();

        public List<SyncProfile> ProfilesToCreate => Profiles
            .Where(p => p.Match == ProfileMatch.Created)
            .Select(p => p.Source)
            .ToList();

        public List<SyncPlayer>                  PlayersToInsert             = new();
        public List<SyncGame>                    GamesToInsert               = new();
        public List<SyncSectionCompletion>       SectionCompletionsToInsert  = new();
        public List<SectionCompletionDateUpdate> SectionCompletionDateUpdates = new();
        public List<SectionProgressWrite>        SectionProgressWrites       = new();

        public int GamesSkipped;

        public int PlayerScoresToInsert => GamesToInsert.Sum(g => g.PlayerScores.Count);

        /// <summary>
        /// True when applying the plan would write anything to the score database or profiles.
        /// </summary>
        public bool HasChanges =>
            Profiles.Any(p => p.Match == ProfileMatch.Created)
            || PlayersToInsert.Count > 0
            || GamesToInsert.Count > 0
            || SectionCompletionsToInsert.Count > 0
            || SectionCompletionDateUpdates.Count > 0
            || SectionProgressWrites.Count > 0;
    }

    /// <summary>
    /// The score sync merge rules (docs/score-sync-design.md, "Merge rules"), as a pure function
    /// from the local rows and one source file to the rows to write.
    /// </summary>
    /// <remarks>
    /// The merge is a union: nothing local is ever removed, and the only local values changed
    /// are a section completion's date (the earlier one wins) and a section progress row, which
    /// is recomputed from the merged completions. Planning against the result of applying a
    /// plan yields a plan with no changes.
    /// </remarks>
    public static class ScoreSyncMerge
    {
        public static ScoreSyncPlan Plan(ScoreSyncData local, ScoreSyncData source)
        {
            var plan = new ScoreSyncPlan();

            ResolvePlayers(local, source, plan);
            PlanGames(local, source, plan);
            var completionKeysChanged = PlanSectionCompletions(local, source, plan);
            PlanSectionProgress(local, source, plan, completionKeysChanged);

            return plan;
        }

        #region Players

        private static void ResolvePlayers(ScoreSyncData local, ScoreSyncData source, ScoreSyncPlan plan)
        {
            var localProfileIds = new HashSet<Guid>(local.Profiles.Select(p => p.Id));
            var localPlayerIds = new HashSet<Guid>(local.Players.Select(p => p.Id));

            // First profile wins when two local profiles share a name, so the choice is stable.
            // Only profiles that existed before this merge are matched by name: two players the
            // source PC keeps apart (a same-named profile, or a deleted profile's scores) stay
            // apart here too, unless a local profile joins them
            var localProfilesByName = new Dictionary<string, Guid>();
            foreach (var profile in local.Profiles)
            {
                string name = NormalizeName(profile.Name);
                if (name.Length > 0 && !localProfilesByName.ContainsKey(name))
                {
                    localProfilesByName.Add(name, profile.Id);
                }
            }

            foreach (var profile in source.Profiles)
            {
                if (plan.PlayerIdMap.ContainsKey(profile.Id))
                {
                    continue;
                }

                ProfileMatch match;
                Guid localId;
                if (localProfileIds.Contains(profile.Id))
                {
                    match = ProfileMatch.ById;
                    localId = profile.Id;
                }
                else if (localProfilesByName.TryGetValue(NormalizeName(profile.Name), out var nameMatch))
                {
                    match = ProfileMatch.ByName;
                    localId = nameMatch;
                }
                else
                {
                    match = ProfileMatch.Created;
                    localId = profile.Id;
                }

                plan.PlayerIdMap.Add(profile.Id, localId);
                plan.Profiles.Add(new ProfileResolution
                {
                    Source = profile,
                    Match = match,
                    LocalId = localId,
                });
            }

            // IDs the file references without a profile (a profile deleted on the source PC
            // whose scores remain). These never create a profile: they keep their ID unless a
            // pre-existing local profile has the same name
            var sourcePlayerNames = new Dictionary<Guid, string>();
            foreach (var player in source.Players)
            {
                sourcePlayerNames.TryAdd(player.Id, player.Name);
            }

            foreach (var id in ReferencedPlayerIds(source))
            {
                if (plan.PlayerIdMap.ContainsKey(id))
                {
                    continue;
                }

                Guid localId = id;
                if (!localProfileIds.Contains(id)
                    && !localPlayerIds.Contains(id)
                    && sourcePlayerNames.TryGetValue(id, out string name)
                    && localProfilesByName.TryGetValue(NormalizeName(name), out var nameMatch))
                {
                    localId = nameMatch;
                }

                plan.PlayerIdMap.Add(id, localId);
            }

            // Players rows: insert if missing, local name wins
            var playersInserted = new HashSet<Guid>();
            foreach (var player in source.Players)
            {
                Guid localId = plan.PlayerIdMap[player.Id];
                if (localPlayerIds.Contains(localId) || !playersInserted.Add(localId))
                {
                    continue;
                }

                plan.PlayersToInsert.Add(new SyncPlayer
                {
                    Id = localId,
                    Name = player.Name,
                });
            }
        }

        private static IEnumerable<Guid> ReferencedPlayerIds(ScoreSyncData source)
        {
            foreach (var player in source.Players)
                yield return player.Id;
            foreach (var game in source.Games)
                foreach (var score in game.PlayerScores)
                    yield return score.PlayerId;
            foreach (var completion in source.SectionCompletions)
                yield return completion.PlayerId;
            foreach (var progress in source.SectionProgress)
                yield return progress.PlayerId;
        }

        /// <summary>
        /// Profile names match trimmed and case-insensitively.
        /// </summary>
        public static string NormalizeName(string name)
        {
            return (name ?? string.Empty).Trim().ToUpperInvariant();
        }

        #endregion

        #region Games

        private static void PlanGames(ScoreSyncData local, ScoreSyncData source, ScoreSyncPlan plan)
        {
            var gameKeys = new HashSet<string>(local.Games.Select(GameKey));

            foreach (var game in source.Games)
            {
                // Also catches a game listed twice in the source
                if (!gameKeys.Add(GameKey(game)))
                {
                    plan.GamesSkipped++;
                    continue;
                }

                var copy = CopyGame(game);
                foreach (var score in copy.PlayerScores)
                {
                    score.PlayerId = plan.PlayerIdMap[score.PlayerId];
                }

                plan.GamesToInsert.Add(copy);
            }
        }

        /// <summary>
        /// Games match on song checksum, exact date ticks and band score.
        /// </summary>
        public static string GameKey(SyncGame game)
        {
            return $"{Convert.ToBase64String(game.SongChecksum)}|{game.DateTicks}|{game.BandScore}";
        }

        private static SyncGame CopyGame(SyncGame game)
        {
            var copy = game.Clone();
            copy.PlayerScores = game.PlayerScores.Select(s => s.Clone()).ToList();
            return copy;
        }

        #endregion

        #region Sections

        /// <returns>
        /// The progress keys whose set of completed sections changed.
        /// </returns>
        private static HashSet<string> PlanSectionCompletions(ScoreSyncData local, ScoreSyncData source,
            ScoreSyncPlan plan)
        {
            var changedProgressKeys = new HashSet<string>();

            var localCompletions = new Dictionary<string, SyncSectionCompletion>();
            foreach (var completion in local.SectionCompletions)
            {
                localCompletions.TryAdd(CompletionKey(completion, completion.PlayerId), completion);
            }

            // Pending writes by key, so a completion listed twice in the source (or two source
            // players remapped onto one local player) merge into one row
            var inserts = new Dictionary<string, SyncSectionCompletion>();
            var dateUpdates = new Dictionary<string, SectionCompletionDateUpdate>();

            foreach (var completion in source.SectionCompletions)
            {
                var playerId = plan.PlayerIdMap[completion.PlayerId];
                string key = CompletionKey(completion, playerId);

                if (localCompletions.TryGetValue(key, out var existing))
                {
                    long current = dateUpdates.TryGetValue(key, out var pending)
                        ? pending.FirstCompletedTicks
                        : existing.FirstCompletedTicks;

                    if (completion.FirstCompletedTicks < current)
                    {
                        dateUpdates[key] = new SectionCompletionDateUpdate
                        {
                            Local = existing,
                            FirstCompletedTicks = completion.FirstCompletedTicks,
                        };
                    }
                }
                else if (inserts.TryGetValue(key, out var inserted))
                {
                    if (completion.FirstCompletedTicks < inserted.FirstCompletedTicks)
                    {
                        inserted.FirstCompletedTicks = completion.FirstCompletedTicks;
                    }
                }
                else
                {
                    var copy = completion.Clone();
                    copy.PlayerId = playerId;
                    inserts.Add(key, copy);
                    changedProgressKeys.Add(ProgressKey(copy.PlayerId, copy.Instrument, copy.SongChecksum,
                        copy.Difficulty, copy.HarmonyIndex));
                }
            }

            plan.SectionCompletionsToInsert.AddRange(inserts.Values);
            plan.SectionCompletionDateUpdates.AddRange(dateUpdates.Values);
            return changedProgressKeys;
        }

        private static void PlanSectionProgress(ScoreSyncData local, ScoreSyncData source, ScoreSyncPlan plan,
            HashSet<string> completionKeysChanged)
        {
            var localProgress = new Dictionary<string, SyncSectionProgress>();
            foreach (var progress in local.SectionProgress)
            {
                localProgress.TryAdd(ProgressKey(progress), progress);
            }

            // Distinct completed section indices per progress key, after the completions merge
            var completed = new Dictionary<string, HashSet<int>>();
            void AddCompleted(SyncSectionCompletion c)
            {
                string key = ProgressKey(c.PlayerId, c.Instrument, c.SongChecksum, c.Difficulty, c.HarmonyIndex);
                if (!completed.TryGetValue(key, out var set))
                {
                    set = new HashSet<int>();
                    completed.Add(key, set);
                }

                set.Add(c.SectionIndex);
            }

            foreach (var completion in local.SectionCompletions)
                AddCompleted(completion);
            foreach (var completion in plan.SectionCompletionsToInsert)
                AddCompleted(completion);

            // The newest row per key among the source's rows (two source players can remap onto
            // one local player) and the local row
            var candidates = new Dictionary<string, SyncSectionProgress>();
            foreach (var progress in source.SectionProgress)
            {
                var remapped = progress.Clone();
                remapped.PlayerId = plan.PlayerIdMap[progress.PlayerId];

                string key = ProgressKey(remapped);
                if (!candidates.TryGetValue(key, out var current) || remapped.LastUpdatedTicks > current.LastUpdatedTicks)
                {
                    candidates[key] = remapped;
                }
            }

            // Local rows whose completions grew need recounting even if the source had no row
            foreach (string key in completionKeysChanged)
            {
                if (!candidates.ContainsKey(key) && localProgress.TryGetValue(key, out var existing))
                {
                    candidates.Add(key, existing);
                }
            }

            foreach (var (key, candidate) in candidates)
            {
                localProgress.TryGetValue(key, out var existing);

                // SectionCount and LastUpdated come from the newer row; local wins a tie
                var newest = existing is not null && existing.LastUpdatedTicks >= candidate.LastUpdatedTicks
                    ? existing
                    : candidate;

                int completedCount = completed.TryGetValue(key, out var set) ? set.Count : 0;
                completedCount = Math.Min(completedCount, newest.SectionCount);

                var row = newest.Clone();
                row.CompletedCount = completedCount;

                if (existing is not null
                    && existing.SectionCount == row.SectionCount
                    && existing.CompletedCount == row.CompletedCount
                    && existing.LastUpdatedTicks == row.LastUpdatedTicks)
                {
                    continue;
                }

                plan.SectionProgressWrites.Add(new SectionProgressWrite
                {
                    Row = row,
                    IsInsert = existing is null,
                });
            }
        }

        private static string CompletionKey(SyncSectionCompletion c, Guid playerId)
        {
            return $"{Convert.ToBase64String(c.SongChecksum)}|{playerId}|{(int) c.Instrument}|"
                + $"{(int) c.Difficulty}|{c.HarmonyIndex}|{c.SectionIndex}";
        }

        private static string ProgressKey(SyncSectionProgress p)
        {
            return ProgressKey(p.PlayerId, p.Instrument, p.SongChecksum, p.Difficulty, p.HarmonyIndex);
        }

        private static string ProgressKey(Guid playerId, Instrument instrument, byte[] checksum,
            Difficulty difficulty, int harmonyIndex)
        {
            return $"{playerId}|{(int) instrument}|{Convert.ToBase64String(checksum)}|"
                + $"{(int) difficulty}|{harmonyIndex}";
        }

        #endregion
    }
}
