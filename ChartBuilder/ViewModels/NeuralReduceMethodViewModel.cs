using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using ChartShared.Core.Charting.Authoring;
using ChartShared.Core.Generation;
using ChartShared.Core.Generation.Neural;
using ChartShared.Core.Settings;
using ChartShared.UI.ViewModels;
using CommunityToolkit.Mvvm.ComponentModel;

namespace ChartBuilder.ViewModels
{
    /// <summary>
    /// The neural generation method's controls (spec §4.1): the pack, the style, the density slider
    /// and Reduce-all chaining. A learned generator with no pack installed is still listed: it shows
    /// that no pack is installed and its draft is Rules' (§3.7).
    /// </summary>
    /// <remarks>
    /// It starts from the defaults (<see cref="GenerationDefaults"/>) and never saves: a run's changes are its own, and the
    /// defaults dialog writes them back (<see cref="WriteTo"/>). A pack's styles are read off the UI thread, through the
    /// cache the generator loads from (<see cref="NeuralPackCache"/>), so the pack is read once.
    /// </remarks>
    public sealed partial class NeuralReduceMethodViewModel : ViewModelBase
    {
        /// <summary>The method's id, as <see cref="GenerationDefaults"/> keeps it.</summary>
        public const string MethodId = NeuralDifficultyGenerator.NeuralMethodId;

        private readonly IGeneratorModelStore? _store;
        private readonly bool _loading = true;
        private int _styleLoads;

        /// <param name="defaults">The defaults the options start from.</param>
        /// <param name="store">The shared generator-model store; null where models aren't managed.</param>
        public NeuralReduceMethodViewModel(GenerationDefaults defaults, IGeneratorModelStore? store)
        {
            ArgumentNullException.ThrowIfNull(defaults);
            _store = store;
            Packs = store is null ? [] : NeuralDifficultyGenerator.InstalledPacks(store);
            SelectedPack = Packs.FirstOrDefault(p => string.Equals(p, defaults.Pack, StringComparison.OrdinalIgnoreCase))
                ?? Packs.FirstOrDefault();
            Density = defaults.Density;
            Chained = defaults.Chained;
            CapLanes = defaults.CapLanes;
            SelectedStyle = defaults.Style;
            _loading = false;
            StylesLoaded = LoadStylesAsync(defaults.Style);
        }

        /// <summary>Raised when an option changes, so the dialog draws the draft again.</summary>
        public event EventHandler? Changed;

        /// <summary>The latest reading of the selected pack's styles; done once they're shown. For tests.</summary>
        public Task StylesLoaded { get; private set; }

        /// <summary>The installed neural packs, by model name.</summary>
        [ObservableProperty]
        public partial IReadOnlyList<string> Packs { get; private set; } = [];

        /// <summary>Whether any neural pack is installed.</summary>
        public bool HasPack => Packs.Count > 0;

        /// <summary>The pack the draft is made by.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HasPack))]
        public partial string? SelectedPack { get; set; }

        /// <summary>The pack's styles, Any first.</summary>
        [ObservableProperty]
        public partial IReadOnlyList<string> Styles { get; private set; } = ["Any"];

        /// <summary>The style the draft imitates (spec §3.3).</summary>
        [ObservableProperty]
        public partial string SelectedStyle { get; set; } = "Any";

        /// <summary>Fewer notes (−1) to more notes (+1); centred changes nothing (spec §3.4).</summary>
        [ObservableProperty]
        public partial double Density { get; set; }

        /// <summary>Reduce all chained (each from the one above) or each from Expert (spec §3.6).</summary>
        [ObservableProperty]
        public partial bool Chained { get; set; } = true;

        /// <summary>Fit kept notes into the target's usual lane range; off keeps every lane the
        /// source uses. Onsets are never dropped for it.</summary>
        [ObservableProperty]
        public partial bool CapLanes { get; set; } = true;

        /// <summary>Why the draft falls back to Rules, or what the pack is; empty when neural runs.</summary>
        [ObservableProperty]
        public partial string StatusText { get; private set; } = "";

        /// <summary>The draft's options from the controls.</summary>
        public GenerationOptions Options()
        {
            var defaults = new GenerationDefaults();
            WriteTo(defaults);
            return defaults.Options();
        }

        /// <summary>Writes the options as they are into <paramref name="defaults"/>, for the defaults dialog to save.</summary>
        public void WriteTo(GenerationDefaults defaults)
        {
            ArgumentNullException.ThrowIfNull(defaults);
            defaults.Pack = SelectedPack;
            defaults.Style = SelectedStyle ?? "Any";
            defaults.Density = Density;
            defaults.Chained = Chained;
            defaults.CapLanes = CapLanes;
        }

        /// <summary>Re-reads the installed packs, keeping the selection when it's still there.</summary>
        public void Refresh()
        {
            string? keep = SelectedPack;
            Packs = _store is null ? [] : NeuralDifficultyGenerator.InstalledPacks(_store);
            SelectedPack = Packs.FirstOrDefault(p => string.Equals(p, keep, StringComparison.OrdinalIgnoreCase))
                ?? Packs.FirstOrDefault();
        }

        partial void OnSelectedPackChanged(string? value)
        {
            if (_loading)
                return;
            StylesLoaded = LoadStylesAsync(SelectedStyle);
            Changed?.Invoke(this, EventArgs.Empty);
        }

        partial void OnSelectedStyleChanged(string value) => Raise();

        partial void OnDensityChanged(double value) => Raise();

        partial void OnChainedChanged(bool value) => Raise();

        partial void OnCapLanesChanged(bool value) => Raise();

        private void Raise()
        {
            if (!_loading)
                Changed?.Invoke(this, EventArgs.Empty);
        }

        // The selected pack's styles, read off the UI thread and shown on it; a later pick's reading replaces this one's.
        private async Task LoadStylesAsync(string? keep)
        {
            int load = ++_styleLoads;
            if (_store is not { } store || SelectedPack is not { } name)
            {
                Show(["Any"], keep, "No neural pack is installed. Install one in Settings (Generator models); drafts use Rules meanwhile.");
                return;
            }

            string status;
            IReadOnlyList<string> styles = ["Any"];
            try
            {
                var (pack, _) = await Task.Run(() => NeuralPackCache.Shared.Load(store, name));
                styles = pack.Manifest.Styles.Select(static s => s.Label).ToList() is { Count: > 0 } labels ? labels : ["Any"];
                status = pack.Manifest.Description;
            }
            catch (PackValidationException ex)
            {
                status = $"Pack '{name}' can't be used ({ex.Message}); drafts use Rules meanwhile.";
            }

            if (load == _styleLoads)
                Show(styles, keep, status);
        }

        private void Show(IReadOnlyList<string> styles, string? keep, string status)
        {
            Styles = styles;
            SelectedStyle = styles.FirstOrDefault(s => string.Equals(s, keep, StringComparison.OrdinalIgnoreCase)) ?? styles[0];
            StatusText = status;
        }
    }
}
