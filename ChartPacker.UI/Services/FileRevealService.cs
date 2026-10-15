using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using ChartPacker.Resources;

namespace ChartPacker.UI.Services
{
    /// <summary>
    /// Shows files in Explorer with them selected, one window per folder; in Finder on macOS; and
    /// elsewhere opens their folder, as there's no one way to select a file.
    /// </summary>
    public sealed class FileRevealService(Func<TopLevel?> topLevel) : IFileRevealService
    {
        /// <summary>The name of the menu item that reveals a file, after the platform's file manager.</summary>
        public static string RevealLabel =>
            OperatingSystem.IsWindows() ? SharedStrings.RevealInExplorer
            : OperatingSystem.IsMacOS() ? SharedStrings.RevealInFinder
            : SharedStrings.RevealInFolder;

        public void Reveal(IReadOnlyList<string> paths)
        {
            var existing = paths
                .Select(p => Path.TrimEndingDirectorySeparator(Path.GetFullPath(p)))
                .Where(p => File.Exists(p) || Directory.Exists(p))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (existing.Count == 0)
                return;

            try
            {
                if (OperatingSystem.IsWindows())
                {
                    foreach (var folder in existing.GroupBy(p => Path.GetDirectoryName(p) ?? p, StringComparer.OrdinalIgnoreCase))
                        SelectInExplorer(folder.Key, [.. folder]);
                }
                else if (OperatingSystem.IsMacOS())
                {
                    var start = new ProcessStartInfo("open") { UseShellExecute = false };
                    start.ArgumentList.Add("-R");
                    foreach (string path in existing)
                        start.ArgumentList.Add(path);
                    Process.Start(start)?.Dispose();
                }
                else if (topLevel()?.Launcher is { } launcher)
                {
                    foreach (string folder in existing.Select(p => Path.GetDirectoryName(p) ?? p).Distinct(StringComparer.Ordinal))
                        _ = launcher.LaunchDirectoryInfoAsync(new DirectoryInfo(folder));
                }
            }
            catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException)
            {
                Trace.TraceWarning($"Couldn't show '{existing[0]}': {ex.Message}");
            }
        }

        public void OpenFolder(string folder)
        {
            if (!Directory.Exists(folder))
                return;

            try
            {
                if (topLevel()?.Launcher is { } launcher)
                    _ = launcher.LaunchDirectoryInfoAsync(new DirectoryInfo(folder));
            }
            catch (Exception ex) when (ex is InvalidOperationException or IOException)
            {
                Trace.TraceWarning($"Couldn't open '{folder}': {ex.Message}");
            }
        }

        /// <summary>Opens <paramref name="folder"/> in Explorer with <paramref name="items"/>, which are in it, selected.</summary>
        private static void SelectInExplorer(string folder, IReadOnlyList<string> items)
        {
            IntPtr folderId = IdOf(folder);
            if (folderId == IntPtr.Zero)
                return;

            var itemIds = items.Select(IdOf).Where(id => id != IntPtr.Zero).ToArray();
            IntPtr array = Marshal.AllocHGlobal(IntPtr.Size * Math.Max(1, itemIds.Length));
            try
            {
                Marshal.Copy(itemIds, 0, array, itemIds.Length);
                int result = SHOpenFolderAndSelectItems(folderId, (uint)itemIds.Length, array, 0);
                if (result < 0)
                    Trace.TraceWarning($"Couldn't show '{folder}' in Explorer: 0x{result:X8}");
            }
            finally
            {
                Marshal.FreeHGlobal(array);
                foreach (IntPtr id in itemIds)
                    ILFree(id);
                ILFree(folderId);
            }
        }

        /// <summary>The shell's item ID list for <paramref name="path"/>, to free with <see cref="ILFree"/>; zero when it can't be made.</summary>
        private static IntPtr IdOf(string path)
        {
            IntPtr text = Marshal.StringToHGlobalUni(path);
            try
            {
                return ILCreateFromPathW(text);
            }
            finally
            {
                Marshal.FreeHGlobal(text);
            }
        }

        [DllImport("shell32.dll")]
        private static extern IntPtr ILCreateFromPathW(IntPtr path);

        [DllImport("shell32.dll")]
        private static extern void ILFree(IntPtr idList);

        [DllImport("shell32.dll")]
        private static extern int SHOpenFolderAndSelectItems(IntPtr folder, uint count, IntPtr items, uint flags);
    }
}
