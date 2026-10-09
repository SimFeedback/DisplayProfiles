// Copyright (c) 2026 Rausch IT
// SPDX-License-Identifier: MIT

namespace DisplayProfiles;

public sealed class Snapshot
{
    public DateTime CapturedAt { get; set; }
    public string Gpu { get; set; } = "";
    public bool HmdConnected { get; set; }
    public string? HmdDisplayId { get; set; }
    public List<DisplayInfo> NvDisplays { get; set; } = new();
    public List<GridInfo> SurroundGrids { get; set; } = new();
    public List<NvPath> NvPaths { get; set; } = new();
    public List<WinOutput> WindowsOutputs { get; set; } = new();
    public CcdRaw WindowsCcd { get; set; } = new();

    // Headset EDID identity, read from the MeganeX superlight 8K on 2026-10-08 (name "SVP-VG 3").
    private const string HmdEdidManufacturer = "SFL";
    private const ushort HmdEdidProduct = 0x0102;

    public static Snapshot Capture()
    {
        var gpus = NvApi.Gpus();
        if (gpus.Count == 0) throw new InvalidOperationException("No NVIDIA GPU found");
        var (gpu, gpuName) = gpus[0];

        var snap = new Snapshot { CapturedAt = DateTime.Now, Gpu = gpuName };

        foreach (var d in NvApi.AllDisplays(gpu))
        {
            bool isHmd = d.EdidManufacturer == HmdEdidManufacturer && d.EdidProduct == HmdEdidProduct;
            snap.NvDisplays.Add(new DisplayInfo
            {
                DisplayId = Hex(d.DisplayId),
                Connector = d.Connector,
                Connected = d.IsConnected,
                PhysicallyConnected = d.IsPhysicallyConnected,
                Active = d.IsActive,
                OsVisible = d.IsOSVisible,
                EdidManufacturer = d.EdidManufacturer,
                EdidProduct = d.EdidProduct is ushort p ? $"0x{p:X4}" : null,
                EdidName = d.EdidName,
                EdidSerialNumber = d.EdidSerialNumber,
                EdidSerialText = d.EdidSerialText,
                IsHmd = isHmd,
            });
            if (isHmd && d.IsConnected)
            {
                snap.HmdConnected = true;
                snap.HmdDisplayId = Hex(d.DisplayId);
            }
        }

        snap.NvPaths = NvApi.DisplayConfig();

        foreach (var raw in NvApi.DisplayGridsRaw())
        {
            var g = NvApi.ParseGrid(raw);
            var ids = g.Displays.Select(x => x.DisplayId).ToHashSet();
            var path = snap.NvPaths.FirstOrDefault(p => p.Targets.Any(t => ids.Contains(t.DisplayId)));
            var target = path?.Targets.FirstOrDefault(t => ids.Contains(t.DisplayId));

            // Bezel-corrected desktop = grid size plus the gap pixels (negative overlap) between displays.
            int plainW = g.Columns * g.Width, plainH = g.Rows * g.Height;
            int bezelW = plainW - g.Displays.Where((_, i) => i % Math.Max(g.Columns, 1) != 0).Sum(x => x.OverlapX);
            int bezelH = plainH - g.Displays.Where((_, i) => i >= g.Columns).Sum(x => x.OverlapY);
            bool hasGaps = bezelW != plainW || bezelH != plainH;

            snap.SurroundGrids.Add(new GridInfo
            {
                ActualRefreshHz = target is null ? null : target.RefreshRate1K / 1000.0,
                DesktopWidth = path?.SourceWidth,
                DesktopHeight = path?.SourceHeight,
                BezelCorrected = path is not null && hasGaps && path.SourceWidth == bezelW && path.SourceHeight == bezelH,
                Rows = g.Rows,
                Columns = g.Columns,
                BezelCorrect = (g.Flags & 1) != 0,
                RawFlags = g.Flags,
                Overlaps = g.Displays.Select(x => new[] { x.OverlapX, x.OverlapY }).ToList(),
                Width = g.Width,
                Height = g.Height,
                Bpp = g.Bpp,
                RefreshHz = g.RefreshHz,
                DisplayIds = g.Displays.Select(x => Hex(x.DisplayId)).ToList(),
                Raw = Convert.ToBase64String(raw),
            });
        }

        snap.WindowsOutputs = WinDisplay.ActiveOutputs().OrderBy(o => o.X).ThenBy(o => o.Y).ToList();
        foreach (var o in snap.WindowsOutputs)
            if (NvApi.DisplayIdByName(o.DeviceName) is uint id) o.NvDisplayId = Hex(id);
        var ccd = WinDisplay.QueryActiveRaw();
        snap.WindowsCcd = new CcdRaw
        {
            PathCount = ccd.PathCount,
            ModeCount = ccd.ModeCount,
            Paths = Convert.ToBase64String(ccd.Paths),
            Modes = Convert.ToBase64String(ccd.Modes),
        };
        return snap;
    }

