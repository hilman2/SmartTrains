using System;
using System.Collections.Generic;

namespace SmartTrains.Core.Metrics
{
    /// <summary>One train as observed in one tick.</summary>
    /// <typeparam name="T">What is recorded about an interval besides its times; see <see cref="Details"/>.</typeparam>
    public struct StateSample<T>
    {
        public long Train;

        /// <summary>
        /// What the train does, as far as it splits intervals: a sample with
        /// another key than the one before ends the train's interval and
        /// begins a new one.
        /// </summary>
        public string Key;

        /// <summary>Metres the train has run, from the game's odometer.</summary>
        public float Odometer;

        /// <summary>
        /// Recorded as it is in the sample that begins an interval; what
        /// later samples of the same interval carry is not looked at. So it
        /// may hold what changes while the train does the same thing, e.g.
        /// where it is.
        /// </summary>
        public T Details;
    }

    /// <summary>A stretch of time during which a train did one thing.</summary>
    public sealed class StateInterval<T>
    {
        public long Train;
        public string Key;

        /// <summary>Simulation frame of the first sample with <see cref="Key"/>.</summary>
        public uint From;

        /// <summary>
        /// Simulation frame of the first sample with another key, so that a
        /// train's intervals follow on without gaps; for a train that is gone,
        /// or at the end of a session, the frame it was last seen in.
        /// </summary>
        public uint To;

        /// <summary>
        /// Metres run from <see cref="From"/> to <see cref="To"/>. 0 where the
        /// odometer went back, which the game does when a train refuels at a
        /// stop.
        /// </summary>
        public float Distance;

        /// <summary>The train was not seen any more, or the session ended.</summary>
        public bool Cut;

        public T Details;
    }

    /// <summary>
    /// Turns what trains do tick by tick into intervals: one record per
    /// stretch of doing the same thing, written when it ends. Totals then
    /// come from adding up durations, and the records stay few however
    /// often trains are observed.
    /// </summary>
    public sealed class IntervalTracker<T>
    {
        private sealed class Open
        {
            public string Key;
            public uint From;
            public uint LastSeen;
            public float StartOdometer;
            public float LastOdometer;
            public T Details;
        }

        private readonly Dictionary<long, Open> m_Open = new Dictionary<long, Open>();

        /// <summary>Trains with an interval under way.</summary>
        public int Count => m_Open.Count;

        /// <summary>
        /// Takes in one tick. Returns the intervals it ends: those whose train
        /// now does something else, and those of trains not in
        /// <paramref name="samples"/>, which are gone.
        /// </summary>
        public List<StateInterval<T>> Observe(uint frame, IEnumerable<StateSample<T>> samples)
        {
            var ended = new List<StateInterval<T>>();
            var seen = new HashSet<long>();
            foreach (StateSample<T> sample in samples)
            {
                seen.Add(sample.Train);
                if (m_Open.TryGetValue(sample.Train, out Open open))
                {
                    if (open.Key == sample.Key)
                    {
                        open.LastSeen = frame;
                        open.LastOdometer = sample.Odometer;
                        continue;
                    }
                    ended.Add(Close(sample.Train, open, frame, sample.Odometer, cut: false));
                }
                m_Open[sample.Train] = new Open
                {
                    Key = sample.Key,
                    From = frame,
                    LastSeen = frame,
                    StartOdometer = sample.Odometer,
                    LastOdometer = sample.Odometer,
                    Details = sample.Details,
                };
            }

            var gone = new List<long>();
            foreach (KeyValuePair<long, Open> entry in m_Open)
            {
                if (!seen.Contains(entry.Key))
                    gone.Add(entry.Key);
            }
            foreach (long train in gone)
            {
                Open open = m_Open[train];
                ended.Add(Close(train, open, open.LastSeen, open.LastOdometer, cut: true));
                m_Open.Remove(train);
            }
            return ended;
        }

        /// <summary>Ends every interval where its train was last seen, e.g. when the session ends.</summary>
        public List<StateInterval<T>> CloseAll()
        {
            var ended = new List<StateInterval<T>>();
            foreach (KeyValuePair<long, Open> entry in m_Open)
                ended.Add(Close(entry.Key, entry.Value, entry.Value.LastSeen, entry.Value.LastOdometer, cut: true));
            m_Open.Clear();
            return ended;
        }

        private static StateInterval<T> Close(long train, Open open, uint to, float odometer, bool cut)
        {
            return new StateInterval<T>
            {
                Train = train,
                Key = open.Key,
                From = open.From,
                To = to,
                Distance = Math.Max(0f, odometer - open.StartOdometer),
                Cut = cut,
                Details = open.Details,
            };
        }
    }
}
