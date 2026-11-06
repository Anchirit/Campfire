using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace Campfire.Platform
{
    /// <summary>A local process guard. Parent pipe EOF also cleans up after a SIGKILL of the CLI.</summary>
    internal static class ProcessSupervisor
    {
        internal const string Command = "--internal-supervise";

        public static ProcessStartInfo StartInfo(string core, string config, int rpcPort, string directory)
        {
            var start = new ProcessStartInfo(Environment.ProcessPath)
            {
                WorkingDirectory = directory,
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            if (Path.GetFileNameWithoutExtension(Environment.ProcessPath) == "dotnet")
                start.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
            foreach (string arg in new[] { Command, core, "-c", config, "-r", "127.0.0.1:" + rpcPort, "--multi-thread" })
                start.ArgumentList.Add(arg);
            return start;
        }

        public static async Task<int> RunAsync(string[] arguments)
        {
            if (arguments.Length < 2) return 2;
            var start = new ProcessStartInfo(arguments[1]) { UseShellExecute = false };
            foreach (string arg in arguments.Skip(2)) start.ArgumentList.Add(arg);
            using var process = Process.Start(start);
            using var term = PosixSignalRegistration.Create(PosixSignal.SIGTERM, context =>
            {
                context.Cancel = true;
                TryKill(process);
            });
            var closed = Console.OpenStandardInput().ReadAsync(new byte[1], 0, 1);
            var exited = process.WaitForExitAsync();
            try
            {
                await Task.WhenAny(exited, closed).ConfigureAwait(false);
            }
            finally
            {
                TryKill(process);
                await exited.ConfigureAwait(false);
                int configIndex = Array.IndexOf(arguments, "-c");
                if (configIndex >= 0 && configIndex + 1 < arguments.Length)
                {
                    string config = arguments[configIndex + 1];
                    try
                    {
                        File.Delete(config);
                        File.Delete(Path.Combine(Path.GetDirectoryName(config), Environment.ProcessId + ".owner"));
                    }
                    catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { }
                }
            }
            return process.ExitCode;
        }

        public static void TryKill(Process process)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (Exception ex) when (ex is InvalidOperationException || ex is System.ComponentModel.Win32Exception) { }
        }
    }
}
