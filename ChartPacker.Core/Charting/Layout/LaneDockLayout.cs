using ChartPacker.Core.Charts;

namespace ChartPacker.Core.Charting.Layout;

/// <summary>Which way a <see cref="DockSplit"/> lays out its children.</summary>
public enum DockOrientation
{
    /// <summary>Side by side, left to right.</summary>
    Horizontal,

    /// <summary>Stacked, top to bottom.</summary>
    Vertical,
}

/// <summary>The side of a lane, or of the whole dock, that a lane is docked against.</summary>
public enum DockEdge
{
    Left,
    Right,
    Top,
    Bottom,
}

/// <summary>A lane, or a split of several, in a <see cref="LaneDockLayout"/>.</summary>
public abstract class DockNode
{
    private protected DockNode()
    {
    }

    /// <summary>The split this is in, or null for the layout's root.</summary>
    public DockSplit? Parent { get; internal set; }
}

/// <summary>One lane of the dock, showing <see cref="Part"/>.</summary>
public sealed class DockLane : DockNode
{
    internal DockLane(int id, LanePart part)
    {
        Id = id;
        Part = part;
    }

    /// <summary>Tells the lane apart from the others in its layout, for as long as it's open.</summary>
    public int Id { get; }

    public LanePart Part { get; internal set; }
}

/// <summary>Two or more nodes side by side or stacked, each given a share of the room in proportion to its weight.</summary>
public sealed class DockSplit : DockNode
{
    internal DockSplit(DockOrientation orientation)
    {
        Orientation = orientation;
    }

    public DockOrientation Orientation { get; }

    internal List<DockNode> ChildList { get; } = [];

    internal List<double> WeightList { get; } = [];

    /// <summary>Left to right, or top to bottom.</summary>
    public IReadOnlyList<DockNode> Children => ChildList;

    /// <summary>Each child's share of the room, relative to the others: positive, one per child.</summary>
    public IReadOnlyList<double> Weights => WeightList;

    internal void Insert(int index, DockNode child, double weight)
    {
        ChildList.Insert(index, child);
        WeightList.Insert(index, weight);
        child.Parent = this;
    }
}

/// <summary>
/// How the Editor's lanes are arranged: a tree of splits whose leaves are the lanes, like Visual Studio's docked tool
/// windows (charter-spec C3). A lane can be docked against any edge of another lane, or of the whole dock, and closed.
/// Each part is shown in at most one lane.
/// </summary>
/// <remarks>
/// After every change the tree is kept in its simplest shape: no split has a single child, and no split runs the same
/// way as the split it's in, whose children it's merged into. UI-free, so the Editor's view only draws it.
/// </remarks>
public sealed class LaneDockLayout
{
    /// <summary>The share of the dock a lane docked against one of its edges takes.</summary>
    public const double EdgeShare = 0.25;

    private int _nextId = 1;

    /// <summary>The whole dock: a lane, a split, or null when every lane is closed.</summary>
    public DockNode? Root { get; private set; }

    /// <summary>Raised when lanes are added, moved, closed or change part. Not raised by <see cref="SetWeights"/>.</summary>
    public event EventHandler? Changed;

    /// <summary>The open lanes, in reading order: left to right, then top to bottom within each split.</summary>
    public IEnumerable<DockLane> Lanes => Root is null ? [] : LanesOf(Root);

    /// <summary>Three Expert highways in a row (guitar, bass, drums) with Vocals the full width below them.</summary>
    public static LaneDockLayout CreateDefault()
    {
        var layout = new LaneDockLayout();
        var row = new DockSplit(DockOrientation.Horizontal);
        foreach (var instrument in new[] { ChartInstrument.Guitar, ChartInstrument.Bass, ChartInstrument.Drums })
            row.Insert(row.ChildList.Count, layout.NewLane(LanePart.Highway(instrument, ChartDifficulty.Expert)), 1);

        var root = new DockSplit(DockOrientation.Vertical);
        root.Insert(0, row, 3);
        root.Insert(1, layout.NewLane(LanePart.Vocals(VocalTrack.Vocals)), 1);
        layout.Root = root;
        layout.Normalize();
        return layout;
    }

