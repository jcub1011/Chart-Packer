using System;
using System.Collections.Generic;
using System.Linq;
using ChartShared.Audio.Playback;
using ChartShared.Core.Settings;
using ChartShared.UI.Resources;

namespace ChartShared.UI
{
    /// <summary>The Settings rows for how audio is output: the low-latency path and the output device. Both apps list them.</summary>
    public static class AudioOutputSettings
    {
        /// <summary>
        /// The low latency switch and the output device choice, in the audio <paramref name="category"/>. SDL reads the
        /// low-latency hint as its audio subsystem starts, so that applies on the next run (issue #280). The device
        /// choices are made from the devices connected when this is called, as the window opens.
        /// </summary>
        public static SettingDefinition[] CreateSettings(
            SettingCategory category,
            bool defaultLowLatency,
            Func<bool> getLowLatency,
            Action<bool> setLowLatency,
            Func<string> getDevice,
            Action<string> setDevice) =>
        [
            new ToggleSetting
            {
                Id = "playback.lowLatencyAudio",
                Category = category,
                Label = SharedStrings.LowLatencyAudioLabel,
                Description = SharedStrings.LowLatencyAudioDescription,
                Keywords = SettingKeywords.Split(SharedStrings.LowLatencyAudioKeywords),
                DefaultValue = defaultLowLatency,
                Get = getLowLatency,
                Set = setLowLatency,
            },
            new ChoiceSetting<string>
            {
                Id = "playback.outputDevice",
                Category = category,
                Label = SharedStrings.OutputDeviceLabel,
                Description = SharedStrings.OutputDeviceDescription,
                Keywords = SettingKeywords.Split(SharedStrings.OutputDeviceKeywords),
                DefaultValue = "",
                Choices = OutputDeviceChoices(getDevice()),
                Get = getDevice,
                Set = setDevice,
            },
        ];

        /// <summary>
        /// The output device choices: the default device, one per connected device, and the <paramref name="saved"/>
        /// device when it isn't connected anymore, so it stays a choice instead of being silently reset.
        /// </summary>
        public static IReadOnlyList<SettingChoice<string>> OutputDeviceChoices(string saved)
        {
            var choices = new List<SettingChoice<string>> { new("", SharedStrings.OutputDeviceDefaultLabel) };
            foreach (var device in AudioDevices.Playback())
                choices.Add(new SettingChoice<string>(device.Identifier, device.Name));

            if (!string.IsNullOrEmpty(saved) && choices.All(choice => choice.Value != saved))
                choices.Add(new SettingChoice<string>(saved, SharedStrings.OutputDeviceNotConnectedLabel));

            return choices;
        }
    }
}
