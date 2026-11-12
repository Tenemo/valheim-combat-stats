using System;
using System.Collections.Generic;
using System.Linq;
using CombatStats.Model;

namespace CombatStats.Model
{
    public enum CombatChannel { DamageDealt, DamageTaken, Healing }

    public static class CombatChannels
    {
        public static readonly CombatChannel[] All = { CombatChannel.DamageDealt, CombatChannel.DamageTaken, CombatChannel.Healing };
    }
    public enum ObservationKind { Activity, Damage, Engagement, Death, Healing, DotAdded, DotCleared }

    // Full network object IDs identify enemies. Player IDs identify characters across respawns.
    public sealed class Observation
    {
        public long Sequence;
        public double Time;
        public ObservationKind Kind;
        public string Target = "";
        public string TargetName = "";
        public string Enemy = "", EnemyName = "";
        public long Victim;
        public long Actor;
        public string ActorName = "";
        public string Pet = "";
        public string Skill = "";
        public float X, Y, Z;
        public bool Boss, Engaged, Periodic;
        public float[] Amounts = new float[DamageKinds.Count];
        public float Overkill;
        public int DotKind;
        public float DotAmount, DotBefore;
        public bool ReplaceDot;
        public float[] Consumed = new float[DamageKinds.Count];

        public bool Valid()
        {
            if (Sequence <= 0 || !Finite(Time) || !Enum.IsDefined(typeof(ObservationKind), Kind) ||
                Target == null || Target.Length > 96 || TargetName == null || TargetName.Length > 160 ||
                Enemy == null || Enemy.Length > 96 || EnemyName == null || EnemyName.Length > 160 ||
                ActorName == null || ActorName.Length > 160 || Pet == null || Pet.Length > 160 ||
                Skill == null || Skill.Length > 80 || !Finite(X) || !Finite(Y) || !Finite(Z) ||
                !Amount(Overkill) || !Amount(DotAmount) || !Amount(DotBefore) ||
                DotKind < 0 || DotKind >= DamageKinds.Count || Amounts == null || Consumed == null ||
                Amounts.Length != DamageKinds.Count || Consumed.Length != DamageKinds.Count) return false;
            return Amounts.All(Amount) && Consumed.All(Amount);
        }

        private static bool Amount(float value) => Finite(value) && value >= 0f && value <= 1e9f;
        private static bool Finite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
    }

    public sealed class Combatant
    {
        public long Id;
        public string Name = "";
        public float[] ByKind = new float[DamageKinds.Count];
        public int Hits, Ticks;
        public float Max, Overkill;
        public Dictionary<string, float> Targets = new Dictionary<string, float>();
        public Dictionary<string, float> Skills = new Dictionary<string, float>();
        public Dictionary<string, float> Pets = new Dictionary<string, float>();
        public float Total => ByKind.Sum();

        /// <summary>
        /// A tamed creature whose owner is not known gets its own row, with an ID from the hash of
        /// its network ID, far below any player's.
        /// </summary>
        public static long UnownedPetId(uint hash) => long.MinValue + hash;

        /// <summary>True for a row made by <see cref="UnownedPetId"/>, whose name is a creature's.</summary>
        public static bool IsUnownedPet(long id) => id < -(1L << 40);

        public void Add(Observation hit, float[] amounts)
        {
            float total = amounts.Sum();
            if (total <= 0f) return;
            for (int kind = 0; kind < amounts.Length; kind++) ByKind[kind] += amounts[kind];
            if (hit.Periodic) Ticks++; else Hits++;
            Max = Math.Max(Max, total);
            Overkill += hit.Overkill;
            AddTo(Targets, hit.TargetName, total);
            AddTo(Skills, hit.Periodic ? "Damage over time" : hit.Skill, total);
            if (hit.Pet.Length > 0) AddTo(Pets, hit.Pet, total);
        }

        public static void AddTo(Dictionary<string, float> values, string key, float amount)
        {
            if (string.IsNullOrEmpty(key)) key = "Other";
            values.TryGetValue(key, out float old);
            values[key] = old + amount;
        }
    }

