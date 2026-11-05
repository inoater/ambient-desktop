using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Microsoft.Win32;
using System.Windows.Forms;

namespace AmbientDesktop
{
    internal sealed class Settings
    {
        public bool Enabled = true;
        public int Intensity = 1;   // 0 Subtle, 1 Balanced, 2 Vivid, 3 Neon
        public int Size = 1;        // 0 Tight, 1 Normal, 2 Wide, 3 Huge
        public int Smoothness = 1;  // 0 Snappy, 1 Smooth, 2 Cinematic
        public int Performance = 1; // 0 Battery saver (15 fps), 1 Balanced (30), 2 Ultra smooth (60)
        public int Taskbar = 2;     // 0 Off, 1 Soft, 2 Bright, 3 Light bar
        public bool FirstRunDone;

        public static string Dir
        {
            get
            {
                return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AmbientDesktop");
            }
        }

        private static string FilePath { get { return Path.Combine(Dir, "settings.ini"); } }

        public static Settings Load()
        {
            var s = new Settings();
            try
            {
                if (!File.Exists(FilePath)) return s;
                var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (string raw in File.ReadAllLines(FilePath))
                {
                    int eq = raw.IndexOf('=');
                    if (eq <= 0) continue;
                    map[raw.Substring(0, eq).Trim()] = raw.Substring(eq + 1).Trim();
                }
                s.Enabled = GetBool(map, "Enabled", s.Enabled);
                s.Intensity = GetInt(map, "Intensity", s.Intensity, 0, 3);
                s.Size = GetInt(map, "Size", s.Size, 0, 3);
                s.Smoothness = GetInt(map, "Smoothness", s.Smoothness, 0, 2);
                s.Performance = GetInt(map, "Performance", s.Performance, 0, 2);
                s.Taskbar = GetInt(map, "Taskbar", s.Taskbar, 0, 3);
                s.FirstRunDone = GetBool(map, "FirstRunDone", false);
            }
            catch (Exception ex) { Log.Write("settings load failed: " + ex.Message); }
            return s;
        }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(Dir);
                var sb = new StringBuilder();
                sb.AppendLine("; Ambient Desktop settings (also editable from the tray menu)");
                sb.AppendLine("Enabled=" + Enabled);
                sb.AppendLine("Intensity=" + Intensity.ToString(CultureInfo.InvariantCulture));
                sb.AppendLine("Size=" + Size.ToString(CultureInfo.InvariantCulture));
                sb.AppendLine("Smoothness=" + Smoothness.ToString(CultureInfo.InvariantCulture));
                sb.AppendLine("Performance=" + Performance.ToString(CultureInfo.InvariantCulture));
                sb.AppendLine("Taskbar=" + Taskbar.ToString(CultureInfo.InvariantCulture));
                sb.AppendLine("FirstRunDone=" + FirstRunDone);
                File.WriteAllText(FilePath, sb.ToString());
            }
            catch (Exception ex) { Log.Write("settings save failed: " + ex.Message); }
        }

        private static bool GetBool(Dictionary<string, string> m, string k, bool def)
        {
            string v;
            bool b;
            return m.TryGetValue(k, out v) && bool.TryParse(v, out b) ? b : def;
        }

        private static int GetInt(Dictionary<string, string> m, string k, int def, int min, int max)
        {
            string v;
            int i;
            if (!m.TryGetValue(k, out v) || !int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out i)) return def;
            return Math.Max(min, Math.Min(max, i));
        }
    }

    /// <summary>Tuned look for each preset combination. Immutable, so the render thread can read it lock-free.</summary>
    internal sealed class GlowParams
    {
        public float Gain;        // brightness multiplier on the sampled light
        public float Saturation;  // >1 makes colors punchier
        public float LumCap;      // soft ceiling on glow brightness (linear light)
        public float SigmaPx;     // glow spread in px at 100% scaling
        public float Tau;         // color transition time constant, seconds
        public int Fps;
        public float TaskbarBoost;  // 0 = ambient taskbar off; otherwise extra gain for light under the taskbar
        public float TaskbarCap;    // brightness ceiling under the taskbar
        public bool TaskbarLightBar; // LED-strip mode: crisp edge colors instead of the soft blur

        public static GlowParams From(Settings s)
        {
            var p = new GlowParams();
            switch (s.Intensity)
            {
                case 0: p.Gain = 1.1f; p.Saturation = 1.10f; p.LumCap = 0.16f; break;
                case 2: p.Gain = 2.2f; p.Saturation = 1.45f; p.LumCap = 0.50f; break;
                case 3: p.Gain = 3.2f; p.Saturation = 1.85f; p.LumCap = 0.85f; break;
                default: p.Gain = 1.6f; p.Saturation = 1.35f; p.LumCap = 0.32f; break;
            }
            switch (s.Size)
            {
                case 0: p.SigmaPx = 50f; break;
                case 2: p.SigmaPx = 150f; break;
                case 3: p.SigmaPx = 230f; break;
                default: p.SigmaPx = 95f; break;
            }
            switch (s.Smoothness)
            {
                case 0: p.Tau = 0.08f; break;
                case 2: p.Tau = 0.80f; break;
                default: p.Tau = 0.30f; break;
            }
            switch (s.Taskbar)
            {
                case 0: p.TaskbarBoost = 0f; p.TaskbarCap = p.LumCap; break;
                case 1: p.TaskbarBoost = 1.0f; p.TaskbarCap = p.LumCap; break;
                case 3: p.TaskbarBoost = 1.5f; p.TaskbarCap = Math.Max(p.LumCap, 0.60f); p.TaskbarLightBar = true; break;
                default: p.TaskbarBoost = 1.9f; p.TaskbarCap = Math.Max(p.LumCap, 0.40f); break;
            }
            switch (s.Performance)
            {
                case 0: p.Fps = 15; break;
                case 2: p.Fps = 60; break;
                default: p.Fps = 30; break;
            }
            return p;
        }
    }

    internal static class Autostart
    {
        private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string ValueName = "AmbientDesktop";

        public static bool IsEnabled()
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(RunKey))
                    return k != null && k.GetValue(ValueName) != null;
            }
            catch { return false; }
        }

        public static void Set(bool on)
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.CreateSubKey(RunKey))
                {
                    if (on) k.SetValue(ValueName, "\"" + Application.ExecutablePath + "\"");
                    else k.DeleteValue(ValueName, false);
                }
            }
            catch (Exception ex) { Log.Write("autostart change failed: " + ex.Message); }
        }
    }

    internal static class Log
    {
        private static readonly object Gate = new object();

        public static string FilePath { get { return Path.Combine(Settings.Dir, "log.txt"); } }

        public static void Write(string msg)
        {
            try
            {
                lock (Gate)
                {
                    Directory.CreateDirectory(Settings.Dir);
                    var fi = new FileInfo(FilePath);
                    if (fi.Exists && fi.Length > 256 * 1024) fi.Delete();
                    File.AppendAllText(FilePath, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + "  " + msg + Environment.NewLine);
                }
            }
            catch { }
        }
    }
}
