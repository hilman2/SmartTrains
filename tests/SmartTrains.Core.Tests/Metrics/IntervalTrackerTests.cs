using System.Collections.Generic;
using SmartTrains.Core.Metrics;
using Xunit;

namespace SmartTrains.Core.Tests.Metrics
{
    public class IntervalTrackerTests
    {
        private static StateSample<string> S(long train, string key, float odometer, string details = "")
        {
            return new StateSample<string> { Train = train, Key = key, Odometer = odometer, Details = details };
        }

        [Fact]
        public void AnIntervalEndsWhenTheTrainDoesSomethingElse()
        {
            var tracker = new IntervalTracker<string>();
            Assert.Empty(tracker.Observe(100, new[] { S(1, "Running", 1000f) }));
            Assert.Empty(tracker.Observe(116, new[] { S(1, "Running", 1100f) }));

            StateInterval<string> run = Assert.Single(tracker.Observe(132, new[] { S(1, "TrainAhead", 1150f) }));
            Assert.Equal("Running", run.Key);
            Assert.Equal(100u, run.From);
            Assert.Equal(132u, run.To);
            Assert.Equal(150f, run.Distance);
            Assert.False(run.Cut);
        }

        [Fact]
        public void DetailsAreThoseOfTheSampleThatBeganTheInterval()
        {
            // Where a standing train stands is recorded when it stops; the
            // blocker it reports later changes nothing about the record.
            var tracker = new IntervalTracker<string>();
            tracker.Observe(100, new[] { S(1, "TrainAhead", 0f, "by #7") });
            tracker.Observe(116, new[] { S(1, "TrainAhead", 0f, "by #8") });
            StateInterval<string> stand = Assert.Single(tracker.Observe(132, new[] { S(1, "Running", 0f, "") }));
            Assert.Equal("by #7", stand.Details);
        }

        [Fact]
        public void AGoneTrainsIntervalEndsWhereItWasLastSeen()
        {
            var tracker = new IntervalTracker<string>();
            tracker.Observe(100, new[] { S(1, "Running", 0f), S(2, "Running", 0f) });
            tracker.Observe(116, new[] { S(1, "Running", 40f), S(2, "Running", 0f) });

            StateInterval<string> gone = Assert.Single(tracker.Observe(132, new[] { S(2, "Running", 0f) }));
            Assert.Equal(1, gone.Train);
            Assert.Equal(116u, gone.To);
            Assert.Equal(40f, gone.Distance);
            Assert.True(gone.Cut);
            Assert.Equal(1, tracker.Count);
        }

        [Fact]
        public void CloseAllEndsEveryIntervalAndForgetsTheTrains()
        {
            var tracker = new IntervalTracker<string>();
            tracker.Observe(100, new[] { S(1, "Running", 0f), S(2, "Boarding", 0f) });
            tracker.Observe(116, new[] { S(1, "Running", 20f), S(2, "Boarding", 0f) });

            List<StateInterval<string>> ended = tracker.CloseAll();
            Assert.Equal(2, ended.Count);
            Assert.All(ended, i => Assert.Equal(116u, i.To));
            Assert.All(ended, i => Assert.True(i.Cut));
            Assert.Equal(0, tracker.Count);
            // A train seen again starts afresh, not where it was.
            Assert.Empty(tracker.Observe(500, new[] { S(1, "Running", 20f) }));
            Assert.Equal(500u, Assert.Single(tracker.CloseAll()).From);
        }

        [Fact]
        public void AnOdometerThatWentBackCountsNoDistance()
        {
            // The game resets the odometer when a train refuels at a stop.
            var tracker = new IntervalTracker<string>();
            tracker.Observe(100, new[] { S(1, "Boarding", 5000f) });
            StateInterval<string> boarding = Assert.Single(tracker.Observe(116, new[] { S(1, "Running", 0f) }));
            Assert.Equal(0f, boarding.Distance);
        }
    }
}