    public sealed class Encounter
    {
        public string Session = "";
        public long Id;
        public long Revision;
        public string Name = "Combat";
        public double Start, End, LastActivity;
        public string Result = "Active";
        public bool Boss;
        public float X, Y, Z;
        public Dictionary<long, string> Participants = new Dictionary<long, string>();
        public Dictionary<long, Combatant> Damage = new Dictionary<long, Combatant>();
        public Dictionary<long, Combatant> Taken = new Dictionary<long, Combatant>();
        public Dictionary<long, Combatant> Healing = new Dictionary<long, Combatant>();
        public Dictionary<long, int> Deaths = new Dictionary<long, int>();
        public bool Active => Result == "Active";
        public double Duration(double now) => Math.Max(1d, (Active ? now : End) - Start);

        public Dictionary<long, Combatant> Rows(CombatChannel channel) =>
            channel == CombatChannel.DamageTaken ? Taken : channel == CombatChannel.Healing ? Healing : Damage;

        public static Combatant Row(Dictionary<long, Combatant> rows, long id, string name)
        {
            if (!rows.TryGetValue(id, out Combatant row)) rows[id] = row = new Combatant { Id = id, Name = name };
            if (!string.IsNullOrEmpty(name)) row.Name = name;
            return row;
        }
    }
}

namespace CombatStats.Stats
{
    // A tick is divided by the remaining accepted contributions, not by the last hit's attacker.
    public sealed class DotLedger
    {
        public sealed class Credit
        {
            public string Name = "";
            public float[] Amounts = new float[DamageKinds.Count];
            public Dictionary<string, float> Pets = new Dictionary<string, float>();
        }
        private sealed class Share
        {
            public long Actor;
            public string Name = "", Pet = "";
            public float Remaining;
        }
        private readonly Dictionary<string, List<Share>> pools = new Dictionary<string, List<Share>>();
        private static string Key(string target, int kind) => target + "/" + kind;

        public void Add(Observation hit)
        {
            string key = Key(hit.Target, hit.DotKind);
            if (!pools.TryGetValue(key, out List<Share> shares)) pools[key] = shares = new List<Share>();
            if (hit.ReplaceDot || hit.DotBefore <= 0f) shares.Clear();
            else
            {
                float known = shares.Sum(x => x.Remaining);
                // A transferred or already running effect may include environmental/unobserved damage.
                if (known > hit.DotBefore && known > 0f)
                    foreach (Share share in shares) share.Remaining *= hit.DotBefore / known;
                else if (hit.DotBefore > known + 0.001f)
                    shares.Add(new Share { Remaining = hit.DotBefore - known });
            }
            if (hit.DotAmount > 0f)
                shares.Add(new Share { Actor = hit.Actor, Name = hit.ActorName, Pet = hit.Pet, Remaining = hit.DotAmount });
        }

        public Dictionary<long, Credit> Tick(Observation hit)
        {
            var credited = new Dictionary<long, Credit>();
            for (int kind = 0; kind < DamageKinds.Count; kind++)
            {
                if (hit.Amounts[kind] <= 0f || !pools.TryGetValue(Key(hit.Target, kind), out List<Share> shares)) continue;
                float remaining = shares.Sum(x => x.Remaining);
                if (remaining <= 0f) continue;
                foreach (Share share in shares)
                {
                    float fraction = share.Remaining / remaining;
                    if (share.Actor != 0)
                    {
                        if (!credited.TryGetValue(share.Actor, out var item))
                            credited[share.Actor] = item = new Credit { Name = share.Name };
                        float damage = hit.Amounts[kind] * fraction;
                        item.Amounts[kind] += damage;
                        if (share.Pet.Length > 0) Combatant.AddTo(item.Pets, share.Pet, damage);
                    }
                    share.Remaining = Math.Max(0f, share.Remaining - hit.Consumed[kind] * fraction);
                }
                if (shares.All(x => x.Remaining <= 0.001f)) pools.Remove(Key(hit.Target, kind));
            }
            return credited;
        }

        public void Clear(string target, int kind) => pools.Remove(Key(target, kind));
        public void ClearTarget(string target)
        {
            for (int kind = 0; kind < DamageKinds.Count; kind++) Clear(target, kind);
        }
    }

