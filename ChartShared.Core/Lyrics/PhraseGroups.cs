using ChartShared.Core.Charting;

namespace ChartShared.Core.Lyrics;

/// <summary>Groups a vocal part's lyrics or notes into the lines a lyrics file writes: one to a phrase.</summary>
internal static class PhraseGroups
{
    /// <summary>
    /// <paramref name="items"/>, in tick order, grouped by the phrase of <paramref name="phrases"/> each is in, with the
    /// phrase, or null for a run of them outside every phrase, which is a line of its own.
    /// </summary>
    public static List<(VocalPhraseEvent? Phrase, List<T> Items)> By<T>(IEnumerable<T> items, Func<T, long> tickOf, IEnumerable<VocalPhraseEvent> phrases)
    {
        var lines = new List<(VocalPhraseEvent?, List<T>)>();
        var ordered = phrases.OrderBy(static p => p.Tick).ToList();
        int at = 0;

        // The phrase the line being filled is of, or -1 for items outside every phrase.
        int current = -2;
        foreach (var item in items)
        {
            long tick = tickOf(item);
            while (at < ordered.Count && ordered[at].EndTick <= tick)
                at++;
            int phrase = at < ordered.Count && ordered[at].Tick <= tick ? at : -1;
            if (phrase != current || lines.Count == 0)
            {
                lines.Add((phrase >= 0 ? ordered[phrase] : null, []));
                current = phrase;
            }

            lines[^1].Item2.Add(item);
        }

        return lines;
    }
}
