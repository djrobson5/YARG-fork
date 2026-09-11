using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using YARG.Core.Chart;
using YARG.Core.Engine.Guitar;
using YARG.Core.Engine.Guitar.Engines;
using YARG.Gameplay.SpPath;

namespace YARG.SpPathTests;

/// <summary>
/// Slice 3, part D: the same model checks as <see cref="SpPathOptimizerTests"/>, but on
/// <see cref="SyntheticChart"/> — the fixture built specifically to exercise the branches
/// <c>drawntotheflame.mid</c> has zero instances of.
/// <para/>
/// The important one is the <b>meter change</b>. On a single-4/4 chart, measure ticks are a
/// constant multiple of quarter ticks, so a flat-beat Star Power bar (CHOpt's model) and YARG's
/// measure-based one are indistinguishable. Here they are not, and
/// <see cref="StarPowerBar_IsMeterAware_NotFlatBeat"/> says so with a number.
/// </summary>
[TestFixture]
public class SyntheticChartTests
{
    /// <summary>Full combo, Star Power suppressed, Expert guitar on the synthetic chart.</summary>
    public const int SyntheticNoSpScore = 30_692;

    /// <summary>The optimizer's result on the synthetic chart.</summary>
    public const int SyntheticOptimalScore = 55_204;

    /// <summary>The stock greedy bot on the synthetic chart.</summary>
    public const int SyntheticGreedyBotScore = 53_327;

    private sealed record Fixture(
        SongChart Chart, InstrumentDifficulty<GuitarNote> Notes, SyncTrack Sync,
        GuitarEngineParameters Params, ScoreModel Model, SpScoreModel Sp);

    private static Fixture Load(bool includeCoda = true)
    {
        var chart = SyntheticChart.Load(includeCoda);
        var notes = SyntheticChart.GuitarNotes(chart);
        var p = ChartFixtures.GuitarParams();
        var model = ScoreModel.Build(notes, chart.SyncTrack, p.MaxMultiplier);
        return new Fixture(chart, notes, chart.SyncTrack, p, model,
            SpScoreModel.FromParameters(model, p));
    }

    // -------------------------------------------------------------------------------------

    /// <summary>
    /// The fixture only earns its keep if it really contains the shapes it was built for. If a
    /// parser change silently drops one, the tests below would keep passing while covering
    /// nothing, so the coverage itself is asserted.
    /// </summary>
    [Test]
    public void Fixture_CoversTheBranchesTheRealChartDoesNot()
    {
        var f = Load();
        var notes = f.Notes.Notes;

        uint burstThreshold = f.Sync.Resolution / ScoreModel.SUSTAIN_BURST_FRACTION;

        int shortSustains = notes.Count(n => n.IsSustain && n.TickLength < burstThreshold);
        int openNotes = 0;
        foreach (var note in notes)
        {
            foreach (var child in note.AllNotes)
            {
                if (child.Fret == (int) FiveFretGuitarFret.Open) openNotes++;
            }
        }

        TestContext.Out.WriteLine($"notes={notes.Count} " +
            $"chords={notes.Count(n => n.ChildNotes.Count > 0)} " +
            $"disjoint={notes.Count(n => n.IsDisjoint)} " +
            $"sustains={notes.Count(n => n.IsSustain)} short={shortSustains} " +
            $"extended={notes.Count(n => n.IsExtendedSustain)} open={openNotes} " +
            $"bre={notes.Count(n => n.IsBigRockEnding)} solo={notes.Count(n => n.IsSolo)} " +
            $"phraseEnds={notes.Count(n => n.IsStarPowerEnd)}");

        Assert.Multiple(() =>
        {
            Assert.That(f.Sync.TimeSignatures.Select(t => $"{t.Numerator}/{t.Denominator}"),
                Is.EqualTo(new[] { "4/4", "3/4", "4/4" }), "The meter change is missing.");
            Assert.That(f.Sync.Tempos, Has.Count.EqualTo(2), "The tempo change is missing.");

            Assert.That(notes.Count(n => n.IsDisjoint), Is.GreaterThan(0), "no disjoint chord");
            Assert.That(shortSustains, Is.GreaterThan(0), "no sub-burst-threshold sustain");
            Assert.That(notes.Count(n => n.IsExtendedSustain), Is.GreaterThan(0),
                "no extended sustain");
            Assert.That(openNotes, Is.GreaterThan(0), "no open note");
            Assert.That(notes.Count(n => n.IsBigRockEnding), Is.GreaterThan(0), "no BRE");
            Assert.That(notes.Count(n => n.IsSolo), Is.GreaterThan(0), "no solo");
            Assert.That(notes.Count(n => n.IsStarPowerEnd), Is.GreaterThanOrEqualTo(6),
                "not enough Star Power phrases to path with");

            // The disjoint chord's children must have *different* lengths, otherwise it is an
            // ordinary chord as far as sustain scoring is concerned.
            var disjoint = notes.First(n => n.IsDisjoint);
            var lengths = new HashSet<uint>();
            foreach (var child in disjoint.AllNotes) lengths.Add(child.TickLength);
            Assert.That(lengths, Has.Count.GreaterThan(1),
                "The disjoint chord's children all have the same sustain length.");
        });
    }

