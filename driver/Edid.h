// Edid.h - synthesis of a minimal but structurally valid 128 byte EDID 1.4
// block for each virtual monitor.
//
// Windows keys per-monitor persisted settings off manufacturer ID + product
// code + serial number, so every monitor index gets a distinct serial. The
// serial also carries the apply generation (0.6.7): serial =
// DESKSPLIT_EDID_SERIAL_BASE + Generation * DESKSPLIT_MAX_MONITORS + index,
// so each apply presents a monitor identity Windows has never seen, and a
// topology once recorded as "disconnected" is never restored for a new apply.
#pragma once

#include "Timing.h"

#define DESKSPLIT_EDID_SIZE 128

// Manufacturer ID "DSP" (5-bit packed, big endian) -> 0x1270.
#define DESKSPLIT_EDID_MFG_BYTE0 0x12
#define DESKSPLIT_EDID_MFG_BYTE1 0x70

// EDID serial for monitor index 0 at generation 0 (see DESKSPLIT_CONFIG).
#define DESKSPLIT_EDID_SERIAL_BASE 0xD5000001u

// Builds a 128 byte EDID for the given monitor index / generation / mode list.
// pEdid must point to at least DESKSPLIT_EDID_SIZE bytes.
// Returns false only if the arguments are invalid (in which case pEdid is not
// written).
_Success_(return != false)
bool DeskSplitBuildEdid(
    _In_ UINT32 MonitorIndex,
    _In_ UINT32 Generation,
    _In_ const DESKSPLIT_MONITOR_CONFIG& Config,
    _Out_writes_bytes_(DESKSPLIT_EDID_SIZE) BYTE* pEdid);
