using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using ChartShared.Core.Charting.Difficulty;
using ChartShared.Core.Launching;

namespace ChartBuilder.Services
{
    /// <summary>
    /// <c>ChartBuilder --difficulty-report</c> (<see cref="DifficultyReportArgs"/>): rates every song under a folder with
    /// the difficulty model and writes the CSV, with no window. Progress and problems go to the console it was run from.
    /// </summary>
    /// <remarks>
    /// Chart Builder is a windowed app, so it attaches to the console that started it to say anything there. A shell
    /// doesn't wait for a windowed app; in PowerShell, <c>Start-Process -Wait</c> or piping the output (<c>| Out-Host</c>)
    /// makes it.
    /// </remarks>
    internal static class DifficultyReportCommand
    {
        // Exit codes.
        private const int Done = 0;
        private const int BadArguments = 1;
        private const int Failed = 2;

        private const int AttachParentProcess = -1;

        [DllImport("kernel32.dll")]
        private static extern bool AttachConsole(int processId);

        public static int Run(string[] args)
        {
            if (OperatingSystem.IsWindows())
                AttachConsole(AttachParentProcess);
            var parsed = DifficultyReportArgs.Parse(args);
            if (parsed.Problem is { } problem)
            {
                Console.Error.WriteLine(problem);
                Console.Error.WriteLine($"Usage: {DifficultyReportArgs.Usage}");
                return BadArguments;
            }

            if (!Directory.Exists(parsed.SongsFolder))
            {
                Console.Error.WriteLine($"There's no folder at {parsed.SongsFolder}.");
                return BadArguments;
            }

            try
            {
                return RunAsync(parsed).GetAwaiter().GetResult();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Console.Error.WriteLine($"The report couldn't be written to {parsed.OutputFile}: {ex.Message}");
                return Failed;
            }
        }

        private static async Task<int> RunAsync(DifficultyReportArgs args)
        {
            Console.WriteLine($"Rating the songs in {args.SongsFolder}…");
            var rows = await DifficultyReport.BuildAsync(args.SongsFolder, args.Depth, Math.Min(Environment.ProcessorCount, 8), new ConsoleProgress());

            string? folder = Path.GetDirectoryName(args.OutputFile);
            if (!string.IsNullOrEmpty(folder))
                Directory.CreateDirectory(folder);
            await using (var writer = new StreamWriter(args.OutputFile, append: false, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true)))
                DifficultyReport.WriteCsv(writer, rows);

            int problems = 0;
            foreach (var row in rows)
            {
                if (row.Problem is { } problem)
                {
                    problems++;
                    Console.Error.WriteLine($"{row.SongPath}: {problem}");
                }
            }

            Console.WriteLine($"Wrote {rows.Count} songs to {args.OutputFile}{(problems > 0 ? $"; {problems} couldn't be read" : "")}.");
            return Done;
        }

        /// <summary>
        /// A line every tenth of the way, so a big library doesn't flood the console. Reported from several threads at
        /// once, so in order under a lock rather than posted as <see cref="Progress{T}"/> would.
        /// </summary>
        private sealed class ConsoleProgress : IProgress<(int Done, int Total)>
        {
            private readonly object _gate = new();
            private int _last = -1;

            public void Report((int Done, int Total) value)
            {
                lock (_gate)
                {
                    int step = Math.Max(1, value.Total / 10);
                    int tenth = value.Done / step;
                    if (tenth <= _last && value.Done != value.Total)
                        return;
                    _last = tenth;
                    Console.WriteLine($"{value.Done} of {value.Total} songs");
                }
            }
        }
    }
}
