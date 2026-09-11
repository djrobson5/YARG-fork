using System;
using UnityEngine;
using YARG.Core.Chart;
using YARG.Core.Logging;
using YARG.Settings;

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
        /// True from the moment a rewind lands until song time reaches the section marker.
        /// </summary>
        /// <remarks>
        /// The freeze itself is <see cref="Rewinding"/>, which every input and engine gate already
        /// reads. This says <i>why</i> it is up, which is what tells <c>BasePlayer.OnGameInput</c>
        /// to drop inputs instead of queueing them for resume, and what stops
        /// <see cref="ResumeCore"/> from lifting the freeze early.
        /// </remarks>
        public bool IsLeadInActive => _leadInActive;

        /// <summary>
        /// The point in the run a rewind should be measured from: the section marker while a
        /// lead-in is running, since the clock is deliberately behind it.
        /// </summary>
        public double RewindReferenceSongTime => _leadInActive ? _leadInMarkerSongTime : SongTime;

        private bool   _leadInActive;
        private double _leadInMarkerSongTime;
        private double _leadInMarkerInputTime;
        private double _leadInLandingInputTime;
        private double _leadInSongSeconds;

        /// <summary>
        /// Rewinds the run to the start of the section at <paramref name="sectionSongTime"/> and
        /// runs the lead-in.
        /// </summary>
        /// <param name="sectionSongTime">
        /// The section marker, in <b>song</b> time, which is how section times come off the chart.
        /// Everything below converts deliberately: <c>SongRunner.InitializeSongTime</c> anchors
        /// <c>InputTime</c> to the value <see cref="SetSongTime"/> is given and derives
        /// <c>SongTime</c> from it as <c>InputTime + AudioCalibration * SongSpeed</c>, so the
        /// landing has to be handed over in input time.
        /// </param>
        /// <remarks>
        /// The engine is re-simulated to the <i>marker</i> and then frozen there for the whole
        /// lead-in, while song time, audio and the highway sit a lead-in earlier. That is what
        /// makes the window safe: the notes inside it are already judged, so they do not respawn
        /// and nothing in the window can be judged again.
        /// </remarks>
        public void RewindToSection(double sectionSongTime)
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

            if (sectionSongTime > RewindReferenceSongTime)
            {
                YargLogger.LogFormatWarning("Refusing to rewind forwards (from {0} to {1})",
                    RewindReferenceSongTime, sectionSongTime);
                return;
            }

            // The lead-in is in REAL seconds, so the seek goes back leadIn * SongSpeed chart
            // seconds and the wall-clock settle time is the same at every song speed.
            double leadInSeconds = SettingsManager.Settings.RewindLeadIn.Value;
            double leadInSongSeconds = leadInSeconds * SongSpeed;

            double audioCalibrationOffset = _songRunner.AudioCalibration * SongSpeed;
            double markerInputTime = sectionSongTime - audioCalibrationOffset;
            double landingInputTime = markerInputTime - leadInSongSeconds;

            YargLogger.LogFormatInfo(
                "Rewinding from {0:0.000} to section \"{1}\" at {2:0.000} " +
                "(landing {3:0.000}, lead-in {4:0.00} s real / {5:0.00} s song)",
                RewindReferenceSongTime, GetSectionNameAt(sectionSongTime), sectionSongTime,
                sectionSongTime - leadInSongSeconds, leadInSeconds, leadInSongSeconds);

            IsRewindingToSection = true;

            // Every hit/miss/overhit/Star Power dispatch the re-simulation makes goes through the
            // ordinary handlers, which gate their feedback on this flag. Without it the rewind
            // fires thousands of hit sounds.
            bool wasSeekingReplay = IsSeekingReplay;
            IsSeekingReplay = true;

            try
            {
                // 1. Song time and audio, at the START of the lead-in window. Done before the
                //    players so notes are not destroyed early, exactly as the replay seek path
                //    orders it. No start delay: the lead-in is the delay. Negative song time is
                //    accepted here on purpose - the mixer schedules silence until zero - so the
                //    first section can own the intro without a clamp.
                SetSongTime(landingInputTime, 0);

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

                // 6. Re-simulate the surviving input log into the fresh engines up to the MARKER,
                //    then truncate it at the consumed index. Visuals are drawn at the landing.
                foreach (var player in _players)
                {
                    player.RewindTo(markerInputTime, VisualTime);
                }

                // 7. Truncate the pause log at the marker. This also drops the pause that opened
                //    the rewind, so a rewind never counts toward pause-abuse invalidation.
                TruncatePauseInfo(sectionSongTime);

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

            // 9. Hold the engine frozen until the clock reaches the marker.
            BeginLeadIn(sectionSongTime, markerInputTime, landingInputTime, leadInSongSeconds);
        }

        /// <summary>
        /// Raises the lead-in freeze and shows the first countdown frame.
        /// </summary>
        private void BeginLeadIn(double markerSongTime, double markerInputTime,
            double landingInputTime, double leadInSongSeconds)
        {
            _leadInMarkerSongTime = markerSongTime;
            _leadInMarkerInputTime = markerInputTime;
            _leadInLandingInputTime = landingInputTime;
            _leadInSongSeconds = leadInSongSeconds;
            _leadInActive = true;

            // Step 3 of the ordered seek checklist. Rewinding is the gate BasePlayer.GameplayUpdate
            // reads to skip UpdateInputs (and therefore BaseEngine.Update) entirely, so the engine
            // is never handed a time inside the window; UpdateVisuals still runs every frame, so
            // the highway scrolls normally through it.
            //
            // Notes inside the window were judged before the marker, so TrackPlayer.UpdateNotes
            // does not respawn them. That includes a sustain crossing the boundary: the engine
            // still holds it (and the resend at the marker decides whether it keeps ticking), but
            // drawing it again is fork-owned piece 7 and is not in this slice.
            Rewinding = true;

            DriveLeadInCountdown();
        }

        /// <summary>
        /// Advances the lead-in, and releases the engine the frame the clock reaches the marker.
        /// </summary>
        /// <remarks>
        /// Driven from <c>GameManager.Update</c> immediately after <c>SongRunner.Update</c> and
        /// before the player loop, so the release never lags the marker by more than the frame it
        /// falls in, and the players' first live update in that same frame is at or after the
        /// marker.
        /// </remarks>
        private void UpdateRewindLeadIn()
        {
            if (!_leadInActive)
            {
                return;
            }

            if (InputTime >= _leadInMarkerInputTime)
            {
                EndLeadIn();
                return;
            }

            DriveLeadInCountdown();
        }

        /// <summary>
        /// Lets the engine go live at the section marker.
        /// </summary>
        private void EndLeadIn()
        {
            _leadInActive = false;

            // Order matters. Clearing the freeze first is what lets the resend below take the live
            // path through BasePlayer.OnGameInput instead of being dropped again; the engine's
            // first update happens after both, in the player loop later this same frame, at an
            // input time at or after the marker it was re-simulated to.
            Rewinding = false;

            foreach (var player in _players)
            {
                player.SendLeadInInputsAtMarker();
            }

            // One last frame at zero, then hand the widget back in a clean state: the engine's
            // own wait countdown must not inherit the lead-in's cached digit.
            DriveLeadInCountdown();

            foreach (var player in _players)
            {
                player.ForceResetLeadInCountdown();
            }

            YargLogger.LogFormatDebug("Rewind lead-in finished at song time {0:0.000} (marker {1:0.000})",
                SongTime, _leadInMarkerSongTime);
        }

        /// <summary>
        /// Replays the whole lead-in from the same target, for a pause taken inside the window.
        /// </summary>
        /// <remarks>
        /// Not the one-second unpause rewind of <see cref="RewindAndResume"/>: the engine is still
        /// frozen at the marker, so nothing was lost and there is nothing to rewind - the window
        /// simply runs again from its start. The pause itself never reached
        /// <see cref="PauseInfo"/>, because <see cref="Pause"/> skips recording one while
        /// <see cref="Rewinding"/> is up, so it cannot count toward pause-abuse invalidation.
        /// </remarks>
        private void RestartLeadIn()
        {
            _pauseMenu.PopAllMenus();
            Time.timeScale = 1f;

            // Audio Calibration and song speed are both editable from the pause menu, and the
            // marker, the landing and the window length were all derived from them. Pick up the
            // new calibration, then seek once to settle SongSpeed to whatever was requested (this
            // is a no-op audio-wise while paused, which prepares but does not play), and only then
            // recompute - otherwise the release test and the countdown drift apart.
            UpdateCalibration();
            SetSongTime(_leadInLandingInputTime, 0);

            _leadInSongSeconds = SettingsManager.Settings.RewindLeadIn.Value * SongSpeed;
            _leadInMarkerInputTime = _leadInMarkerSongTime - _songRunner.AudioCalibration * SongSpeed;
            _leadInLandingInputTime = _leadInMarkerInputTime - _leadInSongSeconds;

            // Hard cut back to the start of the window. No DOTween scrub: the jump can span
            // minutes, and the lead-in is the transition.
            SetSongTime(_leadInLandingInputTime, 0);
            _songRunner.Resume();

            foreach (var player in _players)
            {
                player.RestartLeadInVisuals(VisualTime);
            }

            // Keeps the freeze up and re-arms the countdown from the top.
            BeginLeadIn(_leadInMarkerSongTime, _leadInMarkerInputTime, _leadInLandingInputTime,
                _leadInSongSeconds);

            ResumeCore();
        }

        private void DriveLeadInCountdown()
        {
            foreach (var player in _players)
            {
                player.UpdateLeadInCountdown(_leadInSongSeconds, _leadInMarkerSongTime);
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
