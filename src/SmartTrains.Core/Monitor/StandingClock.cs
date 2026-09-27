using System.Collections.Generic;

namespace SmartTrains.Core.Monitor
{
    /// <summary>
    /// Remembers since when each train has been standing.
    ///
    /// The game keeps no such time, so it is measured from the observations:
    /// a train counts as standing from the first observation that finds it
    /// standing. The result is therefore late by at most one observation
    /// interval.
    ///
    /// Times are simulation frames, which wrap around after 2^32; differences
    /// are taken in unsigned arithmetic, so a wrap in between does no harm.
    /// </summary>
    public sealed class StandingClock
    {
        private readonly Dictionary<long, uint> m_Since = new Dictionary<long, uint>();
        private readonly HashSet<long> m_Observed = new HashSet<long>();

        /// <summary>Number of trains currently standing.</summary>
        public int Standing => m_Since.Count;

        /// <summary>
        /// Records one observation of a train.
        /// </summary>
        /// <param name="train">Any stable key of the train, e.g. its entity index and version combined.</param>
        /// <param name="standing">Whether the train stands in this observation.</param>
        /// <param name="frame">Simulation frame of the observation.</param>
        /// <returns>Frames the train has stood so far, 0 if it moves or was first seen standing just now.</returns>
        public uint Observe(long train, bool standing, uint frame)
        {
            m_Observed.Add(train);
            if (!standing)
            {
                m_Since.Remove(train);
                return 0;
            }
            if (m_Since.TryGetValue(train, out uint since))
                return frame - since;
            m_Since[train] = frame;
            return 0;
        }

        /// <summary>
        /// Looks up since when a train stands, without recording an
        /// observation. For readers other than the regular observation round,
        /// e.g. when the game removes a train.
        /// </summary>
        /// <returns>Whether the train was standing at its last observation.</returns>
        public bool TryGetSince(long train, out uint since)
        {
            return m_Since.TryGetValue(train, out since);
        }

        /// <summary>
        /// Forgets the trains that were not observed since the last sweep,
        /// e.g. because the game removed them. Call once after each full round
        /// of observations; otherwise a train that reappears under a reused key
        /// would inherit an old standing time.
        /// </summary>
        public void Sweep()
        {
            if (m_Since.Count > 0)
            {
                var gone = new List<long>();
                foreach (long train in m_Since.Keys)
                {
                    if (!m_Observed.Contains(train))
                        gone.Add(train);
                }
                foreach (long train in gone)
                    m_Since.Remove(train);
            }
            m_Observed.Clear();
        }
    }
}
