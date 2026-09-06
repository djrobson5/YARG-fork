using TMPro;
using UnityEngine;
using UnityEngine.Events;
using YARG.Menu.Navigation;

namespace YARG.Menu.MusicLibrary
{
    public class PopupMenuItem : MonoBehaviour
    {
        [SerializeField]
        private TextMeshProUGUI _body;

        [field: SerializeField]
        public NavigatableButton Button { get; private set; }

        public void Initialize(string body, UnityAction action)
        {
            _body.text = body;
            Button.SetOnClickEvent(action);
        }

        /// <summary>
        /// Initializes the item with an explicit label color, so an item that exists but
        /// cannot do anything useful can be drawn as deactivated. The item is still
        /// navigatable and still clickable — the action is expected to explain why.
        /// </summary>
        public void Initialize(string body, UnityAction action, Color textColor)
        {
            Initialize(body, action);
            _body.color = textColor;
        }
    }
}