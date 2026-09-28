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

        /// <summary>
        /// The dispatcher refused the train track it needs and holds it for
        /// that. A hold lane without a reason only marks where the grant ends
        /// ahead of a running train, beyond what it needs yet; that is not
        /// holding it.
        /// </summary>
        public bool Holding => HoldLane != Entity.Null && Order.Reason != HoldReason.None;
    }

    /// <summary>
    /// Runs the dispatcher and holds trains where it says.
    ///
    /// Runs in the simulation right before TrainNavigationSystem, which
    /// reserves track for trains. A dispatcher round every
    /// <see cref="kInterval"/> simulation frames decides and puts the marks
    /// in place. With the dispatcher switched off in the panel, rounds still
    /// run so the panel can show what it would do, but no train is marked.
    /// </summary>
    public partial class DispatchSystem : GameSystemBase
    {
        /// <summary>
        /// TrainNavigationSystem moves trains and reserves track for them
        /// every 16 simulation frames, at offset 3, each time by 4/15 of a
        /// second. The dispatcher decides in exactly those frames, right before
        /// it: more often changes nothing, less often leaves trains without an
        /// up-to-date hold for a move.
        /// </summary>
        private const int kInterval = 16;

        private const int kOffset = 3;

        /// <summary>Seconds of train movement per simulation frame (TrainNavigationSystem: 4/15 s per 16 frames).</summary>
        private const float kSecondsPerFrame = 1f / 60f;

        /// <summary>Seconds of train movement per step of TrainNavigationSystem.</summary>
        private const float kStepSeconds = kInterval * kSecondsPerFrame;

        public override int GetUpdateInterval(SystemUpdatePhase phase)
        {
            return kInterval;
        }

        public override int GetUpdateOffset(SystemUpdatePhase phase)
        {
            return kOffset;
        }

        /// <summary>
        /// In-game minutes after which a held train is let go regardless. A
        /// hold that long means the dispatcher misjudges the situation, and
        /// the train is better off with the game's own handling. It must be
        /// long: at high game speed an in-game hour passes in about a minute,
        /// and a train let go too early runs into the junction it was kept
        /// out of. Circles of waiting trains are broken separately, at once.
        /// </summary>
        private const float kMaxHoldMinutes = 240f;

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
        private Game.UI.NameSystem m_Names;
        private Metrics.MetricsSystem m_Metrics;
        private EntityQuery m_TrainQuery;

        private Dispatcher m_Dispatcher;
        private int m_LayoutVersion = -1;

        private readonly Dictionary<Entity, DispatchState> m_States = new Dictionary<Entity, DispatchState>();
        private readonly Dictionary<Entity, HoldMark> m_Marks = new Dictionary<Entity, HoldMark>();

        /// <summary>
        /// Each train's top speed, acceleration and braking, the weakest car
        /// setting each, as TrainNavigationSystem combines them.
        /// </summary>
        private readonly Dictionary<Entity, TrainData> m_Pace = new Dictionary<Entity, TrainData>();

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
            m_Names = World.GetOrCreateSystemManaged<Game.UI.NameSystem>();
            m_Metrics = World.GetOrCreateSystemManaged<Metrics.MetricsSystem>();
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
                m_Watch.Restart();
                Round(layout.Network);
                m_Watch.Stop();
                CountTime(m_Watch.Elapsed.TotalMilliseconds);
                if (Active)
                {
                    PlaceMarks(layout.Network);
                    SlowDown();
                }
                else
                {
                    m_Slowed.Clear();
                    if (m_Marks.Count > 0)
                        ClearAllMarks();
                }
            }
            catch (Exception e)
            {
                Mod.Log.Critical(e, "The dispatcher failed and was switched off until the game is restarted. Trains run as without the mod.");
                ClearAllMarks();
                Enabled = false;
            }
        }

        private readonly System.Diagnostics.Stopwatch m_Watch = new System.Diagnostics.Stopwatch();
        private int m_TimedRounds;
        private double m_TimeSum;
        private double m_TimeMax;

        // The same, counted separately for the metrics; see TakeRoundTimes.
        private int m_MetricRounds;
        private double m_MetricTimeSum;
        private double m_MetricTimeMax;

        /// <summary>
        /// Rounds run since the last call, and their mean and longest time in
        /// milliseconds; starts counting afresh.
        /// </summary>
        internal (int Rounds, double MeanMs, double MaxMs) TakeRoundTimes()
        {
            var times = (m_MetricRounds, m_MetricRounds > 0 ? m_MetricTimeSum / m_MetricRounds : 0.0, m_MetricTimeMax);
            m_MetricRounds = 0;
            m_MetricTimeSum = 0;
            m_MetricTimeMax = 0;
            return times;
        }

        /// <summary>Trains running at the dispatcher's speed advice since the last round; see SlowDown.</summary>
        private readonly HashSet<Entity> m_Slowed = new HashSet<Entity>();

        /// <summary>Whether the train runs at the dispatcher's speed advice.</summary>
        internal bool IsSlowing(Entity train) => m_Slowed.Contains(train);

        /// <summary>Writes how long rounds take, every thousand rounds.</summary>
        private void CountTime(double ms)
        {
            m_MetricRounds++;
            m_MetricTimeSum += ms;
            m_MetricTimeMax = Math.Max(m_MetricTimeMax, ms);
            m_TimedRounds++;
            m_TimeSum += ms;
            m_TimeMax = Math.Max(m_TimeMax, ms);
            if (m_TimedRounds < 1000)
                return;
            Mod.Log.Info($"Dispatcher: {m_TimedRounds} rounds for {m_States.Count} trains, {m_TimeSum / m_TimedRounds:0.00} ms on average, {m_TimeMax:0.0} ms at most.");
            m_TimedRounds = 0;
            m_TimeSum = 0;
            m_TimeMax = 0;
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
            m_Pace.Clear();
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
            var states = new Dictionary<Entity, DispatchState>();
            for (int i = 0; i < trains.Count; i++)
            {
                (Entity train, TrainRoute route) = trains[i];
                // A train whose route is being replaced keeps its last
                // decision; see Input.
                if (HoldActuator.RouteInFlux(EntityManager, train) && m_States.TryGetValue(train, out DispatchState kept))
                {
                    states[train] = kept;
                    continue;
                }
                TrainOrder order = orders[i];
                var state = new DispatchState { Order = order, RouteLength = route.Entries.Count };
                if (order.HoldAt >= 0 && order.HoldAt < route.Entries.Count)
                    state.HoldLane = route.Entries[order.HoldAt].Lane;
                if (order.BlockedAt >= 0 && order.BlockedAt < route.Entries.Count)
                    state.BlockedLane = route.Entries[order.BlockedAt].Lane;
                if (order.WaitingFor != 0)
                    state.WaitingFor = EntityKey.ToEntity(order.WaitingFor);
                states[train] = state;
                if (order.CutBack)
                    m_Metrics.GrantCut(train, state, Active);
            }
            m_States.Clear();
            foreach (KeyValuePair<Entity, DispatchState> entry in states)
                m_States[entry.Key] = entry.Value;

            var gone = new List<Entity>();
            foreach (Entity train in m_LastRoutes.Keys)
            {
                if (!states.ContainsKey(train))
                    gone.Add(train);
            }
            foreach (Entity train in gone)
                m_LastRoutes.Remove(train);

            BreakWaitingCircles();
            ReleaseLongHolds(frame);
        }

        /// <summary>Each train's last route read while it was not being replaced.</summary>
        private readonly Dictionary<Entity, TrainRoute> m_LastRoutes = new Dictionary<Entity, TrainRoute>();

        /// <summary>
        /// The route to dispatch a train with whose route the game is
        /// replacing: its last known route from where its front is now. Its
        /// navigation lanes are cleared or about to change, and dispatching it
        /// with only the lane under it would drop its grant; other trains
        /// could then be granted track it needs, and the game would reserve
        /// that track for it anyway once the new route is in. Holding on to
        /// the old grant until the next ordinary round costs nothing.
        /// </summary>
        private TrainRoute RouteWhileReplaced(Entity train, TrainRoute fresh)
        {
            var kept = new TrainRoute { FrontRemaining = fresh.FrontRemaining };
            kept.Occupied.AddRange(fresh.Occupied);
            int from = -1;
            if (m_LastRoutes.TryGetValue(train, out TrainRoute last))
                from = last.Entries.FindIndex(e => e.Lane == fresh.Entries[0].Lane);
            if (from >= 0)
            {
                kept.Moves.AddRange(last.Moves.GetRange(from, last.Moves.Count - from));
                kept.Entries.AddRange(last.Entries.GetRange(from, last.Entries.Count - from));
            }
            else
            {
                kept.Moves.Add(fresh.Moves[0]);
                kept.Entries.Add(fresh.Entries[0]);
            }
            return kept;
        }

        /// <summary>Trains already reported as held too long, so each is written to the log once.</summary>
        private readonly HashSet<Entity> m_LoggedLongHolds = new HashSet<Entity>();

        /// <summary>Since which simulation frame the dispatcher has been holding each train, without a break.</summary>
        private readonly Dictionary<Entity, uint> m_HeldSince = new Dictionary<Entity, uint>();

        /// <summary>
        /// Trains let go for being held too long, until the dispatcher stops
        /// holding them: each such hold counts once in the metrics, though
        /// the train is let go again every round.
        /// </summary>
        private readonly HashSet<Entity> m_LongHoldsLetGo = new HashSet<Entity>();

        /// <summary>
        /// Lets go of trains held longer than <see cref="kMaxHoldMinutes"/>,
        /// and says why they were held. What counts is how long the
        /// dispatcher has held the train, not how long it has stood: a train
        /// that stood in a jam before the dispatcher came to it has not been
        /// held by it.
        /// </summary>
        private void ReleaseLongHolds(uint frame)
        {
            var stillHeld = new List<Entity>();
            foreach (KeyValuePair<Entity, DispatchState> entry in m_States)
            {
                if (entry.Value.Holding)
                    stillHeld.Add(entry.Key);
            }
            var ended = new List<Entity>();
            foreach (Entity train in m_HeldSince.Keys)
            {
                if (!stillHeld.Contains(train))
                    ended.Add(train);
            }
            foreach (Entity train in ended)
            {
                m_HeldSince.Remove(train);
                m_LongHoldsLetGo.Remove(train);
            }
            foreach (Entity train in stillHeld)
            {
                if (!m_HeldSince.ContainsKey(train))
                    m_HeldSince[train] = frame;
            }

            uint limit = (uint)(kMaxHoldMinutes * TimeSystem.kTicksPerDay / (24f * 60f));
            foreach (Entity train in stillHeld)
            {
                DispatchState state = m_States[train];
                if (frame - m_HeldSince[train] < limit)
                    continue;
                state.HoldLane = Entity.Null;
                state.Released = true;
                if (m_LongHoldsLetGo.Add(train))
                    m_Metrics.LongHoldReleased(train, state, Active);
                if (Active && m_LoggedLongHolds.Add(train))
                {
                    string by = state.WaitingFor != Entity.Null ? $"#{state.WaitingFor.Index}" : "nobody known";
                    Mod.Log.Info($"Train #{train.Index} held for over {kMaxHoldMinutes:0} min ({state.Order.Reason} by {by} at lane #{state.BlockedLane.Index}); the dispatcher lets it go.");
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
                // Only standing trains wait. Two trains still running towards
                // a crossing they both need, each held for the other, sort
                // themselves out: the one that has it runs through and frees
                // it. Only if both have come to a stop is it a circle.
                if (math.length(em.GetComponentData<Game.Objects.Moving>(TrainReader.HeadOf(em, train)).m_Velocity) >= 0.1f)
                    continue;
                if (state.Holding && state.WaitingFor != Entity.Null)
                {
                    waits[EntityKey.Of(train)] = EntityKey.Of(state.WaitingFor);
                    continue;
                }
                // Not held: the train waits for whatever the game reports in
                // its way.
                Entity blocker = TrainReader.TrainOf(em, em.GetComponentData<Blocker>(train).m_Blocker);
                if (blocker != Entity.Null && blocker != train)
                    waits[EntityKey.Of(train)] = EntityKey.Of(blocker);
            }

            var circles = new HashSet<string>();
            foreach (List<long> circle in WaitCycles.Find(waits))
            {
                // Let go of a held train that can then move: one the game
                // does not report stuck behind another train. Letting go of
                // one that is also blocked in the flesh would change nothing.
                // Among those, the one of highest rank.
                Entity release = Entity.Null;
                float best = float.MinValue;
                bool bestCanMove = false;
                foreach (long key in circle)
                {
                    Entity train = EntityKey.ToEntity(key);
                    if (!m_States.TryGetValue(train, out DispatchState state) || !state.Holding)
                        continue;
                    bool canMove = TrainReader.TrainOf(em, em.GetComponentData<Blocker>(train).m_Blocker) == Entity.Null;
                    if ((canMove && !bestCanMove) || (canMove == bestCanMove && state.Order.Rank > best))
                    {
                        best = state.Order.Rank;
                        bestCanMove = canMove;
                        release = train;
                    }
                }
                if (release == Entity.Null)
                    continue;
                DispatchState released = m_States[release];
                released.HoldLane = Entity.Null;
                released.Released = true;
                LogCircle(release, circle);

                // A circle lasts as long as the trains in it stand, and is
                // found again every round; it counts once, when it forms.
                var members = new List<long>(circle);
                members.Sort();
                string circleKey = string.Join(",", members);
                circles.Add(circleKey);
                if (!m_CirclesLastRound.Contains(circleKey))
                    m_Metrics.Circle(circle, release, Active);
            }
            m_CirclesLastRound = circles;
        }

        /// <summary>The circles found in the last round, each as its sorted train keys.</summary>
        private HashSet<string> m_CirclesLastRound = new HashSet<string>();

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
            // The trains and their track, for finding out how the circle came
            // about; only while the dispatcher holds trains, since circles
            // that only its plan contains change nothing in the game.
            if (!Active)
                return;
            var trains = new List<Entity>();
            foreach (long key in circle)
                trains.Add(EntityKey.ToEntity(key));
            uint frame = m_Simulation.frameIndex;
            TrainDiagnosis.WriteCircle(EntityManager, m_Names, t => m_TrainsUI.StandingMinutes(t, frame), trains, m_States);
        }

        /// <summary>The dispatcher's view of one train; null for a train not on train track.</summary>
        private TrainInput Input(TrackNetwork network, Entity train, uint frame, out TrainRoute route)
        {
            EntityManager em = EntityManager;
            route = null;
            Entity prefab = em.GetComponentData<PrefabRef>(train).m_Prefab;
            if (!em.TryGetComponent(prefab, out TrainData data) || (data.m_TrackType & Game.Net.TrackTypes.Train) == 0)
                return null;
            Entity head = TrainReader.HeadOf(em, train);
            if (!em.HasComponent<TrainCurrentLane>(head))
                return null;

            route = RouteReader.Read(em, network, train);
            if (route.Moves.Count == 0)
                return null;
            if (HoldActuator.RouteInFlux(em, train))
                route = RouteWhileReplaced(train, route);
            else
                m_LastRoutes[train] = route;

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
                Speed = math.length(em.GetComponentData<Game.Objects.Moving>(head).m_Velocity),
                DepartureIn = DepartureIn(train, frame),
            };
            input.Route.AddRange(route.Moves);
            input.Occupied.AddRange(route.Occupied);
            MeasureTrain(train, out input.Length, out input.LookAhead);
            return input;
        }

        /// <summary>
        /// Seconds of train movement until the train may leave the platform
        /// it boards at; -1 if it is not boarding. The game lets it go at its
        /// departure frame at the earliest, and later if passengers or cargo
        /// are not aboard yet, so this is a lower bound.
        /// </summary>
        private float DepartureIn(Entity train, uint frame)
        {
            EntityManager em = EntityManager;
            bool boarding = false;
            uint departure = 0;
            if (em.TryGetComponent(train, out Game.Vehicles.PublicTransport passenger) && (passenger.m_State & PublicTransportFlags.Boarding) != 0)
            {
                boarding = true;
                departure = passenger.m_DepartureFrame;
            }
            if (em.TryGetComponent(train, out Game.Vehicles.CargoTransport cargo) && (cargo.m_State & CargoTransportFlags.Boarding) != 0)
            {
                boarding = true;
                departure = math.max(departure, cargo.m_DepartureFrame);
            }
            if (!boarding)
                return -1f;
            return departure > frame ? (departure - frame) * kSecondsPerFrame : 0f;
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
        /// The train's length, and how far ahead it needs track granted: its
        /// braking distance from top speed plus the game's signal distance
        /// (VehicleUtils). Keeps the train's pace in <see cref="m_Pace"/> for
        /// <see cref="SlowDown"/>.
        /// </summary>
        private void MeasureTrain(Entity train, out float length, out float lookAhead)
        {
            TrainMeasure measure = TrainMeasure.Of(EntityManager, train);
            length = measure.Length;
            if (measure.Cars == 0 || measure.Braking <= 0f)
            {
                lookAhead = 1000f;
                return;
            }
            float speed = measure.MaxSpeed;
            lookAhead = 0.5f * speed * speed / measure.Braking + 4f * speed + kLookAheadMargin;
            m_Pace[train] = new TrainData { m_MaxSpeed = speed, m_Acceleration = measure.Acceleration, m_Braking = measure.Braking };
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

        /// <summary>
        /// Slows held trains to the dispatcher's advice, so that they roll up
        /// to their hold as it frees instead of stopping in front of it and
        /// starting again from a standstill.
        ///
        /// Only trains whose hold is in place: a train already past the point
        /// where it could stop runs through, and slowing it would only keep it
        /// on the track longer.
        /// </summary>
        private void SlowDown()
        {
            EntityManager em = EntityManager;
            m_Slowed.Clear();
            foreach (KeyValuePair<Entity, DispatchState> entry in m_States)
            {
                Entity train = entry.Key;
                DispatchState state = entry.Value;
                float advice = state.Order.SpeedAdvice;
                if (!state.Holding || advice <= 0f)
                    continue;
                if (!m_Marks.TryGetValue(train, out HoldMark mark) || mark.Lane != state.HoldLane)
                    continue;
                if (!m_Pace.TryGetValue(train, out TrainData pace) || !em.Exists(train))
                    continue;
                m_Slowed.Add(train);

                // TrainNavigationSystem starts from TrainNavigation.m_Speed and
                // ends its step between one step of braking below it and one
                // step of acceleration above it, as far as track and signals
                // allow. So the speed comes down no faster than the train
                // brakes, and is set a step of acceleration below the target,
                // for the game to end the step at the target.
                // The game reads the head's navigation; see TrainReader.HeadOf.
                Entity head = TrainReader.HeadOf(em, train);
                TrainNavigation navigation = em.GetComponentData<TrainNavigation>(head);
                float target = math.max(advice, navigation.m_Speed - pace.m_Braking * kStepSeconds);
                float rise = VehicleUtils.CalculateSpeedRange(pace, target, kStepSeconds).max - target;
                float start = math.max(0f, target - rise);
                if (start >= navigation.m_Speed)
                    continue;
                navigation.m_Speed = start;
                em.SetComponentData(head, navigation);
            }
        }
    }
}
