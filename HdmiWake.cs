// HdmiWake - brings external monitors back after logon, unlock, resume and display-on.
// Windows sometimes drops a connected external monitor from the active layout after resume
// (laptop-only mode). This app remembers which external outputs were active before sleep or
// display-off and re-activates them, then re-drives the link with a display mode set.
// Build with build.ps1 (uses the .NET Framework csc.exe shipped with Windows; C# 5).
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

[assembly: System.Reflection.AssemblyTitle("HdmiWake")]
[assembly: System.Reflection.AssemblyVersion("1.4.0.0")]

namespace HdmiWake
{
    static class Program
    {
        [STAThread]
        static void Main(string[] args)
        {
            Settings.Load();

            // "HdmiWake.exe --now" runs one wake sequence (activating every connected external monitor) and exits.
            if (Array.IndexOf(args, "--now") >= 0)
            {
                Waker.Run("command line", true);
                return;
            }
            // "HdmiWake.exe --save" records the current layout (modes, positions, main display) and exits.
            if (Array.IndexOf(args, "--save") >= 0)
            {
                Displays.Snapshot("command line");
                return;
            }

            bool created;
            using (var mutex = new Mutex(true, "Local\\HdmiWake.SingleInstance", out created))
            {
                if (!created) return;
                Native.SetProcessDPIAware(); // crisp tray icon and menu at display scaling above 100%
                Application.EnableVisualStyles();
                Application.Run(new HostForm());
            }
        }
    }

    static class Settings
    {
        public static readonly string Dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HdmiWake");
        static readonly string FilePath = Path.Combine(Dir, "settings.ini");

        public static int DelayMs = 2000; // wait after the event before acting
        public static int Retries = 5;    // passes to wait for a monitor that is slow to reappear

        public static void Load()
        {
            try
            {
                Directory.CreateDirectory(Dir);
                if (File.Exists(FilePath))
                {
                    foreach (var raw in File.ReadAllLines(FilePath))
                    {
                        var parts = raw.Split(new[] { '=' }, 2);
                        if (parts.Length != 2) continue;
                        var key = parts[0].Trim().ToLowerInvariant();
                        var val = parts[1].Trim();
                        if (key == "delayms") int.TryParse(val, out DelayMs);
                        else if (key == "retries") int.TryParse(val, out Retries);
                    }
                }
                Save();
            }
            catch { }
        }

        public static void Save()
        {
            try
            {
                File.WriteAllLines(FilePath, new[] { "DelayMs=" + DelayMs, "Retries=" + Retries });
            }
            catch { }
        }
    }

    static class Log
    {
        public static readonly string FilePath = Path.Combine(Settings.Dir, "HdmiWake.log");
        static readonly object Sync = new object();

        public static void Write(string msg)
        {
            lock (Sync)
            {
                try
                {
                    var fi = new FileInfo(FilePath);
                    if (fi.Exists && fi.Length > 512 * 1024) fi.Delete();
                    File.AppendAllText(FilePath, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + msg + Environment.NewLine);
                }
                catch { }
            }
        }
    }

    static class Displays
    {
        // External targets that were active before the last sleep / display-off; null until recorded.
        static HashSet<string> snapshot;
        static readonly object Sync = new object();

        static string AdapterKey(Native.LUID a) { return a.HighPart + "." + a.LowPart; }
        static string TargetKey(Native.PATH p) { return AdapterKey(p.targetInfo.adapterId) + ":" + p.targetInfo.id; }
        static string SourceKey(Native.PATH p) { return AdapterKey(p.sourceInfo.adapterId) + ":" + p.sourceInfo.id; }

        static bool IsExternal(uint tech)
        {
            // HD15 (VGA), DVI, HDMI, DisplayPort external, UDI external
            return tech == 0 || tech == 4 || tech == 5 || tech == 10 || tech == 12;
        }

