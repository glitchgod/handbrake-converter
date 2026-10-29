using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;

namespace Jellyfin.Plugin.HandBrakeConverter.Services;

/// <summary>Suspend, resume and kill a process together with everything it started (HandBrake spawns helpers). Linux only.</summary>
internal static class ProcessControl
{
    [DllImport("libc", SetLastError = true)]
    private static extern int kill(int pid, int sig);

    private const int SigKill = 9, SigCont = 18, SigStop = 19;

    public static void Suspend(int pid) => SignalAll(Tree(pid), SigStop);

    public static void Resume(int pid) => SignalAll(Tree(pid), SigCont);

    /// <summary>A stopped process can't act on SIGKILL queued behind SIGSTOP, so continue everything first.</summary>
    public static void Kill(int pid)
    {
        var tree = Tree(pid);
        SignalAll(tree, SigCont);
        SignalAll(tree, SigKill);
    }

    private static void SignalAll(IEnumerable<int> pids, int sig)
    {
        foreach (var p in pids) kill(p, sig);
    }

    /// <summary>The process and all of its descendants, found by walking /proc.</summary>
    private static List<int> Tree(int root)
    {
        var children = new Dictionary<int, List<int>>();
        foreach (var dir in Directory.EnumerateDirectories("/proc"))
        {
            if (!int.TryParse(Path.GetFileName(dir), out var pid)) continue;
            try
            {
                var stat = File.ReadAllText(Path.Combine(dir, "stat"));
                var fields = stat[(stat.LastIndexOf(')') + 2)..].Split(' '); // after "(name)": state, ppid, ...
                var ppid = int.Parse(fields[1]);
                if (!children.TryGetValue(ppid, out var list)) children[ppid] = list = new List<int>();
                list.Add(pid);
            }
            catch
            {
                // process exited while we were looking
            }
        }

        var all = new List<int> { root };
        for (var i = 0; i < all.Count; i++)
            if (children.TryGetValue(all[i], out var kids)) all.AddRange(kids.Where(k => !all.Contains(k)));
        return all;
    }
}
