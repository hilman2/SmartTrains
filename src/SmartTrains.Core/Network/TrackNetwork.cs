using System;
using System.Collections.Generic;

namespace SmartTrains.Core.Network
{
    /// <summary>A horizontal direction, as a unit vector in the ground plane.</summary>
    public struct Heading
    {
        public float X;
        public float Z;

        public Heading(float x, float z)
        {
            float length = (float)Math.Sqrt(x * x + z * z);
            X = length > 0f ? x / length : 0f;
            Z = length > 0f ? z / length : 0f;
        }

        public Heading Reversed => new Heading { X = -X, Z = -Z };

        public float Dot(Heading other)
        {
            return X * other.X + Z * other.Z;
        }
    }

    public enum LaneKind
    {
        /// <summary>Plain track, including the short joints where two pieces of track meet.</summary>
        Plain,

        /// <summary>A way through a turnout: straight or diverging, both count.</summary>
        Switch,

        /// <summary>A way over a diamond crossing.</summary>
        Crossing,
    }

    /// <summary>One track lane, as the game layer reads it.</summary>
    public struct LaneInput
    {
        /// <summary>The game layer's key of the lane; any value except 0.</summary>
        public long Id;

        /// <summary>
        /// Key of the point where the lane starts. Lanes that share a point
        /// are connected there; the game calls these points path nodes.
        /// </summary>
        public long StartNode;

        public long EndNode;
        public float Length;

        /// <summary>Trains may run it from end to start as well.</summary>
        public bool TwoWay;

        public LaneKind Kind;

        /// <summary>Key of the station the lane is a platform track of, 0 for none.</summary>
        public long Station;

        /// <summary>Direction of travel at the start, running forward (start to end).</summary>
        public Heading StartHeading;

        /// <summary>Direction of travel at the end, running forward.</summary>
        public Heading EndHeading;
    }

    /// <summary>A lane run in one direction.</summary>
    public struct Move : IEquatable<Move>
    {
        /// <summary>Index into <see cref="TrackNetwork.Lanes"/>.</summary>
        public int Lane;

        /// <summary>From start to end; false for a two-way lane run from end to start.</summary>
        public bool Forward;

        public Move(int lane, bool forward)
        {
            Lane = lane;
            Forward = forward;
        }

        public Move Reversed => new Move(Lane, !Forward);

        public bool Equals(Move other)
        {
            return Lane == other.Lane && Forward == other.Forward;
        }

        public override bool Equals(object obj)
        {
            return obj is Move other && Equals(other);
        }

        public override int GetHashCode()
        {
            return Lane * 2 + (Forward ? 1 : 0);
        }

        public override string ToString()
        {
            return $"{Lane}{(Forward ? "+" : "-")}";
        }
    }

    /// <summary>
    /// The track lanes of a city and how they connect, without any game type.
    ///
    /// A train can leave a lane only into a lane that continues its way: the
    /// two must share the point between them, and the second must not point
    /// back the way the first came. Without the second condition, the two
    /// branches of a turnout would count as connected at their common end,
    /// and a train could run in one branch and out the other, which only a
    /// reversing train does.
    /// </summary>
    public sealed class TrackNetwork
    {
        /// <summary>
        /// Least cosine of the angle between the way a train arrives and the
        /// way it leaves. Track bends gently even through a turnout; 0.5 allows
        /// 60 degrees, far more than any turnout, and far less than a reversal.
        /// </summary>
        private const float kMinContinuity = 0.5f;

        private readonly List<LaneInput> m_Lanes;
        private readonly Dictionary<long, int> m_LaneIndex = new Dictionary<long, int>();
        private readonly Dictionary<long, List<int>> m_NodeLanes = new Dictionary<long, List<int>>();
        private readonly List<List<int>> m_Overlaps;

        public IReadOnlyList<LaneInput> Lanes => m_Lanes;

        /// <param name="lanes">Every train track lane; lanes of other track types (tram, subway) left out.</param>
        /// <param name="overlaps">
        /// Pairs of lane keys whose tracks touch or cross, so that two trains
        /// cannot use both at once. Pairs naming a lane that is not in
        /// <paramref name="lanes"/> are ignored.
        /// </param>
        public TrackNetwork(IEnumerable<LaneInput> lanes, IEnumerable<(long A, long B)> overlaps)
        {
            m_Lanes = new List<LaneInput>(lanes);
            for (int i = 0; i < m_Lanes.Count; i++)
            {
                m_LaneIndex[m_Lanes[i].Id] = i;
                AddToNode(m_Lanes[i].StartNode, i);
                AddToNode(m_Lanes[i].EndNode, i);
            }
            m_Overlaps = new List<List<int>>(m_Lanes.Count);
            for (int i = 0; i < m_Lanes.Count; i++)
                m_Overlaps.Add(new List<int>());
            foreach ((long a, long b) in overlaps)
            {
                if (!m_LaneIndex.TryGetValue(a, out int ia) || !m_LaneIndex.TryGetValue(b, out int ib) || ia == ib)
                    continue;
                if (!m_Overlaps[ia].Contains(ib))
                    m_Overlaps[ia].Add(ib);
                if (!m_Overlaps[ib].Contains(ia))
                    m_Overlaps[ib].Add(ia);
            }
        }

