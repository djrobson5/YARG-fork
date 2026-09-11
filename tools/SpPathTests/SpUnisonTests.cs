using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using YARG.Core.Chart;
using YARG.Core.Engine;
using YARG.Core.Engine.Guitar;
using YARG.Core.Game;
using YARG.Gameplay.SpPath;

namespace YARG.SpPathTests;

/// <summary>
/// The unison bonus (<c>docs/sp-path-design.md</c> §1.5, "Unison bonuses", 2026-09-04).
/// <para/>
/// <c>BaseEngine.AwardUnisonBonus</c> (<c>BaseEngine.cs:637-641</c>) hands out a second
/// <c>TicksPerQuarterSpBar</c> on top of the phrase itself whenever every participant clears a
/// unison phrase, driven from <c>EngineManager.OnStarPowerPhraseHit</c>
/// (<c>EngineManager.UnisonEvent.cs:336-360</c>). Modelling it is what stops the plan from
/// banking half of what the player's engine really banks and putting every marker late.
/// <para/>
/// Three things are pinned here:
/// <list type="number">
/// <item>the engine really does pay it in a <b>single-player</b> run — the participant set is the
/// registered engines, so one player clearing the phrase is all of them;</item>
/// <item>with unisons the meter fills faster, the first activation moves <b>earlier</b> and the
/// plan scores more;</item>
/// <item>the unison-aware plan is still <b>exactly</b> reproducible on a live engine — same
/// meter, same window bounds, same <c>TotalScore</c> — which is the property the Unity-side
/// divergence check depends on.</item>
/// </list>
/// </summary>
[TestFixture]
public class SpUnisonTests
{
    /// <summary>
    /// The optimizer on <c>drawntotheflame</c> Expert guitar with its <b>11 unison phrases</b>
    /// modelled, verified below against a live engine registered with an <c>EngineManager</c>.
    /// <para/>
    /// It does not replace <c>SpPathOptimizerTests.DrawnToTheFlameGuitarOptimalScore</c>
    /// (392,750): that golden is the optimum for an engine run with <b>no</b>
    /// <c>EngineManager</c>, which is what every other test in this harness drives and what the
    /// pre-unison model described. Both are exact — they are optima for two different engines.
    /// </summary>
    public const int DrawnToTheFlameGuitarUnisonOptimalScore = 427_954;

    /// <summary>Unison phrases <c>EngineManager.GetUnisonPhrases</c> finds on that chart's guitar.</summary>
    public const int DrawnToTheFlameGuitarUnisonCount = 11;

    // -------------------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------------------

    /// <summary>
    /// The unison ranges the game would hand the model, taken from the same call the engine's own
    /// <c>EngineContainer</c> is built with (<c>EngineManager.cs:71</c>).
    /// </summary>
    private static List<SpUnisonPhrase> UnisonsFor(InstrumentDifficulty<GuitarNote> notes,
        SongChart chart) =>
        EngineManager.GetUnisonPhrases(notes, chart, includeChildNotesInNoteCount: false)
            .Select(phrase => new SpUnisonPhrase(phrase.Tick, phrase.TickEnd))
            .ToList();

    private static (uint[] Ticks, uint[] MeasureTicks, int[] Meters, int[] Notes) Summarise(
        StarPowerPath path) =>
        (path.Activations.Select(a => a.ActivationTick).ToArray(),
            path.Activations.Select(a => a.EndMeasureTick).ToArray(),
            path.Activations.Select(a => a.MeterAtActivation).ToArray(),
            path.Activations.Select(a => a.NoteIndex).ToArray());

    // -------------------------------------------------------------------------------------
    // 1 — the engine's behaviour, from the harness rather than from reading the source
    // -------------------------------------------------------------------------------------

    /// <summary>
    /// A <b>single</b> registered engine on a chart with unisons banks two quarter bars per unison
    /// phrase. This is the fact the perfect-play assumption rests on: the bonus fires when
    /// <c>SuccessCount == ParticipantToPhrase.Count</c> (<c>UnisonEvent.Success</c>), and with one
    /// engine registered that count is one.
    /// </summary>
    [Test]
    public void ASinglePlayerRunReallyIsAwardedTheUnisonBonus()
    {
        var chart = SyntheticChart.Dense.Load(withUnisons: true);
        var notes = SyntheticChart.Dense.GuitarNotes(chart);

        var manager = new EngineManager();
        var engine = new ScriptedBotGuitarEngine(notes, chart.SyncTrack, ChartFixtures.GuitarParams());
        var container = manager.Register(engine, notes, chart, RockMeterPreset.Normal);

        Assert.That(container.UnisonPhrases, Has.Count.EqualTo(SyntheticChart.Dense.Blocks),
            "the fixture's bass track should make the first phrase of every block a unison");

        BotRunner.RunToEnd(engine, notes);

        int phrases = engine.EngineStats.StarPowerPhrasesHit;
        int unisons = container.UnisonPhrases.Count;

        Assert.That(engine.EngineStats.TotalStarPowerTicks,
            Is.EqualTo((uint) (phrases + unisons) * engine.TicksPerQuarterSpBar),
            "a perfect run banks one quarter bar per phrase plus one more per unison");
    }

