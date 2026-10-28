using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using YARG.Core.Chart;
using YARG.Core.Engine.Guitar;
using YARG.Gameplay.SpPath;

namespace YARG.SpPathTests;

/// <summary>
/// The full-whammy model (<c>docs/sp-path-design.md</c>, "Whammy") against the real engine, with
/// a bot that whammies on every pass. Everything is compared exactly: the cumulative gain is
/// <c>round(16·S/15)</c> on any update grid, so there is no tolerance to hide behind.
/// </summary>
[TestFixture]
public class SpWhammyTests
{
    public sealed record Fixture(
        string Name, InstrumentDifficulty<GuitarNote> Notes, SyncTrack Sync,
        GuitarEngineParameters Params, ScoreModel Model, SpScoreModel Whammy, SpScoreModel Plain)
    {
        public override string ToString() => Name;
    }

    /// <summary>A fresh chart per call: the engine marks hit state on the note objects.</summary>
    public static Fixture Load(string name)
    {
        InstrumentDifficulty<GuitarNote> notes;
        SyncTrack sync;
        bool isBass = name == "drawntotheflame bass";

        if (name == "synthetic")
        {
            var chart = SyntheticChart.Load(true);
            notes = SyntheticChart.GuitarNotes(chart);
            sync = chart.SyncTrack;
        }
        else if (name == "whammy")
        {
            var chart = SyntheticChart.Whammy.Load();
            notes = SyntheticChart.Whammy.GuitarNotes(chart);
            sync = chart.SyncTrack;
        }
        else
        {
            var chart = ChartFixtures.LoadChart();
            notes = ChartFixtures.GuitarNotes(chart, isBass);
            sync = chart.SyncTrack;
        }

        var p = ChartFixtures.GuitarParams(isBass);
        var model = ScoreModel.Build(notes, sync, p.MaxMultiplier);
        return new Fixture(name, notes, sync, p, model,
            SpScoreModel.FromParameters(model, p, assumeWhammy: true, unisonPhrases: null),
            SpScoreModel.FromParameters(model, p, assumeWhammy: false, unisonPhrases: null));
    }

    public static readonly string[] Fixtures =
        { "drawntotheflame guitar", "drawntotheflame bass", "synthetic", "whammy" };

    private static readonly double[] Steps = { 1.0 / 30.0, 1.0 / 120.0, 1.0 / 240.0 };

    private static ScriptedBotGuitarEngine Run(Fixture f, IEnumerable<int> activationNotes,
        double step = BotRunner.StepSeconds)
    {
        var engine = new ScriptedBotGuitarEngine(f.Notes, f.Sync, f.Params, activationNotes,
            fullWhammy: true);
        BotRunner.RunToEnd(engine, f.Notes, step);
        return engine;
    }

    // -------------------------------------------------------------------------------------

    /// <summary>
    /// With Star Power never activated, the engine's meter on every note's pass equals the
    /// model's meter on arriving at that note, and the engine's raw whammy total equals the
    /// model's cumulative gain. This pins the gain rule — which sustains, over which ticks, how
    /// rounded — independently of any window.
    /// </summary>
    [TestCaseSource(nameof(Fixtures))]
    public void Meter_MatchesEngine_NoteByNote(string name)
    {
        var f = Load(name);
        var engine = Run(f, null);
        var sp = f.Whammy;
        var notes = f.Model.ScoringNotes;

        var firstSampleAt = new Dictionary<uint, uint>();
        foreach (var sample in engine.MeterTrace)
        {
            firstSampleAt.TryAdd(sample.Tick, sample.Amount);
        }

        uint meter = sp.InitialMeterTicks;
        int compared = 0;
        for (int i = 0; i < notes.Count; i++)
        {
            Assert.That(firstSampleAt.TryGetValue(notes[i].Tick, out uint engineMeter), Is.True,
                $"No engine pass at scoring note {i}'s tick {notes[i].Tick}.");
            Assert.That(engineMeter, Is.EqualTo(meter),
                $"Meter on arriving at scoring note {i} (tick {notes[i].Tick}).");
            compared++;
            meter = sp.MeterTicksAfter(i, meter);
        }

        TestContext.Out.WriteLine($"{f.Name}: {compared} note(s) compared; whammy total " +
            $"{engine.EngineStats.StarPowerWhammyTicks} engine, {sp.WhammyTotal(uint.MaxValue)} model; " +
            $"{f.Model.StarPowerSustains.Count} Star Power sustain(s)");

        Assert.That(f.Model.StarPowerSustains.Count, Is.GreaterThan(0),
            "The fixture has no Star Power sustains, so nothing here tests whammy.");
        Assert.That(engine.EngineStats.StarPowerWhammyTicks,
            Is.EqualTo(sp.WhammyTotal(uint.MaxValue)));
    }

