using System.Collections.Generic;

namespace SmartTrains.Core.Network
{
    /// <summary>A stretch of plain track, from one junction area to the next or to a track end.</summary>
    public sealed class Section
    {
        public int Id;

        /// <summary>The lanes in order, each run in the section's own direction.</summary>
        public List<Move> Moves = new List<Move>();

        /// <summary>Trains may run it in the section's direction.</summary>
        public bool CanRunForward;

        /// <summary>Trains may run it against the section's direction; true for single track.</summary>
        public bool CanRunBackward;

        public float Length;

        /// <summary>The station whose platform track this is, 0 for none.</summary>
        public long Station;

        /// <summary>Junction area at the section's start, -1 for a track end.</summary>
        public int StartArea = -1;

        /// <summary>Junction area at the section's end, -1 for a track end.</summary>
        public int EndArea = -1;

        public bool TwoWay => CanRunForward && CanRunBackward;
    }

    /// <summary>Turnouts and crossings that touch each other, to be passed as a whole.</summary>
    public sealed class JunctionArea
    {
        public int Id;
        public List<int> Lanes = new List<int>();
    }

    /// <summary>
    /// Sections between the same two junction areas: a passing loop, the
    /// tracks of a station, or a double track line.
    /// </summary>
    public sealed class ParallelGroup
    {
        public int Id;
        public int AreaA;
        public int AreaB;
        public List<int> Sections = new List<int>();

        /// <summary>The station if all its sections are platform tracks of the same one, else 0.</summary>
        public long Station;
    }

    public enum GroupKind
    {
        /// <summary>At least two sections a train can take in the same direction, outside a station.</summary>
        PassingLoop,

        /// <summary>Platform tracks of one station.</summary>
        Station,

        /// <summary>One section per direction: a train has no choice.</summary>
        DoubleTrack,
    }

    public struct LayoutSummary
    {
        public int Lanes;
        public int Sections;

        /// <summary>Two-way sections without a parallel: a train on it blocks the other direction.</summary>
        public int SingleTrackSections;

        public float SingleTrackLength;
        public int JunctionAreas;
        public int PassingLoops;
        public int StationGroups;
        public int DoubleTracks;

        public override string ToString()
        {
            return $"{Lanes} track lanes in {Sections} sections and {JunctionAreas} junction areas; "
                + $"{SingleTrackSections} single-track sections ({SingleTrackLength / 1000f:0.0} km), "
                + $"{PassingLoops} passing loops, {StationGroups} station track groups, {DoubleTracks} double-track stretches";
        }
    }

    /// <summary>
    /// The track network divided into what a dispatcher reasons about:
    /// sections where trains run and wait, junction areas they must never
    /// stop in, and parallel sections among which a train can choose.
    /// </summary>
    public sealed class TrackLayout
    {
        private readonly List<Section> m_Sections = new List<Section>();
        private readonly List<JunctionArea> m_Areas = new List<JunctionArea>();
        private readonly List<ParallelGroup> m_Groups = new List<ParallelGroup>();
        private readonly int[] m_SectionOfLane;
        private readonly int[] m_AreaOfLane;
        private readonly int[] m_GroupOfSection;

        public TrackNetwork Network { get; }
        public IReadOnlyList<Section> Sections => m_Sections;
        public IReadOnlyList<JunctionArea> Areas => m_Areas;
        public IReadOnlyList<ParallelGroup> Groups => m_Groups;

        /// <summary>The section a plain lane belongs to, -1 for a lane in a junction area.</summary>
        public int SectionOf(int lane)
        {
            return m_SectionOfLane[lane];
        }

        /// <summary>The junction area a turnout or crossing lane belongs to, -1 for plain track.</summary>
        public int AreaOf(int lane)
        {
            return m_AreaOfLane[lane];
        }

        /// <summary>The parallel group a section belongs to, -1 if it has no parallel.</summary>
        public int GroupOf(int section)
        {
            return m_GroupOfSection[section];
        }