    /// <summary>
    /// The fixture's unisons are the ones the engine finds, not ones the test asserted into
    /// existence — and the tick ranges are the ones the model is handed.
    /// </summary>
    [Test]
    public void DenseWithUnisons_IsWhatTheEngineManagerCallsAUnison()
    {
        var chart = SyntheticChart.Dense.Load(withUnisons: true);
        var notes = SyntheticChart.Dense.GuitarNotes(chart);

        var found = UnisonsFor(notes, chart);
        var expected = SyntheticChart.Dense.UnisonPhraseTicks
            .Select(range => new SpUnisonPhrase((uint) range.Tick, (uint) range.TickEnd))
            .ToArray();

        Assert.That(found.Select(u => (u.Tick, u.TickEnd)),
            Is.EqualTo(expected.Select(u => (u.Tick, u.TickEnd))));

        // And without the bass track there are none at all, which is what makes the pair of
        // fixtures a controlled comparison.
        var plain = SyntheticChart.Dense.Load();
        Assert.That(UnisonsFor(SyntheticChart.Dense.GuitarNotes(plain), plain), Is.Empty);
    }

    /// <summary>
    /// <c>drawntotheflame</c> — the golden fixture — <b>does</b> have unisons: its bass track's
    /// Star Power phrases coincide with eleven of the guitar's twenty. Recorded here because it
    /// is the reason this fixture needs two goldens rather than one.
    /// </summary>
    [Test]
    public void DrawnToTheFlame_HasUnisonPhrases()
    {
        var chart = ChartFixtures.LoadChart();
        var notes = ChartFixtures.GuitarNotes(chart);

        Assert.That(UnisonsFor(notes, chart), Has.Count.EqualTo(DrawnToTheFlameGuitarUnisonCount));
    }

    // -------------------------------------------------------------------------------------
    // 2 — the model's arithmetic
    // -------------------------------------------------------------------------------------

    /// <summary>
    /// A unison phrase end banks two quarter bars, an ordinary one banks one, and both clamp at a
    /// full bar exactly as <c>GainStarPower</c> does (<c>BaseEngine.cs:532-535</c>).
    /// </summary>
    [Test]
    public void UnisonPhraseEnd_BanksTwoQuarterBars()
    {
        var chart = SyntheticChart.Dense.Load(withUnisons: true);
        var notes = SyntheticChart.Dense.GuitarNotes(chart);
        var model = ScoreModel.Build(notes, chart.SyncTrack, ChartFixtures.GuitarParams().MaxMultiplier);

        var sp = new SpScoreModel(model, noStarPowerOverlap: false, UnisonsFor(notes, chart));
        var plain = new SpScoreModel(model, noStarPowerOverlap: false);

        int unison = model.ScoringNotes
            .Select((note, index) => (note, index))
            .First(x => x.note.Tick == SyntheticChart.Dense.PhraseNoteTick(0, 0)).index;
        int ordinary = model.ScoringNotes
            .Select((note, index) => (note, index))
            .First(x => x.note.Tick == SyntheticChart.Dense.PhraseNoteTick(0, 1)).index;

        Assert.Multiple(() =>
        {
            Assert.That(sp.QuarterBarsGainedAt(unison), Is.EqualTo(2));
            Assert.That(sp.QuarterBarsGainedAt(ordinary), Is.EqualTo(1));
            Assert.That(plain.QuarterBarsGainedAt(unison), Is.EqualTo(1),
                "with no unison list the same note is an ordinary phrase end");

            Assert.That(sp.MeterAfter(unison, 0), Is.EqualTo(2));
            Assert.That(sp.MeterAfter(unison, 1), Is.EqualTo(3));
            Assert.That(sp.MeterAfter(unison, 3), Is.EqualTo(SpScoreModel.MaxQuarterBars),
                "the second quarter is dropped by the full-bar clamp, not banked past it");
            Assert.That(sp.MeterAfter(unison, 4), Is.EqualTo(SpScoreModel.MaxQuarterBars));
        });

        Assert.That(sp.UnisonPhraseEndTicks,
            Is.EqualTo(SyntheticChart.Dense.UnisonPhraseTicks.Select(r => (uint) r.Tick)));
    }

