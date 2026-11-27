using DesktopSplitter.Models;
using static DesktopSplitter.Interop.NativeMethods;

namespace DesktopSplitter.Interop;

/// <summary>
/// Changes which display targets are active before display modes and positions are arranged.
/// </summary>
public static class DisplayTopology
{
    internal sealed class RoutingAttempt : IDisposable
    {
        internal HashSet<string> Submitted { get; } = new();
        internal string? LastPathReport { get; set; }
        internal int? ExtendResult { get; set; }
        internal Action? RestorePanel { get; set; }
        public void Dispose() => RestorePanel?.Invoke();
    }

    // True once the queried desktop confirms that each target has its own source.
    internal static bool EnsureIndependentSources(
        IReadOnlyList<MonitorInfo> monitors, IReadOnlyList<(int Width, int Height)> sizes,
        DisplayConfig.Endpoint physical, RoutingAttempt attempt, IProgress<string> log, CancellationToken ct)
    {
        (DISPLAYCONFIG_PATH_INFO[] paths, DISPLAYCONFIG_MODE_INFO[] modes) = DisplayConfig.QueryAllConfig();
        var requested = monitors.Select((m, i) => (m.DisplayTarget, Size: sizes[i]))
            .ToDictionary(entry => entry.DisplayTarget, entry => entry.Size);
        var names = paths.DistinctBy(Source).ToDictionary(Source, DisplayConfig.GetSourceGdiName);
        return ConfigureIndependentSources(paths, modes, requested, names, physical, attempt, log, ct,
            (config, modeArray, flags) => SetDisplayConfig((uint)config.Length, config,
                (uint)modeArray.Length, modeArray, flags),
            flags => SetDisplayConfig(0, null, 0, null, flags));
    }

