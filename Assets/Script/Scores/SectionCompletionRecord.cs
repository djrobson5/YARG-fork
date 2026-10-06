using System;
using SQLite;
using YARG.Core;

namespace YARG.Scores
{
    /// <summary>
    /// A single chart section that a player has perfected (hit every note of) at least once.
    /// </summary>
    /// <remarks>
    /// Rows are only ever added, never removed. A later imperfect run of the same section
    /// leaves the existing row alone, which is what makes the stat cumulative.
    /// </remarks>
    [Table("SectionCompletions")]
    public class SectionCompletionRecord
    {
        // DO NOT change any of these field names
        // without changing the SQL queries!

        [PrimaryKey, AutoIncrement]
        public int Id { get; set; }

        [Indexed]
        public byte[] SongChecksum { get; set; }

        [Indexed]
        public Guid PlayerId { get; set; }

        [Indexed]
        public Instrument Instrument { get; set; }
        public Difficulty Difficulty { get; set; }

        /// <summary>
        /// The harmony part the player was singing, or 0 for every non-harmony instrument.
        /// </summary>
        /// <remarks>
        /// Part of the key, so that HARM1 and HARM2 runs don't merge into one completion set.
        /// </remarks>
        public int HarmonyIndex { get; set; }

        /// <summary>
        /// The index of the section within the chart's section list.
        /// </summary>
        /// <remarks>
        /// Section names are not unique within a song, so the index is the only usable key.
        /// A chart edit changes <see cref="SongChecksum"/>, which invalidates these indices.
        /// </remarks>
        public int SectionIndex { get; set; }

        /// <summary>
        /// The amount of sections that were applicable to this instrument when the row was
        /// written, i.e. the sections that contained at least one note. Sections with no notes
        /// can never be perfected, so they are excluded from the denominator.
        /// </summary>
        public int SectionCount { get; set; }

        public DateTime FirstCompletedDate { get; set; }
    }
}
