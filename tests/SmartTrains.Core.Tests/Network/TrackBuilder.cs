using System.Collections.Generic;
using SmartTrains.Core.Network;

namespace SmartTrains.Core.Tests.Network
{
    /// <summary>
    /// Builds small track networks for tests from named points on a plane.
    /// Lanes are straight, so a lane's heading is the direction from its
    /// start point to its end point.
    /// </summary>
    internal sealed class TrackBuilder
    {
        private readonly Dictionary<string, (float X, float Z)> m_Points = new Dictionary<string, (float X, float Z)>();
        private readonly Dictionary<string, long> m_NodeKeys = new Dictionary<string, long>();
        private readonly List<LaneInput> m_Lanes = new List<LaneInput>();
        private readonly List<(long, long)> m_Overlaps = new List<(long, long)>();

        public TrackBuilder Point(string name, float x, float z)
        {
            m_Points[name] = (x, z);
            m_NodeKeys[name] = 1000 + m_NodeKeys.Count;
            return this;
        }

        /// <returns>The lane's key.</returns>
        public long Lane(string from, string to, LaneKind kind = LaneKind.Plain, bool twoWay = true, long station = 0)
        {
            (float fx, float fz) = m_Points[from];
            (float tx, float tz) = m_Points[to];
            var heading = new Heading(tx - fx, tz - fz);
            long id = m_Lanes.Count + 1;
            m_Lanes.Add(new LaneInput
            {
                Id = id,
                StartNode = m_NodeKeys[from],
                EndNode = m_NodeKeys[to],
                Length = (float)System.Math.Sqrt((tx - fx) * (tx - fx) + (tz - fz) * (tz - fz)),
                TwoWay = twoWay,
                Kind = kind,
                Station = station,
                StartHeading = heading,
                EndHeading = heading,
            });
            return id;
        }

        public TrackBuilder Overlap(long a, long b)
        {
            m_Overlaps.Add((a, b));
            return this;
        }

        public TrackNetwork Build()
        {
            return new TrackNetwork(m_Lanes, m_Overlaps);
        }

        /// <summary>
        /// Single track from A to H with one passing loop:
        /// <code>
        ///                 C ---- l1 ---- E
        ///                /                \
        ///  A --- s1 --- B                  G --- s2 --- H
        ///                \                /
        ///                 D ---- l2 ---- F
        /// </code>
        /// The turnouts are the lanes B-C, B-D (west) and E-G, F-G (east).
        /// </summary>
        public static (TrackBuilder Builder, Loop Lanes) PassingLoop(bool twoWay = true, long station = 0)
        {
            var b = new TrackBuilder()
                .Point("A", 0, 0).Point("B", 100, 0)
                .Point("C", 120, 5).Point("D", 120, -5)
                .Point("E", 320, 5).Point("F", 320, -5)
                .Point("G", 340, 0).Point("H", 440, 0);
            var lanes = new Loop
            {
                S1 = b.Lane("A", "B", twoWay: twoWay),
                W1 = b.Lane("B", "C", LaneKind.Switch, twoWay),
                W2 = b.Lane("B", "D", LaneKind.Switch, twoWay),
                L1 = b.Lane("C", "E", twoWay: twoWay, station: station),
                L2 = b.Lane("D", "F", twoWay: twoWay, station: station),
                E1 = b.Lane("E", "G", LaneKind.Switch, twoWay),
                E2 = b.Lane("F", "G", LaneKind.Switch, twoWay),
                S2 = b.Lane("G", "H", twoWay: twoWay),
            };
            return (b, lanes);
        }

        internal struct Loop
        {
            public long S1, W1, W2, L1, L2, E1, E2, S2;
        }
    }
}