    // Pure accounting/state logic. Unity hooks and transport both feed this same implementation.
    public sealed class EncounterRecorder
    {
        public const double LateReportSeconds = 60d;
        public const int CorrectionHistory = 40;

        /// <summary>How long a fight lasts after its last activity when no enemy is engaged.</summary>
        public const double Grace = 10d;

        /// <summary>How close a hit or an engaged enemy must be to join a fight.</summary>
        public const float Radius = 96f;

        /// <summary>
        /// The longest an owner waits before repeating an unchanged engagement. A report stays
        /// fresh for twice as long, so one late report does not end the engagement.
        /// </summary>
        public const double EngagementKeepAlive = 1d;

        private const double EngagementFreshness = 2d * EngagementKeepAlive;
        private sealed class Enemy
        {
            public long Fight;
            public double Seen;
            public bool Engaged, Dead;
        }
        private sealed class SeenReports
        {
            public readonly HashSet<long> Set = new HashSet<long>();
            public readonly Queue<long> Order = new Queue<long>();
        }
        private readonly Dictionary<string, Enemy> enemies = new Dictionary<string, Enemy>();
        private readonly Dictionary<long, SeenReports> reports = new Dictionary<long, SeenReports>();
        private readonly DotLedger dots = new DotLedger();
        private readonly Dictionary<long, double> pendingKills = new Dictionary<long, double>();
        private long nextId;
        public readonly List<Encounter> Fights = new List<Encounter>();
        public readonly Dictionary<long, long> Redirects = new Dictionary<long, long>();

        public int Receive(long peer, IEnumerable<Observation> batch, double now)
        {
            if (!reports.TryGetValue(peer, out SeenReports seen)) reports[peer] = seen = new SeenReports();
            int accepted = 0;
            foreach (Observation hit in batch.OrderBy(x => x.Time).ThenBy(x => x.Sequence))
            {
                if (!hit.Valid() || hit.Time < now - LateReportSeconds || hit.Time > now + 2d || !seen.Set.Add(hit.Sequence)) continue;
                seen.Order.Enqueue(hit.Sequence);
                if (seen.Order.Count > 4096) seen.Set.Remove(seen.Order.Dequeue());
                Record(hit, now);
                accepted++;
            }
            Tick(now);
            return accepted;
        }

        public void ForgetPeer(long peer) => reports.Remove(peer);

        private Encounter Find(Observation hit, bool create)
        {
            Encounter fight = null;
            string enemyId = hit.Victim == 0 ? hit.Target : hit.Enemy;
            if (enemies.TryGetValue(enemyId, out Enemy enemy))
                fight = Fights.FirstOrDefault(x => x.Id == enemy.Fight && (x.Active || hit.Time <= x.End));
            if (fight == null && hit.Victim == 0 && hit.Kind == ObservationKind.Death) return null;
            var candidates = Fights.Where(x => x.Active &&
                Distance(x.X, x.Y, x.Z, hit.X, hit.Y, hit.Z) <= Radius * Radius).ToList();
            if (fight == null && hit.Kind == ObservationKind.Engagement)
            {
                if (!hit.Engaged || hit.Actor == 0) return null;
                candidates = candidates.Where(x => x.Participants.ContainsKey(hit.Actor)).ToList();
                if (candidates.Count == 0) return null;
            }
            if ((hit.Kind == ObservationKind.Healing || (hit.Kind == ObservationKind.Death && hit.Victim != 0)))
                return Fights.FirstOrDefault(x => x.Active && x.Participants.ContainsKey(hit.Victim));
            if (fight == null && candidates.Count > 0) fight = candidates.OrderBy(x => x.Start).First();
            // A report buffered on another owner can precede a death already delivered to us.
            if (fight == null && create)
                fight = Fights.LastOrDefault(x => !x.Active && hit.Time >= x.Start && hit.Time <= x.End &&
                    Distance(x.X, x.Y, x.Z, hit.X, hit.Y, hit.Z) <= Radius * Radius);
            if (fight == null && create)
            {
                string name = hit.Victim == 0 ? hit.TargetName : hit.EnemyName;
                fight = new Encounter { Id = ++nextId, Name = name.Length > 0 ? name : "Combat",
                    Start = hit.Time, LastActivity = hit.Time, X = hit.X, Y = hit.Y, Z = hit.Z, Boss = hit.Boss };
                Fights.Add(fight);
            }
            if (fight != null && fight.Active)
            {
                foreach (Encounter other in candidates.Where(x => x != fight).ToList()) Merge(fight, other);
                if (hit.Boss) { fight.Boss = true; fight.Name = hit.Victim == 0 ? hit.TargetName : hit.EnemyName; }
            }
            return fight;
        }

