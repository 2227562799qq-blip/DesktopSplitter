// SplitTest — end-to-end split test for CI (0.6.2+).
//
// Sends SET_CONFIG for 2 virtual monitors through the real driver, then
// watches the display topology from the outside, at three levels:
//
//   driver  = GET_STATUS ActiveMonitorCount (monitors the driver arrived)
//   all     = QueryDisplayConfig(QDC_ALL_PATHS) path delta (monitors Windows knows)
//   active  = QueryDisplayConfig(QDC_ONLY_ACTIVE_PATHS) path delta (monitors on the desktop)
//
// The verdict gates on driver + active only. The all-paths count is printed
// as a diagnostic, not a gate: QDC_ALL_PATHS counts every path in the display
// database, including stale entries for long-gone displays (a fresh runner
// already shows 17), and Windows may prune entries when the topology changes,
// so the count does not move by exactly +2 even on success (run 45734589966:
// driver=2, active=+2, all=+0 — a pass the old verdict called a failure).
// The failure split is still the diagnosis when it fails:
//
//   driver 0            -> the driver never created the monitors (see
//                          GET_STATUS AdapterReady / LastErrorStage)
//   driver 2, active 0  -> created but Windows never activated their paths —
//                          this is the 2026-11-25 failure (app waited on
//                          active paths only and reported 0 after 15 s);
//                          allDelta hints whether the OS saw them arrive
//
// Runs elevated (the runner is admin). Reverts with SET_CONFIG 0 at the end
// so the machine is left clean. Exit 0 = pass, 1 = split did not complete,
// 2 = environment error (not elevated / driver missing / CCD query failed).

using System.Runtime.InteropServices;
using System.Security.Principal;
using DesktopSplitter.Interop;

internal static class Program
{
    private const uint QDC_ALL_PATHS = 0x00000001;
    private const uint QDC_ONLY_ACTIVE_PATHS = 0x00000002;
    private const int ERROR_SUCCESS = 0;
    private const int ERROR_INSUFFICIENT_BUFFER = 122;
    private const int PathInfoSize = 72;   // sizeof(DISPLAYCONFIG_PATH_INFO) on x64
    private const int ModeInfoSize = 64;   // sizeof(DISPLAYCONFIG_MODE_INFO) on x64

    private const int ExpectedMonitors = 2;
    private const int WaitSeconds = 30;

    private static int Main()
    {
        if (!IsElevated())
        {
            Console.WriteLine("SPLIT-TEST: not elevated, cannot open the driver device.");
            return 2;
        }

        (int baseAll, _, int baseAllRc) = QueryCounts(QDC_ALL_PATHS);
        (int baseActive, _, int baseActiveRc) = QueryCounts(QDC_ONLY_ACTIVE_PATHS);
        if (baseAllRc != 0 || baseActiveRc != 0)
        {
            Console.WriteLine($"SPLIT-TEST: baseline CCD query failed (all rc={baseAllRc}, active rc={baseActiveRc}).");
            return 2;
        }

        int baseDriver;
        try
        {
            baseDriver = DriverCount();
            PrintDriverStatus("baseline");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"SPLIT-TEST: cannot reach the driver: {ex.Message}");
            return 2;
        }
        Console.WriteLine($"SPLIT-TEST: baseline driver={baseDriver} allPaths={baseAll} activePaths={baseActive}");

