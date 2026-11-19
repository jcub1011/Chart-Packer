using System.Collections.ObjectModel;
using ChartBuilder.ViewModels;
using ChartShared.Core.Validation;

namespace ChartBuilder.Tests.Editing;

/// <summary>
/// The Messages dialog (<see cref="MessagesDialogViewModel"/>): every message the session said, newest first, narrowed by a
/// search of the text, severity and time, and followed as more are said while it's open.
/// </summary>
[TestClass]
public sealed class MessagesDialogTests
{
    private static readonly DateTime Start = new(2026, 1, 1, 12, 0, 0);

    private static EditorMessage Message(Severity severity, string text, int second = 0) =>
        new(severity, text, Start.AddSeconds(second));

    // Newest first, as EditorMessages keeps them.
    private static ObservableCollection<EditorMessage> Log(params EditorMessage[] newestFirst) => [.. newestFirst];

    [TestMethod]
    public void WithNoSearchEveryMessageShowsNewestFirst()
    {
        var saved = Message(Severity.Info, "Saved notes.mid", 2);
        var refused = Message(Severity.Warning, "Can't place a note there", 1);
        using var dialog = new MessagesDialogViewModel(Log(saved, refused), saved);

        Assert.AreSequenceEqual([saved, refused], [.. dialog.Shown]);
        Assert.IsTrue(dialog.HasEntries);
        Assert.IsFalse(dialog.ShowNoMatches);
        Assert.AreSame(saved, dialog.Selected);
    }

    [TestMethod]
    public void TheSearchMatchesTheTextIgnoringCase()
    {
        var saved = Message(Severity.Info, "Saved notes.mid", 2);
        var refused = Message(Severity.Warning, "Can't place a note there", 1);
        using var dialog = new MessagesDialogViewModel(Log(saved, refused), saved);

        dialog.SearchText = "  PLACE ";

        Assert.AreSequenceEqual([refused], [.. dialog.Shown]);
    }

    [TestMethod]
    public void TheSearchMatchesTheSeverityAndTime()
    {
        var failed = Message(Severity.Error, "Couldn't open the audio", 3);
        var saved = Message(Severity.Info, "Saved notes.mid", 2);
        using var dialog = new MessagesDialogViewModel(Log(failed, saved), failed);

        dialog.SearchText = failed.SeverityText;
        Assert.AreSequenceEqual([failed], [.. dialog.Shown]);

        dialog.SearchText = saved.TimeText;
        Assert.AreSequenceEqual([saved], [.. dialog.Shown]);
    }

    [TestMethod]
    public void ClearingTheSearchShowsEveryMessageAgain()
    {
        var saved = Message(Severity.Info, "Saved notes.mid", 2);
        var refused = Message(Severity.Warning, "Can't place a note there", 1);
        using var dialog = new MessagesDialogViewModel(Log(saved, refused), saved);
        Assert.IsFalse(dialog.ClearSearchCommand.CanExecute(null), "Escape in an empty search closes the window instead");

        dialog.SearchText = "saved";
        Assert.IsTrue(dialog.ClearSearchCommand.CanExecute(null));
        dialog.ClearSearchCommand.Execute(null);

        Assert.IsNull(dialog.SearchText);
        Assert.AreSequenceEqual([saved, refused], [.. dialog.Shown]);
    }

    [TestMethod]
    public void ASearchThatMatchesNothingSaysSo()
    {
        var saved = Message(Severity.Info, "Saved notes.mid");
        using var dialog = new MessagesDialogViewModel(Log(saved), saved);

        dialog.SearchText = "tempo";

        Assert.AreEqual(0, dialog.Shown.Count);
        Assert.IsTrue(dialog.ShowNoMatches);
        StringAssert.Contains(dialog.NoMatchesText, "tempo");
        Assert.IsNull(dialog.Selected);
    }

    [TestMethod]
    public void WithNoMessagesItSaysNothingHasBeenSaidRatherThanNoMatches()
    {
        using var dialog = new MessagesDialogViewModel(Log(), null);

        dialog.SearchText = "tempo";

        Assert.IsFalse(dialog.HasEntries);
        Assert.IsFalse(dialog.ShowNoMatches);
    }

    [TestMethod]
    public void APickedMessageTheSearchHidesGivesWayToTheNewestMatch()
    {
        var saved = Message(Severity.Info, "Saved notes.mid", 3);
        var refused = Message(Severity.Warning, "Can't place a note there", 2);
        var placed = Message(Severity.Info, "Placed 4 notes", 1);
        using var dialog = new MessagesDialogViewModel(Log(saved, refused, placed), saved);

        dialog.SearchText = "plac";

        Assert.AreSame(refused, dialog.Selected);
    }

    [TestMethod]
    public void AMessageSaidWhileOpenShowsIfTheSearchMatchesIt()
    {
        var log = Log();
        using var dialog = new MessagesDialogViewModel(log, null);
        var changed = new List<string?>();
        dialog.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        dialog.SearchText = "tempo";

        log.Insert(0, Message(Severity.Info, "Saved notes.mid", 1));
        Assert.IsTrue(dialog.HasEntries);
        Assert.Contains(nameof(MessagesDialogViewModel.HasEntries), changed, "The empty-list text hides once something is said");
        Assert.AreEqual(0, dialog.Shown.Count);

        var tapped = Message(Severity.Info, "Tap tempo: 120 BPM", 2);
        log.Insert(0, tapped);
        Assert.AreSequenceEqual([tapped], [.. dialog.Shown]);
        Assert.AreSame(tapped, dialog.Selected);
    }

    [TestMethod]
    public void OnceDisposedItStopsFollowingTheMessages()
    {
        var log = Log(Message(Severity.Info, "Saved notes.mid"));
        var dialog = new MessagesDialogViewModel(log, null);

        dialog.Dispose();
        log.Insert(0, Message(Severity.Info, "Placed 4 notes", 1));

        Assert.AreEqual(1, dialog.Shown.Count);
    }
}
