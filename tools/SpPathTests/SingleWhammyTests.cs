using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using YARG.Core.Chart;
using YARG.Core.Engine;
using YARG.Core.Engine.Guitar;
using YARG.Core.Engine.Guitar.Engines;
using YARG.Core.Engine.Keys;
using YARG.Core.Game;
using YARG.Gameplay.SingleWhammy;
using YARG.Gameplay.SpPath;

namespace YARG.SpPathTests;

/// <summary>
/// The single-whammy option (<c>docs/sp-path-design.md</c>, "Single whammy"): one whammy per
/// Star Power sustain must earn exactly what whammying every sustain end to end earns, which is
/// what the full-whammy Star Power path plans for.
/// </summary>
[TestFixture]
public class SingleWhammyTests
{
    private static readonly double[] Steps = { 1.0 / 30.0, 1.0 / 120.0, 1.0 / 240.0 };

    /// <summary>
    /// How many passes into each run of Star Power sustains the bot whammies. 0 is a whammy on
    /// the run's first pass; the others land mid-sustain, so the gain has to be backdated.
    /// </summary>
    private static readonly int[] WhammyDelays = { 0, 1, 3 };

    /// <summary>
    /// A bot that whammies once per run of Star Power sustains, after <c>whammyDelay</c> passes,
    /// and activates Star Power only on the scripted notes.
    /// </summary>
    private sealed class OneWhammyBot : SingleWhammyFiveFretGuitarEngine
    {
        private readonly WhammyOncePerRun _whammy;
        private readonly HashSet<int> _activations;

        public OneWhammyBot(SpWhammyTests.Fixture f, int whammyDelay, IEnumerable<int> activations)
            : base(f.Notes, f.Sync, SingleWhammyParameters.WithSingleWhammy(f.Params), isBot: true)
        {
            _whammy = new WhammyOncePerRun(whammyDelay);
            _activations = new HashSet<int>(activations);
        }

        public int Whammies => _whammy.Count;
        public List<ScriptedBotGuitarEngine.MeterSample> MeterTrace { get; } = new();

        protected override void UpdateStarPower()
        {
            // As ScriptedBotGuitarEngine: overwrite base.UpdateBot's greedy toggle before
            // anything reads it.
            IsStarPowerInputActive = NoteIndex < Notes.Count &&
                _activations.Contains(NoteIndex) && CurrentTime >= Notes[NoteIndex].Time;

            if (_whammy.ShouldWhammy(IsStarPowerSustainActive()))
            {
                StartWhammyTimer(CurrentTime);
            }

            base.UpdateStarPower();
            MeterTrace.Add(new(CurrentTick, EngineStats.StarPowerTickAmount,
                EngineStats.IsStarPowerActive));
        }
    }

    /// <summary>The same whammy pattern on the stock engine, as a control.</summary>
    private sealed class OneWhammyStockBot : YargFiveFretGuitarEngine
    {
        private readonly WhammyOncePerRun _whammy;

        public OneWhammyStockBot(SpWhammyTests.Fixture f, int whammyDelay,
            GuitarEngineParameters parameters = null)
            : base(f.Notes, f.Sync, parameters ?? f.Params, isBot: true)
        {
            _whammy = new WhammyOncePerRun(whammyDelay);
        }

        protected override void UpdateStarPower()
        {
            IsStarPowerInputActive = false;
            if (_whammy.ShouldWhammy(IsStarPowerSustainActive()))
            {
                StartWhammyTimer(CurrentTime);
            }

            base.UpdateStarPower();
        }
    }

    /// <summary>The single-whammy five-lane keys engine, driven like <see cref="OneWhammyBot"/>.</summary>
    private sealed class OneWhammyKeysBot : SingleWhammyFiveLaneKeysEngine
    {
        private readonly WhammyOncePerRun _whammy;

        public OneWhammyKeysBot(SpWhammyTests.Fixture f, int whammyDelay)
            : base(f.Notes, f.Sync, SingleWhammyParameters.WithSingleWhammy(
                new EnginePreset.ProKeysPreset().Create(ChartFixtures.StarMultiplierThresholds,
                    ChartFixtures.SoloBonusStarMultiplierThresholds, isBass: false)), isBot: true)
        {
            _whammy = new WhammyOncePerRun(whammyDelay);
        }

        public int Whammies => _whammy.Count;

        protected override void UpdateStarPower()
        {
            IsStarPowerInputActive = false;
            if (_whammy.ShouldWhammy(IsStarPowerSustainActive()))
            {
                StartWhammyTimer(CurrentTime);
            }

            base.UpdateStarPower();
        }
    }

    private sealed class WhammyOncePerRun
    {
        private readonly int _delay;
        private int _passesInRun;
        private bool _whammied;

        public WhammyOncePerRun(int delay) => _delay = delay;

        public int Count { get; private set; }