    /// <summary>
    /// The measure-tick coordinate the Star Power bar drains in is genuinely meter-aware: across
    /// the 3/4 section it advances 4/3 as fast per quarter tick as it does in 4/4. A flat-beat bar
    /// would put the window end somewhere else entirely.
    /// </summary>
    [Test]
    public void StarPowerBar_IsMeterAware_NotFlatBeat()
    {
        var f = Load();

        uint fourFourStart = f.Sync.QuarterTickToMeasureTick(0);
        uint fourFourEnd = f.Sync.QuarterTickToMeasureTick(1440);
        uint threeFourStart = f.Sync.QuarterTickToMeasureTick((uint) SyntheticChart.ThreeFourTick);
        uint threeFourEnd = f.Sync.QuarterTickToMeasureTick((uint) SyntheticChart.ThreeFourTick + 1440);

        TestContext.Out.WriteLine($"1440 quarter ticks span {fourFourEnd - fourFourStart} measure " +
            $"ticks in 4/4 and {threeFourEnd - threeFourStart} in 3/4");

        Assert.Multiple(() =>
        {
            Assert.That(fourFourEnd - fourFourStart, Is.EqualTo(1440u),
                "4/4: one measure is 1920 quarter ticks and 1920 measure ticks, so 1:1.");
            Assert.That(threeFourEnd - threeFourStart, Is.EqualTo(1920u),
                "3/4: one measure is 1440 quarter ticks but still 1920 measure ticks.");
        });
    }

    // -------------------------------------------------------------------------------------

    [Test]
    public void ScoreModel_MatchesBotRunWithStarPowerSuppressed()
    {
        var f = Load();
        var engine = new YargFiveFretGuitarEngine(f.Notes, f.Sync, f.Params, isBot: true);
        engine.AllowStarPower(false);
        BotRunner.RunToEnd(engine, f.Notes);

        TestContext.Out.WriteLine($"engine {engine.EngineStats.TotalScore} " +
            $"(committed {engine.EngineStats.CommittedScore}, solo {engine.EngineStats.SoloBonuses}, " +
            $"coda {engine.EngineStats.CodaBonuses}) vs model {f.Model.ProjectPerfectScore()}");

        Assert.Multiple(() =>
        {
            Assert.That(engine.EngineStats.NotesHit, Is.EqualTo(engine.EngineStats.TotalNotes));
            Assert.That(f.Model.ComboSteps, Is.EqualTo(engine.EngineStats.MaxCombo));
            Assert.That(f.Model.CommittedScore, Is.EqualTo(engine.EngineStats.CommittedScore));
            Assert.That(f.Model.SoloBonusTotal, Is.EqualTo(engine.EngineStats.SoloBonuses));
            Assert.That(f.Model.ProjectPerfectScore(), Is.EqualTo(engine.EngineStats.TotalScore));
            Assert.That(f.Model.ProjectPerfectScore(), Is.EqualTo(SyntheticNoSpScore));
        });
    }

