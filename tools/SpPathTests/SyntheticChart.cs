using System;
using System.Collections.Generic;
using System.Linq;
using Melanchall.DryWetMidi.Common;
using Melanchall.DryWetMidi.Core;
using YARG.Core.Chart;

namespace YARG.SpPathTests;

/// <summary>
/// A hand-authored chart, built as a MIDI in memory and parsed by YARG's own loader, covering the
/// scoring and Star Power branches <c>drawntotheflame.mid</c> has zero instances of.
/// <para/>
/// Slice 2 closed with a list of §1 rules that were <em>read from the engine source</em> rather
/// than exercised (see <c>docs/sp-path-design.md</c>, "Progress"). This fixture exists to turn
/// each of them into a tested one:
/// <list type="bullet">
/// <item>a <b>time-signature change</b> (4/4 → 3/4 → 4/4) and a <b>tempo change</b>, so measure
/// ticks stop being a linear function of quarter ticks and the meter-aware Star Power drain is
/// distinguishable from CHOpt's flat-beat model;</item>
/// <item>a <b>disjoint chord</b> whose children have different sustain lengths, so the per-child
/// sustain rule runs;</item>
/// <item>a <b>sustain shorter than the burst threshold</b>, so the "burst at <c>note.Tick</c>"
/// branch runs;</item>
/// <item>an <b>extended sustain</b> that spans later notes and a multiplier change, so
/// <c>RebaseSustains</c> runs during a sustain;</item>
/// <item>an <b>open note</b>;</item>
/// <item>a <b>BRE</b>, with and without a preceding coda, so both sides of the model's
/// deliberate BRE divergence are pinned;</item>
/// <item><b>Star Power phrases</b> spread across the meter change, so windows are walked in a
/// region where measure ticks and quarter ticks disagree.</item>
/// </list>
/// The MIDI is built in memory and handed straight to <c>SongChart.FromMidi</c> — the same entry
/// point <c>ChartFixtures.LoadChart</c> uses — so nothing is written to disk and the submodule's
/// test charts are untouched.
/// </summary>
public static class SyntheticChart
{
    public const uint Resolution = 480;

    // Expert 5-fret note numbers (MidIOHelper.cs:60-74 territory: 96 is Expert green).
    private const byte Green = 96;
    private const byte Red = 97;
    private const byte Yellow = 98;
    private const byte Blue = 99;
    private const byte Orange = 100;

    private const byte SoloNote = 103;      // MidIOHelper.SOLO_NOTE
    private const byte StarPowerNote = 116; // MidIOHelper.STARPOWER_NOTE
    private const byte BreNote = 120;       // MidIOHelper.BIG_ROCK_ENDING_NOTE_1

    private const byte SustainVelocity = 100;

    /// <summary>Ticks between ordinary notes. An eighth note at <see cref="Resolution"/>.</summary>
    public const long Step = 240;

    /// <summary>
    /// Length given to notes that are not meant to be sustains. Shorter than
    /// <see cref="SustainCutoff"/>, so the parser trims them to zero.
    /// </summary>
    private const long Tap = 40;

    /// <summary>
    /// Sustain cutoff for this fixture, overriding <c>ParseSettings.Default_Midi</c>'s
    /// <c>Resolution / 3</c> (<c>MidReader.cs:122-124</c>) — 160 ticks at this resolution.
    /// <para/>
    /// It <b>has</b> to be overridden to get a short sustain at all: the burst threshold is
    /// <c>Resolution / SUSTAIN_BURST_FRACTION</c> = 120, so with the default cutoff every sustain
    /// that survives parsing is already longer than the burst threshold and the
    /// "burst at <c>note.Tick</c>" branch (<c>BaseEngine.Generic.cs:857-864</c>) is unreachable.
    /// Real charts can set this from <c>song.ini</c>'s <c>sustain_cutoff_threshold</c>
    /// (<c>SongEntry.IniBase.cs:258</c>), so this is a configuration the game genuinely produces,
    /// not a fiction invented for the test.
    /// </summary>
    public const long SustainCutoff = 60;

    /// <summary>A short sustain: past <see cref="SustainCutoff"/>, under the 120-tick burst threshold.</summary>
    private const long ShortSustain = 90;

    /// <summary>Quarter tick where the tempo goes from 120 to 180 BPM (start of measure 5).</summary>
    public const long TempoChangeTick = 9600;

    /// <summary>Quarter tick where the meter goes 4/4 → 3/4 (start of measure 8).</summary>
    public const long ThreeFourTick = 15360;

    /// <summary>Quarter tick where the meter goes back 3/4 → 4/4, after four 3/4 measures.</summary>
    public const long BackToFourFourTick = ThreeFourTick + 4 * 1440;

