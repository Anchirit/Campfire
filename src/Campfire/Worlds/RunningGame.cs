using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Campfire.Link.Minecraft;

namespace Campfire.Worlds
{
    /// <summary>
    /// A Minecraft game running on this computer and the folder it keeps its worlds in, read from its command line.
    /// </summary>
    /// <remarks>
    /// The game folder is read from --gameDir. Only that value and the version path are read; the access
    /// token on the same command line is never kept.
    /// </remarks>
    internal sealed class RunningGame
    {
        private readonly IReadOnlyList<string> _arguments;

        private RunningGame(int processId, string gameDirectory, IReadOnlyList<string> arguments)
        {
            ProcessId = processId;
            GameDirectory = gameDirectory;
            _arguments = arguments;
        }

        public int ProcessId { get; }

        /// <summary>Full path of the game folder; its saves folder holds the worlds.</summary>
        public string GameDirectory { get; }

        public string SavesDirectory => Path.Combine(GameDirectory, "saves");

        /// <summary>Whether the game was started from <paramref name="version"/>, judged by the version folder on its class path.</summary>
        public bool Runs(InstalledVersion version)
        {
            // Only the part from "versions" on is compared.
            string marker = Path.DirectorySeparatorChar + "versions" + Path.DirectorySeparatorChar + version.Name + Path.DirectorySeparatorChar;
            return _arguments.Any(a => a.Contains(marker, StringComparison.Ordinal));
        }

        /// <summary>Every running game whose command line names its game folder.</summary>
        public static IReadOnlyList<RunningGame> Find()
        {
            var games = new List<RunningGame>();
            foreach (int pid in Platform.LinuxProcesses.JavaPids())
            {
                var game = Read(pid);
                if (game != null) games.Add(game);
            }
            return games;
        }

        /// <summary>The game running as process <paramref name="processId"/>, or <c>null</c> when that process is not one.</summary>
        public static RunningGame FromProcess(int processId)
        {
            return processId > 0 ? Read(processId) : null;
        }

        /// <summary>
        /// The installed version this game was started from, found through the version folder its launcher named,
        /// or <c>null</c> when the command line does not tell.
        /// </summary>
        public InstalledVersion FindVersion()
        {
            var arguments = _arguments.ToList();
            int index = arguments.IndexOf("--version");
            if (index < 0 || index + 1 >= arguments.Count || arguments[index + 1].Length == 0
                || arguments[index + 1].IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                return null;
            string name = arguments[index + 1];

            // An isolated game folder is the version folder itself; otherwise it is read from the launcher's paths, such as the natives folder.
            var folders = new List<string>();
            string parent = Path.GetDirectoryName(GameDirectory);
            if (parent != null && string.Equals(Path.GetFileName(parent), "versions", StringComparison.Ordinal))
                folders.Add(Path.GetDirectoryName(parent));
            folders.Add(GameDirectory);
            string classPath = ValueAfter(_arguments, "-cp") ?? ValueAfter(_arguments, "-classpath") ?? ValueAfter(_arguments, "--class-path");
            foreach (string entry in (classPath ?? "").Split(Path.PathSeparator))
            {
                string marker = Path.DirectorySeparatorChar + "versions" + Path.DirectorySeparatorChar + name + Path.DirectorySeparatorChar;
                int at = entry.IndexOf(marker, StringComparison.Ordinal);
                if (at > 0) folders.Add(entry.Substring(0, at));
            }

            foreach (string folder in folders.Select(Full).Where(f => f != null).Distinct(StringComparer.Ordinal))
            {
                if (!Directory.Exists(Path.Combine(folder, "versions", name)))
                    continue;
                var version = InstalledVersion.Load(new GameFolder(folder, GameFolderSource.Default), name);
                if (version != null)
                    return version;
            }
            return null;
        }

        private static RunningGame Read(int processId)
        {
            var arguments = GameAccount.ReadArguments(processId);
            string directory = Full(ValueAfter(arguments, "--gameDir") ?? "");
            return directory == null ? null : new RunningGame(processId, directory, arguments);
        }

        private static string ValueAfter(IReadOnlyList<string> arguments, string option)
        {
            for (int i = 0; i + 1 < arguments.Count; i++)
                if (arguments[i] == option) return arguments[i + 1];
            return null;
        }

        private static string Full(string path)
        {
            try
            {
                return Path.IsPathRooted(path) ? Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar) : null;
            }
            catch (Exception ex) when (ex is ArgumentException || ex is NotSupportedException || ex is PathTooLongException)
            {
                return null;
            }
        }
    }
}
