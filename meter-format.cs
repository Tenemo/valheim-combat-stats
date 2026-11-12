using System.Globalization;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using CombatStats.Model;

namespace CombatStats.Stats
{
    /// <summary>How the meter writes amounts, shares, durations and skill names.</summary>
    public static class MeterFormat
    {
        private static readonly NumberFormatInfo Grouped = GroupedNumbers();

        public static string Caption(EncounterSnapshot snapshot, string name)
        {
            if (!snapshot.HasEncounter) return "";
            var parts = new List<string>();
            if (!string.IsNullOrEmpty(name)) parts.Add(name);
            if (!string.IsNullOrEmpty(snapshot.Result) && snapshot.Result != snapshot.Name) parts.Add(snapshot.Result);
            parts.Add(Duration(snapshot.Duration));
            return string.Join(" | ", parts);
        }

        /// <summary>
        /// A localized name without the text tags the game puts in some, such as the orange around Lord Reto and
        /// Hildir's minibosses. The meter shows names as plain text, so tags would appear literally.
        /// </summary>
        public static string Plain(string text) => string.IsNullOrEmpty(text) ? text : TextTag.Replace(text, "").Trim();

        private static readonly Regex TextTag = new Regex("</?[A-Za-z#][^<>]*>", RegexOptions.CultureInvariant);

        public static string EmptyText(EncounterSnapshot snapshot, CombatChannel channel) => !snapshot.HasEncounter ? "No fights recorded." :
            channel == CombatChannel.Healing ? "No healing received in this view." :
            channel == CombatChannel.DamageTaken ? "No damage taken in this view." : "No damage dealt in this view.";

        /// <summary>Damage as the meter writes it: no decimals from ten up, one below.</summary>
        public static string Number(float value)
        {
            return value >= 10f
                ? value.ToString("#,0", Grouped)
                : value.ToString("0.#", CultureInfo.InvariantCulture);
        }

        /// <summary>A share of a total, as a whole percentage.</summary>
        public static string Percent(float share)
        {
            return (share * 100f).ToString("0", CultureInfo.InvariantCulture) + "%";
        }

        /// <summary>A fight's length as minutes and seconds.</summary>
        public static string Duration(double seconds)
        {
            return ((int)seconds / 60) + ":" + ((int)seconds % 60).ToString("00", CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// A skill as the player reads it: the game's <c>ElementalMagic</c> becomes "Elemental magic".
        /// History saved before 1.0.2 calls creature attacks and environmental damage "None".
        /// </summary>
        public static string Skill(string name)
        {
            if (name == "None")
            {
                return "Other";
            }

            var text = new StringBuilder(name.Length + 4);
            for (int index = 0; index < name.Length; index++)
            {
                char letter = name[index];
                if (index > 0 && char.IsUpper(letter) && char.IsLower(name[index - 1]))
                {
                    text.Append(' ').Append(char.ToLowerInvariant(letter));
                }
                else
                {
                    text.Append(letter);
                }
            }

            return text.ToString();
        }

        private static NumberFormatInfo GroupedNumbers()
        {
            var format = (NumberFormatInfo)CultureInfo.InvariantCulture.NumberFormat.Clone();
            format.NumberGroupSeparator = " ";
            return format;
        }
    }
}
