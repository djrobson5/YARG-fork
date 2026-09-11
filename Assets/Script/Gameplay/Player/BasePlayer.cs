using System.Collections.Generic;
using PlasticBand.Haptics;
using UnityEngine;
using UnityEngine.InputSystem;
using YARG.Core.Audio;
using YARG.Core.Chart;
using YARG.Core.Engine;
using YARG.Core.Input;
using YARG.Core.Logging;
using YARG.Core.Replays;
using YARG.Gameplay.HUD;
using YARG.Gameplay.SpPath;
using YARG.Helpers.Extensions;
using YARG.Helpers.UI;
using YARG.Input;
using YARG.Player;
using YARG.Scores;
using YARG.Settings;

namespace YARG.Gameplay.Player
{
    public abstract class BasePlayer : GameplayBehaviour
    {
        public int HighwayIndex { get; private set; }

        public YargPlayer Player { get; private set; }

        public float NoteSpeed
        {
            get
            {
                float noteSpeed = Player.Profile.NoteSpeed * _noteSpeedDifficultyScale;

                // If we're in a replay, don't change the note speed (it should be like a video
                // slowing down/speeding up). The actual song speed should be taken into account though,
                // which is saved in the engine parameter override.
                if (Player.IsReplay)
                {
                    return noteSpeed / (float) Player.EngineParameterOverride.SongSpeed;
                }

                if (GameManager.IsPractice && GameManager.SongSpeed < 1)
                {
                    return noteSpeed;
                }

                return noteSpeed / GameManager.SongSpeed;
            }
        }

        /// <summary>
        /// The player's input calibration, in seconds.
        /// </summary>
        /// <remarks>
        /// Be aware that this value is negated!
        /// Positive calibration settings will result in a negative number here.
        /// </remarks>
        public double InputCalibration => -Player.Profile.InputCalibrationSeconds;

        public abstract BaseEngine BaseEngine { get; }

        public BaseStats BaseStats => BaseEngine.BaseStats;
        public BaseEngineParameters BaseParameters => BaseEngine.BaseParameters;

        /// <summary>
        /// <p> Star thresholds, from 1 to 5 stars, then gold stars. </p>
        /// <p> These values represent multiples of the score if you were to FC, hold all sustains fully, hit no dynamics, and use no star power. </p>
        /// <p> Multiplying these by the max multiplier of the instrument will also roughly give you the average multiplier needed for that star. </p>
        /// </summary>
        protected abstract float[] StarMultiplierThresholds { get; set; }

        /// <summary>
        /// Multiples of the maximum points it is possible to get from a solo, which is then added to each star point threshold (1 to 5, then gold stars).
        /// <seealso cref="StarMultiplierThresholds"/>
        /// </summary>
        protected readonly float[] SoloBonusStarMultiplierThresholds = {
            0.05f, 0.1f, 0.2f, 0.35f, 0.65f, 0.95f
        };

        public abstract bool ShouldUpdateInputsOnResume { get; }

        public HitWindowSettings HitWindow { get; protected set; }

        public float Stars => BaseStats.Stars;

        public int Score => BaseStats.TotalScore;
        public int BandBonusScore => BaseStats.BandBonusScore;
        public int Combo => BaseStats.Combo;
        public int NotesHit => BaseStats.NotesHit;

        public int TotalNotes { get; protected set; }

        public bool IsFc { get; protected set; }

        public int? LastHighScore { get; private set; }

        public IReadOnlyList<GameInput> ReplayInputs => _replayInputs.AsReadOnly();

        private Dictionary<int, GameInput> LastInputs { get; } = new();
        private Dictionary<int, GameInput> InputsToSendOnResume { get; } = new();

        /// <summary>
        /// Physical button state seen while a rewind lead-in is up. Deliberately <i>not</i>
        /// <see cref="InputsToSendOnResume"/>.
        /// </summary>
        /// <remarks>
        /// Inputs arriving in the lead-in window are dropped, never queued
        /// (<c>docs/rewind-design.md</c>, "Lead-in" -&gt; Engine). This only remembers where the
        /// player's fingers are, so <see cref="SendLeadInInputsAtMarker"/> can hand the engine the
        /// current physical state at the marker.
        /// </remarks>
        private Dictionary<int, GameInput> LeadInInputs { get; } = new();

