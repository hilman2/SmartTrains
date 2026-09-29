namespace SmartTrains.Core.Dispatch
{
    /// <summary>What a train is to the city, as far as its rank goes.</summary>
    public enum TrainKind
    {
        /// <summary>On a passenger line.</summary>
        Passenger,

        /// <summary>On a cargo line.</summary>
        Cargo,

        /// <summary>Passengers between two outside connections, through the city.</summary>
        ThroughPassenger,

        ThroughCargo,

        /// <summary>On its way back to its depot, empty.</summary>
        Returning,
    }

    /// <summary>
    /// The rank a train starts from, before waiting adds to it
    /// (Dispatcher.RankPerMinute). Passengers first, then cargo on the
    /// city's lines, then through traffic, which only crosses the city.
    /// On top, a line train gains with how full it is: the more it carries,
    /// the more is held up while it waits. A full cargo train goes before an
    /// empty passenger train, and a full train has the lead of 20 minutes'
    /// waiting over an empty one of its kind.
    /// </summary>
    public static class BaseRank
    {
        public const float Passenger = 20f;
        public const float Cargo = 12f;
        public const float ThroughPassenger = 10f;
        public const float ThroughCargo = 6f;
        public const float Returning = 4f;

        /// <summary>Rank a full line train gains over an empty one.</summary>
        public const float Full = 40f;

        /// <param name="fill">
        /// Passengers or cargo on board as a share of what the train can
        /// take; counted from 0 to 1, and as 0 if not a number.
        /// </param>
        public static float Of(TrainKind kind, float fill)
        {
            switch (kind)
            {
                case TrainKind.Passenger:
                    return Passenger + Full * Clamp(fill);
                case TrainKind.Cargo:
                    return Cargo + Full * Clamp(fill);
                case TrainKind.ThroughPassenger:
                    return ThroughPassenger;
                case TrainKind.ThroughCargo:
                    return ThroughCargo;
                default:
                    return Returning;
            }
        }

        private static float Clamp(float fill)
        {
            if (float.IsNaN(fill) || fill < 0f)
                return 0f;
            return fill > 1f ? 1f : fill;
        }
    }
}