    internal static bool ConfigureIndependentSources(
        DISPLAYCONFIG_PATH_INFO[] paths, DISPLAYCONFIG_MODE_INFO[] modes,
        IReadOnlyDictionary<DisplayConfig.Endpoint, (int Width, int Height)> sizes,
        IReadOnlyDictionary<DisplayConfig.Endpoint, string> names, DisplayConfig.Endpoint physical, RoutingAttempt attempt,
        IProgress<string> log, CancellationToken ct,
        Func<DISPLAYCONFIG_PATH_INFO[], DISPLAYCONFIG_MODE_INFO[], uint, int> setConfig,
        Func<uint, int> setTopology)
    {
        var active = paths.Where(p => (p.flags & DISPLAYCONFIG_PATH_ACTIVE) != 0).ToList();
        var requested = sizes.Keys.ToHashSet();
        if (HaveIndependentSources(active, requested, names)) return true;

        // Keep unrelated displays (including existing clone groups) exactly once.
        // An active path to one of our targets may be a clone of the laptop panel.
        // Replace its old route in the same SDC call that attaches its new one.
        var activePaths = active.Where(p => !requested.Contains(Target(p))).ToList();
        var occupiedSources = activePaths.Select(Source).ToHashSet();
        var occupiedNames = activePaths.Select(p => names[Source(p)])
            .Where(n => n.Length > 0).ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Drop modes belonging only to the routes being replaced. Clone paths
        // that remain must continue to reference the same source mode index.
        var retainedModes = new List<DISPLAYCONFIG_MODE_INFO>();
        var modeIndices = new Dictionary<uint, uint>();
        uint Retain(uint old)
        {
            if (old == DISPLAYCONFIG_PATH_MODE_IDX_INVALID) return old;
            if (!modeIndices.TryGetValue(old, out uint index))
            {
                index = (uint)retainedModes.Count;
                retainedModes.Add(modes[old]);
                modeIndices.Add(old, index);
            }
            return index;
        }
        for (int i = 0; i < activePaths.Count; i++)
        {
            var p = activePaths[i];
            p.sourceInfo.modeInfoIdx = Retain(p.sourceInfo.modeInfoIdx);
            p.targetInfo.modeInfoIdx = Retain(p.targetInfo.modeInfoIdx);
            activePaths[i] = p;
        }
        var edge = retainedModes.Where(m => m.infoType == DISPLAYCONFIG_MODE_INFO_TYPE_SOURCE)
            .Select(m => m.modeInfo.sourceMode)
            .OrderByDescending(m => m.position.x + (int)m.width).FirstOrDefault();

        var candidates = requested
            .Select(t => new
            {
                Target = t,
                Paths = paths.Where(p => p.targetInfo.targetAvailable && Target(p) == t &&
                                         !occupiedSources.Contains(Source(p)))
                    .OrderByDescending(p => names[Source(p)].Length > 0)
                    .ThenByDescending(p => (p.flags & DISPLAYCONFIG_PATH_ACTIVE) != 0).ToArray(),
            })
            .OrderBy(c => c.Paths.Length)
            .ToArray();
        string report = string.Join("、", candidates.Select(c => $"目标 {c.Target} 有 {c.Paths.Length} 条候选路径"));
        if (candidates.Any(c => c.Paths.Length == 0))
        {
            report += "。筛选前：" + string.Join("；", paths.Where(p => requested.Contains(Target(p)))
                .Select(p => $"{Target(p)} ← {Source(p)} {names[Source(p)]} " +
                    $"可用={p.targetInfo.targetAvailable} 活动={(p.flags & DISPLAYCONFIG_PATH_ACTIVE) != 0} " +
                    $"源已占用={occupiedSources.Contains(Source(p))}"));
            report += "。其他已连接或活动的目标：" + string.Join("、", paths.Where(p => !requested.Contains(Target(p)) &&
                (p.targetInfo.targetAvailable || (p.flags & DISPLAYCONFIG_PATH_ACTIVE) != 0))
                .Select(Target).Distinct());
        }
        if (attempt.LastPathReport != report)
        {
            log.Report("正在为虚拟屏分配独立显示源：" + report + "。");
            attempt.LastPathReport = report;
        }

        // Give each virtual source its own desktop rectangle. Windows picks the target
        // timing from the modes advertised by the driver.
        const uint flags = SDC_USE_SUPPLIED_DISPLAY_CONFIG | SDC_ALLOW_CHANGES;
        const int maxAttempts = 24;
        int attempts = 0;
        var considered = new HashSet<string>(attempt.Submitted);
        if (TryActivate(0, false) || TryActivate(0, true)) return false;

        // EXTEND is a system-wide request. Use it only when the single retained
        // panel and our requested targets account for every available output.
        if (attempts == 0 && attempt.Submitted.Count == 0 && activePaths.Count == 1 && Target(activePaths[0]) == physical &&
            paths.Where(p => p.targetInfo.targetAvailable)
                .All(p => requested.Contains(Target(p)) || Target(p) == physical) &&
            requested.All(t => paths.Any(p => Target(p) == t && p.targetInfo.targetAvailable)))
        {
            if (attempt.ExtendResult is null)
            {
                ct.ThrowIfCancellationRequested();
                // Keep the panel configuration from BEFORE asking Windows to
                // allocate new sources. The next poll can have different mode IDs.
                var panel = activePaths.ToArray();
                var panelModes = retainedModes.ToArray();
                int rc = setTopology(SDC_APPLY | SDC_TOPOLOGY_EXTEND);
                attempt.ExtendResult = rc;
                log.Report($"  请求 Windows 切换到扩展桌面，SetDisplayConfig(EXTEND) 返回 {rc}。");
                if (rc == ERROR_SUCCESS)
                    attempt.RestorePanel = () => log.Report($"  恢复原物理屏配置，Win32 返回 " +
                        $"{setConfig(panel, panelModes, SDC_USE_SUPPLIED_DISPLAY_CONFIG | SDC_APPLY | SDC_ALLOW_CHANGES)}。");
            }
            if (attempt.ExtendResult == ERROR_SUCCESS) return false;
            throw new DisplayArrangeException($"Windows 扩展桌面请求失败，Win32 错误 {attempt.ExtendResult}。");
        }

        throw new DisplayArrangeException(attempts == 0
            ? "无法激活虚拟显示路径：找不到一组可同时使用的显示源。"
            : $"无法激活虚拟显示路径：SetDisplayConfig 尝试了 {attempts} 组路径，请查看上方 Win32 错误。");

        bool TryActivate(int index, bool allowProvisionalNames)
        {
            ct.ThrowIfCancellationRequested();
            if (attempts >= maxAttempts) return false;
            if (index == candidates.Length)
            {
                var configuration = activePaths.ToArray();
                string routes = string.Join("、", configuration.Where(p => requested.Contains(Target(p)))
                    .OrderBy(p => Target(p).ToString(), StringComparer.Ordinal)
                    .Select(p => $"{Target(p)} → 源 {Source(p)}"));
                if (!considered.Add(routes)) return false;
                attempts++;
                var supplied = new List<DISPLAYCONFIG_MODE_INFO>(retainedModes);
                int nextX = edge.position.x + (int)edge.width;
                for (int i = 0; i < configuration.Length; i++)
                {
                    ref var p = ref configuration[i];
                    if (!sizes.TryGetValue(Target(p), out var size)) continue;
                    p.sourceInfo.modeInfoIdx = (uint)supplied.Count;
                    supplied.Add(new DISPLAYCONFIG_MODE_INFO {
                        infoType = DISPLAYCONFIG_MODE_INFO_TYPE_SOURCE, adapterId = p.sourceInfo.adapterId,
                        id = p.sourceInfo.id,
                        modeInfo = new() { sourceMode = new() {
                            width = (uint)size.Width, height = (uint)size.Height,
                            pixelFormat = DISPLAYCONFIG_PIXELFORMAT_32BPP,
                            position = new() { x = nextX, y = edge.position.y },
                        } },
                    });
                    nextX += size.Width;
                }
                var modeArray = supplied.ToArray();
                int rc = setConfig(configuration, modeArray, flags | SDC_VALIDATE);
                if (rc != ERROR_SUCCESS)
                {
                    // Keep the independent routes and let Windows position the new sources
                    // if it cannot fit the temporary row into the existing desktop.
                    for (int i = 0; i < configuration.Length; i++)
                        if (requested.Contains(Target(configuration[i])))
                            configuration[i].sourceInfo.modeInfoIdx = DISPLAYCONFIG_PATH_MODE_IDX_INVALID;
                    modeArray = retainedModes.ToArray();
                    rc = setConfig(configuration, modeArray, flags | SDC_VALIDATE);
                }
                if (rc != ERROR_SUCCESS)
                {
                    log.Report($"  激活第 {attempts} 组路径：SetDisplayConfig 验证返回 Win32 错误 {rc}。");
                    return false;
                }

                rc = setConfig(configuration, modeArray, flags | SDC_APPLY);
                if (rc != ERROR_SUCCESS)
                {
                    log.Report($"  激活第 {attempts} 组路径：SetDisplayConfig 应用返回 Win32 错误 {rc}。");
                    return false;
                }

                attempt.Submitted.Add(routes);
                log.Report("  Windows 已接受独立显示路径：" + routes + "。");
                return true;
            }

            foreach (DISPLAYCONFIG_PATH_INFO candidate in candidates[index].Paths)
            {
                DisplayConfig.Endpoint source = Source(candidate);
                string name = names[source];
                // Source names may still reflect the old topology. Only try aliases
                // after unambiguous names, with distinct source IDs and live readback.
                if (occupiedNames.Contains(name) && !allowProvisionalNames) continue;
                if (!occupiedSources.Add(source)) continue;
                bool reservedName = name.Length > 0 && occupiedNames.Add(name);

                DISPLAYCONFIG_PATH_INFO path = candidate;
                path.flags = DISPLAYCONFIG_PATH_ACTIVE;
                path.sourceInfo.modeInfoIdx = DISPLAYCONFIG_PATH_MODE_IDX_INVALID;
                path.sourceInfo.statusFlags = 0;
                path.targetInfo.modeInfoIdx = DISPLAYCONFIG_PATH_MODE_IDX_INVALID;
                path.targetInfo.rotation = DISPLAYCONFIG_ROTATION_IDENTITY;
                path.targetInfo.scaling = DISPLAYCONFIG_SCALING_IDENTITY;
                path.targetInfo.refreshRate = default;
                path.targetInfo.scanLineOrdering = 0;
                path.targetInfo.statusFlags = 0;
                activePaths.Add(path);
                if (TryActivate(index + 1, allowProvisionalNames)) return true;
                activePaths.RemoveAt(activePaths.Count - 1);
                occupiedSources.Remove(source);
                if (reservedName) occupiedNames.Remove(name);
                if (attempts >= maxAttempts) break;
            }
            return false;
        }
    }

