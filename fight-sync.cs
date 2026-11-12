using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using BepInEx;
using CombatStats.Collect;
using CombatStats.Model;
using CombatStats.Stats;
using CombatStats.Ui;
using Newtonsoft.Json;
using HarmonyLib;
using UnityEngine;

namespace CombatStats.Net
{
    // Jotunn refuses a connection unless server and client run the same version, so the messages
    // carry no protocol number of their own.

    /// <summary>A client's hello to the server. Its position stays on the server.</summary>
    internal sealed class PlayerIdentity
    {
        public long Id;
        public string Name = "", Character = "";
        public float X, Y, Z;
        public double Sent;
    }

    /// <summary>What every client learns about the connected players: enough to credit pets and remote attackers.</summary>
    internal sealed class KnownPlayer
    {
        public long Id;
        public string Name = "", Character = "";
    }

    internal sealed class ClockReply
    {
        public string Session = "";
        public double Echo, Time;
    }

    internal sealed class CombatBatch
    {
        public string Session = "";
        public List<Observation> Events = new List<Observation>();
    }

    internal sealed class CombatFeed
    {
        public string Session = "";
        public double Time;
        public List<Encounter> Fights = new List<Encounter>();
        public Dictionary<long, long> Redirects = new Dictionary<long, long>();
        public List<KnownPlayer> Players = new List<KnownPlayer>();
    }

    /// <summary>
    /// Carries observations from the owners of the hit characters to the server, which groups them
    /// into fights, and the shared fights back to the players they concern.
    /// </summary>
    internal sealed class FightSync : MonoBehaviour
    {
        private const string HelloRpc = "Tenemo.CombatStats.Hello.1";
        private const string ClockRpc = "Tenemo.CombatStats.Clock.1";
        private const string EventsRpc = "Tenemo.CombatStats.Events.1";
        private const string FeedRpc = "Tenemo.CombatStats.Feed.1";
        private static readonly Stopwatch Clock = Stopwatch.StartNew();
        private static readonly Func<ZRoutedRpc, long> ServerPeerOf = AccessTools.MethodDelegate<Func<ZRoutedRpc, long>>(
            AccessTools.Method(typeof(ZRoutedRpc), "GetServerPeerID"));
        private static readonly JsonSerializerSettings Json = new JsonSerializerSettings { MaxDepth = 20 };
        private static double offset;
        private ZRoutedRpc bound;
        private readonly List<Observation> pending = new List<Observation>();
        private readonly Dictionary<long, PlayerIdentity> players = new Dictionary<long, PlayerIdentity>();
        private List<KnownPlayer> known = new List<KnownPlayer>();
        private readonly Dictionary<long, Dictionary<long, long>> published = new Dictionary<long, Dictionary<long, long>>();
        private EncounterRecorder recorder = new EncounterRecorder();
        private string session = "", historyPath = "";
        private long sequence;
        private double nextSend, nextHello;
        private bool synchronized;
        internal static double Now => Clock.Elapsed.TotalSeconds + offset;

        internal void Queue(Observation report)
        {
            if (bound == null) return;
            report.Sequence = ++sequence;
            pending.Add(report);
        }

        internal DamageCollector.Source FindPlayer(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            var matching = known.Where(x => x.Name == name).ToList();
            return matching.Count == 1 ? Source(matching[0]) : null;
        }

        internal DamageCollector.Source FindCharacter(string character)
        {
            KnownPlayer player = known.FirstOrDefault(x => x.Character == character);
            return player == null ? null : Source(player);
        }
        private static DamageCollector.Source Source(KnownPlayer player) => new DamageCollector.Source { Id = player.Id, Name = player.Name };

