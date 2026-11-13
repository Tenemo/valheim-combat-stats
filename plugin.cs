using BepInEx;
using BepInEx.Logging;
using CombatStats.Collect;
using CombatStats.Net;
using CombatStats.Stats;
using CombatStats.Ui;
using HarmonyLib;
using Jotunn.Utils;
using UnityEngine;
using UnityEngine.Rendering;

namespace CombatStats
{
    [BepInPlugin(Id, "Combat stats", Version)]
    [BepInDependency(Jotunn.Main.ModGuid)]
    [BepInIncompatibility("muindor.CombatStats")]
    [NetworkCompatibility(CompatibilityLevel.EveryoneMustHaveMod, VersionStrictness.Patch)]
    public sealed class Plugin : BaseUnityPlugin
    {
        public const string Id = "tenemo.CombatStats";
        public const string Version = "1.0.8";
        internal static ManualLogSource Log;
        internal static ModConfig Settings;
        internal static DamageCollector Collector;
        internal static CombatHistory History = new CombatHistory();
        internal static FightSync Sync;
        private Harmony harmony;

        private void Awake()
        {
            Log = Logger;
            Settings = new ModConfig(Config);
            Collector = new DamageCollector();
            harmony = new Harmony(Id);
            try
            {
                harmony.PatchAll(typeof(Plugin).Assembly);
            }
            catch
            {
                // Half the hooks would report half the damage; fail with none installed.
                harmony.UnpatchSelf();
                throw;
            }

            Sync = gameObject.AddComponent<FightSync>();
            bool headless = SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null;
            if (!headless)
            {
                gameObject.AddComponent<CompactMeter>();
                gameObject.AddComponent<DetailWindow>();
            }
            Log.LogInfo("Combat stats " + Version + " loaded: shared fights, whole-fight DPS and effect attribution.");
        }

        private void OnDestroy() => harmony?.UnpatchSelf();
        internal static void Debug(string text) { if (Settings.IsDebug.Value) Log.LogInfo(text); }
    }
}
