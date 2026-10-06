using YARG.Core.Chart;
using YARG.Core.Engine.Guitar;

namespace YARG.SpPathTests;

public static class BotRunner
{
    /// <summary>
    /// Fixed update step. The engine queues its own updates for note times, sustain bursts and
    /// the Star Power end (<c>BaseEngine.Generic.cs:132-200</c>), so the step size does not
    /// change note/sustain scoring — it only changes how often the bot toggles its inputs.
    /// 1/120 s is a plausible frame rate and keeps the run well under a second.
    /// </summary>
    public const double StepSeconds = 1.0 / 120.0;

    /// <summary>Extra time run past the last note so trailing sustains and solos finish.</summary>
    public const double TailSeconds = 5.0;

    public static double ChartEndTime(InstrumentDifficulty<GuitarNote> notes) =>
        notes.Notes.Count == 0 ? 0.0 : notes.Notes[^1].TimeEnd + TailSeconds;

    public static void RunToEnd(GuitarEngine engine, InstrumentDifficulty<GuitarNote> notes)
    {
        double end = ChartEndTime(notes);
        for (double t = 0.0; t < end; t += StepSeconds)
        {
            engine.Update(t);
        }

        engine.Update(end);
    }
}