        /// <summary>
        /// What the freshly re-simulated engine believes each action's state to be at the marker:
        /// the last value of each action in the surviving (truncated) input log.
        /// </summary>
        private Dictionary<int, int> RewoundEngineInputState { get; } = new();

        protected SyncTrack SyncTrack { get; private set; }

        protected bool IsInitialized { get; private set; }

        protected List<ISantrollerHaptics> SantrollerHaptics { get; private set; } = new();

        protected BaseInputViewer InputViewer { get; private set; }

        protected int  LastCombo;
        protected bool IsStemMuted;

        private List<GameInput> _replayInputs;

        private int _replayInputIndex;

        private float _noteSpeedDifficultyScale;

        protected EngineManager.EngineContainer EngineContainer;

        protected bool PlayerHasFailed;

        /// <summary>
        /// The section marker a rewind re-simulated to, in <b>song</b> time, for as long as the
        /// lead-in window is up. <see cref="double.NaN"/> at every other moment.
        /// </summary>
        /// <remarks>
        /// The engine sits here while the highway sits a lead-in earlier, which is the one gap in
        /// the run where the two clocks disagree - so anything drawing from engine state during
        /// the window has to measure against this rather than against visual time. Today that is
        /// the sustain crossing the boundary (<c>docs/rewind-design.md</c>, "Fork-owned pieces
        /// that need new code" item 7).
        /// </remarks>
        protected double RewindMarkerSongTime { get; private set; } = double.NaN;

        protected override void GameplayAwake()
        {
            _replayInputs = new List<GameInput>();

            // TODO: Couldn't there be more than one input viewer?
            //  We were using FindObjectOfType<BaseInputViewer> before anyway, so we're no worse off in that respect
            InputViewer = FindFirstObjectByType<BaseInputViewer>();

            IsFc = true;
        }

        private void Update()
        {
            //Ensure hud elements get repositioned on screen size change
            if (ScreenSizeDetector.HasScreenSizeChanged)
            {
                UpdateVisuals(GameManager.VisualTime);
            }
        }

        protected void Start()
        {
            if (Player.Bindings is not null)
            {
                SantrollerHaptics = Player.Bindings.GetDevicesByType<ISantrollerHaptics>();
            }

            if (!Player.IsReplay)
            {
                SubscribeToInputEvents();
            }
        }

        protected void Initialize(int index, YargPlayer player, SongChart chart, int? lastHighScore)
        {
            if (IsInitialized)
            {
                return;
            }

            HighwayIndex = index;
            Player = player;

            SyncTrack = chart.SyncTrack;

            LastHighScore = lastHighScore;

            _noteSpeedDifficultyScale = Player.Profile.CurrentDifficulty.NoteSpeedScale();

            if (Player.IsReplay && GameManager.ReplayInfo != null)
            {
                _replayInputs = new List<GameInput>(GameManager.ReplayData.Frames[player.ReplayIndex].Inputs);
                YargLogger.LogFormatDebug("Initialized replay inputs with {0} inputs", _replayInputs.Count);
            }

            if (InputViewer != null)
            {
                InputViewer.SetColors(player.ColorProfile);
                InputViewer.ResetButtons();
            }

            IsInitialized = true;
        }

        public virtual void GameplayUpdate()
        {
            if (!GameManager.Started || GameManager.Paused)
            {
                return;
            }

            if (!GameManager.Rewinding)
            {
                UpdateInputs(GameManager.InputTime);
            }

            UpdateVisuals(GameManager.VisualTime);
        }

        protected abstract void UpdateVisuals(double visualTime);
        protected abstract void ResetVisuals();
        public abstract void Rewind(double visualTime);
        public abstract void PostRewind(double visualTime);

        public virtual void ResetPracticeSection()
        {
            LastCombo = 0;

            IsFc = true;

            ResetVisuals();
        }

        public abstract void SetPracticeSection(uint start, uint end);

        // TODO Make this more generic
        public abstract void SetStemMuteState(bool muted);

        /// <summary>
        /// This player's live section strip state, or <c>null</c> if this run cannot earn
        /// section completion credit.
        /// </summary>
        public SectionStripState SectionState { get; private set; }