    /// <summary>
    /// A unison phrase collected <em>inside</em> a window pushes the end out by <b>two</b> quarter
    /// bars, not one, because <c>GainStarPower</c> — and with it <c>UpdateStarPowerEnds</c> — runs
    /// twice (<c>BaseEngine.cs:543-547</c>). Checked against a live engine that really is awarding
    /// the bonus, so this is the engine's window, not the model's own opinion of it.
    /// <para/>
    /// The activation is the first note of block 0's dense cluster, reached with three quarter
    /// bars banked (block 0's unison pays two, its second phrase one). Its window swallows block
    /// 1's unison phrase and then, because that pushed the end out, block 1's ordinary phrase too
    /// — a cascade, which is exactly the shape a hand-checked constant would get wrong.
    /// </summary>
    [Test]
    public void UnisonPhraseInsideAWindow_ExtendsItTwice()
    {
        var chart = SyntheticChart.Dense.Load(withUnisons: true);
        var notes = SyntheticChart.Dense.GuitarNotes(chart);
        var p = ChartFixtures.GuitarParams();
        var model = ScoreModel.Build(notes, chart.SyncTrack, p.MaxMultiplier);

        var sp = new SpScoreModel(model, noStarPowerOverlap: false, UnisonsFor(notes, chart));
        var plain = new SpScoreModel(model, noStarPowerOverlap: false);

        // First note of block 0's cluster: the first note after both of block 0's phrases.
        uint activationTick = (uint) (SyntheticChart.Dense.SparseStart(0)
            + SyntheticChart.Dense.BlockMeasures * SyntheticChart.Dense.Measure);
        int scoringNote = model.ScoringNotes
            .Select((note, index) => (note, index))
            .First(x => x.note.Tick == activationTick).index;

        Assert.That(FirstNoteWithMeter(sp, 3), Is.LessThanOrEqualTo(scoringNote),
            "three quarter bars should be banked by the time the cluster starts");

        const int meter = 3;
        var window = sp.SimulateWindow(scoringNote, meter);
        uint plainEnd = plain.WindowEndAt(window.ActivationMeasureTick,
            meter * plain.TicksPerQuarterSpBar);

        Assert.That(window.EndMeasureTick - plainEnd, Is.EqualTo(sp.TicksPerQuarterSpBar),
            "the same window, walked without the unison, is one quarter bar shorter");

        var manager = new EngineManager();
        var engine = new ScriptedBotGuitarEngine(notes, chart.SyncTrack, p,
            new[] { window.NoteIndex });
        manager.Register(engine, notes, chart, RockMeterPreset.Normal);
        BotRunner.RunToEnd(engine, notes);

        Assert.That(engine.Windows, Has.Count.EqualTo(1));
        Assert.Multiple(() =>
        {
            Assert.That(engine.Windows[0].MeterAtActivation,
                Is.EqualTo((uint) meter * sp.TicksPerQuarterSpBar));
            Assert.That(engine.Windows[0].ActivationMeasureTick,
                Is.EqualTo(window.ActivationMeasureTick));
            Assert.That(engine.Windows[0].EndMeasureTick, Is.EqualTo(window.EndMeasureTick));
        });
    }

    // -------------------------------------------------------------------------------------
    // 3 — no unisons changes nothing
    // -------------------------------------------------------------------------------------

