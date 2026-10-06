using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using YARG.Core.Chart;
using YARG.Gameplay.SpPath;

namespace YARG.SpPathTests;

/// <summary>
/// Slice 3, part A: settle what a Star Power activation actually <em>does</em>, empirically,
/// before any model is trusted. Every claim in <c>docs/sp-path-design.md</c> §1.5 that was marked
/// "unverified" is pinned here against a real engine run on <c>drawntotheflame.mid</c>.
/// <para/>
/// The scripted engine's contract — "activate at note N" means N is the first doubled note — is
/// itself one of the things under test; see <see cref="ScriptedBotGuitarEngine"/> for how it is
/// arranged and why it needs <c>UpdateStarPower</c> rather than <c>UpdateBot</c>.
/// </summary>
[TestFixture]
public class SpSemanticsTests
{
    /// <summary>
    /// Note indices carrying <c>IsStarPowerEnd</c> on Expert guitar. Recorded so the cases below
    /// read as "the note after the second phrase" rather than as bare magic numbers.
    /// </summary>
    public static readonly int[] GuitarPhraseEndNotes =
    {
        26, 86, 154, 237, 264, 333, 364, 419, 442, 457, 546, 596, 644, 716, 805, 905, 1007, 1051,
        1106, 1131
    };

    private static (InstrumentDifficulty<GuitarNote> Notes, SyncTrack Sync,
        YARG.Core.Engine.Guitar.GuitarEngineParameters Params) Fixture(bool isBass = false)
    {
        var chart = ChartFixtures.LoadChart();
        return (ChartFixtures.GuitarNotes(chart, isBass), chart.SyncTrack,
            ChartFixtures.GuitarParams(isBass));
    }

    private static ScriptedBotGuitarEngine Run(IEnumerable<int> activations, bool isBass = false)
    {
        var (notes, sync, p) = Fixture(isBass);
        var engine = new ScriptedBotGuitarEngine(notes, sync, p, activations);
        BotRunner.RunToEnd(engine, notes);
        return engine;
    }

    [Test]
    public void PhraseEndNoteIndices_AreStable()
    {
        var (notes, _, _) = Fixture();
        var actual = Enumerable.Range(0, notes.Notes.Count)
            .Where(i => notes.Notes[i].IsStarPowerEnd)
            .ToArray();

        Assert.That(actual, Is.EqualTo(GuitarPhraseEndNotes));
    }

    /// <summary>
    /// The meter is exactly one quarter bar per phrase, capped at a full bar — no whammy, no
    /// partial credit (<c>AwardStarPower</c>, <c>BaseEngine.Generic.cs:1158-1163</c>).
    /// </summary>
    [Test]
    public void MeterAtActivation_IsAWholeNumberOfQuarterBars()
    {
        // Two phrases collected -> exactly half a bar, the minimum that can activate.
        var engine = Run(new[] { GuitarPhraseEndNotes[1] + 1 });

        Assert.That(engine.Windows, Has.Count.EqualTo(1));
        Assert.That(engine.Windows[0].MeterAtActivation,
            Is.EqualTo(2 * engine.TicksPerQuarterSpBar));
        Assert.That(engine.TicksPerHalfSpBar, Is.EqualTo(2 * engine.TicksPerQuarterSpBar));
        Assert.That(engine.TicksPerFullSpBar, Is.EqualTo(4 * engine.TicksPerQuarterSpBar));
    }

    /// <summary>
    /// The core semantic: the scripted note is the <b>first</b> doubled note, the window is the
    /// half-open measure-tick interval <c>[m, m + meter)</c>, and the first award at or after
    /// <c>E</c> is <b>not</b> doubled.
    /// </summary>
    /// <remarks>
    /// The four cases are chosen to straddle the two boundaries the design doc calls out as the
    /// most likely places for the model to be wrong (risk 4): half / three-quarter / full meter,
    /// and one window that swallows a phrase end (note 264, at measure tick 90240).
    /// </remarks>
    [TestCase(87, 2, 34560u, 42240u, 110)]
    [TestCase(155, 3, 53760u, 65280u, 193)]
    [TestCase(238, 4, 76800u, 96000u, 267)]
    [TestCase(265, 4, 92160u, 107520u, 298)]
    public void ActivateAtNoteN_DoublesFromNInclusiveToEExclusive(
        int n, int expectedQuarterBars, uint expectedStart, uint expectedEnd, int expectedLastNote)
    {
        var (notes, sync, _) = Fixture();
        var engine = Run(new[] { n });

        var window = engine.Windows.Single();
        var doubled = engine.Awards.Where(a => a.StarPowerActive).ToList();

        TestContext.Out.WriteLine($"window: {window}");
        TestContext.Out.WriteLine($"doubled awards: {doubled.Count}, " +
            $"measure ticks {doubled[0].MeasureTick}..{doubled[^1].MeasureTick}");

        Assert.Multiple(() =>
        {
            Assert.That(engine.EngineStats.StarPowerActivationCount, Is.EqualTo(1));

            // Activation lands on the note's own tick, not a frame boundary near it.
            Assert.That(window.ActivationMeasureTick, Is.EqualTo(expectedStart));
            Assert.That(window.ActivationMeasureTick,
                Is.EqualTo(sync.QuarterTickToMeasureTick(notes.Notes[n].Tick)),
                "Activation must land exactly on the note's measure tick.");
            Assert.That(window.MeterAtActivation,
                Is.EqualTo((uint) expectedQuarterBars * engine.TicksPerQuarterSpBar));

            Assert.That(window.EndMeasureTick, Is.EqualTo(expectedEnd));

            // N is the first doubled note, not N+1.
            Assert.That(doubled[0].NoteIndex, Is.EqualTo(n));
            Assert.That(doubled[0].MeasureTick, Is.EqualTo(expectedStart));

            // Every doubled award is inside [m, E), and every award inside [m, E) is doubled.
            Assert.That(doubled.All(a => a.MeasureTick >= window.ActivationMeasureTick &&
                                         a.MeasureTick < window.EndMeasureTick),
                "A doubled award fell outside [m, E).");
            Assert.That(engine.Awards
                    .Where(a => a.MeasureTick >= window.ActivationMeasureTick &&
                                a.MeasureTick < window.EndMeasureTick)
                    .All(a => a.StarPowerActive),
                "An award inside [m, E) was not doubled.");

            // The boundary itself is exclusive.
            var atOrAfterEnd = engine.Awards.First(a => a.MeasureTick >= window.EndMeasureTick);
            Assert.That(atOrAfterEnd.StarPowerActive, Is.False,
                "An award at measure tick E was doubled; the window is supposed to be half-open.");

            Assert.That(doubled[^1].NoteIndex, Is.EqualTo(expectedLastNote));
        });
    }

