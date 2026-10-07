using System.Text.Json;
using System.Text.Json.Serialization;
using ChartPacker.Core.Charting.Layout;
using ChartPacker.Core.Charts;

namespace ChartPacker.Core.Tests.Charting;

[TestClass]
public sealed class LaneDockLayoutTests
{
    private static readonly LanePart Guitar = LanePart.Highway(ChartInstrument.Guitar, ChartDifficulty.Expert);
    private static readonly LanePart Bass = LanePart.Highway(ChartInstrument.Bass, ChartDifficulty.Expert);
    private static readonly LanePart Drums = LanePart.Highway(ChartInstrument.Drums, ChartDifficulty.Expert);
    private static readonly LanePart Keys = LanePart.Highway(ChartInstrument.Keys, ChartDifficulty.Hard);
    private static readonly LanePart Vocals = LanePart.Vocals(VocalTrack.Vocals);
    private static readonly LanePart Harmony1 = LanePart.Vocals(VocalTrack.Harmony1);

    [TestMethod]
    public void TheDefaultIsThreeHighwaysOverVocals()
    {
        var layout = LaneDockLayout.CreateDefault();

        Assert.AreEqual("V(H(Guitar Bass Drums) Vocals)", Shape(layout));
        CollectionAssert.AreEqual(new[] { 1.5, 0.5 }, ((DockSplit)layout.Root!).Weights.ToArray());
    }

    [TestMethod]
    public void LaneIdsAreUnique()
    {
        var layout = LaneDockLayout.CreateDefault();
        layout.Add(Keys);

        var ids = layout.Lanes.Select(l => l.Id).ToList();
        Assert.AreEqual(ids.Count, ids.Distinct().Count());
    }

    [TestMethod]
    [DataRow(DockEdge.Left, "V(H(Vocals Guitar Bass Drums))")]
    [DataRow(DockEdge.Right, "V(H(Guitar Vocals Bass Drums))")]
    [DataRow(DockEdge.Top, "V(H(V(Vocals Guitar) Bass Drums))")]
    [DataRow(DockEdge.Bottom, "V(H(V(Guitar Vocals) Bass Drums))")]
    public void DockingAgainstALaneSplitsIt(DockEdge edge, string expected)
    {
        var layout = LaneDockLayout.CreateDefault();

        layout.Move(Lane(layout, Vocals), Lane(layout, Guitar), edge);

        // Vocals was the root's other child, so the row is all that's left: shown wrapped for the check's sake.
        Assert.AreEqual(expected, "V(" + Shape(layout) + ")");
    }

    [TestMethod]
    public void DockingBesideALaneInARowHalvesItsShare()
    {
        var layout = LaneDockLayout.CreateDefault();

        layout.Move(Lane(layout, Vocals), Lane(layout, Bass), DockEdge.Right);

        var row = (DockSplit)layout.Root!;
        Assert.AreEqual("H(Guitar Bass Vocals Drums)", Shape(layout));
        CollectionAssert.AreEqual(new[] { 1.0, 0.5, 0.5, 1.0 }, Scaled(row.Weights, 3));
    }

    [TestMethod]
    public void DockingAcrossARowWrapsTheLaneInAnEvenSplit()
    {
        var layout = LaneDockLayout.CreateDefault();

        layout.Move(Lane(layout, Drums), Lane(layout, Guitar), DockEdge.Bottom);

        Assert.AreEqual("V(H(V(Guitar Drums) Bass) Vocals)", Shape(layout));
        var column = (DockSplit)((DockSplit)((DockSplit)layout.Root!).Children[0]).Children[0];
        CollectionAssert.AreEqual(new[] { 1.0, 1.0 }, column.Weights.ToArray());
    }

