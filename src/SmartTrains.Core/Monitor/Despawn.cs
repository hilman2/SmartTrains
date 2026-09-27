using System;
using System.Collections.Generic;

namespace SmartTrains.Core.Monitor
{
    /// <summary>Why the game removed a train.</summary>
    public enum DespawnCause
    {
        /// <summary>Trains blocked each other in a circle; the game's deadlock detection gave up on them.</summary>
        Deadlock,

        /// <summary>The train found no route, and was on no line it could return to.</summary>
        NoRoute,

        /// <summary>The place the train was heading for no longer exists.</summary>
        TargetGone,

        /// <summary>Through traffic that reached its outside connection. Normal.</summary>
        Arrived,

        /// <summary>A train that returned to its depot and was taken off the map. Normal.</summary>
        Depot,

        /// <summary>None of the above, e.g. the player removed its depot or the track under it.</summary>
        Other,
    }

    /// <summary>What the game layer read about a train in the frame the game removed it.</summary>
    public struct DespawnObservation
    {
        /// <summary>The game's deadlock detection had marked the train.</summary>
        public bool Stuck;

        /// <summary>The train's last route request failed.</summary>
        public bool PathFailed;

        /// <summary>The train's target still exists.</summary>
        public bool TargetExists;

        /// <summary>Through traffic between outside connections.</summary>
        public bool Through;

        public bool Returning;

        /// <summary>The train had reached the end of its route.</summary>
        public bool EndReached;
    }

    public static class DespawnClassifier
    {
        public static DespawnCause Classify(DespawnObservation train)
        {
            // The game deletes a deadlocked train through the same branch as
            // one without a route (TransportTrainAISystem), and a stuck train
            // also counts as failed there. Stuck is the more specific cause.
            if (train.Stuck)
                return DespawnCause.Deadlock;
            if (!train.TargetExists)
                return DespawnCause.TargetGone;
            if (train.PathFailed)
                return DespawnCause.NoRoute;
            // Through traffic leaves the map over connection lanes, where the
            // train's front no longer carries EndReached. Without a failed
            // route or a deadlock, its removal is its arrival.
            if (train.Through)
                return DespawnCause.Arrived;
            if (train.EndReached && train.Returning)
                return DespawnCause.Depot;
            return DespawnCause.Other;
        }

        /// <summary>Whether the removal belongs to the normal course of the game and needs no attention.</summary>
        public static bool IsNormal(DespawnCause cause)
        {
            return cause == DespawnCause.Arrived || cause == DespawnCause.Depot;
        }
    }

    /// <summary>A chain of trains, each waiting for the next.</summary>
    public struct BlockerChain
    {
        /// <summary>The trains the first one waits for, in order; the first one itself is not included.</summary>
        public List<long> Trains;

        /// <summary>The last train of the chain waits for the first one again.</summary>
        public bool BackToStart;
    }

    public static class BlockerChains
    {
        /// <summary>
        /// Follows who waits for whom, starting at <paramref name="start"/>.
        /// </summary>
        /// <param name="start">Key of the train to start from.</param>
        /// <param name="blockerOf">
        /// Called as <c>blockerOf(train)</c>. Returns the key of the train that
        /// <c>train</c> waits for, or 0 if it waits for no train.
        /// </param>
        /// <param name="maxLength">The walk stops after this many trains.</param>
        /// <returns>
        /// The trains in the order they wait for each other. The walk ends at a
        /// train that waits for nobody, at the start again, or at a train
        /// already in the chain, i.e. a circle that does not include the start.
        /// </returns>
        public static BlockerChain Follow(long start, Func<long, long> blockerOf, int maxLength = 32)
        {
            var chain = new BlockerChain { Trains = new List<long>() };
            var seen = new HashSet<long> { start };
            long train = start;
            while (chain.Trains.Count < maxLength)
            {
                long next = blockerOf(train);
                if (next == 0)
                    break;
                if (next == start)
                {
                    chain.BackToStart = true;
                    break;
                }
                if (!seen.Add(next))
                    break;
                chain.Trains.Add(next);
                train = next;
            }
            return chain;
        }
    }
}
