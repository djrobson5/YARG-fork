// NO UnityEngine REFERENCES IN THIS FOLDER. See ScoreEvent.cs for why.

using System;
using System.Collections.Generic;
using YARG.Core.Chart;
using YARG.Core.Engine.Guitar;

namespace YARG.Gameplay.SpPath
{
    /// <summary>
    /// Exact dynamic program over <c>(scoring note index, meter in Star Power ticks)</c>, producing
    /// the activation list that maximises <c>TotalScore</c> for a perfect run.
    /// <para/>
    /// <b>Why this is exact for the modelled subset.</b> A window is a closed-form function of its
    /// activation note and the meter spent (<see cref="SpScoreModel.SimulateWindowTicks"/>), and
    /// what it leaves behind — the next note and the whammy carried to it — is too. Passing a note
    /// without activating is deterministic as well (<see cref="SpScoreModel.MeterTicksAfter"/>).
    /// So the whole future depends only on <c>(next note, meter)</c>, and taking the max over "skip
    /// this note" and "activate here" at every reachable state is exhaustive over the legal
    /// activation sets. It is <em>not</em> exact for real play: full combo, no squeezes,
    /// activation exactly on a note, and either no whammy or full whammy are all assumptions
    /// (<c>docs/sp-path-design.md</c> §2.2 and "Whammy").
    /// <para/>
    /// Without whammy the meter only ever holds 0, ¼, ½, ¾ or a full bar, so there are at most
    /// five states per note — the model this was locked with. With whammy the meter at a note is
    /// whatever has been banked since the last release, so a forward pass first collects the
    /// values that are actually reachable at each note; the backward evaluation then runs over
    /// those exactly as before. No candidate pruning is attempted: pruning is where an
    /// "optimizer" quietly stops being optimal.
    /// </summary>
    public static class SpPathOptimizer
    {
        private const long Unsolved = long.MinValue;

        /// <summary>
        /// The call the game makes: everything the model needs comes off the live
        /// <c>GuitarEngineParameters</c> the player's engine was constructed with
        /// (<c>TrackPlayer.cs:245</c>), so neither <c>MaxMultiplier</c> nor
        /// <c>NoStarPowerOverlap</c> can be dropped by a caller.
        /// </summary>
        /// <param name="unisonPhrases">
        /// The player's own unison phrases, as quarter-tick ranges — see
        /// <see cref="SpUnisonPhrase"/>. Each one pays a second quarter bar on top of the phrase
        /// itself, so leaving it out makes the plan bank half what the engine will and pushes the
        /// activations later than they need to be. <c>null</c> means "this chart has no unisons".
        /// </param>
        public static StarPowerPath Optimize(InstrumentDifficulty<GuitarNote> track,
            SyncTrack syncTrack, GuitarEngineParameters parameters,
            IReadOnlyList<SpUnisonPhrase> unisonPhrases = null) =>
            Optimize(track, syncTrack, parameters, assumeWhammy: false, unisonPhrases);

        /// <param name="assumeWhammy">
        /// Plan as if every Star Power sustain is whammied over its whole span
        /// (<see cref="SpScoreModel.AssumeWhammy"/>). No default; the overload without it means
        /// "no whammy".
        /// </param>
        public static StarPowerPath Optimize(InstrumentDifficulty<GuitarNote> track,
            SyncTrack syncTrack, GuitarEngineParameters parameters, bool assumeWhammy,
            IReadOnlyList<SpUnisonPhrase> unisonPhrases)
        {
            if (parameters is null) throw new ArgumentNullException(nameof(parameters));
            var model = ScoreModel.Build(track, syncTrack, parameters.MaxMultiplier);
            return Optimize(new SpScoreModel(model, parameters.NoStarPowerOverlap, assumeWhammy,
                unisonPhrases));
        }

