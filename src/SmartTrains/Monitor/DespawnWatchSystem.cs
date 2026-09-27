using System;
using System.Collections.Generic;
using System.Text;
using Colossal.Entities;
using Colossal.Serialization.Entities;
using Game;
using Game.Common;
using Game.Pathfind;
using Game.Prefabs;
using Game.Simulation;
using Game.Tools;
using Game.UI;
using Game.Vehicles;
using SmartTrains.Core.Monitor;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;

namespace SmartTrains.Monitor
{
    /// <summary>A train the game removed, as it was in its last frame.</summary>
    internal sealed class DespawnRecord
    {
        public int Id;

        /// <summary>In-game time of day, "HH:mm".</summary>
        public string Time;

        public DespawnCause Cause;
        public int Train;

        /// <summary>The line's name, or empty for a train without a line.</summary>
        public string Line;

        public bool Cargo;
        public bool Through;
        public bool Returning;
        public float StoodMinutes;
        public float3 Position;
        public int Passengers;

        /// <summary>Cargo load in percent of the capacity; -1 for passenger trains.</summary>
        public int LoadPercent = -1;

        /// <summary>The largest part of the load, by the game's resource name; empty when empty.</summary>
        public string Resource = "";

        /// <summary>The trains it waited for, in order, as number and line name.</summary>
        public readonly List<(int Train, string Line)> Chain = new List<(int Train, string Line)>();

        public bool ChainBackToStart;
    }

    /// <summary>
    /// The removals since the city was loaded. Both systems that use it run
    /// on the main thread, so it needs no locking.
    /// </summary>
    internal static class DespawnHistory
    {
        private const int kMaxRecords = 100;

        /// <summary>Removals that need attention, newest first.</summary>
        public static readonly List<DespawnRecord> Records = new List<DespawnRecord>();

        public static int Arrived;
        public static int Depot;

        /// <summary>Changes with every addition, so the panel knows when to send the list again.</summary>
        public static int Version;

        private static int s_NextId = 1;

        public static void Add(DespawnRecord record)
        {
            record.Id = s_NextId++;
            Records.Insert(0, record);
            if (Records.Count > kMaxRecords)
                Records.RemoveAt(Records.Count - 1);
            Version++;
        }

        public static void Count(DespawnCause cause)
        {
            if (cause == DespawnCause.Arrived)
                Arrived++;
            else
                Depot++;
            Version++;
        }

        public static void Clear()
        {
            Records.Clear();
            Arrived = 0;
            Depot = 0;
            Version++;
        }
    }

    /// <summary>
    /// Notices every train the game removes and records why.
    ///
    /// The game removes a vehicle by adding Deleted to it through a command
    /// buffer that plays back at the start of the next frame
    /// (VehicleUtils.DeleteVehicle, EndFrameBarrier). From then until the
    /// Cleanup phase the entity still exists with all its components, which
    /// is when this system, in Modification1, reads it.
    /// </summary>
    public partial class DespawnWatchSystem : GameSystemBase
    {
        /// <summary>
        /// More trains than this removed in one frame are not listed one by
        /// one: that is the player bulldozing, or the game clearing a city,
        /// not trains failing.
        /// </summary>
        private const int kBulkRemoval = 20;

        private EntityQuery m_DeletedTrains;
        private NameSystem m_NameSystem;
        private TimeSystem m_TimeSystem;
        private SimulationSystem m_SimulationSystem;
        private UI.TrainsUISystem m_Trains;

        protected override void OnCreate()
        {
            base.OnCreate();
            m_NameSystem = World.GetOrCreateSystemManaged<NameSystem>();
            m_TimeSystem = World.GetOrCreateSystemManaged<TimeSystem>();
            m_SimulationSystem = World.GetOrCreateSystemManaged<SimulationSystem>();
            m_Trains = World.GetOrCreateSystemManaged<UI.TrainsUISystem>();
            m_DeletedTrains = GetEntityQuery(new EntityQueryDesc
            {
                All = new[]
                {
                    ComponentType.ReadOnly<Train>(),
                    ComponentType.ReadOnly<LayoutElement>(),
                    ComponentType.ReadOnly<PathOwner>(),
                    ComponentType.ReadOnly<Deleted>(),
                },
                None = new[] { ComponentType.ReadOnly<Temp>() },
            });
            RequireForUpdate(m_DeletedTrains);
        }

        protected override void OnGameLoaded(Context serializationContext)
        {
            base.OnGameLoaded(serializationContext);
            DespawnHistory.Clear();
        }

        protected override void OnUpdate()
        {
            try
            {
                using (NativeArray<Entity> trains = m_DeletedTrains.ToEntityArray(Allocator.Temp))
                {
                    if (trains.Length > kBulkRemoval)
                    {
                        Mod.Log.Info($"{trains.Length} trains removed in one frame, not listed one by one (bulldozing, or the city being cleared).");
                        return;
                    }
                    foreach (Entity train in trains)
                        Record(train);
                }
            }
            catch (Exception e)
            {
                Mod.Log.Critical(e, "Watching for removed trains failed and was switched off until the game is restarted.");
                Enabled = false;
            }
        }

