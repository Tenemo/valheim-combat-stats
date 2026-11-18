using System.Collections.Generic;
using CombatStats.Stats;

namespace CombatStats.Net
{
    // A batch can arrive after the game transferred or destroyed its target. Only server-observed
    // ownership intervals can authorize those reports. This state expires with the correction window.
    internal sealed class ReportOwnership
    {
        private readonly Dictionary<string, List<(long owner, long victim, double end)>> previous =
            new Dictionary<string, List<(long, long, double)>>();
        private readonly Queue<(string target, double end)> expiry = new Queue<(string, double)>();

        internal void Relinquished(string target, long owner, long victim, double now)
        {
            if (!previous.TryGetValue(target, out var intervals))
                previous[target] = intervals = new List<(long, long, double)>();
            intervals.Add((owner, victim, now));
            expiry.Enqueue((target, now));
        }

        internal bool Allows(string target, long peer, long victim, double time, long currentOwner, long currentVictim)
        {
            if (peer == 0) return false;
            double start = double.NegativeInfinity;
            if (previous.TryGetValue(target, out var intervals))
                foreach (var interval in intervals)
                {
                    if (interval.owner == peer && interval.victim == victim && time >= start && time <= interval.end) return true;
                    start = interval.end;
                }
            // The new owner's first batch can beat the game's ownership update to the server.
            return currentOwner == peer && currentVictim == victim && time >= start - 2d;
        }

        internal void Expire(double now)
        {
            while (expiry.Count > 0 && now - expiry.Peek().end > EncounterRecorder.LateReportSeconds)
            {
                var entry = expiry.Dequeue();
                var intervals = previous[entry.target];
                intervals.RemoveAt(0);
                if (intervals.Count == 0) previous.Remove(entry.target);
            }
        }
    }
}
