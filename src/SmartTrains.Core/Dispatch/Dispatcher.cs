using System;
using System.Collections.Generic;
using SmartTrains.Core.Network;

namespace SmartTrains.Core.Dispatch
{
    /// <summary>One train as the dispatcher sees it in one round.</summary>
    public sealed class TrainInput
    {
        /// <summary>The game layer's key of the train.</summary>
        public long Id;

        /// <summary>Rank before waiting is counted, e.g. higher for passenger trains.</summary>
        public float BasePriority;

        /// <summary>In-game minutes the train has been standing; raises its rank.</summary>
        public float WaitingMinutes;

        /// <summary>Metres from the front of the first car to the back of the last.</summary>
        public float Length;

        /// <summary>
        /// Metres ahead of its front the train needs cleared to keep running
        /// at speed: its braking distance plus what the game's own
        /// reservation looks ahead. Closer than that, the dispatcher can no
        /// longer stop it in front of the next section.
        /// </summary>
        public float LookAhead;

        /// <summary>
        /// The lanes ahead, as the train will run them: the lane under its
        /// front first, then the rest of its route to where it next stops or
        /// leaves the network. Lanes not in the network end the route.
        /// </summary>
        public List<Move> Route = new List<Move>();

        /// <summary>Metres of <c>Route[0]</c> still ahead of the front.</summary>
        public float FrontRemaining;

        /// <summary>
        /// Index into <see cref="Route"/> up to which the game has already
        /// reserved track for the train. Those lanes are the train's no
        /// matter what; 0 if only the lane under its front.
        /// </summary>
        public int Committed;

        /// <summary>Lanes under the train's cars, each the way the train runs it.</summary>
        public List<Move> Occupied = new List<Move>();

        /// <summary>Metres per second.</summary>
        public float Speed;

        /// <summary>Seconds until the train leaves the platform it boards at; -1 if it is not boarding.</summary>
        public float DepartureIn = -1f;

        /// <summary>
        /// Whether the dispatcher may send the train over another track of a
        /// passing loop than the one its route takes.
        /// </summary>
        public bool MayChangeTrack = true;
    }

    public enum HoldReason
    {
        None,

        /// <summary>Track ahead is held by another train.</summary>
        TrackHeld,

        /// <summary>The next safe place to wait is full.</summary>
        NoRoomAhead,

        /// <summary>Another train with a higher rank has claimed the single track ahead.</summary>
        GivingWay,
    }

    /// <summary>Replaces part of a train's route: <c>Route[From..To]</c>, both inclusive, by <see cref="Moves"/>.</summary>
    public sealed class RouteChange
    {
        public int From;
        public int To;
        public List<Move> Moves = new List<Move>();
    }

    /// <summary>What the dispatcher tells one train in one round.</summary>
    public sealed class TrainOrder
    {
        public long Train;

        /// <summary>
        /// Index into the train's route, after any <see cref="Change"/>, of
        /// the first lane it must not enter; -1 if it may run to the end.
        /// </summary>
        public int HoldAt = -1;

        public HoldReason Reason;

        /// <summary>The train it waits for, 0 if none is known.</summary>
        public long WaitingFor;

        /// <summary>Index into the route of the lane that was not free, -1 if none; for diagnosis.</summary>
        public int BlockedAt = -1;

        /// <summary>Index into the route of the last lane granted to the train.</summary>
        public int GrantedEnd;

        /// <summary>A new way through a passing loop, or null.</summary>
        public RouteChange Change;

        /// <summary>The rank the train was dispatched with, for the panel.</summary>
        public float Rank;

        /// <summary>
        /// The grant the train had from the last round was cut back in this
        /// one, because another train now stands in its way.
        /// </summary>
        public bool CutBack;

        /// <summary>
        /// The train the dispatcher knows to be ahead on the track granted to
        /// this one, which it may close up to; 0 if none. A train stopped
        /// behind it waits as planned, not because the dispatcher misjudged
        /// the track.
        /// </summary>
        public long Ahead;

        /// <summary>
        /// Metres per second the train should slow to, so that it reaches
        /// the point where it would stop, its hold or the rear of a standing
        /// train ahead, about when the way on frees; 0 for no advice. Holds
        /// stay: if the way frees later, the train still stops.
        /// </summary>
        public float SpeedAdvice;
    }

    /// <summary>
    /// The central dispatcher. Once per round it grants each train track
    /// ahead, in order of rank, and tells every train where to stop.
    ///
    /// A train is granted track only up to a safe place to wait, and only
    /// all of it or nothing: every junction area on the way, every stretch
    /// of single track, and room in the section it will wait in. So no train
    /// ever waits inside a junction, and none waits where it blocks the only
    /// way of a train it is waiting for. That is what keeps trains from
    /// locking each other up, the way path signals do on real railways.
    ///
    /// Grants carry over from round to round until the train has passed the
    /// track. Only where another train now stands in the way is a grant cut
    /// back, to the last place before it where the train can wait, and never
    /// below what the game has reserved for the train: it may already be too
    /// close to stop.
    /// </summary>
    public sealed class Dispatcher
    {
        /// <summary>Rank gained per in-game minute of standing.</summary>
        public const float RankPerMinute = 2f;

        /// <summary>
        /// In-game minutes of standing after which a refused train claims all
        /// the track it needs; see ClaimPriority. Shorter waits are what a
        /// busy junction costs, and claiming for them would stop trains that
        /// could pass.
        /// </summary>
        public const float PriorityAfterMinutes = 15f;

        /// <summary>Room a waiting train leaves to the junction behind it and to the train ahead, metres.</summary>
        private const float kMargin = 15f;

