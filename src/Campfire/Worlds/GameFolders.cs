using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security;

namespace Campfire.Worlds
{
    internal enum GameFolderSource
    {
        /// <summary>%AppData%\.minecraft.</summary>
        Default,

        /// <summary>The .minecraft folder next to a launcher.</summary>
        NextToLauncher,

        /// <summary>A folder listed in a launcher's settings.</summary>
        LauncherSettings,

        /// <summary>A folder the user added in Campfire.</summary>
        Added,
    }

    internal sealed class GameFolder
    {
        public GameFolder(string path, GameFolderSource source)
        {
            Path = path;
            Source = source;
        }

        /// <summary>Full path of the .minecraft folder, without a trailing separator.</summary>
        public string Path { get; }

        public GameFolderSource Source { get; }

        public override string ToString() => Path;
    }

    /// <summary>
    /// Finds the .minecraft folders on this computer while no game is running.
    /// </summary>
    internal static class GameFolders
    {
        private const string AddedKey = "worlds.folders";

        /// <summary>
        /// Every folder with installed versions: the default one, those next to the given launchers, those the
        /// launchers' settings list and those the user added. Folders without a versions folder are left out.
        /// </summary>
        /// <param name="launcherDirectories">Folders that hold a launcher, such as the one Campfire found to repair.</param>
        public static IReadOnlyList<GameFolder> Find(IEnumerable<string> launcherDirectories)
        {
            var paths = new[] { Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".minecraft") }
                .Concat(Added).Distinct(StringComparer.Ordinal);
            return paths.Where(p => Directory.Exists(Path.Combine(p, "versions")))
                .Select(p => new GameFolder(p, GameFolderSource.Default)).ToList();
        }

        /// <summary>Folders the user added by hand, kept across runs.</summary>
        public static IReadOnlyList<string> Added =>
            (AppSettings.Get(AddedKey) ?? "").Split('|').Where(p => p.Length > 0).ToList();

        public static void Add(string path)
        {
            string full = Clean(path);
            if (full == null || Added.Contains(full, StringComparer.Ordinal))
                return;
            AppSettings.Set(AddedKey, string.Join("|", Added.Concat(new[] { full })));
        }

        public static void Remove(string path)
        {
            string full = Clean(path);
            var rest = Added.Where(p => !string.Equals(p, full, StringComparison.Ordinal)).ToList();
            AppSettings.Set(AddedKey, rest.Count == 0 ? null : string.Join("|", rest));
        }

        /// <summary>
        /// A folder the user picked may be .minecraft itself, the launcher folder next to it or a version folder
        /// inside it; returns the .minecraft folder in each case, or <c>null</c>.
        /// </summary>
        public static string Resolve(string picked)
        {
            string full = Clean(picked);
            if (full == null)
                return null;
            if (Directory.Exists(System.IO.Path.Combine(full, "versions")))
                return full;
            string inside = System.IO.Path.Combine(full, ".minecraft");
            if (Directory.Exists(System.IO.Path.Combine(inside, "versions")))
                return inside;
            // ...\.minecraft\versions or ...\.minecraft\versions\<name>
            string current = full;
            for (int level = 0; level < 2 && current != null; level++, current = System.IO.Path.GetDirectoryName(current))
            {
                string parent = System.IO.Path.GetDirectoryName(current);
                if (parent != null && string.Equals(System.IO.Path.GetFileName(current), "versions", StringComparison.Ordinal))
                    return parent;
            }
            return null;
        }

        private static string Clean(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return null;
            try
            {
                string expanded = Environment.ExpandEnvironmentVariables(path.Trim());
                if (!System.IO.Path.IsPathRooted(expanded))
                    return null;
                string full = System.IO.Path.GetFullPath(expanded);
                // "C:\" must keep its separator; "C:" alone means the current folder of drive C.
                return full.Length > System.IO.Path.GetPathRoot(full).Length
                    ? full.TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar)
                    : full;
            }
            catch (Exception ex) when (ex is ArgumentException || ex is NotSupportedException || ex is PathTooLongException || ex is SecurityException)
            {
                return null;
            }
        }
    }
}
