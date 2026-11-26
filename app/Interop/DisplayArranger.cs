using System.Runtime.InteropServices;
using DesktopSplitter.Models;
using static DesktopSplitter.Interop.NativeMethods;

namespace DesktopSplitter.Interop;

public sealed class DisplayArrangeException : Exception
{
    public DisplayArrangeException(string message) : base(message) { }
}

/// <summary>
/// Places the virtual monitors in virtual-desktop space, immediately to the RIGHT of the
/// physical monitor, preserving the layout's relative geometry, and sets each to its
/// configured mode. Also owns primary-display switching, which requires re-basing every
/// monitor's position so the new primary sits at the virtual-desktop origin.
/// Uses ChangeDisplaySettingsEx with CDS_NORESET batching + a single final commit, and
/// falls back to SetDisplayConfig with a caller-supplied config when CDS cannot arrange
/// the set (see <see cref="ApplyLayoutViaSetDisplayConfig"/>).
/// </summary>
public static class DisplayArranger
{
    public sealed record VirtualPlacement(
        string DeviceName, int Width, int Height, int X, int Y, int Col, int Row);

    /// <summary>
    /// A monitor's desired rect in virtual-desktop space. <paramref name="RefreshHz"/> of 0
    /// means "leave the current refresh rate alone".
    /// </summary>
    public sealed record MonitorPlacement(
        string DeviceName, int Width, int Height, int X, int Y, int RefreshHz, bool IsPrimary)
    {
        public int Right => X + Width;
        public int Bottom => Y + Height;
        public override string ToString()
            => $"{DeviceName} {Width}x{Height}+{X}+{Y}{(IsPrimary ? " [PRIMARY]" : string.Empty)}";
    }

    /// <summary>
    /// Computes desktop-space positions. Column widths / row heights come from the actual
    /// segment resolutions (not the physical monitor), so the virtual desktop stays gap-free
    /// even when segment resolutions differ from their on-screen rects.
    /// </summary>
    public static IReadOnlyList<VirtualPlacement> ComputePlacements(
        MonitorInfo physical,
        LayoutDefinition layout,
        IReadOnlyList<MonitorInfo> virtualMonitors,
        IReadOnlyList<(int Width, int Height)> resolutions)
    {
        if (virtualMonitors.Count != layout.SegmentCount)
            throw new DisplayArrangeException(
                $"Expected {layout.SegmentCount} virtual monitors, found {virtualMonitors.Count}.");
        if (resolutions.Count != layout.SegmentCount)
            throw new DisplayArrangeException(
                $"Expected {layout.SegmentCount} segment resolutions, got {resolutions.Count}.");

        LayoutGeometry.Tracks tracks = LayoutGeometry.ComputeTracks(layout, resolutions);
        int[] colWidth = tracks.ColumnWidths;
        int[] rowHeight = tracks.RowHeights;

        int baseX = physical.PositionX + physical.CurrentWidth;
        int baseY = physical.PositionY;

        var placements = new List<VirtualPlacement>(layout.SegmentCount);
        for (int i = 0; i < layout.SegmentCount; i++)
        {
            LayoutCell cell = layout.Cells[i];
            (int w, int h) = resolutions[i];

            int x = baseX;
            for (int c = 0; c < cell.Col; c++) x += colWidth[c];
            int y = baseY;
            for (int r = 0; r < cell.Row; r++) y += rowHeight[r];

            placements.Add(new VirtualPlacement(virtualMonitors[i].DeviceName, w, h, x, y, cell.Col, cell.Row));
        }
        return placements;
    }

