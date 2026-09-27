using System.Collections.Generic;

namespace SmartTrains.Core.Dispatch
{
    /// <summary>
    /// Finds trains that wait for each other in a circle.
    ///
    /// Each train waits for at most one other: the one the dispatcher holds
    /// it for, or, if it is not held, the one the game reports in its way.
    /// A circle among such waits never resolves by itself. The game's own
    /// deadlock detection sees only the second kind, so a circle that runs
    /// through a dispatcher's hold would stand for good; the dispatcher has
    /// to find and break it itself.
    /// </summary>
    public static class WaitCycles
    {
        /// <param name="waitsFor">For each train key, the key of the train it waits for. Trains that wait for nobody are left out.</param>
        /// <returns>Every circle once, as the trains in the order they wait for each other.</returns>
        public static List<List<long>> Find(IReadOnlyDictionary<long, long> waitsFor)
        {
            var cycles = new List<List<long>>();
            // 1: on the walk being followed now; 2: done, part of no new circle.
            var state = new Dictionary<long, int>();
            foreach (long start in waitsFor.Keys)
            {
                if (state.ContainsKey(start))
                    continue;
                var walk = new List<long>();
                long train = start;
                while (true)
                {
                    if (state.TryGetValue(train, out int seen))
                    {
                        if (seen == 1)
                        {
                            // The walk has come back to a train on itself:
                            // from there on, the walk is a circle.
                            int from = walk.IndexOf(train);
                            cycles.Add(walk.GetRange(from, walk.Count - from));
                        }
                        break;
                    }
                    state[train] = 1;
                    walk.Add(train);
                    if (!waitsFor.TryGetValue(train, out long next))
                        break;
                    train = next;
                }
                foreach (long done in walk)
                    state[done] = 2;
            }
            return cycles;
        }
    }
}
