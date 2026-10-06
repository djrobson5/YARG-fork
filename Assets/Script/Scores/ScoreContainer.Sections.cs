using System;
using System.Collections.Generic;
using YARG.Core;
using YARG.Core.Logging;
using YARG.Core.Song;

namespace YARG.Scores
{
    /// <summary>
    /// A player's cumulative section completion for one song, instrument, difficulty, and
    /// harmony part, as read back for display.
    /// </summary>
    public readonly struct SectionProgress
    {
        /// <summary>
        /// The amount of sections perfected at least once, across every run.
        /// </summary>
        public readonly int CompletedCount;

        /// <summary>
        /// The amount of sections that can be perfected, i.e. the ones with at least one note.
        /// </summary>
        public readonly int SectionCount;

        public SectionProgress(int completedCount, int sectionCount)
        {
            CompletedCount = completedCount;
            SectionCount = sectionCount;
        }

        /// <summary>
        /// Whether every applicable section has been perfected at least once.
        /// </summary>
        public bool IsSectionFullCombo => SectionCount > 0 && CompletedCount >= SectionCount;
    }

    public static partial class ScoreContainer
    {
        /// <summary>
        /// Identifies a single section progress row within one player and instrument.
        /// </summary>
        /// <remarks>
        /// The player and instrument are not part of the key; the cache only ever holds a single
        /// player and instrument at a time, mirroring the high score cache.
        /// </remarks>
        public readonly struct SectionProgressKey : IEquatable<SectionProgressKey>
        {
            public readonly HashWrapper SongChecksum;
            public readonly Difficulty  Difficulty;
            public readonly int         HarmonyIndex;

            public SectionProgressKey(HashWrapper songChecksum, Difficulty difficulty, int harmonyIndex)
            {
                SongChecksum = songChecksum;
                Difficulty = difficulty;
                HarmonyIndex = harmonyIndex;
            }

            public bool Equals(SectionProgressKey other)
            {
                return SongChecksum.Equals(other.SongChecksum) &&
                    Difficulty == other.Difficulty &&
                    HarmonyIndex == other.HarmonyIndex;
            }

            public override bool Equals(object obj) => obj is SectionProgressKey other && Equals(other);

            public override int GetHashCode()
            {
                return HashCode.Combine(SongChecksum, (int) Difficulty, HarmonyIndex);
            }
        }

        private static readonly Dictionary<SectionProgressKey, SectionProgress> PlayerSectionProgress = new();

        private static Instrument _sectionProgressInstrument = Instrument.Band;
        private static Guid       _sectionProgressPlayerId;
        private static bool       _sectionProgressWasFetched;

