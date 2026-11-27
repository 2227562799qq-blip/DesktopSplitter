// DeskSplitProtocol.h — shared contract between the DesktopSplitter virtual
// display driver, the compositor, and the control app.
// C-compatible: consumed by the UMDF driver (C++) and P/Invoked from C#.
#pragma once

#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

// Device interface exposed by the driver. Control app opens it with CreateFile
// via SetupDiGetClassDevs / CM_Get_Device_Interface_List.
// {8E7A9B21-4C55-4C83-9B2E-7D3A1F60D2AD}
#if defined(DEFINE_GUID)
DEFINE_GUID(GUID_DEVINTERFACE_DESKSPLIT,
    0x8e7a9b21, 0x4c55, 0x4c83, 0x9b, 0x2e, 0x7d, 0x3a, 0x1f, 0x60, 0xd2, 0xad);
#endif
#define DESKSPLIT_DEVINTERFACE_GUID_STR L"{8E7A9B21-4C55-4C83-9B2E-7D3A1F60D2AD}"

// Hardware ID of the root-enumerated software device.
#define DESKSPLIT_HARDWARE_ID L"Root\\DesktopSplitterVdd"

#define DESKSPLIT_MAX_MONITORS      4
#define DESKSPLIT_MAX_MODES         8   // per monitor

// Cap on DESKSPLIT_CONFIG.Generation: the app wraps to 1 past this (it never
// gets there in practice — one generation per apply). The driver rejects
// anything larger so a corrupt config cannot wrap the EDID serial.
#define DESKSPLIT_MAX_GENERATION    10000000u

#define DESKSPLIT_PROTOCOL_VERSION  1

// IOCTLs (FILE_DEVICE_UNKNOWN, METHOD_BUFFERED, FILE_ANY_ACCESS)
#define IOCTL_DESKSPLIT_SET_CONFIG \
    CTL_CODE(FILE_DEVICE_UNKNOWN, 0x900, METHOD_BUFFERED, FILE_ANY_ACCESS)
#define IOCTL_DESKSPLIT_GET_STATUS \
    CTL_CODE(FILE_DEVICE_UNKNOWN, 0x901, METHOD_BUFFERED, FILE_ANY_ACCESS)

#pragma pack(push, 4)

typedef struct DESKSPLIT_MODE {
    uint32_t Width;
    uint32_t Height;
    uint32_t RefreshMillihertz;   // e.g. 164999 for 165 Hz fractional
} DESKSPLIT_MODE;

typedef struct DESKSPLIT_MONITOR_CONFIG {
    uint32_t       ModeCount;                        // 1..DESKSPLIT_MAX_MODES
    uint32_t       PreferredModeIndex;               // index into Modes
    DESKSPLIT_MODE Modes[DESKSPLIT_MAX_MODES];
} DESKSPLIT_MONITOR_CONFIG;

// Input buffer for IOCTL_DESKSPLIT_SET_CONFIG.
// MonitorCount == 0 removes all virtual monitors.
typedef struct DESKSPLIT_CONFIG {
    uint32_t                 Version;                // DESKSPLIT_PROTOCOL_VERSION
    uint32_t                 MonitorCount;           // 0..DESKSPLIT_MAX_MONITORS
    DESKSPLIT_MONITOR_CONFIG Monitors[DESKSPLIT_MAX_MONITORS];
    // 0.6.7: per-apply identity generation. The driver stamps each monitor's
    // EDID serial as DESKSPLIT_EDID_SERIAL_BASE + Generation *
    // DESKSPLIT_MAX_MONITORS + monitor index, so every apply presents Windows
    // with monitors it has never seen before. Windows remembers a monitor
    // set's topology (active vs disconnected) keyed by manufacturer + product
    // code + serial; the fixed serials of <= 0.6.6 meant a set once recorded
    // as disconnected came back disconnected on every later apply, and
    // ChangeDisplaySettingsEx cannot arrange a monitor whose path is not
    // active. Generation 0 reproduces the old fixed serials. Appended only:
    // older drivers/apps keep parsing the fields above it.
    uint32_t                 Generation;
} DESKSPLIT_CONFIG;

// Output buffer for IOCTL_DESKSPLIT_GET_STATUS.
// Extended in 0.6.2: the adapter-ready flag and the last failure stage are
// what the control app reports when an apply times out, so a silent driver
// failure (which never reaches the Windows event log) is diagnosable from
// the app log alone. Fields are appended only; the first two are unchanged.
typedef struct DESKSPLIT_STATUS {
    uint32_t Version;               // driver protocol version
    uint32_t ActiveMonitorCount;    // monitors currently arrived
    uint32_t AdapterReady;          // 1 = IddCx adapter initialised, 0 = not yet / failed
    uint32_t LastErrorStage;        // DESKSPLIT_STAGE_* below, 0 = none
    uint32_t LastErrorNtStatus;     // NTSTATUS of the last failure, 0 = none
} DESKSPLIT_STATUS;

// DESKSPLIT_STATUS.LastErrorStage values.
#define DESKSPLIT_STAGE_NONE              0
#define DESKSPLIT_STAGE_ADAPTER_INIT      1
#define DESKSPLIT_STAGE_MONITOR_CREATE    2
#define DESKSPLIT_STAGE_MONITOR_ARRIVAL   3
#define DESKSPLIT_STAGE_QUEUED_NOT_READY  4

#pragma pack(pop)

// Persisted config: the driver serializes the last DESKSPLIT_CONFIG it
// received into a REG_BINARY value "Config" under its software key
// (WdfDeviceOpenRegistryKey / PLUGPLAY_REGKEY_DRIVER) and replays it at
// adapter start so monitors survive reboot.
#define DESKSPLIT_REG_CONFIG_VALUE L"Config"

// Compositor stop event (control app signals this to shut the compositor down)
#define DESKSPLIT_COMPOSITOR_STOP_EVENT L"Global\\DeskSplitCompositorStop"

#ifdef __cplusplus
}
#endif
