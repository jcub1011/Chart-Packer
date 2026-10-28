using ChartShared.Core.Diagnostics;

namespace ChartShared.Core.Tests.Diagnostics;

[TestClass]
public sealed class CrashReportTests
{
    private static readonly DateTimeOffset When = new(2026, 10, 28, 14, 5, 9, TimeSpan.FromHours(-4));

    [TestMethod]
    public void DetailsNameTheAppAndTheSystem()
    {
        string details = CrashReport.Details("Chart Builder", "1.0.0", "Windows 10", "10.0.0 X64", When, Thrown());

        StringAssert.StartsWith(details, "Chart Builder 1.0.0");
        StringAssert.Contains(details, "OS: Windows 10");
        StringAssert.Contains(details, ".NET: 10.0.0 X64");
        StringAssert.Contains(details, "Time: 2026-10-28 14:05:09 -04:00");
    }

    [TestMethod]
    public void DetailsHoldTheExceptionItsStackAndItsInnerException()
    {
        string details = CrashReport.Details("Chart Builder", "1.0.0", "Windows 10", "10.0.0 X64", When, Thrown());

        StringAssert.Contains(details, "System.InvalidOperationException: The lane had no notes");
        StringAssert.Contains(details, "System.FormatException: Bad tick");
        StringAssert.Contains(details, nameof(Thrown));
    }

    [TestMethod]
    public void TheIssueIsTitledWithTheAppTheVersionAndTheType()
    {
        var url = CrashReport.IssueUrl("Chart Builder", "1.0.0-beta.1", Thrown());

        StringAssert.StartsWith(url.AbsoluteUri, CrashReport.NewIssueUrl + "?title=");
        StringAssert.Contains(url.AbsoluteUri, "title=Chart%20Builder%201.0.0-beta.1%3A%20InvalidOperationException&body=");
    }

    [TestMethod]
    public void TheIssueLeavesOutTheMessageAndTheStack()
    {
        var url = CrashReport.IssueUrl("Chart Builder", "1.0.0", Thrown(@"C:\Users\someone\Songs\a.mid"));

        string decoded = Uri.UnescapeDataString(url.AbsoluteUri);
        Assert.DoesNotContain("someone", decoded);
        Assert.DoesNotContain(nameof(Thrown), decoded);
        Assert.DoesNotContain("FormatException", decoded);
    }

    // An exception as the dispatcher would have it: thrown, so it has a stack, with an inner one.
    private static InvalidOperationException Thrown(string message = "The lane had no notes")
    {
        try
        {
            throw new InvalidOperationException(message, new FormatException("Bad tick"));
        }
        catch (InvalidOperationException ex)
        {
            return ex;
        }
    }
}
