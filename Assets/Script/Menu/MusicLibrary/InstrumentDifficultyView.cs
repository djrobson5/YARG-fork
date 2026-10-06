using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.UI;
using YARG.Helpers.Extensions;
using YARG.Settings;

namespace YARG.Menu.MusicLibrary
{
    public class InstrumentDifficultyView : MonoBehaviour
    {
        private static readonly Dictionary<string, Sprite> SpriteCache = new();

        [SerializeField]
        private Image _instrumentIcon;

        [SerializeField]
        private Image _difficultyIcon;

        [SerializeField]
        private TextMeshProUGUI _percentText;

        [Space]
        [SerializeField]
        [Tooltip("The percent color when the run was a full combo.")]
        private Color _fullComboColor = new(1f, 208 / 255f, 41 / 255f);

        [SerializeField]
        [Tooltip("The section fraction color when every section has been perfected. " +
            "Matches the score card's violet accent.")]
        private Color _sectionFullComboColor = new(0.678f, 0.478f, 1f);

        /// <summary>
        /// The pill's width with a plain percent, matching the prefab.
        /// </summary>
        private const float BASE_WIDTH = 130f;

        /// <summary>
        /// The pill's width with a percent that has decimals.
        /// </summary>
        private const float DECIMAL_WIDTH = 150f;

        /// <summary>
        /// The distance from the pill's left edge to the earliest point the percent text may
        /// start at. The instrument icon sits at the pill's left edge and the difficulty icon
        /// ends 60 units in, so anything to the left of this would be drawn over them.
        /// </summary>
        private const float TEXT_INSET = 64f;

        /// <summary>
        /// The gap the prefab leaves between the percent text and the pill's right edge.
        /// </summary>
        private const float TEXT_MARGIN = 10f;

        /// <summary>
        /// The pill width minus its percent text box's width, in the prefab. Used to derive the
        /// box's base width from the pill's, so the decimal pill's wider box is accounted for.
        /// </summary>
        private const float PERCENT_BOX_INSET = 60f;

        /// <summary>
        /// A little slack so the measured text never sits flush against the box's edge.
        /// </summary>
        private const float PERCENT_PADDING = 4f;


        public void SetInfo(ViewType.ScoreInfo scoreInfo)
        {
            bool showDecimals = SettingsManager.Settings.ShowPercentDecimals.Value;

            // Set instrument icon
            _instrumentIcon.sprite = GetSprite($"InstrumentIcons[{scoreInfo.Instrument.ToResourceName()}]");

            // Set difficulty icon
            _difficultyIcon.sprite = GetSprite($"DifficultyIcons[{scoreInfo.Difficulty.ToString()}]");

            // Set percent value
            string text;
            if (showDecimals)
            {
                var percent = Mathf.Floor(scoreInfo.Percent * 1000f) / 10f;
                text = $"{percent:0.0}%";
            }
            else
            {
                text = $"{Mathf.FloorToInt(scoreInfo.Percent * 100f)}%";
            }

            // Append the cumulative section completion, if this chart has any recorded
            if (scoreInfo.Sections is { } sections)
            {
                string fraction = $"{sections.CompletedCount}/{sections.SectionCount}";

                // The percent keeps its own meaning (gold on a full combo); only the fraction
                // turns violet, and only once every section has been perfected
                if (sections.IsSectionFullCombo)
                {
                    fraction = $"<color=#{ColorUtility.ToHtmlStringRGB(_sectionFullComboColor)}>" +
                        $"{fraction}</color>";
                }

                text = $"{text} · {fraction}";
            }

            _percentText.text = text;
            _percentText.color = scoreInfo.IsFc ? _fullComboColor : Color.white;

            SetWidth(showDecimals ? DECIMAL_WIDTH : BASE_WIDTH,
                scoreInfo.Sections is not null ? _percentText.GetPreferredValues(text).x : 0f);
        }

        /// <summary>
        /// Grows the pill and its percent box to fit text wider than the prefab's box.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The percent text is pinned to the pill's right edge and is right aligned, so the only
        /// thing that keeps it off the two icons is the pill being wide enough: the text is sized
        /// from where it has to start (<see cref="TEXT_INSET"/>, just past the difficulty icon)
        /// rather than by adding the overflow to the prefab's box, which would have grown the
        /// text leftwards over the icons instead.
        /// </para>
        /// <para>
        /// The pill has no <see cref="LayoutElement"/>, and the horizontal layout group it sits
        /// in doesn't control child widths, so the group reads this rect's <c>sizeDelta</c>
        /// directly as its preferred width. That also means nothing marks the row dirty when the
        /// width changes here, hence the explicit rebuild: without it the row keeps last frame's
        /// arrangement and the pill silently overlaps the star row.
        /// </para>
        /// </remarks>
        private void SetWidth(float baseWidth, float preferredTextWidth)
        {
            float textWidth = Mathf.Ceil(preferredTextWidth) + PERCENT_PADDING;

            var rect = (RectTransform) transform;
            float width = Mathf.Max(baseWidth, TEXT_INSET + textWidth + TEXT_MARGIN);
            rect.sizeDelta = new Vector2(width, rect.sizeDelta.y);

            // The background stretches to the pill, so it follows the width above on its own
            var percentRect = _percentText.rectTransform;
            float percentWidth = Mathf.Max(baseWidth - PERCENT_BOX_INSET, textWidth);
            percentRect.sizeDelta = new Vector2(percentWidth, percentRect.sizeDelta.y);

            // Walks up to the outermost layout group, which is the row itself, so the star row
            // and the score text move over. Marking the parent alone would rebuild the same root
            LayoutRebuilder.MarkLayoutForRebuild(rect);
        }

        private static Sprite GetSprite(string assetKey)
        {
            if (!SpriteCache.TryGetValue(assetKey, out var sprite))
            {
                SpriteCache[assetKey] = sprite = Addressables.LoadAssetAsync<Sprite>(assetKey).WaitForCompletion();
            }

            return sprite;
        }
    }
}