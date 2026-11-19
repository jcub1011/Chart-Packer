using ChartBuilder.Resources;
using ChartBuilder.Services;
using ChartBuilder.ViewModels;
using ChartShared.Core.Charting.Layout;
using ChartShared.Core.Charts;
using ChartShared.UI.Services;

namespace ChartBuilder.Tests.Editing;

/// <summary>
/// Manage Layouts and the layout name dialog (GitHub issue #263), with prompts that answer as each test says and settings
/// in a temp folder.
/// </summary>
[TestClass]
public sealed class ManageLayoutsViewModelTests
{
    private static readonly LanePart Bass = LanePart.Highway(ChartInstrument.Bass, ChartDifficulty.Expert);

    private DirectoryInfo _root = null!;

    [TestInitialize]
    public void CreateRoot() => _root = Directory.CreateTempSubdirectory("chartpacker-manage-layouts-");

    [TestCleanup]
    public void DeleteRoot() => _root.Delete(recursive: true);

    private sealed class Answers : IConfirmationService, ILayoutDialogService
    {
        public bool Confirm { get; set; }

        public string? Name { get; set; }

        public int Asked { get; private set; }

        public LayoutNameViewModel? LastNamePrompt { get; private set; }

        public Task<bool> ConfirmAsync(string title, string message, string confirmLabel, string? confirmIcon = null)
        {
            Asked++;
            return Task.FromResult(Confirm);
        }

        public Task<ConfirmChoice> ChooseAsync(string title, string message, string firstLabel, string secondLabel, string? firstIcon = null, string? secondIcon = null) => Task.FromResult(ConfirmChoice.Cancel);

        public Task AlertAsync(string title, string message) => Task.CompletedTask;

        public Task<string?> AskNameAsync(LayoutNameViewModel viewModel)
        {
            Asked++;
            LastNamePrompt = viewModel;
            return Task.FromResult(Name);
        }

        public Task ShowManageLayoutsAsync(LayoutTemplateStore store, Func<LaneDockState> current) => Task.CompletedTask;
    }

    private (ManageLayoutsViewModel Manage, LayoutTemplateStore Store, Answers Answers) Make()
    {
        var store = new LayoutTemplateStore(BuilderSettingsService.Load(Path.Combine(_root.FullName, "builder-settings.json")));
        var answers = new Answers();
        var current = new LaneDockLayout();
        current.Add(Bass);
        return (new ManageLayoutsViewModel(store, current.ToState, answers, answers), store, answers);
    }

    [TestMethod]
    public void TheFirstTemplateStartsSelected()
    {
        var (manage, _, _) = Make();

        Assert.AreEqual(Strings.LayoutDefaultName, manage.Selected);
        Assert.IsTrue(manage.HasLayouts);
        Assert.IsFalse(manage.MoveUpCommand.CanExecute(null));
        Assert.IsFalse(manage.MoveDownCommand.CanExecute(null));
        Assert.IsTrue(manage.RenameCommand.CanExecute(null));
    }

    [TestMethod]
    public async Task NewLayoutSavesTheLanesOpenAtTheBottomAndSelectsIt()
    {
        var (manage, store, answers) = Make();
        answers.Name = "Bass";

        await manage.NewCommand.ExecuteAsync(null);

        Assert.AreSequenceEqual([Strings.LayoutDefaultName, "Bass"], [.. manage.Layouts]);
        Assert.AreEqual("Bass", manage.Selected);
        Assert.AreSequenceEqual([Bass], [.. LaneDockLayout.FromState(store.Get("Bass"))!.Lanes.Select(l => l.Part)]);
        Assert.IsTrue(manage.MoveUpCommand.CanExecute(null));
        Assert.IsFalse(manage.MoveDownCommand.CanExecute(null));
    }

    [TestMethod]
    public async Task ACancelledPromptChangesNothing()
    {
        var (manage, store, answers) = Make();
        int changes = 0;
        store.Changed += (_, _) => changes++;

        await manage.NewCommand.ExecuteAsync(null);
        await manage.RenameCommand.ExecuteAsync(null);
        await manage.OverwriteCommand.ExecuteAsync(null);
        await manage.DeleteCommand.ExecuteAsync(null);

        Assert.AreEqual(4, answers.Asked);
        Assert.AreEqual(0, changes);
        Assert.AreSequenceEqual([Strings.LayoutDefaultName], [.. manage.Layouts]);
    }

