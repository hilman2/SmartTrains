using System.Collections.Generic;
using System.Linq;
using SmartTrains.Core.Dispatch;
using SmartTrains.Core.Network;
using SmartTrains.Core.Tests.Network;
using Xunit;

namespace SmartTrains.Core.Tests.Dispatch
{
    public class SpeedAdviceTests
    {
        /// <summary>
        /// Section P (400 m), a level crossing X, section S. Track Z crosses
        /// X, and train 2 is on it: train 1 is held in front of X until
        /// train 2 has cleared it.
        /// </summary>
        private sealed class Crossing
        {
            public TrackNetwork Network;
            public long P, X, S, Q, Z, R;
        }

        private static Crossing Build()
        {
            var b = new TrackBuilder()
                .Point("A", 0, 0).Point("B", 400, 0).Point("C", 420, 0).Point("D", 720, 0)
                .Point("G", 410, 200).Point("E", 410, 10).Point("F", 410, -10).Point("H", 410, -400);
            var c = new Crossing
            {
                P = b.Lane("A", "B", twoWay: false),
                X = b.Lane("B", "C", LaneKind.Crossing, twoWay: false),
                S = b.Lane("C", "D", twoWay: false),
                Q = b.Lane("G", "E", twoWay: false),
                Z = b.Lane("E", "F", LaneKind.Crossing, twoWay: false),
                R = b.Lane("F", "H", twoWay: false),
            };
            b.Overlap(c.X, c.Z);
            c.Network = b.Build();
            return c;
        }

        private static Move M(TrackNetwork n, long lane) => new Move(n.IndexOf(lane), true);

        private static TrainInput Held(Crossing c, float speed)
        {
            TrackNetwork n = c.Network;
            var train = new TrainInput { Id = 1, BasePriority = 10, Length = 100f, LookAhead = 10000f, FrontRemaining = 300f, Speed = speed };
            train.Route.AddRange(new[] { M(n, c.P), M(n, c.X), M(n, c.S) });
            train.Occupied.Add(M(n, c.P));
            return train;
        }

        private static TrainInput Crossing2(Crossing c, float speed)
        {
            TrackNetwork n = c.Network;
            var train = new TrainInput { Id = 2, BasePriority = 50, Length = 150f, LookAhead = 10000f, FrontRemaining = 10f, Speed = speed };
            train.Route.AddRange(new[] { M(n, c.Z), M(n, c.R) });
            train.Occupied.Add(M(n, c.Z));
            train.Occupied.Add(M(n, c.Q));
            return train;
        }

        private static TrainOrder Order(Crossing c, TrainInput held, TrainInput crossing)
        {
            return new Dispatcher(new TrackLayout(c.Network)).Dispatch(new[] { held, crossing }).Single(o => o.Train == 1);
        }

        [Fact]
        public void AHeldTrainSlowsToArriveWhenTheCrossingTrainHasPassed()
        {
            // Train 2 needs 150 m / 10 m/s = 15 s to clear the crossing. Train
            // 1 is 300 m away: 20 m/s gets it there just in time.
            Crossing c = Build();
            TrainOrder order = Order(c, Held(c, 30f), Crossing2(c, 10f));
            Assert.Equal(1, order.HoldAt);
            Assert.Equal(20f, order.SpeedAdvice, 1);
        }

        [Fact]
        public void NoAdviceWhenTheTrainIsSlowerAlready()
        {
            Crossing c = Build();
            Assert.Equal(0f, Order(c, Held(c, 15f), Crossing2(c, 10f)).SpeedAdvice);
        }

        [Fact]
        public void NoAdviceWhileTheTrainIsStillOnAJunctionBehind()
        {
            // Slowing now would keep its rear on the junction longer.
            Crossing c = Build();
            TrainInput held = Held(c, 30f);
            held.Occupied.Add(M(c.Network, c.Z));
            Assert.Equal(0f, Order(c, held, Crossing2(c, 10f)).SpeedAdvice);
        }

        [Fact]
        public void NoAdviceWhenItIsUnknownWhenTheWayFrees()
        {
            // Train 2 stands on the crossing and is not boarding.
            Crossing c = Build();
            Assert.Equal(0f, Order(c, Held(c, 30f), Crossing2(c, 0f)).SpeedAdvice);
        }