    /// <summary>
    /// A phrase completed while Star Power is active extends the window by exactly one quarter
    /// bar, clamped to a full bar measured from that phrase note
    /// (<c>BaseEngine.cs:543-547</c> plus the amount clamp at <c>:532-535</c>).
    /// </summary>
    [Test]
    public void PhraseCompletedWhileActive_ExtendsTheWindowByAQuarterBar()
    {
        var (notes, sync, _) = Fixture();

        // Activating at note 238 with a full bar runs the window over phrase end 264.
        var engine = Run(new[] { 238 });
        var window = engine.Windows.Single();

        uint start = sync.QuarterTickToMeasureTick(notes.Notes[238].Tick);
        uint phrase = sync.QuarterTickToMeasureTick(notes.Notes[264].Tick);
        uint unextended = start + engine.TicksPerFullSpBar;

        Assert.Multiple(() =>
        {
            Assert.That(phrase, Is.GreaterThan(start).And.LessThan(unextended),
                "Fixture assumption: phrase end 264 must fall inside the un-extended window.");
            Assert.That(window.EndMeasureTick,
                Is.EqualTo(unextended + engine.TicksPerQuarterSpBar));
            Assert.That(window.EndMeasureTick,
                Is.LessThanOrEqualTo(phrase + engine.TicksPerFullSpBar),
                "The extension must never push the end past a full bar from the phrase note.");
        });

        // And the model reproduces it without running an engine.
        var guitarParams = ChartFixtures.GuitarParams();
        var sp = SpScoreModel.FromParameters(
            ScoreModel.Build(notes, sync, guitarParams.MaxMultiplier), guitarParams);
        int scoringIndex = ScoringIndexOf(sp, 238);
        var modelled = sp.SimulateWindow(scoringIndex, 4);

        Assert.That(modelled.EndMeasureTick, Is.EqualTo(window.EndMeasureTick));
        Assert.That(modelled.ActivationMeasureTick, Is.EqualTo(window.ActivationMeasureTick));
    }

    /// <summary>
    /// The input only needs to be raised on the activation pass; holding it across the whole
    /// window changes nothing, because <c>ActivateStarPower</c> returns immediately while Star
    /// Power is already active (<c>BaseEngine.cs:483-486</c>).
    /// </summary>
    [Test]
    public void HoldingTheInput_ScoresTheSameAsPulsingIt()
    {
        var pulsed = Run(new[] { 87 });
        var held = Run(Enumerable.Range(87, 12));

        TestContext.Out.WriteLine($"pulsed = {pulsed.EngineStats.TotalScore}, " +
            $"held = {held.EngineStats.TotalScore}");

        Assert.Multiple(() =>
        {
            Assert.That(held.EngineStats.StarPowerActivationCount, Is.EqualTo(1),
                "A held input must not re-activate inside its own window.");
            Assert.That(held.EngineStats.TotalScore, Is.EqualTo(pulsed.EngineStats.TotalScore));
            Assert.That(held.Windows.Single().EndMeasureTick,
                Is.EqualTo(pulsed.Windows.Single().EndMeasureTick));
        });
    }

    /// <summary>
    /// An activation request below half a bar is simply ignored — the model's
    /// <c>MinQuarterBarsToActivate</c> is a real gate, not a convention.
    /// </summary>
    [Test]
    public void RequestingActivationBelowHalfABar_DoesNothing()
    {
        // Only one phrase has been collected by note 27.
        var engine = Run(new[] { GuitarPhraseEndNotes[0] + 1 });

        Assert.Multiple(() =>
        {
            Assert.That(engine.EverActivatedStarPower, Is.False);
            Assert.That(engine.EngineStats.StarPowerActivationCount, Is.Zero);
            Assert.That(engine.EngineStats.TotalScore,
                Is.EqualTo(ScoreModelTests.DrawnToTheFlameGuitarNoSpScore));
        });
    }

    internal static int ScoringIndexOf(SpScoreModel sp, int noteIndex)
    {
        var notes = sp.Model.ScoringNotes;
        for (int i = 0; i < notes.Count; i++)
        {
            if (notes[i].NoteIndex == noteIndex)
            {
                return i;
            }
        }

        throw new AssertionException($"Note {noteIndex} is not a scoring note.");
    }
}
