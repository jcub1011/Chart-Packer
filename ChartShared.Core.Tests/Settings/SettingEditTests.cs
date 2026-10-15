using System.ComponentModel;
using ChartShared.Core.Settings;

namespace ChartShared.Core.Tests.Settings;

[TestClass]
public sealed class SettingEditTests
{
    private static readonly SettingCategory Category = new("test", "Test");

    private int _depth = 2;
    private int _depthWrites;
    private double _volume = 0.5;
    private string? _folder;
    private char _choice = 'b';

    private IntegerSetting Depth() => new()
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
    };

    private RangeSetting Volume() => new()
    {
        Id = "volume",
        Category = Category,
        Label = "Volume",
        DefaultValue = 1,
        Minimum = 0,
        Maximum = 1,
        Get = () => _volume,
        Set = v => _volume = v,
    };

    private FolderSetting Folder() => new()
    {
        Id = "folder",
        Category = Category,
        Label = "Folder",
        DefaultValue = null,
        Get = () => _folder,
        Set = v => _folder = v,
    };

    private ChoiceSetting<char> Choice() => new()
    {
        Id = "choice",
        Category = Category,
        Label = "Choice",
        DefaultValue = 'a',
        Choices = [new('a', "A"), new('b', "B"), new('c', "C")],
        Get = () => _choice,
        Set = v => _choice = v,
    };

    private static List<string?> RecordChanges(INotifyPropertyChanged source)
    {
        var names = new List<string?>();
        source.PropertyChanged += (_, e) => names.Add(e.PropertyName);
        return names;
    }

    [TestMethod]
    public void StartsFromTheLiveValue()
    {
        _depth = 7;
        var edit = (SettingEdit<int>)Depth().BeginEdit();

        Assert.AreEqual(7, edit.Value);
        Assert.IsFalse(edit.IsModified);
        Assert.IsFalse(edit.IsDefault);
    }

    [TestMethod]
    public void ChangingAndChangingBackTogglesIsModified()
    {
        var edit = (SettingEdit<int>)Depth().BeginEdit();

        edit.Value = 5;
        Assert.IsTrue(edit.IsModified);

        edit.Value = 2;
        Assert.IsFalse(edit.IsModified);
        Assert.AreEqual(0, _depthWrites);
    }

    [TestMethod]
    public void ApplyWritesOnceAndClearsIsModified()
    {
        var edit = (SettingEdit<int>)Depth().BeginEdit();
        edit.Value = 5;

        Assert.IsTrue(edit.Apply());
        Assert.AreEqual(5, _depth);
        Assert.AreEqual(1, _depthWrites);
        Assert.IsFalse(edit.IsModified);

        Assert.IsFalse(edit.Apply());
        Assert.AreEqual(1, _depthWrites);
    }

    [TestMethod]
    public void ApplyTakesTheValueReadBack()
    {
        // The owner of the live value rounds whatever it's given.
        double live = 0.5;
        var definition = new RangeSetting
        {
            Id = "rounded",
            Category = Category,
            Label = "Rounded",
            DefaultValue = 1,
            Minimum = 0,
            Maximum = 1,
            Get = () => live,
            Set = v => live = Math.Round(v, 1),
        };
        var edit = (SettingEdit<double>)definition.BeginEdit();

        edit.Value = 0.26;
        edit.Apply();

        Assert.AreEqual(0.3, edit.Value);
        Assert.IsFalse(edit.IsModified);
    }

    [TestMethod]
    public void RevertRestoresTheLiveValue()
    {
        var edit = (SettingEdit<int>)Depth().BeginEdit();
        edit.Value = 9;

        edit.Revert();

        Assert.AreEqual(2, edit.Value);
        Assert.IsFalse(edit.IsModified);
    }

    [TestMethod]
    public void ResetToDefaultStagesTheDefaultWithoutWriting()
    {
        _depth = 6;
        var edit = (SettingEdit<int>)Depth().BeginEdit();

        edit.ResetToDefault();

        Assert.AreEqual(2, edit.Value);
        Assert.IsTrue(edit.IsDefault);
        Assert.IsTrue(edit.IsModified);
        Assert.AreEqual(6, _depth);
    }

    [TestMethod]
    [DataRow(-3, 0)]
    [DataRow(4, 4)]
    [DataRow(99, 10)]
    public void IntegerIsClamped(int staged, int expected)
    {
        var edit = (SettingEdit<int>)Depth().BeginEdit();

        edit.Value = staged;

        Assert.AreEqual(expected, edit.Value);
    }

    [TestMethod]
    [DataRow(-0.5, 0)]
    [DataRow(2.0, 1)]
    [DataRow(double.NaN, 1)]
    [DataRow(double.PositiveInfinity, 1)]
    public void RangeIsClampedAndNonFiniteBecomesTheDefault(double staged, double expected)
    {
        var edit = (SettingEdit<double>)Volume().BeginEdit();

        edit.Value = staged;

        Assert.AreEqual(expected, edit.Value);
    }

    [TestMethod]
    public void RangeFormatsWithItsFormatter()
    {
        var definition = new RangeSetting
        {
            Id = "percent",
            Category = Category,
            Label = "Percent",
            DefaultValue = 1,
            Minimum = 0,
            Maximum = 1,
            Format = v => $"{v * 100:0}%",
            Get = () => 1,
            Set = _ => { },
        };

        Assert.AreEqual("25%", definition.FormatValue(0.25));
    }

    [TestMethod]
    [DataRow("")]
    [DataRow("   ")]
    public void BlankFolderBecomesNull(string staged)
    {
        _folder = @"C:\out";
        var edit = (SettingEdit<string?>)Folder().BeginEdit();

        edit.Value = staged;

        Assert.IsNull(edit.Value);
        Assert.IsTrue(edit.IsDefault);
    }

    [TestMethod]
    public void UnknownChoiceBecomesTheDefault()
    {
        var edit = (SettingEdit<char>)Choice().BeginEdit();

        edit.Value = 'z';

        Assert.AreEqual('a', edit.Value);
    }

    [TestMethod]
    public void ChoiceEditIsSeenByIndex()
    {
        var edit = (IChoiceEdit)Choice().BeginEdit();

        Assert.AreSequenceEqual(["A", "B", "C"], [.. edit.ChoiceLabels]);
        Assert.AreEqual(1, edit.SelectedIndex);

        edit.SelectedIndex = 2;
        Assert.AreEqual('c', ((SettingEdit<char>)edit).Value);

        edit.SelectedIndex = 7;
        Assert.AreEqual(2, edit.SelectedIndex);
    }

    [TestMethod]
    public void RaisesValueAndFlagsOnlyWhenTheyChange()
    {
        _depth = 5;
        var edit = (SettingEdit<int>)Depth().BeginEdit();
        var changes = RecordChanges(edit);

        edit.Value = 2;
        Assert.AreSequenceEqual(["Value", "IsModified", "IsDefault"], changes);

        changes.Clear();
        edit.Value = 2;
        Assert.IsEmpty(changes);

        edit.Value = 3;
        Assert.AreSequenceEqual(["Value", "IsDefault"], changes);
    }

    [TestMethod]
    public void ChoiceRaisesSelectedIndex()
    {
        var edit = (SettingEdit<char>)Choice().BeginEdit();
        var changes = RecordChanges(edit);

        edit.Value = 'c';

        CollectionAssert.Contains(changes, "SelectedIndex");
    }
}
