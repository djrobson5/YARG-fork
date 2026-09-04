// NO UnityEngine REFERENCES IN THIS FOLDER. See ScoreEvent.cs for why.

using System;
using System.Collections.Generic;
using YARG.Core.Engine.Guitar;

namespace YARG.Gameplay.SpPath
{
    /// <summary>
    /// The Star Power window model: meter arithmetic, the closed-form window walk, and the total
    /// score for a given activation list.
    /// <para/>
    /// Everything here lives in <b>measure-tick</b> space, because that is the only space Star
    /// Power is measured in. The drain is exactly 1:1 with measure ticks
    /// (<c>CalculateStarPowerDrain</c>, <c>BaseEngine.Generic.cs:1073-1076</c>), the position is
    /// <c>SyncTrack.QuarterTickToMeasureTick(CurrentTick)</c> (<c>:996</c>), and the window end is
    /// the pure function <c>StarPowerTickEndPosition = StarPowerTickPosition + StarPowerTickAmount</c>
    /// (<c>BaseEngine.cs:553</c>). Measure ticks advance <c>MeasureResolution</c> per measure no
    /// matter what the time signature is (<c>TimeSignatureEvent.QuarterTickToMeasureTick</c>), so a
    /// full bar is always 8 measures of chart time — meter-aware, and the reason CHOpt's flat-beat
    /// bar cannot be used as an oracle.
    /// <para/>
    /// <b>Semantics settled empirically in slice 3</b> (see <c>SpSemanticsTests</c> and
    /// <c>docs/sp-path-design.md</c> §1.5):
    /// <list type="bullet">
    /// <item>"Activate at note N" means the activation runs at <c>CurrentTick == Notes[N].Tick</c>,
    /// before that pass's hit logic, so <b>N is the first doubled note</b>.</item>
    /// <item>The window is the half-open measure-tick interval <c>[m, E)</c> where
    /// <c>m = MeasureTick(N)</c> and <c>E = m + meter</c>. An award whose measure tick is exactly
    /// <c>E</c> is <b>not</b> doubled: <c>UpdateStarPower</c> runs first in the loop and releases
    /// on that pass.</item>
    /// <item>A phrase completed while active pushes the end out by one quarter bar, clamped to a
    /// full bar measured from the phrase note: <c>E ← min(E + quarter, m_phrase + full)</c>.</item>
    /// <item>The meter is emptied by a window: any phrase collected inside it extended the window
    /// instead of banking, and the release happens at amount 0. So the state after a window is
    /// always meter 0.</item>
    /// <item>The input only has to be raised on the activation pass. Holding it does nothing
    /// further — <c>ActivateStarPower</c> returns early while already active
    /// (<c>BaseEngine.cs:483-486</c>).</item>
    /// </list>
    /// </summary>
    public sealed class SpScoreModel
    {
        /// <summary>Meter states, in quarter bars. Phrases-only gain, so these are the only ones.</summary>
        public const int MaxQuarterBars = 4;

        /// <summary><c>CanStarPowerActivate</c> needs half a bar (<c>BaseEngine.cs:44</c>).</summary>
        public const int MinQuarterBarsToActivate = 2;

        private readonly ScoreModel _model;

        /// <summary>Prefix sums of event values, indexed by event count. <c>_prefix[k]</c> is the
        /// un-doubled score of the first <c>k</c> events.</summary>
        private readonly long[] _prefix;

        /// <summary>Event measure ticks, ascending — the search key for the prefix sums.</summary>
        private readonly uint[] _eventMeasureTicks;