    [TestMethod]
    [DataRow(DockEdge.Left, "H(Vocals V(H(Guitar Bass) Drums))")]
    [DataRow(DockEdge.Right, "H(V(H(Guitar Bass) Drums) Vocals)")]
    [DataRow(DockEdge.Top, "V(Vocals H(Guitar Bass) Drums)")]
    [DataRow(DockEdge.Bottom, "V(H(Guitar Bass) Drums Vocals)")]
    public void DockingAgainstTheWholeDockTakesAQuarterOfIt(DockEdge edge, string expected)
    {
        var layout = LaneDockLayout.CreateDefault();
        layout.Move(Lane(layout, Drums), null, DockEdge.Bottom);

        layout.Move(Lane(layout, Vocals), null, edge);

        Assert.AreEqual(expected, Shape(layout));
        var root = (DockSplit)layout.Root!;
        int index = root.Children.ToList().FindIndex(c => c is DockLane { Part.IsVocal: true });
        Assert.AreEqual(LaneDockLayout.EdgeShare, root.Weights[index] / root.Weights.Sum(), 1e-9);
    }

    [TestMethod]
    public void DockingALaneAgainstItselfDoesNothing()
    {
        var layout = LaneDockLayout.CreateDefault();
        int changes = 0;
        layout.Changed += (_, _) => changes++;

        layout.Move(Lane(layout, Bass), Lane(layout, Bass), DockEdge.Left);

        Assert.AreEqual("V(H(Guitar Bass Drums) Vocals)", Shape(layout));
        Assert.AreEqual(0, changes);
    }

    [TestMethod]
    public void MovingTheOnlyLaneToAnEdgeKeepsIt()
    {
        var layout = new LaneDockLayout();
        var lane = layout.Add(Guitar)!;

        layout.Move(lane, null, DockEdge.Left);

        Assert.AreEqual("Guitar", Shape(layout));
    }

    [TestMethod]
    public void ClosingALaneGivesItsShareToTheLanesBesideIt()
    {
        var layout = LaneDockLayout.CreateDefault();
        layout.Move(Lane(layout, Vocals), Lane(layout, Bass), DockEdge.Right);

        layout.Close(Lane(layout, Bass));

        var row = (DockSplit)layout.Root!;
        Assert.AreEqual("H(Guitar Vocals Drums)", Shape(layout));
        CollectionAssert.AreEqual(new[] { 1.0, 0.5, 1.0 }, Scaled(row.Weights, 2.5));
    }

    [TestMethod]
    public void ClosingALaneCollapsesAndMergesItsSplit()
    {
        var layout = LaneDockLayout.CreateDefault();
        layout.Move(Lane(layout, Drums), Lane(layout, Guitar), DockEdge.Bottom);

        layout.Close(Lane(layout, Guitar));

        // V(Guitar Drums) collapsed to Drums; Bass's row then holds Drums and Bass side by side.
        Assert.AreEqual("V(H(Drums Bass) Vocals)", Shape(layout));
    }

    [TestMethod]
    public void ASplitRunningTheSameWayAsItsParentIsMergedIntoIt()
    {
        var layout = LaneDockLayout.CreateDefault();

        // Closing Vocals leaves the root a split of one row, which becomes the root.
        layout.Close(Lane(layout, Vocals));
        layout.Move(Lane(layout, Drums), null, DockEdge.Bottom);
        layout.Move(Lane(layout, Bass), Lane(layout, Drums), DockEdge.Bottom);

        Assert.AreEqual("V(Guitar Drums Bass)", Shape(layout));
        Assert.IsTrue(layout.Lanes.All(l => l.Parent == layout.Root));
    }

    [TestMethod]
    public void ClosingEveryLaneLeavesNoRoot()
    {
        var layout = LaneDockLayout.CreateDefault();

        foreach (var lane in layout.Lanes.ToList())
            layout.Close(lane);

        Assert.IsNull(layout.Root);
        Assert.AreEqual(0, layout.Lanes.Count());
    }

    [TestMethod]
    public void AHighwayIsAddedAfterTheLastOneAsWideAsTheOthers()
    {
        var layout = LaneDockLayout.CreateDefault();

        layout.Add(Keys);

        var row = (DockSplit)((DockSplit)layout.Root!).Children[0];
        Assert.AreEqual("V(H(Guitar Bass Drums Keys.Hard) Vocals)", Shape(layout));
        Assert.IsTrue(row.Weights.All(w => Math.Abs(w - 1) < 1e-9));
    }