        /// <summary>
        /// Hands this player the section state built for it at song start.
        /// </summary>
        /// <remarks>
        /// The eligibility gates live in <c>GameManager</c> next to the ones the end-of-song scan
        /// uses, so there is only one place that decides whether a run counts.
        /// </remarks>
        public void SetSectionState(SectionStripState state)
        {
            SectionState = state;
            OnSectionStateSet();
        }

        /// <summary>
        /// The phrases of this player's own note track, or <c>null</c> for a player that has none.
        /// </summary>
        /// <remarks>
        /// Exists so the rewind picker can find the BRE and coda spans without knowing which
        /// concrete player it is looking at: a section that starts inside one is not a valid
        /// rewind target (<c>docs/rewind-design.md</c>, "Section starting inside a BRE/coda").
        /// The track is the post-modifier one, which is the one actually being played.
        /// </remarks>
        public virtual IReadOnlyList<Phrase> TrackPhrases => null;

        /// <summary>
        /// Called once the section state has been assigned, so that players with somewhere to
        /// draw it can pass it along.
        /// </summary>
        protected virtual void OnSectionStateSet()
        {
        }

        /// <summary>
        /// Clears the fail and near-fail look after a rewind has refilled the rock meter.
        /// </summary>
        /// <remarks>
        /// Fork-owned latched view state, like the ones listed in <c>docs/rewind-design.md</c>
        /// under "Fork-owned pieces that need new code". A no-op for players with no highway.
        /// </remarks>
        public virtual void ClearRewindFailState()
        {
        }

        /// <summary>
        /// The optimal Star Power path computed for this player at song load, or <c>null</c> when
        /// the overlay is off, the instrument is unsupported, or this is a band run.
        /// </summary>
        public StarPowerPath StarPowerPath { get; private set; }

        /// <summary>
        /// Whether the Star Power path overlay is switched on for this run
        /// (<c>docs/sp-path-design.md</c> §4.5). Set once at song load; a player with this off
        /// never computes a path, not even on a practice-section change.
        /// </summary>
        public bool StarPowerPathEnabled { get; private set; }

        /// <summary>
        /// Set once the player's actual Star Power state stops matching the plan. Never un-set
        /// within a run — only a practice-section change or a replay seek clears it, both of
        /// which rebuild the path anyway.
        /// </summary>
        public bool SpPathDiverged { get; protected set; }

        /// <summary>
        /// Turns the overlay on for this player and computes the first path.
        /// </summary>
        /// <remarks>
        /// The gates live in <c>GameManager.InitializeStarPowerPaths</c>, next to the section
        /// strip's, so there is only one place that decides whether a run gets an overlay.
        /// </remarks>
        public void EnableStarPowerPath()
        {
            StarPowerPathEnabled = true;
            RecomputeStarPowerPath();
        }

        /// <summary>
        /// Rebuilds the path from the player's current note track. A no-op for players that do
        /// not support the overlay, and whenever <see cref="StarPowerPathEnabled"/> is false.
        /// </summary>
        public virtual void RecomputeStarPowerPath()
        {
        }

        /// <summary>
        /// Hands this player a freshly computed path (or <c>null</c> to clear it), and resets the
        /// divergence flag, since a new path describes a run that has not started yet.
        /// </summary>
        protected void SetStarPowerPath(StarPowerPath path)
        {
            StarPowerPath = path;
            SpPathDiverged = false;
            OnStarPowerPathSet();
        }

        /// <summary>
        /// Called once the path has been assigned, so that players with somewhere to draw it can
        /// pass it along.
        /// </summary>
        protected virtual void OnStarPowerPathSet()
        {
        }

        /// <summary>
        /// Tells the section state that a note at the given tick was missed, which drops the
        /// section containing it for this run.
        /// </summary>
        /// <remarks>
        /// The single place the per-instrument miss paths funnel into, so that adding an
        /// instrument never means adding another hook.
        /// <para>
        /// Held off for the duration of a section rewind, like <see cref="NotifySectionNoteHit"/>.
        /// A miss is idempotent, so this gate is defensive rather than load-bearing: it keeps the
        /// two feeds symmetric, so that the whole of the strip's input is off for the re-simulation
        /// rather than half of it.
        /// </para>
        /// </remarks>
        protected void NotifySectionNoteMissed(uint tick)
        {
            if (GameManager.IsRewindingToSection)
            {
                return;
            }

            SectionState?.OnNoteMissed(tick);
        }

