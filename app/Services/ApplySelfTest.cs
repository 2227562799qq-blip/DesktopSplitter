using System.IO;
using DesktopSplitter.Interop;
using DesktopSplitter.Models;
using static DesktopSplitter.Interop.NativeMethods;

namespace DesktopSplitter.Services;

/// <summary>
/// Headless end-to-end apply test, run by CI with --self-test-apply.
///
/// Runs the REAL ApplyService.Apply for the "2 — 左右并排" layout on the primary
/// physical monitor, verifies the resulting topology (both virtual monitors active,
/// segment 1 primary at (0,0), segment 2 at (w/2, 0), physical moved left), then
/// reverts and verifies the virtual monitors are gone. After that, an
/// inactive-repro phase (see RunInactiveRepro) proves the remembered-topology
/// mechanism behind lu's arrange failure and validates the generation-rotation
/// fix, so a green build means the fix was actually exercised, not just the
/// already-working path.
///
/// The verdict is the TOPOLOGY plus the apply log, not whether Apply() returned:
/// a compositor failure after a successful arrange still counts as an arrange
/// pass (reported separately as a warning), because the arrange is what this
/// test gates. Exit codes: 0 = arrange + inactive-repro + rotation + revert
/// passed; 1 = failed; 2 = skipped (no physical display mode big enough for
/// a 2-way split).
/// </summary>
public static class ApplySelfTest
{
    /// <summary>Writes to both the console and the log file CI reads back.</summary>
    private sealed class Log : IProgress<string>
    {
        private readonly StreamWriter _file;
        public List<string> Lines { get; } = new();
        public Log(StreamWriter file) => _file = file;
        public void Report(string value)
        {
            Lines.Add(value);
            Console.WriteLine(value);
            _file.WriteLine(value);
            _file.Flush();
        }
    }

    public static int Run()
    {
        string logPath = Path.GetFullPath(Path.Combine(
            Directory.GetCurrentDirectory(), "apply-self-test.log"));
        using var file = new StreamWriter(logPath, append: false);
        var log = new Log(file);
        try
        {
            return RunCore(log);
        }
        catch (Exception ex)
        {
            log.Report($"SELF-TEST-ERROR: {ex.GetType().Name}: {ex.Message}");
            return 1;
        }
    }

