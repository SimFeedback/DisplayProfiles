// Copyright (c) 2026 Rausch IT
// SPDX-License-Identifier: MIT

using System.Text.Json;

namespace DisplayProfiles;

// Apply a profile [check only]
//   1. monitors of the profile must be connected (same output, same serial)
//   2. NVIDIA Surround grids: validate, then SetDisplayGrids (CURRENT_GPU_TOPOLOGY | NO_DRIVER_RELOAD)
//      – skipped when the Surround layout already matches the profile
//   3. Windows layout + refresh rate via SetDisplayConfig
//   4. read back and compare with the profile (re-read once if it deviates: the DP link may still be settling)
public static class Apply
{
    // Result codes, identical to the CLI exit codes.
    public const int Ok = 0;
    public const int MonitorsMissing = 5;
    public const int ValidationFailed = 6;
    public const int SurroundFailed = 7;
    public const int Deviation = 8;

    private const int ErrorGenFailure = 31;   // SetDisplayConfig: typically a powered-off HDMI monitor that still reports "connected"

    public static ApplyResult Run(string name, Snapshot profile, Snapshot current, bool checkOnly, string profileDir, JsonSerializerOptions json, TextWriter log)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        void Took(string step) { log.WriteLine($"  ({step}: {sw.Elapsed.TotalSeconds:0.0} s)"); sw.Restart(); }

        // 1. Monitors present?
        var problems = new List<string>();
        var wanted = profile.SurroundGrids.SelectMany(g => g.DisplayIds)
            .Concat(profile.WindowsOutputs.Select(o => o.NvDisplayId).OfType<string>())
            .Distinct();
        foreach (var id in wanted)
        {
            var want = profile.NvDisplays.FirstOrDefault(d => d.DisplayId == id);
            var have = current.NvDisplays.FirstOrDefault(d => d.DisplayId == id);
            if (have is null || !have.Connected)
                problems.Add($"{id} ({want?.EdidName} SN {want?.EdidSerialText}) is not connected");
            else if (want?.EdidSerialText is not null && have.EdidSerialText != want.EdidSerialText)
                problems.Add($"{id}: expected SN {want.EdidSerialText}, connected is SN {have.EdidSerialText}");
        }
        if (problems.Count > 0)
        {
            log.WriteLine("Aborted – the connected monitors do not match the profile:");
            problems.ForEach(p => log.WriteLine("  " + p));
            return new ApplyResult(MonitorsMissing, "Connected monitors do not match the profile", problems);
        }

        // 2. Surround grids
        var gridsToSet = BuildGrids(profile, current, out bool surroundChange);
        if (surroundChange)
        {
            var (rc, status) = NvApi.ValidateGrids(gridsToSet);
            bool ok = rc == 0;
            for (int i = 0; i < status.Count; i++)
            {
                var s = status[i];
                uint warn = s.WarningFlags | s.Displays.Aggregate(0u, (a, d) => a | d.WarningFlags);
                uint err = s.ErrorFlags | s.Displays.Aggregate(0u, (a, d) => a | d.ErrorFlags);
                if (err != 0) ok = false;
                if ((warn & NvApi.WarningDriverReloadRequired) != 0)
                {
                    ok = false;
                    log.WriteLine($"  Grid {i + 1}: would require a driver reload – not done.");
                }
                if (err != 0 || warn != 0)
                    log.WriteLine($"  Grid {i + 1}: errors=0x{err:X} warnings=0x{warn:X}");
            }
            log.WriteLine($"NVIDIA Surround validation: {(ok ? "OK" : $"FAILED (status {rc})")}");
            Took("NVIDIA ValidateDisplayGrids");
            if (!ok) return new ApplyResult(ValidationFailed, "NVIDIA rejected the Surround grid");
        }
        else
        {
            log.WriteLine("Surround layout already matches the profile – no NVIDIA change needed.");
            int vrc = WinDisplay.ApplyCcd(profile.WindowsCcd, validateOnly: true);
            log.WriteLine($"Windows validation: {(vrc == 0 ? "OK" : $"FAILED {vrc}")}");
            if (vrc != 0) return new ApplyResult(ValidationFailed, $"Windows rejected the layout (error {vrc})");
        }