        private readonly TrackLayout m_Layout;
        private readonly TrackNetwork m_Network;

        /// <summary>The last lane granted to each train, as it appeared in its route.</summary>
        private readonly Dictionary<long, Move> m_GrantedEnd = new Dictionary<long, Move>();

        /// <summary>Per lane in a section, where in its section the lane starts, in metres along the section's own direction.</summary>
        private readonly float[] m_LaneOffset;

        public Dispatcher(TrackLayout layout)
        {
            m_Layout = layout;
            m_Network = layout.Network;
            m_LaneOffset = new float[m_Network.Lanes.Count];
            foreach (Section section in layout.Sections)
            {
                float offset = 0f;
                foreach (Move move in section.Moves)
                {
                    m_LaneOffset[move.Lane] = offset;
                    offset += m_Network.Lanes[move.Lane].Length;
                }
            }
        }

        /// <summary>A train in a section, or granted it.</summary>
        private sealed class SectionUser
        {
            public long Train;

            /// <summary>It runs the section in the section's own direction.</summary>
            public bool Forward;

            public float Length;

            /// <summary>It is in the section, not only granted it.</summary>
            public bool Occupies;

            /// <summary>
            /// Its grant goes on beyond the section: it will run out of it,
            /// and makes room for a train behind as it goes.
            /// </summary>
            public bool Leaving;

            /// <summary>
            /// How far its front has come through the section, in metres and
            /// in its own direction; positive infinity if its front has left
            /// the section and only its rear is still in it, negative infinity
            /// if it is not in the section.
            /// </summary>
            public float Along;
        }

        /// <summary>Holders of track during one round.</summary>
        private sealed class Holdings
        {
            /// <summary>Junction lanes and the train holding each.</summary>
            public readonly Dictionary<int, long> Lanes = new Dictionary<int, long>();

            /// <summary>Junction lanes a train is on, not only granted, and that train.</summary>
            public readonly Dictionary<int, long> OccupiedLanes = new Dictionary<int, long>();

            /// <summary>Per section, the trains in it or granted into it, their direction and length.</summary>
            public readonly Dictionary<int, List<SectionUser>> Sections = new Dictionary<int, List<SectionUser>>();

            /// <summary>Single-track sections claimed for one direction by a waiting train of higher rank.</summary>
            public readonly Dictionary<int, (long Train, bool Forward)> Claims = new Dictionary<int, (long, bool)>();

            /// <summary>Junction lanes claimed by a train that has waited long; see ClaimPriority.</summary>
            public readonly Dictionary<int, long> PriorityLanes = new Dictionary<int, long>();

            /// <summary>Sections claimed by a train that has waited long; see ClaimPriority.</summary>
            public readonly Dictionary<int, long> PrioritySections = new Dictionary<int, long>();

            /// <summary>Per refused train, the sections and junction lanes of the way it was refused; see Inside.</summary>
            public readonly Dictionary<long, (HashSet<int> Sections, HashSet<int> Lanes)> Refused = new Dictionary<long, (HashSet<int>, HashSet<int>)>();
        }

        /// <summary>Dispatches one round.</summary>
        /// <returns>One order per train, in the order of <paramref name="trains"/>.</returns>
        public List<TrainOrder> Dispatch(IList<TrainInput> trains)
        {
            var holdings = new Holdings();
            var granted = new Dictionary<long, int>();
            var routes = new Dictionary<long, List<Move>>();
            var cutBack = new HashSet<long>();

            m_Fronts.Clear();
            foreach (TrainInput train in trains)
                m_Fronts[train.Id] = Front(train);
            // Where the trains are comes first, for all of them: a section a
            // train is in counts as occupied even if it is also granted to it,
            // and the grants below are checked against where trains stand.
            foreach (TrainInput train in trains)
            {
                foreach (Move move in train.Occupied)
                    Hold(holdings, train, move.Lane, move.Forward, occupies: true);
            }
            foreach (TrainInput train in trains)
            {
                routes[train.Id] = train.Route;
                int end = Math.Min(Math.Max(train.Committed, GrantedIndex(train)), train.Route.Count - 1);
                // A grant from an earlier round is cut back where another
                // train now stands in the way, e.g. one the game drove there
                // on its own. The granted train cannot get past it, and the
                // rest of the grant would only keep others from the track.
                // What the game has reserved stays: the train may be too
                // close to stop.
                if (end > train.Committed)
                {
                    int reach = Unobstructed(train, train.Route, end, holdings);
                    if (reach < end)
                    {
                        end = WaitingPlaceBefore(train, train.Route, reach);
                        cutBack.Add(train.Id);
                    }
                }
                granted[train.Id] = end;
                Grant(train, train.Route, 0, end, holdings);
            }

            var orders = new Dictionary<long, TrainOrder>();
            var ranked = new List<TrainInput>(trains);
            ranked.Sort((a, b) =>
            {
                int byRank = Rank(b).CompareTo(Rank(a));
                return byRank != 0 ? byRank : a.Id.CompareTo(b.Id);
            });
            foreach (TrainInput train in ranked)
            {
                var order = new TrainOrder { Train = train.Id, Rank = Rank(train), CutBack = cutBack.Contains(train.Id) };
                orders[train.Id] = order;
                if (train.Route.Count == 0)
                    continue;
                List<Move> route = routes[train.Id];
                int end = granted[train.Id];
                while (end < route.Count - 1 && Distance(train, route, end) < train.LookAhead)
                {
                    if (!TryExtend(train, ref route, ref end, holdings, order))
                        break;
                }
                routes[train.Id] = route;
                granted[train.Id] = end;
                order.GrantedEnd = end;
                order.HoldAt = end < route.Count - 1 ? end + 1 : -1;
                if (order.HoldAt < 0)
                {
                    order.Reason = HoldReason.None;
                    order.WaitingFor = 0;
                    order.BlockedAt = -1;
                }
            }

            var context = new AdviceContext { Trains = new Dictionary<long, TrainInput>(), Orders = orders, Routes = routes };
            foreach (TrainInput train in trains)
            {
                context.Trains[train.Id] = train;
                orders[train.Id].Ahead = TrainAhead(train, routes[train.Id], granted[train.Id], holdings).Train;
            }
            foreach (TrainInput train in trains)
                orders[train.Id].SpeedAdvice = SpeedAdvice(train, orders[train.Id], context);

            m_GrantedEnd.Clear();
            var result = new List<TrainOrder>(trains.Count);
            foreach (TrainInput train in trains)
            {
                List<Move> route = routes[train.Id];
                if (route.Count > 0)
                    m_GrantedEnd[train.Id] = route[granted[train.Id]];
                result.Add(orders[train.Id]);
            }
            return result;
        }

