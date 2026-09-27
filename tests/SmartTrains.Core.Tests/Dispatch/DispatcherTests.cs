using System.Collections.Generic;
using System.Linq;
using SmartTrains.Core.Dispatch;
using SmartTrains.Core.Network;
using SmartTrains.Core.Tests.Network;
using Xunit;

namespace SmartTrains.Core.Tests.Dispatch
{
    public class DispatcherTests
    {
        private const float kTrainLength = 150f;
        private const float kFarAhead = 10000f;

        private static Move M(TrackNetwork n, long lane, bool forward) => new Move(n.IndexOf(lane), forward);

        private static TrainInput Train(long id, float rank, params Move[] route)
        {
            var train = new TrainInput
            {
                Id = id,
                BasePriority = rank,
                Length = kTrainLength,
                LookAhead = kFarAhead,
                FrontRemaining = 50f,
            };
            train.Route.AddRange(route);
            train.Occupied.Add(route[0]);
            return train;
        }

        private static TrainOrder Order(List<TrainOrder> orders, long train) => orders.Single(o => o.Train == train);

        // ---- Single track with one passing loop (TrackBuilder.PassingLoop) ----

        private static (TrackNetwork Network, TrackBuilder.Loop Lanes, Dispatcher Dispatcher) Loop(bool twoWay = true)
        {
            var (b, l) = TrackBuilder.PassingLoop(twoWay);
            b.Overlap(l.W1, l.W2).Overlap(l.E1, l.E2);
            TrackNetwork n = b.Build();
            return (n, l, new Dispatcher(new TrackLayout(n)));
        }

        private static Move[] East(TrackNetwork n, TrackBuilder.Loop l) =>
            new[] { M(n, l.S1, true), M(n, l.W1, true), M(n, l.L1, true), M(n, l.E1, true), M(n, l.S2, true) };

        private static Move[] West(TrackNetwork n, TrackBuilder.Loop l) =>
            new[] { M(n, l.S2, false), M(n, l.E1, false), M(n, l.L1, false), M(n, l.W1, false), M(n, l.S1, false) };

        [Fact]
        public void AFreeLineIsGrantedToTheEnd()
        {
            var (n, l, d) = Loop();
            TrainOrder order = Assert.Single(d.Dispatch(new[] { Train(1, 10, East(n, l)) }));
            Assert.Equal(-1, order.HoldAt);
        }

        [Fact]
        public void NothingBeyondTheLookAheadIsGranted()
        {
            // 50 m of the first lane are left; the train does not need more yet.
            var (n, l, d) = Loop();
            TrainInput train = Train(1, 10, East(n, l));
            train.LookAhead = 40f;
            Assert.Equal(1, Assert.Single(d.Dispatch(new[] { train })).HoldAt);
        }

        [Fact]
        public void OpposingTrainsMeetInTheLoopOnDifferentTracks()
        {
            // Both routes take the upper loop track, as the game's pathfinder
            // would. The first train gets it and waits there for the single
            // track; the second is sent over the lower track.
            var (n, l, d) = Loop();
            List<TrainOrder> orders = d.Dispatch(new[] { Train(1, 20, East(n, l)), Train(2, 10, West(n, l)) });

            TrainOrder first = Order(orders, 1);
            Assert.Equal(3, first.HoldAt);
            Assert.Equal(2, first.WaitingFor);
            Assert.Null(first.Change);

            TrainOrder second = Order(orders, 2);
            Assert.NotNull(second.Change);
            Assert.Equal(1, second.Change.From);
            Assert.Equal(3, second.Change.To);
            Assert.Equal(new[] { M(n, l.E2, false), M(n, l.L2, false), M(n, l.W2, false) }, second.Change.Moves);
            Assert.Equal(3, second.HoldAt);
            Assert.Equal(1, second.WaitingFor);
        }

        [Fact]
        public void OnceBothWaitInTheLoopTheyPassEachOther()
        {
            var (n, l, d) = Loop();
            TrainInput first = Train(1, 20, M(n, l.L1, true), M(n, l.E1, true), M(n, l.S2, true));
            TrainInput second = Train(2, 10, M(n, l.L2, false), M(n, l.W2, false), M(n, l.S1, false));
            List<TrainOrder> orders = d.Dispatch(new[] { first, second });
            Assert.Equal(-1, Order(orders, 1).HoldAt);
            Assert.Equal(-1, Order(orders, 2).HoldAt);
        }