    [TestMethod]
    public void AHighwayAfterOneOnItsOwnGoesToItsRight()
    {
        var layout = LaneDockLayout.CreateDefault();
        layout.Move(Lane(layout, Drums), Lane(layout, Vocals), DockEdge.Bottom);

        layout.Add(Keys);

        Assert.AreEqual("V(H(Guitar Bass) Vocals H(Drums Keys.Hard))", Shape(layout));
    }

    [TestMethod]
    public void AHighwayWithOnlyVocalsOpenGoesAboveThem()
    {
        var layout = new LaneDockLayout();
        layout.Add(Vocals);

        layout.Add(Guitar);

        var root = (DockSplit)layout.Root!;
        Assert.AreEqual("V(Guitar Vocals)", Shape(layout));
        Assert.AreEqual(LaneDockLayout.EdgeShare, root.Weights[1] / root.Weights.Sum(), 1e-9);
    }

    [TestMethod]
    public void AVocalPartGoesTheFullWidthAlongTheBottom()
    {
        var layout = LaneDockLayout.CreateDefault();

        layout.Add(Harmony1);

        var root = (DockSplit)layout.Root!;
        Assert.AreEqual("V(H(Guitar Bass Drums) Vocals Harmony1)", Shape(layout));
        Assert.AreEqual(LaneDockLayout.EdgeShare, root.Weights[2] / root.Weights.Sum(), 1e-9);
    }

    [TestMethod]
    public void TheFirstLaneIsTheRoot()
    {
        var layout = new LaneDockLayout();
        int changes = 0;
        layout.Changed += (_, _) => changes++;

        var lane = layout.Add(Guitar);

        Assert.AreSame(lane, layout.Root);
        Assert.AreEqual(1, changes);
    }

    [TestMethod]
    public void APartAlreadyOpenIsNotAddedAgain()
    {
        var layout = LaneDockLayout.CreateDefault();

        Assert.IsNull(layout.Add(Guitar));
        Assert.AreEqual(4, layout.Lanes.Count());
    }

    [TestMethod]
    public void ALaneCanShowAnotherPart()
    {
        var layout = LaneDockLayout.CreateDefault();
        var lane = Lane(layout, Bass);

        Assert.IsTrue(layout.SetPart(lane, Harmony1));

        Assert.AreEqual("V(H(Guitar Harmony1 Drums) Vocals)", Shape(layout));
    }

    [TestMethod]
    public void ALaneCanNotShowAPartAnotherLaneShows()
    {
        var layout = LaneDockLayout.CreateDefault();

        Assert.IsFalse(layout.SetPart(Lane(layout, Bass), Guitar));

        Assert.AreEqual("V(H(Guitar Bass Drums) Vocals)", Shape(layout));
    }

    [TestMethod]
    public void ALaneNotInTheLayoutThrows()
    {
        var layout = LaneDockLayout.CreateDefault();
        var other = LaneDockLayout.CreateDefault();

        Assert.ThrowsExactly<ArgumentException>(() => layout.Close(Lane(other, Bass)));
    }

    [TestMethod]
    public void WeightsAreSetOnlyWhenThereIsOnePositiveWeightPerChild()
    {
        var layout = LaneDockLayout.CreateDefault();
        var row = (DockSplit)((DockSplit)layout.Root!).Children[0];

        layout.SetWeights(row, [1, 2]);
        layout.SetWeights(row, [1, 0, 2]);
        layout.SetWeights(row, [1, double.NaN, 2]);
        CollectionAssert.AreEqual(new[] { 1.0, 1.0, 1.0 }, row.Weights.ToArray());

        layout.SetWeights(row, [100, 200, 300]);
        CollectionAssert.AreEqual(new[] { 0.5, 1.0, 1.5 }, row.Weights.ToArray());
    }