        /// <summary>
        /// Tells the section state that <paramref name="count"/> of the notes the scanner counts
        /// were just hit at the given tick, which advances that section's live progress.
        /// </summary>
        /// <remarks>
        /// The mirror image of <see cref="NotifySectionNoteMissed"/>, and the single place the
        /// per-instrument hit paths funnel into.
        /// <para>
        /// A section rewind re-simulates the surviving input log into a fresh engine, which
        /// re-dispatches every hit before the target, and then rewinds the strip deliberately
        /// (<c>SectionStripState.RewindTo</c>). The rewind clears the target and everything after
        /// it, but deliberately does <i>not</i> clear the blocks before it - those already hold
        /// the surviving timeline's own counts from live play. This adds, so letting the replayed
        /// hits through would roughly double every one of them (<c>docs/rewind-design.md</c>,
        /// "Ordered seek checklist" step 6); the feed is held off instead. The gate is the
        /// rewind's own flag rather than <c>GameManager.IsSeekingReplay</c>, which the rewind also
        /// raises: the double count is a rewind problem, and a run with a live strip is never
        /// scrubbed any other way.
        /// </para>
        /// </remarks>
        protected void NotifySectionNoteHit(uint tick, int count)
        {
            if (GameManager.IsRewindingToSection)
            {
                return;
            }

            SectionState?.OnNoteHit(tick, count);
        }

        /// <summary>
        /// Determines which of the chart's sections had every one of their notes hit this run.
        /// </summary>
        /// <returns>
        /// One result per section, in section order, or <c>null</c> if this player
        /// does not support section completion tracking.
        /// </returns>
        public virtual IReadOnlyList<SectionCompletionResult> ScanSectionCompletion(
            IReadOnlyList<Section> sections)
        {
            return null;
        }

        public virtual void SetStarPowerFX(bool active)
        {
            GameManager.ChangeStemReverbState(SongStem.Song, active);
        }

        /// <summary>
        /// Throws this player's engine away and builds a fresh one, re-establishing every
        /// subscription and re-pointing the engine manager at it.
        /// </summary>
        /// <remarks>
        /// Step 1 of the ordered seek checklist in <c>docs/rewind-design.md</c>. A fresh engine is
        /// mandatory: <c>BaseEngine.Reset()</c> leaves the Star Power position block,
        /// <c>BaseTimeInStarPower</c>, the scheduled-update list and (for keys) <c>ActiveSustains</c>
        /// set, so re-simulating on a reused engine diverges. Never reuse.
        /// </remarks>
        public abstract void RebuildEngineForRewind();

        /// <summary>
        /// Re-simulates the surviving input log into the freshly built engine, then truncates the
        /// log at the point the engine consumed up to.
        /// </summary>
        /// <remarks>
        /// Steps 5 of the ordered seek checklist. Call <see cref="RebuildEngineForRewind"/> first.
        /// </remarks>
        /// <param name="markerInputTime">
        /// The section marker, in <i>input</i> time. The engine is re-simulated up to here and then
        /// held frozen through the lead-in, so its state is the state at the marker and the notes
        /// inside the lead-in window stay judged.
        /// </param>
        /// <param name="markerSongTime">
        /// The same marker in song time, which is the clock the chart's own note times are on.
        /// </param>
        /// <param name="landingVisualTime">
        /// Where the highway is drawn from: the start of the lead-in window, a whole lead-in
        /// earlier than the marker, in visual time.
        /// </param>
        public virtual void RewindTo(double markerInputTime, double markerSongTime,
            double landingVisualTime)
        {
            IsFc = true;

            // Set before the visuals are rebuilt below, because rebuilding them is what has to
            // redraw the sustain crossing the marker.
            RewindMarkerSongTime = markerSongTime;

            // The engine's timeline is input time plus this player's input calibration, because
            // that is the offset UpdateInputs applies on every live update. Re-simulating on the
            // raw input time would leave the engine ahead of (or behind) the first live update by
            // the calibration amount.
            double engineTime = markerInputTime + InputCalibration;

            _replayInputIndex = BaseEngine.ProcessUpToTime(engineTime, ReplayInputs);

            // The recorded log is the replay, and the replay is the surviving timeline only.
            // ProcessUpToTime returns the number of inputs it consumed, which is where the
            // surviving timeline ends.
            if (_replayInputIndex < _replayInputs.Count)
            {
                _replayInputs.RemoveRange(_replayInputIndex, _replayInputs.Count - _replayInputIndex);
            }

            CaptureRewoundEngineInputState();
            LeadInInputs.Clear();

            // A rewind fired from the pause menu means the player has been pressing things since
            // the pause, and those went to InputsToSendOnResume rather than LastInputs. Fold them
            // in - they are the physical truth - and drop the queue, so the marker resend cannot
            // re-assert a stale "held" and the orphans cannot flush at some later, unrelated
            // resume.
            foreach (var queued in InputsToSendOnResume.Values)
            {
                LastInputs[queued.Action] = queued;
            }

            InputsToSendOnResume.Clear();

            SetStemMuteState(false);

            RestartLeadInVisuals(landingVisualTime);

            LastCombo = Combo;
        }

