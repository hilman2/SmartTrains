using System;
using System.Collections.Generic;
using Colossal.Entities;
using Colossal.Serialization.Entities;
using Game;
using Game.Prefabs;
using Game.Simulation;
using Game.Vehicles;
using SmartTrains.Core.Dispatch;
using SmartTrains.Core.Network;
using SmartTrains.Monitor;
using SmartTrains.Network;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;

namespace SmartTrains.Dispatch
{
    /// <summary>What the dispatcher decided for one train in its last round.</summary>
    internal sealed class DispatchState
    {
        public TrainOrder Order;

        /// <summary>The lane the train must not enter; Null if it may run on.</summary>
        public Entity HoldLane;

        /// <summary>The train it waits for; Null if none.</summary>
        public Entity WaitingFor;

        /// <summary>The lane the dispatcher found not free, for diagnosis; Null if none.</summary>
        public Entity BlockedLane;

        /// <summary>
        /// The dispatcher would hold the train, but lets it go because the
        /// hold closes a circle of trains waiting for each other.
        /// </summary>
        public bool Released;

        /// <summary>Lanes in the route the dispatcher saw, for diagnosis.</summary>
        public int RouteLength;
    }

    /// <summary>
    /// Runs the dispatcher and holds trains where it says.
    ///
    /// Runs in the simulation right before TrainNavigationSystem, which
    /// reserves track for trains. A dispatcher round every
    /// <see cref="kRoundInterval"/> steps decides; every step the marks are
    /// put back in place, since the game rebuilds navigation lanes whenever a
    /// train gets a new route. With the dispatcher switched off in the
    /// panel, rounds still run so the panel can show what it would do, but
    /// no train is marked.
    /// </summary>
    public partial class DispatchSystem : GameSystemBase
    {
        private const int kRoundInterval = 4;

        /// <summary>
        /// In-game minutes after which a held train is let go regardless. A
        /// hold that long means the dispatcher misjudges the situation, and
        /// the train is better off with the game's own handling.
        /// </summary>
        private const float kMaxHoldMinutes = 30f;

        // Base ranks: passengers first, then freight on a line, then through
        // traffic, which only crosses the city. Two points are one minute of
        // waiting (Dispatcher.RankPerMinute).
        private const float kRankPassenger = 20f;
        private const float kRankCargo = 12f;
        private const float kRankThroughPassenger = 10f;
        private const float kRankThroughCargo = 6f;
        private const float kRankReturning = 4f;

        /// <summary>
        /// Metres added to a train's braking distance for the look-ahead: the
        /// dispatcher must grant track before the game reserves it, and the
        /// game looks ahead by the braking distance plus a little.
        /// </summary>
        private const float kLookAheadMargin = 100f;

        private NetworkSystem m_Network;
        private SimulationSystem m_Simulation;
        private UI.TrainsUISystem m_TrainsUI;
        private EntityQuery m_TrainQuery;

        private Dispatcher m_Dispatcher;
        private int m_LayoutVersion = -1;
        private int m_Step;

        private readonly Dictionary<Entity, DispatchState> m_States = new Dictionary<Entity, DispatchState>();
        private readonly Dictionary<Entity, HoldMark> m_Marks = new Dictionary<Entity, HoldMark>();

        /// <summary>The last round's decision per train, for the panel.</summary>
        internal IReadOnlyDictionary<Entity, DispatchState> States => m_States;

        /// <summary>Whether trains are held, not just planned for.</summary>
        public bool Active => Mod.Settings != null && Mod.Settings.DispatcherActive;

        protected override void OnCreate()
        {
            base.OnCreate();
            m_Network = World.GetOrCreateSystemManaged<NetworkSystem>();
            m_Simulation = World.GetOrCreateSystemManaged<SimulationSystem>();
            m_TrainsUI = World.GetOrCreateSystemManaged<UI.TrainsUISystem>();
            m_TrainQuery = GetEntityQuery(TrainReader.QueryDesc());
        }

        protected override void OnGameLoaded(Context serializationContext)
        {
            base.OnGameLoaded(serializationContext);
            m_States.Clear();
            m_Marks.Clear();
            m_Dispatcher = null;
            m_LayoutVersion = -1;
        }

