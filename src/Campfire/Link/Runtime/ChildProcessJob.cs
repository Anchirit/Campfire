using System.Diagnostics;
namespace Campfire.Link.Runtime
{
    // Linux uses the parent-pipe supervisor created in EasyTierInstance.StartLocked.
    internal static class ChildProcessJob { public static void Assign(Process process) { } }
}
