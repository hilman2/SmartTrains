using System;
using System.Collections.Generic;
using System.IO;
using Colossal.Entities;
using Colossal.Serialization.Entities;
using Game;
using Game.City;
using Game.Simulation;
using Game.Vehicles;
using SmartTrains.Core.Metrics;
using SmartTrains.Core.Monitor;
using SmartTrains.Core.Network;
using SmartTrains.Dispatch;
using SmartTrains.Monitor;
using SmartTrains.Network;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;

namespace SmartTrains.Metrics
{
    /// <summary>
    /// Records how trains run, for comparing versions of the mod and finding
    /// where trains get stuck. The records go to ModsData/SmartTrains/Metrics
    /// in the game's user folder; tools/metrics/README.md describes them and
    /// the report that reads them.
    ///
    /// A session runs from loading a city until the next load or the end of
    /// the game. At every navigation step, right after the dispatcher's
    /// round, each train is sorted into what it does, and a record is written
    /// whenever that changes. Other systems report events here: the
    /// dispatcher its circles, releases and cut grants, the network reader a
    /// new network, the removal watch every removed train.
    ///
    /// Metrics must never get in the way of the game: if writing fails, they
    /// switch off until the next load and say so in the log.
    /// </summary>
    public partial class MetricsSystem : GameSystemBase
    {
        /// <summary>The frames in which TrainNavigationSystem moves trains; see DispatchSystem.</summary>
        private const int kInterval = 16;

        private const int kOffset = 3;

        /// <summary>Simulation frames per second of train movement (TrainNavigationSystem: 4/15 s per 16 frames).</summary>
        private const float kFramesPerSecond = 60f;

        /// <summary>Simulation frames between snapshots: half a minute of train movement.</summary>
        private const uint kSnapshotFrames = 1800;

        private SimulationSystem m_Simulation;
        private NetworkSystem m_Network;
        private DispatchSystem m_Dispatch;
        private CityConfigurationSystem m_City;
        private Game.UI.NameSystem m_Names;
        private EntityQuery m_TrainQuery;
        private TrainReader m_Reader;

        private MetricsLog m_Log;
        private readonly IntervalTracker<Details> m_Tracker = new IntervalTracker<Details>();
        private readonly HashSet<long> m_KnownTrains = new HashSet<long>();
        private readonly HashSet<int> m_KnownStations = new HashSet<int>();
        private uint m_NextSnapshot;
        private bool? m_LastActive;

        /// <summary>A city is loaded for playing, not the editor or the main menu.</summary>
        private bool m_InGame;

        /// <summary>Writing failed; nothing more is recorded until the next load.</summary>
        private bool m_Failed;

        /// <summary>What an interval records besides its times; taken when it begins.</summary>
        private struct Details
        {
            /// <summary>"Running", "Slowed", "Queued", or the name of the WaitReason.</summary>
            public string State;

            /// <summary>The HoldReason while the dispatcher holds (or, switched off, would hold) the standing train; null otherwise.</summary>
            public string Hold;

            public bool Active;

            /// <summary>The dispatcher let the train go although it would hold it: a waiting circle, or held too long.</summary>
            public bool Released;

            /// <summary>Entity index of the train in the way or waited for; 0 if none.</summary>
            public int By;

            public int Version;

            /// <summary>Entity index of the lane under the train's front.</summary>
            public int Lane;

            public float X;
            public float Z;

            /// <summary>A car stands on a turnout or a crossing. Only known for standing trains.</summary>
            public bool Junction;

            /// <summary>Entity index of the station of the lane under the front; 0 off platforms.</summary>
            public int Station;

            /// <summary>The dispatcher's speed advice in metres per second, for a slowed train.</summary>
            public float Advice;
        }

        public override int GetUpdateInterval(SystemUpdatePhase phase)
        {
            return kInterval;
        }

        public override int GetUpdateOffset(SystemUpdatePhase phase)
        {
            return kOffset;
        }

        protected override void OnCreate()
        {
            base.OnCreate();
            m_Simulation = World.GetOrCreateSystemManaged<SimulationSystem>();
            m_Network = World.GetOrCreateSystemManaged<NetworkSystem>();
            m_Dispatch = World.GetOrCreateSystemManaged<DispatchSystem>();
            m_City = World.GetOrCreateSystemManaged<CityConfigurationSystem>();
            m_Names = World.GetOrCreateSystemManaged<Game.UI.NameSystem>();
            m_TrainQuery = GetEntityQuery(TrainReader.QueryDesc());
            m_Reader = new TrainReader(EntityManager, m_TrainQuery);
        }