    /// <summary>
    /// The guard on the goldens: an empty (or absent) unison list has to leave the model bit-for-bit
    /// where it was, on both fixtures. <c>SpPathOptimizerTests</c>' and <c>GoldenScoreTests</c>'
    /// numbers are all taken with no <c>EngineManager</c> in play, so they must not move.
    /// </summary>
    [Test]
    public void NoUnisonPhrases_LeavesThePathExactlyWhereItWas()
    {
        var chart = ChartFixtures.LoadChart();
        var notes = ChartFixtures.GuitarNotes(chart);
        var p = ChartFixtures.GuitarParams();
        var model = ScoreModel.Build(notes, chart.SyncTrack, p.MaxMultiplier);

        var implicitly_ = SpPathOptimizer.Optimize(new SpScoreModel(model, p.NoStarPowerOverlap));
        var empty = SpPathOptimizer.Optimize(
            new SpScoreModel(model, p.NoStarPowerOverlap, new List<SpUnisonPhrase>()));
        var viaTrack = SpPathOptimizer.Optimize(notes, chart.SyncTrack, p, unisonPhrases: null);

        Assert.Multiple(() =>
        {
            Assert.That(implicitly_.ProjectedScore,
                Is.EqualTo(SpPathOptimizerTests.DrawnToTheFlameGuitarOptimalScore));
            Assert.That(empty.ProjectedScore,
                Is.EqualTo(SpPathOptimizerTests.DrawnToTheFlameGuitarOptimalScore));
            Assert.That(viaTrack.ProjectedScore,
                Is.EqualTo(SpPathOptimizerTests.DrawnToTheFlameGuitarOptimalScore));

            Assert.That(Summarise(empty), Is.EqualTo(Summarise(implicitly_)));
            Assert.That(Summarise(viaTrack), Is.EqualTo(Summarise(implicitly_)));

            Assert.That(empty.UnisonPhraseEndTicks, Is.Empty);
            Assert.That(viaTrack.UnisonPhraseEndTicks, Is.Empty);
        });
    }

    /// <summary>
    /// A unison list that matches none of the chart's phrase ends is the same as no list — the
    /// model matches on the phrase-end <em>note</em>'s tick, not on the range merely existing.
    /// </summary>
    [Test]
    public void UnisonRangesThatMatchNoPhraseEnd_ChangeNothing()
    {
        var chart = SyntheticChart.Dense.Load();
        var notes = SyntheticChart.Dense.GuitarNotes(chart);
        var p = ChartFixtures.GuitarParams();
        var model = ScoreModel.Build(notes, chart.SyncTrack, p.MaxMultiplier);

        var baseline = SpPathOptimizer.Optimize(new SpScoreModel(model, false));
        var bogus = SpPathOptimizer.Optimize(new SpScoreModel(model, false,
            new List<SpUnisonPhrase> { new(1, 2), new(uint.MaxValue - 1, uint.MaxValue) }));

        Assert.That(bogus.UnisonPhraseEndTicks, Is.Empty);
        Assert.That(Summarise(bogus), Is.EqualTo(Summarise(baseline)));
        Assert.That(bogus.ProjectedScore, Is.EqualTo(baseline.ProjectedScore));
    }

    // -------------------------------------------------------------------------------------
    // 4 — the bug this closes: the meter fills earlier, so the activation moves earlier
    // -------------------------------------------------------------------------------------

    /// <summary>
    /// The symptom from the 2026-09-04 report, in miniature: with unisons modelled the meter
    /// reaches the half-bar activation minimum at an earlier note, the first marker moves earlier,
    /// and the plan is worth more.
    /// </summary>
    [Test]
    public void UnisonPhrases_FillTheMeterSoonerAndMoveTheFirstActivationEarlier()
    {
        var chart = SyntheticChart.Dense.Load(withUnisons: true);
        var notes = SyntheticChart.Dense.GuitarNotes(chart);
        var p = ChartFixtures.GuitarParams();
        var model = ScoreModel.Build(notes, chart.SyncTrack, p.MaxMultiplier);

        var sp = new SpScoreModel(model, false, UnisonsFor(notes, chart));
        var plain = new SpScoreModel(model, false);

        Assert.That(FirstNoteWithMeter(sp, SpScoreModel.MinQuarterBarsToActivate),
            Is.LessThan(FirstNoteWithMeter(plain, SpScoreModel.MinQuarterBarsToActivate)),
            "half a bar — the activation minimum — should be reached at an earlier note");
        Assert.That(FirstNoteWithMeter(sp, SpScoreModel.MaxQuarterBars),
            Is.LessThan(FirstNoteWithMeter(plain, SpScoreModel.MaxQuarterBars)),
            "and so should a full bar");

        var withPath = SpPathOptimizer.Optimize(sp);
        var withoutPath = SpPathOptimizer.Optimize(plain);

        Assert.Multiple(() =>
        {
            Assert.That(withPath.UnisonPhraseEndTicks,
                Has.Count.EqualTo(SyntheticChart.Dense.Blocks));
            Assert.That(withPath.Activations[0].ActivationTick,
                Is.LessThan(withoutPath.Activations[0].ActivationTick),
                "the first marker should move earlier once the plan knows the meter fills faster");
            Assert.That(withPath.ProjectedScore, Is.GreaterThan(withoutPath.ProjectedScore));
        });
    }