        public static void Snapshot(string reason)
        {
            Native.PATH[] paths; IntPtr modes; uint numModes;
            if (!Query(Native.QDC_ONLY_ACTIVE_PATHS, out paths, out modes, out numModes)) return;
            Marshal.FreeHGlobal(modes);
            var set = new HashSet<string>();
            var saved = ModeStore.Load();
            foreach (var p in paths)
            {
                if (IsExternal(p.targetInfo.outputTechnology)) set.Add(TargetKey(p));

                // Remember every display's resolution, refresh rate and position (the main display
                // sits at 0,0) so the whole layout survives re-activation.
                string monitor = MonitorPath(p), gdi = GdiName(p);
                var dm = Native.NewDevMode();
                if (monitor != null && gdi != null && Native.EnumDisplaySettings(gdi, Native.ENUM_CURRENT_SETTINGS, ref dm))
                {
                    var mode = new ModeStore.Mode { W = dm.dmPelsWidth, H = dm.dmPelsHeight, Hz = dm.dmDisplayFrequency, X = dm.dmPositionX, Y = dm.dmPositionY };
                    saved[monitor] = mode;
                }
            }
            ModeStore.Save(saved);
            lock (Sync) snapshot = set;
            Log.Write("Snapshot (" + reason + "): " + set.Count + " external display(s) active");
        }

        static string MonitorPath(Native.PATH p)
        {
            var req = new Native.TARGET_DEVICE_NAME();
            req.header.type = Native.DISPLAYCONFIG_DEVICE_INFO_GET_TARGET_NAME;
            req.header.size = (uint)Marshal.SizeOf(typeof(Native.TARGET_DEVICE_NAME));
            req.header.adapterId = p.targetInfo.adapterId;
            req.header.id = p.targetInfo.id;
            return Native.DisplayConfigGetDeviceInfo(ref req) == 0 && !string.IsNullOrEmpty(req.monitorDevicePath) ? req.monitorDevicePath : null;
        }

        static string GdiName(Native.PATH p)
        {
            var req = new Native.SOURCE_DEVICE_NAME();
            req.header.type = Native.DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME;
            req.header.size = (uint)Marshal.SizeOf(typeof(Native.SOURCE_DEVICE_NAME));
            req.header.adapterId = p.sourceInfo.adapterId;
            req.header.id = p.sourceInfo.id;
            return Native.DisplayConfigGetDeviceInfo(ref req) == 0 && !string.IsNullOrEmpty(req.viewGdiDeviceName) ? req.viewGdiDeviceName : null;
        }

        // Windows gives a re-activated display a default (often 59/60 Hz) mode and leaves the laptop
        // as main display. Put back the saved refresh rates, then the saved positions (the main
        // display is the one at 0,0).
        static void RestoreLayout(List<Native.PATH> added)
        {
            var saved = ModeStore.Load();
            foreach (var p in added) RestoreRefresh(p, saved);
            int rc = RestorePositions(saved);
            Log.Write("  Restore positions: " + (rc == 1 ? "no complete saved layout" : "rc=" + rc));
        }

        // Saved resolution and refresh rate, or the highest refresh rate at the current resolution.
        static void RestoreRefresh(Native.PATH p, Dictionary<string, ModeStore.Mode> saved)
        {
            string monitor = MonitorPath(p), gdi = GdiName(p);
            if (gdi == null) return;
            var cur = Native.NewDevMode();
            if (!Native.EnumDisplaySettings(gdi, Native.ENUM_CURRENT_SETTINGS, ref cur)) return;

            ModeStore.Mode s = new ModeStore.Mode();
            bool has = monitor != null && saved.TryGetValue(monitor, out s);
            int w = has ? s.W : cur.dmPelsWidth, h = has ? s.H : cur.dmPelsHeight;
            int hz = PickRefresh(gdi, w, h, has ? s.Hz : 0);
            if (hz == 0 || (cur.dmPelsWidth == w && cur.dmPelsHeight == h && cur.dmDisplayFrequency == hz)) return;

            cur.dmPelsWidth = w;
            cur.dmPelsHeight = h;
            cur.dmDisplayFrequency = hz;
            cur.dmFields = Native.DM_PELSWIDTH | Native.DM_PELSHEIGHT | Native.DM_DISPLAYFREQUENCY;
            int rc = Native.ChangeDisplaySettingsEx(gdi, ref cur, IntPtr.Zero, Native.CDS_UPDATEREGISTRY, IntPtr.Zero);
            Log.Write("  Set " + gdi + " to " + w + "x" + h + "@" + hz + (has ? " (saved)" : " (max)") + ": rc=" + rc);
        }