    /// <summary>Whether a lane shows <paramref name="part"/>.</summary>
    public bool Contains(LanePart part) => Lanes.Any(l => l.Part == part);

    /// <summary>The lane with <paramref name="id"/>, or null when it isn't open.</summary>
    public DockLane? Find(int id) => Lanes.FirstOrDefault(l => l.Id == id);

    /// <summary>
    /// Opens a lane showing <paramref name="part"/>. A highway goes to the right of the last highway, as wide as the
    /// lanes beside it, or above the vocals when there are no highways; a vocal part goes the full width along the
    /// bottom.
    /// </summary>
    /// <returns>The new lane, or null when a lane already shows <paramref name="part"/>.</returns>
    public DockLane? Add(LanePart part)
    {
        if (Contains(part))
            return null;

        var lane = NewLane(part);
        var lastHighway = Lanes.LastOrDefault(l => !l.Part.IsVocal);
        if (part.IsVocal || Root is null)
        {
            Insert(lane, null, DockEdge.Bottom);
        }
        else if (lastHighway is null)
        {
            // With only vocals open, a highway goes above them, the full width, leaving them their EdgeShare.
            double weight = (1 - EdgeShare) / EdgeShare;
            if (Root is DockSplit { Orientation: DockOrientation.Vertical } column)
                column.Insert(0, lane, column.WeightList.Sum() * weight);
            else
                Wrap(Root, lane, DockOrientation.Vertical, laneAfter: false, existingWeight: 1 / weight);
        }
        else if (lastHighway.Parent is { Orientation: DockOrientation.Horizontal } row)
        {
            row.Insert(row.ChildList.IndexOf(lastHighway) + 1, lane, row.WeightList.Average());
        }
        else
        {
            Insert(lane, lastHighway, DockEdge.Right);
        }

        Normalize();
        OnChanged();
        return lane;
    }

    /// <summary>
    /// Docks <paramref name="lane"/> against <paramref name="edge"/> of <paramref name="target"/>, taking half of its
    /// room, or against that edge of the whole dock when <paramref name="target"/> is null, taking
    /// <see cref="EdgeShare"/> of it. Docking a lane against itself does nothing.
    /// </summary>
    public void Move(DockLane lane, DockLane? target, DockEdge edge)
    {
        RequireOpen(lane);
        if (target is not null)
            RequireOpen(target);
        if (target == lane)
            return;

        Detach(lane);
        Normalize();
        Insert(lane, target, edge);
        Normalize();
        OnChanged();
    }

    /// <summary>Closes <paramref name="lane"/>. The lanes beside it share its room, in proportion to their own.</summary>
    public void Close(DockLane lane)
    {
        RequireOpen(lane);
        Detach(lane);
        Normalize();
        OnChanged();
    }

    /// <summary>Shows <paramref name="part"/> in <paramref name="lane"/>, where it is.</summary>
    /// <returns>False, changing nothing, when another lane already shows <paramref name="part"/>.</returns>
    public bool SetPart(DockLane lane, LanePart part)
    {
        RequireOpen(lane);
        if (lane.Part == part)
            return true;
        if (Contains(part))
            return false;

        lane.Part = part;
        OnChanged();
        return true;
    }

    /// <summary>
    /// Sets the weights of <paramref name="split"/>'s children, such as the sizes they were dragged to. Ignored unless
    /// there's one positive, finite weight per child.
    /// </summary>
    public void SetWeights(DockSplit split, IReadOnlyList<double> weights)
    {
        if (!IsInLayout(split) || weights.Count != split.WeightList.Count || !weights.All(IsWeight))
            return;

        double total = weights.Sum();
        for (int i = 0; i < weights.Count; i++)
            split.WeightList[i] = weights[i] * weights.Count / total;
    }

