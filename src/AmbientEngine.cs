using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace AmbientDesktop
{
    /// <summary>
    /// The light engine. Each frame, per monitor:
    ///   1. find every real app window on screen
    ///   2. grab a tiny (1/12 scale) snapshot of the screen
    ///   3. keep only the cells that are fully *inside* windows - that's our light source
    ///   4. blur that outward (in linear light) so it bleeds onto the black desktop around each window
    ///   5. grade it (gain, saturation, soft brightness ceiling), ease it toward the new colors over time
    ///   6. black out window interiors, upscale with dithering, blit
    /// Untouched desktop stays pure #000000.
    /// </summary>
    internal sealed class AmbientEngine
    {
        private readonly DesktopHost _host;
        public volatile GlowParams Params;
        public volatile bool Suspended;
        public volatile bool DumpWindowsOnce;
        public static readonly bool Debug = Environment.GetEnvironmentVariable("AMBIENT_DEBUG") == "1";

        private volatile bool _running;
        private Thread _thread;

        // stats (read by UI)
        public volatile float AvgFrameMs;
        public volatile int PresentsPerSecond;

        private static readonly float[] SrgbToLin = new float[256];
        private const int LinLutSize = 16384;
        private static readonly float[] LinToSrgb = new float[LinLutSize + 1];
        private static readonly float[] Bayer = new float[64];

        private readonly uint _ourPid = (uint)Process.GetCurrentProcess().Id;
        private readonly List<Native.RECT> _windows = new List<Native.RECT>(64);
        private readonly List<Native.RECT> _taskbars = new List<Native.RECT>(4);
        private readonly StringBuilder _cls = new StringBuilder(256);
        private readonly Native.EnumWindowsProc _enumProc;
        private StringBuilder _dump;
        private readonly ParallelOptions _parallel;

        private static readonly HashSet<string> IgnoredClasses = new HashSet<string>(StringComparer.Ordinal)
        {
            "Progman", "WorkerW", "Windows.UI.Core.CoreWindow", "XamlExplorerHostIslandWindow",
            "TopLevelWindowForOverflowXamlIsland", "NotifyIconOverflowWindow", "Xaml_WindowedPopupClass",
            "ForegroundStaging", "MultitaskingViewFrame", "TaskSwitcherWnd", "EdgeUiInputTopWndClass",
            "Shell_InputSwitchTopLevelWindow", "Windows.Internal.Shell.TabProxyWindow", "SysShadow",
            "tooltips_class32", "#32768"
        };

        static AmbientEngine()
        {
            for (int i = 0; i < 256; i++)
            {
                double c = i / 255.0;
                SrgbToLin[i] = (float)(c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4));
            }
            for (int i = 0; i <= LinLutSize; i++)
            {
                double l = (double)i / LinLutSize;
                double s = l <= 0.0031308 ? l * 12.92 : 1.055 * Math.Pow(l, 1 / 2.4) - 0.055;
                LinToSrgb[i] = (float)(s * 255.0);
            }
            int[] b8 =
            {
                 0, 32,  8, 40,  2, 34, 10, 42,
                48, 16, 56, 24, 50, 18, 58, 26,
                12, 44,  4, 36, 14, 46,  6, 38,
                60, 28, 52, 20, 62, 30, 54, 22,
                 3, 35, 11, 43,  1, 33,  9, 41,
                51, 19, 59, 27, 49, 17, 57, 25,
                15, 47,  7, 39, 13, 45,  5, 37,
                63, 31, 55, 23, 61, 29, 53, 21
            };
            for (int i = 0; i < 64; i++) Bayer[i] = (b8[i] + 0.5f) / 64f;
        }

        public AmbientEngine(DesktopHost host, GlowParams p)
        {
            _host = host;
            Params = p;
            _enumProc = EnumCallback;
            DumpWindowsOnce = Debug;
            _parallel = new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, Math.Min(6, Environment.ProcessorCount / 2)) };
        }

        public bool IsRunning { get { return _running; } }

        public void Start()
        {
            if (_running) return;
            _running = true;
            _thread = new Thread(Loop) { IsBackground = true, Name = "AmbientEngine", Priority = ThreadPriority.BelowNormal };
            _thread.Start();
        }

        public void Stop()
        {
            _running = false;
            Thread t = _thread;
            if (t != null) t.Join(3000);
            _thread = null;
        }

        private void Loop()
        {
            Native.timeBeginPeriod(1);
            var sw = Stopwatch.StartNew();
            double statWindowStart = 0, statMs = 0;
            int statFrames = 0, statPresents = 0;
            try
            {
                while (_running)
                {
                    double start = sw.Elapsed.TotalSeconds;
                    GlowParams p = Params;
                    int presents = 0;
                    if (!Suspended)
                    {
                        try { presents = Frame(p, start); }
                        catch (Exception ex)
                        {
                            Log.Write("frame error: " + ex);
                            Thread.Sleep(500);
                        }
                    }
                    double end = sw.Elapsed.TotalSeconds;
                    statMs += (end - start) * 1000; statFrames++; statPresents += presents;
                    if (end - statWindowStart >= 2.0)
                    {
                        AvgFrameMs = (float)(statMs / Math.Max(1, statFrames));
                        PresentsPerSecond = (int)Math.Round(statPresents / (end - statWindowStart));
                        statWindowStart = end; statMs = 0; statFrames = 0; statPresents = 0;
                    }

                    int sleep = Suspended ? 250 : (int)((1.0 / p.Fps - (end - start)) * 1000);
                    Thread.Sleep(Math.Max(1, sleep));
                }
            }
            finally { Native.timeEndPeriod(1); }
        }

        /// <summary>Dev tool: run the pipeline against an in-memory "screen" DC with a fixed window list.</summary>
        internal void RenderOffline(MonitorState m, IntPtr screenDc, List<Native.RECT> windows, GlowParams p, double now)
        {
            _windows.Clear();
            _windows.AddRange(windows);
            _taskbars.Clear();
            m.Dirty = true;
            ProcessMonitor(m, p, screenDc, now);
        }

        // debug-only stage timings (ms, accumulated)
        private double _tEnum, _tCap, _tGrade, _tUp; private int _tFrames;
        private static readonly Stopwatch Clock = Stopwatch.StartNew();
        private static double Ms() { return Clock.Elapsed.TotalMilliseconds; }

        private int Frame(GlowParams p, double now)
        {
            double t0 = Ms();
            CollectWindows();
            _tEnum += Ms() - t0;
            if (Debug && ++_tFrames % 60 == 0)
            {
                Log.Write(string.Format("debug timings per frame: enum {0:0.00} ms, capture {1:0.00}, grade {2:0.00}, upscale+present {3:0.00}",
                    _tEnum / 60, _tCap / 60, _tGrade / 60, _tUp / 60));
                _tEnum = _tCap = _tGrade = _tUp = 0;
            }
            int presents = 0;
            lock (_host.Sync)
            {
                if (_host.Monitors.Count == 0) return 0;
                IntPtr screen = Native.GetDC(IntPtr.Zero);
                if (screen == IntPtr.Zero) return 0;
                try
                {
                    foreach (MonitorState m in _host.Monitors)
                        if (ProcessMonitor(m, p, screen, now)) presents++;
                }
                finally { Native.ReleaseDC(IntPtr.Zero, screen); }
            }
            return presents;
        }

        // ---------------------------------------------------------------- windows ---

        private void CollectWindows()
        {
            _windows.Clear();
            _taskbars.Clear();
            if (DumpWindowsOnce) _dump = new StringBuilder("window scan:\r\n");
            Native.EnumWindows(_enumProc, IntPtr.Zero);
            if (_dump != null)
            {
                Log.Write(_dump.ToString());
                _dump = null;
                DumpWindowsOnce = false;
            }
        }

        private bool EnumCallback(IntPtr h, IntPtr lParam)
        {
            if (!Native.IsWindowVisible(h) || Native.IsIconic(h)) return true;

            uint pid;
            Native.GetWindowThreadProcessId(h, out pid);
            if (pid == _ourPid) return true;

            string cls = Native.ClassOf(h, _cls);
            Native.RECT r;
            if (cls == "Shell_TrayWnd" || cls == "Shell_SecondaryTrayWnd")
            {
                if (Native.GetWindowRect(h, out r)) _taskbars.Add(r);
                return true;
            }
            if (IgnoredClasses.Contains(cls)) return true;

            int ex = Native.GetWindowLong(h, Native.GWL_EXSTYLE);
            if ((ex & (Native.WS_EX_TRANSPARENT | Native.WS_EX_TOOLWINDOW)) != 0) return true;

            int cloaked;
            if (Native.DwmGetWindowAttribute(h, Native.DWMWA_CLOAKED, out cloaked, 4) == 0 && cloaked != 0) return true;
            if (Native.GetWindowTextLength(h) == 0) return true;

            if ((ex & Native.WS_EX_LAYERED) != 0)
            {
                uint key, flags; byte alpha;
                if (Native.GetLayeredWindowAttributes(h, out key, out alpha, out flags) &&
                    (flags & Native.LWA_ALPHA) != 0 && alpha < 40) return true;
            }

            if (Native.DwmGetWindowAttribute(h, Native.DWMWA_EXTENDED_FRAME_BOUNDS, out r, 16) != 0 &&
                !Native.GetWindowRect(h, out r)) return true;
            if (r.Width < 48 || r.Height < 48) return true;

            _windows.Add(r);
            if (_dump != null)
                _dump.AppendFormat("  [{0},{1} {2}x{3}] {4} \"{5}\"\r\n", r.Left, r.Top, r.Width, r.Height, cls, Native.TitleOf(h));
            return true;
        }

        // -------------------------------------------------------------- per monitor ---

        /// <returns>true if a new frame was presented</returns>
        private unsafe bool ProcessMonitor(MonitorState m, GlowParams p, IntPtr screen, double now)
        {
            int lw = m.LW, lh = m.LH, n = m.N, cell = m.Cell, W = m.W, H = m.H;
            int ox = m.Bounds.X, oy = m.Bounds.Y;
            byte[] sample = m.Sample, cover = m.Cover, occ = m.Occluded, bar = m.Bar;
            Array.Clear(sample, 0, n);
            Array.Clear(cover, 0, n);
            Array.Clear(occ, 0, n);
            Array.Clear(bar, 0, n);

            int hash = 17;
            bool anySample = false;
            for (int k = 0; k < _windows.Count; k++)
            {
                Native.RECT r = _windows[k];
                int l = r.Left - ox, t = r.Top - oy, rr = r.Right - ox, b = r.Bottom - oy;
                if (rr <= 0 || b <= 0 || l >= W || t >= H) continue;
                unchecked { hash = (((hash * 31 + l) * 31 + t) * 31 + rr) * 31 + b; }
                anySample |= MarkInside(sample, lw, lh, cell, l, t, rr, b);
                MarkInside(cover, lw, lh, cell, l + cell, t + cell, rr - cell, b - cell);
                MarkCenters(occ, lw, lh, cell, l, t, rr, b);
            }
            for (int k = 0; k < _taskbars.Count; k++)
            {
                Native.RECT r = _taskbars[k];
                MarkCenters(bar, lw, lh, cell, r.Left - ox, r.Top - oy, r.Right - ox, r.Bottom - oy);
            }

            // Fully covered (fullscreen video, game, maximized window)? Nothing to see - skip the work.
            // With the ambient taskbar on, a translucent taskbar next to a maximized window still counts as visible.
            bool taskbarAmbient = p.TaskbarBoost > 0;
            bool anyVisible = false;
            for (int i = 0; i < n; i++)
                if (occ[i] == 0 && (bar[i] == 0 || taskbarAmbient)) { anyVisible = true; break; }
            if (!anyVisible && !m.NeedsPresent) return false;

            if (hash != m.MaskHash)
            {
                m.MaskHash = hash;
                m.FastUntil = now + 0.25; // windows moved: follow them quickly instead of trailing
            }

            float[] R = m.R, G = m.G, B = m.B;
            if (anySample)
            {
                if (m.Capture == null) m.Capture = new MonitorCapture(m);
                double tc = Ms();
                int capResult = m.Capture.Capture(screen, now);
                _tCap += Ms() - tc;
                if (capResult < 0) return false; // nothing usable (locked, mode switch...)
                // 0 = screen unchanged: reuse the previous pixels, but the window mask may still have moved.
                byte[] px = m.LowPixels;
                for (int i = 0; i < n; i++)
                {
                    if (sample[i] != 0)
                    {
                        int o = i << 2;
                        B[i] = SrgbToLin[px[o]];
                        G[i] = SrgbToLin[px[o + 1]];
                        R[i] = SrgbToLin[px[o + 2]];
                    }
                    else { R[i] = 0; G[i] = 0; B[i] = 0; }
                }

                float sigmaCells = p.SigmaPx * m.DpiScale / cell;
                int radius = Math.Max(1, (int)Math.Round(Math.Sqrt(sigmaCells * sigmaCells + 0.25) - 0.5));
                if (p.TaskbarLightBar)
                {
                    // Keep a crisp copy of the edge light (only smeared sideways a little) for the LED-strip look.
                    Array.Copy(R, m.HR, n); Array.Copy(G, m.HG, n); Array.Copy(B, m.HB, n);
                    BoxH(m.HR, m.Tmp, lw, lh, 1); BoxH(m.Tmp, m.HR, lw, lh, 1);
                    BoxH(m.HG, m.Tmp, lw, lh, 1); BoxH(m.Tmp, m.HG, lw, lh, 1);
                    BoxH(m.HB, m.Tmp, lw, lh, 1); BoxH(m.Tmp, m.HB, lw, lh, 1);
                }
                Blur(R, m.Tmp, lw, lh, radius);
                Blur(G, m.Tmp, lw, lh, radius);
                Blur(B, m.Tmp, lw, lh, radius);
            }
            else
            {
                Array.Clear(R, 0, n); Array.Clear(G, 0, n); Array.Clear(B, 0, n);
            }

            double tg = Ms();
            double dt = m.LastTime > 0 ? Math.Min(1.0, now - m.LastTime) : 1.0;
            m.LastTime = now;
            double tau = now < m.FastUntil ? Math.Min(p.Tau, 0.045) : p.Tau;
            float a = (float)(1.0 - Math.Exp(-dt / tau));

            float gain = p.Gain, sat = p.Saturation, cap = p.LumCap;
            float[] CR = m.CR, CG = m.CG, CB = m.CB, DR = m.DR, DG = m.DG, DB = m.DB, PR = m.PR, PG = m.PG, PB = m.PB;
            int dx0 = int.MaxValue, dx1 = -1, dy0 = int.MaxValue, dy1 = -1; // dirty cell bounds
            for (int cy = 0; cy < lh; cy++)
            {
                int row = cy * lw;
                for (int cx = 0; cx < lw; cx++)
                {
                    int i = row + cx;
                    float cr, cg, cb;
                    if (cover[i] != 0 || (bar[i] != 0 && !taskbarAmbient))
                    {
                        cr = 0; cg = 0; cb = 0; // window interiors (and the taskbar when its glow is off) stay black
                    }
                    else
                    {
                        bool onBar = taskbarAmbient && bar[i] != 0;
                        float gi = onBar ? gain * p.TaskbarBoost : gain;
                        float ci = onBar ? p.TaskbarCap : cap;
                        float r = R[i] * gi, g = G[i] * gi, b = B[i] * gi;
                        int src = onBar && p.TaskbarLightBar && anySample ? NearestSampleAbove(sample, lw, lh, cx, cy) : -1;
                        if (src >= 0)
                        {
                            // Light bar: show the actual colors along the window edge right above this spot.
                            r = Math.Max(r, m.HR[src] * gi); g = Math.Max(g, m.HG[src] * gi); b = Math.Max(b, m.HB[src] * gi);
                        }
                        float L = 0.2126f * r + 0.7152f * g + 0.0722f * b;
                        if (L > 1e-7f)
                        {
                            r = L + (r - L) * sat; if (r < 0) r = 0;
                            g = L + (g - L) * sat; if (g < 0) g = 0;
                            b = L + (b - L) * sat; if (b < 0) b = 0;
                            float k = ci * (1f - (float)Math.Exp(-L / ci)) / L; // soft ceiling
                            r *= k; g *= k; b *= k;
                        }
                        else { r = 0; g = 0; b = 0; }
                        cr = CR[i] + (r - CR[i]) * a;
                        cg = CG[i] + (g - CG[i]) * a;
                        cb = CB[i] + (b - CB[i]) * a;
                    }
                    CR[i] = cr; CG[i] = cg; CB[i] = cb;

                    float dr = ToSrgb(cr), dg = ToSrgb(cg), db = ToSrgb(cb);
                    DR[i] = dr; DG[i] = dg; DB[i] = db;
                    // Compare against what's actually on screen, so slow fades still land.
                    // Cells buried under windows (incl. all neighbours) can't show, so they never force a redraw.
                    if ((Math.Abs(dr - PR[i]) > 0.25f || Math.Abs(dg - PG[i]) > 0.25f || Math.Abs(db - PB[i]) > 0.25f)
                        && !Buried(occ, lw, lh, cx, cy))
                    {
                        if (cx < dx0) dx0 = cx;
                        if (cx > dx1) dx1 = cx;
                        if (cy < dy0) dy0 = cy;
                        if (cy > dy1) dy1 = cy;
                    }
                }
            }

            _tGrade += Ms() - tg;
            bool force = m.Dirty || m.NeedsPresent;
            if (force) { dx0 = 0; dy0 = 0; dx1 = lw - 1; dy1 = lh - 1; }
            if (dx1 < 0) return false;

            // A cell influences pixels up to one cell away (bilinear), so grow the dirty box by one.
            dx0 = Math.Max(0, dx0 - 1); dy0 = Math.Max(0, dy0 - 1);
            dx1 = Math.Min(lw - 1, dx1 + 1); dy1 = Math.Min(lh - 1, dy1 + 1);
            for (int cy = dy0; cy <= dy1; cy++)
            {
                int row = cy * lw;
                for (int cx = dx0; cx <= dx1; cx++) { int i = row + cx; PR[i] = DR[i]; PG[i] = DG[i]; PB[i] = DB[i]; }
            }
            int px0 = dx0 == 0 ? 0 : m.SpanStart[dx0 - 1];
            int px1 = dx1 >= lw - 1 ? W : m.SpanEnd[dx1 + 1];
            int py0 = dy0 == 0 ? 0 : m.RowStart[dy0 - 1];
            int py1 = dy1 >= lh - 1 ? H : m.RowEnd[dy1 + 1];

            double tu = Ms();
            lock (m.FrameLock)
            {
                Upscale(m, px0, px1, py0, py1);
                if (Debug && now - m.DebugLast > 2)
                {
                    m.DebugLast = now;
                    Log.Write("debug " + m.Bounds + ": present " + px0 + "," + py0 + " " + (px1 - px0) + "x" + (py1 - py0) + (force ? " (forced)" : ""));
                }
                if (m.Surface != null)
                {
                    if (force) m.Surface.NeedsPresent = false;
                    m.Surface.Present(px0, py0, px1 - px0, py1 - py0);
                }
            }
            _tUp += Ms() - tu;
            m.Dirty = false;
            return true;
        }

        /// <summary>Closest window-interior cell straight above (or, for a top taskbar, below) this one.</summary>
        private static int NearestSampleAbove(byte[] sample, int lw, int lh, int cx, int cy)
        {
            for (int d = 1; d <= 8; d++)
            {
                int y = cy - d;
                if (y >= 0 && sample[y * lw + cx] != 0) return y * lw + cx;
            }
            for (int d = 1; d <= 8; d++)
            {
                int y = cy + d;
                if (y < lh && sample[y * lw + cx] != 0) return y * lw + cx;
            }
            return -1;
        }

        /// <summary>True if this cell and all 8 neighbours sit under windows (its bilinear footprint is invisible).</summary>
        private static bool Buried(byte[] occ, int lw, int lh, int cx, int cy)
        {
            for (int y = cy - 1; y <= cy + 1; y++)
            {
                if (y < 0 || y >= lh) continue;
                int row = y * lw;
                for (int x = cx - 1; x <= cx + 1; x++)
                {
                    if (x < 0 || x >= lw) continue;
                    if (occ[row + x] == 0) return false;
                }
            }
            return true;
        }

        private static float ToSrgb(float lin)
        {
            if (lin <= 0f) return 0f;
            if (lin >= 1f) return 255f;
            float v = LinToSrgb[(int)(lin * LinLutSize + 0.5f)];
            return v < 0.35f ? 0f : v; // keep the far field truly #000000
        }

        /// <summary>Mark grid cells whose whole area lies inside [l,r) x [t,b).</summary>
        private static bool MarkInside(byte[] mask, int lw, int lh, int cell, int l, int t, int r, int b)
        {
            int cx0 = CeilDiv(l, cell), cx1 = FloorDiv(r, cell) - 1;
            int cy0 = CeilDiv(t, cell), cy1 = FloorDiv(b, cell) - 1;
            if (cx0 < 0) cx0 = 0; if (cy0 < 0) cy0 = 0;
            if (cx1 > lw - 1) cx1 = lw - 1; if (cy1 > lh - 1) cy1 = lh - 1;
            if (cx0 > cx1 || cy0 > cy1) return false;
            for (int y = cy0; y <= cy1; y++)
            {
                int row = y * lw;
                for (int x = cx0; x <= cx1; x++) mask[row + x] = 1;
            }
            return true;
        }

        /// <summary>Mark grid cells whose center lies inside [l,r) x [t,b).</summary>
        private static void MarkCenters(byte[] mask, int lw, int lh, int cell, int l, int t, int r, int b)
        {
            int half = cell / 2;
            int cx0 = CeilDiv(l - half, cell), cx1 = CeilDiv(r - half, cell) - 1;
            int cy0 = CeilDiv(t - half, cell), cy1 = CeilDiv(b - half, cell) - 1;
            if (cx0 < 0) cx0 = 0; if (cy0 < 0) cy0 = 0;
            if (cx1 > lw - 1) cx1 = lw - 1; if (cy1 > lh - 1) cy1 = lh - 1;
            for (int y = cy0; y <= cy1; y++)
            {
                int row = y * lw;
                for (int x = cx0; x <= cx1; x++) mask[row + x] = 1;
            }
        }

        private static int FloorDiv(int a, int b) { int q = a / b; if ((a % b != 0) && ((a < 0) != (b < 0))) q--; return q; }
        private static int CeilDiv(int a, int b) { return -FloorDiv(-a, b); }

        // ------------------------------------------------------------------- blur ---

        /// <summary>Three box passes per axis ~= gaussian. Zero outside the screen.</summary>
        private static void Blur(float[] a, float[] tmp, int w, int h, int r)
        {
            BoxH(a, tmp, w, h, r); BoxH(tmp, a, w, h, r); BoxH(a, tmp, w, h, r);
            BoxV(tmp, a, w, h, r); BoxV(a, tmp, w, h, r); BoxV(tmp, a, w, h, r);
        }

        private static void BoxH(float[] src, float[] dst, int w, int h, int r)
        {
            float inv = 1f / (2 * r + 1);
            for (int y = 0; y < h; y++)
            {
                int o = y * w;
                float acc = 0;
                for (int i = 0; i <= r && i < w; i++) acc += src[o + i];
                for (int x = 0; x < w; x++)
                {
                    dst[o + x] = acc * inv;
                    int add = x + r + 1; if (add < w) acc += src[o + add];
                    int rem = x - r; if (rem >= 0) acc -= src[o + rem];
                }
            }
        }

        private static void BoxV(float[] src, float[] dst, int w, int h, int r)
        {
            float inv = 1f / (2 * r + 1);
            for (int x = 0; x < w; x++)
            {
                float acc = 0;
                for (int i = 0; i <= r && i < h; i++) acc += src[i * w + x];
                for (int y = 0; y < h; y++)
                {
                    dst[y * w + x] = acc * inv;
                    int add = y + r + 1; if (add < h) acc += src[add * w + x];
                    int rem = y - r; if (rem >= 0) acc -= src[rem * w + x];
                }
            }
        }

        // ---------------------------------------------------------------- upscale ---

        /// <summary>Bilinear upscale of the display grid into the pixel rect [x0,x1) x [y0,y1), with 8x8 ordered
        /// dithering so the soft gradients don't band on black. Spans that are black on both ends are just zero-filled.</summary>
        private unsafe void Upscale(MonitorState m, int x0, int x1, int y0, int y1)
        {
            int W = m.W, lw = m.LW;
            IntPtr bitsPtr = m.Frame.Bits;
            float[] DR = m.DR, DG = m.DG, DB = m.DB;
            int[] X0 = m.X0, X1 = m.X1, Y0 = m.Y0, Y1 = m.Y1, spanEnd = m.SpanEnd;
            float[] FX = m.FX, FY = m.FY;

            int rows = y1 - y0;
            if (rows <= 0 || x1 <= x0) return;
            // Small regions (e.g. just the taskbar band) aren't worth waking the thread pool for.
            bool small = (long)rows * (x1 - x0) < 250000;
            int chunks = small ? 1 : Math.Max(1, Math.Min(rows / 16, _parallel.MaxDegreeOfParallelism * 3));
            int rowsPer = (rows + chunks - 1) / chunks;

            Action<int> body = c =>
            {
                uint* bits = (uint*)bitsPtr;
                float[] tr = new float[lw], tg = new float[lw], tb = new float[lw];
                int ys = y0 + c * rowsPer, ye = Math.Min(y1, ys + rowsPer);
                for (int y = ys; y < ye; y++)
                {
                    int b0 = Y0[y] * lw, b1 = Y1[y] * lw;
                    float fy = FY[y];
                    float mx = 0;
                    for (int i = 0; i < lw; i++)
                    {
                        float r0 = DR[b0 + i], g0 = DG[b0 + i], bb0 = DB[b0 + i];
                        float r = r0 + (DR[b1 + i] - r0) * fy;
                        float g = g0 + (DG[b1 + i] - g0) * fy;
                        float b = bb0 + (DB[b1 + i] - bb0) * fy;
                        tr[i] = r; tg[i] = g; tb[i] = b;
                        mx += r + g + b;
                    }

                    uint* row = bits + (long)y * W;
                    if (mx <= 0f)
                    {
                        for (int x = x0; x < x1; x++) row[x] = 0;
                        continue;
                    }

                    int dy = (y & 7) << 3;
                    int px = x0;
                    while (px < x1)
                    {
                        int i0 = X0[px], i1 = X1[px];
                        int pe = spanEnd[i0];
                        if (pe > x1) pe = x1;
                        float r0 = tr[i0], g0 = tg[i0], b0c = tb[i0];
                        float rd = tr[i1] - r0, gd = tg[i1] - g0, bd = tb[i1] - b0c;
                        if (r0 + g0 + b0c + tr[i1] + tg[i1] + tb[i1] <= 0f)
                        {
                            for (; px < pe; px++) row[px] = 0;
                            continue;
                        }
                        for (; px < pe; px++)
                        {
                            float fx = FX[px];
                            float d = Bayer[dy + (px & 7)];
                            int ri = (int)(r0 + rd * fx + d), gi = (int)(g0 + gd * fx + d), bi = (int)(b0c + bd * fx + d);
                            if (ri > 255) ri = 255;
                            if (gi > 255) gi = 255;
                            if (bi > 255) bi = 255;
                            row[px] = 0xFF000000u | (uint)((ri << 16) | (gi << 8) | bi);
                        }
                    }
                }
            };
            if (chunks == 1) body(0);
            else Parallel.For(0, chunks, _parallel, body);
        }
    }
}
