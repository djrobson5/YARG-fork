// NO UnityEngine REFERENCES IN THIS FOLDER: tools/SpPathTests compiles it headlessly.

using System;
using YARG.Core.Chart;
using YARG.Core.Engine;
using YARG.Core.Engine.Guitar;
using YARG.Core.Engine.Guitar.Engines;
using YARG.Core.Engine.Keys;
using YARG.Core.Engine.Keys.Engines;

namespace YARG.Gameplay.SingleWhammy
{
    // The stock engines with single whammy (SingleWhammyState) added. C# has no mixins and the
    // logic touches protected engine members, so each instrument repeats the same two members:
    //
    // - The constructor rebuilds the whammy timer from the real buffer. The base constructor
    //   built it from the encoded one (BaseEngine.Generic.cs:70); SetSpeed runs later, so the
    //   song speed still applies.
    // - UpdateStarPower wraps the stock one. On a held pass it restarts the timer with
    //   EngineTimer.Start, not StartWhammyTimer (which would zero the remainder once the timer
    //   has lapsed), and lowers LastTick and FirstWhammyTick so the stock gain line,
    //   Max(LastTick, FirstWhammyTick) to CurrentTick, credits from the backdated tick.
    //   LastTick is read nowhere else in the pass and is put back afterwards.

    public class SingleWhammyFiveFretGuitarEngine : YargFiveFretGuitarEngine
    {
        private SingleWhammyState _singleWhammy;

        public SingleWhammyFiveFretGuitarEngine(InstrumentDifficulty<GuitarNote> chart,
            SyncTrack syncTrack, GuitarEngineParameters engineParameters, bool isBot)
            : base(chart, syncTrack, engineParameters, isBot)
        {
            StarPowerWhammyTimer = new EngineTimer("Star Power Whammy",
                SingleWhammyParameters.RealBuffer(engineParameters));
        }

        public override void Reset(bool keepCurrentButtons = false)
        {
            base.Reset(keepCurrentButtons);
            _singleWhammy = default;
        }

        protected override void UpdateStarPower()
        {
            uint lastTick = LastTick;
            if (_singleWhammy.Begin(IsStarPowerSustainActive(),
                SingleWhammyState.EarliestStarPowerSustainTick(ActiveSustains),
                StarPowerWhammyTimer.IsActive, CurrentTick, ref WhammyTicksRemainder,
                out uint creditFrom))
            {
                StarPowerWhammyTimer.Start(CurrentTime);
                LastTick = creditFrom;
                FirstWhammyTick = Math.Min(FirstWhammyTick, creditFrom);
            }

            base.UpdateStarPower();

            LastTick = lastTick;
            _singleWhammy.End(CurrentTick, WhammyTicksRemainder);
        }
    }

    public class SingleWhammySixFretGuitarEngine : YargSixFretGuitarEngine
    {
        private SingleWhammyState _singleWhammy;

        public SingleWhammySixFretGuitarEngine(InstrumentDifficulty<GuitarNote> chart,
            SyncTrack syncTrack, GuitarEngineParameters engineParameters, bool isBot)
            : base(chart, syncTrack, engineParameters, isBot)
        {
            StarPowerWhammyTimer = new EngineTimer("Star Power Whammy",
                SingleWhammyParameters.RealBuffer(engineParameters));
        }

        public override void Reset(bool keepCurrentButtons = false)
        {
            base.Reset(keepCurrentButtons);
            _singleWhammy = default;
        }

        protected override void UpdateStarPower()
        {
            uint lastTick = LastTick;
            if (_singleWhammy.Begin(IsStarPowerSustainActive(),
                SingleWhammyState.EarliestStarPowerSustainTick(ActiveSustains),
                StarPowerWhammyTimer.IsActive, CurrentTick, ref WhammyTicksRemainder,
                out uint creditFrom))
            {
                StarPowerWhammyTimer.Start(CurrentTime);
                LastTick = creditFrom;
                FirstWhammyTick = Math.Min(FirstWhammyTick, creditFrom);
            }

            base.UpdateStarPower();

            LastTick = lastTick;
            _singleWhammy.End(CurrentTick, WhammyTicksRemainder);
        }
    }

    public class SingleWhammyFiveLaneKeysEngine : YargFiveLaneKeysEngine
    {
        private SingleWhammyState _singleWhammy;

        public SingleWhammyFiveLaneKeysEngine(InstrumentDifficulty<GuitarNote> chart,
            SyncTrack syncTrack, KeysEngineParameters engineParameters, bool isBot)
            : base(chart, syncTrack, engineParameters, isBot)
        {
            StarPowerWhammyTimer = new EngineTimer("Star Power Whammy",
                SingleWhammyParameters.RealBuffer(engineParameters));
        }

        public override void Reset(bool keepCurrentButtons = false)
        {
            base.Reset(keepCurrentButtons);
            _singleWhammy = default;
        }

        protected override void UpdateStarPower()
        {
            uint lastTick = LastTick;
            if (_singleWhammy.Begin(IsStarPowerSustainActive(),
                SingleWhammyState.EarliestStarPowerSustainTick(ActiveSustains),
                StarPowerWhammyTimer.IsActive, CurrentTick, ref WhammyTicksRemainder,
                out uint creditFrom))
            {
                StarPowerWhammyTimer.Start(CurrentTime);
                LastTick = creditFrom;
                FirstWhammyTick = Math.Min(FirstWhammyTick, creditFrom);
            }

            base.UpdateStarPower();

            LastTick = lastTick;
            _singleWhammy.End(CurrentTick, WhammyTicksRemainder);
        }
    }

    public class SingleWhammyProKeysEngine : YargProKeysEngine
    {
        private SingleWhammyState _singleWhammy;

        public SingleWhammyProKeysEngine(InstrumentDifficulty<ProKeysNote> chart,
            SyncTrack syncTrack, KeysEngineParameters engineParameters, bool isBot)
            : base(chart, syncTrack, engineParameters, isBot)
        {
            StarPowerWhammyTimer = new EngineTimer("Star Power Whammy",
                SingleWhammyParameters.RealBuffer(engineParameters));
        }

        public override void Reset(bool keepCurrentButtons = false)
        {
            base.Reset(keepCurrentButtons);
            _singleWhammy = default;
        }

        protected override void UpdateStarPower()
        {
            uint lastTick = LastTick;
            if (_singleWhammy.Begin(IsStarPowerSustainActive(),
                SingleWhammyState.EarliestStarPowerSustainTick(ActiveSustains),
                StarPowerWhammyTimer.IsActive, CurrentTick, ref WhammyTicksRemainder,
                out uint creditFrom))
            {
                StarPowerWhammyTimer.Start(CurrentTime);
                LastTick = creditFrom;
                FirstWhammyTick = Math.Min(FirstWhammyTick, creditFrom);
            }

            base.UpdateStarPower();

            LastTick = lastTick;
            _singleWhammy.End(CurrentTick, WhammyTicksRemainder);
        }
    }
}
