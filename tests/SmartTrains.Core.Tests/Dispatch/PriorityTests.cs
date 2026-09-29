using System.Collections.Generic;
using System.Linq;
using SmartTrains.Core.Dispatch;
using SmartTrains.Core.Network;
using SmartTrains.Core.Tests.Network;
using Xunit;

namespace SmartTrains.Core.Tests.Dispatch
{
    public class PriorityTests
    {
        private static Move M(TrackNetwork n, long lane) => new Move(n.IndexOf(lane), true);

        private static TrainInput Train(long id, float rank, TrackNetwork n, params long[] lanes)
        {
            var train = new TrainInput { Id = id, BasePriority = rank, Length = 150f, LookAhead = 10000f, FrontRemaining = 10f, MayChangeTrack = false };
            foreach (long lane in lanes)
                train.Route.Add(M(n, lane));
            train.Occupied.Add(train.Route[0]);
            return train;
        }

        private static TrainOrder Order(IEnumerable<TrainOrder> orders, long train) => orders.Single(o => o.Train == train);

        /// <summary>
        /// Two one-way approaches, P1 and P2, merge over the turnouts J1 and
        /// J2 into W, 350 m, where the routes end. Train 3 stands on J1 on
        /// its way into W; room in W is left for one train more.
        /// </summary>
        private sealed class Merge
        {
            public TrackNetwork Network;
            public long P1, P2, J1, J2, W;
        }

        private static Merge Build()
        {
            var b = new TrackBuilder()
                .Point("A1", 0, 0).Point("B1", 400, 0)
                .Point("A2", 0, 40).Point("B2", 400, 40)
                .Point("M", 420, 20).Point("N", 770, 20);
            var m = new Merge
            {
                P1 = b.Lane("A1", "B1", twoWay: false),
                P2 = b.Lane("A2", "B2", twoWay: false),
                J1 = b.Lane("B1", "M", LaneKind.Switch, twoWay: false),
                J2 = b.Lane("B2", "M", LaneKind.Switch, twoWay: false),
                W = b.Lane("M", "N", twoWay: false),
            };
            m.Network = b.Build();
            return m;
        }

        private static List<TrainOrder> Dispatch(Merge m, float waited, float otherWaited = 0f)
        {
            TrackNetwork n = m.Network;
            TrainInput waiting = Train(1, 10, n, m.P1, m.J1, m.W);
            waiting.WaitingMinutes = waited;
            // Its base rank keeps it below train 1 however long it waits.
            TrainInput other = Train(2, -100, n, m.P2, m.J2, m.W);
            other.WaitingMinutes = otherWaited;
            TrainInput onTurnout = Train(3, -200, n, m.J1, m.W);
            return new Dispatcher(new TrackLayout(n)).Dispatch(new[] { waiting, other, onTurnout });
        }

        [Fact]
        public void AClaimDoesNotKeepOutATrainThatHasWaitedLongItself()
        {
            // Both trains starve. Claims that keep starving trains out of each
            // other's way make every long wait longer, and once many trains
            // wait long, a jam feeds itself. Train 2 takes the free room.
            List<TrainOrder> orders = Dispatch(Build(), waited: Dispatcher.PriorityAfterMinutes + 20f,
                otherWaited: Dispatcher.PriorityAfterMinutes + 1f);
            Assert.Equal(-1, Order(orders, 2).HoldAt);
        }

        [Fact]
        public void ATrainThatWaitedLongKeepsOthersOutOfTheTrackItNeeds()
        {
            // Train 1 waits for J1. Train 2 would take the last room in W, and
            // train 1 would find W full once J1 is free: over and over, with
            // one train after another, train 1 never gets in.
            List<TrainOrder> orders = Dispatch(Build(), waited: Dispatcher.PriorityAfterMinutes + 1f);

            TrainOrder other = Order(orders, 2);
            Assert.Equal(1, other.HoldAt);
            Assert.Equal(HoldReason.GivingWay, other.Reason);
            Assert.Equal(1, other.WaitingFor);
        }

        [Fact]
        public void ATrainInsideTheClaimedTrackMayLeaveIt()
        {
            // Train 3 on J1 is what train 1 waits for; held back, it would
            // keep train 1 waiting for good.
            List<TrainOrder> orders = Dispatch(Build(), waited: Dispatcher.PriorityAfterMinutes + 1f);
            Assert.Equal(-1, Order(orders, 3).HoldAt);
        }

        [Fact]
        public void AShortWaitClaimsNothing()
        {
            // Holding track for every waiting train would stop trains that
            // could pass it; the claim is for trains starved of track.
            List<TrainOrder> orders = Dispatch(Build(), waited: 0f);
            Assert.Equal(-1, Order(orders, 2).HoldAt);
        }

