using System;
using System.Collections.Generic;
using YARG.Core;
using YARG.Core.Logging;
using YARG.Core.Song;

namespace YARG.Scores
{
    public static partial class ScoreContainer
    {
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

            try
            {
                // Queried directly rather than through GetCompletedSections, so that a failed
                // read aborts the write instead of inserting duplicate rows
                var completedIndices = new HashSet<int>();
                foreach (var record in _db.QuerySectionCompletions(songChecksum, playerId, instrument,
                    difficulty, harmonyIndex))
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

                completedCount = completedIndices.Count;
                return true;
            }
            catch (Exception e)
            {
                YargLogger.LogException(e, "Failed to add section completions into database.");
                return false;
            }
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
        /// The denominator comes from the stored rows rather than from the chart, so it is
        /// available without loading the chart. The largest <see cref="SectionCompletionRecord.SectionCount"/>
        /// across the matching rows is used: every row for a given key should carry the same
        /// value (the song checksum pins the chart), and taking the max means a row written by an
        /// older build that used a larger count can never make the fraction read as complete early.
        /// Both values are zero when the player has never perfected a section here.
        /// </remarks>
        public static (int CompletedCount, int SectionCount) GetSectionProgress(HashWrapper songChecksum,
            Guid playerId, Instrument instrument, Difficulty difficulty, int harmonyIndex)
        {
            try
            {
                var records = _db.QuerySectionCompletions(songChecksum, playerId, instrument, difficulty,
                    harmonyIndex);

                var indices = new HashSet<int>();
                int sectionCount = 0;

                foreach (var record in records)
                {
                    indices.Add(record.SectionIndex);
                    if (record.SectionCount > sectionCount)
                    {
                        sectionCount = record.SectionCount;
                    }
                }

                return (indices.Count, sectionCount);
            }
            catch (Exception e)
            {
                YargLogger.LogException(e, "Failed to load section completions from database.");
                return (0, 0);
            }
        }
    }
}
