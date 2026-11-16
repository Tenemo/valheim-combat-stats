using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;

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
            Flush();
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

        // Writes waiting for the worker, in the order they were asked for; the first is the one being written.
        private static readonly Queue<(string path, string text, Action<string> failed)> queued = new Queue<(string, string, Action<string>)>();
        private static bool writing;

        /// <summary>
        /// Replaces the file like <see cref="Write"/>, on a worker after every write queued before it: replacing a history
        /// of a few hundred kilobytes held the game's frame. <paramref name="failed"/> hears what went wrong, on the worker.
        /// </summary>
        internal static void WriteLater(string path, string text, Action<string> failed)
        {
            lock (queued)
            {
                queued.Enqueue((path, text, failed));
                if (writing) return;
                writing = true;
            }
            ThreadPool.QueueUserWorkItem(state => WriteQueued());
        }

        private static void WriteQueued()
        {
            while (true)
            {
                (string path, string text, Action<string> failed) next;
                lock (queued)
                {
                    if (queued.Count == 0)
                    {
                        writing = false;
                        Monitor.PulseAll(queued);
                        return;
                    }
                    next = queued.Peek();
                }
                // Nothing may stop the worker with writes still queued.
                try
                {
                    string problem = Write(next.path, next.text);
                    if (problem != null) next.failed?.Invoke(problem);
                }
                catch (Exception error)
                {
                    try { next.failed?.Invoke("Could not write " + next.path + ": " + error.Message); } catch { }
                }
                lock (queued) queued.Dequeue();
            }
        }

        /// <summary>Waits until every queued write is done.</summary>
        internal static void Flush()
        {
            lock (queued)
                while (writing) Monitor.Wait(queued);
        }
    }
}
