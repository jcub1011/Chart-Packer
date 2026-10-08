using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using ChartPacker.Core.Scanning;
using ChartPacker.UI.Services;

namespace ChartPacker.Services
{
    /// <summary>
    /// Warns before Chart Packer writes to a song that Chart Builder has open, which may overwrite the change when it
    /// saves the song (charter-spec §4.8). The user can go ahead or cancel.
    /// </summary>
    public static class ChartBuilderSongs
    {
        /// <summary>
        /// Asks whether to write to <paramref name="songs"/> when Chart Builder has any of them open; true when it has
        /// none, or when there's no one to ask.
        /// </summary>
        /// <param name="isOpen">Whether Chart Builder has the song at a path open (<see cref="SongLock.IsHeld"/>).</param>
        /// <param name="oneFormat">The question for one song, given its name.</param>
        /// <param name="manyFormat">The question for several, given how many.</param>
        public static async Task<bool> ConfirmWriteAsync(
            IConfirmationService? confirmation,
            Func<string, bool>? isOpen,
            IEnumerable<SongEntry> songs,
            string oneFormat,
            string manyFormat,
            string confirmLabel)
        {
            if (confirmation is null || isOpen is null)
                return true;

            var open = songs.Where(s => isOpen(s.FullPath)).ToList();
            if (open.Count == 0)
                return true;

            string message = open.Count == 1
                ? string.Format(CultureInfo.CurrentCulture, oneFormat, open[0].DisplayName)
                : string.Format(CultureInfo.CurrentCulture, manyFormat, open.Count);
            return await confirmation.ConfirmAsync(Resources.Strings.ChartBuilderOpenTitle, message, confirmLabel);
        }

        /// <summary>
        /// Asks whether to write to the song at <paramref name="path"/>, such as a <c>.sng</c> a merge overwrites, when
        /// Chart Builder has it open; true when it hasn't, or when there's no one to ask.
        /// </summary>
        /// <param name="isOpen">Whether Chart Builder has the song at a path open (<see cref="SongLock.IsHeld"/>).</param>
        /// <param name="format">The question, given <paramref name="name"/>.</param>
        public static async Task<bool> ConfirmWriteAsync(
            IConfirmationService? confirmation,
            Func<string, bool>? isOpen,
            string path,
            string name,
            string format,
            string confirmLabel)
        {
            if (confirmation is null || isOpen is null || !isOpen(path))
                return true;

            string message = string.Format(CultureInfo.CurrentCulture, format, name);
            return await confirmation.ConfirmAsync(Resources.Strings.ChartBuilderOpenTitle, message, confirmLabel);
        }
    }
}
