using System;
using System.Collections.Generic;
using YARG.Assets.Script.Helpers;
using YARG.Core.Chart;
using YARG.Scores;

namespace YARG.Gameplay.HUD
{
    /// <summary>
    /// How a single block of the in-game section strip stands right now.
    /// </summary>
    /// <remarks>
    /// This is the live counterpart of <c>SectionCompletionState</c>, which describes a section
    /// after the run is over. The two are deliberately separate: the strip has a "clean so far"
    /// state that only exists while the song is playing.
    /// </remarks>
    public enum SectionStripBlockState
    {
        /// <summary>
        /// Already perfected in an earlier run. Nothing this run can do to it, either way.
        /// </summary>
        PerfectedEarlier,

        /// <summary>
        /// Never perfected, and not yet reached this run.
        /// </summary>
        Needed,

        /// <summary>
        /// Never perfected, reached this run, and nothing missed in it so far.
        /// </summary>
        Clean,

        /// <summary>
        /// Never perfected, and a note in it was missed this run.
        /// </summary>
        Dropped,
    }

    /// <summary>
    /// One player's live view of the chart's sections: which ones are already banked, which one
    /// the song is currently in, and which ones this run has already lost.
    /// </summary>
    /// <remarks>
    /// Built once at song start and driven from two places afterwards: the song clock (via
    /// <see cref="UpdateSongTime"/>) and the player's note-missed path (via
    /// <see cref="OnNoteMissed"/>). It owns no Unity objects, so the strip that draws it is free
    /// to be created, hidden, or destroyed independently.
    /// <para>
    /// Only sections that contain at least one note for the player's instrument get a block, so
    /// the block list matches the score screen's strip and the "N of M" denominator. The cursor
    /// still walks the full section list, since a section with no notes is still time the song
    /// spends somewhere.
    /// </para>
    /// </remarks>
    public class SectionStripState
    {
        private readonly IReadOnlyList<Section> _sections;

        private readonly SectionStripBlockState[] _blockStates;
        private readonly string[]                 _blockNames;

        /// <summary>
        /// The number of notes each block's section contains, straight from the pre-run scan, and
        /// how many of them have been hit this run.
        /// </summary>
        private readonly int[] _blockTotals;
        private readonly int[] _blockHits;

        /// <summary>
        /// The block each section maps to, or -1 for a section with no notes on this instrument.
        /// </summary>
        private readonly int[] _sectionToBlock;

        /// <summary>
        /// The section the song clock is currently in. Walks the full section list, blocks or not.
        /// </summary>
        private int _sectionCursor;

        /// <summary>
        /// Set when the cursor has been parked on a section the song clock has not reached yet, so
        /// that the section is entered when the clock arrives rather than when the cursor moved.
        /// </summary>
        /// <remarks>
        /// Only a rewind does that: the highlight has to sit on the target for the whole lead-in,
        /// while the target itself still has to look unplayed until it is actually replayed
        /// (<c>docs/rewind-design.md</c>, "Rule table: HUD and highway elements").
        /// </remarks>
        private bool _cursorEntryPending;

        public int BlockCount => _blockStates.Length;

        /// <summary>
        /// The block the song is currently in, or -1 before the first section with any notes.
        /// </summary>
        /// <remarks>
        /// While the song is inside a section that has no notes on this instrument, the highlight
        /// stays on the last block it was on rather than disappearing; those sections have no
        /// block of their own to move to.
        /// </remarks>
        public int CurrentBlockIndex { get; private set; } = -1;

        /// <summary>
        /// Raised with the index of a block whose state changed.
        /// </summary>
        public event Action<int> BlockStateChanged;

        /// <summary>
        /// Raised with the index of a block whose hit count changed.
        /// </summary>
        /// <remarks>
        /// Separate from <see cref="BlockStateChanged"/> because progress moves far more often
        /// than state does, and only the label cares about it.
        /// </remarks>
        public event Action<int> BlockProgressChanged;

        /// <summary>
        /// Raised with the new value of <see cref="CurrentBlockIndex"/>.
        /// </summary>
        public event Action<int> CurrentBlockChanged;

