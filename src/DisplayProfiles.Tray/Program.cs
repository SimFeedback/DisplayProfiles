// Copyright (c) 2026 Rausch IT
// SPDX-License-Identifier: MIT

using DisplayProfiles;
using DisplayProfiles.Tray;

// DisplayProfiles tray app: hosts the REST service (ApiHost) and the tray icon / companion window that use it.
// A second start does not run a second instance; it opens the companion window of the running one.
internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        bool autostart = args.Any(a => a.Equals(Autostart.Argument, StringComparison.OrdinalIgnoreCase));

        using var mutex = new Mutex(true, @"Local\DisplayProfiles.Tray", out bool first);
        if (!first)
        {
            if (!autostart) ShowRunningInstance();
            return 0;
        }

        ApplicationConfiguration.Initialize();

        DisplayService svc;
        try
        {
            svc = new DisplayService(new ProfileStore());
        }
        catch (Exception ex)
        {
            MessageBox.Show("NVIDIA interface not available:\n" + ex.Message, "DisplayProfiles", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }

        using var tray = new TrayContext();
        var app = ApiHost.Build(svc, tray.ShowCompanion);
        try
        {
            app.StartAsync().GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"REST service could not start ({ApiHost.BaseUrl}):\n{ex.Message}", "DisplayProfiles", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }
        tray.StopService = () => app.StopAsync();

        _ = tray.RefreshAsync(capture: true);
        if (!autostart) tray.ShowCompanion();

        Application.Run(tray);
        app.DisposeAsync().AsTask().GetAwaiter().GetResult();
        return 0;
    }

    private static void ShowRunningInstance()
    {
        try
        {
            using var http = new HttpClient { BaseAddress = new Uri(ApiHost.BaseUrl), Timeout = TimeSpan.FromSeconds(5) };
            http.PostAsync("api/ui/show", null).GetAwaiter().GetResult();
        }
        catch
        {
            // running instance not reachable – nothing else to do
        }
    }
}
