// Copyright (c) 2026 Rausch IT
// SPDX-License-Identifier: MIT

using Microsoft.Win32;

namespace DisplayProfiles.Tray;

// Tray icon: right-click = profile menu (one click switches), double-click = companion window, "Quit" = quit app + service.
internal sealed class TrayContext : ApplicationContext
{
    private readonly ApiClient _api = new();
    private readonly NotifyIcon _icon;
    private readonly ContextMenuStrip _menu = new();
    private readonly Control _invoker = new();
    private readonly System.Windows.Forms.Timer _poll = new() { Interval = 2000 };
    private readonly System.Windows.Forms.Timer _displayChanged = new() { Interval = 1500 };
    private CompanionForm? _companion;

    private StatusDto? _status;
    private List<ProfileDto> _profiles = new();
    private bool _switching;

    public Func<Task>? StopService { get; set; }

    public TrayContext()
    {
        _invoker.CreateControl();
        _icon = new NotifyIcon { Icon = AppIcons.Idle, Text = "DisplayProfiles", ContextMenuStrip = _menu, Visible = true };
        _icon.DoubleClick += (_, _) => ShowCompanion();
        _icon.BalloonTipClicked += (_, _) => ShowCompanion();
        _menu.Opening += (_, _) =>
        {
            BuildMenu();
            _ = RefreshAsync(capture: true);
        };

        // Cheap poll (no driver access) so the icon shows switches started elsewhere, e.g. from the companion or SimFeedback.
        _poll.Tick += async (_, _) => await PollAsync();
        _poll.Start();

        // Windows display changes (monitor on/off, other tools): re-read once things have settled.
        _displayChanged.Tick += async (_, _) =>
        {
            _displayChanged.Stop();
            await RefreshAsync(capture: true);
        };
        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;

        BuildMenu();
    }

    public void ShowCompanion()
    {
        if (_invoker.InvokeRequired) { _invoker.BeginInvoke(ShowCompanion); return; }
        if (_companion is null || _companion.IsDisposed) _companion = new CompanionForm(_api);
        _companion.Show();
        if (_companion.WindowState == FormWindowState.Minimized) _companion.WindowState = FormWindowState.Normal;
        _companion.Activate();
    }

    public async Task RefreshAsync(bool capture)
    {
        try
        {
            _status = await _api.Status(capture);
            _profiles = await _api.Profiles();
        }
        catch (Exception ex)
        {
            _icon.Text = Trim("DisplayProfiles – service not reachable: " + ex.Message);
        }
        UpdateIcon();
        if (_menu.Visible) BuildMenu();
    }

    private async Task PollAsync()
    {
        try
        {
            var s = await _api.Status(refresh: false);
            bool changed = s.Busy != _status?.Busy || s.LastResult?.Finished != _status?.LastResult?.Finished;
            _status = s;
            if (changed) await RefreshAsync(capture: false);
            else UpdateIcon();
        }
        catch
        {
            // service not reachable – shown on the next explicit refresh
        }
    }

    private void OnDisplaySettingsChanged(object? sender, EventArgs e)
    {
        if (_invoker.InvokeRequired) { _invoker.BeginInvoke(() => OnDisplaySettingsChanged(sender, e)); return; }
        _displayChanged.Stop();
        _displayChanged.Start();
    }