    /// <summary>
    /// The BRE divergence, both ways. <see cref="ScoreModel"/> skips BRE notes unconditionally;
    /// the engine skips them only once <c>CodaHasStarted</c> (<c>Guitar/GuitarEngine.cs:247</c>).
    /// With a coda the two agree exactly; without one the engine scores the BRE notes and counts
    /// them towards combo, and the model is deliberately lower.
    /// </summary>
    /// <remarks>
    /// Keeping the model's skip unconditional is a decision, not an oversight: a BRE with no coda
    /// is malformed charting, and modelling <c>CodaHasStarted</c> would mean simulating the coda
    /// phrase. This test is what stops that decision from being invisible.
    /// </remarks>
    [Test]
    public void BigRockEnding_IsSkippedByBothSidesOnlyWhenACodaStartsIt()
    {
        var withCoda = Load(includeCoda: true);
        var withoutCoda = Load(includeCoda: false);

        int breNotes = withCoda.Notes.Notes.Count(n => n.IsBigRockEnding);

        int engineWith = RunNoSp(withCoda);
        int engineWithout = RunNoSp(withoutCoda);

        TestContext.Out.WriteLine($"{breNotes} BRE notes. " +
            $"With coda: engine {engineWith}, model {withCoda.Model.ProjectPerfectScore()}. " +
            $"Without: engine {engineWithout}, model {withoutCoda.Model.ProjectPerfectScore()}.");

        Assert.Multiple(() =>
        {
            Assert.That(breNotes, Is.GreaterThan(0));

            Assert.That(withCoda.Model.ProjectPerfectScore(), Is.EqualTo(engineWith),
                "With a coda, both sides skip the BRE and the scores must agree exactly.");

            Assert.That(withoutCoda.Model.ProjectPerfectScore(), Is.EqualTo(engineWith),
                "The model's BRE skip is unconditional, so removing the coda must not move it.");
            Assert.That(engineWithout, Is.GreaterThan(engineWith),
                "Without a coda the engine scores the BRE notes — this is the known divergence.");
        });
    }

    private static int RunNoSp(Fixture f)
    {
        var engine = new YargFiveFretGuitarEngine(f.Notes, f.Sync, f.Params, isBot: true);
        engine.AllowStarPower(false);
        BotRunner.RunToEnd(engine, f.Notes);
        return engine.EngineStats.TotalScore;
    }

    // -------------------------------------------------------------------------------------

    /// <summary>
    /// Hand-picked activation lists on the synthetic chart, described by policy rather than by
    /// index so they survive the fixture being edited.
    /// </summary>
    [TestCase(2, TestName = "ActivateAtHalfBar")]
    [TestCase(3, TestName = "ActivateAtThreeQuarterBar")]
    [TestCase(4, TestName = "ActivateAtFullBar")]
    public void SpModel_MatchesTheEngine_AcrossTheMeterChange(int quarterBars)
    {
        var f = Load();
        var activations = ActivationsAtMeter(f, quarterBars);

        TestContext.Out.WriteLine($"activating with {quarterBars}/4 bar at scoring notes " +
            string.Join(",", activations));

        var windows = new List<SpWindow>();
        long extra = f.Sp.DoubledPointsForActivations(activations, windows);
        int projected = f.Model.ProjectPerfectScore() + (int) extra;

        var engine = new ScriptedBotGuitarEngine(f.Notes, f.Sync, f.Params,
            activations.Select(i => f.Model.ScoringNotes[i].NoteIndex));
        BotRunner.RunToEnd(engine, f.Notes);

        // The 3/4 stretch, in measure ticks. A window that never touches it would leave this
        // fixture testing nothing the real chart does not already cover, so it is asserted rather
        // than assumed.
        uint threeFourStart = f.Sync.QuarterTickToMeasureTick((uint) SyntheticChart.ThreeFourTick);
        uint threeFourEnd = f.Sync.QuarterTickToMeasureTick((uint) SyntheticChart.BackToFourFourTick);
        var straddling = windows
            .Where(w => w.ActivationMeasureTick < threeFourEnd && w.EndMeasureTick > threeFourStart)
            .ToList();

        foreach (var w in windows) TestContext.Out.WriteLine("  " + w);
        TestContext.Out.WriteLine($"3/4 spans measure ticks [{threeFourStart}, {threeFourEnd}); " +
            $"{straddling.Count} window(s) overlap it");
        TestContext.Out.WriteLine($"model {projected} vs engine {engine.EngineStats.TotalScore}");

        Assert.Multiple(() =>
        {
            Assert.That(activations, Is.Not.Empty);
            Assert.That(straddling, Is.Not.Empty,
                "No Star Power window overlaps the 3/4 stretch, so this case is not actually " +
                "testing the meter-aware drain — the whole point of the synthetic fixture.");
            Assert.That(engine.EngineStats.NotesHit, Is.EqualTo(engine.EngineStats.TotalNotes));
            Assert.That(engine.EngineStats.StarPowerActivationCount, Is.EqualTo(activations.Count));
            Assert.That(engine.Windows.Select(w => w.ActivationMeasureTick).ToArray(),
                Is.EqualTo(windows.Select(w => w.ActivationMeasureTick).ToArray()));
            Assert.That(engine.Windows.Select(w => w.EndMeasureTick).ToArray(),
                Is.EqualTo(windows.Select(w => w.EndMeasureTick).ToArray()));
            Assert.That(projected, Is.EqualTo(engine.EngineStats.TotalScore));
        });
    }