        if (checkOnly)
        {
            if (surroundChange) log.WriteLine("The Windows layout can only be validated after the Surround change.");
            log.WriteLine("Check only, nothing changed.");
            return new ApplyResult(Ok, "Check OK, nothing changed");
        }

        var backupDir = Path.Combine(profileDir, ProfileStore.BackupDir);
        Directory.CreateDirectory(backupDir);
        var backup = Path.Combine(backupDir, $"before-{name}-{DateTime.Now:yyyyMMdd-HHmmss}.json");
        File.WriteAllText(backup, JsonSerializer.Serialize(current, json));
        log.WriteLine($"Backup of the current state: {backup}");

        sw.Restart();
        if (surroundChange)
        {
            int rc = NvApi.SetGrids(gridsToSet);
            log.WriteLine($"Set Surround: {(rc == 0 ? "OK" : $"FAILED (status {rc})")}");
            Took("NVIDIA SetDisplayGrids");
            if (rc != 0) return new ApplyResult(SurroundFailed, $"Setting Surround failed (status {rc})");
            Thread.Sleep(3000);
            sw.Restart();
        }

        // 3. Windows layout + refresh rate
        int crc = WinDisplay.ApplyCcd(profile.WindowsCcd, validateOnly: true);
        Took("Windows validation");
        if (crc == 0) crc = WinDisplay.ApplyCcd(profile.WindowsCcd, validateOnly: false);
        log.WriteLine($"Set Windows layout/refresh rate: {(crc == 0 ? "OK" : $"FAILED {crc}")}");
        if (crc == ErrorGenFailure)
            log.WriteLine("  Hint: error 31 occurs when a monitor of the profile is switched off (HDMI still reports 'connected').");
        Took("Windows SetDisplayConfig");
        Thread.Sleep(2000);
        sw.Restart();

        // 4. Read back and compare; one re-read after a short wait, because the DP link may still be settling.
        var after = Snapshot.Capture();
        var diffs = Compare(profile, after);
        if (diffs.Count > 0)
        {
            log.WriteLine("Deviation on first read-back – reading again in 2 s.");
            Thread.Sleep(2000);
            after = Snapshot.Capture();
            diffs = Compare(profile, after);
        }
        Took("Read back state");
        log.WriteLine();
        Snapshot.Print(after, log);
        log.WriteLine();
        if (diffs.Count == 0) log.WriteLine("RESULT: state matches the profile.");
        else
        {
            log.WriteLine("RESULT: deviations from the profile:");
            diffs.ForEach(d => log.WriteLine("  " + d));
        }

