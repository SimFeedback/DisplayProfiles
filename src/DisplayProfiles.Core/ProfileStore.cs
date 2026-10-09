// Copyright (c) 2026 Rausch IT
// SPDX-License-Identifier: MIT

using System.Text.Json;
using System.Text.RegularExpressions;

namespace DisplayProfiles;

// Profiles are JSON snapshots in %LOCALAPPDATA%\DisplayProfiles\profiles\<name>.json.
// Nothing is ever deleted: "delete" moves the file to _archive, overwriting keeps a copy in _backup.
public sealed class ProfileStore
{
    public const string BackupDir = "_backup";
    public const string ArchiveDir = "_archive";

    public static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    private static readonly Regex ValidName = new(@"^[A-Za-z0-9][A-Za-z0-9 ._-]{0,39}$");

    public string Dir { get; }

    public ProfileStore(string? dir = null)
    {
        Dir = dir ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DisplayProfiles", "profiles");
        Directory.CreateDirectory(Dir);
    }

    public static string? CheckName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "Name is missing";
        if (!ValidName.IsMatch(name) || name.EndsWith('.') || name.EndsWith(' '))
            return "Allowed: letters, digits, space, dot, dash and underscore (max. 40 characters, must start with a letter or digit)";
        return null;
    }

    public IReadOnlyList<string> Names() =>
        Directory.EnumerateFiles(Dir, "*.json")
            .Select(Path.GetFileNameWithoutExtension)
            .OfType<string>()
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList();

    public bool Exists(string name) => CheckName(name) is null && File.Exists(PathOf(name));

    public Snapshot Load(string name)
    {
        RequireValid(name);
        var file = PathOf(name);
        if (!File.Exists(file)) throw new ProfileException(404, $"Profile '{name}' not found");
        return JsonSerializer.Deserialize<Snapshot>(File.ReadAllText(file))
               ?? throw new ProfileException(500, $"Profile '{name}' could not be read");
    }

    public void Save(string name, Snapshot snapshot, bool overwrite)
    {
        RequireValid(name);
        var file = PathOf(name);
        if (File.Exists(file))
        {
            if (!overwrite) throw new ProfileException(409, $"Profile '{name}' already exists");
            Backup(name, "overwritten");
        }
        File.WriteAllText(file, JsonSerializer.Serialize(snapshot, Json));
    }

    public void Rename(string name, string newName)
    {
        RequireValid(name);
        RequireValid(newName);
        var from = PathOf(name);
        var to = PathOf(newName);
        if (!File.Exists(from)) throw new ProfileException(404, $"Profile '{name}' not found");
        bool caseOnly = string.Equals(name, newName, StringComparison.OrdinalIgnoreCase);
        if (!caseOnly && File.Exists(to)) throw new ProfileException(409, $"Profile '{newName}' already exists");
        if (caseOnly)
        {
            // Windows file names are case-insensitive: go through a temporary name.
            var tmp = from + ".rename";
            File.Move(from, tmp);
            File.Move(tmp, to);
        }
        else File.Move(from, to);
    }

    // "Delete" = move into _archive with a timestamp.
    public string Archive(string name)
    {
        RequireValid(name);
        var file = PathOf(name);
        if (!File.Exists(file)) throw new ProfileException(404, $"Profile '{name}' not found");
        var dir = Path.Combine(Dir, ArchiveDir);
        Directory.CreateDirectory(dir);
        var target = Path.Combine(dir, $"{name}-{DateTime.Now:yyyyMMdd-HHmmss}.json");
        File.Move(file, target);
        return target;
    }

    private void Backup(string name, string reason)
    {
        var dir = Path.Combine(Dir, BackupDir);
        Directory.CreateDirectory(dir);
        File.Copy(PathOf(name), Path.Combine(dir, $"{name}-{reason}-{DateTime.Now:yyyyMMdd-HHmmss}.json"));
    }

    private string PathOf(string name) => Path.Combine(Dir, name + ".json");

    private static void RequireValid(string name)
    {
        if (CheckName(name) is string err) throw new ProfileException(400, err);
    }
}

public sealed class ProfileException(int status, string message) : Exception(message)
{
    public int Status { get; } = status;
}
