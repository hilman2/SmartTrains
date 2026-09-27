using System.Collections.Generic;
using System.Text;
using Colossal.Entities;
using Game.Common;
using Game.Net;
using Game.Pathfind;
using Game.UI;
using Game.Vehicles;
using SmartTrains.Core.Monitor;
using Unity.Entities;
using Unity.Mathematics;

namespace SmartTrains.Monitor
{
    /// <summary>
    /// Writes everything that explains why a train stands to the log: the
    /// train, the chain of trains it waits for, and for each of them the
    /// track ahead with its reservations. Meant for finding out where a
    /// jam starts, from the log alone.
    /// </summary>
    internal static class TrainDiagnosis
    {
        /// <summary>Navigation lanes listed for the train the player asked about.</summary>
        private const int kLanesFirst = 12;

        /// <summary>Navigation lanes listed for each train further along the chain.</summary>
        private const int kLanesChain = 6;

        /// <param name="dispatch">The dispatcher's last decision per train.</param>
        /// <param name="dispatcherActive">Whether the dispatcher holds trains, or only plans.</param>
        public static void Write(EntityManager em, NameSystem names, TrainReader reader, Entity train, uint frame,
            IReadOnlyDictionary<Entity, Dispatch.DispatchState> dispatch, bool dispatcherActive)
        {
            if (!em.Exists(train))
                return;
            // A train the dispatcher holds reports nothing in its way; the
            // chain follows the train the dispatcher holds it for instead.
            BlockerChain chain = BlockerChains.Follow(EntityKey.Of(train), key =>
            {
                Entity t = EntityKey.ToEntity(key);
                if (dispatcherActive && dispatch.TryGetValue(t, out Dispatch.DispatchState s) && s.HoldLane != Entity.Null && s.WaitingFor != Entity.Null)
                    return EntityKey.Of(s.WaitingFor);
                return BlockerOf(em, key);
            });

            var text = new StringBuilder();
            text.Append($"Diagnosis of train #{train.Index}, waiting chain of {chain.Trains.Count} train(s)");
            text.Append(chain.BackToStart ? ", leading back to it (deadlock)." : ".");
            text.Append(dispatcherActive ? " Dispatcher on." : " Dispatcher off, only planning.");
            text.AppendLine();
            Describe(text, em, names, reader, train, frame, kLanesFirst, dispatch);
            foreach (long key in chain.Trains)
            {
                text.AppendLine("  waits for:");
                Describe(text, em, names, reader, EntityKey.ToEntity(key), frame, kLanesChain, dispatch);
            }
            Mod.Log.Info(text.ToString());
        }

        /// <summary>The key of the train the given train waits for, or 0; the signature <see cref="BlockerChains.Follow"/> asks for.</summary>
        public static long BlockerOf(EntityManager em, long key)
        {
            Entity train = EntityKey.ToEntity(key);
            if (!em.Exists(train) || !em.TryGetComponent(train, out Blocker blocker))
                return 0;
            Entity other = TrainReader.TrainOf(em, blocker.m_Blocker);
            return other == Entity.Null ? 0 : EntityKey.Of(other);
        }

