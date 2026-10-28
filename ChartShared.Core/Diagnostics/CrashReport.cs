using System.Globalization;
using System.Text;

namespace ChartShared.Core.Diagnostics;

/// <summary>
/// What the crash dialog gives for an exception nothing caught: its details to copy, and a new issue on GitHub to report
/// it in. Both are in English whatever the app's language, as they're read by its developer.
/// </summary>
public static class CrashReport
{
    /// <summary>Where a new issue is opened on GitHub.</summary>
    public const string NewIssueUrl = "https://github.com/jcub1011/Chart-Packer/issues/new";

    /// <summary>
    /// The app, its version and the system it ran on, when, then <paramref name="exception"/> with its stack and any
    /// inner exceptions.
    /// </summary>
    /// <param name="os">The system, e.g. <c>RuntimeInformation.OSDescription</c>.</param>
    /// <param name="runtime">The .NET version and architecture, e.g. <c>10.0.0 X64</c>.</param>
    public static string Details(string app, string version, string os, string runtime, DateTimeOffset when, Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        var text = new StringBuilder();
        text.Append(CultureInfo.InvariantCulture, $"{app} {version}").AppendLine();
        text.Append(CultureInfo.InvariantCulture, $"OS: {os}").AppendLine();
        text.Append(CultureInfo.InvariantCulture, $".NET: {runtime}").AppendLine();
        text.Append(CultureInfo.InvariantCulture, $"Time: {when:yyyy-MM-dd HH:mm:ss zzz}").AppendLine();
        text.AppendLine();
        text.Append(exception.ToString());
        return text.ToString();
    }

    /// <summary>
    /// A new issue titled with the app, its version and the exception's type. Its body asks for the details, which the
    /// dialog puts on the clipboard: the exception's message and stack aren't in the address, as a message can name the
    /// user's files and a stack is too long for one.
    /// </summary>
    public static Uri IssueUrl(string app, string version, Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        string title = $"{app} {version}: {exception.GetType().Name}";
        const string body =
            "**What were you doing when the error came up?**\n\n\n\n" +
            "**Details**\n\n" +
            "The error's details are on your clipboard: paste them between the lines below.\n\n" +
            "```\n\n```\n";
        return new Uri($"{NewIssueUrl}?title={Uri.EscapeDataString(title)}&body={Uri.EscapeDataString(body)}");
    }
}
