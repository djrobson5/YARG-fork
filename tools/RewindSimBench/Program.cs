using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using YARG.Core;
using YARG.Core.Chart;
using YARG.Core.Engine;
using YARG.Core.Engine.Drums;
using YARG.Core.Engine.Drums.Engines;
using YARG.Core.Engine.Guitar.Engines;
using YARG.Core.Engine.Vocals.Engines;
using YARG.Core.Game;
using YARG.Core.Input;

namespace YARG.RewindSimBench
{
    public static class Program
    {
        private static readonly float[] StarThresholds =
            { 0.06f, 0.12f, 0.2f, 0.45f, 0.75f, 1.09f };

        private static readonly float[] SoloThresholds =
            { 0.05f, 0.1f, 0.2f, 0.35f, 0.65f, 0.95f };

        /// <summary>Wall-clock timing repeats per target.</summary>
        private const int Repeats = 5;

        public static int Main()
        {
            var sw = Stopwatch.StartNew();
            var chart = LongChart.Load();
            Console.WriteLine($"Chart parsed in {sw.Elapsed.TotalMilliseconds:F0} ms");

            var guitarNotes = chart.FiveFretGuitar.GetDifficulty(Difficulty.Expert);
            var drumNotes = chart.FourLaneDrums.GetDifficulty(Difficulty.Expert);

            double lastTime = guitarNotes.Notes[^1].TimeEnd;
            Console.WriteLine(
                $"Chart length {lastTime:F1} s | guitar notes {guitarNotes.Notes.Count} " +
                $"| drum notes {drumNotes.Notes.Count} " +
                $"| SP phrases {guitarNotes.Phrases.Count(p => p.Type == PhraseType.StarPower)}");
            Console.WriteLine();

            var spTimes = new[] { 60.0, 150.0, 240.0, 330.0, 420.0, 510.0, 600.0, 690.0 };

            RunGuitar(chart, guitarNotes, spTimes, lastTime);
            RunDrums(chart, drumNotes, lastTime);
            RunVocals(chart, spTimes, lastTime);

            return 0;
        }

        // ------------------------------------------------------------------ guitar

        private static void RunGuitar(SongChart chart, InstrumentDifficulty<GuitarNote> notes,
            double[] spTimes, double lastTime)
        {
            var inputs = InputSynth.Guitar(notes.Notes, spTimes);
            Console.WriteLine($"=== FIVE-FRET GUITAR ({inputs.Count} inputs) ===");

            Func<BaseEngine> factory = () => new YargFiveFretGuitarEngine(
                notes, chart.SyncTrack,
                new EnginePreset.FiveFretGuitarPreset().Create(StarThresholds, SoloThresholds, false),
                isBot: false);

            // A sustain roughly halfway through, for a mid-sustain target.
            var sustain = notes.Notes.First(n => n.TimeLength > 0.4 && n.Time > 300);
            double midSustain = sustain.Time + sustain.TimeLength * 0.5;

            var targets = new (string Label, double Time)[]
            {
                ("early (30 s)", 30),
                ("mid (180 s)", 180),
                ($"mid-sustain ({midSustain:F1} s)", midSustain),
                ("mid-SP (+3 s after 330 s activation)", 333),
                ("late (600 s)", 600),
                ("full (end of chart)", lastTime + 1),
            };

            Report(factory, inputs, targets);
        }

        // ------------------------------------------------------------------ drums

        private static void RunDrums(SongChart chart, InstrumentDifficulty<DrumNote> notes, double lastTime)
        {
            var inputs = InputSynth.Drums(notes.Notes, Array.Empty<double>());
            Console.WriteLine($"=== FOUR-LANE DRUMS ({inputs.Count} inputs) ===");

            Func<BaseEngine> factory = () => new YargDrumsEngine(
                notes, chart.SyncTrack,
                new EnginePreset.DrumsPreset().Create(StarThresholds, SoloThresholds,
                    DrumsEngineParameters.DrumMode.NonProFourLane),
                isBot: false, isMidiDrumsInput: false);

            var targets = new (string Label, double Time)[]
            {
                ("early (30 s)", 30),
                ("mid (180 s)", 180),
                ("mid (360 s)", 360),
                ("late (600 s)", 600),
                ("full (end of chart)", lastTime + 1),
            };

            Report(factory, inputs, targets);
        }

        // ------------------------------------------------------------------ vocals

        private static void RunVocals(SongChart chart, double[] spTimes, double lastTime)
        {
            if (chart.Vocals.Parts.Count == 0)
            {
                Console.WriteLine("=== VOCALS === (no parts parsed, skipped)");
                Console.WriteLine();
                return;
            }

            var part = chart.Vocals.Parts[0];
            var notes = part.CloneAsInstrumentDifficulty();

            const float updatesPerSecond = 30f;
            var inputs = InputSynth.Vocals(notes.Notes, updatesPerSecond, spTimes);
            Console.WriteLine($"=== VOCALS ({inputs.Count} inputs, {notes.Notes.Count} phrases) ===");

            Func<BaseEngine> factory = () =>
            {
                var engine = new YargVocalsEngine(
                    part.CloneAsInstrumentDifficulty(), chart.SyncTrack,
                    new EnginePreset.VocalsPreset().Create(StarThresholds, SoloThresholds,
                        Difficulty.Expert, updatesPerSecond, true),
                    isBot: false);
                engine.BuildCountdownsFromSelectedPart();
                return engine;
            };

            var targets = new (string Label, double Time)[]
            {
                ("early (30 s)", 30),
                ("mid (180 s)", 180),
                ("mid-phrase (183.7 s)", 183.7),
                ("late (600 s)", 600),
                ("full (end of chart)", lastTime + 1),
            };

            Report(factory, inputs, targets);
        }

