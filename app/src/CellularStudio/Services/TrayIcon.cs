using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using CellularStudio.ViewModels;

namespace CellularStudio.Services;

/// <summary>
/// System tray icon: shows connection state as a small colored dot over the app icon,
/// a carrier/status tooltip, notifications, and the menu that owns "Exit".
/// </summary>
public sealed class TrayIcon : IDisposable
{
    private readonly NotifyIcon _icon;
    private readonly MainViewModel _vm;
    private readonly Action _show;
    private readonly Action _exit;
    private readonly Icon _baseIcon;
    private readonly Dictionary<string, Icon> _cache = [];
    private readonly ToolStripMenuItem _statusItem;
    private readonly ToolStripMenuItem _startupItem;
    private Action? _balloonClick;

    public TrayIcon(MainViewModel vm, Action show, Action exit)
    {
        _vm = vm;
        _show = show;
        _exit = exit;

        using (var s = System.Windows.Application.GetResourceStream(new Uri("pack://application:,,,/Assets/app.ico"))!.Stream)
            _baseIcon = new Icon(s, SystemInformation.SmallIconSize);

        var menu = new ContextMenuStrip
        {
            Font = new Font("Segoe UI", 9.5f),
            ShowImageMargin = false,
            Renderer = new ModernRenderer(),
            Padding = new Padding(2, 4, 2, 4),
        };
        _statusItem = new ToolStripMenuItem("Checking…") { Enabled = false };
        menu.Items.Add(_statusItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(Item("Open Cellular Studio", (_, _) => _show(), bold: true));
        menu.Items.Add(Item("Check balance", (_, _) => { _show(); _vm.CurrentPage = "Overview"; _vm.CheckBalanceCommand.Execute(null); }));
        menu.Items.Add(Item("New message", (_, _) => { _show(); _vm.NewMessageCommand.Execute(null); }));
        menu.Items.Add(Item("USSD", (_, _) => { _show(); _vm.CurrentPage = "USSD"; }));
        menu.Items.Add(new ToolStripSeparator());
        _startupItem = Item("Start with Windows", (_, _) => ToggleStartup());
        _startupItem.CheckOnClick = false;
        menu.Items.Add(_startupItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(Item("Exit", (_, _) => _exit()));
        menu.Opening += (_, _) =>
        {
            _startupItem.Checked = StartupRegistration.IsEnabled;
            _startupItem.Text = (_startupItem.Checked ? "✓  " : "      ") + "Start with Windows";
        };

        _icon = new NotifyIcon
        {
            Icon = _baseIcon,
            Text = "Cellular Studio",
            ContextMenuStrip = menu,
            Visible = true,
        };
        _icon.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) _show(); };
        _icon.BalloonTipClicked += (_, _) => { (_balloonClick ?? _show)(); _balloonClick = null; };

        _vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(MainViewModel.StatusKind) or nameof(MainViewModel.StatusText)
                or nameof(MainViewModel.CarrierName) or nameof(MainViewModel.NetworkLabel) or nameof(MainViewModel.UnreadTotal)
                or nameof(MainViewModel.Status))
                UpdateState();
        };
        UpdateState();
    }

    private static ToolStripMenuItem Item(string text, EventHandler onClick, bool bold = false)
    {
        var i = new ToolStripMenuItem(text, null, onClick) { Padding = new Padding(6, 3, 18, 3) };
        if (bold) i.Font = new Font(i.Font, System.Drawing.FontStyle.Bold);
        return i;
    }

    private void ToggleStartup() => _vm.RunAtStartup = !StartupRegistration.IsEnabled;

    private void UpdateState()
    {
        var unread = _vm.UnreadTotal;
        var status = $"{_vm.CarrierName} · {_vm.StatusText}";
        _statusItem.Text = status + (unread > 0 ? $" · {unread} unread" : "");

        var tip = $"Cellular Studio\n{_vm.CarrierName} — {_vm.NetworkLabel}";
        if (unread > 0) tip += $"\n{unread} unread message{(unread == 1 ? "" : "s")}";
        _icon.Text = tip.Length > 127 ? tip[..127] : tip;
        _icon.Icon = IconFor(_vm.StatusKind, unread > 0);
    }

    /// <summary>App icon with a status dot (green/amber/red), or an accent dot for unread messages.</summary>
    private Icon IconFor(string kind, bool unread)
    {
        var key = unread ? "unread" : kind;
        if (_cache.TryGetValue(key, out var cached)) return cached;

        var size = SystemInformation.SmallIconSize;
        using var bmp = new Bitmap(size.Width, size.Height);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.DrawIcon(_baseIcon, new Rectangle(0, 0, size.Width, size.Height));
            var color = key switch
            {
                "unread" => Color.FromArgb(0x3B, 0x5B, 0xDB),
                "ok" => Color.FromArgb(0x1F, 0x9D, 0x55),
                "warn" => Color.FromArgb(0xE0, 0x8A, 0x00),
                _ => Color.FromArgb(0xD6, 0x45, 0x45),
            };
            int d = (int)Math.Round(size.Width * 0.5);
            var r = new Rectangle(size.Width - d, size.Height - d, d - 1, d - 1);
            using var ring = new SolidBrush(Color.White);
            g.FillEllipse(ring, Rectangle.Inflate(r, 1, 1));
            using var fill = new SolidBrush(color);
            g.FillEllipse(fill, r);
        }
        var icon = Icon.FromHandle(bmp.GetHicon());
        _cache[key] = icon;
        return icon;
    }

    public void Notify(string title, string text, ToolTipIcon kind = ToolTipIcon.Info, Action? onClick = null)
    {
        _balloonClick = onClick;
        _icon.ShowBalloonTip(5000, title, string.IsNullOrWhiteSpace(text) ? " " : text, kind);
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
        foreach (var i in _cache.Values) i.Dispose();
        _baseIcon.Dispose();
    }

    /// <summary>Flat, light context menu closer to the Windows 11 look than the default renderer.</summary>
    private sealed class ModernRenderer : ToolStripProfessionalRenderer
    {
        public ModernRenderer() : base(new Colors()) { RoundedEdges = false; }

        protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
        {
            if (!e.Item.Selected || !e.Item.Enabled) return;
            var r = new Rectangle(4, 1, e.Item.Width - 8, e.Item.Height - 2);
            using var b = new SolidBrush(Color.FromArgb(0xEE, 0xF0, 0xF4));
            using var path = Rounded(r, 4);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            e.Graphics.FillPath(b, path);
        }

        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            e.TextColor = e.Item.Enabled ? Color.FromArgb(0x17, 0x1A, 0x21) : Color.FromArgb(0x8C, 0x93, 0xA0);
            base.OnRenderItemText(e);
        }

        private static GraphicsPath Rounded(Rectangle r, int rad)
        {
            var p = new GraphicsPath();
            int d = rad * 2;
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        private sealed class Colors : ProfessionalColorTable
        {
            public override Color ToolStripDropDownBackground => Color.White;
            public override Color MenuBorder => Color.FromArgb(0xD5, 0xD9, 0xE0);
            public override Color SeparatorDark => Color.FromArgb(0xE3, 0xE6, 0xEC);
            public override Color SeparatorLight => Color.White;
            public override Color ImageMarginGradientBegin => Color.White;
            public override Color ImageMarginGradientMiddle => Color.White;
            public override Color ImageMarginGradientEnd => Color.White;
        }
    }
}
