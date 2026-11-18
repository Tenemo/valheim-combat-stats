using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
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
        private static readonly List<long> NetworkUsers = AccessTools.StaticFieldRefAccess<List<long>>(typeof(ZDOID), "m_userIDs");
        private static readonly JsonSerializerSettings Json = new JsonSerializerSettings { MaxDepth = 20 };
        // The serializer and text buffer JsonConvert would build for every message, kept; only the main thread encodes and
        // decodes. JsonConvert checks for text after the value when reading, so this one does too.
        private static readonly JsonSerializer Serializer = ReadingSerializer();
        private static readonly StringWriter Text = new StringWriter(new StringBuilder(256), CultureInfo.InvariantCulture);
        private static double offset;
        private ZRoutedRpc bound;
        private readonly List<Observation> pending = new List<Observation>();
        private readonly Dictionary<long, PlayerIdentity> players = new Dictionary<long, PlayerIdentity>();
        private List<KnownPlayer> known = new List<KnownPlayer>();
        private readonly Dictionary<long, Dictionary<long, long>> published = new Dictionary<long, Dictionary<long, long>>();
        private EncounterRecorder recorder = new EncounterRecorder();
        private ReportOwnership ownership = new ReportOwnership();
        private readonly Dictionary<long, Queue<(Observation hit, double received)>> incoming =
            new Dictionary<long, Queue<(Observation, double)>>();
        private readonly Dictionary<int, Type> characterTypes = new Dictionary<int, Type>();
        private string session = "", historyPath = "";
        // The world and player historyPath was built for.
        private long historyWorld, historyPlayer;
        private Player avatar;
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
            // In the frame the game removes the character to respawn it, the character is still there but its id reads 0:
            // that is no other player, and opening a history for it would lose the fights finished before the new one.
            if (Player.m_localPlayer && DamageCollector.LocalId() != 0)
            {
                long world = net.GetWorldUID(), player = DamageCollector.LocalId();
                if (historyPath.Length == 0 || world != historyWorld || player != historyPlayer)
                {
                    string path = Path.Combine(Paths.BepInExRootPath, "combat-stats-state", "world-" + world + "-player-" + player + ".dat");
                    if (path != historyPath) OpenHistory(path);
                    historyWorld = world;
                    historyPlayer = player;
                }
                Player local = Player.m_localPlayer;
                if (avatar != local && !local.IsDead())
                {
                    avatar = local;
                    nextHello = 0d;
                    Queue(new Observation { Kind = ObservationKind.Spawn, Time = Now, Victim = player, Target = DamageCollector.Key(local) });
                }
                if (Clock.Elapsed.TotalSeconds >= nextHello)
                {
                    nextHello = Clock.Elapsed.TotalSeconds + (synchronized ? 2d : 0.25d);
                    Vector3 position = local.transform.position;
                    var identity = new PlayerIdentity { Id = local.GetPlayerID(), Name = local.GetPlayerName(),
                        Character = DamageCollector.Key(local), Sent = Clock.Elapsed.TotalSeconds,
                        X = position.x, Y = position.y, Z = position.z };
                    if (net.IsServer()) players[ZNet.GetUID()] = identity;
                    else net.GetServerPeer()?.m_rpc.Invoke(HelloRpc, Encode(identity));
                }
            }
            if (Clock.Elapsed.TotalSeconds < nextSend) return;
            nextSend = Clock.Elapsed.TotalSeconds + 0.25d;
            Plugin.Collector.Engagement();
            Flush(net);
            if (net.IsServer())
            {
                ownership.Expire(Now);
                foreach (var pair in incoming)
                    if (net.GetPeer(pair.Key) != null) CollectAuthorized(pair.Key, pair.Value);
                recorder.Tick(Now);
                Publish(net);
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
            Plugin.Debug("Fight sync started for a new world.");
        }

        private void EndSession()
        {
            CloseHistory();
            recorder = new EncounterRecorder();
            ownership = new ReportOwnership();
            incoming.Clear(); characterTypes.Clear();
            avatar = null;
            pending.Clear(); players.Clear(); known.Clear(); published.Clear(); sequence = 0;
            bound = null; synchronized = false; nextHello = 0d; nextSend = 0d;
            Plugin.Collector?.Clear();
            DetailWindow.Instance?.Close();
        }

        // Bind reports and replies to their actual game connection.
        [HarmonyPatch(typeof(ZNet), "OnNewConnection")]
        private static class Connection
        {
            private static void Postfix(ZNet __instance, ZNetPeer __0)
            {
                var peer = __0;
                if (__instance.IsServer())
                {
                    peer.m_rpc.Register<string>(HelloRpc, (rpc, json) => { if (peer.IsReady()) Plugin.Sync.Hello(peer, json); });
                    peer.m_rpc.Register<string>(EventsRpc, (rpc, json) => { if (peer.IsReady()) Plugin.Sync.Events(peer.m_uid, json); });
                }
                else if (peer.m_server)
                {
                    peer.m_rpc.Register<string>(ClockRpc, (rpc, json) => Plugin.Sync.SetClock(json));
                    peer.m_rpc.Register<string>(FeedRpc, (rpc, json) => Plugin.Sync.Feed(json));
                }
            }
        }

        private void Hello(ZNetPeer peer, string json)
        {
            if (!ZNet.instance.IsServer()) return;
            PlayerIdentity identity = Decode<PlayerIdentity>(json);
            if (identity == null || identity.Id == 0 || identity.Name == null || identity.Name.Length > 160 ||
                identity.Character != peer.m_characterID.ToString()) return;
            var data = ZDOMan.instance.GetZDO(peer.m_characterID);
            if (data == null || data.GetLong(ZDOVars.s_playerID) != identity.Id) return;
            players[peer.m_uid] = identity;
            peer.m_rpc.Invoke(ClockRpc, Encode(new ClockReply { Session = session, Echo = identity.Sent, Time = Now }));
        }

        private void SetClock(string json)
        {
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

        private void Flush(ZNet net)
        {
            if (pending.Count == 0) return;
            if (!net.IsServer() && !synchronized) return;
            while (pending.Count > 0)
            {
                var batch = new CombatBatch { Session = session, Events = pending.Take(128).ToList() };
                pending.RemoveRange(0, batch.Events.Count);
                if (net.IsServer()) recorder.Receive(ZNet.GetUID(), batch.Events, Now);
                else net.GetServerPeer()?.m_rpc.Invoke(EventsRpc, Encode(batch));
            }
        }

        private void Events(long sender, string json)
        {
            if (!ZNet.instance.IsServer() || !players.ContainsKey(sender) || ZNet.instance.GetPeer(sender) == null) return;
            CombatBatch batch = Decode<CombatBatch>(json);
            if (batch == null || batch.Session != session || batch.Events == null ||
                batch.Events.Count > 128 || batch.Events.Any(x => x == null)) return;
            if (!incoming.TryGetValue(sender, out var queue)) incoming[sender] = queue = new Queue<(Observation, double)>();
            double now = Now;
            foreach (var hit in batch.Events)
                if (queue.Count < 4096 && hit.Valid() && hit.Time >= now - EncounterRecorder.LateReportSeconds && hit.Time <= now + 2d)
                    queue.Enqueue((hit, now));
            CollectAuthorized(sender, queue);
        }

        // A combat message can arrive just before the game's own object replication. Keep the peer's
        // ordering during that short wait, including a damage-over-time application followed by a tick.
        private void CollectAuthorized(long sender, Queue<(Observation hit, double received)> queue)
        {
            List<Observation> accepted = null;
            double now = Now;
            while (queue.Count > 0)
            {
                var entry = queue.Peek();
                var hit = entry.hit;
                var data = TargetData(hit.Target);
                long victim = 0;
                long owner = CharacterVictim(data, out victim) ? data.GetOwner() : 0;
                if (ownership.Allows(hit.Target, sender, hit.Victim, hit.Time, owner, victim))
                    (accepted ??= new List<Observation>()).Add(hit);
                else if (now - entry.received < 2d) break;
                queue.Dequeue();
            }
            if (accepted != null) recorder.Receive(sender, accepted, now);
        }

        private static ZDO TargetData(string key)
        {
            int split = key.IndexOf(':');
            if (split <= 0 || !long.TryParse(key.Substring(0, split), NumberStyles.Integer, CultureInfo.InvariantCulture, out long user) ||
                !uint.TryParse(key.Substring(split + 1), NumberStyles.None, CultureInfo.InvariantCulture, out uint id) ||
                !NetworkUsers.Contains(user)) return null;
            // Constructing a ZDOID interns its user in the game. Only construct server-known IDs.
            var target = new ZDOID(user, id);
            return target.ToString() == key ? ZDOMan.instance.GetZDO(target) : null;
        }

        private bool CharacterVictim(ZDO data, out long victim)
        {
            victim = 0;
            if (data == null || !ZNetScene.instance) return false;
            int hash = data.GetPrefab();
            if (!characterTypes.TryGetValue(hash, out Type type))
            {
                var prefab = ZNetScene.instance.GetPrefab(hash);
                if (!prefab) return false;
                type = prefab.GetComponent<Character>()?.GetType();
                characterTypes[hash] = type;
            }
            if (type == null) return false;
            if (!typeof(Player).IsAssignableFrom(type)) return true;
            victim = data.GetLong(ZDOVars.s_playerID);
            return victim != 0;
        }

        private void Retire(ZDO data, long next)
        {
            if (!ZNet.instance || !ZNet.instance.IsServer() || data == null) return;
            long previous = data.GetOwner();
            if (previous != next && CharacterVictim(data, out long victim))
                ownership.Relinquished(data.m_uid.ToString(), previous, victim, Now);
        }

        [HarmonyPatch(typeof(ZDO), "SetOwnerInternal")]
        private static class Transfer
        {
            private static void Prefix(ZDO __instance, long __0) => Plugin.Sync?.Retire(__instance, __0);
        }

        [HarmonyPatch(typeof(ZDOMan), "HandleDestroyedZDO")]
        private static class Destruction
        {
            private static void Prefix(ZDOMan __instance, ZDOID __0) => Plugin.Sync?.Retire(__instance.GetZDO(__0), 0);
        }

        private void Publish(ZNet net)
        {
            var connected = new HashSet<long>(net.GetPeers().Select(x => x.m_uid)) { ZNet.GetUID() };
            foreach (long departed in players.Keys.Where(x => !connected.Contains(x)).ToList())
            { players.Remove(departed); published.Remove(departed); incoming.Remove(departed); recorder.ForgetPeer(departed); }
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
                else net.GetPeer(pair.Key)?.m_rpc.Invoke(FeedRpc, data);
                foreach (Encounter fight in feed.Fights) revisions[fight.Id] = fight.Revision;
                foreach (long id in revisions.Keys.Where(x => recorder.Fights.All(f => f.Id != x)).ToList()) revisions.Remove(id);
            }
        }

        private void Feed(string json)
        {
            if (!synchronized) return;
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

        private static void SaveHistory() => Plugin.History.Save(problem => Plugin.Log.LogWarning(problem));

        // A history write still queued when the game closes is finished first.
        private void OnApplicationQuit() => Storage.Flush();

        private static JsonSerializer ReadingSerializer()
        {
            var serializer = JsonSerializer.Create(Json);
            serializer.CheckAdditionalContent = true;
            return serializer;
        }

        internal static string Encode(object value)
        {
            // JsonConvert would also apply settings another mod made the default.
            if (JsonConvert.DefaultSettings != null) return JsonConvert.SerializeObject(value, Json);
            Text.GetStringBuilder().Clear();
            using (var writer = new JsonTextWriter(Text) { CloseOutput = false })
            {
                writer.Formatting = Serializer.Formatting;
                Serializer.Serialize(writer, value);
            }
            return Text.ToString();
        }

        private static T Decode<T>(string value) where T : class
        {
            if (value == null || value.Length > 1024 * 1024) return null;
            try
            {
                if (JsonConvert.DefaultSettings != null) return JsonConvert.DeserializeObject<T>(value, Json);
                using (var reader = new JsonTextReader(new StringReader(value))) return (T)Serializer.Deserialize(reader, typeof(T));
            }
            catch (JsonException error) { Plugin.Log.LogWarning("Invalid combat packet: " + error.Message); return null; }
        }
    }
}