        /// <summary>
        /// Builds the state for one player.
        /// </summary>
        /// <param name="results">
        /// A fresh scan of the player's own note track, from <c>BasePlayer.ScanSectionCompletion</c>.
        /// Only the note totals are read, so this must be taken before the song starts, while
        /// nothing has been hit yet. Using the scanner rather than counting notes here keeps
        /// "which sections are applicable" defined in exactly one place.
        /// </param>
        /// <param name="perfectedEarlier">
        /// The section indices already banked from previous runs, from
        /// <c>ScoreContainer.GetCompletedSections</c>.
        /// </param>
        /// <returns>
        /// The state, or <c>null</c> if no section on this chart has any notes for this player,
        /// in which case there is nothing to show.
        /// </returns>
        public static SectionStripState Create(IReadOnlyList<Section> sections,
            IReadOnlyList<SectionCompletionResult> results, ICollection<int> perfectedEarlier)
        {
            if (sections is null || sections.Count == 0 || results is null)
            {
                return null;
            }

            var sectionToBlock = new int[sections.Count];
            var blockStates = new List<SectionStripBlockState>();
            var blockNames = new List<string>();
            var blockTotals = new List<int>();

            for (int i = 0; i < sections.Count; i++)
            {
                sectionToBlock[i] = -1;
            }

            foreach (var result in results)
            {
                if (result.NotesTotal <= 0 || result.SectionIndex >= sections.Count)
                {
                    // A section with no notes can never be perfected, so it gets no block
                    continue;
                }

                sectionToBlock[result.SectionIndex] = blockStates.Count;
                blockStates.Add(perfectedEarlier != null && perfectedEarlier.Contains(result.SectionIndex)
                    ? SectionStripBlockState.PerfectedEarlier
                    : SectionStripBlockState.Needed);
                blockNames.Add(PracticeSectionHelper.ParseSectionName(sections[result.SectionIndex].Name));
                blockTotals.Add(result.NotesTotal);
            }

            if (blockStates.Count == 0)
            {
                return null;
            }

            return new SectionStripState(sections, sectionToBlock, blockStates.ToArray(),
                blockNames.ToArray(), blockTotals.ToArray());
        }

        private SectionStripState(IReadOnlyList<Section> sections, int[] sectionToBlock,
            SectionStripBlockState[] blockStates, string[] blockNames, int[] blockTotals)
        {
            _sections = sections;
            _sectionToBlock = sectionToBlock;
            _blockStates = blockStates;
            _blockNames = blockNames;
            _blockTotals = blockTotals;
            _blockHits = new int[blockTotals.Length];

            // Notes before the first section belong to section 0, so the song is considered to be
            // in it from the very beginning. This matches the scanner's tick convention.
            EnterSection(0);
        }

        public SectionStripBlockState GetBlockState(int blockIndex) => _blockStates[blockIndex];

        /// <summary>
        /// The state of the block belonging to a <i>chart section</i> index, for surfaces that
        /// walk the chart's sections rather than the strip's blocks.
        /// </summary>
        /// <returns>
        /// <c>false</c> when that section has no block, which means it has no notes for this
        /// player and can never be perfected. Such a section has no result to show.
        /// </returns>
        public bool TryGetSectionState(int sectionIndex, out SectionStripBlockState state)
        {
            if (sectionIndex < 0 || sectionIndex >= _sectionToBlock.Length)
            {
                state = default;
                return false;
            }

            int block = _sectionToBlock[sectionIndex];
            if (block < 0)
            {
                state = default;
                return false;
            }

            state = _blockStates[block];
            return true;
        }

        public string GetBlockName(int blockIndex) => _blockNames[blockIndex];

        /// <summary>
        /// How far into its section this run has got: the number of notes hit in it so far, and
        /// the number it contains.
        /// </summary>
        /// <remarks>
        /// Both figures use the engine's own chord semantics, since the total comes straight from
        /// the scan and every hit is worth exactly one of it. The two therefore meet exactly when
        /// the section's last note is hit. The total is never zero: a section with no notes gets
        /// no block.
        /// </remarks>
        public (int Hit, int Total) GetSectionProgress(int blockIndex)
            => (_blockHits[blockIndex], _blockTotals[blockIndex]);

