using System.Diagnostics;
namespace Campfire.Launcher
{
    internal static class LauncherProcess
    {
        internal static string TryGetImagePath(Process process) => Platform.LinuxProcesses.ImagePath(process.Id);
    }
}