        // The saved refresh rate if the monitor still offers it at w x h, otherwise the highest; 0 if none.
        static int PickRefresh(string gdi, int w, int h, int wantHz)
        {
            int best = 0;
            var dm = Native.NewDevMode();
            for (int i = 0; Native.EnumDisplaySettings(gdi, i, ref dm); i++)
            {
                if (dm.dmPelsWidth != w || dm.dmPelsHeight != h) continue;
                if (dm.dmDisplayFrequency == wantHz) return wantHz;
                best = Math.Max(best, dm.dmDisplayFrequency);
            }
            return best;
        }

        // Moves every active display to its saved position through its source mode. Returns 0 on
        // success or nothing to do, 1 when some display has no saved position, else the error code.
        static int RestorePositions(Dictionary<string, ModeStore.Mode> saved)
        {
            Native.PATH[] paths; IntPtr modes; uint numModes;
            if (!Query(Native.QDC_ONLY_ACTIVE_PATHS, out paths, out modes, out numModes)) return -1;
            try
            {
                bool changed = false;
                foreach (var p in paths)
                {
                    string monitor = MonitorPath(p);
                    ModeStore.Mode s;
                    uint idx = p.sourceInfo.modeInfoIdx;
                    if (monitor == null || !saved.TryGetValue(monitor, out s) || idx >= numModes) return 1;
                    IntPtr mode = new IntPtr(modes.ToInt64() + idx * Native.MODE_INFO_SIZE);
                    if (Marshal.ReadInt32(mode, 0) != Native.DISPLAYCONFIG_MODE_INFO_TYPE_SOURCE) return 1;
                    if (Marshal.ReadInt32(mode, Native.SOURCE_MODE_X) == s.X && Marshal.ReadInt32(mode, Native.SOURCE_MODE_Y) == s.Y) continue;
                    Marshal.WriteInt32(mode, Native.SOURCE_MODE_X, s.X);
                    Marshal.WriteInt32(mode, Native.SOURCE_MODE_Y, s.Y);
                    changed = true;
                }
                if (!changed) return 0;
                return Native.SetDisplayConfig((uint)paths.Length, paths, numModes, modes,
                    Native.SDC_APPLY | Native.SDC_USE_SUPPLIED_DISPLAY_CONFIG | Native.SDC_ALLOW_CHANGES | Native.SDC_SAVE_TO_DATABASE);
            }
            finally { Marshal.FreeHGlobal(modes); }
        }

        public static int ActiveCount()
        {
            uint paths, modes;
            if (Native.GetDisplayConfigBufferSizes(Native.QDC_ONLY_ACTIVE_PATHS, out paths, out modes) != 0) return -1;
            return (int)paths;
        }

        // Activates connected-but-inactive external monitors: all of them when useAll is set or no
        // snapshot exists, otherwise only those active before sleep. Returns how many wanted
        // monitors are still inactive (not yet reported as connected), or -1 on error.
        public static int ActivateMissing(bool useAll, out bool changed)
        {
            changed = false;
            HashSet<string> wanted = null;
            if (!useAll) lock (Sync) if (snapshot != null) wanted = new HashSet<string>(snapshot);

            Native.PATH[] active; IntPtr modes; uint numModes;
            if (!Query(Native.QDC_ONLY_ACTIVE_PATHS, out active, out modes, out numModes)) return -1;
            try
            {
                Native.PATH[] all; IntPtr allModes; uint allNumModes;
                if (!Query(Native.QDC_ALL_PATHS, out all, out allModes, out allNumModes)) return -1;
                Marshal.FreeHGlobal(allModes);

                var activeTargets = new HashSet<string>();
                var usedSources = new HashSet<string>();
                foreach (var p in active) { activeTargets.Add(TargetKey(p)); usedSources.Add(SourceKey(p)); }

                var added = new List<Native.PATH>();
                foreach (var p in all)
                {
                    var key = TargetKey(p);
                    if (p.targetInfo.targetAvailable == 0 || !IsExternal(p.targetInfo.outputTechnology)) continue;
                    if (activeTargets.Contains(key) || usedSources.Contains(SourceKey(p))) continue;
                    if (wanted != null && !wanted.Contains(key)) continue;

                    var np = p;
                    np.flags = Native.DISPLAYCONFIG_PATH_ACTIVE;
                    np.sourceInfo.modeInfoIdx = Native.MODE_IDX_INVALID;
                    np.targetInfo.modeInfoIdx = Native.MODE_IDX_INVALID;
                    added.Add(np);
                    activeTargets.Add(key);
                    usedSources.Add(SourceKey(p));
                }

                if (added.Count > 0)
                {
                    var list = new List<Native.PATH>(active);
                    list.AddRange(added);
                    const uint flags = Native.SDC_APPLY | Native.SDC_USE_SUPPLIED_DISPLAY_CONFIG |
                                       Native.SDC_ALLOW_CHANGES | Native.SDC_SAVE_TO_DATABASE;
                    int rc = Native.SetDisplayConfig((uint)list.Count, list.ToArray(), numModes, modes, flags);
                    if (rc != 0)
                    {
                        // Let Windows pick every mode if the existing ones don't fit the new layout.
                        var loose = list.ToArray();
                        for (int i = 0; i < loose.Length; i++)
                        {
                            loose[i].sourceInfo.modeInfoIdx = Native.MODE_IDX_INVALID;
                            loose[i].targetInfo.modeInfoIdx = Native.MODE_IDX_INVALID;
                        }
                        int rc2 = Native.SetDisplayConfig((uint)loose.Length, loose, 0, IntPtr.Zero, flags);
                        Log.Write("  Activate " + added.Count + " display(s): rc=" + rc + ", retry without modes rc=" + rc2);
                        rc = rc2;
                    }
                    else Log.Write("  Activated " + added.Count + " external display(s)");
                    changed = rc == 0;
                    if (changed) RestoreLayout(added);
                }

                if (wanted == null) return 0;
                int missing = 0;
                foreach (var key in wanted) if (!activeTargets.Contains(key)) missing++;
                return missing;
            }
            finally { Marshal.FreeHGlobal(modes); }
        }

