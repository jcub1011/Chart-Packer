using ChartShared.Core.Charts;

namespace ChartShared.Core.Tests.Charts;

[TestClass]
public sealed class ListPoolTests
{
    // A type of its own, so no other test's lists are in its pool.
    private readonly record struct Item(long Value);

    // Types of their own for the trimming tests, which let go of everything their pools keep.
    private readonly record struct IdleItem(long Value);

    private readonly record struct RecentItem(long Value);

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
    public void AListUnusedForAWhileIsLetGo()
    {
        var list = ListPool<IdleItem>.Rent(100);
        ListPool<IdleItem>.Return(list);

        ListPool<IdleItem>.Trim(Environment.TickCount64 + ListPool<IdleItem>.IdleMilliseconds);

        Assert.AreNotSame(list, ListPool<IdleItem>.Rent(100));
    }

    [TestMethod]
    public void AListGivenBackJustNowIsKeptByATrim()
    {
        var list = ListPool<RecentItem>.Rent(100);
        ListPool<RecentItem>.Return(list);

        ListPool<RecentItem>.Trim(Environment.TickCount64);

        Assert.AreSame(list, ListPool<RecentItem>.Rent(100));
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
