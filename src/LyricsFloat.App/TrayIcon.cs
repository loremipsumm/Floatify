using System.Diagnostics;
using System.IO;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;

namespace LyricsFloat.App;

/// <summary>Notification-area icon. The overlay has no taskbar button, so this is where its menu lives.</summary>
public sealed class TrayIcon : IDisposable
{
    private static readonly (string Label, double Tint)[] TintChoices =
        [("Bening", 0), ("Tipis", 0.18), ("Sedang", 0.32), ("Gelap", 0.5)];

    private readonly Forms.NotifyIcon _icon;

    public TrayIcon(MainWindow window, AppSettings settings, Action exit)
    {
        var menu = new Forms.ContextMenuStrip();

        menu.Items.Add("Tampilkan / sembunyikan    Ctrl+Alt+K", null, (_, _) => window.ToggleVisibility());

        var clickThrough = new Forms.ToolStripMenuItem("Kunci && tembus klik    Ctrl+Alt+L", null,
            (_, _) => window.ClickThrough = !window.ClickThrough);
        menu.Items.Add(clickThrough);

        // Only shown while a lyrics lookup is waiting to reconnect; works even when the overlay is locked.
        var reconnect = new Forms.ToolStripMenuItem("Hubungkan ulang sekarang", null, (_, _) => window.RetryLyricsNow());
        menu.Items.Add(reconnect);

        var tint = new Forms.ToolStripMenuItem("Warna kaca");
        foreach (var (label, value) in TintChoices)
            tint.DropDownItems.Add(new Forms.ToolStripMenuItem(label, null, (_, _) => window.GlassTint = value) { Tag = value });
        menu.Items.Add(tint);

        menu.Items.Add("Reset posisi", null, (_, _) => window.ResetPosition());
        menu.Items.Add(new Forms.ToolStripSeparator());

        var autoStart = new Forms.ToolStripMenuItem("Jalankan saat Windows mulai", null,
            (_, _) => AutoStart.Enabled = !AutoStart.Enabled);
        menu.Items.Add(autoStart);

        menu.Items.Add("Buka folder data", null, (_, _) =>
        {
            Directory.CreateDirectory(AppSettings.DataDirectory);
            Process.Start("explorer.exe", AppSettings.DataDirectory);
        });
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Keluar", null, (_, _) => exit());

        menu.Opening += (_, _) =>
        {
            clickThrough.Checked = window.ClickThrough;
            reconnect.Visible = window.IsWaitingToRetry;
            autoStart.Checked = AutoStart.Enabled;
            foreach (Forms.ToolStripMenuItem item in tint.DropDownItems)
                item.Checked = Math.Abs((double)item.Tag! - settings.GlassTint) < 0.01;
        };

        _icon = new Forms.NotifyIcon
        {
            Icon = CreateIcon(),
            Text = "Spotify Lyrics Float",
            ContextMenuStrip = menu,
            Visible = true,
        };
        _icon.MouseClick += (_, e) =>
        {
            if (e.Button == Forms.MouseButtons.Left) window.ToggleVisibility();
        };
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
    }

    private static Drawing.Icon CreateIcon()
    {
        using var bitmap = new Drawing.Bitmap(32, 32);
        using (var g = Drawing.Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.TextRenderingHint = Drawing.Text.TextRenderingHint.AntiAliasGridFit;

            using var fill = new Drawing.SolidBrush(Drawing.Color.FromArgb(124, 92, 255));
            g.FillEllipse(fill, 1, 1, 30, 30);

            using var font = new Drawing.Font("Segoe UI Symbol", 18, Drawing.FontStyle.Bold, Drawing.GraphicsUnit.Pixel);
            using var format = new Drawing.StringFormat
            {
                Alignment = Drawing.StringAlignment.Center,
                LineAlignment = Drawing.StringAlignment.Center,
            };
            g.DrawString("♪", font, Drawing.Brushes.White, new Drawing.RectangleF(0, 1, 32, 32), format);
        }
        return Drawing.Icon.FromHandle(bitmap.GetHicon());
    }
}
