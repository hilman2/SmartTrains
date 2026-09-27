namespace SmartTrains.Core.Monitor
{
    /// <summary>
    /// What stands in the way of a train, as the game's navigation reports it
    /// for the train's next stretch of track. Mirrors the game's BlockerType,
    /// which the core cannot reference; the game layer translates.
    /// </summary>
    public enum Obstacle
    {
        None,

        /// <summary>
        /// Something on the train's own way ahead: a train in front, or track
        /// ahead that another train has reserved.
        /// </summary>
        Ahead,

        /// <summary>Something whose way crosses this train's, at a switch or crossing.</summary>
        Crossing,

        /// <summary>Something coming the other way.</summary>
        Oncoming,

        /// <summary>A stop signal.</summary>
        Signal,

        /// <summary>Only the speed limit or the train's own braking; nothing in the way.</summary>
        Limit,

        Other,
    }

    /// <summary>Why a train does not move. The panel shows one reason per train.</summary>
    public enum WaitReason
    {
        /// <summary>The train moves.</summary>
        None,

        /// <summary>At a platform before the departure time: passengers board, or cargo is loaded.</summary>
        Boarding,

        /// <summary>At a platform after the departure time, waiting for passengers still walking to the train.</summary>
        LatePassengers,

        /// <summary>A train ahead is in the way, or holds the track ahead.</summary>
        TrainAhead,

        /// <summary>A train crosses the way at a switch or crossing.</summary>
        CrossingTrain,

        /// <summary>A train comes the other way.</summary>
        OncomingTrain,

        Signal,

        /// <summary>Something other than a train is in the way, e.g. a car on a level crossing.</summary>
        Obstacle,

        /// <summary>The game computes a new route for the train.</summary>
        RoutePending,

        /// <summary>The game has found the train in a deadlock and removes it.</summary>
        Deadlock,

        /// <summary>Standing, and the game reports no cause.</summary>
        Unknown,
    }

    /// <summary>What the game layer read about one train, reduced to what decides its wait reason.</summary>
    public struct TrainObservation
    {
        public bool Moving;

        /// <summary>The train stands at a platform and boards or loads.</summary>
        public bool Boarding;

        /// <summary>The train's departure time has come; only meaningful while <see cref="Boarding"/>.</summary>
        public bool DepartureDue;

        public Obstacle Obstacle;

        /// <summary>The obstacle is part of another train.</summary>
        public bool ObstacleIsTrain;

        public bool RoutePending;

        /// <summary>The game's deadlock detection has marked the train.</summary>
        public bool Stuck;
    }

    public static class WaitClassifier
    {
        /// <summary>Returns the one reason the panel shows for the train.</summary>
        public static WaitReason Classify(TrainObservation train)
        {
            // A deadlocked train is removed by the game at its next update,
            // whatever else is true about it. That is the one thing the player
            // must see.
            if (train.Stuck)
                return WaitReason.Deadlock;

            // At a platform the navigation still reports whatever lies ahead,
            // usually the red end of the reserved track. The train stands
            // because it boards, not because of that.
            if (train.Boarding)
                return train.DepartureDue ? WaitReason.LatePassengers : WaitReason.Boarding;

            if (train.Moving)
                return WaitReason.None;

            // While a new route is computed the game clears the train's
            // planned track, and the train brakes for the end of it.
            if (train.RoutePending)
                return WaitReason.RoutePending;

            switch (train.Obstacle)
            {
                case Obstacle.Ahead:
                    return train.ObstacleIsTrain ? WaitReason.TrainAhead : WaitReason.Obstacle;
                case Obstacle.Crossing:
                    return train.ObstacleIsTrain ? WaitReason.CrossingTrain : WaitReason.Obstacle;
                case Obstacle.Oncoming:
                    return train.ObstacleIsTrain ? WaitReason.OncomingTrain : WaitReason.Obstacle;
                case Obstacle.Signal:
                    return WaitReason.Signal;
                default:
                    return WaitReason.Unknown;
            }
        }
    }
}
