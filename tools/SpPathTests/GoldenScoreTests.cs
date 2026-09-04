using NUnit.Framework;
using YARG.Core;
using YARG.Core.Engine.Guitar.Engines;

namespace YARG.SpPathTests;

/// <summary>
/// Slice 1 of docs/sp-path-design.md: prove the whole verification story — a net8.0 NUnit
/// project can load a real chart, drive a headless bot engine and get a stable score — before
/// any scoring model is written.
/// </summary>
[TestFixture]
public class GoldenScoreTests
{
    /// <summary>
    /// Golden: stock bot policy (greedy — activates the instant the bar reaches half,
    /// <c>YargFiveFretGuitarEngine.cs:30</c>) on Expert 5-fret guitar, default preset
    /// (MaxMultiplier 4), stepped at 1/120 s.
    /// </summary>
    public const int DrawnToTheFlameGuitarGreedyBotScore = 376_558;

    [Test]
    public void StockBot_OnDrawnToTheFlame_ScoresTheGoldenTotal()
    {
        var chart = ChartFixtures.LoadChart();
        var notes = ChartFixtures.GuitarNotes(chart);
        var engine = new YargFiveFretGuitarEngine(notes, chart.SyncTrack, ChartFixtures.GuitarParams(),
            isBot: true);

        BotRunner.RunToEnd(engine, notes);

        TestContext.Out.WriteLine($"TotalScore        = {engine.EngineStats.TotalScore}");
        TestContext.Out.WriteLine($"CommittedScore    = {engine.EngineStats.CommittedScore}");
        TestContext.Out.WriteLine($"SoloBonuses       = {engine.EngineStats.SoloBonuses}");
        TestContext.Out.WriteLine($"NotesHit/Total    = {engine.EngineStats.NotesHit}/{engine.EngineStats.TotalNotes}");
        TestContext.Out.WriteLine($"SP phrases hit    = {engine.EngineStats.StarPowerPhrasesHit}");
        TestContext.Out.WriteLine($"SP activations    = {engine.EngineStats.StarPowerActivationCount}");

        Assert.Multiple(() =>
        {
            Assert.That(engine.EngineStats.NotesHit, Is.EqualTo(engine.EngineStats.TotalNotes),
                "The bot is expected to full-combo the chart.");
            Assert.That(engine.EngineStats.StarPowerActivationCount, Is.GreaterThan(0),
                "The stock bot policy should have activated Star Power.");
            Assert.That(engine.EngineStats.TotalScore, Is.EqualTo(DrawnToTheFlameGuitarGreedyBotScore));
        });
    }
}