        static bool Query(uint flags, out Native.PATH[] paths, out IntPtr modes, out uint numModes)
        {
            paths = null; modes = IntPtr.Zero; numModes = 0;
            for (int attempt = 0; attempt < 3; attempt++)
            {
                uint np, nm;
                if (Native.GetDisplayConfigBufferSizes(flags, out np, out nm) != 0) return false;
                var p = new Native.PATH[np];
                IntPtr m = Marshal.AllocHGlobal((int)Math.Max(1, nm) * Native.MODE_INFO_SIZE);
                int rc = Native.QueryDisplayConfig(flags, ref np, p, ref nm, m, IntPtr.Zero);
                if (rc == 0)
                {
                    Array.Resize(ref p, (int)np);
                    paths = p; modes = m; numModes = nm;
                    return true;
                }
                Marshal.FreeHGlobal(m);
                if (rc != 122) return false; // ERROR_INSUFFICIENT_BUFFER: layout changed, retry
            }
            return false;
        }
    }

    // Last known resolution/refresh/position per monitor, keyed by monitor device path.
    static class ModeStore
    {
        public struct Mode { public int W, H, Hz, X, Y; }

        static readonly string FilePath = Path.Combine(Settings.Dir, "modes.txt");
        static readonly object Sync = new object();

        public static Dictionary<string, Mode> Load()
        {
            var result = new Dictionary<string, Mode>(StringComparer.OrdinalIgnoreCase);
            lock (Sync)
            {
                try
                {
                    if (!File.Exists(FilePath)) return result;
                    foreach (var line in File.ReadAllLines(FilePath))
                    {
                        var f = line.Split('|');
                        int w, h, hz, x, y;
                        if (f.Length == 6 && int.TryParse(f[1], out w) && int.TryParse(f[2], out h) && int.TryParse(f[3], out hz)
                            && int.TryParse(f[4], out x) && int.TryParse(f[5], out y))
                            result[f[0]] = new Mode { W = w, H = h, Hz = hz, X = x, Y = y };
                    }
                }
                catch { }
            }
            return result;
        }

        public static void Save(Dictionary<string, Mode> modes)
        {
            var lines = new List<string>();
            foreach (var kv in modes)
                lines.Add(kv.Key + "|" + kv.Value.W + "|" + kv.Value.H + "|" + kv.Value.Hz + "|" + kv.Value.X + "|" + kv.Value.Y);
            lock (Sync)
            {
                try { File.WriteAllLines(FilePath, lines.ToArray()); }
                catch { }
            }
        }
    }

    static class Waker
    {
        static int running;
        static int rerun;          // set when a wake is requested while one is running
        static string rerunReason;
        static bool rerunAll;