        /// <summary>Slowest speed advised, metres per second; slower, a train barely moves and might as well stop.</summary>
        public const float MinAdvisedSpeed = 4f;

        /// <summary>Seconds a train standing at a platform needs after its departure time to clear the track ahead.</summary>
        private const float kStartUpSeconds = 20f;

        /// <summary>What one round knows about all trains, for the speed advice.</summary>
        private sealed class AdviceContext
        {
            public Dictionary<long, TrainInput> Trains;
            public Dictionary<long, TrainOrder> Orders;
            public Dictionary<long, List<Move>> Routes;

            /// <summary>Per standing train, seconds until it can move off, or null if unknown; see UntilMoving.</summary>
            public readonly Dictionary<long, float?> UntilMoving = new Dictionary<long, float?>();
        }

        /// <summary>
        /// How fast a train should run on, so that it reaches the point where
        /// it would stop about when the way on frees, and rolls on instead of
        /// stopping and starting again; 0 for none.
        ///
        /// The point where it would stop is the rear of a standing train ahead
        /// of it in its section, or else the lane the dispatcher holds it in
        /// front of. When the way frees is estimated from the train it waits
        /// for, and from the one that train waits for in turn (see
        /// UntilLaneFrees). If that is not known, the train stops as it
        /// otherwise would.
        ///
        /// A train on a junction lane does not slow down, nor one with a
        /// junction lane between it and the point where it would stop: it
        /// would stand in others' way longer. Anywhere else a train that rolls
        /// on clears the track sooner than one that stands and starts again.
        /// </summary>
        private float SpeedAdvice(TrainInput train, TrainOrder order, AdviceContext context)
        {
            if (train.Speed <= 0.1f)
                return 0f;
            foreach (Move move in train.Occupied)
            {
                if (m_Layout.SectionOf(move.Lane) < 0)
                    return 0f;
            }
            (float distance, float? seconds) = StopAhead(train, order, context, new HashSet<long> { train.Id }, slowing: true);
            if (seconds == null || seconds.Value <= 0.1f)
                return 0f;
            float advice = distance / seconds.Value;
            if (advice >= train.Speed)
                return 0f;
            return Math.Max(advice, MinAdvisedSpeed);
        }

        /// <summary>
        /// Where ahead the train would stop, in metres from its front, and in
        /// how many seconds the way on frees there; seconds is null if the
        /// train has no such point, or if when the way frees is not known.
        /// <paramref name="visiting"/> holds the trains whose wait is being
        /// worked out, so that a circle of trains waiting for each other ends
        /// in "not known". With <paramref name="slowing"/>, for a train that
        /// is to slow down, a hold with a junction lane before it counts as no
        /// such point; see SpeedAdvice.
        /// </summary>
        private (float Distance, float? Seconds) StopAhead(TrainInput train, TrainOrder order, AdviceContext context, HashSet<long> visiting, bool slowing)
        {
            // Behind a standing train in its own section, the train stops at
            // that train's rear, once that train has moved up to where it
            // stops itself, and may go on when that train moves off from there.
            if (order.Ahead != 0 && context.Trains.TryGetValue(order.Ahead, out TrainInput ahead) && ahead.Speed <= 1f
                && m_Fronts.TryGetValue(train.Id, out (int Section, float Along) own) && own.Section >= 0
                && m_Fronts.TryGetValue(ahead.Id, out (int Section, float Along) theirs) && theirs.Section == own.Section
                && theirs.Along > own.Along)
            {
                float gap = Math.Max(0f, theirs.Along - ahead.Length - kMargin - own.Along);
                (float further, float? free) = StandingStop(ahead, context, visiting);
                return (gap + further, free);
            }

            List<Move> route = context.Routes[train.Id];
            if (order.HoldAt < 1 || order.Reason == HoldReason.None || order.BlockedAt < 0 || order.BlockedAt >= route.Count)
                return (0f, null);
            for (int i = 1; slowing && i < order.HoldAt; i++)
            {
                if (m_Layout.SectionOf(route[i].Lane) < 0)
                    return (0f, null);
            }
            return (Distance(train, route, order.HoldAt - 1), UntilLaneFrees(order.WaitingFor, route[order.BlockedAt].Lane, context, visiting));
        }