    [TestMethod]
    public void TheLayoutReadsBackFromItsState()
    {
        var layout = LaneDockLayout.CreateDefault();
        layout.Move(Lane(layout, Drums), Lane(layout, Guitar), DockEdge.Bottom);
        layout.Add(Keys);
        layout.SetWeights((DockSplit)layout.Root!, [2, 1]);

        var read = LaneDockLayout.FromState(layout.ToState());

        Assert.IsNotNull(read);
        Assert.AreEqual(Shape(layout), Shape(read));
        CollectionAssert.AreEqual(((DockSplit)layout.Root!).Weights.ToArray(), ((DockSplit)read.Root!).Weights.ToArray());
    }

    [TestMethod]
    public void ALanesLookAheadIsKeptWithoutChangingTheLayout()
    {
        var layout = LaneDockLayout.CreateDefault();
        int changes = 0;
        layout.Changed += (_, _) => changes++;

        layout.SetLookAhead(Lane(layout, Bass), 3.5);
        layout.SetLookAhead(Lane(layout, Drums), -1);
        string json = JsonSerializer.Serialize(layout.ToState(), LaneDockTestJsonContext.Default.LaneDockState);
        var read = LaneDockLayout.FromState(JsonSerializer.Deserialize(json, LaneDockTestJsonContext.Default.LaneDockState))!;

        Assert.AreEqual(0, changes, "A look-ahead isn't a change to the layout's shape.");
        Assert.AreEqual(3.5, Lane(read, Bass).LookAhead);
        Assert.IsNull(Lane(read, Drums).LookAhead, "A look-ahead that isn't a positive number is the default.");
        Assert.IsNull(Lane(read, Guitar).LookAhead);
    }

    [TestMethod]
    public void TheStateSurvivesJson()
    {
        var layout = LaneDockLayout.CreateDefault();
        layout.Move(Lane(layout, Vocals), Lane(layout, Bass), DockEdge.Top);

        string json = JsonSerializer.Serialize(layout.ToState(), LaneDockTestJsonContext.Default.LaneDockState);
        var read = LaneDockLayout.FromState(JsonSerializer.Deserialize(json, LaneDockTestJsonContext.Default.LaneDockState));

        Assert.AreEqual(Shape(layout), Shape(read!));
        Assert.IsTrue(json.Contains("\"Bass\"", StringComparison.Ordinal), "Parts are saved by name.");
    }

    [TestMethod]
    public void AnEmptyStateIsADockWithNoLanes()
    {
        var read = LaneDockLayout.FromState(new LaneDockState());

        Assert.IsNotNull(read);
        Assert.IsNull(read.Root);
    }

    [TestMethod]
    public void NoStateIsNoLayout() => Assert.IsNull(LaneDockLayout.FromState(null));

    [TestMethod]
    [DynamicData(nameof(InvalidStates))]
    public void AnInvalidStateIsNoLayout(string description, LaneDockNodeState root) =>
        Assert.IsNull(LaneDockLayout.FromState(new LaneDockState { Root = root }), description);

    public static IEnumerable<object[]> InvalidStates()
    {
        static LaneDockNodeState Highway(string instrument, string difficulty = "Expert") => new() { Instrument = instrument, Difficulty = difficulty };
        static LaneDockNodeState Split(string orientation, double[] weights, params LaneDockNodeState[] children) =>
            new() { Split = orientation, Weights = [.. weights], Children = [.. children] };

        yield return ["a split of one", Split("Horizontal", [1], Highway("Guitar"))];
        yield return ["a split of none", Split("Horizontal", [])];
        yield return ["too few weights", Split("Horizontal", [1], Highway("Guitar"), Highway("Bass"))];
        yield return ["a zero weight", Split("Horizontal", [1, 0], Highway("Guitar"), Highway("Bass"))];
        yield return ["a negative weight", Split("Horizontal", [1, -1], Highway("Guitar"), Highway("Bass"))];
        yield return ["a NaN weight", Split("Horizontal", [1, double.NaN], Highway("Guitar"), Highway("Bass"))];
        yield return ["no weights", new LaneDockNodeState { Split = "Horizontal", Children = [Highway("Guitar"), Highway("Bass")] }];
        yield return ["an unknown orientation", Split("Diagonal", [1, 1], Highway("Guitar"), Highway("Bass"))];
        yield return ["an unknown instrument", Highway("Banjo")];
        yield return ["an unknown difficulty", Highway("Guitar", "Impossible")];
        yield return ["a numbered instrument", Highway("1")];
        yield return ["a lowercase instrument", Highway("guitar")];
        yield return ["an instrument without a difficulty", new LaneDockNodeState { Instrument = "Guitar" }];
        yield return ["an unknown vocal part", new LaneDockNodeState { Vocal = "Harmony4" }];
        yield return ["a highway and a vocal part", new LaneDockNodeState { Instrument = "Guitar", Difficulty = "Expert", Vocal = "Vocals" }];
        yield return ["a lane with nothing", new LaneDockNodeState()];
        yield return ["a split with a part", new LaneDockNodeState { Split = "Horizontal", Weights = [1, 1], Children = [Highway("Guitar"), Highway("Bass")], Vocal = "Vocals" }];
        yield return ["a lane with children", new LaneDockNodeState { Instrument = "Guitar", Difficulty = "Expert", Children = [Highway("Bass")] }];
        yield return ["a part twice", Split("Vertical", [1, 1], Highway("Guitar"), Highway("Guitar"))];
        yield return ["a missing child", Split("Vertical", [1, 1], Highway("Guitar"), null!)];
    }

