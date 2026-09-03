using YARG.Core.Engine;
using YARG.Core.Replays;
using YARG.Player;
using YARG.Replays;

namespace YARG.Menu.ScoreScreen
{
    /// <summary>
    /// How a single chart section stands after the run that just finished.
    /// </summary>
    public enum SectionCompletionState
    {
        /// <summary>
        /// Never perfected, this run included.
        /// </summary>
        Missing = 0,

        /// <summary>
        /// Perfected during an earlier run.
        /// </summary>
        CompletedEarlier = 1,

        /// <summary>
        /// Perfected for the first time during this run.
        /// </summary>
        CompletedThisRun = 2
    }

    /// <summary>
    /// A player's cumulative section completion after the run that just finished.
    /// </summary>
    /// <remarks>
    /// Filled in by <c>GameManager</c> from the same scan that gets persisted, so that the
    /// score screen never has to query the database. Players that can't earn section credit
    /// (bots, replays, invalid scores) get no summary at all.
    /// </remarks>
    public class PlayerSectionSummary
    {
        /// <summary>
        /// The amount of sections that contained at least one note for this instrument.
        /// Sections with no notes can never be perfected, so they are not part of the total.
        /// </summary>
        public int ApplicableCount;

        /// <summary>
        /// The total amount of sections perfected after this run, including the ones that
        /// were already perfected beforehand.
        /// </summary>
        public int CompletedCount;

        /// <summary>
        /// The indices of the sections that were perfected for the first time this run.
        /// </summary>
        public int[] NewlyCompletedIndices;

        /// <summary>
        /// The state of every applicable section, in chart order. One entry per strip block.
        /// </summary>
        public SectionCompletionState[] SectionStates;

        public int NewlyCompletedCount => NewlyCompletedIndices.Length;

        /// <summary>
        /// Whether every applicable section has now been perfected at least once. Cumulative,
        /// so this stays true on every later run of a song whose set is already closed.
        /// </summary>
        public bool IsSectionFullCombo => ApplicableCount > 0 && CompletedCount >= ApplicableCount;

        /// <summary>
        /// Whether this run is the one that closed the set. Unlike
        /// <see cref="IsSectionFullCombo"/> this is only ever true once, so it is what the
        /// score card's tag and accent color key off of.
        /// </summary>
        public bool ClosedSetThisRun => NewlyCompletedCount > 0 && IsSectionFullCombo;
    }

    public struct PlayerScoreCard
    {
        public bool  IsHighScore;
        public float AverageMultiplier;

        public YargPlayer Player;
        public BaseStats  Stats;

        /// <summary>
        /// The player's section completion, or <c>null</c> if this run earned no section credit.
        /// </summary>
        public PlayerSectionSummary Sections;
    }

    public struct ScoreScreenStats
    {
        public PlayerScoreCard[] PlayerScores;

        public int BandStars;
        public int BandScore;

#nullable enable
        public ReplayInfo? ReplayInfo;
#nullable disable
    }
}
