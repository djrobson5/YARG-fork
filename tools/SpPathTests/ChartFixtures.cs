using System;
using System.IO;
using Melanchall.DryWetMidi.Core;
using YARG.Core;
using YARG.Core.Chart;
using YARG.Core.Engine.Guitar;
using YARG.Core.Game;

namespace YARG.SpPathTests;

/// <summary>
/// Chart loading + engine parameter construction, mirroring
/// <c>YARG.Core.UnitTests/Engine/EngineTester.cs:34-49</c>.
/// </summary>
public static class ChartFixtures
{
    /// <summary>
    /// Star multiplier thresholds as used by the upstream engine tests
    /// (<c>EngineTester.cs:15-23</c>). They only affect star counts, never score.
    /// </summary>
    public static readonly float[] StarMultiplierThresholds =
    {
        0.06f, 0.12f, 0.2f, 0.45f, 0.75f, 1.09f
    };

    public static readonly float[] SoloBonusStarMultiplierThresholds =
    {
        0.05f, 0.1f, 0.2f, 0.35f, 0.65f, 0.95f
    };

    /// <summary>
    /// The one chart the repo ships that is readable from outside the submodule:
    /// <c>YARG.Core/YARG.Core.UnitTests/Engine/Test Charts/drawntotheflame.mid</c>.
    /// Read-only; the submodule is never modified.
    /// </summary>
    public const string DrawnToTheFlame = "drawntotheflame.mid";

    private static string RepoRoot
    {
        get
        {
            // .../tools/SpPathTests/bin/Debug/net8.0 -> repo root
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "YARG.Core")))
            {
                dir = dir.Parent;
            }

            if (dir is null)
            {
                throw new DirectoryNotFoundException("Could not locate the repository root from " +
                    AppContext.BaseDirectory);
            }

            return dir.FullName;
        }
    }

    public static string ChartPath(string fileName) => Path.Combine(
        RepoRoot, "YARG.Core", "YARG.Core.UnitTests", "Engine", "Test Charts", fileName);

    public static SongChart LoadChart(string fileName = DrawnToTheFlame)
    {
        var midi = MidiFile.Read(ChartPath(fileName));
        return SongChart.FromMidi(in ParseSettings.Default_Midi, midi);
    }

    /// <summary>
    /// Builds the guitar engine parameters from the stock default preset, the same call the
    /// game makes (<c>EnginePreset.Instruments.cs:144-166</c>). MaxMultiplier comes out as 4
    /// for guitar and 6 for bass.
    /// </summary>
    public static GuitarEngineParameters GuitarParams(bool isBass = false) =>
        new EnginePreset.FiveFretGuitarPreset().Create(
            StarMultiplierThresholds, SoloBonusStarMultiplierThresholds, isBass);

    public static InstrumentDifficulty<GuitarNote> GuitarNotes(SongChart chart, bool isBass = false,
        Difficulty difficulty = Difficulty.Expert) =>
        (isBass ? chart.FiveFretBass : chart.FiveFretGuitar).GetDifficulty(difficulty);
}
