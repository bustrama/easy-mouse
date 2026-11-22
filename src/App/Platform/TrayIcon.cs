using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace EasyMouse.App.Platform;

public enum TrayState { Idle, Connected, Stopped }

/// <summary>The notification-area icon (WinForms NotifyIcon): state icon, dark context menu, connect toasts.</summary>
public sealed class TrayIcon : IDisposable
{
    private readonly NotifyIcon _icon;
    private readonly ContextMenuStrip _menu = new();
    private readonly Dictionary<TrayState, Icon> _icons;
    private readonly ToolStripMenuItem _showQr = new(), _settings = new(), _devices = new(), _disconnect = new(), _resume = new(), _quit = new();

    public TrayIcon()
    {
        _icons = new()
        {
            [TrayState.Idle] = Draw(null),
            [TrayState.Connected] = Draw(Color.FromArgb(0x22, 0xC5, 0x5E)),
            [TrayState.Stopped] = Draw(Color.FromArgb(0xFB, 0xBF, 0x24)),
        };
        _menu.Renderer = new DarkRenderer();
        _menu.ShowImageMargin = false;
        _menu.Font = new Font("Segoe UI", 9.5f);
        _menu.Items.AddRange([_showQr, _settings, _devices, new ToolStripSeparator(), _disconnect, _resume, new ToolStripSeparator(), _quit]);
        _showQr.Click += (_, _) => ShowQrClicked?.Invoke();
        _settings.Click += (_, _) => SettingsClicked?.Invoke();
        _devices.Click += (_, _) => DevicesClicked?.Invoke();
        _disconnect.Click += (_, _) => DisconnectAllClicked?.Invoke();
        _resume.Click += (_, _) => ResumeClicked?.Invoke();
        _quit.Click += (_, _) => QuitClicked?.Invoke();
        _icon = new NotifyIcon { Icon = _icons[TrayState.Idle], ContextMenuStrip = _menu, Visible = true };
        _icon.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) ShowQrClicked?.Invoke(); };
        _icon.BalloonTipClicked += (_, _) => ShowQrClicked?.Invoke();
    }

    public event Action? ShowQrClicked, SettingsClicked, DevicesClicked, DisconnectAllClicked, ResumeClicked, QuitClicked;

    public void SetTexts(string showQr, string settings, string devices, string disconnectAll, string resume, string quit, bool rtl)
    {
        _showQr.Text = showQr; _settings.Text = settings; _devices.Text = devices;
        _disconnect.Text = disconnectAll; _resume.Text = resume; _quit.Text = quit;
        _menu.RightToLeft = rtl ? RightToLeft.Yes : RightToLeft.No;
    }

    /// <summary>Shows the "Start listening again" entry only while stopped by the emergency hotkey.</summary>
    public void SetResumeVisible(bool visible) => _resume.Visible = visible;

    public void SetState(TrayState state, string tooltip)
    {
        _icon.Icon = _icons[state];
        _icon.Text = tooltip.Length > 63 ? tooltip[..63] : tooltip; // NotifyIcon limit
    }

    public void Toast(string title, string body) => _icon.ShowBalloonTip(5000, title, body, ToolTipIcon.Info);

    private static Icon Draw(Color? badge)
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using var path = RoundedRect(new RectangleF(1, 1, 30, 30), 8);
            using var fill = new SolidBrush(Color.FromArgb(0x25, 0x63, 0xEB));
            g.FillPath(fill, path);
            using var pen = new Pen(Color.White, 2.4f) { LineJoin = LineJoin.Round };
            using var mouse = RoundedRect(new RectangleF(10.5f, 6.5f, 11, 17), 5.5f);
            g.DrawPath(pen, mouse);
            g.DrawLine(pen, 16, 6.5f, 16, 13);
            if (badge is { } color)
            {
                using var ring = new SolidBrush(Color.FromArgb(0x0E, 0x11, 0x16));
                using var dot = new SolidBrush(color);
                g.FillEllipse(ring, 17, 17, 15, 15);
                g.FillEllipse(dot, 19.5f, 19.5f, 10, 10);
            }
        }
        var handle = bmp.GetHicon();
        try { return (Icon)Icon.FromHandle(handle).Clone(); }
        finally { DestroyIcon(handle); }
    }

    private static GraphicsPath RoundedRect(RectangleF r, float radius)
    {
        float d = radius * 2;
        var p = new GraphicsPath();
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr handle);

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
        _menu.Dispose();
        foreach (var icon in _icons.Values) icon.Dispose();
    }

    private sealed class DarkRenderer : ToolStripProfessionalRenderer
    {
        public DarkRenderer() : base(new DarkColors()) { }

        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            e.TextColor = Color.FromArgb(0xF3, 0xF5, 0xF7);
            base.OnRenderItemText(e);
        }
    }

    private sealed class DarkColors : ProfessionalColorTable
    {
        private static readonly Color Surface = Color.FromArgb(0x17, 0x1C, 0x24), Border = Color.FromArgb(0x2A, 0x32, 0x3E), Hover = Color.FromArgb(0x2A, 0x36, 0x4A);
        public override Color ToolStripDropDownBackground => Surface;
        public override Color MenuBorder => Border;
        public override Color MenuItemBorder => Hover;
        public override Color MenuItemSelected => Hover;
        public override Color ImageMarginGradientBegin => Surface;
        public override Color ImageMarginGradientMiddle => Surface;
        public override Color ImageMarginGradientEnd => Surface;
        public override Color SeparatorDark => Border;
        public override Color SeparatorLight => Border;
    }
}
