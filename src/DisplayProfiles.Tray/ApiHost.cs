// Copyright (c) 2026 Rausch IT
// SPDX-License-Identifier: MIT

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace DisplayProfiles.Tray;

// Local REST service (127.0.0.1 only). The tray menu and the companion window use it, and so can other
// programs such as SimFeedback. Contract: docs/rest-api.md.
//
//   GET    /api/status[?refresh=false]        current layout, headset, matching profiles, running operation
//   GET    /api/profiles                      all profiles with a short layout description
//   GET    /api/profiles/{name}               raw profile (snapshot JSON)
//   POST   /api/profiles                      { name, overwrite } – capture the current state as a profile
//   PUT    /api/profiles/{name}               { newName } – rename
//   POST   /api/profiles/{name}/recapture     overwrite with the current state (old version -> _backup)
//   DELETE /api/profiles/{name}               move to _archiv
//   POST   /api/profiles/{name}/apply[?check=true]  switch to the profile (blocks until done, Surround ~15-20 s)
//   POST   /api/ui/show                       open the companion window
internal static class ApiHost
{
    public const int Port = 47811;
    public static string BaseUrl => $"http://127.0.0.1:{Port}/";

    public sealed record CreateRequest(string Name, bool Overwrite);
    public sealed record RenameRequest(string NewName);

    public static WebApplication Build(DisplayService svc, Action showCompanion)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { ContentRootPath = AppContext.BaseDirectory });
        builder.WebHost.UseUrls($"http://127.0.0.1:{Port}");
        builder.Logging.ClearProviders();
        var app = builder.Build();

        app.Use(async (ctx, next) =>
        {
            try { await next(ctx); }
            catch (ProfileException ex) { await Error(ctx, ex.Status, ex.Message); }
            catch (BadHttpRequestException ex) { await Error(ctx, 400, ex.Message); }
            catch (Exception ex) { await Error(ctx, 500, ex.Message); }
        });

        app.MapGet("/api/status", (bool? refresh) => svc.GetStatus(refresh ?? true));
        app.MapGet("/api/profiles", () => svc.ListProfiles());
        app.MapGet("/api/profiles/{name}", (string name) => svc.Store.Load(name));
        app.MapPost("/api/profiles", (CreateRequest req) =>
        {
            var p = svc.Create(req.Name?.Trim() ?? "", req.Overwrite);
            return Results.Created($"/api/profiles/{Uri.EscapeDataString(p.Name)}", p);
        });
        app.MapPut("/api/profiles/{name}", (string name, RenameRequest req) =>
        {
            svc.Rename(name, req.NewName?.Trim() ?? "");
            return Results.NoContent();
        });
        app.MapPost("/api/profiles/{name}/recapture", (string name) => svc.Recapture(name));
        app.MapDelete("/api/profiles/{name}", (string name) => Results.Ok(new { archivedTo = svc.Archive(name) }));
        app.MapPost("/api/profiles/{name}/apply", (string name, bool? check) => svc.Apply(name, check ?? false));
        app.MapPost("/api/ui/show", () =>
        {
            showCompanion();
            return Results.NoContent();
        });

        return app;
    }

    private static Task Error(HttpContext ctx, int status, string message)
    {
        if (ctx.Response.HasStarted) return Task.CompletedTask;
        ctx.Response.StatusCode = status;
        return ctx.Response.WriteAsJsonAsync(new { error = message });
    }
}
