using System;
using ChartBuilder.Resources;
using ChartBuilder.Services;
using ChartShared.UI.ViewModels;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ChartBuilder.ViewModels
{
    /// <summary>
    /// The name of a layout template (issue 263), new or renamed. It can't be confirmed while it's blank, too long, or
    /// another template's (<see cref="LayoutTemplateStore.ValidateName"/>).
    /// </summary>
    public sealed partial class LayoutNameViewModel : ViewModelBase
    {
        private readonly Func<string, string?> _validate;

        /// <param name="validate">What's wrong with a name, or null when nothing is.</param>
        /// <param name="initial">The name to start with: the template's own when it's renamed.</param>
        private LayoutNameViewModel(string title, string? intro, string confirmLabel, Func<string, string?> validate, string initial)
        {
            Title = title;
            Intro = intro;
            ConfirmLabel = confirmLabel;
            _validate = validate;
            Name = initial;
        }

        /// <summary>Naming a new template, of the lanes open, in <paramref name="store"/>.</summary>
        public static LayoutNameViewModel ForNew(LayoutTemplateStore store) =>
            new(Strings.LayoutNewTitle, Strings.LayoutNewIntro, Strings.LayoutSaveButton, n => store.ValidateName(n), "");

        /// <summary>Renaming the template named <paramref name="name"/> in <paramref name="store"/>.</summary>
        public static LayoutNameViewModel ForRename(LayoutTemplateStore store, string name) =>
            new(Strings.LayoutRenameTitle, null, Strings.LayoutRenameButton, n => store.ValidateName(n, name), name);

        public string Title { get; }

        /// <summary>A line about what's named, or null for none.</summary>
        public string? Intro { get; }

        public bool HasIntro => Intro is not null;

        /// <summary>The confirming button's label: Save or Rename.</summary>
        public string ConfirmLabel { get; }

        /// <summary>The name, as typed.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(Error), nameof(ShownError), nameof(HasShownError))]
        [NotifyCanExecuteChangedFor(nameof(ConfirmCommand))]
        public partial string Name { get; set; } = "";

        /// <summary>What's wrong with the name, or null when nothing is.</summary>
        public string? Error => _validate(Name ?? "");

        /// <summary>The error shown under the name: none while it's blank, which the disabled button says enough.</summary>
        public string? ShownError => string.IsNullOrWhiteSpace(Name) ? null : Error;

        public bool HasShownError => ShownError is not null;

        /// <summary>The name, trimmed, once it's confirmed.</summary>
        public string? Result { get; private set; }

        /// <summary>Raised when a name with nothing wrong with it is confirmed: once, however often Confirm runs.</summary>
        public event EventHandler? Confirmed;

        private bool CanConfirm() => Result is null && Error is null;

        [RelayCommand(CanExecute = nameof(CanConfirm))]
        private void Confirm()
        {
            if (!CanConfirm())
                return;
            Result = Name.Trim();
            ConfirmCommand.NotifyCanExecuteChanged();
            Confirmed?.Invoke(this, EventArgs.Empty);
        }
    }
}
