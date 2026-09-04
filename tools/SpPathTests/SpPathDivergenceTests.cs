using System.Linq;
using NUnit.Framework;
using YARG.Gameplay.SpPath;

namespace YARG.SpPathTests;

/// <summary>
/// The model-side facts the Unity divergence check leans on
/// (<c>TrackPlayer.CheckStarPowerPathMeter</c>, <c>docs/sp-path-design.md</c> §4.4).
/// <para/>
/// The check dims the overlay when the engine's banked <c>StarPowerTickAmount</c> at a planned
/// activation is below <c>Activation.MeterAtActivation × StarPowerPath.TicksPerQuarterSpBar</c>.
/// That comparison is only meaningful if the path really does carry the engine's quarter-bar
/// constant, and if the meter the plan claims to spend is the number the engine actually has
/// banked on a run that follows the plan. Both are pinned here, because the Unity side cannot be
/// tested at all and a silent mismatch would show up as an overlay that dims for no reason —
/// which is exactly the bug this replaced.
/// </summary>
[TestFixture]
public class SpPathDivergenceTests
{
    [Test]
    public void StarPowerPath_CarriesTheEnginesQuarterBarConstant()
    {
        var fixture = SpPathOptimizerTests.Load();
        var path = SpPathOptimizer.Optimize(fixture.Sp);

        Assert.That(path.TicksPerQuarterSpBar, Is.EqualTo(fixture.Sp.TicksPerQuarterSpBar));

        // BaseEngine.cs:168 — a quarter bar is two measures.
        Assert.That(path.TicksPerQuarterSpBar,
            Is.EqualTo(fixture.Sync.MeasureResolution * 2));
    }

    /// <summary>
    /// The model's phrase set has to be the engine's phrase set. Both read
    /// <c>IsStarPowerEnd</c> off the same post-modifier note track — the model in
    /// <c>ScoreModel.Build</c>, the engine at <c>Guitar/GuitarEngine.cs:263-267</c> — so a
    /// difference means one of them is looking at a different track.
    /// </summary>
    [Test]
    public void PhraseEndTicks_MatchTheEnginesPhraseCountAndTheChart()
    {
        var fixture = SpPathOptimizerTests.Load();
        var path = SpPathOptimizer.Optimize(fixture.Sp);

        var engine = new ScriptedBotGuitarEngine(fixture.Notes, fixture.Sync, fixture.Params);

        Assert.That(path.PhraseEndTicks.Count,
            Is.EqualTo(engine.EngineStats.TotalStarPowerPhrases),
            "the model and the engine disagree about how many Star Power phrases the chart has");

        var chartPhraseEnds = fixture.Notes.Notes
            .Where(note => note.IsStarPowerEnd)
            .Select(note => note.Tick)
            .ToArray();

        Assert.That(path.PhraseEndTicks, Is.EqualTo(chartPhraseEnds));
        Assert.That(path.PhraseEndTicks, Is.Ordered.Ascending);
    }

    /// <summary>
    /// <c>MeterAtActivation × TicksPerQuarterSpBar</c> is exactly what a run following the plan
    /// has banked when it activates — the quantity the divergence check compares against
    /// <c>BaseStats.StarPowerTickAmount</c>.
    /// </summary>
    [Test]
    public void MeterAtActivation_IsTheAmountTheEngineHasBankedAtThatActivation()
    {
        var fixture = SpPathOptimizerTests.Load();
        var path = SpPathOptimizer.Optimize(fixture.Sp);
        Assert.That(path.Activations.Count, Is.GreaterThan(0));

        var engine = new ScriptedBotGuitarEngine(fixture.Notes, fixture.Sync, fixture.Params,
            path.Activations.Select(activation => activation.NoteIndex));
        BotRunner.RunToEnd(engine, fixture.Notes);

        Assert.That(engine.Windows.Count, Is.EqualTo(path.Activations.Count));

        for (int i = 0; i < path.Activations.Count; i++)
        {
            var activation = path.Activations[i];
            Assert.That(engine.Windows[i].MeterAtActivation,
                Is.EqualTo((uint) activation.MeterAtActivation * path.TicksPerQuarterSpBar),
                $"activation {i + 1} spends a different meter than the engine had banked");
        }
    }

    /// <summary>
    /// A perfect run never trips the meter rule: at every planned activation the engine has at
    /// least what the plan spends. This is the "no false positive on a clean run" case, stated in
    /// the terms the Unity check uses.
    /// </summary>
    [Test]
    public void APerfectRunNeverFallsShortOfThePlansMeter()
    {
        var fixture = SpPathOptimizerTests.Load();
        var path = SpPathOptimizer.Optimize(fixture.Sp);

        var engine = new ScriptedBotGuitarEngine(fixture.Notes, fixture.Sync, fixture.Params,
            path.Activations.Select(activation => activation.NoteIndex));
        BotRunner.RunToEnd(engine, fixture.Notes);

        for (int i = 0; i < path.Activations.Count; i++)
        {
            uint needed = (uint) path.Activations[i].MeterAtActivation * path.TicksPerQuarterSpBar;
            Assert.That(engine.Windows[i].MeterAtActivation, Is.GreaterThanOrEqualTo(needed));
        }

        // And the run really did collect every phrase, so nothing was stripped along the way.
        Assert.That(engine.EngineStats.StarPowerPhrasesHit,
            Is.EqualTo(engine.EngineStats.TotalStarPowerPhrases));
    }
}