    /// <summary>
    /// §1.4's discontinuity, made into a test: a sustain is scored entirely by whether its
    /// <em>burst</em> tick is inside the Star Power window, not by how much of it overlapped. So a
    /// sustain whose note sits on one side of a window edge and whose burst sits on the other is
    /// the single place a model that thinks in "overlap" instead of "burst tick" first diverges.
    /// </summary>
    /// <remarks>
    /// The existence of such a sustain is asserted, not assumed: if a fixture edit removes it, the
    /// boundary stops being covered and this test says so instead of quietly passing.
    /// </remarks>
    [Test]
    public void ASustainStraddlingAWindowEdge_IsScoredByItsBurstTick()
    {
        var f = Load();

        // Burst events, paired with the measure tick of the note they belong to.
        var bursts = f.Model.Events
            .Where(e => e.Kind == ScoreEventKind.SustainBurst)
            .Select(e => (e.NoteIndex, NoteMeasureTick: f.Model.Events
                .First(n => n.NoteIndex == e.NoteIndex && n.Kind == ScoreEventKind.Note)
                .MeasureTick, BurstMeasureTick: e.MeasureTick))
            .ToList();

        (int QuarterBars, SpWindow Window, int NoteIndex, string Which)? found = null;

        for (int quarterBars = SpScoreModel.MinQuarterBarsToActivate;
             quarterBars <= SpScoreModel.MaxQuarterBars && found is null;
             quarterBars++)
        {
            var candidates = ActivationsAtMeter(f, quarterBars);
            foreach (int index in candidates)
            {
                var window = f.Sp.SimulateWindow(index, quarterBars);
                foreach (var b in bursts)
                {
                    bool noteIn = b.NoteMeasureTick >= window.ActivationMeasureTick &&
                                  b.NoteMeasureTick < window.EndMeasureTick;
                    bool burstIn = b.BurstMeasureTick >= window.ActivationMeasureTick &&
                                   b.BurstMeasureTick < window.EndMeasureTick;

                    if (noteIn == burstIn) continue;

                    found = (quarterBars, window, b.NoteIndex,
                        burstIn ? "note outside, burst inside" : "note inside, burst outside");
                    break;
                }

                if (found is not null) break;
            }
        }

        Assert.That(found, Is.Not.Null,
            "No sustain straddles a Star Power window edge on this fixture, so the burst-tick " +
            "rule at the window boundary (design doc §1.4, risk 4) is no longer covered.");

        var (bars, straddled, noteIndex, which) = found.Value;
        TestContext.Out.WriteLine($"note {noteIndex}: {which}, against {straddled}");

        // And the whole run containing that window must still reproduce on the engine, so the
        // straddling sustain is scored the model's way and not merely observed.
        var activations = ActivationsAtMeter(f, bars);
        long extra = f.Sp.DoubledPointsForActivations(activations);
        int projected = f.Model.ProjectPerfectScore() + (int) extra;

        var engine = new ScriptedBotGuitarEngine(f.Notes, f.Sync, f.Params,
            activations.Select(i => f.Model.ScoringNotes[i].NoteIndex));
        BotRunner.RunToEnd(engine, f.Notes);

        Assert.Multiple(() =>
        {
            Assert.That(engine.EngineStats.NotesHit, Is.EqualTo(engine.EngineStats.TotalNotes));
            Assert.That(projected, Is.EqualTo(engine.EngineStats.TotalScore));
        });
    }

