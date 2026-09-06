using System;
using System.Collections.Generic;
using System.Linq;
using Cysharp.Threading.Tasks;
using UnityEngine;
using UnityEngine.InputSystem;
using YARG.Core.Audio;
using YARG.Core.Chart;
using YARG.Core.Engine;
using YARG.Core.Game;
using YARG.Core.Input;
using YARG.Core.Logging;
using YARG.Core.Replays;
using YARG.Core.Replays.Analyzer;
using YARG.Core.Song;
using YARG.Gameplay.HUD;
using YARG.Gameplay.Player;
using YARG.Gameplay.Visuals;
using YARG.Input;
using YARG.Integration;
using YARG.Localization;
using YARG.Menu.Navigation;
using YARG.Menu.Persistent;
using YARG.Menu.ScoreScreen;
using YARG.Playback;
using YARG.Player;
using YARG.Replays;
using YARG.Scores;
using YARG.Settings;
using YARG.Settings.Types;
using YARG.Venue.Characters;
using YARG.Venue.VenueCamera;

namespace YARG.Gameplay
{
    [DefaultExecutionOrder(-1)]
    public partial class GameManager : MonoBehaviour
    {
        public const double SONG_START_DELAY = SongRunner.SONG_START_DELAY;
        public const double SONG_END_DELAY = SONG_START_DELAY;

        public const double PAUSE_REWIND_LENGTH   = 1;
        public const double MAXIMUM_REWIND_TIME   = 3;
        public const double MAXIMUM_REWIND_WINDOW = 20;

        public const float TRACK_SPACING_X = 100f;


        public bool IsSeekingReplay;

        [Header("References")]
        [SerializeField]
        private TrackViewManager _trackViewManager;
        [SerializeField]
        private ReplayController _replayController;
        [SerializeField]
        private PauseMenuManager _pauseMenu;
        [SerializeField]
        private DraggableHudManager _draggableHud;

        [SerializeField]
        private LyricBar _lyricBar;

        [SerializeField]
        private FailMeter _failMeter;

        [SerializeField]
        private UnisonDisplay _unisonDisplay;

        [SerializeField]
        private BREBox _breBox;

        [field: SerializeField]
        public VocalTrack VocalTrack { get; private set; }

        /// <summary>
        /// Equal to either <see cref="PlayerContainer.Players"/> or the players in the replay.
        /// </summary>
        public IReadOnlyList<YargPlayer> YargPlayers { get; private set;}

        private List<BasePlayer> _players;

        public int TotalPlayers => _players.Count;

        public bool IsSongStarted { get; private set; } = false;

        private SongRunner _songRunner;
        private float _appliedSongSpeed = float.NaN;

        /// <remarks>
        /// This is not initialized on awake, but rather, in
        /// <see cref="GameplayBehaviour.OnChartLoaded"/>.
        /// </remarks>
        public BeatEventHandler BeatEventHandler { get;    private set; }
        public CrowdEventHandler CrowdEventHandler  { get; private set; }
        public CameraManager     VenueCameraManager { get; private set; }
        public CharacterManager  VenueCharacterManager { get; private set; }

        public PracticeManager  PracticeManager  { get; private set; }
        public BackgroundManager BackgroundManager { get; private set; }
        public EngineManager EngineManager { get; private set; }

        public SongEntry Song  { get; private set; }
        public SongChart    Chart { get; private set; }

        // For clarity, try to avoid using these properties inside GameManager itself
        // These are just to expose properties from the song runner to the outside
        /// <inheritdoc cref="SongRunner.SongTime"/>
        public double SongTime => _songRunner.SongTime;

        /// <inheritdoc cref="SongRunner.VisualTime"/>
        public double VisualTime => _songRunner.VisualTime;

        /// <inheritdoc cref="SongRunner.InputTime"/>
        public double InputTime => _songRunner.InputTime;

        /// <inheritdoc cref="SongRunner.SongSpeed"/>
        public float SongSpeed => _songRunner.SongSpeed;

        /// <inheritdoc cref="SongRunner.IsAudioSyncCorrectionActive"/>
        public bool IsAudioSyncCorrectionActive => _songRunner.IsAudioSyncCorrectionActive;

        /// <inheritdoc cref="SongRunner.Started"/>
        public bool Started => _songRunner?.Started ?? false;

        /// <inheritdoc cref="SongRunner.Paused"/>
        public bool Paused => _songRunner?.Paused ?? true;

        /// <summary>
        /// The current song's specific offset (in milliseconds), editable from the pause menu
        /// and by <see cref="Helpers.AutoCalibrator"/>. Backed by <see cref="Song.SongOffsetContainer"/>.
        /// </summary>
        public IntSetting SongOffsetOverride { get; private set; }

        /// <summary>
        /// Set when we are in the middle of resuming, but have not yet fully resumed
        /// </summary>
        public bool Rewinding { get; private set; }

        public double SongLength { get; private set; }

        public bool IsPractice      { get; private set; }

        public bool IsReplay => ReplayInfo != null && !GlobalVariables.State.PlayingWithReplay;

        public int BandScore
        {
            get => EngineManager.Score;
            set => EngineManager.Score = value;
        }

        public int BandCombo
        {
            get => EngineManager.Combo;
            set => EngineManager.Combo = value;
        }

        public float BandStars => EngineManager.Stars;

        public int BandMultiplier => EngineManager.BandMultiplier;

        public double FirstNoteTime { get; private set; }
        public double LastNoteTime  { get; private set; }

        public ReplayInfo ReplayInfo { get; private set; }
        public ReplayData ReplayData { get; private set; }

        public List<PauseInfo> PauseInfo { get; } = new List<PauseInfo>();

        public IReadOnlyList<BasePlayer> Players => _players;

