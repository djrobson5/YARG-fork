using System;
using System.Collections.Generic;
using UnityEngine;
using YARG.Core.Chart;
using YARG.Core.Extensions;
using YARG.Gameplay;
using YARG.Playback;
using YARG.Settings;
using Random = UnityEngine.Random;

namespace YARG.Venue
{
    public partial class LightManager : GameplayBehaviour
    {
        public struct LightState
        {
            /// <summary>
            /// The intensity of the light between <c>0</c> and <c>1</c>. <c>1</c> is the default value.
            /// </summary>
            public float Intensity;

            /// <summary>
            /// The color of the light. <see cref="Intensity"/> should be taken into consideration.
            /// <c>null</c> indicates default.
            /// </summary>
            public Color? Color;

            public float Delta;
        }

        private readonly Dictionary<Performer, VenueSpotLightLocation> _spotlightLocations = new()
        {
            { Performer.Bass, VenueSpotLightLocation.Bass },
            { Performer.Drums, VenueSpotLightLocation.Drums },
            { Performer.Guitar, VenueSpotLightLocation.Guitar },
            { Performer.Vocals, VenueSpotLightLocation.Vocals },
        };

        public LightingType Animation { get; private set; }
        public int AnimationFrame { get; private set; }


        // Double because spots stay on for the duration of the event and then turn off without an off event, so we store time
        private double[]     _spotlightStates;
        private LightState[] _lightStates;
        public  LightState   GenericLightState => _lightStates[(int) VenueLightLocation.Generic];
		public  LightState   LeftLightState    => _lightStates[(int) VenueLightLocation.Left];
		public  LightState   RightLightState   => _lightStates[(int) VenueLightLocation.Right];
		public  LightState   FrontLightState   => _lightStates[(int) VenueLightLocation.Front];
		public  LightState   BackLightState    => _lightStates[(int) VenueLightLocation.Back];
		public  LightState   CenterLightState  => _lightStates[(int) VenueLightLocation.Center];
		public  LightState   CrowdLightState   => _lightStates[(int) VenueLightLocation.Crowd];

        [SerializeField]
        private float _gradientLightingSpeed = 0.125f;

		private float _initialGradientSpeed;

        [SerializeField]
        private float _gradientRandomness = 0.5f;

        [Space]
        [SerializeField]
        private Color[] _warmColors;
        [SerializeField]
        private Color[] _coolColors;
        [SerializeField]
        private Color[] _dissonantColors;
        [SerializeField]
        private Color[] _harmoniousColors;
        [SerializeField]
        private Color _silhouetteColor;

        private List<LightingEvent>  _lightingEvents;
        private List<PerformerEvent> _performerEvents;

        private Gradient _warmGradient;
        private Gradient _coolGradient;
        private Gradient _dissonantGradient;
        private Gradient _harmoniousGradient;

        private        int  _lightingEventIndex;
        private        int  _performerEventIndex;
        private        int  _beatIndex;
        private static bool ReducedFlashing => SettingsManager.Settings.ReduceFlashingLights.Value;

        // Minimum interval between lighting/performer events when reduce flashing is enabled
        private const float REDUCED_FLASHING_LIGHT_INTERVAL = 1.0f;

        protected override void OnChartLoaded(SongChart chart)
        {
            _lightStates = new LightState[EnumExtensions<VenueLightLocation>.Count];
            _spotlightStates = new double[EnumExtensions<VenueSpotLightLocation>.Count];

            var lightingEvents = chart.VenueTrack.Lighting;
            var performerEvents = chart.VenueTrack.Performer;

            if (ReducedFlashing)
            {
                (lightingEvents, performerEvents) = ReduceFlashingEvents(lightingEvents, performerEvents);
            }

            _lightingEvents = lightingEvents;
            _performerEvents = performerEvents;

            // If the color arrays are empty, add basic ones for safety

            if (_warmColors is not { Length: > 0 })
            {
                _warmColors = new[]
                {
                    Color.red,
                    Color.yellow
                };
            }

            if (_coolColors is not { Length: > 0 })
            {
                _coolColors = new[]
                {
                    Color.blue,
                    Color.green
                };
            }

            if (_dissonantColors is not { Length: > 0 })
            {
                _dissonantColors = new[]
                {
                    Color.red,
                    Color.green,
                    Color.blue,
                };
            }

            if (_harmoniousColors is not { Length: > 0 })
            {
                _harmoniousColors = new[]
                {
                    Color.yellow,
                    Color.red,
                    Color.blue,
                };
            }

			// Store gradient speed for temporary Frenzy/BRE speedup
			_initialGradientSpeed = _gradientLightingSpeed;

            // Setup gradients
            _warmGradient = CreateGradient(_warmColors);
            _coolGradient = CreateGradient(_coolColors);
            _dissonantGradient = CreateGradient(_dissonantColors);
            _harmoniousGradient = CreateGradient(_harmoniousColors);

            // 1/8th of a beat is a 32nd note
            GameManager.BeatEventHandler.Visual.Subscribe(UpdateLightAnimation, BeatEventType.QuarterNote, division: 1f / 8f);

            GameManager.SetVenueLightManager(this);
        }