    /// <summary>
    /// <c>NoStarPowerOverlap == true</c>, which no preset in the fork sets, so it cannot be driven
    /// through a real engine here — this is a pure-model test of the two consequences the flag has
    /// (<c>Guitar/GuitarEngine.cs:259-261</c>): a phrase hit while Star Power is active is
    /// stripped, so the window <b>never extends</b>, and the phrase is gone rather than banked.
    /// </summary>
    [Test]
    public void NoStarPowerOverlap_WindowsNeverExtend_AndSwallowedPhrasesDoNotBank()
    {
        var f = Load();
        var noOverlap = new SpScoreModel(f.Model, noStarPowerOverlap: true);
        var notes = f.Model.ScoringNotes;

        int extendedUnderOverlap = 0;
        SpWindow? swallowing = null;

        for (int i = 0; i < notes.Count; i++)
        {
            for (int q = SpScoreModel.MinQuarterBarsToActivate;
                 q <= SpScoreModel.MaxQuarterBars;
                 q++)
            {
                uint unextended = notes[i].MeasureTick + (uint) q * noOverlap.TicksPerQuarterSpBar;
                var strict = noOverlap.SimulateWindow(i, q);

                Assert.That(strict.EndMeasureTick, Is.EqualTo(unextended),
                    $"Window at note {i} with {q}/4 bar extended, which NoStarPowerOverlap forbids.");

                if (f.Sp.SimulateWindow(i, q).EndMeasureTick > unextended)
                {
                    extendedUnderOverlap++;

                    // A window that swallows two phrase ends: enough meter for a whole further
                    // activation, if it banked. It must not.
                    int swallowed = 0;
                    for (int k = i; k < strict.NextNoteIndex; k++)
                    {
                        if (notes[k].IsPhraseEnd) swallowed++;
                    }

                    if (swallowed >= SpScoreModel.MinQuarterBarsToActivate && swallowing is null)
                    {
                        swallowing = strict;
                    }
                }
            }
        }

        TestContext.Out.WriteLine($"{extendedUnderOverlap} (note, meter) window(s) would have " +
            $"extended with overlap allowed; none did with it forbidden");

        Assert.That(extendedUnderOverlap, Is.GreaterThan(0),
            "No window on this fixture would have extended anyway, so the no-extension assertion " +
            "above is vacuous.");
        Assert.That(swallowing, Is.Not.Null,
            "No window swallows two phrases, so the no-banking case is not covered.");

        // Activating again on the first note after that window must be rejected for lack of
        // meter: the phrases inside it were stripped, not banked.
        // NextNoteIndex is the first note at or after the window end, so no phrase can have been
        // collected between the two — any meter there could only have come from inside the window.
        var window = swallowing.Value;
        int next = window.NextNoteIndex;

        TestContext.Out.WriteLine($"window {window} swallowed phrases; retrying at note {next}");

        Assert.That(
            () => noOverlap.DoubledPointsForActivations(new[] { window.ScoringNoteIndex, next }),
            Throws.ArgumentException.With.Message.Contains("the meter is only 0"),
            "Phrases inside a window banked meter; NoStarPowerOverlap strips them instead.");
    }

    [Test]
    public void Optimizer_BeatsGreedy_AndItsProjectionIsReproducibleOnTheEngine()
    {
        var f = Load();
        var path = SpPathOptimizer.Optimize(f.Sp);

        TestContext.Out.WriteLine(path.ToString());
        foreach (var a in path.Activations) TestContext.Out.WriteLine("  " + a);

        var engine = new ScriptedBotGuitarEngine(f.Notes, f.Sync, f.Params,
            path.Activations.Select(a => a.NoteIndex));
        BotRunner.RunToEnd(engine, f.Notes);

        var greedy = new YargFiveFretGuitarEngine(f.Notes, f.Sync, f.Params, isBot: true);
        BotRunner.RunToEnd(greedy, f.Notes);

        TestContext.Out.WriteLine($"optimizer {path.ProjectedScore}, engine " +
            $"{engine.EngineStats.TotalScore}, greedy {greedy.EngineStats.TotalScore}");

        Assert.Multiple(() =>
        {
            Assert.That(engine.EngineStats.NotesHit, Is.EqualTo(engine.EngineStats.TotalNotes));
            Assert.That(engine.EngineStats.TotalScore, Is.EqualTo(path.ProjectedScore));
            Assert.That(greedy.EngineStats.TotalScore, Is.EqualTo(SyntheticGreedyBotScore));
            Assert.That(path.ProjectedScore, Is.GreaterThanOrEqualTo(greedy.EngineStats.TotalScore));
            Assert.That(path.ProjectedScore, Is.EqualTo(SyntheticOptimalScore));
        });
    }