        private void Update()
        {
            ZNet net = ZNet.instance;
            ZRoutedRpc rpc = ZRoutedRpc.instance;
            if (!net || rpc == null)
            {
                if (bound != null) EndSession();
                return;
            }
            if (!ReferenceEquals(bound, rpc)) Bind(rpc, net);
            if (Player.m_localPlayer)
            {
                string path = Path.Combine(Paths.BepInExRootPath, "combat-stats-state",
                    "world-" + net.GetWorldUID() + "-player-" + DamageCollector.LocalId() + ".dat");
                if (path != historyPath) OpenHistory(path);
                if (Clock.Elapsed.TotalSeconds >= nextHello)
                {
                    nextHello = Clock.Elapsed.TotalSeconds + (synchronized ? 2d : 0.25d);
                    Player local = Player.m_localPlayer;
                    Vector3 position = local.transform.position;
                    var identity = new PlayerIdentity { Id = local.GetPlayerID(), Name = local.GetPlayerName(),
                        Character = DamageCollector.Key(local), Sent = Clock.Elapsed.TotalSeconds,
                        X = position.x, Y = position.y, Z = position.z };
                    if (net.IsServer()) players[ZNet.GetUID()] = identity;
                    else rpc.InvokeRoutedRPC(ServerPeerOf(rpc), HelloRpc, Encode(identity));
                }
            }
            if (Clock.Elapsed.TotalSeconds < nextSend) return;
            nextSend = Clock.Elapsed.TotalSeconds + 0.25d;
            Plugin.Collector.Engagement();
            Flush(net, rpc);
            if (net.IsServer())
            {
                recorder.Tick(Now);
                Publish(net, rpc);
            }
            else if (synchronized) Plugin.History.Now = Now;
        }

        private void Bind(ZRoutedRpc rpc, ZNet net)
        {
            EndSession();
            bound = rpc;
            session = net.IsServer() ? Guid.NewGuid().ToString("N") : "";
            synchronized = net.IsServer();
            offset = 0d;
            rpc.Register<string>(HelloRpc, Hello);
            rpc.Register<string>(ClockRpc, SetClock);
            rpc.Register<string>(EventsRpc, Events);
            rpc.Register<string>(FeedRpc, Feed);
            Plugin.Debug("Fight sync registered for a new world.");
        }

        private void EndSession()
        {
            CloseHistory();
            recorder = new EncounterRecorder();
            pending.Clear(); players.Clear(); known.Clear(); published.Clear(); sequence = 0;
            bound = null; synchronized = false; nextHello = 0d; nextSend = 0d;
            Plugin.Collector?.Clear();
            DetailWindow.Instance?.Close();
        }

        private void Hello(long sender, string json)
        {
            if (!ZNet.instance.IsServer()) return;
            PlayerIdentity identity = Decode<PlayerIdentity>(json);
            ZNetPeer peer = ZNet.instance.GetPeer(sender);
            if (identity == null || peer == null || identity.Id == 0 || identity.Name == null || identity.Name.Length > 160 ||
                identity.Character != peer.m_characterID.ToString()) return;
            players[sender] = identity;
            bound.InvokeRoutedRPC(sender, ClockRpc, Encode(new ClockReply { Session = session, Echo = identity.Sent, Time = Now }));
        }

        private void SetClock(long sender, string json)
        {
            if (ZNet.instance.IsServer() || sender != ServerPeerOf(bound)) return;
            ClockReply reply = Decode<ClockReply>(json);
            if (reply == null || string.IsNullOrEmpty(reply.Session)) return;
            double elapsed = Clock.Elapsed.TotalSeconds;
            double roundTrip = elapsed - reply.Echo;
            if (roundTrip < 0d || roundTrip > 5d || double.IsNaN(reply.Time) || double.IsInfinity(reply.Time)) return;
            double adjustment = reply.Time + roundTrip * 0.5d - elapsed;
            // Keep already queued observations on the same clock after a synchronization correction.
            foreach (Observation observation in pending) observation.Time += adjustment - offset;
            offset = adjustment;
            session = reply.Session;
            synchronized = true;
        }

        private void Flush(ZNet net, ZRoutedRpc rpc)
        {
            if (pending.Count == 0) return;
            if (!net.IsServer() && !synchronized) return;
            while (pending.Count > 0)
            {
                var batch = new CombatBatch { Session = session, Events = pending.Take(128).ToList() };
                pending.RemoveRange(0, batch.Events.Count);
                if (net.IsServer()) recorder.Receive(ZNet.GetUID(), batch.Events, Now);
                else rpc.InvokeRoutedRPC(ServerPeerOf(rpc), EventsRpc, Encode(batch));
            }
        }