    /// <summary>
    /// The whole desktop as it should look once the split is up: every monitor that is not one
    /// of <paramref name="placements"/> keeps its current rect, the virtual monitors take theirs,
    /// and everything is re-based so <paramref name="primaryDevice"/> is primary at the origin.
    /// Written as ONE batch by <see cref="ApplyLayout"/>, the primary never leaves (0,0) halfway
    /// through — Windows rejects that, and the rejected half-batch makes every later display
    /// change fail too. Pure function: no display APIs are called.
    /// </summary>
    public static IReadOnlyList<MonitorPlacement> ComposeLayout(
        IReadOnlyList<MonitorInfo> current,
        IReadOnlyList<VirtualPlacement> placements,
        int virtualRefreshHz,
        string primaryDevice)
    {
        var byName = placements.ToDictionary(p => p.DeviceName, StringComparer.OrdinalIgnoreCase);

        var layout = current
            .Select(m => byName.TryGetValue(m.DeviceName, out VirtualPlacement? v)
                ? new MonitorPlacement(v.DeviceName, v.Width, v.Height, v.X, v.Y, virtualRefreshHz, false)
                : new MonitorPlacement(m.DeviceName, m.CurrentWidth, m.CurrentHeight, m.PositionX, m.PositionY, 0, false))
            .ToList();
        layout.AddRange(placements
            .Where(v => !current.Any(m => string.Equals(m.DeviceName, v.DeviceName, StringComparison.OrdinalIgnoreCase)))
            .Select(v => new MonitorPlacement(v.DeviceName, v.Width, v.Height, v.X, v.Y, virtualRefreshHz, false)));

        return RebaseToPrimary(layout, primaryDevice);
    }

    // ------------------------------------------------------------------ primary switching

    /// <summary>
    /// Shifts every monitor so that <paramref name="primaryDevice"/> lands on the virtual-desktop
    /// origin (0,0) and is flagged primary. Windows requires the primary display to be at (0,0),
    /// so promoting a different monitor necessarily re-bases all the others by the same vector —
    /// which leaves every relative offset, and therefore the whole arrangement, untouched.
    /// Pure function: no display APIs are called.
    /// </summary>
    public static IReadOnlyList<MonitorPlacement> RebaseToPrimary(
        IReadOnlyList<MonitorPlacement> placements, string primaryDevice)
    {
        if (placements.Count == 0)
            throw new DisplayArrangeException("Cannot re-base an empty monitor set.");

        MonitorPlacement? origin = placements.FirstOrDefault(
            p => string.Equals(p.DeviceName, primaryDevice, StringComparison.OrdinalIgnoreCase));
        if (origin is null)
        {
            throw new DisplayArrangeException(
                $"显示器“{primaryDevice}”不在当前显示器集合中，无法将其设为主显示器。" +
                $"已知：{string.Join(", ", placements.Select(p => p.DeviceName))}。");
        }

        int dx = -origin.X;
        int dy = -origin.Y;

        return placements
            .Select(p => p with
            {
                X = p.X + dx,
                Y = p.Y + dy,
                IsPrimary = string.Equals(p.DeviceName, primaryDevice, StringComparison.OrdinalIgnoreCase),
            })
            .ToList();
    }