        protected override void OnUpdate()
        {
            try
            {
                TrackLayout layout = m_Network.Layout;
                if (layout == null)
                    return;
                if (m_Network.Version != m_LayoutVersion)
                {
                    // Lane indices change with a new layout; grants and marks
                    // of the old one mean nothing any more.
                    ClearAllMarks();
                    m_States.Clear();
                    m_Dispatcher = new Dispatcher(layout);
                    m_LayoutVersion = m_Network.Version;
                }
                if (m_Step++ % kRoundInterval == 0)
                    Round(layout.Network);
                if (Active)
                    PlaceMarks(layout.Network);
                else if (m_Marks.Count > 0)
                    ClearAllMarks();
            }
            catch (Exception e)
            {
                Mod.Log.Critical(e, "The dispatcher failed and was switched off until the game is restarted. Trains run as without the mod.");
                ClearAllMarks();
                Enabled = false;
            }
        }

        /// <summary>
        /// Takes every mark off. Called before the game saves, so that a save
        /// never holds a train that the mod, if removed, would not release.
        /// </summary>
        public void ClearAllMarks()
        {
            foreach (KeyValuePair<Entity, HoldMark> entry in m_Marks)
                HoldActuator.Clear(EntityManager, entry.Key, entry.Value);
            m_Marks.Clear();
        }

        // ---- Deciding ----

        private void Round(TrackNetwork network)
        {
            uint frame = m_Simulation.frameIndex;
            var inputs = new List<TrainInput>();
            var trains = new List<(Entity Train, TrainRoute Route)>();
            using (NativeArray<Entity> entities = m_TrainQuery.ToEntityArray(Allocator.Temp))
            {
                foreach (Entity train in entities)
                {
                    TrainInput input = Input(network, train, frame, out TrainRoute route);
                    if (input == null)
                        continue;
                    inputs.Add(input);
                    trains.Add((train, route));
                }
            }

            List<TrainOrder> orders = m_Dispatcher.Dispatch(inputs);
            m_States.Clear();
            for (int i = 0; i < trains.Count; i++)
            {
                (Entity train, TrainRoute route) = trains[i];
                TrainOrder order = orders[i];
                var state = new DispatchState { Order = order, RouteLength = route.Entries.Count };
                if (order.HoldAt >= 0 && order.HoldAt < route.Entries.Count)
                    state.HoldLane = route.Entries[order.HoldAt].Lane;
                if (order.BlockedAt >= 0 && order.BlockedAt < route.Entries.Count)
                    state.BlockedLane = route.Entries[order.BlockedAt].Lane;
                if (order.WaitingFor != 0)
                    state.WaitingFor = EntityKey.ToEntity(order.WaitingFor);
                m_States[train] = state;
            }
            BreakWaitingCircles();
            ReleaseLongHolds(frame);
        }

        /// <summary>Trains already reported as held too long, so each is written to the log once.</summary>
        private readonly HashSet<Entity> m_LoggedLongHolds = new HashSet<Entity>();

        /// <summary>Lets go of trains held longer than <see cref="kMaxHoldMinutes"/>, and says why they were held.</summary>
        private void ReleaseLongHolds(uint frame)
        {
            foreach (KeyValuePair<Entity, DispatchState> entry in m_States)
            {
                DispatchState state = entry.Value;
                if (state.HoldLane == Entity.Null || m_TrainsUI.StandingMinutes(entry.Key, frame) < kMaxHoldMinutes)
                    continue;
                state.HoldLane = Entity.Null;
                state.Released = true;
                if (Active && m_LoggedLongHolds.Add(entry.Key))
                {
                    string by = state.WaitingFor != Entity.Null ? $"#{state.WaitingFor.Index}" : "nobody known";
                    Mod.Log.Info($"Train #{entry.Key.Index} held for over {kMaxHoldMinutes:0} min ({state.Order.Reason} by {by} at lane #{state.BlockedLane.Index}); the dispatcher lets it go.");
                }
            }
            if (m_LoggedLongHolds.Count > 500)
                m_LoggedLongHolds.Clear();
        }

