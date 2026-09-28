using System.Collections.Generic;
using Colossal.Entities;
using Game.Common;
using Game.Economy;
using Game.Net;
using Game.Pathfind;
using Game.Prefabs;
using Game.Routes;
using Game.Simulation;
using Game.Tools;
using Game.UI.InGame;
using Game.Vehicles;
using SmartTrains.Core.Monitor;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;

namespace SmartTrains.Monitor
{
    /// <summary>One train as the panel shows it. Entity fields are Null where there is nothing to show.</summary>
    internal sealed class TrainRow
    {
        /// <summary>The controller, i.e. the leading car, which the game treats as the train.</summary>
        public Entity Train;

        public bool Cargo;

        /// <summary>Through traffic between two outside connections; it never stops in the city.</summary>
        public bool Through;

        /// <summary>On the way back to its depot.</summary>
        public bool Returning;

        public Entity Line;

        /// <summary>The line's colour as "#rrggbb", or null without a line.</summary>
        public string LineColor;

        public bool Moving;
        public WaitReason Reason;
        public float StandingMinutes;

        /// <summary>The train in the way, as its controller; Null if nothing or not a train is in the way.</summary>
        public Entity Blocker;

        public int Passengers;
        public int PassengerCapacity;
        public int Load;
        public int LoadCapacity;

        /// <summary>The resource with the largest share of the load, by its enum name; null when empty.</summary>
        public string MainResource;

        public Entity From;
        public Entity To;

        /// <summary>The dispatcher's last decision on the train; null if it does not manage it.</summary>
        public Dispatch.DispatchState Dispatch;
    }

    /// <summary>
    /// Reads the state of every rail train from the game's components.
    ///
    /// Runs on the main thread through the EntityManager, which waits for the
    /// jobs that write the components it reads. That is why the UI system
    /// calls it about once a second and not every frame.
    /// </summary>
    internal sealed class TrainReader
    {
        /// <summary>Below this speed, in metres per second, a train counts as standing.</summary>
        private const float kStandingSpeed = 0.1f;

        private readonly EntityManager m_EntityManager;
        private readonly EntityQuery m_TrainQuery;
        private readonly StandingClock m_Clock = new StandingClock();

        public TrainReader(EntityManager entityManager, EntityQuery trainQuery)
        {
            m_EntityManager = entityManager;
            m_TrainQuery = trainQuery;
        }

        /// <summary>The query that finds train controllers; create it in the owning system.</summary>
        public static EntityQueryDesc QueryDesc()
        {
            return new EntityQueryDesc
            {
                All = new[]
                {
                    ComponentType.ReadOnly<Train>(),
                    ComponentType.ReadOnly<LayoutElement>(),
                    ComponentType.ReadOnly<Target>(),
                    ComponentType.ReadOnly<PathOwner>(),
                    ComponentType.ReadOnly<Blocker>(),
                    ComponentType.ReadOnly<Game.Objects.Moving>(),
                    ComponentType.ReadOnly<PrefabRef>(),
                },
                None = new[] { ComponentType.ReadOnly<Deleted>(), ComponentType.ReadOnly<Temp>() },
            };
        }

        /// <summary>Reads all rail trains; trams and subways are left out.</summary>
        /// <param name="frame">The current simulation frame, for the standing times.</param>
        /// <param name="dispatch">The dispatcher's last decision per train.</param>
        /// <param name="dispatcherActive">Whether the dispatcher holds trains, or only plans.</param>
        public List<TrainRow> Read(uint frame, IReadOnlyDictionary<Entity, Dispatch.DispatchState> dispatch, bool dispatcherActive)
        {
            var rows = new List<TrainRow>();
            using (NativeArray<Entity> trains = m_TrainQuery.ToEntityArray(Allocator.Temp))
            {
                foreach (Entity train in trains)
                {
                    dispatch.TryGetValue(train, out Dispatch.DispatchState state);
                    TrainRow row = ReadTrain(train, frame, state, dispatcherActive);
                    if (row != null)
                        rows.Add(row);
                }
            }
            m_Clock.Sweep();
            return rows;
        }

        private TrainRow ReadTrain(Entity train, uint frame, Dispatch.DispatchState dispatch, bool dispatcherActive)
        {
            TrainRow row = ReadState(train, frame, dispatch, dispatcherActive);
            if (row == null)
                return null;
            uint frames = m_Clock.Observe(EntityKey.Of(train), !row.Moving, frame);
            row.StandingMinutes = ToMinutes(frames);

            ReadLoad(m_EntityManager, train, row);
            ReadEnds(train, row);
            return row;
        }

