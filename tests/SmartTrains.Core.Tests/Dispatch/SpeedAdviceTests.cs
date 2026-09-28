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
