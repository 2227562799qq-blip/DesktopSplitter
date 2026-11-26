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
/// inactive-repro phase (see RunInactiveRepro) records whether lu's remembered-
/// topology failure reproduces, a rotation phase validates the generation-rotation
/// fix independently of the repro, and an SDC-fallback phase (see
/// RunSdcFallbackCheck) proves the SetDisplayConfig fallback arranges a real
/// split at exactly 60000 mHz.
///
/// The verdict is the TOPOLOGY plus the apply log, not whether Apply() returned:
/// a compositor failure after a successful arrange still counts as an arrange
/// pass (reported separately as a warning), because the arrange is what this
/// test gates. Exit codes: 0 = arrange + rotation + SDC-fallback + revert
/// passed; 1 = failed; 2 = skipped (no physical display mode big enough for
/// a 2-way split). The inactive-repro result is reported in the RESULT line
/// but is NOT part of the exit gate: round 4 proved the cloud cannot reproduce
/// lu's failure mode (even disconnected-then-recreated inactive monitors
/// arrange fine via CDS), so the repro can never pass here and gating on it
/// would block every release forever. It stays visible, honestly FAIL, as a
/// record — it is not hidden to ship.
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

        // Virtual refresh is fixed at 60 Hz whatever the physical panel reports
        // (0.6.8: a 32 Hz panel's virtual monitors never got display paths).
        // BuildDriverConfig must ignore the physical rate entirely.
        foreach ((uint fakeMhz, int fakeHz) in new[] { (165007u, 165), (32000u, 32) })
        {
            MonitorInfo fakePhysical = request.Physical.WithMaxRefresh(fakeMhz, fakeHz);
            var testCfg = ApplyService.BuildDriverConfig(
                fakePhysical, request.Layout, request.Resolutions, 999);
            if (testCfg.Monitors[0].Modes[0].RefreshMillihertz != ApplyService.VirtualRefreshMillihertz)
            {
                log.Report($"APPLY-SELF-TEST: fixed virtual refresh FAILED: physical {fakeMhz} mHz -> virtual {testCfg.Monitors[0].Modes[0].RefreshMillihertz} mHz, expected {ApplyService.VirtualRefreshMillihertz} mHz.");
                return 1;
            }
        }
        log.Report("APPLY-SELF-TEST: fixed virtual refresh ok (165007->60000, 32000->60000).");

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

        // Inactive-repro phase (0.6.7): records whether lu's remembered-topology
        // failure reproduces in the cloud, and validates the generation-rotation
        // fix independently of the repro result. Runs after the normal revert
        // and before the physical mode is restored.
        (bool reproPassed, bool rotationPassed) = RunInactiveRepro(log, request, halfW, height);

        // SetDisplayConfig fallback + 60000 mHz phase (0.6.7 round 5): proves
        // the new arrange fallback end to end at the fixed 60 Hz virtual rate. Runs after the
        // repro phase (which always reverts in its finally) and before the
        // physical mode is restored.
        bool sdcFallbackPassed = RunSdcFallbackCheck(log, request, halfW, height);

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
                   $"sdc-fallback={(sdcFallbackPassed ? "PASS" : "FAIL")} " +
                   $"revert={(revertPassed ? "PASS" : "FAIL")}");
        // inactive-repro is reported above but deliberately not gated: the
        // cloud cannot reproduce lu's failure (see the class comment), so it
        // can never pass here. The gate is everything that CAN be verified
        // in the cloud: arrange + rotation + SDC fallback + revert.
        return arrangePassed && rotationPassed && sdcFallbackPassed && revertPassed ? 0 : 1;
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
    /// Inactive-repro phase (0.6.7) + rotation validation, reported
    /// independently: the repro records whether lu's remembered-topology
    /// failure reproduces in the cloud, the rotation check validates the
    /// generation-rotation fix. The rotation check ALWAYS runs, even when
    /// the repro fails — it used to sit behind an early return and never
    /// ran (rounds 2–4: rotation=FAIL only because the repro aborted first).
    ///
    /// The repro itself is informational in the cloud: round 4 proved the
    /// cloud cannot reproduce lu's failure (even disconnected-then-recreated
    /// inactive monitors arrange fine via CDS), because a fresh identity
    /// always arrives active and activation commits modes for that identity.
    /// Its FAIL stays in the RESULT line as a record; it does not gate the
    /// build (see the class comment).
    ///
    /// Steps 1–4 (repro) are unchanged from rounds 2–4; step 5 (rotation)
    /// is the same full Apply with a fresh generation, now unconditional.
    /// Returns (reproPassed, rotationPassed). A finally always reverts, so
    /// a failed phase never leaves monitors behind for the mode restore.
    /// </summary>
    private static (bool ReproPassed, bool RotationPassed) RunInactiveRepro(
        Log log, ApplyRequest request, int halfW, int height)
    {
        bool reproPassed = false;
        bool rotationPassed = false;
        try
        {
            reproPassed = RunReproPhase(log, request);
            rotationPassed = RunRotationCheck(log, request, halfW, height);
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
    /// Steps 1–4 of the repro (see RunInactiveRepro): create one generation
    /// twice and require the OLD ChangeDisplaySettingsEx arrange to fail on
    /// the re-arrived inactive monitors exactly like on lu's machine.
    /// Returns false on any abort (create threw, could not deactivate,
    /// re-arrived monitors activated, old arrange succeeded).
    /// </summary>
    private static bool RunReproPhase(Log log, ApplyRequest request)
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
            return false;
        }

        List<string> firstNames = first.Select(m => m.DeviceName).ToList();
        if (StaysInactiveFor(log, firstNames, TimeSpan.FromSeconds(5)))
        {
            log.Report("INACTIVE-REPRO: first create arrived INACTIVE and stayed inactive for 5 s " +
                       "(runner already behaves like lu's machine) — skipping the disconnect/teach step.");
            return OldArrangeFails(log, request, first);
        }

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
            return false;
        }
        log.Report("INACTIVE-REPRO: disconnected — both monitors inactive.");

        try
        {
            ApplyService.Revert(log, CancellationToken.None);
        }
        catch (Exception ex)
        {
            log.Report($"INACTIVE-REPRO: revert after disconnect threw {ex.GetType().Name}: {ex.Message}");
            return false;
        }
        if (DisplayConfig.EnumerateAll().Any(m => m.IsDeskSplitVirtual))
        {
            log.Report("INACTIVE-REPRO: revert left virtual monitors behind; aborting.");
            return false;
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
            return false;
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
            return false;
        }
        log.Report($"INACTIVE-REPRO: inactive state confirmed — re-arrived generation {gen} " +
                   "monitors stayed inactive for 15 s.");

        return OldArrangeFails(log, request, second);
    }

    /// <summary>
    /// Step 5 (rotation validation): a full Apply claims a FRESH generation.
    /// The new monitors must arrive active and arrange cleanly. Runs
    /// unconditionally — even when the repro phase failed — so rotation is
    /// never again hidden behind a repro abort.
    /// </summary>
    private static bool RunRotationCheck(Log log, ApplyRequest request, int halfW, int height)
    {
        try
        {
            ApplyService.Revert(log, CancellationToken.None);
        }
        catch (Exception ex)
        {
            log.Report($"INACTIVE-REPRO: revert before rotation check threw {ex.GetType().Name}: {ex.Message}");
            return false;
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
        bool rotationPassed = arrangeReachedConfig && topologyOk;
        log.Report(rotationPassed
            ? "INACTIVE-REPRO: ROTATION-PASSED — fresh generation arranged cleanly."
            : "INACTIVE-REPRO: ROTATION-FAILED.");
        return rotationPassed;
    }

    /// <summary>
    /// Forced SetDisplayConfig fallback + 60000 mHz validation (0.6.7 round 5).
    /// The runner's physical monitor is 64 Hz; lu's failing panel is 60.020 Hz.
    /// Creates monitors at exactly 60000 mHz and arranges them through the SDC
    /// fallback path only — CDS would succeed here, so the fallback is exercised
    /// directly instead of waiting for a CDS failure that cannot happen in the
    /// cloud. Verifies the resulting topology, then reverts. Proves both that
    /// the 60 Hz mode itself is fine and that the SDC fallback code arranges a
    /// real split end to end.
    /// </summary>
    private static bool RunSdcFallbackCheck(Log log, ApplyRequest request, int halfW, int height)
    {
        log.Report("SDC-FALLBACK-TEST: 强制 60000 mHz 创建 + SetDisplayConfig 兜底排列验证。");
        var request60 = new ApplyRequest
        {
            Physical = request.Physical.WithMaxRefresh(60000, 60),
            Layout = request.Layout,
            Resolutions = request.Resolutions,
            HidePhysicalDisplay = false,
            PromptToConfirm = false,
            Confirmer = null,
            SpecializationUi = null,
        };
        IReadOnlyList<MonitorInfo> virtualMonitors;
        try
        {
            virtualMonitors = ApplyService.CreateVirtualMonitors(
                request60, NonVirtualBaseline(), log, CancellationToken.None);
        }
        catch (Exception ex)
        {
            log.Report($"SDC-FALLBACK-TEST: FAILED — 创建 60000 mHz 显示器失败：{ex.GetType().Name}: {ex.Message}");
            return false;
        }
        try
        {
            IReadOnlyList<MonitorInfo> current = DisplayConfig.Enumerate();
            MonitorInfo? physicalNow = current.FirstOrDefault(m => !m.IsDeskSplitVirtual);
            if (physicalNow is null)
            {
                log.Report("SDC-FALLBACK-TEST: FAILED — 找不到物理显示器。");
                return false;
            }
            IReadOnlyList<DisplayArranger.VirtualPlacement> placements =
                DisplayArranger.ComputePlacements(physicalNow, request60.Layout, virtualMonitors, request60.Resolutions);
            IReadOnlyList<DisplayArranger.MonitorPlacement> composed =
                DisplayArranger.ComposeLayout(current, placements, 60, virtualMonitors[0].DeviceName);
            try
            {
                DisplayArranger.ApplyLayoutViaSetDisplayConfig(composed, log.Report);
            }
            catch (DisplayArrangeException ex)
            {
                log.Report($"SDC-FALLBACK-TEST: FAILED — SetDisplayConfig 兜底排列失败：{ex.Message}");
                return false;
            }
            var result = new ApplyResult
            {
                VirtualMonitors = virtualMonitors,
                ConfigPath = string.Empty,
                CompositorPid = 0,
            };
            bool topologyOk = VerifyTopology(log, result, halfW, height);
            log.Report(topologyOk
                ? "SDC-FALLBACK-TEST: PASSED — 60000 mHz + SetDisplayConfig 兜底排列验证通过。"
                : "SDC-FALLBACK-TEST: FAILED — 拓扑核对不通过。");
            return topologyOk;
        }
        finally
        {
            try
            {
                ApplyService.Revert(log, CancellationToken.None);
                bool clean = DisplayConfig.EnumerateAll().All(m => !m.IsDeskSplitVirtual);
                log.Report(clean
                    ? "SDC-FALLBACK-TEST: 已撤销，虚拟显示器已移除。"
                    : "SDC-FALLBACK-TEST: 警告 — 撤销后仍有虚拟显示器残留。");
            }
            catch (Exception ex)
            {
                log.Report($"SDC-FALLBACK-TEST: 撤销失败：{ex.GetType().Name}: {ex.Message}");
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
