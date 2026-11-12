using System;

namespace CombatStats.Model
{
    /// <summary>
    /// The damage types the game splits a hit into. The values are indexes into the per-hit
    /// arrays the meter passes around and saves, so their order is part of the wire format and
    /// of saved history and must not change.
    /// </summary>
    public enum DamageKind
    {
        Blunt = 0,
        Slash = 1,
        Pierce = 2,
        Chop = 3,
        Pickaxe = 4,
        Fire = 5,
        Frost = 6,
        Lightning = 7,
        Poison = 8,
        Spirit = 9,

        /// <summary>
        /// The game's type-less <c>damage</c> field, what goes through resistances untouched, and
        /// its <c>nonPlayer</c> field, damage only creatures take. Rare on weapons, common on falls
        /// and on a few effects, and counted so that the totals of the meter match the health the
        /// target really lost.
        /// </summary>
        Generic = 10,
    }

    /// <summary>Names and colors of the damage kinds, in kind order.</summary>
    public static class DamageKinds
    {
        /// <summary>How many kinds there are; the length of every per-hit array.</summary>
        public const int Count = 11;

        private static readonly string[] Keys =
        {
            "$inventory_blunt", "$inventory_slash", "$inventory_pierce", "$inventory_chop",
            "$inventory_pickaxe", "$inventory_fire", "$inventory_frost", "$inventory_lightning",
            "$inventory_poison", "$inventory_spirit", "$inventory_damage",
        };

        private static readonly string[] Names =
        {
            "Blunt", "Slash", "Pierce", "Chop", "Pickaxe", "Fire",
            "Frost", "Lightning", "Poison", "Spirit", "Damage",
        };

        private static readonly string[] Colors =
        {
            "#8a8f9c", "#cfd3d8", "#c9a85b", "#74603f", "#6e7a72", "#bb7644",
            "#7ea3b4", "#988dba", "#829a5d", "#a3cbb3", "#b98c8f",
        };

        /// <summary>
        /// The key the game itself uses for this kind in item tooltips, so the meter shows the
        /// name in the player's own language without shipping a single translation.
        /// </summary>
        public static string LocalizationKey(DamageKind kind) => Keys[Index(kind)];

        /// <summary>What to show when the game has no word for the key (a stripped localization).</summary>
        public static string EnglishName(DamageKind kind) => Names[Index(kind)];

        /// <summary>
        /// The default color of the kind, muted on purpose: a segmented bar should read as one
        /// object on a dark HUD, not as a row of warning lights. Neighboring physical kinds still
        /// differ in hue or lightness so a bar's segments can be told apart. Overridable in the config.
        /// </summary>
        public static string ColorHex(DamageKind kind) => Colors[Index(kind)];

        private static int Index(DamageKind kind) =>
            kind >= DamageKind.Blunt && kind <= DamageKind.Generic ? (int)kind : throw new ArgumentOutOfRangeException(nameof(kind));
    }
}
