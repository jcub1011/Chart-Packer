using ChartPacker.Core.Settings;

namespace ChartPacker.Core.Tests.Settings;

[TestClass]
public sealed class SettingsSessionTests
{
    private static readonly SettingCategory Category = new("test", "Test");

    private bool _flat;
    private int _flatWrites;
    private int _depth = 2;
    private int _depthWrites;

    private SettingsCatalog Catalog() => new(
        [Category],
        [
            new ToggleSetting
            {
                Id = "flat",
                Category = Category,
                Label = "Flat",
                DefaultValue = false,
                Get = () => _flat,
                Set = v =>
                {
                    _flat = v;
                    _flatWrites++;
                },
            },
            new IntegerSetting
            {
                Id = "depth",
                Category = Category,
                Label = "Depth",
                DefaultValue = 2,
                Minimum = 0,
                Maximum = 10,
                Get = () => _depth,
                Set = v =>
                {
                    _depth = v;
                    _depthWrites++;
                },
            },
        ]);

    [TestMethod]
    public void HasOneEditPerDefinitionInCatalogOrder()
    {
        var catalog = Catalog();
        var session = new SettingsSession(catalog);

        CollectionAssert.AreEqual(catalog.Definitions.ToArray(), session.Edits.Select(e => e.Definition).ToArray());
        Assert.AreSame(session.Edits[1], session.EditFor(catalog.Definitions[1]));
    }

    [TestMethod]
    public void HasChangesFollowsTheEditsAndIsRaised()
    {
        var session = new SettingsSession(Catalog());
        int raised = 0;
        session.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SettingsSession.HasChanges))
                raised++;
        };
        var flat = (SettingEdit<bool>)session.Edits[0];
        var depth = (SettingEdit<int>)session.Edits[1];

        Assert.IsFalse(session.HasChanges);

        flat.Value = true;
        depth.Value = 4;
        Assert.IsTrue(session.HasChanges);
        Assert.AreEqual(1, raised);

        flat.Value = false;
        Assert.IsTrue(session.HasChanges);

        depth.Revert();
        Assert.IsFalse(session.HasChanges);
        Assert.AreEqual(2, raised);
    }

    [TestMethod]
    public void ApplyWritesOnlyModifiedSettings()
    {
        var session = new SettingsSession(Catalog());
        ((SettingEdit<int>)session.Edits[1]).Value = 4;

        Assert.IsTrue(session.Apply());

        Assert.AreEqual(4, _depth);
        Assert.AreEqual(1, _depthWrites);
        Assert.AreEqual(0, _flatWrites);
        Assert.IsFalse(session.HasChanges);
    }

    [TestMethod]
    public void ASettingIsEnabledOnlyWhileTheToggleItRequiresIsStagedOn()
    {
        var (session, waveforms, lanes) = RequiringSession(waveformsOn: true);

        Assert.IsTrue(lanes.IsEnabled);
        Assert.AreSame(waveforms.Definition, lanes.Requirement);

        var raised = new List<string?>();
        lanes.PropertyChanged += (_, e) => raised.Add(e.PropertyName);
        waveforms.Value = false;

        Assert.IsFalse(lanes.IsEnabled);
        CollectionAssert.Contains(raised, nameof(SettingEdit.IsEnabled));

        // Cancelling puts the toggle back, and the setting with it.
        waveforms.Revert();
        Assert.IsTrue(lanes.IsEnabled);
        Assert.IsFalse(session.HasChanges);
    }

    [TestMethod]
    public void ASettingStartsLockedWhenTheToggleItRequiresIsOff()
    {
        var (_, _, lanes) = RequiringSession(waveformsOn: false);

        Assert.IsFalse(lanes.IsEnabled);
    }

    [TestMethod]
    public void ASettingWithoutARequirementIsAlwaysEnabled()
    {
        var session = new SettingsSession(Catalog());

        Assert.IsTrue(session.Edits.All(e => e.IsEnabled && e.Requirement is null));
    }

    [TestMethod]
    public void LockingASettingKeepsItsValue()
    {
        var (session, waveforms, lanes) = RequiringSession(waveformsOn: true);

        waveforms.Value = false;
        session.Apply();

        Assert.IsTrue(lanes.Value);
    }

    private static (SettingsSession Session, SettingEdit<bool> Waveforms, SettingEdit<bool> Lanes) RequiringSession(bool waveformsOn)
    {
        bool waveforms = waveformsOn, lanes = true;
        var session = new SettingsSession(new SettingsCatalog(
            [Category],
            [
                new ToggleSetting { Id = "waveforms", Category = Category, Label = "Show waveforms", DefaultValue = true, Get = () => waveforms, Set = v => waveforms = v },
                new ToggleSetting { Id = "lanes", Category = Category, Label = "Lane waveforms", RequiresId = "waveforms", DefaultValue = true, Get = () => lanes, Set = v => lanes = v },
            ]));
        return (session, (SettingEdit<bool>)session.Edits[0], (SettingEdit<bool>)session.Edits[1]);
    }

    [TestMethod]
    public void ApplyWithNoChangesWritesNothing()
    {
        var session = new SettingsSession(Catalog());

        Assert.IsFalse(session.Apply());

        Assert.AreEqual(0, _depthWrites);
        Assert.AreEqual(0, _flatWrites);
    }
}
