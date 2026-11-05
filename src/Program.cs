using System;
using System.Threading;
using System.Windows.Forms;

namespace AmbientDesktop
{
    internal static class Program
    {
        public const string AppName = "Ambient Desktop";
        public const string ShowEventName = @"Local\AmbientDesktop.Show";
        public const string ExitEventName = @"Local\AmbientDesktop.Exit";

        [STAThread]
        private static int Main(string[] args)
        {
            Native.EnableDpiAwareness();

            if (args.Length >= 2 && args[0] == "--write-icon")
            {
                IconArt.WriteIco(args[1]);
                return 0;
            }
            if (args.Length >= 2 && args[0] == "--capture-test")
                return DevTools.CaptureTest(args[1]);
            if (args.Length >= 2 && args[0] == "--render-test")
                return DevTools.RenderTest(args);
            if (args.Length >= 1 && args[0] == "--exit")
            {
                Signal(ExitEventName);
                return 0;
            }

            bool created;
            using (var mutex = new Mutex(true, @"Local\AmbientDesktop.SingleInstance", out created))
            {
                if (!created)
                {
                    Signal(ShowEventName);
                    return 0;
                }

                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
                Application.ThreadException += delegate(object s, ThreadExceptionEventArgs e) { Log.Write("UI error: " + e.Exception); };
                AppDomain.CurrentDomain.UnhandledException += delegate(object s, UnhandledExceptionEventArgs e) { Log.Write("fatal: " + e.ExceptionObject); };

                Log.Write("starting " + AppName + " on " + Environment.OSVersion.VersionString);
                using (var app = new TrayApp()) Application.Run(app);
                Log.Write("exited cleanly");
                GC.KeepAlive(mutex);
            }
            return 0;
        }

        private static void Signal(string name)
        {
            try
            {
                using (EventWaitHandle e = EventWaitHandle.OpenExisting(name)) e.Set();
            }
            catch (WaitHandleCannotBeOpenedException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