        /// <summary>
        /// Persists every section that was perfected during a single run.
        /// </summary>
        /// <remarks>
        /// Sections that were already complete are left untouched, and sections that were
        /// dropped this time around never remove an existing row.
        /// </remarks>
        /// <param name="sectionCount">
        /// The amount of sections that contained at least one note for this instrument.
        /// Sections with no notes can never be perfected, so they are not part of the total.
        /// </param>
        /// <param name="completedCount">
        /// The total amount of sections that are complete after this run, including the ones
        /// that were already complete beforehand. Zero if the write failed.
        /// </param>
        /// <returns>
        /// <c>true</c> if the completions were written (or there was nothing new to write).
        /// </returns>
        public static bool RecordSectionCompletions(HashWrapper songChecksum, Guid playerId,
            Instrument instrument, Difficulty difficulty, int harmonyIndex, int sectionCount,
            IReadOnlyList<SectionCompletionResult> results, out int completedCount)
        {
            completedCount = 0;

            int written = 0;

            try
            {
                // The per-section rows and the summary that counts them have to agree, so they
                // are committed together: a failure part way through leaves neither behind
                _db.RunInTransaction(() =>
                {
                    // Queried directly rather than through GetCompletedSections, so that a failed
                    // read aborts the write instead of inserting duplicate rows
                    var completedIndices = new HashSet<int>();
                    foreach (var record in _db.QuerySectionCompletions(songChecksum, playerId,
                        instrument, difficulty, harmonyIndex))
                    {
                        completedIndices.Add(record.SectionIndex);
                    }

                    var newRecords = new List<SectionCompletionRecord>();
                    var date = DateTime.Now;

                    foreach (var result in results)
                    {
                        if (!result.IsPerfected || !completedIndices.Add(result.SectionIndex))
                        {
                            continue;
                        }

                        newRecords.Add(new SectionCompletionRecord
                        {
                            SongChecksum = songChecksum.HashBytes,
                            PlayerId = playerId,

                            Instrument = instrument,
                            Difficulty = difficulty,
                            HarmonyIndex = harmonyIndex,

                            SectionIndex = result.SectionIndex,
                            SectionCount = sectionCount,

                            FirstCompletedDate = date,
                        });
                    }

                    if (newRecords.Count > 0)
                    {
                        _db.InsertSectionCompletions(newRecords);
                    }

                    // Counted over this run's applicable sections rather than over every stored
                    // row, so that rows left behind by sections that are no longer applicable
                    // can't push the fraction past what the score card shows
                    int applicableCount = 0;
                    int completed = 0;

                    foreach (var result in results)
                    {
                        if (result.NotesTotal <= 0)
                        {
                            continue;
                        }

                        applicableCount++;
                        if (completedIndices.Contains(result.SectionIndex))
                        {
                            completed++;
                        }
                    }

                    // The caller counts the applicable sections itself; if the two ever disagree
                    // the denominator is the untrustworthy one, so the numerator is held below it
                    // rather than being allowed to read as a section full combo early
                    if (applicableCount != sectionCount)
                    {
                        YargLogger.LogFormatWarning(
                            "Section count mismatch: {0} applicable sections in the results, {1} reported. Clamping.",
                            applicableCount, sectionCount);
                    }

                    written = Math.Min(completed, sectionCount);

                    // Written on every valid run, even when nothing new was perfected, so that
                    // the music library has a denominator to show from the very first run
                    _db.UpsertSectionProgress(new SectionProgressRecord
                    {
                        SongChecksum = songChecksum.HashBytes,
                        PlayerId = playerId,

                        Instrument = instrument,
                        Difficulty = difficulty,
                        HarmonyIndex = harmonyIndex,

                        SectionCount = sectionCount,
                        CompletedCount = written,

                        LastUpdated = date,
                    });
                });
            }
            catch (Exception e)
            {
                YargLogger.LogException(e,
                    "Failed to write section completions and progress into database. Nothing was committed.");
                completedCount = 0;
                return false;
            }

            // Only once the transaction has committed, so a rolled back run can't leave the cache
            // claiming progress the database doesn't have
            completedCount = written;
            UpdateCachedSectionProgress(songChecksum, playerId, instrument, difficulty, harmonyIndex,
                new SectionProgress(written, sectionCount));

            return true;
        }

        /// <summary>
        /// Gets the indices of every section the player has ever perfected for this song,
        /// instrument, difficulty, and harmony part.
        /// </summary>
        public static HashSet<int> GetCompletedSections(HashWrapper songChecksum, Guid playerId,
            Instrument instrument, Difficulty difficulty, int harmonyIndex)
        {
            var indices = new HashSet<int>();

            try
            {
                var records = _db.QuerySectionCompletions(songChecksum, playerId, instrument, difficulty,
                    harmonyIndex);
                foreach (var record in records)
                {
                    indices.Add(record.SectionIndex);
                }
            }
            catch (Exception e)
            {
                YargLogger.LogException(e, "Failed to load section completions from database.");
            }

            return indices;
        }

        /// <summary>
        /// Gets the amount of sections the player has ever perfected for this song,
        /// instrument, difficulty, and harmony part.
        /// </summary>
        public static int GetCompletedSectionCount(HashWrapper songChecksum, Guid playerId,
            Instrument instrument, Difficulty difficulty, int harmonyIndex)
        {
            return GetCompletedSections(songChecksum, playerId, instrument, difficulty, harmonyIndex).Count;
        }

        /// <summary>
        /// Gets the player's section progress for this song, instrument, difficulty, and harmony
        /// part, as the amount of sections perfected out of the amount that can be perfected.
        /// </summary>
        /// <remarks>
        /// Read from the <see cref="SectionProgressRecord"/> summary rather than from the
        /// per-section rows: the summary is written on every valid run, so it knows the
        /// denominator even before a single section has been perfected, and it is one row per key
        /// rather than one per section, which is what makes the bulk library query cheap. The
        /// per-section rows stay the source of truth and are still read through
        /// <see cref="GetCompletedSections"/> for the score screen.
        /// </remarks>
        /// <param name="allowCacheUpdate">
        /// Sets whether every section progress row for this player and instrument should be
        /// cached. Set this to true when reading a large number of songs for the same player and
        /// instrument (such as the music library), and false when reading across players.
        /// </param>
        /// <returns>
        /// The progress, or <c>null</c> if the player has never finished a valid run here.
        /// </returns>
        public static SectionProgress? GetSectionProgress(HashWrapper songChecksum, Guid playerId,
            Instrument instrument, Difficulty difficulty, int harmonyIndex, bool allowCacheUpdate = true)
        {
            if (allowCacheUpdate)
            {
                FetchSectionProgress(playerId, instrument);
            }

            if (_sectionProgressWasFetched && _sectionProgressInstrument == instrument &&
                _sectionProgressPlayerId == playerId)
            {
                var key = new SectionProgressKey(songChecksum, difficulty, harmonyIndex);
                return PlayerSectionProgress.TryGetValue(key, out var progress) ? progress : null;
            }

            return GetSectionProgressFromDatabase(songChecksum, playerId, instrument, difficulty,
                harmonyIndex);
        }

