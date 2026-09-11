using System;
using YARG.Core.Chart;
using YARG.Core.Logging;

namespace YARG.Gameplay
{
    /// <summary>
    /// Rewind to an earlier point in the song, mid-run (<c>docs/rewind-design.md</c>).
    /// </summary>
    /// <remarks>
    /// The restore mechanism is a fresh engine per player plus a re-simulation of the truncated
    /// input log, not a snapshot: <see cref="YARG.Core.Engine.BaseEngine.Reset"/> leaves the Star
    /// Power position block, <c>BaseTimeInStarPower</c>, the scheduled-update list and
    /// <c>ActiveSustains</c> set, so a reused engine diverges measurably.
    /// </remarks>
    public partial class GameManager
    {
        /// <summary>
        /// True for the duration of a section rewind, including the re-simulation.
        /// </summary>
        /// <remarks>
        /// <see cref="IsSeekingReplay"/> is set alongside this for the re-simulation, because it
        /// is the flag every SFX / haptics / stem-mute / banner gate already reads
        /// (<c>docs/rewind-design.md</c>, "Ordered seek checklist" step 2). This one exists so
        /// later slices can tell a rewind apart from a replay scrub.
        /// </remarks>
        public bool IsRewindingToSection { get; private set; }

        /// <summary>
        /// Jumps the run back to <paramref name="targetSongTime"/> and resumes play there.
        /// </summary>
        /// <param name="targetSongTime">
        /// The time to land at, in the same units <see cref="SetSongTime"/> takes. That is an
        /// <i>input</i> time: <c>SongRunner.InitializeSongTime</c> anchors <c>InputTime</c> to
        /// this value and derives <c>SongTime</c> from it as
        /// <c>InputTime + AudioCalibration * SongSpeed</c>. Section times come off the chart in
        /// song time, and the two differ by the audio calibration; the lead-in slice, which
        /// subtracts a real-seconds window from a section start, is where that conversion starts
        /// to matter.
        /// </param>
        /// <remarks>
        /// No lead-in and no countdown yet: the engine goes live at the target immediately.
        /// </remarks>
        public void RewindToTime(double targetSongTime)
        {
            if (_players == null || _players.Count == 0)
            {
                return;
            }

            // A rewind is a live-run action. Practice has its own section restart and the replay
            // viewer has its own scrub.
            if (IsPractice || IsReplay || IsRewindingToSection)
            {
                return;
            }

            if (targetSongTime > SongTime)
            {
                YargLogger.LogFormatWarning("Refusing to rewind forwards (from {0} to {1})",
                    SongTime, targetSongTime);
                return;
            }

            YargLogger.LogFormatInfo("Rewinding from {0:0.000} to {1:0.000}", SongTime, targetSongTime);

            IsRewindingToSection = true;

            // Every hit/miss/overhit/Star Power dispatch the re-simulation makes goes through the
            // ordinary handlers, which gate their feedback on this flag. Without it the rewind
            // fires thousands of hit sounds.
            bool wasSeekingReplay = IsSeekingReplay;
            IsSeekingReplay = true;

            try
            {
                // 1. Song time and audio. Done before the players so notes are not destroyed
                //    early, exactly as the replay seek path orders it. No lead-in in this slice,
                //    so there is no start delay: the engine is live at the target.
                SetSongTime(targetSongTime, 0);

                // 2. Fresh engine per player, with every subscription re-established and the
                //    engine manager re-pointed at it. Done before ResetState so that the
                //    unregister/register churn (unison participants, band state, coda count)
                //    settles before the band state is zeroed.
                foreach (var player in _players)
                {
                    player.RebuildEngineForRewind();
                }

                // 3. HUD elements that snapshot the engine containers have to be re-pointed at
                //    the new ones, or they read dead engines for the rest of the run.
                _failMeter.RebuildPlayers();

                // 4. Band-level state, including the rock meter. The per-player stats are
                //    re-established by the re-simulation below.
                EngineManager.ResetState();

                // 5. The band combo is rebuilt from the players' OnComboIncrement dispatches
                //    during the re-simulation, so it has to start from nothing. ResetState above
                //    already zeroes it; this states the requirement where the loop can see it.
                BandCombo = 0;

                // 6. Re-simulate the surviving input log into the fresh engines, then truncate it
                //    at the consumed index.
                foreach (var player in _players)
                {
                    player.RewindTo(targetSongTime);
                }

                // 7. Truncate the pause log at the same song time. This also drops the pause that
                //    opened the rewind, so a rewind never counts toward pause-abuse invalidation.
                TruncatePauseInfo(targetSongTime);

                // 8. Deliberately NOT CheckForRewindInvalidation(): it can call InvalidateScores,
                //    which drops each player's section state. A rewound run stays a normal high
                //    score (docs/rewind-design.md, "Interactions with existing fork features").
                //    Section state is left exactly as it stands; the strip's live percent
                //    double-counts until the section-strip rewind lands.
            }
            finally
            {
                IsSeekingReplay = wasSeekingReplay;
                IsRewindingToSection = false;
            }
        }