    /// <summary>
    /// Writes a whole desktop layout in one batch. The primary is written first (Windows is
    /// happiest re-anchoring the origin before the satellites move), then every other monitor,
    /// then a single commit. ChangeDisplaySettingsEx is tried first; when it cannot arrange
    /// the set, the same layout is retried through SetDisplayConfig with a caller-supplied
    /// config (see <see cref="ApplyLayoutViaSetDisplayConfig"/>), which does not consult
    /// the display database's committed modes.
    /// </summary>
    public static void ApplyLayout(IReadOnlyList<MonitorPlacement> placements, Action<string>? log = null)
    {
        if (placements.Count == 0) return;

        int primaryCount = placements.Count(p => p.IsPrimary);
        if (primaryCount != 1)
            throw new DisplayArrangeException($"必须恰好有一台主显示器，实际为 {primaryCount} 台。");

        MonitorPlacement primary = placements.First(p => p.IsPrimary);
        if (primary.X != 0 || primary.Y != 0)
            throw new DisplayArrangeException(
                $"主显示器必须位于 (0,0)；{primary.DeviceName} 位于 ({primary.X},{primary.Y})。");

        var problems = new List<string>();

        foreach (MonitorPlacement p in placements.OrderByDescending(p => p.IsPrimary))
        {
            uint flags = CDS_UPDATEREGISTRY | CDS_NORESET;
            if (p.IsPrimary) flags |= CDS_SET_PRIMARY;

            int rc = SetPlacement(p, flags, useRefresh: p.RefreshHz > 1);
            if (rc != DISP_CHANGE_SUCCESSFUL && p.RefreshHz > 1)
            {
                log?.Invoke($"  {p.DeviceName}：在 {p.RefreshHz}Hz 下被拒绝（{DispChangeToString(rc)}）；" +
                            "不指定刷新率重试。");
                rc = SetPlacement(p, flags, useRefresh: false);
            }

            if (rc != DISP_CHANGE_SUCCESSFUL)
                problems.Add($"{p.DeviceName} -> ({p.X},{p.Y}): {DispChangeToString(rc)}");
            else
                log?.Invoke($"  {p.DeviceName}：{p.Width}x{p.Height}，位于 ({p.X},{p.Y})" +
                            (p.IsPrimary ? "   <-- 主显示器" : string.Empty));
        }

        int commit = ChangeDisplaySettingsExApply(null, IntPtr.Zero, IntPtr.Zero, 0, IntPtr.Zero);
        if (commit != DISP_CHANGE_SUCCESSFUL)
            problems.Add($"最终应用：{DispChangeToString(commit)}");

        if (problems.Count == 0) return;

        // CDS could not arrange this set. On lu's machine every CDS attempt fails
        // because the virtual monitors' identities have never had a mode committed,
        // so CDS validation has nothing to validate against (primary BADMODE, second
        // FAILED, physical fine). Retrying the same layout through SetDisplayConfig
        // with a caller-supplied config does not consult the committed-mode database.
        // Keep both errors if the fallback fails too — the CDS error is the
        // diagnostic that matters, the SDC error says whether the rescue worked.
        string cdsErrors = string.Join("； ", problems);
        log?.Invoke($"  CDS 排列失败（{cdsErrors}），改用 SetDisplayConfig 兜底重排。");
        try
        {
            ApplyLayoutViaSetDisplayConfig(placements, log);
            log?.Invoke("  SetDisplayConfig 兜底排列成功。");
        }
        catch (DisplayArrangeException ex)
        {
            throw new DisplayArrangeException(
                "无法排列显示器：CDS 失败（" + cdsErrors + "）；SetDisplayConfig 兜底也失败（" + ex.Message + "）。");
        }
    }