    private void BuildMenu()
    {
        _menu.SuspendLayout();
        _menu.Items.Clear();

        bool busy = _switching || _status?.Busy == true;
        var active = _status?.ActiveProfiles ?? new();
        string head = busy ? $"Running: {_status?.Operation ?? "switch"} …"
            : active.Count > 0 ? $"Active: {string.Join(", ", active)}"
            : _status is null ? "Reading state …" : "Active: no saved profile";
        _menu.Items.Add(new ToolStripMenuItem(head) { Enabled = false });
        _menu.Items.Add(new ToolStripMenuItem(_status?.HmdConnected == true ? "VR headset: connected" : "VR headset: not detected") { Enabled = false });
        _menu.Items.Add(new ToolStripSeparator());

        if (_profiles.Count == 0)
            _menu.Items.Add(new ToolStripMenuItem("(no profiles)") { Enabled = false });
        foreach (var p in _profiles)
        {
            var item = new ToolStripMenuItem(p.Name)
            {
                Checked = p.Active,
                Enabled = !busy && p.Error is null,
                ToolTipText = p.Error ?? string.Join("\n", p.Layout),
            };
            var name = p.Name;
            item.Click += async (_, _) => await SwitchAsync(name);
            _menu.Items.Add(item);
        }

        _menu.Items.Add(new ToolStripSeparator());
        var open = new ToolStripMenuItem("Open companion …") { Font = new Font(_menu.Font, FontStyle.Bold) };
        open.Click += (_, _) => ShowCompanion();
        _menu.Items.Add(open);
        var autostart = new ToolStripMenuItem("Start with Windows") { Checked = Autostart.IsEnabled };
        autostart.Click += (_, _) => ToggleAutostart();
        _menu.Items.Add(autostart);
        _menu.Items.Add(new ToolStripSeparator());
        var quit = new ToolStripMenuItem("Quit");
        quit.Click += async (_, _) => await QuitAsync();
        _menu.Items.Add(quit);

        _menu.ResumeLayout();
    }

    private async Task SwitchAsync(string name)
    {
        _switching = true;
        UpdateIcon();
        _icon.ShowBalloonTip(3000, "DisplayProfiles", $"Switching to '{name}' … (with Surround approx. 20 s)", ToolTipIcon.Info);
        try
        {
            var r = await _api.Apply(name, checkOnly: false);
            var detail = r.Details.Count > 0 ? "\n" + string.Join("\n", r.Details.Take(3)) : "";
            _icon.ShowBalloonTip(5000, r.Success ? $"'{name}' active" : $"'{name}': problem",
                r.Message + detail, r.Success ? ToolTipIcon.Info : ToolTipIcon.Warning);
        }
        catch (Exception ex)
        {
            _icon.ShowBalloonTip(5000, $"'{name}' not switched", ex.Message, ToolTipIcon.Error);
        }
        finally
        {
            _switching = false;
            await RefreshAsync(capture: false);
        }
    }

    private void ToggleAutostart()
    {
        try { Autostart.Set(!Autostart.IsEnabled); }
        catch (Exception ex) { MessageBox.Show(ex.Message, "DisplayProfiles – Autostart", MessageBoxButtons.OK, MessageBoxIcon.Error); }
        _companion?.UpdateAutostart();
    }

    private void UpdateIcon()
    {
        bool busy = _switching || _status?.Busy == true;
        _icon.Icon = busy ? AppIcons.Busy : _status?.HmdConnected == true ? AppIcons.Hmd : AppIcons.Idle;
        var active = _status?.ActiveProfiles ?? new();
        _icon.Text = Trim(busy ? $"DisplayProfiles – {_status?.Operation ?? "switch running"}"
            : $"DisplayProfiles – {(active.Count > 0 ? string.Join(", ", active) : "no profile active")}{(_status?.HmdConnected == true ? " · Headset" : "")}");
    }

    private async Task QuitAsync()
    {
        if (_switching || _status?.Busy == true)
        {
            var answer = MessageBox.Show("A switch is running. Quit anyway?", "DisplayProfiles",
                MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
            if (answer != DialogResult.Yes) return;
        }
        _poll.Stop();
        _displayChanged.Stop();
        SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
        _icon.Visible = false;
        _companion?.CloseForQuit();
        if (StopService is not null)
        {
            try { await StopService(); } catch { /* exiting anyway */ }
        }
        ExitThread();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _icon.Dispose();
            _menu.Dispose();
            _poll.Dispose();
            _displayChanged.Dispose();
            _invoker.Dispose();
        }
        base.Dispose(disposing);
    }

    // NotifyIcon.Text is limited to 127 characters.
    private static string Trim(string s) => s.Length <= 127 ? s : s[..124] + "...";
}
