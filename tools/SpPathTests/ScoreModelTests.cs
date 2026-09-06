using NUnit.Framework;
using YARG.Core.Engine.Guitar.Engines;
using YARG.Gameplay.SpPath;

namespace YARG.SpPathTests;

/// <summary>
/// Slice 2 of docs/sp-path-design.md: the Unity-free <see cref="ScoreModel"/> must reproduce a
/// full-combo run's <c>TotalScore</c> exactly with Star Power suppressed.
/// <para/>
/// Star Power is switched off with <c>AllowStarPower(false)</c>
/// (<c>BaseEngine.Generic.cs:424-447</c>), which strips <c>NoteFlags.StarPower</c> from every
/// note. That leaves <c>CanStarPowerActivate</c> permanently false, so the stock bot's greedy
/// toggle never fires.
/// </summary>
[TestFixture]
public class ScoreModelTests
{
    /// <summary>
    /// Golden: full-combo, no-Star-Power, Expert 5-fret guitar on drawntotheflame.mid with the
    /// default preset (MaxMultiplier 4). Includes 28,000 of solo bonus.
    /// </summary>
    public const int DrawnToTheFlameGuitarNoSpScore = 317_774;

    /// <summary>
    /// Same run on Expert 5-fret bass (MaxMultiplier 6, and this chart's bass has no solo).
    /// Sustain-heavy, which is the point of running it: it exercises the burst-tick multiplier.
    /// </summary>
    public const int DrawnToTheFlameBassNoSpScore = 389_279;

    [TestCase(false, DrawnToTheFlameGuitarNoSpScore, TestName = "Guitar")]
    [TestCase(true, DrawnToTheFlameBassNoSpScore, TestName = "Bass")]
    public void ScoreModel_MatchesBotRunWithStarPowerDisabled(bool isBass, int goldenScore)
    {
        var chart = ChartFixtures.LoadChart();
        var notes = ChartFixtures.GuitarNotes(chart, isBass);
        var engineParams = ChartFixtures.GuitarParams(isBass);

        var engine = new YargFiveFretGuitarEngine(notes, chart.SyncTrack, engineParams, isBot: true);
        engine.AllowStarPower(false);

        BotRunner.RunToEnd(engine, notes);

        // Built after AllowStarPower(false) so the model sees exactly the note flags the engine
        // scored against. (SP flags do not affect this projection, but the ordering matters for
        // slice 3, where they will.)
        var model = ScoreModel.Build(notes, chart.SyncTrack, engineParams.MaxMultiplier);

        TestContext.Out.WriteLine($"engine TotalScore     = {engine.EngineStats.TotalScore}");
        TestContext.Out.WriteLine($"engine CommittedScore = {engine.EngineStats.CommittedScore}");
        TestContext.Out.WriteLine($"engine SoloBonuses    = {engine.EngineStats.SoloBonuses}");
        TestContext.Out.WriteLine($"model  ProjectedScore = {model.ProjectPerfectScore()}");
        TestContext.Out.WriteLine($"model  CommittedScore = {model.CommittedScore}");
        TestContext.Out.WriteLine($"model  SoloBonusTotal = {model.SoloBonusTotal}");
        TestContext.Out.WriteLine($"model  events         = {model.Events.Count}, combo steps = {model.ComboSteps}");

        Assert.Multiple(() =>
        {
            Assert.That(engine.EngineStats.NotesHit, Is.EqualTo(engine.EngineStats.TotalNotes),
                "The bot is expected to full-combo the chart.");
            Assert.That(engine.EngineStats.StarPowerActivationCount, Is.Zero,
                "AllowStarPower(false) should make activation impossible.");

            Assert.That(model.ComboSteps, Is.EqualTo(engine.EngineStats.MaxCombo),
                "Combo step count must match the engine's.");
            Assert.That(model.CommittedScore, Is.EqualTo(engine.EngineStats.CommittedScore),
                "CommittedScore mismatch — a note/sustain rounding or multiplier rule diverged.");
            Assert.That(model.SoloBonusTotal, Is.EqualTo(engine.EngineStats.SoloBonuses),
                "Solo bonus mismatch.");
            Assert.That(model.ProjectPerfectScore(), Is.EqualTo(engine.EngineStats.TotalScore));
            Assert.That(model.ProjectPerfectScore(), Is.EqualTo(goldenScore));
        });
    }

    /// <summary>
    /// The scripted bot (slice 1's other deliverable) with an empty activation set must score the
    /// same as the stock bot with Star Power stripped — it proves the override point replaces the
    /// greedy policy rather than merely adding to it, which is what slice 3 depends on.
    /// </summary>
    [Test]
    public void ScriptedBot_WithNoActivations_MatchesTheNoStarPowerModel()
    {
        var chart = ChartFixtures.LoadChart();
        var notes = ChartFixtures.GuitarNotes(chart);
        var engineParams = ChartFixtures.GuitarParams();

        var engine = new ScriptedBotGuitarEngine(notes, chart.SyncTrack, engineParams);
        BotRunner.RunToEnd(engine, notes);

        var model = ScoreModel.Build(notes, chart.SyncTrack, engineParams.MaxMultiplier);

        TestContext.Out.WriteLine($"scripted TotalScore = {engine.EngineStats.TotalScore}, " +
            $"SP phrases hit = {engine.EngineStats.StarPowerPhrasesHit}, " +
            $"activations = {engine.EngineStats.StarPowerActivationCount}");

        Assert.Multiple(() =>
        {
            Assert.That(engine.EngineStats.NotesHit, Is.EqualTo(engine.EngineStats.TotalNotes));
            Assert.That(engine.EverActivatedStarPower, Is.False);
            Assert.That(engine.EngineStats.StarPowerActivationCount, Is.Zero);
            // Star Power was collected (the phrases are still there), just never spent.
            Assert.That(engine.EngineStats.StarPowerPhrasesHit, Is.GreaterThan(0));
            Assert.That(engine.EngineStats.TotalScore, Is.EqualTo(model.ProjectPerfectScore()));
            Assert.That(engine.EngineStats.TotalScore, Is.EqualTo(DrawnToTheFlameGuitarNoSpScore));
        });
    }

    /// <summary>
    /// The model's own multiplier rule is off by one from <c>CalculateChartScores</c>
    /// (<c>Guitar/GuitarEngine.cs:399-444</c>), which reads the combo before the increment. This
    /// pins that difference so a future reader does not "fix" the model to match
    /// <c>BaseScore</c>.
    /// </summary>
    [Test]
    public void EngineBaseScore_IsNotTheSameAsAFullComboRun()
    {
        var chart = ChartFixtures.LoadChart();
        var notes = ChartFixtures.GuitarNotes(chart);
        var engine = new YargFiveFretGuitarEngine(notes, chart.SyncTrack, ChartFixtures.GuitarParams(),
            isBot: true);
        engine.AllowStarPower(false);
        BotRunner.RunToEnd(engine, notes);

        TestContext.Out.WriteLine($"BaseScore = {engine.BaseScore}, CommittedScore = {engine.EngineStats.CommittedScore}");

        Assert.That(engine.EngineStats.CommittedScore, Is.GreaterThan(engine.BaseScore));
    }
}