    /// <summary>
    /// The whammy path's projection is what a full-whammy bot following it actually scores, on
    /// three update grids.
    /// </summary>
    [Test]
    public void WhammyPath_ReproducesOnEngine(
        [ValueSource(nameof(Fixtures))] string name, [ValueSource(nameof(Steps))] double step)
    {
        var f = Load(name);
        var path = SpPathOptimizer.Optimize(f.Whammy);
        var engine = Run(f, path.Activations.Select(a => a.NoteIndex), step);

        TestContext.Out.WriteLine($"{f.Name} @ 1/{1 / step:0} s: {path}; engine " +
            $"{engine.EngineStats.TotalScore} in {engine.Windows.Count} window(s)");
        foreach (var a in path.Activations)
        {
            TestContext.Out.WriteLine($"  plan:   {a} ({a.MeterTicksAtActivation} tick(s))");
        }

        foreach (var w in engine.Windows)
        {
            TestContext.Out.WriteLine($"  engine: note {w.NoteIndex} [{w.ActivationMeasureTick}, " +
                $"{w.EndMeasureTick}) with {w.MeterAtActivation}");
        }

        Assert.Multiple(() =>
        {
            Assert.That(engine.EngineStats.NotesHit, Is.EqualTo(engine.EngineStats.TotalNotes));
            Assert.That(engine.Windows.Count, Is.EqualTo(path.Activations.Count));
            Assert.That(engine.EngineStats.TotalScore, Is.EqualTo(path.ProjectedScore));

            for (int i = 0; i < path.Activations.Count && i < engine.Windows.Count; i++)
            {
                Assert.That(engine.Windows[i].MeterAtActivation,
                    Is.EqualTo(path.Activations[i].MeterTicksAtActivation),
                    $"Meter spent by activation {i}.");
            }
        });
    }

    /// <summary>
    /// The full-whammy optimum on each fixture, pinned after <see cref="WhammyPath_ReproducesOnEngine"/>
    /// confirmed each one on the engine at three update grids. A change to the model that moves
    /// one of these has to explain itself.
    /// </summary>
    [TestCase("drawntotheflame guitar", 415_078)]
    [TestCase("drawntotheflame bass", 518_771)]
    [TestCase("synthetic", 58_479)]
    [TestCase("whammy", 33_718)]
    public void WhammyOptimum_IsPinned(string name, int expected)
    {
        var path = SpPathOptimizer.Optimize(Load(name).Whammy);
        Assert.That(path.ProjectedScore, Is.EqualTo(expected));
    }

    /// <summary>More meter can only help: the whammy optimum never scores below the plain one.</summary>
    [TestCaseSource(nameof(Fixtures))]
    public void Whammy_NeverScoresBelowNoWhammy(string name)
    {
        var f = Load(name);
        var whammy = SpPathOptimizer.Optimize(f.Whammy);
        var plain = SpPathOptimizer.Optimize(f.Plain);

        TestContext.Out.WriteLine($"{f.Name}: whammy {whammy}; plain {plain}");
        Assert.That(whammy.ProjectedScore, Is.GreaterThanOrEqualTo(plain.ProjectedScore));
        Assert.That(plain.StateCount, Is.LessThanOrEqualTo(
            (f.Model.ScoringNotes.Count + 1) * (SpScoreModel.MaxQuarterBars + 1)));
    }