        protected override void OnGameLoaded(Context serializationContext)
        {
            base.OnGameLoaded(serializationContext);
            EndSession();
            m_InGame = serializationContext.purpose == Purpose.NewGame || serializationContext.purpose == Purpose.LoadGame;
            m_Failed = false;
        }

        protected override void OnDestroy()
        {
            EndSession();
            base.OnDestroy();
        }

        protected override void OnUpdate()
        {
            if (!EnsureSession())
                return;
            try
            {
                uint frame = m_Simulation.frameIndex;
                bool active = m_Dispatch.Active;
                if (m_LastActive != active)
                {
                    m_Log.Write("event", Event(frame, "dispatcher", active));
                    m_LastActive = active;
                }

                TrackNetwork network = m_Network.Layout?.Network;
                var samples = new List<StateSample<Details>>();
                var counts = new Dictionary<string, int>();
                using (NativeArray<Entity> trains = m_TrainQuery.ToEntityArray(Allocator.Temp))
                {
                    foreach (Entity train in trains)
                    {
                        m_Dispatch.States.TryGetValue(train, out DispatchState state);
                        TrainRow row = m_Reader.ReadState(train, frame, state, active);
                        if (row == null)
                            continue;
                        long key = EntityKey.Of(train);
                        if (m_KnownTrains.Add(key))
                            WriteTrain(train, row, frame);
                        Details details = Describe(train, row, state, active, network);
                        counts.TryGetValue(details.State, out int count);
                        counts[details.State] = count + 1;
                        samples.Add(new StateSample<Details>
                        {
                            Train = key,
                            Key = $"{details.State}|{details.Hold}|{details.Active}",
                            Odometer = EntityManager.TryGetComponent(train, out Odometer odometer) ? odometer.m_Distance : 0f,
                            Details = details,
                        });
                    }
                }
                foreach (StateInterval<Details> interval in m_Tracker.Observe(frame, samples))
                    WriteInterval(interval);

                if (frame >= m_NextSnapshot)
                {
                    WriteSnapshot(frame, samples.Count, counts, active);
                    m_NextSnapshot = frame + kSnapshotFrames;
                }
                m_Log.Flush(now: false);
            }
            catch (Exception e)
            {
                Fail(e);
            }
        }

        // ---- Session ----