    /// <summary>The layout as plain data, to save.</summary>
    public LaneDockState ToState() => new() { Root = Root is null ? null : StateOf(Root) };

    /// <summary>
    /// The layout <paramref name="state"/> describes, or null when it doesn't describe one: a split without two or more
    /// children, weights that aren't one positive number per child, a lane that isn't exactly one known highway or
    /// vocal part, or a part in two lanes.
    /// </summary>
    public static LaneDockLayout? FromState(LaneDockState? state)
    {
        if (state is null)
            return null;

        var layout = new LaneDockLayout();
        if (state.Root is not null)
        {
            if (layout.NodeOf(state.Root) is not { } root)
                return null;
            layout.Root = root;
        }

        var parts = layout.Lanes.Select(l => l.Part).ToList();
        if (parts.Distinct().Count() != parts.Count)
            return null;

        layout.Normalize();
        return layout;
    }

    private DockLane NewLane(LanePart part) => new(_nextId++, part);

    private void OnChanged() => Changed?.Invoke(this, EventArgs.Empty);

    private void RequireOpen(DockLane lane)
    {
        if (!IsInLayout(lane))
            throw new ArgumentException("The lane isn't open in this layout.", nameof(lane));
    }

    private bool IsInLayout(DockNode node)
    {
        while (node.Parent is { } parent)
            node = parent;
        return node == Root;
    }

    /// <summary>Takes <paramref name="lane"/> out of the tree, leaving its split as it is until <see cref="Normalize"/>.</summary>
    private void Detach(DockLane lane)
    {
        if (lane.Parent is { } split)
        {
            int index = split.ChildList.IndexOf(lane);
            split.ChildList.RemoveAt(index);
            split.WeightList.RemoveAt(index);
        }
        else
        {
            Root = null;
        }

        lane.Parent = null;
    }

    private void Insert(DockLane lane, DockLane? target, DockEdge edge)
    {
        var orientation = edge is DockEdge.Left or DockEdge.Right ? DockOrientation.Horizontal : DockOrientation.Vertical;
        bool after = edge is DockEdge.Right or DockEdge.Bottom;

        if (target is null)
        {
            if (Root is null)
            {
                Root = lane;
            }
            else if (Root is DockSplit split && split.Orientation == orientation)
            {
                // The new lane's weight is the share of the total that leaves it EdgeShare of the whole.
                double weight = split.WeightList.Sum() * EdgeShare / (1 - EdgeShare);
                split.Insert(after ? split.ChildList.Count : 0, lane, weight);
            }
            else
            {
                Wrap(Root, lane, orientation, after, (1 - EdgeShare) / EdgeShare);
            }

            return;
        }

        if (target.Parent is { } parent && parent.Orientation == orientation)
        {
            int index = parent.ChildList.IndexOf(target);
            double half = parent.WeightList[index] / 2;
            parent.WeightList[index] = half;
            parent.Insert(after ? index + 1 : index, lane, half);
            return;
        }

        Wrap(target, lane, orientation, after, 1);
    }

    /// <summary>
    /// Puts a new split where <paramref name="existing"/> is, with its weight, holding it, weighted
    /// <paramref name="existingWeight"/>, and <paramref name="lane"/>, weighted 1.
    /// </summary>
    private void Wrap(DockNode existing, DockLane lane, DockOrientation orientation, bool laneAfter, double existingWeight)
    {
        var parent = existing.Parent;
        var split = new DockSplit(orientation);
        if (parent is null)
        {
            Root = split;
        }
        else
        {
            parent.ChildList[parent.ChildList.IndexOf(existing)] = split;
            split.Parent = parent;
        }

        split.Insert(0, existing, existingWeight);
        split.Insert(laneAfter ? 1 : 0, lane, 1);
    }

