using System;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using ChartShared.Core.Generation;
using ChartShared.UI.Resources;
using ChartShared.UI.Services;
using ChartShared.UI.ViewModels;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ChartShared.UI.ViewModels;

/// <summary>A stored generator model as the storage dialog lists it.</summary>
public sealed class GeneratorModelRowViewModel(GeneratorModelInfo info)
{
    public GeneratorModelId Id { get; } = info.Id;

    public string TypeLabel => Id.Type.ToString();

    public string Name => Id.Name;

    public string FileName => System.IO.Path.GetFileName(info.Path);

    /// <summary>The file's size as shown, e.g. "12.4 MB".</summary>
    public string SizeText => FormatSize(info.Size);

    public DateTimeOffset Modified => info.Modified;

    public string ModifiedText => info.Modified == DateTimeOffset.MinValue
        ? ""
        : info.Modified.LocalDateTime.ToString(CultureInfo.CurrentCulture);

    private static string FormatSize(long size) => size switch
    {
        < 0 => "",
        < 1024 => string.Format(CultureInfo.CurrentCulture, SharedStrings.GeneratorModelsSizeBytesFormat, size),
        < 1024 * 1024 => string.Format(CultureInfo.CurrentCulture, SharedStrings.GeneratorModelsSizeKbFormat, size / 1024.0),
        < 1024L * 1024 * 1024 => string.Format(CultureInfo.CurrentCulture, SharedStrings.GeneratorModelsSizeMbFormat, size / (1024.0 * 1024)),
        _ => string.Format(CultureInfo.CurrentCulture, SharedStrings.GeneratorModelsSizeGbFormat, size / (1024.0 * 1024 * 1024)),
    };
}

/// <summary>
/// The generator model storage dialog (issue #154), shared by both apps: what folder holds
/// the models, and their import, export, rename and delete. Opened from either app's Settings,
/// and later from the generation windows.
/// </summary>
public sealed partial class GeneratorModelsViewModel : ViewModelBase
{
    private readonly IGeneratorModelStore _store;
    private readonly IFilePickerService _files;
    private readonly IFolderPickerService _folders;
    private readonly IConfirmationService _confirmations;
    private readonly IFileRevealService _reveal;

    public GeneratorModelsViewModel(
        IGeneratorModelStore store,
        IFilePickerService files,
        IFolderPickerService folders,
        IConfirmationService confirmations,
        IFileRevealService reveal)
    {
        _store = store;
        _files = files;
        _folders = folders;
        _confirmations = confirmations;
        _reveal = reveal;
        Folder = store.ResolvedFolder;
        Refresh();
    }

    public string Title => SharedStrings.GeneratorModelsTitle;

    /// <summary>Where models are stored now.</summary>
    [ObservableProperty]
    public partial string Folder { get; private set; }

    public ObservableCollection<GeneratorModelRowViewModel> Models { get; } = [];

    /// <summary>The listed model acted on; null for none.</summary>
    [ObservableProperty]
    public partial GeneratorModelRowViewModel? SelectedModel { get; set; }

    /// <summary>The name a rename gives the selected model.</summary>
    [ObservableProperty]
    public partial string NewName { get; set; } = "";

    /// <summary>What the dialog last did or failed, shown under the list.</summary>
    [ObservableProperty]
    public partial string Status { get; private set; } = "";

    public bool HasModels => Models.Count > 0;

    /// <summary>Whether anything is acting: while it is, the buttons hold off.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ImportCommand), nameof(ExportCommand), nameof(DeleteCommand), nameof(RenameCommand), nameof(ChangeFolderCommand))]
    public partial bool IsBusy { get; private set; }

    partial void OnSelectedModelChanged(GeneratorModelRowViewModel? value)
    {
        NewName = value?.Name ?? "";
        ExportCommand.NotifyCanExecuteChanged();
        DeleteCommand.NotifyCanExecuteChanged();
        RenameCommand.NotifyCanExecuteChanged();
    }

    private bool CanAct() => !IsBusy;

    private bool CanActOnSelection() => !IsBusy && SelectedModel is not null;

