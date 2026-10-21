namespace ChartShared.Core.Charting.Authoring;

/// <summary>
/// Picks the spots Generate star power proposes (<see cref="StarPowerGenerator"/>): first the phrases that charge the
/// meter for the hard passages, then the rest spread over the stretches between.
/// </summary>
/// <remarks>
/// <para><b>Charging.</b> The passages are taken most prominent first, each kept where the plan with it still works: for
/// each passage in time order, the meter is simulated (<see cref="StarPowerMeter"/>) up to it, with a player activating
/// at each passage planned before, and it's given the phrases that bring the meter to the threshold, none to two. The
/// last completes <see cref="StarPowerParameters.MinLeadMeasures"/> to <see cref="StarPowerParameters.MaxLeadMeasures"/>
/// measures of playing before the passage, best <see cref="StarPowerParameters.IdealLeadMeasures"/>; the first of two
/// starts after the activation before has run out, within <see cref="StarPowerParameters.PairSpanMeasures"/>. Of the
/// spots that fit, the best pair is taken: each pair of a few dozen spots is tried, as there are few passages and few
/// spots near each. A passage still in the star power of the one before, or that would take more than
/// <see cref="StarPowerParameters.SpikeShare"/> of the phrases, isn't planned for.</para>
/// <para><b>Spreading.</b> The phrases left are shared over the stretches of playing between the phrases there, one at a
/// time to whichever stretch would then have the most room between them, and each goes to the best spot near its even
/// share of the stretch. One that would put star power on over a passage planned for, so it can't be activated there,
/// is left for the next best spot. Then any stretch longer than <see cref="StarPowerParameters.MaxGapSeconds"/> is given
/// one in its middle, up to <see cref="StarPowerParameters.MaxExtraPhrases"/> past the count.</para>
/// <para>Each phrase is at least <see cref="StarPowerParameters.MinGapMeasures"/> from every other, those already there
/// too. Ties go to the earlier spot, so the same chart always gets the same phrases.</para>
/// </remarks>
internal sealed class StarPowerScheduler
{
    // What spreading a pair over a long lead-in costs, at the most: a pair close together, just before the passage, is best.
    private const double PairSpread = 0.5;

    private readonly StarPowerContext _c;
    private readonly StarPowerParameters _p;
    private readonly List<StarPowerCandidate> _candidates;
    private readonly List<Placed> _fixed;
    private readonly int _target;
    private readonly int _fixedInRegion;

    public StarPowerScheduler(StarPowerContext context, List<StarPowerCandidate> candidates, StarPowerOptions options)
    {
        _c = context;
        _p = context.Parameters;
        _candidates = candidates;
        var timeline = context.Timeline;
        _fixed = [];
        foreach (var phrase in context.Part.Phrases.AsSpan())
        {
            if (phrase.Kind != PhraseKind.StarPower)
                continue;
            var covered = context.Source.Notes.Between(phrase.Tick, phrase.CoverEnd - 1);
            double? complete = covered.IsEmpty ? null : timeline.MeasureAt(covered[^1].Tick);
            _fixed.Add(new Placed(phrase.Tick, phrase.CoverEnd, timeline.MeasureAt(phrase.Tick), timeline.MeasureAt(phrase.CoverEnd), complete, timeline.ActiveSecondsAt(phrase.Tick), timeline.ActiveSecondsAt(phrase.CoverEnd), null));
            if (phrase.Tick <= context.To && phrase.CoverEnd > context.From)
                _fixedInRegion++;
        }

        double active = timeline.ActiveSecondsAt(context.To) - timeline.ActiveSecondsAt(context.From);
        int count = (int)Math.Round(active / options.SpacingSeconds, MidpointRounding.AwayFromZero);
        _target = context.Span is null ? Math.Max(_p.MinPhrases, count) : Math.Max(1, count);
    }

    /// <summary>The phrases to propose, in order, charging for <paramref name="spikes"/> (most prominent first) where they can.</summary>
    public List<StarPowerProposal> Schedule(IReadOnlyList<StarPowerSpike> spikes)
    {
        int remaining = Math.Max(0, _target - _fixedInRegion);
        var picks = new List<Placed>();
        var activations = new List<double>();
        if (remaining >= 2 && spikes.Count > 0)
        {
            int budget = (int)Math.Floor(remaining * _p.SpikeShare);
            var planned = new List<StarPowerSpike>();
            foreach (var spike in spikes)
            {
                // Each passage takes a phrase or two, so once the share is spent no more can be planned for.
                if (picks.Count >= budget)
                    break;
                if (spike.StartTick <= _c.From || spike.StartTick > _c.To)
                    continue;
                if (Plan([.. planned, spike]) is not { } plan || plan.Picks.Count > budget)
                    continue;
                planned.Add(spike);
                (picks, activations) = plan;
            }
        }

        Spread(picks, activations, remaining - picks.Count);
        Close(picks, activations);
        return [.. picks.OrderBy(static p => p.Tick).Select(static p => new StarPowerProposal(p.Candidate!.Value.Phrase, p.Spike is null ? StarPowerRole.Filler : StarPowerRole.SpikeLeadIn, p.Spike))];
    }

