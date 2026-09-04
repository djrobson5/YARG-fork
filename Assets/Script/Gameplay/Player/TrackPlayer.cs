using System;
using System.Collections.Generic;
using System.Linq;
using Cysharp.Threading.Tasks;
using UnityEngine;
using UnityEngine.Serialization;
using YARG.Assets.Script.Helpers;
using YARG.Core;
using YARG.Core.Audio;
using YARG.Core.Chart;
using YARG.Core.Engine;
using YARG.Core.Logging;
using YARG.Gameplay.HUD;
using YARG.Gameplay.SpPath;
using YARG.Gameplay.Visuals;
using YARG.Localization;
using YARG.Helpers;
using YARG.Helpers.Extensions;
using YARG.Playback;
using YARG.Player;
using YARG.Scores;
using YARG.Settings;
using YARG.Themes;

namespace YARG.Gameplay.Player
{
    public abstract class TrackPlayer : BasePlayer
    {
        public const float STRIKE_LINE_POS       = -2f;
        public const float DEFAULT_ZERO_FADE_POS = 3f;
        public const float NOTE_SPAWN_OFFSET     = 5f;

        public const float TRACK_WIDTH  = 2f;

        /// <summary>
        /// How far from a planned activation the player's own activation still counts as being on
        /// plan, in seconds. Wide enough to absorb a human tap and the engine's own loop
        /// granularity, narrow enough that a deliberately different activation is caught.
        /// </summary>
        private const double SP_PATH_ACTIVATION_GRACE = 0.25;

        /// <summary>
        /// The cap on the HUD chip's lead-in, so a very slow chart cannot leave the chip up half
        /// the song. Raised to the player's own lead-in whenever they ask for a longer one.
        /// </summary>
        private const double SP_CHIP_DEFAULT_MAX_LEAD_IN = 6.0;

        /// <summary>
        /// The HUD chip's lead-in, which is deliberately much longer than the highway tick's one
        /// beat: the chip is the only cue while the activation note is still beyond the spawn
        /// horizon, and a sub-second chip is imperceptible (editor test, 2026-09-04). The chip
        /// appears at the *earlier* of the previous measure line and this many seconds before the
        /// activation, but never more than <see cref="_spChipMaxLeadIn"/> seconds before it.
        /// </summary>
        /// <remarks>
        /// These three come from <c>Settings.StarPowerPathChipLeadIn</c> and
        /// <c>Settings.StarPowerPathChipHold</c>, read once in <see cref="OnStarPowerPathSet"/>
        /// like every other Star Power path setting — so a change made from the pause menu takes
        /// effect on the next song.
        /// </remarks>
        private double _spChipMinLeadIn = 3.0;

        private double _spChipMaxLeadIn = SP_CHIP_DEFAULT_MAX_LEAD_IN;

        /// How long the chip stays up past the grace window: a short beat of confirmation.
        private double _spChipHold = 0.75;

        public static int HighwayCount = 1;

        public double SpawnTimeOffset => (ZeroFadePosition + _spawnAheadDelay + -STRIKE_LINE_POS) / NoteSpeed;

        protected TrackView TrackView { get; private set; }

        [field: Header("Visuals")]
        [field: SerializeField]
        public Camera TrackCamera { get; private set; }

        [SerializeField]
        protected CameraPositioner CameraPositioner;
        [SerializeField]
        protected HighwayCameraRendering HighwayCameraRendering;
        [SerializeField]
        protected TrackMaterial TrackMaterial;
        [SerializeField]
        protected StrikelineAnimator StrikelineAnimator;
        [SerializeField]
        protected ComboMeter ComboMeter;
        [SerializeField]
        protected StarpowerBar StarpowerBar;
        [SerializeField]
        protected SunburstEffects SunburstEffects;
        [SerializeField]
        protected IndicatorStripes IndicatorStripes;
        [SerializeField]
        protected HitWindowDisplay HitWindowDisplay;

        [SerializeField]
        private Transform _hudLocation;

        [Header("Pools")]
        [SerializeField]
        protected KeyedPool NotePool;
        [SerializeField]
        protected Pool LanePool;
        [SerializeField]
        protected Pool BeatlinePool;
        [SerializeField]
        protected Pool EffectPool;

        public float ZeroFadePosition { get; private set; }
        public float FadeSize         { get; private set; }

        [field: Header("Star Power Trim Effect")]
        [SerializeField]
        protected StarPowerEffectElement StarPowerEffect;

        protected List<Beatline> Beatlines;

        protected int BeatlineIndex;

        /// <summary>
        /// Spawn cursor into <c>StarPowerPath.Activations</c>. Reset everywhere
        /// <see cref="BeatlineIndex"/> is (<c>docs/sp-path-design.md</c> §4.3).
        /// </summary>
        protected int SpPathIndex;

        /// Activations whose grace window the song has entered, and whose grace window it has left.
        /// The player's activation count has to sit between the two, or they are off-plan.
        private int _spPlanEarlyIndex;
        private int _spPlanLateIndex;

        /// Activations whose note the song has reached, and whose meter has therefore been checked
        /// against the plan's. Separate from the two above because it fires at the note itself, not
        /// at either edge of the grace window.
        private int _spPlanMeterIndex;

        /// How many Star Power phrases the engine has stripped this run, and when the last one
        /// went. Bookkeeping for the logs only - a stripped phrase is not itself a divergence
        /// (see <see cref="UpdateStarPowerPathDivergence"/>).
        private int    _spPhrasesLost;
        private double _spLastPhraseLostTime = double.NaN;

        /// Built on demand the first time a path arrives; null when the overlay is off.
        private Pool _spPathMarkerPool;

        /// <summary>
        /// Everything the highway and the HUD need to draw one activation, worked out once when
        /// the path arrives rather than per spawn: the beat the lead-in tick sits on, how long
        /// the band is, and which lanes the activation note occupies.
        /// </summary>
        protected readonly struct SpPathMarkerInfo
        {
            public readonly Activation Activation;
            public readonly double     LeadInTime;
            public readonly double     ChipLeadInTime;
            public readonly double     BandDuration;
            public readonly float[]    LaneXPositions;

            public SpPathMarkerInfo(Activation activation, double leadInTime, double chipLeadInTime,
                double bandDuration, float[] laneXPositions)
            {
                Activation = activation;
                LeadInTime = leadInTime;
                ChipLeadInTime = chipLeadInTime;
                BandDuration = bandDuration;
                LaneXPositions = laneXPositions;
            }
        }

        private readonly List<SpPathMarkerInfo> _spMarkers = new();

        /// <summary>
        /// Note indices the plan activates on, for the guaranteed-visible half of the cue: the
        /// activation note itself is recoloured green (2026-09-04). A set rather than a cursor
        /// because the spawn loop is re-entered after every seek and practice-section rebuild.
        /// </summary>
        private readonly HashSet<int> _spActivationNoteIndices = new();

        /// <summary>
        /// True while the note spawn loop is spawning the notes of an activation chord, so
        /// <c>SpawnNote</c> can flag every child of the chord, not just the parent.
        /// </summary>
        protected bool SpawningActivationNote;

        /// Cursor over <see cref="_spMarkers"/> for the HUD chip and the strike line glow. Unlike
        /// the spawn cursor this one tracks the *song* clock, not the highway's spawn horizon.
        private int _spHudIndex;

        /// The last countdown the chip was given, and the string it was formatted into, so the
        /// per-frame HUD update only allocates when the beat count moves.
        private int    _spCountdownBeats = -1;
        private string _spCountdownText;

        /// The steady green wash over the strike line while an activation is due. Built with the
        /// marker pool; null when the overlay is off or flashing reduction is on.
        private MeshRenderer _spFretGlow;

        /// How long the strike line glow is, in track units, and how strongly it is tinted.
        private const float SP_FRET_GLOW_LENGTH = 0.55f;
        private const float SP_FRET_GLOW_ALPHA  = 0.32f;

        protected bool IsBass { get; private set; }

        public int LaneCount { get; protected set; }

        private float _spawnAheadDelay;

        protected float SongLength;

        protected LaneElement[] BRELanes;