    /// <summary>Quarter tick the coda / BRE starts at.</summary>
    public const long BreStartTick = BackToFourFourTick + 8 * 1920;

    /// <summary>Quarter tick the BRE (and the chart) ends at.</summary>
    public const long BreEndTick = BreStartTick + 1920;

    public static SongChart Load(bool includeCoda = true)
    {
        var settings = ParseSettings.Default_Midi;
        settings.SustainCutoffThreshold = SustainCutoff;
        return SongChart.FromMidi(in settings, BuildMidi(includeCoda));
    }

    public static InstrumentDifficulty<GuitarNote> GuitarNotes(SongChart chart) =>
        chart.FiveFretGuitar.GetDifficulty(YARG.Core.Difficulty.Expert);

    // -------------------------------------------------------------------------------------

    public static MidiFile BuildMidi(bool includeCoda = true)
    {
        var midi = new MidiFile();
        midi.TimeDivision = new TicksPerQuarterNoteTimeDivision((short) Resolution);

        midi.Chunks.Add(BuildSyncTrack());
        midi.Chunks.Add(BuildEventsTrack(includeCoda));
        midi.Chunks.Add(BuildGuitarTrack());

        return midi;
    }

    private static TrackChunk BuildSyncTrack()
    {
        var track = new TrackBuilder("synthetic");

        // 120 BPM, 4/4.
        track.Add(0, new SetTempoEvent(500_000));
        track.Add(0, new TimeSignatureEvent(4, 4));

        // A tempo change, to prove the Star Power bar is tempo-independent: it is 8 measures of
        // chart time either side of this (BaseEngine.Generic.cs:1073-1076).
        track.Add(TempoChangeTick, new SetTempoEvent(333_333));

        // A meter change, which is where measure ticks stop tracking quarter ticks: 3/4 measures
        // are 1440 quarter ticks but still MeasureResolution (1920) measure ticks, so Star Power
        // drains 4/3 as fast per quarter tick here.
        track.Add(ThreeFourTick, new TimeSignatureEvent(3, 4));
        track.Add(BackToFourFourTick, new TimeSignatureEvent(4, 4));

        return track.Build();
    }

    private static TrackChunk BuildEventsTrack(bool includeCoda)
    {
        var track = new TrackBuilder("EVENTS");

        if (includeCoda)
        {
            // MidIOHelper.CODA_START. Without it the BRE phrase is still parsed, but the engine
            // never sets CodaHasStarted and therefore scores the BRE notes normally.
            track.Add(BreStartTick, new Melanchall.DryWetMidi.Core.TextEvent("[coda]"));
        }

        return track.Build();
    }