        /// <summary>
        /// Rebuilds every visual from scratch at <paramref name="visualTime"/>, the start of the
        /// lead-in window.
        /// </summary>
        /// <remarks>
        /// Split out of <see cref="RewindTo"/> because a pause taken inside the lead-in replays the
        /// whole window on resume, which has to put the highway back without touching the engine.
        /// </remarks>
        public virtual void RestartLeadInVisuals(double visualTime)
        {
            ResetVisuals();
            UpdateVisuals(visualTime);
        }

        /// <summary>
        /// Records what the re-simulated engine believes each action's state to be, so the marker
        /// resend can send only the actions the player's hands actually disagree with.
        /// </summary>
        private void CaptureRewoundEngineInputState()
        {
            RewoundEngineInputState.Clear();

            // _replayInputs is already truncated to the surviving timeline, so its last value per
            // action is exactly what ProcessUpToTime left the engine holding.
            foreach (var input in _replayInputs)
            {
                RewoundEngineInputState[input.Action] = input.Integer;
            }
        }

        /// <summary>
        /// Whether <paramref name="action"/> latches - the engine keeps holding it until a release
        /// arrives - as opposed to a momentary action whose press is an event in its own right.
        /// </summary>
        /// <remarks>
        /// Only held-state actions may be re-asserted at a rewind marker. A strum, a Star Power
        /// tap or a velocity-carrying pad hit is consumed the instant it is queued, so replaying a
        /// press would be a phantom event, not a restoration. Defaults to nothing: a player that
        /// wants presses resent has to name its held actions.
        /// </remarks>
        protected virtual bool IsHeldStateAction(int action)
        {
            return false;
        }