        /// <summary>
        /// Lets go of holds that close a circle of waiting trains. Such a
        /// circle can form where the dispatcher holds a train that others
        /// need out of the way, e.g. one standing at a platform that a queue
        /// waits to enter, while the track it would leave by is held by that
        /// queue. It also forms from jams the city had before the dispatcher
        /// was switched on. The game cannot see these circles: a held train
        /// reports nothing in its way. So the dispatcher lets the train of
        /// highest rank in the circle go and leaves it to the game, which at
        /// worst handles it as it would without the mod.
        /// </summary>
        private void BreakWaitingCircles()
        {
            EntityManager em = EntityManager;
            var waits = new Dictionary<long, long>();
            foreach (KeyValuePair<Entity, DispatchState> entry in m_States)
            {
                Entity train = entry.Key;
                DispatchState state = entry.Value;
                if (state.HoldLane != Entity.Null && state.WaitingFor != Entity.Null)
                {
                    waits[EntityKey.Of(train)] = EntityKey.Of(state.WaitingFor);
                    continue;
                }
                // Not held: the train waits for whatever the game reports in
                // its way, if it stands.
                if (math.length(em.GetComponentData<Game.Objects.Moving>(train).m_Velocity) >= 0.1f)
                    continue;
                Entity blocker = TrainReader.TrainOf(em, em.GetComponentData<Blocker>(train).m_Blocker);
                if (blocker != Entity.Null && blocker != train)
                    waits[EntityKey.Of(train)] = EntityKey.Of(blocker);
            }

            foreach (List<long> circle in WaitCycles.Find(waits))
            {
                Entity release = Entity.Null;
                float best = float.MinValue;
                foreach (long key in circle)
                {
                    Entity train = EntityKey.ToEntity(key);
                    if (m_States.TryGetValue(train, out DispatchState state) && state.HoldLane != Entity.Null && state.Order.Rank > best)
                    {
                        best = state.Order.Rank;
                        release = train;
                    }
                }
                if (release == Entity.Null)
                    continue;
                DispatchState released = m_States[release];
                released.HoldLane = Entity.Null;
                released.Released = true;
                LogCircle(release, circle);
            }
        }

        /// <summary>Circles already written to the log, so a circle that lasts is not written every round.</summary>
        private readonly HashSet<string> m_LoggedCircles = new HashSet<string>();

        private void LogCircle(Entity released, List<long> circle)
        {
            var names = new List<string>();
            foreach (long key in circle)
                names.Add("#" + EntityKey.ToEntity(key).Index);
            var sorted = new List<string>(names);
            sorted.Sort(StringComparer.Ordinal);
            if (!m_LoggedCircles.Add(string.Join(",", sorted)))
                return;
            if (m_LoggedCircles.Count > 200)
                m_LoggedCircles.Clear();
            Mod.Log.Info($"Waiting circle {string.Join(" -> ", names)} -> {names[0]}; the dispatcher lets #{released.Index} go.");
        }

        /// <summary>The dispatcher's view of one train; null for a train not on train track.</summary>
        private TrainInput Input(TrackNetwork network, Entity train, uint frame, out TrainRoute route)
        {
            EntityManager em = EntityManager;
            route = null;
            Entity prefab = em.GetComponentData<PrefabRef>(train).m_Prefab;
            if (!em.TryGetComponent(prefab, out TrainData data) || (data.m_TrackType & Game.Net.TrackTypes.Train) == 0)
                return null;
            if (!em.HasComponent<TrainCurrentLane>(train))
                return null;

            // A train whose route the game is replacing keeps only what it
            // stands on; its navigation lanes are about to be rebuilt.
            route = RouteReader.Read(em, network, train);
            if (route.Moves.Count == 0)
                return null;
            if (HoldActuator.RouteInFlux(em, train))
            {
                route.Moves.RemoveRange(1, route.Moves.Count - 1);
                route.Entries.RemoveRange(1, route.Entries.Count - 1);
                route.Committed = 0;
            }

            var input = new TrainInput
            {
                Id = EntityKey.Of(train),
                BasePriority = BaseRank(train),
                WaitingMinutes = m_TrainsUI.StandingMinutes(train, frame),
                FrontRemaining = route.FrontRemaining,
                Committed = route.Committed,
                // Changing track needs the route rewritten in the game, which
                // comes later; until then the dispatcher only holds trains.
                MayChangeTrack = false,
            };
            input.Route.AddRange(route.Moves);
            input.Occupied.AddRange(route.Occupied);
            MeasureTrain(train, out input.Length, out input.LookAhead);
            return input;
        }

