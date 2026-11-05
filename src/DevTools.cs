using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.Runtime.InteropServices;

namespace AmbientDesktop
{
    /// <summary>
    /// Hidden developer tool for previewing the look without touching the real desktop:
    ///   AmbientDesktop.exe --render-test out.png [--intensity 0-3] [--size 0-3] image1.png x,y,w,h [image2.png x,y,w,h ...]
    /// Builds a fake 1920x1080 desktop with those "windows", runs the glow pipeline, and saves the composite.
    /// </summary>
    internal static class DevTools
    {
        /// <summary>AmbientDesktop.exe --capture-test outPrefix : saves what DXGI capture sees per monitor.</summary>
        public static int CaptureTest(string prefix)
        {
            int idx = 0;
            foreach (System.Windows.Forms.Screen s in System.Windows.Forms.Screen.AllScreens)
            {
                Rectangle b = s.Bounds;
                int level = Math.Max(2, Math.Min(6, (int)Math.Round(Math.Log(b.Height / 80.0, 2))));
                int lw = b.Width >> level, lh = b.Height >> level;
                var sw = System.Diagnostics.Stopwatch.StartNew();
                DxgiCapture c = DxgiCapture.TryCreate(b, level, lw, lh);
                Console.WriteLine("monitor " + idx + " " + b + ": dxgi=" + (c != null) + " init " + sw.ElapsedMilliseconds + "ms");
                if (c != null)
                {
                    byte[] px = new byte[lw * lh * 4];
                    int r = -1;
                    for (int t = 0; t < 150 && r != 1; t++) { r = c.Capture(px); if (r != 1) System.Threading.Thread.Sleep(20); }
                    int news = 0, same = 0;
                    double busy = 0;
                    for (int t = 0; t < 60; t++)
                    {
                        sw.Restart();
                        int q = c.Capture(px); if (q == 1) news++; else if (q == 0) same++;
                        busy += sw.Elapsed.TotalMilliseconds;
                        System.Threading.Thread.Sleep(33);
                    }
                    Console.WriteLine("  first=" + r + "  60 calls @30Hz: " + news + " new, " + same + " unchanged, avg " + (busy / 60).ToString("0.00") + " ms");
                    using (var bmp = new Bitmap(lw, lh, PixelFormat.Format32bppRgb))
                    {
                        BitmapData bd = bmp.LockBits(new Rectangle(0, 0, lw, lh), ImageLockMode.WriteOnly, PixelFormat.Format32bppRgb);
                        for (int y = 0; y < lh; y++) Marshal.Copy(px, y * lw * 4, new IntPtr(bd.Scan0.ToInt64() + (long)y * bd.Stride), lw * 4);
                        bmp.UnlockBits(bd);
                        bmp.Save(prefix + idx + ".png", ImageFormat.Png);
                    }
                    c.Dispose();
                }
                idx++;
            }
            return 0;
        }

        public static int RenderTest(string[] args)
        {
            string outPath = args[1];
            var settings = new Settings();
            var windows = new List<KeyValuePair<string, Rectangle>>();
            for (int i = 2; i < args.Length; i++)
            {
                if (args[i] == "--intensity") { settings.Intensity = int.Parse(args[++i], CultureInfo.InvariantCulture); continue; }
                if (args[i] == "--size") { settings.Size = int.Parse(args[++i], CultureInfo.InvariantCulture); continue; }
                string[] p = args[i + 1].Split(',');
                windows.Add(new KeyValuePair<string, Rectangle>(args[i], new Rectangle(
                    int.Parse(p[0], CultureInfo.InvariantCulture), int.Parse(p[1], CultureInfo.InvariantCulture),
                    int.Parse(p[2], CultureInfo.InvariantCulture), int.Parse(p[3], CultureInfo.InvariantCulture))));
                i++;
            }

            const int W = 1920, H = 1080;
            using (var desk = new Bitmap(W, H, PixelFormat.Format32bppRgb))
            {
                using (Graphics g = Graphics.FromImage(desk))
                {
                    g.Clear(Color.Black);
                    foreach (var w in windows)
                        using (Image img = Image.FromFile(w.Key)) g.DrawImage(img, w.Value);
                }

                // Put the fake screen in a DC so the engine can StretchBlt from it like the real screen.
                using (var screen = new Dib(W, H))
                {
                    BitmapData bd = desk.LockBits(new Rectangle(0, 0, W, H), ImageLockMode.ReadOnly, PixelFormat.Format32bppRgb);
                    byte[] buf = new byte[W * H * 4];
                    Marshal.Copy(bd.Scan0, buf, 0, buf.Length);
                    Marshal.Copy(buf, 0, screen.Bits, buf.Length);
                    desk.UnlockBits(bd);

                    var rects = new List<Native.RECT>();
                    foreach (var w in windows)
                        rects.Add(new Native.RECT(w.Value.Left, w.Value.Top, w.Value.Right, w.Value.Bottom));

                    var m = new MonitorState(new Rectangle(0, 0, W, H), 1f);
                    var engine = new AmbientEngine(new DesktopHost(), GlowParams.From(settings));
                    GlowParams gp = GlowParams.From(settings);
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    for (int f = 1; f <= 60; f++) engine.RenderOffline(m, screen.Dc, rects, gp, f * 0.1);
                    Console.WriteLine("avg ms/frame (offline, incl. upscale): " + (sw.Elapsed.TotalMilliseconds / 60).ToString("0.00"));

                    // Composite: glow layer, then the windows on top (what you'd actually see).
                    using (var result = new Bitmap(W, H, PixelFormat.Format32bppRgb))
                    {
                        BitmapData rd = result.LockBits(new Rectangle(0, 0, W, H), ImageLockMode.WriteOnly, PixelFormat.Format32bppRgb);
                        Marshal.Copy(m.Frame.Bits, buf, 0, buf.Length);
                        Marshal.Copy(buf, 0, rd.Scan0, buf.Length);
                        result.UnlockBits(rd);
                        using (Graphics g = Graphics.FromImage(result))
                            foreach (var w in windows)
                                using (Image img = Image.FromFile(w.Key)) g.DrawImage(img, w.Value);
                        result.Save(outPath, ImageFormat.Png);
                    }
                    m.Dispose();
                }
            }
            return 0;
        }
    }
}
