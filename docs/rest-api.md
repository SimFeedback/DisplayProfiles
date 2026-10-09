# REST API

The tray app (`DisplayProfiles.exe`) hosts a small REST API. The tray menu and the companion window
use it themselves, and so can other programs — a Stream Deck plugin, SimFeedback, scripts.

- Base URL: `http://127.0.0.1:47811/` — bound to localhost only, no authentication
- JSON, camelCase property names
- Errors: HTTP status code plus `{ "error": "<message>" }`
- Messages are meant for humans and may change; decide on HTTP status and `code`, not on text

## Endpoints

| Method | Path | Changes anything? | Purpose |
|--------|------|-------------------|---------|
| GET | `/api/status` | no | Reads the current state (~0.1 s). `?refresh=false` returns the cached state without driver access — use it for polling |
| GET | `/api/profiles` | no | All profiles with a short layout description and an `active` flag |
| GET | `/api/profiles/{name}` | no | Raw profile (snapshot JSON) |
| POST | `/api/profiles` | writes a profile | Body `{ "name": "...", "overwrite": false }`. Captures the current state twice, 1.5 s apart, and refuses if they differ (state still settling) |
| PUT | `/api/profiles/{name}` | renames | Body `{ "newName": "..." }` |
| POST | `/api/profiles/{name}/recapture` | overwrites | Replaces the profile with the current state; the old version goes to `_backup` |
| DELETE | `/api/profiles/{name}` | moves a file | Moves the profile to `_archive` (nothing is deleted) |
| POST | `/api/profiles/{name}/apply` | **switches** | Blocks until done (Surround ~15–20 s). `?check=true` validates only |
| POST | `/api/ui/show` | no | Opens the companion window |

Status codes: `200` / `201` / `204` success · `400` invalid name · `404` profile not found · `409`
profile exists, state not stable, or another operation is running · `500` unexpected error.

Profile names: letters, digits, space, `.`, `-`, `_`; at most 40 characters; must start with a letter
or a digit. A profile is `active` when the current state matches it (a VR headset is ignored).

All driver access is serialized. While a switch runs, other writing calls get `409`, and
`/api/status` returns the last known state with `busy: true` and the running `operation`.

## Responses

`GET /api/status`:

```json
{
  "capturedAt": "2026-10-09T14:37:20+02:00",
  "hmdConnected": false,
  "hmdDisplayId": null,
  "layout": ["LG ULTRAGEAR+ (HDMI) · 3840x2160 @ 240 Hz · primary"],
  "activeProfiles": ["desktop"],
  "busy": false,
  "operation": null,
  "lastResult": null
}
```

`POST /api/profiles/{name}/apply` returns an `OperationResult`; the last switch is also reported as
`lastResult` in the status:

```json
{
  "profile": "desktop",
  "checkOnly": true,
  "code": 0,
  "success": true,
  "message": "Check OK, nothing changed",
  "details": [],
  "log": "…full log…",
  "started": "2026-10-09T14:37:30+02:00",
  "finished": "2026-10-09T14:37:30+02:00"
}
```

An apply that ran returns HTTP 200 even if it did not succeed — check `code`:

| Code | Meaning |
|------|---------|
| 0 | Success; the read-back state matches the profile (or: check passed) |
| 1 | Unexpected error |
| 5 | A monitor of the profile is not connected, or a different monitor (serial number) is on that output. Nothing was changed |
| 6 | Validation rejected (NVIDIA Surround grid or Windows layout). Nothing was changed |
| 7 | NVIDIA failed to set the Surround grid |
| 8 | Windows failed to set the layout, or the read-back state differs from the profile |

## Calling it (C#)

```csharp
using var http = new HttpClient
{
    BaseAddress = new Uri("http://127.0.0.1:47811/"),
    Timeout = TimeSpan.FromMinutes(2),
};
var res = await http.PostAsync($"api/profiles/{Uri.EscapeDataString("triple")}/apply", null);
// 200 -> OperationResult: check "code" / "success"
// 404 unknown profile · 409 another operation running · connection refused -> tray app not running
```

Recommendations:

- Call asynchronously; a Surround switch blocks the request for 15–20 s.
- Use `?check=true` first to show "would work / would not work" before switching.
- For a live display, poll `GET /api/status?refresh=false` and call `GET /api/status` (fresh read)
  when something changed.

## Console tool

Without the tray app, `DisplayProfiles.Cli.exe` offers the same core functions. It shares the profile
folder but has no locking against the tray app — do not switch from both at the same time.

| Command | Changes anything? | Purpose |
|---------|-------------------|---------|
| `status` | no | Print NVIDIA outputs, Surround grids, Windows desktop, headset detection |
| `save <name>` | writes a profile | Store the current state; refuses to overwrite |
| `apply <name> --check` | no | Validate a profile against the current hardware |
| `apply <name>` | yes | Switch to the profile |
| `probe <name>` | no | Diagnostics: validate the profile's Surround grid in several variants |

Exit codes are the `code` values above, plus `2` (wrong usage) and `3` (`save`: profile exists,
`apply`: profile not found).
