using ChartShared.Core.Charting.Authoring;
using ChartShared.Core.Charts;

namespace ChartShared.Core.Charting.Editing;

/// <summary>
/// A run of Generate star power (charter-spec C8 item 1): the phrases <see cref="StarPowerGenerator"/> proposed for a
/// 5-fret or keys part, drawn as ghosts, each kept or dropped by the author, until the kept ones are added as one undoable
/// step or the run is cancelled. The session owns it (<see cref="EditorSession.StarPower"/>).
/// </summary>
/// <remarks>
/// Over the whole part, it's offered only while the part has no star power, as the author's phrases are theirs to keep;
/// over a span it proposes beside those there, which it keeps clear of and never removes. The proposals were worked out
/// from the chart as it was, so any change to the chart but the run's own, or to the song's settings, ends the run.
/// </remarks>
public sealed class StarPowerProposals : PhraseProposals
{
    private const string NotFiveFret = "Star power is generated only for 5-fret and keys parts.";
    private const string NoNotes = "The part has no notes to propose star power over.";
    private const string HasStarPower = "The part has star power already. Select a span to propose more beside it.";
    private const string NoSpot = "No star power phrase fits: none has enough notes clear of sections, solos' edges and the star power there, early enough for it to be used.";

    private readonly List<StarPowerProposal> _proposals = [];
    private ChartInstrument? _part;

    internal StarPowerProposals(EditorSession session)
        : base(session)
    {
    }

    public override ChartInstrument? ProposedPart => _part;

    public override PhraseKind Kind => PhraseKind.StarPower;

    /// <summary>The phrases proposed, earliest first, with why each was.</summary>
    public IReadOnlyList<StarPowerProposal> Proposals => _proposals;

    private protected override string NoneKeptStatus => "No proposed star power phrase is kept, so there's nothing to add.";

    private protected override string DroppedStatus => "The proposed star power was dropped, as the chart changed. Generate it again to see it.";

    /// <summary>
    /// Starts a run, in place of any going on: proposes star power for <paramref name="part"/> over
    /// <paramref name="span"/>, or the whole part when it has none, every phrase kept. When none fits, the status says why.
    /// </summary>
    /// <returns>Whether any were proposed.</returns>
    public bool Start(ChartInstrument part, StarPowerOptions options, (long From, long To)? span)
    {
        ArgumentNullException.ThrowIfNull(options);
        Clear();
        if (span is null && Session.Chart.PartOf(part) is { } whole && StarPowerGenerator.CanGenerate(whole.Kind) && StarPowerGenerator.HasStarPower(whole))
        {
            Session.Status = HasStarPower;
            return false;
        }

        var generated = StarPowerGenerator.Propose(Session.Chart, part, options, Session.ReadOptions, span);
        if (generated.Proposals.Count == 0)
        {
            Session.Status = generated.Reason switch
            {
                NoStarPowerReason.NotFiveFret => NotFiveFret,
                NoStarPowerReason.NoNotes => NoNotes,
                _ => NoSpot,
            };
            return false;
        }

        _part = part;
        _proposals.AddRange(generated.Proposals);
        Begin(generated.Proposals.Select(static p => p.Phrase));
        return true;
    }

    private protected override void Forget()
    {
        _proposals.Clear();
        _part = null;
    }
}
