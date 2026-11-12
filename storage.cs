using System;
using System.IO;
using System.Text;

namespace CombatStats
{
    /// <summary>
    /// The mod's private files under <c>BepInEx/combat-stats-state/</c>. A disk problem is
    /// returned for the caller to log instead of thrown into a setting change or a network handler.
    /// </summary>
    internal static class Storage
    {
        /// <summary>
        /// The file's text, or null when it is missing or cannot be read; only the second case
        /// sets <paramref name="problem"/>.
        /// </summary>
        internal static string Read(string path, out string problem)
        {
            problem = null;
            try
            {
                return File.Exists(path) ? File.ReadAllText(path) : null;
            }
            catch (Exception error) when (error is IOException || error is UnauthorizedAccessException)
            {
                problem = "Could not read " + path + ": " + error.Message;
                return null;
            }
        }

        /// <summary>Replaces the file in one step. Returns what went wrong, or null.</summary>
        internal static string Write(string path, string text)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                string temporary = path + ".tmp";
                File.WriteAllText(temporary, text, new UTF8Encoding(false));
                if (File.Exists(path)) File.Replace(temporary, path, null);
                else File.Move(temporary, path);
                return null;
            }
            catch (Exception error) when (error is IOException || error is UnauthorizedAccessException)
            {
                return "Could not write " + path + ": " + error.Message;
            }
        }
    }
}