        [Fact]
        public void AClaimNeverKeepsOutTheTrainTheClaimantWaitsFor()
        {
            // Two stations on single track, S1 and S2, with a double track
            // between them too short to wait on. Train 1 stands in S1 and runs
            // east through S2. Train 2, of higher rank, comes from the east
            // through S2 and into S1, and waits for train 1 to leave S1. Its
            // claim on S2 against the east direction must not keep train 1
            // out: train 1 leaving is what train 2 waits for. Kept out, the
            // two wait for each other (as they did in the game, at (1159,
            // -1964), until the game removed both).
            var b = new TrackBuilder()
                .Point("S1w", 0, 0).Point("S1e", 200, 0)
                .Point("De0", 220, 5).Point("De1", 300, 5).Point("Dw1", 300, -5).Point("Dw0", 220, -5)
                .Point("S2w", 320, 0).Point("S2e", 520, 0)
                .Point("Ee0", 540, 5).Point("Ee1", 940, 5).Point("Ew1", 940, -5).Point("Ew0", 540, -5);
            long s1 = b.Lane("S1w", "S1e");
            long j1 = b.Lane("S1e", "De0", LaneKind.Switch, twoWay: false);
            long de = b.Lane("De0", "De1", twoWay: false);
            long j2 = b.Lane("De1", "S2w", LaneKind.Switch, twoWay: false);
            long s2 = b.Lane("S2w", "S2e");
            long j3 = b.Lane("S2e", "Ee0", LaneKind.Switch, twoWay: false);
            long ee = b.Lane("Ee0", "Ee1", twoWay: false);
            long ew = b.Lane("Ew1", "Ew0", twoWay: false);
            long j3b = b.Lane("Ew0", "S2e", LaneKind.Switch, twoWay: false);
            long j2b = b.Lane("S2w", "Dw1", LaneKind.Switch, twoWay: false);
            long dw = b.Lane("Dw1", "Dw0", twoWay: false);
            long j1b = b.Lane("Dw0", "S1e", LaneKind.Switch, twoWay: false);
            TrackNetwork n = b.Build();

            TrainInput east = Train(1, 20, n, s1, j1, de, j2, s2, j3, ee);
            var west = new TrainInput { Id = 2, BasePriority = 36, Length = 150f, LookAhead = 10000f, FrontRemaining = 10f, MayChangeTrack = false };
            west.Route.AddRange(new[]
            {
                M(n, ew), M(n, j3b), new Move(n.IndexOf(s2), false), M(n, j2b), M(n, dw), M(n, j1b), new Move(n.IndexOf(s1), false),
            });
            west.Occupied.Add(west.Route[0]);
            List<TrainOrder> orders = new Dispatcher(new TrackLayout(n)).Dispatch(new[] { east, west });

            Assert.Equal(-1, Order(orders, 1).HoldAt);
            Assert.Equal(1, Order(orders, 2).WaitingFor);
        }

        [Fact]
        public void ATrainWaitingBehindAnotherClaimsNothing()
        {
            // One-way section P, 600 m, then turnout J into W. The leader
            // stands at the front of P, the follower, waiting long, behind it.
            // It waits for the leader, not for a stream of trains; a claim
            // would keep the leader out of J, and the two would wait for each
            // other.
            var b = new TrackBuilder()
                .Point("A", 0, 0).Point("B", 300, 0).Point("C", 600, 0)
                .Point("D", 620, 5).Point("E", 620, -5).Point("F", 1020, 5);
            long p1 = b.Lane("A", "B", twoWay: false);
            long p2 = b.Lane("B", "C", twoWay: false);
            long j = b.Lane("C", "D", LaneKind.Switch, twoWay: false);
            b.Lane("C", "E", LaneKind.Switch, twoWay: false);
            long w = b.Lane("D", "F", twoWay: false);
            TrackNetwork n = b.Build();

            TrainInput leader = Train(1, 10, n, p2, j, w);
            TrainInput follower = Train(2, 10, n, p1, p2, j, w);
            follower.FrontRemaining = 100f;
            follower.WaitingMinutes = Dispatcher.PriorityAfterMinutes + 1f;
            List<TrainOrder> orders = new Dispatcher(new TrackLayout(n)).Dispatch(new[] { leader, follower });

            Assert.Equal(-1, Order(orders, 1).HoldAt);
            Assert.Equal(1, Order(orders, 2).WaitingFor);
            // The follower stopping behind the leader is what the dispatcher
            // planned; the metrics tell that apart from a misjudged track.
            Assert.Equal(1, Order(orders, 2).Ahead);
            Assert.Equal(0, Order(orders, 1).Ahead);
        }
    }
}