        // ------------------------------------------------------------------ core

        private static void Report(Func<BaseEngine> factory, List<GameInput> inputs,
            (string Label, double Time)[] targets)
        {
            // One reused replay engine across all targets, so Reset hygiene is under test too.
            var reusedReplay = factory();

            // Warm up the JIT so the first timed target is not paying for it.
            factory().ProcessUpToTime(targets[^1].Time, inputs);

            Console.WriteLine();
            Console.WriteLine($"{"target",-42} {"inputs",8} {"min ms",11} {"median ms",11} {"us/input",10}");

            var results = new List<(string Label, double Time, int Inputs, double Min, double Median)>();

            foreach (var (label, time) in targets)
            {
                int inputCount = inputs.Count(i => i.Time <= time);
                var times = new List<double>();

                for (int r = 0; r < Repeats; r++)
                {
                    var engine = factory();
                    var sw = Stopwatch.StartNew();
                    engine.ProcessUpToTime(time, inputs);
                    sw.Stop();
                    times.Add(sw.Elapsed.TotalMilliseconds);
                }

                times.Sort();
                double min = times[0];
                double median = times[times.Count / 2];

                results.Add((label, time, inputCount, min, median));

                Console.WriteLine(
                    $"{label,-42} {inputCount,8} {min,11:F1} {median,11:F1} " +
                    $"{(inputCount == 0 ? 0 : median * 1000 / inputCount),10:F2}");
            }

            // ---- Reset hygiene: what does Reset() fail to restore? ----
            {
                var pristine = factory();
                var pristineSnap = Snapshot.Capture(pristine);

                var used = factory();
                used.ProcessUpToTime(targets[^1].Time, inputs);
                used.Reset();
                var resetSnap = Snapshot.Capture(used);

                Console.WriteLine();
                PrintDiff("  Reset() hygiene (fresh vs. played-then-Reset)", Snapshot.Diff(pristineSnap, resetSnap));
            }

            // ---- Control: two live drives at different frame cadences ----
            {
                double t = targets[^2].Time;

                var a = factory();
                DriveLive(a, inputs, t, 1.0 / 60.0, 1);
                var b = factory();
                DriveLive(b, inputs, t, 1.0 / 144.0, 2);

                Console.WriteLine();
                PrintDiff($"  Control: live@60fps vs live@144fps at {t:F1} s",
                    Snapshot.Diff(Snapshot.Capture(a), Snapshot.Capture(b)));
            }

            Console.WriteLine();
            Console.WriteLine("determinism (live incremental drive vs. reset + ProcessUpToTime):");

            foreach (var (label, time) in targets)
            {
                var live = factory();
                DriveLive(live, inputs, time);
                var liveSnap = Snapshot.Capture(live);

                var fresh = factory();
                fresh.ProcessUpToTime(time, inputs);
                var freshSnap = Snapshot.Capture(fresh);

                reusedReplay.ProcessUpToTime(time, inputs);
                var reusedSnap = Snapshot.Capture(reusedReplay);

                var freshDiff = Snapshot.Diff(liveSnap, freshSnap);
                var reusedDiff = Snapshot.Diff(liveSnap, reusedSnap);

                Console.WriteLine(
                    $"  {label,-42} score={liveSnap.Values["stats.CommittedScore"]} " +
                    $"combo={liveSnap.Values["stats.Combo"]} " +
                    $"max={liveSnap.Values["stats.MaxCombo"]} " +
                    $"hit={liveSnap.Values["stats.NotesHit"]} " +
                    $"sp={liveSnap.Values["stats.StarPowerTickAmount"]} " +
                    $"spActive={liveSnap.Values["stats.IsStarPowerActive"]}");

                PrintDiff("      fresh replay ", freshDiff);
                PrintDiff("      reused replay", reusedDiff);
            }

            Console.WriteLine();
        }

        private static void PrintDiff(string label, List<(string Key, string Left, string Right)> diff)
        {
            if (diff.Count == 0)
            {
                Console.WriteLine($"{label}: IDENTICAL");
                return;
            }

            Console.WriteLine($"{label}: {diff.Count} MISMATCH(ES)");
            foreach (var (key, left, right) in diff)
            {
                Console.WriteLine($"        {key}: live={left} replay={right}");
            }
        }

        /// <summary>
        /// Drives the engine the way gameplay does: one <c>Update</c> per rendered frame, with the
        /// inputs queued through <c>QueueInput</c> as they arrive. The frame cadence is jittered so
        /// the update boundaries genuinely differ from the single-<c>Update</c> replay path.
        /// </summary>
        private static void DriveLive(BaseEngine engine, List<GameInput> inputs, double target,
            double frameStep = 1.0 / 60.0, int seed = 1234)
        {
            var rng = new Random(seed);
            int idx = 0;
            double t = 0;

            while (t < target)
            {
                double step = frameStep * (0.6 + rng.NextDouble() * 0.8);
                t = Math.Min(t + step, target);

                while (idx < inputs.Count && inputs[idx].Time <= t)
                {
                    var input = inputs[idx];
                    engine.QueueInput(ref input);
                    idx++;
                }

                engine.Update(t);
            }
        }
    }
}
