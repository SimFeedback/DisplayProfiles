// Copyright (c) 2026 Rausch IT
// SPDX-License-Identifier: MIT

namespace DisplayProfiles.Tray;

// Companion window: current state, profile management (create, rename, re-capture, delete) and switching.
// Closing the window only hides it; the app keeps running in the tray.
internal sealed class CompanionForm : Form
{
    private readonly ApiClient _api;
    private readonly Label _stateText = new() { AutoSize = true, Margin = new Padding(0, 0, 0, 4) };
    private readonly ListView _list = new()
    {
        View = View.Details, FullRowSelect = true, MultiSelect = false, HideSelection = false,
        Dock = DockStyle.Fill, HeaderStyle = ColumnHeaderStyle.Nonclickable, Margin = new Padding(0),
    };
    private readonly TextBox _log = new()
    {
        Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, WordWrap = false,
        Dock = DockStyle.Fill, Font = new Font("Consolas", 9.5f), Margin = new Padding(0),
    };
    private readonly SplitContainer _split = new() { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal };
    private readonly Label _busy = new() { AutoSize = true, ForeColor = Color.FromArgb(200, 100, 0), Margin = new Padding(16, 6, 0, 0) };
    private readonly CheckBox _autostart = new() { Text = "Start with Windows", AutoSize = true, Margin = new Padding(0, 4, 0, 0) };
    private readonly Button _apply = Ui.Button("Activate");
    private readonly Button _check = Ui.Button("Check");
    private readonly Button _create = Ui.Button("New profile …");
    private readonly Button _recapture = Ui.Button("Re-capture …");
    private readonly Button _rename = Ui.Button("Rename …");
    private readonly Button _delete = Ui.Button("Delete …");
    private readonly Button _refresh = Ui.Button("Refresh");
    private readonly System.Windows.Forms.Timer _poll = new() { Interval = 2000 };

    private bool _working;
    private bool _quitting;
    private StatusDto? _status;

    public CompanionForm(ApiClient api)
    {
        _api = api;
        SuspendLayout();
        Text = "DisplayProfiles";
        Icon = AppIcons.Idle;
        Font = Ui.BaseFont;
        StartPosition = FormStartPosition.CenterScreen;
        Size = new Size(this.S(1040), this.S(760));
        MinimumSize = new Size(this.S(760), this.S(540));

        _list.Columns.Add("Profile");
        _list.Columns.Add("Layout (left → right)");
        _list.Columns.Add("Captured");

        // Current state: text + refresh button
        var stateRow = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 2, RowCount = 1, Margin = new Padding(0) };
        stateRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        stateRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        stateRow.Controls.Add(_stateText, 0, 0);
        stateRow.Controls.Add(_refresh, 1, 0);