        /// <summary>
        /// Seconds until the train <paramref name="other"/> has cleared
        /// <paramref name="lane"/>, or a lane crossing it; null if not known.
        /// A train running clears it after running to and past it; a
        /// standing one once it has moved off and got going.
        /// </summary>
        private float? UntilLaneFrees(long other, int lane, AdviceContext context, HashSet<long> visiting)
        {
            if (!context.Trains.TryGetValue(other, out TrainInput train))
                return null;
            if (train.Speed > 1f)
            {
                List<Move> route = context.Routes[other];
                int at = route.FindIndex(m => m.Lane == lane);
                float distance = (at >= 0 ? Distance(train, route, at) : 0f) + train.Length;
                return distance / train.Speed;
            }
            float? start = UntilMoving(other, context, visiting);
            return start + kStartUpSeconds;
        }

        /// <summary>
        /// Seconds until the train can move off: 0 if it is moving, or
        /// standing with track before it up to where it would stop; its
        /// departure time if it boards; otherwise, if the dispatcher holds it
        /// or it stands behind a standing train, until its own way on frees.
        /// Null if not known, e.g. for a train the game stops, or one in a
        /// circle of trains waiting for each other.
        /// </summary>
        private float? UntilMoving(long id, AdviceContext context, HashSet<long> visiting)
        {
            if (context.UntilMoving.TryGetValue(id, out float? known))
                return known;
            if (!context.Trains.TryGetValue(id, out TrainInput train))
                return null;
            float? seconds;
            if (train.Speed > 1f)
            {
                seconds = 0f;
            }
            else
            {
                (float distance, float? free) = StandingStop(train, context, visiting);
                seconds = free != null && distance > kMargin ? 0f : free;
            }
            context.UntilMoving[id] = seconds;
            return seconds;
        }

        /// <summary>
        /// For a standing train: how far it will move up before it stops
        /// again, and in how many seconds its way on frees there. A train
        /// that boards stays where it is until its departure time.
        /// </summary>
        private (float Distance, float? Seconds) StandingStop(TrainInput train, AdviceContext context, HashSet<long> visiting)
        {
            if (train.DepartureIn >= 0f)
                return (0f, train.DepartureIn);
            if (!visiting.Add(train.Id))
                return (0f, null);
            (float Distance, float? Seconds) stop = StopAhead(train, context.Orders[train.Id], context, visiting, slowing: false);
            visiting.Remove(train.Id);
            return stop;
        }

        public static float Rank(TrainInput train)
        {
            return train.BasePriority + RankPerMinute * train.WaitingMinutes;
        }

        /// <summary>
        /// Where the grant of the last round ends in this round's route, -1
        /// if it is not in it: the train is new, or its route has changed.
        /// </summary>
        private int GrantedIndex(TrainInput train)
        {
            if (!m_GrantedEnd.TryGetValue(train.Id, out Move end))
                return -1;
            return train.Route.IndexOf(end);
        }

        /// <summary>Metres from the train's front to the end of <c>route[end]</c>.</summary>
        private float Distance(TrainInput train, List<Move> route, int end)
        {
            float distance = train.FrontRemaining;
            for (int i = 1; i <= end; i++)
                distance += m_Network.Lanes[route[i].Lane].Length;
            return distance;
        }

        /// <summary>Whether a move runs its section in the section's own direction.</summary>
        private bool SectionForward(int section, Move move)
        {
            foreach (Move own in m_Layout.Sections[section].Moves)
            {
                if (own.Lane == move.Lane)
                    return own.Forward == move.Forward;
            }
            return true;
        }

        /// <summary>Each train's front section and how far into it the front is; see <see cref="Front"/>.</summary>
        private readonly Dictionary<long, (int Section, float Along)> m_Fronts = new Dictionary<long, (int, float)>();

        /// <summary>
        /// The section the train's front is in, and how far the front has
        /// come through it in the train's direction; section -1 if the front
        /// is in a junction area.
        /// </summary>
        private (int Section, float Along) Front(TrainInput train)
        {
            if (train.Route.Count == 0)
                return (-1, 0f);
            Move front = train.Route[0];
            int section = m_Layout.SectionOf(front.Lane);
            if (section < 0)
                return (-1, 0f);
            float laneLength = m_Network.Lanes[front.Lane].Length;
            float start = m_LaneOffset[front.Lane];
            // With the section's direction, the front is at the lane's start
            // offset plus what it has run of the lane; against it, the
            // section's far end is where the train came in.
            if (SectionForward(section, front))
                return (section, start + laneLength - train.FrontRemaining);
            return (section, m_Layout.Sections[section].Length - start - train.FrontRemaining);
        }

        private void Hold(Holdings holdings, TrainInput train, int lane, bool forward, bool occupies)
        {
            int section = m_Layout.SectionOf(lane);
            if (section < 0)
            {
                holdings.Lanes[lane] = train.Id;
                if (occupies)
                    holdings.OccupiedLanes[lane] = train.Id;
                return;
            }
            if (!holdings.Sections.TryGetValue(section, out List<SectionUser> users))
            {
                users = new List<SectionUser>();
                holdings.Sections[section] = users;
            }
            foreach (SectionUser user in users)
            {
                if (user.Train == train.Id)
                    return;
            }
            float along = float.NegativeInfinity;
            if (occupies)
            {
                (int frontSection, float frontAlong) = m_Fronts[train.Id];
                along = frontSection == section ? frontAlong : float.PositiveInfinity;
            }
            users.Add(new SectionUser
            {
                Train = train.Id,
                Forward = SectionForward(section, new Move(lane, forward)),
                Length = train.Length,
                Occupies = occupies,
                Along = along,
            });
        }

        // ---- Extending a grant ----

