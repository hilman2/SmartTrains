using System.Collections.Generic;
using System.Linq;
using SmartTrains.Core.Network;
using Xunit;

namespace SmartTrains.Core.Tests.Network
{
    public class TrackNetworkTests
    {
        private static Move Fwd(TrackNetwork n, long id) => new Move(n.IndexOf(id), true);
        private static Move Back(TrackNetwork n, long id) => new Move(n.IndexOf(id), false);

        [Fact]
        public void ATrainCanTakeEitherBranchOfATurnout()
        {
            var (b, l) = TrackBuilder.PassingLoop();
            TrackNetwork n = b.Build();
            List<Move> next = n.Successors(Fwd(n, l.S1));
            Assert.Equal(new[] { Fwd(n, l.W1), Fwd(n, l.W2) }.OrderBy(m => m.Lane), next.OrderBy(m => m.Lane));
        }

        [Fact]
        public void NoWayLeadsFromOneBranchOfATurnoutIntoTheOther()
        {
            // Coming back through branch B-C, the train reaches B, where B-D
            // also starts. Only a reversing train could take it.
            var (b, l) = TrackBuilder.PassingLoop();
            TrackNetwork n = b.Build();
            Assert.Equal(new[] { Back(n, l.S1) }, n.Successors(Back(n, l.W1)));
        }

        [Fact]
        public void AOneWayLaneIsNeverRunBackward()
        {
            var b = new TrackBuilder().Point("A", 0, 0).Point("B", 100, 0).Point("C", 200, 0);
            long ab = b.Lane("A", "B");
            long cb = b.Lane("C", "B", twoWay: false);
            TrackNetwork n = b.Build();
            Assert.Empty(n.Successors(Fwd(n, ab)));
            Assert.Equal(new[] { Back(n, ab) }, n.Successors(Fwd(n, cb)));
        }

        [Fact]
        public void APredecessorOnOneWayTrackIsFound()
        {
            var b = new TrackBuilder().Point("A", 0, 0).Point("B", 100, 0).Point("C", 200, 0);
            long ab = b.Lane("A", "B", twoWay: false);
            long bc = b.Lane("B", "C", twoWay: false);
            TrackNetwork n = b.Build();
            Assert.Equal(new[] { Fwd(n, ab) }, n.Predecessors(Fwd(n, bc)));
        }

        [Fact]
        public void PredecessorsRespectTheTurnoutToo()
        {
            var (b, l) = TrackBuilder.PassingLoop();
            TrackNetwork n = b.Build();
            Assert.Equal(new[] { Fwd(n, l.S1) }, n.Predecessors(Fwd(n, l.W2)));
            Assert.Equal(new[] { Fwd(n, l.W1) }, n.Predecessors(Fwd(n, l.L1)));
        }

        [Fact]
        public void OverlapsAreKnownFromBothSidesAndUnknownLanesAreIgnored()
        {
            var (b, l) = TrackBuilder.PassingLoop();
            b.Overlap(l.W1, l.W2).Overlap(l.W1, 999);
            TrackNetwork n = b.Build();
            Assert.Equal(new[] { n.IndexOf(l.W2) }, n.Overlaps(n.IndexOf(l.W1)));
            Assert.Equal(new[] { n.IndexOf(l.W1) }, n.Overlaps(n.IndexOf(l.W2)));
        }
    }
}
