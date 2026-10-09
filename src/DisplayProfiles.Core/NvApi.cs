// Copyright (c) 2026 Rausch IT
// SPDX-License-Identifier: MIT
// NVAPI function IDs and structure layouts derived from the NVIDIA NVAPI SDK headers (MIT,
// Copyright (c) NVIDIA CORPORATION & AFFILIATES) - see THIRD-PARTY-NOTICES.md.

using System.Runtime.InteropServices;
using System.Text;

namespace DisplayProfiles;

// Minimal NVAPI access. Function IDs and struct layouts from NVIDIA/nvapi
// (nvapi_interface.h, nvapi.h, nvapi_lite_common.h).
// The only write path is SetGrids (NvAPI_Mosaic_SetDisplayGrids), always called with NO_DRIVER_RELOAD.
public static class NvApi
{
    [DllImport("nvapi64.dll", EntryPoint = "nvapi_QueryInterface", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr QueryInterface(uint id);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int DInit();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int DEnumGpus([Out] IntPtr[] handles, out uint count);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int DFullName(IntPtr gpu, StringBuilder name);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int DGetAllIds(IntPtr gpu, IntPtr ids, ref uint count);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int DGetEdid(IntPtr gpu, uint displayId, IntPtr edid);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int DEnumGrids(IntPtr grids, ref uint count);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int DIdByName([MarshalAs(UnmanagedType.LPStr)] string displayName, out uint displayId);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int DGetDisplayConfig(ref uint count, IntPtr paths);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int DValidateGrids(uint flags, IntPtr grids, IntPtr status, uint count);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int DSetGrids(IntPtr grids, uint count, uint flags);

    private const uint ID_Initialize = 0x0150e828;
    private const uint ID_EnumPhysicalGPUs = 0xe5ac921f;
    private const uint ID_GPU_GetFullName = 0xceee8e9f;
    private const uint ID_GPU_GetAllDisplayIds = 0x785210a2;
    private const uint ID_GPU_GetEDID = 0x37d32e69;
    private const uint ID_Mosaic_EnumDisplayGrids = 0xdf2887af;
    private const uint ID_DISP_GetDisplayIdByDisplayName = 0xae457190;
    private const uint ID_DISP_GetDisplayConfig = 0x11abccf8;
    private const uint ID_Mosaic_ValidateDisplayGrids = 0xcf43903d;
    private const uint ID_Mosaic_SetDisplayGrids = 0x4d959a89;

    // NVAPI_MOSAIC_SETDISPLAYTOPO_FLAG_*: keep current GPU topology, never reload the driver.
    public const uint SetTopoFlags = (1u << 0) | (1u << 1);
    public const uint WarningDriverReloadRequired = 1u << 1;

    // NV_MOSAIC_DISPLAY_TOPO_STATUS: 16-byte header + NVAPI_MAX_DISPLAYS (32*4 = 128) entries of 16 bytes (VER1).
    private const int TopoStatusSize = 16 + 128 * 16;
    private const uint TopoStatusVer = TopoStatusSize | (1u << 16);

    // x64 layouts for NvAPI_DISP_GetDisplayConfig.
    private const int PathInfoV2Size = 48;      // NV_DISPLAYCONFIG_PATH_INFO_V2 (VER2)
    private const int TargetInfoV2Size = 24;    // NV_DISPLAYCONFIG_PATH_TARGET_INFO_V2
    private const int SourceModeInfoSize = 32;  // NV_DISPLAYCONFIG_SOURCE_MODE_INFO_V1
    private const int AdvTargetInfoSize = 128;  // NV_DISPLAYCONFIG_PATH_ADVANCED_TARGET_INFO_V1 (VER1)

    // NV_GPU_DISPLAYIDS (VER2 = size | 3<<16)
    private const int DisplayIdsSize = 16;
    private const uint DisplayIdsVer = DisplayIdsSize | (3u << 16);

    // NV_EDID_V3 (VER3 = size | 3<<16)
    private const int EdidSize = 4 + 256 + 4 + 4 + 4;
    private const uint EdidVer = EdidSize | (3u << 16);

    // NV_MOSAIC_GRID_TOPO_V2 (VER2 = size | 2<<16)
    public const int MosaicMaxDisplays = 64;
    private const int GridDisplayV2Size = 28;
    private const int DisplaySettingV1Size = 20;
    private const int GridHeaderSize = 20;
    public const int GridV2Size = GridHeaderSize + MosaicMaxDisplays * GridDisplayV2Size + DisplaySettingV1Size;
    private const uint GridV2Ver = GridV2Size | (2u << 16);

    private static bool _initialized;

    private static T Fn<T>(uint id) where T : Delegate
    {
        var p = QueryInterface(id);
        if (p == IntPtr.Zero) throw new InvalidOperationException($"NVAPI function 0x{id:x8} not available");
        return Marshal.GetDelegateForFunctionPointer<T>(p);
    }

    private static void Check(int rc, string what)
    {
        if (rc != 0) throw new InvalidOperationException($"{what} failed (NVAPI status {rc})");
    }

    public static void Init()
    {
        if (_initialized) return;
        Check(Fn<DInit>(ID_Initialize)(), "NvAPI_Initialize");
        _initialized = true;
    }

    public static List<(IntPtr Handle, string Name)> Gpus()
    {
        var handles = new IntPtr[64];
        Check(Fn<DEnumGpus>(ID_EnumPhysicalGPUs)(handles, out var count), "NvAPI_EnumPhysicalGPUs");
        var result = new List<(IntPtr, string)>();
        for (int i = 0; i < count; i++)
        {
            var sb = new StringBuilder(64);
            Fn<DFullName>(ID_GPU_GetFullName)(handles[i], sb);
            result.Add((handles[i], sb.ToString()));
        }
        return result;
    }

    public static List<NvDisplay> AllDisplays(IntPtr gpu)
    {
        var fn = Fn<DGetAllIds>(ID_GPU_GetAllDisplayIds);
        uint n = 0;
        Check(fn(gpu, IntPtr.Zero, ref n), "NvAPI_GPU_GetAllDisplayIds(count)");
        var list = new List<NvDisplay>();
        if (n == 0) return list;

        var buf = Marshal.AllocHGlobal((int)n * DisplayIdsSize);
        try
        {
            ZeroFill(buf, (int)n * DisplayIdsSize);
            for (int i = 0; i < n; i++) Marshal.WriteInt32(buf + i * DisplayIdsSize, unchecked((int)DisplayIdsVer));
            Check(fn(gpu, buf, ref n), "NvAPI_GPU_GetAllDisplayIds");
            for (int i = 0; i < n; i++)
            {
                var e = buf + i * DisplayIdsSize;
                uint bits = unchecked((uint)Marshal.ReadInt32(e, 12));
                list.Add(new NvDisplay
                {
                    DisplayId = unchecked((uint)Marshal.ReadInt32(e, 8)),
                    Connector = ConnectorName(Marshal.ReadInt32(e, 4)),
                    IsDynamic = (bits & 1) != 0,
                    IsActive = ((bits >> 2) & 1) != 0,
                    IsOSVisible = ((bits >> 4) & 1) != 0,
                    IsConnected = ((bits >> 6) & 1) != 0,
                    IsPhysicallyConnected = ((bits >> 17) & 1) != 0,
                });
            }
        }
        finally { Marshal.FreeHGlobal(buf); }

        foreach (var d in list.Where(d => d.IsConnected))
            d.Edid = ReadEdidBase(gpu, d.DisplayId);
        return list;
    }

    // Returns the first 128-byte EDID block, or null if unavailable.
    private static byte[]? ReadEdidBase(IntPtr gpu, uint displayId)
    {
        var buf = Marshal.AllocHGlobal(EdidSize);
        try
        {
            ZeroFill(buf, EdidSize);
            Marshal.WriteInt32(buf, unchecked((int)EdidVer));
            int rc = Fn<DGetEdid>(ID_GPU_GetEDID)(gpu, displayId, buf);
            if (rc != 0) return null;
            var data = new byte[128];
            Marshal.Copy(buf + 4, data, 0, 128);
            return data;
        }
        finally { Marshal.FreeHGlobal(buf); }
    }

    // Maps a Windows display name (e.g. "\\.\DISPLAY2") to its NVIDIA display ID.
    // With Surround active, NVIDIA returns only the top-left display of the grid.
    public static uint? DisplayIdByName(string displayName)
    {
        int rc = Fn<DIdByName>(ID_DISP_GetDisplayIdByDisplayName)(displayName, out var id);
        return rc == 0 ? id : null;
    }

    // Current display configuration as the driver actually drives it (source desktop size, refresh rate per target).
    // Mosaic grid queries report a different (non-current) frequency, so this is the source of truth for Hz.
    public static List<NvPath> DisplayConfig()
    {
        var fn = Fn<DGetDisplayConfig>(ID_DISP_GetDisplayConfig);
        uint n = 0;
        Check(fn(ref n, IntPtr.Zero), "NvAPI_DISP_GetDisplayConfig(pass 1)");
        var result = new List<NvPath>();
        if (n == 0) return result;

        var allocs = new List<IntPtr>();
        IntPtr Alloc(int size)
        {
            var p = Marshal.AllocHGlobal(size);
            ZeroFill(p, size);
            allocs.Add(p);
            return p;
        }

        try
        {
            var paths = Alloc((int)n * PathInfoV2Size);
            for (int i = 0; i < n; i++) Marshal.WriteInt32(paths + i * PathInfoV2Size, PathInfoV2Size | (2 << 16));
            Check(fn(ref n, paths), "NvAPI_DISP_GetDisplayConfig(pass 2)");

            for (int i = 0; i < n; i++)
            {
                var p = paths + i * PathInfoV2Size;
                int tc = Marshal.ReadInt32(p, 8);
                var tgts = Alloc(Math.Max(tc, 1) * TargetInfoV2Size);
                for (int t = 0; t < tc; t++)
                {
                    var adv = Alloc(AdvTargetInfoSize);
                    Marshal.WriteInt32(adv, 0, AdvTargetInfoSize | (1 << 16));
                    Marshal.WriteIntPtr(tgts + t * TargetInfoV2Size, 8, adv);
                }
                Marshal.WriteIntPtr(p, 16, tgts);
                Marshal.WriteIntPtr(p, 24, Alloc(SourceModeInfoSize));
            }
            Check(fn(ref n, paths), "NvAPI_DISP_GetDisplayConfig(pass 3)");

            for (int i = 0; i < n; i++)
            {
                var p = paths + i * PathInfoV2Size;
                var src = Marshal.ReadIntPtr(p, 24);
                var path = new NvPath
                {
                    SourceWidth = Marshal.ReadInt32(src, 0),
                    SourceHeight = Marshal.ReadInt32(src, 4),
                    X = Marshal.ReadInt32(src, 16),
                    Y = Marshal.ReadInt32(src, 20),
                    Primary = (Marshal.ReadInt32(src, 28) & 1) != 0,
                };
                int tc = Marshal.ReadInt32(p, 8);
                var tgts = Marshal.ReadIntPtr(p, 16);
                for (int t = 0; t < tc; t++)
                {
                    var adv = Marshal.ReadIntPtr(tgts + t * TargetInfoV2Size, 8);
                    path.Targets.Add(new NvPathTarget
                    {
                        DisplayId = unchecked((uint)Marshal.ReadInt32(tgts + t * TargetInfoV2Size, 0)),
                        RefreshRate1K = Marshal.ReadInt32(adv, 12),
                        Rotation = Marshal.ReadInt32(adv, 4),
                        TimingWidth = (ushort)Marshal.ReadInt16(adv, 32),
                        TimingHeight = (ushort)Marshal.ReadInt16(adv, 32 + 12),
                    });
                }
                result.Add(path);
            }
        }
        finally
        {
            foreach (var p in allocs) Marshal.FreeHGlobal(p);
        }
        return result;
    }

    // Returns each grid as its raw NV_MOSAIC_GRID_TOPO_V2 bytes (needed for an exact restore later).
    public static List<byte[]> DisplayGridsRaw()
    {
        var fn = Fn<DEnumGrids>(ID_Mosaic_EnumDisplayGrids);
        uint n = 0;
        Check(fn(IntPtr.Zero, ref n), "NvAPI_Mosaic_EnumDisplayGrids(count)");
        var grids = new List<byte[]>();
        if (n == 0) return grids;

        var buf = Marshal.AllocHGlobal((int)n * GridV2Size);
        try
        {
            ZeroFill(buf, (int)n * GridV2Size);
            for (int i = 0; i < n; i++) Marshal.WriteInt32(buf + i * GridV2Size, unchecked((int)GridV2Ver));
            Check(fn(buf, ref n), "NvAPI_Mosaic_EnumDisplayGrids");
            for (int i = 0; i < n; i++)
            {
                var raw = new byte[GridV2Size];
                Marshal.Copy(buf + i * GridV2Size, raw, 0, GridV2Size);
                grids.Add(raw);
            }
        }
        finally { Marshal.FreeHGlobal(buf); }
        return grids;
    }

    public static (int Rc, List<GridStatus> Status) ValidateGrids(List<byte[]> grids) => ValidateGrids(grids, SetTopoFlags);

    public static (int Rc, List<GridStatus> Status) ValidateGrids(List<byte[]> grids, uint flags)
    {
        var gBuf = Marshal.AllocHGlobal(grids.Count * GridV2Size);
        var sBuf = Marshal.AllocHGlobal(grids.Count * TopoStatusSize);
        try
        {
            for (int i = 0; i < grids.Count; i++) Marshal.Copy(grids[i], 0, gBuf + i * GridV2Size, GridV2Size);
            ZeroFill(sBuf, grids.Count * TopoStatusSize);
            for (int i = 0; i < grids.Count; i++) Marshal.WriteInt32(sBuf + i * TopoStatusSize, unchecked((int)TopoStatusVer));

            int rc = Fn<DValidateGrids>(ID_Mosaic_ValidateDisplayGrids)(flags, gBuf, sBuf, (uint)grids.Count);
            var result = new List<GridStatus>();
            for (int i = 0; i < grids.Count; i++)
            {
                var s = sBuf + i * TopoStatusSize;
                var st = new GridStatus
                {
                    ErrorFlags = unchecked((uint)Marshal.ReadInt32(s, 4)),
                    WarningFlags = unchecked((uint)Marshal.ReadInt32(s, 8)),
                };
                int dc = Math.Min(Marshal.ReadInt32(s, 12), 128);
                for (int d = 0; d < dc; d++)
                {
                    var e = s + 16 + d * 16;
                    st.Displays.Add((unchecked((uint)Marshal.ReadInt32(e, 0)),
                                     unchecked((uint)Marshal.ReadInt32(e, 4)),
                                     unchecked((uint)Marshal.ReadInt32(e, 8))));
                }
                result.Add(st);
            }
            return (rc, result);
        }
        finally
        {
            Marshal.FreeHGlobal(gBuf);
            Marshal.FreeHGlobal(sBuf);
        }
    }

    public static int SetGrids(List<byte[]> grids)
    {
        var gBuf = Marshal.AllocHGlobal(grids.Count * GridV2Size);
        try
        {
            for (int i = 0; i < grids.Count; i++) Marshal.Copy(grids[i], 0, gBuf + i * GridV2Size, GridV2Size);
            return Fn<DSetGrids>(ID_Mosaic_SetDisplayGrids)(gBuf, (uint)grids.Count, SetTopoFlags);
        }
        finally { Marshal.FreeHGlobal(gBuf); }
    }

    // Grid flag bits (NV_MOSAIC_GRID_TOPO_V2): 0 applyWithBezelCorrect, 1 immersiveGaming, 2 baseMosaic,
    // 3 driverReloadAllowed, 4 acceleratePrimaryDisplay, 5 pixelShift.
    // GeForce Surround is reported as immersiveGaming – that mode bit must be kept, otherwise the driver
    // looks for a Mosaic SLI topology and fails with NVAPI_NO_ACTIVE_SLI_TOPOLOGY (-113).
    private const uint GridModeBits = (1u << 1) | (1u << 2) | (1u << 5);

    // Copy of a grid prepared for SetDisplayGrids: version set, mode bits kept, bezel bit as requested,
    // driverReloadAllowed always cleared, optional frequency.
    public static byte[] PrepareGrid(byte[] raw, bool bezelCorrect, int? hz)
    {
        var g = (byte[])raw.Clone();
        BitConverter.GetBytes(GridV2Ver).CopyTo(g, 0);
        uint flags = (BitConverter.ToUInt32(raw, 16) & GridModeBits) | (bezelCorrect ? 1u : 0u);
        BitConverter.GetBytes(flags).CopyTo(g, 16);
        if (hz is int f) BitConverter.GetBytes(f).CopyTo(g, GridHeaderSize + MosaicMaxDisplays * GridDisplayV2Size + 16);
        return g;
    }

    // A 1x1 grid for one display taken out of an existing grid (same display entry and per-display mode, no overlap).
    public static byte[] SingleFromGrid(byte[] raw, int displayIndex)
    {
        var g = new byte[GridV2Size];
        BitConverter.GetBytes(GridV2Ver).CopyTo(g, 0);
        BitConverter.GetBytes(1).CopyTo(g, 4);
        BitConverter.GetBytes(1).CopyTo(g, 8);
        BitConverter.GetBytes(1).CopyTo(g, 12);
        Array.Copy(raw, GridHeaderSize + displayIndex * GridDisplayV2Size, g, GridHeaderSize, GridDisplayV2Size);
        BitConverter.GetBytes(0).CopyTo(g, GridHeaderSize + 8);   // overlapX
        BitConverter.GetBytes(0).CopyTo(g, GridHeaderSize + 12);  // overlapY
        int ds = GridHeaderSize + MosaicMaxDisplays * GridDisplayV2Size;
        Array.Copy(raw, ds, g, ds, DisplaySettingV1Size);
        return g;
    }

    public static NvGrid ParseGrid(byte[] raw)
    {
        int I(int off) => BitConverter.ToInt32(raw, off);
        var grid = new NvGrid
        {
            Rows = I(4),
            Columns = I(8),
            DisplayCount = I(12),
            Flags = unchecked((uint)I(16)),
        };
        int ds = GridHeaderSize + MosaicMaxDisplays * GridDisplayV2Size;
        grid.Width = I(ds + 4);
        grid.Height = I(ds + 8);
        grid.Bpp = I(ds + 12);
        grid.RefreshHz = I(ds + 16);
        for (int d = 0; d < grid.DisplayCount && d < MosaicMaxDisplays; d++)
        {
            int o = GridHeaderSize + d * GridDisplayV2Size;
            grid.Displays.Add(new NvGridDisplay
            {
                DisplayId = unchecked((uint)I(o + 4)),
                OverlapX = I(o + 8),
                OverlapY = I(o + 12),
                Rotation = I(o + 16),
            });
        }
        return grid;
    }

    private static void ZeroFill(IntPtr p, int len)
    {
        for (int i = 0; i < len; i++) Marshal.WriteByte(p, i, 0);
    }

    private static string ConnectorName(int t) => t switch
    {
        4 => "HDMI",
        5 => "DVI",
        7 => "DP",
        -1 => "unknown",
        _ => t.ToString(),
    };
}

public sealed class NvDisplay
{
    public uint DisplayId { get; set; }
    public string Connector { get; set; } = "";
    public bool IsDynamic { get; set; }
    public bool IsActive { get; set; }
    public bool IsOSVisible { get; set; }
    public bool IsConnected { get; set; }
    public bool IsPhysicallyConnected { get; set; }
    public byte[]? Edid { get; set; }

    // EDID bytes 8-9: big-endian PnP manufacturer ID (3 letters), bytes 10-11: little-endian product code.
    public string? EdidManufacturer => Edid is null ? null : DecodePnpId(Edid[8], Edid[9]);
    public ushort? EdidProduct => Edid is null ? null : (ushort)(Edid[10] | (Edid[11] << 8));
    public string? EdidName => Edid is null ? null : DescriptorText(Edid, 0xFC);
    // EDID bytes 12-15: little-endian numeric serial; descriptor 0xFF: serial as text (both optional per spec).
    public uint? EdidSerialNumber => Edid is null ? null : BitConverter.ToUInt32(Edid, 12);
    public string? EdidSerialText => Edid is null ? null : DescriptorText(Edid, 0xFF);

    private static string DecodePnpId(byte hi, byte lo)
    {
        int v = (hi << 8) | lo;
        char C(int shift) => (char)('A' - 1 + ((v >> shift) & 0x1F));
        return $"{C(10)}{C(5)}{C(0)}";
    }

    private static string? DescriptorText(byte[] edid, byte tag)
    {
        for (int o = 54; o <= 108; o += 18)
        {
            if (edid[o] == 0 && edid[o + 1] == 0 && edid[o + 3] == tag)
                return Encoding.ASCII.GetString(edid, o + 5, 13).Split('\n')[0].Trim();
        }
        return null;
    }
}

public sealed class NvGrid
{
    public int Rows { get; set; }
    public int Columns { get; set; }
    public int DisplayCount { get; set; }
    public uint Flags { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public int Bpp { get; set; }
    public int RefreshHz { get; set; }
    public List<NvGridDisplay> Displays { get; } = new();
}

public sealed class GridStatus
{
    public uint ErrorFlags { get; set; }
    public uint WarningFlags { get; set; }
    public List<(uint DisplayId, uint ErrorFlags, uint WarningFlags)> Displays { get; } = new();
}

public sealed class NvPath
{
    public int SourceWidth { get; set; }
    public int SourceHeight { get; set; }
    public int X { get; set; }
    public int Y { get; set; }
    public bool Primary { get; set; }
    public List<NvPathTarget> Targets { get; set; } = new();
}

public sealed class NvPathTarget
{
    public uint DisplayId { get; set; }
    public int RefreshRate1K { get; set; }
    public int Rotation { get; set; }
    public int TimingWidth { get; set; }
    public int TimingHeight { get; set; }
}

public sealed class NvGridDisplay
{
    public uint DisplayId { get; set; }
    public int OverlapX { get; set; }
    public int OverlapY { get; set; }
    public int Rotation { get; set; }
}