        public bool ShouldWhammy(bool starPowerSustainActive)
        {
            if (!starPowerSustainActive)
            {
                _passesInRun = 0;
                _whammied = false;
                return false;
            }

            if (_whammied || _passesInRun++ < _delay)
            {
                return false;
            }

            _whammied = true;
            Count++;
            return true;
        }
    }

    private static OneWhammyBot Run(SpWhammyTests.Fixture f, int delay, double step,
        IEnumerable<int> activations = null)
    {
        var engine = new OneWhammyBot(f, delay, activations ?? Enumerable.Empty<int>());
        BotRunner.RunToEnd(engine, f.Notes, step);
        return engine;
    }

    // -------------------------------------------------------------------------------------

    /// <summary>
    /// With Star Power never activated, one whammy per sustain earns the full-whammy model's
    /// cumulative gain to the tick, however late in the sustain it lands.
    /// </summary>
    [Test]
    public void OneWhammyPerSustain_EarnsTheFullWhammyTotal(
        [ValueSource(typeof(SpWhammyTests), nameof(SpWhammyTests.Fixtures))] string name,
        [ValueSource(nameof(Steps))] double step,
        [ValueSource(nameof(WhammyDelays))] int delay)
    {
        var f = SpWhammyTests.Load(name);
        var engine = Run(f, delay, step);
        uint expected = f.Whammy.WhammyTotal(uint.MaxValue);

        TestContext.Out.WriteLine($"{name} @ 1/{1 / step:0} s, delay {delay}: " +
            $"{engine.Whammies} whammies, {engine.EngineStats.StarPowerWhammyTicks} engine, " +
            $"{expected} model");

        Assert.That(engine.Whammies, Is.GreaterThan(0));
        Assert.That(engine.EngineStats.StarPowerWhammyTicks, Is.EqualTo(expected));
    }

    /// <summary>
    /// The control: the same whammies on the stock engine earn less, so the test above is
    /// measuring the backdating and not a pattern that happens to whammy everything.
    /// </summary>
    [TestCase("whammy", 3)]
    [TestCase("drawntotheflame guitar", 3)]
    public void OneWhammyPerSustain_OnTheStockEngine_EarnsLess(string name, int delay)
    {
        var f = SpWhammyTests.Load(name);
        var engine = new OneWhammyStockBot(f, delay);
        BotRunner.RunToEnd(engine, f.Notes);

        Assert.That(engine.EngineStats.StarPowerWhammyTicks,
            Is.LessThan(f.Whammy.WhammyTotal(uint.MaxValue)));
    }

    /// <summary>
    /// The keys engines override sustain holding (<c>FiveLaneKeysEngine.CanSustainHold</c>), so
    /// the five-lane keys subclass is checked on its own, on the same guitar fixtures.
    /// </summary>
    [Test]
    public void FiveLaneKeys_OneWhammyPerSustain_EarnsTheFullWhammyTotal(
        [ValueSource(typeof(SpWhammyTests), nameof(SpWhammyTests.Fixtures))] string name,
        [ValueSource(nameof(WhammyDelays))] int delay)
    {
        var f = SpWhammyTests.Load(name);
        var engine = new OneWhammyKeysBot(f, delay);
        double end = BotRunner.ChartEndTime(f.Notes);
        for (double t = 0.0; t < end; t += BotRunner.StepSeconds)
        {
            engine.Update(t);
        }

        engine.Update(end);

        Assert.That(engine.Whammies, Is.GreaterThan(0));
        Assert.That(engine.EngineStats.StarPowerWhammyTicks,
            Is.EqualTo(f.Whammy.WhammyTotal(uint.MaxValue)));
    }

    /// <summary>
    /// YARG.Core's replay analyzer runs the <em>stock</em> engine on a single-whammy replay, so
    /// the stock engine has to get through the encoded whammy buffer, whammies included, without
    /// throwing. It reads the buffer as a whammy that never lapses, so it earns whammy, though
    /// never more than full whammy.
    /// </summary>
    [TestCaseSource(typeof(SpWhammyTests), nameof(SpWhammyTests.Fixtures))]
    public void StockEngine_SurvivesTheEncodedParameters(string name)
    {
        var f = SpWhammyTests.Load(name);
        var engine = new OneWhammyStockBot(f, 1, SingleWhammyParameters.WithSingleWhammy(f.Params));

        Assert.DoesNotThrow(() => BotRunner.RunToEnd(engine, f.Notes));
        TestContext.Out.WriteLine($"{name}: stock engine earned " +
            $"{engine.EngineStats.StarPowerWhammyTicks} whammy tick(s), model " +
            $"{f.Whammy.WhammyTotal(uint.MaxValue)}");
        Assert.That(engine.EngineStats.StarPowerWhammyTicks,
            Is.GreaterThan(0u).And.LessThanOrEqualTo(f.Whammy.WhammyTotal(uint.MaxValue)));
    }

