namespace ChartShared.Core.Charts;

/// <summary>
/// Lists used again from one chart read to the next. A track's events and notes fill lists whose arrays are often past
/// the large object heap's threshold, and made anew for every chart, each one brought a full garbage collection
/// nearer; many charts read at once, as the Validator reads them, spent more time collecting than reading. A list given
/// back keeps its array, so the next chart reads into it.
/// </summary>
/// <remarks>
/// <para>A list rented must be given back once nothing uses it, and not used after; one never given back is only
/// collected, as any list is. Lists of types holding references aren't kept, as clearing one would walk its array.</para>
/// <para>What's kept is capped, and after each full collection a list unused for a while is let go, as
/// <see cref="System.Buffers.ArrayPool{T}.Shared"/> lets its arrays go, so a burst of reading doesn't hold its memory
/// for the rest of the session.</para>
/// </remarks>
internal static class ListPool<T>
{
    // How many lists are kept, and how many items the largest one kept may hold.
    private const int MaxKept = 128;
    private const int MaxCapacity = 1 << 20;

    // How long a list kept may go unused before a full collection lets it go.
    private const long IdleMilliseconds = 10_000;

    private static readonly Lock Gate = new();
    // In the order given back, the last at the end.
    private static readonly List<(List<T> List, long ReturnedAt)> Kept = [];
    private static readonly bool CanKeep = !System.Runtime.CompilerServices.RuntimeHelpers.IsReferenceOrContainsReferences<T>();

    static ListPool() => _ = new TrimAfterFullCollections();

    /// <summary>
    /// A list, empty, with room for <paramref name="capacity"/> items: the smallest given back that has the room, so a
    /// large one is kept for a large track, or else the largest, which grows the least.
    /// </summary>
    public static List<T> Rent(int capacity = 0)
    {
        if (CanKeep)
        {
            lock (Gate)
            {
                int best = -1;
                for (int i = Kept.Count - 1; i >= 0; i--)
                {
                    int has = Kept[i].List.Capacity;
                    if (best < 0)
                    {
                        best = i;
                        continue;
                    }

                    int bestHas = Kept[best].List.Capacity;
                    if (bestHas >= capacity ? has >= capacity && has < bestHas : has > bestHas)
                        best = i;
                }

                if (best >= 0)
                {
                    var list = Kept[best].List;
                    Kept.RemoveAt(best);
                    list.EnsureCapacity(capacity);
                    return list;
                }
            }
        }

        return new List<T>(capacity);
    }

    /// <summary>Gives <paramref name="list"/> back, to be rented again. Nothing may use it after.</summary>
    public static void Return(List<T> list)
    {
        if (!CanKeep || list.Capacity > MaxCapacity)
            return;

        list.Clear();
        lock (Gate)
        {
            if (Kept.Count < MaxKept)
                Kept.Add((list, Environment.TickCount64));
        }
    }

    // Lets go of the lists unused for a while.
    private static void Trim()
    {
        long now = Environment.TickCount64;
        lock (Gate)
            Kept.RemoveAll(k => now - k.ReturnedAt >= IdleMilliseconds);
    }

    /// <summary>
    /// Trims the pool each time it's finalized, and lives on to be finalized again: once it's reached the oldest
    /// generation, that's after each full collection.
    /// </summary>
    private sealed class TrimAfterFullCollections
    {
        ~TrimAfterFullCollections()
        {
            if (Environment.HasShutdownStarted)
                return;

            Trim();
            GC.ReRegisterForFinalize(this);
        }
    }
}