        private static void Describe(StringBuilder text, EntityManager em, NameSystem names, TrainReader reader, Entity train, uint frame, int laneCount,
            IReadOnlyDictionary<Entity, Dispatch.DispatchState> dispatch)
        {
            if (!em.Exists(train))
            {
                text.AppendLine($"  #{train.Index}: no longer exists");
                return;
            }
            string line = em.TryGetComponent(train, out Game.Routes.CurrentRoute route) ? NameText.Of(names, em, route.m_Route) : "no line";
            float3 position = em.TryGetComponent(train, out Game.Objects.Transform transform) ? transform.m_Position : default;
            float speed = em.TryGetComponent(train, out Game.Objects.Moving moving) ? math.length(moving.m_Velocity) : 0f;
            text.AppendLine($"  #{train.Index} ({line}) at ({position.x:0}, {position.y:0}, {position.z:0}), speed {speed:0.0} m/s, standing {reader.StandingMinutes(train, frame):0} min");

            if (em.TryGetComponent(train, out Blocker blocker))
            {
                Entity other = TrainReader.TrainOf(em, blocker.m_Blocker);
                string who = blocker.m_Blocker == Entity.Null ? "nothing" : other != Entity.Null ? $"car #{blocker.m_Blocker.Index} of train #{other.Index}" : $"entity #{blocker.m_Blocker.Index} (not a train)";
                text.AppendLine($"    blocker: {blocker.m_Type} by {who}, max speed {blocker.m_MaxSpeed}");
            }
            if (em.TryGetComponent(train, out PathOwner pathOwner))
            {
                int length = em.TryGetBuffer(train, true, out DynamicBuffer<PathElement> path) ? path.Length : 0;
                Entity target = em.TryGetComponent(train, out Target t) ? t.m_Target : Entity.Null;
                text.AppendLine($"    path: {pathOwner.m_State}, element {pathOwner.m_ElementIndex} of {length}, target #{target.Index} ({NameText.Of(names, em, target)})");
            }
            if (dispatch.TryGetValue(train, out Dispatch.DispatchState state))
                text.AppendLine("    " + DescribeDispatch(em, state));
            if (em.TryGetComponent(train, out Game.Vehicles.PublicTransport passenger))
                text.AppendLine($"    passenger service: {passenger.m_State}, departure frame {passenger.m_DepartureFrame} (now {frame})");
            if (em.TryGetComponent(train, out Game.Vehicles.CargoTransport cargo))
                text.AppendLine($"    cargo service: {cargo.m_State}, departure frame {cargo.m_DepartureFrame} (now {frame})");

            if (em.TryGetComponent(train, out TrainCurrentLane current))
            {
                text.AppendLine($"    front on {Lane(em, current.m_Front.m_Lane)} at {current.m_Front.m_CurvePosition.y:0.00}, flags {current.m_Front.m_LaneFlags}");
                text.AppendLine($"    rear on  {Lane(em, current.m_Rear.m_Lane)} at {current.m_Rear.m_CurvePosition.y:0.00}");
            }
            if (em.TryGetBuffer(train, true, out DynamicBuffer<TrainNavigationLane> lanes))
            {
                text.AppendLine($"    track ahead ({lanes.Length} lane(s) planned):");
                for (int i = 0; i < lanes.Length && i < laneCount; i++)
                    text.AppendLine($"      {i + 1}. {Lane(em, lanes[i].m_Lane)}, flags {lanes[i].m_Flags}");
            }
        }

        /// <summary>The dispatcher's decision on a train in one line: rank, where it holds it, and what it found not free.</summary>
        private static string DescribeDispatch(EntityManager em, Dispatch.DispatchState state)
        {
            var text = new StringBuilder($"dispatcher: rank {state.Order.Rank:0}, granted up to route lane {state.Order.GrantedEnd} of {state.RouteLength}");
            if (state.Released)
                text.Append(", would hold it but lets it go: the hold closes a waiting circle");
            else if (state.HoldLane == Entity.Null)
                text.Append(", lets it run");
            else
                text.Append($", holds it before lane #{state.HoldLane.Index} (route lane {state.Order.HoldAt})");
            if (state.Order.Reason != Core.Dispatch.HoldReason.None)
            {
                string by = state.WaitingFor != Entity.Null ? $"train #{state.WaitingFor.Index}" : "nobody known";
                string where = state.BlockedLane != Entity.Null ? $" on {Lane(em, state.BlockedLane)} (route lane {state.Order.BlockedAt})" : "";
                text.Append($"; {state.Order.Reason} by {by}{where}");
            }
            return text.ToString();
        }

        /// <summary>
        /// One lane in a line: its track flags, who holds a reservation on it
        /// and which vehicles are on it. The reservation is what makes other
        /// trains stop in front of it (TrainNavigationSystem.CanReserveLane).
        /// </summary>
        private static string Lane(EntityManager em, Entity lane)
        {
            if (lane == Entity.Null)
                return "no lane";
            if (!em.Exists(lane))
                return $"lane #{lane.Index} (gone)";
            var text = new StringBuilder($"lane #{lane.Index}");
            if (em.TryGetComponent(lane, out Owner owner))
                text.Append($" of #{owner.m_Owner.Index}");
            if (em.TryGetComponent(lane, out TrackLane track))
                text.Append($" [{track.m_Flags}]");
            if (em.TryGetComponent(lane, out LaneReservation reservation) && reservation.GetPriority() != 0)
            {
                Entity holder = TrainReader.TrainOf(em, reservation.m_Blocker);
                string by = holder != Entity.Null ? $"train #{holder.Index}" : $"#{reservation.m_Blocker.Index}";
                text.Append($" reserved {reservation.m_Prev.m_Priority}/{reservation.m_Next.m_Priority} by {by}");
            }
            if (em.TryGetBuffer(lane, true, out DynamicBuffer<LaneObject> objects) && objects.Length > 0)
            {
                text.Append(" occupied by");
                for (int i = 0; i < objects.Length && i < 4; i++)
                {
                    Entity other = TrainReader.TrainOf(em, objects[i].m_LaneObject);
                    text.Append(other != Entity.Null ? $" train #{other.Index}" : $" #{objects[i].m_LaneObject.Index}");
                }
                if (objects.Length > 4)
                    text.Append($" and {objects.Length - 4} more");
            }
            return text.ToString();
        }
    }
}
