// NO UnityEngine REFERENCES IN THIS FOLDER. See ScoreEvent.cs for why.

using System;
using System.Collections.Generic;
using YARG.Core.Chart;

namespace YARG.Gameplay.SpPath
{
    /// <summary>
    /// A Unity-free reimplementation of YARG's 5-fret scoring for a full-combo run, built once
    /// per player from the post-modifier note track.
    /// <para/>
    /// Slice 2 of <c>docs/sp-path-design.md</c>: it reproduces <c>TotalScore</c> exactly for a
    /// perfect run with Star Power suppressed. Star Power windows (slice 3) only ever double a
    /// contiguous run of <see cref="Events"/>, so this table is the whole substrate the optimizer
    /// works on.
    /// <para/>
    /// The constants below are duplicated from <c>BaseEngine</c>, where they are
    /// <c>protected const</c> and therefore unreadable from <c>Assets/Script</c>. The bot-run
    /// verification test in <c>tools/SpPathTests</c> is the drift guard.
    /// </summary>
    public sealed class ScoreModel
    {
        /// <summary><c>BaseEngine.cs:11</c></summary>
        public const int POINTS_PER_NOTE = 50;

        /// <summary><c>BaseEngine.cs:12</c>. Not used by 5-fret; kept so a later pro-guitar/keys
        /// extension does not have to rediscover it.</summary>
        public const int POINTS_PER_PRO_NOTE = POINTS_PER_NOTE + 10;

        /// <summary><c>BaseEngine.cs:13</c></summary>
        public const int POINTS_PER_BEAT = 25;

        /// <summary><c>BaseEngine.cs:17</c></summary>
        public const int STAR_POWER_MAX_MEASURES = 8;

        /// <summary><c>BaseEngine.cs:20</c></summary>
        public const int SUSTAIN_BURST_FRACTION = 4;

        /// <summary>Solo bonus rate, <c>BaseEngine.Generic.cs:1196</c> and <c>:1315</c>.</summary>
        public const int SOLO_POINTS_PER_NOTE = 100;

        /// <summary>Solo bonuses are floored to a multiple of this, <c>BaseEngine.Generic.cs:1199</c>.</summary>
        public const int SOLO_BONUS_ROUNDING = 50;

        /// <summary>Below this hit ratio a solo pays nothing, <c>BaseEngine.Generic.cs:1186</c>.</summary>
        public const double SOLO_MINIMUM_PERCENT = 0.6;

        // ---------------------------------------------------------------------------------

        private readonly List<ScoreEvent> _events;
        private readonly List<ScoringNote> _scoringNotes;

        private ScoreModel(SyncTrack syncTrack, int maxMultiplier, List<ScoreEvent> events,
            List<ScoringNote> scoringNotes, int soloBonusTotal)
        {
            SyncTrack = syncTrack;
            MaxMultiplier = maxMultiplier;
            _events = events;
            _scoringNotes = scoringNotes;
            SoloBonusTotal = soloBonusTotal;

            TicksPerQuarterSpBar = syncTrack.MeasureResolution * 2;
            TicksPerHalfSpBar = TicksPerQuarterSpBar * 2;
            TicksPerFullSpBar = TicksPerQuarterSpBar * 4;

            int total = 0;
            foreach (var e in events)
            {
                total += e.Value;
            }

            CommittedScore = total;
        }

        public SyncTrack SyncTrack { get; }

        /// <summary>Read from the live engine parameters, never hardcoded — bass is 6, not 4.</summary>
        public int MaxMultiplier { get; }

        /// <summary><c>BaseEngine.cs:168-170</c></summary>
        public uint TicksPerQuarterSpBar { get; }

        public uint TicksPerHalfSpBar { get; }

        public uint TicksPerFullSpBar { get; }

        /// <summary>Every point award, ordered by commit tick then note-before-sustain.</summary>
        public IReadOnlyList<ScoreEvent> Events => _events;

