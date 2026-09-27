using System.Linq;
using SmartTrains.Core.Network;
using Xunit;

namespace SmartTrains.Core.Tests.Network
{
    public class TrackLayoutTests
    {
        [Fact]
        public void APassingLoopIsAGroupOfTwoSectionsBetweenTwoJunctionAreas()
        {
            var (b, l) = TrackBuilder.PassingLoop();
            TrackNetwork n = b.Build();
            var layout = new TrackLayout(n);

            Assert.Equal(4, layout.Sections.Count);
            Assert.Equal(2, layout.Areas.Count);
            ParallelGroup group = Assert.Single(layout.Groups);
            int l1 = layout.SectionOf(n.IndexOf(l.L1));
            int l2 = layout.SectionOf(n.IndexOf(l.L2));
            Assert.Equal(new[] { l1, l2 }.OrderBy(x => x), group.Sections.OrderBy(x => x));
            Assert.Equal(-1, layout.GroupOf(layout.SectionOf(n.IndexOf(l.S1))));
        }

        [Fact]
        public void BothLoopTracksAreAlternativesFromEitherSide()
        {
            var (b, _) = TrackBuilder.PassingLoop();
            var layout = new TrackLayout(b.Build());
            ParallelGroup group = layout.Groups[0];
            Assert.Equal(2, layout.Alternatives(group, group.AreaA).Count);
            Assert.Equal(2, layout.Alternatives(group, group.AreaB).Count);
        }

        [Fact]
        public void DoubleTrackOffersOneWayInEachDirection()
        {
            // The loop drawn as double track: the upper track runs east only,
            // the lower one west only.
            var b = new TrackBuilder()
                .Point("B", 100, 0).Point("C", 120, 5).Point("D", 120, -5)
                .Point("E", 320, 5).Point("F", 320, -5).Point("G", 340, 0);
            b.Lane("B", "C", LaneKind.Switch);
            b.Lane("B", "D", LaneKind.Switch);
            b.Lane("C", "E", twoWay: false);
            b.Lane("F", "D", twoWay: false);
            b.Lane("E", "G", LaneKind.Switch);
            b.Lane("F", "G", LaneKind.Switch);
            var layout = new TrackLayout(b.Build());

            ParallelGroup group = Assert.Single(layout.Groups);
            Assert.Single(layout.Alternatives(group, group.AreaA));
            Assert.Single(layout.Alternatives(group, group.AreaB));
            Assert.NotEqual(layout.Alternatives(group, group.AreaA)[0], layout.Alternatives(group, group.AreaB)[0]);
        }

        [Fact]
        public void ASectionRunsThroughJointsAndAddsUpItsLength()
        {
            var b = new TrackBuilder().Point("A", 0, 0).Point("B", 100, 0).Point("C", 102, 0).Point("D", 300, 0);
            long ab = b.Lane("A", "B");
            b.Lane("B", "C");
            long cd = b.Lane("C", "D");
            TrackNetwork n = b.Build();
            var layout = new TrackLayout(n);

            Section section = Assert.Single(layout.Sections);
            Assert.Equal(300f, section.Length, 3);
            Assert.Equal(new[] { n.IndexOf(ab), n.IndexOf(ab) + 1, n.IndexOf(cd) }, section.Moves.Select(m => m.Lane));
            Assert.Equal(-1, section.StartArea);
            Assert.Equal(-1, section.EndArea);
            Assert.Empty(layout.Groups);
        }

        [Fact]
        public void ALaneDrawnTheOtherWayStillContinuesTheSection()
        {
            // B-C is drawn from C to B. A two-way section runs through it both
            // ways; had it been one-way, the section could be run in neither.
            var b = new TrackBuilder().Point("A", 0, 0).Point("B", 100, 0).Point("C", 200, 0);
            b.Lane("A", "B");
            b.Lane("C", "B");
            Section section = Assert.Single(new TrackLayout(b.Build()).Sections);
            Assert.True(section.TwoWay);

            var one = new TrackBuilder().Point("A", 0, 0).Point("B", 100, 0).Point("C", 200, 0);
            one.Lane("A", "B", twoWay: false);
            one.Lane("C", "B", twoWay: false);
            Section blocked = Assert.Single(new TrackLayout(one.Build()).Sections);
            Assert.False(blocked.CanRunForward);
            Assert.False(blocked.CanRunBackward);
        }

        [Fact]
        public void TurnoutsThatShareAPointOrOverlapFormOneArea()
        {
            // A crossover: two turnouts joined directly, plus a diamond whose
            // lanes only overlap and share no point with them.
            var b = new TrackBuilder()
                .Point("A", 0, 0).Point("B", 10, 0).Point("C", 20, 5)
                .Point("P", 50, -10).Point("Q", 60, 10).Point("R", 50, 10).Point("S", 60, -10);
            long t1 = b.Lane("A", "B", LaneKind.Switch);
            b.Lane("B", "C", LaneKind.Switch);
            long x1 = b.Lane("P", "Q", LaneKind.Crossing);
            long x2 = b.Lane("R", "S", LaneKind.Crossing);
            b.Overlap(x1, x2);
            TrackNetwork n = b.Build();
            var layout = new TrackLayout(n);

            Assert.Equal(2, layout.Areas.Count);
            Assert.Equal(layout.AreaOf(n.IndexOf(x1)), layout.AreaOf(n.IndexOf(x2)));
            Assert.NotEqual(layout.AreaOf(n.IndexOf(t1)), layout.AreaOf(n.IndexOf(x1)));
        }

        [Fact]
        public void PlatformTracksOfOneStationMakeAStationGroup()
        {
            var (b, _) = TrackBuilder.PassingLoop(station: 77);
            var layout = new TrackLayout(b.Build());
            Assert.Equal(77, Assert.Single(layout.Groups).Station);
        }

        [Fact]
        public void APlatformNextToPlainTrackMakesNoStationGroup()
        {
            // The loop again, with only the upper track as a platform.
            var (b, l) = TrackBuilder.PassingLoop();
            TrackNetwork n = b.Build();
            var lanes = n.Lanes.Select(lane => lane.Id == l.L1 ? WithStation(lane, 5) : lane).ToList();
            var layout = new TrackLayout(new TrackNetwork(lanes, new (long, long)[0]));
            Assert.Equal(0, Assert.Single(layout.Groups).Station);
        }

        private static LaneInput WithStation(LaneInput lane, long station)
        {
            lane.Station = station;
            return lane;
        }
    }
}
