using ChartBuilder.Resources;
using ChartBuilder.ViewModels;
using ChartPacker.Core.Jobs;
using ChartPacker.UI.Services;

namespace ChartBuilder.Tests.Charting.Export;

/// <summary>The Export to .sng dialog's logic: target preview, if-exists choice, error and transcode gates.</summary>
[TestClass]
public sealed class ExportSngViewModelTests
{
    private sealed class StubFolders(string? pick) : IFolderPickerService
    {
        public Task<string?> PickFolderAsync(string title, string? startFolder = null) => Task.FromResult(pick);
    }

    private static ExportSngViewModel Dialog(
        string folder = "/out",
        OverwritePolicy policy = OverwritePolicy.Overwrite,
        bool transcode = false,
        bool isSng = false,
        int errors = 0,
        int pending = 0,
        Func<string, string>? targetFor = null,
        Func<string, bool>? exists = null,
        IReadOnlyList<ExportStemRow>? stems = null) => new(
        "Artist – Name",
        [new ExportMetadataRow("name", "Name")],
        "album.png",
        null,
        stems ?? [new ExportStemRow("guitar", "guitar.ogg", false), new ExportStemRow("song", "song.opus", true)],
        folder,
        isSng,
        errors,
        pending,
        policy,
        transcode,
        targetFor ?? (dest => dest + "/Band.sng"),
        new StubFolders(null),
        "Browse",
        exists ?? (_ => false));

    [TestMethod]
    public void ShowsResolvedTargetAndAllowsExport()
    {
        var vm = Dialog();

        Assert.AreEqual("/out/Band.sng", vm.TargetPath);
        Assert.AreEqual("/out/Band.sng", vm.EffectiveTarget);
        Assert.IsTrue(vm.HasTarget);
        Assert.IsFalse(vm.TargetExists);
        Assert.IsNull(vm.Problem);
        Assert.IsTrue(vm.ExportCommand.CanExecute(null));
    }

    [TestMethod]
    public void EmptyFolderBlocksExport()
    {
        var vm = Dialog(folder: "");

        Assert.AreEqual("", vm.TargetPath);
        Assert.IsNotNull(vm.Problem);
        Assert.IsFalse(vm.ExportCommand.CanExecute(null));
    }

    [TestMethod]
    public void ExistingTargetWithOverwriteWarnsButAllows()
    {
        var vm = Dialog(exists: _ => true);

        Assert.IsTrue(vm.TargetExists);
        Assert.IsNotNull(vm.ExistsText);
        Assert.AreEqual("/out/Band.sng", vm.EffectiveTarget);
        Assert.IsNull(vm.Problem);
        Assert.IsTrue(vm.ExportCommand.CanExecute(null));
    }

    [TestMethod]
    public void RenameOffersFirstFreeName()
    {
        string folder = Path.Combine("out");
        var taken = new HashSet<string>(
            [Path.Combine(folder, "Band.sng"), Path.Combine(folder, "Band (2).sng")]);
        var vm = Dialog(
            folder: folder,
            policy: OverwritePolicy.Rename,
            targetFor: dest => Path.Combine(dest, "Band.sng"),
            exists: taken.Contains);

        Assert.AreEqual(Path.Combine(folder, "Band (3).sng"), vm.EffectiveTarget);
        Assert.IsNotNull(vm.RenameText);
        Assert.IsNull(vm.Problem);
    }

    [TestMethod]
    public void SkipKeepsPreferredTargetForCommitToDecide()
    {
        var vm = Dialog(policy: OverwritePolicy.Skip, exists: _ => true);

        Assert.AreEqual("/out/Band.sng", vm.EffectiveTarget);
        Assert.IsNull(vm.Problem);
    }

    [TestMethod]
    public void ErrorsBlockExport()
    {
        var vm = Dialog(errors: 2);

        Assert.IsNotNull(vm.Problem);
        Assert.IsNotNull(vm.ErrorsText);
        Assert.IsFalse(vm.ExportCommand.CanExecute(null));
    }

    [TestMethod]
    public void TranscodeWithNonOpusBlocksExportUntilUnchecked()
    {
        var vm = Dialog();

        vm.TranscodeToOpus = true;

        Assert.IsTrue(vm.TranscodeBlocked);
        Assert.IsFalse(vm.ExportCommand.CanExecute(null));

        vm.TranscodeToOpus = false;

        Assert.IsFalse(vm.TranscodeBlocked);
        Assert.IsTrue(vm.ExportCommand.CanExecute(null));
    }

