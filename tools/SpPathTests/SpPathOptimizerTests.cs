using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using NUnit.Framework;
using YARG.Core.Chart;
using YARG.Core.Engine.Guitar;
using YARG.Core.Engine.Guitar.Engines;
using YARG.Gameplay.SpPath;

namespace YARG.SpPathTests;

/// <summary>
/// Slice 3, parts B and C: the Star Power window model must reproduce the engine's
/// <c>TotalScore</c> exactly for <em>any</em> activation list, and the optimizer must beat the
/// stock greedy bot while its own projection is reproducible on the engine.
/// <para/>
/// The hand-picked activation sets exist so the model is shown to be <em>right</em>, not merely
/// self-consistent with the optimizer that produced it.
/// </summary>
[TestFixture]
public class SpPathOptimizerTests
{
    /// <summary>
    /// The optimizer's result on Expert guitar, default preset. Greedy scores 376,558 and a run
    /// with no Star Power at all scores 317,774.
    /// </summary>
    public const int DrawnToTheFlameGuitarOptimalScore = 392_750;

    /// <summary>Same on Expert bass (MaxMultiplier 6, no solo on this chart's bass).</summary>
    public const int DrawnToTheFlameBassOptimalScore = 484_979;

    /// <summary>The stock greedy bot on Expert bass, for the "must not lose" comparison.</summary>
    public const int DrawnToTheFlameBassGreedyBotScore = 465_083;

    internal sealed record Fixture(
        bool IsBass, InstrumentDifficulty<GuitarNote> Notes, SyncTrack Sync,
        GuitarEngineParameters Params, ScoreModel Model, SpScoreModel Sp);

    internal static Fixture Load(bool isBass = false)
    {
        var chart = ChartFixtures.LoadChart();
        var notes = ChartFixtures.GuitarNotes(chart, isBass);
        var p = ChartFixtures.GuitarParams(isBass);
        var model = ScoreModel.Build(notes, chart.SyncTrack, p.MaxMultiplier);
        return new Fixture(isBass, notes, chart.SyncTrack, p, model,
            SpScoreModel.FromParameters(model, p));
    }

    // -------------------------------------------------------------------------------------
    // B — the window model against the engine, on activation sets the optimizer did not pick
    // -------------------------------------------------------------------------------------

    /// <summary>
    /// Hand-picked activation sets, in <b>note-track indices</b>. Each is legal (half a bar
    /// available, no activation inside an earlier window) and none is what the optimizer picks.
    /// </summary>
    /// <remarks>
    /// Chosen to straddle the two boundaries risk 4 of the design doc calls out — the sustain
    /// burst tick and the window end:
    /// <list type="bullet">
    /// <item><c>EarliestPossible</c> fires on the first note after every second phrase, i.e. the
    /// greedy policy expressed as notes.</item>
    /// <item><c>Hoarded</c> waits for a full bar every time, so every window runs at least the
    /// full 8 measures and the phrase-while-active extension has to be right.</item>
    /// <item><c>OnPhraseEndNotes</c> activates <em>on</em> the phrase note itself — the case where
    /// the activation and the phrase award happen in the same engine pass, in that order.</item>
    /// <item><c>LateAndSparse</c> spends a full bar in three places the optimizer avoids.</item>
    /// </list>
    /// </remarks>
    private static IEnumerable<TestCaseData> HandPickedPaths()
    {
        yield return new TestCaseData(new[] { 87, 238, 365, 443, 547, 645, 806, 1008, 1107 })
            .SetName("HandPicked_EarliestPossible");
        yield return new TestCaseData(new[] { 238, 458, 717, 1052 })
            .SetName("HandPicked_Hoarded");
        yield return new TestCaseData(new[] { 154, 364, 546, 805, 1106 })
            .SetName("HandPicked_OnPhraseEndNotes");
        yield return new TestCaseData(new[] { 500, 900, 1150 })
            .SetName("HandPicked_LateAndSparse");
    }

    [TestCaseSource(nameof(HandPickedPaths))]
    public void SpModel_MatchesTheEngine_ForAHandPickedActivationList(int[] activationNotes)
    {
        AssertModelMatchesScriptedRun(Load(), activationNotes);
    }

