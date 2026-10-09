# Technical Notes

How DisplayProfiles works internally, what was learned about NVAPI and Windows on the way, and what to
keep in mind when reusing the core library. Observations are marked *verified* (reproduced and
checked) or *observed* (seen, not systematically reproduced).

## Architecture

| Component | Responsibility |
|-----------|----------------|
| `DisplayProfiles.Core` | All display logic. No dependencies besides the .NET runtime, `nvapi64.dll` (installed with the NVIDIA driver) and `user32.dll` |
| `DisplayProfiles.Tray` | WinForms tray app, companion window, ASP.NET Core minimal API on `127.0.0.1:47811` |
| `DisplayProfiles.Cli` | Console front end for the same core functions |

Core files:

| File | Responsibility |
|------|----------------|
| `NvApi.cs` | NVAPI via `nvapi_QueryInterface`: GPUs, display IDs, EDID, Surround grids (read, validate, set), display config (real refresh rate) |
| `WinDisplay.cs` | Windows CCD API: read the active layout, `SetDisplayConfig` (validate / apply) incl. adapter LUID remapping |
| `Snapshot.cs` | Captures the complete state (NVIDIA + Windows), headset detection, derived values (real Hz, bezel corrected), layout description, profile JSON format |
| `Apply.cs` | The switch sequence below, incl. monitor check, backup and read-back comparison |
| `ProfileStore.cs` | Profile files: list, load, save, rename, archive, name validation |
| `DisplayService.cs` | Thread-safe facade for long-running hosts: status, profile CRUD, apply |

The tray menu and the companion window talk to the REST API like any other client; there is no
second code path. `DisplayService` serializes all driver access and caches the last state, so status
polling never touches the driver while a switch runs.

## What a switch does

1. **Monitor check** — every display of the profile must be connected on the same GPU output with the
   same EDID serial number. Otherwise nothing is changed (code 5).
2. **NVIDIA Surround** — only if the Surround layout differs from the profile: validate with
   `NvAPI_Mosaic_ValidateDisplayGrids`, then set with `NvAPI_Mosaic_SetDisplayGrids`, flags *keep
   current GPU topology* and *no driver reload*. A grid that would need a driver reload is refused.
   Displays of an active Surround that the profile does not use are split out as single displays.
3. **Windows layout** — `SetDisplayConfig` with the saved paths and modes: active displays,
   positions, resolution, refresh rate, primary display. Validated first, then applied and saved to
   the Windows display database.
4. **Read-back** — the state is captured again and compared with the profile. On a deviation the tool
   waits 2 s and reads once more before reporting it.

A backup of the previous state is written before step 2 (`profiles\_backup\before-<name>-<time>.json`).

## Profiles

Profiles are JSON snapshots in `%LOCALAPPDATA%\DisplayProfiles\profiles\<name>.json`. Besides readable
fields they contain the raw NVIDIA grid structures and the raw Windows CCD path/mode buffers (Base64);
that is what makes an exact restore possible. Overwritten profiles are copied to `_backup`, deleted
profiles are moved to `_archive`.

Creating a profile captures the state twice, 1.5 s apart, and refuses to save if the two reads
differ — right after a mode change the display state can still be settling.

## Headset detection

The headset is recognised by its EDID (manufacturer `SFL`, product `0x0102`, name `SVP-VG 3` —
Shiftall MeganeX superlight 8K). It reports `connected` on its GPU output as soon as it is plugged in
and powered, and `active` only while SteamVR drives it in direct mode. It also appears as a USB
composite device (`USB\VID_28DE&PID_2300\…`), which could serve as an independent trigger.

## Behaviour and pitfalls

1. **Surround blocks a shared output.** With the Surround grid dissolved, the headset plugged into the
   formerly shared DisplayPort output was detected without a reboot (*verified*). With Surround
   active it was not detected until a reboot (*observed* repeatedly before the tool existed).
2. **Switch before swapping cables.** Whether dissolving Surround *after* the headset is plugged in
   also works is not tested.
3. **Do not plug or unplug displays during a switch.** A display disappearing mid-switch triggers the
   Windows hot-plug handling, which overrides the layout (*verified*). Running the same switch again
   fixes it.
4. **A switched-off HDMI monitor still reports "connected"** (its EDID stays readable). Validation
   passes, but `SetDisplayConfig` fails with error 31 (*verified*). DisplayPort monitors report
   "not connected" when switched off.
5. **Short-lived deviations.** Right after a mode change a DisplayPort monitor can drop its link for a
   moment, so an immediate read-back may differ from the final state (*verified once*). Hence the
   second read before a deviation is reported.
6. **Windows remembers a layout per monitor combination.** Because the layout is saved to the Windows
   display database, Windows restores it automatically when the same set of monitors appears again —
   e.g. switching the Surround monitors off falls back to the single desktop monitor (*verified*).
7. **Surround does not come back by itself** once it was dissolved — at least not with only two of
   the three monitors present (*verified*).
8. **Adding a display next to an active Surround resets the Surround mode.** Turning on a fourth
   monitor in the Windows settings made Windows pick the plain Surround resolution at the highest
   refresh rate — bezel correction off, although the gap values stayed in the NVIDIA grid
   (*verified*). Set the bezel-corrected resolution in the Windows display settings again, then
   re-capture. The layout description shows "(Bezel)" only when the desktop really is bezel corrected.
