using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using CombatStats.Model;
using CombatStats.Net;
using CombatStats.Stats;
using HarmonyLib;
using UnityEngine;

namespace CombatStats.Collect
{
    internal sealed class DamageCollector
    {
        internal sealed class Source
        {
            internal long Id;
            internal string Name = "", Pet = "";
        }
        internal sealed class DamageState
        {
            internal float Health;
            internal bool Valid;
            internal float[] Raw;
        }
        internal sealed class DotState
        {
            internal float Before, Amount;
            internal int Kind;
        }
        internal sealed class RpcContext
        {
            internal Character Target;
            internal HitData Hit;
        }
        private sealed class EffectKinds { internal readonly HashSet<int> Values = new HashSet<int>(); }

        /// <summary>An enemy this game owns and reports engagement for, and what it last reported.</summary>
        private sealed class Tracked
        {
            internal Character Character;
            internal bool Reported;
            internal long Actor;
            internal double Sent;
        }
        private readonly Dictionary<string, Tracked> tracked = new Dictionary<string, Tracked>();
        private readonly ConditionalWeakTable<Character, Source> petOwners = new ConditionalWeakTable<Character, Source>();
        private readonly ConditionalWeakTable<StatusEffect, EffectKinds> effectKinds = new ConditionalWeakTable<StatusEffect, EffectKinds>();
        internal static RpcContext CurrentHit;
        internal static StatusEffect CurrentEffect;
        private static readonly AccessTools.FieldRef<StatusEffect, Character> EffectCharacter = AccessTools.FieldRefAccess<StatusEffect, Character>("m_character");
        private static readonly AccessTools.FieldRef<SE_Burning, float> FireLeft = AccessTools.FieldRefAccess<SE_Burning, float>("m_fireDamageLeft");
        private static readonly AccessTools.FieldRef<SE_Burning, float> SpiritLeft = AccessTools.FieldRefAccess<SE_Burning, float>("m_spiritDamageLeft");
        private static readonly AccessTools.FieldRef<SE_Poison, float> PoisonLeft = AccessTools.FieldRefAccess<SE_Poison, float>("m_damageLeft");
        private static readonly AccessTools.FieldRef<AnimalAI, Character> AnimalTarget = AccessTools.FieldRefAccess<AnimalAI, Character>("m_target");

        internal static long LocalId() => Player.m_localPlayer ? Player.m_localPlayer.GetPlayerID() : 0L;
        internal static string Key(Character character) => character.GetZDOID().ToString();
        internal static string Name(Character character) => character is Player player ? player.GetPlayerName() : character.m_name;
        internal static bool Owns(Character character) => character && character.IsOwner();

        private Source Actor(Character actor)
        {
            if (!actor) return new Source();
            if (actor is Player player) return new Source { Id = player.GetPlayerID(), Name = player.GetPlayerName() };
            if (!actor.IsTamed()) return new Source();
            var ai = actor.GetBaseAI() as MonsterAI;
            GameObject following = ai ? ai.GetFollowTarget() : null;
            var owner = following ? following.GetComponent<Player>() : null;
            if (owner)
            {
                var source = new Source { Id = owner.GetPlayerID(), Name = owner.GetPlayerName(), Pet = Name(actor) };
                petOwners.Remove(actor);
                petOwners.Add(actor, source);
                return source;
            }
            if (petOwners.TryGetValue(actor, out Source known)) return known;
            // Vanilla persists a follow name. Resolve it only when it identifies one connected character.
            var view = actor.GetComponent<ZNetView>();
            string saved = view && view.IsValid() ? view.GetZDO().GetString("follow", "") : "";
            Source resolved = Plugin.Sync.FindPlayer(saved);
            if (resolved != null)
            {
                resolved.Pet = Name(actor);
                petOwners.Add(actor, resolved);
                return resolved;
            }
            return new Source { Id = Combatant.UnownedPetId((uint)Key(actor).GetStableHashCode()), Name = Name(actor), Pet = Name(actor) };
        }