        if (crc == ErrorGenFailure)
            return new ApplyResult(Deviation, "Windows layout not set (error 31) – is a monitor switched off?", diffs);
        if (crc != 0)
            return new ApplyResult(Deviation, $"Windows layout not set (error {crc})", diffs);
        return diffs.Count == 0
            ? new ApplyResult(Ok, "Profile active")
            : new ApplyResult(Deviation, "State deviates from the profile", diffs);
    }

    private static List<byte[]> BuildGrids(Snapshot profile, Snapshot current, out bool surroundChange)
    {
        var profileMulti = profile.SurroundGrids.Where(g => g.DisplayIds.Count > 1).ToList();
        var currentMulti = current.SurroundGrids.Where(g => g.DisplayIds.Count > 1).ToList();

        surroundChange = !SameLayout(profileMulti, currentMulti);
        if (!surroundChange) return new();

        var grids = new List<byte[]>();
        foreach (var g in profile.SurroundGrids)
        {
            int? hz = g.ActualRefreshHz is double a ? (int)Math.Round(a) : null;
            grids.Add(NvApi.PrepareGrid(Convert.FromBase64String(g.Raw), g.BezelCorrected, hz));
        }

        // Displays of an active Surround that the profile does not use: split them out as single displays.
        var used = profile.SurroundGrids.SelectMany(g => g.DisplayIds).ToHashSet();
        foreach (var g in currentMulti)
        {
            var raw = Convert.FromBase64String(g.Raw);
            for (int i = 0; i < g.DisplayIds.Count; i++)
                if (!used.Contains(g.DisplayIds[i]))
                    grids.Add(NvApi.SingleFromGrid(raw, i));
        }
        return grids;
    }

    private static bool SameLayout(List<GridInfo> a, List<GridInfo> b)
    {
        if (a.Count != b.Count) return false;
        return a.All(x => b.Any(y =>
            x.DisplayIds.SequenceEqual(y.DisplayIds)
            && x.Overlaps.Count == y.Overlaps.Count
            && x.Overlaps.Zip(y.Overlaps).All(p => p.First[0] == p.Second[0] && p.First[1] == p.Second[1])));
    }

    // Differences between a profile and a captured state; empty = the state matches the profile. The HMD is ignored.
    public static List<string> Compare(Snapshot profile, Snapshot now)
    {
        var diffs = new List<string>();

        var wantActive = profile.NvDisplays.Where(d => d.Active).Select(d => d.DisplayId).Where(id => !IsHmd(profile, id)).ToHashSet();
        var haveActive = now.NvDisplays.Where(d => d.Active).Select(d => d.DisplayId).Where(id => !IsHmd(now, id)).ToHashSet();
        foreach (var id in wantActive.Except(haveActive)) diffs.Add($"{id} should be active but is not");
        foreach (var id in haveActive.Except(wantActive)) diffs.Add($"{id} is active but should not be");

        foreach (var g in profile.SurroundGrids.Where(g => g.DisplayIds.Count > 1))
        {
            var match = now.SurroundGrids.FirstOrDefault(x => x.DisplayIds.SequenceEqual(g.DisplayIds));
            if (match is null) { diffs.Add($"Surround grid [{string.Join(", ", g.DisplayIds)}] is missing"); continue; }
            if (g.BezelCorrected != match.BezelCorrected) diffs.Add($"Bezel correction: expected {g.BezelCorrected}, is {match.BezelCorrected}");
            if (g.DesktopWidth is int w && match.DesktopWidth != w) diffs.Add($"Desktop width: expected {w}, is {match.DesktopWidth}");
            if (g.ActualRefreshHz is double hz && (match.ActualRefreshHz is not double h || Math.Abs(h - hz) > 0.5))
                diffs.Add($"Refresh rate: expected {hz:0.###} Hz, is {match.ActualRefreshHz:0.###} Hz");
        }
        if (now.SurroundGrids.Any(g => g.DisplayIds.Count > 1) && !profile.SurroundGrids.Any(g => g.DisplayIds.Count > 1))
            diffs.Add("Surround is still active but the profile has none");

        foreach (var o in profile.WindowsOutputs)
        {
            var m = now.WindowsOutputs.FirstOrDefault(x => x.NvDisplayId == o.NvDisplayId);
            if (m is null) { diffs.Add($"Windows: {o.NvDisplayId} is missing"); continue; }
            if (m.Width != o.Width || m.Height != o.Height) diffs.Add($"Windows {o.NvDisplayId}: {m.Width}x{m.Height} instead of {o.Width}x{o.Height}");
            if (m.RefreshHz != o.RefreshHz) diffs.Add($"Windows {o.NvDisplayId}: {m.RefreshHz} Hz instead of {o.RefreshHz} Hz");
            if (m.X != o.X || m.Y != o.Y) diffs.Add($"Windows {o.NvDisplayId}: position ({m.X},{m.Y}) instead of ({o.X},{o.Y})");
            if (m.Primary != o.Primary) diffs.Add($"Windows {o.NvDisplayId}: primary={m.Primary} instead of {o.Primary}");
        }
        return diffs;
    }

    private static bool IsHmd(Snapshot s, string id) => s.NvDisplays.Any(d => d.DisplayId == id && d.IsHmd);
}

public sealed record ApplyResult(int Code, string Message, List<string>? Details = null)
{
    public bool Success => Code == Apply.Ok;
}
