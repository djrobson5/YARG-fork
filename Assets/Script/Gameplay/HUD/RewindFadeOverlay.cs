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
    /// </remarks>
    public class RewindFadeOverlay : MonoBehaviour
    {
        private Image _image;

        private float _from;
        private float _to;
        private float _duration;
        private float _elapsed;

        /// <summary>
        /// True while the plate is still moving toward the alpha it was last asked for.
        /// </summary>
        public bool IsFading => _elapsed < _duration;

        public static RewindFadeOverlay Create(Transform parent)
        {
            var overlayObject = new GameObject(nameof(RewindFadeOverlay));
            overlayObject.transform.SetParent(parent, false);

            var canvas = overlayObject.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = short.MaxValue;

            // Swallows anything aimed at what is underneath for as long as the plate is up.
            overlayObject.AddComponent<GraphicRaycaster>();

            var plateObject = new GameObject("Plate");
            plateObject.transform.SetParent(overlayObject.transform, false);

            var rect = plateObject.AddComponent<RectTransform>();
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;

            var overlay = overlayObject.AddComponent<RewindFadeOverlay>();
            overlay._image = plateObject.AddComponent<Image>();
            overlay.SetAlpha(0f);
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

            if (_duration <= 0f)
            {
                SetAlpha(_to);
            }
        }

        private void Update()
        {
            if (!IsFading)
            {
                return;
            }

            _elapsed = Mathf.Min(_elapsed + Time.unscaledDeltaTime, _duration);

            // Smoothstep rather than a straight ramp: at 0.15 s a linear alpha on a full-screen
            // black plate reads as a shutter edge at both ends of the fade.
            SetAlpha(Mathf.Lerp(_from, _to, Mathf.SmoothStep(0f, 1f, _elapsed / _duration)));
        }

        private void SetAlpha(float alpha)
        {
            _image.color = new Color(0f, 0f, 0f, alpha);

            // Fully clear means out of the way entirely, so the raycaster is not sitting on top
            // of the menus for the rest of the run.
            gameObject.SetActive(alpha > 0f);
        }
    }
}
