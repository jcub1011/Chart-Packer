using ChartShared.Core.Charts;

namespace ChartShared.Core.Charting.Editing;

/// <summary>
/// A run of Generate fills (charter-spec C6 item 5): the fills <see cref="FillGenerator"/> proposed for the drums, drawn
/// as ghosts, each kept or dropped by the author, until the kept ones are added as one undoable step or the run is
/// cancelled. Fills are never added without the author's say. The session owns it (<see cref="EditorSession.Fills"/>).
/// </summary>
/// <remarks>
/// The proposals were worked out from the chart as it was, so any change to the chart but the run's own, or to the
/// song's settings, ends the run, and the status says so. A gesture's preview doesn't, as it's taken back or committed.
/// </remarks>
public sealed class FillProposals : PhraseProposals
{
    private const string NoDrumNotes = "The drums have no notes to propose fills over.";
    private const string TooFewStarPower = "Fills are proposed only after the second star power phrase ends, and the drums have fewer than two.";
    private const string NoCrashSpot = "No fill fits: none ends on a crash on a measure's first beat that every difficulty with notes has a note on, after the second star power phrase and clear of star power, other fills and [coda].";
    private const string NoNoteSpot = "No fill fits: none ends on a note on a measure's first beat that every difficulty with notes has, after the second star power phrase and clear of star power, other fills and [coda].";

    private readonly List<FillProposal> _proposals = [];

    internal FillProposals(EditorSession session)
        : base(session)
    {
    }

    /// <summary>The part the fills are proposed for.</summary>
    public static ChartInstrument Part => ChartInstrument.Drums;

    public override ChartInstrument? ProposedPart => IsActive ? Part : null;

    public override PhraseKind Kind => PhraseKind.DrumFill;

    /// <summary>The fills proposed, earliest first.</summary>
    public IReadOnlyList<FillProposal> Proposals => _proposals;

    private protected override string NoneKeptStatus => "No proposed fill is kept, so there's nothing to add.";

    private protected override string DroppedStatus => "The proposed fills were dropped, as the chart changed. Generate them again to see them.";

    /// <summary>
    /// Starts a run, in place of any going on: proposes fills over <paramref name="span"/>, or the whole part, every one
    /// kept. When none fits, the status says why.
    /// </summary>
    /// <returns>Whether any were proposed.</returns>
    public bool Start(FillOptions options, (long From, long To)? span)
    {
        ArgumentNullException.ThrowIfNull(options);
        Clear();
        var generated = FillGenerator.Propose(Session.Chart, Session.DrumsVariantShown, options, span);
        if (generated.Proposals.Count == 0)
        {
            Session.Status = generated.Reason switch
            {
                NoFillsReason.NoDrumNotes => NoDrumNotes,
                NoFillsReason.TooFewStarPower => TooFewStarPower,
                _ => options.EndOn == FillEnd.Crash ? NoCrashSpot : NoNoteSpot,
            };
            return false;
        }

        _proposals.AddRange(generated.Proposals);
        Begin(generated.Proposals.Select(static p => p.Fill));
        return true;
    }

    private protected override void Forget() => _proposals.Clear();
}
