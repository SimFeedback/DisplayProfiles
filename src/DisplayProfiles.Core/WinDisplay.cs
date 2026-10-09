// Copyright (c) 2026 Rausch IT
// SPDX-License-Identifier: MIT

using System.Runtime.InteropServices;

namespace DisplayProfiles;

// Windows display configuration (CCD API + EnumDisplaySettings).
// The only write path is ApplyCcd (SetDisplayConfig), the same API the Windows display settings use.
public static class WinDisplay
{
    private const uint QDC_ALL_PATHS = 0x1;
    private const uint QDC_ONLY_ACTIVE_PATHS = 0x2;

    private const uint SDC_USE_SUPPLIED_DISPLAY_CONFIG = 0x20;
    private const uint SDC_VALIDATE = 0x40;
    private const uint SDC_APPLY = 0x80;
    private const uint SDC_SAVE_TO_DATABASE = 0x200;
    private const uint SDC_ALLOW_CHANGES = 0x400;

    // Offsets inside DISPLAYCONFIG_PATH_INFO / DISPLAYCONFIG_MODE_INFO.
    private const int PathSourceLuid = 0;
    private const int PathTargetLuid = 20;
    private const int PathTargetId = 28;
    private const int ModeLuid = 8;
    private const int PathInfoSize = 72;  // DISPLAYCONFIG_PATH_INFO
    private const int ModeInfoSize = 64;  // DISPLAYCONFIG_MODE_INFO

    [DllImport("user32.dll")]
    private static extern int GetDisplayConfigBufferSizes(uint flags, out uint numPaths, out uint numModes);

    [DllImport("user32.dll")]
    private static extern int QueryDisplayConfig(uint flags, ref uint numPaths, IntPtr paths, ref uint numModes, IntPtr modes, IntPtr topologyId);

