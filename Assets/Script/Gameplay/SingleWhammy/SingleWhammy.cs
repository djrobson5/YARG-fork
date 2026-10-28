// NO UnityEngine REFERENCES IN THIS FOLDER: tools/SpPathTests compiles it headlessly.

using System;
using YARG.Core.Chart;
using YARG.Core.Engine;
using YARG.Core.Engine.Guitar;
using YARG.Core.Engine.Keys;

namespace YARG.Gameplay.SingleWhammy
{
    /// <summary>
    /// The single-whammy accessibility option: one whammy on a Star Power sustain earns all the
    /// Star Power that sustain can give, as if it had been whammied from its first tick to its
    /// last. See <c>docs/sp-path-design.md</c>, "Single whammy".
    /// <para/>
    /// <b>How a replay knows.</b> The replay format belongs to YARG.Core, which the fork never
    /// modifies, so the option rides in an engine parameter replays already store:
    /// <see cref="BaseEngineParameters.StarPowerWhammyBuffer"/> holds <c>-1 - buffer</c>. A real
    /// buffer is never negative, so the encoding is unambiguous and the real buffer comes back
    /// exactly. Everything (engine choice, the Star Power path) keys off the parameters rather
    /// than the setting, so a replay plays back the way it was recorded whatever the setting is
    /// now.
    /// </summary>
    public static class SingleWhammyParameters
    {
        public static bool IsEnabled(BaseEngineParameters parameters) =>
            parameters.StarPowerWhammyBuffer < 0;

        /// <summary>The whammy buffer the engine preset actually asked for.</summary>
        public static double RealBuffer(BaseEngineParameters parameters) =>
            IsEnabled(parameters)
                ? -1 - parameters.StarPowerWhammyBuffer
                : parameters.StarPowerWhammyBuffer;

        private static double Encode(BaseEngineParameters parameters) =>
            -1 - Math.Max(RealBuffer(parameters), 0);

        // The copies go through the public constructors because the fields are readonly. A
        // field upstream adds later would be dropped silently, so SingleWhammyTests compares
        // every public field of a copy against its source.

        public static GuitarEngineParameters WithSingleWhammy(GuitarEngineParameters p) =>
            new(p.HitWindow, p.MaxMultiplier, Encode(p), p.SustainDropLeniency,
                p.StarMultiplierThresholds, p.SoloBonusStarMultiplierThresholds, p.HopoLeniency,
                p.StrumLeniency, p.StrumLeniencySmall, p.InfiniteFrontEnd, p.AntiGhosting,
                p.SoloTaps, p.NoStarPowerOverlap, p.EnableLanes)
            {
                SongSpeed = p.SongSpeed
            };

        public static KeysEngineParameters WithSingleWhammy(KeysEngineParameters p) =>
            new(p.HitWindow, p.MaxMultiplier, Encode(p), p.SustainDropLeniency,
                p.StarMultiplierThresholds, p.SoloBonusStarMultiplierThresholds,
                p.ChordStaggerWindow, p.FatFingerWindow, p.NoStarPowerOverlap, p.EnableLanes,
                p.ReleaseKeysCanHitNotes)
            {
                SongSpeed = p.SongSpeed
            };
    }

    /// <summary>
    /// The per-engine state behind single whammy. Each engine subclass calls
    /// <see cref="Begin"/> at the top of <c>UpdateStarPower</c> and <see cref="End"/> after
    /// <c>base.UpdateStarPower()</c>; the base method does the crediting itself, so the half-bar
    /// event, stats, end-time update and clamp all stay stock.
    /// <para/>
    /// A <b>run</b> is a stretch of passes with at least one Star Power sustain active (a chord or
    /// an extended sustain keeps one run going). The run is <b>held</b> from the first pass where
    /// the whammy timer is active, which is a real whammy or one that landed just before the run.
    /// While held, the engine keeps the timer running, and gain is credited from
    /// <c>max(earliest Star Power sustain tick, credited-through tick)</c>, so the pass that
    /// latches the run backdates to the sustain's start and no tick is credited twice.
    /// <para/>
    /// The whammy remainder is carried from one held run to the next, because the player's next
    /// whammy restarts an expired timer, and <c>StartWhammyTimer</c> zeroes the remainder.
    /// That makes the cumulative gain <c>round(16·S/15)</c>, exactly what the full-whammy Star
    /// Power path plans for.
    /// </summary>
    public struct SingleWhammyState
    {
        private bool _held;
        private uint _creditedThrough;
        private double _remainder;

        /// <returns>
        /// True if this pass is held. The engine then restarts the whammy timer and lowers
        /// <c>LastTick</c> and <c>FirstWhammyTick</c> to <paramref name="creditFrom"/> for the
        /// base call.
        /// </returns>
        public bool Begin(bool starPowerSustainActive, uint earliestStarPowerSustainTick,
            bool whammyTimerActive, uint currentTick, ref double whammyTicksRemainder,
            out uint creditFrom)
        {
            creditFrom = currentTick;

            if (!starPowerSustainActive)
            {
                _held = false;
                return false;
            }

            if (!_held)
            {
                if (!whammyTimerActive)
                {
                    return false;
                }

                _held = true;
                whammyTicksRemainder = _remainder;
            }

            // Clamped so a note hit early (pass before its tick) credits nothing yet rather than
            // underflowing the gain's unsigned subtraction.
            creditFrom = Math.Min(Math.Max(earliestStarPowerSustainTick, _creditedThrough),
                currentTick);
            return true;
        }

        public void End(uint currentTick, double whammyTicksRemainder)
        {
            if (_held)
            {
                _creditedThrough = currentTick;
                _remainder = whammyTicksRemainder;
            }
        }

        public static uint EarliestStarPowerSustainTick<TNote>(SustainList<TNote> sustains)
            where TNote : Note<TNote>
        {
            uint earliest = uint.MaxValue;
            foreach (var sustain in sustains)
            {
                if (sustain.Note.IsStarPower && sustain.Note.Tick < earliest)
                {
                    earliest = sustain.Note.Tick;
                }
            }

            return earliest;
        }
    }
}