    [TestMethod]
    public void AStateIsSimplifiedOnReading()
    {
        var state = new LaneDockState
        {
            Root = new LaneDockNodeState
            {
                Split = "Horizontal",
                Weights = [1, 2],
                Children =
                [
                    new LaneDockNodeState { Instrument = "Guitar", Difficulty = "Expert" },
                    new LaneDockNodeState
                    {
                        Split = "Horizontal",
                        Weights = [1, 1],
                        Children = [new() { Instrument = "Bass", Difficulty = "Expert" }, new() { Instrument = "Drums", Difficulty = "Expert" }],
                    },
                ],
            },
        };

        var read = LaneDockLayout.FromState(state)!;

        Assert.AreEqual("H(Guitar Bass Drums)", Shape(read));
        CollectionAssert.AreEqual(new[] { 1.0, 1.0, 1.0 }, ((DockSplit)read.Root!).Weights.ToArray());
    }

    private static DockLane Lane(LaneDockLayout layout, LanePart part) => layout.Lanes.Single(l => l.Part == part);

    /// <summary>The weights, scaled so they add up to <paramref name="total"/>, to three decimal places.</summary>
    private static double[] Scaled(IReadOnlyList<double> weights, double total) =>
        [.. weights.Select(w => Math.Round(w * total / weights.Sum(), 3))];

    /// <summary>The tree as text: H(…) and V(…) for splits, and each lane's part, e.g. <c>V(H(Guitar Bass) Vocals)</c>.</summary>
    private static string Shape(LaneDockLayout layout)
    {
        Assert.IsTrue(layout.Root is null || layout.Root.Parent is null, "The root has a parent.");
        return layout.Root is null ? "" : Shape(layout.Root);
    }

    private static string Shape(DockNode node)
    {
        if (node is DockLane lane)
        {
            return lane.Part.Track is { } track
                ? track.Difficulty == ChartDifficulty.Expert ? $"{track.Instrument}" : $"{track.Instrument}.{track.Difficulty}"
                : $"{lane.Part.Vocal}";
        }

        var split = (DockSplit)node;
        Assert.IsTrue(split.Children.Count >= 2, "A split has fewer than two children.");
        Assert.AreEqual(split.Children.Count, split.Weights.Count);
        Assert.IsTrue(split.Children.All(c => c.Parent == split), "A child's parent is wrong.");
        Assert.IsTrue(split.Children.All(c => c is not DockSplit inner || inner.Orientation != split.Orientation), "A split runs the same way as its parent.");
        string tag = split.Orientation == DockOrientation.Horizontal ? "H" : "V";
        return $"{tag}({string.Join(' ', split.Children.Select(Shape))})";
    }
}

[JsonSerializable(typeof(LaneDockState))]
internal sealed partial class LaneDockTestJsonContext : JsonSerializerContext
{
}
