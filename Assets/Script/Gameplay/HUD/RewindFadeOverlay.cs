using UnityEngine;
using UnityEngine.UI;

namespace YARG.Gameplay.HUD
{
    /// <summary>
    /// The full-screen black plate that hides a section rewind while the run is rebuilt
    /// (<c>docs/rewind-design.md</c>, "What the player sees when it lands").
    /// </summary>
    /// <remarks>
    /// Built in code rather than authored into the gameplay scene. It has to cover the highway,
    /// the HUD <i>and</i> the venue, which are three different cameras, and it has to sit above
    /// the pause menu as well, so the only thing that works is a screen-space overlay canvas at
    /// the top of the sorting order - and there is nothing on it worth an inspector.
    /// <para>
    /// The alpha is driven from <see cref="Time.unscaledDeltaTime"/> because the fade-out runs
    /// while the pause menu still holds <c>Time.timeScale</c> at zero.
    /// </para>
    /// <para>
    /// The plate is deliberately <i>not</i> a raycast target and carries no
    /// <c>GraphicRaycaster</c>: it must never be able to swallow a pointer click aimed at a menu
    /// underneath it. Nothing needs it to - the two pause bindings are gated by
    /// <c>GameManager.IsRewindFading</c> and the menus are popped before the fade starts.
    /// </para>
    /// </remarks>
    public class RewindFadeOverlay : MonoBehaviour
    {
        private Image _image;

        private float _from;
        private float _to;
        private float _duration;
        private float _elapsed;

        /// <summary>
        /// Whether a fade is still running.
        /// </summary>
        /// <remarks>
        /// An explicit latch rather than <c>_elapsed &lt; _duration</c>. The old test could never
        /// be satisfied once the plate switched itself off, and the old <c>SetAlpha</c> did
        /// exactly that: it deactivated the object the moment alpha hit zero, which the fade-in's
        /// smoothstep ramp can reach in float32 a few microseconds <i>before</i> <c>_elapsed</c>
        /// reaches <c>_duration</c>. A fade-in that ticked on one of those values stopped
        /// <see cref="Update"/> for good, so <c>IsFading</c> stayed true forever and
        /// <c>GameManager.RewindToSectionBehindFade</c> waited on it forever - leaving
        /// <c>_rewindFadeInProgress</c> set and every later rewind in the run silently refused.
        /// <para>
        /// Rare (the values that round to an exact zero are scattered through the last fraction
        /// of a percent of the ramp), which is why the coroutine also carries a watchdog: the
        /// shape of the failure - one fade stranding every later rewind - matters more than this
        /// one route into it. <see cref="Finish"/> is now the only place the object is switched
        /// off, and it runs after the latch drops.
        /// </para>
        /// </remarks>
        public bool IsFading { get; private set; }

        public static RewindFadeOverlay Create(Transform parent)
        {
            var overlayObject = new GameObject(nameof(RewindFadeOverlay));
            overlayObject.transform.SetParent(parent, false);

            var canvas = overlayObject.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = short.MaxValue;

            var plateObject = new GameObject("Plate");
            plateObject.transform.SetParent(overlayObject.transform, false);

            var rect = plateObject.AddComponent<RectTransform>();
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;

            var overlay = overlayObject.AddComponent<RewindFadeOverlay>();
            overlay._image = plateObject.AddComponent<Image>();
            overlay._image.raycastTarget = false;
            overlay.Finish(0f);
            return overlay;
        }

        /// <summary>
        /// Starts a fade from wherever the plate is now to <paramref name="alpha"/>.
        /// </summary>
        public void FadeTo(float alpha, float duration)
        {
            gameObject.SetActive(true);

            _from = _image.color.a;
            _to = Mathf.Clamp01(alpha);
            _duration = Mathf.Max(duration, 0f);
            _elapsed = 0f;
            IsFading = true;

            if (_duration <= 0f)
            {
                Finish(_to);
            }
        }

        /// <summary>
        /// Ends any fade in flight and puts the plate straight on <paramref name="alpha"/>.
        /// </summary>
        /// <remarks>
        /// The way out for a caller that has to know the plate is not in the way - a refused
        /// rewind, a pause, the end of the song - without waiting on a fade it did not start.
        /// </remarks>
        public void Clear(float alpha = 0f)
        {
            Finish(Mathf.Clamp01(alpha));
        }

        private void Update()
        {
            if (!IsFading)
            {
                return;
            }

            _elapsed = Mathf.Min(_elapsed + Time.unscaledDeltaTime, _duration);

            if (_elapsed >= _duration)
            {
                Finish(_to);
                return;
            }

            // Smoothstep rather than a straight ramp: at 0.15 s a linear alpha on a full-screen
            // black plate reads as a shutter edge at both ends of the fade.
            SetAlpha(Mathf.Lerp(_from, _to, Mathf.SmoothStep(0f, 1f, _elapsed / _duration)));
        }

        /// <summary>
        /// Lands the plate on its target and drops the fade latch.
        /// </summary>
        /// <remarks>
        /// The only place the object is ever switched off, and it runs after
        /// <see cref="IsFading"/> is cleared, so an in-flight fade can never deactivate itself and
        /// strand its own <see cref="Update"/>.
        /// </remarks>
        private void Finish(float alpha)
        {
            IsFading = false;
            _elapsed = _duration;

            SetAlpha(alpha);

            // Fully clear means out of the way entirely, so an idle plate is not sitting over the
            // menus (and not costing a full-screen overdraw) for the rest of the run.
            gameObject.SetActive(alpha > 0f);
        }

        private void SetAlpha(float alpha)
        {
            _image.color = new Color(0f, 0f, 0f, alpha);
        }
    }
}
