using ChartBuilder.Resources;
using ChartBuilder.ViewModels;
using ChartShared.Core.Jobs;

namespace ChartBuilder.Tests.Charting.Export;

/// <summary>The export progress dialog's mapping of transcode and pack reports to its bar and status.</summary>
[TestClass]
public sealed class ExportProgressViewModelTests
{
    [TestMethod]
    public void StartsAtZeroWithStartingText()
    {
        var vm = new ExportProgressViewModel();

        Assert.AreEqual(0, vm.ProgressValue);
        Assert.AreEqual(Strings.ExportProgressStarting, vm.StatusText);
    }

    [TestMethod]
    public void TranscodeReportStaysBelowPackingStart()
    {
        var vm = new ExportProgressViewModel();

        vm.Report(new ExportProgress(ExportPhase.Transcoding, 0, 2, "guitar", 0.5));

        Assert.AreEqual(42.5, vm.ProgressValue, delta: 1e-9);
        StringAssert.Contains(vm.StatusText, "guitar");
        StringAssert.Contains(vm.StatusText, "1 of 2");
        StringAssert.Contains(vm.StatusText, "50%");
    }

    [TestMethod]
    public void PackingReportSitsAbovePackingStart()
    {
        var vm = new ExportProgressViewModel();

        vm.Report(new ExportProgress(ExportPhase.Packing, 0, 1, "Band.sng", 0.5));

        Assert.AreEqual(92.5, vm.ProgressValue, delta: 1e-9);
        StringAssert.Contains(vm.StatusText, "Band.sng");
        StringAssert.Contains(vm.StatusText, "50%");
    }

    [TestMethod]
    public void FullPackingReachesAHundred()
    {
        var vm = new ExportProgressViewModel();

        vm.Report(new ExportProgress(ExportPhase.Packing, 0, 1, "Band.sng", 1));

        Assert.AreEqual(100, vm.ProgressValue, delta: 1e-9);
    }

    [TestMethod]
    public void CancelRaisesCancelRequested()
    {
        var vm = new ExportProgressViewModel();
        bool raised = false;
        vm.CancelRequested += (_, _) => raised = true;

        vm.CancelCommand.Execute(null);

        Assert.IsTrue(raised);
    }
}
