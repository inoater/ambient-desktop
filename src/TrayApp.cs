using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace AmbientDesktop
{
    /// <summary>Lives in the system tray, owns the host + engine, and handles all the UI.</summary>
    internal sealed class TrayApp : ApplicationContext
    {
        private readonly Settings _settings;
        private readonly DesktopHost _host;
        private readonly AmbientEngine _engine;
        private readonly NotifyIcon _tray;
        private readonly ContextMenuStrip _menu;
        private readonly System.Windows.Forms.Timer _health;
        private readonly Control _invoker;
        private readonly EventWaitHandle _showEvt, _exitEvt;
        private readonly ManualResetEvent _quit = new ManualResetEvent(false);
        private ToolStripMenuItem _toggleItem, _startupItem;
        private bool _locked, _disposed;
        private int _attachFailures;

        public TrayApp()
        {
            _settings = Settings.Load();
            _invoker = new Control();
            IntPtr forceHandle = _invoker.Handle;

            _host = new DesktopHost();
            _engine = new AmbientEngine(_host, GlowParams.From(_settings));

            _menu = BuildMenu();
            _tray = new NotifyIcon();
            _tray.Icon = IconArt.CreateIcon(Math.Max(16, SystemInformation.SmallIconSize.Width));
            _tray.Text = Program.AppName;
            _tray.ContextMenuStrip = _menu;
            _tray.MouseUp += OnTrayMouseUp;
            _tray.MouseDoubleClick += delegate(object s, MouseEventArgs e) { if (e.Button == MouseButtons.Left) SetEnabled(!_settings.Enabled); };
            _tray.Visible = true;

            SystemEvents.DisplaySettingsChanged += OnDisplayChanged;
            SystemEvents.SessionSwitch += OnSessionSwitch;

            _health = new System.Windows.Forms.Timer { Interval = 2000 };
            _health.Tick += OnHealthTick;
            _health.Start();

            if (_settings.Enabled) TurnOn();

            if (!_settings.FirstRunDone)
            {
                _settings.FirstRunDone = true;
                _settings.Save();
                _tray.ShowBalloonTip(6000, "Ambient Desktop is on",
                    "Your desktop is now pure black, lit by whatever's on screen. Right-click the tray icon to tune it - double-click to toggle.",
                    ToolTipIcon.None);
            }

            bool created;
            _showEvt = new EventWaitHandle(false, EventResetMode.AutoReset, Program.ShowEventName, out created);
            _exitEvt = new EventWaitHandle(false, EventResetMode.AutoReset, Program.ExitEventName, out created);
            var t = new Thread(SignalLoop) { IsBackground = true, Name = "Signals" };
            t.Start();
        }

        // -------------------------------------------------------------- on / off ---

        private void TurnOn()
        {
            if (!_host.IsAttached && !_host.Attach())
            {
                Log.Write("attach failed (desktop not ready yet?) - will retry");
            }
            _engine.Start();
        }

        private void TurnOff()
        {
            _engine.Stop();
            _host.Detach();
        }

        private void SetEnabled(bool on)
        {
            _settings.Enabled = on;
            _settings.Save();
            if (on) TurnOn(); else TurnOff();
            UpdateMenuState();
        }

        private void Reattach(string why)
        {
            Log.Write("re-attaching: " + why);
            if (!_host.Attach()) Log.Write("re-attach failed, will retry");
        }

        private void OnHealthTick(object sender, EventArgs e)
        {
            if (_settings.Enabled && !_locked)
            {
                if (!_host.CheckHealth())
                {
                    // Back off if Explorer isn't there (e.g. right after login).
                    if (_attachFailures < 5 || _attachFailures % 5 == 0) Reattach("health check");
                    _attachFailures = _host.IsAttached ? 0 : _attachFailures + 1;
                }
                else _attachFailures = 0;
            }
            string status = !_settings.Enabled ? "off"
                : string.Format("{0} fps, {1:0.0} ms/frame", _engine.PresentsPerSecond, _engine.AvgFrameMs);
            string text = Program.AppName + " - " + status;
            _tray.Text = text.Length > 63 ? text.Substring(0, 63) : text;
        }

        private void OnDisplayChanged(object sender, EventArgs e)
        {
            BeginInvoke(delegate
            {
                if (_settings.Enabled) Reattach("display settings changed");
            });
        }

        private void OnSessionSwitch(object sender, SessionSwitchEventArgs e)
        {
            BeginInvoke(delegate
            {
                if (e.Reason == SessionSwitchReason.SessionLock || e.Reason == SessionSwitchReason.ConsoleDisconnect)
                {
                    _locked = true;
                    _engine.Suspended = true;
                }
                else if (e.Reason == SessionSwitchReason.SessionUnlock || e.Reason == SessionSwitchReason.ConsoleConnect)
                {
                    _locked = false;
                    _engine.Suspended = false;
                }
            });
        }

        private void BeginInvoke(MethodInvoker action)
        {
            if (_disposed) return;
            try { _invoker.BeginInvoke(action); } catch (InvalidOperationException) { }
        }

        private void SignalLoop()
        {
            var handles = new WaitHandle[] { _showEvt, _exitEvt, _quit };
            while (true)
            {
                int i = WaitHandle.WaitAny(handles);
                if (i == 0)
                    BeginInvoke(delegate
                    {
                        _tray.ShowBalloonTip(3000, "Ambient Desktop is already running",
                            "It lives in your system tray. Right-click the icon for options.", ToolTipIcon.None);
                    });
                else if (i == 1) { BeginInvoke(ExitThread); return; }
                else return;
            }
        }

        // ------------------------------------------------------------------ menu ---

        private ContextMenuStrip BuildMenu()
        {
            var menu = new ContextMenuStrip();
            menu.Renderer = new DarkMenuRenderer();
            menu.ShowImageMargin = false;
            menu.ShowCheckMargin = true;
            menu.Font = new Font("Segoe UI", 9.5f);

            var header = new ToolStripLabel("AMBIENT DESKTOP");
            header.Font = new Font("Segoe UI Semibold", 8.5f);
            header.ForeColor = Color.FromArgb(120, 200, 255);
            header.Padding = new Padding(0, 4, 0, 2);
            menu.Items.Add(header);

            _toggleItem = new ToolStripMenuItem("Ambient glow");
            _toggleItem.Click += delegate { SetEnabled(!_settings.Enabled); };
            menu.Items.Add(_toggleItem);
            menu.Items.Add(new ToolStripSeparator());

            menu.Items.Add(Choice("Intensity", new[] { "Subtle", "Balanced", "Vivid", "Neon" },
                delegate { return _settings.Intensity; }, delegate(int v) { _settings.Intensity = v; }));
            menu.Items.Add(Choice("Glow size", new[] { "Tight", "Normal", "Wide", "Huge" },
                delegate { return _settings.Size; }, delegate(int v) { _settings.Size = v; }));
            menu.Items.Add(Choice("Color transitions", new[] { "Snappy", "Smooth", "Cinematic" },
                delegate { return _settings.Smoothness; }, delegate(int v) { _settings.Smoothness = v; }));
            menu.Items.Add(Choice("Performance", new[] { "Battery saver (15 fps)", "Balanced (30 fps)", "Ultra smooth (60 fps)" },
                delegate { return _settings.Performance; }, delegate(int v) { _settings.Performance = v; }));
            menu.Items.Add(Choice("Ambient taskbar", new[] { "Off", "Soft", "Bright", "Light bar" },
                delegate { return _settings.Taskbar; },
                delegate(int v)
                {
                    _settings.Taskbar = v;
                    if (v > 0 && !TransparencyEffectsOn())
                        _tray.ShowBalloonTip(6000, "Turn on transparency effects",
                            "The taskbar glow shines through the taskbar, so it needs Settings > Personalization > Colors > Transparency effects turned on.",
                            ToolTipIcon.None);
                }));
            menu.Items.Add(new ToolStripSeparator());

            _startupItem = new ToolStripMenuItem("Start with Windows");
            _startupItem.Click += delegate { Autostart.Set(!Autostart.IsEnabled()); UpdateMenuState(); };
            menu.Items.Add(_startupItem);

            var diag = new ToolStripMenuItem("Troubleshooting");
            var dump = new ToolStripMenuItem("Log detected windows");
            dump.Click += delegate { _engine.DumpWindowsOnce = true; Log.Write("host mode: " + _host.Mode); };
            var openLog = new ToolStripMenuItem("Open log");
            openLog.Click += delegate { try { Log.Write("log opened"); Process.Start("notepad.exe", "\"" + Log.FilePath + "\""); } catch { } };
            var reattach = new ToolStripMenuItem("Re-attach to desktop");
            reattach.Click += delegate { if (_settings.Enabled) Reattach("user request"); };
            diag.DropDownItems.AddRange(new ToolStripItem[] { reattach, dump, openLog });
            StyleDropDown(diag);
            menu.Items.Add(diag);

            menu.Items.Add(new ToolStripSeparator());
            var exit = new ToolStripMenuItem("Exit");
            exit.Click += delegate { ExitThread(); };
            menu.Items.Add(exit);

            menu.Opening += delegate { UpdateMenuState(); };
            UpdateMenuState();
            return menu;
        }

        private ToolStripMenuItem Choice(string title, string[] options, Func<int> get, Action<int> set)
        {
            var parent = new ToolStripMenuItem(title);
            for (int i = 0; i < options.Length; i++)
            {
                int index = i;
                var item = new ToolStripMenuItem(options[i]);
                item.Click += delegate
                {
                    set(index);
                    _settings.Save();
                    _engine.Params = GlowParams.From(_settings);
                    ForceRedraw();
                };
                parent.DropDownItems.Add(item);
            }
            parent.DropDownOpening += delegate
            {
                int cur = get();
                for (int i = 0; i < parent.DropDownItems.Count; i++)
                    ((ToolStripMenuItem)parent.DropDownItems[i]).Checked = i == cur;
            };
            StyleDropDown(parent);
            return parent;
        }

        private static bool TransparencyEffectsOn()
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                {
                    object v = k == null ? null : k.GetValue("EnableTransparency");
                    return !(v is int) || (int)v != 0;
                }
            }
            catch { return true; }
        }

        private static void StyleDropDown(ToolStripMenuItem item)
        {
            var dd = item.DropDown as ToolStripDropDownMenu;
            if (dd == null) return;
            dd.Renderer = new DarkMenuRenderer();
            dd.ShowImageMargin = false;
            dd.ShowCheckMargin = true;
        }

        private void ForceRedraw()
        {
            lock (_host.Sync)
                foreach (MonitorState m in _host.Monitors) m.Dirty = true;
        }

        private void UpdateMenuState()
        {
            if (_toggleItem == null) return;
            _toggleItem.Checked = _settings.Enabled;
            _toggleItem.Text = _settings.Enabled ? "Ambient glow: on" : "Ambient glow: off (wallpaper shown)";
            _startupItem.Checked = Autostart.IsEnabled();
        }

        private void OnTrayMouseUp(object sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left) return;
            // Left click opens the menu too.
            MethodInfo mi = typeof(NotifyIcon).GetMethod("ShowContextMenu", BindingFlags.Instance | BindingFlags.NonPublic);
            if (mi != null) mi.Invoke(_tray, null);
        }

        // --------------------------------------------------------------- cleanup ---

        protected override void Dispose(bool disposing)
        {
            if (disposing && !_disposed)
            {
                _disposed = true;
                SystemEvents.DisplaySettingsChanged -= OnDisplayChanged;
                SystemEvents.SessionSwitch -= OnSessionSwitch;
                _health.Stop();
                _health.Dispose();
                _engine.Stop();
                _host.Dispose();
                _tray.Visible = false;
                _tray.Dispose();
                _menu.Dispose();
                _quit.Set();
                _invoker.Dispose();
            }
            base.Dispose(disposing);
        }
    }

    // ------------------------------------------------------------------ styling ---

    internal sealed class DarkMenuRenderer : ToolStripProfessionalRenderer
    {
        private static readonly Color Text = Color.FromArgb(236, 236, 242);
        private static readonly Color TextDim = Color.FromArgb(120, 120, 132);

        public DarkMenuRenderer() : base(new DarkColors()) { RoundedEdges = false; }

        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            if (!(e.Item is ToolStripLabel)) e.TextColor = e.Item.Enabled ? Text : TextDim;
            base.OnRenderItemText(e);
        }

        protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
        {
            e.ArrowColor = Color.FromArgb(170, 170, 185);
            base.OnRenderArrow(e);
        }

        protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            Rectangle r = e.ImageRectangle;
            int d = Math.Max(6, Math.Min(r.Width, r.Height) / 2);
            var dot = new Rectangle(r.X + (r.Width - d) / 2, r.Y + (r.Height - d) / 2, d, d);
            var glow = Rectangle.Inflate(dot, d / 2, d / 2);
            using (var path = new GraphicsPath())
            {
                path.AddEllipse(glow);
                using (var pg = new PathGradientBrush(path))
                {
                    pg.CenterColor = Color.FromArgb(120, 120, 120, 255);
                    pg.SurroundColors = new[] { Color.FromArgb(0, 120, 120, 255) };
                    g.FillEllipse(pg, glow);
                }
            }
            using (var lg = new LinearGradientBrush(dot, Color.FromArgb(0, 210, 255), Color.FromArgb(255, 70, 190), 45f))
                g.FillEllipse(lg, dot);
        }

        private sealed class DarkColors : ProfessionalColorTable
        {
            private static readonly Color Bg = Color.FromArgb(20, 20, 24);
            private static readonly Color Hover = Color.FromArgb(42, 42, 52);
            private static readonly Color Border = Color.FromArgb(58, 58, 70);

            public override Color ToolStripDropDownBackground { get { return Bg; } }
            public override Color ImageMarginGradientBegin { get { return Bg; } }
            public override Color ImageMarginGradientMiddle { get { return Bg; } }
            public override Color ImageMarginGradientEnd { get { return Bg; } }
            public override Color MenuBorder { get { return Border; } }
            public override Color MenuItemBorder { get { return Hover; } }
            public override Color MenuItemSelected { get { return Hover; } }
            public override Color MenuItemSelectedGradientBegin { get { return Hover; } }
            public override Color MenuItemSelectedGradientEnd { get { return Hover; } }
            public override Color MenuItemPressedGradientBegin { get { return Hover; } }
            public override Color MenuItemPressedGradientMiddle { get { return Hover; } }
            public override Color MenuItemPressedGradientEnd { get { return Hover; } }
            public override Color SeparatorDark { get { return Border; } }
            public override Color SeparatorLight { get { return Bg; } }
            public override Color CheckBackground { get { return Bg; } }
            public override Color CheckSelectedBackground { get { return Hover; } }
            public override Color CheckPressedBackground { get { return Hover; } }
        }
    }
}
