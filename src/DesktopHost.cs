using System;
using System.Collections.Generic;
using System.Drawing;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace AmbientDesktop
{
    /// <summary>
    /// Plants one glow surface per monitor *inside* the Windows desktop, underneath the desktop icons
    /// and on top of the wallpaper - the same layer live-wallpaper apps use.
    ///
    /// Windows 11 24H2+:  Progman { SHELLDLL_DefView (icons), [our surfaces], WorkerW (wallpaper) }
    /// Older Windows:     WorkerW (wallpaper) { [our surfaces] } sitting behind the icon WorkerW
    /// </summary>
    internal sealed class DesktopHost : IDisposable
    {
        public readonly object Sync = new object();
        public readonly List<MonitorState> Monitors = new List<MonitorState>();
        public string Mode = "detached";

        private IntPtr _parent, _defView;
        private Rectangle[] _screens = new Rectangle[0];

        public bool IsAttached { get { return _parent != IntPtr.Zero; } }

        public bool Attach()
        {
            lock (Sync)
            {
                DetachInternal();

                IntPtr progman = Native.FindWindow("Progman", null);
                if (progman == IntPtr.Zero) return false;

                // Ask Explorer to spawn the wallpaper WorkerW (no-op if it already exists).
                IntPtr ignored;
                Native.SendMessageTimeout(progman, 0x052C, new IntPtr(0xD), new IntPtr(1), Native.SMTO_NORMAL, 1000, out ignored);
                Native.SendMessageTimeout(progman, 0x052C, IntPtr.Zero, IntPtr.Zero, Native.SMTO_NORMAL, 1000, out ignored);

                IntPtr defView = Native.FindWindowEx(progman, IntPtr.Zero, "SHELLDLL_DefView", null);
                IntPtr innerWorker = Native.FindWindowEx(progman, IntPtr.Zero, "WorkerW", null);
                bool layered;

                if (defView != IntPtr.Zero && innerWorker != IntPtr.Zero)
                {
                    _parent = progman; _defView = defView; layered = true; Mode = "progman (24H2+)";
                }
                else
                {
                    IntPtr legacyWorker = IntPtr.Zero;
                    Native.EnumWindows(delegate(IntPtr h, IntPtr l)
                    {
                        if (Native.FindWindowEx(h, IntPtr.Zero, "SHELLDLL_DefView", null) != IntPtr.Zero)
                            legacyWorker = Native.FindWindowEx(IntPtr.Zero, h, "WorkerW", null);
                        return true;
                    }, IntPtr.Zero);

                    if (legacyWorker != IntPtr.Zero)
                    {
                        _parent = legacyWorker; _defView = IntPtr.Zero; layered = false; Mode = "workerw (legacy)";
                    }
                    else if (defView != IntPtr.Zero)
                    {
                        _parent = progman; _defView = defView; layered = true; Mode = "progman (fallback)";
                    }
                    else return false;
                }

                Native.RECT pr;
                Native.GetWindowRect(_parent, out pr);
                Screen[] screens = Screen.AllScreens;
                _screens = new Rectangle[screens.Length];
                for (int i = 0; i < screens.Length; i++)
                {
                    Rectangle b = screens[i].Bounds;
                    _screens[i] = b;
                    var surface = new GlowSurface(_parent, b, new Point(pr.Left, pr.Top), layered);
                    if (_defView != IntPtr.Zero) PlaceBelowIcons(surface.Handle);
                    Monitors.Add(new MonitorState(surface, Native.GetDpiScale(b)));
                }
                Log.Write("attached: mode=" + Mode + ", monitors=" + screens.Length + ", parent origin=" + pr.Left + "," + pr.Top);
                return true;
            }
        }

        public void Detach()
        {
            lock (Sync) DetachInternal();
        }

        private void DetachInternal()
        {
            IntPtr parent = _parent;
            foreach (MonitorState m in Monitors) m.Dispose();
            Monitors.Clear();
            _parent = IntPtr.Zero;
            _defView = IntPtr.Zero;
            Mode = "detached";
            if (parent != IntPtr.Zero && Native.IsWindow(parent))
                Native.RedrawWindow(parent, IntPtr.Zero, IntPtr.Zero,
                    Native.RDW_INVALIDATE | Native.RDW_ERASE | Native.RDW_ALLCHILDREN | Native.RDW_UPDATENOW);
        }

        private void PlaceBelowIcons(IntPtr hwnd)
        {
            Native.SetWindowPos(hwnd, _defView, 0, 0, 0, 0, Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
        }

        /// <summary>Called every couple of seconds. Returns false if we need a full re-attach
        /// (Explorer restarted, monitors changed...). Also quietly repairs z-order drift.</summary>
        public bool CheckHealth()
        {
            lock (Sync)
            {
                if (_parent == IntPtr.Zero || !Native.IsWindow(_parent)) return false;
                foreach (MonitorState m in Monitors)
                    if (!Native.IsWindow(m.Surface.Handle)) return false;

                Screen[] screens = Screen.AllScreens;
                if (screens.Length != _screens.Length) return false;
                for (int i = 0; i < screens.Length; i++)
                    if (screens[i].Bounds != _screens[i]) return false;

                if (_defView != IntPtr.Zero)
                {
                    if (Native.FindWindowEx(_parent, IntPtr.Zero, "SHELLDLL_DefView", null) != _defView) return false;
                    // Our surfaces should sit directly under the icon layer.
                    IntPtr next = Native.GetWindow(_defView, Native.GW_HWNDNEXT);
                    bool ok = true;
                    for (int i = 0; i < Monitors.Count; i++)
                    {
                        if (!IsOurs(next)) { ok = false; break; }
                        next = Native.GetWindow(next, Native.GW_HWNDNEXT);
                    }
                    if (!ok)
                    {
                        Log.Write("z-order drift detected, repairing");
                        for (int i = Monitors.Count - 1; i >= 0; i--) PlaceBelowIcons(Monitors[i].Surface.Handle);
                    }
                }
                return true;
            }
        }

        private bool IsOurs(IntPtr h)
        {
            foreach (MonitorState m in Monitors) if (m.Surface.Handle == h) return true;
            return false;
        }

        public void Dispose() { Detach(); }
    }

    /// <summary>A borderless child window that just shows a pre-rendered frame.</summary>
    internal sealed class GlowSurface : NativeWindow, IDisposable
    {
        public readonly Rectangle Bounds;
        public readonly Dib Frame;
        public readonly object FrameLock = new object();
        public volatile bool NeedsPresent = true;

        public GlowSurface(IntPtr parent, Rectangle monitor, Point parentOrigin, bool layered)
        {
            Bounds = monitor;
            Frame = new Dib(monitor.Width, monitor.Height); // DIB sections start zeroed = pure black

            var cp = new CreateParams();
            cp.Caption = "AmbientDesktopSurface";
            cp.Style = Native.WS_CHILD | Native.WS_VISIBLE | Native.WS_CLIPSIBLINGS;
            cp.ExStyle = Native.WS_EX_NOACTIVATE | Native.WS_EX_TRANSPARENT | (layered ? Native.WS_EX_LAYERED : 0);
            cp.X = monitor.X - parentOrigin.X;
            cp.Y = monitor.Y - parentOrigin.Y;
            cp.Width = monitor.Width;
            cp.Height = monitor.Height;
            cp.Parent = parent;
            CreateHandle(cp);
            if (layered) Native.SetLayeredWindowAttributes(Handle, 0, 255, Native.LWA_ALPHA);
        }

        /// <summary>Blit part of the current frame. Caller holds FrameLock.</summary>
        public void Present(int x, int y, int w, int h)
        {
            IntPtr dc = Native.GetDC(Handle);
            if (dc == IntPtr.Zero) return;
            Native.BitBlt(dc, x, y, w, h, Frame.Dc, x, y, Native.SRCCOPY);
            Native.ReleaseDC(Handle, dc);
        }

        protected override void WndProc(ref Message m)
        {
            switch (m.Msg)
            {
                case Native.WM_ERASEBKGND:
                    m.Result = new IntPtr(1);
                    return;
                case Native.WM_PAINT:
                    {
                        Native.PAINTSTRUCT ps;
                        IntPtr hdc = Native.BeginPaint(Handle, out ps);
                        if (Monitor.TryEnter(FrameLock, 8))
                        {
                            try { Native.BitBlt(hdc, 0, 0, Frame.Width, Frame.Height, Frame.Dc, 0, 0, Native.SRCCOPY); }
                            finally { Monitor.Exit(FrameLock); }
                        }
                        else NeedsPresent = true;
                        Native.EndPaint(Handle, ref ps);
                        return;
                    }
                case Native.WM_NCHITTEST:
                    m.Result = new IntPtr(Native.HTTRANSPARENT);
                    return;
                case Native.WM_MOUSEACTIVATE:
                    m.Result = new IntPtr(Native.MA_NOACTIVATE);
                    return;
            }
            base.WndProc(ref m);
        }

        public void Dispose()
        {
            if (Handle != IntPtr.Zero) DestroyHandle();
            lock (FrameLock) Frame.Dispose();
        }
    }

    /// <summary>Per-monitor render state: a coarse light-field grid that gets blurred and upscaled.</summary>
    internal sealed class MonitorState : IDisposable
    {
        public readonly GlowSurface Surface; // null when rendering offline (render test)
        public readonly Dib Frame;
        public readonly object FrameLock;
        public readonly Rectangle Bounds;
        public readonly float DpiScale;
        public readonly int W, H, Cell, MipLevel, LW, LH, N;
        public readonly Dib Low;                       // GDI fallback target
        public readonly byte[] LowPixels;              // latest downscaled capture (BGRA)
        public readonly bool ForceGdi;
        public MonitorCapture Capture;

        public readonly float[] R, G, B, Tmp;          // sampled / blurred light (linear)
        public readonly float[] HR, HG, HB;            // crisp edge light for the taskbar light bar
        public readonly float[] CR, CG, CB;            // smoothed state (linear)
        public readonly float[] DR, DG, DB;            // display values (sRGB, 0..255)
        public readonly byte[] Sample, Cover, Occluded, Bar; // Bar = cells under the taskbar
        public readonly int[] X0, X1, Y0, Y1;
        public readonly float[] FX, FY;
        public readonly int[] SpanStart, SpanEnd, RowStart, RowEnd; // pixel range whose left/top sample is cell i
        public readonly float[] PR, PG, PB;            // values currently on screen

        public int MaskHash;
        public double FastUntil, LastTime, DebugLast;
        public bool Dirty = true;

        public MonitorState(GlowSurface surface, float dpiScale)
            : this(surface.Bounds, dpiScale, surface, surface.Frame, surface.FrameLock) { }

        public MonitorState(Rectangle bounds, float dpiScale)
            : this(bounds, dpiScale, null, new Dib(bounds.Width, bounds.Height), new object()) { }

        private MonitorState(Rectangle bounds, float dpiScale, GlowSurface surface, Dib frame, object frameLock)
        {
            Surface = surface;
            Frame = frame;
            FrameLock = frameLock;
            Bounds = bounds;
            DpiScale = dpiScale;
            W = Bounds.Width;
            H = Bounds.Height;
            // Grid cell = power of two so the GPU's mip chain can do the downscale (16px at 1080p, 32px at 4K).
            MipLevel = Math.Max(2, Math.Min(6, (int)Math.Round(Math.Log(H / 80.0, 2))));
            Cell = 1 << MipLevel;
            LW = Math.Max(1, W >> MipLevel);
            LH = Math.Max(1, H >> MipLevel);
            N = LW * LH;
            LowPixels = new byte[N * 4];
            ForceGdi = surface == null;

            Low = new Dib(LW, LH);
            Native.SetStretchBltMode(Low.Dc, Native.HALFTONE);
            Native.SetBrushOrgEx(Low.Dc, 0, 0, IntPtr.Zero);

            R = new float[N]; G = new float[N]; B = new float[N]; Tmp = new float[N];
            HR = new float[N]; HG = new float[N]; HB = new float[N];
            CR = new float[N]; CG = new float[N]; CB = new float[N];
            DR = new float[N]; DG = new float[N]; DB = new float[N];
            Sample = new byte[N]; Cover = new byte[N]; Occluded = new byte[N]; Bar = new byte[N];

            PR = new float[N]; PG = new float[N]; PB = new float[N];
            BuildAxis(W, LW, out X0, out X1, out FX);
            BuildAxis(H, LH, out Y0, out Y1, out FY);
            BuildSpans(X0, LW, out SpanStart, out SpanEnd);
            BuildSpans(Y0, LH, out RowStart, out RowEnd);
        }

        private static void BuildSpans(int[] i0, int low, out int[] start, out int[] end)
        {
            start = new int[low]; end = new int[low];
            for (int i = 0; i < low; i++) { start[i] = int.MaxValue; end[i] = 0; }
            for (int p = 0; p < i0.Length; p++)
            {
                int i = i0[p];
                if (p < start[i]) start[i] = p;
                end[i] = p + 1;
            }
            // A cell that never starts a span (can't happen with cell >= 4, but be safe) collapses onto its neighbor's edge.
            for (int i = 0; i < low; i++)
                if (start[i] == int.MaxValue) { start[i] = i > 0 ? end[i - 1] : 0; end[i] = start[i]; }
        }

        private void BuildAxis(int full, int low, out int[] i0, out int[] i1, out float[] f)
        {
            i0 = new int[full]; i1 = new int[full]; f = new float[full];
            for (int p = 0; p < full; p++)
            {
                float u = (p + 0.5f) / Cell - 0.5f;
                int i = (int)Math.Floor(u);
                float fr = u - i;
                if (i < 0) { i = 0; fr = 0; }
                if (i >= low - 1) { i = low - 1; fr = 0; }
                i0[p] = i;
                i1[p] = Math.Min(i + 1, low - 1);
                f[p] = fr;
            }
        }

        public bool NeedsPresent { get { return Surface != null && Surface.NeedsPresent; } }

        public void Dispose()
        {
            if (Capture != null) Capture.Dispose();
            if (Surface != null) Surface.Dispose(); else Frame.Dispose();
            Low.Dispose();
        }
    }
}