        public TrackLayout(TrackNetwork network)
        {
            Network = network;
            int count = network.Lanes.Count;
            m_SectionOfLane = new int[count];
            m_AreaOfLane = new int[count];
            for (int i = 0; i < count; i++)
            {
                m_SectionOfLane[i] = -1;
                m_AreaOfLane[i] = -1;
            }
            BuildAreas();
            BuildSections();
            m_GroupOfSection = new int[m_Sections.Count];
            for (int i = 0; i < m_Sections.Count; i++)
                m_GroupOfSection[i] = -1;
            BuildGroups();
        }

        private bool IsJunction(int lane)
        {
            return Network.Lanes[lane].Kind != LaneKind.Plain;
        }

        // ---- Junction areas ----

        /// <summary>
        /// Joins turnout and crossing lanes into areas: lanes that share a
        /// point, or whose tracks overlap, belong to the same area. A train
        /// that enters an area must be able to leave it, since standing in it
        /// would block every way through.
        /// </summary>
        private void BuildAreas()
        {
            int count = Network.Lanes.Count;
            var parent = new int[count];
            for (int i = 0; i < count; i++)
                parent[i] = i;

            for (int lane = 0; lane < count; lane++)
            {
                if (!IsJunction(lane))
                    continue;
                LaneInput input = Network.Lanes[lane];
                foreach (long node in new[] { input.StartNode, input.EndNode })
                {
                    foreach (int other in Network.LanesAt(node))
                    {
                        if (IsJunction(other))
                            Union(parent, lane, other);
                    }
                }
                foreach (int other in Network.Overlaps(lane))
                {
                    if (IsJunction(other))
                        Union(parent, lane, other);
                }
            }

            var areaOfRoot = new Dictionary<int, int>();
            for (int lane = 0; lane < count; lane++)
            {
                if (!IsJunction(lane))
                    continue;
                int root = Find(parent, lane);
                if (!areaOfRoot.TryGetValue(root, out int area))
                {
                    area = m_Areas.Count;
                    areaOfRoot[root] = area;
                    m_Areas.Add(new JunctionArea { Id = area });
                }
                m_Areas[area].Lanes.Add(lane);
                m_AreaOfLane[lane] = area;
            }
        }

        private static int Find(int[] parent, int i)
        {
            while (parent[i] != i)
            {
                parent[i] = parent[parent[i]];
                i = parent[i];
            }
            return i;
        }

        private static void Union(int[] parent, int a, int b)
        {
            int ra = Find(parent, a);
            int rb = Find(parent, b);
            if (ra != rb)
                parent[ra] = rb;
        }

        // ---- Sections ----

        private void BuildSections()
        {
            for (int lane = 0; lane < Network.Lanes.Count; lane++)
            {
                if (IsJunction(lane) || m_SectionOfLane[lane] >= 0)
                    continue;
                var section = new Section { Id = m_Sections.Count };
                m_Sections.Add(section);

                var moves = new LinkedList<Move>();
                var start = new Move(lane, true);
                moves.AddFirst(start);
                m_SectionOfLane[lane] = section.Id;
                for (Move move = start; TryContinue(move, section.Id, out Move next); move = next)
                    moves.AddLast(next);
                for (Move move = start.Reversed; TryContinue(move, section.Id, out Move next); move = next)
                    moves.AddFirst(next.Reversed);
                section.Moves.AddRange(moves);

                FinishSection(section);
            }
        }

        /// <summary>
        /// The plain lane that continues <paramref name="move"/>, if the track
        /// goes on without a branch: the point after the move joins exactly
        /// two lanes, and the other one is plain and not yet in a section.
        /// The direction of the next lane is whatever continues the way, so a
        /// section also runs through lanes whose own direction is reversed.
        /// </summary>
        private bool TryContinue(Move move, int section, out Move next)
        {
            next = default;
            long node = Network.To(move);
            IReadOnlyList<int> lanes = Network.LanesAt(node);
            if (lanes.Count != 2)
                return false;
            int other = lanes[0] == move.Lane ? lanes[1] : lanes[0];
            if (IsJunction(other) || m_SectionOfLane[other] >= 0)
                return false;
            LaneInput input = Network.Lanes[other];
            next = new Move(other, input.StartNode == node);
            if (Network.Leaving(move).Dot(Network.Entering(next)) < 0.5f)
                return false;
            m_SectionOfLane[other] = section;
            return true;
        }