    /// <summary>First scoring note after which a perfect run has banked <paramref name="quarterBars"/>.</summary>
    private static int FirstNoteWithMeter(SpScoreModel sp, int quarterBars)
    {
        int meter = 0;
        for (int i = 0; i < sp.NoteCount; i++)
        {
            meter = sp.MeterAfter(i, meter);
            if (meter >= quarterBars)
            {
                return i;
            }
        }

        return int.MaxValue;
    }

    // -------------------------------------------------------------------------------------
    // 5 — the unison-aware plan against a live engine
    // -------------------------------------------------------------------------------------

    /// <summary>
    /// The extension of <c>SpPathDivergenceTests.MeterAtActivation_IsTheAmountTheEngineHasBankedAtThatActivation</c>
    /// to a run where unisons <em>are</em> awarded. The engine is registered with an
    /// <c>EngineManager</c>, which is the only thing that makes
    /// <c>EngineManager.OnStarPowerPhraseHit</c> fire, so this is the first test in the harness
    /// where <c>AwardUnisonBonus</c> runs at all.
    /// <para/>
    /// Everything has to line up, not just the meter: if the model's window walk got the
    /// double extension wrong, the window bounds and the total would drift even where the meter
    /// at activation happened to agree.
    /// </summary>
    [TestCase(false, TestName = "UnisonAwarePlan_MatchesALiveEngine(dense synthetic)")]
    [TestCase(true, TestName = "UnisonAwarePlan_MatchesALiveEngine(drawntotheflame)")]
    public void UnisonAwarePlan_MatchesALiveEngineThatAwardsUnisons(bool drawnToTheFlame)
    {
        var chart = drawnToTheFlame
            ? ChartFixtures.LoadChart()
            : SyntheticChart.Dense.Load(withUnisons: true);
        var notes = drawnToTheFlame
            ? ChartFixtures.GuitarNotes(chart)
            : SyntheticChart.Dense.GuitarNotes(chart);
        var p = ChartFixtures.GuitarParams();

        var unisons = UnisonsFor(notes, chart);
        Assert.That(unisons, Is.Not.Empty, "the fixture is supposed to have unisons");

        var model = ScoreModel.Build(notes, chart.SyncTrack, p.MaxMultiplier);
        var path = SpPathOptimizer.Optimize(SpScoreModel.FromParameters(model, p, unisons));
        Assert.That(path.Activations, Is.Not.Empty);

        var manager = new EngineManager();
        var engine = new ScriptedBotGuitarEngine(notes, chart.SyncTrack, p,
            path.Activations.Select(activation => activation.NoteIndex));
        manager.Register(engine, notes, chart, RockMeterPreset.Normal);
        BotRunner.RunToEnd(engine, notes);

        Assert.That(engine.Windows, Has.Count.EqualTo(path.Activations.Count));

        for (int i = 0; i < path.Activations.Count; i++)
        {
            var activation = path.Activations[i];
            var window = engine.Windows[i];

            Assert.Multiple(() =>
            {
                Assert.That(window.MeterAtActivation,
                    Is.EqualTo((uint) activation.MeterAtActivation * path.TicksPerQuarterSpBar),
                    $"activation {i + 1} spends a different meter than the engine had banked");
                Assert.That(window.ActivationMeasureTick,
                    Is.EqualTo(activation.ActivationMeasureTick));
                Assert.That(window.EndMeasureTick, Is.EqualTo(activation.EndMeasureTick),
                    $"window {i + 1} ends somewhere else than the model walked it to");
            });
        }

        Assert.That(engine.EngineStats.NotesHit, Is.EqualTo(engine.EngineStats.TotalNotes));
        Assert.That(engine.EngineStats.TotalScore, Is.EqualTo(path.ProjectedScore),
            "the unison-aware projection must be exactly what the engine scores");

        if (drawnToTheFlame)
        {
            Assert.That(path.UnisonPhraseEndTicks,
                Has.Count.EqualTo(DrawnToTheFlameGuitarUnisonCount));
            Assert.That(path.ProjectedScore,
                Is.EqualTo(DrawnToTheFlameGuitarUnisonOptimalScore));

            // And it beats the pre-unison plan on the engine that actually pays the bonuses,
            // which is the whole point of modelling them.
            Assert.That(path.ProjectedScore,
                Is.GreaterThan(SpPathOptimizerTests.DrawnToTheFlameGuitarOptimalScore));
        }
    }
}