    /// <summary>
    /// A window that releases part way through a Star Power sustain banks the whammy after the
    /// release. Activating again as soon as that carried meter (plus whatever follows) reaches
    /// half a bar has to reproduce on the engine — if the carry were wrong, the second window
    /// would be the wrong length or illegal altogether.
    /// </summary>
    [Test]
    public void CarriedMeter_AfterAMidSustainRelease_ReproducesOnEngine()
    {
        int found = 0;

        foreach (string name in Fixtures)
        {
            var f = Load(name);
            var sp = f.Whammy;
            var notes = f.Model.ScoringNotes;

            uint meter = sp.InitialMeterTicks;
            for (int i = 0; i < notes.Count; i++)
            {
                if (meter >= sp.TicksPerHalfSpBar)
                {
                    var window = sp.SimulateWindowTicks(i, meter);
                    if (window.CarriedMeterTicks > 0)
                    {
                        int second = FirstLegalActivation(sp, window.NextNoteIndex,
                            window.CarriedMeterTicks);
                        var set = second < 0 ? new[] { i } : new[] { i, second };
                        int projected = sp.ScoreForActivations(set);

                        var fresh = Load(name);
                        var engine = Run(fresh, set.Select(k => notes[k].NoteIndex));

                        TestContext.Out.WriteLine($"{name}: {window}; second activation at " +
                            $"scoring note {second}; model {projected}, engine " +
                            $"{engine.EngineStats.TotalScore} in {engine.Windows.Count} window(s)");

                        Assert.Multiple(() =>
                        {
                            Assert.That(engine.Windows.Count, Is.EqualTo(set.Length));
                            Assert.That(engine.EngineStats.TotalScore, Is.EqualTo(projected));
                        });

                        found++;
                        break;
                    }
                }

                meter = sp.MeterTicksAfter(i, meter);
            }
        }

        Assert.That(found, Is.GreaterThan(0),
            "No fixture releases a window in the middle of a Star Power sustain, so the carry " +
            "is untested.");
    }

    /// <summary>
    /// The three activations the whammy fixture was built around — one releasing inside a Star
    /// Power sustain, one funded by the meter that release carried, one on a note played over a
    /// held Star Power sustain — reproduce on the engine, window by window.
    /// </summary>
    [Test]
    public void WhammyFixture_HandPickedActivations_ReproduceOnEngine()
    {
        var f = Load("whammy");
        var notes = f.Notes.Notes;
        var sp = f.Whammy;

        int NoteAt(long tick) => notes.FindIndex(n => n.Tick == (uint) tick);
        int ScoringAt(long tick)
        {
            var scoring = f.Model.ScoringNotes;
            for (int i = 0; i < scoring.Count; i++)
            {
                if (scoring[i].Tick == (uint) tick) return i;
            }

            return -1;
        }

        var ticks = new[]
        {
            SyntheticChart.Whammy.FirstActivation,
            SyntheticChart.Whammy.SecondActivation,
            SyntheticChart.Whammy.HeldActivation,
        };

        var scoringSet = ticks.Select(ScoringAt).ToArray();
        Assert.That(scoringSet, Has.None.EqualTo(-1), "A hand-picked tick has no note.");

        var windows = new List<SpWindow>();
        long extra = sp.DoubledPointsForActivations(scoringSet, windows);
        int projected = f.Model.ProjectPerfectScore() + (int) extra;

        var engine = Run(f, ticks.Select(NoteAt));

        foreach (var w in windows)
        {
            TestContext.Out.WriteLine($"model:  {w} (release tick {w.ReleaseTick})");
        }

        foreach (var w in engine.Windows)
        {
            TestContext.Out.WriteLine($"engine: note {w.NoteIndex} [{w.ActivationMeasureTick}, " +
                $"{w.EndMeasureTick}) with {w.MeterAtActivation}");
        }

        TestContext.Out.WriteLine($"model {projected}, engine {engine.EngineStats.TotalScore}");

        Assert.Multiple(() =>
        {
            Assert.That(windows[0].CarriedMeterTicks, Is.GreaterThan(0),
                "The first window should release inside the Star Power sustain and carry whammy.");
            Assert.That(windows[1].MeterTicks % sp.TicksPerQuarterSpBar, Is.Not.EqualTo(0),
                "The second activation should spend a meter that is not whole quarter bars.");
            Assert.That(windows[2].MeterTicks, Is.GreaterThan(sp.TicksPerQuarterSpBar * 3),
                "The third activation should spend whammy earned on the held sustain.");

            Assert.That(engine.EngineStats.NotesHit, Is.EqualTo(engine.EngineStats.TotalNotes));
            Assert.That(engine.Windows.Count, Is.EqualTo(windows.Count));
            for (int i = 0; i < windows.Count && i < engine.Windows.Count; i++)
            {
                Assert.That(engine.Windows[i].MeterAtActivation, Is.EqualTo(windows[i].MeterTicks),
                    $"Meter spent by window {i}.");
                Assert.That(engine.Windows[i].EndMeasureTick, Is.EqualTo(windows[i].EndMeasureTick),
                    $"End of window {i}.");
            }

            Assert.That(engine.EngineStats.TotalScore, Is.EqualTo(projected));
        });
    }