        private void Events(long sender, string json)
        {
            if (!ZNet.instance.IsServer() || !players.ContainsKey(sender) || ZNet.instance.GetPeer(sender) == null) return;
            CombatBatch batch = Decode<CombatBatch>(json);
            if (batch == null || batch.Session != session || batch.Events == null ||
                batch.Events.Count > 128 || batch.Events.Any(x => x == null)) return;
            recorder.Receive(sender, batch.Events, Now);
        }

        private void Publish(ZNet net, ZRoutedRpc rpc)
        {
            var connected = new HashSet<long>(net.GetPeers().Select(x => x.m_uid)) { ZNet.GetUID() };
            foreach (long departed in players.Keys.Where(x => !connected.Contains(x)).ToList())
            { players.Remove(departed); published.Remove(departed); recorder.ForgetPeer(departed); }
            known = players.Values.Select(x => new KnownPlayer { Id = x.Id, Name = x.Name, Character = x.Character }).ToList();
            const float reach = EncounterRecorder.Radius * EncounterRecorder.Radius;
            foreach (var pair in players.ToList())
            {
                PlayerIdentity player = pair.Value;
                if (!published.TryGetValue(pair.Key, out var revisions)) published[pair.Key] = revisions = new Dictionary<long, long>();
                // Finished fights are immutable except for late corrections. Do not resend the
                // whole history four times a second to every connected player.
                var feed = new CombatFeed { Session = session, Time = Now, Redirects = recorder.Redirects,
                    Players = known, Fights = recorder.Fights.Where(x =>
                        (x.Participants.ContainsKey(player.Id) || revisions.ContainsKey(x.Id) || (x.Active && EncounterRecorder.Distance(x.X, x.Y, x.Z, player.X, player.Y, player.Z) <= reach)) &&
                        (x.Active || !revisions.TryGetValue(x.Id, out long revision) || revision != x.Revision)).ToList() };
                string data = Encode(feed);
                if (pair.Key == ZNet.GetUID()) AcceptFeed(Decode<CombatFeed>(data));
                else rpc.InvokeRoutedRPC(pair.Key, FeedRpc, data);
                foreach (Encounter fight in feed.Fights) revisions[fight.Id] = fight.Revision;
                foreach (long id in revisions.Keys.Where(x => recorder.Fights.All(f => f.Id != x)).ToList()) revisions.Remove(id);
            }
        }

        private void Feed(long sender, string json)
        {
            if (ZNet.instance.IsServer() || sender != ServerPeerOf(bound) || !synchronized) return;
            CombatFeed feed = Decode<CombatFeed>(json);
            if (feed == null || feed.Session != session) return;
            AcceptFeed(feed);
        }

        private void AcceptFeed(CombatFeed feed)
        {
            if (feed == null || historyPath.Length == 0) return;
            if (!ZNet.instance.IsServer()) known = feed.Players ?? new List<KnownPlayer>();
            Plugin.History.Receive(feed.Session, feed.Time, feed.Fights, feed.Redirects);
        }

        private void OpenHistory(string path)
        {
            CloseHistory();
            historyPath = path;
            Plugin.History = CombatHistory.Load(path, out string problem);
            if (problem != null) Plugin.Log.LogError(problem);
            Plugin.History.Changed += SaveHistory;
        }

        private void CloseHistory()
        {
            SaveHistory();
            Plugin.History.Changed -= SaveHistory;
            Plugin.History = new CombatHistory();
            historyPath = "";
        }

        private static void SaveHistory()
        {
            string problem = Plugin.History.Save();
            if (problem != null) Plugin.Log.LogWarning(problem);
        }

        internal static string Encode(object value) => JsonConvert.SerializeObject(value, Json);
        private static T Decode<T>(string value) where T : class
        {
            if (value == null || value.Length > 1024 * 1024) return null;
            try { return JsonConvert.DeserializeObject<T>(value, Json); }
            catch (JsonException error) { Plugin.Log.LogWarning("Invalid combat packet: " + error.Message); return null; }
        }
    }
}