        public static void Run(string reason, bool all)
        {
            if (Interlocked.Exchange(ref running, 1) == 1)
            {
                rerunReason = reason;
                rerunAll = rerunAll || all;
                Interlocked.Exchange(ref rerun, 1);
                return;
            }
            try
            {
                RunOnce(reason, all);
                while (Interlocked.Exchange(ref rerun, 0) == 1)
                {
                    bool a = rerunAll; rerunAll = false;
                    RunOnce(rerunReason, a);
                }
            }
            finally
            {
                Interlocked.Exchange(ref running, 0);
            }
        }

        static void RunOnce(string reason, bool all)
        {
            try
            {
                Log.Write("Wake (" + reason + "), active displays = " + Displays.ActiveCount());

                // 1. Ask every display to power on and generate real input so Windows leaves display-off.
                Native.PostMessage(Native.HWND_BROADCAST, Native.WM_SYSCOMMAND,
                    (IntPtr)Native.SC_MONITORPOWER, (IntPtr)(-1));
                NudgeMouse();
                Thread.Sleep(1000);

                // 2. Re-activate external monitors Windows dropped from the layout. A monitor can take a
                //    few seconds after resume to report as connected, so retry while any is missing.
                bool anyChanged = false;
                for (int pass = 0; pass < Math.Max(1, Settings.Retries); pass++)
                {
                    bool changed;
                    int missing = Displays.ActivateMissing(all, out changed);
                    anyChanged |= changed;
                    if (missing <= 0) break;
                    if (pass == 0) Log.Write("  Waiting for " + missing + " display(s) to reconnect");
                    Thread.Sleep(3000);
                }

                // 3. If the layout was already right, re-apply it to force a mode set that re-drives the
                //    HDMI/DP link for monitors that are active but still asleep.
                if (!anyChanged)
                {
                    int rc = Native.SetDisplayConfig(0, null, 0, IntPtr.Zero,
                        Native.SDC_APPLY | Native.SDC_USE_DATABASE_CURRENT);
                    if (rc != 0) Log.Write("  Re-apply layout rc=" + rc + (rc == 5 ? " (lock screen; will retry on unlock)" : ""));
                }

                Thread.Sleep(1500);
                NudgeMouse();
                Log.Write("  Done, active displays = " + Displays.ActiveCount());
            }
            catch (Exception ex)
            {
                Log.Write("  Error: " + ex.Message);
            }
        }

        static void NudgeMouse()
        {
            var inputs = new Native.INPUT[2];
            inputs[0].type = Native.INPUT_MOUSE;
            inputs[0].mi.dx = 1;
            inputs[0].mi.dwFlags = Native.MOUSEEVENTF_MOVE;
            inputs[1].type = Native.INPUT_MOUSE;
            inputs[1].mi.dx = -1;
            inputs[1].mi.dwFlags = Native.MOUSEEVENTF_MOVE;
            Native.SendInput(2, inputs, Marshal.SizeOf(typeof(Native.INPUT)));
        }
    }

    // Monitor glyph from the Windows 11 icon font, white on a dark taskbar and black on a light one,
    // drawn at the tray's icon size for the current display scaling.
    static class TrayIcon
    {
        const string Glyph = ""; // TVMonitor

        public static Icon Create()
        {
            int size = SystemInformation.SmallIconSize.Width;
            var family = FindFamily("Segoe Fluent Icons") ?? FindFamily("Segoe MDL2 Assets");
            if (family == null) return SystemIcons.Application;

            using (var bmp = new Bitmap(size, size))
            using (var g = Graphics.FromImage(bmp))
            using (var path = new System.Drawing.Drawing2D.GraphicsPath())
            using (var brush = new SolidBrush(LightTaskbar() ? Color.Black : Color.White))
            {
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
                path.AddString(Glyph, family, (int)FontStyle.Regular, size, Point.Empty, StringFormat.GenericTypographic);

                // Fit the glyph's outline into the icon box and center it.
                var b = path.GetBounds();
                float scale = size * 0.94f / Math.Max(b.Width, b.Height);
                using (var m = new System.Drawing.Drawing2D.Matrix())
                {
                    m.Translate(size / 2f, size / 2f);
                    m.Scale(scale, scale);
                    m.Translate(-(b.X + b.Width / 2f), -(b.Y + b.Height / 2f));
                    path.Transform(m);
                }
                g.FillPath(brush, path);
                return Icon.FromHandle(bmp.GetHicon());
            }
        }

