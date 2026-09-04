using System.Collections.Generic;
using YARG.Core.Chart;
using YARG.Core.Engine.Guitar;
using YARG.Core.Engine.Guitar.Engines;

namespace YARG.SpPathTests;

/// <summary>
/// A bot guitar engine whose Star Power activations are scripted instead of greedy, plus a
/// per-award score trace so a test can see exactly which notes and sustains were doubled.
/// <para/>
/// <b>Why the input is driven from <c>UpdateStarPower</c> and not <c>UpdateBot</c>.</b>
/// <c>RunEngineLoop</c> (<c>BaseEngine.cs:396-406</c>) runs <c>UpdateStarPower()</c> and then
/// <c>UpdateHitLogic(time)</c>, and <c>UpdateBot</c> runs inside the hit logic. So an input set
/// from <c>UpdateBot</c> is only *read* on a later pass, and on the bot's note passes that later
/// pass is whatever the caller stepped to next — usually a plain frame tick some milliseconds
/// before the next note. Setting the input at the top of <c>UpdateStarPower</c> instead makes
/// the activation land on a pass we choose, before that pass's hit logic runs.
/// <para/>
/// <b>The contract this gives.</b> "Activate at note N" means the input is raised on the first
/// engine pass whose time has reached <c>Notes[N].Time</c> while <c>NoteIndex</c> is still N —
/// which for a bot is the engine's own queued "Bot Note Time" update for note N
/// (<c>BaseEngine.Generic.cs:199-203</c>). <c>ActivateStarPower</c> therefore runs at
/// <c>CurrentTick == Notes[N].Tick</c> and <b>note N is the first doubled note</b>, matching a
/// human who taps SP exactly on note N (their input is drained before the loop, so
/// <c>UpdateStarPower</c> sees it on the same pass). <c>SpSemanticsTests</c> pins all of this.
/// <para/>
/// <c>base.UpdateBot</c> still runs and still executes its own greedy toggle
/// (<c>YargFiveFretGuitarEngine.cs:30</c>), but that assignment is overwritten at the top of the
/// next <c>UpdateStarPower</c> before anything reads it, so the greedy policy is fully replaced.
/// <para/>
/// Feeding <see cref="YARG.Core.Input.GameInput"/>s is not an option: <c>BaseEngine.Update</c>
/// skips <c>ProcessInputs</c> entirely when <c>IsBot</c> (<c>BaseEngine.cs:199-202</c>).
/// </summary>
public class ScriptedBotGuitarEngine : YargFiveFretGuitarEngine
{
    private readonly HashSet<int> _activationNoteIndices;
    private readonly bool _useStockPolicy;

    /// <param name="activationNoteIndices">
    /// Note indices at which the Star Power input should be raised. Empty (or null) means "never
    /// activate", which is the policy slice 2 verifies the no-SP scoring model against.
    /// </param>
    /// <param name="useStockPolicy">
    /// Leave the stock greedy toggle in place and script nothing. Used to get the score trace and
    /// the window log off an otherwise completely unmodified bot run.
    /// </param>
    public ScriptedBotGuitarEngine(InstrumentDifficulty<GuitarNote> chart, SyncTrack syncTrack,
        GuitarEngineParameters engineParameters, IEnumerable<int> activationNoteIndices = null,
        bool useStockPolicy = false)
        : base(chart, syncTrack, engineParameters, isBot: true)
    {
        _activationNoteIndices = activationNoteIndices is null
            ? new HashSet<int>()
            : new HashSet<int>(activationNoteIndices);
        _useStockPolicy = useStockPolicy;

        OnStarPowerStatus += OnStarPowerStatusChanged;
    }

    /// <summary>Set true once the engine has actually entered Star Power at least once.</summary>
    public bool EverActivatedStarPower { get; private set; }

    /// <summary>Every point award the engine made, in order, with the multiplier it used.</summary>
    public List<ScoreAward> Awards { get; } = new();

    /// <summary>Every Star Power window the engine actually opened.</summary>
    public List<Window> Windows { get; } = new();

    public readonly record struct ScoreAward(
        int NoteIndex, uint Tick, uint MeasureTick, int Points, int Multiplier,
        bool StarPowerActive, bool IsSustain);

    public readonly record struct Window(
        int NoteIndex, uint ActivationMeasureTick, uint EndMeasureTick, uint MeterAtActivation)
    {
        public uint Length => EndMeasureTick - ActivationMeasureTick;
    }

    private void OnStarPowerStatusChanged(bool active)
    {
        if (active)
        {
            EverActivatedStarPower = true;
            Windows.Add(new Window(NoteIndex, StarPowerTickActivationPosition,
                StarPowerTickEndPosition, EngineStats.StarPowerTickAmount));
        }
        else if (Windows.Count > 0)
        {
            // The end position can be pushed out by phrases collected while active
            // (BaseEngine.cs:543-547), so re-read it at release.
            var open = Windows[^1];
            Windows[^1] = open with { EndMeasureTick = StarPowerTickEndPosition };
        }
    }

    /// <summary>
    /// Raise the Star Power input on the pass that will hit the scripted note, before that pass's
    /// hit logic runs. See the class remarks.
    /// </summary>
    protected override void UpdateStarPower()
    {
        if (_useStockPolicy)
        {
            // Leave base.UpdateBot's greedy toggle alone: this instance is the stock bot, kept
            // only for the score trace and the window log.
            base.UpdateStarPower();
            return;
        }

        IsStarPowerInputActive =
            NoteIndex < Notes.Count &&
            _activationNoteIndices.Contains(NoteIndex) &&
            CurrentTime >= Notes[NoteIndex].Time;

        base.UpdateStarPower();
    }

    protected override void AddScore(GuitarNote note)
    {
        Awards.Add(new ScoreAward(NoteIndex, CurrentTick,
            SyncTrack.QuarterTickToMeasureTick(CurrentTick),
            POINTS_PER_NOTE * (1 + note.ChildNotes.Count), EngineStats.ScoreMultiplier,
            EngineStats.IsStarPowerActive, IsSustain: false));

        base.AddScore(note);
    }

    protected override void UpdateSustains()
    {
        int before = EngineStats.CommittedScore;
        int multiplier = EngineStats.ScoreMultiplier;
        bool spActive = EngineStats.IsStarPowerActive;

        base.UpdateSustains();

        int delta = EngineStats.CommittedScore - before;
        if (delta != 0)
        {
            Awards.Add(new ScoreAward(NoteIndex, CurrentTick,
                SyncTrack.QuarterTickToMeasureTick(CurrentTick),
                delta / multiplier, multiplier, spActive, IsSustain: true));
        }
    }
}
