using System;
using System.Collections.Generic;
using Colossal.Entities;
using Colossal.UI.Binding;
using Game;
using Game.Rendering;
using Game.Simulation;
using Game.UI;
using Game.UI.InGame;
using SmartTrains.Core.Monitor;
using SmartTrains.Monitor;
using Unity.Entities;

namespace SmartTrains.UI
{
    /// <summary>
    /// Connects the train panel (src/SmartTrains.UI) with the simulation.
    ///
    /// Once a second it reads every rail train and, while the panel is open,
    /// sends the list. The trains are read even with the panel closed, so
    /// that standing times are already known when the player opens it. The
    /// panel talks back through triggers, all in the binding group
    /// "smartTrains".
    /// </summary>
    public partial class TrainsUISystem : UISystemBase
    {
        private const string kGroup = "smartTrains";

        /// <summary>More rows than this are cut off, the moving trains first (see Order).</summary>
        private const int kMaxRows = 400;

        private static readonly TimeSpan kReadInterval = TimeSpan.FromSeconds(1);

        private NameSystem m_NameSystem;
        private SimulationSystem m_SimulationSystem;
        private SelectedInfoUISystem m_SelectedInfo;
        private CameraUpdateSystem m_CameraSystem;

        /// <summary>
        /// Looked up on first use: DispatchSystem asks this system for
        /// standing times when it is created, so creating it from here as
        /// well would go round in a circle.
        /// </summary>
        private Dispatch.DispatchSystem m_Dispatch;

        private TrainReader m_Reader;
        private RawValueBinding m_TrainsBinding;
        private RawValueBinding m_DespawnsBinding;

        private bool m_PanelOpen;
        private DateTime m_NextRead;
        private List<TrainRow> m_Rows = new List<TrainRow>();

        /// <summary>DespawnHistory.Version when the removals were last sent; -1 forces a send.</summary>
        private int m_SentDespawns = -1;

        protected override void OnCreate()
        {
            base.OnCreate();
            m_NameSystem = World.GetOrCreateSystemManaged<NameSystem>();
            m_SimulationSystem = World.GetOrCreateSystemManaged<SimulationSystem>();
            m_SelectedInfo = World.GetOrCreateSystemManaged<SelectedInfoUISystem>();
            m_CameraSystem = World.GetOrCreateSystemManaged<CameraUpdateSystem>();
            m_Reader = new TrainReader(EntityManager, GetEntityQuery(TrainReader.QueryDesc()));

            m_TrainsBinding = new RawValueBinding(kGroup, "trains", WriteTrains);
            AddBinding(m_TrainsBinding);
            m_DespawnsBinding = new RawValueBinding(kGroup, "despawns", WriteDespawns);
            AddBinding(m_DespawnsBinding);
            AddBinding(new TriggerBinding<bool>(kGroup, "setPanelOpen", OnSetPanelOpen));
            AddBinding(new TriggerBinding<int, int>(kGroup, "select", OnSelect));
            AddBinding(new TriggerBinding<int, int>(kGroup, "diagnose", OnDiagnose));
            AddBinding(new TriggerBinding<int>(kGroup, "showDespawn", OnShowDespawn));
            AddBinding(new TriggerBinding(kGroup, "toggleDispatcher", OnToggleDispatcher));
        }

        private Dispatch.DispatchSystem DispatchSystem => m_Dispatch ?? (m_Dispatch = World.GetExistingSystemManaged<Dispatch.DispatchSystem>());

        private void OnToggleDispatcher()
        {
            if (Mod.Settings == null)
                return;
            Mod.Settings.DispatcherActive = !Mod.Settings.DispatcherActive;
            Mod.Settings.ApplyAndSave();
            Mod.Log.Info(Mod.Settings.DispatcherActive ? "Dispatcher switched on: it now holds trains." : "Dispatcher switched off: it only plans, trains run as without the mod.");
            m_NextRead = default;
        }

        protected override void OnUpdate()
        {
            try
            {
                base.OnUpdate();
                if (DateTime.UtcNow < m_NextRead)
                    return;
                m_NextRead = DateTime.UtcNow + kReadInterval;
                Dispatch.DispatchSystem dispatch = DispatchSystem;
                IReadOnlyDictionary<Entity, Dispatch.DispatchState> states = dispatch != null ? dispatch.States : new Dictionary<Entity, Dispatch.DispatchState>();
                m_Rows = m_Reader.Read(m_SimulationSystem.frameIndex, states, dispatch != null && dispatch.Active);
                m_Rows.Sort(Order);
                if (!m_PanelOpen)
                    return;
                m_TrainsBinding.Update();
                if (m_SentDespawns != DespawnHistory.Version)
                {
                    m_SentDespawns = DespawnHistory.Version;
                    m_DespawnsBinding.Update();
                }
            }
            catch (Exception e)
            {
                Mod.Log.Critical(e, "The train panel failed and was switched off until the game is restarted.");
                Enabled = false;
            }
        }