        public static void Destroy(Icon icon)
        {
            if (icon == null || icon == SystemIcons.Application) return;
            Native.DestroyIcon(icon.Handle);
            icon.Dispose();
        }

        static FontFamily FindFamily(string name)
        {
            foreach (var f in FontFamily.Families)
                if (f.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) return f;
            return null;
        }

        static bool LightTaskbar()
        {
            try
            {
                var v = Microsoft.Win32.Registry.GetValue(
                    @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize",
                    "SystemUsesLightTheme", 0);
                return v is int && (int)v != 0;
            }
            catch { return false; }
        }
    }

    class HostForm : Form
    {
        readonly NotifyIcon tray;
        readonly System.Windows.Forms.Timer debounce;
        string pendingReason = "";
        bool pendingAll;
        int lastDisplayState = -1;
        IntPtr powerNotify = IntPtr.Zero;

        public HostForm()
        {
            ShowInTaskbar = false;
            FormBorderStyle = FormBorderStyle.None;

            var menu = new ContextMenuStrip();
            menu.Items.Add("Wake monitor now", null, delegate { Schedule("manual", true, 0); });
            menu.Items.Add("Save current layout", null, delegate { Displays.Snapshot("manual"); });
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Open log", null, delegate
            {
                if (File.Exists(Log.FilePath)) System.Diagnostics.Process.Start("notepad.exe", "\"" + Log.FilePath + "\"");
            });
            menu.Items.Add("Exit", null, delegate { Close(); });

            tray = new NotifyIcon();
            tray.Icon = TrayIcon.Create();
            tray.Text = "HdmiWake";
            tray.ContextMenuStrip = menu;
            tray.DoubleClick += delegate { Schedule("manual", true, 0); };
            tray.Visible = true;

            debounce = new System.Windows.Forms.Timer();
            debounce.Tick += delegate
            {
                debounce.Stop();
                var reason = pendingReason;
                var all = pendingAll;
                pendingAll = false;
                ThreadPool.QueueUserWorkItem(delegate { Waker.Run(reason, all); });
            };

            Microsoft.Win32.SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
        }