    private static int RunCore(Log log)
    {
        log.Report("APPLY-SELF-TEST: headless end-to-end apply (2-way split).");

        // The driver reports the rate Apply sends it as the virtual monitor's only
        // mode, and a DEVMODE can only ask for whole hertz. Guard the rounding
        // here: the runner's physical monitor is an exact whole-hertz rate, so the
        // end-to-end apply below can never catch a fractional-rate regression.
        (uint input, uint expected)[] refreshCases =
        {
            (60020, 60000),   // 60.020 Hz panel (the failing machine)
            (60012, 60000),
            (59940, 60000),   // 59.94 Hz
            (164999, 165000), // 164.999 Hz
            (64000, 64000),   // already whole
            (60000, 60000),
        };
        foreach ((uint input, uint expected) in refreshCases)
        {
            uint actual = ApplyService.RoundRefreshMillihertzToWholeHertz(input);
            if (actual != expected)
            {
                log.Report($"APPLY-SELF-TEST: refresh rounding FAILED: {input} mHz -> {actual} mHz, expected {expected} mHz.");
                return 1;
            }
        }
        log.Report("APPLY-SELF-TEST: refresh rounding ok (60020->60000, 164999->165000, 64000->64000).");

        IReadOnlyList<MonitorInfo> current = DisplayConfig.Enumerate();
        MonitorInfo? physical = current.FirstOrDefault(m => m.IsPrimary && !m.IsDeskSplitVirtual)
            ?? current.FirstOrDefault(m => !m.IsDeskSplitVirtual);
        if (physical is null)
        {
            log.Report("APPLY-SELF-TEST: no physical monitor found.");
            return 1;
        }
        log.Report($"APPLY-SELF-TEST: physical {physical.DeviceName} ({physical.FriendlyName}) " +
                   $"{physical.CurrentWidth}x{physical.CurrentHeight} at " +
                   $"({physical.PositionX},{physical.PositionY}).");

        // The 2-way split needs each half >= 640 px wide (ValidateRequest). A headless
        // runner's display is often 1024x768, so widen it first when needed, and put
        // the original mode back at the end.
        DEVMODE? originalMode = null;
        bool widened = false;
        if (physical.CurrentWidth < 1280)
        {
            var currentMode = DEVMODE.Create();
            if (EnumDisplaySettingsEx(physical.DeviceName, ENUM_CURRENT_SETTINGS, ref currentMode, 0))
                originalMode = currentMode;

            if (!TryWidenPhysical(physical, log, out MonitorInfo widenedPhysical))
            {
                log.Report("APPLY-SELF-TEST: SKIPPED - no display mode >= 1280 px wide available.");
                return 2;
            }
            physical = widenedPhysical;
            widened = true;
        }

        int halfW = physical.CurrentWidth / 2;
        int height = physical.CurrentHeight;
        LayoutDefinition layout = LayoutDefinition.All.First(l => l.Kind == LayoutKind.TwoVertical);
        var request = new ApplyRequest
        {
            Physical = physical,
            Layout = layout,
            Resolutions = new[] { (halfW, height), (halfW, height) },
            HidePhysicalDisplay = false,
            PromptToConfirm = false,
            Confirmer = null,
            SpecializationUi = null,
        };

        Exception? applyError = null;
        ApplyResult? result = null;
        try
        {
            result = ApplyService.Apply(request, log, CancellationToken.None);
            log.Report("APPLY-SELF-TEST: Apply() returned without throwing.");
        }
        catch (Exception ex)
        {
            applyError = ex;
            log.Report($"APPLY-SELF-TEST: Apply() threw {ex.GetType().Name}: {ex.Message}");
        }

        // The arrange step (c) writes config.json only after ApplyLayout succeeded,
        // so reaching step (d) proves the arrange worked even when a later step
        // (e.g. the compositor) threw and the failure cleanup already tore the
        // split down again.
        bool arrangeReachedConfig = log.Lines.Any(l => l.Contains("已写入 "));
        bool topologyOk = false;
        if (applyError is null && result is not null)
            topologyOk = VerifyTopology(log, result, halfW, height);

        bool arrangePassed = applyError is null
            ? arrangeReachedConfig && topologyOk
            : arrangeReachedConfig;
        log.Report(arrangePassed
            ? "APPLY-SELF-TEST: ARRANGE-PASSED"
            : "APPLY-SELF-TEST: ARRANGE-FAILED");
        if (applyError is not null && arrangePassed)
            log.Report("APPLY-SELF-TEST: note - Apply() threw after the arrange; " +
                       "the failure is downstream of the arrange (compositor or later).");

        // Revert whatever is left (a successful apply, or the leftovers of a failed
        // one) and require the virtual monitors to be gone.
        bool revertPassed = false;
        try
        {
            ApplyService.Revert(log, CancellationToken.None);
            revertPassed = DisplayConfig.EnumerateAll().All(m => !m.IsDeskSplitVirtual);
            log.Report(revertPassed
                ? "APPLY-SELF-TEST: REVERT-PASSED (no virtual monitors remain)"
                : "APPLY-SELF-TEST: REVERT-FAILED (virtual monitors still listed)");
        }
        catch (Exception ex)
        {
            log.Report($"APPLY-SELF-TEST: Revert() threw {ex.GetType().Name}: {ex.Message}");
        }

        // Inactive-repro phase (0.6.7): prove the remembered-topology mechanism
        // and validate the generation-rotation fix. Runs after the normal revert
        // and before the physical mode is restored.
        (bool reproPassed, bool rotationPassed) = RunInactiveRepro(log, request, halfW, height);

        if (widened && originalMode.HasValue)
        {
            DEVMODE restore = originalMode.Value;
            int rc = ChangeDisplaySettingsEx(physical.DeviceName, ref restore, IntPtr.Zero, 0, IntPtr.Zero);
            log.Report(rc == DISP_CHANGE_SUCCESSFUL
                ? "APPLY-SELF-TEST: physical display mode restored."
                : $"APPLY-SELF-TEST: warning - could not restore the physical display mode (rc {rc}).");
        }

        log.Report($"APPLY-SELF-TEST: RESULT arrange={(arrangePassed ? "PASS" : "FAIL")} " +
                   $"inactive-repro={(reproPassed ? "PASS" : "FAIL")} " +
                   $"rotation={(rotationPassed ? "PASS" : "FAIL")} " +
                   $"revert={(revertPassed ? "PASS" : "FAIL")}");
        return arrangePassed && reproPassed && rotationPassed && revertPassed ? 0 : 1;
    }