        /// <param name="noStarPowerOverlap">
        /// Deliberately has no default — see <see cref="SpScoreModel"/>.
        /// </param>
        public static StarPowerPath Optimize(InstrumentDifficulty<GuitarNote> track,
            SyncTrack syncTrack, int maxMultiplier, bool noStarPowerOverlap,
            IReadOnlyList<SpUnisonPhrase> unisonPhrases = null)
        {
            var model = ScoreModel.Build(track, syncTrack, maxMultiplier);
            return Optimize(new SpScoreModel(model, noStarPowerOverlap, unisonPhrases));
        }

        public static StarPowerPath Optimize(SpScoreModel sp)
        {
            if (sp is null) throw new ArgumentNullException(nameof(sp));

            int n = sp.NoteCount;
            uint half = sp.TicksPerHalfSpBar;

            // Forward: the meter values reachable on arriving at each note. Index n is "past the
            // last note", where nothing more can be earned.
            var reachable = new List<uint>[n + 1];
            for (int i = 0; i <= n; i++)
            {
                reachable[i] = new List<uint>();
            }

            var seen = new HashSet<uint>[n + 1];
            for (int i = 0; i <= n; i++)
            {
                seen[i] = new HashSet<uint>();
            }

            void Reach(int note, uint meter)
            {
                if (seen[note].Add(meter))
                {
                    reachable[note].Add(meter);
                }
            }

            // The window opened at (note, meter), computed once and reused by both passes.
            var windows = new Dictionary<(int, uint), SpWindow>();

            SpWindow WindowAt(int note, uint meter)
            {
                if (!windows.TryGetValue((note, meter), out var window))
                {
                    window = sp.SimulateWindowTicks(note, meter);
                    windows.Add((note, meter), window);
                }

                return window;
            }

            if (n > 0)
            {
                Reach(0, sp.InitialMeterTicks);
            }

            for (int i = 0; i < n; i++)
            {
                foreach (uint meter in reachable[i])
                {
                    Reach(i + 1, sp.MeterTicksAfter(i, meter));

                    if (meter >= half)
                    {
                        var window = WindowAt(i, meter);
                        Reach(window.NextNoteIndex, window.CarriedMeterTicks);
                    }
                }
            }

            // Backward: best[(i, meter)] = the most extra points obtainable from note i onwards
            // with that meter; choice = true when the best move is to activate on note i.
            var best = new Dictionary<(int, uint), long>();
            var choice = new Dictionary<(int, uint), bool>();

            foreach (uint meter in reachable[n])
            {
                best[(n, meter)] = 0;
            }

            int states = 0;

            // Solved backwards so no recursion (and no stack depth worry on long charts).
            for (int i = n - 1; i >= 0; i--)
            {
                foreach (uint meter in reachable[i])
                {
                    states++;

                    // Option 1: don't activate on this note. Collect its phrase and the whammy up
                    // to the next note.
                    long value = best[(i + 1, sp.MeterTicksAfter(i, meter))];
                    bool activate = false;

                    // Option 2: activate here, if there is half a bar.
                    if (meter >= half)
                    {
                        var window = WindowAt(i, meter);
                        long candidate = window.DoubledPoints +
                            best[(window.NextNoteIndex, window.CarriedMeterTicks)];

                        // Ties go to activating *here* rather than later. A human has to hit the
                        // marker: an earlier activation leaves more chart behind it to absorb a
                        // late tap, and a marker the player has already passed is worse than one
                        // they have not reached yet. `>=` is what makes that happen — with `>`
                        // the "skip" branch keeps its value and the activation slides to the last
                        // note that still ties. It cannot introduce a pointless zero-gain window:
                        // the activation note is itself inside the window, so DoubledPoints is
                        // always at least POINTS_PER_NOTE. Deterministic either way — every state
                        // is solved from fixed inputs, so the same chart always produces the same
                        // path.
                        if (candidate >= value)
                        {
                            value = candidate;
                            activate = true;
                        }
                    }

                    best[(i, meter)] = value;
                    choice[(i, meter)] = activate;
                }
            }

            // Walk the choices forward to recover the activation list.
            var activations = new List<Activation>();
            var syncTrack = sp.Model.SyncTrack;

            uint walkMeter = sp.InitialMeterTicks;
            for (int i = 0; i < n;)
            {
                if (!choice[(i, walkMeter)])
                {
                    walkMeter = sp.MeterTicksAfter(i, walkMeter);
                    i++;
                    continue;
                }

                var window = WindowAt(i, walkMeter);
                activations.Add(new Activation(
                    window.NoteIndex,
                    window.ActivationTick,
                    window.ActivationMeasureTick,
                    window.EndMeasureTick,
                    syncTrack.TickToTime(window.ActivationTick),
                    // FindMinTimeForMeasureTick is a ~100-iteration binary search; it is correct to
                    // call once per emitted marker and never in a loop (design doc §2.1).
                    syncTrack.FindMinTimeForMeasureTick(window.EndMeasureTick),
                    window.MeterQuarterBars,
                    window.MeterTicks,
                    (int) window.DoubledPoints,
                    window.ScoringNoteIndex));

                i = window.NextNoteIndex;
                walkMeter = window.CarriedMeterTicks;
            }

            // The phrase ends the model is counting on, kept so the log can be checked against the
            // engine's own TotalStarPowerPhrases and against the chart. Same authority the engine
            // uses to award (IsStarPowerEnd, Guitar/GuitarEngine.cs:263-267), so a mismatch means
            // the model is looking at a different note track than the engine is.
            var phraseEndTicks = new List<uint>();
            foreach (var note in sp.Model.ScoringNotes)
            {
                if (note.IsPhraseEnd)
                {
                    phraseEndTicks.Add(note.Tick);
                }
            }

            long extra = n == 0 ? 0 : best[(0, sp.InitialMeterTicks)];
            return new StarPowerPath(activations, sp.Model.ProjectPerfectScore() + (int) extra,
                (int) extra, sp.TicksPerQuarterSpBar, phraseEndTicks,
                sp.UnisonPhraseEndTicks, sp.AssumeWhammy, states);
        }
    }