    /// <summary>
    /// Applies a layout through SetDisplayConfig with a caller-supplied config
    /// (SDC_USE_SUPPLIED_DISPLAY_CONFIG | SDC_ALLOW_CHANGES | SDC_SAVE_TO_DATABASE
    /// | SDC_APPLY). Unlike ChangeDisplaySettingsEx this does not consult the
    /// display database's committed modes or the GDI mode list: every path and
    /// mode is supplied by the caller, so a monitor whose identity has never had
    /// a mode committed can still be activated and arranged.
    ///
    /// Modes: each virtual monitor's target mode is built from the same CVT
    /// reduced-blanking timing the driver reports for its only mode (see
    /// driver/Timing.cpp DeskSplitComputeTiming), so CCD hands the driver the
    /// mode it already advertises. A monitor whose placement carries no refresh
    /// (the physical monitor) keeps the target mode Windows is currently using
    /// for it, verbatim. The primary is the placement at (0,0) — the layout
    /// must already be re-based, as <see cref="ComposeLayout"/> does.
    /// Any other currently-active path not in <paramref name="placements"/>
    /// is carried over unchanged, so the fallback never switches off a monitor
    /// it was not asked to move.
    /// </summary>
    internal static void ApplyLayoutViaSetDisplayConfig(
        IReadOnlyList<MonitorPlacement> placements, Action<string>? log = null)
    {
        if (placements.Count == 0) return;

        int primaryCount = placements.Count(p => p.IsPrimary);
        if (primaryCount != 1)
            throw new DisplayArrangeException($"必须恰好有一台主显示器，实际为 {primaryCount} 台。");
        MonitorPlacement primary = placements.First(p => p.IsPrimary);
        if (primary.X != 0 || primary.Y != 0)
            throw new DisplayArrangeException(
                $"主显示器必须位于 (0,0)；{primary.DeviceName} 位于 ({primary.X},{primary.Y})。");

        IReadOnlyList<DisplayConfig.PathIdentity> identities = DisplayConfig.QueryPathIdentities();
        var byName = identities.ToDictionary(i => i.DeviceName, StringComparer.OrdinalIgnoreCase);

        // Enumerated monitors, for the current refresh of a monitor whose
        // placement carries none (the physical monitor) and whose target mode
        // Windows has no record of.
        var enumerated = DisplayConfig.EnumerateAll()
            .ToDictionary(m => m.DeviceName, StringComparer.OrdinalIgnoreCase);

        var paths = new List<DISPLAYCONFIG_PATH_INFO>();
        var modes = new List<DISPLAYCONFIG_MODE_INFO>();
        var placedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (MonitorPlacement p in placements)
        {
            if (!byName.TryGetValue(p.DeviceName, out DisplayConfig.PathIdentity? identity))
                throw new DisplayArrangeException(
                    $"显示器 {p.DeviceName} 在显示配置中没有对应的路径，无法用 SetDisplayConfig 排列。");
            placedNames.Add(p.DeviceName);

            // Source mode: the placement's rect, 32 bpp. Position carries the
            // desktop-space location; (0,0) is what makes a monitor primary.
            var sourceMode = new DISPLAYCONFIG_MODE_INFO
            {
                infoType = DISPLAYCONFIG_MODE_INFO_TYPE_SOURCE,
                id = identity.Path.sourceInfo.id,
                adapterId = identity.Path.sourceInfo.adapterId,
                modeInfo = new DISPLAYCONFIG_MODE_INFO_UNION
                {
                    sourceMode = new DISPLAYCONFIG_SOURCE_MODE
                    {
                        width = (uint)p.Width,
                        height = (uint)p.Height,
                        pixelFormat = DISPLAYCONFIG_PIXELFORMAT_32BPP,
                        position = new POINTL { x = p.X, y = p.Y },
                    },
                },
            };
            uint sourceIdx = (uint)modes.Count;
            modes.Add(sourceMode);

            DISPLAYCONFIG_MODE_INFO targetMode;
            if (p.RefreshHz <= 1 && identity.CurrentTargetMode is not null)
            {
                // The physical monitor: keep its current target mode verbatim.
                targetMode = identity.CurrentTargetMode.Value;
            }
            else
            {
                uint mhz = 60000u;
                if (p.RefreshHz > 1)
                {
                    mhz = (uint)p.RefreshHz * 1000u;
                }
                else if (enumerated.TryGetValue(p.DeviceName, out MonitorInfo? info) &&
                         info is not null && info.CurrentRefreshMillihertz > 0)
                {
                    mhz = info.CurrentRefreshMillihertz;
                }
                targetMode = new DISPLAYCONFIG_MODE_INFO
                {
                    infoType = DISPLAYCONFIG_MODE_INFO_TYPE_TARGET,
                    id = identity.Path.targetInfo.id,
                    adapterId = identity.Path.targetInfo.adapterId,
                    modeInfo = new DISPLAYCONFIG_MODE_INFO_UNION
                    {
                        targetMode = new DISPLAYCONFIG_TARGET_MODE
                        {
                            targetVideoSignalInfo = ComputeTargetSignalInfo(p.Width, p.Height, mhz),
                        },
                    },
                };
            }
            uint targetIdx = (uint)modes.Count;
            modes.Add(targetMode);

            DISPLAYCONFIG_PATH_INFO path = identity.Path;
            path.sourceInfo.modeInfoIdx = sourceIdx;
            path.sourceInfo.statusFlags = 0;
            path.targetInfo.modeInfoIdx = targetIdx;
            path.targetInfo.refreshRate = targetMode.modeInfo.targetMode.targetVideoSignalInfo.vSyncFreq;
            path.targetInfo.statusFlags = 0;
            path.targetInfo.targetAvailable = true;
            path.flags = DISPLAYCONFIG_PATH_ACTIVE;
            paths.Add(path);
        }

        // Any other currently-active path not in the layout is carried over
        // unchanged (existing modes, current position), so the fallback never
        // switches off a monitor it was not asked to move. SetDisplayConfig
        // with a supplied config exclusively enables the paths it is given.
        foreach (DisplayConfig.PathIdentity identity in identities)
        {
            if (placedNames.Contains(identity.DeviceName)) continue;
            if ((identity.Path.flags & DISPLAYCONFIG_PATH_ACTIVE) == 0) continue;
            if (identity.CurrentSourceMode is null || identity.CurrentTargetMode is null)
            {
                log?.Invoke($"  警告：{identity.DeviceName} 处于活动状态但没有可用的现行模式，兜底配置里不含它。");
                continue;
            }
            uint sourceIdx = (uint)modes.Count;
            modes.Add(identity.CurrentSourceMode.Value);
            uint targetIdx = (uint)modes.Count;
            modes.Add(identity.CurrentTargetMode.Value);
            DISPLAYCONFIG_PATH_INFO path = identity.Path;
            path.sourceInfo.modeInfoIdx = sourceIdx;
            path.targetInfo.modeInfoIdx = targetIdx;
            paths.Add(path);
        }

        int rc = SetDisplayConfig(
            (uint)paths.Count, paths.ToArray(),
            (uint)modes.Count, modes.ToArray(),
            SDC_USE_SUPPLIED_DISPLAY_CONFIG | SDC_ALLOW_CHANGES | SDC_SAVE_TO_DATABASE | SDC_APPLY,
            IntPtr.Zero);
        if (rc != 0)
            throw new DisplayArrangeException($"SetDisplayConfig 返回 Win32 错误 {rc}。");

        foreach (MonitorPlacement p in placements)
            log?.Invoke($"  {p.DeviceName}：{p.Width}x{p.Height}，位于 ({p.X},{p.Y})" +
                        (p.IsPrimary ? "   <-- 主显示器" : string.Empty));
    }