        public virtual void Initialize(int index, YargPlayer player, SongChart chart, TrackView trackView,
            StemMixer mixer, int? lastHighScore)
        {
            if (IsInitialized)
            {
                return;
            }

            Initialize(index, player, chart, lastHighScore);

            TrackView = trackView;

            Beatlines = SyncTrack.Beatlines;
            BeatlineIndex = 0;
            ResetStarPowerPathCursors();

            var preset = player.EnginePreset;
            IndicatorStripes.Initialize(preset);

            // Set fade information and highway length
            ZeroFadePosition = DEFAULT_ZERO_FADE_POS * Player.Profile.HighwayLength;
            FadeSize = Player.CameraPreset.FadeLength;

            _spawnAheadDelay = GameManager.IsPractice ? SettingsManager.Settings.PracticeRestartDelay.Value : 2;
            if (player.Profile.HighwayLength > 1)
            {
                FadeSize *= player.Profile.HighwayLength;
            }

            // Move the HUD location based on the highway length
            var change = ZeroFadePosition - DEFAULT_ZERO_FADE_POS;
            _hudLocation.position = _hudLocation.position.AddZ(change);

            // Must be done after the HUD location is set
            StarPowerEffect.Initialize();
            StarPowerEffect.gameObject.SetActive(false);

            // Determine if a track is bass or not for the BASS GROOVE text notification
            IsBass = Player.Profile.CurrentInstrument
                is Instrument.FiveFretBass
                or Instrument.SixFretBass
                or Instrument.ProBass_17Fret
                or Instrument.ProBass_22Fret;

            TrackView.ShowPlayerName(player);
        }

        protected override void OnSectionStateSet()
        {
            TrackView.SetSectionState(SectionState);
        }

        /// <summary>
        /// Puts the spawn cursor and the divergence cursors back to the start of the path, and
        /// clears the divergence flag with them.
        /// </summary>
        /// <remarks>
        /// The flag is derived from where the cursors are, so rewinding them and leaving it set
        /// would leave the two disagreeing. Every caller is a seek or a rebuild — the run in
        /// front of the player has not happened yet either way.
        /// </remarks>
        protected void ResetStarPowerPathCursors()
        {
            SpPathIndex = 0;
            _spPlanEarlyIndex = 0;
            _spPlanLateIndex = 0;
            _spPlanMeterIndex = 0;
            _spHudIndex = 0;
            _spPhrasesLost = 0;
            _spLastPhraseLostTime = double.NaN;
            SpPathDiverged = false;
        }

        protected override void OnStarPowerPathSet()
        {
            ResetStarPowerPathCursors();

            // A new path describes a run that has not happened yet, so nothing already on the
            // highway belongs to it.
            if (_spPathMarkerPool != null)
            {
                _spPathMarkerPool.ReturnAllObjects();
            }

            _spMarkers.Clear();
            _spActivationNoteIndices.Clear();
            SpawningActivationNote = false;
            if (TrackView != null)
            {
                TrackView.SetStarPowerPathChip(false, null);
            }

            ShowStarPowerPathGlow(false);

            if (StarPowerPath is null || StarPowerPath.Activations.Count == 0)
            {
                return;
            }

            ReadStarPowerPathSettings();

            BuildStarPowerPathMarkerInfos();

            if (_spPathMarkerPool == null)
            {
                _spPathMarkerPool = SpPathMarkerElement.CreateRuntimePool(BeatlinePool);
            }

            // A steady wash, not a strobe, skipped when the player has turned it off, and never
            // built at all when they have asked for less flashing — the accessibility setting
            // wins over the cue's own toggle. Built once and toggled, so both are read here only.
            if (_spFretGlow == null &&
                SettingsManager.Settings.StarPowerPathFretGlow.Value &&
                !SettingsManager.Settings.ReduceFlashingLights.Value)
            {
                _spFretGlow = SpPathMarkerElement.CreateStrikeLineGlow(BeatlinePool,
                    SP_FRET_GLOW_LENGTH, SP_FRET_GLOW_ALPHA);
            }

            YargLogger.LogInfo(
                $"SP path: {_spMarkers.Count} activation(s) to draw, " +
                $"pool {(_spPathMarkerPool != null ? "ready" : "MISSING")}, " +
                $"glow {(_spFretGlow != null ? "ready" : "off")}, " +
                $"spawn offset {SpawnTimeOffset:0.000}s");
        }

        /// <summary>
        /// Pulls the player's Star Power path customisations in, once per path.
        /// </summary>
        /// <remarks>
        /// The colour goes into <see cref="SpPathMarkerElement"/>, which every part of the cue
        /// reads — the highway geometry, the recoloured activation note and the HUD chip — and
        /// the two chip timings into the fields the HUD update runs on. The 6 s cap holds unless
        /// the player has asked for a longer lead-in than that, in which case their value is the
        /// cap: capping a lead-in below what was explicitly asked for would silently ignore the
        /// setting.
        /// </remarks>
        private void ReadStarPowerPathSettings()
        {
            var settings = SettingsManager.Settings;

            SpPathMarkerElement.SetCueColor(settings.StarPowerPathColor.Value);

            _spChipMinLeadIn = settings.StarPowerPathChipLeadIn.Value;
            _spChipMaxLeadIn = Math.Max(SP_CHIP_DEFAULT_MAX_LEAD_IN, _spChipMinLeadIn);
            _spChipHold = settings.StarPowerPathChipHold.Value;
        }

        /// <summary>
        /// Works out the lead-in beat, the band length and the lanes to ring for every
        /// activation, once.
        /// </summary>
        /// <remarks>
        /// The lead-in is the last beatline strictly before the activation, so it is one beat on
        /// a chart whose activation lands on a beat and up to two on one where it does not — the
        /// short lead-in the redesign locks, not the whole Star Power window. Beat timing comes
        /// from the chart's own beatlines rather than from the optimizer, which is deliberately
        /// Unity-free and has no business in the rendering layer.
        /// </remarks>
        private void BuildStarPowerPathMarkerInfos()
        {
            var lanes = new List<float>(5);

            foreach (var activation in StarPowerPath.Activations)
            {
                double time = activation.ActivationTime;

                // Last beat strictly before the activation, with a small epsilon so a beat the
                // activation sits exactly on is not mistaken for the lead-in.
                int index = FindLastBeatlineBefore(time - 0.01);
                double leadIn = index >= 0 ? Beatlines[index].Time : time - 0.5;

                // One beat, measured at the activation, so the band is a beat long on any tempo.
                double beat = 0.5;
                if (index >= 0 && index + 1 < Beatlines.Count)
                {
                    beat = Beatlines[index + 1].Time - Beatlines[index].Time;
                }

                // The chip's own lead-in is decoupled from the highway tick's: whichever of the
                // measure line before the activation and the player's chosen lead-in is
                // *earlier*, then capped so a slow chart cannot leave the chip up indefinitely.
                int measureIndex = FindLastMeasureLineBefore(time - 0.01);
                double measureLeadIn = measureIndex >= 0
                    ? Beatlines[measureIndex].Time
                    : time - _spChipMinLeadIn;
                double chipLeadIn = Math.Max(time - _spChipMaxLeadIn,
                    Math.Min(measureLeadIn, time - _spChipMinLeadIn));

                lanes.Clear();
                GetActivationLaneXPositions(activation.NoteIndex, lanes);

                _spMarkers.Add(new SpPathMarkerInfo(activation, leadIn, chipLeadIn,
                    Math.Clamp(beat, 0.1, 2.0), lanes.ToArray()));

                _spActivationNoteIndices.Add(activation.NoteIndex);
            }
        }

        /// <summary>
        /// Whether the plan activates on the note at <paramref name="noteIndex"/> — the note the
        /// spawn loop recolours to the activation green.
        /// </summary>
        protected bool IsStarPowerPathActivationNote(int noteIndex)
        {
            return _spActivationNoteIndices.Count != 0 &&
                _spActivationNoteIndices.Contains(noteIndex);
        }

        /// <summary>
        /// Index of the last beatline at or before <paramref name="time"/>, or <c>-1</c>.
        /// </summary>
        private int FindLastBeatlineBefore(double time)
        {
            if (Beatlines is null || Beatlines.Count == 0 || Beatlines[0].Time > time)
            {
                return -1;
            }

            int low = 0;
            int high = Beatlines.Count - 1;
            while (low < high)
            {
                int mid = (low + high + 1) / 2;
                if (Beatlines[mid].Time <= time)
                {
                    low = mid;
                }
                else
                {
                    high = mid - 1;
                }
            }

            return low;
        }

        /// <summary>
        /// Index of the last <see cref="BeatlineType.Measure"/> line at or before
        /// <paramref name="time"/>, or <c>-1</c> if the activation is in the song's first measure.
        /// </summary>
        private int FindLastMeasureLineBefore(double time)
        {
            for (int i = FindLastBeatlineBefore(time); i >= 0; i--)
            {
                if (Beatlines[i].Type == BeatlineType.Measure)
                {
                    return i;
                }
            }

            return -1;
        }

