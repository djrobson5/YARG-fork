using System;
using System.Collections.Generic;
using System.Linq;
using YARG.Core.Chart;
using YARG.Core.Input;

namespace YARG.RewindSimBench
{
    /// <summary>
    /// Turns a parsed chart into a synthetic input log that looks like what
    /// <c>BasePlayer.OnGameInput</c> would have recorded into <c>_replayInputs</c> during a live
    /// run: a near-perfect play with deliberate misses, whammy, and scripted Star Power
    /// activations.
    /// <para/>
    /// Everything is generated from a fixed seed so the log is byte-identical run to run.
    /// The list is sorted and asserted strictly non-decreasing in time, which is the invariant the
    /// live path gets for free (<c>QueueInput</c> clamps out-of-order inputs forward, and
    /// <c>BasePlayer</c> records the <em>clamped</em> input) but which <c>ProcessUpToTime</c>
    /// assumes rather than enforces.
    /// </summary>
    public static class InputSynth
    {
        /// <summary>Every Nth note is deliberately not played, producing a miss + combo reset.</summary>
        private const int MissEvery = 37;

        public static List<GameInput> Guitar(IReadOnlyList<GuitarNote> notes, double[] spActivationTimes)
        {
            var rng = new Random(20260910);
            var inputs = new List<GameInput>(notes.Count * 4);

            int held = 0;

            for (int i = 0; i < notes.Count; i++)
            {
                var note = notes[i];

                if (i % MissEvery == MissEvery - 1)
                {
                    // Deliberate miss: play nothing at all for this note.
                    continue;
                }

                double jitter = (rng.NextDouble() - 0.5) * 0.030;
                double strumTime = note.Time + jitter;

                int mask = note.NoteMask & 0x1F;
                if (mask == 0)
                {
                    // Open note: release everything.
                    mask = 0;
                }

                int toRelease = held & ~mask;
                int toPress = mask & ~held;

                for (int f = 0; f < 5; f++)
                {
                    if ((toRelease & (1 << f)) != 0)
                    {
                        inputs.Add(GameInput.Create(strumTime - 0.012, (GuitarAction) f, false));
                    }
                }

                for (int f = 0; f < 5; f++)
                {
                    if ((toPress & (1 << f)) != 0)
                    {
                        inputs.Add(GameInput.Create(strumTime - 0.008, (GuitarAction) f, true));
                    }
                }

                held = mask;

                inputs.Add(GameInput.Create(strumTime, GuitarAction.StrumDown, true));
                inputs.Add(GameInput.Create(strumTime + 0.015, GuitarAction.StrumDown, false));

                // Whammy across long sustains.
                if (note.TimeLength > 0.4)
                {
                    for (double t = note.Time + 0.05; t < note.Time + note.TimeLength; t += 1.0 / 30.0)
                    {
                        inputs.Add(GameInput.Create(t, GuitarAction.Whammy, (float) rng.NextDouble()));
                    }
                }
            }

            foreach (var t in spActivationTimes)
            {
                inputs.Add(GameInput.Create(t, GuitarAction.StarPower, true));
                inputs.Add(GameInput.Create(t + 0.05, GuitarAction.StarPower, false));
            }

            return Finish(inputs);
        }

        public static List<GameInput> Drums(IReadOnlyList<DrumNote> notes, double[] spActivationTimes)
        {
            var rng = new Random(20260911);
            var inputs = new List<GameInput>(notes.Count * 2);

            int index = 0;
            foreach (var parent in notes)
            {
                foreach (var note in parent.AllNotes)
                {
                    index++;
                    if (index % MissEvery == 0)
                    {
                        continue;
                    }

                    double jitter = (rng.NextDouble() - 0.5) * 0.030;
                    double t = note.Time + jitter;

                    var action = PadToAction(note.Pad);

                    inputs.Add(GameInput.Create(t, action, 1.0f));
                    inputs.Add(GameInput.Create(t + 0.015, action, 0.0f));
                }
            }

            foreach (var t in spActivationTimes)
            {
                // Drums activate SP by hitting a Star Power activation note; the engine also
                // accepts the dedicated action through IsStarPowerInputActive on other modes.
                // Drums do not have a StarPower action, so nothing is emitted here.
                _ = t;
            }

            return Finish(inputs);
        }

        public static List<GameInput> Vocals(IReadOnlyList<VocalNote> phrases, double updatesPerSecond,
            double[] spActivationTimes)
        {
            var rng = new Random(20260912);
            var inputs = new List<GameInput>();

            double step = 1.0 / updatesPerSecond;
            int index = 0;

            foreach (var phrase in phrases)
            {
                foreach (var note in phrase.ChildNotes)
                {
                    index++;
                    bool miss = index % MissEvery == 0;

                    for (double t = note.Time; t < note.TimeEnd; t += step)
                    {
                        float pitch = note.PitchAtSongTime(t);
                        if (miss)
                        {
                            pitch += 6f;
                        }
                        else
                        {
                            pitch += (float) ((rng.NextDouble() - 0.5) * 0.2);
                        }

                        inputs.Add(GameInput.Create(t, VocalsAction.Pitch, pitch));
                    }
                }
            }

            foreach (var t in spActivationTimes)
            {
                inputs.Add(GameInput.Create(t, VocalsAction.StarPower, true));
                inputs.Add(GameInput.Create(t + 0.05, VocalsAction.StarPower, false));
            }

            return Finish(inputs);
        }

        private static DrumsAction PadToAction(int pad)
        {
            return (FourLaneDrumPad) pad switch
            {
                FourLaneDrumPad.Kick          => DrumsAction.Kick,
                FourLaneDrumPad.RedDrum       => DrumsAction.RedDrum,
                FourLaneDrumPad.YellowDrum    => DrumsAction.YellowDrum,
                FourLaneDrumPad.BlueDrum      => DrumsAction.BlueDrum,
                FourLaneDrumPad.GreenDrum     => DrumsAction.GreenDrum,
                FourLaneDrumPad.YellowCymbal  => DrumsAction.YellowCymbal,
                FourLaneDrumPad.BlueCymbal    => DrumsAction.BlueCymbal,
                FourLaneDrumPad.GreenCymbal   => DrumsAction.GreenCymbal,
                _                             => DrumsAction.RedDrum
            };
        }

        private static List<GameInput> Finish(List<GameInput> inputs)
        {
            var sorted = inputs.OrderBy(i => i.Time).ToList();

            for (int i = 1; i < sorted.Count; i++)
            {
                if (sorted[i].Time < sorted[i - 1].Time)
                {
                    throw new InvalidOperationException("Synthesised input log is not monotonic.");
                }
            }

            return sorted;
        }
    }
}
