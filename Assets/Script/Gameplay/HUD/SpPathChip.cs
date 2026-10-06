using TMPro;
using UnityEngine;
using UnityEngine.UI;
using YARG.Gameplay.Visuals;

namespace YARG.Gameplay.HUD
{
    /// <summary>
    /// The HUD half of the Star Power path cue: a compact outlined chip in the top band that says
    /// how many beats are left until the planned activation
    /// (<c>docs/sp-path-design.md</c> → "Visual redesign, 2026-09-04").
    /// </summary>
    /// <remarks>
    /// Built entirely from code by <see cref="Create"/> and parented into the top element
    /// container, so it scales with <c>ScaleContainer</c> and follows the highway's far end the
    /// way the solo box does, with no edit to <c>TrackView.prefab</c>. It shares the container
    /// with the solo box, which is why <see cref="TrackView"/> hides it whenever a solo is
    /// running.
    /// </remarks>
    public class SpPathChip : MonoBehaviour
    {
        /// Fits inside the notification/solo box width (202) with room to spare.
        private const float CHIP_WIDTH  = 190f;
        private const float CHIP_HEIGHT = 32f;

        /// How thick the green outline is, in canvas units.
        private const float BORDER = 2f;

        /// <summary>
        /// The chip's three colours, all derived from the one cue colour
        /// (<c>Settings.StarPowerPathColor</c>, held by
        /// <see cref="SpPathMarkerElement.ActivationTrimColor"/>) so the chip always matches the
        /// highway: the border is the cue colour itself, the label a desaturated full-value tint
        /// of it, and the body a near-black wash of it.
        /// </summary>
        /// <remarks>
        /// Read when the chip is built, which the track view does lazily on the first show —
        /// after <c>TrackPlayer.OnStarPowerPathSet</c> has pushed the setting in.
        /// </remarks>
        private static Color BorderColor => SpPathMarkerElement.ActivationTrimColor;

        private static Color BodyColor => TintOfCue(0.45f, 0.075f, 0.92f);

        private static Color LabelColor => TintOfCue(0.31f, 1f, 1f);

        private static Color TintOfCue(float saturationScale, float value, float alpha)
        {
            Color.RGBToHSV(SpPathMarkerElement.ActivationTrimColor, out float h, out float s,
                out _);
            var color = Color.HSVToRGB(h, s * saturationScale, value);
            color.a = alpha;
            return color;
        }

        private Image           _border;
        private TextMeshProUGUI _label;

        private string _displayedText;
        private bool   _hasDisplayed;

        /// <summary>
        /// Builds a chip under <paramref name="parent"/>, centred on it. Returns <c>null</c> if
        /// there is no font to draw it with, which is the only way this can fail.
        /// </summary>
        public static SpPathChip Create(RectTransform parent, TMP_FontAsset font)
        {
            if (parent == null || font == null)
            {
                return null;
            }

            var root = new GameObject("SP Path Chip", typeof(RectTransform), typeof(Image),
                typeof(SpPathChip));

            // A GameObject built from code lands on layer 0, not the UI layer the rest of the
            // view is on. Canvases that render through a camera cull by layer, so an unset layer
            // is a silently invisible chip.
            int layer = parent.gameObject.layer;
            root.layer = layer;

            var rect = (RectTransform) root.transform;
            rect.SetParent(parent, false);
            rect.anchorMin = new Vector2(0.5f, 0.5f);
            rect.anchorMax = new Vector2(0.5f, 0.5f);
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.anchoredPosition = Vector2.zero;
            rect.sizeDelta = new Vector2(CHIP_WIDTH, CHIP_HEIGHT);

            var chip = root.GetComponent<SpPathChip>();
            chip._border = root.GetComponent<Image>();
            chip._border.color = BorderColor;
            chip._border.raycastTarget = false;

            // The body is a second, inset image rather than an Outline component: Outline works by
            // duplicating vertices with an offset, which on a solid rect reads as a drop shadow
            // rather than a border.
            var body = new GameObject("Body", typeof(RectTransform), typeof(Image));
            body.layer = layer;
            var bodyRect = (RectTransform) body.transform;
            bodyRect.SetParent(rect, false);
            bodyRect.anchorMin = Vector2.zero;
            bodyRect.anchorMax = Vector2.one;
            bodyRect.offsetMin = new Vector2(BORDER, BORDER);
            bodyRect.offsetMax = new Vector2(-BORDER, -BORDER);
            var bodyImage = body.GetComponent<Image>();
            bodyImage.color = BodyColor;
            bodyImage.raycastTarget = false;

            var labelObject = new GameObject("Label", typeof(RectTransform),
                typeof(TextMeshProUGUI));
            labelObject.layer = layer;
            var labelRect = (RectTransform) labelObject.transform;
            labelRect.SetParent(bodyRect, false);
            labelRect.anchorMin = Vector2.zero;
            labelRect.anchorMax = Vector2.one;
            labelRect.offsetMin = new Vector2(6f, 0f);
            labelRect.offsetMax = new Vector2(-6f, 0f);

            var label = labelObject.GetComponent<TextMeshProUGUI>();
            label.font = font;
            label.color = LabelColor;
            label.alignment = TextAlignmentOptions.Center;
            label.enableAutoSizing = true;
            label.fontSizeMin = 8f;
            label.fontSizeMax = 18f;
            label.fontStyle = FontStyles.Bold;
            label.characterSpacing = 6f;
            label.raycastTarget = false;
            label.text = string.Empty;

            chip._label = label;

            root.SetActive(false);
            return chip;
        }

        /// <summary>Shows the chip with the given copy, or hides it.</summary>
        public void SetState(bool show, string text)
        {
            if (!show)
            {
                if (gameObject.activeSelf)
                {
                    gameObject.SetActive(false);
                }

                return;
            }

            if (!gameObject.activeSelf)
            {
                gameObject.SetActive(true);
            }

            if (_hasDisplayed && text == _displayedText)
            {
                return;
            }

            _hasDisplayed = true;
            _displayedText = text;

            // One state only: the chip never dims. The off-plan variant was removed on the
            // user's instruction (2026-09-04) — see docs/sp-path-design.md.
            _label.text = text;
            _label.color = LabelColor;
            _border.color = BorderColor;
        }
    }
}
