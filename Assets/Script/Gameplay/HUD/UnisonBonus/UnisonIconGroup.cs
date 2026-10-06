using System.Collections.Generic;
using UnityEngine;

namespace YARG.Gameplay.HUD
{
    public class UnisonIconGroup : BaseUnisonObject
    {
        [SerializeField]
        private UnisonIcon _instrumentIconPrefab;
        private readonly Dictionary<int, UnisonIcon> _icons = new();

        public void InitializeIcon(int engineId, string spritePath)
        {
            var newIcon = Instantiate(_instrumentIconPrefab, transform);
            newIcon.SetIcon(spritePath);
            _icons[engineId] = newIcon;
            newIcon.gameObject.SetActive(false);
        }

        public override void AddParticipant(int participantId, int totalNotes)
        {
            base.AddParticipant(participantId, totalNotes);
            if (!_icons.TryGetValue(participantId, out var icon))
            {
                return;
            }

            icon.gameObject.SetActive(true);
            icon.SetProgress(0f);
        }

        public override void SetNotesHit(int engineId, int notesHit)
        {
            base.SetNotesHit(engineId, notesHit);
            if (!HasParticipantSlot(engineId) || !_icons.TryGetValue(engineId, out var icon))
            {
                return;
            }

            if (!ParticipantFailState[engineId])
            {
                icon.SetProgress(ParticipantProgress(engineId));
            }
        }

        public override void FailUnison(int engineId)
        {
            base.FailUnison(engineId);
            if (_icons.TryGetValue(engineId, out var icon))
            {
                icon.SetFailState(true);
            }
        }

        /// <summary>
        /// Destroys every icon built so far, so the group can be rebuilt against a new set of
        /// engine ids (a rewind registers fresh engines, which take fresh ids).
        /// </summary>
        public void ClearIcons()
        {
            foreach ((int _, var icon) in _icons)
            {
                Destroy(icon.gameObject);
            }

            _icons.Clear();
        }

        public override void ResetState()
        {
            base.ResetState();
            foreach ((int _, var icon) in _icons)
            {
                icon.ResetState();
            }
        }
    }
}