        DESKSPLIT_CONFIG cfg = DriverClient.NewConfig();
        cfg.MonitorCount = ExpectedMonitors;
        for (int i = 0; i < ExpectedMonitors; i++)
        {
            cfg.Monitors[i].ModeCount = 1;
            cfg.Monitors[i].PreferredModeIndex = 0;
            cfg.Monitors[i].Modes[0] = new DESKSPLIT_MODE { Width = 1920, Height = 1080, RefreshMillihertz = 60000 };
        }
        try
        {
            DriverClient.SetConfig(cfg);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"SPLIT-TEST: SET_CONFIG failed: {ex.Message}");
            return 2;
        }
        Console.WriteLine($"SPLIT-TEST: SET_CONFIG sent ({ExpectedMonitors} monitors 1920x1080@60).");

        int driver = baseDriver, allDelta = 0, activeDelta = 0;
        DateTime deadline = DateTime.UtcNow + TimeSpan.FromSeconds(WaitSeconds);
        DateTime nextPrint = DateTime.UtcNow;
        while (DateTime.UtcNow < deadline)
        {
            try { driver = DriverCount(); } catch { /* keep last value */ }
            (int all, _, int allRc) = QueryCounts(QDC_ALL_PATHS);
            (int active, _, int activeRc) = QueryCounts(QDC_ONLY_ACTIVE_PATHS);
            if (allRc == 0) allDelta = all - baseAll;
            if (activeRc == 0) activeDelta = active - baseActive;

            if (DateTime.UtcNow >= nextPrint)
            {
                Console.WriteLine($"SPLIT-TEST: driver={driver} all=+{allDelta} active=+{activeDelta}");
                nextPrint = DateTime.UtcNow + TimeSpan.FromSeconds(2);
            }

            if (driver - baseDriver == ExpectedMonitors && activeDelta == ExpectedMonitors)
                break;

            Thread.Sleep(500);
        }

        // Verdict: driver created both monitors AND Windows activated both
        // paths. allDelta is diagnostic only — see the header comment.
        bool pass = driver - baseDriver == ExpectedMonitors && activeDelta == ExpectedMonitors;
        PrintDriverStatus("verdict");
        Console.WriteLine(pass
            ? $"SPLIT-TEST: PASSED — driver={driver} all=+{allDelta} active=+{activeDelta}."
            : $"SPLIT-TEST: FAILED — driver={driver} (want {baseDriver + ExpectedMonitors}) all=+{allDelta} active=+{activeDelta} (want +{ExpectedMonitors}).");

        try
        {
            DriverClient.ClearConfig();
            Thread.Sleep(2000);
            int afterDriver = DriverCount();
            PrintDriverStatus("reverted");
            (int afterAll, _, _) = QueryCounts(QDC_ALL_PATHS);
            (int afterActive, _, _) = QueryCounts(QDC_ONLY_ACTIVE_PATHS);
            Console.WriteLine($"SPLIT-TEST: reverted: driver={afterDriver} allPaths={afterAll} activePaths={afterActive}.");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"SPLIT-TEST: revert failed (machine left with virtual monitors): {ex.Message}");
        }

        return pass ? 0 : 1;
    }

    private static bool IsElevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    private static (int Paths, int Modes, int Rc) QueryCounts(uint flags)
    {
        for (int attempt = 0; attempt < 5; attempt++)
        {
            int rc = GetDisplayConfigBufferSizes(flags, out uint pathCount, out uint modeCount);
            if (rc != ERROR_SUCCESS) return (-1, -1, rc);
            byte[] paths = new byte[pathCount * PathInfoSize];
            byte[] modes = new byte[modeCount * ModeInfoSize];
            rc = QueryDisplayConfig(flags, ref pathCount, paths, ref modeCount, modes, IntPtr.Zero);
            if (rc == ERROR_SUCCESS) return ((int)pathCount, (int)modeCount, 0);
            if (rc != ERROR_INSUFFICIENT_BUFFER) return (-1, -1, rc);
        }
        return (-1, -1, ERROR_INSUFFICIENT_BUFFER);
    }

    private static int DriverCount()
    {
        return (int)DriverClient.GetStatus().ActiveMonitorCount;
    }

    private static void PrintDriverStatus(string prefix)
    {
        try
        {
            DESKSPLIT_STATUS status = DriverClient.GetStatus();
            Console.WriteLine($"SPLIT-TEST: {prefix} driver status: adapterReady={status.AdapterReady} " +
                $"active={status.ActiveMonitorCount} lastErrorStage={status.LastErrorStage} " +
                $"lastError=0x{status.LastErrorNtStatus:X8}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"SPLIT-TEST: {prefix} driver status unreadable: {ex.Message}");
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetDisplayConfigBufferSizes(uint flags, out uint numPathArrayElements, out uint numModeInfoArrayElements);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int QueryDisplayConfig(uint flags, ref uint numPathArrayElements, byte[] pathArray, ref uint numModeInfoArrayElements, byte[] modeArray, IntPtr currentTopologyId);
}