    // The phrases that charge for each of the spikes, and where each is activated; null when one can't be charged for.
    private (List<Placed> Picks, List<double> Activations)? Plan(List<StarPowerSpike> spikes)
    {
        var timeline = _c.Timeline;
        var picks = new List<Placed>();
        var activations = new List<double>();
        foreach (var spike in spikes.OrderBy(static s => s.StartTick))
        {
            double at = timeline.MeasureAt(spike.StartTick), atActive = timeline.ActiveMeasureAt(spike.StartTick);
            var state = StarPowerMeter.StateAt(Completions(picks), activations, at, _p);
            if (state.ActiveUntil is not null)
                return null;
            int need = (int)Math.Max(0, Math.Ceiling(((_p.ActivationThreshold - state.Meter) / _p.MeterPerPhrase) - 1e-9));
            if (need > 0)
            {
                double floor = state.LastEnd ?? double.NegativeInfinity;
                var placed = _fixed.Concat(picks).ToList();
                var chosen = need == 1 ? BestSingle(at, atActive, floor, placed) : BestPair(at, atActive, floor, placed);
                if (chosen is null)
                    return null;
                picks.AddRange(chosen.Select(c => Placed.Of(c, spike.StartTick)));
            }

            activations.Add(at);
        }

        return StarPowerMeter.AllFire(Completions(picks), activations, _p) ? (picks, activations) : null;
    }

    // The best spot to complete a charge before a passage: completing within the lead, nearest the ideal lead.
    private StarPowerCandidate[]? BestSingle(double at, double atActive, double floor, List<Placed> placed)
    {
        StarPowerCandidate? best = null;
        double bestScore = double.NegativeInfinity;
        foreach (var (candidate, score) in Completing(at, atActive, floor, placed))
        {
            if (score > bestScore)
                (best, bestScore) = (candidate, score);
        }

        return best is { } b ? [b] : null;
    }

    // The best two spots to charge a passage from empty: the second completing within the lead, the first before it.
    private StarPowerCandidate[]? BestPair(double at, double atActive, double floor, List<Placed> placed)
    {
        var seconds = Completing(at, atActive, floor, placed).ToList();
        var firsts = _candidates.Where(c => c.StartMeasure >= floor && c.StartActive >= atActive - _p.PairSpanMeasures && c.CompleteMeasure < at && Clear(c, placed)).ToList();
        StarPowerCandidate[]? best = null;
        double bestScore = double.NegativeInfinity;
        foreach (var (second, secondScore) in seconds)
        {
            foreach (var first in firsts)
            {
                if (first.EndMeasure + _p.MinGapMeasures > second.StartMeasure + 1e-9)
                    continue;
                double score = first.Quality + secondScore - (PairSpread * (atActive - first.StartActive) / _p.PairSpanMeasures);
                if (score > bestScore)
                    (best, bestScore) = ([first, second], score);
            }
        }

        return best;
    }

    // The spots that can complete a charge for a passage at the measure position, each with how good it is there.
    private IEnumerable<(StarPowerCandidate Candidate, double Score)> Completing(double at, double atActive, double floor, List<Placed> placed)
    {
        foreach (var c in _candidates)
        {
            double lead = atActive - c.CompleteActive;
            if (c.CompleteMeasure >= at || lead < _p.MinLeadMeasures - 1e-9 || lead > _p.MaxLeadMeasures + 1e-9 || c.StartMeasure < floor || !Clear(c, placed))
                continue;
            yield return (c, c.Quality - (Math.Abs(lead - _p.IdealLeadMeasures) / _p.MaxLeadMeasures));
        }
    }

    // Shares count phrases over the stretches between those placed, and puts each at the best spot near its share.
    private void Spread(List<Placed> picks, List<double> activations, int count)
    {
        if (count <= 0)
            return;
        var gaps = Gaps(picks);
        var slots = new int[gaps.Count];
        for (int s = 0; s < count; s++)
        {
            int widest = -1;
            for (int g = 0; g < gaps.Count; g++)
            {
                if (widest < 0 || Room(gaps[g], g, slots[g] + 1) > Room(gaps[widest], widest, slots[widest] + 1) + 1e-9)
                    widest = g;
            }

            if (widest >= 0)
                slots[widest]++;
        }

        var aims = new List<(double Aim, double Half, double Room)>();
        for (int g = 0; g < gaps.Count; g++)
        {
            double room = Room(gaps[g], g, slots[g]);
            double first = Leads(gaps, g) ? room / 2 : room;
            for (int j = 0; j < slots[g]; j++)
                aims.Add((gaps[g].From + first + (j * room), room / 2, room));
        }

        foreach (var (aim, half, _) in aims.OrderByDescending(static a => a.Room).ThenBy(static a => a.Aim))
            TryAdd(picks, activations, aim, half);

        // The room between phrases a stretch has with n of them in it. The official charts give their first phrase early,
        // about half the spacing in, so the stretch the part starts with has half a room before its first.
        double Room((double From, double To) gap, int g, int n) => (gap.To - gap.From) / (n + (Leads(gaps, g) ? 0.5 : 1));
    }

