using System;
using System.Collections.Generic;
using System.Linq;
using CombatStats.Model;
using Newtonsoft.Json;

namespace CombatStats.Stats
{
    /// <summary>Which fight a window shows.</summary>
    public enum FightView { Current, Last, Overall, History }

    public sealed class SavedHistory
    {
        public List<Encounter> Recent = new List<Encounter>();

        // Overall.End holds the summed length of every counted fight, not a clock time.
        public Encounter Overall = new Encounter { Name = "Overall", Result = "" };
        public string Session = "";

        // Server sessions in the order this player first saw them. Fight times are the server's
        // uptime, which restarts with every session, so they order fights only within one session.
        public List<string> Sessions = new List<string>();
        public double ResetTime;
        public List<string> Accounted = new List<string>();
    }

    public sealed class CombatHistory
    {
        public const int Limit = 10;
        public SavedHistory Saved = new SavedHistory();
        public List<Encounter> Active = new List<Encounter>();
        public double Now;

        /// <summary>The file this history is saved to; empty when it is not saved.</summary>
        public string FilePath { get; private set; } = "";

        public int HistoryCount => Math.Min(Limit, Saved.Recent.Count);
        public event Action Changed;
        private static bool Same(Encounter a, Encounter b) => a.Session == b.Session && a.Id == b.Id;

        /// <summary>
        /// Opens a player's saved fights. Unreadable JSON is kept beside the file and replaced by
        /// an empty history. A file that cannot be read at all is left alone, and this session's
        /// fights are not saved over it. <paramref name="problem"/> says what went wrong.
        /// </summary>
        public static CombatHistory Load(string path, out string problem)
        {
            var history = new CombatHistory();
            string text = Storage.Read(path, out problem);
            if (problem != null) return history;
            history.FilePath = path;
            if (text == null) return history;
            try
            {
                SavedHistory saved = JsonConvert.DeserializeObject<SavedHistory>(text);
                if (saved != null) history.Saved = Repair(saved);
            }
            catch (JsonException error)
            {
                string kept = path + ".invalid-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");
                string failed = Storage.Write(kept, text);
                if (failed != null)
                {
                    history.FilePath = "";
                    problem = "Could not read combat history " + path + " (" + error.Message + ") or keep a copy (" + failed + "); this session's fights are not saved.";
                }
                else problem = "Could not read combat history; preserved it at " + kept + ": " + error.Message;
            }
            return history;
        }

        /// <summary>
        /// Writes the history as it is now to its file, on a worker after the writes queued before it; a failed write is
        /// passed to <paramref name="failed"/> on that worker.
        /// </summary>
        public void Save(Action<string> failed)
        {
            if (FilePath.Length > 0) Storage.WriteLater(FilePath, JsonConvert.SerializeObject(Saved), failed);
        }

        // A hand-edited or truncated file can name a list, a fight's rows or a row's amounts and leave them null or short.
        private static SavedHistory Repair(SavedHistory saved)
        {
            saved.Recent = saved.Recent?.Where(x => x != null).ToList() ?? new List<Encounter>();
            saved.Overall = saved.Overall ?? new SavedHistory().Overall;
            saved.Session = saved.Session ?? "";
            saved.Sessions = saved.Sessions ?? new List<string>();
            saved.Accounted = saved.Accounted ?? new List<string>();
            foreach (Encounter fight in saved.Recent) Repair(fight);
            Repair(saved.Overall);
            return saved;
        }

        private static void Repair(Encounter fight)
        {
            fight.Session = fight.Session ?? "";
            fight.Name = fight.Name ?? "Combat";
            fight.Result = fight.Result ?? "";
            fight.Participants = fight.Participants ?? new Dictionary<long, string>();
            fight.Deaths = fight.Deaths ?? new Dictionary<long, int>();
            fight.Damage = Repair(fight.Damage);
            fight.Taken = Repair(fight.Taken);
            fight.Healing = Repair(fight.Healing);
        }

        private static Dictionary<long, Combatant> Repair(Dictionary<long, Combatant> rows)
        {
            rows = rows ?? new Dictionary<long, Combatant>();
            foreach (long id in rows.Where(x => x.Value == null).Select(x => x.Key).ToList()) rows.Remove(id);
            foreach (Combatant row in rows.Values)
            {
                row.Name = row.Name ?? "";
                if (row.ByKind == null || row.ByKind.Length != DamageKinds.Count) Array.Resize(ref row.ByKind, DamageKinds.Count);
                row.Targets = row.Targets ?? new Dictionary<string, float>();
                row.Skills = row.Skills ?? new Dictionary<string, float>();
                row.Pets = row.Pets ?? new Dictionary<string, float>();
            }
            return rows;
        }