9. **Taskbar on Surround.** The NVIDIA Control Panel option *Desktop → Surround Displays → Confine
   Taskbar to Single Display* keeps the taskbar on the middle monitor. With a fourth display next to
   the Surround the taskbar spanned all Surround monitors; with the Surround alone it was confined
   again (*observed*). The option is not part of the public NVAPI and not documented.
10. **Duration.** A switch that changes Surround takes ~13 s in the driver plus ~5 s settle time in the
    tool. Screens go black briefly. Never run a switch on a UI thread or in a real-time handler.
11. **Never fall back to driver restarts.** Disabling/re-enabling the display adapter (e.g.
    `pnputil /restart-device`) to "reset" the display state took down the Windows compositor and the
    VR headset and required a full reboot (*observed*). DisplayProfiles never does this.

## NVIDIA quirks

- `NvAPI_Mosaic_EnumDisplayGrids` and `NvAPI_Mosaic_GetCurrentTopo` report a fixed refresh rate (180 Hz
  on the test rig, the panels' maximum), regardless of what is actually driven. The real rate comes from
  `NvAPI_DISP_GetDisplayConfig` (`refreshRate1K` in the advanced target info).
- The `applyWithBezelCorrect` grid flag reads as 0 even when bezel correction is active. The tool
  derives it: desktop width = columns × width − sum of the (negative) overlaps, e.g.
  3 × 2560 + 2 × 43 = 7766.
- Grid flag bit 1 (`immersiveGaming`) marks GeForce Surround and must be kept when setting a grid,
  otherwise NVIDIA returns `-113 NVAPI_NO_ACTIVE_SLI_TOPOLOGY`.
- `NvAPI_DISP_GetDisplayConfig` needs three passes: count → path array → targets / source modes.
- Windows adapter LUIDs change on every boot; saved CCD paths and modes are remapped to the current
  LUIDs by target ID before `SetDisplayConfig`.

## NVAPI reference

All calls go through `nvapi_QueryInterface` (`cdecl`, x64). IDs and layouts come from the NVAPI SDK
headers at <https://github.com/NVIDIA/nvapi> (see [THIRD-PARTY-NOTICES.md](../THIRD-PARTY-NOTICES.md)).

| Function | ID | Writes? |
|----------|----|---------|
| `NvAPI_Initialize` | `0x0150e828` | no |
| `NvAPI_EnumPhysicalGPUs` | `0xe5ac921f` | no |
| `NvAPI_GPU_GetFullName` | `0xceee8e9f` | no |
| `NvAPI_GPU_GetAllDisplayIds` | `0x785210a2` | no |
| `NvAPI_GPU_GetEDID` | `0x37d32e69` | no |
| `NvAPI_DISP_GetDisplayIdByDisplayName` | `0xae457190` | no |
| `NvAPI_DISP_GetDisplayConfig` | `0x11abccf8` | no |
| `NvAPI_Mosaic_EnumDisplayGrids` | `0xdf2887af` | no |
| `NvAPI_Mosaic_ValidateDisplayGrids` | `0xcf43903d` | no |
| `NvAPI_Mosaic_SetDisplayGrids` | `0x4d959a89` | **yes** — always with flags `0x3` (current GPU topology + no driver reload) |

Structure sizes (x64); version word = `size | (version << 16)`:

| Struct | Size | Version |
|--------|------|---------|
| `NV_GPU_DISPLAYIDS` | 16 | 3 |
| `NV_EDID_V3` | 272 | 3 |
| `NV_MOSAIC_GRID_TOPO_V2` (header 20 + 64 displays × 28 + settings 20) | 1832 | 2 |
| `NV_MOSAIC_DISPLAY_TOPO_STATUS` (header 16 + 128 × 16) | 2064 | 1 |
| `NV_DISPLAYCONFIG_PATH_INFO_V2` | 48 | 2 |
| `NV_DISPLAYCONFIG_PATH_TARGET_INFO_V2` | 24 | — |
| `NV_DISPLAYCONFIG_SOURCE_MODE_INFO_V1` | 32 | — |
| `NV_DISPLAYCONFIG_PATH_ADVANCED_TARGET_INFO_V1` | 128 | 1 |
| `DISPLAYCONFIG_PATH_INFO` (Windows) | 72 | — |
| `DISPLAYCONFIG_MODE_INFO` (Windows) | 64 | — |

When setting a grid: keep bits 1 (`immersiveGaming`), 2 (`baseMosaic`) and 5 (`pixelShift`) from the
saved grid, set bit 0 (`applyWithBezelCorrect`) from the derived value, always clear bit 3
(`driverReloadAllowed`). Write the rounded real refresh rate into the grid's display settings and let
the following `SetDisplayConfig` set the exact rate. Abort on any validation error flag and on the
warning `DRIVER_RELOAD_REQUIRED`.

## Reusing the core library

`DisplayProfiles.Core.dll` can be referenced by another .NET 8 process, e.g. as a native plugin of a
host application. `DisplayService` is the entry point: it serializes driver access, caches the state
and offers exactly the operations of the REST API. A host that embeds the library must not run in
parallel with the tray app; alternatively it stays a client of the REST API
([rest-api.md](rest-api.md)).
