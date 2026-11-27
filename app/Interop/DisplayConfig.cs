using System.Runtime.InteropServices;
using DesktopSplitter.Models;
using static DesktopSplitter.Interop.NativeMethods;

namespace DesktopSplitter.Interop;

public sealed class DisplayConfigException : Exception
{
    public DisplayConfigException(string message) : base(message) { }
    public DisplayConfigException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>
/// Physical/virtual monitor enumeration via QueryDisplayConfig + DisplayConfigGetDeviceInfo,
/// with mode lists from EnumDisplaySettingsEx.
/// </summary>
public static class DisplayConfig
{
    /// <summary>EDID manufacturer code used by the DesktopSplitter virtual driver (bytes 0x12 0x70).</summary>
    public const string DeskSplitEdidManufacturer = "DSP";

    /// <summary>
    /// EDID serial the driver stamps for monitor index 0 at generation 0;
    /// generation G stamps index N as
    /// <c>DeskSplitSerialBase + G * DESKSPLIT_MAX_MONITORS + N</c> (and product
    /// code N). The low bits still carry the monitor index.
    /// </summary>
    public const uint DeskSplitSerialBase = 0xD5000001;

    /// <summary>
    /// Highest generation the driver accepts (must match DESKSPLIT_MAX_GENERATION
    /// in shared/DeskSplitProtocol.h). The app wraps to 1 past this.
    /// </summary>
    public const uint DeskSplitMaxGeneration = 10000000;

    /// <summary>Highest serial the driver can stamp (any generation, any index).</summary>
    public static uint DeskSplitSerialMax
        => DeskSplitSerialBase + (DeskSplitMaxGeneration + 1) * (uint)DriverClient.DESKSPLIT_MAX_MONITORS - 1;

    private static readonly string[] AdapterMarkers =
    {
        "desktopsplitter", "desktopsplittervdd", "desksplit",
    };

    /// <summary>
    /// True when the EDID carries our vendor id AND a serial inside the reserved range —
    /// the authoritative signal that a monitor came from the DesktopSplitter driver.
    /// </summary>
    public static bool IsDeskSplitEdid(EdidInfo? edid)
        => edid is not null
           && string.Equals(edid.Manufacturer, DeskSplitEdidManufacturer, StringComparison.OrdinalIgnoreCase)
           && edid.SerialNumber >= DeskSplitSerialBase
           && edid.SerialNumber <= DeskSplitSerialMax;

    /// <summary>Driver monitor index from a stamped EDID, or -1.</summary>
    public static int DeskSplitIndexFromEdid(EdidInfo? edid)
        => IsDeskSplitEdid(edid) ? (int)((edid!.SerialNumber - DeskSplitSerialBase) % (uint)DriverClient.DESKSPLIT_MAX_MONITORS) : -1;

    /// <summary>Apply generation from a stamped EDID, or -1.</summary>
    public static int DeskSplitGenerationFromEdid(EdidInfo? edid)
        => IsDeskSplitEdid(edid) ? (int)((edid!.SerialNumber - DeskSplitSerialBase) / (uint)DriverClient.DESKSPLIT_MAX_MONITORS) : -1;

    /// <summary>
    /// Enumerate every active monitor. Entries created by our own virtual driver are
    /// flagged with <see cref="MonitorInfo.IsDeskSplitVirtual"/> rather than dropped so
    /// the apply/revert flows can find them.
    /// </summary>
    public static IReadOnlyList<MonitorInfo> Enumerate() => EnumerateCore(allPaths: false);

    /// <summary>
    /// Enumerate every monitor Windows knows about, including ones whose display
    /// path is not active yet. A virtual monitor the driver has just created
    /// shows up here as soon as it arrives, before Windows activates its path;
    /// the active-only <see cref="Enumerate"/> cannot see it until then. The
    /// apply wait uses this so a slow-to-activate indirect display is counted
    /// as arrived (the arrange step is what activates it), instead of timing
    /// out after 15 seconds reporting 0 monitors on machines where activation
    /// stalls. <see cref="MonitorInfo.IsActivePath"/> tells the two apart.
    /// </summary>
    public static IReadOnlyList<MonitorInfo> EnumerateAll() => EnumerateCore(allPaths: true);

