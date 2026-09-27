using System;
using Game;

namespace SmartTrains.Dispatch
{
    /// <summary>
    /// Takes the dispatcher's marks off every train before the game saves.
    ///
    /// The marks live in the trains' navigation lanes, which the game saves.
    /// A save made with marks on, loaded without the mod, would keep those
    /// trains standing until they happened to get a new route. The Serialize
    /// phase runs only while saving; the dispatcher puts the marks back in
    /// the next simulation step.
    /// </summary>
    public partial class SaveCleanupSystem : GameSystemBase
    {
        private DispatchSystem m_Dispatch;

        protected override void OnCreate()
        {
            base.OnCreate();
            m_Dispatch = World.GetOrCreateSystemManaged<DispatchSystem>();
        }

        protected override void OnUpdate()
        {
            try
            {
                m_Dispatch.ClearAllMarks();
            }
            catch (Exception e)
            {
                Mod.Log.Critical(e, "Could not take the dispatcher's marks off before saving.");
            }
        }
    }
}