    /// <summary>
    /// A whammy on the first pass of every sustain run reproduces the full-whammy model's meter
    /// on arriving at every note.
    /// </summary>
    [TestCaseSource(typeof(SpWhammyTests), nameof(SpWhammyTests.Fixtures))]
    public void Meter_MatchesFullWhammyModel_NoteByNote(string name)
    {
        var f = SpWhammyTests.Load(name);
        var engine = Run(f, 0, BotRunner.StepSeconds);
        var notes = f.Model.ScoringNotes;

        var firstSampleAt = new Dictionary<uint, uint>();
        foreach (var sample in engine.MeterTrace)
        {
            firstSampleAt.TryAdd(sample.Tick, sample.Amount);
        }

        uint meter = f.Whammy.InitialMeterTicks;
        for (int i = 0; i < notes.Count; i++)
        {
            Assert.That(firstSampleAt.TryGetValue(notes[i].Tick, out uint engineMeter), Is.True,
                $"No engine pass at scoring note {i}'s tick {notes[i].Tick}.");
            Assert.That(engineMeter, Is.EqualTo(meter),
                $"Meter on arriving at scoring note {i} (tick {notes[i].Tick}).");
            meter = f.Whammy.MeterTicksAfter(i, meter);
        }
    }

    /// <summary>
    /// The full-whammy path, played with one whammy per sustain on the first pass, scores what
    /// it projected — the guarantee behind the path assuming whammy while single whammy is on.
    /// </summary>
    [Test]
    public void WhammyPath_ReproducesWithOneWhammyPerSustain(
        [ValueSource(typeof(SpWhammyTests), nameof(SpWhammyTests.Fixtures))] string name,
        [ValueSource(nameof(Steps))] double step)
    {
        var f = SpWhammyTests.Load(name);
        var path = SpPathOptimizer.Optimize(f.Whammy);
        var engine = Run(f, 0, step, path.Activations.Select(a => a.NoteIndex));

        Assert.Multiple(() =>
        {
            Assert.That(engine.EngineStats.NotesHit, Is.EqualTo(engine.EngineStats.TotalNotes));
            Assert.That(engine.EngineStats.TotalScore, Is.EqualTo(path.ProjectedScore));
        });
    }

    // -------------------------------------------------------------------------------------

    [Test]
    public void Parameters_RoundTripTheBufferAndFlag()
    {
        var stock = ChartFixtures.GuitarParams(isBass: false);
        var single = SingleWhammyParameters.WithSingleWhammy(stock);

        Assert.Multiple(() =>
        {
            Assert.That(SingleWhammyParameters.IsEnabled(stock), Is.False);
            Assert.That(SingleWhammyParameters.IsEnabled(single), Is.True);
            Assert.That(SingleWhammyParameters.RealBuffer(single),
                Is.EqualTo(stock.StarPowerWhammyBuffer));
            Assert.That(SingleWhammyParameters.RealBuffer(stock),
                Is.EqualTo(stock.StarPowerWhammyBuffer));
            Assert.That(SingleWhammyParameters.WithSingleWhammy(single).StarPowerWhammyBuffer,
                Is.EqualTo(single.StarPowerWhammyBuffer), "Encoding twice changes nothing.");
        });
    }

    /// <summary>
    /// The copies go through the public constructors, so a field upstream adds would be dropped
    /// silently. This compares every public field (inherited ones included) but the buffer.
    /// </summary>
    [Test]
    public void Parameters_CopyEveryOtherField()
    {
        var guitar = ChartFixtures.GuitarParams(isBass: true);
        guitar.SongSpeed = 1.25;
        AssertSameFields(guitar, SingleWhammyParameters.WithSingleWhammy(guitar));

        var keys = new KeysEngineParameters(guitar.HitWindow, 4, 0.3, 0.1,
            new[] { 1f, 2f }, new[] { 3f }, 0.04, 0.02, noStarPowerOverlap: true,
            enableLanes: true, releaseKeysCanHitNotes: true) { SongSpeed = 0.75 };
        AssertSameFields(keys, SingleWhammyParameters.WithSingleWhammy(keys));
    }

    private static void AssertSameFields<T>(T source, T copy) where T : BaseEngineParameters
    {
        var fields = typeof(T).GetFields(BindingFlags.Public | BindingFlags.Instance)
            .Where(field => field.Name != nameof(BaseEngineParameters.StarPowerWhammyBuffer))
            .ToList();

        Assert.That(fields, Is.Not.Empty);
        Assert.Multiple(() =>
        {
            foreach (var field in fields)
            {
                Assert.That(field.GetValue(copy), Is.EqualTo(field.GetValue(source)),
                    $"{typeof(T).Name}.{field.Name}");
            }
        });
    }
}