    /// <summary>
    /// The computed path: where to activate, and what a perfect run following it scores.
    /// </summary>
    public sealed class StarPowerPath
    {
        public StarPowerPath(IReadOnlyList<Activation> activations, int projectedScore,
            int scoreGainOverNoActivations, uint ticksPerQuarterSpBar,
            IReadOnlyList<uint> phraseEndTicks, IReadOnlyList<uint> unisonPhraseEndTicks = null,
            bool assumesWhammy = false, int stateCount = 0)
        {
            Activations = activations;
            ProjectedScore = projectedScore;
            ScoreGainOverNoActivations = scoreGainOverNoActivations;
            TicksPerQuarterSpBar = ticksPerQuarterSpBar;
            PhraseEndTicks = phraseEndTicks ?? Array.Empty<uint>();
            UnisonPhraseEndTicks = unisonPhraseEndTicks ?? Array.Empty<uint>();
            AssumesWhammy = assumesWhammy;
            StateCount = stateCount;
        }

        /// <summary>Whether the plan assumes every Star Power sustain is whammied in full.</summary>
        public bool AssumesWhammy { get; }

        /// <summary>
        /// <c>(note, meter)</c> states the optimizer solved — at most five per note without whammy.
        /// Logged with the path so the cost of the whammy model can be watched on real charts.
        /// </summary>
        public int StateCount { get; }

        /// <summary>Ordered by activation tick.</summary>
        public IReadOnlyList<Activation> Activations { get; }

        /// <summary><c>TotalScore</c> of a perfect run following this path.</summary>
        public int ProjectedScore { get; }

        /// <summary>How much the path beats never activating at all.</summary>
        public int ScoreGainOverNoActivations { get; }

        /// <summary>
        /// One quarter bar of meter, in Star Power ticks (<c>BaseEngine.cs:168</c>). Carried on the
        /// path so the Unity-side divergence check can turn <see cref="Activation.MeterAtActivation"/>
        /// into the tick count to compare <c>BaseStats.StarPowerTickAmount</c> against, without
        /// re-deriving the constant a third time.
        /// </summary>
        public uint TicksPerQuarterSpBar { get; }

