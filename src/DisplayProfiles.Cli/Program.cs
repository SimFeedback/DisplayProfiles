// Copyright (c) 2026 Rausch IT
// SPDX-License-Identifier: MIT

using DisplayProfiles;

// DisplayProfiles (console test tool; the tray app DisplayProfiles.exe is the regular user interface)
//   status                 show NVIDIA displays, Surround grids, Windows outputs, HMD detection
//   save <name>            store the current configuration as profiles\<name>.json
//   apply <name> [--check] restore a saved profile incl. NVIDIA Surround (see Apply.cs);
//                          --check only validates, without changing anything
//   probe <name>           diagnostics: validate the profile's Surround grid in several variants

Console.OutputEncoding = System.Text.Encoding.UTF8;
var cmd = args.Length > 0 ? args[0].ToLowerInvariant() : "status";
var store = new ProfileStore();

try
{
    var startup = System.Diagnostics.Stopwatch.StartNew();
    NvApi.Init();
    var snapshot = Snapshot.Capture();
    if (cmd == "apply") Console.WriteLine($"  (startup + read state: {startup.Elapsed.TotalSeconds:0.0} s)");

    switch (cmd)
    {
        case "status":
            Snapshot.Print(snapshot);
            break;

        case "save":
        {
            if (args.Length < 2) { Console.Error.WriteLine("usage: DisplayProfiles.Cli save <name>"); return 2; }
            if (store.Exists(args[1])) { Console.Error.WriteLine($"Profile already exists: {args[1]}"); return 3; }
            store.Save(args[1], snapshot, overwrite: false);
            Snapshot.Print(snapshot);
            Console.WriteLine($"\nSaved: {Path.Combine(store.Dir, args[1] + ".json")}");
            break;
        }

        case "apply":
        {
            if (args.Length < 2) { Console.Error.WriteLine("usage: DisplayProfiles.Cli apply <name> [--check]"); return 2; }
            bool checkOnly = args.Skip(2).Any(a => a.Equals("--check", StringComparison.OrdinalIgnoreCase));
            if (!store.Exists(args[1])) { Console.Error.WriteLine($"Profile not found: {args[1]}"); return 3; }
            var profile = store.Load(args[1]);
            return Apply.Run(args[1], profile, snapshot, checkOnly, store.Dir, ProfileStore.Json, Console.Out).Code;
        }

        case "probe":
        {
            // Diagnostics: validate the profile's Surround grid in several variants. Validation only, never sets anything.
            if (args.Length < 2) { Console.Error.WriteLine("usage: DisplayProfiles.Cli probe <name>"); return 2; }
            var profile = store.Load(args[1]);
            var g = profile.SurroundGrids.First(x => x.DisplayIds.Count > 1);
            var raw = Convert.FromBase64String(g.Raw);
            int rawHz = g.RefreshHz;
            int hz = g.ActualRefreshHz is double a ? (int)Math.Round(a) : rawHz;

            var variants = new (string Name, byte[] Grid, uint Flags)[]
            {
                ("Bezel off, raw Hz, Flags keep+noReload", NvApi.PrepareGrid(raw, false, null), NvApi.SetTopoFlags),
                ($"Bezel on, {rawHz} Hz, Flags keep+noReload", NvApi.PrepareGrid(raw, true, null), NvApi.SetTopoFlags),
                ($"Bezel off, {hz} Hz, Flags keep+noReload", NvApi.PrepareGrid(raw, false, hz), NvApi.SetTopoFlags),
                ($"Bezel on, {hz} Hz, Flags keep+noReload", NvApi.PrepareGrid(raw, true, hz), NvApi.SetTopoFlags),
                ($"Bezel on, {hz} Hz, flag noReload only", NvApi.PrepareGrid(raw, true, hz), 1u << 1),
                ($"Bezel on, {rawHz} Hz, flag noReload only", NvApi.PrepareGrid(raw, true, null), 1u << 1),
                ("Bezel off, raw Hz, flag noReload only", NvApi.PrepareGrid(raw, false, null), 1u << 1),
            };
            foreach (var v in variants)
            {
                var (rc, st) = NvApi.ValidateGrids(new List<byte[]> { v.Grid }, v.Flags);
                var s = st.FirstOrDefault();
                uint err = s is null ? 0 : s.ErrorFlags | s.Displays.Aggregate(0u, (acc, d) => acc | d.ErrorFlags);
                uint warn = s is null ? 0 : s.WarningFlags | s.Displays.Aggregate(0u, (acc, d) => acc | d.WarningFlags);
                Console.WriteLine($"{v.Name,-45} rc={rc,5}  errors=0x{err:X}  warnings=0x{warn:X}");
            }
            break;
        }

        default:
            Console.Error.WriteLine("usage: DisplayProfiles.Cli [status | save <name> | apply <name> [--check] | probe <name>]");
            return 2;
    }
    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine("Error: " + ex.Message);
    return 1;
}
