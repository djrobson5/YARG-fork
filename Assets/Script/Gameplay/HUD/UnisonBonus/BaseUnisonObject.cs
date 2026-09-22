using System;
using YARG.Core;

namespace YARG.Gameplay.HUD
{
    public abstract class BaseUnisonObject : GameplayBehaviour
    {
        protected bool[] ParticipantFailState;
        protected int[]  ParticipantTotalNotes;
        protected int[]  ParticipantNotesHit;
        protected int    ParticipantCount;

        /// <summary>
        /// Whether this object has a slot for the given engine id.
        /// </summary>
        /// <remarks>
        /// The arrays are sized from the highest engine id in use, and a rewind registers fresh
        /// engines whose ids run past the end of the old arrays. <c>UnisonDisplay.RebindEngines</c>
        /// re-sizes them on that path, so this is a backstop rather than the fix: it keeps an
        /// id that slips through from throwing every frame.
        /// </remarks>
        protected bool HasParticipantSlot(int engineId) =>
            ParticipantFailState != null && engineId >= 0 && engineId < ParticipantFailState.Length;

        protected float ParticipantProgress(int engineId) =>
            !HasParticipantSlot(engineId)
                ? 0f
                : YargMath.InverseLerpF(0f, ParticipantTotalNotes[engineId], ParticipantNotesHit[engineId]);

        public void Initialize(int playerCount)
        {
            ParticipantFailState = new bool[playerCount];
            ParticipantTotalNotes = new int[playerCount];
            ParticipantNotesHit = new int[playerCount];
        }

        public void SetTotalNotes(int engineId, int totalNotes)
        {
            ParticipantTotalNotes[engineId] = totalNotes;
        }

        public virtual void ResetState()
        {
            // Cleared whole rather than by participant count: the ids in use are not necessarily
            // the first N slots. After a rewind the one engine in a single player run carries id
            // 1, so clearing one slot from the front would leave its state standing.
            Array.Clear(ParticipantFailState, 0, ParticipantFailState.Length);
            Array.Clear(ParticipantTotalNotes, 0, ParticipantTotalNotes.Length);
            Array.Clear(ParticipantNotesHit, 0, ParticipantNotesHit.Length);
            ParticipantCount = 0;
        }

        public virtual void AddParticipant(int participantId, int totalNotes)
        {
            if (!HasParticipantSlot(participantId))
            {
                return;
            }

            ParticipantTotalNotes[participantId] = totalNotes;
            ParticipantNotesHit[participantId] = 0;
            ParticipantFailState[participantId] = false;
            ParticipantCount++;
        }

        public virtual void SetNotesHit(int engineId, int notesHit)
        {
            if (!HasParticipantSlot(engineId))
            {
                return;
            }

            if (!ParticipantFailState[engineId])
            {
                ParticipantNotesHit[engineId] = notesHit;
            }
        }

        public virtual void FailUnison(int engineId)
        {
            if (!HasParticipantSlot(engineId))
            {
                return;
            }

            ParticipantFailState[engineId] = true;
        }
    }
}