        /// <summary>
        /// Train 3 is at the front of P, 390 m in, held in front of X for
        /// train 2 crossing; train 1 follows 100 m in. Both 100 m long.
        /// </summary>
        private static (TrainInput Follower, TrainInput Leader) Queue(Crossing c, float leaderDepartureIn = -1f)
        {
            TrackNetwork n = c.Network;
            TrainInput leader = Held(c, 0f);
            leader.Id = 3;
            leader.FrontRemaining = 10f;
            leader.DepartureIn = leaderDepartureIn;
            TrainInput follower = Held(c, 30f);
            follower.FrontRemaining = 300f;
            return (follower, leader);
        }

        [Fact]
        public void AFollowerRollsUpToTheTrainAheadAsItsWayFrees()
        {
            // The leader, standing and held, goes once train 2 has cleared X,
            // in 15 s. The follower stops at the leader's rear, 175 m on
            // (390 - 100 - 15 margin - 100), plus the 10 m the leader still
            // has to its hold: 185 m in 15 s.
            Crossing c = Build();
            (TrainInput follower, TrainInput leader) = Queue(c);
            TrainOrder order = new Dispatcher(new TrackLayout(c.Network))
                .Dispatch(new[] { follower, leader, Crossing2(c, 10f) }).Single(o => o.Train == 1);
            Assert.Equal(3, order.WaitingFor);
            Assert.Equal(185f / 15f, order.SpeedAdvice, 1);
        }

        [Fact]
        public void AChainOfHeldTrainsIsWaitedOutRolling()
        {
            // Train 2 stands on the crossing lane Z, held for room in R, where
            // train 5 boards and leaves in 20 s. Train 5 then needs 20 s to get
            // going, train 2 as long again to clear the crossing: train 1, held
            // in front of X for train 2, has 60 s for its 300 m.
            Crossing c = Build();
            TrackNetwork n = c.Network;
            TrainInput onCrossing = Crossing2(c, 0f);
            onCrossing.Occupied.RemoveAt(1);
            var boarding = new TrainInput { Id = 5, BasePriority = 50, Length = 300f, LookAhead = 10000f, FrontRemaining = 20f, DepartureIn = 20f };
            boarding.Route.Add(M(n, c.R));
            boarding.Occupied.Add(M(n, c.R));

            List<TrainOrder> orders = new Dispatcher(new TrackLayout(n)).Dispatch(new[] { Held(c, 30f), onCrossing, boarding });
            Assert.Equal(5, orders.Single(o => o.Train == 2).WaitingFor);
            TrainOrder order = orders.Single(o => o.Train == 1);
            Assert.Equal(2, order.WaitingFor);
            Assert.Equal(5f, order.SpeedAdvice, 1);
        }

        [Fact]
        public void AFollowerAimsWhereTheTrainAheadWillStand()
        {
            // The leader stands 200 m in, with P free before it: it moves up
            // to X and waits there 15 s for train 2. The follower, 50 m in,
            // stops behind it at X: 35 m to the leader's rear now, plus the
            // 200 m the leader moves up.
            Crossing c = Build();
            (TrainInput follower, TrainInput leader) = Queue(c);
            leader.FrontRemaining = 200f;
            follower.FrontRemaining = 350f;
            TrainOrder order = new Dispatcher(new TrackLayout(c.Network))
                .Dispatch(new[] { follower, leader, Crossing2(c, 10f) }).Single(o => o.Train == 1);
            Assert.Equal(235f / 15f, order.SpeedAdvice, 1);
        }

        [Fact]
        public void AFollowerRollsUpToATrainAheadThatIsBoarding()
        {
            // The leader leaves its platform in 10 s; the follower reaches its
            // rear, 175 m on, then.
            Crossing c = Build();
            (TrainInput follower, TrainInput leader) = Queue(c, leaderDepartureIn: 10f);
            TrainOrder order = new Dispatcher(new TrackLayout(c.Network))
                .Dispatch(new[] { follower, leader }).Single(o => o.Train == 1);
            Assert.Equal(17.5f, order.SpeedAdvice, 1);
        }