    /// <summary>Verifies the live topology after a successful Apply().</summary>
    private static bool VerifyTopology(Log log, ApplyResult result, int halfW, int height)
    {
        IReadOnlyList<MonitorInfo> active = DisplayConfig.Enumerate();
        MonitorInfo? v0 = active.FirstOrDefault(m =>
            string.Equals(m.DeviceName, result.VirtualMonitors[0].DeviceName, StringComparison.OrdinalIgnoreCase));
        MonitorInfo? v1 = active.FirstOrDefault(m =>
            string.Equals(m.DeviceName, result.VirtualMonitors[1].DeviceName, StringComparison.OrdinalIgnoreCase));
        MonitorInfo? physical = active.FirstOrDefault(m => !m.IsDeskSplitVirtual);

        bool ok = true;
        void Check(bool condition, string what)
        {
            log.Report($"APPLY-SELF-TEST: topology {what}: {(condition ? "ok" : "MISMATCH")}");
            if (!condition) ok = false;
        }

        Check(v0 is not null, "segment 1 present and active");
        Check(v1 is not null, "segment 2 present and active");
        if (v0 is not null)
        {
            Check(v0.IsPrimary, "segment 1 is primary");
            Check(v0.PositionX == 0 && v0.PositionY == 0, $"segment 1 at (0,0), actual ({v0.PositionX},{v0.PositionY})");
            Check(v0.CurrentWidth == halfW && v0.CurrentHeight == height,
                $"segment 1 is {halfW}x{height}, actual {v0.CurrentWidth}x{v0.CurrentHeight}");
        }
        if (v1 is not null)
        {
            Check(v1.PositionX == halfW && v1.PositionY == 0, $"segment 2 at ({halfW},0), actual ({v1.PositionX},{v1.PositionY})");
            Check(v1.CurrentWidth == halfW && v1.CurrentHeight == height,
                $"segment 2 is {halfW}x{height}, actual {v1.CurrentWidth}x{v1.CurrentHeight}");
        }
        if (physical is not null)
            Check(physical.PositionX == -physical.CurrentWidth, $"physical moved left, actual ({physical.PositionX},{physical.PositionY})");
        return ok;
    }