    /// <summary>
    /// The stock greedy bot's own run, checked against the window model directly.
    /// </summary>
    /// <remarks>
    /// The greedy set cannot be replayed as note indices: the policy activates on whatever engine
    /// pass follows the meter reaching half (<c>YargFiveFretGuitarEngine.cs:30</c>), which is a
    /// bare frame tick a few milliseconds <em>before</em> the note it first doubles. Re-anchoring
    /// those activations to notes shifts every window end and, on guitar, starves the fourth
    /// activation of meter outright. So this test checks the model against the greedy run as it
    /// actually happened — same window walk, same doubled-interval arithmetic, arbitrary
    /// activation ticks — rather than pretending the path is note-aligned.
    /// </remarks>
    [TestCase(false, GoldenScoreTests.DrawnToTheFlameGuitarGreedyBotScore, TestName = "Guitar")]
    [TestCase(true, DrawnToTheFlameBassGreedyBotScore, TestName = "Bass")]
    public void SpModel_MatchesTheStockGreedyBotsActualRun(bool isBass, int greedyGolden)
    {
        var f = Load(isBass);

        var engine = new ScriptedBotGuitarEngine(f.Notes, f.Sync, f.Params, useStockPolicy: true);
        BotRunner.RunToEnd(engine, f.Notes);

        long extra = 0;
        foreach (var w in engine.Windows)
        {
            uint modelledEnd = f.Sp.WindowEndAt(w.ActivationMeasureTick, w.MeterAtActivation);
            TestContext.Out.WriteLine($"window [{w.ActivationMeasureTick}, {w.EndMeasureTick}) " +
                $"meter {w.MeterAtActivation}, model end {modelledEnd}");

            Assert.That(modelledEnd, Is.EqualTo(w.EndMeasureTick),
                "The window walk disagrees with the engine on where Star Power ended.");

            extra += f.Sp.PointsIn(w.ActivationMeasureTick, w.EndMeasureTick);
        }

        int projected = f.Model.ProjectPerfectScore() + (int) extra;
        TestContext.Out.WriteLine($"model {projected} vs engine {engine.EngineStats.TotalScore}");

        Assert.Multiple(() =>
        {
            Assert.That(engine.EngineStats.NotesHit, Is.EqualTo(engine.EngineStats.TotalNotes));
            Assert.That(engine.EngineStats.StarPowerActivationCount,
                Is.EqualTo(engine.Windows.Count));
            Assert.That(engine.EngineStats.TotalScore, Is.EqualTo(greedyGolden));
            Assert.That(projected, Is.EqualTo(engine.EngineStats.TotalScore));
        });
    }

    private static void AssertModelMatchesScriptedRun(Fixture f, int[] activationNotes)
    {
        var scoringIndices = activationNotes
            .Select(n => SpSemanticsTests.ScoringIndexOf(f.Sp, n))
            .ToArray();

        var windows = new List<SpWindow>();
        long extra = f.Sp.DoubledPointsForActivations(scoringIndices, windows);
        int projected = f.Model.ProjectPerfectScore() + (int) extra;

        var engine = new ScriptedBotGuitarEngine(f.Notes, f.Sync, f.Params, activationNotes);
        BotRunner.RunToEnd(engine, f.Notes);

        TestContext.Out.WriteLine($"model {projected} vs engine {engine.EngineStats.TotalScore}");
        foreach (var w in windows) TestContext.Out.WriteLine("  " + w);

        Assert.Multiple(() =>
        {
            Assert.That(engine.EngineStats.NotesHit, Is.EqualTo(engine.EngineStats.TotalNotes));
            Assert.That(engine.EngineStats.StarPowerActivationCount,
                Is.EqualTo(activationNotes.Length),
                "Every scripted activation must actually have fired.");

            // Window by window, not just the total: compensating errors would pass a total-only
            // check.
            Assert.That(engine.Windows.Select(w => w.ActivationMeasureTick).ToArray(),
                Is.EqualTo(windows.Select(w => w.ActivationMeasureTick).ToArray()),
                "Window start mismatch.");
            Assert.That(engine.Windows.Select(w => w.EndMeasureTick).ToArray(),
                Is.EqualTo(windows.Select(w => w.EndMeasureTick).ToArray()),
                "Window end mismatch — the drain or the phrase extension is modelled wrong.");

            Assert.That(projected, Is.EqualTo(engine.EngineStats.TotalScore));
        });
    }

