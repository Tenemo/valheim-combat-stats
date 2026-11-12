using BepInEx.Configuration;
using UnityEngine;

namespace CombatStats
{
    /// <summary>
    /// Reading the mod's configured keys. Unlike <c>KeyboardShortcut.IsDown</c> other keys may
    /// be held as well, so a shortcut still fires while the player is sprinting or crouching.
    /// </summary>
    internal static class Shortcut
    {
        /// <summary>The shortcut went down this frame and no text field has the keyboard.</summary>
        public static bool IsPressed(KeyboardShortcut shortcut)
        {
            return !IsTyping() && WentDown(shortcut);
        }

        /// <summary>True while anything the player types into has the keyboard.</summary>
        public static bool IsTyping()
        {
            return global::Console.IsVisible()
                   || TextInput.IsVisible()
                   || (Chat.instance != null && Chat.instance.HasFocus());
        }

        private static bool WentDown(KeyboardShortcut shortcut)
        {
            if (shortcut.MainKey == KeyCode.None || !ZInput.GetKeyDown(shortcut.MainKey, logWarning: false))
            {
                return false;
            }

            foreach (KeyCode modifier in shortcut.Modifiers)
            {
                if (!ZInput.GetKey(modifier, logWarning: false))
                {
                    return false;
                }
            }

            return true;
        }
    }
}
