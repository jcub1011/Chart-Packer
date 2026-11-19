using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using ChartPacker.Resources;

namespace ChartPacker.Services
{
    /// <summary>Starts the Chart Builder executable installed beside Chart Packer's own.</summary>
    /// <remarks>
    /// Both apps publish into one folder, so the same lookup works installed and from the portable zip. In
    /// Debug, each app builds into its own folder, and Chart Packer's build records where Chart Builder's is.
    /// </remarks>
    public sealed class ChartBuilderLauncher : IChartBuilderLauncher
    {
        private static readonly string ExecutableName = OperatingSystem.IsWindows() ? "ChartBuilder.exe" : "ChartBuilder";

        /// <summary>Chart Builder's executable, next to this process's, whether or not it's there.</summary>
        public static string ExecutablePath { get; } = FindExecutable();

        public string? Start(IReadOnlyList<string> arguments)
        {
            if (!File.Exists(ExecutablePath))
                return string.Format(CultureInfo.CurrentCulture, Strings.ChartBuilderMissingFormat, ExecutablePath);

            // Chart Builder runs on its own: detached, so it outlives Chart Packer and whatever started that, with nothing
            // waiting for it or reading its output.
            var start = new ProcessStartInfo(ExecutablePath) { UseShellExecute = false, StartDetached = true };
            foreach (string argument in arguments)
                start.ArgumentList.Add(argument);

            try
            {
                Process.StartAndForget(start);
                return null;
            }
            catch (Exception ex) when (ex is Win32Exception or IOException or UnauthorizedAccessException)
            {
                return string.Format(CultureInfo.CurrentCulture, Strings.ChartBuilderStartFailedFormat, ex.Message);
            }
        }

        private static string FindExecutable()
        {
            string beside = Path.Combine(Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory, ExecutableName);
#if DEBUG
            // Debug builds keep each app in its own output folder; ChartPacker.csproj records Chart Builder's.
            if (!File.Exists(beside)
                && typeof(ChartBuilderLauncher).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
                    .FirstOrDefault(a => a.Key == "ChartBuilderDebugFolder")?.Value is { } folder)
            {
                return Path.Combine(folder, ExecutableName);
            }
#endif
            return beside;
        }
    }
}
