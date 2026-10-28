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
    /// <para/>
    /// <b>Whammy</b> (<c>docs/sp-path-design.md</c>, "Whammy"). With <see cref="AssumeWhammy"/>
    /// every Star Power sustain is whammied over its whole span, so the meter also grows between
    /// notes and a window's end keeps moving while it is open. The meter is therefore kept in
    /// Star Power ticks rather than quarter bars, a window can release part way through a
    /// sustain, and the whammy after the release banks toward the next activation
    /// (<see cref="SpWindow.CarriedMeterTicks"/>). Without whammy every one of those reduces to
    /// the phrases-only arithmetic above.
    /// </summary>
    public sealed class SpScoreModel
    {
        /// <summary>A full bar, in quarter bars — the meter's cap.</summary>
        public const int MaxQuarterBars = 4;

        /// <summary><c>CanStarPowerActivate</c> needs half a bar (<c>BaseEngine.cs:44</c>).</summary>
        public const int MinQuarterBarsToActivate = 2;

        private readonly ScoreModel _model;

        /// <summary>
        /// Indexed by scoring-note index: the quarter bars hitting that note banks. 0 for an
        /// ordinary note, 1 for a Star Power phrase end, 2 when that phrase is also a unison the
        /// engine pays a bonus on. Precomputed so the DP never re-scans the unison list.
        /// </summary>
        private readonly byte[] _quarterBarsGained;

        /// <summary>Prefix sums of event values, indexed by event count. <c>_prefix[k]</c> is the
        /// un-doubled score of the first <c>k</c> events.</summary>
        private readonly long[] _prefix;

        /// <summary>Event measure ticks, ascending — the search key for the prefix sums.</summary>
        private readonly uint[] _eventMeasureTicks;

        /// <summary>Event quarter ticks, ascending — the key a window's doubled range is found by.</summary>
        private readonly uint[] _eventTicks;

        /// <summary>
        /// The union of the Star Power sustain spans, merged and ascending, as parallel start/end
        /// arrays, with <c>_whammyPrefix[k]</c> the whammied ticks before span <c>k</c>. Empty
        /// without <see cref="AssumeWhammy"/>, which makes every whammy gain zero.
        /// </summary>
        private readonly uint[] _whammyStart;
        private readonly uint[] _whammyEnd;
        private readonly long[] _whammyPrefix;

        /// <summary>Quarter ticks of the time signature changes: where the drain rate changes.</summary>
        private readonly uint[] _signatureTicks;

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
        /// <param name="unisonPhrases">
        /// The <b>player's own</b> Star Power phrases that are part of a unison, as plain
        /// quarter-tick ranges — <c>EngineManager.EngineContainer.UnisonPhrases</c> on the Unity
        /// side. Hitting the phrase end of one of these banks <b>two</b> quarter bars, not one:
        /// <c>AwardStarPower</c> gains a quarter (<c>BaseEngine.Generic.cs:1158-1163</c>) and then
        /// raises <c>OnStarPowerPhraseHit</c>, which <c>EngineManager.OnStarPowerPhraseHit</c>
        /// (<c>EngineManager.UnisonEvent.cs:336-360</c>) turns into a second quarter via
        /// <c>BaseEngine.AwardUnisonBonus</c> (<c>BaseEngine.cs:637-641</c>). <c>null</c> or empty
        /// means "no unisons", which reproduces the pre-unison model exactly.
        /// </param>
        public SpScoreModel(ScoreModel model, bool noStarPowerOverlap,
            IReadOnlyList<SpUnisonPhrase> unisonPhrases = null)
            : this(model, noStarPowerOverlap, assumeWhammy: false, unisonPhrases)
        {
        }

        /// <param name="assumeWhammy">
        /// Plan as if every Star Power sustain is whammied over its whole span, with the whammy
        /// timer never lapsing. <b>No default</b>, for the same reason as
        /// <paramref name="noStarPowerOverlap"/>: the two models produce different paths. The
        /// constructor without it means "no whammy", the model this class was locked with.
        /// </param>
        public SpScoreModel(ScoreModel model, bool noStarPowerOverlap, bool assumeWhammy,
            IReadOnlyList<SpUnisonPhrase> unisonPhrases)
        {
            _model = model ?? throw new ArgumentNullException(nameof(model));
            NoStarPowerOverlap = noStarPowerOverlap;
            AssumeWhammy = assumeWhammy;

            var events = model.Events;
            _prefix = new long[events.Count + 1];
            _eventMeasureTicks = new uint[events.Count];
            _eventTicks = new uint[events.Count];

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
                _eventTicks[i] = events[i].Tick;
                _prefix[i + 1] = _prefix[i] + events[i].Value;
            }

            (_whammyStart, _whammyEnd, _whammyPrefix) = assumeWhammy
                ? MergeSpans(model.StarPowerSustains)
                : (Array.Empty<uint>(), Array.Empty<uint>(), Array.Empty<long>());

            var signatures = model.SyncTrack.TimeSignatures;
            _signatureTicks = new uint[signatures.Count];
            for (int i = 0; i < signatures.Count; i++)
            {
                _signatureTicks[i] = signatures[i].Tick;
            }

            var scoringNotes = model.ScoringNotes;
            _quarterBarsGained = new byte[scoringNotes.Count];
            var unisonTicks = new List<uint>();

            for (int i = 0; i < scoringNotes.Count; i++)
            {
                if (!scoringNotes[i].IsPhraseEnd)
                {
                    continue;
                }

                bool unison = ContainsTick(unisonPhrases, scoringNotes[i].Tick);
                _quarterBarsGained[i] = (byte) (unison ? 2 : 1);

                if (unison)
                {
                    unisonTicks.Add(scoringNotes[i].Tick);
                }
            }

            UnisonPhraseEndTicks = unisonTicks;
        }

        /// <summary>
        /// Merges the Star Power sustain spans into a sorted, disjoint union. The engine's gain test
        /// is a boolean over every active sustain (<c>IsStarPowerSustainActive</c>), so two
        /// overlapping sustains whammy once, not twice.
        /// </summary>
        private static (uint[] start, uint[] end, long[] prefix) MergeSpans(
            IReadOnlyList<ScoreModel.TickSpan> spans)
        {
            var sorted = new List<ScoreModel.TickSpan>(spans);
            sorted.Sort((a, b) => a.Start.CompareTo(b.Start));

            var starts = new List<uint>();
            var ends = new List<uint>();
            foreach (var span in sorted)
            {
                if (starts.Count > 0 && span.Start <= ends[^1])
                {
                    if (span.End > ends[^1])
                    {
                        ends[^1] = span.End;
                    }

                    continue;
                }

                starts.Add(span.Start);
                ends.Add(span.End);
            }

            var prefix = new long[starts.Count];
            for (int k = 1; k < starts.Count; k++)
            {
                prefix[k] = prefix[k - 1] + (ends[k - 1] - starts[k - 1]);
            }

            return (starts.ToArray(), ends.ToArray(), prefix);
        }

        /// <summary>
        /// The engine matches a phrase hit to a unison by asking whether the hit's time falls
        /// inside the player's own unison phrase, ends included
        /// (<c>EngineManager.UnisonEvent.cs:346</c>:
        /// <c>phrase.Time &lt;= time &amp;&amp; time &lt;= phrase.TimeEnd</c>). Ticks are monotone
        /// in time, so the same test in ticks picks the same phrases, and it keeps a second
        /// coordinate space out of the model.
        /// </summary>
        private static bool ContainsTick(IReadOnlyList<SpUnisonPhrase> phrases, uint tick)
        {
            if (phrases is null)
            {
                return false;
            }

            for (int i = 0; i < phrases.Count; i++)
            {
                if (phrases[i].Contains(tick))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// The call sites should use: takes <c>NoStarPowerOverlap</c> straight off the live
        /// <c>GuitarEngineParameters</c> the player's engine was built with
        /// (<c>TrackPlayer.cs:245</c>), so the flag cannot be dropped on the way in.
        /// </summary>
        public static SpScoreModel FromParameters(ScoreModel model, GuitarEngineParameters parameters,
            IReadOnlyList<SpUnisonPhrase> unisonPhrases = null) =>
            FromParameters(model, parameters, assumeWhammy: false, unisonPhrases);

        /// <summary>As above, with the whammy model chosen explicitly. The game calls this one.</summary>
        public static SpScoreModel FromParameters(ScoreModel model, GuitarEngineParameters parameters,
            bool assumeWhammy, IReadOnlyList<SpUnisonPhrase> unisonPhrases)
        {
            if (parameters is null) throw new ArgumentNullException(nameof(parameters));
            return new SpScoreModel(model, parameters.NoStarPowerOverlap, assumeWhammy, unisonPhrases);
        }

        public ScoreModel Model => _model;

        /// <summary>See the constructor parameter of the same name.</summary>
        public bool NoStarPowerOverlap { get; }

        /// <summary>See the constructor parameter of the same name.</summary>
        public bool AssumeWhammy { get; }

        public uint TicksPerQuarterSpBar => _model.TicksPerQuarterSpBar;

        public uint TicksPerHalfSpBar => _model.TicksPerHalfSpBar;

        public uint TicksPerFullSpBar => _model.TicksPerFullSpBar;

        /// <summary>Number of combo steps / scoring notes.</summary>
        public int NoteCount => _model.ScoringNotes.Count;

        /// <summary>
        /// Quarter ticks of the phrase-end notes the model expects a <em>unison</em> bonus on — the
        /// subset of the phrase ends that fell inside one of the unison ranges handed in. Empty
        /// when the chart has no unisons, which is the case the goldens are pinned on.
        /// </summary>
        public IReadOnlyList<uint> UnisonPhraseEndTicks { get; }

        /// <summary>
        /// Quarter bars banked by hitting scoring note <paramref name="scoringNoteIndex"/>: 0, 1,
        /// or 2 for a unison phrase end. Before the full-bar clamp — see <see cref="MeterAfter"/>.
        /// </summary>
        public int QuarterBarsGainedAt(int scoringNoteIndex) => _quarterBarsGained[scoringNoteIndex];

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
        /// <paramref name="noteIndex"/> with <paramref name="quarterBars"/> of meter. The
        /// phrases-only entry point: whole quarter bars, as every caller before whammy used it.
        /// </summary>
        public SpWindow SimulateWindow(int noteIndex, int quarterBars)
        {
            if (quarterBars < MinQuarterBarsToActivate || quarterBars > MaxQuarterBars)
            {
                throw new ArgumentOutOfRangeException(nameof(quarterBars),
                    $"Star Power needs {MinQuarterBarsToActivate}..{MaxQuarterBars} quarter bars " +
                    $"to activate, got {quarterBars}.");
            }

            return SimulateWindowTicks(noteIndex, (uint) quarterBars * TicksPerQuarterSpBar);
        }

        /// <summary>
        /// Walks the Star Power window opened by activating on scoring note
        /// <paramref name="noteIndex"/> with <paramref name="meterTicks"/> of meter, which must be
        /// between half a bar and a full bar.
        /// </summary>
        /// <remarks>
        /// The window end <c>E</c> is <c>position + amount</c> (<c>BaseEngine.cs:553</c>). It is
        /// fixed except where something adds to the amount: a phrase end pushes it out by a
        /// quarter bar, and whammy pushes it out continuously. Either way it is capped at a full
        /// bar past the current position (<c>BaseEngine.cs:532-535</c>,
        /// <c>BaseEngine.Generic.cs:1081-1090</c>). The window releases on the first update whose
        /// position has reached it — before that update's hit logic — so an award is doubled iff
        /// its tick is before the release tick.
        /// </remarks>
        public SpWindow SimulateWindowTicks(int noteIndex, uint meterTicks)
        {
            if (noteIndex < 0 || noteIndex >= NoteCount)
            {
                throw new ArgumentOutOfRangeException(nameof(noteIndex));
            }

            if (meterTicks < TicksPerHalfSpBar || meterTicks > TicksPerFullSpBar)
            {
                throw new ArgumentOutOfRangeException(nameof(meterTicks),
                    $"Star Power needs {TicksPerHalfSpBar}..{TicksPerFullSpBar} tick(s) of meter " +
                    $"to activate, got {meterTicks}.");
            }

            var notes = _model.ScoringNotes;
            uint startTick = notes[noteIndex].Tick;
            uint start = notes[noteIndex].MeasureTick;

            var release = WalkWindow(start, startTick, meterTicks, noteIndex);

            int next = noteIndex;
            while (next < notes.Count && notes[next].Tick < release.Tick)
            {
                next++;
            }

            // Whammy the window did not get to use banks from the release on. The phrase on the
            // next note itself is not counted here: MeterTicksAfter adds it when that note is
            // passed, the same as for any other note.
            uint carried = next < notes.Count
                ? Math.Min(TicksPerFullSpBar, WhammyGain(release.Tick, notes[next].Tick))
                : 0;

            return new SpWindow(noteIndex, notes[noteIndex].NoteIndex, startTick, start, release.End,
                meterTicks, (int) (meterTicks / TicksPerQuarterSpBar), next,
                PointsInTicks(startTick, release.Tick), release.Tick, carried);
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

            uint startTick = FirstTickAtMeasure(0, startMeasureTick);
            return WalkWindow(startMeasureTick, startTick, meterTicks, from).End;
        }

        private readonly struct Release
        {
            /// <summary>Quarter tick of the update the window releases on.</summary>
            public readonly uint Tick;

            /// <summary>The window end <c>E</c>, in measure ticks, at the moment it released.</summary>
            public readonly uint End;

            public Release(uint tick, uint end)
            {
                Tick = tick;
                End = end;
            }
        }

        /// <summary>
        /// Follows a window from its activation to its release: drift to each phrase end inside it
        /// (whammy and drain), release if the position has caught up, otherwise apply the phrase's
        /// quarter bar(s) (<c>GainStarPower</c> -> <c>UpdateStarPowerEnds</c> while active,
        /// <c>BaseEngine.cs:543-547</c>) and carry on. Within one tick the engine applies whammy and
        /// drain, then releases, then runs the hit logic that awards the phrase
        /// (<c>docs/sp-path-design.md</c>, "Whammy"), which is the order here too.
        /// </summary>
        private Release WalkWindow(uint start, uint startTick, uint meterTicks, int firstNoteIndex)
        {
            var notes = _model.ScoringNotes;
            uint end = start + meterTicks;
            uint tick = startTick;

            for (int i = firstNoteIndex; i < notes.Count; i++)
            {
                if (_quarterBarsGained[i] == 0)
                {
                    continue;
                }

                uint phraseTick = notes[i].Tick;
                if (phraseTick > tick)
                {
                    if (Drift(ref tick, ref end, phraseTick, out uint releaseTick))
                    {
                        return new Release(releaseTick, end);
                    }
                }

                // The release check runs before the hit logic, so a phrase landing exactly on the
                // release tick never extends anything.
                if (notes[i].MeasureTick >= end)
                {
                    return new Release(phraseTick, end);
                }

                if (NoStarPowerOverlap)
                {
                    continue;
                }

                // A unison phrase end runs GainStarPower twice, so it runs this step twice: each
                // call adds a quarter to the amount and re-derives the end from the *same*
                // position, and the amount clamp (BaseEngine.cs:532-535) is the same full-bar cap
                // on the end both times.
                uint capped = notes[i].MeasureTick + TicksPerFullSpBar;
                for (int gain = _quarterBarsGained[i]; gain > 0; gain--)
                {
                    uint extended = end + TicksPerQuarterSpBar;
                    end = extended < capped ? extended : capped;
                }
            }

            Drift(ref tick, ref end, uint.MaxValue, out uint lastRelease);
            return new Release(lastRelease, end);
        }

        /// <summary>
        /// Advances an open window from <paramref name="tick"/> towards <paramref name="until"/>,
        /// with no phrase end in between. Returns true, with the release tick, if the position
        /// reaches the end on the way; otherwise leaves <paramref name="tick"/> at
        /// <paramref name="until"/> and <paramref name="end"/> wherever whammy pushed it.
        /// </summary>
        /// <remarks>
        /// The range is split wherever the rates change: at the edges of the whammied spans and,
        /// inside one, at time signature changes. Without whammy the end is fixed, so the release
        /// is just the first tick whose measure tick reaches it. With whammy the end grows with
        /// the gain while the position grows with the drain; in 4/4 the gain wins and the window
        /// cannot release, in a shorter meter it can, part way through the sustain.
        /// </remarks>
        private bool Drift(ref uint tick, ref uint end, uint until, out uint releaseTick)
        {
            while (tick < until)
            {
                int span = SpanAtOrBefore(tick);
                bool whammying = span >= 0 && tick < _whammyEnd[span];

                if (!whammying)
                {
                    // Nothing moves the end until the next whammied span starts.
                    uint stop = span + 1 < _whammyStart.Length ? _whammyStart[span + 1] : uint.MaxValue;
                    if (stop > until)
                    {
                        stop = until;
                    }

                    uint reach = FirstTickAtMeasure(tick, end);
                    if (reach <= stop)
                    {
                        releaseTick = reach;
                        return true;
                    }

                    tick = stop;
                    continue;
                }

                uint segmentEnd = Math.Min(_whammyEnd[span], until);
                uint signature = NextSignatureTick(tick);
                if (signature < segmentEnd)
                {
                    segmentEnd = signature;
                }

                uint from = tick;
                uint baseEnd = end;
                uint gainBase = WhammyTotal(from);

                // Released iff the position has reached the end as whammy has pushed it so far.
                bool Released(uint q) =>
                    _model.SyncTrack.QuarterTickToMeasureTick(q) >= baseEnd + (WhammyTotal(q) - gainBase);

                if (Released(segmentEnd))
                {
                    // The drain is outrunning the gain, so the gap only closes: binary-search the
                    // first tick it is closed at.
                    uint lo = from;
                    uint hi = segmentEnd;
                    while (hi - lo > 1)
                    {
                        uint mid = lo + (hi - lo) / 2;
                        if (Released(mid))
                        {
                            hi = mid;
                        }
                        else
                        {
                            lo = mid;
                        }
                    }

                    end = baseEnd + (WhammyTotal(hi) - gainBase);
                    releaseTick = hi;
                    return true;
                }

                // The amount is capped at a full bar each update, i.e. the end at a full bar past
                // the position. With the gap only widening, the cap at the segment end is exact.
                uint grown = baseEnd + (WhammyTotal(segmentEnd) - gainBase);
                uint cap = _model.SyncTrack.QuarterTickToMeasureTick(segmentEnd) + TicksPerFullSpBar;
                end = grown < cap ? grown : cap;
                tick = segmentEnd;
            }

            releaseTick = 0;
            return false;
        }

        /// <summary>
        /// Meter after passing scoring note <paramref name="noteIndex"/> without activating, in
        /// quarter bars. Phrases-only: it refuses a model that whammies, whose meter is not a whole
        /// number of quarter bars.
        /// </summary>
        public int MeterAfter(int noteIndex, int quarterBars)
        {
            if (AssumeWhammy)
            {
                throw new InvalidOperationException(
                    "MeterAfter counts whole quarter bars; with whammy use MeterTicksAfter.");
            }

            return (int) (MeterTicksAfter(noteIndex, (uint) quarterBars * TicksPerQuarterSpBar) /
                TicksPerQuarterSpBar);
        }

        /// <summary>
        /// Meter on arriving at the note after <paramref name="noteIndex"/>, having passed it
        /// without activating: its phrase (if any), plus the whammy between the two notes.
        /// </summary>
        public uint MeterTicksAfter(int noteIndex, uint meterTicks)
        {
            // GainStarPower and the whammy path both clamp the amount at a full bar
            // (BaseEngine.cs:532-535, BaseEngine.Generic.cs:1081-1090), and every gain is positive
            // while Star Power is off, so clamping the sum once is exact.
            ulong meter = meterTicks + (ulong) _quarterBarsGained[noteIndex] * TicksPerQuarterSpBar;

            var notes = _model.ScoringNotes;
            if (noteIndex + 1 < notes.Count)
            {
                meter += WhammyGain(notes[noteIndex].Tick, notes[noteIndex + 1].Tick);
            }

            return meter > TicksPerFullSpBar ? TicksPerFullSpBar : (uint) meter;
        }

        /// <summary>
        /// Whammy gain over <c>(from, to]</c>, in Star Power ticks: the difference of the
        /// cumulative gain <see cref="WhammyTotal"/>, never a sum of per-interval roundings.
        /// </summary>
        public uint WhammyGain(uint fromTick, uint toTick) =>
            toTick <= fromTick ? 0 : WhammyTotal(toTick) - WhammyTotal(fromTick);

        /// <summary>
        /// Cumulative whammy gain up to <paramref name="tick"/>: <c>round(16·S/15)</c> with
        /// <c>S</c> the whammied Star Power sustain ticks so far.
        /// </summary>
        /// <remarks>
        /// <c>CalculateStarPowerGain</c> (<c>BaseEngine.Generic.cs:1124-1135</c>) rounds each
        /// update's <c>delta × 32/30</c> and carries the remainder, which never resets while the
        /// whammy timer stays up. So the engine's running total stays within ½ of <c>16·S/15</c>,
        /// which is never a half-integer: it is exactly the rounded value, on any update grid.
        /// <c>(16·S + 7) / 15</c> is that rounding in integers.
        /// </remarks>
        public uint WhammyTotal(uint tick)
        {
            int span = SpanAtOrBefore(tick);
            if (span < 0)
            {
                return 0;
            }

            long whammied = _whammyPrefix[span] + (Math.Min(tick, _whammyEnd[span]) - _whammyStart[span]);
            return (uint) ((16 * whammied + 7) / 15);
        }

        /// <summary>Index of the last whammied span starting at or before the tick, or -1.</summary>
        private int SpanAtOrBefore(uint tick)
        {
            int lo = 0;
            int hi = _whammyStart.Length;
            while (lo < hi)
            {
                int mid = (lo + hi) / 2;
                if (_whammyStart[mid] <= tick)
                {
                    lo = mid + 1;
                }
                else
                {
                    hi = mid;
                }
            }

            return lo - 1;
        }

        private uint NextSignatureTick(uint tick)
        {
            foreach (uint signature in _signatureTicks)
            {
                if (signature > tick)
                {
                    return signature;
                }
            }

            return uint.MaxValue;
        }

        /// <summary>
        /// First quarter tick at or after <paramref name="fromTick"/> whose measure tick is at
        /// least <paramref name="measureTick"/> — the update a fixed window end releases on.
        /// </summary>
        private uint FirstTickAtMeasure(uint fromTick, uint measureTick)
        {
            var sync = _model.SyncTrack;
            if (sync.QuarterTickToMeasureTick(fromTick) >= measureTick)
            {
                return fromTick;
            }

            uint lo = fromTick;
            uint hi = Math.Max(sync.MeasureTickToQuarterTick(measureTick), fromTick + 1);
            while (sync.QuarterTickToMeasureTick(hi) < measureTick)
            {
                lo = hi;
                hi = hi + Math.Max(hi - fromTick, 1u);
            }

            while (hi - lo > 1)
            {
                uint mid = lo + (hi - lo) / 2;
                if (sync.QuarterTickToMeasureTick(mid) >= measureTick)
                {
                    hi = mid;
                }
                else
                {
                    lo = mid;
                }
            }

            return hi;
        }

        /// <summary>
        /// Meter on arriving at scoring note 0. No sustain can start before the first note, so it
        /// is always 0; kept as a method so the optimizer and the replay agree on it by
        /// construction.
        /// </summary>
        public uint InitialMeterTicks => 0;

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
            uint meter = InitialMeterTicks;
            int next = 0;

            for (int i = 0; i < NoteCount;)
            {
                if (next < activationScoringNoteIndices.Count &&
                    activationScoringNoteIndices[next] == i)
                {
                    if (meter < TicksPerHalfSpBar)
                    {
                        throw new ArgumentException(
                            $"Activation at scoring note {i} is illegal: the meter is only " +
                            $"{meter / TicksPerQuarterSpBar} quarter bar(s) ({meter} tick(s)), " +
                            $"and half a bar is the minimum (BaseEngine.cs:44).",
                            nameof(activationScoringNoteIndices));
                    }

                    var window = SimulateWindowTicks(i, meter);
                    windowsOut?.Add(window);
                    extra += window.DoubledPoints;

                    next++;
                    i = window.NextNoteIndex;
                    meter = window.CarriedMeterTicks;
                    continue;
                }

                meter = MeterTicksAfter(i, meter);
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

        /// <summary>
        /// The un-doubled points awarded at quarter ticks in <c>[fromTick, toTick)</c> — what a
        /// window that opens on the update at <paramref name="fromTick"/> and releases on the
        /// update at <paramref name="toTick"/> doubles. Without whammy this picks exactly the same
        /// events as <see cref="PointsIn"/> over the window's measure ticks.
        /// </summary>
        public long PointsInTicks(uint fromTick, uint toTick)
        {
            if (toTick <= fromTick)
            {
                return 0;
            }

            return _prefix[LowerBoundTick(toTick)] - _prefix[LowerBoundTick(fromTick)];
        }

        private int LowerBoundTick(uint tick)
        {
            int lo = 0;
            int hi = _eventTicks.Length;
            while (lo < hi)
            {
                int mid = (lo + hi) / 2;
                if (_eventTicks[mid] < tick)
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

    /// <summary>
    /// One of the player's Star Power phrases that is part of a unison, as a plain quarter-tick
    /// range. Built on the Unity side from <c>EngineManager.EngineContainer.UnisonPhrases</c>,
    /// which is exactly the list <c>EngineManager.OnStarPowerPhraseHit</c> matches a phrase hit
    /// against (<c>ParticipantToPhrase[EngineId]</c>, <c>EngineManager.UnisonEvent.cs:340-346</c>).
    /// <para/>
    /// <b>Perfect play awards every one of them.</b> The bonus fires when
    /// <c>SuccessCount == ParticipantToPhrase.Count</c> (<c>:53-57</c>), and the participant list
    /// holds only the engines that are actually registered — so in a single-player run the one
    /// player clearing the phrase is all of them. That is also what the feature's own gate
    /// guarantees: the overlay exists only for runs with exactly one human player
    /// (<c>GameManager.InitializeStarPowerPaths</c>).
    /// </summary>
    public readonly struct SpUnisonPhrase
    {
        /// <summary>Quarter tick the phrase starts at (<c>Phrase.Tick</c>).</summary>
        public readonly uint Tick;

        /// <summary>Quarter tick the phrase ends at, inclusive (<c>Phrase.TickEnd</c>).</summary>
        public readonly uint TickEnd;

        public SpUnisonPhrase(uint tick, uint tickEnd)
        {
            Tick = tick;
            TickEnd = tickEnd;
        }

        public bool Contains(uint tick) => tick >= Tick && tick <= TickEnd;

        public override string ToString() => $"[{Tick}, {TickEnd}]";
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

        /// <summary>Meter spent, in Star Power ticks.</summary>
        public readonly uint MeterTicks;

        /// <summary>Meter spent, in whole quarter bars (2..4) — exact without whammy.</summary>
        public readonly int MeterQuarterBars;

        /// <summary>First scoring note at or after the release.</summary>
        public readonly int NextNoteIndex;

        /// <summary>Points this window adds over not activating.</summary>
        public readonly long DoubledPoints;

        /// <summary>Quarter tick of the update the window releases on; nothing from it on is doubled.</summary>
        public readonly uint ReleaseTick;

        /// <summary>
        /// Meter on arriving at <see cref="NextNoteIndex"/>: whammy earned after the release by a
        /// Star Power sustain the window ended in the middle of. Always 0 without whammy.
        /// </summary>
        public readonly uint CarriedMeterTicks;

        public SpWindow(int scoringNoteIndex, int noteIndex, uint activationTick,
            uint activationMeasureTick, uint endMeasureTick, uint meterTicks, int meterQuarterBars,
            int nextNoteIndex, long doubledPoints, uint releaseTick, uint carriedMeterTicks)
        {
            ScoringNoteIndex = scoringNoteIndex;
            NoteIndex = noteIndex;
            ActivationTick = activationTick;
            ActivationMeasureTick = activationMeasureTick;
            EndMeasureTick = endMeasureTick;
            MeterTicks = meterTicks;
            MeterQuarterBars = meterQuarterBars;
            NextNoteIndex = nextNoteIndex;
            DoubledPoints = doubledPoints;
            ReleaseTick = releaseTick;
            CarriedMeterTicks = carriedMeterTicks;
        }

        public uint LengthInMeasureTicks => EndMeasureTick - ActivationMeasureTick;

        public override string ToString() =>
            $"activate note {NoteIndex} @ measure tick {ActivationMeasureTick} with " +
            $"{MeterTicks} tick(s) (~{MeterQuarterBars}/4 bar) -> [{ActivationMeasureTick}, " +
            $"{EndMeasureTick}) = +{DoubledPoints}" +
            (CarriedMeterTicks > 0 ? $", carries {CarriedMeterTicks}" : string.Empty);
    }
}