    private static DisplayConfig.Endpoint Source(DISPLAYCONFIG_PATH_INFO path)
        => DisplayConfig.Endpoint.From(path.sourceInfo.adapterId, path.sourceInfo.id);

    private static DisplayConfig.Endpoint Target(DISPLAYCONFIG_PATH_INFO path)
        => DisplayConfig.Endpoint.From(path.targetInfo.adapterId, path.targetInfo.id);

    private static bool HaveIndependentSources(IReadOnlyList<DISPLAYCONFIG_PATH_INFO> active,
        HashSet<DisplayConfig.Endpoint> requested, IReadOnlyDictionary<DisplayConfig.Endpoint, string> names)
    {
        var virtualPaths = active.Where(p => requested.Contains(Target(p))).ToList();
        if (!requested.SetEquals(virtualPaths.Select(Target))) return false;
        return virtualPaths.All(v => names[Source(v)].Length > 0 && active.Count(p =>
            Source(p) == Source(v) || string.Equals(names[Source(p)], names[Source(v)],
                StringComparison.OrdinalIgnoreCase)) == 1);
    }

    /// <summary>
    /// Switches the desktop to the internal display only (Win+P "仅电脑屏幕"),
    /// which disconnects every virtual monitor. Returns false when the call
    /// itself fails; the caller must still verify the monitors are inactive.
    /// </summary>
    public static bool TryDisconnectToInternal(IProgress<string> log)
    {
        int rc = SetDisplayConfig(0, null, 0, null, SDC_APPLY | SDC_TOPOLOGY_INTERNAL);
        if (rc == 0)
        {
            log.Report("SetDisplayConfig 已切到仅内屏（拓扑 INTERNAL），虚拟显示器应已断开。");
            return true;
        }
        log.Report($"SetDisplayConfig 切仅内屏失败（Win32 错误 {rc}）。");
        return false;
    }

