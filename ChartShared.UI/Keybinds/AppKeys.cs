using System;
using ChartShared.Core.Keybinds;

namespace ChartShared.UI.Keybinds
{
    /// <summary>
    /// The keys of the running app, for the controls that can't be handed them: a menu's shortcut text, a tooltip, a control
    /// built without a constructor to pass them through. Set once by the app's composition root, in the style of
    /// <see cref="Views.WindowMemory"/>.
    /// </summary>
    public static class AppKeys
    {
        private static KeybindsService? _service;
        private static KeyMap? _placeholder;

        /// <summary>
        /// The app's keys. Until <see cref="Use"/> is called, a map of the shared actions with their default keys, which
        /// can't be rebound: what an app that hasn't a keys file yet (Chart Builder) has, so the shared windows still work.
        /// </summary>
        public static KeyMap Map => _service?.Map ?? (_placeholder ??= new KeyMap(new KeybindCatalog(SharedKeys.Actions())));

        /// <summary>The file the keys are kept in; null until <see cref="Use"/> is called.</summary>
        public static KeybindsService? Service => _service;

        /// <summary>
        /// Raised when the app's keys change, and once when they are first set (<see cref="Use"/>), so a label that was drawn
        /// before then is drawn again. Subscribe to this, not to <see cref="Map"/>, which is a stand-in until then. The event
        /// outlives every window, so whatever subscribes must unsubscribe, or be held weakly (see <see cref="Shortcut"/>).
        /// </summary>
        public static event EventHandler? Changed;

        /// <summary>Makes <paramref name="service"/>'s keys the app's.</summary>
        /// <exception cref="InvalidOperationException">Called twice.</exception>
        public static void Use(KeybindsService service)
        {
            ArgumentNullException.ThrowIfNull(service);
            if (_service is not null)
                throw new InvalidOperationException("The app's keys are already set.");
            _service = service;
            // The map lives as long as the app, so this one subscription is never undone.
            service.Map.Changed += (_, _) => Changed?.Invoke(null, EventArgs.Empty);
            Changed?.Invoke(null, EventArgs.Empty);
        }

        /// <summary>
        /// Reads the keys again if the other app rebound one since they were last read. Called as a window is activated,
        /// which is when a rebind made in the other app would be noticed.
        /// </summary>
        public static void ReloadIfChanged() => _service?.ReloadIfChanged();
    }
}