        public void Record(Observation hit, double now)
        {
            if (hit.Kind == ObservationKind.DotAdded) { dots.Add(hit); return; }
            if (hit.Kind == ObservationKind.DotCleared) { dots.Clear(hit.Target, hit.DotKind); return; }
            bool contact = hit.Kind == ObservationKind.Activity || hit.Kind == ObservationKind.Damage;
            var credits = hit.Kind == ObservationKind.Damage && hit.Periodic && hit.Actor == 0 ? dots.Tick(hit) : null;
            if (hit.Kind == ObservationKind.Damage && hit.Actor == 0 && hit.Enemy.Length == 0 && (credits == null || credits.Count == 0))
            {
                RecordEnvironment(hit);
                return;
            }
            bool create = contact && (hit.Actor != 0 || (hit.Victim != 0 && hit.Enemy.Length > 0) || (credits != null && credits.Count > 0));
            Encounter fight = Find(hit, create);
            if (fight == null) return;
            fight.Revision++;
            if (hit.Kind == ObservationKind.Healing)
            {
                if (fight.Active && fight.Participants.ContainsKey(hit.Victim))
                    Encounter.Row(fight.Healing, hit.Victim, hit.ActorName).Add(hit, hit.Amounts);
                return;
            }
            if (hit.Kind == ObservationKind.Death && hit.Victim != 0)
            {
                if (!fight.Deaths.ContainsKey(hit.Victim)) fight.Deaths[hit.Victim] = 0;
                fight.Deaths[hit.Victim]++;
                return;
            }
            string enemyId = hit.Victim == 0 ? hit.Target : hit.Enemy;
            if (enemyId.Length > 0)
            {
                if (!enemies.TryGetValue(enemyId, out Enemy enemy) || enemy.Fight != fight.Id)
                {
                    enemies[enemyId] = enemy = new Enemy { Fight = fight.Id };
                    if (!fight.Active && contact) { fight.Result = "Active"; fight.End = 0d; }
                    pendingKills.Remove(fight.Id);
                }
                if (hit.Time >= enemy.Seen)
                {
                    enemy.Seen = hit.Time;
                    if (hit.Kind == ObservationKind.Engagement) enemy.Engaged = hit.Engaged;
                    if (hit.Kind == ObservationKind.Death) { enemy.Dead = true; enemy.Engaged = false; }
                }
            }
            if (contact || (hit.Kind == ObservationKind.Engagement && hit.Engaged))
            {
                fight.Start = Math.Min(fight.Start, hit.Time);
                fight.LastActivity = Math.Max(fight.LastActivity, hit.Time);
                fight.X = hit.X; fight.Y = hit.Y; fight.Z = hit.Z;
                if (hit.Actor != 0) fight.Participants[hit.Actor] = hit.ActorName;
                if (hit.Victim != 0) fight.Participants[hit.Victim] = hit.TargetName;
            }
            if (hit.Kind == ObservationKind.Damage)
            {
                if (hit.Victim != 0) AddTaken(fight, hit);
                // Player-vs-player and pet victims never become outgoing PvE totals.
                else if (hit.Actor != 0) Encounter.Row(fight.Damage, hit.Actor, hit.ActorName).Add(hit, hit.Amounts);
                else if (credits != null)
                {
                    float groupAmount = hit.Amounts.Sum();
                    float fullOverkill = hit.Overkill;
                    foreach (var credit in credits)
                    {
                        hit.Pet = "";
                        hit.Overkill = groupAmount > 0f ? fullOverkill * credit.Value.Amounts.Sum() / groupAmount : 0f;
                        fight.Participants[credit.Key] = credit.Value.Name;
                        Combatant row = Encounter.Row(fight.Damage, credit.Key, credit.Value.Name);
                        row.Add(hit, credit.Value.Amounts);
                        foreach (var pet in credit.Value.Pets) Combatant.AddTo(row.Pets, pet.Key, pet.Value);
                    }
                }
            }
            if (hit.Kind == ObservationKind.Death && hit.Victim == 0)
            {
                dots.ClearTarget(hit.Target);
                if (enemies.Values.Where(x => x.Fight == fight.Id).All(x => x.Dead)) pendingKills[fight.Id] = hit.Time;
            }
        }

