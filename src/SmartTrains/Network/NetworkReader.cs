using System.Collections.Generic;
using Colossal.Entities;
using Colossal.Mathematics;
using Game.Common;
using Game.Net;
using Game.Pathfind;
using Game.Prefabs;
using Game.Tools;
using SmartTrains.Core.Network;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Entities;
using Unity.Mathematics;

namespace SmartTrains.Network
{
    /// <summary>
    /// Reads the train track lanes of the city into the core's
    /// <see cref="TrackNetwork"/>. Tram and subway track are left out.
    /// </summary>
    internal sealed class NetworkReader
    {
        private readonly EntityManager m_EntityManager;
        private readonly EntityQuery m_LaneQuery;

        public NetworkReader(EntityManager entityManager, EntityQuery laneQuery)
        {
            m_EntityManager = entityManager;
            m_LaneQuery = laneQuery;
        }

        public static EntityQueryDesc QueryDesc()
        {
            return new EntityQueryDesc
            {
                All = new[]
                {
                    ComponentType.ReadOnly<Lane>(),
                    ComponentType.ReadOnly<Game.Net.TrackLane>(),
                    ComponentType.ReadOnly<Curve>(),
                    ComponentType.ReadOnly<PrefabRef>(),
                },
                None = new[] { ComponentType.ReadOnly<Deleted>(), ComponentType.ReadOnly<Temp>() },
            };
        }

        /// <summary>
        /// A path node's key. The game keeps it private; PathNode is a
        /// struct around a single 64-bit key, compared by that key alone
        /// (PathNode.Equals), so its bits are the key.
        /// </summary>
        private static long NodeKey(PathNode node)
        {
            return UnsafeUtility.As<PathNode, long>(ref node);
        }

        public TrackNetwork Read()
        {
            EntityManager em = m_EntityManager;
            var lanes = new List<LaneInput>();
            var overlaps = new List<(long, long)>();
            using (NativeArray<Entity> entities = m_LaneQuery.ToEntityArray(Allocator.Temp))
            {
                foreach (Entity entity in entities)
                {
                    Entity prefab = em.GetComponentData<PrefabRef>(entity).m_Prefab;
                    if (!em.TryGetComponent(prefab, out TrackLaneData data) || (data.m_TrackTypes & TrackTypes.Train) == 0)
                        continue;
                    Lane lane = em.GetComponentData<Lane>(entity);
                    Game.Net.TrackLane track = em.GetComponentData<Game.Net.TrackLane>(entity);
                    Curve curve = em.GetComponentData<Curve>(entity);
                    float3 start = MathUtils.Tangent(curve.m_Bezier, 0f);
                    float3 end = MathUtils.Tangent(curve.m_Bezier, 1f);
                    long id = EntityKey.Of(entity);
                    lanes.Add(new LaneInput
                    {
                        Id = id,
                        StartNode = NodeKey(lane.m_StartNode),
                        EndNode = NodeKey(lane.m_EndNode),
                        Length = curve.m_Length,
                        TwoWay = (track.m_Flags & TrackLaneFlags.Twoway) != 0,
                        Kind = KindOf(track.m_Flags),
                        Station = (track.m_Flags & TrackLaneFlags.Station) != 0 ? StationOf(entity) : 0,
                        StartHeading = new Heading(start.x, start.z),
                        EndHeading = new Heading(end.x, end.z),
                    });
                    if (em.TryGetBuffer(entity, true, out DynamicBuffer<LaneOverlap> laneOverlaps))
                    {
                        for (int i = 0; i < laneOverlaps.Length; i++)
                            overlaps.Add((id, EntityKey.Of(laneOverlaps[i].m_Other)));
                    }
                }
            }
            return new TrackNetwork(lanes, overlaps);
        }

        private static LaneKind KindOf(TrackLaneFlags flags)
        {
            if ((flags & (TrackLaneFlags.Switch | TrackLaneFlags.DoubleSwitch)) != 0)
                return LaneKind.Switch;
            if ((flags & TrackLaneFlags.DiamondCrossing) != 0)
                return LaneKind.Crossing;
            return LaneKind.Plain;
        }

        /// <summary>
        /// The station a platform lane belongs to: the first building up its
        /// chain of owners (lane, track piece, building). Falls back to the
        /// track piece if there is no building, e.g. for a platform drawn
        /// with a track upgrade, so that its lanes still group together.
        /// </summary>
        private long StationOf(Entity lane)
        {
            EntityManager em = m_EntityManager;
            Entity entity = lane;
            Entity piece = Entity.Null;
            for (int depth = 0; depth < 4 && em.TryGetComponent(entity, out Owner owner); depth++)
            {
                entity = owner.m_Owner;
                if (piece == Entity.Null)
                    piece = entity;
                if (em.HasComponent<Game.Buildings.Building>(entity))
                    return EntityKey.Of(entity);
            }
            return piece == Entity.Null ? 0 : EntityKey.Of(piece);
        }
    }
}
