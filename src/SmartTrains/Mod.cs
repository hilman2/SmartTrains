using Colossal.Logging;
using Game;
using Game.Modding;
using SmartTrains.Localization;

namespace SmartTrains
{
    /// <summary>
    /// Entry point. The game creates this class without running its
    /// constructor or field initialisers, so all state is static and set in
    /// <see cref="OnLoad"/>. The game also skips mods that take more than two
    /// seconds to load, so nothing expensive happens here.
    /// </summary>
    public class Mod : IMod
    {
        public static ILog Log { get; } = LogManager.GetLogger("SmartTrains").SetShowsErrorsInUI(false);

        public void OnLoad(UpdateSystem updateSystem)
        {
            Log.Info($"Loading Smart Trains {typeof(Mod).Assembly.GetName().Version}");
            LocaleSource.RegisterAll();
            updateSystem.UpdateAt<UI.TrainsUISystem>(SystemUpdatePhase.UIUpdate);
            // Removed trains still exist during the modification phases of
            // the frame after their removal; see DespawnWatchSystem.
            updateSystem.UpdateAt<Monitor.DespawnWatchSystem>(SystemUpdatePhase.Modification1);
        }

        public void OnDispose()
        {
            Log.Info("Unloading Smart Trains");
        }
    }
}