        public int StarPowerActivations { get; private set; } = 0;

        private bool _isReplaySaved;
        private int _originalSleepTimeout;

        private StemMixer _mixer;
        public  StemMixer  Mixer => _mixer;

        private MetronomeScheduler _metronomeScheduler;
        private CrowdClapScheduler _crowdClapScheduler;

        private List<double> _frameTimes;

        private double _pauseTime;
        private double _rewindLimit = double.MinValue;
        private bool   _resumeInProgress;
        private bool   _autoCalibrateVideoOnPause;
        private double _preFadeOutVolume = DEFAULT_VOLUME;

        public bool PlayingAShow => GlobalVariables.State.PlayingAShow;
        public int  ShowIndex = 0;

        private BandComboType _bandComboType;

        private        bool HasBots            => _players.Any(p => !p.Player.SittingOut && p.Player.Profile.IsBot);
        private static bool SaveScoresWithBots => SettingsManager.Settings.SaveScoresWithBots.Value;

        private void Awake()
        {
            // Set references
            PracticeManager = GetComponent<PracticeManager>();
            BackgroundManager = GetComponent<BackgroundManager>();
            EngineManager = new EngineManager();

            YargPlayers = PlayerContainer.Players;

            Song = GlobalVariables.State.CurrentSong;
            ReplayInfo = GlobalVariables.State.CurrentReplay;
            IsPractice = GlobalVariables.State.IsPractice && ReplayInfo == null;
            _bandComboType = SettingsManager.Settings.BandComboTypeSetting.Value;

            Navigator.Instance.PopAllSchemes();
            GameStateFetcher.SetSongEntry(Song);

            if (Song is null)
            {
                YargLogger.LogError("Null song set when loading gameplay!");

                GlobalVariables.Instance.LoadScene(SceneIndex.Menu);
                return;
            }

            // Hide vocals track (will be shown when players are initialized)
            VocalTrack.gameObject.SetActive(false);

            // Prevent screen from sleeping
            _originalSleepTimeout = Screen.sleepTimeout;
            Screen.sleepTimeout = SleepTimeout.NeverSleep;

            // Update countdown display style from global settings
            CountdownDisplay.DisplayStyle = SettingsManager.Settings.CountdownDisplay.Value;

            _frameTimes = new List<double>();
        }

        private void OnDestroy()
        {
            YargLogger.LogInfo("Exiting song");

            if (Navigator.Instance != null)
            {
                Navigator.Instance.NavigationEvent -= OnNavigationEvent;
            }

            // Unsubscribe from other events
            SettingsManager.Settings.NoFail.OnChange -= OnNoFailModeChanged;
            EngineManager.OnSongFailed -= OnSongFailed;
            EngineManager.OnCodaStart -= StartCoda;
            EngineManager.OnCodaEnd -= EndCoda;
            EngineManager.OnUnisonPhraseSuccess -= OnUnisonPhraseSuccess;

            // Stop playback-owned work before teardown callbacks touch the mixer or UI.
            _metronomeScheduler?.Dispose();
            _crowdClapScheduler?.Dispose();
            _songRunner?.Dispose();

            // Restore stem volumes to their original state while the mixer is still valid.
            foreach (var (stem, state) in _stemStates)
            {
                GlobalAudioHandler.SetVolumeSetting(stem, state.Volume);
            }

            DisposeDebug();

            // Scene teardown can destroy this object before GameManager.OnDestroy runs.
            if (_pauseMenu != null)
            {
                _pauseMenu.PopAllMenus();
            }

            // Crowd teardown stops SFX through GlobalAudioHandler, so it must happen while audio is initialized.
            CrowdEventHandler?.Dispose();

            _mixer?.Dispose();

            BackgroundManager.Dispose();

            // Reset the time scale back, as it would be 0 at this point (because of pausing)
            Time.timeScale = 1f;

            // Reset sleep timeout setting
            Screen.sleepTimeout = _originalSleepTimeout;
        }

        private void Update()
        {


            // Pause/unpause
            if (Keyboard.current.escapeKey.wasPressedThisFrame)
            {
                if (_draggableHud.EditMode)
                {
                    SetEditHUD(false);
                }

                if ((!IsPractice || PracticeManager.HasSelectedSection) &&
                    !DialogManager.Instance.IsDialogShowing &&
                    !PlayerHasFailed)
                {
                    SetPaused(!_pauseMenu.IsOpen);
                }
            }

            // Toggle debug text
            if (Keyboard.current.ctrlKey.isPressed && Keyboard.current.tabKey.wasPressedThisFrame)
            {
                ToggleDebugEnabled();
            }

            // Skip the rest if paused
            if (_songRunner.Paused)
            {
                return;
            }

            // Update handlers
            _songRunner.Update();

            ApplySongSpeed();
            BeatEventHandler.Update(_songRunner.SongTime, _songRunner.VisualTime);
            CrowdEventHandler.Update(_songRunner.SongTime);

            // Update players
            int totalScore = 0;
            foreach (var player in _players)
            {
                player.GameplayUpdate();

                totalScore += player.Score;
                totalScore += player.BandBonusScore;
            }

            if (GlobalVariables.VerboseReplays)
            {
                _frameTimes.Add(_songRunner.InputTime);
            }

            BandScore = totalScore;
            EngineManager.UpdateStars();

            // End song if needed (required for the [end] event)
            if (_songRunner.SongTime >= SongLength)
            {
                if (EndSong())
                {
                    return;
                }
            }
        }