    // QDC_ALL_PATHS can list one connected target on several sources. For arrival,
    // the target is the monitor's identity and a GDI source name may not exist yet.
    internal static IReadOnlyList<MonitorInfo> EnumerateConnectedVirtualTargets()
        => EnumerateCore(allPaths: true, connectedVirtualTargets: true);

    private static IReadOnlyList<MonitorInfo> EnumerateCore(bool allPaths, bool connectedVirtualTargets = false)
    {
        (DISPLAYCONFIG_PATH_INFO[] paths, _) = allPaths ? QueryAllConfig() : QueryActiveConfig();

        Dictionary<string, uint> deviceFlags = EnumerateDeviceFlags();

        // One SetupDi sweep per enumeration; Enumerate() is called in a poll loop.
        IReadOnlyDictionary<string, EdidInfo> edidMap = EdidReader.BuildMap();

        var byDevice = new Dictionary<string, MonitorInfo>(StringComparer.OrdinalIgnoreCase);
        var seenTargets = new HashSet<Endpoint>();
        var ordered = new List<MonitorInfo>();

        foreach (DISPLAYCONFIG_PATH_INFO path in paths)
        {
            bool isActive = (path.flags & DISPLAYCONFIG_PATH_ACTIVE) != 0;
            if (!allPaths && !isActive) continue;
            Endpoint targetKey = Endpoint.From(path.targetInfo.adapterId, path.targetInfo.id);
            // QueryDisplayConfig returns active paths before alternate inactive paths.
            if (connectedVirtualTargets && (!path.targetInfo.targetAvailable || !seenTargets.Add(targetKey)))
                continue;

            string gdiName = GetSourceGdiName(path);
            if (!connectedVirtualTargets && string.IsNullOrEmpty(gdiName)) continue;
            if (!connectedVirtualTargets && byDevice.TryGetValue(gdiName, out MonitorInfo? existing))
            {
                // Same GDI device on two paths (clone group, or the same target
                // listed once active and once inactive): prefer the active entry.
                if (!existing.IsActivePath && isActive)
                {
                    ordered.Remove(existing);
                }
                else
                {
                    continue;
                }
            }

            TargetNameInfo target = GetTargetName(path);
            string adapterPath = GetAdapterPath(path);
            EdidReader.TryLookup(edidMap, target.DevicePath, out EdidInfo? edid);
            bool isDeskSplitVirtual = IsOurVirtualMonitor(target, adapterPath, edid);
            if (connectedVirtualTargets && !isDeskSplitVirtual) continue;

            ModeSurvey survey = string.IsNullOrEmpty(gdiName) || (connectedVirtualTargets && !isActive)
                ? default : SurveyModes(gdiName);
            uint currentRefreshMhz = path.targetInfo.refreshRate.ToMillihertz();

            // Exact millihertz is only knowable for the mode currently in use. If the
            // monitor is already at its max rate we can report the true rational
            // (e.g. 164999); otherwise fall back to whole Hz * 1000.
            uint maxRefreshMhz;
            if (currentRefreshMhz != 0 &&
                (int)Math.Round(currentRefreshMhz / 1000.0) == survey.MaxRefreshHz)
            {
                maxRefreshMhz = currentRefreshMhz;
            }
            else
            {
                maxRefreshMhz = (uint)Math.Max(1, survey.MaxRefreshHz) * 1000u;
            }

            deviceFlags.TryGetValue(gdiName, out uint flags);

            // Exact current-mode rational when DisplayConfig supplies one, else whole Hz.
            uint currentMillihertz = currentRefreshMhz != 0
                ? currentRefreshMhz
                : (uint)Math.Max(0, survey.CurrentRefreshHz) * 1000u;

            var info = new MonitorInfo
            {
                DeviceName = gdiName,
                Index = DeviceIndex(gdiName),
                AdapterIdLow = path.targetInfo.adapterId.LowPart,
                AdapterIdHigh = path.targetInfo.adapterId.HighPart,
                TargetId = path.targetInfo.id,
                FriendlyName = string.IsNullOrWhiteSpace(target.FriendlyName) && !string.IsNullOrEmpty(gdiName)
                    ? FallbackName(gdiName)
                    : target.FriendlyName.Trim(),
                AdapterPath = adapterPath,
                MonitorDevicePath = target.DevicePath,
                EdidManufacturer = edid is not null && !string.IsNullOrEmpty(edid.Manufacturer)
                    ? edid.Manufacturer
                    : target.EdidManufacturer,
                Edid = edid,
                DeskSplitMonitorIndex = DeskSplitIndexFromEdid(edid),
                CurrentWidth = survey.CurrentWidth,
                CurrentHeight = survey.CurrentHeight,
                PositionX = survey.PositionX,
                PositionY = survey.PositionY,
                MaxWidth = survey.MaxWidth,
                MaxHeight = survey.MaxHeight,
                MaxRefreshHz = survey.MaxRefreshHz,
                MaxRefreshMillihertz = maxRefreshMhz,
                CurrentRefreshHz = survey.CurrentRefreshHz,
                CurrentRefreshMillihertz = currentMillihertz,
                IsPrimary = (flags & DISPLAY_DEVICE_PRIMARY_DEVICE) != 0,
                IsActivePath = isActive,
                IsDeskSplitVirtual = isDeskSplitVirtual,
            };

            if (!connectedVirtualTargets) byDevice[gdiName] = info;
            ordered.Add(info);
        }

        return ordered
            .OrderBy(m => DeviceIndex(m.DeviceName))
            .ThenBy(m => m.DeviceName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>Monitors that are NOT produced by our virtual driver.</summary>
    public static IReadOnlyList<MonitorInfo> EnumeratePhysical()
        => Enumerate().Where(m => !m.IsDeskSplitVirtual).ToList();

    /// <summary>Monitors produced by our virtual driver.</summary>
    public static IReadOnlyList<MonitorInfo> EnumerateVirtual()
        => Enumerate().Where(m => m.IsDeskSplitVirtual).ToList();

    /// <summary>Numeric suffix of <c>\\.\DISPLAYn</c>; int.MaxValue if unparseable.</summary>
    public static int DeviceIndex(string deviceName)
    {
        int i = deviceName.LastIndexOf("DISPLAY", StringComparison.OrdinalIgnoreCase);
        if (i < 0) return int.MaxValue;
        string tail = deviceName[(i + "DISPLAY".Length)..];
        return int.TryParse(tail, out int n) ? n : int.MaxValue;
    }

    // ------------------------------------------------------------------ internals

    private static (DISPLAYCONFIG_PATH_INFO[] Paths, DISPLAYCONFIG_MODE_INFO[] Modes) QueryActiveConfig()
        => QueryConfig(QDC_ONLY_ACTIVE_PATHS);

    internal static (DISPLAYCONFIG_PATH_INFO[] Paths, DISPLAYCONFIG_MODE_INFO[] Modes) QueryAllConfig()
        => QueryConfig(QDC_ALL_PATHS);

    internal readonly record struct Endpoint(uint AdapterLow, int AdapterHigh, uint Id)
    {
        internal static Endpoint From(LUID adapter, uint id) => new(adapter.LowPart, adapter.HighPart, id);
        public override string ToString() => $"{AdapterHigh:X8}:{AdapterLow:X8}#{Id}";
    }

    /// <summary>
    /// Raw QDC_ALL_PATHS entry for one GDI device, for the SetDisplayConfig
    /// fallback in <see cref="DisplayArranger"/>: it needs the adapter LUID +
    /// source/target ids of every monitor in the layout, including inactive
    /// ones, plus the source/target modes Windows is currently using for that
    /// path (reused verbatim for monitors whose mode must not change, and for
    /// active paths the fallback carries over untouched).
    /// </summary>
    internal sealed record PathIdentity(
        string DeviceName,
        DISPLAYCONFIG_PATH_INFO Path,
        DISPLAYCONFIG_MODE_INFO? CurrentSourceMode,
        DISPLAYCONFIG_MODE_INFO? CurrentTargetMode);

    /// <summary>
    /// Every path Windows knows about, keyed by GDI device name. When one name
    /// appears on several paths (stale records left by a disconnected monitor),
    /// the active path wins; otherwise the first one seen.
    /// </summary>
    internal static IReadOnlyList<PathIdentity> QueryPathIdentities()
    {
        (DISPLAYCONFIG_PATH_INFO[] paths, DISPLAYCONFIG_MODE_INFO[] modes) = QueryAllConfig();
        var byDevice = new Dictionary<string, PathIdentity>(StringComparer.OrdinalIgnoreCase);
        foreach (DISPLAYCONFIG_PATH_INFO path in paths)
        {
            string gdiName = GetSourceGdiName(path);
            if (string.IsNullOrEmpty(gdiName)) continue;
            bool isActive = (path.flags & DISPLAYCONFIG_PATH_ACTIVE) != 0;
            if (byDevice.TryGetValue(gdiName, out PathIdentity? existing) && !isActive)
                continue;

            DISPLAYCONFIG_MODE_INFO? sourceMode = null;
            DISPLAYCONFIG_MODE_INFO? targetMode = null;
            uint srcIdx = path.sourceInfo.modeInfoIdx;
            if (srcIdx < modes.Length && modes[srcIdx].infoType == DISPLAYCONFIG_MODE_INFO_TYPE_SOURCE)
                sourceMode = modes[srcIdx];
            uint tgtIdx = path.targetInfo.modeInfoIdx;
            if (tgtIdx < modes.Length && modes[tgtIdx].infoType == DISPLAYCONFIG_MODE_INFO_TYPE_TARGET)
                targetMode = modes[tgtIdx];

            byDevice[gdiName] = new PathIdentity(gdiName, path, sourceMode, targetMode);
        }
        return byDevice.Values.ToList();
    }

    private static (DISPLAYCONFIG_PATH_INFO[] Paths, DISPLAYCONFIG_MODE_INFO[] Modes) QueryConfig(uint flags)
    {
        // The topology can change between the sizing call and the query call; retry.
        for (int attempt = 0; attempt < 5; attempt++)
        {
            int rc = GetDisplayConfigBufferSizes(flags, out uint pathCount, out uint modeCount);
            if (rc != ERROR_SUCCESS)
                throw new DisplayConfigException($"GetDisplayConfigBufferSizes 失败（错误 {rc}）。");

            var paths = new DISPLAYCONFIG_PATH_INFO[pathCount];
            var modes = new DISPLAYCONFIG_MODE_INFO[modeCount];

            rc = QueryDisplayConfig(flags, ref pathCount, paths, ref modeCount, modes, IntPtr.Zero);
            if (rc == ERROR_SUCCESS)
            {
                Array.Resize(ref paths, (int)pathCount);
                Array.Resize(ref modes, (int)modeCount);
                return (paths, modes);
            }
            if (rc != ERROR_INSUFFICIENT_BUFFER)
                throw new DisplayConfigException($"QueryDisplayConfig 失败（错误 {rc}）。");
        }

        throw new DisplayConfigException(
            "QueryDisplayConfig 持续以 ERROR_INSUFFICIENT_BUFFER 失败——显示拓扑正在变化。");
    }

    internal static string GetSourceGdiName(DISPLAYCONFIG_PATH_INFO path)
    {
        var req = default(DISPLAYCONFIG_SOURCE_DEVICE_NAME);
        req.viewGdiDeviceName = string.Empty;
        req.header.type = DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME;
        req.header.size = (uint)Marshal.SizeOf<DISPLAYCONFIG_SOURCE_DEVICE_NAME>();
        req.header.adapterId = path.sourceInfo.adapterId;
        req.header.id = path.sourceInfo.id;

        return DisplayConfigGetDeviceInfo(ref req) == ERROR_SUCCESS
            ? req.viewGdiDeviceName ?? string.Empty
            : string.Empty;
    }

    private readonly record struct TargetNameInfo(
        string FriendlyName, string DevicePath, string EdidManufacturer, string EdidManufacturerAlt);

    private static TargetNameInfo GetTargetName(DISPLAYCONFIG_PATH_INFO path)
    {
        var req = default(DISPLAYCONFIG_TARGET_DEVICE_NAME);
        req.monitorFriendlyDeviceName = string.Empty;
        req.monitorDevicePath = string.Empty;
        req.header.type = DISPLAYCONFIG_DEVICE_INFO_GET_TARGET_NAME;
        req.header.size = (uint)Marshal.SizeOf<DISPLAYCONFIG_TARGET_DEVICE_NAME>();
        req.header.adapterId = path.targetInfo.adapterId;
        req.header.id = path.targetInfo.id;

        if (DisplayConfigGetDeviceInfo(ref req) != ERROR_SUCCESS)
            return new TargetNameInfo(string.Empty, string.Empty, string.Empty, string.Empty);

        ushort raw = req.edidManufactureId;
        ushort swapped = (ushort)((raw >> 8) | (raw << 8));

        return new TargetNameInfo(
            req.monitorFriendlyDeviceName ?? string.Empty,
            req.monitorDevicePath ?? string.Empty,
            DecodeEdidManufacturer(swapped),
            DecodeEdidManufacturer(raw));
    }

    private static string GetAdapterPath(DISPLAYCONFIG_PATH_INFO path)
    {
        var req = default(DISPLAYCONFIG_ADAPTER_NAME);
        req.adapterDevicePath = string.Empty;
        req.header.type = DISPLAYCONFIG_DEVICE_INFO_GET_ADAPTER_NAME;
        req.header.size = (uint)Marshal.SizeOf<DISPLAYCONFIG_ADAPTER_NAME>();
        req.header.adapterId = path.targetInfo.adapterId;
        req.header.id = path.targetInfo.id;

        return DisplayConfigGetDeviceInfo(ref req) == ERROR_SUCCESS
            ? req.adapterDevicePath ?? string.Empty
            : string.Empty;
    }

    /// <summary>
    /// EDID vendor id: three 5-bit letters packed into 16 bits (bit 15 unused).
    /// Returns "" when the value does not decode to three A-Z letters.
    /// </summary>
    private static string DecodeEdidManufacturer(ushort value)
    {
        int c1 = ((value >> 10) & 0x1F) + 'A' - 1;
        int c2 = ((value >> 5) & 0x1F) + 'A' - 1;
        int c3 = (value & 0x1F) + 'A' - 1;
        if (c1 < 'A' || c1 > 'Z' || c2 < 'A' || c2 > 'Z' || c3 < 'A' || c3 > 'Z')
            return string.Empty;
        return new string(new[] { (char)c1, (char)c2, (char)c3 });
    }

    private static bool IsOurVirtualMonitor(TargetNameInfo target, string adapterPath, EdidInfo? edid)
    {
        // Primary signal: the driver's stamped EDID (vendor 'DSP' + serial 0xD5000001+index).
        if (IsDeskSplitEdid(edid)) return true;

        // An EDID that parsed cleanly and is NOT ours is authoritative in the negative
        // direction too — a real monitor never carries our vendor id and serial range.
        if (edid is not null && !string.IsNullOrEmpty(edid.Manufacturer) &&
            !string.Equals(edid.Manufacturer, DeskSplitEdidManufacturer, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // Fallbacks for when the EDID could not be read (no cached blob, access denied).
        if (string.Equals(target.EdidManufacturer, DeskSplitEdidManufacturer, StringComparison.OrdinalIgnoreCase)) return true;
        if (string.Equals(target.EdidManufacturerAlt, DeskSplitEdidManufacturer, StringComparison.OrdinalIgnoreCase)) return true;

        if (ContainsAnyMarker(target.FriendlyName)) return true;
        if (ContainsAnyMarker(adapterPath)) return true;
        if (ContainsAnyMarker(target.DevicePath)) return true;

        // Monitor instance paths look like \\?\DISPLAY#DSP0001#5&...  — the token after
        // "DISPLAY#" is the EDID id, so a DSP prefix there identifies our monitors too.
        int i = target.DevicePath.IndexOf("DISPLAY#", StringComparison.OrdinalIgnoreCase);
        if (i >= 0)
        {
            string tail = target.DevicePath[(i + "DISPLAY#".Length)..];
            if (tail.StartsWith(DeskSplitEdidManufacturer, StringComparison.OrdinalIgnoreCase)) return true;
        }

        return false;
    }

    private static bool ContainsAnyMarker(string s)
        => !string.IsNullOrEmpty(s) &&
           AdapterMarkers.Any(m => s.Contains(m, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Monitor-level EnumDisplayDevices name (e.g. "Generic PnP Monitor") for targets whose
    /// EDID carries no friendly name. Returns "" rather than the GDI device name so that
    /// <see cref="MonitorInfo.DisplayLabel"/> falls through to "Display n" — repeating
    /// \\.\DISPLAYn as the label made entries look empty/duplicated.
    /// </summary>
    private static string FallbackName(string gdiName)
    {
        var dd = DISPLAY_DEVICE.Create();
        return EnumDisplayDevices(gdiName, 0, ref dd, 0) && !string.IsNullOrWhiteSpace(dd.DeviceString)
            ? dd.DeviceString.Trim()
            : string.Empty;
    }

    private readonly record struct ModeSurvey(
        int CurrentWidth, int CurrentHeight, int PositionX, int PositionY,
        int MaxWidth, int MaxHeight, int MaxRefreshHz, int CurrentRefreshHz);

    /// <summary>
    /// Walks the driver's mode list. Native resolution = largest pixel count; max refresh =
    /// highest dmDisplayFrequency reported at that resolution (progressive modes only when any exist).
    /// </summary>
    private static ModeSurvey SurveyModes(string deviceName)
    {
        var current = DEVMODE.Create();
        int curW = 0, curH = 0, posX = 0, posY = 0, curHz = 0;
        if (EnumDisplaySettingsEx(deviceName, ENUM_CURRENT_SETTINGS, ref current, 0))
        {
            curW = (int)current.dmPelsWidth;
            curH = (int)current.dmPelsHeight;
            posX = current.dmPositionX;
            posY = current.dmPositionY;
            curHz = (int)current.dmDisplayFrequency;
        }

        long bestPixels = 0;
        int maxW = curW, maxH = curH;
        var freqByRes = new Dictionary<(int, int), int>();

        for (int i = 0; ; i++)
        {
            var dm = DEVMODE.Create();
            if (!EnumDisplaySettingsEx(deviceName, i, ref dm, 0)) break;

            int w = (int)dm.dmPelsWidth;
            int h = (int)dm.dmPelsHeight;
            int hz = (int)dm.dmDisplayFrequency;
            if (w <= 0 || h <= 0) continue;
            if (dm.dmBitsPerPel != 0 && dm.dmBitsPerPel < 32) continue;
            if ((dm.dmDisplayFlags & DM_INTERLACED) != 0) continue;
            if (hz <= 1) continue;   // 0 and 1 mean "hardware default"

            long pixels = (long)w * h;
            if (pixels > bestPixels)
            {
                bestPixels = pixels;
                maxW = w;
                maxH = h;
            }

            var key = (w, h);
            if (!freqByRes.TryGetValue(key, out int best) || hz > best)
                freqByRes[key] = hz;
        }

        int maxHz = curHz;
        if (freqByRes.TryGetValue((maxW, maxH), out int nativeHz) && nativeHz > 0)
            maxHz = nativeHz;
        if (maxHz <= 1) maxHz = 60;

        if (curW == 0 || curH == 0) { curW = maxW; curH = maxH; }
        if (curHz <= 1) curHz = maxHz;

        return new ModeSurvey(curW, curH, posX, posY, maxW, maxH, maxHz, curHz);
    }

    private static Dictionary<string, uint> EnumerateDeviceFlags()
    {
        var map = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase);
        for (uint i = 0; ; i++)
        {
            var dd = DISPLAY_DEVICE.Create();
            if (!EnumDisplayDevices(null, i, ref dd, 0)) break;
            if (!string.IsNullOrEmpty(dd.DeviceName))
                map[dd.DeviceName] = dd.StateFlags;
        }
        return map;
    }
}