        /// <summary>
        /// Tries to grant the train track from <c>route[end + 1]</c> to its
        /// next safe place to wait, through another track of a passing loop
        /// if its own is taken. On success moves <paramref name="end"/> there;
        /// otherwise fills in why the train has to wait.
        /// </summary>
        private bool TryExtend(TrainInput train, ref List<Move> route, ref int end, Holdings holdings, TrainOrder order)
        {
            int last = NextWaitingPlace(train, route, end);
            if (IsFree(train, route, end + 1, last, holdings, order))
            {
                Grant(train, route, end + 1, last, holdings);
                end = last;
                return true;
            }
            HoldReason reason = order.Reason;
            long waitingFor = order.WaitingFor;
            int blockedAt = order.BlockedAt;

            if (train.MayChangeTrack && order.Change == null && TryOtherTrack(train, route, end, last, holdings, out RouteChange change))
            {
                var changed = new List<Move>(route.Count);
                changed.AddRange(route.GetRange(0, change.From));
                changed.AddRange(change.Moves);
                changed.AddRange(route.GetRange(change.To + 1, route.Count - change.To - 1));
                int newLast = NextWaitingPlace(train, changed, end);
                var probe = new TrainOrder();
                if (IsFree(train, changed, end + 1, newLast, holdings, probe))
                {
                    Grant(train, changed, end + 1, newLast, holdings);
                    order.Change = change;
                    order.Reason = HoldReason.None;
                    order.WaitingFor = 0;
                    order.BlockedAt = -1;
                    route = changed;
                    end = newLast;
                    return true;
                }
            }

            order.Reason = reason;
            order.WaitingFor = waitingFor;
            order.BlockedAt = blockedAt;
            RecordRefused(train, route, end + 1, last, holdings);
            Claim(train, route, end + 1, last, holdings);
            // A train behind another waits for that one, not for a stream of
            // trains; claiming the way ahead would keep out the very train it
            // waits for.
            if (train.WaitingMinutes >= PriorityAfterMinutes && TrainAhead(train, route, end, holdings).Train == 0)
                ClaimPriority(train, route, end + 1, last, holdings);
            return false;
        }

        /// <summary>
        /// Index of the last lane up to which the train must be granted track
        /// in one piece: the end of the next section it can safely wait in,
        /// or the end of its route.
        /// </summary>
        private int NextWaitingPlace(TrainInput train, List<Move> route, int end)
        {
            for (int i = end + 1; i < route.Count; i++)
            {
                int section = m_Layout.SectionOf(route[i].Lane);
                if (section < 0)
                    continue;
                // The waiting place is the end of the section, as far as the
                // route runs through it.
                int j = i;
                while (j + 1 < route.Count && m_Layout.SectionOf(route[j + 1].Lane) == section)
                    j++;
                if (j == route.Count - 1 || IsSafeToWaitIn(section, train))
                    return j;
                i = j;
            }
            return route.Count - 1;
        }

        /// <summary>
        /// Index of the last lane at or before <c>route[reach]</c> where the
        /// train can wait: in a section it can safely wait in, or in the
        /// section its front is in already. A cut grant ends there rather than
        /// just before what is in the way, which may be in a junction, where
        /// the train would stand in the way of others. Never below
        /// <see cref="TrainInput.Committed"/>.
        /// </summary>
        private int WaitingPlaceBefore(TrainInput train, List<Move> route, int reach)
        {
            int frontSection = m_Fronts.TryGetValue(train.Id, out (int Section, float Along) f) ? f.Section : -1;
            for (int k = reach; k > train.Committed; k--)
            {
                int section = m_Layout.SectionOf(route[k].Lane);
                if (section >= 0 && (section == frontSection || IsSafeToWaitIn(section, train)))
                    return k;
            }
            return train.Committed;
        }

        /// <summary>
        /// Whether a train can wait at the end of a section without being in
        /// anyone's way beyond what waiting always costs: it must fit in
        /// completely, clear of the junction behind it, and the section must
        /// not be the only way for trains in the other direction.
        /// </summary>
        private bool IsSafeToWaitIn(int section, TrainInput train)
        {
            Section s = m_Layout.Sections[section];
            if (s.Length < train.Length + kMargin)
                return false;
            return !s.TwoWay || m_Layout.GroupOf(section) >= 0;
        }