        public void SetSongTime(double time, double delayTime = SONG_START_DELAY)
        {
            _songRunner.SetSongTime(time, delayTime);
            ApplySongSpeed();

            BeatEventHandler.Reset();
            BackgroundManager.SetTime(_songRunner.GetAudioPlaybackTime(_songRunner.SongTime));
            VenueCameraManager?.ResetTime(time);
            VenueCharacterManager?.ResetTime(time);
            if (_lyricBar.gameObject.activeSelf)
            {
                _lyricBar.SetSongTime(time);
            }

            if (_unisonDisplay.gameObject.activeSelf)
            {
                _unisonDisplay.SetSongTime(time);
            }
        }

        public void SetSongSpeed(float speed)
        {
            _songRunner.SetSongSpeed(speed);
            ApplySongSpeed();
        }

        public int GetMixerFFTData(float[] buffer, int fftSize, bool complex)
        {
            return _mixer.GetFFTData(buffer, fftSize, complex);
        }

        public int GetMixerSampleData(float[] buffer)
        {
            return _mixer.GetSampleData(buffer);
        }

        public void AdjustSongSpeed(float deltaSpeed)
        {
            _songRunner.AdjustSongSpeed(deltaSpeed);

            ApplySongSpeed();
        }

        public void AdjustSongSpeedInPlace(float deltaSpeed)
        {
            _songRunner.AdjustSongSpeedInPlace(deltaSpeed);

            ApplySongSpeed();
        }

        private void ApplySongSpeed()
        {
            float speed = _songRunner.SongSpeed;
            if (Mathf.Approximately(speed, _appliedSongSpeed))
            {
                return;
            }

            _appliedSongSpeed = speed;

            // Only scale the player speed in practice.
            if (IsPractice && _players != null)
            {
                float engineSpeed = speed >= 1 ? speed : 1;
                foreach (var player in _players)
                {
                    player.BaseEngine.SetSpeed(engineSpeed);
                }
            }

            BackgroundManager.SetSpeed(speed);
        }

        public void Pause(bool showMenu = true)
        {
            _songRunner.Pause();
            PauseCore(showMenu);
        }

        private void PauseCore(bool showMenu)
        {
            if (showMenu)
            {
                if (!GlobalVariables.State.PlayingWithReplay && ReplayInfo != null)
                {
                    _pauseMenu.PushMenu(PauseMenuManager.Menu.ReplayPause);
                }
                else if (PlayerHasFailed)
                {
                    _pauseMenu.PushMenu(PauseMenuManager.Menu.FailPause);
                }
                else if (IsPractice)
                {
                    _pauseMenu.PushMenu(PauseMenuManager.Menu.PracticePause);
                }
                else if (GlobalVariables.State.PlayingAShow)
                {
                    _pauseMenu.PushMenu(PauseMenuManager.Menu.SetlistPause);
                }
                else
                {
                    _pauseMenu.PushMenu(PauseMenuManager.Menu.QuickPlayPause);
                }
            }

            // Pause the background/venue
            Time.timeScale = 0f;
            BackgroundManager.SetPaused(true);
            GameStateFetcher.SetPaused(true);

            // This uses the raw input update time because it keeps running during the pause
            // allowing us to accurately calculate the length of the pause later
            if (!Rewinding && !IsReplay && showMenu)
            {
                // Save state about the pause
                _pauseTime = InputManager.InputUpdateTime;
                var pauseInfo = new PauseInfo
                {
                    PauseTime = SongTime,
                    PauseLength = 0
                };
                PauseInfo.Add(pauseInfo);

                // Calculate the rewind limit now so it can't be overwritten if the user pauses again before completion
                var rewindTime = Math.Max(SongTime - PAUSE_REWIND_LENGTH, _rewindLimit);
                _rewindLimit = rewindTime;
            }

            _autoCalibrateVideoOnPause = SettingsManager.Settings.AutoCalibrateVideo.Value;

            // Pause any audio samples that are currently playing
            GlobalAudioHandler.PauseAllSfx();

            // Allow sleeping
            Screen.sleepTimeout = _originalSleepTimeout;
        }

        public bool PlayerHasFailed { get; set; } = false;

        public async void Resume(double? rewindDuration = null)
        {
            // We don't rewind in practice mode or in replay, so we can skip all the BS
            if (IsPractice || IsReplay)
            {
                _pauseMenu.PopAllMenus();
                _songRunner.Resume();
                ResumeCore();
                return;
            }

            if (_resumeInProgress)
            {
                return;
            }

            _resumeInProgress = true;
            Rewinding = true;

            // If AutoCalibrateVideo changed while paused, fade the mixer accordingly
            bool autoCalibrateVideoEnabled = SettingsManager.Settings.AutoCalibrateVideo.Value;
            bool didChangeWhilePaused = autoCalibrateVideoEnabled != _autoCalibrateVideoOnPause;
            if (didChangeWhilePaused)
            {
                if (autoCalibrateVideoEnabled)
                {
                    _preFadeOutVolume = _mixer.GetVolume();
                    _mixer.FadeOut(SONG_START_DELAY);
                }
                else
                {
                    _mixer.FadeIn(_preFadeOutVolume, SONG_START_DELAY);
                }
            }

            // try block is here so we can ensure that _resumeInProgress always gets reset
            try
            {
                _pauseMenu.PopAllMenus();
                Time.timeScale = 1f;

                // Update the last PauseInfo with the pause length
                var currentPause = PauseInfo[^1];
                currentPause.PauseLength = InputManager.InputUpdateTime - _pauseTime;
                PauseInfo[^1] = currentPause;

                // Don't allow rewinding past the rewind limit, unless a duration was explicitly passed to the resume function
                var rewindSeconds = Math.Max(0, rewindDuration ?? SongTime - _rewindLimit);
                if (rewindSeconds == PAUSE_REWIND_LENGTH)
                {
                    GlobalAudioHandler.PlaySoundEffect(SfxSample.Rewind);
                }

                var canceled = await RewindAndResume(rewindSeconds);

                if (canceled)
                {
                    return;
                }

                ResumeCore();
            }
            finally
            {
                _resumeInProgress = false;
            }
        }