        // Profiles: list + button column
        var buttons = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink, Dock = DockStyle.Fill, Margin = new Padding(this.S(10), 0, 0, 0),
        };
        buttons.Controls.AddRange(new Control[] { _apply, _check, Spacer(), _create, _recapture, _rename, _delete });

        var profileBody = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = new Padding(0) };
        profileBody.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        profileBody.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        profileBody.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        profileBody.Controls.Add(_list, 0, 0);
        profileBody.Controls.Add(buttons, 1, 0);

        _split.Panel1.Controls.Add(Section("Profiles", profileBody));
        _split.Panel2.Controls.Add(Section("Log", _log));

        var footer = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, Margin = new Padding(0, this.S(6), 0, 0) };
        footer.Controls.Add(_autostart);
        footer.Controls.Add(_busy);

        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4, Padding = new Padding(this.S(12)) };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.Controls.Add(Ui.Header("Current state"), 0, 0);
        root.Controls.Add(stateRow, 0, 1);
        root.Controls.Add(_split, 0, 2);
        root.Controls.Add(footer, 0, 3);
        Controls.Add(root);

        _refresh.Click += async (_, _) => await LoadAsync(capture: true);
        _apply.Click += async (_, _) => await ApplyAsync(checkOnly: false);
        _check.Click += async (_, _) => await ApplyAsync(checkOnly: true);
        _create.Click += async (_, _) => await CreateAsync();
        _recapture.Click += async (_, _) => await RecaptureAsync();
        _rename.Click += async (_, _) => await RenameAsync();
        _delete.Click += async (_, _) => await DeleteAsync();
        _list.SelectedIndexChanged += (_, _) => UpdateButtons();
        _autostart.CheckedChanged += (_, _) =>
        {
            if (_autostart.Checked == Autostart.IsEnabled) return;
            try { Autostart.Set(_autostart.Checked); }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, "Autostart", MessageBoxButtons.OK, MessageBoxIcon.Error); }
            UpdateAutostart();
        };
        _poll.Tick += async (_, _) => await PollAsync();

        UpdateAutostart();
        UpdateButtons();
        ResumeLayout(true);
    }

    public void UpdateAutostart() => _autostart.Checked = Autostart.IsEnabled;

    public void CloseForQuit()
    {
        _quitting = true;
        Close();
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        Ui.SameWidth(_apply, _check, _create, _recapture, _rename, _delete);
        _split.Panel1MinSize = this.S(160);
        _split.Panel2MinSize = this.S(120);
        _split.SplitterWidth = this.S(8);
        _split.SplitterDistance = (int)(_split.Height * 0.55);
        SizeColumns();
    }

    protected override async void OnShown(EventArgs e)
    {
        base.OnShown(e);
        await LoadAsync(capture: true);
    }

    protected override void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);
        if (Visible) _poll.Start(); else _poll.Stop();
        if (Visible && _status is not null) _ = LoadAsync(capture: true);
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (!_quitting && e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            Hide();
            return;
        }
        _poll.Stop();
        base.OnFormClosing(e);
    }

    private async Task LoadAsync(bool capture)
    {
        try
        {
            _status = await _api.Status(capture);
            var profiles = await _api.Profiles();
            ShowStatus();
            string? selected = Selected?.Name;
            _list.BeginUpdate();
            _list.Items.Clear();
            foreach (var p in profiles)
            {
                var item = new ListViewItem((p.Active ? "● " : "    ") + p.Name) { Tag = p };
                item.SubItems.Add(p.Error ?? string.Join("   |   ", p.Layout));
                item.SubItems.Add(p.CapturedAt?.ToString("yyyy-MM-dd HH:mm") ?? "");
                if (p.Active) item.Font = new Font(_list.Font, FontStyle.Bold);
                if (p.Error is not null) item.ForeColor = Color.Firebrick;
                _list.Items.Add(item);
                if (p.Name == selected) item.Selected = true;
            }
            SizeColumns();
            _list.EndUpdate();
        }
        catch (Exception ex)
        {
            _stateText.Text = "Service not reachable: " + ex.Message;
        }
        UpdateButtons();
    }

    // Columns as wide as their content (at least the header); the list scrolls horizontally when needed.
    private void SizeColumns()
    {
        for (int i = 0; i < _list.Columns.Count; i++)
        {
            _list.AutoResizeColumn(i, ColumnHeaderAutoResizeStyle.HeaderSize);
            int header = _list.Columns[i].Width;
            if (_list.Items.Count > 0)
            {
                _list.AutoResizeColumn(i, ColumnHeaderAutoResizeStyle.ColumnContent);
                if (_list.Columns[i].Width < header) _list.Columns[i].Width = header;
            }
            _list.Columns[i].Width += this.S(16);
        }
    }

    private void ShowStatus()
    {
        if (_status is null) return;
        var lines = new List<string>(_status.Layout);
        if (!_status.HmdConnected) lines.Add("VR headset not detected");
        lines.Add(_status.ActiveProfiles.Count > 0
            ? "Matches profile: " + string.Join(", ", _status.ActiveProfiles)
            : "Matches no saved profile");
        _stateText.Text = string.Join(Environment.NewLine, lines);
        _busy.Text = _status.Busy ? $"Running: {_status.Operation} …" : "";
        Icon = _status.Busy ? AppIcons.Busy : _status.HmdConnected ? AppIcons.Hmd : AppIcons.Idle;
    }

    // Cheap poll (no driver access): picks up switches started from the tray menu or by other programs.
    private async Task PollAsync()
    {
        if (_working) return;
        try
        {
            var s = await _api.Status(refresh: false);
            bool changed = s.Busy != _status?.Busy || s.LastResult?.Finished != _status?.LastResult?.Finished;
            _status = s;
            ShowStatus();
            if (changed && !s.Busy)
            {
                if (s.LastResult is not null) ShowResult(s.LastResult);
                await LoadAsync(capture: false);
            }
            UpdateButtons();
        }
        catch
        {
            // shown on the next explicit refresh
        }
    }

    private ProfileDto? Selected => _list.SelectedItems.Count > 0 ? _list.SelectedItems[0].Tag as ProfileDto : null;

    private void UpdateButtons()
    {
        bool idle = !_working && _status?.Busy != true;
        bool sel = Selected is { Error: null };
        _apply.Enabled = idle && sel;
        _check.Enabled = idle && sel;
        _recapture.Enabled = idle && sel;
        _rename.Enabled = idle && Selected is not null;
        _delete.Enabled = idle && Selected is not null;
        _create.Enabled = idle;
        _refresh.Enabled = idle;
    }

    private async Task ApplyAsync(bool checkOnly)
    {
        if (Selected is not { } p) return;
        await Run(checkOnly ? $"Checking '{p.Name}' …" : $"Switching to '{p.Name}' … (with Surround approx. 20 s)", async () =>
        {
            var r = await _api.Apply(p.Name, checkOnly);
            ShowResult(r);
        });
    }

    private async Task CreateAsync()
    {
        using var dlg = new CreateProfileForm(_api);
        if (dlg.ShowDialog(this) == DialogResult.OK)
        {
            _log.Text = $"Profile '{dlg.CreatedName}' saved.";
            await LoadAsync(capture: false);
        }
    }

    private async Task RecaptureAsync()
    {
        if (Selected is not { } p) return;
        var answer = MessageBox.Show(this,
            $"Overwrite profile '{p.Name}' with the current state?\n\nCurrent state:\n{string.Join("\n", _status?.Layout ?? new())}\n\nThe old version is kept in the _backup folder.",
            "Re-capture", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
        if (answer != DialogResult.Yes) return;
        await Run($"Re-capturing '{p.Name}' …", async () =>
        {
            await _api.Recapture(p.Name);
            _log.Text = $"Profile '{p.Name}' re-captured.";
        });
    }

    private async Task RenameAsync()
    {
        if (Selected is not { } p) return;
        var newName = InputDialog.Ask(this, "Rename", $"New name for '{p.Name}':", p.Name);
        if (newName is null || newName == p.Name) return;
        await Run("Renaming …", async () =>
        {
            await _api.Rename(p.Name, newName);
            _log.Text = $"'{p.Name}' is now '{newName}'.";
        });
    }

    private async Task DeleteAsync()
    {
        if (Selected is not { } p) return;
        var answer = MessageBox.Show(this, $"Delete profile '{p.Name}'?\n\nThe file is moved to the _archive folder.",
            "Delete", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
        if (answer != DialogResult.Yes) return;
        await Run("Deleting …", async () =>
        {
            await _api.Archive(p.Name);
            _log.Text = $"Profile '{p.Name}' moved to the archive.";
        });
    }

    private async Task Run(string busyText, Func<Task> action)
    {
        _working = true;
        _busy.Text = busyText;
        UpdateButtons();
        UseWaitCursor = true;
        try
        {
            await action();
        }
        catch (Exception ex)
        {
            _log.Text = "Error: " + ex.Message;
            MessageBox.Show(this, ex.Message, "DisplayProfiles", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        finally
        {
            _working = false;
            UseWaitCursor = false;
            _busy.Text = "";
            await LoadAsync(capture: false);
        }
    }

    private void ShowResult(OperationResult r)
    {
        var head = $"{(r.CheckOnly ? "Check" : "Switch")} '{r.Profile}' {r.Finished:HH:mm:ss}: {r.Message}";
        _log.Text = head + Environment.NewLine + Environment.NewLine + r.Log.Replace("\r\n", "\n").Replace("\n", Environment.NewLine);
    }

    private Control Section(string title, Control body)
    {
        var t = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Margin = new Padding(0) };
        t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        t.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        t.Controls.Add(Ui.Header(title), 0, 0);
        t.Controls.Add(body, 0, 1);
        return t;
    }

    private Control Spacer() => new Panel { Height = this.S(10), Width = 1, Margin = new Padding(0) };
}