    /// <summary>
    /// The exhaustive check the design doc asks for, on a chart small enough to enumerate and
    /// awkward enough (meter change, odd sustains) to catch a window-arithmetic mistake.
    /// </summary>
    [Test]
    public void Optimizer_AgreesWithBruteForce()
    {
        var f = Load();

        var dp = SpPathOptimizer.Optimize(f.Sp);
        var brute = BruteForce.Best(f.Sp);

        TestContext.Out.WriteLine($"dp = {dp.ScoreGainOverNoActivations} at " +
            $"[{string.Join(",", dp.Activations.Select(a => a.ScoringNoteIndex))}], " +
            $"brute = {brute.Extra} at [{string.Join(",", brute.Activations)}]");

        Assert.That(dp.ScoreGainOverNoActivations, Is.EqualTo((int) brute.Extra));
    }

    // -------------------------------------------------------------------------------------

    /// <summary>
    /// The brute-force cross-check on <see cref="SyntheticChart.Dense"/>, whose optimum needs
    /// <b>four</b> activations — so the exhaustive search has to chain four windows correctly, not
    /// just place one or two. The engine then has to reproduce the projection for that four-window
    /// path, which is the part a window-chaining mistake would break.
    /// </summary>
    [Test]
    public void Optimizer_AgreesWithBruteForce_OnADenseChartNeedingFourActivations()
    {
        var chart = SyntheticChart.Dense.Load();
        var notes = SyntheticChart.Dense.GuitarNotes(chart);
        var p = ChartFixtures.GuitarParams();
        var model = ScoreModel.Build(notes, chart.SyncTrack, p.MaxMultiplier);
        var sp = SpScoreModel.FromParameters(model, p);

        int phrases = model.ScoringNotes.Count(n => n.IsPhraseEnd);

        var dp = SpPathOptimizer.Optimize(sp);
        var brute = BruteForce.Best(sp);

        var engine = new ScriptedBotGuitarEngine(notes, chart.SyncTrack, p,
            dp.Activations.Select(a => a.NoteIndex));
        BotRunner.RunToEnd(engine, notes);

        TestContext.Out.WriteLine($"{model.ScoringNotes.Count} notes, {phrases} phrases, " +
            $"brute-force bound {phrases / SpScoreModel.MinQuarterBarsToActivate} activations");
        TestContext.Out.WriteLine($"dp = {dp.ScoreGainOverNoActivations} at " +
            $"[{string.Join(",", dp.Activations.Select(a => a.ScoringNoteIndex))}], " +
            $"brute = {brute.Extra} at [{string.Join(",", brute.Activations)}]");
        foreach (var a in dp.Activations) TestContext.Out.WriteLine("  " + a);

        Assert.Multiple(() =>
        {
            Assert.That(phrases, Is.EqualTo(2 * SyntheticChart.Dense.Blocks),
                "The dense fixture must supply exactly two phrases per block.");
            Assert.That(dp.Activations, Has.Count.GreaterThanOrEqualTo(4),
                "The dense fixture exists so the optimum chains at least four windows; if this " +
                "drops, the brute-force check has stopped covering multi-window chaining.");
            Assert.That(brute.Activations, Has.Count.EqualTo(dp.Activations.Count),
                "Brute force and the DP must agree on how many windows the optimum takes.");
            Assert.That(dp.ScoreGainOverNoActivations, Is.EqualTo((int) brute.Extra));

            Assert.That(engine.EngineStats.NotesHit, Is.EqualTo(engine.EngineStats.TotalNotes));
            Assert.That(engine.EngineStats.StarPowerActivationCount,
                Is.EqualTo(dp.Activations.Count));
            Assert.That(engine.EngineStats.TotalScore, Is.EqualTo(dp.ProjectedScore));
        });
    }

    // -------------------------------------------------------------------------------------

    /// <summary>
    /// Scoring-note indices for a policy of "wait until the meter is exactly
    /// <paramref name="quarterBars"/>, then activate on the next note".
    /// </summary>
    private static List<int> ActivationsAtMeter(Fixture f, int quarterBars)
    {
        var result = new List<int>();
        var notes = f.Model.ScoringNotes;

        int meter = 0;
        for (int i = 0; i < notes.Count;)
        {
            if (meter >= quarterBars)
            {
                result.Add(i);
                i = f.Sp.SimulateWindow(i, meter).NextNoteIndex;
                meter = 0;
                continue;
            }

            meter = f.Sp.MeterAfter(i, meter);
            i++;
        }

        return result;
    }
}
