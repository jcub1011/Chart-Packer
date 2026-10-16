using ChartShared.Core.Charts;

namespace ChartShared.Core.Tests.Charts;

[TestClass]
public sealed class ListPoolTests
{
    // A type of its own, so no other test's lists are in its pool.
    private readonly record struct Item(long Value);

    [TestMethod]
    public void AListGivenBackIsRentedAgainEmptyWithItsRoom()
    {
        var list = ListPool<Item>.Rent(1000);
        list.Add(new Item(1));
        ListPool<Item>.Return(list);

        var again = ListPool<Item>.Rent(500);

        Assert.AreSame(list, again);
        Assert.IsEmpty(again);
        Assert.IsGreaterThanOrEqualTo(1000, again.Capacity);
        ListPool<Item>.Return(again);
    }

    [TestMethod]
    public void TheSmallestListWithRoomEnoughIsRented()
    {
        var small = ListPool<Item>.Rent(10);
        var large = ListPool<Item>.Rent(10_000);
        ListPool<Item>.Return(large);
        ListPool<Item>.Return(small);

        Assert.AreSame(large, ListPool<Item>.Rent(5_000));
        Assert.AreSame(small, ListPool<Item>.Rent(5));
    }

    [TestMethod]
    public void ListsOfReferencesArentKept()
    {
        var list = ListPool<string>.Rent(10);
        list.Add("a");
        ListPool<string>.Return(list);

        Assert.AreNotSame(list, ListPool<string>.Rent(10));
    }
}