    private static TrackChunk BuildGuitarTrack()
    {
        var track = new TrackBuilder("PART GUITAR");
        var frets = new[] { Green, Red, Yellow, Blue, Orange };

        // ---- Section 1: 4/4 at 120 BPM, plain notes plus the odd-shaped sustains ----
        //
        // A plain run first, so the multiplier has climbed before the interesting notes.
        long tick = 0;
        int f = 0;
        while (tick < 1200)
        {
            track.AddNote(frets[f++ % 5], tick, Tap);
            tick += Step;
        }

        // A sustain shorter than the burst threshold (Resolution / 4 = 120 ticks), which commits
        // at note.Tick instead of TickEnd - threshold (BaseEngine.Generic.cs:857-864).
        track.AddNote(Green, 1200, ShortSustain);

        tick = 1440;
        while (tick < 2400)
        {
            track.AddNote(frets[f++ % 5], tick, Tap);
            tick += Step;
        }

        // A disjoint chord: two frets at the same tick with different lengths
        // (Guitar/GuitarEngine.cs:278-296 starts one sustain per sustained child).
        track.AddNote(Green, 2400, 960);
        track.AddNote(Yellow, 2400, 480);

        tick = 3360;
        while (tick < 4800)
        {
            track.AddNote(frets[f++ % 5], tick, Tap);
            tick += Step;
        }

        // An open note. The Phase Shift sysex marks a *range* as open, so it brackets one note.
        track.Add(4800 - 1, OpenPhrase(start: true));
        track.AddNote(Green, 4800, Tap);
        track.Add(4800 + Tap + 1, OpenPhrase(start: false));

        // An extended sustain: long enough to span later notes and at least one multiplier
        // change, which is the only way RebaseSustains runs mid-sustain
        // (BaseEngine.Generic.cs:1249-1271).
        track.AddNote(Green, 5040, 2400);

        tick = 5280;
        while (tick < TempoChangeTick)
        {
            track.AddNote(frets[1 + f++ % 4], tick, Tap);
            tick += Step;
        }

        // ---- Section 2: the tempo change, then the meter change ----
        while (tick < ThreeFourTick)
        {
            track.AddNote(frets[f++ % 5], tick, Tap);
            tick += Step;
        }

        // 3/4. Notes keep the same quarter-tick spacing, so the measure-tick spacing changes.
        while (tick < BackToFourFourTick)
        {
            track.AddNote(frets[f++ % 5], tick, tick % 960 == 0 ? 720 : Tap);
            tick += Step;
        }

        // ---- Section 3: back to 4/4, up to the BRE ----
        while (tick < BreStartTick)
        {
            // A chord every eight notes, so chord scoring is exercised outside the disjoint case.
            if (f % 8 == 0)
            {
                track.AddNote(Red, tick, Tap);
                track.AddNote(Blue, tick, Tap);
                f++;
            }
            else
            {
                track.AddNote(frets[f++ % 5], tick, Tap);
            }

            tick += Step;
        }

        // ---- The BRE ----
        track.Add(BreStartTick, Note.On(BreNote));
        track.Add(BreEndTick, Note.Off(BreNote));
        for (long t = BreStartTick; t < BreEndTick; t += Step)
        {
            track.AddNote(frets[f++ % 5], t, Tap);
        }

        // ---- Star Power phrases ----
        //
        // Eight of them, spread so that at least two fall on either side of the meter change and
        // the optimizer has real choices. Each covers a run of notes; the last note strictly
        // inside carries IsStarPowerEnd (MoonSongLoader.cs:314-326, inclusiveEnd: false).
        AddPhrase(track, StarPowerNote, 0, 1920);
        AddPhrase(track, StarPowerNote, 4800, 6240);
        AddPhrase(track, StarPowerNote, 9600, 11040);
        AddPhrase(track, StarPowerNote, 14400, 15840);   // straddles the 4/4 -> 3/4 change
        AddPhrase(track, StarPowerNote, 19200, 20160);
        AddPhrase(track, StarPowerNote, 24000, 25440);
        AddPhrase(track, StarPowerNote, 28800, 30240);
        AddPhrase(track, StarPowerNote, 33600, 35040);

        // A solo, so the fixed solo-bonus offset is exercised here too.
        AddPhrase(track, SoloNote, 3360, 4800);

        return track.Build();
    }

    /// <summary>
    /// A second, deliberately <b>denser</b> synthetic chart whose optimum needs <b>four</b>
    /// activations, so the brute-force cross-check actually exercises multi-window chaining
    /// rather than one or two isolated windows.
    /// <para/>
    /// The main fixture cannot do this: it has eight phrases spread over a long chart, and its
    /// optimum spends them on a couple of long windows, so the exhaustive search never has to get
    /// the interaction between three or four windows right. Here the shape forces it:
    /// <list type="bullet">
    /// <item>Four <b>dense clusters</b>, each exactly four measures long — the span a half-bar
    /// window covers — separated by four-measure <b>sparse</b> stretches worth an eighth as much.</item>
    /// <item>Exactly two Star Power phrases in each sparse stretch, so half a bar (and only half a
    /// bar) is banked immediately before each cluster, and none of the phrases falls inside a
    /// window where it would extend it instead of banking.</item>
    /// <item>A dense lead-in first, so the combo multiplier is already capped before the first
    /// cluster and the four clusters are worth the same.</item>
    /// </list>
    /// Eight phrases put <see cref="BruteForce"/>'s activation bound at four, which is exactly
    /// what the optimum uses — so the search has to chain all four windows to find it.
    /// <para/>
    /// Everything is 4/4 at 120 BPM: the meter- and tempo-dependent branches are
    /// <see cref="SyntheticChart"/>'s job, and keeping the sync track trivial here keeps the
    /// exhaustive search's job about window <em>chaining</em>.
    /// </summary>
    public static class Dense
    {
        /// <summary>Quarter ticks per 4/4 measure at <see cref="Resolution"/>.</summary>
        public const long Measure = 4 * Resolution;

        /// <summary>Measures in a block — four, which is exactly half a Star Power bar.</summary>
        public const long BlockMeasures = 4;

        /// <summary>Sparse + cluster pairs. Two phrases each, so four activations are affordable.</summary>
        public const int Blocks = 4;

        /// <summary>Measures of dense notes before the first block, to cap the multiplier.</summary>
        public const long LeadInMeasures = 4;

        public static SongChart Load()
        {
            var settings = ParseSettings.Default_Midi;
            settings.SustainCutoffThreshold = SustainCutoff;
            return SongChart.FromMidi(in settings, BuildMidi());
        }

