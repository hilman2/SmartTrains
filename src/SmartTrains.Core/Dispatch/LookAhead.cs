using System;

namespace SmartTrains.Core.Dispatch
{
    /// <summary>
    /// How far ahead a train needs track granted: a little beyond how far
    /// the game reserves track for it (TrainNavigationSystem.
    /// TryReserveNavigationLanes). The game does not reserve past a hold, so a
    /// grant that comes later than that makes the train brake for nothing.
    /// A grant much earlier keeps junctions from other trains while the
    /// train is still far off.
    /// </summary>
    public static class LookAhead
    {
        /// <summary>Seconds of train movement per navigation step.</summary>
        public const float StepSeconds = 4f / 15f;

        /// <summary>
        /// Seconds of full acceleration allowed for: by the time the game
        /// reserves again, the train may be that much faster.
        /// </summary>
        public const float HeadroomSeconds = 2f;

        /// <summary>
        /// Metres beyond what the game reserves: its reservation starts a few
        /// metres ahead of the front bogie, at the front of the car, and
        /// ends two metres further.
        /// </summary>
        public const float Margin = 50f;

        /// <summary>
        /// Metres ahead of a train's front up to which it needs track granted
        /// now. Speeds are in metres per second; acceleration and braking in
        /// metres per second squared, of the weakest car.
        /// </summary>
        public static float For(float speed, float maxSpeed, float acceleration, float braking)
        {
            float soon = Math.Max(speed, Math.Min(maxSpeed, speed + acceleration * HeadroomSeconds));
            return Reserved(soon, braking) + Margin;
        }

        /// <summary>
        /// Metres the game reserves ahead at <paramref name="speed"/>: the
        /// braking distance plus one step's run (VehicleUtils.
        /// GetBrakingDistance), and four seconds' run as signal distance
        /// (VehicleUtils.GetSignalDistance).
        /// </summary>
        private static float Reserved(float speed, float braking)
        {
            return 0.5f * speed * speed / braking + speed * StepSeconds + 4f * speed;
        }
    }
}