        /// <summary>
        /// Whether <c>route[from..to]</c> is free for the train: no junction
        /// lane on it or crossing it is held by another train, no section on
        /// it is used in the other direction, and the section at its end has
        /// room for the train.
        /// </summary>
        private bool IsFree(TrainInput train, List<Move> route, int from, int to, Holdings holdings, TrainOrder order)
        {
            int lastSection = to >= 0 && to < route.Count ? m_Layout.SectionOf(route[to].Lane) : -1;
            var checkedSections = new HashSet<int>();

            // A train cannot pass the train ahead of it on the same track.
            // Track beyond that train is granted to the train ahead first:
            // granted to the one behind, it would hold the one ahead back for
            // a train that cannot get by, and the two would wait for each
            // other. The train may still close up behind it, within the
            // section the train ahead is in.
            (long ahead, int aheadSection) = TrainAhead(train, route, from - 1, holdings);
            if (ahead != 0 && (aheadSection < 0 || LeavesSection(route, from, to, aheadSection)))
                return Refuse(order, HoldReason.TrackHeld, ahead, from);
            int frontSection = m_Fronts.TryGetValue(train.Id, out (int Section, float Along) f) ? f.Section : -1;
            for (int i = from; i <= to; i++)
            {
                int lane = route[i].Lane;
                int section = m_Layout.SectionOf(lane);
                if (section < 0)
                {
                    if (HeldByOther(holdings, lane, train.Id, out long holder))
                        return Refuse(order, HoldReason.TrackHeld, holder, i);
                    foreach (int other in m_Network.Overlaps(lane))
                    {
                        if (HeldByOther(holdings, other, train.Id, out holder))
                            return Refuse(order, HoldReason.TrackHeld, holder, i);
                    }
                    if (PriorityOver(train, holdings, holdings.PriorityLanes, lane, out long claimant))
                        return Refuse(order, HoldReason.GivingWay, claimant, i);
                    foreach (int other in m_Network.Overlaps(lane))
                    {
                        if (PriorityOver(train, holdings, holdings.PriorityLanes, other, out claimant))
                            return Refuse(order, HoldReason.GivingWay, claimant, i);
                    }
                    continue;
                }
                if (!checkedSections.Add(section))
                    continue;
                bool forward = SectionForward(section, route[i]);
                holdings.Sections.TryGetValue(section, out List<SectionUser> users);
                // A claim keeps trains from entering the single track against
                // the claimant; see Inside for the trains it lets through.
                if (holdings.Claims.TryGetValue(section, out (long Train, bool Forward) claim) && claim.Train != train.Id && claim.Forward != forward
                    && !Inside(train, claim.Train, holdings))
                    return Refuse(order, HoldReason.GivingWay, claim.Train, i);
                if (PriorityOver(train, holdings, holdings.PrioritySections, section, out long sectionClaimant))
                    return Refuse(order, HoldReason.GivingWay, sectionClaimant, i);
                if (users == null)
                    continue;
                float used = 0f;
                long last = 0;
                foreach (SectionUser user in users)
                {
                    if (user.Train == train.Id)
                        continue;
                    if (user.Forward != forward)
                        return Refuse(order, HoldReason.TrackHeld, user.Train, i);
                    // Passing through a section a train stands or runs in,
                    // the grant would lead past it; see above.
                    if (section != lastSection && section != frontSection && user.Occupies)
                        return Refuse(order, HoldReason.TrackHeld, user.Train, i);
                    // A train granted beyond the section runs out of it and
                    // makes room: the train behind may close up on it, as
                    // with a moving block, instead of waiting for the
                    // section to be empty.
                    if (user.Leaving)
                        continue;
                    used += user.Length + kMargin;
                    last = user.Train;
                }
                // Room is short only because of other trains. A section at the
                // end of a route may be shorter than the train, e.g. a short
                // platform; with no one else in it the train may still go.
                if (section == lastSection && last != 0 && used + train.Length > m_Layout.Sections[section].Length)
                    return Refuse(order, HoldReason.NoRoomAhead, last, i);
            }
            return true;
        }

        /// <summary>
        /// How far along <c>route[0..end]</c> the train can get past the
        /// trains standing or running there: the index of the last lane it
        /// can reach. A train on a junction lane on the way, or across it,
        /// stops it before that lane. A train in a section on the way stops it
        /// before the section if it comes the other way, and at the end of the
        /// section if it runs the same way, since the train may close up
        /// behind it.
        /// </summary>
        private int Unobstructed(TrainInput train, List<Move> route, int end, Holdings holdings)
        {
            (int frontSection, float frontAlong) = m_Fronts.TryGetValue(train.Id, out (int, float) f) ? f : (-1, 0f);
            for (int i = 1; i <= end && i < route.Count; i++)
            {
                int lane = route[i].Lane;
                int section = m_Layout.SectionOf(lane);
                if (section < 0)
                {
                    if (OccupiedByOther(holdings, lane, train.Id, out _))
                        return i - 1;
                    continue;
                }
                int last = i;
                while (last + 1 < route.Count && m_Layout.SectionOf(route[last + 1].Lane) == section)
                    last++;
                if (holdings.Sections.TryGetValue(section, out List<SectionUser> users))
                {
                    bool forward = SectionForward(section, route[i]);
                    foreach (SectionUser user in users)
                    {
                        if (user.Train == train.Id || !user.Occupies)
                            continue;
                        if (user.Forward != forward)
                            return i - 1;
                        if (section != frontSection || user.Along > frontAlong)
                            return Math.Min(end, last);
                    }
                }
                i = last;
            }
            return end;
        }

        /// <summary>Whether a train other than <paramref name="train"/> is on the junction lane or on one crossing or touching it.</summary>
        private bool OccupiedByOther(Holdings holdings, int lane, long train, out long other)
        {
            if (holdings.OccupiedLanes.TryGetValue(lane, out other) && other != train)
                return true;
            foreach (int overlap in m_Network.Overlaps(lane))
            {
                if (holdings.OccupiedLanes.TryGetValue(overlap, out other) && other != train)
                    return true;
            }
            other = 0;
            return false;
        }

        /// <summary>
        /// The first train on <c>route[0..end]</c>, the track the train is on
        /// or already granted, that is ahead of it in its direction; with the
        /// section it is in, or -1 for a junction area. Returns (0, -1) if
        /// there is none.
        /// </summary>
        private (long Train, int Section) TrainAhead(TrainInput train, List<Move> route, int end, Holdings holdings)
        {
            (int frontSection, float frontAlong) = m_Fronts.TryGetValue(train.Id, out (int, float) f) ? f : (-1, 0f);
            var seen = new HashSet<int>();
            for (int i = 0; i <= end && i < route.Count; i++)
            {
                int lane = route[i].Lane;
                int section = m_Layout.SectionOf(lane);
                if (section < 0)
                {
                    // On a junction lane on the way, or across it, another
                    // train is in the way.
                    if (OccupiedByOther(holdings, lane, train.Id, out long other))
                        return (other, -1);
                    continue;
                }
                if (!seen.Add(section) || !holdings.Sections.TryGetValue(section, out List<SectionUser> users))
                    continue;
                bool forward = SectionForward(section, route[i]);
                foreach (SectionUser user in users)
                {
                    if (user.Train == train.Id || !user.Occupies || user.Forward != forward)
                        continue;
                    // In the train's own section, only a train further along
                    // is ahead; in any later section, every train in it is.
                    if (section != frontSection || user.Along > frontAlong)
                        return (user.Train, section);
                }
            }
            return (0, -1);
        }