    // Whether the stretch at g is the one the part's playing starts with, before any phrase.
    private bool Leads(List<(double From, double To)> gaps, int g) =>
        g == 0 && gaps.Count > 0 && Math.Abs(gaps[0].From - _c.Timeline.ActiveSecondsAt(_c.From)) < 1e-9;

    // Puts one phrase in the middle of each stretch longer than the longest allowed, longest first, while extras are left.
    private void Close(List<Placed> picks, List<double> activations)
    {
        int extras = 0;
        while (extras < _p.MaxExtraPhrases)
        {
            bool added = false;
            foreach (var gap in Gaps(picks).Where(g => g.To - g.From > _p.MaxGapSeconds).OrderByDescending(static g => g.To - g.From).ThenBy(static g => g.From))
            {
                double half = (gap.To - gap.From) / 2;
                if (TryAdd(picks, activations, gap.From + half, half))
                {
                    added = true;
                    break;
                }
            }

            if (!added)
                return;
            extras++;
        }
    }

    // Adds the best spot whose middle is within half of the aim, in seconds of playing, that leaves every activation
    // planned firing; whether there was one.
    private bool TryAdd(List<Placed> picks, List<double> activations, double aim, double half)
    {
        var placed = _fixed.Concat(picks).ToList();
        var near = _candidates
            .Where(c => Math.Abs(c.CenterSeconds - aim) <= half + 1e-9 && Clear(c, placed))
            .Select(c => (Candidate: c, Score: c.Quality - (Math.Abs(c.CenterSeconds - aim) / Math.Max(half, 1e-9))))
            .OrderByDescending(static c => c.Score).ThenBy(static c => c.Candidate.Phrase.Tick);
        foreach (var (candidate, _) in near)
        {
            var pick = Placed.Of(candidate, null);
            if (activations.Count > 0 && !StarPowerMeter.AllFire(Completions([.. picks, pick]), activations, _p))
                continue;
            picks.Add(pick);
            return true;
        }

        return false;
    }

    // The stretches of playing, in seconds, between the counted region's ends and the phrases placed in it.
    private List<(double From, double To)> Gaps(List<Placed> picks)
    {
        var timeline = _c.Timeline;
        double start = timeline.ActiveSecondsAt(_c.From), end = timeline.ActiveSecondsAt(_c.To);
        var gaps = new List<(double, double)>();
        double from = start;
        foreach (var placed in _fixed.Concat(picks).Where(p => p.Tick <= _c.To && p.CoverEnd > _c.From).OrderBy(static p => p.Tick))
        {
            if (placed.StartSeconds > from)
                gaps.Add((from, placed.StartSeconds));
            from = Math.Max(from, placed.EndSeconds);
        }

        if (end > from)
            gaps.Add((from, end));
        return gaps;
    }

    // Whether a spot overlaps none of the phrases placed, and is the gap from each.
    private bool Clear(StarPowerCandidate c, List<Placed> placed)
    {
        foreach (var other in placed)
        {
            if (c.Phrase.Tick < other.CoverEnd && other.Tick < c.Phrase.CoverEnd)
                return false;
            double gap = other.Tick < c.Phrase.Tick ? c.StartMeasure - other.EndMeasure : other.StartMeasure - c.EndMeasure;
            if (gap < _p.MinGapMeasures - 1e-9)
                return false;
        }

        return true;
    }

    // Where every phrase there and picked is completed, as measure positions.
    private IEnumerable<double> Completions(List<Placed> picks) =>
        _fixed.Concat(picks).Where(static p => p.Complete is not null).Select(static p => p.Complete!.Value);

    /// <summary>A phrase there or picked: where it falls, where it's completed (null when it covers no note), and what it charges for.</summary>
    private readonly record struct Placed(long Tick, long CoverEnd, double StartMeasure, double EndMeasure, double? Complete, double StartSeconds, double EndSeconds, long? Spike)
    {
        public StarPowerCandidate? Candidate { get; init; }

        public static Placed Of(StarPowerCandidate c, long? spike) =>
            new(c.Phrase.Tick, c.Phrase.CoverEnd, c.StartMeasure, c.EndMeasure, c.CompleteMeasure, c.StartSeconds, c.EndSeconds, spike) { Candidate = c };
    }
}
