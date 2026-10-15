using ChartPacker.Core.Input;
using ChartPacker.Core.Settings;

namespace ChartBuilder.Services
{
    /// <summary>
    /// Loads and saves <c>input-bindings.json</c> (charter-spec C19 item 2, §10) through <see cref="JsonSettingsFile{T}"/>.
    /// The path is always passed in; tests pass a temp file.
    /// </summary>
    public sealed class InputSettingsService
    {
        private readonly JsonSettingsFile<InputSettings> _file;

        private InputSettingsService(JsonSettingsFile<InputSettings> file, InputSettings settings)
        {
            _file = file;
            Settings = settings;
        }

        public InputSettings Settings { get; }

        /// <summary>Loads the bindings from <paramref name="filePath"/>, falling back to the keyboard's defaults when it's missing or unreadable.</summary>
        public static InputSettingsService Load(string filePath)
        {
            var file = new JsonSettingsFile<InputSettings>(filePath, InputSettingsJsonContext.Default.InputSettings);
            var settings = file.Load();
            settings.Sanitize();
            return new InputSettingsService(file, settings);
        }

        /// <summary>Writes the profiles and devices changed in this run, keeping other processes' changes to the rest.</summary>
        public void Save() => _file.Save(Settings);
    }
}