        public void UpdateCalibration()
        {
            _songRunner.UpdateCalibration();
        }

        public void ResumeCore()
        {
            if (_draggableHud.EditMode)
            {
                SetEditHUD(false);
            }

            if (!Rewinding)
            {
                _pauseMenu.PopAllMenus();
            }

            if (_songRunner.SongTime >= SongLength + SONG_END_DELAY)
            {
                return;
            }

            // Unpause the background/venue
            Time.timeScale = 1f;
            BackgroundManager.SetPaused(false);
            GameStateFetcher.SetPaused(false);

            // Unpause any audio samples that are currently playing
            GlobalAudioHandler.ResumeAllSfx();

            // Disallow sleeping
            Screen.sleepTimeout = SleepTimeout.NeverSleep;

            _isReplaySaved = false;

            Rewinding = false;

            foreach (var player in _players)
            {
                player.SendInputsOnResume();
            }

        }

        public void SetPaused(bool paused)
        {
            // Does not delegate out to _songRunner.SetPaused since we need extra logic
            if (paused)
            {
                Pause();
            }
            else
            {
                Resume();
            }
        }

        public void OverridePause()
        {
            _songRunner.OverridePause();
            PauseCore(showMenu: false);
        }

        public bool OverrideResume()
        {
            bool resumed = _songRunner.OverrideResume();
            if (resumed)
            {
                ResumeCore();
            }

            return resumed;
        }

        public double GetInputTime(double inputSystemTime)
            => _songRunner.GetInputTime(inputSystemTime);

        /// <inheritdoc cref="SongRunner.GetAudioPlaybackTime"/>
        public double GetAudioPlaybackTime(double songTime)
            => _songRunner.GetAudioPlaybackTime(songTime);

        private bool EndSong()
        {
            _crowdClapScheduler?.Dispose();
            // Dispose the crowd handler
            CrowdEventHandler?.Dispose();

            if (IsPractice)
            {
                PracticeManager.ResetPractice();
                return false;
            }

            if (_songRunner.SongTime < SongLength + SONG_END_DELAY)
            {
                return false;
            }

            if (!GlobalVariables.State.PlayingWithReplay && ReplayInfo != null)
            {
                Pause(false);
                return true;
            }
#nullable enable
            ReplayInfo? replayInfo = null;
#nullable disable
            try
            {
                _isReplaySaved = false;
                replayInfo = SaveReplay(_songRunner.InputTime, ScoreContainer.ScoreReplayDirectory);
            }
            catch (Exception e)
            {
                YargLogger.LogException(e, "Failed to save replay!");
            }

            // Scanned up front so that the score screen and the database both read the same
            // results, rather than the scan being run twice
            var sectionCompletions = ScanSectionCompletions();

            // Built before the scores are recorded, but only published afterwards: whether the
            // section rows actually made it to the database is not known until RecordScores has
            // run, and the card must never show progress that was silently dropped
            var playerScores = _players.Select(player => new PlayerScoreCard
            {
                IsHighScore = player.Score > player.LastHighScore,
                Player = player.Player,
                Stats = player.BaseStats,
                IsReplay = player.Player.IsReplay,
                Sections = sectionCompletions.TryGetValue(player, out var completion)
                    ? completion.Summary
                    : null,
            }).ToArray();

            bool sectionsRecorded = RecordScores(replayInfo, sectionCompletions);
            if (!sectionsRecorded)
            {
                // RecordScores bailed before writing anything, so there is no persisted progress
                // to report. PlayerScoreCard is a struct, so this has to go through the array.
                for (int i = 0; i < playerScores.Length; i++)
                {
                    playerScores[i].Sections = null;
                }
            }

            // Pass the score info to the stats screen
            GlobalVariables.State.ScoreScreenStats = new ScoreScreenStats
            {
                PlayerScores = playerScores,
                BandScore = BandScore,
                BandStars = (int) BandStars,

                // TODO: When online comes out, change
                // .Where(player => !player.Player.Profile.IsBot)
                // to:
                // .Where(player => !(player.Player.Profile.IsBot || player.Player.IsRemote))
                MeanAverageOffset = _players
                    .Where(player => !player.Player.Profile.IsBot)
                    .Select(player => player.BaseStats.GetAverageOffset())
                    .DefaultIfEmpty(0)
                    .Average(),

                ReplayInfo = replayInfo,
            };

            // Go to the score screen
            GlobalVariables.Instance.LoadScene(SceneIndex.Score);
            return true;
        }

