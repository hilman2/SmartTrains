using System.Collections.Generic;
using Colossal.Entities;
using Game.Net;
using Game.Pathfind;
using Game.Vehicles;
using SmartTrains.Core.Network;
using Unity.Entities;
using Unity.Mathematics;

namespace SmartTrains.Dispatch
{
    /// <summary>Where a lane of a train's route is stored in the game.</summary>
    internal enum RouteSource
    {
        /// <summary>The lane under the front bogie (TrainCurrentLane.m_Front).</summary>
        Front,

        /// <summary>An entry of the TrainNavigationLane buffer: taken over from the path, maybe reserved.</summary>
        Navigation,

        /// <summary>An entry of the PathElement buffer the train has not taken over yet.</summary>
        Path,
    }

    /// <summary>One lane of a route and where it is stored.</summary>
    internal struct RouteEntry
    {
        public Entity Lane;
        public RouteSource Source;

        /// <summary>Index into the buffer named by <see cref="Source"/>.</summary>
        public int Index;
    }

    /// <summary>A train's route as the dispatcher sees it, with the way back to the game's buffers.</summary>
    internal sealed class TrainRoute
    {
        public readonly List<Move> Moves = new List<Move>();

        /// <summary>Parallel to <see cref="Moves"/>.</summary>
        public readonly List<RouteEntry> Entries = new List<RouteEntry>();

        public readonly List<Move> Occupied = new List<Move>();
        public int Committed;
        public float FrontRemaining;
    }

    /// <summary>
    /// Reads a train's route from the game: the lane under its front, the
    /// navigation lanes it has taken over from its path, then the path
    /// elements it has not taken over yet.
    ///
    /// The route ends at the first lane that is not train track in the
    /// network, e.g. the connection into an outside connection or a depot.
    /// The dispatcher cannot reason about what lies beyond.
    /// </summary>
    internal static class RouteReader
    {
        public static TrainRoute Read(EntityManager em, TrackNetwork network, Entity train)
        {
            var route = new TrainRoute();
            TrainCurrentLane current = em.GetComponentData<TrainCurrentLane>(train);
            float4 front = current.m_Front.m_CurvePosition;
            if (!TryAdd(route, network, current.m_Front.m_Lane, front.w >= front.x, RouteSource.Front, 0))
                return route;
            if (em.TryGetComponent(current.m_Front.m_Lane, out Curve frontCurve))
                route.FrontRemaining = frontCurve.m_Length * math.abs(front.w - front.y);
            // A route ends where the train reverses, as it does for the game,
            // which reserves nothing beyond that point until the train has
            // turned. To the dispatcher, the reversing point is where the
            // train is going; once it has turned, the way back is a new route.
            if ((current.m_Front.m_LaneFlags & TrainLaneFlags.Return) != 0)
                return Finish(em, network, train, route);

            bool reserving = true;
            DynamicBuffer<TrainNavigationLane> lanes = em.GetBuffer<TrainNavigationLane>(train, true);
            for (int i = 0; i < lanes.Length; i++)
            {
                TrainNavigationLane lane = lanes[i];
                bool known = SameAsLast(route, lane.m_Lane)
                    || TryAdd(route, network, lane.m_Lane, lane.m_CurvePosition.y >= lane.m_CurvePosition.x, RouteSource.Navigation, i);
                if (!known)
                    return Finish(em, network, train, route);
                // The game reserves navigation lanes in order from the front;
                // the first one it has not reserved ends the committed part.
                reserving &= (lane.m_Flags & TrainLaneFlags.Reserved) != 0;
                if (reserving)
                    route.Committed = route.Moves.Count - 1;
                if ((lane.m_Flags & TrainLaneFlags.Return) != 0)
                    return Finish(em, network, train, route);
            }

            PathOwner owner = em.GetComponentData<PathOwner>(train);
            DynamicBuffer<PathElement> path = em.GetBuffer<PathElement>(train, true);
            for (int i = owner.m_ElementIndex; i < path.Length; i++)
            {
                PathElement element = path[i];
                bool known = SameAsLast(route, element.m_Target)
                    || TryAdd(route, network, element.m_Target, element.m_TargetDelta.y >= element.m_TargetDelta.x, RouteSource.Path, i);
                if (!known || (element.m_Flags & PathElementFlags.Return) != 0)
                    break;
            }
            return Finish(em, network, train, route);
        }

        private static bool SameAsLast(TrainRoute route, Entity lane)
        {
            return route.Entries.Count > 0 && route.Entries[route.Entries.Count - 1].Lane == lane;
        }

        private static bool TryAdd(TrainRoute route, TrackNetwork network, Entity lane, bool forward, RouteSource source, int index)
        {
            int indexInNetwork = network.IndexOf(EntityKey.Of(lane));
            if (indexInNetwork < 0)
                return false;
            route.Moves.Add(new Move(indexInNetwork, forward));
            route.Entries.Add(new RouteEntry { Lane = lane, Source = source, Index = index });
            return true;
        }

        /// <summary>Adds the lanes under every car, each the way the car runs it.</summary>
        private static TrainRoute Finish(EntityManager em, TrackNetwork network, Entity train, TrainRoute route)
        {
            var seen = new HashSet<int>();
            DynamicBuffer<LayoutElement> layout = em.GetBuffer<LayoutElement>(train, true);
            for (int i = 0; i < layout.Length; i++)
            {
                if (!em.TryGetComponent(layout[i].m_Vehicle, out TrainCurrentLane car))
                    continue;
                AddOccupied(route, network, seen, car.m_Front.m_Lane, car.m_Front.m_CurvePosition.x, car.m_Front.m_CurvePosition.w);
                AddOccupied(route, network, seen, car.m_Rear.m_Lane, car.m_Rear.m_CurvePosition.x, car.m_Rear.m_CurvePosition.w);
                AddOccupied(route, network, seen, car.m_FrontCache.m_Lane, car.m_FrontCache.m_CurvePosition.x, car.m_FrontCache.m_CurvePosition.y);
                AddOccupied(route, network, seen, car.m_RearCache.m_Lane, car.m_RearCache.m_CurvePosition.x, car.m_RearCache.m_CurvePosition.y);
            }
            return route;
        }

        /// <summary>
        /// Adds one lane a car is on. The car runs it the way its curve
        /// positions go; where they do not tell, e.g. a bogie that has just
        /// entered at the lane's end, the way the route runs the lane.
        /// </summary>
        private static void AddOccupied(TrainRoute route, TrackNetwork network, HashSet<int> seen, Entity lane, float from, float to)
        {
            int index = network.IndexOf(EntityKey.Of(lane));
            if (index < 0 || !seen.Add(index))
                return;
            bool forward = to >= from;
            if (from == to)
            {
                foreach (Move move in route.Moves)
                {
                    if (move.Lane == index)
                        forward = move.Forward;
                }
            }
            route.Occupied.Add(new Move(index, forward));
        }
    }
}