        /// <param name="noStarPowerOverlap">
        /// <c>GuitarEngineParameters.NoStarPowerOverlap</c> (<c>GuitarEngineParameters.cs:15,32</c>),
        /// read from the live engine. When it is <c>true</c>, a phrase hit while Star Power is
        /// active is stripped rather than awarded (<c>Guitar/GuitarEngine.cs:259-261</c>), so
        /// windows never extend.
        /// <para/>
        /// <b>Deliberately has no default.</b> The two settings produce genuinely different paths,
        /// and the common preset value (<c>false</c>) is exactly the one a forgotten argument would
        /// silently pick, so the mistake would only show up on the presets that matter. Prefer
        /// <see cref="FromParameters"/>, which reads it off the live engine parameters.
        /// </param>
        public SpScoreModel(ScoreModel model, bool noStarPowerOverlap)
        {
            _model = model ?? throw new ArgumentNullException(nameof(model));
            NoStarPowerOverlap = noStarPowerOverlap;

            var events = model.Events;
            _prefix = new long[events.Count + 1];
            _eventMeasureTicks = new uint[events.Count];

            uint previous = 0;
            for (int i = 0; i < events.Count; i++)
            {
                uint measureTick = events[i].MeasureTick;
                if (measureTick < previous)
                {
                    // QuarterTickToMeasureTick is monotone, so the event list being tick-ordered
                    // makes it measure-tick-ordered too. If that ever stops holding, the binary
                    // searches below are silently wrong, so say so loudly.
                    throw new InvalidOperationException(
                        "Score events are not ordered by measure tick; the SP window prefix sums " +
                        "assume they are.");
                }

                previous = measureTick;
                _eventMeasureTicks[i] = measureTick;
                _prefix[i + 1] = _prefix[i] + events[i].Value;
            }
        }

        /// <summary>
        /// The call sites should use: takes <c>NoStarPowerOverlap</c> straight off the live
        /// <c>GuitarEngineParameters</c> the player's engine was built with
        /// (<c>TrackPlayer.cs:245</c>), so the flag cannot be dropped on the way in.
        /// </summary>
        public static SpScoreModel FromParameters(ScoreModel model, GuitarEngineParameters parameters)
        {
            if (parameters is null) throw new ArgumentNullException(nameof(parameters));
            return new SpScoreModel(model, parameters.NoStarPowerOverlap);
        }

        public ScoreModel Model => _model;

        /// <summary>See the constructor parameter of the same name.</summary>
        public bool NoStarPowerOverlap { get; }

        public uint TicksPerQuarterSpBar => _model.TicksPerQuarterSpBar;

        public uint TicksPerHalfSpBar => _model.TicksPerHalfSpBar;

        public uint TicksPerFullSpBar => _model.TicksPerFullSpBar;

        /// <summary>Number of combo steps / scoring notes.</summary>
        public int NoteCount => _model.ScoringNotes.Count;

        /// <summary>
        /// The un-doubled points awarded in the half-open measure-tick interval
        /// <c>[from, to)</c>. Activating adds exactly this much again, because
        /// <c>AddScore</c> multiplies by a <c>ScoreMultiplier</c> that Star Power has doubled
        /// (<c>BaseEngine.cs:451-454</c>).
        /// </summary>
        public long PointsIn(uint fromMeasureTick, uint toMeasureTick)
        {
            if (toMeasureTick <= fromMeasureTick)
            {
                return 0;
            }

            int lo = LowerBound(fromMeasureTick);
            int hi = LowerBound(toMeasureTick);
            return _prefix[hi] - _prefix[lo];
        }

        /// <summary>
        /// Walks the deterministic Star Power window opened by activating on scoring note
        /// <paramref name="noteIndex"/> with <paramref name="quarterBars"/> of meter.
        /// </summary>
        public SpWindow SimulateWindow(int noteIndex, int quarterBars)
        {
            if (noteIndex < 0 || noteIndex >= NoteCount)
            {
                throw new ArgumentOutOfRangeException(nameof(noteIndex));
            }

            if (quarterBars < MinQuarterBarsToActivate || quarterBars > MaxQuarterBars)
            {
                throw new ArgumentOutOfRangeException(nameof(quarterBars),
                    $"Star Power needs {MinQuarterBarsToActivate}..{MaxQuarterBars} quarter bars " +
                    $"to activate, got {quarterBars}.");
            }

            var notes = _model.ScoringNotes;
            uint start = notes[noteIndex].MeasureTick;
            uint end = WalkWindowEnd(start, (uint) quarterBars * TicksPerQuarterSpBar, noteIndex);

            int next = noteIndex;
            while (next < notes.Count && notes[next].MeasureTick < end)
            {
                next++;
            }

            return new SpWindow(noteIndex, notes[noteIndex].NoteIndex, notes[noteIndex].Tick,
                start, end, quarterBars, next, PointsIn(start, end));
        }