        /// <summary>
        /// The notes a full combo actually scores, in order — the original note list minus BRE
        /// notes. Star Power activations are indexed into <em>this</em> list by the optimizer and
        /// reported back as <see cref="ScoringNote.NoteIndex"/>, an index into the post-modifier
        /// note track.
        /// </summary>
        public IReadOnlyList<ScoringNote> ScoringNotes => _scoringNotes;

        /// <summary>Combo steps a full combo takes — one per scoring note (chords count once).</summary>
        public int ComboSteps => _scoringNotes.Count;

        /// <summary>
        /// <c>CommittedScore</c> for a full-combo run with no Star Power ever active.
        /// </summary>
        public int CommittedScore { get; }

        /// <summary>
        /// Solo bonuses for a full combo. Not combo-scaled and not SP-scaled
        /// (<c>BaseEngine.Generic.cs:1181-1204</c>), so this is a fixed offset for every path.
        /// </summary>
        public int SoloBonusTotal { get; }

        /// <summary>
        /// <c>TotalScore</c> for a perfect run that never activates Star Power.
        /// <c>BaseStats.TotalScore = CommittedScore + PendingScore + SoloBonuses + CodaBonuses</c>
        /// (<c>BaseStats.cs:34</c>); <c>PendingScore</c> is zero once every sustain has burst, and
        /// coda bonuses are out of the model (see <c>docs/sp-path-design.md</c> §1.6).
        /// </summary>
        public int ProjectPerfectScore() => CommittedScore + SoloBonusTotal;

        // ---------------------------------------------------------------------------------