        public void Receive(string session, double now, List<Encounter> fights, Dictionary<long, long> redirects)
        {
            Now = now;
            bool changed = false;
            // History saved before 1.0.2 knows only its last session.
            if (Saved.Sessions.Count == 0 && Saved.Session.Length > 0) Saved.Sessions.Add(Saved.Session);
            if (!Saved.Sessions.Contains(session)) Saved.Sessions.Add(session);
            if (Saved.Session != session) { Saved.Session = session; Saved.ResetTime = 0d; }
            foreach (long old in redirects.Keys)
            {
                Encounter previous = Saved.Recent.Find(x => x.Session == session && x.Id == old);
                if (previous == null) continue;
                AddOverall(previous, -1f);
                Saved.Recent.Remove(previous);
                Saved.Accounted.Remove(session + "/" + old);
                changed = true;
            }
            Active = fights.Where(x => x.Active).ToList();
            foreach (Encounter fight in fights)
            {
                fight.Session = session;
                if (!fight.Active && fight.End <= Saved.ResetTime) continue;
                string key = session + "/" + fight.Id;
                Encounter previous = Saved.Recent.Find(x => Same(x, fight));
                if (previous != null && previous.Revision == fight.Revision && !fight.Active) continue;
                if (previous == null && !fight.Active && Saved.Accounted.Contains(key)) continue;
                if (previous != null)
                {
                    // Replace snapshots so late reports correct the overall total rather than double it.
                    AddOverall(previous, -1f);
                    Saved.Recent.Remove(previous);
                    Saved.Accounted.Remove(key);
                }
                if (!fight.Active)
                {
                    AddOverall(fight, 1f);
                    Saved.Recent.Insert(0, fight);
                    Saved.Accounted.Add(key);
                    if (Saved.Accounted.Count > 512) Saved.Accounted.RemoveAt(0);
                    changed = true;
                }
                else if (previous != null) changed = true;
            }
            if (!Kept(session, now))
                Saved.Recent = Saved.Recent.OrderByDescending(x => Saved.Sessions.IndexOf(x.Session)).ThenByDescending(x => x.End)
                    .Where((x, index) => Keeps(x, index, session, now)).ToList();
            Saved.Sessions.RemoveAll(x => x != session && Saved.Recent.All(fight => fight.Session != x));
            if (changed) Changed?.Invoke();
        }

        private static bool Keeps(Encounter fight, int index, string session, double now) =>
            index < EncounterRecorder.CorrectionHistory || (fight.Session == session && now - fight.End <= EncounterRecorder.LateReportSeconds);

        // Whether the recent fights are already in order and all kept, which the sort and filter, being stable, would
        // leave as they are; most feeds bring no finished fight.
        private bool Kept(string session, double now)
        {
            List<Encounter> recent = Saved.Recent;
            for (int index = 0; index < recent.Count; index++)
            {
                if (!Keeps(recent[index], index, session, now)) return false;
                if (index == 0) continue;
                int order = Saved.Sessions.IndexOf(recent[index - 1].Session).CompareTo(Saved.Sessions.IndexOf(recent[index].Session));
                if (order < 0 || order == 0 && recent[index - 1].End.CompareTo(recent[index].End) < 0) return false;
            }
            return true;
        }

        private void AddOverall(Encounter fight, float factor)
        {
            foreach (CombatChannel channel in CombatChannels.All)
                EncounterRecorder.CombineRows(Saved.Overall.Rows(channel), fight.Rows(channel), factor);
            Saved.Overall.End += fight.Duration(fight.End) * factor;
        }

        public bool Reset()
        {
            if (Active.Count > 0) return false;
            Saved = new SavedHistory { Session = Saved.Session, ResetTime = Now };
            Changed?.Invoke();
            return true;
        }

        public Encounter Current(long local)
        {
            return Active.Where(x => x.Participants.ContainsKey(local)).OrderByDescending(x => x.Start).FirstOrDefault()
                ?? Active.OrderByDescending(x => x.Start).FirstOrDefault()
                ?? Saved.Recent.FirstOrDefault();
        }

        /// <summary>Whether <see cref="Current"/> finds a fight: neither list holds a null one.</summary>
        public bool HasFight => Active.Count > 0 || Saved.Recent.Count > 0;

        public EncounterSnapshot Snapshot(FightView view, CombatChannel channel, long local, int history = 0)
        {
            if (view == FightView.Overall)
            {
                if (Saved.Overall.End <= 0d && Active.Count == 0) return EncounterSnapshot.Empty;
                var overall = new Encounter { Name = "Overall", Result = "", End = Saved.Overall.End };
                foreach (CombatChannel kind in CombatChannels.All)
                    EncounterRecorder.CombineRows(overall.Rows(kind), Saved.Overall.Rows(kind));
                foreach (Encounter current in Active)
                {
                    overall.End += current.Duration(Now);
                    foreach (CombatChannel kind in CombatChannels.All)
                        EncounterRecorder.CombineRows(overall.Rows(kind), current.Rows(kind));
                }
                return EncounterSnapshot.From(overall, channel, Now);
            }
            Encounter chosen = view == FightView.Current ? Current(local) :
                Saved.Recent.ElementAtOrDefault(view == FightView.Last ? 0 : Math.Min(Limit - 1, Math.Max(0, history)));
            return EncounterSnapshot.From(chosen, channel, Now);
        }
    }
}
