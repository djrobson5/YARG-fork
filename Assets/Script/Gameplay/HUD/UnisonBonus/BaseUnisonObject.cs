using System;
using System.Collections.Generic;
using DG.Tweening;
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
        /// The arrays are sized once from the engine count, and engine ids are only inside that
        /// range while every engine is the one registered at song start. A rewind registers fresh
        /// engines, which take new ids past the end. Until the display learns to re-key itself
        /// (the unison display does not follow a rewind yet) these guards keep an out-of-range id
        /// from throwing every frame.
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

        public virtual void ResetState()
        {
            Array.Clear(ParticipantFailState, 0, ParticipantCount);
            Array.Clear(ParticipantTotalNotes, 0, ParticipantCount);
            Array.Clear(ParticipantNotesHit, 0, ParticipantCount);
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