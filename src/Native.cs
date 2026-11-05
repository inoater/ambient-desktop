using System;
using System.Runtime.InteropServices;
using System.Text;

namespace AmbientDesktop
{
    /// <summary>All the Win32 plumbing in one place.</summary>
    internal static class Native
    {
        public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential)]
        public struct RECT
        {
            public int Left, Top, Right, Bottom;
            public RECT(int l, int t, int r, int b) { Left = l; Top = t; Right = r; Bottom = b; }
            public int Width { get { return Right - Left; } }
            public int Height { get { return Bottom - Top; } }
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct BITMAPINFOHEADER
        {
            public int biSize;
            public int biWidth;
            public int biHeight;
            public short biPlanes;
            public short biBitCount;
            public int biCompression;
            public int biSizeImage;
            public int biXPelsPerMeter;
            public int biYPelsPerMeter;
            public int biClrUsed;
            public int biClrImportant;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct PAINTSTRUCT
        {
            public IntPtr hdc;
            public int fErase;
            public RECT rcPaint;
            public int fRestore;
            public int fIncUpdate;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)]
            public byte[] rgbReserved;
        }

        // ---- constants -------------------------------------------------------------
        public const int GWL_STYLE = -16;
        public const int GWL_EXSTYLE = -20;

        public const int WS_CHILD = 0x40000000;
        public const int WS_VISIBLE = 0x10000000;
        public const int WS_CLIPSIBLINGS = 0x04000000;
        public const int WS_CAPTION = 0x00C00000;

        public const int WS_EX_TOOLWINDOW = 0x00000080;
        public const int WS_EX_TRANSPARENT = 0x00000020;
        public const int WS_EX_LAYERED = 0x00080000;
        public const int WS_EX_NOACTIVATE = 0x08000000;

        public const uint LWA_ALPHA = 0x2;

        public const uint SWP_NOSIZE = 0x0001;
        public const uint SWP_NOMOVE = 0x0002;
        public const uint SWP_NOACTIVATE = 0x0010;

        public const uint GW_HWNDNEXT = 2;

        public const int WM_PAINT = 0x000F;
        public const int WM_ERASEBKGND = 0x0014;
        public const int WM_NCHITTEST = 0x0084;
        public const int WM_MOUSEACTIVATE = 0x0021;
        public const int HTTRANSPARENT = -1;
        public const int MA_NOACTIVATE = 3;

        public const uint SMTO_NORMAL = 0x0000;

        public const int SRCCOPY = 0x00CC0020;
        public const int HALFTONE = 4;

        public const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;
        public const int DWMWA_CLOAKED = 14;

        public const uint RDW_INVALIDATE = 0x0001;
        public const uint RDW_ERASE = 0x0004;
        public const uint RDW_ALLCHILDREN = 0x0080;
        public const uint RDW_UPDATENOW = 0x0100;

        // ---- user32 ----------------------------------------------------------------
        [DllImport("user32.dll")]
        public static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern IntPtr FindWindow(string lpClassName, string lpWindowName);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern IntPtr FindWindowEx(IntPtr parent, IntPtr childAfter, string className, string windowTitle);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern IntPtr SendMessageTimeout(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam,
            uint fuFlags, uint uTimeout, out IntPtr lpdwResult);

        [DllImport("user32.dll")]
        public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint flags);

        [DllImport("user32.dll")]
        public static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);

        [DllImport("user32.dll")]
        public static extern IntPtr GetWindow(IntPtr hWnd, uint cmd);

        [DllImport("user32.dll")]
        public static extern bool IsWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern bool IsIconic(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern int GetWindowLong(IntPtr hWnd, int index);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern int GetClassName(IntPtr hWnd, StringBuilder buf, int max);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern int GetWindowText(IntPtr hWnd, StringBuilder buf, int max);

        [DllImport("user32.dll")]
        public static extern int GetWindowTextLength(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);

        [DllImport("user32.dll")]
        public static extern bool SetLayeredWindowAttributes(IntPtr hWnd, uint key, byte alpha, uint flags);

        [DllImport("user32.dll")]
        public static extern bool GetLayeredWindowAttributes(IntPtr hWnd, out uint key, out byte alpha, out uint flags);

        [DllImport("user32.dll")]
        public static extern IntPtr GetDC(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern int ReleaseDC(IntPtr hWnd, IntPtr hdc);

        [DllImport("user32.dll")]
        public static extern IntPtr BeginPaint(IntPtr hWnd, out PAINTSTRUCT ps);

        [DllImport("user32.dll")]
        public static extern bool EndPaint(IntPtr hWnd, ref PAINTSTRUCT ps);

        [DllImport("user32.dll")]
        public static extern bool RedrawWindow(IntPtr hWnd, IntPtr rcUpdate, IntPtr hrgnUpdate, uint flags);

        [DllImport("user32.dll")]
        public static extern IntPtr MonitorFromRect(ref RECT rc, uint flags);

        [DllImport("user32.dll")]
        private static extern bool SetProcessDpiAwarenessContext(IntPtr value);

        [DllImport("user32.dll")]
        private static extern bool SetProcessDPIAware();

        [DllImport("shcore.dll")]
        private static extern int GetDpiForMonitor(IntPtr hmonitor, int dpiType, out uint dpiX, out uint dpiY);

        // ---- gdi32 -----------------------------------------------------------------
        [DllImport("gdi32.dll")]
        public static extern IntPtr CreateCompatibleDC(IntPtr hdc);

        [DllImport("gdi32.dll")]
        public static extern bool DeleteDC(IntPtr hdc);

        [DllImport("gdi32.dll")]
        public static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);

        [DllImport("gdi32.dll")]
        public static extern bool DeleteObject(IntPtr obj);

        [DllImport("gdi32.dll")]
        public static extern IntPtr CreateDIBSection(IntPtr hdc, ref BITMAPINFOHEADER bmi, uint usage,
            out IntPtr bits, IntPtr section, uint offset);

        [DllImport("gdi32.dll")]
        public static extern bool BitBlt(IntPtr dst, int x, int y, int w, int h, IntPtr src, int sx, int sy, int rop);

        [DllImport("gdi32.dll")]
        public static extern bool StretchBlt(IntPtr dst, int x, int y, int w, int h,
            IntPtr src, int sx, int sy, int sw, int sh, int rop);

        [DllImport("gdi32.dll")]
        public static extern int SetStretchBltMode(IntPtr hdc, int mode);

        [DllImport("gdi32.dll")]
        public static extern bool SetBrushOrgEx(IntPtr hdc, int x, int y, IntPtr prev);

        [DllImport("gdi32.dll")]
        public static extern bool GdiFlush();

        // ---- dwm / winmm -----------------------------------------------------------
        [DllImport("dwmapi.dll")]
        public static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out RECT value, int size);

        [DllImport("dwmapi.dll")]
        public static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out int value, int size);

        [DllImport("winmm.dll")]
        public static extern uint timeBeginPeriod(uint ms);

        [DllImport("winmm.dll")]
        public static extern uint timeEndPeriod(uint ms);

        // ---- helpers ---------------------------------------------------------------
        public static void EnableDpiAwareness()
        {
            // The manifest already asks for Per-Monitor V2; this is a belt-and-braces fallback.
            try { if (SetProcessDpiAwarenessContext(new IntPtr(-4))) return; }
            catch (EntryPointNotFoundException) { }
            try { SetProcessDPIAware(); }
            catch (EntryPointNotFoundException) { }
        }

        public static float GetDpiScale(System.Drawing.Rectangle bounds)
        {
            try
            {
                RECT r = new RECT(bounds.Left, bounds.Top, bounds.Right, bounds.Bottom);
                IntPtr mon = MonitorFromRect(ref r, 2 /* MONITOR_DEFAULTTONEAREST */);
                uint dx, dy;
                if (GetDpiForMonitor(mon, 0, out dx, out dy) == 0 && dx > 0) return dx / 96f;
            }
            catch (DllNotFoundException) { }
            catch (EntryPointNotFoundException) { }
            return 1f;
        }

        public static string ClassOf(IntPtr hWnd, StringBuilder buf)
        {
            buf.Length = 0;
            GetClassName(hWnd, buf, buf.Capacity);
            return buf.ToString();
        }

        public static string TitleOf(IntPtr hWnd)
        {
            var sb = new StringBuilder(256);
            GetWindowText(hWnd, sb, sb.Capacity);
            return sb.ToString();
        }
    }

    /// <summary>A 32-bit top-down DIB section selected into its own memory DC.</summary>
    internal sealed class Dib : IDisposable
    {
        public readonly int Width, Height;
        public IntPtr Dc { get; private set; }
        public IntPtr Bits { get; private set; }
        private IntPtr _bitmap, _old;

        public Dib(int width, int height)
        {
            Width = width;
            Height = height;
            var bi = new Native.BITMAPINFOHEADER();
            bi.biSize = Marshal.SizeOf(typeof(Native.BITMAPINFOHEADER));
            bi.biWidth = width;
            bi.biHeight = -height; // top-down
            bi.biPlanes = 1;
            bi.biBitCount = 32;

            IntPtr screen = Native.GetDC(IntPtr.Zero);
            Dc = Native.CreateCompatibleDC(screen);
            Native.ReleaseDC(IntPtr.Zero, screen);

            IntPtr bits;
            _bitmap = Native.CreateDIBSection(Dc, ref bi, 0, out bits, IntPtr.Zero, 0);
            if (_bitmap == IntPtr.Zero) throw new InvalidOperationException("CreateDIBSection failed");
            Bits = bits;
            _old = Native.SelectObject(Dc, _bitmap);
        }

        public void Dispose()
        {
            if (Dc == IntPtr.Zero) return;
            Native.SelectObject(Dc, _old);
            Native.DeleteObject(_bitmap);
            Native.DeleteDC(Dc);
            Dc = IntPtr.Zero;
            Bits = IntPtr.Zero;
        }
    }
}