    /// <summary>
    /// Inactive-repro phase (0.6.7): proves the remembered-topology mechanism
    /// behind lu's arrange failure, and validates the generation-rotation fix.
    ///
    /// 1. Create 2 monitors with ONE claimed generation (both creates reuse
    ///    it) and confirm they arrive active on the fresh runner. (If they
    ///    arrive inactive and stay inactive for 5 s, the runner already behaves
    ///    like lu's machine and the disconnect/teach step is skipped.)
    /// 2. Disconnect them (SetDisplayConfig INTERNAL, falling back to a CDS
    ///    detach) so Windows records those identities as disconnected, then
    ///    Revert to remove them.
    /// 3. Create the SAME generation again and confirm the monitors arrive
    ///    INACTIVE and stay inactive for 15 s — the remembered topology.
    ///    The re-arrived monitors are identified by DISPLAY number (the
    ///    highest numbers are the newest arrivals), not by EDID/target
    ///    identity: after a disconnect + revert every path resolves to the
    ///    same EDID, so identity dedup cannot separate the new monitors
    ///    from the stale path records (round 3 proved this: all four paths
    ///    decoded to serial 0xD5000009, index 0).
    /// 4. Run the OLD ChangeDisplaySettingsEx arrange on them and require it
    ///    to fail exactly like on lu's machine (BADMODE / FAILED).
    /// 5. Revert, then run a full Apply (which claims a FRESH generation) and
    ///    require the new monitors to arrive active and arrange cleanly.
    ///
    /// Returns (reproPassed, rotationPassed). A finally always reverts, so a
    /// failed repro never leaves monitors behind for the mode restore.
    /// </summary>
    private static (bool ReproPassed, bool RotationPassed) RunInactiveRepro(
        Log log, ApplyRequest request, int halfW, int height)
    {
        bool reproPassed = false;
        bool rotationPassed = false;
        try
        {
            uint gen = ApplyService.ClaimNextDisplayGeneration(log);
            log.Report($"INACTIVE-REPRO: test generation {gen} (both creates use it).");

            IReadOnlyList<MonitorInfo> first;
            try
            {
                first = ApplyService.CreateVirtualMonitors(
                    request, NonVirtualBaseline(), log, CancellationToken.None, gen);
            }
            catch (Exception ex)
            {
                log.Report($"INACTIVE-REPRO: first create threw {ex.GetType().Name}: {ex.Message}");
                return (false, false);
            }

            List<string> firstNames = first.Select(m => m.DeviceName).ToList();
            if (StaysInactiveFor(log, firstNames, TimeSpan.FromSeconds(5)))
            {
                log.Report("INACTIVE-REPRO: first create arrived INACTIVE and stayed inactive for 5 s " +
                           "(runner already behaves like lu's machine) — skipping the disconnect/teach step.");
                reproPassed = OldArrangeFails(log, request, first);
            }
            else
            {
                log.Report("INACTIVE-REPRO: first create arrived active, as expected on a fresh machine.");

                bool disconnected = DisplayTopology.TryDisconnectToInternal(log)
                    && WaitUntilInactive(log, firstNames, TimeSpan.FromSeconds(5));
                if (!disconnected)
                {
                    log.Report("INACTIVE-REPRO: topology switch did not deactivate; trying CDS detach.");
                    disconnected = DisplayTopology.TryDetachDisplays(firstNames, log)
                        && WaitUntilInactive(log, firstNames, TimeSpan.FromSeconds(5));
                }
                if (!disconnected)
                {
                    log.Report("INACTIVE-REPRO: could not deactivate the monitors; aborting.");
                    return (false, false);
                }
                log.Report("INACTIVE-REPRO: disconnected — both monitors inactive.");

                try
                {
                    ApplyService.Revert(log, CancellationToken.None);
                }
                catch (Exception ex)
                {
                    log.Report($"INACTIVE-REPRO: revert after disconnect threw {ex.GetType().Name}: {ex.Message}");
                    return (false, false);
                }
                if (DisplayConfig.EnumerateAll().Any(m => m.IsDeskSplitVirtual))
                {
                    log.Report("INACTIVE-REPRO: revert left virtual monitors behind; aborting.");
                    return (false, false);
                }
                log.Report("INACTIVE-REPRO: reverted; monitors removed.");

                IReadOnlyList<MonitorInfo> second;
                try
                {
                    // Identity dedup cannot work here: the re-arrived monitors
                    // all resolve to the same EDID (round 3: all four paths
                    // decoded to serial 0xD5000009, index 0), and the target
                    // dedup saw the wrong number of groups too. The new
                    // arrivals are identified by DISPLAY number instead — GDI
                    // names are handed out in arrival order, so the stale
                    // records keep the old numbers and the new monitors take
                    // the highest ones.
                    second = ApplyService.CreateVirtualMonitors(
                        request, NonVirtualBaseline(), log, CancellationToken.None, gen,
                        newestDisplayFallback: true);
                }
                catch (Exception ex)
                {
                    log.Report($"INACTIVE-REPRO: second create threw {ex.GetType().Name}: {ex.Message}");
                    return (false, false);
                }
                List<string> secondNames = second.Select(m => m.DeviceName).ToList();
                // Prove the re-arrived monitors really carry the same generation:
                // their EDID serials must decode to gen, not to a fresh one.
                log.Report("INACTIVE-REPRO: re-arrived monitors carry generation " +
                           string.Join(", ", second.Select(m => DisplayConfig.DeskSplitGenerationFromEdid(m.Edid))) +
                           $" (expected {gen}).");
                if (!StaysInactiveFor(log, secondNames, TimeSpan.FromSeconds(15)))
                {
                    log.Report("INACTIVE-REPRO: NOT-REPRODUCED — re-arrived monitors activated.");
                    return (false, false);
                }
                log.Report($"INACTIVE-REPRO: inactive state confirmed — re-arrived generation {gen} " +
                           "monitors stayed inactive for 15 s.");

                reproPassed = OldArrangeFails(log, request, second);
            }

            if (!reproPassed)
            {
                return (false, false);
            }

            // Rotation validation: a full Apply claims a FRESH generation. The
            // new monitors must arrive active and arrange cleanly.
            try
            {
                ApplyService.Revert(log, CancellationToken.None);
            }
            catch (Exception ex)
            {
                log.Report($"INACTIVE-REPRO: revert before rotation check threw {ex.GetType().Name}: {ex.Message}");
                return (true, false);
            }

            int mark = log.Lines.Count;
            Exception? applyError = null;
            ApplyResult? result = null;
            try
            {
                result = ApplyService.Apply(request, log, CancellationToken.None);
            }
            catch (Exception ex)
            {
                applyError = ex;
                log.Report($"INACTIVE-REPRO: rotation Apply() threw {ex.GetType().Name}: {ex.Message}");
            }
            bool arrangeReachedConfig = log.Lines.Skip(mark).Any(l => l.Contains("已写入 "));
            bool topologyOk = applyError is null && result is not null && VerifyTopology(log, result, halfW, height);
            List<string> arrivalLines = log.Lines.Skip(mark).Where(l => l.Contains("  找到 ")).ToList();
            bool arrivedActive = arrivalLines.Count > 0 && arrivalLines.All(l => !l.Contains("[路径未激活"));
            log.Report(arrivedActive
                ? "INACTIVE-REPRO: fresh-generation monitors arrived ACTIVE (auto-activated)."
                : "INACTIVE-REPRO: warning — fresh-generation monitors arrived INACTIVE (see arrival lines above).");
            rotationPassed = arrangeReachedConfig && topologyOk;
            log.Report(rotationPassed
                ? "INACTIVE-REPRO: ROTATION-PASSED — fresh generation arranged cleanly."
                : "INACTIVE-REPRO: ROTATION-FAILED.");
            return (reproPassed, rotationPassed);
        }
        finally
        {
            try
            {
                ApplyService.Revert(log, CancellationToken.None);
                bool clean = DisplayConfig.EnumerateAll().All(m => !m.IsDeskSplitVirtual);
                log.Report(clean
                    ? "INACTIVE-REPRO: final revert done, no virtual monitors remain."
                    : "INACTIVE-REPRO: warning — virtual monitors remain after final revert.");
            }
            catch (Exception ex)
            {
                log.Report($"INACTIVE-REPRO: final revert threw {ex.GetType().Name}: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Runs the OLD ChangeDisplaySettingsEx arrange on the given (inactive)
    /// virtual monitors. Returns true only when it throws DisplayArrangeException
    /// — the BADMODE/FAILED lu sees. A success means the repro did NOT
    /// reproduce the failure.
    /// </summary>
    private static bool OldArrangeFails(Log log, ApplyRequest request, IReadOnlyList<MonitorInfo> virtualMonitors)
    {
        IReadOnlyList<MonitorInfo> current = DisplayConfig.Enumerate();
        MonitorInfo? physicalNow = current.FirstOrDefault(m => !m.IsDeskSplitVirtual);
        if (physicalNow is null)
        {
            log.Report("INACTIVE-REPRO: no active physical monitor to arrange against; aborting.");
            return false;
        }
        IReadOnlyList<DisplayArranger.VirtualPlacement> placements =
            DisplayArranger.ComputePlacements(physicalNow, request.Layout, virtualMonitors, request.Resolutions);
        try
        {
            DisplayArranger.ApplyLayout(
                DisplayArranger.ComposeLayout(current, placements, request.Physical.MaxRefreshHz, virtualMonitors[0].DeviceName),
                log.Report);
        }
        catch (DisplayArrangeException ex)
        {
            log.Report($"INACTIVE-REPRO: old arrange failed as on lu's machine: {ex.Message}");
            return true;
        }
        log.Report("INACTIVE-REPRO: NOT-REPRODUCED — old arrange SUCCEEDED on inactive monitors.");
        return false;
    }

    private static HashSet<string> NonVirtualBaseline()
        => DisplayConfig.Enumerate()
            .Where(m => !m.IsDeskSplitVirtual)
            .Select(m => m.DeviceName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>True once every named monitor is present but inactive (bounded wait).</summary>
    private static bool WaitUntilInactive(Log log, IReadOnlyList<string> deviceNames, TimeSpan timeout)
    {
        DateTime deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            IReadOnlyList<MonitorInfo> all = DisplayConfig.EnumerateAll();
            bool allInactive = true;
            foreach (string name in deviceNames)
            {
                MonitorInfo? m = all.FirstOrDefault(x => string.Equals(x.DeviceName, name, StringComparison.OrdinalIgnoreCase));
                if (m is null || m.IsActivePath)
                {
                    allInactive = false;
                    break;
                }
            }
            if (allInactive) return true;
            if (DateTime.UtcNow >= deadline)
            {
                foreach (string name in deviceNames)
                {
                    MonitorInfo? m = all.FirstOrDefault(x => string.Equals(x.DeviceName, name, StringComparison.OrdinalIgnoreCase));
                    log.Report($"INACTIVE-REPRO: 超时时 {name} 状态：{(m is null ? "不在列表中" : m.IsActivePath ? "仍激活" : "未激活")}。");
                }
                return false;
            }
            Thread.Sleep(400);
        }
    }

    /// <summary>
    /// True only if every named monitor stays inactive for the WHOLE window —
    /// a single active sighting (including slow activation) returns false, so
    /// a momentary post-arrival inactive state cannot fake a repro.
    /// </summary>
    private static bool StaysInactiveFor(Log log, IReadOnlyList<string> deviceNames, TimeSpan window)
    {
        DateTime started = DateTime.UtcNow;
        DateTime deadline = started + window;
        while (true)
        {
            IReadOnlyList<MonitorInfo> all = DisplayConfig.EnumerateAll();
            foreach (string name in deviceNames)
            {
                MonitorInfo? m = all.FirstOrDefault(x => string.Equals(x.DeviceName, name, StringComparison.OrdinalIgnoreCase));
                if (m is null)
                {
                    log.Report($"INACTIVE-REPRO: {name} 不在列表中（已消失）。");
                    return false;
                }
                if (m.IsActivePath)
                {
                    log.Report($"INACTIVE-REPRO: {name} 已激活（距等待开始 {(DateTime.UtcNow - started).TotalSeconds:0.0} 秒）。");
                    return false;
                }
            }
            if (DateTime.UtcNow >= deadline) return true;
            Thread.Sleep(400);
        }
    }

    /// <summary>Switches the physical monitor to a mode at least 1280 px wide.</summary>
    private static bool TryWidenPhysical(MonitorInfo physical, Log log, out MonitorInfo widened)
    {
        widened = physical;
        var modes = new List<DEVMODE>();
        for (int i = 0; i < 200; i++)
        {
            var dm = DEVMODE.Create();
            if (!EnumDisplaySettingsEx(physical.DeviceName, i, ref dm, 0)) break;
            if (dm.dmPelsWidth > 0 && dm.dmPelsHeight > 0) modes.Add(dm);
        }

        DEVMODE? target = modes
            .Where(dm => dm.dmPelsWidth >= 1280)
            .OrderBy(dm => dm.dmPelsWidth == 1920 && dm.dmPelsHeight == 1080 ? 0 : 1)
            .ThenByDescending(dm => dm.dmPelsWidth)
            .ThenByDescending(dm => dm.dmPelsHeight)
            .Cast<DEVMODE?>()
            .FirstOrDefault();
        if (target is null) return false;

        DEVMODE chosen = target.Value;
        log.Report($"APPLY-SELF-TEST: widening physical display to {chosen.dmPelsWidth}x{chosen.dmPelsHeight} " +
                   $"for the split (was {physical.CurrentWidth}x{physical.CurrentHeight}).");
        int rc = ChangeDisplaySettingsEx(physical.DeviceName, ref chosen, IntPtr.Zero, 0, IntPtr.Zero);
        if (rc != DISP_CHANGE_SUCCESSFUL)
        {
            log.Report($"APPLY-SELF-TEST: widening failed (rc {rc}).");
            return false;
        }

        MonitorInfo? fresh = DisplayConfig.Enumerate()
            .FirstOrDefault(m => string.Equals(m.DeviceName, physical.DeviceName, StringComparison.OrdinalIgnoreCase));
        if (fresh is null || fresh.CurrentWidth < 1280)
        {
            log.Report("APPLY-SELF-TEST: widening did not take effect.");
            return false;
        }
        widened = fresh;
        return true;
    }
}
