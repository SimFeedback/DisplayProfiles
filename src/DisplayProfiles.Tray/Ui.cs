// Copyright (c) 2026 Rausch IT
// SPDX-License-Identifier: MIT

namespace DisplayProfiles.Tray;

// Small layout helpers. All pixel values are given at 96 DPI and scaled to the control's DPI.
internal static class Ui
{
    public static int S(this Control c, int px) => (int)Math.Round(px * c.DeviceDpi / 96.0);

    public static Font BaseFont => new("Segoe UI", 9.5f);

    public static Label Header(string text) => new()
    {
        Text = text,
        AutoSize = true,
        Font = new Font("Segoe UI Semibold", 10.5f),
        Margin = new Padding(0, 8, 0, 4),
    };

    public static Button Button(string text) => new()
    {
        Text = text,
        AutoSize = true,
        AutoSizeMode = AutoSizeMode.GrowAndShrink,
        Padding = new Padding(10, 4, 10, 4),
        Margin = new Padding(0, 0, 0, 6),
    };

    // Give a set of buttons the same width (the widest preferred width).
    public static void SameWidth(params Button[] buttons)
    {
        int w = buttons.Max(b => b.PreferredSize.Width);
        foreach (var b in buttons) b.MinimumSize = new Size(w, 0);
    }
}
