using System;
using ChartPacker.Audio.Playback;
using ChartPacker.Core.Charting.Recovery;
using ChartPacker.UI.Services;

namespace ChartBuilder.Services
{
    /// <summary>What an Editor window works with, beyond its song.</summary>
    /// <param name="Settings">Chart Builder's settings, and where the layout and the recent songs are saved.</param>
    /// <param name="SettingsWindow">Opens Program settings.</param>
    /// <param name="Confirmations">Asks before a change that loses something, and Save, Discard or Cancel on closing.</param>
    /// <param name="FolderPicker">Picks the folder Save and pack writes to, the folder Export copy writes to, and a new chart's parent folder.</param>
    /// <param name="ImagePicker">Picks the album art the Art tab puts in place of the song's.</param>
    /// <param name="FilePicker">Picks the tracks and backgrounds Chart settings and the Art tab add.</param>
    /// <param name="FirstSave">Asks where a new chart's first save makes its folder.</param>
    /// <param name="ExportSng">Shows the Export to <c>.sng</c> dialog: preview, location and if-exists choice.</param>
    /// <param name="MessagesDialog">Shows what the Editor has said this session, from its message bar.</param>
    /// <param name="OpenOutput">Opens the audio output, for playback.</param>
    /// <param name="Latency">The audio/visual latency, which what's drawn against the audio allows for.</param>
    /// <param name="Calibrate">Opens the calibration tool, from the transport bar.</param>
    /// <param name="LockFolder">Where the songs open are locked; a new chart's song is locked here once it's saved.</param>
    /// <param name="ChartPackerSettingsFile">Chart Packer's settings, read for its library and packing settings, never written.</param>
    /// <param name="Recovery">Where the chart is autosaved, for recovery after a crash.</param>
    /// <param name="Authoring">Shows the authoring tools' dialogs: Reduce and the rest.</param>
    public sealed record EditorServices(
        BuilderSettingsService Settings,
        ISettingsWindowService SettingsWindow,
        IConfirmationService Confirmations,
        IFolderPickerService FolderPicker,
        IImagePickerService ImagePicker,
        IFilePickerService FilePicker,
        IFirstSaveDialogService FirstSave,
        IExportSngDialogService ExportSng,
        IMessagesDialogService MessagesDialog,
        Func<int, IAudioOutput> OpenOutput,
        AudioLatencyService Latency,
        Action Calibrate,
        string LockFolder,
        string ChartPackerSettingsFile,
        RecoveryStore Recovery,
        IAuthoringDialogService Authoring);
}