        private static SectionProgress? GetSectionProgressFromDatabase(HashWrapper songChecksum,
            Guid playerId, Instrument instrument, Difficulty difficulty, int harmonyIndex)
        {
            try
            {
                var record = _db.QuerySectionProgress(songChecksum, playerId, instrument, difficulty,
                    harmonyIndex);
                if (record is null)
                {
                    return null;
                }

                return new SectionProgress(record.CompletedCount, record.SectionCount);
            }
            catch (Exception e)
            {
                YargLogger.LogException(e, "Failed to load section progress from database.");
                return null;
            }
        }

        /// <summary>
        /// Loads every section progress row for a player and instrument into the cache, if it
        /// isn't cached already.
        /// </summary>
        /// <remarks>
        /// Mirrors the high score cache: one bulk query per player and instrument, so that
        /// displaying a whole library page never turns into one database read per row.
        /// </remarks>
        /// <returns>
        /// The cached progress, keyed by song checksum, difficulty, and harmony part.
        /// </returns>
        public static IReadOnlyDictionary<SectionProgressKey, SectionProgress> FetchSectionProgress(
            Guid playerId, Instrument instrument)
        {
            if (_sectionProgressWasFetched && _sectionProgressPlayerId == playerId &&
                _sectionProgressInstrument == instrument)
            {
                // Already cached. No need to fetch again from the database.
                return PlayerSectionProgress;
            }

            try
            {
                PlayerSectionProgress.Clear();

                foreach (var record in _db.QueryPlayerSectionProgress(playerId, instrument))
                {
                    var key = new SectionProgressKey(HashWrapper.Create(record.SongChecksum),
                        record.Difficulty, record.HarmonyIndex);
                    PlayerSectionProgress[key] = new SectionProgress(record.CompletedCount,
                        record.SectionCount);
                }

                _sectionProgressInstrument = instrument;
                _sectionProgressPlayerId = playerId;
                _sectionProgressWasFetched = true;
            }
            catch (Exception e)
            {
                YargLogger.LogException(e, "Failed to load section progress from database.");
            }

            return PlayerSectionProgress;
        }

        /// <summary>
        /// Writes a freshly recorded run's progress into the cache, if the cache is holding the
        /// player and instrument it belongs to.
        /// </summary>
        /// <remarks>
        /// Mirrors how <c>RecordScore</c> keeps the high score cache fresh: the write updates the
        /// cached entry rather than dropping the whole cache, so returning to the music library
        /// after a song doesn't re-query every row. A cache holding some other player or
        /// instrument is left alone, since nothing about it changed.
        /// </remarks>
        private static void UpdateCachedSectionProgress(HashWrapper songChecksum, Guid playerId,
            Instrument instrument, Difficulty difficulty, int harmonyIndex, SectionProgress progress)
        {
            if (!_sectionProgressWasFetched || _sectionProgressPlayerId != playerId ||
                _sectionProgressInstrument != instrument)
            {
                return;
            }

            PlayerSectionProgress[new SectionProgressKey(songChecksum, difficulty, harmonyIndex)] =
                progress;
        }

        /// <summary>
        /// Drops the cached section progress, so the next read goes back to the database.
        /// </summary>
        /// <remarks>
        /// Called from <see cref="InvalidateScoreCache"/>, which is what runs when the cache's
        /// player or instrument stops being the current one. A recorded run doesn't come through
        /// here; it updates the cached entry in place instead.
        /// </remarks>
        public static void InvalidateSectionProgressCache()
        {
            PlayerSectionProgress.Clear();

            _sectionProgressPlayerId = Guid.Empty;
            _sectionProgressInstrument = Instrument.Band;
            _sectionProgressWasFetched = false;
        }
    }
}