    /// <summary>Collapses splits of one child, merges splits into a parent running the same way, and rescales weights.</summary>
    private void Normalize()
    {
        if (Root is null)
            return;

        Root = Simplify(Root);
        Root.Parent = null;
    }

    private static DockNode Simplify(DockNode node)
    {
        if (node is not DockSplit split)
            return node;

        var children = new List<DockNode>(split.ChildList.Count);
        var weights = new List<double>(split.ChildList.Count);
        for (int i = 0; i < split.ChildList.Count; i++)
        {
            var child = Simplify(split.ChildList[i]);
            double weight = split.WeightList[i];
            if (child is DockSplit inner && inner.Orientation == split.Orientation)
            {
                // Its children take its place, sharing its weight as they shared it.
                double total = inner.WeightList.Sum();
                children.AddRange(inner.ChildList);
                weights.AddRange(inner.WeightList.Select(w => w * weight / total));
            }
            else
            {
                children.Add(child);
                weights.Add(weight);
            }
        }

        if (children.Count == 1)
            return children[0];

        split.ChildList.Clear();
        split.WeightList.Clear();
        double sum = weights.Sum();
        for (int i = 0; i < children.Count; i++)
            split.Insert(i, children[i], weights[i] * children.Count / sum);
        return split;
    }

    private static IEnumerable<DockLane> LanesOf(DockNode node)
    {
        if (node is DockLane lane)
        {
            yield return lane;
            yield break;
        }

        foreach (var child in ((DockSplit)node).ChildList)
        {
            foreach (var inner in LanesOf(child))
                yield return inner;
        }
    }

    private static bool IsWeight(double weight) => double.IsFinite(weight) && weight > 0;

    private static LaneDockNodeState StateOf(DockNode node)
    {
        if (node is DockLane lane)
        {
            return new LaneDockNodeState
            {
                Instrument = lane.Part.Track?.Instrument.ToString(),
                Difficulty = lane.Part.Track?.Difficulty.ToString(),
                Vocal = lane.Part.Vocal?.ToString(),
            };
        }

        var split = (DockSplit)node;
        return new LaneDockNodeState
        {
            Split = split.Orientation.ToString(),
            Children = [.. split.ChildList.Select(StateOf)],
            Weights = [.. split.WeightList],
        };
    }

    private DockNode? NodeOf(LaneDockNodeState state)
    {
        if (state.Split is null)
            return PartOf(state) is { } part ? NewLane(part) : null;

        if (state.Instrument is not null || state.Difficulty is not null || state.Vocal is not null
            || !TryParse(state.Split, out DockOrientation orientation)
            || state.Children is not { Count: >= 2 } children
            || state.Weights is not { } weights || weights.Count != children.Count || !weights.All(IsWeight))
        {
            return null;
        }

        var split = new DockSplit(orientation);
        for (int i = 0; i < children.Count; i++)
        {
            if (children[i] is null || NodeOf(children[i]) is not { } child)
                return null;
            split.Insert(i, child, weights[i]);
        }

        return split;
    }

    private static LanePart? PartOf(LaneDockNodeState state)
    {
        if (state.Children is not null || state.Weights is not null)
            return null;

        if (state.Vocal is not null)
        {
            return state.Instrument is null && state.Difficulty is null && TryParse(state.Vocal, out VocalTrack vocal)
                ? LanePart.Vocals(vocal)
                : null;
        }

        return TryParse(state.Instrument, out ChartInstrument instrument) && TryParse(state.Difficulty, out ChartDifficulty difficulty)
            ? LanePart.Highway(instrument, difficulty)
            : null;
    }

    /// <summary>Parses a member's name, exactly as <see cref="Enum.ToString()"/> writes it: never a number.</summary>
    private static bool TryParse<TEnum>(string? name, out TEnum value)
        where TEnum : struct, Enum
    {
        value = default;
        return name is not null && Enum.GetNames<TEnum>().Contains(name, StringComparer.Ordinal) && Enum.TryParse(name, out value);
    }
}