        protected override void GameplayDestroy()
        {
            GameManager.BeatEventHandler.Visual.Unsubscribe(UpdateLightAnimation);
        }

        private static (List<LightingEvent> LightingEvents, List<PerformerEvent> PerformerEvents) ReduceFlashingEvents(
            List<LightingEvent> lightingEvents, List<PerformerEvent> performerEvents)
        {
            var replacedLightingEvents = ReplaceFlashingLightingEvents(lightingEvents);
            var filteredLightingEvents = ChartEvent.FilterByInterval(
                replacedLightingEvents,
                REDUCED_FLASHING_LIGHT_INTERVAL
            );
            var filteredPerformerEvents = ChartEvent.FilterByInterval(
                performerEvents,
                REDUCED_FLASHING_LIGHT_INTERVAL
            );

            return (filteredLightingEvents, filteredPerformerEvents);
        }

        /// <summary>
        /// Replaces flashing lighting types with reduced equivalents.
        /// </summary>
        private static List<LightingEvent> ReplaceFlashingLightingEvents(List<LightingEvent> events)
        {
            var mapped = new List<LightingEvent>(events.Count);
            foreach (var ev in events)
            {
                var replacement = GetReducedLightingType(ev.Type);
                mapped.Add(replacement.HasValue
                    ? new LightingEvent(replacement.Value, ev.Time, ev.Tick)
                    : ev);
            }

            return mapped;
        }

        private static LightingType? GetReducedLightingType(LightingType type) => type switch
        {
            LightingType.StrobeFastest => LightingType.Harmony,
            LightingType.StrobeFast => LightingType.Harmony,
            LightingType.StrobeMedium => LightingType.Harmony,
            LightingType.StrobeSlow => LightingType.Harmony,
            LightingType.FlareFast => LightingType.FlareSlow,
            LightingType.BlackoutFast => LightingType.Harmony,
            LightingType.BlackoutSlow => LightingType.Harmony,
            LightingType.BlackoutSpotlight => LightingType.Harmony,
            LightingType.BigRockEnding => LightingType.Chorus,
            LightingType.Frenzy => LightingType.Chorus,
            _ => null,
        };

        private void Update()
        {
            // Look for new lighting events
            while (_lightingEventIndex < _lightingEvents.Count &&
                _lightingEvents[_lightingEventIndex].Time <= GameManager.VisualTime)
            {
                ApplyLightingEvent(_lightingEvents[_lightingEventIndex]);

                _lightingEventIndex++;
            }

            // Decrement the spotlight times
            for (int i = 0; i < _spotlightStates.Length; i++)
            {
                if (_spotlightStates[i] <= 0)
                {
                    continue;
                }

                _spotlightStates[i] -= Time.deltaTime;
            }

            // Look for new performer events
            // TODO: Fix the event parsing so that Time and TimeEnd aren't backwards (with the attendant negative length)
            while (_performerEventIndex < _performerEvents.Count &&
                _performerEvents[_performerEventIndex].Time <= GameManager.VisualTime)
            {
                var current = _performerEvents[_performerEventIndex];
                if (current.Type != PerformerEventType.Spotlight)
                {
                    _performerEventIndex++;
                    continue;
                }
                if (!_spotlightLocations.TryGetValue(current.Performers, out var location))
                {
                    _performerEventIndex++;
                    continue;
                }

                _spotlightStates[(int) location] = current.TimeLength;

                _performerEventIndex++;
            }

            UpdateLightStates();
        }

        private void ApplyLightingEvent(LightingEvent current)
        {
            switch (current.Type)
            {
                case LightingType.KeyframeNext:
                    AnimationFrame++;
                    break;
                case LightingType.KeyframePrevious:
                    AnimationFrame--;
                    break;
                case LightingType.KeyframeFirst:
                    AnimationFrame = 0;
                    break;
                case LightingType.WarmAutomatic:
                case LightingType.WarmManual:
                case LightingType.CoolAutomatic:
                case LightingType.CoolManual:
                case LightingType.Verse:
                case LightingType.Chorus:
                case LightingType.Searchlights:
                    // Add a slight randomness to colored cues
                    for (int i = 0; i < _lightStates.Length; i++)
                    {
                        _lightStates[i].Delta = Random.Range(0f, _gradientRandomness);
                    }

                    goto default;
                default:
                    Animation = current.Type;
                    AnimationFrame = 0;
                    break;
            }
        }

