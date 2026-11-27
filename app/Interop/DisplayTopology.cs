using DesktopSplitter.Models;
using static DesktopSplitter.Interop.NativeMethods;

namespace DesktopSplitter.Interop;

/// <summary>
/// Changes which display targets are active before display modes and positions are arranged.
/// </summary>
public static class DisplayTopology
{
    internal static void ActivateVirtualTargets(
        IReadOnlyList<MonitorInfo> monitors, IProgress<string> log, CancellationToken ct)
    {
        if (monitors.All(m => m.IsActivePath)) return;

        (DISPLAYCONFIG_PATH_INFO[] paths, DISPLAYCONFIG_MODE_INFO[] modes) = DisplayConfig.QueryAllConfig();
        var activePaths = paths.Where(p => (p.flags & DISPLAYCONFIG_PATH_ACTIVE) != 0).ToList();
        var activeTargets = activePaths.Select(Target).ToHashSet();
        var occupiedSources = activePaths.Select(Source).ToHashSet();
        var missing = monitors.Select(m => m.DisplayTarget).Where(t => !activeTargets.Contains(t)).ToList();
        if (missing.Count == 0) return;

        var candidates = missing
            .Select(t => new
            {
                Target = t,
                Paths = paths.Where(p => p.targetInfo.targetAvailable && Target(p) == t &&
                                         !occupiedSources.Contains(Source(p))).ToArray(),
            })
            .OrderBy(c => c.Paths.Length)
            .ToArray();
        log.Report("正在激活虚拟显示路径：" +
                   string.Join("、", candidates.Select(c => $"目标 {c.Target} 有 {c.Paths.Length} 条候选路径")) + "。");

        // Keep every active path and its existing mode indices. For a new path, let Windows
        // choose the initial source and target modes from the driver's advertised modes.
        // Several targets can offer the same source. An extended desktop needs distinct sources.
        const uint flags = SDC_USE_SUPPLIED_DISPLAY_CONFIG | SDC_ALLOW_CHANGES;
        const int maxAttempts = 24;
        int attempts = 0;
        if (TryActivate(0)) return;

        throw new DisplayArrangeException(attempts == 0
            ? "无法激活虚拟显示路径：找不到一组可同时使用的显示源。"
            : $"无法激活虚拟显示路径：SetDisplayConfig 尝试了 {attempts} 组路径，请查看上方 Win32 错误。");

        bool TryActivate(int index)
        {
            ct.ThrowIfCancellationRequested();
            if (attempts >= maxAttempts) return false;
            if (index == candidates.Length)
            {
                attempts++;
                var configuration = activePaths.ToArray();
                int rc = SetDisplayConfig((uint)configuration.Length, configuration,
                    (uint)modes.Length, modes, flags | SDC_VALIDATE);
                if (rc != ERROR_SUCCESS)
                {
                    log.Report($"  激活第 {attempts} 组路径：SetDisplayConfig 验证返回 Win32 错误 {rc}。");
                    return false;
                }

                rc = SetDisplayConfig((uint)configuration.Length, configuration,
                    (uint)modes.Length, modes, flags | SDC_APPLY);
                if (rc != ERROR_SUCCESS)
                {
                    log.Report($"  激活第 {attempts} 组路径：SetDisplayConfig 应用返回 Win32 错误 {rc}。");
                    return false;
                }

                log.Report($"  已激活 {missing.Count} 台虚拟显示器，正在读取实际显示设备名称。");
                return true;
            }

            foreach (DISPLAYCONFIG_PATH_INFO candidate in candidates[index].Paths)
            {
                DisplayConfig.Endpoint source = Source(candidate);
                if (!occupiedSources.Add(source)) continue;

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
                if (TryActivate(index + 1)) return true;
                activePaths.RemoveAt(activePaths.Count - 1);
                occupiedSources.Remove(source);
                if (attempts >= maxAttempts) break;
            }
            return false;
        }

        static DisplayConfig.Endpoint Source(DISPLAYCONFIG_PATH_INFO path)
            => DisplayConfig.Endpoint.From(path.sourceInfo.adapterId, path.sourceInfo.id);

        static DisplayConfig.Endpoint Target(DISPLAYCONFIG_PATH_INFO path)
            => DisplayConfig.Endpoint.From(path.targetInfo.adapterId, path.targetInfo.id);
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
