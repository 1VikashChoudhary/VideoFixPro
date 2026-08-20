using System.Collections.Concurrent;
using System.Diagnostics;

namespace VideoFixPro;

/// <summary>
/// Ensures all background FFmpeg processes are terminated when the application exits,
/// even in the event of an unexpected crash or hard kill.
/// </summary>
public static class ProcessGuard
{
    private static readonly ConcurrentDictionary<int, Process> _activeProcesses = new();

    static ProcessGuard()
    {
        // Hook into multiple exit points for maximum safety
        AppDomain.CurrentDomain.ProcessExit += (s, e) => CleanUp();
        AppDomain.CurrentDomain.UnhandledException += (s, e) => CleanUp();
    }

    /// <summary>
    /// Call this when the app starts to ensure a clean state
    /// </summary>
    public static void Initialize()
    {
        // Static constructor will run
    }

    /// <summary>
    /// Register a process to be tracked
    /// </summary>
    public static void Watch(Process? process)
    {
        if (process == null) return;
        try
        {
            if (process.HasExited) return;
            int pid = process.Id;
            _activeProcesses.TryAdd(pid, process);
            
            // Remove from list when it exits naturally
            process.Exited += (s, e) => _activeProcesses.TryRemove(pid, out _);
            process.EnableRaisingEvents = true;
        }
        catch (Exception)
        {
            // Process may have already exited or cannot raise events
        }
    }

    /// <summary>
    /// Unregister a process (e.g. if it finished successfully)
    /// </summary>
    public static void Unwatch(Process? process)
    {
        if (process == null) return;
        try
        {
            _activeProcesses.TryRemove(process.Id, out _);
        }
        catch (Exception)
        {
            // Process may already be disposed or exited
        }
    }

    /// <summary>
    /// Kills all remaining tracked processes
    /// </summary>
    public static void CleanUp()
    {
        foreach (var kvp in _activeProcesses)
        {
            try
            {
                if (!kvp.Value.HasExited)
                {
                    kvp.Value.Kill(true); // Kill entire tree
                    Debug.WriteLine($"[ProcessGuard] Killed orphaned process: {kvp.Key}");
                }
            }
            catch { /* Ignore errors during mass kill */ }
        }
        _activeProcesses.Clear();
    }
}
