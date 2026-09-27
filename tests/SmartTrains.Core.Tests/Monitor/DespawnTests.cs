using System.Collections.Generic;
using SmartTrains.Core.Monitor;
using Xunit;

namespace SmartTrains.Core.Tests.Monitor
{
    public class DespawnClassifierTests
    {
        private static DespawnObservation Alive()
        {
            return new DespawnObservation { TargetExists = true };
        }

        [Fact]
        public void AStuckTrainIsADeadlockEvenThoughItsRouteAlsoCountsAsFailed()
        {
            DespawnObservation train = Alive();
            train.Stuck = true;
            train.PathFailed = true;
            Assert.Equal(DespawnCause.Deadlock, DespawnClassifier.Classify(train));
        }

        [Fact]
        public void ThroughTrafficAtTheEndOfItsRouteArrivedNormally()
        {
            DespawnObservation train = Alive();
            train.Through = true;
            train.EndReached = true;
            DespawnCause cause = DespawnClassifier.Classify(train);
            Assert.Equal(DespawnCause.Arrived, cause);
            Assert.True(DespawnClassifier.IsNormal(cause));
        }

        [Fact]
        public void ThroughTrafficLeavingOverAConnectionLaneArrivedNormally()
        {
            DespawnObservation train = Alive();
            train.Through = true;
            Assert.Equal(DespawnCause.Arrived, DespawnClassifier.Classify(train));
        }

        [Fact]
        public void ThroughTrafficRemovedOnTheWayIsNotNormal()
        {
            // Through traffic without a route is removed where it stands. That
            // must not be counted as an arrival.
            DespawnObservation train = Alive();
            train.Through = true;
            train.PathFailed = true;
            DespawnCause cause = DespawnClassifier.Classify(train);
            Assert.Equal(DespawnCause.NoRoute, cause);
            Assert.False(DespawnClassifier.IsNormal(cause));
        }

        [Fact]
        public void AReturningTrainAtItsDepotIsNormal()
        {
            DespawnObservation train = Alive();
            train.Returning = true;
            train.EndReached = true;
            Assert.Equal(DespawnCause.Depot, DespawnClassifier.Classify(train));
        }

        [Fact]
        public void AMissingTargetIsNamedBeforeAFailedRoute()
        {
            var train = new DespawnObservation { TargetExists = false, PathFailed = true, Returning = true };
            Assert.Equal(DespawnCause.TargetGone, DespawnClassifier.Classify(train));
        }

        [Fact]
        public void AnythingElseIsOther()
        {
            Assert.Equal(DespawnCause.Other, DespawnClassifier.Classify(Alive()));
        }
    }

    public class BlockerChainTests
    {
        private static System.Func<long, long> Waits(params (long train, long waitsFor)[] pairs)
        {
            var map = new Dictionary<long, long>();
            foreach ((long train, long waitsFor) in pairs)
                map[train] = waitsFor;
            return train => map.TryGetValue(train, out long next) ? next : 0;
        }

        [Fact]
        public void ACircleBackToTheStartIsReportedAsSuch()
        {
            BlockerChain chain = BlockerChains.Follow(1, Waits((1, 2), (2, 3), (3, 1)));
            Assert.Equal(new long[] { 2, 3 }, chain.Trains);
            Assert.True(chain.BackToStart);
        }

        [Fact]
        public void AChainEndingAtAFreeTrainIsNoCircle()
        {
            BlockerChain chain = BlockerChains.Follow(1, Waits((1, 2), (2, 3)));
            Assert.Equal(new long[] { 2, 3 }, chain.Trains);
            Assert.False(chain.BackToStart);
        }

        [Fact]
        public void ACircleFurtherAlongEndsTheWalkWithoutLooping()
        {
            // 1 waits in a queue that runs into a circle of 3 and 4. The walk
            // must stop there instead of going round forever, and must not
            // claim that 1 is part of the circle.
            BlockerChain chain = BlockerChains.Follow(1, Waits((1, 2), (2, 3), (3, 4), (4, 3)));
            Assert.Equal(new long[] { 2, 3, 4 }, chain.Trains);
            Assert.False(chain.BackToStart);
        }

        [Fact]
        public void StopsAtTheMaximumLength()
        {
            BlockerChain chain = BlockerChains.Follow(1, train => train + 1, maxLength: 5);
            Assert.Equal(5, chain.Trains.Count);
        }
    }
}