    // ------------------------------------------------------------------ CVT timing
    // Mirrors driver/Timing.cpp (DeskSplitComputeTiming): the driver reports
    // exactly one mode per virtual monitor, computed with CVT reduced
    // blanking; the supplied target mode must describe the same timing, or
    // CCD hands the driver a mode it does not advertise.

    private static DISPLAYCONFIG_VIDEO_SIGNAL_INFO ComputeTargetSignalInfo(
        int width, int height, uint refreshMillihertz)
    {
        if (width < 640 || width > 7680 || height < 640 || height > 7680 ||
            refreshMillihertz < 23000 || refreshMillihertz > 1000000)
            throw new DisplayArrangeException(
                $"无法为 {width}x{height} @ {refreshMillihertz} mHz 构造目标模式（超出驱动的合法范围）。");

        const uint hBlank = 160; // CVT-RB: 48 front porch + 32 sync + 80 back porch
        uint hTotal = (uint)width + hBlank;

        uint vSyncWidth = VSyncWidthForAspect(width, height);
        uint minVBlank = 3 + vSyncWidth + 6;
        double refreshHz = refreshMillihertz / 1000.0;
        double framePeriodUs = 1_000_000.0 / refreshHz;
        uint vBlank = minVBlank;
        double linePeriodUs = (framePeriodUs - 460.0) / height;
        if (linePeriodUs > 0)
        {
            double blankLines = Math.Ceiling(460.0 / linePeriodUs);
            if (blankLines > minVBlank)
                vBlank = blankLines > 4095 ? 4095u : (uint)blankLines;
        }
        uint vTotal = (uint)height + vBlank;

        ulong pixelRate = (refreshMillihertz * (ulong)hTotal * vTotal + 500) / 1000;

        (uint vNum, uint vDen) = ReduceRational(refreshMillihertz, 1000);
        (uint hNum, uint hDen) = ReduceRational(refreshMillihertz * (ulong)vTotal, 1000);

        return new DISPLAYCONFIG_VIDEO_SIGNAL_INFO
        {
            pixelRate = pixelRate,
            hSyncFreq = new DISPLAYCONFIG_RATIONAL { Numerator = hNum, Denominator = hDen },
            vSyncFreq = new DISPLAYCONFIG_RATIONAL { Numerator = vNum, Denominator = vDen },
            activeSize = new DISPLAYCONFIG_2DREGION { cx = (uint)width, cy = (uint)height },
            totalSize = new DISPLAYCONFIG_2DREGION { cx = hTotal, cy = vTotal },
            // Matches the driver's FillSignalInfo for a target mode:
            // videoStandard 255 ("other"), vSyncFreqDivider 1.
            videoStandardAndFlags = 255u | (1u << 16),
            scanLineOrdering = DISPLAYCONFIG_SCANLINE_ORDERING_PROGRESSIVE,
        };
    }