        // Redraw the icon when the taskbar switches between light and dark.
        void OnUserPreferenceChanged(object sender, Microsoft.Win32.UserPreferenceChangedEventArgs e)
        {
            if (e.Category != Microsoft.Win32.UserPreferenceCategory.General) return;
            if (InvokeRequired) { BeginInvoke(new Action(delegate { OnUserPreferenceChanged(sender, e); })); return; }
            var old = tray.Icon;
            tray.Icon = TrayIcon.Create();
            TrayIcon.Destroy(old);
        }

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            var guid = Native.GUID_CONSOLE_DISPLAY_STATE;
            powerNotify = Native.RegisterPowerSettingNotification(Handle, ref guid, Native.DEVICE_NOTIFY_WINDOW_HANDLE);
            Native.WTSRegisterSessionNotification(Handle, Native.NOTIFY_FOR_THIS_SESSION);
            Log.Write("Started");
            Schedule("logon/start", true, Settings.DelayMs + 2000);
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            if (powerNotify != IntPtr.Zero) Native.UnregisterPowerSettingNotification(powerNotify);
            Native.WTSUnRegisterSessionNotification(Handle);
            Microsoft.Win32.SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
            tray.Visible = false;
            TrayIcon.Destroy(tray.Icon);
            tray.Dispose();
            Log.Write("Exited");
            base.OnFormClosed(e);
        }

        void Schedule(string reason, bool all, int delayMs)
        {
            pendingReason = reason;
            pendingAll = pendingAll || all;
            debounce.Stop();
            debounce.Interval = Math.Max(1, delayMs);
            debounce.Start();
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == Native.WM_POWERBROADCAST)
            {
                int ev = m.WParam.ToInt32();
                if (ev == Native.PBT_APMSUSPEND)
                {
                    Displays.Snapshot("suspend");
                }
                else if (ev == Native.PBT_APMRESUMEAUTOMATIC || ev == Native.PBT_APMRESUMESUSPEND)
                {
                    Schedule("resume", false, Settings.DelayMs);
                }
                else if (ev == Native.PBT_POWERSETTINGCHANGE && m.LParam != IntPtr.Zero)
                {
                    var setting = (Guid)Marshal.PtrToStructure(m.LParam, typeof(Guid));
                    if (setting == Native.GUID_CONSOLE_DISPLAY_STATE)
                    {
                        int state = Marshal.ReadInt32(m.LParam, 20); // 0 off, 1 on, 2 dimmed
                        if (state == 0 && lastDisplayState != 0) Displays.Snapshot("display off");
                        if (state == 1 && lastDisplayState == 0) Schedule("display on", false, Settings.DelayMs);
                        lastDisplayState = state;
                    }
                }
            }
            else if (m.Msg == Native.WM_WTSSESSION_CHANGE)
            {
                int ev = m.WParam.ToInt32();
                if (ev == Native.WTS_SESSION_LOCK)
                    Displays.Snapshot("lock");
                else if (ev == Native.WTS_SESSION_UNLOCK || ev == Native.WTS_SESSION_LOGON || ev == Native.WTS_CONSOLE_CONNECT)
                    Schedule("session " + ev, false, Settings.DelayMs);
            }
            base.WndProc(ref m);
        }

        protected override void SetVisibleCore(bool value)
        {
            if (!IsHandleCreated) { CreateHandle(); OnLoad(EventArgs.Empty); }
            base.SetVisibleCore(false);
        }
    }

    static class Native
    {
        public static readonly IntPtr HWND_BROADCAST = (IntPtr)0xFFFF;
        public const int WM_SYSCOMMAND = 0x0112;
        public const int SC_MONITORPOWER = 0xF170;
        public const int WM_POWERBROADCAST = 0x0218;
        public const int PBT_APMSUSPEND = 0x0004;
        public const int PBT_APMRESUMESUSPEND = 0x0007;
        public const int PBT_APMRESUMEAUTOMATIC = 0x0012;
        public const int PBT_POWERSETTINGCHANGE = 0x8013;
        public const int WM_WTSSESSION_CHANGE = 0x02B1;
        public const int WTS_CONSOLE_CONNECT = 0x1;
        public const int WTS_SESSION_LOGON = 0x5;
        public const int WTS_SESSION_LOCK = 0x7;
        public const int WTS_SESSION_UNLOCK = 0x8;
        public const int NOTIFY_FOR_THIS_SESSION = 0;
        public const int DEVICE_NOTIFY_WINDOW_HANDLE = 0;
        public static Guid GUID_CONSOLE_DISPLAY_STATE = new Guid("6fe69556-704a-47a0-8f24-c28d936fda47");

        public const uint QDC_ALL_PATHS = 0x00000001;
        public const uint QDC_ONLY_ACTIVE_PATHS = 0x00000002;
        public const uint SDC_USE_DATABASE_CURRENT = 0x0000000F;
        public const uint SDC_USE_SUPPLIED_DISPLAY_CONFIG = 0x00000020;
        public const uint SDC_APPLY = 0x00000080;
        public const uint SDC_SAVE_TO_DATABASE = 0x00000200;
        public const uint SDC_ALLOW_CHANGES = 0x00000400;
        public const uint DISPLAYCONFIG_PATH_ACTIVE = 0x00000001;
        public const uint MODE_IDX_INVALID = 0xFFFFFFFF;
        public const int MODE_INFO_SIZE = 64; // sizeof(DISPLAYCONFIG_MODE_INFO)
        public const int DISPLAYCONFIG_MODE_INFO_TYPE_SOURCE = 1;
        public const int SOURCE_MODE_X = 28;  // DISPLAYCONFIG_MODE_INFO.sourceMode.position.x
        public const int SOURCE_MODE_Y = 32;  // DISPLAYCONFIG_MODE_INFO.sourceMode.position.y

        public const uint DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME = 1;
        public const uint DISPLAYCONFIG_DEVICE_INFO_GET_TARGET_NAME = 2;

        public const int ENUM_CURRENT_SETTINGS = -1;
        public const int DM_POSITION = 0x00000020;
        public const int DM_PELSWIDTH = 0x00080000;
        public const int DM_PELSHEIGHT = 0x00100000;
        public const int DM_DISPLAYFREQUENCY = 0x00400000;
        public const uint CDS_UPDATEREGISTRY = 0x00000001;
        public const uint CDS_NORESET = 0x10000000;

        public const uint INPUT_MOUSE = 0;
        public const uint MOUSEEVENTF_MOVE = 0x0001;

        [StructLayout(LayoutKind.Sequential)]
        public struct LUID { public uint LowPart; public int HighPart; }

        [StructLayout(LayoutKind.Sequential)]
        public struct PATH_SOURCE_INFO
        {
            public LUID adapterId;
            public uint id, modeInfoIdx, statusFlags;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct PATH_TARGET_INFO
        {
            public LUID adapterId;
            public uint id, modeInfoIdx, outputTechnology, rotation, scaling;
            public uint refreshNumerator, refreshDenominator, scanLineOrdering;
            public int targetAvailable;
            public uint statusFlags;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct PATH
        {
            public PATH_SOURCE_INFO sourceInfo;
            public PATH_TARGET_INFO targetInfo;
            public uint flags;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct DEVICE_INFO_HEADER
        {
            public uint type, size;
            public LUID adapterId;
            public uint id;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct SOURCE_DEVICE_NAME
        {
            public DEVICE_INFO_HEADER header;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string viewGdiDeviceName;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct TARGET_DEVICE_NAME
        {
            public DEVICE_INFO_HEADER header;
            public uint flags, outputTechnology;
            public ushort edidManufactureId, edidProductCodeId;
            public uint connectorInstance;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string monitorFriendlyDeviceName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string monitorDevicePath;
        }

        // DEVMODEW, display variant of the union.
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct DEVMODE
        {
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
            public short dmSpecVersion, dmDriverVersion, dmSize, dmDriverExtra;
            public int dmFields;
            public int dmPositionX, dmPositionY, dmDisplayOrientation, dmDisplayFixedOutput;
            public short dmColor, dmDuplex, dmYResolution, dmTTOption, dmCollate;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
            public short dmLogPixels;
            public int dmBitsPerPel, dmPelsWidth, dmPelsHeight, dmDisplayFlags, dmDisplayFrequency;
            public int dmICMMethod, dmICMIntent, dmMediaType, dmDitherType, dmReserved1, dmReserved2, dmPanningWidth, dmPanningHeight;
        }

        public static DEVMODE NewDevMode()
        {
            var dm = new DEVMODE();
            dm.dmSize = (short)Marshal.SizeOf(typeof(DEVMODE));
            return dm;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct MOUSEINPUT
        {
            public int dx, dy;
            public uint mouseData, dwFlags, time;
            public IntPtr dwExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct INPUT
        {
            public uint type;
            public MOUSEINPUT mi; // largest union member on x64 alongside KEYBDINPUT/HARDWAREINPUT
        }

        [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll")] public static extern uint SendInput(uint n, INPUT[] inputs, int size);
        [DllImport("user32.dll")] public static extern int GetDisplayConfigBufferSizes(uint flags, out uint numPaths, out uint numModes);
        [DllImport("user32.dll")] public static extern int QueryDisplayConfig(uint flags, ref uint numPaths, [Out] PATH[] paths, ref uint numModes, IntPtr modes, IntPtr topologyId);
        [DllImport("user32.dll")] public static extern int SetDisplayConfig(uint numPaths, [In] PATH[] paths, uint numModes, IntPtr modes, uint flags);
        [DllImport("user32.dll")] public static extern int DisplayConfigGetDeviceInfo(ref SOURCE_DEVICE_NAME request);
        [DllImport("user32.dll")] public static extern int DisplayConfigGetDeviceInfo(ref TARGET_DEVICE_NAME request);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern bool EnumDisplaySettings(string device, int modeNum, ref DEVMODE dm);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int ChangeDisplaySettingsEx(string device, ref DEVMODE dm, IntPtr hwnd, uint flags, IntPtr param);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int ChangeDisplaySettingsEx(string device, IntPtr dm, IntPtr hwnd, uint flags, IntPtr param);
        [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
        [DllImport("user32.dll")] public static extern bool DestroyIcon(IntPtr handle);
        [DllImport("user32.dll")] public static extern IntPtr RegisterPowerSettingNotification(IntPtr hRecipient, ref Guid guid, int flags);
        [DllImport("user32.dll")] public static extern bool UnregisterPowerSettingNotification(IntPtr handle);
        [DllImport("wtsapi32.dll")] public static extern bool WTSRegisterSessionNotification(IntPtr hWnd, int flags);
        [DllImport("wtsapi32.dll")] public static extern bool WTSUnRegisterSessionNotification(IntPtr hWnd);
    }
}
