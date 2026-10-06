using System;
using SQLite;
using YARG.Core;

namespace YARG.Scores
{
    /// <summary>
    /// A player's cumulative section completion progress for one song, instrument, difficulty,
    /// and harmony part.
    /// </summary>
    /// <remarks>
    /// This is a denormalized cache of the <see cref="SectionCompletionRecord"/> rows, which stay
    /// the source of truth. It exists so the music library can show the fraction with one bulk
    /// query per player and instrument, and so that a valid run which perfected nothing still
    /// leaves behind a denominator to display (<c>0/12</c> rather than no fraction at all).
    /// One row is upserted per valid run.
    /// </remarks>
    [Table("SectionProgress")]
    public class SectionProgressRecord
    {
        // DO NOT change any of these field names
        // without changing the SQL queries!

        [PrimaryKey, AutoIncrement]
        public int Id { get; set; }

        // One composite unique index over the whole key enforces the one-row-per-key invariant
        // that the upsert relies on. sqlite-net allows only a single [Indexed] per property, so
        // the player and instrument lead the index rather than getting one each: that way its
        // leading columns still serve the per-player bulk query, and the single-row lookup
        // matches on all five either way
        [Indexed(Name = "SectionProgress_Key", Order = 1, Unique = true)]
        public Guid PlayerId { get; set; }

        [Indexed(Name = "SectionProgress_Key", Order = 2, Unique = true)]
        public Instrument Instrument { get; set; }

        [Indexed(Name = "SectionProgress_Key", Order = 3, Unique = true)]
        public byte[] SongChecksum { get; set; }

        [Indexed(Name = "SectionProgress_Key", Order = 4, Unique = true)]
        public Difficulty Difficulty { get; set; }

        /// <summary>
        /// The harmony part the player was singing, or 0 for every non-harmony instrument.
        /// </summary>
        [Indexed(Name = "SectionProgress_Key", Order = 5, Unique = true)]
        public int HarmonyIndex { get; set; }

        /// <summary>
        /// The amount of sections that contained at least one note for this instrument as of the
        /// last run, i.e. the denominator of the fraction. Sections with no notes can never be
        /// perfected, so they are excluded.
        /// </summary>
        public int SectionCount { get; set; }

        /// <summary>
        /// The amount of those sections that have been perfected at least once, across every run.
        /// </summary>
        public int CompletedCount { get; set; }

        public DateTime LastUpdated { get; set; }
    }
}