        /// <summary>
        /// Falls, fire and other damage nobody is credited with count only for a player already in
        /// a fight. They never start, extend, move or merge fights; uncredited ticks on enemies are
        /// dropped.
        /// </summary>
        private void RecordEnvironment(Observation hit)
        {
            if (hit.Victim == 0) return;
            Encounter fight = Fights.FirstOrDefault(x => x.Active && x.Participants.ContainsKey(hit.Victim));
            if (fight == null) return;
            fight.Revision++;
            AddTaken(fight, hit);
        }

        // The damage-taken breakdown lists sources, so the row's target is the attacker or cause.
        private static void AddTaken(Encounter fight, Observation hit)
        {
            string victimName = hit.TargetName;
            hit.TargetName = hit.EnemyName.Length > 0 ? hit.EnemyName : hit.Periodic ? "Damage over time" : "Environment";
            Encounter.Row(fight.Taken, hit.Victim, victimName).Add(hit, hit.Amounts);
            hit.TargetName = victimName;
        }

        public void Tick(double now)
        {
            foreach (Encounter fight in Fights.Where(x => x.Active).ToList())
            {
                var alive = enemies.Values.Where(x => x.Fight == fight.Id && !x.Dead).ToList();
                // Other targets of an area attack can be reported by another owner in the next batch.
                // Confirm the kill briefly, but end the clock at the actual final death.
                if (alive.Count == 0 && pendingKills.TryGetValue(fight.Id, out double death))
                {
                    if (now - death >= 1d) { Finish(fight, death, "Killed"); pendingKills.Remove(fight.Id); }
                    continue;
                }
                if (alive.Any(x => x.Engaged && now - x.Seen <= EngagementFreshness)) continue;
                if (now - fight.LastActivity < Grace) continue;
                string result = alive.Any(x => x.Engaged) ? "Interrupted" : "Disengaged";
                if (fight.Participants.Count > 0 && fight.Participants.Keys.All(x => fight.Deaths.ContainsKey(x))) result = "Wipe";
                Finish(fight, fight.LastActivity, result);
            }
            foreach (Encounter old in Fights.Where(x => !x.Active).OrderByDescending(x => x.End)
                         .Skip(CorrectionHistory).Where(x => now - x.End > LateReportSeconds).ToList())
            {
                Fights.Remove(old);
                foreach (string target in enemies.Where(x => x.Value.Fight == old.Id).Select(x => x.Key).ToList())
                { enemies.Remove(target); dots.ClearTarget(target); }
            }
            foreach (long id in Redirects.Where(x => Fights.All(f => f.Id != x.Value)).Select(x => x.Key).ToList()) Redirects.Remove(id);
        }

        private static void Finish(Encounter fight, double end, string result)
        {
            if (!fight.Active) return;
            fight.End = Math.Max(end, fight.Start);
            fight.Result = result;
            fight.Revision++;
        }