        [Fact]
        public void AFullPlatformKeepsTheNextTrainOutOfTheJunction()
        {
            // One-way track; the train's route ends at the upper loop track,
            // where another train already stands. Both do not fit.
            var (n, l, d) = Loop(twoWay: false);
            TrainInput standing = Train(1, 5, M(n, l.L1, true));
            TrainInput arriving = Train(2, 10, M(n, l.S1, true), M(n, l.W1, true), M(n, l.L1, true));
            arriving.MayChangeTrack = false;
            TrainOrder order = Order(d.Dispatch(new[] { standing, arriving }), 2);
            Assert.Equal(1, order.HoldAt);
            Assert.Equal(HoldReason.NoRoomAhead, order.Reason);
            Assert.Equal(1, order.WaitingFor);
        }

        [Fact]
        public void ATrainNeverWaitsForItselfAtAShortPlatform()
        {
            // A 100 m platform section of two lanes at the end of the route.
            // The 400 m train stands with its front on the first lane and
            // needs the second. Nobody else is there, so it must be let on.
            var b = new TrackBuilder().Point("A", 0, 0).Point("B", 50, 0).Point("C", 100, 0);
            long first = b.Lane("A", "B", twoWay: false);
            long second = b.Lane("B", "C", twoWay: false);
            TrackNetwork n = b.Build();
            var d = new Dispatcher(new TrackLayout(n));
            TrainInput train = Train(1, 10, M(n, first, true), M(n, second, true));
            train.Length = 400f;
            TrainOrder order = Assert.Single(d.Dispatch(new[] { train }));
            Assert.Equal(-1, order.HoldAt);
        }

        [Fact]
        public void TheRefusedLaneIsReported()
        {
            var (n, l, d) = Loop();
            List<TrainOrder> orders = d.Dispatch(new[] { Train(1, 20, East(n, l)), Train(2, 10, West(n, l)) });
            // Train 1 is refused at the single track S2, index 4 of its route.
            Assert.Equal(4, Order(orders, 1).BlockedAt);
            Assert.Equal(2, Order(orders, 1).GrantedEnd);
        }

        [Fact]
        public void AGrantIsNeverTakenBack()
        {
            var (n, l, d) = Loop();
            TrainInput opposing = Train(2, 10, West(n, l));
            d.Dispatch(new[] { Train(1, 20, East(n, l)), opposing });

            // Next round the first train has moved on to the turnout. A train
            // now stands on its loop track; the grant of the loop track stands.
            TrainInput moved = Train(1, 20, M(n, l.W1, true), M(n, l.L1, true), M(n, l.E1, true), M(n, l.S2, true));
            TrainInput intruder = Train(3, 50, M(n, l.L1, true));
            TrainOrder order = Order(d.Dispatch(new[] { moved, intruder, opposing }), 1);
            Assert.Equal(2, order.HoldAt);
        }

        // ---- Two loops joined by single track ----

        /// <summary>
        /// A three-track loop in the west, single track S, a two-track loop in
        /// the east:
        /// <code>
        ///  a ====\                   /==== p
        ///  b =====J2 ------ S ------K
        ///  c ====/                   \==== q
        /// </code>
        /// S is 400 m, longer than a train, so a train would fit on it. Only
        /// its being single track makes it no place to wait.
        /// </summary>
        private sealed class TwoLoops
        {
            public TrackNetwork Network;
            public long A, B, C, AJ, BJ, CJ, S, KP, KQ, P, Q;
        }