    /// <summary>Lists what's stored now.</summary>
    [RelayCommand]
    private void Refresh()
    {
        try
        {
            Folder = _store.ResolvedFolder;
            var selected = SelectedModel?.Id;
            Models.Clear();
            foreach (var info in _store.List())
                Models.Add(new GeneratorModelRowViewModel(info));
            SelectedModel = selected is { } id ? Models.FirstOrDefault(m => m.Id == id) : null;
            OnPropertyChanged(nameof(HasModels));
            if (Models.Count == 0 && string.IsNullOrEmpty(Status))
                Status = SharedStrings.GeneratorModelsEmpty;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or PathTooLongException)
        {
            Status = string.Format(CultureInfo.CurrentCulture, SharedStrings.GeneratorModelsFailedFormat,
                SharedStrings.GeneratorModelsRefreshAction, ex.Message);
        }

        ExportCommand.NotifyCanExecuteChanged();
        DeleteCommand.NotifyCanExecuteChanged();
        RenameCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Copies picked files into storage, asking before overwriting what's stored.</summary>
    [RelayCommand(CanExecute = nameof(CanAct))]
    private async Task ImportAsync()
    {
        var paths = await _files.PickFilesAsync(SharedStrings.GeneratorModelsPickImportTitle);
        if (paths.Count == 0)
            return;

        IsBusy = true;
        try
        {
            int imported = 0;
            int skipped = 0;
            foreach (string path in paths)
            {
                GeneratorModelId id;
                if (GeneratorModelNaming.TryParseFileName(path, out var parsed) && parsed is not null)
                {
                    id = parsed;
                }
                else
                {
                    string stem = System.IO.Path.GetFileNameWithoutExtension(path);
                    foreach (var type in Enum.GetValues<GeneratorType>().OrderByDescending(t => t.ToString().Length))
                    {
                        string prefix = type.ToString();
                        if (stem.StartsWith(prefix, StringComparison.Ordinal) && stem.Length > prefix.Length)
                        {
                            stem = stem[prefix.Length..];
                            break;
                        }
                    }

                    string name = GeneratorModelNaming.SanitizeName(stem);
                    if (string.IsNullOrEmpty(name))
                    {
                        skipped++;
                        continue;
                    }

                    id = new GeneratorModelId(GeneratorType.Difficulty, name);
                }

                string destPath;
                try
                {
                    destPath = _store.ResolvePath(id);
                }
                catch (ArgumentException)
                {
                    skipped++;
                    continue;
                }
                catch (NotSupportedException)
                {
                    skipped++;
                    continue;
                }
                catch (PathTooLongException)
                {
                    skipped++;
                    continue;
                }
                catch (IOException)
                {
                    skipped++;
                    continue;
                }
                catch (UnauthorizedAccessException)
                {
                    skipped++;
                    continue;
                }

                if (SamePath(path, destPath))
                {
                    skipped++;
                    continue;
                }

                if (Exists(id) && !await _confirmations.ConfirmAsync(
                        SharedStrings.GeneratorModelsOverwriteTitle,
                        string.Format(CultureInfo.CurrentCulture, SharedStrings.GeneratorModelsOverwriteFormat, id.Type, id.Name),
                        SharedStrings.GeneratorModelsOverwriteConfirm))
                {
                    skipped++;
                    continue;
                }

                await _store.ImportAsync(path, id, overwrite: true);
                imported++;
            }

            Refresh();
            Status = string.Format(CultureInfo.CurrentCulture, SharedStrings.GeneratorModelsImportedFormat, imported, skipped);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or PathTooLongException)
        {
            Status = string.Format(CultureInfo.CurrentCulture, SharedStrings.GeneratorModelsFailedFormat,
                SharedStrings.GeneratorModelsImport, ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Copies the selected model to a picked folder, asking before overwriting there.</summary>
    [RelayCommand(CanExecute = nameof(CanActOnSelection))]
    private async Task ExportAsync()
    {
        if (SelectedModel is not { } selected)
        {
            Status = SharedStrings.GeneratorModelsSelectFirst;
            return;
        }

        string? folder = await _folders.PickFolderAsync(SharedStrings.GeneratorModelsPickExportTitle, _store.ResolvedFolder);
        if (folder is null)
            return;

        IsBusy = true;
        try
        {
            string dest = System.IO.Path.Combine(folder, selected.FileName);
            if (System.IO.File.Exists(dest) && !await _confirmations.ConfirmAsync(
                    SharedStrings.GeneratorModelsOverwriteTitle,
                    string.Format(CultureInfo.CurrentCulture, SharedStrings.GeneratorModelsExportExistsFormat, selected.FileName),
                    SharedStrings.GeneratorModelsOverwriteConfirm))
                return;

            await _store.ExportAsync(selected.Id, dest, overwrite: true);
            Status = string.Format(CultureInfo.CurrentCulture, SharedStrings.GeneratorModelsExportedFormat, selected.FileName);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Status = string.Format(CultureInfo.CurrentCulture, SharedStrings.GeneratorModelsFailedFormat,
                SharedStrings.GeneratorModelsExport, ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Deletes the selected model, after asking.</summary>
    [RelayCommand(CanExecute = nameof(CanActOnSelection))]
    private async Task DeleteAsync()
    {
        if (SelectedModel is not { } selected)
        {
            Status = SharedStrings.GeneratorModelsSelectFirst;
            return;
        }

        if (!await _confirmations.ConfirmAsync(
                SharedStrings.GeneratorModelsDeleteTitle,
                string.Format(CultureInfo.CurrentCulture, SharedStrings.GeneratorModelsDeleteFormat, selected.TypeLabel, selected.Name),
                SharedStrings.GeneratorModelsDelete))
            return;

        IsBusy = true;
        try
        {
            _store.Delete(selected.Id);
            Refresh();
            Status = string.Format(CultureInfo.CurrentCulture, SharedStrings.GeneratorModelsDeletedFormat, selected.FileName);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Status = string.Format(CultureInfo.CurrentCulture, SharedStrings.GeneratorModelsFailedFormat,
                SharedStrings.GeneratorModelsDelete, ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Gives the selected model <see cref="NewName"/>, asking before overwriting what's stored.</summary>
    [RelayCommand(CanExecute = nameof(CanActOnSelection))]
    private async Task RenameAsync()
    {
        if (SelectedModel is not { } selected)
        {
            Status = SharedStrings.GeneratorModelsSelectFirst;
            return;
        }

        string name;
        try
        {
            name = GeneratorModelNaming.ValidateName(NewName);
        }
        catch (ArgumentException)
        {
            Status = SharedStrings.GeneratorModelsNameBlank;
            return;
        }

        if (string.Equals(name, selected.Name, StringComparison.Ordinal))
            return;

        var renamed = selected.Id with { Name = name };
        if (Exists(renamed) && !await _confirmations.ConfirmAsync(
                SharedStrings.GeneratorModelsOverwriteTitle,
                string.Format(CultureInfo.CurrentCulture, SharedStrings.GeneratorModelsOverwriteFormat, renamed.Type, renamed.Name),
                SharedStrings.GeneratorModelsOverwriteConfirm))
            return;

        IsBusy = true;
        try
        {
            _store.Rename(selected.Id, name, overwrite: true);
            Refresh();
            SelectedModel = Models.FirstOrDefault(m => m.Id == renamed);
            Status = string.Format(CultureInfo.CurrentCulture, SharedStrings.GeneratorModelsRenamedFormat, name);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Status = string.Format(CultureInfo.CurrentCulture, SharedStrings.GeneratorModelsFailedFormat,
                SharedStrings.GeneratorModelsRename, ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// Moves storage to a picked folder. When the old folder holds models, asks whether to copy
    /// them over (the old folder keeps its files), and whether to overwrite collisions.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanAct))]
    private async Task ChangeFolderAsync()
    {
        string? folder = await _folders.PickFolderAsync(SharedStrings.GeneratorModelsPickFolderTitle, _store.ResolvedFolder);
        if (string.IsNullOrWhiteSpace(folder))
            return;

        if (SamePath(folder, _store.ResolvedFolder))
            return;

        IsBusy = true;
        try
        {
            string from = _store.ResolvedFolder;
            int stored;
            try
            {
                stored = _store.List().Count;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or PathTooLongException)
            {
                Status = string.Format(CultureInfo.CurrentCulture, SharedStrings.GeneratorModelsFailedFormat,
                    SharedStrings.GeneratorModelsChangeFolder, ex.Message);
                return;
            }

            bool overwrite = false;
            if (stored > 0)
            {
                var choice = await _confirmations.ChooseAsync(
                    SharedStrings.GeneratorModelsMigrateTitle,
                    string.Format(CultureInfo.CurrentCulture, SharedStrings.GeneratorModelsMigrateFormat, stored),
                    SharedStrings.GeneratorModelsMigrateCopy,
                    SharedStrings.GeneratorModelsMigrateLeave);
                if (choice == ConfirmChoice.Cancel)
                    return;

                if (choice == ConfirmChoice.First && Collisions(from, folder) is var collisions && collisions > 0)
                {
                    overwrite = await _confirmations.ConfirmAsync(
                        SharedStrings.GeneratorModelsOverwriteTitle,
                        string.Format(CultureInfo.CurrentCulture, SharedStrings.GeneratorModelsMigrateCollisionsFormat, collisions),
                        SharedStrings.GeneratorModelsOverwriteConfirm);
                }

                if (choice == ConfirmChoice.First)
                {
                    var migrated = await _store.MigrateCopyAsync(folder, overwrite);
                    if (migrated.Errors is { Count: > 0 } errors)
                    {
                        Status = string.Format(CultureInfo.CurrentCulture, SharedStrings.GeneratorModelsFailedFormat,
                            SharedStrings.GeneratorModelsMigrateCopy,
                            string.Join("; ", errors.Select(e => $"{e.File}: {e.Reason}")));
                        return;
                    }

                    if (!_store.SetCustomFolder(folder))
                    {
                        Status = string.Format(CultureInfo.CurrentCulture, SharedStrings.GeneratorModelsFailedFormat,
                            SharedStrings.GeneratorModelsChangeFolder, folder);
                        return;
                    }

                    Refresh();
                    Status = string.Format(CultureInfo.CurrentCulture, SharedStrings.GeneratorModelsMigratedFormat,
                        folder, migrated.Copied, migrated.Skipped);
                    return;
                }
            }

            if (!_store.SetCustomFolder(folder))
            {
                Status = string.Format(CultureInfo.CurrentCulture, SharedStrings.GeneratorModelsFailedFormat,
                    SharedStrings.GeneratorModelsChangeFolder, folder);
                return;
            }

            Refresh();
            Status = string.Format(CultureInfo.CurrentCulture, SharedStrings.GeneratorModelsFolderChangedFormat, folder);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or PathTooLongException)
        {
            Status = string.Format(CultureInfo.CurrentCulture, SharedStrings.GeneratorModelsFailedFormat,
                SharedStrings.GeneratorModelsChangeFolder, ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Shows the storage folder in the file manager.</summary>
    [RelayCommand]
    private void OpenFolder() => _reveal.OpenFolder(_store.ResolvedFolder);

    private bool Exists(GeneratorModelId id)
    {
        try
        {
            return System.IO.File.Exists(_store.ResolvePath(id));
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (NotSupportedException)
        {
            return false;
        }
        catch (PathTooLongException)
        {
            return false;
        }
    }

    // How many of the old folder's models are already in the new one.
    private static int Collisions(string from, string to)
    {
        try
        {
            if (!System.IO.Directory.Exists(from) || !System.IO.Directory.Exists(to))
                return 0;

            var names = System.IO.Directory.EnumerateFiles(to)
                .Where(p => GeneratorModelNaming.TryParseFileName(p, out _))
                .Select(System.IO.Path.GetFileName)
                .ToHashSet(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
            StringComparer comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
            return System.IO.Directory.EnumerateFiles(from)
                .Where(p => GeneratorModelNaming.TryParseFileName(p, out _))
                .Select(System.IO.Path.GetFileName)
                .Count(name => name is not null && names.Contains(name, comparer));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    private static bool SamePath(string a, string b)
    {
        try
        {
            return string.Equals(System.IO.Path.GetFullPath(a), System.IO.Path.GetFullPath(b),
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
