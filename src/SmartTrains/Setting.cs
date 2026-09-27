using Colossal.IO.AssetDatabase;
using Game.Modding;
using Game.Settings;

namespace SmartTrains
{
    /// <summary>
    /// Settings kept between sessions, in ModsSettings/SmartTrains. They are
    /// changed from the panel, not from the game's options screen.
    /// </summary>
    [FileLocation("ModsSettings/SmartTrains/SmartTrains")]
    public class Setting : ModSetting
    {
        public Setting(IMod mod) : base(mod)
        {
            SetDefaults();
        }

        /// <summary>
        /// Whether the dispatcher holds trains. Off, it still works out what
        /// it would do, and the panel shows that, but trains run as in the
        /// game without the mod.
        /// </summary>
        public bool DispatcherActive { get; set; }

        public override void SetDefaults()
        {
            DispatcherActive = false;
        }
    }
}