        /// <summary>Whether <c>route[from..to]</c> reaches a lane outside the given section.</summary>
        private bool LeavesSection(List<Move> route, int from, int to, int section)
        {
            for (int i = from; i <= to; i++)
            {
                if (m_Layout.SectionOf(route[i].Lane) != section)
                    return true;
            }
            return false;
        }

        private static bool HeldByOther(Holdings holdings, int lane, long train, out long holder)
        {
            return holdings.Lanes.TryGetValue(lane, out holder) && holder != train;
        }

        private static bool Refuse(TrainOrder order, HoldReason reason, long holder, int at)
        {
            order.Reason = reason;
            order.WaitingFor = holder;
            order.BlockedAt = at;
            return false;
        }

        /// <summary>
        /// Enters <c>route[from..to]</c> as granted to the train, and marks
        /// the sections its grant now runs out of as ones it is leaving.
        /// </summary>
        private void Grant(TrainInput train, List<Move> route, int from, int to, Holdings holdings)
        {
            for (int i = from; i <= to; i++)
                Hold(holdings, train, route[i].Lane, route[i].Forward, occupies: false);
            for (int i = 0; i < to; i++)
            {
                int section = m_Layout.SectionOf(route[i].Lane);
                if (section < 0 || m_Layout.SectionOf(route[i + 1].Lane) == section)
                    continue;
                if (!holdings.Sections.TryGetValue(section, out List<SectionUser> users))
                    continue;
                foreach (SectionUser user in users)
                {
                    if (user.Train == train.Id)
                        user.Leaving = true;
                }
            }
        }

        /// <summary>
        /// Keeps trains of lower rank from entering the single track the
        /// refused train needs, in the other direction. Otherwise a stream of
        /// such trains could keep it waiting for good. Only single track is
        /// claimed: holding a station throat for a train that waits for its
        /// platform would stop trains that could pass it. After a long wait
        /// the train claims all it needs; see ClaimPriority.
        /// </summary>
        private void Claim(TrainInput train, List<Move> route, int from, int to, Holdings holdings)
        {
            for (int i = from; i <= to && i < route.Count; i++)
            {
                int section = m_Layout.SectionOf(route[i].Lane);
                if (section < 0 || !m_Layout.Sections[section].TwoWay || m_Layout.GroupOf(section) >= 0)
                    continue;
                if (!holdings.Claims.ContainsKey(section))
                    holdings.Claims[section] = (train.Id, SectionForward(section, route[i]));
            }
        }

        /// <summary>
        /// Keeps trains of lower rank out of all the track the refused train
        /// needs: every junction lane and section of <c>route[from..to]</c>.
        ///
        /// For a train that has waited long. A train needs its way up to the
        /// next place to wait all at once, while others with shorter ways can
        /// take a part of it whenever that part is free: one takes the
        /// turnout, the next the room at the platform, and the train that
        /// waits never finds all of it free, however high its rank. With the
        /// claim, what is in the way now runs out of it, nothing new comes
        /// in, and the train gets its way. Trains that already hold a grant
        /// keep it.
        /// </summary>
        private void ClaimPriority(TrainInput train, List<Move> route, int from, int to, Holdings holdings)
        {
            for (int i = from; i <= to && i < route.Count; i++)
            {
                int lane = route[i].Lane;
                int section = m_Layout.SectionOf(lane);
                if (section < 0)
                {
                    if (!holdings.PriorityLanes.ContainsKey(lane))
                        holdings.PriorityLanes[lane] = train.Id;
                }
                else if (!holdings.PrioritySections.ContainsKey(section))
                {
                    holdings.PrioritySections[section] = train.Id;
                }
            }
        }

        /// <summary>
        /// Whether another train's priority claim on <paramref name="key"/>, a
        /// lane or a section as the dictionary holds them, keeps
        /// <paramref name="train"/> out; see Inside for the trains it lets
        /// through.
        ///
        /// A train that has waited long itself is not kept out. The claim is
        /// against the stream of trains that come and go while the claimant
        /// waits; holding starving trains out of each other's way makes every
        /// long wait longer, and once many trains wait long, a jam feeds
        /// itself. Among starving trains, rank decides, as it does anyway.
        /// </summary>
        private bool PriorityOver(TrainInput train, Holdings holdings, Dictionary<int, long> claims, int key, out long claimant)
        {
            return claims.TryGetValue(key, out claimant) && claimant != train.Id && train.WaitingMinutes < PriorityAfterMinutes
                && !Inside(train, claimant, holdings);
        }

        /// <summary>Notes the way <c>route[from..to]</c> the train was refused, for Inside.</summary>
        private void RecordRefused(TrainInput train, List<Move> route, int from, int to, Holdings holdings)
        {
            var sections = new HashSet<int>();
            var lanes = new HashSet<int>();
            for (int i = from; i <= to && i < route.Count; i++)
            {
                int section = m_Layout.SectionOf(route[i].Lane);
                if (section < 0)
                    lanes.Add(route[i].Lane);
                else
                    sections.Add(section);
            }
            holdings.Refused[train.Id] = (sections, lanes);
        }

