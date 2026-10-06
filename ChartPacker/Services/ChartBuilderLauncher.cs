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
        /// <summary>
        /// Whether Chart Builder can open a song yet. Edit chart stays disabled until it can: it's turned on in
        /// milestone CM2, when Chart Builder gets its editor (charter-spec §13).
        /// </summary>
        public const bool CanOpenSongs = false;

        private static readonly string ExecutableName = OperatingSystem.IsWindows() ? "ChartBuilder.exe" : "ChartBuilder";

        /// <summary>Chart Builder's executable, next to this process's, whether or not it's there.</summary>
        public static string ExecutablePath { get; } = FindExecutable();

        public string? Start(IReadOnlyList<string> arguments)
        {
            if (!File.Exists(ExecutablePath))
                return string.Format(CultureInfo.CurrentCulture, Strings.ChartBuilderMissingFormat, ExecutablePath);

            var start = new ProcessStartInfo(ExecutablePath) { UseShellExecute = false };
            foreach (string argument in arguments)
                start.ArgumentList.Add(argument);

            try
            {
                // Chart Builder runs on its own; nothing waits for it or reads its output.
                Process.Start(start)?.Dispose();
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
