namespace ChartShared.Core.Charting.Commands;

/// <summary>
/// The changes a command made to a chart's lists, step by step, to undo exactly. <see cref="Undo"/> takes the steps
/// back last first, so each index is what it was when its step was made.
/// </summary>
internal sealed class EditLog
{
    private readonly List<Action> _undo = [];

    /// <summary>Whether nothing has been changed.</summary>
    public bool IsEmpty => _undo.Count == 0;

    /// <summary>Adds <paramref name="item"/> to <paramref name="list"/>, after the items it ranks equal with.</summary>
    /// <returns>The index it went at.</returns>
    /// <exception cref="InvalidOperationException">The list holds one item per place, and already has one at its place.</exception>
    public int Place<T>(TickList<T> list, T item)
    {
        int index = list.Place(item);
        if (index < 0)
            throw new InvalidOperationException($"The list already has an item at the place of {item}.");
        _undo.Add(() => list.RemoveAt(index));
        return index;
    }

    /// <summary>Removes the item at <paramref name="index"/> of <paramref name="list"/>.</summary>
    /// <returns>The item removed.</returns>
    public T RemoveAt<T>(TickList<T> list, int index)
    {
        var item = list[index];
        list.RemoveAt(index);
        _undo.Add(() => list.Insert(index, item));
        return item;
    }

    /// <summary>
    /// Puts <paramref name="item"/> at <paramref name="index"/> of <paramref name="list"/> in place of the item there,
    /// keeping its place among the items it ranks equal with. It must rank where the item it replaces did.
    /// </summary>
    public void Replace<T>(TickList<T> list, int index, T item)
    {
        var old = list[index];
        list.RemoveAt(index);
        list.Insert(index, item);
        _undo.Add(() =>
        {
            list.RemoveAt(index);
            list.Insert(index, old);
        });
    }

    /// <summary>Adds a step of the command's own, which <paramref name="undo"/> takes back.</summary>
    public void Then(Action undo) => _undo.Add(undo);

    /// <summary>Takes back every step, last first, and forgets them.</summary>
    public void Undo()
    {
        for (int i = _undo.Count - 1; i >= 0; i--)
            _undo[i]();
        _undo.Clear();
    }
}