    // -------------------------------------------------------------------------------------
    // C — the optimizer
    // -------------------------------------------------------------------------------------

    [TestCase(false, GoldenScoreTests.DrawnToTheFlameGuitarGreedyBotScore,
        DrawnToTheFlameGuitarOptimalScore, TestName = "Guitar")]
    [TestCase(true, DrawnToTheFlameBassGreedyBotScore,
        DrawnToTheFlameBassOptimalScore, TestName = "Bass")]
    public void Optimizer_BeatsGreedy_AndItsProjectionIsReproducibleOnTheEngine(
        bool isBass, int greedyGolden, int optimalGolden)
    {
        var f = Load(isBass);

        var stopwatch = Stopwatch.StartNew();
        var path = SpPathOptimizer.Optimize(f.Sp);
        stopwatch.Stop();
        double cold = stopwatch.Elapsed.TotalMilliseconds;

        // The cold number is mostly JIT. The one the design doc quotes is the warm median, since
        // that is what the loading screen actually pays. Both are printed so the doc's figure can
        // be re-derived from a test run rather than remembered.
        var samples = new List<double>();
        for (int i = 0; i < 9; i++)
        {
            var again = Stopwatch.StartNew();
            SpPathOptimizer.Optimize(f.Sp);
            again.Stop();
            samples.Add(again.Elapsed.TotalMilliseconds);
        }

        samples.Sort();
        TestContext.Out.WriteLine($"{path} over {f.Sp.NoteCount} notes; " +
            $"solve {cold:0.00} ms cold, {samples[samples.Count / 2]:0.00} ms warm median " +
            $"(min {samples[0]:0.00}, max {samples[^1]:0.00}), " +
#if DEBUG
            "Debug build");
#else
            "Release build");
#endif
        foreach (var a in path.Activations) TestContext.Out.WriteLine("  " + a);

        var engine = new ScriptedBotGuitarEngine(f.Notes, f.Sync, f.Params,
            path.Activations.Select(a => a.NoteIndex));
        BotRunner.RunToEnd(engine, f.Notes);

        GreedyActivationNotes(f, out int greedyScore);
        TestContext.Out.WriteLine($"engine TotalScore = {engine.EngineStats.TotalScore}, " +
            $"greedy = {greedyScore}");

        Assert.Multiple(() =>
        {
            Assert.That(greedyScore, Is.EqualTo(greedyGolden), "The greedy golden changed.");
            Assert.That(engine.EngineStats.NotesHit, Is.EqualTo(engine.EngineStats.TotalNotes));
            Assert.That(engine.EngineStats.StarPowerActivationCount,
                Is.EqualTo(path.Activations.Count));

            Assert.That(engine.EngineStats.TotalScore, Is.EqualTo(path.ProjectedScore),
                "A scripted run of the optimizer's own path must reproduce the projection exactly.");
            Assert.That(path.ProjectedScore, Is.GreaterThanOrEqualTo(greedyGolden),
                "The optimizer is not allowed to lose to the stock greedy bot.");
            Assert.That(path.ProjectedScore, Is.EqualTo(optimalGolden));

            // Ordered, and no activation inside an earlier window.
            for (int i = 1; i < path.Activations.Count; i++)
            {
                Assert.That(path.Activations[i].ActivationMeasureTick,
                    Is.GreaterThanOrEqualTo(path.Activations[i - 1].EndMeasureTick));
            }

            Assert.That(path.Activations.All(a => a.MeterAtActivation is >= 2 and <= 4));
            Assert.That(path.Activations.All(a => a.EndTime > a.ActivationTime));
        });
    }

    /// <summary>
    /// The DP against an exhaustive search over activation <em>sets</em>. Run on a truncated
    /// prefix of the real chart so the enumeration is tractable while the sync track, sustains
    /// and phrases stay real. <see cref="SyntheticChartTests"/> runs the same comparison over a
    /// chart with tempo and time-signature changes.
    /// </summary>
    [Test]
    public void Optimizer_AgreesWithBruteForce_OnAChartPrefix()
    {
        var f = Load();
        var truncated = new InstrumentDifficulty<GuitarNote>(
            f.Notes.Instrument, f.Notes.Difficulty, f.Notes.Notes.Take(300).ToList(),
            f.Notes.Phrases, f.Notes.TextEvents);

        var prefix = new SpScoreModel(
            ScoreModel.Build(truncated, f.Sync, f.Params.MaxMultiplier), f.Params.NoStarPowerOverlap);

        var dp = SpPathOptimizer.Optimize(prefix);
        var brute = BruteForce.Best(prefix);

        TestContext.Out.WriteLine($"dp = {dp.ScoreGainOverNoActivations} at " +
            $"[{string.Join(",", dp.Activations.Select(a => a.ScoringNoteIndex))}], " +
            $"brute = {brute.Extra} at [{string.Join(",", brute.Activations)}]");

        Assert.That(dp.ScoreGainOverNoActivations, Is.EqualTo((int) brute.Extra));
    }

    // -------------------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------------------

    /// <summary>
    /// The note the stock greedy bot (<c>YargFiveFretGuitarEngine.cs:30</c>) first doubles in each
    /// of its windows, recovered by watching <c>OnStarPowerStatus</c>.
    /// </summary>
    internal static List<int> GreedyActivationNotes(Fixture f, out int totalScore)
    {
        var engine = new YargFiveFretGuitarEngine(f.Notes, f.Sync,
            ChartFixtures.GuitarParams(f.IsBass), isBot: true);

        var activationTicks = new List<uint>();
        engine.OnStarPowerStatus += active =>
        {
            if (active) activationTicks.Add(engine.StarPowerTickActivationPosition);
        };

        BotRunner.RunToEnd(engine, f.Notes);
        totalScore = engine.EngineStats.TotalScore;

        var notes = f.Model.ScoringNotes;
        var result = new List<int>();
        foreach (uint tick in activationTicks)
        {
            for (int i = 0; i < notes.Count; i++)
            {
                if (notes[i].MeasureTick >= tick)
                {
                    result.Add(notes[i].NoteIndex);
                    break;
                }
            }
        }

        return result;
    }
}