        /// <returns>
        /// Whether the section completions were written. False means this method returned early
        /// and nothing at all was recorded, section rows included.
        /// </returns>
        private bool RecordScores(ReplayInfo replayInfo,
            IReadOnlyDictionary<BasePlayer, PendingSectionCompletion> sectionCompletions)
        {
            if (!ScoreContainer.IsBandScoreValid(SongSpeed))
            {
                return false;
            }

            // Get all of the individual player score entries
            var playerEntries = new List<PlayerScoreRecord>();
            var starScoreCutoffsList = new List<int[]>();
            foreach (var player in _players)
            {
                var profile = player.Player.Profile;

                // Skip bots and anyone that's obviously cheating.
                if (!ScoreContainer.IsSoloScoreValid(SongSpeed, player.Player))
                {
                    continue;
                }

                playerEntries.Add(new PlayerScoreRecord
                {
                    PlayerId = profile.Id,

                    Instrument = profile.CurrentInstrument,
                    Difficulty = profile.CurrentDifficulty,

                    EnginePresetId = profile.EnginePreset,

                    Score = player.Score,
                    Stars = StarAmountHelper.GetStarsFromInt((int) player.Stars),

                    NotesHit = player.BaseStats.NotesHit,
                    NotesMissed = player.BaseStats.NotesMissed,
                    IsFc = player.IsFc,
                    IsReplay = player.Player.IsReplay,

                    Percent = player.BaseStats.Percent
                });

                starScoreCutoffsList.Add(player.BaseEngine.StarScoreThresholds);
            }

            var validScoreCount = _players.Count(p => ScoreContainer.IsSoloScoreValid(SongSpeed, p.Player));
            if (validScoreCount == 0)
            {
                return false;
            }

            int humanBandScore = 0;
            float humanBandStars = 0;
            int humanCount = playerEntries.Count;
            if (HasBots && SaveScoresWithBots)
            {
                // Simulate the replay with only human players to calculate the correct score.
                // This will remove band multiplier and Star Power contribution from bots
                if (replayInfo == null || ReplayData == null)
                {
                    return false;
                }
                var results = ReplayAnalyzer.AnalyzeReplay(Chart, replayInfo, ReplayData);
                foreach (var result in results)
                {
                    humanBandScore += result.ResultStats.TotalScore + result.ResultStats.BandBonusScore;
                }
                var humanStarScoreCutoffs = EngineManager.GetStarScoreCutoffs(starScoreCutoffsList);
                // Determine where in the cutoffs humanBandScore is
                // Iterating backwards is slightly faster assuming people are good at the game
                for (int i = humanStarScoreCutoffs.Length - 1; i >= 0; i--)
                {
                    if (humanBandScore >= humanStarScoreCutoffs[i])
                    {
                        // This gives humanBandStars as an int, which is not exactly correct but should make no difference
                        // since it is converted into StarAmount by int anyway
                        humanBandStars = i + 1;
                        YargLogger.LogFormatDebug("Star count: {0}", humanBandStars);
                        break;
                    }
                }
            }
            else
            {
                // No bots, use live scores directly
                foreach (var player in _players)
                {
                    humanBandScore += player.Score + player.BaseStats.BandBonusScore;
                }
                humanBandStars = EngineManager.Stars;
            }

            var bandStars = humanCount > 0
                ? StarAmountHelper.GetStarsFromInt(Mathf.FloorToInt(humanBandStars))
                : StarAmount.None;

            // Section completions are written alongside the score, so that the two are either
            // both recorded or both skipped
            RecordSectionCompletions(sectionCompletions.Values);

            ScoreContainer.RecordScore(new GameRecord
            {
                Date = DateTime.Now,

                SongChecksum = Song.Hash.HashBytes,
                SongName = Song.Name,
                SongArtist = Song.Artist,
                SongCharter = Song.Charter,

                ReplayFileName = replayInfo?.ReplayName,
                ReplayChecksum = replayInfo?.ReplayChecksum.HashBytes,

                BandScore = humanBandScore,
                BandStars = bandStars,

                SongSpeed = SongSpeed,
                PlayedWithReplay = GlobalVariables.State.PlayingWithReplay,
                HasBots = HasBots,
            }, playerEntries);

            return true;
        }

        /// <summary>
        /// The section completions of a single player, waiting to be written to the database.
        /// </summary>
        private class PendingSectionCompletion
        {
            public YargProfile Profile;

            /// <summary>
            /// The amount of sections that contained at least one note for this player's
            /// instrument. Empty sections can never be perfected, so they are not counted.
            /// </summary>
            public int ApplicableSectionCount;

            public int PerfectedThisRun;

            public IReadOnlyList<SectionCompletionResult> Results;

            /// <summary>
            /// The same results, shaped for the score screen.
            /// </summary>
            public PlayerSectionSummary Summary;
        }

        /// <summary>
        /// Builds the live section strip state of every player that is allowed to earn credit,
        /// and hands it to them.
        /// </summary>
        /// <remarks>
        /// The gates are the same ones <see cref="ScanSectionCompletions"/> applies at the end of
        /// the song, so a run that will never be recorded never gets a strip promising otherwise.
        /// <para>
        /// Called before the song starts, while nothing has been hit, so the scan's hit counts are
        /// all zero and only its note totals carry information: which sections have notes for this
        /// player, and therefore which ones get a block. Reusing the scanner for that keeps
        /// "applicable" defined in exactly one place.
        /// </para>
        /// </remarks>
        private void InitializeSectionStripStates()
        {
            // Slice 5 gates: the master switch turns the whole feature off, and ShowSectionStrip
            // hides just this surface while everything else keeps working
            if (!SettingsManager.Settings.TrackSectionCompletion.Value ||
                !SettingsManager.Settings.ShowSectionStrip.Value)
            {
                return;
            }

            if (IsPractice || GlobalVariables.State.PlayingWithReplay ||
                !ScoreContainer.IsBandScoreValid(SongSpeed))
            {
                return;
            }

            var sections = Chart.Sections;
            if (sections.Count == 0)
            {
                return;
            }

            foreach (var player in _players)
            {
                // Skip bots, replays, and anyone that's obviously cheating.
                if (player.Player.IsReplay || !ScoreContainer.IsSoloScoreValid(SongSpeed, player.Player))
                {
                    continue;
                }

                // Only highway players have a TrackView to draw the strip on. Vocals keep the
                // miss hook (see BasePlayer.NotifySectionNoteMissed) for a later vocals surface,
                // but building a state they cannot show is dead work.
                if (player is not TrackPlayer)
                {
                    continue;
                }

                var results = player.ScanSectionCompletion(sections);
                if (results is null)
                {
                    continue;
                }

                var profile = player.Player.Profile;
                var completedEarlier = ScoreContainer.GetCompletedSections(Song.Hash, profile.Id,
                    profile.CurrentInstrument, profile.CurrentDifficulty, profile.HarmonyIndex);

                player.SetSectionState(SectionStripState.Create(sections, results, completedEarlier));
            }
        }

