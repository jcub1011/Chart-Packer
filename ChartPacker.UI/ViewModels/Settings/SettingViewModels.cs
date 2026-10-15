using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using ChartPacker.Core.Settings;
using ChartPacker.Resources;
using ChartPacker.UI.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ChartPacker.ViewModels.Settings
{
    /// <summary>
    /// One row in the Settings window: a staged <see cref="SettingEdit"/> shaped for its editor control.
    /// Each subclass has a DataTemplate in <c>SettingsWindow.axaml</c>.
    /// </summary>
    public abstract partial class SettingViewModel : ObservableObject
    {
        private readonly SettingEdit _edit;

        protected SettingViewModel(SettingEdit edit)
        {
            _edit = edit;
            Actions = [.. edit.Definition.Actions.Select(a => new SettingActionViewModel(a))];
            edit.PropertyChanged += OnEditPropertyChanged;
        }

        public string Label => _edit.Definition.Label;

        /// <summary>The setting's buttons, shown beside its editor (<see cref="SettingDefinition.Actions"/>).</summary>
        public IReadOnlyList<SettingActionViewModel> Actions { get; }

        public bool HasActions => Actions.Count > 0;

        public string Description => _edit.Definition.Description;

        public bool HasDescription => Description.Length > 0;

        /// <summary>Changed here but not yet applied.</summary>
        public bool IsModified => _edit.IsModified;

        public bool IsDefault => _edit.IsDefault;

        /// <summary>The setting can be changed: the toggle it requires, if any, is on as staged.</summary>
        public bool IsEnabled => _edit.IsEnabled;

        /// <summary>Why the setting can't be changed, while it can't.</summary>
        public string? LockedNote => !IsEnabled && _edit.Requirement is { } requirement
            ? string.Format(CultureInfo.CurrentCulture, SharedStrings.SettingLockedFormat, requirement.Label)
            : null;

        private bool CanReset() => !IsDefault && IsEnabled;

        /// <summary>Stages the default; OK or Apply writes it.</summary>
        [RelayCommand(CanExecute = nameof(CanReset))]
        private void Reset() => _edit.ResetToDefault();

        /// <summary>Raises the editor's own properties after the staged value changes.</summary>
        protected abstract void OnValueChanged();

        private void OnEditPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            switch (e.PropertyName)
            {
                case nameof(SettingEdit<object>.Value):
                    OnValueChanged();
                    break;
                case nameof(SettingEdit.IsModified):
                    OnPropertyChanged(nameof(IsModified));
                    break;
                case nameof(SettingEdit.IsDefault):
                    OnPropertyChanged(nameof(IsDefault));
                    ResetCommand.NotifyCanExecuteChanged();
                    break;
                case nameof(SettingEdit.IsEnabled):
                    OnPropertyChanged(nameof(IsEnabled));
                    OnPropertyChanged(nameof(LockedNote));
                    ResetCommand.NotifyCanExecuteChanged();
                    break;
            }
        }
    }

    /// <summary>A button in a setting's row. It acts at once, whether or not the setting can be changed.</summary>
    public sealed partial class SettingActionViewModel(SettingAction action)
    {
        /// <summary>The button's text, or with an <see cref="Icon"/>, its tooltip.</summary>
        public string Label => action.Label;

        /// <summary>The line icon shown instead of the text, or <see langword="null"/> for a text button.</summary>
        public Geometry? Icon { get; } =
            action.IconKey is { } key && Application.Current?.TryFindResource(key, out object? icon) == true ? icon as Geometry : null;

        public bool HasIcon => Icon is not null;

        [RelayCommand]
        private void Run() => action.Run();
    }

    /// <summary>Picks the editor for a setting by its kind. A new kind needs a case here and a DataTemplate.</summary>
    public static class SettingEditorFactory
    {
        public static SettingViewModel Create(SettingEdit edit, IFolderPickerService folderPicker) => edit.Definition switch
        {
            ToggleSetting => new ToggleSettingViewModel((SettingEdit<bool>)edit),
            IntegerSetting integer => new IntegerSettingViewModel((SettingEdit<int>)edit, integer),
            RangeSetting range => new RangeSettingViewModel((SettingEdit<double>)edit, range),
            FolderSetting folder => new FolderSettingViewModel((SettingEdit<string?>)edit, folder, folderPicker),
            TextSetting text => new TextSettingViewModel((SettingEdit<string>)edit, text),
            ActionSetting => new ActionSettingViewModel(edit),
            _ when edit is IChoiceEdit choice => new ChoiceSettingViewModel(edit, choice),
            _ => throw new NotSupportedException($"No editor for setting '{edit.Definition.Id}' ({edit.Definition.GetType().Name})."),
        };
    }

    /// <summary>A row with only its buttons (<see cref="ActionSetting"/>), and no editor.</summary>
    public sealed class ActionSettingViewModel(SettingEdit edit) : SettingViewModel(edit)
    {
        protected override void OnValueChanged()
        {
        }
    }

    /// <summary>An on/off setting, shown as a toggle switch.</summary>
    public sealed class ToggleSettingViewModel(SettingEdit<bool> edit) : SettingViewModel(edit)
    {
        public bool Value
        {
            get => edit.Value;
            set => edit.Value = value;
        }

        protected override void OnValueChanged() => OnPropertyChanged(nameof(Value));
    }

    /// <summary>A whole number, shown as a spin box.</summary>
    public sealed class IntegerSettingViewModel(SettingEdit<int> edit, IntegerSetting definition) : SettingViewModel(edit)
    {
        public decimal Minimum { get; } = definition.Minimum;

        public decimal Maximum { get; } = definition.Maximum;

        /// <summary>Nullable decimal to match <c>NumericUpDown.Value</c>.</summary>
        public decimal? Value
        {
            get => edit.Value;
            set
            {
                if (value is { } number)
                    edit.Value = (int)Math.Clamp(Math.Round(number), int.MinValue, int.MaxValue);

                // The box was cleared, or the value was clamped: show the staged value.
                OnPropertyChanged(nameof(Value));
            }
        }

        protected override void OnValueChanged() => OnPropertyChanged(nameof(Value));
    }

    /// <summary>A number in a range, shown as a slider and its formatted value.</summary>
    public sealed class RangeSettingViewModel(SettingEdit<double> edit, RangeSetting definition) : SettingViewModel(edit)
    {
        public double Minimum => definition.Minimum;

        public double Maximum => definition.Maximum;

        public double SmallChange => definition.SmallChange;

        public double Value
        {
            get => edit.Value;
            set => edit.Value = value;
        }

        public string ValueText => definition.FormatValue(edit.Value);

        protected override void OnValueChanged()
        {
            OnPropertyChanged(nameof(Value));
            OnPropertyChanged(nameof(ValueText));
        }
    }

    /// <summary>One of a fixed list, shown as a drop-down.</summary>
    public sealed class ChoiceSettingViewModel(SettingEdit edit, IChoiceEdit choice) : SettingViewModel(edit)
    {
        public IReadOnlyList<string> Choices => choice.ChoiceLabels;

        public int SelectedIndex
        {
            get => choice.SelectedIndex;
            set => choice.SelectedIndex = value;
        }

        protected override void OnValueChanged() => OnPropertyChanged(nameof(SelectedIndex));
    }

    /// <summary>A line of text, shown as a text box.</summary>
    public sealed class TextSettingViewModel(SettingEdit<string> edit, TextSetting definition) : SettingViewModel(edit)
    {
        public string? Placeholder => definition.Placeholder;

        public string Value
        {
            get => edit.Value;
            set
            {
                edit.Value = value;

                // A dropped character: show the staged value.
                OnPropertyChanged(nameof(Value));
            }
        }

        protected override void OnValueChanged() => OnPropertyChanged(nameof(Value));
    }

    /// <summary>A folder path, shown as its path (or what happens without one) and a Browse button.</summary>
    public sealed partial class FolderSettingViewModel(SettingEdit<string?> edit, FolderSetting definition, IFolderPickerService folderPicker) : SettingViewModel(edit)
    {
        public bool IsEmpty => edit.Value is null;

        public string DisplayText => edit.Value ?? definition.EmptyLabel ?? "";

        [RelayCommand]
        private async Task BrowseAsync()
        {
            var path = await folderPicker.PickFolderAsync(definition.PickerTitle ?? definition.Label, edit.Value);
            if (path is not null)
                edit.Value = path;
        }

        protected override void OnValueChanged()
        {
            OnPropertyChanged(nameof(IsEmpty));
            OnPropertyChanged(nameof(DisplayText));
        }
    }
}
