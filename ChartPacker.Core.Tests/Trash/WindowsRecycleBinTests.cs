using System.Runtime.Versioning;
using ChartPacker.Core.Trash;

namespace ChartPacker.Core.Tests.Trash;

[TestClass]
[SupportedOSPlatform("windows")]
[OSCondition(OperatingSystems.Windows)]
public sealed class WindowsRecycleBinTests
{
    private const int AccessDenied = unchecked((int)0x80070005);
    private const int Cancelled = unchecked((int)0x800704C7);

    [TestMethod]
    public void AnEarlyFailureIsAFailureNotARefusal() =>
        Assert.AreEqual(TrashStatus.Failed, WindowsRecycleBin.Interpret(AccessDenied, aborted: true, deleteResult: null, recycled: false).Status);

    [TestMethod]
    public void AnAbortWithoutAnErrorIsTheUserRefusing() =>
        Assert.AreEqual(TrashStatus.Declined, WindowsRecycleBin.Interpret(0, aborted: true, deleteResult: null, recycled: false).Status);

    [TestMethod]
    public void ACancelIsTheUserRefusing()
    {
        Assert.AreEqual(TrashStatus.Declined, WindowsRecycleBin.Interpret(Cancelled, aborted: true, deleteResult: null, recycled: false).Status);
        Assert.AreEqual(TrashStatus.Declined, WindowsRecycleBin.Interpret(0, aborted: true, deleteResult: Cancelled, recycled: false).Status);
    }

    [TestMethod]
    public void AFailedDeleteIsAFailure() =>
        Assert.AreEqual(TrashStatus.Failed, WindowsRecycleBin.Interpret(0, aborted: false, deleteResult: AccessDenied, recycled: false).Status);

    [TestMethod]
    public void ADeleteIsTrashedOnlyWhenARecycleBinItemWasMade()
    {
        Assert.AreEqual(TrashStatus.Trashed, WindowsRecycleBin.Interpret(0, aborted: false, deleteResult: 0, recycled: true).Status);
        Assert.AreEqual(TrashStatus.DeletedPermanently, WindowsRecycleBin.Interpret(0, aborted: false, deleteResult: 0, recycled: false).Status);
    }
}