        /// <summary>
        /// Whether the train stands on the way the claimant was refused: in
        /// one of its sections, or on or across one of its junction lanes.
        /// The claimant's claims do not hold such a train back. It is in the
        /// claimant's way, and the claimant can go only once it has left,
        /// whichever way it leaves; kept out of a claimed section on its way
        /// out, it would wait for the claimant while the claimant waits for
        /// it.
        /// </summary>
        private bool Inside(TrainInput train, long claimant, Holdings holdings)
        {
            if (!holdings.Refused.TryGetValue(claimant, out (HashSet<int> Sections, HashSet<int> Lanes) way))
                return false;
            foreach (Move move in train.Occupied)
            {
                int section = m_Layout.SectionOf(move.Lane);
                if (section >= 0)
                {
                    if (way.Sections.Contains(section))
                        return true;
                    continue;
                }
                if (way.Lanes.Contains(move.Lane))
                    return true;
                foreach (int overlap in m_Network.Overlaps(move.Lane))
                {
                    if (way.Lanes.Contains(overlap))
                        return true;
                }
            }
            return false;
        }

        // ---- Another track of a passing loop ----

        /// <summary>
        /// Looks for a free parallel track to wait on, when the section at
        /// the end of the refused stretch belongs to a passing loop. The new
        /// way runs from the last lane before the junction area in front of
        /// the loop to the first lane after the junction area behind it.
        /// </summary>
        private bool TryOtherTrack(TrainInput train, List<Move> route, int end, int last, Holdings holdings, out RouteChange change)
        {
            change = null;
            int section = m_Layout.SectionOf(route[last].Lane);
            if (section < 0)
                return false;
            int group = m_Layout.GroupOf(section);
            if (group < 0)
                return false;
            ParallelGroup g = m_Layout.Groups[group];
            if (g.Station != 0)
                return false;

            // Where the route enters and leaves the group's junction areas.
            int enterArea = -1, first = -1;
            for (int i = last; i > end; i--)
            {
                int area = m_Layout.AreaOf(route[i].Lane);
                if (area >= 0)
                {
                    enterArea = area;
                    first = i;
                    while (first - 1 > end && m_Layout.AreaOf(route[first - 1].Lane) == area)
                        first--;
                    break;
                }
            }
            if (enterArea < 0 || (enterArea != g.AreaA && enterArea != g.AreaB))
                return false;
            int exitArea = enterArea == g.AreaA ? g.AreaB : g.AreaA;
            int after = -1;
            for (int i = last + 1; i < route.Count; i++)
            {
                if (m_Layout.AreaOf(route[i].Lane) == exitArea)
                    continue;
                if (i > last + 1 && m_Layout.AreaOf(route[i - 1].Lane) == exitArea)
                {
                    after = i;
                    break;
                }
                if (m_Layout.SectionOf(route[i].Lane) != section)
                    break;
            }
            if (after < 0)
                return false;

            foreach (int alternative in m_Layout.Alternatives(g, enterArea))
            {
                if (alternative == section || m_Layout.Sections[alternative].Length < train.Length + kMargin)
                    continue;
                List<Move> way = FindWay(route[first - 1], route[after], enterArea, alternative, exitArea);
                if (way == null)
                    continue;
                change = new RouteChange { From = first, To = after - 1, Moves = way };
                var probeRoute = new List<Move>(route.GetRange(0, first));
                probeRoute.AddRange(way);
                int probeLast = first + way.Count - 1;
                // Only the part up to the end of the new loop track has to be
                // free now; the way out is granted like any other later.
                while (probeLast > first && m_Layout.SectionOf(probeRoute[probeLast].Lane) != alternative)
                    probeLast--;
                if (IsFree(train, probeRoute, end + 1, probeLast, holdings, new TrainOrder()))
                    return true;
            }
            change = null;
            return false;
        }

        /// <summary>
        /// The moves strictly between <paramref name="before"/> and
        /// <paramref name="after"/>, running through junction area
        /// <paramref name="enterArea"/>, all of section
        /// <paramref name="through"/>, and area <paramref name="exitArea"/>;
        /// null if there is no such way.
        /// </summary>
        private List<Move> FindWay(Move before, Move after, int enterArea, int through, int exitArea)
        {
            // Breadth-first over moves, allowed only on the two areas and the
            // loop track, so the search stays within the loop. The queue is a
            // list with a read position: the game's runtime has Queue<T> in
            // two assemblies, and a mod cannot name it unambiguously.
            var previous = new Dictionary<Move, Move>();
            var queue = new List<Move> { before };
            previous[before] = before;
            for (int head = 0; head < queue.Count; head++)
            {
                Move current = queue[head];
                foreach (Move next in m_Network.Successors(current))
                {
                    if (next.Equals(after))
                    {
                        if (!Passes(current, previous, before, through))
                            continue;
                        var way = new List<Move>();
                        for (Move m = current; !m.Equals(before); m = previous[m])
                            way.Add(m);
                        way.Reverse();
                        return way;
                    }
                    if (previous.ContainsKey(next))
                        continue;
                    int area = m_Layout.AreaOf(next.Lane);
                    bool allowed = area == enterArea || area == exitArea || m_Layout.SectionOf(next.Lane) == through;
                    if (!allowed)
                        continue;
                    previous[next] = current;
                    queue.Add(next);
                }
            }
            return null;
        }

        /// <summary>Whether the way that ends in <paramref name="last"/> runs through section <paramref name="through"/>.</summary>
        private bool Passes(Move last, Dictionary<Move, Move> previous, Move before, int through)
        {
            for (Move m = last; !m.Equals(before); m = previous[m])
            {
                if (m_Layout.SectionOf(m.Lane) == through)
                    return true;
            }
            return false;
        }
    }
}