        private Observation Create(Character target, HitData hit)
        {
            if (!Owns(target) || (!target.IsPlayer() && target.IsTamed())) return null;
            Character attacker = hit?.GetAttacker();
            if (attacker && attacker.IsPlayer() && target.IsPlayer()) return null;
            Source source = Actor(attacker);
            if (!attacker && hit != null && !hit.m_attacker.IsNone())
            {
                Source remote = Plugin.Sync.FindCharacter(hit.m_attacker.ToString());
                if (remote != null) source = remote;
            }
            var position = target.transform.position;
            var result = new Observation { Time = FightSync.Now, Target = Key(target), TargetName = Name(target),
                Actor = source.Id, ActorName = source.Name, Pet = source.Pet, Skill = SkillOf(hit, attacker),
                Victim = target is Player victim ? victim.GetPlayerID() : 0L, X = position.x, Y = position.y, Z = position.z,
                Boss = target.IsBoss() };
            if (!target.IsPlayer()) Track(result.Target, target);
            else if (attacker && !attacker.IsPlayer() && !attacker.IsTamed())
            {
                result.Enemy = Key(attacker); result.EnemyName = Name(attacker); result.Boss = attacker.IsBoss();
                if (attacker.IsOwner()) Track(result.Enemy, attacker);
            }
            return result;
        }

        // Creatures attack without a skill; so do falls and other environmental damage.
        private static string SkillOf(HitData hit, Character attacker)
        {
            if (hit == null) return "Other";
            if (hit.m_skill != Skills.SkillType.None) return hit.m_skill.ToString();
            return attacker && !attacker.IsPlayer() ? "Creature attack" : "Other";
        }

        private void Track(string key, Character character)
        {
            if (!tracked.TryGetValue(key, out Tracked entry) || entry.Character != character)
                tracked[key] = new Tracked { Character = character };
        }

        internal void Activity(Character target, HitData hit)
        {
            if (!target || target.IsDead() || target.IsTeleporting() || hit == null) return;
            Observation activity = Create(target, hit);
            if (activity == null || (activity.Actor == 0 && activity.Enemy.Length == 0)) return;
            activity.Kind = ObservationKind.Activity;
            Plugin.Sync.Queue(activity);
        }

        internal void TrackAttacker(Character target, HitData hit)
        {
            if (!target || hit == null || (!target.IsPlayer() && !target.IsTamed())) return;
            Character attacker = hit.GetAttacker();
            // The victim's owner applies the hit. The creature's owner must still report its AI
            // engagement when the creature starts combat before any player has struck it.
            if (attacker && !attacker.IsPlayer() && !attacker.IsTamed() && attacker.IsOwner()) Track(Key(attacker), attacker);
        }

        internal void Applied(Character target, HitData hit, DamageState state)
        {
            if (!state.Valid || hit == null) return;
            Observation report = Create(target, hit);
            if (report == null) return;
            float[] final = Values(hit);
            float total = final.Sum();
            if (total <= 0.1f) return;
            float effective = Math.Min(total, Math.Max(0f, state.Health));
            float fraction = effective / total;
            for (int kind = 0; kind < final.Length; kind++) final[kind] *= fraction;
            report.Kind = ObservationKind.Damage;
            report.Amounts = final;
            report.Overkill = total - effective;
            report.Periodic = CurrentEffect is SE_Burning || CurrentEffect is SE_Poison;
            if (report.Actor == 0 && report.Victim == 0 && !report.Periodic) return;
            if (report.Periodic) report.Consumed = state.Raw;
            Plugin.Sync.Queue(report);
        }

        internal void Death(Character target)
        {
            Observation report = Create(target, null);
            if (report == null) return;
            report.Kind = ObservationKind.Death;
            Plugin.Sync.Queue(report);
            tracked.Remove(report.Target);
        }

        internal void Healed(Character target, float before)
        {
            if (!Owns(target) || !(target is Player player)) return;
            float gained = target.GetHealth() - before;
            if (gained <= 0f) return;
            Observation report = Create(target, null);
            report.Kind = ObservationKind.Healing;
            report.ActorName = player.GetPlayerName();
            report.Amounts[(int)DamageKind.Generic] = gained;
            Plugin.Sync.Queue(report);
        }

        private Source EngagedActor(Character target)
        {
            BaseAI ai = target.GetBaseAI();
            if (!ai || !ai.IsAlerted()) return null;
            Character enemy = ai is AnimalAI animal ? AnimalTarget(animal) : ai.GetTargetCreature();
            if (!enemy || enemy.IsDead()) return null;
            Source source = Actor(enemy);
            return source.Id != 0 ? source : null;
        }

