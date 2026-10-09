// Copyright (c) 2026 Rausch IT
// SPDX-License-Identifier: MIT

using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;

namespace DisplayProfiles.Tray;

// Tray icons drawn at runtime: "DS" on a coloured tile. grey = idle, blue = headset detected,
// orange = switching. Same design as installer\make-icon.ps1 (application icon).
internal static class AppIcons
{
    private const string Label = "DS";

    public static readonly Icon Idle = Make(Color.FromArgb(96, 104, 112));
    public static readonly Icon Hmd = Make(Color.FromArgb(0, 120, 215));
    public static readonly Icon Busy = Make(Color.FromArgb(230, 126, 34));

    private static Icon Make(Color background)
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            using var path = new GraphicsPath();
            const int r = 8;
            path.AddArc(0, 0, r, r, 180, 90);
            path.AddArc(31 - r, 0, r, r, 270, 90);
            path.AddArc(31 - r, 31 - r, r, r, 0, 90);
            path.AddArc(0, 31 - r, r, r, 90, 90);
            path.CloseFigure();
            using var fill = new SolidBrush(background);
            g.FillPath(fill, path);
            using var font = new Font("Segoe UI", 13f, FontStyle.Bold, GraphicsUnit.Pixel);
            var size = g.MeasureString(Label, font);
            g.DrawString(Label, font, Brushes.White, (32 - size.Width) / 2, (32 - size.Height) / 2);
        }
        return Icon.FromHandle(bmp.GetHicon());
    }
}
