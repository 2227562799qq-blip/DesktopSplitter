using static DesktopSplitter.Interop.NativeMethods;

namespace DesktopSplitter.Interop;

/// <summary>
/// Topology-only display calls, used by the self-test's inactive-repro phase
/// (and nowhere in production): disconnecting the virtual monitors so Windows
/// records their identities as disconnected, to prove the remembered-topology
/// mechanism behind lu's arrange failure.
/// </summary>
public static class DisplayTopology
{
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
