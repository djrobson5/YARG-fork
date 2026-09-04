using System.Collections.Generic;
using YARG.Core.Chart;
using YARG.Core.Engine.Guitar;
using YARG.Core.Engine.Guitar.Engines;

namespace YARG.SpPathTests;

/// <summary>
/// A bot guitar engine whose Star Power activations are scripted instead of greedy.
/// <para/>
/// <c>YargFiveFretGuitarEngine.UpdateBot</c> (<c>Guitar/Engines/YargFiveFretGuitarEngine.cs:23</c>)
/// opens with <c>IsStarPowerInputActive = CanStarPowerActivate &amp;&amp; !IsStarPowerInputActive;</c>,
/// i.e. it toggles the input every bot tick and fires the instant the bar reaches half. Calling
/// <c>base.UpdateBot</c> and then overwriting <c>IsStarPowerInputActive</c> replaces that policy
/// while keeping the perfect-play note hitting.
/// <para/>
/// This is legal because <c>UpdateBot</c> is <c>protected virtual</c> and not sealed, and
/// <c>IsStarPowerInputActive</c> is <c>{ get; protected set; }</c> (<c>BaseEngine.cs:89</c>).
/// Feeding <see cref="YARG.Core.Input.GameInput"/>s is not an option: <c>BaseEngine.Update</c>
/// skips <c>ProcessInputs</c> entirely when <c>IsBot</c> (<c>BaseEngine.cs:199-202</c>).
/// <para/>
/// Ordering note: <c>RunEngineLoop</c> runs <c>UpdateStarPower()</c> before <c>UpdateHitLogic()</c>
/// (<c>BaseEngine.cs:400-405</c>), and <c>UpdateBot</c> runs inside the hit logic, so an
/// activation requested at note N takes effect on the *next* engine loop pass. The engine
/// re-runs the loop after a note is hit (<c>ReRunHitLogic</c>), which is what makes
/// "activate at note N" land with N itself doubled — the scripted set is validated against a
/// real run in slice 3, not assumed here.
/// </summary>
public class ScriptedBotGuitarEngine : YargFiveFretGuitarEngine
{
    private readonly HashSet<int> _activationNoteIndices;

    /// <summary>
    /// Note indices at which the Star Power input should be held. Empty means "never
    /// activate", which is the policy slice 2 verifies the no-SP scoring model against.
    /// </summary>
    public ScriptedBotGuitarEngine(InstrumentDifficulty<GuitarNote> chart, SyncTrack syncTrack,
        GuitarEngineParameters engineParameters, IEnumerable<int> activationNoteIndices = null)
        : base(chart, syncTrack, engineParameters, isBot: true)
    {
        _activationNoteIndices = activationNoteIndices is null
            ? new HashSet<int>()
            : new HashSet<int>(activationNoteIndices);
    }

    /// <summary>Set true once the engine has actually entered Star Power at least once.</summary>
    public bool EverActivatedStarPower { get; private set; }

    protected override void UpdateBot(double time)
    {
        base.UpdateBot(time);

        // Once every note has been consumed there is no NoteIndex left to script against.
        // Clear the input rather than leaving it alone: a held input would otherwise latch past
        // the last note and could still trigger an activation from a bar filled by a trailing
        // phrase.
        if (!IsBot || NoteIndex >= Notes.Count)
        {
            IsStarPowerInputActive = false;
            return;
        }

        IsStarPowerInputActive = _activationNoteIndices.Contains(NoteIndex);

        if (EngineStats.IsStarPowerActive)
        {
            EverActivatedStarPower = true;
        }
    }
}
