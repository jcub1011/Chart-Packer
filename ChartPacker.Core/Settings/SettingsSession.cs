using System.ComponentModel;

namespace ChartPacker.Core.Settings;

/// <summary>Staged edits to every setting in a catalog, applied together (a settings window's OK or Apply).</summary>
public sealed class SettingsSession : INotifyPropertyChanged
{
    private readonly Dictionary<SettingDefinition, SettingEdit> _editsByDefinition = [];

    public SettingsSession(SettingsCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        Catalog = catalog;

        var edits = new List<SettingEdit>(catalog.Definitions.Count);
        foreach (var definition in catalog.Definitions)
        {
            var edit = definition.BeginEdit();
            edit.PropertyChanged += OnEditPropertyChanged;
            _editsByDefinition.Add(definition, edit);
            edits.Add(edit);
        }

        Edits = edits;
    }

    public SettingsCatalog Catalog { get; }

    /// <summary>One edit per definition, in catalog order.</summary>
    public IReadOnlyList<SettingEdit> Edits { get; }

    /// <summary>Some setting's staged value differs from its live value.</summary>
    public bool HasChanges { get; private set; }

    public event PropertyChangedEventHandler? PropertyChanged;

    public SettingEdit EditFor(SettingDefinition definition) => _editsByDefinition[definition];

    /// <summary>Writes every modified setting, in catalog order.</summary>
    /// <returns>Whether anything was written.</returns>
    public bool Apply()
    {
        bool applied = false;
        foreach (var edit in Edits)
            applied |= edit.Apply();

        return applied;
    }

    private void OnEditPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(SettingEdit.IsModified))
            return;

        bool hasChanges = Edits.Any(edit => edit.IsModified);
        if (hasChanges == HasChanges)
            return;

        HasChanges = hasChanges;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasChanges)));
    }
}
