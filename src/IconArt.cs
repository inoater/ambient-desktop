using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;

namespace AmbientDesktop
{
    /// <summary>The app icon, drawn in code: a little window glowing on pure black.</summary>
    internal static class IconArt
    {
        public static Bitmap Render(int size)
        {
            var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.Clear(Color.Transparent);
                float s = size;

                RectangleF bg = new RectangleF(s * 0.02f, s * 0.02f, s * 0.96f, s * 0.96f);
                using (GraphicsPath bgPath = RoundRect(bg, s * 0.22f))
                {
                    using (var b = new SolidBrush(Color.Black)) g.FillPath(b, bgPath);
                    g.SetClip(bgPath);

                    RectangleF win = new RectangleF(s * 0.29f, s * 0.33f, s * 0.42f, s * 0.34f);
                    int layers = size <= 20 ? 4 : 9;
                    for (int i = layers; i >= 1; i--)
                    {
                        float grow = s * 0.26f * i / layers;
                        RectangleF rr = RectangleF.Inflate(win, grow, grow);
                        int alpha = (int)(255 * Math.Pow(1.0 - (i - 0.5) / layers, 1.6) * 0.55);
                        using (GraphicsPath p = RoundRect(rr, s * 0.05f + grow))
                        using (var lg = new LinearGradientBrush(rr, Color.FromArgb(alpha, 0, 200, 255),
                                   Color.FromArgb(alpha, 255, 60, 190), LinearGradientMode.ForwardDiagonal))
                        {
                            var blend = new ColorBlend(3);
                            blend.Colors = new[]
                            {
                                Color.FromArgb(alpha, 0, 210, 255),
                                Color.FromArgb(alpha, 130, 90, 255),
                                Color.FromArgb(alpha, 255, 70, 180)
                            };
                            blend.Positions = new[] { 0f, 0.5f, 1f };
                            lg.InterpolationColors = blend;
                            g.FillPath(lg, p);
                        }
                    }
                    g.ResetClip();

                    using (GraphicsPath wp = RoundRect(win, s * 0.05f))
                    {
                        using (var b = new SolidBrush(Color.FromArgb(255, 14, 14, 18))) g.FillPath(b, wp);
                        using (var pen = new Pen(Color.FromArgb(150, 255, 255, 255), Math.Max(1f, s * 0.022f))) g.DrawPath(pen, wp);
                    }
                    if (size >= 24)
                    {
                        // title bar hint
                        using (var pen = new Pen(Color.FromArgb(90, 255, 255, 255), Math.Max(1f, s * 0.018f)))
                            g.DrawLine(pen, win.Left + s * 0.04f, win.Top + win.Height * 0.28f, win.Right - s * 0.04f, win.Top + win.Height * 0.28f);
                    }
                }
            }
            return bmp;
        }

        public static Icon CreateIcon(int size)
        {
            using (Bitmap b = Render(size))
            {
                IntPtr h = b.GetHicon();
                return Icon.FromHandle(h);
            }
        }

        /// <summary>Writes a multi-resolution .ico (PNG-compressed frames).</summary>
        public static void WriteIco(string path)
        {
            int[] sizes = { 16, 20, 24, 32, 40, 48, 64, 128, 256 };
            var frames = new List<byte[]>();
            foreach (int sz in sizes)
                using (Bitmap b = Render(sz))
                using (var ms = new MemoryStream())
                {
                    b.Save(ms, ImageFormat.Png);
                    frames.Add(ms.ToArray());
                }

            using (var fs = new FileStream(path, FileMode.Create))
            using (var w = new BinaryWriter(fs))
            {
                w.Write((ushort)0); w.Write((ushort)1); w.Write((ushort)sizes.Length);
                int offset = 6 + 16 * sizes.Length;
                for (int i = 0; i < sizes.Length; i++)
                {
                    byte dim = (byte)(sizes[i] >= 256 ? 0 : sizes[i]);
                    w.Write(dim); w.Write(dim); w.Write((byte)0); w.Write((byte)0);
                    w.Write((ushort)1); w.Write((ushort)32);
                    w.Write(frames[i].Length); w.Write(offset);
                    offset += frames[i].Length;
                }
                foreach (byte[] f in frames) w.Write(f);
            }
        }

        public static GraphicsPath RoundRect(RectangleF r, float radius)
        {
            float d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
            var p = new GraphicsPath();
            if (d <= 0.5f) { p.AddRectangle(r); return p; }
            p.AddArc(r.Left, r.Top, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Top, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.Left, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }
    }
}