        /// <summary>
        /// Seeks the lighting to <paramref name="time"/>, in either direction.
        /// </summary>
        /// <remarks>
        /// Modelled on <see cref="VenueCamera.CameraManager.ResetTime"/>, but lighting cannot be
        /// seeked by parking a cursor: the cue, the keyframe position and the per-location
        /// gradient deltas are all <i>state</i> that the events build up, so the only way to know
        /// what the lights looked like at <paramref name="time"/> is to put everything back to its
        /// initial state and replay the events from zero.
        /// <para>
        /// The replay is silent by construction rather than by a flag: every lighting consumer
        /// (<see cref="VenueLight"/>, <see cref="NeonLightManager"/>) polls
        /// <see cref="GetLightStateFor"/> once per frame instead of being called back, and no
        /// lighting event is a one-shot, so nothing fires while the events run and only the state
        /// they leave behind is ever seen.
        /// </para>
        /// </remarks>
        public void ResetTime(double time)
        {
            // Defensive: the registration that makes this reachable happens inside OnChartLoaded,
            // after the event lists are assigned.
            if (_lightingEvents is null)
            {
                return;
            }

            // Back to the initial state, then replay.
            Animation = LightingType.Default;
            AnimationFrame = 0;
            _gradientLightingSpeed = _initialGradientSpeed;
            Array.Clear(_lightStates, 0, _lightStates.Length);
            Array.Clear(_spotlightStates, 0, _spotlightStates.Length);

            // Clearing leaves Intensity at zero, but full brightness is the documented default and
            // the one the `default:` arm of UpdateLightStates restores. Without this every seek -
            // including the replay scrub and a practice section change, neither of which has a
            // fade to hide it - lerps the venue up from black over several frames.
            for (int i = 0; i < _lightStates.Length; i++)
            {
                _lightStates[i].Intensity = 1f;
            }

            _lightingEventIndex = 0;
            while (_lightingEventIndex < _lightingEvents.Count &&
                _lightingEvents[_lightingEventIndex].Time <= time)
            {
                ApplyLightingEvent(_lightingEvents[_lightingEventIndex]);

                _lightingEventIndex++;
            }

            // Spotlights have no off event - they elapse - so instead of latching every one that
            // has ever started, only the ones still running at `time` come back, with the time
            // they have left rather than their full length.
            _performerEventIndex = 0;
            while (_performerEventIndex < _performerEvents.Count &&
                _performerEvents[_performerEventIndex].Time <= time)
            {
                var current = _performerEvents[_performerEventIndex];
                _performerEventIndex++;

                if (current.Type != PerformerEventType.Spotlight ||
                    !_spotlightLocations.TryGetValue(current.Performers, out var location))
                {
                    continue;
                }

                double remaining = current.Time + current.TimeLength - time;
                if (remaining > 0)
                {
                    _spotlightStates[(int) location] = remaining;
                }
            }

            _beatIndex = GetBeatIndexAt(time);
        }

        /// <summary>
        /// The value <see cref="_beatIndex"/> would have reached by <paramref name="time"/>.
        /// </summary>
        /// <remarks>
        /// Keyframed cues step on beats, so a seek that left the counter at zero would put the
        /// strobe cues a 32nd note out of phase. The counter cannot be read back off the beat
        /// handler here, because <c>BeatEventHandler.Reset</c> - which the seek has already run -
        /// clears the fired-count without recomputing the progress, so it is derived from the sync
        /// track the same way the handler derives it.
        /// </remarks>
        private int GetBeatIndexAt(double time)
        {
            if (time <= 0)
            {
                return 0;
            }

            var sync = GameManager.Chart.SyncTrack;

            // The subscription above is a quarter note at 1/8th division, so one event per 32nd
            // note. The count itself, with no + 1: the Reset the seek has already run re-arms the
            // event, so the next Visual.Update fires the callback for the beat the seek landed in
            // once more, and that is the increment that brings the counter level with the live
            // path.
            return (int) (sync.GetQuarterNotePosition(sync.TimeToTick(time)) * 8);
        }

        private void UpdateLightAnimation()
        {
            _beatIndex++;

            switch (Animation)
            {
                case LightingType.StrobeFast:
                    AnimationFrame++;
                    break;
                case LightingType.StrobeSlow:
                    if (_beatIndex % 2 == 1)
                    {
                        AnimationFrame++;
                    }

                    break;
            }
        }