        /// <summary>
        /// Hands the engine the current physical button state, at the section marker.
        /// </summary>
        /// <remarks>
        /// Step 4 of the ordered seek checklist. A sustain crossing the section boundary was
        /// rebuilt by the re-simulation; if the frets are still held it keeps ticking, and if they
        /// are not it drops under normal rules. Only the actions whose physical state differs from
        /// what the engine was left believing are sent, so nothing redundant is queued.
        /// <para>
        /// <see cref="ShouldUpdateInputsOnResume"/> is honoured exactly as the unpause resume path
        /// honours it: drums and vocals have no held state worth restoring, and a resend there
        /// would be a phantom hit.
        /// </para>
        /// <para>
        /// Where this diverges from <see cref="SendInputsOnResume"/>: that path carries only the
        /// actions that <i>changed</i> during the pause and still differ from
        /// <see cref="LastInputs"/>, because the engine it resumes into is the same engine that
        /// was paused, so anything unchanged is already correct. A rewind resumes into a
        /// <i>fresh</i> engine re-simulated to a point in the past, so "unchanged" says nothing -
        /// the comparison has to be against what the re-simulation actually left the engine
        /// believing, which is <see cref="RewoundEngineInputState"/>. That widens the candidate
        /// set to every action, which is why the momentary ones are then excluded explicitly by
        /// <see cref="IsHeldStateAction"/>; the resume path never needed that filter because a
        /// momentary press that came and went during a pause cancels itself out of its queue.
        /// </para>
        /// </remarks>
        public void SendLeadInInputsAtMarker()
        {
            // Whatever arrived during the window was dropped, but it is still the truth about
            // where the player's hands are.
            foreach (var captured in LeadInInputs.Values)
            {
                LastInputs[captured.Action] = captured;
            }

            LeadInInputs.Clear();

            // The window is over: engine and highway are back on the same clock, so the crossing
            // sustain is drawn by the ordinary rules from here.
            RewindMarkerSongTime = double.NaN;
            OnLeadInMarkerReached();

            if (!ShouldUpdateInputsOnResume)
            {
                RewoundEngineInputState.Clear();
                return;
            }

            // OnGameInput writes back into LastInputs, so iterate a snapshot.
            var physicalState = new List<GameInput>(LastInputs.Values);
            foreach (var physical in physicalState)
            {
                if (RewoundEngineInputState.TryGetValue(physical.Action, out int engineValue) &&
                    engineValue == physical.Integer)
                {
                    // The engine already holds this; sending it again would be a spurious event.
                    continue;
                }

                // A press may only be re-asserted for a latching action. A strum or a Star Power
                // tap inside the window whose release lands after the marker would otherwise be
                // delivered here as a true, which is an overstrum or a phantom deploy - exactly
                // the judging the window is supposed to drop. A release is always safe to send:
                // at worst it clears a held belief the player is no longer backing.
                if (physical.Button && !IsHeldStateAction(physical.Action))
                {
                    continue;
                }

                var input = new GameInput(InputManager.CurrentInputTime, physical.Action, physical.Integer);
                OnGameInput(ref input);
            }

            RewoundEngineInputState.Clear();
        }

        /// <summary>
        /// Drives this player's countdown widget for a rewind lead-in.
        /// </summary>
        public virtual void UpdateLeadInCountdown(double countdownLength, double endSongTime)
        {
        }

        /// <summary>
        /// Puts this player's countdown widget back in its reset state once the lead-in is over.
        /// </summary>
        public virtual void ForceResetLeadInCountdown()
        {
        }

        /// <summary>
        /// Called once the lead-in window has closed, for anything a player worked out during the
        /// re-simulation and only needed for the duration of the window.
        /// </summary>
        protected virtual void OnLeadInMarkerReached()
        {
        }

        public virtual void SetReplayTime(double time)
        {
            IsFc = true;

            _replayInputIndex = BaseEngine.ProcessUpToTime(time, ReplayInputs);

            SetStemMuteState(false);

            ResetVisuals();
            UpdateVisuals(time);
        }

        protected override void GameplayDestroy()
        {
            if (!Player.IsReplay)
            {
                UnsubscribeFromInputEvents();
            }

            FinishDestruction();
        }

        protected virtual void FinishDestruction()
        {
        }

        protected virtual void UpdateInputs(double time)
        {
            // Apply input offset
            // Video offset is already accounted for
            time += InputCalibration;

            if (Player.IsReplay && GameManager.ReplayInfo != null)
            {
                while (_replayInputIndex < ReplayInputs.Count)
                {
                    var input = ReplayInputs[_replayInputIndex];

                    // Current input does not meet the time requirement
                    if (time < input.Time)
                    {
                        break;
                    }

                    BaseEngine.QueueInput(ref input);
                    OnInputQueued(input);

                    _replayInputIndex++;
                }
            }

            BaseEngine.Update(time);
        }

        private void SubscribeToInputEvents()
        {
            Player.Bindings.SubscribeToGameplayInputs(Player.Profile.GameMode, OnGameInput);

            Player.Bindings.DeviceAdded += OnDeviceAdded;
            Player.Bindings.DeviceRemoved += OnDeviceRemoved;
        }

        private void UnsubscribeFromInputEvents()
        {
            Player.Bindings.UnsubscribeFromGameplayInputs(Player.Profile.GameMode, OnGameInput);

            Player.Bindings.DeviceAdded -= OnDeviceAdded;
            Player.Bindings.DeviceRemoved -= OnDeviceRemoved;
        }

        private void OnDeviceAdded(InputDevice device)
        {
            if (device is ISantrollerHaptics haptics)
            {
                SantrollerHaptics.Add(haptics);
            }
        }