        /// <summary>
        /// Reports whom each tracked enemy fights: at once when that changes, otherwise as a
        /// keepalive, so a long fight without hits stays open.
        /// </summary>
        internal void Engagement()
        {
            // A newly spawned or newly alerted enemy can join a fight before either side lands a hit.
            foreach (Character target in Character.GetAllCharacters())
            {
                if (!Owns(target) || target.IsPlayer() || target.IsTamed() || target.IsDead()) continue;
                string key = Key(target);
                if (!tracked.ContainsKey(key) && EngagedActor(target) != null) Track(key, target);
            }
            double now = FightSync.Now;
            foreach (var entry in tracked.ToList())
            {
                Character target = entry.Value.Character;
                // Gone, owned by another game or tamed: no longer an enemy this game reports.
                if (!Owns(target) || target.IsTamed()) { tracked.Remove(entry.Key); continue; }
                if (target.IsDead()) { Death(target); continue; }
                Source source = EngagedActor(target);
                long actor = source?.Id ?? 0L;
                if (entry.Value.Reported && actor == entry.Value.Actor && now - entry.Value.Sent < EncounterRecorder.EngagementKeepAlive) continue;
                entry.Value.Reported = true; entry.Value.Actor = actor; entry.Value.Sent = now;
                Observation report = Create(target, null);
                report.Kind = ObservationKind.Engagement;
                report.Actor = actor; report.ActorName = source?.Name ?? "";
                report.Engaged = source != null;
                Plugin.Sync.Queue(report);
                if (!report.Engaged) tracked.Remove(entry.Key);
            }
        }

        internal DotState BeforeDot(StatusEffect effect, float amount, int kind)
        {
            return new DotState { Before = Remaining(effect, kind), Amount = amount, Kind = kind };
        }

        private static float Remaining(StatusEffect effect, int kind) =>
            effect is SE_Poison poison ? PoisonLeft(poison) : kind == (int)DamageKind.Fire ? FireLeft((SE_Burning)effect) : SpiritLeft((SE_Burning)effect);

        internal void AddedDot(StatusEffect effect, DotState state, bool accepted)
        {
            Character target = EffectCharacter(effect);
            if (!accepted || !Owns(target)) return;
            HitData source = CurrentHit != null && CurrentHit.Target == target ? CurrentHit.Hit : null;
            Observation report = Create(target, source);
            if (report == null) return;
            report.Kind = ObservationKind.DotAdded;
            report.DotKind = state.Kind;
            report.DotBefore = state.Before;
            report.ReplaceDot = effect is SE_Poison;
            report.DotAmount = report.ReplaceDot ? Remaining(effect, state.Kind) : Math.Max(0f, Remaining(effect, state.Kind) - state.Before);
            effectKinds.GetOrCreateValue(effect).Values.Add(state.Kind);
            Plugin.Sync.Queue(report);
        }

        internal void RemovedDot(StatusEffect effect)
        {
            if (!effect || !effectKinds.TryGetValue(effect, out EffectKinds kinds)) return;
            Character target = EffectCharacter(effect);
            foreach (int kind in kinds.Values)
            {
                Observation report = Create(target, null);
                if (report == null) continue;
                report.Kind = ObservationKind.DotCleared; report.DotKind = kind;
                Plugin.Sync.Queue(report);
            }
            effectKinds.Remove(effect);
        }

        internal void Clear() => tracked.Clear();

        internal static float[] Values(HitData hit)
        {
            HitData.DamageTypes d = hit.m_damage;
            // Vanilla's total includes nonPlayer damage. It shares the type-less slot, so the wire format keeps eleven kinds.
            return new[] { d.m_blunt, d.m_slash, d.m_pierce, d.m_chop, d.m_pickaxe, d.m_fire, d.m_frost,
                d.m_lightning, d.m_poison, d.m_spirit, Math.Max(0f, d.m_damage) + Math.Max(0f, d.m_nonPlayer) }.Select(x => Math.Max(0f, x)).ToArray();
        }
    }

    [HarmonyPatch(typeof(Character), nameof(Character.Damage))]
    internal static class ObserveOutgoing
    {
        private static void Prefix(Character __instance, HitData hit) => Plugin.Collector.TrackAttacker(__instance, hit);
    }

    [HarmonyPatch(typeof(Character), "RPC_Damage")]
    internal static class ObserveContact
    {
        private static void Prefix(Character __instance, HitData hit, out DamageCollector.RpcContext __state)
        {
            __state = DamageCollector.CurrentHit;
            if (!DamageCollector.Owns(__instance)) return;
            DamageCollector.CurrentHit = new DamageCollector.RpcContext { Target = __instance, Hit = hit };
            Plugin.Collector.Activity(__instance, hit);
        }
        private static void Finalizer(DamageCollector.RpcContext __state) => DamageCollector.CurrentHit = __state;
    }