    private static uint VSyncWidthForAspect(int width, int height)
    {
        if (height == 0) return 10;
        double aspect = (double)width / height;
        const double tolerance = 0.02;
        if (Math.Abs(aspect - 4.0 / 3.0) < tolerance) return 4;
        if (Math.Abs(aspect - 16.0 / 9.0) < tolerance) return 5;
        if (Math.Abs(aspect - 16.0 / 10.0) < tolerance) return 6;
        if (Math.Abs(aspect - 5.0 / 4.0) < tolerance) return 7;
        if (Math.Abs(aspect - 15.0 / 9.0) < tolerance) return 7;
        return 10;
    }

    private static (uint Numerator, uint Denominator) ReduceRational(ulong numerator, ulong denominator)
    {
        if (denominator == 0) denominator = 1;
        ulong divisor = Gcd(numerator, denominator);
        numerator /= divisor;
        denominator /= divisor;
        const ulong maxUint32 = 0xFFFFFFFFul;
        while ((numerator > maxUint32 || denominator > maxUint32) && denominator > 1)
        {
            numerator >>= 1;
            denominator >>= 1;
        }
        if (numerator > maxUint32) numerator = maxUint32;
        if (denominator == 0) denominator = 1;
        return ((uint)numerator, (uint)denominator);
    }

    private static ulong Gcd(ulong a, ulong b)
    {
        while (b != 0)
        {
            ulong t = a % b;
            a = b;
            b = t;
        }
        return a != 0 ? a : 1;
    }

    /// <summary>Snapshot of the live desktop as placements (refresh left untouched).</summary>
    public static IReadOnlyList<MonitorPlacement> SnapshotLayout(IEnumerable<MonitorInfo> monitors)
        => monitors
            .Select(m => new MonitorPlacement(
                m.DeviceName, m.CurrentWidth, m.CurrentHeight, m.PositionX, m.PositionY, 0, m.IsPrimary))
            .ToList();

    private static int SetPlacement(MonitorPlacement p, uint flags, bool useRefresh)
    {
        var dm = DEVMODE.Create();
        if (!EnumDisplaySettingsEx(p.DeviceName, ENUM_CURRENT_SETTINGS, ref dm, 0))
            dm = DEVMODE.Create();

        dm.dmDeviceName = string.Empty;
        dm.dmFormName = string.Empty;
        dm.dmSize = (ushort)Marshal.SizeOf<DEVMODE>();
        dm.dmPelsWidth = (uint)p.Width;
        dm.dmPelsHeight = (uint)p.Height;
        dm.dmBitsPerPel = 32;
        dm.dmPositionX = p.X;
        dm.dmPositionY = p.Y;
        dm.dmFields = DM_POSITION | DM_PELSWIDTH | DM_PELSHEIGHT | DM_BITSPERPEL;

        if (useRefresh && p.RefreshHz > 1)
        {
            dm.dmDisplayFrequency = (uint)p.RefreshHz;
            dm.dmFields |= DM_DISPLAYFREQUENCY;
        }

        return ChangeDisplaySettingsEx(p.DeviceName, ref dm, IntPtr.Zero, flags, IntPtr.Zero);
    }
}