        private void Merge(Encounter into, Encounter other)
        {
            into.Start = Math.Min(into.Start, other.Start);
            into.LastActivity = Math.Max(into.LastActivity, other.LastActivity);
            if (other.Boss) { into.Boss = true; into.Name = other.Name; }
            foreach (var entry in other.Participants) into.Participants[entry.Key] = entry.Value;
            foreach (CombatChannel channel in CombatChannels.All) CombineRows(into.Rows(channel), other.Rows(channel));
            foreach (var entry in other.Deaths)
            { into.Deaths.TryGetValue(entry.Key, out int count); into.Deaths[entry.Key] = count + entry.Value; }
            foreach (Enemy enemy in enemies.Values.Where(x => x.Fight == other.Id)) enemy.Fight = into.Id;
            foreach (long id in Redirects.Where(x => x.Value == other.Id).Select(x => x.Key).ToList()) Redirects[id] = into.Id;
            Redirects[other.Id] = into.Id;
            pendingKills.Remove(other.Id);
            pendingKills.Remove(into.Id);
            Fights.Remove(other);
        }

        public static void CombineRows(Dictionary<long, Combatant> into, Dictionary<long, Combatant> source, float factor = 1f)
        {
            foreach (Combatant value in source.Values)
            {
                Combatant row = Encounter.Row(into, value.Id, value.Name);
                for (int kind = 0; kind < DamageKinds.Count; kind++) row.ByKind[kind] += value.ByKind[kind] * factor;
                row.Hits += (int)(value.Hits * factor); row.Ticks += (int)(value.Ticks * factor);
                row.Overkill += value.Overkill * factor; row.Max = Math.Max(row.Max, value.Max);
                foreach (var item in value.Targets) Combatant.AddTo(row.Targets, item.Key, item.Value * factor);
                foreach (var item in value.Skills) Combatant.AddTo(row.Skills, item.Key, item.Value * factor);
                foreach (var item in value.Pets) Combatant.AddTo(row.Pets, item.Key, item.Value * factor);
            }
        }

        public static float Distance(float x, float y, float z, float a, float b, float c) =>
            (x - a) * (x - a) + (y - b) * (y - b) + (z - c) * (z - c);
    }

    public sealed class CombatantRow
    {
        public long Id;
        public string Name = "";
        public float Total, Share, Max, Dps, Overkill;
        public int Hits, Ticks;
        public float[] ByKind = new float[DamageKinds.Count];
        public Dictionary<string, float> Targets, Skills, Pets;
        public float Average => Hits + Ticks > 0 ? Total / (Hits + Ticks) : 0f;
    }

    public sealed class EncounterSnapshot
    {
        public bool HasEncounter;
        public string Name = "No fights recorded", Result = "";
        public double Duration;
        public float Total;
        public float Dps => Total / (float)Math.Max(1d, Duration);
        public List<CombatantRow> Rows = new List<CombatantRow>();
        public static EncounterSnapshot Empty => new EncounterSnapshot();

        public static EncounterSnapshot From(Encounter fight, CombatChannel channel, double now)
        {
            if (fight == null) return Empty;
            var snapshot = new EncounterSnapshot { HasEncounter = true, Name = fight.Name, Result = fight.Result,
                Duration = fight.Duration(now), Total = fight.Rows(channel).Values.Sum(x => x.Total) };
            foreach (Combatant value in fight.Rows(channel).Values.Where(x => x.Total > 0f).OrderByDescending(x => x.Total).ThenBy(x => x.Id))
            {
                snapshot.Rows.Add(new CombatantRow { Id = value.Id, Name = value.Name, Total = value.Total,
                    Share = snapshot.Total > 0f ? value.Total / snapshot.Total : 0f, Dps = value.Total / (float)snapshot.Duration,
                    Hits = value.Hits, Ticks = value.Ticks, Max = value.Max, Overkill = value.Overkill,
                    ByKind = value.ByKind, Targets = value.Targets, Skills = value.Skills, Pets = value.Pets });
            }
            return snapshot;
        }

        /// <summary>
        /// The leading <paramref name="limit"/> rows, with the local player's row always among
        /// them: when it ranks lower, it takes the last place.
        /// </summary>
        public List<CombatantRow> Visible(int limit, long local)
        {
            List<CombatantRow> shown = Rows.Take(Math.Max(0, limit)).ToList();
            if (shown.Count == 0 || shown.Count < limit || shown.Any(x => x.Id == local)) return shown;
            CombatantRow own = Rows.Skip(shown.Count).FirstOrDefault(x => x.Id == local);
            if (own != null) shown[shown.Count - 1] = own;
            return shown;
        }
    }
}