        public static InstrumentDifficulty<GuitarNote> GuitarNotes(SongChart chart) =>
            chart.FiveFretGuitar.GetDifficulty(YARG.Core.Difficulty.Expert);

        private static MidiFile BuildMidi()
        {
            var midi = new MidiFile
            {
                TimeDivision = new TicksPerQuarterNoteTimeDivision((short) Resolution)
            };

            var sync = new TrackBuilder("dense");
            sync.Add(0, new SetTempoEvent(500_000));
            sync.Add(0, new TimeSignatureEvent(4, 4));
            midi.Chunks.Add(sync.Build());

            midi.Chunks.Add(BuildGuitarTrack());
            return midi;
        }

        private static TrackChunk BuildGuitarTrack()
        {
            var track = new TrackBuilder("PART GUITAR");
            var frets = new[] { Green, Red, Yellow, Blue, Orange };
            int f = 0;

            // Lead-in: 32 notes, enough combo for the 4x cap (min(combo/10 + 1, 4)).
            long tick = 0;
            long leadInEnd = LeadInMeasures * Measure;
            for (; tick < leadInEnd; tick += Step)
            {
                track.AddNote(frets[f++ % 5], tick, Tap);
            }

            for (int b = 0; b < Blocks; b++)
            {
                // Sparse stretch: two notes, each its own Star Power phrase. A phrase's last note
                // strictly inside the range carries IsStarPowerEnd, so a range covering exactly
                // one note makes that note the phrase end.
                long sparseStart = tick;
                for (int k = 0; k < 2; k++)
                {
                    long noteTick = sparseStart + k * 2 * Measure;
                    track.AddNote(frets[f++ % 5], noteTick, Tap);
                    AddPhrase(track, StarPowerNote, noteTick, noteTick + Resolution);
                }

                // Dense cluster: exactly the span a half-bar window covers, and worth eight times
                // the sparse stretch it follows.
                long clusterStart = sparseStart + BlockMeasures * Measure;
                for (tick = clusterStart; tick < clusterStart + BlockMeasures * Measure;
                     tick += Resolution)
                {
                    track.AddNote(frets[f++ % 5], tick, Tap);
                }
            }

            return track.Build();
        }
    }

    private static void AddPhrase(TrackBuilder track, byte note, long start, long end)
    {
        track.Add(start, Note.On(note));
        track.Add(end, Note.Off(note));
    }

    /// <summary>DryWetMidi wants <c>SevenBitNumber</c>s, not bytes.</summary>
    private static class Note
    {
        public static NoteOnEvent On(byte note) =>
            new((SevenBitNumber) note, (SevenBitNumber) SustainVelocity);

        public static NoteOffEvent Off(byte note) =>
            new((SevenBitNumber) note, (SevenBitNumber) 0);
    }

    private static NormalSysExEvent OpenPhrase(bool start) =>
        // PhaseShiftSysEx: "PS\0", type Phrase, difficulty Expert, code Guitar_Open, value, F7.
        new(new byte[] { 0x50, 0x53, 0x00, 0x00, 0x03, 0x01, (byte) (start ? 0x01 : 0x00), 0xF7 });

    // -------------------------------------------------------------------------------------

    /// <summary>
    /// Collects absolutely-timed events and emits a <see cref="TrackChunk"/> with the delta times
    /// filled in. Note-offs sort before note-ons at the same tick, which is what keeps a note
    /// ending exactly where the next one starts from swallowing it.
    /// </summary>
    private sealed class TrackBuilder
    {
        private readonly List<(long Tick, int Order, int Seq, MidiEvent Event)> _events = new();
        private readonly string _name;
        private int _seq;

        public TrackBuilder(string name) => _name = name;

        public void Add(long tick, MidiEvent midiEvent)
        {
            if (tick < 0) throw new ArgumentOutOfRangeException(nameof(tick));
            int order = midiEvent is NoteOffEvent ? 0 : 1;
            _events.Add((tick, order, _seq++, midiEvent));
        }

        public void AddNote(byte note, long tick, long length)
        {
            Add(tick, Note.On(note));
            Add(tick + length, Note.Off(note));
        }

        public TrackChunk Build()
        {
            var chunk = new TrackChunk();
            chunk.Events.Add(new SequenceTrackNameEvent(_name));

            long previous = 0;
            foreach (var e in _events.OrderBy(e => e.Tick).ThenBy(e => e.Order).ThenBy(e => e.Seq))
            {
                e.Event.DeltaTime = e.Tick - previous;
                previous = e.Tick;
                chunk.Events.Add(e.Event);
            }

            return chunk;
        }
    }
}