        /// <summary>
        /// In-game minutes a train has stood, as of the last reading. For
        /// DespawnWatchSystem, which sees a train only in the frame the game
        /// removes it.
        /// </summary>
        public float StandingMinutes(Entity train, uint frame)
        {
            return m_Reader.StandingMinutes(train, frame);
        }

        private void OnSetPanelOpen(bool open)
        {
            m_PanelOpen = open;
            // Send everything right away instead of up to a second later.
            m_NextRead = default;
            m_SentDespawns = -1;
        }

        private void OnDiagnose(int index, int version)
        {
            var train = new Entity { Index = index, Version = version };
            Dispatch.DispatchSystem dispatch = DispatchSystem;
            IReadOnlyDictionary<Entity, Dispatch.DispatchState> states = dispatch != null ? dispatch.States : new Dictionary<Entity, Dispatch.DispatchState>();
            TrainDiagnosis.Write(EntityManager, m_NameSystem, m_Reader, train, m_SimulationSystem.frameIndex, states, dispatch != null && dispatch.Active);
        }

        /// <summary>Moves the camera to where the game removed a train.</summary>
        private void OnShowDespawn(int id)
        {
            DespawnRecord record = DespawnHistory.Records.Find(r => r.Id == id);
            IGameCameraController camera = m_CameraSystem.activeCameraController;
            if (record == null || camera == null)
                return;
            // A followed train would pull the camera straight back.
            m_SelectedInfo.Focus(Entity.Null);
            camera.pivot = record.Position;
        }

        /// <summary>Selects the train in the game, which opens its info panel, and follows it with the camera.</summary>
        private void OnSelect(int index, int version)
        {
            var train = new Entity { Index = index, Version = version };
            if (!EntityManager.Exists(train))
                return;
            m_SelectedInfo.SetSelection(train);
            m_SelectedInfo.Focus(train);
        }

        /// <summary>
        /// Order of the list: deadlocks first, since the game is about to
        /// remove those trains; then trains standing on the line, longest
        /// first; then trains at a platform; then moving trains by line.
        /// </summary>
        private static int Order(TrainRow a, TrainRow b)
        {
            int rank = Rank(a).CompareTo(Rank(b));
            if (rank != 0)
                return rank;
            if (!a.Moving)
                return b.StandingMinutes.CompareTo(a.StandingMinutes);
            return a.Line.Index.CompareTo(b.Line.Index);
        }

        private static int Rank(TrainRow row)
        {
            switch (row.Reason)
            {
                case WaitReason.Deadlock:
                    return 0;
                case WaitReason.None:
                    return 3;
                case WaitReason.Boarding:
                case WaitReason.LatePassengers:
                    return 2;
                default:
                    return 1;
            }
        }

        // ---- Writing ----

        private void WriteTrains(IJsonWriter writer)
        {
            int moving = 0, standing = 0, atPlatform = 0, deadlocked = 0;
            foreach (TrainRow row in m_Rows)
            {
                switch (Rank(row))
                {
                    case 0:
                        deadlocked++;
                        break;
                    case 1:
                        standing++;
                        break;
                    case 2:
                        atPlatform++;
                        break;
                    default:
                        moving++;
                        break;
                }
            }

            writer.TypeBegin("smartTrains.Trains");
            writer.PropertyName("total");
            writer.Write(m_Rows.Count);
            writer.PropertyName("moving");
            writer.Write(moving);
            writer.PropertyName("standing");
            writer.Write(standing);
            writer.PropertyName("atPlatform");
            writer.Write(atPlatform);
            writer.PropertyName("deadlocked");
            writer.Write(deadlocked);
            int holding = 0;
            foreach (TrainRow row in m_Rows)
            {
                if (row.Dispatch != null && row.Dispatch.Holding)
                    holding++;
            }
            writer.PropertyName("dispatcherActive");
            writer.Write(DispatchSystem != null && DispatchSystem.Active);
            writer.PropertyName("holding");
            writer.Write(holding);
            int count = Math.Min(m_Rows.Count, kMaxRows);
            writer.PropertyName("rows");
            writer.ArrayBegin((uint)count);
            for (int i = 0; i < count; i++)
                WriteRow(writer, m_Rows[i]);
            writer.ArrayEnd();
            writer.TypeEnd();
        }