    [TestMethod]
    public void TranscodeWithAllOpusStemsIsHarmless()
    {
        var vm = Dialog(stems: [new ExportStemRow("song", "song.opus", true)]);

        vm.TranscodeToOpus = true;

        Assert.IsFalse(vm.TranscodeBlocked);
        Assert.IsTrue(vm.ExportCommand.CanExecute(null));
    }

    [TestMethod]
    public void SngSourceDisablesTranscode()
    {
        var vm = Dialog(isSng: true, transcode: true);

        Assert.IsFalse(vm.CanTranscode);
        Assert.IsFalse(vm.TranscodeToOpus);
        Assert.IsTrue(vm.ExportCommand.CanExecute(null));
    }

    [TestMethod]
    public void StemNotesFollowTranscodeToggle()
    {
        var vm = Dialog();

        Assert.IsTrue(vm.StemDisplays.Any(s => s.Note == Strings.ExportSngStemAsIs));

        vm.TranscodeToOpus = true;

        Assert.IsTrue(vm.StemDisplays.Any(s => s.Note == Strings.ExportSngStemWillTranscode));
        Assert.IsTrue(vm.StemDisplays.Where(s => s.FileName == "song.opus").All(s => s.Note == Strings.ExportSngStemPassthrough));
    }

    [TestMethod]
    public async Task BrowseTakesPickedFolder()
    {
        var vm = new ExportSngViewModel(
            "Artist – Name",
            [],
            "none",
            null,
            [],
            "/out",
            false,
            0,
            0,
            OverwritePolicy.Overwrite,
            false,
            dest => dest + "/Band.sng",
            new StubFolders("/picked"),
            "Browse");

        await vm.BrowseCommand.ExecuteAsync(null);

        Assert.AreEqual("/picked", vm.ExportFolder);
        Assert.AreEqual("/picked/Band.sng", vm.TargetPath);
    }

    [TestMethod]
    public void ExportRaisesExported()
    {
        var vm = Dialog();
        bool raised = false;
        vm.Exported += (_, _) => raised = true;

        vm.ExportCommand.Execute(null);

        Assert.IsTrue(raised);
    }

    [TestMethod]
    public void HeadlineFieldsGetDedicatedSpots()
    {
        var vm = new ExportSngViewModel(
            "Artist – Name",
            [
                new ExportMetadataRow("name", "Name"),
                new ExportMetadataRow("artist", "Artist"),
                new ExportMetadataRow("album", "Album"),
                new ExportMetadataRow("genre", "Rock"),
                new ExportMetadataRow("year", "2011"),
                new ExportMetadataRow("charter", "Charter"),
                new ExportMetadataRow("song_length", "204000"),
                new ExportMetadataRow("diff_guitar", "4"),
            ],
            "album.png",
            null,
            [],
            "/out",
            false,
            0,
            0,
            OverwritePolicy.Overwrite,
            false,
            dest => dest + "/Band.sng",
            new StubFolders(null),
            "Browse");

        Assert.AreEqual("Name", vm.Name);
        Assert.AreEqual("Artist", vm.Artist);
        Assert.AreEqual("Album", vm.Album);
        Assert.AreEqual("Rock", vm.Genre);
        Assert.AreEqual("2011", vm.Year);
        Assert.AreEqual("Charter", vm.Charter);
        Assert.AreEqual("3:24", vm.SongLength);
        Assert.IsTrue(vm.HasDetails);
        CollectionAssert.AreEquivalent(
            new[] { "diff_guitar" },
            vm.OtherMetadata.Select(m => m.Key).ToList());
    }

    [TestMethod]
    public void MissingHeadlineFieldsAreHidden()
    {
        var vm = Dialog();

        Assert.AreEqual("Name", vm.Name);
        Assert.IsNull(vm.Album);
        Assert.IsNull(vm.SongLength);
        Assert.IsFalse(vm.HasAlbum);
        Assert.IsFalse(vm.HasSongLength);
        Assert.IsFalse(vm.HasOtherMetadata);
    }
}