        /// <summary>
        /// Advances the current section for the given song time.
        /// </summary>
        /// <remarks>
        /// The same advancing cursor as <c>PracticeHud</c>: sections are contiguous, so crossing
        /// <c>TimeEnd</c> is all it takes. The cursor stops on the last section rather than
        /// walking off the end, so the strip keeps a current block until the song is over.
        /// <para>
        /// Only the section the cursor lands in is entered. Frame to frame the cursor moves at
        /// most one section (sections are contiguous and far longer than a frame), so nothing is
        /// skipped during normal play; the loop only runs more than once when the song time
        /// jumps, and a jumped-over section was never played and must not be promoted to clean.
        /// The trade-off is that a seek lands mid-section and the section it lands in still turns
        /// clean even though its earlier notes were skipped. That is harmless in practice: seeking
        /// forward in a full-song run means a rewind-and-resume, which is bounded and already
        /// invalidates scores once it goes too far.
        /// </para>
        /// <para>
        /// The cursor never regresses on its own, so a pause-rewind that crosses back over a section
        /// boundary leaves the highlight on the later section until the song time catches up.
        /// That is cosmetic only: the notes replayed over that stretch were already resolved and
        /// are not dispatched a second time, so no block's state or progress rides on it. A
        /// section rewind is the one thing that does move it backwards, deliberately, through
        /// <see cref="RewindTo"/>.
        /// </para>
        /// </remarks>
        public void UpdateSongTime(double songTime)
        {
            int previousCursor = _sectionCursor;

            while (_sectionCursor < _sections.Count - 1 && songTime >= _sections[_sectionCursor].TimeEnd)
            {
                _sectionCursor++;
            }

            if (_sectionCursor != previousCursor)
            {
                _cursorEntryPending = false;
                EnterSection(_sectionCursor);
                return;
            }

            // A rewind parks the cursor on its target before the clock gets there, so the
            // highlight is already on it through the lead-in. The section is only entered - and so
            // only turns clean - once the clock actually arrives. Section 0 is the deliberate
            // exception: it owns everything before the first marker, so it is entered at the
            // landing rather than at the marker, exactly as the constructor enters it at song
            // start rather than at Sections[0].Time.
            if (_cursorEntryPending &&
                (_sectionCursor == 0 || songTime >= _sections[_sectionCursor].Time))
            {
                _cursorEntryPending = false;
                EnterSection(_sectionCursor);
            }
        }

        /// <summary>
        /// Takes the strip back to how it stood when the run first entered the section at
        /// <paramref name="sectionIndex"/>, for a mid-song rewind to it.
        /// </summary>
        /// <remarks>
        /// Blocks before the target are left exactly as they are: the surviving timeline did not
        /// change, so neither did their state or their progress. That is only true because the
        /// re-simulation is not allowed to feed them a second time - <see cref="OnNoteMissed"/> is
        /// idempotent, but <see cref="OnNoteHit"/> adds, so replayed hits would roughly double
        /// every earlier block's percent (<c>docs/rewind-design.md</c>, "Ordered seek checklist"
        /// step 6). <c>BasePlayer.NotifySectionNoteHit</c> holds that feed off for the duration.
        /// <para>
        /// The target and everything after it go back to the unplayed look. A block perfected in
        /// an earlier run keeps its state, since it is banked and not this run's to take away.
        /// A target section with no notes of its own has no block to reset, and the highlight
        /// stays on the last block before it - the same thing the cursor does when it walks into
        /// such a section normally.
        /// </para>
        /// <para>
        /// End-of-song credit rides on none of this: <c>SectionCompletionScanner</c> reads
        /// <c>Note.WasHit</c>, which the engine rebuild clears and the re-simulation re-establishes.
        /// </para>
        /// </remarks>
        public void RewindTo(int sectionIndex)
        {
            // Create refuses an empty section list, so there is always at least one section here.
            sectionIndex = Math.Clamp(sectionIndex, 0, _sections.Count - 1);

            // Blocks are numbered in section order, so the first one at or after the target and
            // every block after it is exactly the set to clear. -1 means no section from the
            // target on has a block, and there is nothing to clear at all.
            int firstBlock = FirstBlockFrom(sectionIndex);
            if (firstBlock >= 0)
            {
                for (int block = firstBlock; block < _blockStates.Length; block++)
                {
                    if (_blockHits[block] != 0)
                    {
                        _blockHits[block] = 0;
                        BlockProgressChanged?.Invoke(block);
                    }

                    var state = _blockStates[block];
                    if (state == SectionStripBlockState.Clean || state == SectionStripBlockState.Dropped)
                    {
                        _blockStates[block] = SectionStripBlockState.Needed;
                        BlockStateChanged?.Invoke(block);
                    }
                }
            }

            _sectionCursor = sectionIndex;
            _cursorEntryPending = true;

            int current = LastBlockAtOrBefore(sectionIndex);
            if (current == CurrentBlockIndex)
            {
                return;
            }

            CurrentBlockIndex = current;
            CurrentBlockChanged?.Invoke(current);
        }

