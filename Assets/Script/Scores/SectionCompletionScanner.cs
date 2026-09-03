using System;
using System.Collections.Generic;
using YARG.Core.Chart;

namespace YARG.Scores
{
    /// <summary>
    /// The note completion of a single chart section for a single run.
    /// </summary>
    public readonly struct SectionCompletionResult
    {
        /// <summary>
        /// The index of the section within the chart's section list.
        /// </summary>
        public readonly int SectionIndex;

        /// <summary>
        /// The amount of notes in the section, counted with the same chord semantics
        /// as <c>EngineStats.TotalNotes</c>.
        /// </summary>
        public readonly int NotesTotal;

        /// <summary>
        /// The amount of those notes that were hit.
        /// </summary>
        public readonly int NotesHit;

        public SectionCompletionResult(int sectionIndex, int notesTotal, int notesHit)
        {
            SectionIndex = sectionIndex;
            NotesTotal = notesTotal;
            NotesHit = notesHit;
        }

        /// <remarks>
        /// A section containing no notes for this instrument is never perfected, and is
        /// excluded from the completion totals entirely.
        /// </remarks>
        public bool IsPerfected => NotesTotal > 0 && NotesHit == NotesTotal;
    }

    /// <summary>
    /// Maps a played note track onto the chart's sections to determine which sections
    /// had every one of their notes hit.
    /// </summary>
    /// <remarks>
    /// This is a pure computation over the hit flags left on the notes by the engine
    /// (see <c>Note.WasHit</c>); it does no grading of its own. Whether a given run is
    /// allowed to earn section credit is decided by the caller, not here.
    /// </remarks>
    public static class SectionCompletionScanner
    {
        /// <summary>
        /// Scans a regular (non-vocals) note track.
        /// </summary>
        /// <param name="getNoteCount">
        /// Must be the engine's <c>GetNumberOfNotes</c>, so that the totals match
        /// <c>EngineStats.TotalNotes</c> for instruments that treat chords as separate notes.
        /// </param>
        public static List<SectionCompletionResult> ScanNotes<TNote>(IReadOnlyList<Section> sections,
            IReadOnlyList<TNote> notes, Func<TNote, int> getNoteCount)
            where TNote : Note<TNote>
        {
            if (sections.Count == 0)
            {
                return new List<SectionCompletionResult>();
            }

            int[] totals = new int[sections.Count];
            int[] hits = new int[sections.Count];

            int sectionIndex = 0;
            foreach (var note in notes)
            {
                // Notes during a big rock ending don't count towards the total note count,
                // so a section overlapping one could never reach 100% if they were included.
                if (note.IsBigRockEnding)
                {
                    continue;
                }

                sectionIndex = AdvanceSectionIndex(sections, sectionIndex, note.Tick);

                int total = getNoteCount(note);
                totals[sectionIndex] += total;

                if (total <= 1)
                {
                    // Chords count as a single note, so all of the sub-notes must be hit
                    if (note.WasFullyHit())
                    {
                        hits[sectionIndex]++;
                    }
                }
                else
                {
                    // Chords count as separate notes, so each sub-note counts on its own
                    foreach (var subNote in note.AllNotes)
                    {
                        if (subNote.WasHit)
                        {
                            hits[sectionIndex]++;
                        }
                    }
                }
            }

            return BuildResults(totals, hits);
        }

        /// <summary>
        /// Scans a vocals track. Vocals are graded per phrase rather than per note,
        /// so a section is perfected when every phrase within it was hit.
        /// </summary>
        public static List<SectionCompletionResult> ScanVocalPhrases(IReadOnlyList<Section> sections,
            IReadOnlyList<VocalNote> phrases)
        {
            if (sections.Count == 0)
            {
                return new List<SectionCompletionResult>();
            }

            int[] totals = new int[sections.Count];
            int[] hits = new int[sections.Count];

            int sectionIndex = 0;
            foreach (var phrase in phrases)
            {
                // Percussion phrases cannot be hit and are excluded from the engine's note count
                if (phrase.IsPercussionPhrase || phrase.IsBigRockEnding)
                {
                    continue;
                }

                sectionIndex = AdvanceSectionIndex(sections, sectionIndex, phrase.Tick);

                totals[sectionIndex]++;
                if (phrase.WasHit)
                {
                    hits[sectionIndex]++;
                }
            }

            return BuildResults(totals, hits);
        }

        /// <remarks>
        /// Sections are half-open ranges of <c>[Tick, TickEnd)</c>, and both the section list and
        /// the note list are tick-sorted, so a single advancing cursor is enough.
        /// <para>
        /// The cursor only ever moves forward one section at a time because sections are contiguous
        /// by construction: <c>MoonSongLoader.LoadSections</c> back-fills each section's length from
        /// the tick of the next one, and <c>SongChart.PostProcessSections</c> fixes up the length of
        /// the last section (or generates a full set of sections when the chart has none). There are
        /// therefore no gaps between <c>Sections[i].TickEnd</c> and <c>Sections[i + 1].Tick</c> that
        /// a note could fall into.
        /// </para>
        /// Notes before the first section fall into index 0, matching practice mode's
        /// <c>FindSectionAtTime</c> fallback; notes past the last section stay in the last one.
        /// </remarks>
        private static int AdvanceSectionIndex(IReadOnlyList<Section> sections, int sectionIndex, uint tick)
        {
            while (sectionIndex < sections.Count - 1 && tick >= sections[sectionIndex].TickEnd)
            {
                sectionIndex++;
            }

            return sectionIndex;
        }

        private static List<SectionCompletionResult> BuildResults(int[] totals, int[] hits)
        {
            var results = new List<SectionCompletionResult>(totals.Length);
            for (int i = 0; i < totals.Length; i++)
            {
                results.Add(new SectionCompletionResult(i, totals[i], hits[i]));
            }

            return results;
        }
    }
}