        private void Record(Entity train)
        {
            EntityManager em = EntityManager;
            // Trams and subways share the vehicle components with trains, but
            // the panel and the dispatcher are about trains only.
            if (!em.TryGetComponent(train, out PrefabRef prefab) || !em.TryGetComponent(prefab.m_Prefab, out TrainData data)
                || (data.m_TrackType & Game.Net.TrackTypes.Train) == 0)
                return;
            PathFlags path = em.GetComponentData<PathOwner>(train).m_State;
            Entity target = em.TryGetComponent(train, out Target t) ? t.m_Target : Entity.Null;
            bool endReached = em.TryGetComponent(train, out TrainCurrentLane lane) && (lane.m_Front.m_LaneFlags & TrainLaneFlags.EndReached) != 0;

            var row = new TrainRow();
            if (em.TryGetComponent(train, out Game.Vehicles.PublicTransport passenger))
            {
                row.Through = (passenger.m_State & PublicTransportFlags.DummyTraffic) != 0;
                row.Returning = (passenger.m_State & PublicTransportFlags.Returning) != 0;
            }
            else if (em.TryGetComponent(train, out Game.Vehicles.CargoTransport cargo))
            {
                row.Cargo = true;
                row.Through = (cargo.m_State & CargoTransportFlags.DummyTraffic) != 0;
                row.Returning = (cargo.m_State & CargoTransportFlags.Returning) != 0;
            }

            DespawnCause cause = DespawnClassifier.Classify(new DespawnObservation
            {
                Stuck = (path & PathFlags.Stuck) != 0,
                PathFailed = (path & PathFlags.Failed) != 0,
                TargetExists = target != Entity.Null && em.Exists(target) && !em.HasComponent<Deleted>(target),
                Through = row.Through,
                Returning = row.Returning,
                EndReached = endReached,
            });
            if (DespawnClassifier.IsNormal(cause))
            {
                DespawnHistory.Count(cause);
                return;
            }

            TrainReader.ReadLoad(em, train, row);
            var record = new DespawnRecord
            {
                Time = m_TimeSystem.GetCurrentDateTime().ToString("HH:mm"),
                Cause = cause,
                Train = train.Index,
                Line = em.TryGetComponent(train, out Game.Routes.CurrentRoute route) ? NameText.Of(m_NameSystem, em, route.m_Route) : "",
                Cargo = row.Cargo,
                Through = row.Through,
                Returning = row.Returning,
                StoodMinutes = m_Trains.StandingMinutes(train, m_SimulationSystem.frameIndex),
                Position = em.TryGetComponent(train, out Game.Objects.Transform transform) ? transform.m_Position : default,
                Passengers = row.Passengers,
                LoadPercent = row.Cargo && row.LoadCapacity > 0 ? (int)math.round(100f * row.Load / row.LoadCapacity) : -1,
                Resource = row.MainResource ?? "",
            };

            BlockerChain chain = BlockerChains.Follow(EntityKey.Of(train), key => TrainDiagnosis.BlockerOf(em, key));
            foreach (long key in chain.Trains)
            {
                Entity other = EntityKey.ToEntity(key);
                string line = em.TryGetComponent(other, out Game.Routes.CurrentRoute otherRoute) ? NameText.Of(m_NameSystem, em, otherRoute.m_Route) : "";
                record.Chain.Add((other.Index, line));
            }
            record.ChainBackToStart = chain.BackToStart;

            DespawnHistory.Add(record);
            Mod.Log.Info(Describe(record));
        }

        /// <summary>One log line per removal, readable without the panel.</summary>
        private static string Describe(DespawnRecord r)
        {
            var text = new StringBuilder($"The game removed train #{r.Train}");
            string kind = r.Through ? "through traffic" : r.Cargo ? "freight" : "passenger";
            text.Append(r.Line != "" ? $" ({r.Line}, {kind})" : $" ({kind})");
            text.Append($": {r.Cause}. Stood {r.StoodMinutes:0} min at ({r.Position.x:0}, {r.Position.y:0}, {r.Position.z:0}).");
            if (r.Chain.Count > 0)
            {
                text.Append(" Waited for");
                foreach ((int train, string line) in r.Chain)
                    text.Append(line != "" ? $" #{train} ({line}) ->" : $" #{train} ->");
                text.Length -= 3;
                text.Append(r.ChainBackToStart ? $", which waited for #{r.Train} again." : ".");
            }
            if (r.Passengers > 0)
                text.Append($" {r.Passengers} passengers on board.");
            if (r.LoadPercent > 0)
                text.Append($" Load {r.LoadPercent} % {r.Resource}.");
            return text.ToString();
        }
    }
}