        /// <summary>
        /// What the train is and does, without load, ends and standing time:
        /// the kind of train, its line, whether it moves, why not, and who is
        /// in its way. Cheap enough to call for every train at every
        /// navigation step. Null for a train that is not a rail train.
        /// </summary>
        public TrainRow ReadState(Entity train, uint frame, Dispatch.DispatchState dispatch, bool dispatcherActive)
        {
            EntityManager em = m_EntityManager;
            Entity prefab = em.GetComponentData<PrefabRef>(train).m_Prefab;
            if (!em.TryGetComponent(prefab, out TrainData trainData) || (trainData.m_TrackType & TrackTypes.Train) == 0)
                return null;

            var row = new TrainRow { Train = train, Dispatch = dispatch };
            bool held = dispatcherActive && dispatch != null && dispatch.Holding;
            bool boarding = false;
            bool departureDue = false;
            if (em.TryGetComponent(train, out Game.Vehicles.PublicTransport passenger))
            {
                boarding = (passenger.m_State & PublicTransportFlags.Boarding) != 0;
                departureDue = frame >= passenger.m_DepartureFrame;
                row.Through = (passenger.m_State & PublicTransportFlags.DummyTraffic) != 0;
                row.Returning = (passenger.m_State & PublicTransportFlags.Returning) != 0;
            }
            else if (em.TryGetComponent(train, out Game.Vehicles.CargoTransport cargo))
            {
                row.Cargo = true;
                boarding = (cargo.m_State & CargoTransportFlags.Boarding) != 0;
                departureDue = frame >= cargo.m_DepartureFrame;
                row.Through = (cargo.m_State & CargoTransportFlags.DummyTraffic) != 0;
                row.Returning = (cargo.m_State & CargoTransportFlags.Returning) != 0;
            }

            if (em.TryGetComponent(train, out CurrentRoute route) && em.Exists(route.m_Route))
            {
                row.Line = route.m_Route;
                if (em.TryGetComponent(route.m_Route, out Game.Routes.Color color))
                    row.LineColor = $"#{color.m_Color.r:x2}{color.m_Color.g:x2}{color.m_Color.b:x2}";
            }

            row.Moving = math.length(em.GetComponentData<Game.Objects.Moving>(HeadOf(em, train)).m_Velocity) >= kStandingSpeed;
            Blocker blocker = em.GetComponentData<Blocker>(train);
            PathFlags path = em.GetComponentData<PathOwner>(train).m_State;
            Entity blockingTrain = TrainOf(blocker.m_Blocker);
            row.Reason = WaitClassifier.Classify(new TrainObservation
            {
                Moving = row.Moving,
                Boarding = boarding,
                DepartureDue = departureDue,
                Obstacle = ToObstacle(blocker.m_Type),
                ObstacleIsTrain = blockingTrain != Entity.Null,
                RoutePending = (path & PathFlags.Pending) != 0,
                Stuck = (path & PathFlags.Stuck) != 0,
                HeldByDispatcher = held,
            });
            if (row.Reason == WaitReason.TrainAhead || row.Reason == WaitReason.CrossingTrain || row.Reason == WaitReason.OncomingTrain)
                row.Blocker = blockingTrain;
            else if (row.Reason == WaitReason.AtSignal)
                row.Blocker = dispatch.WaitingFor;
            return row;
        }

        /// <summary>
        /// In-game minutes the train has stood, as of the last reading; 0 if
        /// it was moving then or has not been read yet.
        /// </summary>
        public float StandingMinutes(Entity train, uint frame)
        {
            return m_Clock.TryGetSince(EntityKey.Of(train), out uint since) ? ToMinutes(frame - since) : 0f;
        }

        private static float ToMinutes(uint frames)
        {
            return frames * (24f * 60f / TimeSystem.kTicksPerDay);
        }

        private static Obstacle ToObstacle(BlockerType type)
        {
            switch (type)
            {
                case BlockerType.None:
                    return Obstacle.None;
                case BlockerType.Continuing:
                    return Obstacle.Ahead;
                case BlockerType.Crossing:
                    return Obstacle.Crossing;
                case BlockerType.Oncoming:
                    return Obstacle.Oncoming;
                case BlockerType.Signal:
                    return Obstacle.Signal;
                case BlockerType.Limit:
                    return Obstacle.Limit;
                default:
                    return Obstacle.Other;
            }
        }

        private Entity TrainOf(Entity blocker)
        {
            return TrainOf(m_EntityManager, blocker);
        }

        /// <summary>
        /// The car at the head of the train: the first in its layout, which
        /// the game steers the train by (TrainNavigationSystem). That is the
        /// controller only until the train first reverses: VehicleUtils.
        /// ReverseTrain turns the layout around and leaves the controller,
        /// which keeps the train's route, navigation lanes and blocker, at the
        /// tail. Where the train is, how fast, and its navigation speed are
        /// read from and written to the head.
        /// </summary>
        public static Entity HeadOf(EntityManager em, Entity train)
        {
            if (em.TryGetBuffer(train, true, out DynamicBuffer<LayoutElement> layout) && layout.Length > 0)
                return layout[0].m_Vehicle;
            return train;
        }

