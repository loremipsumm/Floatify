using System.Diagnostics;
using System.IO;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;

namespace LyricsFloat.App;

/// <summary>Notification-area icon. The overlay has no taskbar button, so this is where its menu lives.</summary>
public sealed class TrayIcon : IDisposable
{
    private static readonly (string Label, double Transparency)[] TransparencyChoices =
        [("Solid", 0.1), ("Normal", 0.35), ("Frosted", 0.6), ("Clear", 0.85)];

    private readonly Forms.NotifyIcon _icon;

    public TrayIcon(MainWindow window, AppSettings settings, Action exit)
    {
        var menu = new Forms.ContextMenuStrip { Renderer = new Forms.ToolStripProfessionalRenderer(new DarkMenuColors()) };
        menu.ForeColor = Drawing.Color.White;
        menu.ShowImageMargin = true;

        menu.Items.Add("Show / hide    Ctrl+Alt+K", null, (_, _) => window.ToggleVisibility());

        var clickThrough = new Forms.ToolStripMenuItem("Lock && click through    Ctrl+Alt+L", null,
            (_, _) => window.ClickThrough = !window.ClickThrough);
        menu.Items.Add(clickThrough);

        // Only shown while a lyrics lookup is waiting to reconnect; works even when the overlay is locked.
        var reconnect = new Forms.ToolStripMenuItem("Reconnect now", null, (_, _) => window.RetryLyricsNow());
        menu.Items.Add(reconnect);

        var background = new Forms.ToolStripMenuItem("Transparency");
        foreach (var (label, value) in TransparencyChoices)
            background.DropDownItems.Add(new Forms.ToolStripMenuItem(label, null, (_, _) => window.Transparency = value) { Tag = value });
        menu.Items.Add(background);

        var autoHide = new Forms.ToolStripMenuItem("Hide when Spotify isn't playing", null,
            (_, _) => window.AutoHide = !window.AutoHide);
        menu.Items.Add(autoHide);

        menu.Items.Add("Reset position", null, (_, _) => window.ResetPosition());
        menu.Items.Add(new Forms.ToolStripSeparator());

        var autoStart = new Forms.ToolStripMenuItem("Start with Windows", null,
            (_, _) => AutoStart.Enabled = !AutoStart.Enabled);
        menu.Items.Add(autoStart);

        menu.Items.Add("Open data folder", null, (_, _) =>
        {
            Directory.CreateDirectory(AppSettings.DataDirectory);
            Process.Start("explorer.exe", AppSettings.DataDirectory);
        });
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Quit", null, (_, _) => exit());

        menu.Opening += (_, _) =>
        {
            clickThrough.Checked = window.ClickThrough;
            reconnect.Visible = window.IsWaitingToRetry;
            autoStart.Checked = AutoStart.Enabled;
            autoHide.Checked = window.AutoHide;
            foreach (Forms.ToolStripMenuItem item in background.DropDownItems)
                item.Checked = Math.Abs((double)item.Tag! - settings.Transparency) < 0.01;
        };

        _icon = new Forms.NotifyIcon
        {
            Icon = CreateIcon(),
            Text = "Floatify",
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

    /// <summary>macOS-style dark menu colours: dark material with the system blue highlight.</summary>
    private sealed class DarkMenuColors : Forms.ProfessionalColorTable
    {
        private static readonly Drawing.Color Background = Drawing.Color.FromArgb(0x1C, 0x1C, 0x1E);
        private static readonly Drawing.Color Accent = Drawing.Color.FromArgb(0x0A, 0x84, 0xFF);
        private static readonly Drawing.Color Border = Drawing.Color.FromArgb(0x3A, 0x3A, 0x3C);

        public override Drawing.Color ToolStripDropDownBackground => Background;
        public override Drawing.Color ImageMarginGradientBegin => Background;
        public override Drawing.Color ImageMarginGradientMiddle => Background;
        public override Drawing.Color ImageMarginGradientEnd => Background;
        public override Drawing.Color MenuBorder => Border;
        public override Drawing.Color MenuItemBorder => Accent;
        public override Drawing.Color MenuItemSelected => Accent;
        public override Drawing.Color MenuItemSelectedGradientBegin => Accent;
        public override Drawing.Color MenuItemSelectedGradientEnd => Accent;
        public override Drawing.Color SeparatorDark => Border;
        public override Drawing.Color SeparatorLight => Background;
        public override Drawing.Color CheckBackground => Accent;
        public override Drawing.Color CheckSelectedBackground => Accent;
        public override Drawing.Color CheckPressedBackground => Accent;
    }
}