        /// <summary>
        /// The first block belonging to a section at or after <paramref name="sectionIndex"/>, or
        /// -1 when no section from there on has one.
        /// </summary>
        private int FirstBlockFrom(int sectionIndex)
        {
            for (int i = sectionIndex; i < _sectionToBlock.Length; i++)
            {
                if (_sectionToBlock[i] >= 0)
                {
                    return _sectionToBlock[i];
                }
            }

            return -1;
        }

        /// <summary>
        /// The block of the nearest section at or before <paramref name="sectionIndex"/> that has
        /// one, or -1 when none does. This is where the cursor would have left the highlight had
        /// it walked to the target the ordinary way.
        /// </summary>
        private int LastBlockAtOrBefore(int sectionIndex)
        {
            for (int i = sectionIndex; i >= 0; i--)
            {
                if (_sectionToBlock[i] >= 0)
                {
                    return _sectionToBlock[i];
                }
            }

            return -1;
        }

        /// <summary>
        /// Marks the section containing the given tick as dropped for this run.
        /// </summary>
        /// <remarks>
        /// A section already perfected in an earlier run is left alone: a miss costs this run,
        /// not the banked completion.
        /// <para>
        /// The section is looked up from the tick rather than walked to with a cursor. Notes do
        /// not reach here in tick order: <c>BaseEngine.SkipPreviousNotes</c> dispatches the misses
        /// for notes it steps over in decreasing tick order, and a lane auto-hit can arrive after
        /// a later note has already resolved. A forward-only cursor would blame the wrong section
        /// in both cases, and could keep adding progress to a section after the miss that dropped
        /// it. The lookup is a binary search over a list that is built once, so it costs nothing
        /// worth a cursor.
        /// </para>
        /// </remarks>
        public void OnNoteMissed(uint tick)
        {
            int section = SectionCompletionScanner.FindSectionIndex(_sections, tick);

            int block = _sectionToBlock[section];
            if (block < 0)
            {
                return;
            }

            var state = _blockStates[block];
            if (state != SectionStripBlockState.Needed && state != SectionStripBlockState.Clean)
            {
                return;
            }

            _blockStates[block] = SectionStripBlockState.Dropped;
            BlockStateChanged?.Invoke(block);
        }

        /// <summary>
        /// Adds <paramref name="count"/> hit notes to the section containing the given tick.
        /// </summary>
        /// <remarks>
        /// A section perfected in an earlier run still counts its hits, so the progress is there
        /// for any later surface that wants it; the strip simply shows no percent for a section
        /// that has nothing left to earn.
        /// <para>
        /// The section is looked up from the tick for the same reason as in
        /// <see cref="OnNoteMissed"/>: hits do not arrive in tick order either.
        /// </para>
        /// </remarks>
        public void OnNoteHit(uint tick, int count)
        {
            if (count <= 0)
            {
                return;
            }

            int section = SectionCompletionScanner.FindSectionIndex(_sections, tick);

            int block = _sectionToBlock[section];
            if (block < 0)
            {
                return;
            }

            // The total is what the scanner counted before the run and every hit is worth one of
            // it, so this clamp should never bite. It is here so that a future accounting
            // mismatch surfaces as a percent that stops at 100 rather than one that runs past it.
            int hits = Math.Min(_blockHits[block] + count, _blockTotals[block]);
            if (hits == _blockHits[block])
            {
                return;
            }

            _blockHits[block] = hits;
            BlockProgressChanged?.Invoke(block);
        }

        private void EnterSection(int sectionIndex)
        {
            int block = _sectionToBlock[sectionIndex];
            if (block < 0)
            {
                return;
            }

            if (_blockStates[block] == SectionStripBlockState.Needed)
            {
                // Clean until proven otherwise; a miss inside it is what takes this away
                _blockStates[block] = SectionStripBlockState.Clean;
                BlockStateChanged?.Invoke(block);
            }

            if (CurrentBlockIndex == block)
            {
                return;
            }

            CurrentBlockIndex = block;
            CurrentBlockChanged?.Invoke(block);
        }
    }
}
