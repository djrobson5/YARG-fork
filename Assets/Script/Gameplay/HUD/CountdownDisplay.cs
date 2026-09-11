using System.Collections;
using TMPro;
using DG.Tweening;
using UnityEngine;
using UnityEngine.UI;
using System;

namespace YARG.Gameplay.HUD
{
    public enum CountdownDisplayMode
    {
        Disabled,
        Measures,
        Seconds
    }

    public class CountdownDisplay : GameplayBehaviour
    {
        private const float FADE_ANIM_LENGTH = 0.5f;
        private const double HIDE_DELAY = 1;

        public static CountdownDisplayMode DisplayStyle;

        [SerializeField]
        private Image _backgroundCircle;
        [SerializeField]
        private TextMeshProUGUI _countdownText;
        [SerializeField]
        private Image _progressBar;

        [Space]
        [SerializeField]
        private CanvasGroup _canvasGroup;

        private Coroutine _currentCoroutine;

        private bool _displayActive;
        private int _displayedCountdownValue = int.MinValue;

        public void UpdateCountdown(double countdownLength, double endTime)
        {
            if (DisplayStyle == CountdownDisplayMode.Disabled)
            {
                return;
            }

            double currentTime = GameManager.SongTime;
            double timeRemaining = endTime - currentTime;
            if (timeRemaining < 0)
            {
                return;
            }
            bool shouldDisplay = timeRemaining > HIDE_DELAY + FADE_ANIM_LENGTH;

            if (GameManager.IsPractice)
            {
                double sectionStartTime = GameManager.PracticeManager.TimeStart;
                if (currentTime <= sectionStartTime)
                {
                    // Do not show a countdown before the start of a practice section
                    // where all of the notes before that section are removed for practice stats
                    shouldDisplay = false;
                }
            }

            ToggleDisplay(shouldDisplay);

            if (!gameObject.activeSelf)
            {
                return;
            }

            switch (DisplayStyle)
            {
                case CountdownDisplayMode.Seconds:
                {
                    SetCountdownValue((int) Math.Ceiling(timeRemaining));
                    break;
                }
                case CountdownDisplayMode.Measures:
                {
                    var syncTrack = GameManager.Chart.SyncTrack;
                    // This is floored to snap the end time to the start of the measure
                    double endMeasure = Math.Floor(syncTrack.GetMeasurePosition(endTime));
                    double currentMeasure = syncTrack.GetMeasurePosition(currentTime);
                    int remainingMeasures = (int) Math.Ceiling(endMeasure - currentMeasure);
                    SetCountdownValue(remainingMeasures);
                    break;
                }
            }

            _progressBar.fillAmount = (float) (timeRemaining / countdownLength);
        }

        /// <summary>
        /// Drives the widget for a rewind lead-in, counting down to the section marker.
        /// </summary>
        /// <param name="countdownLength">
        /// The whole lead-in window, in song seconds (<c>leadIn * SongSpeed</c>), so the ring
        /// empties exactly once over the window.
        /// </param>
        /// <param name="endSongTime">The section marker, in song time.</param>
        /// <remarks>
        /// Deliberately not <see cref="UpdateCountdown"/>: a lead-in is forced on regardless of the
        /// player's Countdown Display setting, and both the 1.5 s early-hide and the
        /// <see cref="CountdownDisplayMode.Disabled"/> style are bypassed, so the countdown runs
        /// all the way to the marker at every lead-in length
        /// (<c>docs/rewind-design.md</c>, "Lead-in" -&gt; Countdown).
        /// <para>
        /// The digits are always in <i>real</i> seconds and always numeric: the setting is in real
        /// seconds, and a 0.5 to 5 s window has no meaningful measure count.
        /// </para>
        /// </remarks>
        public void UpdateLeadInCountdown(double countdownLength, double endSongTime)
        {
            double timeRemaining = endSongTime - GameManager.SongTime;

            // No HIDE_DELAY here: the widget stays up until the marker itself.
            ToggleDisplay(timeRemaining > 0);

            if (!gameObject.activeSelf || timeRemaining <= 0)
            {
                return;
            }

            float songSpeed = GameManager.SongSpeed;
            double realSecondsRemaining = songSpeed > 0 ? timeRemaining / songSpeed : timeRemaining;
            SetCountdownValue((int) Math.Ceiling(realSecondsRemaining));

            _progressBar.fillAmount = countdownLength > 0
                ? Mathf.Clamp01((float) (timeRemaining / countdownLength))
                : 0f;
        }

        public void ForceReset()
        {
            StopCurrentCoroutine();

            _canvasGroup.alpha = 0f;
            gameObject.SetActive(true);
            _displayActive = false;
            _displayedCountdownValue = int.MinValue;
        }

        private void SetCountdownValue(int value)
        {
            if (_displayedCountdownValue == value)
            {
                return;
            }

            _displayedCountdownValue = value;
            _countdownText.SetText(value.ToString());
        }

        private void ToggleDisplay(bool isActive)
        {
            if (isActive == _displayActive)
            {
                return;
            }

            _displayActive = isActive;

            StopCurrentCoroutine();

            if (isActive)
            {
                _canvasGroup.alpha = 0f;
                gameObject.SetActive(true);

                _currentCoroutine = StartCoroutine(ShowCoroutine());
            }
            else
            {
                if (_canvasGroup.alpha == 0f)
                {
                    // Do not animate a fade out if this is already invisible
                    gameObject.SetActive(false);
                    return;
                }

                _currentCoroutine = StartCoroutine(HideCoroutine());
            }
        }

        private IEnumerator ShowCoroutine()
        {
            // Fade in
            yield return _canvasGroup
                .DOFade(1f, FADE_ANIM_LENGTH)
                .WaitForCompletion();
        }

        private IEnumerator HideCoroutine()
        {
            // Fade out
            yield return _canvasGroup
                .DOFade(0f, FADE_ANIM_LENGTH)
                .WaitForCompletion();

            gameObject.SetActive(false);
            _currentCoroutine = null;
        }

        private void StopCurrentCoroutine()
        {
            if (_currentCoroutine != null)
            {
                StopCoroutine(_currentCoroutine);
                _currentCoroutine = null;
            }
        }
    }
}