        private void UpdateLightStates()
        {
            for (int i = 0; i < _lightStates.Length; i++)
            {
                var location = (VenueLightLocation) i;

                switch (Animation)
                {
					case LightingType.Default:
						_lightStates[i] = Default(_lightStates[i], location, _harmoniousGradient);
						break;
                    case LightingType.Verse:
                        _lightStates[i] = AutoGradientSplit(_lightStates[i], location, _harmoniousGradient, _dissonantGradient);
						_gradientLightingSpeed = _initialGradientSpeed;
                        break;
                    case LightingType.Chorus:
                        _lightStates[i] = AutoGradientSplit(_lightStates[i], location, _warmGradient, _coolGradient);
						_gradientLightingSpeed = _initialGradientSpeed;
                        break;
                    case LightingType.BlackoutFast:
                        _lightStates[i] = BlackOut(_lightStates[i], 30f);
                        break;
                    case LightingType.BlackoutSlow:
                        _lightStates[i] = BlackOut(_lightStates[i], 5f);
                        break;
                    case LightingType.BlackoutSpotlight:
                        _lightStates[i] = BlackOutSpot(_lightStates[i], 30f, location);
                        break;
                    case LightingType.Dischord:
						_lightStates[i] = AutoGradient(_lightStates[i], location, _dissonantGradient);
						_gradientLightingSpeed = _initialGradientSpeed;
						break;
                    case LightingType.BigRockEnding:
                        _lightStates[i] = AutoGradientSplit(_lightStates[i], location, _dissonantGradient, _harmoniousGradient);
						_gradientLightingSpeed = _initialGradientSpeed*16f;
                        break;
                    case LightingType.Frenzy:
                        _lightStates[i] = AutoGradientSplit(_lightStates[i], location, _dissonantGradient, _harmoniousGradient);
						_gradientLightingSpeed = _initialGradientSpeed*8f;
                        break;
                    case LightingType.CoolAutomatic:
                    case LightingType.CoolManual:
					case LightingType.Sweep:
                        _lightStates[i] = AutoGradient(_lightStates[i], location, _coolGradient);
						_gradientLightingSpeed = _initialGradientSpeed;
                        break;
                    case LightingType.FlareFast:
                        _lightStates[i] = Flare(_lightStates[i], 30f);
                        break;
                    case LightingType.FlareSlow:
                        _lightStates[i] = Flare(_lightStates[i], 5f);
                        break;
                    case LightingType.Harmony:
                        _lightStates[i] = AutoGradient(_lightStates[i], location, _harmoniousGradient);
						_gradientLightingSpeed = _initialGradientSpeed;
                        break;
                    case LightingType.Silhouettes:
                        _lightStates[i] = Silhouette(_lightStates[i], location);
                        break;
                    case LightingType.SilhouettesSpotlight:
                        _lightStates[i] = SilhouetteSpot(_lightStates[i], location);
                        break;
					case LightingType.Searchlights:
						_lightStates[i] = Searchlights(_lightStates[i], location, _warmGradient);
						_gradientLightingSpeed = _initialGradientSpeed;
						break;
                    case LightingType.StrobeFast:
                    case LightingType.StrobeSlow:
                        _lightStates[i] = Strobe(_lightStates[i]);
                        break;
                    case LightingType.Stomp:
						_lightStates[i] = Stomp(_lightStates[i], _warmGradient);
						break;
                    case LightingType.WarmAutomatic:
                    case LightingType.WarmManual:
                        _lightStates[i] = AutoGradient(_lightStates[i], location, _warmGradient);
						_gradientLightingSpeed = _initialGradientSpeed;
                        break;
                    default:
                        _lightStates[i].Intensity = 1f;
                        _lightStates[i].Color = null;
                        _lightStates[i].Delta = 0f;
                        break;
                }
            }
        }

        public LightState GetLightStateFor(VenueLightLocation location)
        {
            return _lightStates[(int) location];
        }

        public bool GetSpotlightStateFor(VenueSpotLightLocation location)
        {
            return _spotlightStates[(int) location] > 0;
        }

        private static Gradient CreateGradient(Color[] colors)
        {
            var gradient = new Gradient();

            var keys = new GradientColorKey[colors.Length + 1];

            // Make the gradient loop nice without snapping
            keys[0] = new GradientColorKey(colors[^1], 0f);

            // Add the rest of the colors
            for (int i = 1; i < keys.Length; i++)
            {
                keys[i] = new GradientColorKey(colors[i - 1], 1f / colors.Length * i);
            }

            // No alpha for gradient
            gradient.SetKeys(keys, new[]
            {
                new GradientAlphaKey(1f, 0f)
            });

            return gradient;
        }
    }
}
