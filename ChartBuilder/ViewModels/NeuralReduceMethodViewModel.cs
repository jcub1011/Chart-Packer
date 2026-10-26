using System;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using ChartBuilder.Services;
using ChartShared.Core.Charting.Authoring;
using ChartShared.Core.Generation;
using ChartShared.Core.Generation.Neural;
using ChartShared.UI.ViewModels;
using CommunityToolkit.Mvvm.ComponentModel;

namespace ChartBuilder.ViewModels
{
    /// <summary>
    /// The neural generation method's controls (spec §4.1): the pack, the style, the density slider
    /// and Reduce-all chaining. A learned generator with no pack installed is still listed: it shows
    /// that no pack is installed and its draft is Rules' (§3.7).
    /// </summary>
    public sealed partial class NeuralReduceMethodViewModel : ViewModelBase
    {
        /// <summary>The method's id, as <see cref="BuilderSettings"/> persists it.</summary>
        public const string MethodId = "neural";

        private readonly IGeneratorModelStore? _store;
        private readonly BuilderSettingsService _settings;
        private bool _loading = true;

        /// <param name="settings">The settings the method's own options are kept in.</param>
        /// <param name="store">The shared generator-model store; null where models aren't managed.</param>
        public NeuralReduceMethodViewModel(BuilderSettingsService settings, IGeneratorModelStore? store)
        {
            _settings = settings;
            _store = store;
            var saved = Load(settings);
            Packs = InstalledPacks();
            SelectedPack = Packs.FirstOrDefault(p => string.Equals(p, saved.Pack, StringComparison.OrdinalIgnoreCase))
                ?? Packs.FirstOrDefault();
            Density = saved.Density;
            Chained = saved.Chained;
            CapLanes = saved.CapLanes ?? true;
            RefreshStyles(saved.Style);
            _loading = false;
        }

        /// <summary>Raised when an option changes, so the dialog draws the draft again.</summary>
        public event EventHandler? Changed;

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
        public GenerationOptions Options() => new(
            Style: SelectedStyle ?? "Any",
            // Centred changes nothing (§3.4). Trained emissions have multi-unit magnitudes, so the
            // slider spans several units of per-chord score to move density a useful amount.
            DensityBias: Density * 6.0,
            Chained: Chained,
            ModelName: SelectedPack,
            CapLanes: CapLanes);

        /// <summary>Re-reads the installed packs, keeping the selection when it's still there.</summary>
        public void Refresh()
        {
            string? keep = SelectedPack;
            Packs = InstalledPacks();
            SelectedPack = Packs.FirstOrDefault(p => string.Equals(p, keep, StringComparison.OrdinalIgnoreCase))
                ?? Packs.FirstOrDefault();
        }

        partial void OnSelectedPackChanged(string? value)
        {
            RefreshStyles(SelectedStyle);
            Save();
            if (!_loading)
                Changed?.Invoke(this, EventArgs.Empty);
        }

        partial void OnSelectedStyleChanged(string value)
        {
            Save();
            if (!_loading)
                Changed?.Invoke(this, EventArgs.Empty);
        }

        partial void OnDensityChanged(double value)
        {
            Save();
            if (!_loading)
                Changed?.Invoke(this, EventArgs.Empty);
        }

        partial void OnChainedChanged(bool value)
        {
            Save();
            if (!_loading)
                Changed?.Invoke(this, EventArgs.Empty);
        }

        partial void OnCapLanesChanged(bool value)
        {
            Save();
            if (!_loading)
                Changed?.Invoke(this, EventArgs.Empty);
        }

        private IReadOnlyList<string> InstalledPacks()
        {
            if (_store is null)
                return [];
            try
            {
                return [.. _store.List()
                    .Where(m => m.Id.Type == GeneratorType.NeuralDifficulty)
                    .Select(m => m.Id.Name)
                    .OrderBy(static n => n, StringComparer.OrdinalIgnoreCase)];
            }
            catch
            {
                return [];
            }
        }

        private void RefreshStyles(string? keep)
        {
            if (_store is null || SelectedPack is null)
            {
                Styles = ["Any"];
                SelectedStyle = "Any";
                StatusText = "No neural pack is installed. Install one in Settings (Generator models); drafts use Rules meanwhile.";
                return;
            }

            try
            {
                string path = _store.ResolvePath(new GeneratorModelId(GeneratorType.NeuralDifficulty, SelectedPack));
                var pack = NeuralPack.LoadFromBytes(File.ReadAllBytes(path), SelectedPack);
                var styles = pack.Manifest.Styles.Select(static s => s.Label).ToList();
                if (styles.Count == 0)
                    styles.Add("Any");
                Styles = styles;
                SelectedStyle = styles.FirstOrDefault(s => string.Equals(s, keep, StringComparison.OrdinalIgnoreCase))
                    ?? styles[0];
                StatusText = pack.Manifest.Description;
            }
            catch (PackValidationException ex)
            {
                Styles = ["Any"];
                SelectedStyle = "Any";
                StatusText = $"Pack '{SelectedPack}' failed to load ({ex.Message}); drafts use Rules meanwhile.";
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Styles = ["Any"];
                SelectedStyle = "Any";
                StatusText = $"Pack '{SelectedPack}' couldn't be read ({ex.Message}); drafts use Rules meanwhile.";
            }
        }

        private void Save()
        {
            var settings = _settings.Settings;
            settings.ReduceMethodSettings ??= new Dictionary<string, string>();
            settings.ReduceMethodSettings[MethodId] = System.Text.Json.JsonSerializer.Serialize(
                new NeuralMethodSettings(SelectedPack, SelectedStyle, Density, Chained, CapLanes),
                BuilderSettingsJsonContext.Default.NeuralMethodSettings);
            _settings.Save();
        }

        private static NeuralMethodSettings Load(BuilderSettingsService settings)
        {
            try
            {
                if (settings.Settings.ReduceMethodSettings?.TryGetValue(MethodId, out string? json) == true && json is not null)
                    return System.Text.Json.JsonSerializer.Deserialize(json, BuilderSettingsJsonContext.Default.NeuralMethodSettings) ?? new NeuralMethodSettings(null, "Any", 0, true, true);
            }
            catch (System.Text.Json.JsonException)
            {
            }

            return new NeuralMethodSettings(null, "Any", 0, true, true);
        }

        /// <summary>The method's own options as persisted in settings.</summary>
        public sealed record NeuralMethodSettings(string? Pack, string Style, double Density, bool Chained, bool? CapLanes);
    }
}