    [HarmonyPatch(typeof(Character), nameof(Character.ApplyDamage))]
    internal static class ObserveDamage
    {
        [HarmonyPriority(Priority.Last)]
        private static void Prefix(Character __instance, HitData hit, out DamageCollector.DamageState __state)
        {
            __state = new DamageCollector.DamageState { Valid = DamageCollector.Owns(__instance) && !__instance.IsDead() &&
                !__instance.IsTeleporting() && !__instance.InCutscene() && !__instance.IsDebugFlying() && !CinematicsManager.IsPlaying(),
                Health = __instance.GetHealth(), Raw = hit == null ? new float[DamageKinds.Count] : DamageCollector.Values(hit) };
        }
        private static void Postfix(Character __instance, HitData hit, DamageCollector.DamageState __state) => Plugin.Collector.Applied(__instance, hit, __state);
    }

    [HarmonyPatch(typeof(Character), nameof(Character.OnDeath))]
    internal static class ObserveDeath
    {
        private static void Prefix(Character __instance) => Plugin.Collector.Death(__instance);
    }

    // The game's Player.OnDeath never calls Character.OnDeath.
    [HarmonyPatch(typeof(Player), nameof(Player.OnDeath))]
    internal static class ObservePlayerDeath
    {
        private static void Prefix(Player __instance) => Plugin.Collector.Death(__instance);
    }

    [HarmonyPatch(typeof(Character), "RPC_Heal")]
    internal static class ObserveHealing
    {
        private static void Prefix(Character __instance, out float __state) => __state = __instance.GetHealth();
        private static void Postfix(Character __instance, float __state) => Plugin.Collector.Healed(__instance, __state);
    }

    [HarmonyPatch]
    internal static class ObserveTick
    {
        private static IEnumerable<System.Reflection.MethodBase> TargetMethods()
        {
            yield return AccessTools.Method(typeof(SE_Burning), nameof(SE_Burning.UpdateStatusEffect));
            yield return AccessTools.Method(typeof(SE_Poison), nameof(SE_Poison.UpdateStatusEffect));
        }
        private static void Prefix(StatusEffect __instance, out StatusEffect __state)
        { __state = DamageCollector.CurrentEffect; DamageCollector.CurrentEffect = __instance; }
        private static void Finalizer(StatusEffect __state) => DamageCollector.CurrentEffect = __state;
    }

    [HarmonyPatch(typeof(SE_Burning), nameof(SE_Burning.AddFireDamage))]
    internal static class ObserveFire
    {
        private static void Prefix(SE_Burning __instance, float __0, out DamageCollector.DotState __state) =>
            __state = Plugin.Collector.BeforeDot(__instance, __0, (int)DamageKind.Fire);
        private static void Postfix(SE_Burning __instance, bool __result, DamageCollector.DotState __state) =>
            Plugin.Collector.AddedDot(__instance, __state, __result);
    }

    [HarmonyPatch(typeof(SE_Burning), nameof(SE_Burning.AddSpiritDamage))]
    internal static class ObserveSpirit
    {
        private static void Prefix(SE_Burning __instance, float __0, out DamageCollector.DotState __state) =>
            __state = Plugin.Collector.BeforeDot(__instance, __0, (int)DamageKind.Spirit);
        private static void Postfix(SE_Burning __instance, bool __result, DamageCollector.DotState __state) =>
            Plugin.Collector.AddedDot(__instance, __state, __result);
    }

    [HarmonyPatch(typeof(SE_Poison), nameof(SE_Poison.AddDamage))]
    internal static class ObservePoison
    {
        private static void Prefix(SE_Poison __instance, float __0, out DamageCollector.DotState __state) =>
            __state = Plugin.Collector.BeforeDot(__instance, __0, (int)DamageKind.Poison);
        private static void Postfix(SE_Poison __instance, DamageCollector.DotState __state) =>
            Plugin.Collector.AddedDot(__instance, __state, __state.Amount >= __state.Before);
    }

    [HarmonyPatch(typeof(SEMan), nameof(SEMan.RemoveStatusEffect), new[] { typeof(StatusEffect), typeof(bool) })]
    internal static class ObserveEffectRemoval
    {
        private static void Postfix(StatusEffect __0, bool __result) { if (__result) Plugin.Collector.RemovedDot(__0); }
    }
}