        public static ScoreModel Build(InstrumentDifficulty<GuitarNote> track, SyncTrack syncTrack,
            int maxMultiplier)
        {
            if (track is null) throw new ArgumentNullException(nameof(track));
            if (syncTrack is null) throw new ArgumentNullException(nameof(syncTrack));

            var notes = track.Notes;

            // BaseEngine.Generic.cs:106-107
            double ticksPerSustainPoint = syncTrack.Resolution / (double) POINTS_PER_BEAT;
            uint sustainBurstThreshold = syncTrack.Resolution / SUSTAIN_BURST_FRACTION;

            var noteEvents = new List<ScoreEvent>(notes.Count);
            var sustainEvents = new List<PendingSustain>();
            var scoringNotes = new List<ScoringNote>(notes.Count);

            // Ticks of the notes that take a combo step, in order — used to resolve the
            // multiplier at each sustain burst.
            var comboTicks = new List<uint>(notes.Count);

            for (int i = 0; i < notes.Count; i++)
            {
                var note = notes[i];

                // BRE notes are never scored and never take a combo step
                // (Guitar/GuitarEngine.cs:247-252, BaseEngine.Generic.cs:76-98).
                //
                // DIVERGENCE: the engine's skip is *conditional* — `if (CodaHasStarted &&
                // note.IsBigRockEnding)` (Guitar/GuitarEngine.cs:247). This skip is
                // unconditional. On a chart where the coda never starts (no coda phrase, or the
                // player never reaches it), the engine would score those notes and count them as
                // combo steps, while this model drops them. Kept unconditional deliberately: a
                // BRE without a coda is malformed charting, and modelling `CodaHasStarted` would
                // require simulating the coda phrase.
                //
                // TODO(slice 3): the drawntotheflame fixture has 0 BRE notes, so neither branch is
                // exercised. The synthetic fixture slice 3 adds must include a BRE — both with and
                // without a coda — to pin which side of this divergence is real.
                if (note.IsBigRockEnding)
                {
                    continue;
                }

                // The note is scored at the multiplier its own combo increment produced
                // (Guitar/GuitarEngine.cs:270-274: IncrementCombo, then UpdateMultiplier, then
                // AddScore), so the combo value is the number of steps *including* this one.
                int multiplier = MultiplierForCombo(comboTicks.Count + 1, maxMultiplier);

                int notePoints = POINTS_PER_NOTE * (1 + note.ChildNotes.Count);
                uint noteMeasureTick = syncTrack.QuarterTickToMeasureTick(note.Tick);
                noteEvents.Add(new ScoreEvent(i, note.Tick, noteMeasureTick, notePoints, multiplier,
                    ScoreEventKind.Note));

                // The phrase is credited at the note carrying IsStarPowerEnd
                // (Guitar/GuitarEngine.cs:263-267), not at Phrase.TickEnd.
                scoringNotes.Add(new ScoringNote(i, note.Tick, noteMeasureTick, note.IsStarPowerEnd));

                // Guitar/GuitarEngine.cs:278-296: a disjoint chord starts one sustain per sustained
                // child (AllNotes includes the parent); anything else starts at most one.
                if (note.IsDisjoint)
                {
                    foreach (var child in note.AllNotes)
                    {
                        if (child.IsSustain)
                        {
                            AddSustain(sustainEvents, i, child, ticksPerSustainPoint, sustainBurstThreshold);
                        }
                    }
                }
                else if (note.IsSustain)
                {
                    AddSustain(sustainEvents, i, note, ticksPerSustainPoint, sustainBurstThreshold);
                }

                comboTicks.Add(note.Tick);
            }

            var events = new List<ScoreEvent>(noteEvents.Count + sustainEvents.Count);
            events.AddRange(noteEvents);

            foreach (var pending in sustainEvents)
            {
                // UpdateSustains runs after CheckForNoteHit in the same engine pass
                // (YargFiveFretGuitarEngine.cs:228-229), so every note at or before the burst tick
                // has already incremented the combo.
                int combo = CountAtOrBefore(comboTicks, pending.BurstTick);
                int multiplier = MultiplierForCombo(combo, maxMultiplier);

                events.Add(new ScoreEvent(pending.NoteIndex, pending.BurstTick,
                    syncTrack.QuarterTickToMeasureTick(pending.BurstTick), pending.Points,
                    multiplier, ScoreEventKind.SustainBurst));
            }

            events.Sort(CompareEvents);

            int soloBonus = CalculateFullComboSoloBonus(notes);

            return new ScoreModel(syncTrack, maxMultiplier, events, scoringNotes, soloBonus);
        }

        // ---------------------------------------------------------------------------------

        /// <summary>
        /// <c>BaseEngine.cs:447-450</c>, minus the Star Power doubling.
        /// <paramref name="combo"/> is <c>BaseStats.Combo</c> at the moment the points are
        /// committed, i.e. *after* the hit note's own increment.
        /// <para/>
        /// Note this is off by one from <c>CalculateChartScores</c>
        /// (<c>Guitar/GuitarEngine.cs:414</c>), which reads the combo *before* the increment. That
        /// is why <c>BaseScore</c> is not the same number as a full-combo no-SP run's
        /// <c>CommittedScore</c>; the engine's live behaviour is what this model reproduces.
        /// </summary>
        private static int MultiplierForCombo(int combo, int maxMultiplier) =>
            Math.Min(combo / 10 + 1, maxMultiplier);

        private static void AddSustain(List<PendingSustain> into, int noteIndex, GuitarNote note,
            double ticksPerSustainPoint, uint sustainBurstThreshold)
        {
            // BaseEngine.Generic.cs:1354-1364 plus the rebasing at :1249-1271: rebases are
            // score-neutral, so the whole sustain always pays
            // ceil(TickLength / TicksPerSustainPoint), committed once.
            int points = (int) Math.Ceiling(note.TickLength / ticksPerSustainPoint);
            if (points <= 0)
            {
                return;
            }

            // BaseEngine.Generic.cs:857-864. A sustain too short for a burst is committed as soon
            // as CurrentTick >= note.Tick, i.e. in the same pass that hit the note.
            uint burstTick = note.TickLength < sustainBurstThreshold
                ? note.Tick
                : note.TickEnd - sustainBurstThreshold;

            into.Add(new PendingSustain(noteIndex, burstTick, points));
        }