/// <summary>
/// An exhaustive search over activation sets, deliberately written as a different shape from the
/// DP: it enumerates candidate <em>sets</em> and scores each one through
/// <see cref="SpScoreModel.DoubledPointsForActivations"/>, rather than solving state by state.
/// Illegal sets (not enough meter, or an activation swallowed by an earlier window) throw and are
/// skipped, which also exercises that validation.
/// </summary>
internal static class BruteForce
{
    public static (long Extra, IReadOnlyList<int> Activations) Best(SpScoreModel sp)
    {
        // Upper bound on the number of activations any legal path can have, so the enumeration
        // terminates. It is exact, not a guess: meter comes only from phrases, one quarter bar
        // each (AwardStarPower, BaseEngine.Generic.cs:1158-1163); every activation spends at
        // least MinQuarterBarsToActivate of them (BaseEngine.cs:44); and a phrase collected while
        // Star Power is active extends the window instead of banking, so it can never fund a
        // later activation. Hence activations <= floor(phrases / 2).
        int phrases = 0;
        for (int i = 0; i < sp.NoteCount; i++)
        {
            if (sp.Model.ScoringNotes[i].IsPhraseEnd) phrases++;
        }

        int maxActivations = phrases / SpScoreModel.MinQuarterBarsToActivate;

        long best = 0;
        IReadOnlyList<int> bestSet = Array.Empty<int>();
        var current = new List<int>();

        void Search(int from)
        {
            if (current.Count > 0)
            {
                try
                {
                    long extra = sp.DoubledPointsForActivations(current);
                    if (extra > best)
                    {
                        best = extra;
                        bestSet = current.ToArray();
                    }
                }
                catch (ArgumentException)
                {
                    // This prefix is illegal, so prune the whole subtree below it. That is sound
                    // because legality of the k-th activation is a function of the activations
                    // *before* it only — the meter it has available and whether an earlier window
                    // has already swallowed it. Appending more activations after an illegal one
                    // therefore cannot rescue it, so every superset that keeps this prefix is
                    // illegal too. Sets that drop the offending index are still enumerated: they
                    // live under the sibling branches of the loop below, which this return does
                    // not touch.
                    return;
                }
            }

            if (current.Count >= maxActivations)
            {
                return;
            }

            for (int i = from; i < sp.NoteCount; i++)
            {
                current.Add(i);
                Search(i + 1);
                current.RemoveAt(current.Count - 1);
            }
        }

        Search(0);
        return (best, bestSet);
    }
}