        private void FinishSection(Section section)
        {
            section.CanRunForward = true;
            section.CanRunBackward = true;
            var stations = new Dictionary<long, float>();
            foreach (Move move in section.Moves)
            {
                LaneInput lane = Network.Lanes[move.Lane];
                section.Length += lane.Length;
                section.CanRunForward &= Network.CanRun(move);
                section.CanRunBackward &= Network.CanRun(move.Reversed);
                if (lane.Station != 0)
                {
                    stations.TryGetValue(lane.Station, out float length);
                    stations[lane.Station] = length + lane.Length;
                }
            }
            // A platform track often continues past the platform ends within
            // the station; the section belongs to the station that has most
            // of its length.
            float best = 0f;
            foreach (KeyValuePair<long, float> entry in stations)
            {
                if (entry.Value > best)
                {
                    best = entry.Value;
                    section.Station = entry.Key;
                }
            }
            section.StartArea = AreaAt(Network.From(section.Moves[0]));
            section.EndArea = AreaAt(Network.To(section.Moves[section.Moves.Count - 1]));
        }

        private int AreaAt(long node)
        {
            foreach (int lane in Network.LanesAt(node))
            {
                if (m_AreaOfLane[lane] >= 0)
                    return m_AreaOfLane[lane];
            }
            return -1;
        }

        // ---- Parallel groups ----

        private void BuildGroups()
        {
            var byEnds = new Dictionary<(int, int), List<int>>();
            foreach (Section section in m_Sections)
            {
                int a = section.StartArea;
                int b = section.EndArea;
                if (a < 0 || b < 0 || a == b)
                    continue;
                (int, int) key = a < b ? (a, b) : (b, a);
                if (!byEnds.TryGetValue(key, out List<int> list))
                {
                    list = new List<int>();
                    byEnds[key] = list;
                }
                list.Add(section.Id);
            }
            foreach (KeyValuePair<(int, int), List<int>> entry in byEnds)
            {
                if (entry.Value.Count < 2)
                    continue;
                var group = new ParallelGroup { Id = m_Groups.Count, AreaA = entry.Key.Item1, AreaB = entry.Key.Item2 };
                group.Sections.AddRange(entry.Value);
                long station = m_Sections[entry.Value[0]].Station;
                foreach (int section in entry.Value)
                {
                    m_GroupOfSection[section] = group.Id;
                    if (m_Sections[section].Station != station)
                        station = 0;
                }
                group.Station = station;
                m_Groups.Add(group);
            }
        }

        /// <summary>What kind of place a parallel group is, for the log and the panel.</summary>
        public GroupKind KindOf(ParallelGroup group)
        {
            if (group.Station != 0)
                return GroupKind.Station;
            int most = System.Math.Max(Alternatives(group, group.AreaA).Count, Alternatives(group, group.AreaB).Count);
            return most >= 2 ? GroupKind.PassingLoop : GroupKind.DoubleTrack;
        }

        /// <summary>Counts for the log, to check the division against what the player built.</summary>
        public LayoutSummary Summarize()
        {
            var summary = new LayoutSummary
            {
                Lanes = Network.Lanes.Count,
                Sections = m_Sections.Count,
                JunctionAreas = m_Areas.Count,
            };
            foreach (Section section in m_Sections)
            {
                if (section.TwoWay && GroupOf(section.Id) < 0)
                {
                    summary.SingleTrackSections++;
                    summary.SingleTrackLength += section.Length;
                }
            }
            foreach (ParallelGroup group in m_Groups)
            {
                switch (KindOf(group))
                {
                    case GroupKind.PassingLoop:
                        summary.PassingLoops++;
                        break;
                    case GroupKind.Station:
                        summary.StationGroups++;
                        break;
                    default:
                        summary.DoubleTracks++;
                        break;
                }
            }
            return summary;
        }

        /// <summary>
        /// The sections of a group a train can run from junction area
        /// <paramref name="fromArea"/> to the group's other end.
        /// </summary>
        public List<int> Alternatives(ParallelGroup group, int fromArea)
        {
            var result = new List<int>();
            foreach (int id in group.Sections)
            {
                Section section = m_Sections[id];
                bool forward = section.StartArea == fromArea;
                if (forward ? section.CanRunForward : section.CanRunBackward)
                    result.Add(id);
            }
            return result;
        }
    }
}