        [Fact]
        public void ARearInThePlainSectionBehindDoesNotStopTheAdvice()
        {
            // P is split by a bend into PA and PB; the train's rear is still in
            // PA. No junction is involved, so it may slow as in the first test.
            var b = new TrackBuilder()
                .Point("O", -100, -200).Point("A", 0, 0).Point("B", 400, 0).Point("C", 420, 0).Point("D", 720, 0)
                .Point("G", 410, 200).Point("E", 410, 10).Point("F", 410, -10).Point("H", 410, -400);
            var c = new Crossing
            {
                P = b.Lane("A", "B", twoWay: false),
                X = b.Lane("B", "C", LaneKind.Crossing, twoWay: false),
                S = b.Lane("C", "D", twoWay: false),
                Q = b.Lane("G", "E", twoWay: false),
                Z = b.Lane("E", "F", LaneKind.Crossing, twoWay: false),
                R = b.Lane("F", "H", twoWay: false),
            };
            long pa = b.Lane("O", "A", twoWay: false);
            b.Overlap(c.X, c.Z);
            c.Network = b.Build();
            var layout = new TrackLayout(c.Network);
            Assert.NotEqual(layout.SectionOf(c.Network.IndexOf(pa)), layout.SectionOf(c.Network.IndexOf(c.P)));

            TrainInput held = Held(c, 30f);
            held.Occupied.Add(M(c.Network, pa));
            TrainOrder order = new Dispatcher(layout).Dispatch(new[] { held, Crossing2(c, 10f) }).Single(o => o.Train == 1);
            Assert.Equal(20f, order.SpeedAdvice, 1);
        }

        [Fact]
        public void TrainsWaitingForEachOtherGiveNoAdviceAndNoEndlessLoop()
        {
            // Passing loop, one track each way only. Train 2 stands in loop
            // track L1 at its west end, held for S1; train 1 stands at the end
            // of S1, held for L1. Train 4 follows train 1 on S1: its wait runs
            // into the circle and is not known.
            var (b, l) = TrackBuilder.PassingLoop();
            b.Overlap(l.W1, l.W2).Overlap(l.E1, l.E2);
            TrackNetwork n = b.Build();
            Move East(long lane) => new Move(n.IndexOf(lane), true);
            Move West(long lane) => new Move(n.IndexOf(lane), false);

            var standing = new TrainInput { Id = 1, BasePriority = 10, Length = 40f, LookAhead = 10000f, FrontRemaining = 5f, MayChangeTrack = false };
            standing.Route.AddRange(new[] { East(l.S1), East(l.W1), East(l.L1), East(l.E1), East(l.S2) });
            standing.Occupied.Add(East(l.S1));
            var facing = new TrainInput { Id = 2, BasePriority = 20, Length = 40f, LookAhead = 10000f, FrontRemaining = 5f, MayChangeTrack = false };
            facing.Route.AddRange(new[] { West(l.L1), West(l.W1), West(l.S1) });
            facing.Occupied.Add(West(l.L1));
            var follower = new TrainInput { Id = 4, BasePriority = 5, Length = 20f, LookAhead = 10000f, FrontRemaining = 70f, Speed = 20f, MayChangeTrack = false };
            follower.Route.AddRange(new[] { East(l.S1), East(l.W1), East(l.L1) });
            follower.Occupied.Add(East(l.S1));

            List<TrainOrder> orders = new Dispatcher(new TrackLayout(n)).Dispatch(new[] { standing, facing, follower });
            Assert.Equal(2, orders.Single(o => o.Train == 1).WaitingFor);
            Assert.Equal(1, orders.Single(o => o.Train == 2).WaitingFor);
            Assert.Equal(1, orders.Single(o => o.Train == 4).Ahead);
            Assert.Equal(0f, orders.Single(o => o.Train == 4).SpeedAdvice);
        }

        [Fact]
        public void AnAdviceNeverGoesBelowWalkingPace()
        {
            // Train 2 leaves its platform in 400 s; 300 m in 420 s would be
            // under 1 m/s.
            Crossing c = Build();
            TrainInput standing = Crossing2(c, 0f);
            standing.DepartureIn = 400f;
            Assert.Equal(Dispatcher.MinAdvisedSpeed, Order(c, Held(c, 30f), standing).SpeedAdvice);
        }
    }
}
