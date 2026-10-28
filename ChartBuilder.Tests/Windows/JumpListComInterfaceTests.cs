using System.Reflection;
using ChartBuilder.Services;

namespace ChartBuilder.Tests.Windows;

/// <summary>
/// The Jump List's hand-declared shell interfaces. A COM call goes by its method's place in the vtable, so a method moved or
/// added in the middle would call another shell method, and corrupt memory rather than fail to build. These pin the order
/// the shell's headers give; nothing here touches the real shell.
/// </summary>
[TestClass]
public sealed class JumpListComInterfaceTests
{
    [TestMethod]
    public void CustomDestinationListKeepsTheShellsOrder() => AssertOrder(
        typeof(ICustomDestinationList),
        "SetAppID", "BeginList", "AppendCategory", "AppendKnownCategory", "AddUserTasks", "CommitList",
        "GetRemovedDestinations", "DeleteList", "AbortList");

    [TestMethod]
    public void ObjectArrayKeepsTheShellsOrder() => AssertOrder(typeof(IObjectArray), "GetCount", "GetAt");

    [TestMethod]
    public void ObjectCollectionExtendsTheArrayAndAddsItsOwnInOrder()
    {
        CollectionAssert.Contains(typeof(IObjectCollection).GetInterfaces(), typeof(IObjectArray));
        AssertOrder(typeof(IObjectCollection), "AddObject", "AddFromArray", "RemoveObjectAt", "Clear");
    }

    [TestMethod]
    public void ShellLinkKeepsTheShellsOrder() => AssertOrder(
        typeof(IShellLinkW),
        "GetPath", "GetIDList", "SetIDList", "GetDescription", "SetDescription", "GetWorkingDirectory", "SetWorkingDirectory",
        "GetArguments", "SetArguments", "GetHotkey", "SetHotkey", "GetShowCmd", "SetShowCmd", "GetIconLocation",
        "SetIconLocation", "SetRelativePath", "Resolve", "SetPath");

    [TestMethod]
    public void PropertyStoreKeepsTheShellsOrder() => AssertOrder(
        typeof(IPropertyStore), "GetCount", "GetAt", "GetValue", "SetValue", "Commit");

    // Declaration order is the metadata's (tokens go up as the compiler meets each method). The generator re-declares a base
    // interface's methods on the interface that extends it, after its own, so those are left out: they aren't its own slots.
    private static void AssertOrder(Type type, params string[] expected)
    {
        var inherited = type.GetInterfaces().SelectMany(i => i.GetMethods()).Select(m => m.Name).ToHashSet();
        string[] declared = type
            .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly)
            .Where(m => !inherited.Contains(m.Name))
            .OrderBy(m => m.MetadataToken)
            .Select(m => m.Name)
            .ToArray();

        CollectionAssert.AreEqual(expected, declared, $"{type.Name}'s methods, in the order declared");
    }
}