        /// <summary>
        /// The end of a window opened at an arbitrary measure tick with an arbitrary meter — the
        /// same walk as <see cref="SimulateWindow"/>, but not tied to activating on a note.
        /// <para/>
        /// Needed to check the model against a run whose activation did <em>not</em> land on a
        /// note: the stock greedy bot activates on whatever engine pass follows the meter reaching
        /// half (<c>YargFiveFretGuitarEngine.cs:30</c>), which is usually a bare frame tick.
        /// </summary>
        public uint WindowEndAt(uint startMeasureTick, uint meterTicks)
        {
            var notes = _model.ScoringNotes;

            int from = 0;
            while (from < notes.Count && notes[from].MeasureTick < startMeasureTick)
            {
                from++;
            }

            return WalkWindowEnd(startMeasureTick, meterTicks, from);
        }

        /// <summary>
        /// Walks the phrase ends inside a window, pushing the end out by a quarter bar for each
        /// (<c>GainStarPower</c> -> <c>UpdateStarPowerEnds</c> while active,
        /// <c>BaseEngine.cs:543-547</c>), with the full-bar clamp at <c>:532-535</c> — which,
        /// because the end is <c>position + amount</c>, is the same as clamping the end to a full
        /// bar past the phrase note.
        /// </summary>
        private uint WalkWindowEnd(uint start, uint meterTicks, int firstNoteIndex)
        {
            var notes = _model.ScoringNotes;
            uint end = start + meterTicks;

            for (int i = firstNoteIndex; i < notes.Count && notes[i].MeasureTick < end; i++)
            {
                if (!notes[i].IsPhraseEnd || NoStarPowerOverlap)
                {
                    continue;
                }

                uint extended = end + TicksPerQuarterSpBar;
                uint capped = notes[i].MeasureTick + TicksPerFullSpBar;
                end = extended < capped ? extended : capped;
            }

            return end;
        }

        /// <summary>
        /// Meter after passing scoring note <paramref name="noteIndex"/> without activating.
        /// </summary>
        public int MeterAfter(int noteIndex, int quarterBars)
        {
            if (!_model.ScoringNotes[noteIndex].IsPhraseEnd)
            {
                return quarterBars;
            }

            return quarterBars + 1 > MaxQuarterBars ? MaxQuarterBars : quarterBars + 1;
        }

        /// <summary>
        /// Total <c>TotalScore</c> for a perfect run that activates Star Power on exactly the given
        /// scoring-note indices, in order.
        /// </summary>
        /// <param name="activationScoringNoteIndices">
        /// Indices into <see cref="ScoreModel.ScoringNotes"/>, strictly increasing. Each must be
        /// reachable — i.e. not inside a window opened by an earlier activation — and must have at
        /// least half a bar of meter.
        /// </param>
        public int ScoreForActivations(IReadOnlyList<int> activationScoringNoteIndices) =>
            _model.ProjectPerfectScore() + (int) DoubledPointsForActivations(activationScoringNoteIndices);

