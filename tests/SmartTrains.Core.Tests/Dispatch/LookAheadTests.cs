using SmartTrains.Core.Dispatch;
using Xunit;

namespace SmartTrains.Core.Tests.Dispatch
{
    public class LookAheadTests
    {
        private const float kMaxSpeed = 50f;
        private const float kAcceleration = 1f;

        /// <summary>
        /// How far the game reserves ahead of a train's front bogie, as
        /// TrainNavigationSystem.TryReserveNavigationLanes does: braking
        /// distance plus a step's run, and signal distance plus the front
        /// overhang plus two metres. The overhang is taken as 15 m, more than
        /// any car has.
        /// </summary>
        private static float GameReserves(float speed, float braking)
        {
            const float step = 4f / 15f;
            return 0.5f * speed * speed / braking + speed * step + 4f * speed + 15f + 2f;
        }

        [Theory]
        [InlineData(0.5f)]
        [InlineData(1f)]
        [InlineData(3f)]
        public void TheGrantComesBeforeTheGameReservesInTheNextStep(float braking)
        {
            // At any speed: however far the game may reserve after one more
            // step of acceleration, the dispatcher has looked further.
            for (float speed = 0f; speed <= kMaxSpeed; speed += 0.5f)
            {
                float next = System.Math.Min(kMaxSpeed, speed + kAcceleration * LookAhead.StepSeconds);
                Assert.True(LookAhead.For(speed, kMaxSpeed, kAcceleration, braking) >= GameReserves(next, braking),
                    $"at {speed} m/s");
            }
        }

        [Fact]
        public void ASlowTrainLooksMuchLessFarAhead()
        {
            // A train crawling up to a junction does not need it granted from
            // the braking distance at top speed.
            float slow = LookAhead.For(5f, kMaxSpeed, kAcceleration, 1f);
            float fast = LookAhead.For(kMaxSpeed, kMaxSpeed, kAcceleration, 1f);
            Assert.True(slow < fast / 5f, $"{slow} m against {fast} m");
        }
    }
}
