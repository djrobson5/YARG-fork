// NO UnityEngine REFERENCES IN THIS FOLDER.
//
// Everything under Assets/Script/Gameplay/SpPath/ is compiled by link into
// tools/SpPathTests/SpPathTests.csproj, which has no Unity assemblies available. One stray
// `using UnityEngine;` and the verification harness stops compiling. See docs/sp-path-design.md §3.

namespace YARG.Gameplay.SpPath
{
    /// <summary>
    /// A single point award the engine makes, at the tick it is committed and with the combo
    /// multiplier that was current at that instant.
    /// </summary>
    /// <remarks>
    /// The engine commits points in exactly two places for 5-fret guitar:
    /// <list type="bullet">
    /// <item><c>GuitarEngine.AddScore(GuitarNote)</c> (<c>Guitar/GuitarEngine.cs:335-340</c>) —
    /// the note itself, at <c>note.Tick</c>.</item>
    /// <item><c>BaseEngine.UpdateSustains</c> (<c>BaseEngine.Generic.cs:898-912</c>) — the whole
    /// sustain in one lump, at its burst tick.</item>
    /// </list>
    /// Both go through <c>AddScore(int)</c> (<c>BaseEngine.Generic.cs:781-806</c>), which does
    /// <c>CommittedScore += score * ScoreMultiplier</c>. So the whole committed score is
    /// <c>Σ Points × Multiplier</c> over these events, doubled on the ones inside an SP window.
    /// </remarks>
    public readonly struct ScoreEvent
    {
        /// <summary>Index into the post-modifier note list this award belongs to.</summary>
        public readonly int NoteIndex;

        /// <summary>Quarter tick at which the points are committed.</summary>
        public readonly uint Tick;

        /// <summary>Un-multiplied point value.</summary>
        public readonly int Points;

        /// <summary>
        /// Combo multiplier at commit time, <em>without</em> the Star Power doubling —
        /// <c>min(combo / 10 + 1, MaxMultiplier)</c> (<c>BaseEngine.cs:447-450</c>).
        /// </summary>
        public readonly int Multiplier;

        /// <summary>What produced this award. Notes sort before sustain bursts at the same tick.</summary>
        public readonly ScoreEventKind Kind;

        public ScoreEvent(int noteIndex, uint tick, int points, int multiplier, ScoreEventKind kind)
        {
            NoteIndex = noteIndex;
            Tick = tick;
            Points = points;
            Multiplier = multiplier;
            Kind = kind;
        }

        public int Value => Points * Multiplier;

        public override string ToString() =>
            $"[{Tick}] note {NoteIndex} {Kind}: {Points} x{Multiplier} = {Value}";
    }

    public enum ScoreEventKind
    {
        /// <summary>
        /// Awarded inside <c>HitNote</c>, after <c>IncrementCombo()</c> and
        /// <c>UpdateMultiplier()</c> (<c>Guitar/GuitarEngine.cs:257-276</c>) — so a note is scored
        /// at the multiplier its own combo increment produced.
        /// </summary>
        Note = 0,

        /// <summary>
        /// Awarded inside <c>UpdateSustains</c>, which runs <em>after</em> <c>CheckForNoteHit</c>
        /// in the same engine pass (<c>YargFiveFretGuitarEngine.cs:228-229</c>). A sustain
        /// bursting on the same tick as a note therefore uses the multiplier that note produced.
        /// </summary>
        SustainBurst = 1,
    }
}