    [DllImport("user32.dll")]
    private static extern int SetDisplayConfig(uint numPaths, byte[] paths, uint numModes, byte[] modes, uint flags);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DISPLAY_DEVICE
    {
        public int cb;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
        public int StateFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceID;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DEVMODE
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
        public short dmSpecVersion, dmDriverVersion, dmSize, dmDriverExtra;
        public int dmFields;
        public int dmPositionX, dmPositionY;
        public int dmDisplayOrientation, dmDisplayFixedOutput;
        public short dmColor, dmDuplex, dmYResolution, dmTTOption, dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
        public short dmLogPixels;
        public int dmBitsPerPel, dmPelsWidth, dmPelsHeight, dmDisplayFlags, dmDisplayFrequency;
        public int dmICMMethod, dmICMIntent, dmMediaType, dmDitherType, dmReserved1, dmReserved2, dmPanningWidth, dmPanningHeight;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool EnumDisplayDevices(string? lpDevice, uint iDevNum, ref DISPLAY_DEVICE lpDisplayDevice, uint dwFlags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool EnumDisplaySettings(string deviceName, int modeNum, ref DEVMODE devMode);

    // Raw active CCD paths and modes, needed later for SetDisplayConfig.
    public static (byte[] Paths, byte[] Modes, uint PathCount, uint ModeCount) QueryActiveRaw() => QueryRaw(QDC_ONLY_ACTIVE_PATHS);

    private static (byte[] Paths, byte[] Modes, uint PathCount, uint ModeCount) QueryRaw(uint flags)
    {
        int rc = GetDisplayConfigBufferSizes(flags, out var np, out var nm);
        if (rc != 0) throw new InvalidOperationException($"GetDisplayConfigBufferSizes failed ({rc})");

        var pBuf = Marshal.AllocHGlobal((int)np * PathInfoSize);
        var mBuf = Marshal.AllocHGlobal((int)nm * ModeInfoSize);
        try
        {
            rc = QueryDisplayConfig(flags, ref np, pBuf, ref nm, mBuf, IntPtr.Zero);
            if (rc != 0) throw new InvalidOperationException($"QueryDisplayConfig failed ({rc})");
            var paths = new byte[np * PathInfoSize];
            var modes = new byte[nm * ModeInfoSize];
            Marshal.Copy(pBuf, paths, 0, paths.Length);
            Marshal.Copy(mBuf, modes, 0, modes.Length);
            return (paths, modes, np, nm);
        }
        finally
        {
            Marshal.FreeHGlobal(pBuf);
            Marshal.FreeHGlobal(mBuf);
        }
    }

    // Applies a saved CCD configuration. validateOnly = true asks Windows whether it would work, without changing anything.
    // Adapter LUIDs change on every boot, so saved LUIDs are remapped to the current ones via the target IDs.
    // Returns the Win32 error code (0 = OK).
    public static int ApplyCcd(CcdRaw saved, bool validateOnly)
    {
        var paths = Convert.FromBase64String(saved.Paths);
        var modes = Convert.FromBase64String(saved.Modes);
        RemapLuids(paths, modes, saved.PathCount, saved.ModeCount);

        uint flags = SDC_USE_SUPPLIED_DISPLAY_CONFIG | (validateOnly
            ? SDC_VALIDATE
            : SDC_APPLY | SDC_SAVE_TO_DATABASE | SDC_ALLOW_CHANGES);
        return SetDisplayConfig(saved.PathCount, paths, saved.ModeCount, modes, flags);
    }

    private static void RemapLuids(byte[] paths, byte[] modes, uint pathCount, uint modeCount)
    {
        var current = QueryRaw(QDC_ALL_PATHS);
        var map = new Dictionary<long, long>();

        for (int i = 0; i < pathCount; i++)
        {
            int o = i * PathInfoSize;
            long oldLuid = BitConverter.ToInt64(paths, o + PathTargetLuid);
            uint targetId = BitConverter.ToUInt32(paths, o + PathTargetId);

            var candidates = new HashSet<long>();
            for (int j = 0; j < current.PathCount; j++)
            {
                int c = j * PathInfoSize;
                if (BitConverter.ToUInt32(current.Paths, c + PathTargetId) == targetId)
                    candidates.Add(BitConverter.ToInt64(current.Paths, c + PathTargetLuid));
            }
            if (candidates.Count != 1)
                throw new InvalidOperationException($"Output (target 0x{targetId:X}) cannot be mapped unambiguously ({candidates.Count} matches) – aborted.");

            long newLuid = candidates.First();
            if (map.TryGetValue(oldLuid, out var existing) && existing != newLuid)
                throw new InvalidOperationException("Contradictory adapter mapping – aborted.");
            map[oldLuid] = newLuid;
        }

        void Patch(byte[] buf, int offset)
        {
            long v = BitConverter.ToInt64(buf, offset);
            if (map.TryGetValue(v, out var nv)) BitConverter.GetBytes(nv).CopyTo(buf, offset);
        }
        for (int i = 0; i < pathCount; i++)
        {
            Patch(paths, i * PathInfoSize + PathSourceLuid);
            Patch(paths, i * PathInfoSize + PathTargetLuid);
        }
        for (int i = 0; i < modeCount; i++)
            Patch(modes, i * ModeInfoSize + ModeLuid);
    }

    public static List<WinOutput> ActiveOutputs()
    {
        var list = new List<WinOutput>();
        for (uint i = 0; i < 64; i++)
        {
            var a = new DISPLAY_DEVICE { cb = Marshal.SizeOf<DISPLAY_DEVICE>() };
            if (!EnumDisplayDevices(null, i, ref a, 0)) break;
            if ((a.StateFlags & 1) == 0) continue; // not attached to desktop

            var m = new DEVMODE { dmSize = (short)Marshal.SizeOf<DEVMODE>() };
            if (!EnumDisplaySettings(a.DeviceName, -1, ref m)) continue;

            var mon = new DISPLAY_DEVICE { cb = Marshal.SizeOf<DISPLAY_DEVICE>() };
            string monitorId = EnumDisplayDevices(a.DeviceName, 0, ref mon, 0) ? mon.DeviceID : "";

            list.Add(new WinOutput
            {
                DeviceName = a.DeviceName,
                Adapter = a.DeviceString,
                MonitorId = monitorId,
                Primary = (a.StateFlags & 4) != 0,
                Width = m.dmPelsWidth,
                Height = m.dmPelsHeight,
                RefreshHz = m.dmDisplayFrequency,
                X = m.dmPositionX,
                Y = m.dmPositionY,
            });
        }
        return list;
    }
}

public sealed class WinOutput
{
    public string DeviceName { get; set; } = "";
    public string Adapter { get; set; } = "";
    public string MonitorId { get; set; } = "";
    public bool Primary { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public int RefreshHz { get; set; }
    public int X { get; set; }
    public int Y { get; set; }
    public string? NvDisplayId { get; set; }
}