        /// <summary>
        /// Fills <paramref name="xPositions"/> with the highway X of every lane the activation
        /// note occupies. The default is empty — only instruments with a Star Power path override
        /// it, and a full-width or open note contributes nothing, because the band already spans
        /// everything a ring could.
        /// </summary>
        protected virtual void GetActivationLaneXPositions(int noteIndex, List<float> xPositions)
        {
        }

        /// <summary>
        /// Spawns the markers whose activation note is close enough to the highway's far end,
        /// exactly as <c>UpdateBeatlines</c> does.
        /// </summary>
        protected void UpdateStarPowerPathMarkers(double time)
        {
            if (_spPathMarkerPool == null || _spMarkers.Count == 0)
            {
                return;
            }

            while (SpPathIndex < _spMarkers.Count &&
                _spMarkers[SpPathIndex].Activation.ActivationTime <= time + SpawnTimeOffset)
            {
                // Skip this frame if the pool is full
                if (!_spPathMarkerPool.CanSpawnAmount(1))
                {
                    break;
                }

                var poolable = _spPathMarkerPool.TakeWithoutEnabling();
                if (poolable == null)
                {
                    YargLogger.LogWarning("Attempted to spawn a Star Power path marker, " +
                        "but it's at its cap!");
                    break;
                }

                var info = _spMarkers[SpPathIndex];

                var marker = (SpPathMarkerElement) poolable;
                marker.ActivationRef = info.Activation;
                marker.LeadInTime = info.LeadInTime;
                marker.BandDuration = info.BandDuration;
                marker.RingLaneXPositions = info.LaneXPositions;
                marker.EnableFromPool();

                YargLogger.LogInfo(
                    $"SP path: spawned marker {SpPathIndex + 1}/{_spMarkers.Count} at " +
                    $"t={info.Activation.ActivationTime:0.000}s " +
                    $"(lead-in {info.LeadInTime:0.000}s, band {info.BandDuration:0.000}s, " +
                    $"{info.LaneXPositions.Length} lane(s))");

                SpPathIndex++;
            }
        }

        /// <summary>
        /// Drives the temporal half of the cue: the HUD chip through the lead-in, and the steady
        /// strike line glow while the activation itself is due.
        /// </summary>
        protected void UpdateStarPowerPathHud()
        {
            // TrackView is guarded here for the same reason OnStarPowerPathSet guards it: the
            // markers exist whether or not there is a view to put a chip in, and this runs every
            // frame.
            if (_spMarkers.Count == 0 || TrackView == null)
            {
                return;
            }

            double time = GameManager.SongTime;

            // The cursor holds on to an activation past its grace window so the chip can sit on
            // ACTIVATE for a moment, and lets go early if the next activation's own
            // chip lead-in has already started — overlapping windows are rare, and the nearer
            // activation is the useful one.
            while (_spHudIndex < _spMarkers.Count)
            {
                var current = _spMarkers[_spHudIndex];
                double graceEnd = current.Activation.ActivationTime + SP_PATH_ACTIVATION_GRACE;
                if (time <= graceEnd)
                {
                    break;
                }

                bool held = time <= graceEnd + ChipHoldDuration;
                bool nextIsDue = _spHudIndex + 1 < _spMarkers.Count &&
                    time >= _spMarkers[_spHudIndex + 1].ChipLeadInTime;
                if (held && !nextIsDue)
                {
                    break;
                }

                _spHudIndex++;
            }

            if (_spHudIndex >= _spMarkers.Count)
            {
                TrackView.SetStarPowerPathChip(false, null);
                ShowStarPowerPathGlow(false);
                return;
            }

            var info = _spMarkers[_spHudIndex];
            double activationTime = info.Activation.ActivationTime;
            bool atActivation = time >= activationTime - SP_PATH_ACTIVATION_GRACE &&
                time <= activationTime + SP_PATH_ACTIVATION_GRACE;

            // Steady, never strobing, and on exactly the window it always was — activation
            // through grace. Deliberately independent of SpPathDiverged (2026-09-04): the cue
            // stays at full strength for the whole song whatever the player does.
            ShowStarPowerPathGlow(atActivation);

            // The chip runs on its own, much longer window: from its lead-in through the grace
            // window and a short hold past it.
            if (time < info.ChipLeadInTime ||
                time > activationTime + SP_PATH_ACTIVATION_GRACE + ChipHoldDuration)
            {
                TrackView.SetStarPowerPathChip(false, null);
                return;
            }

            string text;
            if (time >= activationTime - SP_PATH_ACTIVATION_GRACE)
            {
                text = Localize.Key("Gameplay.StarPowerPath.ActivateNow");
            }
            else
            {
                // Formatted only when the count actually moves: this runs every frame, and
                // KeyFormat allocates a string on every call.
                int beats = BeatsUntil(time, info.Activation.ActivationTime);
                if (beats != _spCountdownBeats || _spCountdownText is null)
                {
                    _spCountdownBeats = beats;
                    _spCountdownText = Localize.KeyFormat("Gameplay.StarPowerPath.ActivateIn", beats);
                }

                text = _spCountdownText;
            }

            TrackView.SetStarPowerPathChip(true, text);
        }

        /// <summary>
        /// How long the chip lingers past the grace window — just long enough to confirm the
        /// activation. There used to be a longer, OFF PLAN hold; the off-plan state is gone
        /// (2026-09-04), so there is one duration left, and the player sets it.
        /// </summary>
        private double ChipHoldDuration => _spChipHold;

        /// <summary>Whole beats left between <paramref name="from"/> and <paramref name="to"/>.</summary>
        private int BeatsUntil(double from, double to)
        {
            int start = FindLastBeatlineBefore(from);
            int end = FindLastBeatlineBefore(to - 0.01);
            if (start < 0 || end < 0)
            {
                return 1;
            }

            return Math.Max(1, end - start + 1);
        }

        private void ShowStarPowerPathGlow(bool show)
        {
            if (_spFretGlow == null || _spFretGlow.gameObject.activeSelf == show)
            {
                return;
            }

            _spFretGlow.gameObject.SetActive(show);
        }

        /// <summary>
        /// Flips <see cref="BasePlayer.SpPathDiverged"/> the first time the player's actual Star
        /// Power state stops matching the plan (<c>docs/sp-path-design.md</c> §4.4).
        /// </summary>
        /// <remarks>
        /// <b>Log-only since 2026-09-04.</b> The flag is still computed and every transition is
        /// still logged, because it is the cheapest check of the Star Power model against the live
        /// engine — but nothing visual reads it any more. At the user's instruction the whole cue
        /// is shown at full brightness for the entire song whatever the player does: the path is
        /// information about where the *next* run should activate, so it is worth the same after a
        /// dropped phrase as before one.
        /// <para/>
        /// Only Star Power state counts, not score. An ordinary missed note costs points but
        /// leaves the plan followable, so it does not dim anything. Three things do: an activation
        /// the plan does not call for yet, a planned activation going by without one, and the
        /// player's meter being short of what the plan spends at an activation. The first two are
        /// counted rather than compared tick by tick, because <c>StarPowerActivationCount</c> is
        /// the only thing that says an activation happened at all. Never un-set — only a rebuilt
        /// path clears it.
        /// <para/>
        /// <b>A stripped Star Power phrase is deliberately not one of them (2026-09-04).</b> It
        /// used to dim on the spot, straight off <c>OnStarPowerPhraseMissed</c>. The event itself
        /// is engine truth — <c>StripStarPower</c> only runs on a real miss, a real overstrum, or
        /// the unused <c>NoStarPowerOverlap</c> rule — but the inference drawn from it was not.
        /// The plan is only dead if the meter it spends is no longer there, and the player's real
        /// meter is fed by sources the model does not carry. Unison bonuses are the big one
        /// (<c>BaseEngine.AwardUnisonBonus</c>, a free quarter bar for every unison phrase all
        /// participants clear), and on a chart with several of them the player can be a whole bar
        /// ahead of the plan and lose a phrase for free. So the loss is logged and the verdict is
        /// left to the meter check at the activation itself, which measures the thing that
        /// actually decides whether the marker can be followed.
        /// </remarks>
        protected void UpdateStarPowerPathDivergence()
        {
            if (StarPowerPath is null || SpPathDiverged)
            {
                return;
            }

            var activations = StarPowerPath.Activations;

            // SongTime, not InputTime: the plan's activation times are chart times, and SongTime
            // is the same clock. InputTime leads it by the calibration offset, which would shift
            // both grace windows by that offset.
            double time = GameManager.SongTime;

            while (_spPlanEarlyIndex < activations.Count &&
                time >= activations[_spPlanEarlyIndex].ActivationTime - SP_PATH_ACTIVATION_GRACE)
            {
                _spPlanEarlyIndex++;
            }

            while (_spPlanLateIndex < activations.Count &&
                time >= activations[_spPlanLateIndex].ActivationTime + SP_PATH_ACTIVATION_GRACE)
            {
                _spPlanLateIndex++;
            }

            int activated = BaseStats.StarPowerActivationCount;

            if (activated > _spPlanEarlyIndex)
            {
                SetStarPowerPathDiverged("Star Power was activated off-plan");
                return;
            }

            if (activated < _spPlanLateIndex)
            {
                SetStarPowerPathDiverged("a planned activation was not taken");
                return;
            }

            CheckStarPowerPathMeter(time, activations);
        }