        private void AddToNode(long node, int lane)
        {
            if (!m_NodeLanes.TryGetValue(node, out List<int> list))
            {
                list = new List<int>(3);
                m_NodeLanes[node] = list;
            }
            if (!list.Contains(lane))
                list.Add(lane);
        }

        /// <summary>
        /// A number that changes when any lane, its ends, direction or kind
        /// changes, but not with the order the lanes were read in; the game
        /// hands them over in no fixed order.
        /// </summary>
        public long Fingerprint()
        {
            long sum = m_Lanes.Count;
            long mix = 0;
            foreach (LaneInput lane in m_Lanes)
            {
                long h = lane.Id;
                h = h * 1000003 ^ lane.StartNode;
                h = h * 1000003 ^ lane.EndNode;
                h = h * 1000003 ^ (lane.TwoWay ? 1 : 0);
                h = h * 1000003 ^ (long)lane.Kind;
                h = h * 1000003 ^ lane.Station;
                // Two combinations, so that swapping values between lanes
                // does not cancel out as it would in a plain sum or xor.
                sum += h;
                mix ^= h * 0x5bd1e995;
            }
            return sum * 31 + mix;
        }

        /// <summary>The lane with the game layer's key, or -1.</summary>
        public int IndexOf(long laneId)
        {
            return m_LaneIndex.TryGetValue(laneId, out int index) ? index : -1;
        }

        /// <summary>Lanes whose tracks touch or cross the given one.</summary>
        public IReadOnlyList<int> Overlaps(int lane)
        {
            return m_Overlaps[lane];
        }

        /// <summary>Lanes that start or end at a point.</summary>
        public IReadOnlyList<int> LanesAt(long node)
        {
            return m_NodeLanes.TryGetValue(node, out List<int> list) ? list : (IReadOnlyList<int>)Array.Empty<int>();
        }

        public long From(Move move)
        {
            return move.Forward ? m_Lanes[move.Lane].StartNode : m_Lanes[move.Lane].EndNode;
        }

        public long To(Move move)
        {
            return move.Forward ? m_Lanes[move.Lane].EndNode : m_Lanes[move.Lane].StartNode;
        }

        /// <summary>Direction of travel where the move enters its lane.</summary>
        public Heading Entering(Move move)
        {
            return move.Forward ? m_Lanes[move.Lane].StartHeading : m_Lanes[move.Lane].EndHeading.Reversed;
        }

        /// <summary>Direction of travel where the move leaves its lane.</summary>
        public Heading Leaving(Move move)
        {
            return move.Forward ? m_Lanes[move.Lane].EndHeading : m_Lanes[move.Lane].StartHeading.Reversed;
        }

        /// <summary>Whether a train may run the lane this way.</summary>
        public bool CanRun(Move move)
        {
            return move.Forward || m_Lanes[move.Lane].TwoWay;
        }

        /// <summary>The moves a train can make after <paramref name="move"/> without reversing.</summary>
        public List<Move> Successors(Move move)
        {
            var result = new List<Move>(2);
            long node = To(move);
            Heading arriving = Leaving(move);
            foreach (int lane in LanesAt(node))
            {
                if (lane == move.Lane)
                    continue;
                LaneInput next = m_Lanes[lane];
                // A lane that starts and ends at the same point cannot occur on
                // real track; both directions are checked all the same.
                if (next.StartNode == node)
                    TryAdd(result, new Move(lane, true), arriving);
                if (next.EndNode == node && next.TwoWay)
                    TryAdd(result, new Move(lane, false), arriving);
            }
            return result;
        }

        private void TryAdd(List<Move> result, Move candidate, Heading arriving)
        {
            if (arriving.Dot(Entering(candidate)) >= kMinContinuity)
                result.Add(candidate);
        }

        /// <summary>The moves a train can have made just before <paramref name="move"/>, without reversing.</summary>
        public List<Move> Predecessors(Move move)
        {
            var result = new List<Move>(2);
            long node = From(move);
            Heading entering = Entering(move);
            foreach (int lane in LanesAt(node))
            {
                if (lane == move.Lane)
                    continue;
                LaneInput previous = m_Lanes[lane];
                if (previous.EndNode == node)
                    TryAddBefore(result, new Move(lane, true), entering);
                if (previous.StartNode == node && previous.TwoWay)
                    TryAddBefore(result, new Move(lane, false), entering);
            }
            return result;
        }

        private void TryAddBefore(List<Move> result, Move candidate, Heading entering)
        {
            if (Leaving(candidate).Dot(entering) >= kMinContinuity)
                result.Add(candidate);
        }
    }
}
