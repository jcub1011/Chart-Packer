using ChartBuilder.ViewModels;

namespace ChartBuilder.Tests.Editing;

/// <summary>
/// The dialog a new chart's first save opens when its name or artist isn't set (<see cref="SongInfoViewModel"/>): what's
/// set starts filled in, and Save waits for both.
/// </summary>
[TestClass]
public sealed class SongInfoViewModelTests
{
    [TestMethod]
    public void WhatIsAlreadySetStartsFilledIn()
    {
        var dialog = new SongInfoViewModel("Song", "");

        Assert.AreEqual("Song", dialog.Name);
        Assert.AreEqual("", dialog.Artist);
        Assert.IsTrue(dialog.HasName);
    }

    [TestMethod]
    public void WithNothingSetTheNameIsAskedFirst()
    {
        Assert.IsFalse(new SongInfoViewModel("", "").HasName);
    }

    [TestMethod]
    [DataRow("", "")]
    [DataRow("Song", "")]
    [DataRow("", "Band")]
    [DataRow("  ", "Band")]
    public void SaveWaitsForBothToBeSet(string name, string artist)
    {
        var dialog = new SongInfoViewModel(name, artist);

        Assert.IsFalse(dialog.SaveCommand.CanExecute(null));
    }

    [TestMethod]
    public void SavingOnceBothAreSetConfirms()
    {
        var dialog = new SongInfoViewModel("Song", "");
        int confirmed = 0;
        dialog.Confirmed += (_, _) => confirmed++;

        dialog.Artist = "Band";
        Assert.IsTrue(dialog.SaveCommand.CanExecute(null));
        dialog.SaveCommand.Execute(null);

        Assert.AreEqual(1, confirmed);
    }

    [TestMethod]
    public void ClearingAFieldTurnsSaveOffAgain()
    {
        var dialog = new SongInfoViewModel("Song", "Band");
        Assert.IsTrue(dialog.SaveCommand.CanExecute(null));

        dialog.Name = " ";

        Assert.IsFalse(dialog.SaveCommand.CanExecute(null));
    }
}