        /// <summary>
        /// The meter half of the divergence rule (<c>docs/sp-path-design.md</c> §4.4, third
        /// bullet): at each activation's own note, the engine's banked Star Power has to be at
        /// least what the plan spends there.
        /// </summary>
        /// <remarks>
        /// Evaluated at <c>ActivationTime</c> rather than at the start of the grace window, so a
        /// phrase landing in the last quarter second before the marker still counts — that is the
        /// latest moment at which the verdict is still worth anything, and the one the design doc
        /// names. Skipped entirely while Star Power is running: the amount is then a draining
        /// window rather than a bank, which is exactly what it looks like when the player took
        /// this very activation early inside its grace window, or when a previous window is still
        /// open (a case the two count rules above already own).
        /// </remarks>
        private void CheckStarPowerPathMeter(double time, IReadOnlyList<Activation> activations)
        {
            uint ticksPerQuarter = StarPowerPath.TicksPerQuarterSpBar;

            while (_spPlanMeterIndex < activations.Count &&
                time >= activations[_spPlanMeterIndex].ActivationTime)
            {
                var activation = activations[_spPlanMeterIndex];
                _spPlanMeterIndex++;

                if (ticksPerQuarter == 0 || BaseStats.IsStarPowerActive)
                {
                    continue;
                }

                uint needed = (uint) activation.MeterAtActivation * ticksPerQuarter;
                if (BaseStats.StarPowerTickAmount >= needed)
                {
                    continue;
                }

                SetStarPowerPathDiverged(
                    $"the meter is short at planned activation {_spPlanMeterIndex} — " +
                    $"{BaseStats.StarPowerTickAmount} tick(s) banked, {needed} needed " +
                    $"({activation.MeterAtActivation}/4 bar)");
                return;
            }
        }

        /// <summary>
        /// Records a Star Power phrase the engine stripped. Not a divergence on its own — see the
        /// remarks on <see cref="UpdateStarPowerPathDivergence"/> — but the single most useful
        /// thing to have in the log when a later meter shortfall has to be explained.
        /// </summary>
        protected void NoteStarPowerPhraseLost(uint noteTick, double noteTime, bool noteWasMissed)
        {
            _spPhrasesLost++;
            _spLastPhraseLostTime = GameManager.SongTime;

            if (StarPowerPath is null)
            {
                return;
            }

            YargLogger.LogInfo(
                $"SP path: Star Power phrase stripped at note tick {noteTick} " +
                $"({noteTime:0.000}s) by {(noteWasMissed ? "a missed note" : "an overstrum")} — " +
                $"{_spPhrasesLost} lost this run. Not a divergence on its own; the plan only dims " +
                $"if the meter is short at an activation. " +
                $"({Player.Profile.CurrentInstrument}, {GameManager.SongTime:0.000}s; " +
                $"{DescribeStarPowerState()})");
        }

        /// <summary>
        /// Marks the run as off-plan, once. Safe to call from anywhere, including players with no
        /// path at all.
        /// </summary>
        protected void SetStarPowerPathDiverged(string reason)
        {
            if (StarPowerPath is null || SpPathDiverged)
            {
                return;
            }

            SpPathDiverged = true;
            YargLogger.LogInfo(
                $"SP path: diverged — {reason}. Diagnostic only: nothing on screen changes, the " +
                $"cue stays at full brightness for the rest of the run. " +
                $"({Player.Profile.CurrentInstrument}, {GameManager.SongTime:0.000}s; " +
                $"{DescribeStarPowerState()}; {DescribeStarPowerPlanState()})");
        }

        /// <summary>The engine's live Star Power state, for the divergence and phrase-loss logs.</summary>
        private string DescribeStarPowerState()
        {
            uint ticksPerQuarter = StarPowerPath?.TicksPerQuarterSpBar ?? 0;
            double quarterBars = ticksPerQuarter == 0
                ? 0
                : BaseStats.StarPowerTickAmount / (double) ticksPerQuarter;

            string lastLoss = double.IsNaN(_spLastPhraseLostTime)
                ? string.Empty
                : $", last at {_spLastPhraseLostTime:0.000}s";

            return $"engine: phrases hit {BaseStats.StarPowerPhrasesHit}/" +
                $"{BaseStats.TotalStarPowerPhrases} (missed {BaseStats.StarPowerPhrasesMissed}, " +
                $"stripped this run {_spPhrasesLost}{lastLoss}), " +
                $"meter {BaseStats.StarPowerTickAmount} tick(s) = {quarterBars:0.00}/4 bar, " +
                $"active {BaseStats.IsStarPowerActive}, " +
                $"activations {BaseStats.StarPowerActivationCount}, " +
                $"whammy ticks {BaseStats.StarPowerWhammyTicks}, " +
                $"total earned {BaseStats.TotalStarPowerTicks} tick(s)";
        }

        /// <summary>Where the plan thinks the run is, for the divergence log.</summary>
        private string DescribeStarPowerPlanState()
        {
            var activations = StarPowerPath.Activations;
            string next = "none left";
            if (_spPlanLateIndex < activations.Count)
            {
                var activation = activations[_spPlanLateIndex];
                next = $"#{_spPlanLateIndex + 1} on note {activation.NoteIndex} at " +
                    $"tick {activation.ActivationTick} ({activation.ActivationTime:0.000}s) " +
                    $"spending {activation.MeterAtActivation}/4 bar, window " +
                    $"[{activation.ActivationMeasureTick}, {activation.EndMeasureTick})";
            }

            return $"plan: {activations.Count} activation(s), " +
                $"{StarPowerPath.PhraseEndTicks.Count} modelled phrase(s), cursors early " +
                $"{_spPlanEarlyIndex} / late {_spPlanLateIndex} / meter {_spPlanMeterIndex}, " +
                $"next {next}";
        }

        protected override void ResetVisuals()
        {
            // "Muting a stem" isn't technically a visual,
            // but it's a form of feedback so we'll put it here.
            SetStemMuteState(false);

            ComboMeter.SetFullCombo(IsFc);
            TrackView.ForceReset();
            GameManager.ResetCoda();

            NotePool.ReturnAllObjects();
            LanePool.ReturnAllObjects();
            BeatlinePool.ReturnAllObjects();

            if (_spPathMarkerPool != null)
            {
                _spPathMarkerPool.ReturnAllObjects();
            }

            ShowStarPowerPathGlow(false);

            HitWindowDisplay.SetHitWindowSize();
        }
    }

