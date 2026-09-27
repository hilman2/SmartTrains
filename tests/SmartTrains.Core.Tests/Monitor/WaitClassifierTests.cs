using SmartTrains.Core.Monitor;
using Xunit;

namespace SmartTrains.Core.Tests.Monitor
{
    public class WaitClassifierTests
    {
        private static TrainObservation Standing(Obstacle obstacle, bool isTrain)
        {
            return new TrainObservation { Obstacle = obstacle, ObstacleIsTrain = isTrain };
        }

        [Fact]
        public void BoardingWinsOverTheTrainAheadTheNavigationStillReports()
        {
            TrainObservation train = Standing(Obstacle.Ahead, isTrain: true);
            train.Boarding = true;
            Assert.Equal(WaitReason.Boarding, WaitClassifier.Classify(train));

            train.DepartureDue = true;
            Assert.Equal(WaitReason.LatePassengers, WaitClassifier.Classify(train));
        }

        [Fact]
        public void DeadlockWinsOverEverything()
        {
            var train = new TrainObservation { Stuck = true, Boarding = true, Moving = true, Obstacle = Obstacle.Ahead, ObstacleIsTrain = true };
            Assert.Equal(WaitReason.Deadlock, WaitClassifier.Classify(train));
        }

        [Fact]
        public void AMovingTrainHasNoReasonWhateverLiesAhead()
        {
            TrainObservation train = Standing(Obstacle.Crossing, isTrain: true);
            train.Moving = true;
            Assert.Equal(WaitReason.None, WaitClassifier.Classify(train));
        }

        [Theory]
        [InlineData(Obstacle.Ahead, WaitReason.TrainAhead)]
        [InlineData(Obstacle.Crossing, WaitReason.CrossingTrain)]
        [InlineData(Obstacle.Oncoming, WaitReason.OncomingTrain)]
        public void NamesTheKindOfConflictWithAnotherTrain(Obstacle obstacle, WaitReason expected)
        {
            Assert.Equal(expected, WaitClassifier.Classify(Standing(obstacle, isTrain: true)));
        }

        [Theory]
        [InlineData(Obstacle.Ahead)]
        [InlineData(Obstacle.Crossing)]
        [InlineData(Obstacle.Oncoming)]
        public void SomethingOtherThanATrainIsAnObstacle(Obstacle obstacle)
        {
            Assert.Equal(WaitReason.Obstacle, WaitClassifier.Classify(Standing(obstacle, isTrain: false)));
        }

        [Fact]
        public void ASignalStopsWhoeverItsBlockerIs()
        {
            Assert.Equal(WaitReason.Signal, WaitClassifier.Classify(Standing(Obstacle.Signal, isTrain: false)));
        }

        [Fact]
        public void APendingRouteExplainsTheStopBeforeWhateverIsAhead()
        {
            TrainObservation train = Standing(Obstacle.Ahead, isTrain: true);
            train.RoutePending = true;
            Assert.Equal(WaitReason.RoutePending, WaitClassifier.Classify(train));
        }

        [Fact]
        public void AHeldTrainWaitsAtTheSignalWhateverTheGameReportsAhead()
        {
            TrainObservation train = Standing(Obstacle.Ahead, isTrain: true);
            train.HeldByDispatcher = true;
            Assert.Equal(WaitReason.AtSignal, WaitClassifier.Classify(train));

            train.Boarding = true;
            Assert.Equal(WaitReason.Boarding, WaitClassifier.Classify(train));
        }

        [Theory]
        [InlineData(Obstacle.None)]
        [InlineData(Obstacle.Limit)]
        [InlineData(Obstacle.Other)]
        public void StandingWithoutAReportedCauseIsUnknown(Obstacle obstacle)
        {
            Assert.Equal(WaitReason.Unknown, WaitClassifier.Classify(Standing(obstacle, isTrain: true)));
        }
    }
}
