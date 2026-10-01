using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using ChartPacker.Core.Library;

namespace ChartPacker.Services
{
    /// <summary>Loads and saves <see cref="AppSettings"/> as JSON.</summary>
    /// <remarks>
    /// The file path is always passed in, never defaulted, so nothing but the composition root
    /// (which passes <see cref="Core.UserConfigPaths.SettingsFile"/>) can touch the real settings.
    /// Tests must pass a temp file.
    /// </remarks>
    public sealed class SettingsService
    {
        private readonly string _filePath;

        private SettingsService(string filePath, AppSettings settings)
        {
            _filePath = filePath;
            Settings = settings;
        }

        /// <summary>The slowest <see cref="PlaybackSpeed"/>: 1%.</summary>
        public const double MinPlaybackSpeed = 0.01;

        /// <summary>The fastest <see cref="PlaybackSpeed"/>: 1000%.</summary>
        public const double MaxPlaybackSpeed = 10;

        public AppSettings Settings { get; }

        /// <summary>
        /// How fast the players play, as a fraction: 1 is the audio's own speed. Shared by every song and player, but
        /// kept for this run only; never written to the settings file, so each run starts at 1.
        /// </summary>
        public double PlaybackSpeed { get; set; } = 1;

        /// <summary>Loads settings from <paramref name="filePath"/>, falling back to defaults when it's missing or unreadable.</summary>
        public static SettingsService Load(string filePath)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
            AppSettings? settings = null;

            try
            {
                if (File.Exists(filePath))
                {
                    using var stream = File.OpenRead(filePath);
                    settings = JsonSerializer.Deserialize(stream, AppSettingsJsonContext.Default.AppSettings);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                Trace.TraceWarning($"Ignoring unreadable settings file '{filePath}': {ex.Message}");
            }

            settings ??= new AppSettings();
            Sanitize(settings);
            return new SettingsService(filePath, settings);
        }

        /// <summary>Writes settings to a temp file and renames it into place.</summary>
        public void Save()
        {
            var tempPath = _filePath + ".tmp";
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
                using (var stream = File.Create(tempPath))
                {
                    JsonSerializer.Serialize(stream, Settings, AppSettingsJsonContext.Default.AppSettings);
                }
                File.Move(tempPath, _filePath, overwrite: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Trace.TraceWarning($"Couldn't save settings to '{_filePath}': {ex.Message}");
                try { File.Delete(tempPath); } catch (Exception) { }
            }
        }

        private static void Sanitize(AppSettings settings)
        {
            if (!double.IsFinite(settings.LibraryPaneWidth) || settings.LibraryPaneWidth < AppSettings.MinLibraryPaneWidth)
                settings.LibraryPaneWidth = AppSettings.DefaultLibraryPaneWidth;

            settings.ScanMaxDepth = Math.Clamp(settings.ScanMaxDepth, 0, AppSettings.MaxScanMaxDepth);

            if (settings.ScanParallelism < AppSettings.MinScanParallelism)
                settings.ScanParallelism = AppSettings.DefaultScanParallelism;

            if (string.IsNullOrWhiteSpace(settings.LastFolder))
                settings.LastFolder = null;

            if (string.IsNullOrWhiteSpace(settings.OutputFolder))
                settings.OutputFolder = null;

            if (string.IsNullOrWhiteSpace(settings.DownloadFolder))
                settings.DownloadFolder = null;

            if (!Enum.IsDefined(settings.OverwritePolicy))
                settings.OverwritePolicy = AppSettings.DefaultOverwritePolicy;

            if (!Enum.IsDefined(settings.DedupKeepPreference))
                settings.DedupKeepPreference = AppSettings.DefaultDedupKeepPreference;

            if (!Enum.IsDefined(settings.WaveformFidelity))
                settings.WaveformFidelity = AppSettings.DefaultWaveformFidelity;

            if (!Enum.IsDefined(settings.ScrubStep))
                settings.ScrubStep = AppSettings.DefaultScrubStep;

            if (!Enum.IsDefined(settings.LibrarySortField))
                settings.LibrarySortField = LibrarySortField.ScanOrder;

            settings.MasterVolume = double.IsFinite(settings.MasterVolume)
                ? Math.Clamp(settings.MasterVolume, 0, 1)
                : AppSettings.DefaultMasterVolume;

            settings.HighwayNoteSpeed = double.IsFinite(settings.HighwayNoteSpeed)
                ? Math.Clamp(settings.HighwayNoteSpeed, AppSettings.MinHighwayNoteSpeed, AppSettings.MaxHighwayNoteSpeed)
                : AppSettings.DefaultHighwayNoteSpeed;

            settings.FineScrubMilliseconds = double.IsFinite(settings.FineScrubMilliseconds)
                ? Math.Clamp(settings.FineScrubMilliseconds, AppSettings.MinFineScrubMilliseconds, AppSettings.MaxFineScrubMilliseconds)
                : AppSettings.DefaultFineScrubMilliseconds;
        }
    }
}
