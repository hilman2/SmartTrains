using SmartTrains.Core.Monitor;
using Xunit;

namespace SmartTrains.Core.Tests.Monitor
{
    public class StandingClockTests
    {
        [Fact]
        public void CountsFromTheFirstStandingObservation()
        {
            var clock = new StandingClock();
            Assert.Equal(0u, clock.Observe(1, standing: false, frame: 100));
            Assert.Equal(0u, clock.Observe(1, standing: true, frame: 200));
            Assert.Equal(300u, clock.Observe(1, standing: true, frame: 500));
        }

        [Fact]
        public void MovingInBetweenStartsTheCountAgain()
        {
            var clock = new StandingClock();
            clock.Observe(1, standing: true, frame: 0);
            clock.Observe(1, standing: false, frame: 1000);
            clock.Observe(1, standing: true, frame: 2000);
            Assert.Equal(50u, clock.Observe(1, standing: true, frame: 2050));
        }

        [Fact]
        public void SurvivesTheFrameCounterWrappingAround()
        {
            var clock = new StandingClock();
            clock.Observe(1, standing: true, frame: uint.MaxValue - 9);
            Assert.Equal(30u, clock.Observe(1, standing: true, frame: 20));
        }

        [Fact]
        public void KeepsTrainsObservedSinceTheLastSweep()
        {
            var clock = new StandingClock();
            clock.Observe(1, standing: true, frame: 0);
            clock.Sweep();
            clock.Observe(1, standing: true, frame: 100);
            clock.Sweep();
            Assert.Equal(200u, clock.Observe(1, standing: true, frame: 200));
        }

        [Fact]
        public void ForgetsATrainMissingFromARound()
        {
            // The game reuses entity indices. A new train under the key of a
            // removed one must not inherit the removed train's standing time.
            var clock = new StandingClock();
            clock.Observe(1, standing: true, frame: 0);
            clock.Observe(2, standing: true, frame: 0);
            clock.Sweep();
            clock.Observe(2, standing: true, frame: 100);
            clock.Sweep();

            Assert.Equal(1, clock.Standing);
            Assert.Equal(0u, clock.Observe(1, standing: true, frame: 5000));
        }
    }
}