    public abstract class TrackPlayer<TEngine, TNote> : TrackPlayer
        where TEngine : BaseEngine
        where TNote : Note<TNote>
    {
        public TEngine Engine { get; private set; }

        public override BaseEngine BaseEngine => Engine;

        protected List<TNote> Notes { get; set; }

        protected int NoteIndex { get; private set; }

        public InstrumentDifficulty<TNote> NoteTrack { get; private set; }

        private InstrumentDifficulty<TNote> OriginalNoteTrack { get; set; }

        private int _currentMultiplier;
        private int _previousMultiplier;

        private bool _isHotStartChecked;
        private bool _previousBassGrooveState;
        private bool _newHighScoreShown;

        private double _previousStarPowerAmount;

        private bool _wasStarPowerActive;
        private bool _didLowerTrack;

        private Queue<TrackEffect> _upcomingEffects = new();
        private List<TrackEffectElement> _currentEffects = new();
        protected List<TrackEffect> _trackEffects = new();

        private List<Phrase> _brePhrases = new();
        private int _breIndex;

        private List<EngineManager.UnisonPhrase> _unisonPhrases = new();
        private int                              _unisonStartIndex;
        private int                              _unisonEndIndex;

        protected SongChart Chart;

        private AutoCalibrator _autoCalibrator;

        protected CodaSection CurrentCoda;

        public override void Initialize(int index, YargPlayer player, SongChart chart, TrackView trackView,
            StemMixer mixer, int? currentHighScore)
        {
            if (IsInitialized)
            {
                return;
            }

            // Get player count
            if (index == 0)
            {
                // Reset
                HighwayCount = 1;
            }
            else if (index + 1 > HighwayCount)
            {
                HighwayCount = index + 1;
            }

            // Consolidate tracks into a parent object for animation purposes
            transform.SetParent(GameObject.Find("Visuals").transform);

            base.Initialize(index, player, chart, trackView, mixer, currentHighScore);

            SetupTheme();

            Chart = chart;

            OriginalNoteTrack = GetNotes(chart);
            player.Profile.ApplyModifiers(OriginalNoteTrack, chart.SyncTrack);

            NoteTrack = OriginalNoteTrack;
            Notes = NoteTrack.Notes;

            var events = NoteTrack.TextEvents;

            Engine = CreateEngine();
            base.ComboMeter.Initialize(player.EnginePreset, Engine.BaseParameters.MaxMultiplier, GameManager.Players.Count > 1);

            Engine.OnComboIncrement += OnComboIncrement;
            Engine.OnComboReset += OnComboReset;
            if (GameManager.IsPractice)
            {
                Engine.SetSpeed(GameManager.SongSpeed >= 1 ? GameManager.SongSpeed : 1);
            }
            else if (Player.IsReplay)
            {
                // If it's a replay, the "SongSpeed" parameter should be set properly
                // when it gets deserialized. Transfer this over to the engine.
                Engine.SetSpeed(Player.EngineParameterOverride.SongSpeed);
            }
            else
            {
                Engine.SetSpeed(GameManager.SongSpeed);
            }

            GameManager.BeatEventHandler.Visual.Subscribe(SunburstEffects.PulseSunburst, BeatEventType.StrongBeat);
            InitializeTrackEffects();
            InitializeCodaEvents();
            InitializeUnisonEvents();

            ResetNoteCounters();

            FinishInitialization();

            SongLength = (float) chart.GetEndTime();

            _autoCalibrator = new AutoCalibrator(GameManager);
        }

        protected override void FinishDestruction()
        {
            GameManager.BeatEventHandler.Visual.Unsubscribe(SunburstEffects.PulseSunburst);

            _autoCalibrator?.Dispose();

            base.FinishDestruction();
        }

        private void InitializeCodaEvents()
        {
            foreach (var phrase in NoteTrack.Phrases)
            {
                if (phrase.Type == PhraseType.BigRockEnding)
                {
                    _brePhrases.Add(phrase);
                }
            }
        }

        private void InitializeUnisonEvents()
        {
            _unisonStartIndex = 0;
            _unisonEndIndex = 0;
            _unisonPhrases = EngineContainer.UnisonPhrases;
        }

        private void InitializeTrackEffects()
        {

            // If the user doesn't want track effects, generate no effects
            if (!SettingsManager.Settings.EnableTrackEffects.Value)
            {
                return;
            }

            var phrases = new List<Phrase>();

            foreach (var phrase in NoteTrack.Phrases)
            {
                // We only want solo and drum fill here. Unisons are added later
                // and there are no track effects for the other phrase types
                if (phrase.Type is PhraseType.Solo or PhraseType.DrumFill)
                {
                    // It turns out that some charts have drum fill phrases that aren't SP activation
                    // (they have no notes), so we need to ignore those
                    if (phrase.Type is PhraseType.DrumFill)
                    {
                        foreach (var note in Notes)
                        {
                            if (note.Time >= phrase.Time && note.Time <= phrase.TimeEnd)
                            {
                                phrases.Add(phrase);
                                break;
                            }
                        }
                    }
                    else
                    {
                        phrases.Add(phrase);
                    }
                }
            }

            phrases.AddRange(EngineContainer.UnisonPhrases);

            var effects = TrackEffect.PhrasesToEffects(Notes, phrases);
            _trackEffects.AddRange(effects);
        }

        private void FinalizeTrackEffects()
        {
            foreach (var effect in TrackEffect.SliceEffects(NoteSpeed, _trackEffects))
            {
                _upcomingEffects.Enqueue(effect);
            }
        }

        private void SetupTheme()
        {
            var (gameMode, instrument) = (Player.Profile.GameMode, Player.Profile.CurrentInstrument);

            var style = VisualStyleHelpers.GetVisualStyle(gameMode, instrument);

            var themePrefab = ThemeManager.Instance.CreateNotePrefabFromTheme(
                Player.ThemePreset, style, NotePool.Prefab);
            NotePool.SetPrefabAndReset(themePrefab);
        }

        protected abstract InstrumentDifficulty<TNote> GetNotes(SongChart chart);
        protected abstract TEngine CreateEngine();

        protected virtual void FinishInitialization()
        {
            TrackMaterial.Initialize(Player.HighwayPreset);
            CameraPositioner.Initialize(Player.CameraPreset);
            FinalizeTrackEffects();

            GameManager.EngineManager.OnPlayerFailed += OnPlayerFailed;
            GameManager.EngineManager.OnPlayerRevived += OnPlayerRevived;
        }

        protected void ResetNoteCounters()
        {
            NoteIndex = 0;
            TotalNotes = Notes.Where(n => !n.IsBigRockEnding).Sum(i => Engine.GetNumberOfNotes(i));
        }

        public override void ResetPracticeSection()
        {
            Engine.Reset(true);

            if (NoteTrack.Notes.Count > 0)
            {
                NoteTrack.Notes[0].OverridePreviousNote();
                NoteTrack.Notes[^1].OverrideNextNote();
            }

            BeatlineIndex = 0;
            ResetStarPowerPathCursors();
            ResetNoteCounters();

            ResetTrackEffectOverlay(0);

            CurrentCoda = null;
            _breIndex = 0;
            _unisonStartIndex = 0;
            _unisonEndIndex = 0;

            ResetLastHitTimes();

            base.ResetPracticeSection();
        }

        protected virtual void ResetLastHitTimes()
        {

        }

        public override void Rewind(double visualTime)
        {
            for (int index = NotePool.AllSpawned.Count - 1; index >= 0; index--)
            {
                var poolable = NotePool.AllSpawned[index];
                if (poolable is INoteElement note)
                {
                    note.OnRewind();
                }
            }
        }

        public override void PostRewind(double visualTime)
        {

        }

        protected override void UpdateVisuals(double visualTime)
        {
            // Allow the HUD to track the highway with animations
            TrackView.UpdateHUDPosition(HighwayIndex, HighwayCount);

            UpdateNotes(visualTime);
            UpdateBeatlines(visualTime);
            UpdateStarPowerPathMarkers(visualTime);
            UpdateStarPowerPathDivergence();
            UpdateStarPowerPathHud();
            UpdateTrackEffects(visualTime);
            UpdateCodaEvents(visualTime);
            UpdateUnisonEvents(visualTime);

            var stats = Engine.BaseStats;

            int maxMultiplier = Engine.BaseParameters.MaxMultiplier;
            if (stats.IsStarPowerActive)
            {
                maxMultiplier *= 2;
            }

            double currentStarPowerAmount = Engine.GetStarPowerBarAmount();

            bool groove = stats.ScoreMultiplier == maxMultiplier;

            _currentMultiplier = stats.ScoreMultiplier;

            TrackMaterial.SetTrackScroll(visualTime, NoteSpeed);
            TrackMaterial.GrooveMode = groove;
            TrackMaterial.StarpowerMode = stats.IsStarPowerActive;

            // In multiplayer, don't double the score multiplier in the strikeline element
            // Otherwise, it looks like the band multiplier applies on top of the score multiplier
            int displayMultiplier = GameManager.TotalPlayers > 1 && stats.IsStarPowerActive
                ? stats.ScoreMultiplier / 2
                : stats.ScoreMultiplier;

            ComboMeter.SetCombo(stats.ScoreMultiplier, displayMultiplier, maxMultiplier, stats.Combo, Engine.CodaHasStarted);
            StarpowerBar.SetStarpower(currentStarPowerAmount, stats.IsStarPowerActive, Engine.CodaHasStarted);
            StarpowerBar.UpdateFlash(GameManager.BeatEventHandler.Visual.StrongBeat.CurrentPercentage);
            SunburstEffects.SetSunburstEffects(groove, stats.IsStarPowerActive, _currentMultiplier);

            TrackView.UpdateNoteStreak(stats.Combo);


            // Could be if (!_isHotStartChecked && groove), but that would make it so hot start doesn't show
            // for bass until 6x.
            if (!_isHotStartChecked && stats.ScoreMultiplier == (!stats.IsStarPowerActive ? 4 : 8))
            {
                _isHotStartChecked = true;

                if (IsFc)
                {
                    TrackView.ShowHotStart();
                }
            }

            bool currentBassGrooveState = IsBass && groove;

            if (!_previousBassGrooveState && currentBassGrooveState)
            {
                TrackView.ShowBassGroove();
            }

            _previousBassGrooveState = currentBassGrooveState;

            if (stats.IsStarPowerActive && !_wasStarPowerActive && !_didLowerTrack)
            {
                CameraPositioner.Scoop();
            }

            _previousStarPowerAmount = currentStarPowerAmount;
            _wasStarPowerActive = stats.IsStarPowerActive;

            foreach (var haptics in SantrollerHaptics)
            {
                haptics.SetStarPowerFill((float) currentStarPowerAmount);
            }

            bool isSongEnd = visualTime > SongLength;
            bool shouldLowerTrack = isSongEnd || GameManager.PlayerHasFailed;
            if (!_didLowerTrack && shouldLowerTrack)
            {
                _didLowerTrack = true;
                CameraPositioner.Lower(isSongEnd);
            }
            else if (_didLowerTrack && !shouldLowerTrack)
            {
                _didLowerTrack = false;
                CameraPositioner.Raise(false);
            }
        }

        private void UpdateNotes(double visualTime)
        {
            while (NoteIndex < Notes.Count && Notes[NoteIndex].Time <= visualTime + SpawnTimeOffset)
            {
                var note = Notes[NoteIndex];

                // Skip this frame if the pool is full or note is part of a BRE
                if (!NotePool.CanSpawnAmount(note.ChildNotes.Count + 1))
                {
                    break;
                }

                int spawnedNoteIndex = NoteIndex;
                NoteIndex++;

                // Don't spawn the note if it is under a BRE
                if (note.IsBigRockEnding)
                {
                    continue;
                }

                OnNoteSpawned(note);

                // Don't spawn hit or missed notes
                if (note.WasHit || note.WasMissed)
                {
                    continue;
                }

                // Every note of an activation chord is recoloured, so the flag is set for the
                // whole chord rather than per child.
                SpawningActivationNote = IsStarPowerPathActivationNote(spawnedNoteIndex);

                // Spawn all of the notes and child notes
                foreach (var child in note.AllNotes)
                {
                    SpawnNote(child);
                }

                SpawningActivationNote = false;
            }
        }

        private void UpdateBeatlines(double time)
        {
            while (BeatlineIndex < Beatlines.Count && Beatlines[BeatlineIndex].Time <= time + SpawnTimeOffset)
            {
                if (BeatlineIndex + 1 < Beatlines.Count && Beatlines[BeatlineIndex + 1].Time <= time + SpawnTimeOffset)
                {
                    BeatlineIndex++;
                    continue;
                }

                var beatline = Beatlines[BeatlineIndex];

                if (Notes.Count > 0 && beatline.Time > Notes[^1].TimeEnd)
                {
                    return;
                }

                // Skip this frame if the pool is full
                if (!BeatlinePool.CanSpawnAmount(1))
                {
                    break;
                }

                var poolable = BeatlinePool.TakeWithoutEnabling();
                if (poolable == null)
                {
                    YargLogger.LogWarning("Attempted to spawn beatline, but it's at its cap!");
                    break;
                }

                ((BeatlineElement) poolable).BeatlineRef = beatline;
                poolable.EnableFromPool();

                BeatlineIndex++;
            }
        }

        private void UpdateCodaEvents(double time)
        {
            while (_breIndex < _brePhrases.Count && _brePhrases[_breIndex].Time <= time + SpawnTimeOffset)
            {

                var phrase = _brePhrases[_breIndex];
                _breIndex++;

                StartBRE(phrase.Time, phrase.TimeEnd);
            }
        }

        private void UpdateUnisonEvents(double time)
        {
            if (_unisonStartIndex < _unisonPhrases.Count && _unisonPhrases[_unisonStartIndex].Time <= time)
            {
                OnUnisonStart();
                _unisonStartIndex++;
            }

            if (_unisonEndIndex < _unisonPhrases.Count && _unisonPhrases[_unisonEndIndex].TimeEnd <= time)
            {
                OnUnisonEnd();
                _unisonEndIndex++;
            }
        }

        private void UpdateTrackEffects(double time)
        {
            if (_upcomingEffects.TryPeek(out var nextEffect) && nextEffect.Time <= time + SpawnTimeOffset)
            {
                SpawnEffect(nextEffect, false);
            }

            // If any of the current effects are drum fill, we need to react
            // when starpower goes from unavailable to available

            // Remove past effects from current list
            // This may actually fail if an effect is reused from the pool
            // too quickly, but as long as it is only being used for setting
            // drum fill visibility, it shouldn't break.
            for (var i = 0; i < _currentEffects.Count; i++)
            {
                var trackEffectElement = _currentEffects[i];
                if (!trackEffectElement.Active)
                {
                    _currentEffects.RemoveAt(i);
                }
                else
                {
                    // See if it's an invisible drum fill and if starpower has become available
                    // Since we never change visibility on anything but drum fills, there's no need to check
                    // the effect type.
                    if ((trackEffectElement.Visibility < 1.0f && Engine.CanStarPowerActivate) && !Engine.BaseStats.IsStarPowerActive)
                    {
                        trackEffectElement.MakeVisible();
                        // If start transition is disabled, previous should be disabled
                        if (!trackEffectElement.EffectRef.StartTransitionEnable && i > 0)
                        {
                            _currentEffects[i - 1].SetEndTransitionVisible(false);
                        }

                        // If end transition is disabled, next should be disabled if it is spawned
                        if (_currentEffects.Count > i + 1 && !trackEffectElement.EffectRef.EndTransitionEnable)
                        {
                            _currentEffects[i + 1].SetStartTransitionVisible(false);
                        }
                    }
                    // We also need to make already spawned drum fills disappear if the player activated SP
                    // And we do need to check effect type here
                    if (trackEffectElement.EffectRef.EffectType == TrackEffectType.DrumFill &&
                        (trackEffectElement.Visibility == 1.0f && Engine.BaseStats.IsStarPowerActive))
                    {
                        if (trackEffectElement.EffectRef.OriginalEffectType == TrackEffectType.DrumFillAndUnison)
                        {
                            // Turn this into a unison
                            trackEffectElement.EffectRef.EffectType = TrackEffectType.Unison;
                            SwapEffect(trackEffectElement);
                            return;
                        }

                        if (trackEffectElement.EffectRef.OriginalEffectType == TrackEffectType.SoloAndDrumFill)
                        {
                            // Turn this into a solo
                            trackEffectElement.EffectRef.EffectType = TrackEffectType.Solo;
                            SwapEffect(trackEffectElement);
                            return;
                        }

                        trackEffectElement.MakeVisible(false);

                        if (!trackEffectElement.EffectRef.StartTransitionEnable && i > 0)
                        {
                            // Previous maybe needs end transition enabled since we're disappearing
                            // (if the effect type doesn't have an end transition set, it won't
                            //  be active regardless of what we do here, so a hard enable is ok)
                            _currentEffects[i - 1].SetEndTransitionVisible(true);
                        }

                        if (!trackEffectElement.EffectRef.EndTransitionEnable)
                        {
                            // next needs start transition enabled, if it is spawned
                            // if it isn't yet spawned, it should already be set correctly
                            if (_currentEffects.Count > i + 1)
                            {
                                _currentEffects[i + 1].SetStartTransitionVisible(true);
                            }
                        }
                    }
                }
            }
        }

        private static async void SwapEffect(TrackEffectElement trackEffectElement)
        {
            await trackEffectElement.MakeVisibleAsync(false);
            trackEffectElement.Reinitialize();
            // ReSharper disable once MethodHasAsyncOverload
            trackEffectElement.MakeVisible(true);
        }

        private void SpawnEffect(TrackEffect nextEffect, bool seeking)
        {
            var poolable = EffectPool.TakeWithoutEnabling();
            if (poolable == null)
            {
                YargLogger.LogWarning("Attempted to spawn track effect, but it's at its cap!");
                return;
            }

            // The seeking code handles this for us if we're seeking
            if (!seeking)
            {
                _upcomingEffects.Dequeue();
            }

            // Do some magic to vanish drum fills if the player doesn't have enough SP to activate
            // or if SP is already active.

            if (Engine.BaseStats.IsStarPowerActive || !Engine.CanStarPowerActivate)
            {
                if (nextEffect.EffectType is TrackEffectType.DrumFill)
                {
                    nextEffect.Visibility = 0.0f;
                    if (!nextEffect.StartTransitionEnable)
                    {
                        if (_currentEffects.Count > 0)
                        {
                            _currentEffects[^1].SetEndTransitionVisible(true);
                            _currentEffects[^1].SetTransitionState();
                        }
                    }
                    if (!nextEffect.EndTransitionEnable)
                    {
                        // Get next next and turn on its start transition
                        // Since we are only spawning now, it shouldn't be possible
                        // for next next to be spawned yet.
                        if (_upcomingEffects.TryPeek(out var nextNextEffect))
                        {
                            nextNextEffect.StartTransitionEnable = true;
                        }
                    }

                    if (!nextEffect.StartTransitionEnable)
                    {
                        // Turn on end transition for previous effect

                        // Previous effect is by definition already spawned,
                        // but we'll check that _currentEffects isn't length zero
                        if (_currentEffects.Count > 0)
                        {
                            _currentEffects[^1].SetEndTransitionVisible(true);
                        }
                    }
                }

                if (nextEffect.EffectType is TrackEffectType.DrumFillAndUnison)
                {
                    nextEffect.EffectType = TrackEffectType.Unison;
                }

                if (nextEffect.EffectType is TrackEffectType.SoloAndDrumFill)
                {
                    nextEffect.EffectType = TrackEffectType.Solo;
                }
            }

            ((TrackEffectElement) poolable).EffectRef = nextEffect;
            _currentEffects.Add((TrackEffectElement) poolable);
            poolable.EnableFromPool();
        }

        // ReSharper disable once InconsistentNaming
        protected virtual void StartBRE(double timeStart, double timeEnd)
        {
            RescaleLanesForBRE();

            if (!LanePool.CanSpawnAmount(BRELanes.Length))
            {
                return;
            }

            for (int i = 0; i < BRELanes.Length; i++)
            {
                var newLane = (LaneElement) LanePool.TakeWithoutEnabling();

                if (newLane == null)
                {
                    YargLogger.LogWarning("Attempted to spawn BRE lane, but it's at its cap!");
                    return;
                }

                newLane.SetTimeRange(timeStart, timeEnd);
                InitializeBRELane(newLane, i);
                newLane.EnableFromPool();

                newLane.SetEmissionColor(0);

                BRELanes[i] = newLane;
            }
        }

        protected virtual void OnNoteSpawned(TNote parentNote)
        {
            SpawnLanesFromNote(parentNote);
        }

        protected virtual void SpawnLanesFromNote(TNote parentNote)
        {
            if (!Engine.BaseParameters.EnableLanes)
            {
                return;
            }

            if (!LanePool.CanSpawnAmount(1))
            {
                return;
            }

            bool containsLaneStart = false;
            foreach (var childNote in parentNote.AllNotes)
            {
                if (childNote.IsLaneStart)
                {
                    containsLaneStart = true;
                    break;
                }
            }

            if (containsLaneStart)
            {
                var laneStartNotes = new Dictionary<int, TNote>();
                var laneEndTimes = new Dictionary<int, double>();

                // Iterate forward to find the length of all lanes in this phrase
                var noteRef = parentNote;
                var thisLaneFlag = parentNote.IsTrill ? NoteFlags.Trill : NoteFlags.Tremolo;

                while (noteRef != null)
                {
                    // Create one lane for single notes, create multiple lanes for non-drum chords
                    bool containsLaneEnd = false;
                    foreach (var childNote in noteRef.AllNotes)
                    {
                        if (childNote.IsLaneEnd)
                        {
                            containsLaneEnd = true;
                        }

                        if (childNote.IsLane)
                        {
                            if (!laneStartNotes.ContainsKey(childNote.LaneNote))
                            {
                                laneStartNotes[childNote.LaneNote] = childNote;
                            }

                            laneEndTimes[childNote.LaneNote] = noteRef.Time;
                        }
                    }

                    if (containsLaneEnd)
                    {
                        break;
                    }

                    noteRef = noteRef.NextNote;
                }

                foreach (var (laneIndex, note) in laneStartNotes)
                {
                    if (!laneEndTimes.ContainsKey(laneIndex))
                    {
                        // Ending note was not found, do not create lane
                        continue;
                    }

                    var firstLaneNote = laneStartNotes[laneIndex];
                    double startTime = firstLaneNote.Time;
                    double endTime = laneEndTimes[laneIndex];

                    // Extend a previous lane if possible instead of creating two adjoining lanes at the same index
                    bool extendExisting = false;
                    foreach (LaneElement existingLane in LanePool.AllSpawned)
                    {
                        if (existingLane.ContainsIndex(laneIndex))
                        {
                            if (startTime - existingLane.EndTime <= LaneElement.COMBINE_LANE_THRESHOLD)
                            {
                                // New lane will overlap with existing one
                                // Determine if the previous notes in this chart should prevent combining
                                int notesToSearch = firstLaneNote.IsTrill ? 2 : 1;
                                noteRef = firstLaneNote.PreviousNote;
                                for (int n = 0; n < notesToSearch; n++)
                                {
                                    if (noteRef == null)
                                    {
                                        break;
                                    }

                                    if (existingLane.ContainsIndex(noteRef.LaneNote) && (noteRef.Flags & thisLaneFlag) != 0)
                                    {
                                        extendExisting = true;
                                        break;
                                    }

                                    noteRef = noteRef.PreviousNote;
                                }
                            }

                            if (extendExisting)
                            {
                                existingLane.SetTimeRange(existingLane.ElementTime, Math.Max(endTime, existingLane.EndTime));
                            }

                            break;
                        }
                    }

                    if (extendExisting)
                    {
                        continue;
                    }

                    // Create a new lane element at this index
                    var newLane = (LaneElement) LanePool.TakeWithoutEnabling();
                    newLane.SetTimeRange(startTime, endTime);
                    InitializeSpawnedLane(newLane, note);
                    ModifyLaneFromNote(newLane, firstLaneNote);

                    newLane.EnableFromPool();
                }
            }
        }

        public override void SetPracticeSection(uint start, uint end)
        {
            var practiceNotes = OriginalNoteTrack.Notes.Where(n => n.Tick >= start && n.Tick < end).ToList();

            YargLogger.LogFormatDebug("Practice notes: {0}", practiceNotes.Count);

            var instrument = OriginalNoteTrack.Instrument;
            var difficulty = OriginalNoteTrack.Difficulty;
            var phrases = OriginalNoteTrack.Phrases;
            var textEvents = OriginalNoteTrack.TextEvents;
            var shiftEvents = OriginalNoteTrack.RangeShiftEvents;

            NoteTrack = new InstrumentDifficulty<TNote>(instrument, difficulty, practiceNotes, phrases, textEvents, shiftEvents);
            Notes = NoteTrack.Notes;

            ResetNoteCounters();

            BeatlineIndex = 0;
            ResetStarPowerPathCursors();

            // Removed by EngineManager
            EngineContainer = null;

            Engine = CreateEngine();

            if (GameManager.IsPractice)
            {
                Engine.SetSpeed(GameManager.SongSpeed >= 1 ? GameManager.SongSpeed : 1);
            }
            else
            {
                Engine.SetSpeed(GameManager.SongSpeed);
            }

            // The note track was rebuilt from a tick range and the engine recreated, so the old
            // plan's note indices mean nothing (docs/sp-path-design.md 4.3).
            RecomputeStarPowerPath();

            ResetPracticeSection();
        }

        public override void SetReplayTime(double time)
        {
            BeatlineIndex = 0;
            ResetStarPowerPathCursors();
            ResetNoteCounters();

            // Reset the track effect overlay
            ResetTrackEffectOverlay(time);

            base.SetReplayTime(time);
        }

        private void ResetTrackEffectOverlay(double time)
        {
            // despawn any existing track effects, rebuild track effect structures, spawn any that are now in current
            _upcomingEffects.Clear();
            for(var i = 0; i < EffectPool.AllSpawned.Count; i++)
            {
                var poolable = EffectPool.AllSpawned[i];
                poolable.ParentPool.Return(poolable);
            }

            foreach (var effect in TrackEffect.SliceEffects(NoteSpeed, _trackEffects))
            {
                if (effect.Time >= time)
                {
                    _upcomingEffects.Enqueue(effect);
                } else if (effect.Time < time && time < effect.TimeEnd)
                {
                    // current effect, spawn it
                    SpawnEffect(effect, true);
                }
            }
        }

        protected void SpawnNote(TNote note)
        {
            var poolable = NotePool.KeyedTakeWithoutEnabling(note);
            if (poolable == null)
            {
                YargLogger.LogWarning("Attempted to spawn note, but it's at its cap!");
                return;
            }

            InitializeSpawnedNote(poolable, note);

            // Always assigned, never only when true: note elements are pooled, so a stale flag
            // from a previous activation would leave an ordinary note green. Set after
            // InitializeSpawnedNote and before EnableFromPool, because EnableFromPool is what
            // runs InitializeElement (and with it the colour pass that reads the flag).
            if (poolable is INoteElement noteElement)
            {
                noteElement.IsStarPowerPathActivation = SpawningActivationNote;
            }

            poolable.EnableFromPool();
        }

        protected abstract void InitializeSpawnedNote(IPoolable poolable, TNote note);
        protected abstract void InitializeSpawnedLane(LaneElement lane, TNote note);
        protected abstract void InitializeBRELane(LaneElement lane, int laneIndex);
        protected virtual void ModifyLaneFromNote(LaneElement lane, TNote note) {}

        protected abstract void RescaleLanesForBRE();

        public override IReadOnlyList<SectionCompletionResult> ScanSectionCompletion(
            IReadOnlyList<Section> sections)
        {
            // The engine's note count is used so that the totals line up with EngineStats.TotalNotes,
            // which counts chords as either one note or one note per lane depending on the instrument.
            //
            // The lambda must stay a lambda: Engine.GetNumberOfNotes is itself generic over the note
            // type, so passing it as a method group gives the compiler nothing to infer ScanNotes'
            // TNote from and the call fails to resolve. Calling it inside the lambda fixes TNote to
            // this player's note type first.
            return SectionCompletionScanner.ScanNotes(sections, Notes, note => Engine.GetNumberOfNotes(note));
        }

        protected virtual void OnNoteHit(int index, TNote note)
        {
            if (!Player.Profile.IsBot)
            {
                _autoCalibrator.RecordAccuracy(Engine.CurrentTime, note.Time);
            }

            // Big rock ending notes aren't part of a section's note total, so hitting one can't
            // move its progress either.
            //
            // The count is always one, because every hit the engine dispatches is worth exactly
            // one of the scanner's NotesTotal. Instruments that treat a chord as one note hit the
            // whole chord at once, and the ones that don't hit each sub-note separately, so the
            // unit matches either way. Engine.GetNumberOfNotes is deliberately not used here even
            // though it is what builds those totals: it answers a different question - how many
            // notes a note object stands for - and for a chord parent under separate-chord
            // semantics (drums, keys) it returns the size of the whole chord, which the sub-notes'
            // own hits would then count a second time and push the section past 100%.
            //
            // Known limitation: the dispatch is not quite one per counted note. On drums, a star
            // power activation note the player skips is marked hit by the engine itself
            // (DrumsEngine's activation handling) without an OnNoteHit dispatch, so the live
            // percent for the section containing it can never reach 100 while that section is
            // current. Only the percent is affected: the block still goes clean on entry and is
            // only dropped by an actual miss, and the end-of-song scan reads WasHit off the notes
            // rather than counting dispatches, so section credit is awarded correctly. Fixing it
            // would mean a new dispatch in YARG.Core, which this fork does not modify.
            if (!note.IsBigRockEnding)
            {
                NotifySectionNoteHit(note.Tick, 1);
            }

            if (!GameManager.IsSeekingReplay)
            {
                UpdateMuteState(note, false);
                if (_currentMultiplier != _previousMultiplier)
                {
                    _previousMultiplier = _currentMultiplier;

                    foreach (var haptics in SantrollerHaptics)
                    {
                        haptics.SetMultiplier((byte) Math.Clamp(_currentMultiplier, 1, byte.MaxValue));
                    }
                }

                if (index >= Notes.Count - 1 && note.ParentOrSelf.WasFullyHit())
                {
                    if (IsFc)
                    {
                        TrackView.ShowFullCombo();
                    }
                    else if (Combo >= 30) // 30 to coincide with 4x multiplier (including on bass)
                    {
                        TrackView.ShowStrongFinish();
                    }
                }
            }

            LastCombo = Combo;
        }

        protected virtual void OnNoteMissed(int index, TNote note)
        {
            if (IsFc)
            {
                ComboMeter.SetFullCombo(false);
                IsFc = false;
            }

            // Big rock ending notes aren't part of a section's note total, so missing one can't
            // drop the section either
            if (!note.IsBigRockEnding)
            {
                NotifySectionNoteMissed(note.Tick);
            }

            if (!GameManager.IsSeekingReplay)
            {
                UpdateMuteState(note, true);

                if (LastCombo >= 10)
                {
                    GlobalAudioHandler.PlaySoundEffect(SfxSample.NoteMiss);
                    CameraPositioner.Punch();
                }

                foreach (var haptics in SantrollerHaptics)
                {
                    haptics.SetMultiplier(0);
                }
            }

            LastCombo = Combo;
        }

        protected virtual void OnOverhit()
        {
            if (IsFc)
            {
                ComboMeter.SetFullCombo(false);
                IsFc = false;
            }

            if (LastCombo >= 10)
            {
                CameraPositioner.Punch();
            }

            LastCombo = Combo;
        }

        protected virtual void UpdateMuteState(TNote note, bool isMuted)
        {
            SetStemMuteState(isMuted);
        }

        protected virtual void OnSoloStart(SoloSection solo)
        {
            TrackView.StartSolo(solo);

            foreach (var haptic in SantrollerHaptics)
            {
                haptic.SetSoloActive(true);
            }
        }

        protected virtual void OnSoloEnd(SoloSection solo)
        {
            TrackView.EndSolo(solo.SoloBonus);

            foreach (var haptic in SantrollerHaptics)
            {
                haptic.SetSoloActive(false);
            }
        }

        protected virtual void OnCodaStart(CodaSection coda)
        {
            CurrentCoda = coda;
            SetStemMuteState(false);
            TrackView.StartCoda();
        }

        protected virtual void OnCodaEnd(CodaSection coda)
        {
            TrackView.EndCoda();
        }

        private void OnUnisonStart()
        {
            TrackView.StartUnison();
        }

        private void OnUnisonEnd()
        {
            TrackView.EndUnison();
        }

        protected virtual void OnCountdownChange(double countdownLength, double endTime)
        {
            TrackView.UpdateCountdown(countdownLength, endTime);
        }

        protected virtual void OnStarPowerPhraseMissed(TNote note)
        {
            // Recorded, not acted on. This used to dim the whole path on the spot, which produced
            // a real false positive: the engine strips a phrase on a missed note, on an overstrum
            // whose next note happens to sit inside a phrase, or under the unused
            // NoStarPowerOverlap rule (Guitar/GuitarEngine.cs:193, :261, :322), and none of those
            // means the plan has stopped being followable. The player's meter is fed by sources
            // the model does not carry - unison bonuses above all (BaseEngine.AwardUnisonBonus, a
            // free quarter bar for every unison phrase all participants clear) - so a lost phrase
            // is routinely free. The verdict belongs to the meter check at each planned activation
            // (CheckStarPowerPathMeter, docs/sp-path-design.md §4.4).
            if (note is not null)
            {
                NoteStarPowerPhraseLost(note.Tick, note.Time, note.WasMissed);
            }

            OnStarPowerPhraseMissed();
        }

        protected virtual void OnStarPowerPhraseHit(TNote note)
        {
            if (SettingsManager.Settings.EnableTrackEffects.Value)
            {
                StarPowerEffect.gameObject.SetActive(true);
                StarPowerEffect.PlayAnimation();
            }

            OnStarPowerPhraseHit();
        }

        protected override void OnStarPowerReady()
        {
            base.OnStarPowerReady();
            TrackView.ShowStarPowerReady();
        }

        protected void OnHappinessOverFail()
        {
            TrackMaterial.FailState = 0f;
        }

        protected void OnHappinessNearFail()
        {
            if (SettingsManager.Settings.NoFail.Value == NoFailMode.Off && !GameManager.IsPractice)
            {
                TrackMaterial.FailState = 1f;
            }
        }

        protected void OnPlayerFailed(int engineId)
        {
            if (SettingsManager.Settings.NoFail.Value != NoFailMode.Off
                || engineId != EngineContainer.EngineId
                || GameManager.IsPractice)
            {
                // Not for us
                return;
            }

            // Mark as failed and lower highway
            PlayerHasFailed = true;
            CameraPositioner.Lower(false);
        }

        protected void OnPlayerRevived()
        {
            if (!PlayerHasFailed)
            {
                return;
            }

            // Unfail and raise highway
            PlayerHasFailed = false;
            CameraPositioner.Raise(false);
        }

        public override void GameplayUpdate()
        {
            base.GameplayUpdate();

            if (LastHighScore != null && !_newHighScoreShown && Score > LastHighScore)
            {
                _newHighScoreShown = true;
                TrackView.ShowNewHighScore();
            }
        }

        protected override void GameplayDestroy()
        {
            base.GameplayDestroy();

            GameManager.EngineManager.OnPlayerFailed -= OnPlayerFailed;
            GameManager.EngineManager.OnPlayerRevived -= OnPlayerRevived;
        }
    }
}