    // Human-readable layout, one line per Windows desktop output from left to right (plus the headset).
    public static List<string> Describe(Snapshot s)
    {
        var lines = new List<string>();
        foreach (var o in s.WindowsOutputs)
        {
            var grid = o.NvDisplayId is null ? null
                : s.SurroundGrids.FirstOrDefault(g => g.DisplayIds.Count > 1 && g.DisplayIds.Contains(o.NvDisplayId));
            string what;
            if (grid is not null)
                what = $"Surround {grid.DisplayIds.Count} monitors{(grid.BezelCorrected ? " (Bezel)" : "")}";
            else
            {
                var d = s.NvDisplays.FirstOrDefault(x => x.DisplayId == o.NvDisplayId);
                what = d is null ? o.DeviceName : $"{d.EdidName ?? d.DisplayId} ({d.Connector})";
            }
            lines.Add($"{what} · {o.Width}x{o.Height} @ {o.RefreshHz} Hz{(o.Primary ? " · primary" : "")}");
        }
        if (s.HmdConnected) lines.Add("VR headset connected");
        return lines;
    }

    public static void Print(Snapshot s) => Print(s, Console.Out);

    public static void Print(Snapshot s, TextWriter w)
    {
        w.WriteLine($"GPU: {s.Gpu}");
        w.WriteLine($"VR headset: {(s.HmdConnected ? $"CONNECTED ({s.HmdDisplayId})" : "not detected")}");

        w.WriteLine("\nNVIDIA outputs:");
        foreach (var d in s.NvDisplays)
        {
            var edid = d.EdidManufacturer is null ? "" : $"  EDID {d.EdidManufacturer} {d.EdidProduct} {d.EdidName}  SN {Serial(d)}";
            w.WriteLine($"  {d.DisplayId} {d.Connector,-5} connected={B(d.Connected)} active={B(d.Active)}{edid}{(d.IsHmd ? "  <= HMD" : "")}");
        }

        w.WriteLine("\nSurround grids (displays left to right):");
        foreach (var g in s.SurroundGrids)
        {
            var hz = g.ActualRefreshHz is double a ? $"{a:0.###} Hz" : "? Hz";
            var desk = g.DesktopWidth is null ? "" : $"  Desktop {g.DesktopWidth}x{g.DesktopHeight}";
            var bezel = g.DisplayIds.Count > 1 ? $"  bezel corrected={(g.BezelCorrected ? "yes" : "no")}" : "";
            w.WriteLine($"  {g.Rows}x{g.Columns}  each {g.Width}x{g.Height} @ {hz}{desk}{bezel}  [{string.Join(", ", g.DisplayIds.Select(Label))}]");
            if (g.DisplayIds.Count > 1)
                for (int i = 0; i < g.DisplayIds.Count && i < g.Overlaps.Count; i++)
                    w.WriteLine($"      {g.DisplayIds[i]}  overlapX={g.Overlaps[i][0]} overlapY={g.Overlaps[i][1]}  (negative = gap/bezel)");
        }

        w.WriteLine("\nWindows desktop (left to right):");
        int n = 1;
        foreach (var o in s.WindowsOutputs)
            w.WriteLine($"  {n++}. {o.DeviceName} {o.Width}x{o.Height} @ {o.RefreshHz} Hz pos=({o.X},{o.Y}){(o.Primary ? " primary" : "")}  -> {(o.NvDisplayId is null ? "?" : Label(o.NvDisplayId))}");

        string Label(string id)
        {
            var d = s.NvDisplays.FirstOrDefault(x => x.DisplayId == id);
            return d?.EdidName is null ? id : $"{id} {d.EdidName} SN {Serial(d)}";
        }
    }

    private static string Serial(DisplayInfo d)
    {
        if (!string.IsNullOrEmpty(d.EdidSerialText)) return d.EdidSerialText!;
        return d.EdidSerialNumber is uint sn && sn != 0 ? sn.ToString() : "-";
    }

    private static string Hex(uint v) => $"0x{v:X8}";
    private static string B(bool b) => b ? "1" : "0";
}

public sealed class DisplayInfo
{
    public string DisplayId { get; set; } = "";
    public string Connector { get; set; } = "";
    public bool Connected { get; set; }
    public bool PhysicallyConnected { get; set; }
    public bool Active { get; set; }
    public bool OsVisible { get; set; }
    public string? EdidManufacturer { get; set; }
    public string? EdidProduct { get; set; }
    public string? EdidName { get; set; }
    public uint? EdidSerialNumber { get; set; }
    public string? EdidSerialText { get; set; }
    public bool IsHmd { get; set; }
}

public sealed class GridInfo
{
    public int Rows { get; set; }
    public int Columns { get; set; }
    public double? ActualRefreshHz { get; set; }   // from NvAPI_DISP_GetDisplayConfig (what the driver really drives)
    public int? DesktopWidth { get; set; }
    public int? DesktopHeight { get; set; }
    public bool BezelCorrected { get; set; }       // derived: desktop size == grid size + gap pixels
    public bool BezelCorrect { get; set; }         // raw applyWithBezelCorrect bit as returned by EnumDisplayGrids (unreliable on read)
    public uint RawFlags { get; set; }             // raw grid flag bits (bit 1 = immersiveGaming = GeForce Surround)
    public List<int[]> Overlaps { get; set; } = new();
    public int Width { get; set; }
    public int Height { get; set; }
    public int Bpp { get; set; }
    public int RefreshHz { get; set; }             // raw grid frequency from EnumDisplayGrids (NOT the current rate)
    public List<string> DisplayIds { get; set; } = new();
    public string Raw { get; set; } = "";
}

public sealed class CcdRaw
{
    public uint PathCount { get; set; }
    public uint ModeCount { get; set; }
    public string Paths { get; set; } = "";
    public string Modes { get; set; } = "";
}