        private float BaseRank(Entity train)
        {
            EntityManager em = EntityManager;
            if (em.TryGetComponent(train, out Game.Vehicles.PublicTransport passenger))
            {
                if ((passenger.m_State & PublicTransportFlags.Returning) != 0)
                    return kRankReturning;
                return (passenger.m_State & PublicTransportFlags.DummyTraffic) != 0 ? kRankThroughPassenger : kRankPassenger;
            }
            if (em.TryGetComponent(train, out Game.Vehicles.CargoTransport cargo))
            {
                if ((cargo.m_State & CargoTransportFlags.Returning) != 0)
                    return kRankReturning;
                return (cargo.m_State & CargoTransportFlags.DummyTraffic) != 0 ? kRankThroughCargo : kRankCargo;
            }
            return kRankReturning;
        }

        /// <summary>
        /// The train's length, as VehicleUtils.CalculateLength has it, and how
        /// far ahead it needs track granted: its braking distance from top
        /// speed plus the game's signal distance (VehicleUtils), with the
        /// slowest-braking car setting the pace.
        /// </summary>
        private void MeasureTrain(Entity train, out float length, out float lookAhead)
        {
            EntityManager em = EntityManager;
            length = 0f;
            float speed = float.MaxValue;
            float braking = float.MaxValue;
            DynamicBuffer<LayoutElement> layout = em.GetBuffer<LayoutElement>(train, true);
            for (int i = 0; i < layout.Length; i++)
            {
                Entity prefab = em.GetComponentData<PrefabRef>(layout[i].m_Vehicle).m_Prefab;
                if (!em.TryGetComponent(prefab, out TrainData data))
                    continue;
                length += math.csum(data.m_AttachOffsets);
                speed = math.min(speed, data.m_MaxSpeed);
                braking = math.min(braking, data.m_Braking);
            }
            if (speed == float.MaxValue || braking <= 0f)
            {
                lookAhead = 1000f;
                return;
            }
            lookAhead = 0.5f * speed * speed / braking + 4f * speed + kLookAheadMargin;
        }

        // ---- Holding ----

        /// <summary>
        /// Puts every hold in place. Each train's route is read again, since
        /// it has moved since the round; the hold lane is looked up in it by
        /// entity. A hold that the train has already reserved can no longer be
        /// kept; the train is then past the point where it could stop.
        /// </summary>
        private void PlaceMarks(TrackNetwork network)
        {
            var released = new List<Entity>();
            foreach (KeyValuePair<Entity, HoldMark> entry in m_Marks)
            {
                if (!m_States.TryGetValue(entry.Key, out DispatchState state) || state.HoldLane != entry.Value.Lane)
                    released.Add(entry.Key);
            }
            foreach (Entity train in released)
            {
                HoldActuator.Clear(EntityManager, train, m_Marks[train]);
                m_Marks.Remove(train);
            }

            foreach (KeyValuePair<Entity, DispatchState> entry in m_States)
            {
                Entity train = entry.Key;
                Entity holdLane = entry.Value.HoldLane;
                if (holdLane == Entity.Null || !EntityManager.Exists(train) || HoldActuator.RouteInFlux(EntityManager, train))
                    continue;
                TrainRoute route = RouteReader.Read(EntityManager, network, train);
                int holdAt = route.Entries.FindIndex(e => e.Lane == holdLane);
                if (holdAt <= route.Committed)
                    continue;
                if (m_Marks.TryGetValue(train, out HoldMark existing) && IsMarked(train, existing))
                    continue;
                HoldMark mark = HoldActuator.Apply(EntityManager, train, route, holdAt);
                if (mark.Lane != Entity.Null)
                    m_Marks[train] = mark;
            }
        }

        /// <summary>Whether the mark is still on the train's navigation lanes; the game drops them with every new route.</summary>
        private bool IsMarked(Entity train, HoldMark mark)
        {
            DynamicBuffer<TrainNavigationLane> lanes = EntityManager.GetBuffer<TrainNavigationLane>(train, true);
            for (int i = 0; i < lanes.Length; i++)
            {
                if (lanes[i].m_Lane == mark.Lane && (lanes[i].m_Flags & TrainLaneFlags.ParkingSpace) != 0)
                    return true;
            }
            return false;
        }
    }
}