        /// <summary>Number of entries in the (ascending) list that are &lt;= <paramref name="tick"/>.</summary>
        private static int CountAtOrBefore(List<uint> ascendingTicks, uint tick)
        {
            int lo = 0;
            int hi = ascendingTicks.Count;
            while (lo < hi)
            {
                int mid = (lo + hi) / 2;
                if (ascendingTicks[mid] <= tick)
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

        private static int CompareEvents(ScoreEvent a, ScoreEvent b)
        {
            int byTick = a.Tick.CompareTo(b.Tick);
            if (byTick != 0) return byTick;

            int byKind = ((int) a.Kind).CompareTo((int) b.Kind);
            if (byKind != 0) return byKind;

            return a.NoteIndex.CompareTo(b.NoteIndex);
        }

        /// <summary>
        /// Replicates <c>GetSoloSections</c> (<c>BaseEngine.Generic.cs:1394-1428</c>) and
        /// <c>EndSolo</c> (<c>:1181-1204</c>) for a full combo, where every solo pays
        /// <c>100 x NoteCount</c> (the hit ratio is 1, so the clamp is 1 and the floor-to-50 is a
        /// no-op). Guitar constructs with <c>isChordSeparate: false</c>, so a chord counts once.
        /// </summary>
        private static int CalculateFullComboSoloBonus(IReadOnlyList<GuitarNote> notes)
        {
            if (notes.Count == 0)
            {
                return 0;
            }

            int total = 0;
            int i = 0;
            while (i < notes.Count)
            {
                bool isStart = notes[i].IsSoloStart || (i == 0 && notes[i].IsSolo);
                if (!isStart)
                {
                    i++;
                    continue;
                }

                int soloNoteCount = 0;
                while (true)
                {
                    soloNoteCount++;
                    bool isEnd = notes[i].IsSoloEnd ||
                        (i == notes.Count - 1 && notes[i].IsSolo);
                    if (isEnd || i + 1 == notes.Count)
                    {
                        break;
                    }

                    i++;
                }

                double points = SOLO_POINTS_PER_NOTE * soloNoteCount;
                points -= points % SOLO_BONUS_ROUNDING;
                total += (int) points;

                i++;
            }

            return total;
        }

        /// <summary>
        /// One combo step: a note the engine will actually score, with the two coordinates the
        /// Star Power window model needs and whether it completes a Star Power phrase.
        /// </summary>
        public readonly struct ScoringNote
        {
            /// <summary>Index into the post-modifier note track.</summary>
            public readonly int NoteIndex;

            /// <summary>Quarter tick of the note.</summary>
            public readonly uint Tick;

            /// <summary>Measure tick of the note — the Star Power coordinate space.</summary>
            public readonly uint MeasureTick;

            /// <summary>
            /// The note carries <c>IsStarPowerEnd</c>, so hitting it awards
            /// <c>TicksPerQuarterSpBar</c> (<c>Guitar/GuitarEngine.cs:263-267</c>).
            /// </summary>
            public readonly bool IsPhraseEnd;

            public ScoringNote(int noteIndex, uint tick, uint measureTick, bool isPhraseEnd)
            {
                NoteIndex = noteIndex;
                Tick = tick;
                MeasureTick = measureTick;
                IsPhraseEnd = isPhraseEnd;
            }
        }

        private readonly struct PendingSustain
        {
            public readonly int NoteIndex;
            public readonly uint BurstTick;
            public readonly int Points;

            public PendingSustain(int noteIndex, uint burstTick, int points)
            {
                NoteIndex = noteIndex;
                BurstTick = burstTick;
                Points = points;
            }
        }
    }
}