        /// <summary>
        /// Turns the optimal Star Power path overlay on for the one human player, when the run
        /// qualifies for it.
        /// </summary>
        /// <remarks>
        /// Called right after <see cref="InitializeSectionStripStates"/>, for the same reason: the
        /// optimizer needs the post-modifier note track and the live engine parameters, neither of
        /// which exists before <c>CreatePlayers()</c> (<c>docs/sp-path-design.md</c> §4.1).
        /// <para>
        /// The band gate is §4.5: Star Power is coupled across players (the band multiplier and
        /// the unison bonus), so a single-player path is not merely approximate in a band run, it
        /// is wrong. Bots do not count towards the human total, so playing alongside one still
        /// gets an overlay.
        /// </para>
        /// <para>
        /// Practice and replays are excluded outright, the way the section strip excludes them.
        /// Practice because upstream swallows every Star Power input there
        /// (<c>FiveFretGuitarPlayer.InterceptInput</c>), so a path could never be followed;
        /// replays because the inputs are already fixed and an overlay telling the viewer what to
        /// press is meaningless.
        /// </para>
        /// </remarks>
        private void InitializeStarPowerPaths()
        {
            if (!SettingsManager.Settings.ShowStarPowerPath.Value)
            {
                return;
            }

            if (IsPractice)
            {
                YargLogger.LogInfo("SP path: skipped, practice mode");
                return;
            }

            if (GlobalVariables.State.PlayingWithReplay)
            {
                YargLogger.LogInfo("SP path: skipped, replay playback");
                return;
            }

            // §4.5. Sitting-out players are not in the run at all, and bots are not humans.
            int humanCount = _players.Count(p => !p.Player.SittingOut && !p.Player.Profile.IsBot);
            if (humanCount != 1)
            {
                YargLogger.LogFormatInfo("SP path: skipped, {0} human player(s) in this run",
                    humanCount);
                return;
            }

            foreach (var player in _players)
            {
                if (player.Player.SittingOut || player.Player.Profile.IsBot ||
                    player.Player.IsReplay)
                {
                    continue;
                }

                // Everything else (instrument support) is the player's own business, since it
                // also has to hold on a practice-section rebuild.
                player.EnableStarPowerPath();
            }
        }

        /// <summary>
        /// Scans the section completion of every player that is allowed to earn credit for it.
        /// </summary>
        /// <remarks>
        /// Run once, before the score screen data is built, so that the card and the database
        /// write share a single scan.
        /// </remarks>
        private Dictionary<BasePlayer, PendingSectionCompletion> ScanSectionCompletions()
        {
            var completions = new Dictionary<BasePlayer, PendingSectionCompletion>();

            // Slice 5 master switch. An empty result means nothing is written to the section
            // tables and every score card gets a null Sections, which hides the row, the strip
            // and the tag. Existing rows are left in the database untouched.
            if (!SettingsManager.Settings.TrackSectionCompletion.Value)
            {
                return completions;
            }

            // Same gate as the band score; an invalid band score means nothing gets recorded
            if (!ScoreContainer.IsBandScoreValid(SongSpeed))
            {
                return completions;
            }

            foreach (var player in _players)
            {
                // Skip bots and anyone that's obviously cheating.
                if (!ScoreContainer.IsSoloScoreValid(SongSpeed, player.Player))
                {
                    continue;
                }

                var completion = ScanSectionCompletion(player);
                if (completion != null)
                {
                    completions.Add(player, completion);
                }
            }

            return completions;
        }

        /// <summary>
        /// Determines which chart sections this player perfected, or <c>null</c> if this run
        /// cannot earn section completion credit.
        /// </summary>
        /// <remarks>
        /// This is only reached for full-song runs; <see cref="EndSong"/> returns early in
        /// practice mode, so practice never earns section completion credit.
        /// </remarks>
        private PendingSectionCompletion ScanSectionCompletion(BasePlayer player)
        {
            // Replays never earn credit, neither playback nor playing alongside one
            if (player.Player.IsReplay || GlobalVariables.State.PlayingWithReplay)
            {
                return null;
            }

            var sections = Chart.Sections;
            if (sections.Count == 0)
            {
                return null;
            }

            var results = player.ScanSectionCompletion(sections);
            if (results is null)
            {
                return null;
            }

            int perfectedThisRun = 0;
            int applicableCount = 0;
            foreach (var result in results)
            {
                if (result.NotesTotal <= 0)
                {
                    // A section with no notes for this instrument is not part of the total
                    continue;
                }

                applicableCount++;
                if (result.IsPerfected)
                {
                    perfectedThisRun++;
                }
            }

            if (applicableCount == 0)
            {
                // Nothing on this instrument lines up with the chart's sections
                return null;
            }

            var profile = player.Player.Profile;

            // The pre-run set is needed either way to tell "perfected earlier" blocks apart from
            // "perfected just now" ones, so the cumulative count is built from it rather than
            // waiting on the database write, which happens after the score screen data is built
            var completedBefore = ScoreContainer.GetCompletedSections(Song.Hash, profile.Id,
                profile.CurrentInstrument, profile.CurrentDifficulty, profile.HarmonyIndex);

            return new PendingSectionCompletion
            {
                Profile = profile,
                ApplicableSectionCount = applicableCount,
                PerfectedThisRun = perfectedThisRun,
                Results = results,
                Summary = BuildSectionSummary(results, applicableCount, completedBefore),
            };
        }