    /// <summary>
    /// The DP against the exhaustive search over activation sets, with whammy. Same prefix as
    /// the plain brute-force test, so the enumeration stays tractable.
    /// </summary>
    [Test]
    public void Optimizer_AgreesWithBruteForce_WithWhammy()
    {
        var f = Load("drawntotheflame guitar");
        var truncated = new InstrumentDifficulty<GuitarNote>(
            f.Notes.Instrument, f.Notes.Difficulty, f.Notes.Notes.Take(300).ToList(),
            f.Notes.Phrases, f.Notes.TextEvents);

        var prefix = new SpScoreModel(ScoreModel.Build(truncated, f.Sync, f.Params.MaxMultiplier),
            f.Params.NoStarPowerOverlap, assumeWhammy: true, unisonPhrases: null);

        var dp = SpPathOptimizer.Optimize(prefix);
        var brute = BruteForce.Best(prefix);

        TestContext.Out.WriteLine($"dp = {dp.ScoreGainOverNoActivations} at " +
            $"[{string.Join(",", dp.Activations.Select(a => a.ScoringNoteIndex))}], " +
            $"brute = {brute.Extra} at [{string.Join(",", brute.Activations)}]");

        Assert.That(dp.ScoreGainOverNoActivations, Is.EqualTo((int) brute.Extra));
    }

    /// <summary>
    /// The same, on the whammy fixture: all 3/4, so windows release inside Star Power sustains and
    /// the DP's states include carried meter, which the 4/4 prefix above never produces.
    /// </summary>
    [Test]
    public void Optimizer_AgreesWithBruteForce_OnCarryStates()
    {
        var sp = Load("whammy").Whammy;

        var dp = SpPathOptimizer.Optimize(sp);
        var brute = BruteForce.Best(sp);

        var windows = new List<SpWindow>();
        sp.DoubledPointsForActivations(brute.Activations, windows);

        TestContext.Out.WriteLine($"dp = {dp.ScoreGainOverNoActivations} at " +
            $"[{string.Join(",", dp.Activations.Select(a => a.ScoringNoteIndex))}], " +
            $"brute = {brute.Extra} at [{string.Join(",", brute.Activations)}]; " +
            $"brute's windows carry [{string.Join(",", windows.Select(w => w.CarriedMeterTicks))}]");

        Assert.That(dp.ScoreGainOverNoActivations, Is.EqualTo((int) brute.Extra));
    }

    /// <summary>
    /// The first scoring note at or after <paramref name="from"/> where the meter, starting from
    /// <paramref name="meter"/>, reaches half a bar; -1 if it never does.
    /// </summary>
    private static int FirstLegalActivation(SpScoreModel sp, int from, uint meter)
    {
        for (int i = from; i < sp.NoteCount; i++)
        {
            if (meter >= sp.TicksPerHalfSpBar)
            {
                return i;
            }

            meter = sp.MeterTicksAfter(i, meter);
        }

        return -1;
    }
}