    [TestMethod]
    public async Task RenamingKeepsTheTemplateSelected()
    {
        var (manage, _, answers) = Make();
        answers.Name = "Mine";

        await manage.RenameCommand.ExecuteAsync(null);

        Assert.AreSequenceEqual(["Mine"], [.. manage.Layouts]);
        Assert.AreEqual("Mine", manage.Selected);
        Assert.AreEqual(Strings.LayoutDefaultName, answers.LastNamePrompt!.Name, "Renaming starts from the template's own name.");
    }

    [TestMethod]
    public async Task OverwriteLayoutPutsTheLanesOpenInTheSelectedTemplate()
    {
        var (manage, store, answers) = Make();
        answers.Confirm = true;

        await manage.OverwriteCommand.ExecuteAsync(null);

        Assert.AreSequenceEqual([Bass], [.. LaneDockLayout.FromState(store.Get(Strings.LayoutDefaultName))!.Lanes.Select(l => l.Part)]);
    }

    [TestMethod]
    public async Task DeletingSelectsTheTemplateInItsPlace()
    {
        var (manage, store, answers) = Make();
        store.Add("B", new LaneDockState());
        store.Add("C", new LaneDockState());
        answers.Confirm = true;
        manage.Selected = "C";

        await manage.DeleteCommand.ExecuteAsync(null);
        Assert.AreEqual("B", manage.Selected, "The last deleted, the one before it is selected.");

        manage.Selected = Strings.LayoutDefaultName;
        await manage.DeleteCommand.ExecuteAsync(null);
        Assert.AreEqual("B", manage.Selected);

        await manage.DeleteCommand.ExecuteAsync(null);
        Assert.IsNull(manage.Selected);
        Assert.IsFalse(manage.HasLayouts);
        Assert.IsFalse(manage.DeleteCommand.CanExecute(null));
    }

    [TestMethod]
    public void MovingKeepsTheTemplateSelected()
    {
        var (manage, store, _) = Make();
        store.Add("B", new LaneDockState());
        manage.Selected = "B";

        manage.MoveUpCommand.Execute(null);

        Assert.AreSequenceEqual(["B", Strings.LayoutDefaultName], [.. manage.Layouts]);
        Assert.AreEqual("B", manage.Selected);
        Assert.IsFalse(manage.MoveUpCommand.CanExecute(null));
        Assert.IsTrue(manage.MoveDownCommand.CanExecute(null));
    }

    [TestMethod]
    public void TheListFollowsChangesMadeElsewhere()
    {
        var (manage, store, _) = Make();

        store.Add("From the menu", new LaneDockState());

        Assert.AreSequenceEqual([Strings.LayoutDefaultName, "From the menu"], [.. manage.Layouts]);
        Assert.AreEqual(Strings.LayoutDefaultName, manage.Selected);

        manage.Dispose();
        store.Add("After closing", new LaneDockState());
        Assert.HasCount(2, manage.Layouts);
    }

    [TestMethod]
    public void ANameCanBeConfirmedOnlyWhenNothingIsWrongWithIt()
    {
        var (_, store, _) = Make();
        var prompt = LayoutNameViewModel.ForNew(store);
        string? confirmed = null;
        prompt.Confirmed += (_, _) => confirmed = prompt.Result;

        Assert.IsFalse(prompt.ConfirmCommand.CanExecute(null));
        Assert.IsNull(prompt.ShownError, "A blank name says nothing yet.");

        prompt.Name = Strings.LayoutDefaultName.ToLowerInvariant();
        Assert.IsFalse(prompt.ConfirmCommand.CanExecute(null));
        Assert.IsNotNull(prompt.ShownError);

        prompt.Name = "  Fresh ";
        Assert.IsTrue(prompt.ConfirmCommand.CanExecute(null));
        prompt.ConfirmCommand.Execute(null);
        Assert.AreEqual("Fresh", confirmed);
    }

    [TestMethod]
    public void ARenameStartsAllowed()
    {
        var (_, store, _) = Make();

        var prompt = LayoutNameViewModel.ForRename(store, Strings.LayoutDefaultName);

        Assert.IsTrue(prompt.ConfirmCommand.CanExecute(null));
    }

    [TestMethod]
    public void ANameIsConfirmedOnce()
    {
        var (_, store, _) = Make();
        var prompt = LayoutNameViewModel.ForNew(store);
        int confirmations = 0;
        prompt.Confirmed += (_, _) => confirmations++;
        prompt.Name = "Fresh";

        prompt.ConfirmCommand.Execute(null);
        prompt.ConfirmCommand.Execute(null);

        Assert.AreEqual(1, confirmations);
        Assert.IsFalse(prompt.ConfirmCommand.CanExecute(null));
    }
}
