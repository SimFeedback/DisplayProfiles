// Copyright (c) 2026 Rausch IT
// SPDX-License-Identifier: MIT

namespace DisplayProfiles;

// Thread-safe facade over NVAPI/CCD for long-running hosts (tray service, a later SimFeedback plugin).
// All driver access is serialized; while a switch runs, status calls return the last known state with Busy = true.
public sealed class DisplayService
{
    private readonly SemaphoreSlim _lock = new(1, 1);
    private Snapshot? _current;
    private string? _operation;
    private OperationResult? _lastResult;

    public ProfileStore Store { get; }

    // Raised after anything that may change the display state or the profile list (any thread).
    public event Action? Changed;

    public DisplayService(ProfileStore store)
    {
        Store = store;
        NvApi.Init();
    }

    public StatusDto GetStatus(bool refresh)
    {
        if (refresh || _current is null) TryCapture();
        var s = _current;
        return new StatusDto(
            s?.CapturedAt,
            s?.HmdConnected ?? false,
            s?.HmdDisplayId,
            s is null ? new() : Snapshot.Describe(s),
            ActiveProfiles(s),
            _operation is not null,
            _operation,
            _lastResult);
    }

    // Fresh read unless a switch is running. A concurrent plain read (~0.1 s) is waited for instead of skipped,
    // so two clients starting at the same time both get a state.
    private void TryCapture()
    {
        if (!_lock.Wait(_operation is null ? 3000 : 0)) return;
        try { _current = Snapshot.Capture(); }
        finally { _lock.Release(); }
    }

    public List<ProfileDto> ListProfiles()
    {
        if (_current is null) TryCapture();
        var active = ActiveProfiles(_current).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var list = new List<ProfileDto>();
        foreach (var name in Store.Names())
        {
            try
            {
                var p = Store.Load(name);
                list.Add(new ProfileDto(name, Snapshot.Describe(p), p.CapturedAt, active.Contains(name), null));
            }
            catch (Exception ex)
            {
                list.Add(new ProfileDto(name, new(), null, false, ex.Message));
            }
        }
        return list;
    }

    // Create = capture the current state (twice, must be stable) and store it under a new name.
    public ProfileDto Create(string name, bool overwrite) => Exclusive($"Create profile '{name}'", () =>
    {
        if (ProfileStore.CheckName(name) is string err) throw new ProfileException(400, err);
        if (!overwrite && Store.Exists(name)) throw new ProfileException(409, $"Profile '{name}' already exists");
        var snap = CaptureStable();
        Store.Save(name, snap, overwrite);
        return new ProfileDto(name, Snapshot.Describe(snap), snap.CapturedAt, true, null);
    });

    // Re-capture = overwrite an existing profile with the current state (old version goes to _backup).
    public ProfileDto Recapture(string name) => Exclusive($"Re-capture profile '{name}'", () =>
    {
        if (!Store.Exists(name)) throw new ProfileException(404, $"Profile '{name}' not found");
        var snap = CaptureStable();
        Store.Save(name, snap, overwrite: true);
        return new ProfileDto(name, Snapshot.Describe(snap), snap.CapturedAt, true, null);
    });

    public void Rename(string name, string newName)
    {
        Store.Rename(name, newName);
        Changed?.Invoke();
    }

    public string Archive(string name)
    {
        var target = Store.Archive(name);
        Changed?.Invoke();
        return target;
    }

    public OperationResult Apply(string name, bool checkOnly)
    {
        var profile = Store.Load(name);
        return Exclusive(checkOnly ? $"Check profile '{name}'" : $"Switch to '{name}'", () =>
        {
            var log = new StringWriter();
            var started = DateTime.Now;
            ApplyResult r;
            try
            {
                var current = Snapshot.Capture();
                r = DisplayProfiles.Apply.Run(name, profile, current, checkOnly, Store.Dir, ProfileStore.Json, log);
            }
            catch (Exception ex)
            {
                log.WriteLine("Error: " + ex.Message);
                r = new ApplyResult(1, ex.Message);
            }
            var result = new OperationResult(name, checkOnly, r.Code, r.Success, r.Message, r.Details ?? new(), log.ToString(), started, DateTime.Now);
            if (!checkOnly) _lastResult = result;
            return result;
        });
    }

    private T Exclusive<T>(string operation, Func<T> work)
    {
        if (!_lock.Wait(0)) throw new ProfileException(409, $"Please wait – already running: {_operation}");
        _operation = operation;
        Changed?.Invoke();
        try
        {
            var result = work();
            try { _current = Snapshot.Capture(); }
            catch { /* status keeps the previous state; the next refresh retries */ }
            return result;
        }
        finally
        {
            _operation = null;
            _lock.Release();
            Changed?.Invoke();
        }
    }

    // Two reads 1.5 s apart must agree, otherwise the display state is still settling (e.g. right after a switch).
    private static Snapshot CaptureStable()
    {
        var a = Snapshot.Capture();
        Thread.Sleep(1500);
        var b = Snapshot.Capture();
        if (DisplayProfiles.Apply.Compare(a, b).Count > 0 || DisplayProfiles.Apply.Compare(b, a).Count > 0)
            throw new ProfileException(409, "The display state is still changing – wait a few seconds and try again");
        return b;
    }

    private List<string> ActiveProfiles(Snapshot? now)
    {
        var result = new List<string>();
        if (now is null) return result;
        foreach (var name in Store.Names())
        {
            try
            {
                if (DisplayProfiles.Apply.Compare(Store.Load(name), now).Count == 0) result.Add(name);
            }
            catch
            {
                // unreadable profile: shown with an error in ListProfiles
            }
        }
        return result;
    }
}

public sealed record StatusDto(
    DateTime? CapturedAt,
    bool HmdConnected,
    string? HmdDisplayId,
    List<string> Layout,
    List<string> ActiveProfiles,
    bool Busy,
    string? Operation,
    OperationResult? LastResult);

public sealed record ProfileDto(string Name, List<string> Layout, DateTime? CapturedAt, bool Active, string? Error);

public sealed record OperationResult(
    string Profile,
    bool CheckOnly,
    int Code,
    bool Success,
    string Message,
    List<string> Details,
    string Log,
    DateTime Started,
    DateTime Finished);