        private static TwoLoops BuildTwoLoops()
        {
            var b = new TrackBuilder()
                .Point("A1", 120, 10).Point("A2", 320, 10)
                .Point("B1", 120, 0).Point("B2", 320, 0)
                .Point("C1", 120, -10).Point("C2", 320, -10)
                .Point("J2", 340, 0).Point("K", 740, 0)
                .Point("P1", 760, 10).Point("P2", 960, 10)
                .Point("Q1", 760, -10).Point("Q2", 960, -10);
            var t = new TwoLoops
            {
                A = b.Lane("A1", "A2"),
                B = b.Lane("B1", "B2"),
                C = b.Lane("C1", "C2"),
                AJ = b.Lane("A2", "J2", LaneKind.Switch),
                BJ = b.Lane("B2", "J2", LaneKind.Switch),
                CJ = b.Lane("C2", "J2", LaneKind.Switch),
                S = b.Lane("J2", "K"),
                KP = b.Lane("K", "P1", LaneKind.Switch),
                KQ = b.Lane("K", "Q1", LaneKind.Switch),
                P = b.Lane("P1", "P2"),
                Q = b.Lane("Q1", "Q2"),
            };
            t.Network = b.Build();
            return t;
        }

        [Fact]
        public void ATrainOfLowerRankGivesWayToOneWaitingForTheSingleTrack()
        {
            // Train 1 waits in the west for the single track, where train 2
            // comes the other way. Train 3 would follow train 2 onto it and
            // keep train 1 waiting; it must not.
            TwoLoops t = BuildTwoLoops();
            TrackNetwork n = t.Network;
            var d = new Dispatcher(new TrackLayout(n));
            TrainInput waiting = Train(1, 30, M(n, t.A, true), M(n, t.AJ, true), M(n, t.S, true), M(n, t.KP, true), M(n, t.P, true));
            TrainInput oncoming = Train(2, 10, M(n, t.S, false), M(n, t.BJ, false), M(n, t.B, false));
            TrainInput following = Train(3, 5, M(n, t.Q, false), M(n, t.KQ, false), M(n, t.S, false), M(n, t.CJ, false), M(n, t.C, false));
            List<TrainOrder> orders = d.Dispatch(new[] { waiting, oncoming, following });

            Assert.Equal(2, Order(orders, 1).WaitingFor);
            Assert.Equal(-1, Order(orders, 2).HoldAt);
            TrainOrder third = Order(orders, 3);
            Assert.Equal(1, third.HoldAt);
            Assert.Equal(HoldReason.GivingWay, third.Reason);
            Assert.Equal(1, third.WaitingFor);
        }

        [Fact]
        public void WaitingLongEnoughOutranksAHigherBaseRank()
        {
            TwoLoops t = BuildTwoLoops();
            TrackNetwork n = t.Network;
            var d = new Dispatcher(new TrackLayout(n));
            TrainInput fresh = Train(1, 30, M(n, t.A, true), M(n, t.AJ, true), M(n, t.S, true), M(n, t.KP, true), M(n, t.P, true));
            TrainInput patient = Train(3, 5, M(n, t.Q, false), M(n, t.KQ, false), M(n, t.S, false), M(n, t.CJ, false), M(n, t.C, false));
            patient.WaitingMinutes = 20f;
            List<TrainOrder> orders = d.Dispatch(new[] { fresh, patient });

            Assert.Equal(-1, Order(orders, 3).HoldAt);
            TrainOrder first = Order(orders, 1);
            Assert.Equal(1, first.HoldAt);
            Assert.Equal(3, first.WaitingFor);
        }

        [Fact]
        public void ATrainNeverEntersSingleTrackWithoutAPlaceToWaitBeyond()
        {
            // Both tracks of the east loop are taken. Train 1 must stay in the
            // west loop instead of running onto the single track and waiting
            // there, where it would block trains leaving the east loop.
            TwoLoops t = BuildTwoLoops();
            TrackNetwork n = t.Network;
            var d = new Dispatcher(new TrackLayout(n));
            TrainInput train = Train(1, 30, M(n, t.A, true), M(n, t.AJ, true), M(n, t.S, true), M(n, t.KP, true), M(n, t.P, true));
            TrainInput onP = Train(2, 10, M(n, t.P, false));
            TrainInput onQ = Train(3, 10, M(n, t.Q, false));
            TrainOrder order = Order(d.Dispatch(new[] { train, onP, onQ }), 1);
            Assert.Equal(1, order.HoldAt);
        }
    }
}