        /// <summary>Opens the session's files on first use after a load; false if nothing is to be recorded.</summary>
        private bool EnsureSession()
        {
            if (m_Log != null)
                return true;
            if (!m_InGame || m_Failed)
                return false;
            try
            {
                string root = Path.Combine(UnityEngine.Application.persistentDataPath, "ModsData", "SmartTrains", "Metrics");
                m_Log = new MetricsLog(root);
                uint frame = m_Simulation.frameIndex;
                m_Log.Write("session", m_Log.Record()
                    .Add("started", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"))
                    .Add("city", m_City.cityName)
                    .Add("build", Mod.Build)
                    .Add("frame", frame)
                    .Add("active", m_Dispatch.Active));
                m_NextSnapshot = frame;
                Mod.Log.Info($"Metrics session {m_Log.Session} in {root}.");
                return true;
            }
            catch (Exception e)
            {
                Fail(e);
                return false;
            }
        }

        /// <summary>Writes what is still open and closes the files.</summary>
        private void EndSession()
        {
            if (m_Log != null)
            {
                try
                {
                    foreach (StateInterval<Details> interval in m_Tracker.CloseAll())
                        WriteInterval(interval);
                    m_Log.Flush(now: true);
                }
                catch (Exception e)
                {
                    Mod.Log.Warn(e, "Could not write the end of the metrics session.");
                }
                m_Log.Dispose();
                m_Log = null;
            }
            m_Tracker.CloseAll();
            m_KnownTrains.Clear();
            m_KnownStations.Clear();
            m_LastActive = null;
        }

        private void Fail(Exception e)
        {
            Mod.Log.Critical(e, "Recording metrics failed and was switched off until the next city is loaded. Trains are not affected.");
            m_Failed = true;
            try
            {
                m_Log?.Dispose();
            }
            catch (Exception)
            {
                // The files are broken already; the log above says so.
            }
            m_Log = null;
        }

        // ---- Trains ----

        private Details Describe(Entity train, TrainRow row, DispatchState state, bool active, TrackNetwork network)
        {
            EntityManager em = EntityManager;
            var details = new Details
            {
                State = row.Reason != WaitReason.None ? row.Reason.ToString() : m_Dispatch.IsSlowing(train) ? "Slowed" : "Running",
                Active = active,
                Released = state != null && state.Released,
                By = row.Blocker.Index,
                Version = train.Version,
            };
            // Stopped behind the train the dispatcher knows to be ahead: the
            // train has closed up as planned. Any other train in the way is
            // one the dispatcher did not expect there.
            bool byTrain = row.Reason == WaitReason.TrainAhead || row.Reason == WaitReason.CrossingTrain || row.Reason == WaitReason.OncomingTrain;
            if (byTrain && state != null && row.Blocker != Entity.Null && state.Order.Ahead == EntityKey.Of(row.Blocker))
                details.State = "Queued";
            if (!row.Moving && state != null && state.Holding)
                details.Hold = state.Order.Reason.ToString();
            if (details.State == "Slowed")
                details.Advice = state.Order.SpeedAdvice;
            if (em.TryGetComponent(train, out Game.Objects.Transform transform))
            {
                details.X = transform.m_Position.x;
                details.Z = transform.m_Position.z;
            }
            if (em.TryGetComponent(train, out TrainCurrentLane current))
            {
                Entity lane = current.m_Front.m_Lane;
                details.Lane = lane.Index;
                int index = network != null && lane != Entity.Null ? network.IndexOf(EntityKey.Of(lane)) : -1;
                if (index >= 0 && network.Lanes[index].Station != 0)
                    details.Station = Station(network.Lanes[index].Station);
            }
            if (!row.Moving && network != null)
                details.Junction = OnJunction(train, network);
            return details;
        }

        /// <summary>Whether a car of the train has a bogie on a turnout or crossing lane.</summary>
        private bool OnJunction(Entity train, TrackNetwork network)
        {
            EntityManager em = EntityManager;
            DynamicBuffer<LayoutElement> layout = em.GetBuffer<LayoutElement>(train, true);
            for (int i = 0; i < layout.Length; i++)
            {
                if (!em.TryGetComponent(layout[i].m_Vehicle, out TrainCurrentLane car))
                    continue;
                if (IsJunction(network, car.m_Front.m_Lane) || IsJunction(network, car.m_Rear.m_Lane))
                    return true;
            }
            return false;
        }

        private static bool IsJunction(TrackNetwork network, Entity lane)
        {
            int index = lane != Entity.Null ? network.IndexOf(EntityKey.Of(lane)) : -1;
            return index >= 0 && network.Lanes[index].Kind != LaneKind.Plain;
        }

        /// <summary>The station's entity index; writes its name the first time it comes up.</summary>
        private int Station(long key)
        {
            Entity station = EntityKey.ToEntity(key);
            if (m_KnownStations.Add(station.Index))
            {
                m_Log.Write("name", m_Log.Record()
                    .Add("kind", "station")
                    .Add("id", station.Index)
                    .Add("name", NameText.Of(m_Names, EntityManager, station)));
            }
            return station.Index;
        }

        private void WriteTrain(Entity train, TrainRow row, uint frame)
        {
            TrainMeasure measure = TrainMeasure.Of(EntityManager, train);
            m_Log.Write("train", m_Log.Record()
                .Add("f", frame)
                .Add("train", train.Index)
                .Add("v", train.Version)
                .Add("kind", row.Cargo ? "cargo" : "passenger")
                .Add("through", row.Through)
                .Add("returning", row.Returning)
                .Add("line", row.Line != Entity.Null ? NameText.Of(m_Names, EntityManager, row.Line) : null)
                .Add("cars", measure.Cars)
                .Add("length", measure.Length)
                .Add("maxSpeed", measure.MaxSpeed));
        }

        private void WriteInterval(StateInterval<Details> interval)
        {
            Details d = interval.Details;
            m_Log.Write("interval", m_Log.Record()
                .Add("train", EntityKey.ToEntity(interval.Train).Index)
                .Add("v", d.Version)
                .Add("state", d.State)
                .Add("hold", d.Hold)
                .Add("active", d.Active)
                .Add("released", d.Released)
                .Add("by", d.By)
                .Add("f", interval.From)
                .Add("until", interval.To)
                .Add("sec", (interval.To - interval.From) / kFramesPerSecond)
                .Add("dist", interval.Distance)
                .Add("truncated", interval.Cut)
                .Add("lane", d.Lane)
                .Add("x", math.round(d.X))
                .Add("z", math.round(d.Z))
                .Add("junction", d.Junction)
                .Add("station", d.Station)
                .Add("advice", d.Advice));
        }

        private void WriteSnapshot(uint frame, int trains, Dictionary<string, int> counts, bool active)
        {
            (int rounds, double meanMs, double maxMs) = m_Dispatch.TakeRoundTimes();
            JsonLine record = m_Log.Record()
                .Add("f", frame)
                .Add("trains", trains)
                .Add("active", active)
                .Add("rounds", rounds)
                .Add("roundMs", meanMs)
                .Add("roundMaxMs", maxMs);
            // Every state, also those no train is in, so that each snapshot
            // has the same fields.
            record.Add("Running", Count(counts, "Running")).Add("Slowed", Count(counts, "Slowed")).Add("Queued", Count(counts, "Queued"));
            foreach (WaitReason reason in (WaitReason[])Enum.GetValues(typeof(WaitReason)))
            {
                if (reason != WaitReason.None)
                    record.Add(reason.ToString(), Count(counts, reason.ToString()));
            }
            m_Log.Write("snapshot", record);
        }

        private static long Count(Dictionary<string, int> counts, string state)
        {
            return counts.TryGetValue(state, out int count) ? count : 0;
        }

        // ---- Events ----

        private JsonLine Event(uint frame, string type, bool active)
        {
            return m_Log.Record().Add("f", frame).Add("type", type).Add("active", active);
        }

        /// <summary>Writes an event, if a session is open; a failure switches metrics off like any other.</summary>
        private void WriteEvent(Func<uint, JsonLine> build)
        {
            if (!EnsureSession())
                return;
            try
            {
                m_Log.Write("event", build(m_Simulation.frameIndex));
            }
            catch (Exception e)
            {
                Fail(e);
            }
        }

        private JsonLine AddTrain(JsonLine record, Entity train)
        {
            record.Add("train", train.Index).Add("v", train.Version);
            if (EntityManager.Exists(train) && EntityManager.TryGetComponent(train, out Game.Objects.Transform transform))
                record.Add("x", math.round(transform.m_Position.x)).Add("z", math.round(transform.m_Position.z));
            return record;
        }

        /// <summary>The dispatcher cut back the train's grant, because another train now stands in its way.</summary>
        internal void GrantCut(Entity train, DispatchState state, bool active)
        {
            WriteEvent(frame => AddTrain(Event(frame, "cut", active), train)
                .Add("hold", state.Order.Reason.ToString())
                .Add("by", state.WaitingFor.Index)
                .Add("lane", state.HoldLane.Index));
        }

        /// <summary>The dispatcher let the train go after holding it too long.</summary>
        internal void LongHoldReleased(Entity train, DispatchState state, bool active)
        {
            WriteEvent(frame => AddTrain(Event(frame, "release", active), train)
                .Add("hold", state.Order.Reason.ToString())
                .Add("by", state.WaitingFor.Index)
                .Add("lane", state.BlockedLane.Index));
        }

        /// <summary>A circle of trains waiting for each other formed; the dispatcher let <paramref name="released"/> go.</summary>
        internal void Circle(List<long> circle, Entity released, bool active)
        {
            var trains = new List<long>();
            foreach (long key in circle)
                trains.Add(EntityKey.ToEntity(key).Index);
            WriteEvent(frame => AddTrain(Event(frame, "circle", active), released).Add("trains", trains));
        }

        /// <summary>The network was read anew.</summary>
        internal void NetworkRead(int version, LayoutSummary summary, long milliseconds)
        {
            WriteEvent(frame => Event(frame, "network", m_Dispatch.Active)
                .Add("version", version)
                .Add("lanes", summary.Lanes)
                .Add("sections", summary.Sections)
                .Add("junctionAreas", summary.JunctionAreas)
                .Add("singleTrack", summary.SingleTrackSections)
                .Add("singleTrackKm", summary.SingleTrackLength / 1000f)
                .Add("passingLoops", summary.PassingLoops)
                .Add("stationGroups", summary.StationGroups)
                .Add("doubleTracks", summary.DoubleTracks)
                .Add("ms", milliseconds));
        }

        /// <summary>The game removed the train; <paramref name="normal"/> if at the end of its trip.</summary>
        internal void Removed(Entity train, DespawnCause cause, bool normal)
        {
            WriteEvent(frame => AddTrain(Event(frame, "removed", m_Dispatch.Active), train)
                .Add("cause", cause.ToString())
                .Add("normal", normal));
        }
    }
}