        /// <summary>
        /// Shapes a scan's results into the per-section states and counts the score card displays.
        /// </summary>
        private static PlayerSectionSummary BuildSectionSummary(IReadOnlyList<SectionCompletionResult> results,
            int applicableCount, HashSet<int> completedBefore)
        {
            var states = new List<SectionCompletionState>(applicableCount);
            var newlyCompleted = new List<int>();

            foreach (var result in results)
            {
                if (result.NotesTotal <= 0)
                {
                    // Sections with no notes get no block, matching the denominator
                    continue;
                }

                if (completedBefore.Contains(result.SectionIndex))
                {
                    states.Add(SectionCompletionState.CompletedEarlier);
                }
                else if (result.IsPerfected)
                {
                    states.Add(SectionCompletionState.CompletedThisRun);
                    newlyCompleted.Add(result.SectionIndex);
                }
                else
                {
                    states.Add(SectionCompletionState.Missing);
                }
            }

            // Derived from the states rather than from completedBefore.Count, so that rows left
            // behind by sections that are no longer applicable can never push the fraction past
            // what the strip actually shows
            int completedCount = 0;
            foreach (var state in states)
            {
                if (state != SectionCompletionState.Missing)
                {
                    completedCount++;
                }
            }

            return new PlayerSectionSummary
            {
                ApplicableCount = applicableCount,
                CompletedCount = completedCount,
                NewlyCompletedIndices = newlyCompleted.ToArray(),
                SectionStates = states.ToArray(),
            };
        }

        /// <summary>
        /// Writes the collected section completions to the database and logs the cumulative progress.
        /// </summary>
        private void RecordSectionCompletions(IEnumerable<PendingSectionCompletion> completions)
        {
            foreach (var completion in completions)
            {
                var profile = completion.Profile;
                int sectionCount = completion.ApplicableSectionCount;

                bool success = ScoreContainer.RecordSectionCompletions(Song.Hash, profile.Id,
                    profile.CurrentInstrument, profile.CurrentDifficulty, profile.HarmonyIndex,
                    sectionCount, completion.Results, out int completedTotal);

                if (!success)
                {
                    // The failure itself is already logged; don't follow it with a bogus total
                    continue;
                }

                YargLogger.LogFormatInfo(
                    "Section FC ({0}, {1}): {2}/{3} sections perfected this run, {4}/{5} cumulative.",
                    profile.Name, profile.CurrentInstrument, completion.PerfectedThisRun, sectionCount,
                    completedTotal, sectionCount);
            }
        }

        public void ForceQuitSong()
        {
            GlobalVariables.State = PersistentState.Default;
            GlobalVariables.Instance.LoadScene(SceneIndex.Menu);
        }

        public void SetVenueCameraManager(CameraManager cameraManager)
        {
            VenueCameraManager = cameraManager;
            InitializeCameraDebug();
        }

        public void SetVenueCharacterManager(CharacterManager characterManager)
        {
            VenueCharacterManager = characterManager;
            InitializeCharacterDebug();
        }

        public void SetEditHUD(bool on)
        {
            if (on)
            {
                _pauseMenu.gameObject.SetActive(false);
                _draggableHud.SetEditHUD(true);
            }
            else
            {
                _draggableHud.SetEditHUD(false);
                _pauseMenu.gameObject.SetActive(true);
            }
        }

#nullable enable
        public ReplayInfo? SaveReplay(double length, string directory)
#nullable disable
        {
            if (_isReplaySaved)
            {
                return null;
            }

            var frames = new List<ReplayFrame>(_players.Count);
            var replayStats = new List<ReplayStats>(_players.Count);
            var colorProfiles = new Dictionary<Guid, ColorProfile>();
            var cameraPresets = new Dictionary<Guid, CameraPreset>();
            var rockMeterPresets = new Dictionary<Guid, RockMeterPreset>();

            int bandScore = 0;
            float bandStars = EngineManager.Stars;
            for (int i = 0; i < _players.Count; i++)
            {
                var player = _players[i];
                if (player.Player.Profile.IsBot)
                {
                    continue;
                }

                var (frame, stats) = player.ConstructReplayData();
                frames.Add(frame);
                replayStats.Add(stats);
                bandScore += player.Score;

                if (!player.Player.ColorProfile.DefaultPreset)
                {
                    colorProfiles.TryAdd(player.Player.ColorProfile.Id, player.Player.ColorProfile);
                }

                if (!player.Player.CameraPreset.DefaultPreset)
                {
                    cameraPresets.TryAdd(player.Player.CameraPreset.Id, player.Player.CameraPreset);
                }

                if (!player.Player.RockMeterPreset.DefaultPreset)
                {
                    rockMeterPresets.TryAdd(player.Player.RockMeterPreset.Id, player.Player.RockMeterPreset);
                }
            }

            if (frames.Count == 0)
            {
                return null;
            }

            var noFail = SettingsManager.Settings.NoFail.Value != NoFailMode.Off;
            var stars = StarAmountHelper.GetStarsFromInt(Mathf.FloorToInt(bandStars));
            ReplayData = new ReplayData(colorProfiles, cameraPresets, rockMeterPresets, noFail, frames.ToArray(), _frameTimes.ToArray());

            (bool success, var replayInfo) = ReplayIO.TrySerialize(directory, Song, SongSpeed, length, bandScore, stars, PauseInfo.ToArray(), SettingsManager.Settings.CensorMatureContent.Value, replayStats.ToArray(), ReplayData);
            if (!success)
            {
                return null;
            }

            ReplayContainer.AddEntry(replayInfo);
            _isReplaySaved = true;
            return replayInfo;
        }

        private void OnNavigationEvent(NavigationContext context)
        {
            switch (context.Action)
            {
                // Pause
                case MenuAction.Start:
                    if (_draggableHud.EditMode)
                    {
                        SetEditHUD(false);
                    }

                    if ((!IsPractice || PracticeManager.HasSelectedSection) && !DialogManager.Instance.IsDialogShowing && !PlayerHasFailed)
                    {
                        SetPaused(!_songRunner.Paused);
                    }
                    break;
            }
        }

