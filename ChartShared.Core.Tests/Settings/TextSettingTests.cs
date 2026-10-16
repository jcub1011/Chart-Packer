using ChartShared.Core.Settings;

namespace ChartShared.Core.Tests.Settings;

[TestClass]
public sealed class TextSettingTests
{
    private static readonly SettingCategory Category = new("test", "Test");

    private string _name = "";

    private TextSetting Name() => new()
    {
        Id = "name",
        Category = Category,
        Label = "Name",
        DefaultValue = "",
        ForbiddenCharacters = ";",
        Get = () => _name,
        Set = v => _name = v,
    };

    [TestMethod]
    public void SpacesAreKeptWhileTyping()
    {
        var edit = (SettingEdit<string>)Name().BeginEdit();

        edit.Value = "Jo ";
        Assert.AreEqual("Jo ", edit.Value);

        edit.Value = " Jo Bloggs ";
        Assert.AreEqual(" Jo Bloggs ", edit.Value);
    }

    [TestMethod]
    public void ForbiddenCharactersAndLineBreaksAreDropped()
    {
        var edit = (SettingEdit<string>)Name().BeginEdit();

        edit.Value = "a;b\r\nc\nd";

        Assert.AreEqual("abcd", edit.Value);
        Assert.IsTrue(edit.Apply());
        Assert.AreEqual("abcd", _name);
    }

    [TestMethod]
    public void NullIsEmpty() => Assert.AreEqual("", Name().Coerce(null!));
}
