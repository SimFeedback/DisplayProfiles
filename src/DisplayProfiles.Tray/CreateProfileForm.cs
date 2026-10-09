// Copyright (c) 2026 Rausch IT
// SPDX-License-Identifier: MIT

namespace DisplayProfiles.Tray;

// "New profile": the user arranges the displays (Windows / NVIDIA Control Panel), checks the preview, names it, saves.
internal sealed class CreateProfileForm : Form
{
    private readonly ApiClient _api;
    private readonly Label _preview = new() { AutoSize = true, Margin = new Padding(0, 0, 0, 8) };
    private readonly TextBox _name = new();
    private readonly Button _save = Ui.Button("Save");
    private readonly Button _reread = Ui.Button("Read again");
    private readonly Button _cancel = Ui.Button("Cancel");
    private readonly Label _status = new() { AutoSize = true, ForeColor = Color.FromArgb(200, 100, 0) };

    public string? CreatedName { get; private set; }

    public CreateProfileForm(ApiClient api)
    {
        _api = api;
        SuspendLayout();
        Text = "New profile";
        Icon = AppIcons.Idle;
        Font = Ui.BaseFont;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        StartPosition = FormStartPosition.CenterParent;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        _cancel.DialogResult = DialogResult.Cancel;
        AcceptButton = _save;
        CancelButton = _cancel;
        _name.Width = this.S(320);
        _status.MaximumSize = new Size(this.S(600), 0);

        var hint = new Label
        {
            AutoSize = true,
            MaximumSize = new Size(this.S(600), 0),
            Margin = new Padding(0, 0, 0, 4),
            Text = "Set up the desired layout first in Windows or the NVIDIA Control Panel " +
                   "(monitors, Surround, bezel, refresh rate). Exactly the state shown below is saved.",
        };

        var nameRow = new FlowLayoutPanel { AutoSize = true, Margin = new Padding(0, 4, 0, 4) };
        nameRow.Controls.Add(new Label { Text = "Name:", AutoSize = true, Margin = new Padding(0, 6, 6, 0) });
        nameRow.Controls.Add(_name);

        var buttons = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, Margin = new Padding(0, 8, 0, 0) };
        _cancel.Margin = _save.Margin = _reread.Margin = new Padding(6, 0, 0, 0);
        buttons.Controls.AddRange(new Control[] { _cancel, _save, _reread });

        var root = new TableLayoutPanel { AutoSize = true, ColumnCount = 1, Padding = new Padding(this.S(14)) };
        root.Controls.Add(hint);
        root.Controls.Add(Ui.Header("Current state (will be saved)"));
        root.Controls.Add(_preview);
        root.Controls.Add(nameRow);
        root.Controls.Add(_status);
        root.Controls.Add(buttons);
        Controls.Add(root);

        _reread.Click += async (_, _) => await ReadAsync();
        _save.Click += async (_, _) => await SaveAsync();
        ResumeLayout(true);
    }

    protected override async void OnShown(EventArgs e)
    {
        base.OnShown(e);
        _name.Focus();
        await ReadAsync();
    }

    private async Task ReadAsync()
    {
        _preview.Text = "reading …";
        try
        {
            var s = await _api.Status(refresh: true);
            var lines = new List<string>(s.Layout);
            if (!s.HmdConnected) lines.Add("VR headset not detected");
            if (s.ActiveProfiles.Count > 0) lines.Add("Already matches profile: " + string.Join(", ", s.ActiveProfiles));
            _preview.Text = string.Join(Environment.NewLine, lines);
        }
        catch (Exception ex)
        {
            _preview.Text = "Error: " + ex.Message;
        }
    }

    private async Task SaveAsync()
    {
        var name = _name.Text.Trim();
        if (ProfileStore.CheckName(name) is string err)
        {
            _status.Text = err;
            return;
        }
        SetWorking(true, "Saving … (the state is read twice)");
        try
        {
            try
            {
                await _api.Create(name, overwrite: false);
            }
            catch (ApiException ex) when (ex.Status == 409 && ex.Message.Contains("already exists"))
            {
                var answer = MessageBox.Show(this, $"Profile '{name}' already exists. Overwrite it?\n\nThe old version is kept in the _backup folder.",
                    "New profile", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
                if (answer != DialogResult.Yes) { SetWorking(false, ""); return; }
                await _api.Create(name, overwrite: true);
            }
            CreatedName = name;
            DialogResult = DialogResult.OK;
            Close();
        }
        catch (Exception ex)
        {
            SetWorking(false, ex.Message);
        }
    }

    private void SetWorking(bool working, string text)
    {
        _status.Text = text;
        _save.Enabled = _reread.Enabled = _name.Enabled = !working;
        UseWaitCursor = working;
    }
}

// Minimal text prompt (rename).
internal static class InputDialog
{
    public static string? Ask(IWin32Window owner, string title, string prompt, string value)
    {
        using var form = new Form
        {
            Text = title, FormBorderStyle = FormBorderStyle.FixedDialog, MaximizeBox = false, MinimizeBox = false,
            StartPosition = FormStartPosition.CenterParent, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Font = Ui.BaseFont, Icon = AppIcons.Idle,
        };
        var box = new TextBox { Text = value, Width = form.S(340) };
        var error = new Label { AutoSize = true, ForeColor = Color.Firebrick, MaximumSize = new Size(form.S(360), 0) };
        var ok = Ui.Button("OK");
        var cancel = Ui.Button("Cancel");
        cancel.DialogResult = DialogResult.Cancel;
        ok.Margin = cancel.Margin = new Padding(6, 0, 0, 0);
        ok.Click += (_, _) =>
        {
            if (ProfileStore.CheckName(box.Text.Trim()) is string err) { error.Text = err; return; }
            form.DialogResult = DialogResult.OK;
        };
        var buttons = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, Margin = new Padding(0, 8, 0, 0) };
        buttons.Controls.AddRange(new Control[] { cancel, ok });
        var root = new TableLayoutPanel { AutoSize = true, ColumnCount = 1, Padding = new Padding(form.S(14)) };
        root.Controls.Add(new Label { Text = prompt, AutoSize = true, Margin = new Padding(0, 0, 0, 6) });
        root.Controls.Add(box);
        root.Controls.Add(error);
        root.Controls.Add(buttons);
        form.Controls.Add(root);
        form.AcceptButton = ok;
        form.CancelButton = cancel;
        box.SelectAll();
        return form.ShowDialog(owner) == DialogResult.OK ? box.Text.Trim() : null;
    }
}
