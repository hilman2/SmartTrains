using Colossal.Entities;
using Game.Net;
using Game.Pathfind;
using Game.Vehicles;
using Unity.Entities;

namespace SmartTrains.Dispatch
{
    /// <summary>The lane a train is held in front of, and what was changed on it.</summary>
    internal struct HoldMark
    {
        public Entity Lane;

        /// <summary>KeepClear was added by the mark and goes again with it.</summary>
        public bool AddedKeepClear;
    }

    /// <summary>
    /// Holds a single train in front of a lane, through the train's own
    /// navigation lanes, so that no other train is affected.
    ///
    /// The lane gets the ParkingSpace flag. TrainNavigationSystem stops
    /// reserving at a navigation lane with that flag (TryReserveNavigationLanes)
    /// and a train never runs onto a lane it has not reserved; it brakes in
    /// time to stop in front of it, as at the end of its reserved track. The
    /// game sets the flag itself only on the last navigation lane, where a
    /// train parks; TransportTrainAISystem acts on it only there, and only
    /// once the train has reached the end of its path.
    ///
    /// The mark must therefore never be on the last navigation lane:
    /// VehicleUtils.ClearEndOfPath drops trailing ParkingSpace lanes when the
    /// game appends the next leg of a route. The mark adds KeepClear, which
    /// makes the game keep taking over lanes past it (FillNavigationPaths),
    /// and <see cref="Apply"/> takes over one more lane when needed.
    /// </summary>
    internal static class HoldActuator
    {
        private const PathFlags kRouteInFlux = PathFlags.Pending | PathFlags.Failed | PathFlags.Obsolete | PathFlags.Updated;

        /// <summary>Whether the game is about to replace the train's route; its navigation lanes then mean nothing.</summary>
        public static bool RouteInFlux(EntityManager em, Entity train)
        {
            return (em.GetComponentData<PathOwner>(train).m_State & kRouteInFlux) != 0;
        }

        /// <summary>
        /// Marks route entry <paramref name="holdAt"/>, taking lanes over from
        /// the path first if it is still there.
        /// </summary>
        /// <returns>The mark, or a mark with Null lane if the lane cannot be marked, e.g. because the train has reserved it.</returns>
        public static HoldMark Apply(EntityManager em, Entity train, TrainRoute route, int holdAt)
        {
            RouteEntry entry = route.Entries[holdAt];
            DynamicBuffer<TrainNavigationLane> lanes = em.GetBuffer<TrainNavigationLane>(train);
            int index = entry.Index;
            if (entry.Source == RouteSource.Path)
            {
                index = TakeOver(em, train, lanes, entry.Index);
                if (index < 0)
                    return default;
            }
            else if (entry.Source != RouteSource.Navigation)
            {
                return default;
            }
            if (index == lanes.Length - 1)
                TakeOver(em, train, lanes, em.GetComponentData<PathOwner>(train).m_ElementIndex);

            TrainNavigationLane lane = lanes[index];
            if (lane.m_Lane != entry.Lane || (lane.m_Flags & (TrainLaneFlags.Reserved | TrainLaneFlags.TryReserve)) != 0)
                return default;
            var mark = new HoldMark { Lane = lane.m_Lane, AddedKeepClear = (lane.m_Flags & TrainLaneFlags.KeepClear) == 0 };
            lane.m_Flags |= TrainLaneFlags.ParkingSpace | TrainLaneFlags.KeepClear;
            lanes[index] = lane;
            return mark;
        }

        /// <summary>Takes the mark off again, if the lane is still among the train's navigation lanes.</summary>
        public static void Clear(EntityManager em, Entity train, HoldMark mark)
        {
            if (mark.Lane == Entity.Null || !em.Exists(train) || !em.HasBuffer<TrainNavigationLane>(train))
                return;
            DynamicBuffer<TrainNavigationLane> lanes = em.GetBuffer<TrainNavigationLane>(train);
            for (int i = 0; i < lanes.Length; i++)
            {
                TrainNavigationLane lane = lanes[i];
                if (lane.m_Lane != mark.Lane || (lane.m_Flags & TrainLaneFlags.ParkingSpace) == 0)
                    continue;
                lane.m_Flags &= ~TrainLaneFlags.ParkingSpace;
                if (mark.AddedKeepClear)
                    lane.m_Flags &= ~TrainLaneFlags.KeepClear;
                lanes[i] = lane;
                return;
            }
        }

        /// <summary>
        /// Takes path elements over into navigation lanes up to and including
        /// <paramref name="upTo"/>, the way TrainNavigationSystem.FillNavigationPaths
        /// does for track lanes. Stops at the first element that is not a
        /// track lane and leaves the rest to the game.
        /// </summary>
        /// <returns>The navigation lane index of element <paramref name="upTo"/>, -1 if it was not reached.</returns>
        private static int TakeOver(EntityManager em, Entity train, DynamicBuffer<TrainNavigationLane> lanes, int upTo)
        {
            PathOwner owner = em.GetComponentData<PathOwner>(train);
            DynamicBuffer<PathElement> path = em.GetBuffer<PathElement>(train);
            int result = -1;
            while (owner.m_ElementIndex <= upTo && owner.m_ElementIndex < path.Length)
            {
                PathElement element = path[owner.m_ElementIndex];
                if (!em.TryGetComponent(element.m_Target, out TrackLane track))
                    break;
                // The game does not take over the last element while it
                // waits for the next leg of the route, which is appended there.
                if (owner.m_ElementIndex + 1 >= path.Length && (owner.m_State & PathFlags.Pending) != 0)
                    break;
                owner.m_ElementIndex++;
                var lane = new TrainNavigationLane { m_Lane = element.m_Target, m_CurvePosition = element.m_TargetDelta };
                if (owner.m_ElementIndex >= path.Length)
                {
                    lane.m_Flags |= TrainLaneFlags.EndOfPath;
                }
                else
                {
                    if ((element.m_Flags & PathElementFlags.Return) != 0)
                        lane.m_Flags |= TrainLaneFlags.Return;
                    bool keepClear = (track.m_Flags & (TrackLaneFlags.Twoway | TrackLaneFlags.Switch | TrackLaneFlags.DiamondCrossing | TrackLaneFlags.CrossingTraffic)) != 0
                        && (track.m_Flags & TrackLaneFlags.MergingTraffic) == 0;
                    if (keepClear || (element.m_Flags & PathElementFlags.Reverse) != 0)
                        lane.m_Flags |= TrainLaneFlags.KeepClear;
                }
                if ((track.m_Flags & TrackLaneFlags.Exclusive) != 0)
                    lane.m_Flags |= TrainLaneFlags.Exclusive;
                if ((track.m_Flags & TrackLaneFlags.TurnLeft) != 0)
                    lane.m_Flags |= TrainLaneFlags.TurnLeft;
                if ((track.m_Flags & TrackLaneFlags.TurnRight) != 0)
                    lane.m_Flags |= TrainLaneFlags.TurnRight;
                lanes.Add(lane);
                if (owner.m_ElementIndex - 1 == upTo)
                    result = lanes.Length - 1;
            }
            em.SetComponentData(train, owner);
            return result;
        }
    }
}