        /// <summary>
        /// Drops every pause recorded at or after <paramref name="songTime"/>, and moves the
        /// unpause rewind floor to the rewind target.
        /// </summary>
        /// <remarks>
        /// <c>_rewindLimit</c> is a floor on how far back the 1-second unpause rewind may go, so
        /// lowering it to the target lets a pause taken straight after a rewind land back at the
        /// target rather than being clamped to somewhere ahead of it.
        /// <para>
        /// Beware when the pause-menu entry point lands: <see cref="Resume"/> reads
        /// <c>PauseInfo[^1]</c> unconditionally to record the pause length, and this can empty the
        /// list (the pause that opened the rewind menu is one of the entries it drops). That path
        /// has to tolerate an empty list.
        /// </para>
        /// </remarks>
        private void TruncatePauseInfo(double songTime)
        {
            for (int i = PauseInfo.Count - 1; i >= 0; i--)
            {
                if (PauseInfo[i].PauseTime >= songTime)
                {
                    PauseInfo.RemoveAt(i);
                }
            }

            _rewindLimit = Math.Min(_rewindLimit, songTime);
        }

        /// <summary>
        /// The start time of the section this rewind should target, given where the song is now.
        /// </summary>
        /// <remarks>
        /// The current section's start, unless the song has only just entered it, in which case
        /// the previous section's. The first section owns the intro, so it rewinds to song start.
        /// Returns <c>null</c> when the chart has no sections.
        /// </remarks>
        public double? GetRewindTargetSectionTime(double fromSongTime, bool preferPrevious = false)
        {
            var sections = Chart?.Sections;
            if (sections == null || sections.Count == 0)
            {
                return null;
            }

            int index = -1;
            for (int i = 0; i < sections.Count; i++)
            {
                if (sections[i].Time > fromSongTime)
                {
                    break;
                }

                index = i;
            }

            if (index < 0)
            {
                // Before the first section; there is nowhere to go but the start of the song.
                return 0;
            }

            const double RESTART_SAME_SECTION_WINDOW = 2;
            if (preferPrevious || fromSongTime - sections[index].Time < RESTART_SAME_SECTION_WINDOW)
            {
                index--;
            }

            if (index < 0)
            {
                // The first section owns the intro, so its start is the start of the song.
                return 0;
            }

            return index == 0 ? 0 : sections[index].Time;
        }

        /// <summary>
        /// The name of the section starting at (or containing) the given song time, for logging.
        /// </summary>
        private string GetSectionNameAt(double songTime)
        {
            var sections = Chart?.Sections;
            if (sections == null || sections.Count == 0)
            {
                return "<no sections>";
            }

            Section found = null;
            foreach (var section in sections)
            {
                if (section.Time > songTime)
                {
                    break;
                }

                found = section;
            }

            return found?.Name ?? "<intro>";
        }
    }
}
