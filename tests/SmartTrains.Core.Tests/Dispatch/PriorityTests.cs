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

        private static List<TrainOrder> Dispatch(Merge m, float waited)
        {
            TrackNetwork n = m.Network;
            TrainInput waiting = Train(1, 10, n, m.P1, m.J1, m.W);
            waiting.WaitingMinutes = waited;
            TrainInput other = Train(2, 8, n, m.P2, m.J2, m.W);
            TrainInput onTurnout = Train(3, 5, n, m.J1, m.W);
            return new Dispatcher(new TrackLayout(n)).Dispatch(new[] { waiting, other, onTurnout });
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