        private void OnApplicationFocus(bool hasFocus)
        {
            if (!hasFocus && !Paused && SettingsManager.Settings.PauseOnFocusLoss.Value)
            {
                SetPaused(true);
            }
        }

        public void ResetBandCombo()
        {
            switch (_bandComboType)
            {
                case BandComboType.Strict:
                    BandCombo = 0;
                break;
                case BandComboType.Lenient:
                    BandCombo = Players.Sum(e => e.Combo * e.BaseStats.BandComboUnits);
                break;
            }
        }

        public void AddBandCombo(int amount)
        {
            BandCombo += amount;
        }

        private async void OnSongFailed()
        {
            if (SettingsManager.Settings.NoFail.Value != NoFailMode.Off || IsPractice)
            {
                return;
            }

            if (!PlayerHasFailed)
            {
                PlayerHasFailed = true;

                if (_players.Count > 1)
                {
                    // For some reason you seem to need this many frames to pass before pause for every highway to lower?
                    await UniTask.DelayFrame(_players.Count - 1);
                }

                // Pause gameplay immediately, but don't show the menu until the highways have lowered
                _songRunner.Pause();
                _mixer.FadeOut(SONG_END_DELAY);
                await UniTask.Delay(TimeSpan.FromSeconds(SONG_END_DELAY));
                GlobalAudioHandler.PlayVoxSample(VoxSample.FailSound);
                Pause();
            }
        }

        public void UnfailSong()
        {
            YargLogger.LogFormatDebug("Unfailing song at SongTime {0}", SongTime);
            PlayerHasFailed = false;
            EngineManager.RevivePlayer();
            EngineManager.NoFailChanged(true);
            _mixer.FadeIn(DEFAULT_VOLUME, SONG_START_DELAY);
            InvalidateScores("Menu.Toast.ResumeAfterFailInvalidate");
            // This is an arbitrary value, just want to give players enough time to adjust
            Resume(SONG_START_DELAY + 1);
        }
        // If we go from no fail to fail, we need to reinitialize the happiness state so we avoid
        // the possibility of an instant fail. Yes, this is cheeseable since toggling no fail resets happiness.
        private void OnNoFailModeChanged(NoFailMode mode)
        {
            // If we're going from no fail to fail and happiness would result in a player being in the red, reset happiness
            if (mode == NoFailMode.Off && EngineManager.GetLowestHappiness()?.Happiness <= 0.333f)
            {
                EngineManager.InitializeHappiness(false);
            }

            InvalidateScores("Menu.Toast.NoFailScore");

            EngineManager.NoFailChanged(mode != NoFailMode.Off);
            _failMeter.SetActive(mode != NoFailMode.NoMeter);
        }

        internal void InvalidateScores(string toastKey)
        {
            bool invalidated = false;

            foreach (var player in _players)
            {
                if (player.Player.IsScoreValid)
                {
                    invalidated = true;
                }

                player.Player.IsScoreValid = false;

                // Nothing from here on can be recorded, so the strip would be promising credit
                // that this run can no longer earn. Dropping the state hides it.
                player.SetSectionState(null);
            }

            if (invalidated && !string.IsNullOrEmpty(toastKey))
            {
                ToastManager.ToastWarning(Localize.Key(toastKey));
            }
        }

        private void CheckForRewindInvalidation()
        {
            if (PauseInfo.Count == 0)
            {
                return;
            }

            // If there is more than MAXIMUM_REWIND_TIME seconds of rewind in MAXIMUM_REWIND_WINDOW of song time, invalidate scores
            var start = 0;

            for (var end = 0; end < PauseInfo.Count; end++)
            {
                var endTime = PauseInfo[end].PauseTime;

                while (PauseInfo[start].PauseTime < endTime - MAXIMUM_REWIND_WINDOW)
                {
                    start++;
                }

                var pauses = end - start + 1;

                if (pauses * PAUSE_REWIND_LENGTH > MAXIMUM_REWIND_TIME)
                {
                    InvalidateScores("Menu.Toast.TooManyPauses");
                    return;
                }
            }
        }

        private async UniTask<bool> RewindAndResume(double seconds)
        {
            YargLogger.LogFormatDebug("Rewinding {0} seconds at VisualTime {1}", seconds, VisualTime);

            if (_lyricBar.gameObject.activeSelf)
            {
                _lyricBar.Rewind(VisualTime - seconds, 0.5f);
            }

            // Rewind players
            foreach (var player in _players)
            {
                player.Rewind(VisualTime - seconds);
            }

            double? targetTime = null;
            if (PauseInfo.Count > 0)
            {
                targetTime = PauseInfo[^1].PauseTime;
            }

            var canceled = await _songRunner.RewindAndResume(seconds, targetTime);

            if (canceled)
            {
                return true;
            }

            foreach (var player in _players)
            {
                player.PostRewind(VisualTime - seconds);
            }

            CheckForRewindInvalidation();

            return false;
        }

        private void OnUnisonPhraseSuccess()
        {
            if (_unisonDisplay.gameObject.activeSelf)
            {
                _unisonDisplay.OnUnisonPhraseSuccess();
            }
        }

        public void StartCoda(CodaSection _)
        {
            _breBox.StartCoda(EngineManager);
        }

        public void EndCoda(CodaSection coda)
        {
            var songEnding = SongTime >= LastNoteTime;
            _breBox.EndCoda(EngineManager.TotalCodaBonus, songEnding, null);
        }

        public void ResetCoda()
        {
            _breBox.ForceReset();
        }
    }
}