        /// <summary>
        /// Quarter ticks of the notes the model expects to award a phrase on — every scoring note
        /// carrying <c>IsStarPowerEnd</c>. Diagnostics only: comparing its count against the
        /// engine's <c>BaseStats.TotalStarPowerPhrases</c> is what catches the model and the engine
        /// disagreeing about which phrases exist.
        /// </summary>
        public IReadOnlyList<uint> PhraseEndTicks { get; }

        /// <summary>
        /// The subset of <see cref="PhraseEndTicks"/> the model expects a unison bonus on — a
        /// second <see cref="TicksPerQuarterSpBar"/> from <c>BaseEngine.AwardUnisonBonus</c>
        /// (<c>BaseEngine.cs:637-641</c>). Empty on a chart with no unisons, in which case the
        /// plan is identical to the pre-unison model's.
        /// </summary>
        public IReadOnlyList<uint> UnisonPhraseEndTicks { get; }

        public override string ToString() =>
            $"{Activations.Count} activation(s), projected {ProjectedScore} " +
            $"(+{ScoreGainOverNoActivations} over no Star Power), " +
            $"{PhraseEndTicks.Count} phrase(s), " +
            $"{UnisonPhraseEndTicks.Count} unison(s), " +
            $"{(AssumesWhammy ? "full whammy" : "no whammy")}, {StateCount} state(s)";
    }

    /// <summary>One point on the path. Plain C#, no <c>UnityEngine</c> types — see §3 of the design doc.</summary>
    public readonly struct Activation
    {
        /// <summary>Index into the post-modifier note track (<c>TrackPlayer.Notes</c>).</summary>
        public readonly int NoteIndex;

        /// <summary>Quarter tick of that note — where the player has to hit the Star Power input.</summary>
        public readonly uint ActivationTick;

        /// <summary>Measure tick of that note; the window start, inclusive.</summary>
        public readonly uint ActivationMeasureTick;

        /// <summary>Measure tick the window ends at, exclusive (<c>E</c>).</summary>
        public readonly uint EndMeasureTick;

        /// <summary>Time of the activation note, for rendering.</summary>
        public readonly double ActivationTime;

        /// <summary>Time the window ends, for rendering the far edge of a region marker.</summary>
        public readonly double EndTime;

        /// <summary>Meter spent, in whole quarter bars (2..4). Exact without whammy; for logs.</summary>
        public readonly int MeterAtActivation;

        /// <summary>Meter spent, in Star Power ticks — the divergence check compares against this.</summary>
        public readonly uint MeterTicksAtActivation;

        /// <summary>Points this window adds over not activating.</summary>
        public readonly int ScoreGain;

        /// <summary>Index into <see cref="ScoreModel.ScoringNotes"/>, for the model's own bookkeeping.</summary>
        public readonly int ScoringNoteIndex;

        public Activation(int noteIndex, uint activationTick, uint activationMeasureTick,
            uint endMeasureTick, double activationTime, double endTime, int meterAtActivation,
            uint meterTicksAtActivation, int scoreGain, int scoringNoteIndex)
        {
            MeterTicksAtActivation = meterTicksAtActivation;
            NoteIndex = noteIndex;
            ActivationTick = activationTick;
            ActivationMeasureTick = activationMeasureTick;
            EndMeasureTick = endMeasureTick;
            ActivationTime = activationTime;
            EndTime = endTime;
            MeterAtActivation = meterAtActivation;
            ScoreGain = scoreGain;
            ScoringNoteIndex = scoringNoteIndex;
        }

        public override string ToString() =>
            $"note {NoteIndex} @ {ActivationTime:0.000}s (measure tick {ActivationMeasureTick}) " +
            $"with {MeterAtActivation}/4 bar, ends at {EndMeasureTick}, +{ScoreGain}";
    }
}