        /// <summary>
        /// The extra points the given activation list adds over never activating, and the windows
        /// it produces.
        /// </summary>
        public long DoubledPointsForActivations(IReadOnlyList<int> activationScoringNoteIndices,
            List<SpWindow> windowsOut = null)
        {
            windowsOut?.Clear();

            if (activationScoringNoteIndices is null || activationScoringNoteIndices.Count == 0)
            {
                return 0;
            }

            long extra = 0;
            int meter = 0;
            int next = 0;

            for (int i = 0; i < NoteCount;)
            {
                if (next < activationScoringNoteIndices.Count &&
                    activationScoringNoteIndices[next] == i)
                {
                    if (meter < MinQuarterBarsToActivate)
                    {
                        throw new ArgumentException(
                            $"Activation at scoring note {i} is illegal: the meter is only " +
                            $"{meter} quarter bar(s), and half a bar is the minimum " +
                            $"(BaseEngine.cs:44).", nameof(activationScoringNoteIndices));
                    }

                    var window = SimulateWindow(i, meter);
                    windowsOut?.Add(window);
                    extra += window.DoubledPoints;

                    next++;
                    i = window.NextNoteIndex;
                    meter = 0;
                    continue;
                }

                meter = MeterAfter(i, meter);
                i++;
            }

            if (next < activationScoringNoteIndices.Count)
            {
                throw new ArgumentException(
                    $"Activation at scoring note {activationScoringNoteIndices[next]} was never " +
                    "reached — it is either out of order or inside an earlier window.",
                    nameof(activationScoringNoteIndices));
            }

            return extra;
        }

        /// <summary>First event index whose measure tick is &gt;= <paramref name="measureTick"/>.</summary>
        private int LowerBound(uint measureTick)
        {
            int lo = 0;
            int hi = _eventMeasureTicks.Length;
            while (lo < hi)
            {
                int mid = (lo + hi) / 2;
                if (_eventMeasureTicks[mid] < measureTick)
                {
                    lo = mid + 1;
                }
                else
                {
                    hi = mid;
                }
            }

            return lo;
        }
    }

    /// <summary>One Star Power window, fully determined by its activation note and meter.</summary>
    public readonly struct SpWindow
    {
        /// <summary>Index into <see cref="ScoreModel.ScoringNotes"/>.</summary>
        public readonly int ScoringNoteIndex;

        /// <summary>Index into the post-modifier note track.</summary>
        public readonly int NoteIndex;

        /// <summary>Quarter tick of the activation note.</summary>
        public readonly uint ActivationTick;

        /// <summary>Measure tick of the activation note — the window start, inclusive.</summary>
        public readonly uint ActivationMeasureTick;

        /// <summary>Measure tick the window ends at, exclusive (<c>E</c>).</summary>
        public readonly uint EndMeasureTick;

        /// <summary>Meter spent, in quarter bars (2..4).</summary>
        public readonly int MeterQuarterBars;

        /// <summary>First scoring note at or after <see cref="EndMeasureTick"/>.</summary>
        public readonly int NextNoteIndex;

        /// <summary>Points this window adds over not activating.</summary>
        public readonly long DoubledPoints;

        public SpWindow(int scoringNoteIndex, int noteIndex, uint activationTick,
            uint activationMeasureTick, uint endMeasureTick, int meterQuarterBars,
            int nextNoteIndex, long doubledPoints)
        {
            ScoringNoteIndex = scoringNoteIndex;
            NoteIndex = noteIndex;
            ActivationTick = activationTick;
            ActivationMeasureTick = activationMeasureTick;
            EndMeasureTick = endMeasureTick;
            MeterQuarterBars = meterQuarterBars;
            NextNoteIndex = nextNoteIndex;
            DoubledPoints = doubledPoints;
        }

        public uint LengthInMeasureTicks => EndMeasureTick - ActivationMeasureTick;

        public override string ToString() =>
            $"activate note {NoteIndex} @ measure tick {ActivationMeasureTick} with " +
            $"{MeterQuarterBars}/4 bar -> [{ActivationMeasureTick}, {EndMeasureTick}) " +
            $"= +{DoubledPoints}";
    }
}
