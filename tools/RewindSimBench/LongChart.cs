using System;
using System.Collections.Generic;
using System.Linq;
using Melanchall.DryWetMidi.Common;
using Melanchall.DryWetMidi.Core;
using YARG.Core.Chart;

namespace YARG.RewindSimBench
{
    /// <summary>
    /// A dense ~12 minute chart, built as a MIDI in memory and parsed by YARG's own loader.
    /// <para/>
    /// The dev box's song library has exactly one song in it and it is a CON, which the
    /// standalone loader here cannot open, so the chart is synthesised instead. It is deliberately
    /// on the heavy side of a real Expert chart (~4.4 NPS sustained for 12 minutes, ~3.1k guitar
    /// notes) so the timings are an upper bound rather than a best case.
    /// <para/>
    /// Shape:
    /// <list type="bullet">
    /// <item>4/4 at 120 BPM, with a tempo change to 144 BPM at the 6 minute mark and a stretch of
    /// 3/4 so measure ticks and quarter ticks disagree (that is where Star Power drain is
    /// meter-dependent).</item>
    /// <item>Eighth notes throughout, with a chord every 8th note and a 2-beat extended sustain
    /// every 32nd note.</item>
    /// <item>A Star Power phrase every 16 measures, so there is far more Star Power available
    /// than can be spent — activations are then chosen by the input synthesiser.</item>
    /// <item>Two solos.</item>
    /// <item>A parallel PART DRUMS and PART VOCALS on the same grid.</item>
    /// </list>
    /// </summary>
    public static class LongChart
    {
        public const uint Resolution = 480;

        private const byte GtrGreen  = 96;
        private const byte GtrOrange = 100;

        // Expert drums: 96 kick, 97 red, 98 yellow, 99 blue, 100 green.
        private const byte DrumKick = 96;

        private const byte SoloNote      = 103;
        private const byte StarPowerNote = 116;

        private const byte VocalPhraseNote = 105;

        private const long Step = 240;            // eighth note
        private const long Tap  = 40;             // trimmed to zero by the parser
        private const long LongSustain = 960;     // two beats

        /// <summary>Quarter tick of the 120 -> 144 BPM change (6 minutes in at 120 BPM).</summary>
        public const long TempoChangeTick = 345600;

        /// <summary>Ticks after the tempo change, covering another 6 minutes at 144 BPM.</summary>
        private const long AfterTempoTicks = 414720;

        public const long EndTick = TempoChangeTick + AfterTempoTicks;

        private const long ThreeFourStart = 460800;
        private const long ThreeFourEnd   = 483840;

        public static SongChart Load()
        {
            var settings = ParseSettings.Default_Midi;
            return SongChart.FromMidi(in settings, BuildMidi());
        }

        public static MidiFile BuildMidi()
        {
            var midi = new MidiFile
            {
                TimeDivision = new TicksPerQuarterNoteTimeDivision((short) Resolution)
            };

            midi.Chunks.Add(BuildSyncTrack());
            midi.Chunks.Add(BuildGuitarTrack());
            midi.Chunks.Add(BuildDrumsTrack());
            midi.Chunks.Add(BuildVocalsTrack());

            return midi;
        }

        private static TrackChunk BuildSyncTrack()
        {
            var track = new TrackBuilder("bench");

            track.Add(0, new SetTempoEvent(500_000));       // 120 BPM
            track.Add(0, new TimeSignatureEvent(4, 4));

            track.Add(ThreeFourStart, new TimeSignatureEvent(3, 4));
            track.Add(ThreeFourEnd, new TimeSignatureEvent(4, 4));

            track.Add(TempoChangeTick, new SetTempoEvent(416_666)); // 144 BPM

            return track.Build();
        }

        private static TrackChunk BuildGuitarTrack()
        {
            var track = new TrackBuilder("PART GUITAR");
            var frets = new byte[] { GtrGreen, 97, 98, 99, GtrOrange };

            int n = 0;
            for (long tick = 0; tick < EndTick; tick += Step, n++)
            {
                if (n % 32 == 31)
                {
                    // Extended sustain: spans the following notes and at least one multiplier
                    // change, which is the only way RebaseSustains runs mid-sustain.
                    track.AddNote(frets[n % 5], tick, LongSustain);
                }
                else if (n % 8 == 0 && n > 0)
                {
                    // Chord.
                    track.AddNote(frets[n % 5], tick, Tap);
                    track.AddNote(frets[(n + 2) % 5], tick, Tap);
                }
                else
                {
                    track.AddNote(frets[n % 5], tick, Tap);
                }
            }

            // A Star Power phrase every 16 measures, each covering four beats of notes.
            for (long tick = 3840; tick + 1920 < EndTick; tick += 16 * 1920)
            {
                AddPhrase(track, StarPowerNote, tick, tick + 1920);
            }

            AddPhrase(track, SoloNote, 96000, 103680);
            AddPhrase(track, SoloNote, 480000, 487680);

            return track.Build();
        }

        private static TrackChunk BuildDrumsTrack()
        {
            var track = new TrackBuilder("PART DRUMS");
            // Kick + one pad, alternating, on the same eighth-note grid.
            var pads = new byte[] { 97, 98, 99, 100 };

            int n = 0;
            for (long tick = 0; tick < EndTick; tick += Step, n++)
            {
                if (n % 2 == 0)
                {
                    track.AddNote(DrumKick, tick, Tap);
                }
                else
                {
                    track.AddNote(pads[(n / 2) % 4], tick, Tap);
                }
            }

            for (long tick = 3840; tick + 1920 < EndTick; tick += 16 * 1920)
            {
                AddPhrase(track, StarPowerNote, tick, tick + 1920);
            }

            return track.Build();
        }

        private static TrackChunk BuildVocalsTrack()
        {
            var track = new TrackBuilder("PART VOCALS");

            // Phrases of two measures, eight notes each, pitches walking a scale.
            var scale = new byte[] { 55, 57, 59, 60, 62, 64, 65, 67 };

            int n = 0;
            for (long phraseStart = 0; phraseStart + 3840 < EndTick; phraseStart += 3840)
            {
                AddPhrase(track, VocalPhraseNote, phraseStart, phraseStart + 3600);

                for (int i = 0; i < 8; i++)
                {
                    long tick = phraseStart + i * 440;
                    byte pitch = scale[n % scale.Length];
                    n++;

                    track.Add(tick, new Melanchall.DryWetMidi.Core.LyricEvent("la"));
                    track.AddNote(pitch, tick, 400);
                }
            }

            for (long tick = 3840; tick + 1920 < EndTick; tick += 16 * 3840)
            {
                AddPhrase(track, StarPowerNote, tick, tick + 3600);
            }

            return track.Build();
        }

        private static void AddPhrase(TrackBuilder track, byte note, long start, long end)
        {
            track.Add(start, Note.On(note));
            track.Add(end, Note.Off(note));
        }

        private static class Note
        {
            public static NoteOnEvent On(byte note) =>
                new((SevenBitNumber) note, (SevenBitNumber) 100);

            public static NoteOffEvent Off(byte note) =>
                new((SevenBitNumber) note, (SevenBitNumber) 0);
        }

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
}
