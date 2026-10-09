// Copyright (c) 2026 Rausch IT
// SPDX-License-Identifier: MIT

using System.Net.Http.Json;
using System.Text.Json;

namespace DisplayProfiles.Tray;

// REST client used by the tray menu and the companion window.
internal sealed class ApiClient
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    // A Surround switch blocks the request for ~15-20 s; leave plenty of room.
    private readonly HttpClient _http = new() { BaseAddress = new Uri(ApiHost.BaseUrl), Timeout = TimeSpan.FromMinutes(2) };

    public Task<StatusDto> Status(bool refresh) => Send<StatusDto>(HttpMethod.Get, $"api/status?refresh={(refresh ? "true" : "false")}");
    public Task<List<ProfileDto>> Profiles() => Send<List<ProfileDto>>(HttpMethod.Get, "api/profiles");
    public Task<ProfileDto> Create(string name, bool overwrite) => Send<ProfileDto>(HttpMethod.Post, "api/profiles", new { name, overwrite });
    public Task<ProfileDto> Recapture(string name) => Send<ProfileDto>(HttpMethod.Post, $"api/profiles/{E(name)}/recapture");
    public Task Rename(string name, string newName) => Send<object>(HttpMethod.Put, $"api/profiles/{E(name)}", new { newName });
    public Task Archive(string name) => Send<object>(HttpMethod.Delete, $"api/profiles/{E(name)}");
    public Task<OperationResult> Apply(string name, bool checkOnly) =>
        Send<OperationResult>(HttpMethod.Post, $"api/profiles/{E(name)}/apply{(checkOnly ? "?check=true" : "")}");

    private async Task<T> Send<T>(HttpMethod method, string url, object? body = null)
    {
        using var req = new HttpRequestMessage(method, url);
        if (body is not null) req.Content = JsonContent.Create(body, options: Json);
        using var res = await _http.SendAsync(req);
        if (!res.IsSuccessStatusCode)
        {
            string message = $"HTTP {(int)res.StatusCode}";
            try
            {
                var err = await res.Content.ReadFromJsonAsync<JsonElement>(Json);
                if (err.TryGetProperty("error", out var e)) message = e.GetString() ?? message;
            }
            catch (JsonException) { }
            throw new ApiException((int)res.StatusCode, message);
        }
        if (res.StatusCode == System.Net.HttpStatusCode.NoContent || typeof(T) == typeof(object)) return default!;
        return (await res.Content.ReadFromJsonAsync<T>(Json))!;
    }

    private static string E(string name) => Uri.EscapeDataString(name);
}

internal sealed class ApiException(int status, string message) : Exception(message)
{
    public int Status { get; } = status;
}