        /// <summary>
        /// The train a blocker belongs to, as its controller; Null if the
        /// blocker is not part of a train. The game names a single car as the
        /// blocker, or the car that reserved the track ahead.
        /// </summary>
        public static Entity TrainOf(EntityManager em, Entity blocker)
        {
            if (blocker == Entity.Null || !em.Exists(blocker) || !em.HasComponent<Train>(blocker))
                return Entity.Null;
            if (em.TryGetComponent(blocker, out Controller controller) && controller.m_Controller != Entity.Null)
                return controller.m_Controller;
            return blocker;
        }

        /// <summary>
        /// Sums passengers and cargo over all cars into <paramref name="row"/>,
        /// as the game's own vehicle panel does (PassengersSection, CargoSection).
        /// </summary>
        public static void ReadLoad(EntityManager em, Entity train, TrainRow row)
        {
            var resources = new Dictionary<Resource, int>();
            DynamicBuffer<LayoutElement> layout = em.GetBuffer<LayoutElement>(train, true);
            for (int i = 0; i < layout.Length; i++)
            {
                Entity car = layout[i].m_Vehicle;
                Entity prefab = em.GetComponentData<PrefabRef>(car).m_Prefab;
                if (em.TryGetComponent(prefab, out PublicTransportVehicleData passengerData))
                    row.PassengerCapacity += passengerData.m_PassengerCapacity;
                if (em.TryGetComponent(prefab, out CargoTransportVehicleData cargoData))
                    row.LoadCapacity += cargoData.m_CargoCapacity;
                if (em.TryGetBuffer(car, true, out DynamicBuffer<Passenger> passengers))
                    row.Passengers += passengers.Length;
                if (em.TryGetBuffer(car, true, out DynamicBuffer<Resources> load))
                {
                    for (int j = 0; j < load.Length; j++)
                    {
                        if (load[j].m_Amount <= 0)
                            continue;
                        row.Load += load[j].m_Amount;
                        resources.TryGetValue(load[j].m_Resource, out int amount);
                        resources[load[j].m_Resource] = amount + load[j].m_Amount;
                    }
                }
            }
            int largest = 0;
            foreach (KeyValuePair<Resource, int> entry in resources)
            {
                if (entry.Value > largest)
                {
                    largest = entry.Value;
                    row.MainResource = entry.Key.ToString();
                }
            }
        }

        /// <summary>
        /// Where the train comes from and goes to. For a line train these are
        /// its previous and next stop; for through traffic, the outside
        /// connections; for a train returning to its depot, the depot.
        /// </summary>
        private void ReadEnds(Entity train, TrainRow row)
        {
            EntityManager em = m_EntityManager;
            row.To = VehicleUIUtils.GetDestination(em, train);
            if (row.Through)
            {
                row.From = OutsideConnectionOf(train);
                return;
            }
            if (row.Returning || row.Line == Entity.Null)
                return;
            Entity target = em.GetComponentData<Target>(train).m_Target;
            if (em.TryGetComponent(target, out Waypoint waypoint) && em.TryGetBuffer(row.Line, true, out DynamicBuffer<RouteWaypoint> waypoints))
                row.From = PreviousStop(waypoints, waypoint.m_Index);
        }

        /// <summary>
        /// The station before waypoint <paramref name="index"/> on the line,
        /// walking backwards past waypoints without a stop. Returns the
        /// station building, like VehicleUIUtils.GetDestination does for the
        /// next stop, so both ends read alike.
        /// </summary>
        private Entity PreviousStop(DynamicBuffer<RouteWaypoint> waypoints, int index)
        {
            EntityManager em = m_EntityManager;
            int count = waypoints.Length;
            for (int step = 1; step < count; step++)
            {
                int i = ((index - step) % count + count) % count;
                if (!em.TryGetComponent(waypoints[i].m_Waypoint, out Connected connected) || connected.m_Connected == Entity.Null)
                    continue;
                Entity stop = connected.m_Connected;
                return em.TryGetComponent(stop, out Owner owner) ? owner.m_Owner : stop;
            }
            return Entity.Null;
        }

        /// <summary>The outside connection a through train came from: the first owner up the chain that is one.</summary>
        private Entity OutsideConnectionOf(Entity train)
        {
            EntityManager em = m_EntityManager;
            Entity entity = train;
            for (int depth = 0; depth < 8 && em.TryGetComponent(entity, out Owner owner); depth++)
            {
                entity = owner.m_Owner;
                if (em.HasComponent<Game.Objects.OutsideConnection>(entity))
                    return entity;
            }
            return Entity.Null;
        }
    }
}
