using System;
using System.Diagnostics;
using Colossal.Serialization.Entities;
using Game;
using Game.Common;
using Game.Net;
using Game.Tools;
using SmartTrains.Core.Network;
using Unity.Entities;

namespace SmartTrains.Network
{
    /// <summary>
    /// Keeps the core's picture of the track network up to date: builds it
    /// after loading, and again once the player has stopped changing track
    /// for a moment.
    ///
    /// Runs in ModificationEnd, the last phase in which lanes the game has
    /// just created, changed or deleted still carry Created, Updated or
    /// Deleted. Those marks last one frame, so the check runs every frame;
    /// the rebuild itself waits until no change has come for
    /// <see cref="kSettle"/>, since building a line of track changes lanes
    /// over many frames.
    /// </summary>
    public partial class NetworkSystem : GameSystemBase
    {
        private static readonly TimeSpan kSettle = TimeSpan.FromSeconds(2);

        private EntityQuery m_Changed;
        private NetworkReader m_Reader;
        private bool m_Dirty = true;
        private DateTime m_ChangedAt = DateTime.MinValue;

        /// <summary>The current division of the track network; null before the first build.</summary>
        public TrackLayout Layout { get; private set; }

        /// <summary>Counts the builds, so users of <see cref="Layout"/> can tell it changed.</summary>
        public int Version { get; private set; }

        protected override void OnCreate()
        {
            base.OnCreate();
            m_Reader = new NetworkReader(EntityManager, GetEntityQuery(NetworkReader.QueryDesc()));
            // Tram track lies in road lanes, which carry CarLane as well; the
            // game updates those with every change to a road or junction,
            // e.g. new traffic lights. Train track has no CarLane.
            m_Changed = GetEntityQuery(new EntityQueryDesc
            {
                All = new[] { ComponentType.ReadOnly<Lane>(), ComponentType.ReadOnly<TrackLane>() },
                Any = new[] { ComponentType.ReadOnly<Created>(), ComponentType.ReadOnly<Updated>(), ComponentType.ReadOnly<Deleted>() },
                None = new[] { ComponentType.ReadOnly<Temp>(), ComponentType.ReadOnly<CarLane>() },
            });
        }

        protected override void OnGameLoaded(Context serializationContext)
        {
            base.OnGameLoaded(serializationContext);
            // Entity keys of another city may match by chance; build anew.
            Layout = null;
            m_Dirty = true;
            m_ChangedAt = DateTime.MinValue;
        }

        protected override void OnUpdate()
        {
            try
            {
                if (!m_Changed.IsEmptyIgnoreFilter)
                {
                    m_Dirty = true;
                    m_ChangedAt = DateTime.UtcNow;
                }
                if (!m_Dirty || DateTime.UtcNow - m_ChangedAt < kSettle)
                    return;
                m_Dirty = false;
                Rebuild();
            }
            catch (Exception e)
            {
                Mod.Log.Critical(e, "Reading the track network failed and was switched off until the game is restarted.");
                Enabled = false;
            }
        }

        /// <summary>
        /// Reads the network again, and keeps the old layout if nothing a
        /// train runs on has changed. A new layout makes the dispatcher start
        /// over and forget what it has granted, which must not happen for an
        /// update that changed nothing, e.g. one the game sends to all lanes
        /// of a rebuilt node.
        /// </summary>
        private void Rebuild()
        {
            Stopwatch watch = Stopwatch.StartNew();
            TrackNetwork network = m_Reader.Read();
            long fingerprint = network.Fingerprint();
            if (Layout != null && fingerprint == m_Fingerprint)
                return;
            var layout = new TrackLayout(network);
            watch.Stop();
            m_Fingerprint = fingerprint;
            Layout = layout;
            Version++;
            if (network.Lanes.Count > 0)
            {
                LayoutSummary summary = layout.Summarize();
                Mod.Log.Info($"Track network: {summary}. Read in {watch.ElapsedMilliseconds} ms.");
                World.GetOrCreateSystemManaged<Metrics.MetricsSystem>().NetworkRead(Version, layout, watch.ElapsedMilliseconds);
            }
        }

        private long m_Fingerprint;
    }
}