        private void OnDeviceRemoved(InputDevice device)
        {
            if (device is ISantrollerHaptics haptics)
            {
                SantrollerHaptics.Remove(haptics);
            }

            if (!GameManager.Paused && SettingsManager.Settings.PauseOnDeviceDisconnect.Value)
            {
                GameManager.SetPaused(true);
            }
        }

        public void SendInputsOnResume()
        {
            foreach (var originalInput in InputsToSendOnResume.Values)
            {
                var input = new GameInput(InputManager.CurrentInputTime, originalInput.Action, originalInput.Integer);
                OnGameInput(ref input);
            }

            InputsToSendOnResume.Clear();
        }

        protected void OnGameInput(ref GameInput input)
        {
            // Ignore completely if the song hasn't started yet or player failed
            if (!GameManager.Started || PlayerHasFailed)
                return;

            // Ignore while paused
            if (GameManager.Paused || GameManager.Rewinding)
            {
                if (GameManager.IsLeadInActive)
                {
                    // Dropped, not queued: nothing pressed during a lead-in is ever judged
                    // (docs/rewind-design.md, "Lead-in" -> Engine). All that is kept is where the
                    // buttons are, for the physical-state resend at the marker.
                    if (ShouldUpdateInputsOnResume)
                    {
                        LeadInInputs[input.Action] = input;
                    }

                    return;
                }

                if (!ShouldUpdateInputsOnResume)
                {
                    return;
                }

                if (LastInputs.TryGetValue(input.Action, out var lastInput))
                {
                    if (lastInput.Button != input.Button)
                    {
                        InputsToSendOnResume[input.Action] = input;
                    }
                    else
                    {
                        InputsToSendOnResume.Remove(input.Action);
                    }
                }

                return;
            }

            LastInputs[input.Action] = input;

            double adjustedTime = GameManager.GetInputTime(input.Time);
            // Apply input offset
            adjustedTime += InputCalibration;
            input = new(adjustedTime, input.Action, input.Integer);

            // Allow the input to be explicitly ignored before processing it
            if (InterceptInput(ref input)) return;

            BaseEngine.QueueInput(ref input);
            OnInputQueued(input);
            _replayInputs.Add(input);
        }

        protected virtual void OnStarPowerPhraseHit()
        {
            if (!GameManager.Paused && !GameManager.IsSeekingReplay)
            {
                GlobalAudioHandler.PlaySoundEffect(SfxSample.StarPowerAward);
            }
        }

        protected virtual void OnStarPowerReady()
        {
            if (!GameManager.Paused && !GameManager.IsSeekingReplay)
            {
                GlobalAudioHandler.PlaySoundEffect(SfxSample.StarPowerReady);
            }
        }

        protected virtual void OnStarPowerPhraseMissed()
        {

        }

        protected virtual void OnStarPowerStatus(bool active)
        {
            var deploySample = SfxSample.StarPowerDeploy;
            if (SettingsManager.Settings.UseCrowdCheering.Value &&
                !GlobalVariables.State.CrowdSfxVenueOverride)
            {
                deploySample = SfxSample.StarPowerDeployCrowd;
            }

            // IsSeekingReplay covers a rewind's re-simulation too, which otherwise replays every
            // deploy and release of the discarded run at once.
            if (!GameManager.Paused && !GameManager.IsSeekingReplay)
            {
                GlobalAudioHandler.PlaySoundEffect(active
                    ? deploySample
                    : SfxSample.StarPowerRelease);

                SetStarPowerFX(active);
            }

            GameManager.ChangeStarPowerStatus(active);

            foreach (var haptics in SantrollerHaptics)
            {
                haptics.SetStarPowerActive(active);
            }
        }

        protected abstract bool InterceptInput(ref GameInput input);

        protected virtual void OnInputQueued(GameInput input)
        {
            if (InputViewer != null)
            {
                InputViewer.OnInput(input);
            }
        }

        protected void OnComboIncrement(int amount)
        {
            GameManager.AddBandCombo(amount);
        }

        protected void OnComboReset()
        {
            GameManager.ResetBandCombo();
        }

        public abstract (ReplayFrame Frame, ReplayStats Stats) ConstructReplayData();
    }
}
