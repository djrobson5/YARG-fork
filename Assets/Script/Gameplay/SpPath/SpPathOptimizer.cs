// NO UnityEngine REFERENCES IN THIS FOLDER. See ScoreEvent.cs for why.

using System;
using System.Collections.Generic;
using YARG.Core.Chart;
using YARG.Core.Engine.Guitar;

namespace YARG.Gameplay.SpPath
{
    /// <summary>
    /// Exact dynamic program over <c>(scoring note index, meter in quarter bars)</c>, producing the
    /// activation list that maximises <c>TotalScore</c> for a perfect run.
    /// <para/>
    /// <b>Why this is exact for the modelled subset.</b> With phrases-only gain the meter takes
    /// exactly five values (0, ¼, ½, ¾, full), and a window is a closed-form function of its
    /// activation note and the meter spent (<see cref="SpScoreModel.SimulateWindow"/>) — the walk
    /// through the phrase ends inside it is deterministic, and it always leaves the meter at 0. So
    /// the whole future depends only on <c>(next note, meter)</c>, and taking the max over "skip
    /// this note" and "activate here" at every state is exhaustive over the reachable activation
    /// sets. It is <em>not</em> exact for real play: full combo, no whammy, no squeezes, and
    /// activation exactly on a note are all assumptions (<c>docs/sp-path-design.md</c> §2.2).
    /// <para/>
    /// Cost is <c>O(notes × 5 × window length)</c>: five meter states per note, and the activation
    /// transition walks the notes inside its window. On a 1,269-note chart that is well under a
    /// millisecond, which is why no candidate pruning is attempted — the simple version is exact
    /// and fast enough, and pruning is where an "optimizer" quietly stops being optimal.
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
            IReadOnlyList<SpUnisonPhrase> unisonPhrases = null)
        {
            if (parameters is null) throw new ArgumentNullException(nameof(parameters));
            return Optimize(track, syncTrack, parameters.MaxMultiplier,
                parameters.NoStarPowerOverlap, unisonPhrases);
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
            int states = SpScoreModel.MaxQuarterBars + 1;

            // best[i * states + q] = the most extra points obtainable from note i onwards with
            // meter q. choice[...] = the note to activate on, or -1 for "don't activate here".
            var best = new long[(n + 1) * states];
            var choice = new int[(n + 1) * states];

            for (int i = 0; i < best.Length; i++)
            {
                best[i] = Unsolved;
                choice[i] = -1;
            }

            for (int q = 0; q < states; q++)
            {
                best[n * states + q] = 0;
            }

            // Solved backwards so no recursion (and no stack depth worry on long charts).
            for (int i = n - 1; i >= 0; i--)
            {
                for (int q = 0; q < states; q++)
                {
                    // Option 1: don't activate on this note. Collect its phrase if it has one.
                    int nextMeter = sp.MeterAfter(i, q);
                    long value = best[(i + 1) * states + nextMeter];
                    int pick = -1;

                    // Option 2: activate here, if there is half a bar.
                    if (q >= SpScoreModel.MinQuarterBarsToActivate)
                    {
                        var window = sp.SimulateWindow(i, q);
                        long candidate = window.DoubledPoints + best[window.NextNoteIndex * states];

                        // Ties go to activating *here* rather than later. A human has to hit the
                        // marker: an earlier activation leaves more chart behind it to absorb a
                        // late tap, and a marker the player has already passed is worse than one
                        // they have not reached yet. `>=` is what makes that happen — with `>`
                        // the "skip" branch keeps its value and the activation slides to the last
                        // note that still ties. It cannot introduce a pointless zero-gain window:
                        // the activation note is itself inside [m, E), so DoubledPoints is always
                        // at least POINTS_PER_NOTE. Deterministic either way — the scan order over
                        // (i, q) is fixed, so the same chart always produces the same path.
                        if (candidate >= value)
                        {
                            value = candidate;
                            pick = i;
                        }
                    }

                    best[i * states + q] = value;
                    choice[i * states + q] = pick;
                }
            }

            // Walk the choices forward to recover the activation list.
            var activations = new List<Activation>();
            var syncTrack = sp.Model.SyncTrack;

            for (int i = 0, q = 0; i < n;)
            {
                if (choice[i * states + q] < 0)
                {
                    q = sp.MeterAfter(i, q);
                    i++;
                    continue;
                }

                var window = sp.SimulateWindow(i, q);
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
                    (int) window.DoubledPoints,
                    window.ScoringNoteIndex));

                i = window.NextNoteIndex;
                q = 0;
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

            long extra = n == 0 ? 0 : best[0];
            return new StarPowerPath(activations, sp.Model.ProjectPerfectScore() + (int) extra,
                (int) extra, sp.TicksPerQuarterSpBar, phraseEndTicks,
                sp.UnisonPhraseEndTicks);
        }
    }

    /// <summary>
    /// The computed path: where to activate, and what a perfect run following it scores.
    /// </summary>
    public sealed class StarPowerPath
    {
        public StarPowerPath(IReadOnlyList<Activation> activations, int projectedScore,
            int scoreGainOverNoActivations, uint ticksPerQuarterSpBar,
            IReadOnlyList<uint> phraseEndTicks, IReadOnlyList<uint> unisonPhraseEndTicks = null)
        {
            Activations = activations;
            ProjectedScore = projectedScore;
            ScoreGainOverNoActivations = scoreGainOverNoActivations;
            TicksPerQuarterSpBar = ticksPerQuarterSpBar;
            PhraseEndTicks = phraseEndTicks ?? Array.Empty<uint>();
            UnisonPhraseEndTicks = unisonPhraseEndTicks ?? Array.Empty<uint>();
        }

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
            $"{UnisonPhraseEndTicks.Count} unison(s)";
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

        /// <summary>Meter spent, in quarter bars (2..4) — the divergence check compares against this.</summary>
        public readonly int MeterAtActivation;

        /// <summary>Points this window adds over not activating.</summary>
        public readonly int ScoreGain;

        /// <summary>Index into <see cref="ScoreModel.ScoringNotes"/>, for the model's own bookkeeping.</summary>
        public readonly int ScoringNoteIndex;

        public Activation(int noteIndex, uint activationTick, uint activationMeasureTick,
            uint endMeasureTick, double activationTime, double endTime, int meterAtActivation,
            int scoreGain, int scoringNoteIndex)
        {
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
