using System.Collections.Generic;
using UnityEngine;
using YARG.Core.Chart;
using YARG.Gameplay;

namespace YARG.Venue.Stage
{
    public class StageManager : GameplayBehaviour
    {
        [SerializeField]
        private GameObject _venue;

        private StageElement[] _stageElements;
        private bool           _hasStageEvents;

        private List<StageEffectEvent> _stageEvents;
        private int                    _stageEventIndex = 0;

        private List<StageElement> _pyroElements = new();
        private List<StageElement> _fogElements = new();

        protected override void OnChartLoaded(SongChart chart)
        {
            if (_venue == null)
            {
                return;
            }

            _stageElements = _venue.GetComponentsInChildren<StageElement>();

            if (_stageElements.Length == 0)
            {
                return;
            }

            foreach (var stageElement in _stageElements)
            {
                if (stageElement.ElementType == StageElementType.Pyro)
                {
                    _pyroElements.Add(stageElement);
                }
                else if (stageElement.ElementType == StageElementType.Fog)
                {
                    _fogElements.Add(stageElement);
                }
            }

            _stageEvents = chart.VenueTrack.Stage;
            if (_stageEvents.Count > 0)
            {
                _hasStageEvents = true;
            }

            GameManager.SetVenueStageManager(this);
        }

        /// <summary>
        /// Seeks the stage effects to <paramref name="time"/>, in either direction.
        /// </summary>
        /// <remarks>
        /// Only the fog latches, so only the fog is reproduced: bonus FX is a one-shot pyro burst
        /// and is deliberately not re-fired while the events replay, because a seek shows the
        /// state the stage is in and not the bursts that got it there.
        /// </remarks>
        public void ResetTime(double time)
        {
            if (!_hasStageEvents)
            {
                return;
            }

            bool fogOn = false;

            _stageEventIndex = 0;
            while (_stageEventIndex < _stageEvents.Count &&
                _stageEvents[_stageEventIndex].Time <= time)
            {
                switch (_stageEvents[_stageEventIndex].Effect)
                {
                    case StageEffect.FogOn:
                        fogOn = true;
                        break;
                    case StageEffect.FogOff:
                        fogOn = false;
                        break;
                }

                _stageEventIndex++;
            }

            // A burst that was still going when the seek landed belongs to the discarded timeline.
            foreach (var stageElement in _pyroElements)
            {
                stageElement.StopEffect();
            }

            foreach (var stageElement in _fogElements)
            {
                if (fogOn)
                {
                    stageElement.StartEffect();
                }
                else
                {
                    stageElement.StopEffect();
                }
            }
        }

        private void Update()
        {
            if (!_hasStageEvents)
            {
                return;
            }

            // Check for stage events and dispatch them as required
            while (_stageEventIndex < _stageEvents.Count &&
                _stageEvents[_stageEventIndex].Time <= GameManager.VisualTime)
            {
                var stageEvent = _stageEvents[_stageEventIndex];
                _stageEventIndex++;

                // TODO: Handle optional once the fail meter exists
                switch (stageEvent.Effect)
                {
                    case StageEffect.BonusFx:
                        TriggerBonusFx(stageEvent);
                        break;
                    case StageEffect.FogOn:
                        StartFog(stageEvent);
                        break;
                    case StageEffect.FogOff:
                        StopFog(stageEvent);
                        break;
                }
            }
        }

        private void TriggerBonusFx(StageEffectEvent stageEvent)
        {
            foreach (var stageElement in _pyroElements)
            {
                stageElement.StartEffect();
            }
        }

        private void StartFog(StageEffectEvent stageEvent)
        {
            foreach (var stageElement in _fogElements)
            {
                stageElement.StartEffect();
            }
        }

        private void StopFog(StageEffectEvent stageEvent)
        {
            foreach (var stageElement in _fogElements)
            {
                stageElement.StopEffect();
            }
        }
    }


}