    /// <summary>
    /// Detaches the given displays with ChangeDisplaySettingsEx (0x0 mode +
    /// CDS_UPDATEREGISTRY, batched, one commit). Fallback when the topology
    /// switch does not deactivate the monitors.
    /// </summary>
    public static bool TryDetachDisplays(IEnumerable<string> deviceNames, IProgress<string> log)
    {
        foreach (string deviceName in deviceNames)
        {
            var dm = DEVMODE.Create();
            dm.dmPelsWidth = 0;
            dm.dmPelsHeight = 0;
            dm.dmFields = DM_PELSWIDTH | DM_PELSHEIGHT;
            int rc = ChangeDisplaySettingsEx(deviceName, ref dm, IntPtr.Zero, CDS_UPDATEREGISTRY | CDS_NORESET, IntPtr.Zero);
            log.Report(rc == DISP_CHANGE_SUCCESSFUL
                ? $"  {deviceName}：已发分离（0x0 模式）。"
                : $"  {deviceName}：分离失败（{DispChangeToString(rc)}）。");
        }
        int commit = ChangeDisplaySettingsExApply(null, IntPtr.Zero, IntPtr.Zero, 0, IntPtr.Zero);
        log.Report(commit == DISP_CHANGE_SUCCESSFUL
            ? "分离提交成功。"
            : $"分离提交失败（{DispChangeToString(commit)}）。");
        return commit == DISP_CHANGE_SUCCESSFUL;
    }
}