        private void WriteRow(IJsonWriter writer, TrainRow row)
        {
            writer.TypeBegin("smartTrains.Train");
            writer.PropertyName("index");
            writer.Write(row.Train.Index);
            writer.PropertyName("version");
            writer.Write(row.Train.Version);
            writer.PropertyName("model");
            WriteName(writer, row.Train);
            writer.PropertyName("cargo");
            writer.Write(row.Cargo);
            writer.PropertyName("through");
            writer.Write(row.Through);
            writer.PropertyName("returning");
            writer.Write(row.Returning);
            writer.PropertyName("line");
            WriteName(writer, row.Line);
            writer.PropertyName("lineColor");
            writer.Write(row.LineColor ?? "");
            writer.PropertyName("moving");
            writer.Write(row.Moving);
            writer.PropertyName("reason");
            writer.Write((int)row.Reason);
            writer.PropertyName("minutes");
            writer.Write(row.StandingMinutes);
            writer.PropertyName("blocker");
            WriteTrainRef(writer, row.Blocker);
            writer.PropertyName("passengers");
            writer.Write(row.Passengers);
            writer.PropertyName("passengerCapacity");
            writer.Write(row.PassengerCapacity);
            writer.PropertyName("load");
            writer.Write(row.Load);
            writer.PropertyName("loadCapacity");
            writer.Write(row.LoadCapacity);
            writer.PropertyName("resource");
            writer.Write(row.MainResource ?? "");
            writer.PropertyName("from");
            WriteName(writer, row.From);
            writer.PropertyName("to");
            WriteName(writer, row.To);
            writer.PropertyName("dispatch");
            WriteDispatch(writer, row.Dispatch);
            writer.TypeEnd();
        }

        /// <summary>
        /// The dispatcher's decision on a train: its rank, and if it holds the
        /// train, why and for whom. Written whether the dispatcher is active
        /// or only plans, so the panel can show what it would do.
        /// </summary>
        private void WriteDispatch(IJsonWriter writer, Dispatch.DispatchState state)
        {
            if (state == null)
            {
                writer.WriteNull();
                return;
            }
            writer.TypeBegin("smartTrains.Dispatch");
            writer.PropertyName("rank");
            writer.Write(state.Order.Rank);
            writer.PropertyName("holding");
            writer.Write(state.Holding);
            writer.PropertyName("reason");
            writer.Write((int)state.Order.Reason);
            writer.PropertyName("waitingFor");
            WriteTrainRef(writer, state.WaitingFor);
            writer.TypeEnd();
        }

        private void WriteTrainRef(IJsonWriter writer, Entity train)
        {
            if (train == Entity.Null || !EntityManager.Exists(train))
            {
                writer.WriteNull();
                return;
            }
            writer.TypeBegin("smartTrains.TrainRef");
            writer.PropertyName("index");
            writer.Write(train.Index);
            writer.PropertyName("version");
            writer.Write(train.Version);
            writer.PropertyName("line");
            WriteName(writer, EntityManager.TryGetComponent(train, out Game.Routes.CurrentRoute route) ? route.m_Route : Entity.Null);
            writer.TypeEnd();
        }

        /// <summary>
        /// Writes an entity's name in the game's own name format, which the
        /// panel renders with LocalizedEntityName. That way a line, station
        /// or train reads exactly as in the game's panels, custom names
        /// included. Null for no entity.
        /// </summary>
        private void WriteName(IJsonWriter writer, Entity entity)
        {
            if (entity == Entity.Null || !EntityManager.Exists(entity))
                writer.WriteNull();
            else
                m_NameSystem.BindName(writer, entity);
        }

        /// <summary>
        /// The removals since the city was loaded. Names are plain text here,
        /// taken when the train was removed, since train and line may be gone
        /// by now.
        /// </summary>
        private void WriteDespawns(IJsonWriter writer)
        {
            writer.TypeBegin("smartTrains.Despawns");
            writer.PropertyName("arrived");
            writer.Write(DespawnHistory.Arrived);
            writer.PropertyName("depot");
            writer.Write(DespawnHistory.Depot);
            writer.PropertyName("records");
            writer.ArrayBegin((uint)DespawnHistory.Records.Count);
            foreach (DespawnRecord r in DespawnHistory.Records)
            {
                writer.TypeBegin("smartTrains.Despawn");
                writer.PropertyName("id");
                writer.Write(r.Id);
                writer.PropertyName("time");
                writer.Write(r.Time);
                writer.PropertyName("cause");
                writer.Write((int)r.Cause);
                writer.PropertyName("train");
                writer.Write(r.Train);
                writer.PropertyName("line");
                writer.Write(r.Line);
                writer.PropertyName("cargo");
                writer.Write(r.Cargo);
                writer.PropertyName("through");
                writer.Write(r.Through);
                writer.PropertyName("returning");
                writer.Write(r.Returning);
                writer.PropertyName("minutes");
                writer.Write(r.StoodMinutes);
                writer.PropertyName("passengers");
                writer.Write(r.Passengers);
                writer.PropertyName("loadPercent");
                writer.Write(r.LoadPercent);
                writer.PropertyName("resource");
                writer.Write(r.Resource);
                writer.PropertyName("chain");
                writer.ArrayBegin((uint)r.Chain.Count);
                foreach ((int train, string line) in r.Chain)
                {
                    writer.TypeBegin("smartTrains.ChainLink");
                    writer.PropertyName("train");
                    writer.Write(train);
                    writer.PropertyName("line");
                    writer.Write(line);
                    writer.TypeEnd();
                }
                writer.ArrayEnd();
                writer.PropertyName("backToStart");
                writer.Write(r.ChainBackToStart);
                writer.TypeEnd();
            }
            writer.ArrayEnd();
            writer.TypeEnd();
        }
    }
}
