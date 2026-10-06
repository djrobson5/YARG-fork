using TMPro;
using UnityEngine;
using YARG.Localization;

namespace YARG.Gameplay.HUD
{
    public class GenericPauseOption : MonoBehaviour
    {
        [SerializeField]
        private TextMeshProUGUI _optionText;

        [SerializeField]
        private string _localizationKey;

        public void Awake()
        {
            if (_localizationKey == string.Empty)
            {
                return;
            }
            _optionText.text = Localize.Key(_localizationKey);
        }

        /// <summary>
        /// Recolours the row's label, so the pause list can be faded back while another pane on the
        /// same page has focus.
        /// </summary>
        public void SetTextColor(Color color)
        {
            if (_optionText != null)
            {
                _optionText.color = color;
            }
        }
    }
}