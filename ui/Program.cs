// FanManager.exe - RedmiBook 14 2020 (TM1901) fan & power manager
// Single-file WinForms app, .NET Framework 4.8 (in-box), admin manifest.
// Build: csc /codepage:65001 /target:winexe /out:FanManager.exe /r:System.dll /r:System.Core.dll
//        /r:System.Drawing.dll /r:System.Windows.Forms.dll /r:System.ServiceProcess.dll
//        /win32manifest:app.manifest /win32icon:app.ico Program.cs
// Modes: quiet/balanced/performance/dnd/restore. EC 0x93/0x94, MSR 0x610 via WinRing0_1_2_0.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.ServiceProcess;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Xml;
using Microsoft.Win32;
using System.Security.Principal;
using System.Drawing;

namespace FanManager
{
    static class Program
    {
        [STAThread]
        static void Main(string[] args)
        {
            string arg = args.Length > 0 ? args[0].ToLower() : "";
            if (arg == "--selftest") { SelfTest.CliRun(); return; }
            if (arg == "--screenshot") { App.RunScreenshot(); return; }
            App.Run();
        }
    }

    public static class Config
    {
        // BaseDir = repo root (injected by FanManager.ps1 via Init). Fallback: %ProgramData%.
        public static string BaseDir = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData) + @"\RedmiBookFanControl";
        public static string LogDir = BaseDir + @"\logs";
        public static string AppLog { get { return LogDir + @"\app.log"; } }
        public const string NbfcDir = @"C:\Program Files (x86)\NoteBook FanControl";
        public const string NbfcExe = NbfcDir + @"\nbfc.exe";
        public const string EcProbe = NbfcDir + @"\ec-probe.exe";
        public const string NbfcConfigs = NbfcDir + @"\Configs";
        public static string MyConfigs = BaseDir + @"\nbfc-configs";
        public static string ScriptsDir = BaseDir + @"\scripts";
        public static string MsrBackup = BaseDir + @"\data\msr-610-original.txt";
        public static void Init(string baseDir)
        {
            BaseDir = baseDir;
            LogDir = baseDir + @"\logs";
            MyConfigs = baseDir + @"\nbfc-configs";
            ScriptsDir = baseDir + @"\scripts";
            MsrBackup = baseDir + @"\data\msr-610-original.txt";
            try { Directory.CreateDirectory(LogDir); Directory.CreateDirectory(BaseDir + @"\data"); } catch { }
            Profile.Load();
        }
        public const string SilentGuid = "9a10e4a1-1111-4b7a-8c0d-000000000001";
        public const string BalancedGuid = "9a10e4a1-1111-4b7a-8c0d-000000000002";
        public const string PerfGuid = "9a10e4a1-1111-4b7a-8c0d-000000000003";
        public const string DndGuid = "9a10e4a1-1111-4b7a-8c0d-000000000004";
        public const string SystemGuid = "a1841308-3541-4fab-bc81-f71556f20b4a";
        public const string TaskName = "ZFanAutoMode";
        public const string FreqGuid = "75b0ae3f-bce0-45a7-8c89-c9611c25e100"; // hidden max processor frequency (MHz)
        public const string SubProcessor = "54533251-82be-4824-96c1-47b60b740d00";
        public static readonly string[] SilentCfg = { "RedmiBook 14 2020 Silent", SilentGuid };
        public static readonly string[] BalancedCfg = { "RedmiBook 14 2020 Balanced", BalancedGuid };
        public static readonly string[] PerfCfg = { "RedmiBook 14 2020 Performance", PerfGuid };
        public static readonly string[] DndCfg = { "RedmiBook 14 2020 DoNotDisturb", DndGuid };

        public static void Log(string msg)
        {
            try { File.AppendAllText(AppLog, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + msg + "\r\n", Encoding.UTF8); } catch { }
        }

        public static string Run(string exe, string args, int timeoutMs = 15000, int gbk = 0)
        {
            try
            {
                var psi = new ProcessStartInfo(exe, args)
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                    StandardOutputEncoding = gbk == 1 ? Encoding.GetEncoding(936) : Encoding.Default
                };
                using (var p = Process.Start(psi))
                {
                    var so = p.StandardOutput.ReadToEndAsync();
                    var se = p.StandardError.ReadToEndAsync();
                    if (!p.WaitForExit(timeoutMs)) { try { p.Kill(); p.WaitForExit(2000); } catch { } }
                    so.Wait();
                    se.Wait();
                    return so.Result;
                }
            }
            catch (Exception ex) { return "ERR: " + ex.Message; }
        }

        // serialize all ec-probe invocations inside this process (the EC port must not be
        // hammered by concurrent readers when the status engine + an operation overlap)
        public static readonly object EcLock = new object();

        public static int EcRead(int reg)
        {
            string outp;
            lock (EcLock) outp = Run(EcProbe, "read " + reg, 8000);
            Match m = Regex.Match(outp, @"\((0x[0-9A-Fa-f]+)\)");
            if (m.Success) return Convert.ToInt32(m.Groups[1].Value, 16);
            return -1;
        }

        public static string EcDump()
        {
            lock (EcLock) return Run(EcProbe, "dump", 60000);
        }

        public static string ActivePlanGuid()
        {
            string s = Run("powercfg", "/getactivescheme", 8000, 1);
            Match m = Regex.Match(s, @"([0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12})");
            return m.Success ? m.Groups[1].Value.ToLower() : "";
        }
    }

    // ---- UI activity log: what the app is doing right now, shown in the side/bottom log panel ----
    // Thread-safe: operations run on background threads, the form subscribes and marshals to the UI.
    public static class UiLog
    {
        static readonly object gate = new object();
        static readonly List<string> lines = new List<string>();
        static string currentAction = "空闲";

        public static event Action<string> Entry;          // one log line (already time-stamped)
        public static event Action<string> ActionChanged;  // the one-line "what is happening now"

        public static string CurrentAction { get { lock (gate) return currentAction; } }
        public static string[] Recent() { lock (gate) return lines.ToArray(); }

        public static void Action(string text)             // set current action + log it
        {
            lock (gate) currentAction = text;
            Log(text);
            var h = ActionChanged; if (h != null) try { h(text); } catch { }
        }
        public static void Done(string result) { Action("完成: " + result); }
        public static void Step(string text) { Log("  · " + text); }
        public static void Fail(string text) { Action("失败: " + text); }
        public static void Log(string msg)
        {
            string line = DateTime.Now.ToString("HH:mm:ss") + "  " + msg;
            lock (gate)
            {
                lines.Add(line);
                if (lines.Count > 600) lines.RemoveRange(0, 100);
            }
            Config.Log(msg);
            var h = Entry; if (h != null) try { h(line); } catch { }
        }
    }

    // ---- machine profile: only TM1901 register mapping is verified, every other model is read-only ----
    public static class Profile
    {
        public const int FanModeReg = 147;
        public const int FanDutyReg = 148;
        public const int FanFeedbackReg = 149;
        public const int CpuTempReg = 168;
        public static string Model = "?";
        public static bool Supported = false;

        public static void Load()
        {
            try
            {
                using (var k = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\BIOS"))
                {
                    if (k != null)
                    {
                        string sys = (k.GetValue("SystemProductName") as string) ?? "?";
                        string board = (k.GetValue("BaseBoardProduct") as string) ?? "?";
                        Model = sys + " / " + board;
                    }
                }
            }
            catch { Model = "?"; }
            Supported = Model.IndexOf("TM1901", StringComparison.OrdinalIgnoreCase) >= 0
                     || Model.IndexOf("RedmiBook", StringComparison.OrdinalIgnoreCase) >= 0;
        }
    }

    // ---- telemetry: RAPL package power (MSR 0x606 units + 0x611 energy counter) and 10-min history ----
    public static class Telemetry
    {
        public class Sample { public int Temp; public int Duty; public double Watts; public int Mhz = -1; public DateTime Ts; }
        public const int Capacity = 120;   // 120 samples x 5s = 10 minutes
        public static readonly object Sync = new object();
        static readonly List<Sample> history = new List<Sample>();
        static uint lastCounter;
        static DateTime lastRead;
        static bool primed;
        static double joulePerCount = 1.0 / 16384.0;

        // snapshot for cross-thread painting (background sampler + UI painter)
        public static List<Sample> Copy()
        {
            lock (Sync) return new List<Sample>(history);
        }

        public static double PackageWatts()
        {
            ulong unit;
            if (Msr.Read(0x606, out unit))
            {
                int e = (int)((unit >> 8) & 0x1F);
                if (e <= 31) joulePerCount = 1.0 / (1L << e);
            }
            ulong raw;
            if (!Msr.Read(0x611, out raw)) { primed = false; return -1; }
            uint counter = (uint)(raw & 0xFFFFFFFF);
            DateTime now = DateTime.UtcNow;
            double watts = -1;
            if (primed)
            {
                double secs = (now - lastRead).TotalSeconds;
                if (secs >= 0.2 && secs < 120) watts = unchecked(counter - lastCounter) * joulePerCount / secs;
            }
            lastCounter = counter; lastRead = now; primed = true;
            return watts;
        }

        public static void Add(int temp, int duty, double watts) { Add(temp, duty, watts, -1); }

        public static void Add(int temp, int duty, double watts, int mhz)
        {
            lock (Sync)
            {
                history.Add(new Sample { Temp = temp, Duty = duty, Watts = watts, Mhz = mhz, Ts = DateTime.Now });
                while (history.Count > Capacity) history.RemoveAt(0);
            }
        }

        // full history dump for offline analysis (logs/history-*.csv)
        public static string ExportCsv(string path)
        {
            var sb = new StringBuilder("time,temperature_c,fan_percent,package_watts,cpu_mhz\r\n");
            foreach (var s in Copy())
                sb.Append(s.Ts.ToString("yyyy-MM-dd HH:mm:ss")).Append(',')
                  .Append(s.Temp).Append(',')
                  .Append(s.Duty).Append(',')
                  .Append(s.Watts.ToString("0.0##", System.Globalization.CultureInfo.InvariantCulture)).Append(',')
                  .Append(s.Mhz).Append("\r\n");
            File.WriteAllText(path, sb.ToString(), Encoding.UTF8);
            return path;
        }

        public static void Clear() { lock (Sync) { history.Clear(); } }
    }

    public class TrendChart : Control
    {
        public int CurrentTemp = -1;

        public TrendChart()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            BackColor = Color.White;
        }

        int MapTemp(int t, Rectangle p)
        {
            if (t < 30) t = 30;
            if (t > 100) t = 100;
            return p.Bottom - (t - 30) * p.Height / 70;
        }

        int MapPercent(int v, Rectangle p)
        {
            if (v < 0) v = 0;
            if (v > 100) v = 100;
            return p.Bottom - v * p.Height / 100;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.Clear(BackColor);
            Rectangle r = ClientRectangle;
            Rectangle plot = new Rectangle(r.Left + 34, r.Top + 14, Math.Max(10, r.Width - 42), Math.Max(10, r.Height - 36));
            using (var gridPen = new Pen(Color.FromArgb(232, 232, 232)))
            using (var axisBrush = new SolidBrush(Color.Gray))
            using (var axisFont = new Font("Consolas", 8F))
            {
                for (int t = 30; t <= 100; t += 10)
                {
                    int y = MapTemp(t, plot);
                    g.DrawLine(gridPen, plot.Left, y, plot.Right, y);
                    g.DrawString(t + "C", axisFont, axisBrush, 2, y - 7);
                }
                // time axis: the buffer is fixed-width (10 min), newest sample at the right edge
                for (int m = 10; m >= 2; m -= 2)
                {
                    int x = plot.Left + (int)((double)(10 - m) / 10 * (plot.Width - 1));
                    g.DrawLine(gridPen, x, plot.Top, x, plot.Bottom);
                    g.DrawString("-" + m + "m", axisFont, axisBrush, x - 8, plot.Bottom + 2);
                }
                g.DrawString("now", axisFont, axisBrush, plot.Right - 18, plot.Bottom + 2);
                g.DrawRectangle(Pens.Gainsboro, plot);
            }

            List<Telemetry.Sample> data = Telemetry.Copy();
            if (data.Count < 2)
            {
                using (var gray = new SolidBrush(Color.Gray))
                    g.DrawString("采样中…（每 5 秒一个点，保留 10 分钟）", Font, gray, plot.Left + 8, plot.Top + 8);
                return;
            }

            Point[] temps = new Point[data.Count];
            Point[] duties = new Point[data.Count];
            for (int i = 0; i < data.Count; i++)
            {
                int x = plot.Left + (int)((double)i / (Telemetry.Capacity - 1) * (plot.Width - 1));
                temps[i] = new Point(x, MapTemp(data[i].Temp, plot));
                duties[i] = new Point(x, MapPercent(data[i].Duty, plot));
            }
            using (var tempPen = new Pen(Color.FromArgb(220, 80, 40), 1.6f))
            using (var dutyPen = new Pen(Color.FromArgb(40, 110, 200), 1.2f))
            using (var freqPen = new Pen(Color.FromArgb(120, 150, 40), 1.2f))
            {
                g.DrawLines(tempPen, temps);
                g.DrawLines(dutyPen, duties);
                // CPU frequency as % of 4.9 GHz max turbo (i7-10510U); skip gaps with no valid samples
                var fpts = new List<Point>();
                for (int i = 0; i < data.Count; i++)
                {
                    if (data[i].Mhz <= 0) { if (fpts.Count > 1) break; fpts.Clear(); continue; }
                    int x = plot.Left + (int)((double)i / (Telemetry.Capacity - 1) * (plot.Width - 1));
                    fpts.Add(new Point(x, MapPercent(Math.Min(100, (int)Math.Round(data[i].Mhz / 4900.0 * 100)), plot)));
                }
                if (fpts.Count > 1) g.DrawLines(freqPen, fpts.ToArray());
            }
            if (CurrentTemp > 0)
            {
                int y = MapTemp(CurrentTemp, plot);
                using (var dash = new Pen(Color.FromArgb(130, 220, 80, 40)) { DashStyle = System.Drawing.Drawing2D.DashStyle.Dash })
                    g.DrawLine(dash, plot.Left, y, plot.Right, y);
            }
            using (var tempBrush = new SolidBrush(Color.FromArgb(220, 80, 40)))
            using (var dutyBrush = new SolidBrush(Color.FromArgb(40, 110, 200)))
            using (var freqBrush = new SolidBrush(Color.FromArgb(120, 150, 40)))
            using (var legend = new Font("Microsoft YaHei UI", 8F))
            {
                g.DrawString("温度 ℃", legend, tempBrush, plot.Left + 4, plot.Top + 2);
                g.DrawString("风扇 %", legend, dutyBrush, plot.Left + 64, plot.Top + 2);
                g.DrawString("频率 %", legend, freqBrush, plot.Left + 124, plot.Top + 2);
            }
        }
    }

    // ---- step-function preview of the edited fan curve ----
    public class CurvePreview : Control
    {
        public List<int[]> Rows;          // [0] = {0, baseSpeed}, [i] = {upTemp, speed}
        public int CurrentTemp = -1;

        public CurvePreview()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            BackColor = Color.White;
        }

        int XOf(int temp, Rectangle p)
        {
            if (temp < 20) temp = 20;
            if (temp > 90) temp = 90;
            return p.Left + (temp - 20) * p.Width / 70;
        }

        int YOf(int pct, Rectangle p)
        {
            if (pct < 0) pct = 0;
            if (pct > 100) pct = 100;
            return p.Bottom - pct * p.Height / 100;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.Clear(BackColor);
            Rectangle r = ClientRectangle;
            Rectangle plot = new Rectangle(r.Left + 34, r.Top + 14, Math.Max(10, r.Width - 44), Math.Max(10, r.Height - 32));
            using (var gridPen = new Pen(Color.FromArgb(232, 232, 232)))
            using (var axisBrush = new SolidBrush(Color.Gray))
            using (var axisFont = new Font("Consolas", 8F))
            {
                for (int t = 20; t <= 90; t += 10)
                {
                    int x = XOf(t, plot);
                    g.DrawLine(gridPen, x, plot.Top, x, plot.Bottom);
                    g.DrawString(t.ToString(), axisFont, axisBrush, x - 8, plot.Bottom + 2);
                }
                for (int v = 0; v <= 100; v += 25)
                {
                    int y = YOf(v, plot);
                    g.DrawLine(gridPen, plot.Left, y, plot.Right, y);
                    g.DrawString(v + "%", axisFont, axisBrush, 2, y - 7);
                }
                g.DrawRectangle(Pens.Gainsboro, plot);
            }
            if (Rows == null || Rows.Count == 0) return;

            var pts = new List<Point>();
            int speed = Rows[0][1];
            int prevX = plot.Left;
            pts.Add(new Point(prevX, YOf(speed, plot)));
            for (int i = 1; i < Rows.Count; i++)
            {
                int x = XOf(Rows[i][0], plot);
                if (x < prevX) x = prevX;
                pts.Add(new Point(x, YOf(speed, plot)));
                speed = Rows[i][1];
                pts.Add(new Point(x, YOf(speed, plot)));
                prevX = x;
            }
            pts.Add(new Point(plot.Right, YOf(speed, plot)));
            using (var pen = new Pen(Color.FromArgb(220, 80, 40), 2f))
                g.DrawLines(pen, pts.ToArray());

            if (CurrentTemp > 0)
            {
                int x = XOf(CurrentTemp, plot);
                using (var dash = new Pen(Color.FromArgb(150, 40, 110, 200)) { DashStyle = System.Drawing.Drawing2D.DashStyle.Dash })
                    g.DrawLine(dash, x, plot.Top, x, plot.Bottom);
                using (var blue = new SolidBrush(Color.FromArgb(40, 110, 200)))
                    g.DrawString("当前 " + CurrentTemp + "℃", Font, blue, Math.Min(x + 3, plot.Right - 70), plot.Top + 2);
            }
        }
    }

    // ---- one-click bundle for support: logs + EC dump + self-test + environment ----
    public static class Diagnostics
    {
        public static string Export()
        {
            string dir = "";
            try
            {
                string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
                dir = Config.LogDir + "\\diag-" + stamp;
                Directory.CreateDirectory(dir);
                foreach (var f in Directory.GetFiles(Config.LogDir))
                {
                    string n = Path.GetFileName(f);
                    if (n.StartsWith("diag-", StringComparison.OrdinalIgnoreCase)) continue;
                    try { File.Copy(f, Path.Combine(dir, n), true); } catch { }
                }
                if (Directory.Exists(Config.MyConfigs))
                    foreach (var f in Directory.GetFiles(Config.MyConfigs, "*.xml"))
                        try { File.Copy(f, Path.Combine(dir, Path.GetFileName(f)), true); } catch { }

                UiLog.Step("诊断包 1/3 转储 EC 全寄存器");
                try { File.WriteAllText(Path.Combine(dir, "ec-dump.txt"), Config.EcDump(), Encoding.UTF8); } catch { }

                UiLog.Step("诊断包 2/3 运行完整自测与环境采集");
                var sb = new StringBuilder();
                foreach (var r in SelfTest.RunAll()) sb.AppendLine("[" + r[1] + "] " + r[0] + ": " + r[2]);
                sb.AppendLine();
                sb.AppendLine("model      : " + Profile.Model);
                sb.AppendLine("supported  : " + Profile.Supported);
                sb.AppendLine("os         : " + Environment.OSVersion.VersionString);
                sb.AppendLine("active plan: " + Config.ActivePlanGuid());
                sb.AppendLine("PL1        : " + Msr.Pl1W() + " W, locked=" + Msr.Locked());
                File.WriteAllText(Path.Combine(dir, "env.txt"), sb.ToString(), Encoding.UTF8);

                UiLog.Step("诊断包 3/3 压缩打包");
                string zip = dir + ".zip";
                Config.Run("powershell", "-NoProfile -Command \"Compress-Archive -Path '" + dir + "\\*' -DestinationPath '" + zip + "' -Force\"", 120000);
                if (Directory.Exists(dir)) Directory.Delete(dir, true);
                if (File.Exists(zip)) { Config.Log("diag bundle: " + zip); return zip; }
                return "打包失败";
            }
            catch (Exception ex) { return "失败: " + ex.Message; }
        }
    }

    // ---- WinRing0 MSR access (NBFC's kernel driver, IOCTL direct) ----
    public static class Msr
    {
        [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Unicode)]
        static extern IntPtr CreateFile(string name, uint access, uint share, IntPtr sec, uint disp, uint flags, IntPtr tmpl);
        [DllImport("kernel32", SetLastError = true)]
        static extern bool DeviceIoControl(IntPtr h, uint code, byte[] inBuf, uint inSize, byte[] outBuf, uint outSize, out uint ret, IntPtr ov);
        [DllImport("kernel32")] static extern bool CloseHandle(IntPtr h);

        const uint FILE_SHARE_READ = 1, FILE_SHARE_WRITE = 2, OPEN_EXISTING = 3;
        static uint CTL(uint t, uint f, uint m, uint a) { return (t << 16) | (a << 14) | (f << 2) | m; }
        static uint IOCTL_READ_MSR = CTL(40000, 0x821, 0, 0);
        static uint IOCTL_WRITE_MSR = CTL(40000, 0x822, 0, 0);

        public static bool Read(uint idx, out ulong val)
        {
            val = 0;
            IntPtr h = CreateFile(@"\\.\WinRing0_1_2_0", 0xC0000000, FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
            if (h == (IntPtr)(-1)) return false;
            try
            {
                byte[] inb = BitConverter.GetBytes(idx); byte[] outb = new byte[8]; uint ret;
                if (!DeviceIoControl(h, IOCTL_READ_MSR, inb, 4, outb, 8, out ret, IntPtr.Zero)) return false;
                val = BitConverter.ToUInt64(outb, 0); return true;
            }
            finally { CloseHandle(h); }
        }
        public static bool Write(uint idx, ulong val)
        {
            IntPtr h = CreateFile(@"\\.\WinRing0_1_2_0", 0xC0000000, FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
            if (h == (IntPtr)(-1)) return false;
            try
            {
                byte[] inb = new byte[12];
                BitConverter.GetBytes(idx).CopyTo(inb, 0);
                BitConverter.GetBytes(val).CopyTo(inb, 4);
                uint ret;
                return DeviceIoControl(h, IOCTL_WRITE_MSR, inb, 12, null, 0, out ret, IntPtr.Zero);
            }
            finally { CloseHandle(h); }
        }
        public static double Pl1W()
        {
            ulong v; if (!Read(0x610, out v)) return -1;
            uint low = (uint)(v & 0xFFFFFFFF);
            return (low & 0x7FFF) * 0.125;
        }
        public static bool Locked()
        {
            ulong v; if (!Read(0x610, out v)) return true;
            return (v >> 63) != 0;
        }
        public static bool SetPl1Watts(double watts)
        {
            ulong v; if (!Read(0x610, out v)) return false;
            uint low = (uint)(v & 0xFFFFFFFF), high = (uint)(v >> 32);
            uint raw = (uint)Math.Round(watts * 8.0);
            if (raw > 0x7FFF) raw = 0x7FFF;
            low = (low & 4294934528u) | raw | 32768u; // clear bits14:0, set PL1 + enable bit15
            ulong nv = ((ulong)high << 32) | low;
            return Write(0x610, nv);
        }
    }

    // current CPU frequency via IA32_PERF_STATUS (0x198); bits 15:8 = ratio in 100 MHz units on this gen
    public static class MsrFreq
    {
        public static int CurrentMhz()
        {
            ulong v;
            if (!Msr.Read(0x198, out v)) return -1;
            int ratio = (int)((v >> 8) & 0xFF);
            return ratio > 4 && ratio < 120 ? ratio * 100 : -1;
        }
    }

    public static class Modes
    {
        public static string[] Names = { "静音模式", "均衡模式", "极限性能", "勿扰全静" };
        public static string[] Keys = { "quiet", "balanced", "performance", "dnd" };
        public static string[][] Meta = { Config.SilentCfg, Config.BalancedCfg, Config.PerfCfg, Config.DndCfg };

        public static string[] MetaFor(string mode)
        {
            if (mode == "quiet") return Config.SilentCfg;
            if (mode == "balanced") return Config.BalancedCfg;
            if (mode == "performance") return Config.PerfCfg;
            if (mode == "dnd") return Config.DndCfg;
            return null;
        }

        public static string KeyFromPlan(string guid)
        {
            if (guid == Config.SilentGuid) return "quiet";
            if (guid == Config.BalancedGuid) return "balanced";
            if (guid == Config.PerfGuid) return "performance";
            if (guid == Config.DndGuid) return "dnd";
            if (guid == Config.SystemGuid) return "restore";
            return null;
        }

        public static string NameOf(string key)
        {
            for (int i = 0; i < Keys.Length; i++) if (Keys[i] == key) return Names[i];
            return key == "restore" ? "恢复默认" : key;
        }

        // apply, then verify; on mismatch roll back to the mode that was active before
        public static string Apply(string mode)
        {
            string prev = KeyFromPlan(Config.ActivePlanGuid());
            UiLog.Step("切换前模式: " + NameOf(prev ?? "未知"));
            string r = ApplyCore(mode);
            UiLog.Step("4/4 回读校验中…");
            string err = Verify(mode);
            if (err == null) return r;
            Config.Log("verify failed for " + mode + ": " + err);
            if (prev != null && prev != mode)
            {
                UiLog.Action("校验失败，正在回滚到" + NameOf(prev) + "…");
                string back = ApplyCore(prev);
                Config.Log("rolled back to " + prev + ": " + back);
                return r + " | 校验失败，已回滚到" + NameOf(prev) + ": " + err;
            }
            return r + " | 校验失败: " + err;
        }

        static string ApplyCore(string mode)
        {
            string log = "";
            if (mode == "restore")
            {
                UiLog.Step("1/3 停止 NBFC（风扇控制权交还 EC 固件）");
                Config.Run(Config.NbfcExe, "stop");
                log += "NBFC stopped (EC auto). ";
                ulong orig;
                if (File.Exists(Config.MsrBackup) &&
                    ulong.TryParse(Regex.Match(File.ReadAllText(Config.MsrBackup), @"0x([0-9A-Fa-f]{16})").Groups[1].Value,
                    System.Globalization.NumberStyles.HexNumber, null, out orig))
                {
                    UiLog.Step("2/3 还原功耗墙 PL1");
                    if (Msr.Write(0x610, orig)) log += "PL restored. ";
                }
                UiLog.Step("3/3 切回系统默认电源方案");
                Config.Run("powercfg", "/setactive " + Config.SystemGuid);
                Config.Log("mode switch: restore");
                return log + "plan=system default";
            }
            string[] meta = MetaFor(mode);
            if (meta == null) return "unknown mode";
            if (mode != "performance")
            {
                // leaving performance: restore original PL if overridden
                ulong cur, orig;
                if (Msr.Read(0x610, out cur) && File.Exists(Config.MsrBackup) &&
                    ulong.TryParse(Regex.Match(File.ReadAllText(Config.MsrBackup), @"0x([0-9A-Fa-f]{16})").Groups[1].Value,
                    System.Globalization.NumberStyles.HexNumber, null, out orig) && cur != orig)
                { UiLog.Step("· 退出性能模式，还原功耗墙 PL1"); Msr.Write(0x610, orig); log += "PL restored. "; }
            }
            UiLog.Step("1/4 切换电源方案 → " + meta[0]);
            Config.Run("powercfg", "/setactive " + meta[1]);
            UiLog.Step("2/4 切换 NBFC 配置");
            Config.Run(Config.NbfcExe, "config -s \"" + meta[0] + "\"");
            System.Threading.Thread.Sleep(300);
            UiLog.Step("3/4 重启 NBFC 接管风扇");
            Config.Run(Config.NbfcExe, "start");
            if (mode == "performance")
            {
                ulong cur;
                if (Msr.Read(0x610, out cur))
                {
                    if (!File.Exists(Config.MsrBackup))
                        File.WriteAllText(Config.MsrBackup, "0x" + cur.ToString("X16"));
                    UiLog.Step("· 解锁 PL1 28W");
                    Msr.SetPl1Watts(28);
                    log += "PL1=28W. ";
                }
            }
            Config.Log("mode switch: " + mode);
            return log + "plan=" + meta[0];
        }

        public static string Verify(string mode)
        {
            string[] meta = MetaFor(mode);
            if (mode != "restore" && meta == null) return "未知模式";
            string want = mode == "restore" ? Config.SystemGuid : meta[1];
            if (Config.ActivePlanGuid() != want) return "电源方案未切换到目标值";
            if (mode == "restore")
            {
                int ecAuto = Config.EcRead(Profile.FanModeReg);
                if (ecAuto >= 0 && ecAuto != 0x04) return "EC 未回到自动模式 (0x" + ecAuto.ToString("X2") + ")";
                return null;
            }
            try
            {
                if (new ServiceController("NbfcService").Status != ServiceControllerStatus.Running) return "NBFC 服务未运行";
            }
            catch { return "NBFC 服务未安装"; }
            string st = Config.Run(Config.NbfcExe, "status", 8000);
            Match m = Regex.Match(st, @"Selected config name\s*:\s*(.+)");
            if (!m.Success || m.Groups[1].Value.Trim() != meta[0]) return "NBFC 配置未切换";
            int ecMode = Config.EcRead(Profile.FanModeReg);
            if (ecMode >= 0 && ecMode != 0x14 && ecMode != 0x1C) return "EC 未进入手动模式 (0x" + ecMode.ToString("X2") + ")";
            return null;
        }
    }

    public static class Curves
    {
        // default v2 curves: {up, down, speed}
        public static int[][][] Defaults = new int[][][] {
            new int[][] { new[]{0,0,0}, new[]{58,51,35}, new[]{64,58,55}, new[]{70,64,75}, new[]{74,68,100} },   // silent
            new int[][] { new[]{0,0,0}, new[]{50,44,40}, new[]{60,54,60}, new[]{70,64,85}, new[]{76,70,100} },   // balanced
            new int[][] { new[]{0,0,40}, new[]{44,38,65}, new[]{54,48,85}, new[]{63,57,100} },                    // performance
            new int[][] { new[]{0,0,0}, new[]{60,53,40}, new[]{68,61,70}, new[]{74,67,100} }                      // dnd
        };

        public static int[][] Load(int modeIdx)
        {
            try
            {
                string path = Config.MyConfigs + "\\" + Modes.Meta[modeIdx][0] + ".xml";
                XmlDocument doc = new XmlDocument(); doc.Load(path);
                var nodes = doc.SelectNodes("//TemperatureThresholds/TemperatureThreshold");
                var list = new System.Collections.Generic.List<int[]>();
                foreach (XmlNode n in nodes)
                {
                    int up = int.Parse(n["UpThreshold"].InnerText);
                    int down = int.Parse(n["DownThreshold"].InnerText);
                    double sp = double.Parse(n["FanSpeed"].InnerText, System.Globalization.CultureInfo.InvariantCulture);
                    list.Add(new[] { up, down, (int)Math.Round(sp) });
                }
                return list.ToArray();
            }
            catch { return Defaults[modeIdx]; }
        }

        public static string Save(int modeIdx, int[][] steps) // steps[0]={0,baseSpeed}; steps[i>0]={up,speed}; length varies
        {
            try
            {
                string path = Config.MyConfigs + "\\" + Modes.Meta[modeIdx][0] + ".xml";
                UiLog.Step("1/3 写入曲线 XML");
                XmlDocument doc = new XmlDocument(); doc.Load(path);
                XmlNode tths = doc.SelectSingleNode("//TemperatureThresholds");
                tths.RemoveAll();
                AddTh(tths, doc, 0, 0, steps[0][1]);
                for (int i = 1; i < steps.Length; i++)
                {
                    int up = steps[i][0], sp = steps[i][1];
                    int down = Math.Max(1, up - 7);
                    AddTh(tths, doc, up, down, sp);
                }
                doc.Save(path);
                UiLog.Step("2/3 同步到 NBFC 配置目录");
                File.Copy(path, Config.NbfcConfigs + "\\" + Modes.Meta[modeIdx][0] + ".xml", true);
                Config.Run(Config.NbfcExe, "config -s \"" + Modes.Meta[modeIdx][0] + "\"");
                System.Threading.Thread.Sleep(300);
                UiLog.Step("3/3 重启 NBFC 生效");
                Config.Run(Config.NbfcExe, "start");
                Config.Log("curve saved for " + Modes.Meta[modeIdx][0]);
                return "已保存并生效";
            }
            catch (Exception ex) { return "失败: " + ex.Message; }
        }

        static void AddTh(XmlNode parent, XmlDocument doc, int up, int down, int speed)
        {
            XmlNode n = doc.CreateElement("TemperatureThreshold");
            XmlElement e;
            e = doc.CreateElement("UpThreshold"); e.InnerText = up.ToString(); n.AppendChild(e);
            e = doc.CreateElement("DownThreshold"); e.InnerText = down.ToString(); n.AppendChild(e);
            e = doc.CreateElement("FanSpeed"); e.InnerText = speed.ToString(); n.AppendChild(e);
            parent.AppendChild(n);
        }

        public static string ResetDefaults(int modeIdx)
        {
            try
            {
                string path = Config.MyConfigs + "\\" + Modes.Meta[modeIdx][0] + ".xml";
                // regenerate from template saved at install: just re-save defaults over current xml thresholds
                UiLog.Step("1/3 重写默认阈值");
                XmlDocument doc = new XmlDocument(); doc.Load(path);
                XmlNode tths = doc.SelectSingleNode("//TemperatureThresholds");
                tths.RemoveAll();
                var d = Defaults[modeIdx];
                AddTh(tths, doc, 0, 0, d[0][2]);
                for (int i = 1; i < d.Length; i++) AddTh(tths, doc, d[i][0], d[i][1], d[i][2]);
                doc.Save(path);
                UiLog.Step("2/3 同步到 NBFC 配置目录");
                File.Copy(path, Config.NbfcConfigs + "\\" + Modes.Meta[modeIdx][0] + ".xml", true);
                Config.Run(Config.NbfcExe, "config -s \"" + Modes.Meta[modeIdx][0] + "\"");
                System.Threading.Thread.Sleep(300);
                UiLog.Step("3/3 重启 NBFC 生效");
                Config.Run(Config.NbfcExe, "start");
                return "已恢复默认曲线并生效";
            }
            catch (Exception ex) { return "失败: " + ex.Message; }
        }
    }

    public static class SelfTest
    {
        public static System.Collections.Generic.List<string[]> RunAll()
        {
            var results = new System.Collections.Generic.List<string[]>();
            results.Add(new[] { "机型适配", Profile.Supported ? "OK" : "WARN", Profile.Model + (Profile.Supported ? "（已适配）" : "（未适配，只读）") });
            bool isAdmin = false;
            try { isAdmin = new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator); } catch { }
            results.Add(new[] { "管理员权限", isAdmin ? "OK" : "FAIL", isAdmin ? "已提权" : "未提权（功能受限）" });

            try { var svc = new ServiceController("NbfcService"); results.Add(new[] { "NBFC 服务", svc.Status == ServiceControllerStatus.Running ? "OK" : "FAIL", svc.Status.ToString() }); }
            catch (Exception ex) { results.Add(new[] { "NBFC 服务", "FAIL", ex.Message }); }

            bool drvOpen = false; ulong v;
            if (Msr.Read(0x610, out v)) drvOpen = true;
            results.Add(new[] { "WinRing0 驱动/MSR", drvOpen ? "OK" : "FAIL", drvOpen ? ("PL1=" + Msr.Pl1W().ToString("0.#") + "W, locked=" + Msr.Locked()) : "无法打开驱动设备" });

            int m = Config.EcRead(Profile.FanModeReg);
            results.Add(new[] { "EC 访问", (m == 4 || m == 20) ? "OK" : "FAIL", "mode(0x93)=0x" + m.ToString("X2") + (m == 4 ? " (自动)" : m == 20 ? " (手动)" : " (异常)") });

            int dd = Config.EcRead(Profile.FanDutyReg), fb = Config.EcRead(Profile.FanFeedbackReg);
            results.Add(new[] { "EC 占空比/回读", (dd >= 0 && fb >= 0) ? "OK" : "FAIL", string.Format("0x{0:X2} / 0x{1:X2} (差 {2:+0;-0;0})", dd, fb, fb - dd) });

            int t = Config.EcRead(Profile.CpuTempReg);
            results.Add(new[] { "温度传感器", (t >= 20 && t <= 95) ? "OK" : "FAIL", "EC 0x" + Profile.CpuTempReg.ToString("X2") + "=" + t + " C" });

            int cfgOk = 0; string missing = "";
            foreach (var meta in Modes.Meta)
            {
                string f = Config.NbfcConfigs + "\\" + meta[0] + ".xml";
                if (File.Exists(f)) cfgOk++; else missing += meta[0] + " ";
            }
            results.Add(new[] { "NBFC 配置文件", cfgOk == 4 ? "OK" : "FAIL", cfgOk + "/4" + (missing == "" ? "" : ", 缺: " + missing) });

            int planOk = 0;
            foreach (var g in new[] { Config.SilentGuid, Config.BalancedGuid, Config.PerfGuid, Config.DndGuid })
                if (Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Power\User\PowerSchemes\" + g) != null) planOk++;
            results.Add(new[] { "电源方案", planOk == 4 ? "OK" : "FAIL", planOk + "/4 个 Z-fan 方案" });

            results.Add(new[] { "功耗墙备份", File.Exists(Config.MsrBackup) ? "OK" : "WARN", File.Exists(Config.MsrBackup) ? File.ReadAllText(Config.MsrBackup).Trim() : "尚无（进一次极限模式后生成）" });

            bool taskExists = false, taskEnabled = false;
            try
            {
                using (var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Schedule\TaskCache\Tree\" + Config.TaskName))
                {
                    taskExists = key != null;
                }
                string q = Config.Run("schtasks", "/Query /TN " + Config.TaskName + " /FO CSV /NH", 8000, 1);
                taskEnabled = q != null && !q.Contains("禁用");
            }
            catch { }
            results.Add(new[] { "智能自动任务", taskExists ? (taskEnabled ? "ON" : "OFF (已创建)") : "未创建", taskExists ? (taskEnabled ? "运行中/就绪" : "已禁用") : "可在工具页开启" });

            return results;
        }

        public static void CliRun()
        {
            var sb = new StringBuilder();
            sb.AppendLine("==== FanManager self-test " + DateTime.Now + " ====");
            foreach (var r in RunAll()) sb.AppendLine(string.Format("[{0}] {1}: {2}", r[1], r[0], r[2]));
            File.WriteAllText(Config.LogDir + @"\app-selftest.txt", sb.ToString(), Encoding.UTF8);
        }
    }

    // ---- UI preferences (data/ui-prefs.txt, gitignored) ----
    public static class Prefs
    {
        public static int MaxFanRpm = 7000;   // full-speed RPM used by the estimated-RPM display
        public static int WinLeft = int.MinValue, WinTop = int.MinValue, WinWidth = 0, WinHeight = 0;
        public static bool WinMaximized;
        public static int ActivityHeight = 150;
        static string Path { get { return Config.BaseDir + @"\data\ui-prefs.txt"; } }

        static int ParseInt(string v, int fallback)
        {
            int n; return int.TryParse(v, out n) ? n : fallback;
        }

        public static void Load()
        {
            try
            {
                foreach (var line in File.ReadAllLines(Path))
                {
                    var kv = line.Split('=');
                    if (kv.Length < 2) continue;
                    var k = kv[0].Trim(); var v = kv[1].Trim();
                    int n;
                    if (k == "MaxFanRpm" && int.TryParse(v, out n) && n >= 2000 && n <= 15000) MaxFanRpm = n;
                    else if (k == "WinLeft") WinLeft = ParseInt(v, WinLeft);
                    else if (k == "WinTop") WinTop = ParseInt(v, WinTop);
                    else if (k == "WinWidth") WinWidth = ParseInt(v, 0);
                    else if (k == "WinHeight") WinHeight = ParseInt(v, 0);
                    else if (k == "WinMaximized") WinMaximized = v == "1";
                    else if (k == "ActivityHeight") { n = ParseInt(v, ActivityHeight); if (n >= 80 && n <= 400) ActivityHeight = n; }
                }
            }
            catch { }
        }

        public static void Save()
        {
            try
            {
                Directory.CreateDirectory(Config.BaseDir + @"\data");
                File.WriteAllLines(Path, new[]
                {
                    "MaxFanRpm=" + MaxFanRpm,
                    "WinLeft=" + WinLeft,
                    "WinTop=" + WinTop,
                    "WinWidth=" + WinWidth,
                    "WinHeight=" + WinHeight,
                    "WinMaximized=" + (WinMaximized ? "1" : "0"),
                    "ActivityHeight=" + ActivityHeight
                });
            }
            catch { }
        }
    }

    // ---- background status engine -------------------------------------------------------------
    // The old design ran every poll (nbfc status, 3x ec-probe read, powercfg, schtasks,
    // ServiceController) on the UI thread every 5 seconds; any slow child process froze
    // the window, which is exactly why clicks "did nothing". All polling now happens on
    // one worker thread that publishes an immutable snapshot; the UI timer only paints.
    public class StatusSnapshot
    {
        public int Temp = -1, Duty = -1, EcMode = -1, Feedback = -1;
        public string NbfcTemp = "-", FanCur = "-", FanTgt = "-", Cfg = "-";
        public double FanPct = -1;
        public int EstRpm = -1;
        public string FanState = "未知";
        public string PlanName = "未知", PlanGuid = "", SvcText = "-", AutoText = "-";
        public double Pl = -1, Watts = -1;
        public int CpuMhz = -1;
        public bool PlLocked;
        public DateTime Stamp = DateTime.MinValue;
        public string Err = "";
    }

    public static class StatusEngine
    {
        static readonly object gate = new object();
        static StatusSnapshot latest = new StatusSnapshot();
        static System.Threading.Thread worker;
        static volatile bool kicked;      // refresh immediately on next wake-up
        public static int IntervalMs = 5000;

        // set by MainForm: guard inputs/outputs for the manual-fan overheat watchdog
        public static Func<bool> ManualEngaged;
        public static Action<int> GuardTriggered;

        public static StatusSnapshot Latest { get { lock (gate) return latest; } }

        public static void Start()
        {
            lock (gate)
            {
                if (worker != null && worker.IsAlive) return;
                worker = new System.Threading.Thread(Loop) { IsBackground = true, Name = "StatusEngine" };
                worker.Start();
            }
        }

        public static void Kick() { kicked = true; }

        static void Loop()
        {
            UiLog.Log("后台状态引擎已启动（每 " + (IntervalMs / 1000) + " 秒自动刷新）");
            int failStreak = 0;
            int warnLevel = 0;          // 0 normal, 1 hot (>=75C) — one notice per episode with hysteresis
            var slowLogStamp = DateTime.MinValue;
            while (true)
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                StatusSnapshot s;
                try { s = Collect(); }
                catch (Exception ex) { s = new StatusSnapshot { Err = ex.Message }; }
                sw.Stop();
                lock (gate) latest = s;

                // engine health: a single cycle taking as long as the interval means the
                // machine/child processes are degraded - surface it instead of failing silently
                if (sw.ElapsedMilliseconds > IntervalMs && (DateTime.Now - slowLogStamp).TotalMinutes > 10)
                {
                    slowLogStamp = DateTime.Now;
                    UiLog.Log("状态刷新偏慢：本轮耗时 " + sw.ElapsedMilliseconds + " ms（周期 " + IntervalMs + " ms）");
                }

                // overheat notice (>=75C on, <70C off) — EC/NBFC temps are read in Collect
                if (s.Temp >= 75 && warnLevel == 0)
                {
                    warnLevel = 1;
                    UiLog.Action("温度偏高：" + s.Temp + "℃（≥75℃，注意负载/通风；曲线过热兜底在 " +
                        "CriticalTemperature 强制 100%）");
                    Config.Log("overheat notice: " + s.Temp + "C");
                }
                else if (s.Temp > 0 && s.Temp < 70 && warnLevel == 1)
                {
                    warnLevel = 0;
                    UiLog.Log("温度已回落：" + s.Temp + "℃");
                }

                if (s.Err == "" && failStreak > 0) UiLog.Log("状态刷新已恢复");
                if (s.Err != "" && ++failStreak % 6 == 1) UiLog.Log("状态刷新失败: " + s.Err);
                if (s.Err == "") failStreak = 0;

                kicked = false;
                int left = IntervalMs;
                while (left > 0 && !kicked) { System.Threading.Thread.Sleep(150); left -= 150; }
            }
        }

        static StatusSnapshot Collect()
        {
            var s = new StatusSnapshot();
            try
            {
                // one ec-probe dump per cycle instead of 3-4 separate spawns; falls back to reads
                byte[] ec = ParseEcDump(Config.EcDump());
                if (ec != null)
                {
                    s.EcMode = ec[Profile.FanModeReg];
                    s.Duty = ec[Profile.FanDutyReg];
                    s.Feedback = ec[Profile.FanFeedbackReg];
                    s.Temp = ec[Profile.CpuTempReg];
                    if (s.Temp < 5 || s.Temp > 95) s.Temp = -1;   // implausible -> treat as read failure
                }
                else
                {
                    s.Temp = Config.EcRead(Profile.CpuTempReg);
                    s.Duty = Config.EcRead(Profile.FanDutyReg);
                    s.EcMode = Config.EcRead(Profile.FanModeReg);
                }
                string st = Config.Run(Config.NbfcExe, "status", 8000);
                s.Pl = Msr.Pl1W();
                s.CpuMhz = MsrFreq.CurrentMhz();
                s.PlLocked = Msr.Locked();
                s.Watts = Telemetry.PackageWatts();
                s.PlanGuid = Config.ActivePlanGuid();
                if (s.PlanGuid == Config.SilentGuid) s.PlanName = "静音模式";
                else if (s.PlanGuid == Config.BalancedGuid) s.PlanName = "均衡模式";
                else if (s.PlanGuid == Config.PerfGuid) s.PlanName = "极限性能";
                else if (s.PlanGuid == Config.DndGuid) s.PlanName = "勿扰全静";
                else if (s.PlanGuid == Config.SystemGuid) s.PlanName = "系统默认(节能)";

                Match m;
                if ((m = Regex.Match(st, @"Selected config name\s*:\s*(.+)")).Success) s.Cfg = m.Groups[1].Value.Trim();
                if ((m = Regex.Match(st, @"Temperature\s*:\s*([\d.\-]+)")).Success) s.NbfcTemp = m.Groups[1].Value;
                if ((m = Regex.Match(st, @"Current fan speed\s*:\s*([\d.\-]+)")).Success) s.FanCur = m.Groups[1].Value;
                if ((m = Regex.Match(st, @"Target fan speed\s*:\s*([\d.\-]+)")).Success) s.FanTgt = m.Groups[1].Value;
                if (!double.TryParse(s.FanCur, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out s.FanPct)) s.FanPct = -1;

                if (s.FanPct >= 0) s.EstRpm = (int)Math.Round(s.FanPct / 100.0 * Prefs.MaxFanRpm);
                s.FanState = FanStateOf(s.FanPct, s.Duty);

                try { s.SvcText = new ServiceController("NbfcService").Status.ToString(); }
                catch { s.SvcText = "未安装"; }
                s.AutoText = Extensions.TaskSchedulerFallback();
                // prediction display: mirror auto-mode.ps1's rules (>=62C x2 -> balanced, <=54C x5 -> quiet)
                if (s.AutoText == "ON")
                {
                    try
                    {
                        string sf = Config.BaseDir + @"\logs\auto-mode-state.txt";
                        int hot = -1, cool = -1;
                        foreach (var ln in File.ReadAllLines(sf))
                        {
                            if (ln.StartsWith("hot=", StringComparison.Ordinal)) int.TryParse(ln.Substring(4), out hot);
                            if (ln.StartsWith("cool=", StringComparison.Ordinal)) int.TryParse(ln.Substring(5), out cool);
                        }
                        if (s.PlanGuid == Config.SilentGuid && hot >= 0) s.AutoText = string.Format("ON（{0}/2 次 ≥62℃ → 升均衡）", hot);
                        else if (s.PlanGuid == Config.BalancedGuid && cool >= 0) s.AutoText = string.Format("ON（{0}/5 次 ≤54℃ → 回静音）", cool);
                    }
                    catch { }
                }

                Telemetry.Add(s.Temp, s.FanPct < 0 ? -1 : (int)Math.Round(s.FanPct), s.Watts, s.CpuMhz);

                // manual-fan overheat guard: same rule as before, now on the worker thread
                try
                {
                    var g = ManualEngaged;
                    if (g != null && g() && s.Temp >= 80)
                    {
                        Config.Run(Config.NbfcExe, "set -a");
                        var fired = GuardTriggered;
                        if (fired != null) fired(s.Temp);
                    }
                }
                catch (Exception ex) { Config.Log("manual guard error: " + ex.Message); }

                s.Err = "";
            }
            catch (Exception ex) { s.Err = ex.Message; }
            s.Stamp = DateTime.Now;
            return s;
        }

        // fan state semantics: NBFC % when available, otherwise raw duty bands (reversed mapping)
        public static string FanStateOf(double fanPct, int duty)
        {
            if (fanPct >= 0)
            {
                if (fanPct <= 0.5) return "停转";
                if (fanPct <= 40) return "低速";
                if (fanPct <= 70) return "中速";
                return "高速";
            }
            if (duty < 0) return "未知";
            if (duty >= 0xC8) return "停转";
            if (duty >= 0xA4) return "低速";
            if (duty >= 0x90) return "中速";
            return "高速";
        }

        // parse ec-probe dump output ("90 | 00 01 00 04 86 88 ...") into a 256-byte array; null if unusable
        public static byte[] ParseEcDump(string dump)
        {
            if (string.IsNullOrEmpty(dump)) return null;
            try
            {
                var buf = new byte[256];
                bool any = false;
                foreach (var line in dump.Split('\n'))
                {
                    var m = Regex.Match(line, @"^\s*([0-9A-Fa-f]{2})\s*\|\s*([0-9A-Fa-f]{2}(?:\s+[0-9A-Fa-f]{2})*)\s*$");
                    if (!m.Success) continue;
                    int row = Convert.ToInt32(m.Groups[1].Value, 16);
                    var cells = m.Groups[2].Value.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                    for (int i = 0; i < cells.Length && row + i < 256; i++)
                    {
                        buf[row + i] = Convert.ToByte(cells[i], 16);
                        any = true;
                    }
                }
                return any ? buf : null;
            }
            catch { return null; }
        }
    }

    public class MainForm : Form
    {
        [DllImport("user32.dll")] static extern bool RegisterHotKey(IntPtr hWnd, int id, uint mods, uint vk);
        [DllImport("user32.dll")] static extern bool UnregisterHotKey(IntPtr hWnd, int id);
        const uint MOD_ALT = 1, MOD_CONTROL = 2;

        Timer timer;
        Label lblMode, lblTemp, lblFan, lblFanRpm, lblDuty, lblPl, lblSvc, lblAuto, lblHint, lblModel;
        Label lblMonPl, lblMonHint, lblMonStats;
        Button[] modeButtons;
        Button btnRestore, btnManualApply, btnManualAuto, btnCurveSave, btnCurveReset, btnCurveAdd, btnCurveDel;
        bool manualEngaged = false;
        bool busy;
        bool exiting;
        int lastTemp = -1;
        TabControl tabs;
        NotifyIcon tray;

        ComboBox cmbCurveMode; Label lblCurveStatus;
        TableLayoutPanel curveGrid;
        CurvePreview curvePreview;
        readonly List<int[]> curveRows = new List<int[]>();

        ComboBox cmbFreq; Button btnDptfStop, btnDptfStart; Label lblDptf, lblToolStatus;
        ListView lvTest; Button btnTest, btnDump;
        TextBox txtLog;
        TrendChart chart;
        Label lblNow;           // one-line "what is the app doing right now"
        TextBox txtActivity;    // activity log panel (always visible, bottom)
        Panel activityPanel;
        int activityTrim = 0;
        TableLayoutPanel banner;
        Label lblBanner;
        Button btnBannerFix;
        bool isElevated;
        bool bannerAdmin;
        bool bannerDismissed;

        public MainForm()
        {
            Profile.Load();
            Prefs.Load();
            Icon = App.Icon;   // taskbar / title bar icon from ui\app.ico
            try { isElevated = new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator); } catch { }
            bool restored = Prefs.WinWidth >= 400 && Prefs.WinHeight >= 400 && Prefs.WinLeft > -40000;
            if (restored)
            {
                StartPosition = FormStartPosition.Manual;
                Location = new Point(Prefs.WinLeft, Prefs.WinTop);
                Size = new Size(Prefs.WinWidth, Prefs.WinHeight);
                if (Prefs.WinMaximized) WindowState = FormWindowState.Maximized;
            }
            Text = "风扇管家 - RedmiBook 14 增强版 2020";
            MinimumSize = new Size(560, 700);
            if (!restored) Size = new Size(560, 700);
            StartPosition = FormStartPosition.CenterScreen;
            Font = new Font("Microsoft YaHei UI", 9F);
            tabs = new TabControl { Dock = DockStyle.Fill };
            tabs.TabPages.Add(BuildMainTab());
            tabs.TabPages.Add(BuildMonitorTab());
            tabs.TabPages.Add(BuildCurveTab());
            tabs.TabPages.Add(BuildToolTab());
            tabs.TabPages.Add(BuildTestTab());
            Controls.Add(tabs);
            BuildActivityBar();
            BuildBanner();
            if (!isElevated) ShowBanner("未以管理员运行：EC/MSR 读取不可用，功能受限", true);
            ApplyProfileGates();
            timer = new Timer { Interval = 1000 };   // paints from the engine snapshot only - never blocks
            timer.Tick += (s, e) => PaintStatus();
            Load += OnFormLoad;
            FormClosing += OnFormClosing;
            Resize += (s, e) => { if (WindowState == FormWindowState.Minimized) Hide(); };
            // in-app shortcuts: 1-4 switch modes, 5 restores defaults (Ctrl+Alt+N works globally).
            // Ignored while typing in a numeric/combo/phone-like control, or "5" in the curve
            // editor would fire a mode switch instead of entering a digit.
            KeyPreview = true;
            KeyDown += (s, e) =>
            {
                var act = ActiveControl;
                if (act is TextBoxBase || act is NumericUpDown || act is ComboBox) return;
                int idx = (int)(e.KeyCode - Keys.D1);
                if (idx >= 0 && idx < Modes.Keys.Length) ApplyMode(Modes.Keys[idx]);
                else if (e.KeyCode == Keys.D5) ApplyMode("restore");
            };
            if (!App.Headless) BuildTray();
        }

        // bottom activity bar: one-line current action + scrolling timestamped log.
        // Operations run on background threads; UiLog events arrive cross-thread -> BeginInvoke.
        void BuildActivityBar()
        {
            var panel = new Panel { Dock = DockStyle.Bottom, Height = Prefs.ActivityHeight, Padding = new Padding(6, 2, 6, 6) };
            activityPanel = panel;
            var head = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 28, Padding = new Padding(1, 4, 0, 0) };
            lblNow = new Label
            {
                Text = "状态: 空闲",
                AutoSize = true,
                Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Bold),
                ForeColor = Color.FromArgb(20, 90, 160),
                Padding = new Padding(0, 2, 12, 0)
            };
            var btnClear = new Button { Text = "清空", Width = 46, Height = 24 };
            btnClear.Click += (s, e) => { txtActivity.Clear(); activityTrim = 0; };
            var btnOpenLog = new Button { Text = "日志文件", Width = 70, Height = 24 };
            btnOpenLog.Click += (s, e) => { try { Process.Start(Config.LogDir); } catch { } };
            head.Controls.Add(lblNow);
            head.Controls.Add(btnClear);
            head.Controls.Add(btnOpenLog);
            txtActivity = new TextBox
            {
                Dock = DockStyle.Fill,
                Multiline = true,
                ReadOnly = true,
                ScrollBars = ScrollBars.Vertical,
                BackColor = Color.White,
                Font = new Font("Consolas", 8.5F)
            };
            panel.Controls.Add(txtActivity);
            panel.Controls.Add(head);
            Controls.Add(panel);   // added after tabs -> docks first, tabs keep the rest

            UiLog.Entry += line => { try { BeginInvoke((Action)(() => AppendActivity(line))); } catch { } };
            UiLog.ActionChanged += text =>
            {
                try
                {
                    BeginInvoke((Action)(() =>
                    {
                        lblNow.Text = "状态: " + text;
                        // semantic coloring: green for done, red for failures, blue otherwise
                        lblNow.ForeColor = text.StartsWith("完成") ? Color.FromArgb(20, 130, 60)
                            : text.StartsWith("失败") ? Color.Firebrick
                            : Color.FromArgb(20, 90, 160);
                    }));
                }
                catch { }
            };
            foreach (var l in UiLog.Recent()) AppendActivity(l);
        }

        // top warning banner: not-admin / NBFC service down; with one-click self re-elevation.
        // Self-sizing on purpose: a fixed height clipped the buttons once the text wrapped.
        void BuildBanner()
        {
            banner = new TableLayoutPanel
            {
                Dock = DockStyle.Top,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                ColumnCount = 2,
                RowCount = 1,
                Padding = new Padding(8, 6, 4, 6),
                BackColor = Color.FromArgb(255, 244, 214),
                Visible = false
            };
            banner.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            banner.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            banner.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            lblBanner = new Label
            {
                Text = "",
                AutoSize = true,
                MaximumSize = new Size(400, 0),   // caps width so long messages wrap and the row grows
                ForeColor = Color.FromArgb(140, 80, 0),
                Margin = new Padding(0, 2, 8, 2)
            };
            btnBannerFix = new Button { Text = "重新提权启动", Width = 110, Height = 25, Margin = new Padding(0) };
            btnBannerFix.Click += (s, e) => ReElevate();
            var btnHide = new Button { Text = "×", Width = 26, Height = 25, Margin = new Padding(6, 0, 0, 0) };
            btnHide.Click += (s, e) => { bannerDismissed = true; banner.Visible = false; };
            var buttons = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = false, Margin = new Padding(0) };
            buttons.Controls.Add(btnBannerFix);
            buttons.Controls.Add(btnHide);
            banner.Controls.Add(lblBanner, 0, 0);
            banner.Controls.Add(buttons, 1, 0);
            banner.SizeChanged += (s, e) => LayoutBanner();
            Controls.Add(banner);
        }

        void LayoutBanner()
        {
            if (banner == null || lblBanner == null) return;
            int reserved = (btnBannerFix != null && btnBannerFix.Visible ? 150 : 44);
            int avail = banner.ClientSize.Width - reserved - 20;
            if (avail < 200) avail = 200;
            Size want = new Size(avail, 0);
            if (lblBanner.MaximumSize != want) lblBanner.MaximumSize = want;
        }

        void ShowBanner(string text, bool adminIssue)
        {
            if (adminIssue) { bannerAdmin = true; bannerDismissed = false; }
            if (bannerDismissed) return;
            lblBanner.Text = text;
            if (btnBannerFix != null) btnBannerFix.Visible = bannerAdmin;
            banner.Visible = true;
            LayoutBanner();
        }

        void ReElevate()
        {
            try
            {
                var psi = new ProcessStartInfo("powershell.exe",
                    "-NoProfile -ExecutionPolicy Bypass -File \"" + Config.BaseDir + "\\ui\\FanManager.ps1\"")
                { Verb = "RunAs", UseShellExecute = true };
                Process.Start(psi);
                UiLog.Action("已请求以管理员重新启动，当前实例退出…");
                exiting = true;
                Close();
            }
            catch (Exception ex)
            {
                UiLog.Fail("重新提权失败: " + ex.Message);
            }
        }

        void AppendActivity(string line)
        {
            if (txtActivity == null || txtActivity.IsDisposed) return;
            txtActivity.AppendText(line + Environment.NewLine);
            if (++activityTrim > 400)
            {
                var keep = txtActivity.Lines;
                int n = Math.Min(240, keep.Length);
                var tail = new string[n];
                Array.Copy(keep, keep.Length - n, tail, 0, n);
                txtActivity.Text = string.Join(Environment.NewLine, tail);
                activityTrim = 0;
                txtActivity.SelectionStart = txtActivity.TextLength;
                txtActivity.ScrollToCaret();
            }
        }

        void OnFormLoad(object sender, EventArgs e)
        {
            StatusEngine.ManualEngaged = () => manualEngaged;
            StatusEngine.GuardTriggered = temp =>
            {
                manualEngaged = false;
                Config.Log("manual fan guard triggered at " + temp + "C");
                try
                {
                    BeginInvoke((Action)(() =>
                    {
                        lblHint.Text = "温度≥80℃，手动模式已自动交还！";
                        UiLog.Action("温度≥80℃，手动风扇已自动交还自动曲线！");
                        Notify("手动模式已交还", "温度达到 " + temp + "℃，风扇回到自动曲线", ToolTipIcon.Warning);
                    }));
                }
                catch { }
            };
            StatusEngine.Start();
            if (!App.Headless) RegisterHotkeys();
            timer.Start();   // paints from the snapshot; safe (and needed) in headless/screenshot mode too
        }

        void OnFormClosing(object sender, FormClosingEventArgs e)
        {
            if (manualEngaged) { Config.Run(Config.NbfcExe, "set -a"); }
            if (exiting || App.Headless)
            {
                StatusEngine.ManualEngaged = null;
                StatusEngine.GuardTriggered = null;
                Prefs.WinMaximized = WindowState == FormWindowState.Maximized;
                Rectangle rb = WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;
                if (rb.Width >= 400)
                {
                    Prefs.WinLeft = rb.Left; Prefs.WinTop = rb.Top;
                    Prefs.WinWidth = rb.Width; Prefs.WinHeight = rb.Height;
                }
                if (activityPanel != null) Prefs.ActivityHeight = activityPanel.Height;
                Prefs.Save();
                try { UnregisterHotkeys(); } catch { }
                if (tray != null) { tray.Visible = false; tray.Dispose(); tray = null; }
                return;
            }
            e.Cancel = true;
            Hide();
            Notify("风扇管家仍在运行", "已最小化到托盘，双击图标恢复；退出请用托盘菜单", ToolTipIcon.Info);
        }

        void BuildTray()
        {
            var menu = new ContextMenuStrip();
            menu.Items.Add("显示主界面", null, (s, e) => ShowFromTray());
            menu.Items.Add(new ToolStripSeparator());
            for (int i = 0; i < Modes.Keys.Length; i++)
            {
                string key = Modes.Keys[i];
                ToolStripItem item = menu.Items.Add(Modes.Names[i] + "  (Ctrl+Alt+" + (i + 1) + ")", null, (s, e) => ApplyMode(key));
                item.Enabled = Profile.Supported;
            }
            ToolStripItem restoreItem = menu.Items.Add("恢复默认", null, (s, e) => ApplyMode("restore"));
            restoreItem.Enabled = Profile.Supported;
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("退出", null, (s, e) => { exiting = true; Close(); });
            tray = new NotifyIcon { Icon = App.Icon, Text = "风扇管家", Visible = true, ContextMenuStrip = menu };
            tray.DoubleClick += (s, e) => ShowFromTray();
            tray.BalloonTipClicked += (s, e) => ShowFromTray();
        }

        void ShowFromTray()
        {
            Show();
            WindowState = FormWindowState.Normal;
            Activate();
        }

        void Notify(string title, string text, ToolTipIcon icon)
        {
            if (tray == null || !tray.Visible) return;
            try { tray.ShowBalloonTip(3000, title, text, icon); } catch { }
        }

        void RegisterHotkeys()
        {
            for (int i = 0; i < Modes.Keys.Length; i++)
                try { RegisterHotKey(Handle, i + 1, MOD_CONTROL | MOD_ALT, (uint)(0x31 + i)); } catch { }
        }

        void UnregisterHotkeys()
        {
            for (int i = 0; i < Modes.Keys.Length; i++)
                try { UnregisterHotKey(Handle, i + 1); } catch { }
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == 0x0312)
            {
                int id = m.WParam.ToInt32();
                if (id >= 1 && id <= Modes.Keys.Length) ApplyMode(Modes.Keys[id - 1]);
            }
            base.WndProc(ref m);
        }

        bool TryBeginBusy()
        {
            if (busy) return false;
            busy = true;
            foreach (var b in modeButtons) b.Enabled = false;
            btnRestore.Enabled = false;
            return true;
        }

        void EndBusy()
        {
            busy = false;
            foreach (var b in modeButtons) b.Enabled = Profile.Supported;
            btnRestore.Enabled = Profile.Supported;
        }

        void ApplyMode(string mode)
        {
            if (!Profile.Supported)
            {
                lblHint.Text = "未适配机型（" + Profile.Model + "）：已阻止写入风扇/电源设置";
                Notify("已阻止切换", "当前机型未适配，仅支持查看状态", ToolTipIcon.Warning);
                return;
            }
            if (!TryBeginBusy())
            {
                // a click during a running operation used to vanish silently - tell the user instead
                UiLog.Action("上一操作还在进行中，请稍候…（已忽略本次点击）");
                Notify("请稍候", "上一个操作还没结束", ToolTipIcon.Warning);
                return;
            }
            UiLog.Action("正在切换到「" + Modes.NameOf(mode) + "」…");
            Task.Run(() => Modes.Apply(mode)).ContinueWith(t =>
            {
                string r = t.IsFaulted ? "error: " + t.Exception.GetBaseException().Message : t.Result;
                Config.Log("apply " + mode + ": " + r);
                try
                {
                    BeginInvoke((Action)(() =>
                    {
                        EndBusy();
                        UiLog.Done(Modes.NameOf(mode) + " — " + r);
                        if (mode != "restore" && cmbCurveMode != null)
                        {
                            int mi = Array.IndexOf(Modes.Keys, mode);
                            if (mi >= 0) cmbCurveMode.SelectedIndex = mi;   // curve page follows the applied mode
                        }
                        StatusEngine.Kick();
                        Notify(Modes.NameOf(mode), r, r.Contains("失败") ? ToolTipIcon.Warning : ToolTipIcon.Info);
                    }));
                }
                catch { }
            });
        }

        TabPage BuildMainTab()
        {
            var page = new TabPage("主面板");
            var top = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 100, Padding = new Padding(8) };
            var tips = new ToolTip { AutoPopDelay = 8000, InitialDelay = 400 };
            modeButtons = new Button[4];
            string[] labels = { "静音模式", "均衡模式", "极限性能", "勿扰全静" };
            string[] tipTexts = {
                "≤58℃ 风扇全程 0 转，之后最低档轻柔介入\n电源: 60% 状态、睿频关 —— 日常默认",
                "≤50℃ 停转，随温度平滑上升\n电源: 全性能 + 睿频",
                "风扇先行领跑，PL1 解锁 28W + EPP 0 + 激进睿频\n重负载/外接电源时用，退出自动还原功耗墙",
                "≤60℃ 风扇 0 转\n电源: 50% 状态 —— 会议/影音/夜间",
            };
            for (int i = 0; i < 4; i++)
            {
                var b = new Button { Text = labels[i], Size = new Size(118, 36), Tag = new[] { "quiet", "balanced", "performance", "dnd" }[i] };
                string captured = (string)b.Tag;
                b.Click += (s, e) => ApplyMode(captured);
                tips.SetToolTip(b, tipTexts[i] + "\n快捷键: " + (i + 1) + " / Ctrl+Alt+" + (i + 1));
                modeButtons[i] = b; top.Controls.Add(b);
            }
            btnRestore = new Button { Text = "恢复默认", Size = new Size(118, 36) };
            btnRestore.Click += (s, e) => ApplyMode("restore");
            tips.SetToolTip(btnRestore, "停用 NBFC，风扇交还 EC 固件自动曲线，电源回系统默认\n快捷键: 5");
            top.Controls.Add(btnRestore);

            var info = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, Padding = new Padding(10) };
            lblModel = MkLabel(info, "机型: -");
            lblMode = MkLabel(info, "当前模式: -");
            lblTemp = MkLabel(info, "温度: -");
            lblFan = MkLabel(info, "风扇: -");
            lblFanRpm = MkLabel(info, "风扇转速: -");
            lblDuty = MkLabel(info, "EC 占空比: -");
            lblPl = MkLabel(info, "功耗墙 PL1: -");
            lblSvc = MkLabel(info, "NBFC 服务: -");
            lblAuto = MkLabel(info, "智能自动: -");
            lblHint = MkLabel(info, "提示：悬停模式按钮查看各模式说明；窗口内按 1-4 切模式、5 恢复默认");

            var manualGroup = new GroupBox { Text = "手动风扇（测试/特殊用途，温度≥80℃ 自动交还自动控制）", Dock = DockStyle.Top, Height = 100 };
            var slider = new TrackBar { Minimum = 0, Maximum = 100, TickFrequency = 10, Value = 35, Location = new Point(140, 22), Width = 260 };
            var lblManVal = new Label { Text = "35%", Location = new Point(410, 28), AutoSize = true };
            btnManualApply = new Button { Text = "应用手动转速", Width = 120, Location = new Point(12, 60) };
            btnManualAuto = new Button { Text = "回到自动曲线", Width = 120, Location = new Point(140, 60) };
            slider.ValueChanged += (s, e) => lblManVal.Text = slider.Value + "%";
            btnManualApply.Click += (s, e) =>
            {
                if (!Profile.Supported) { lblHint.Text = "未适配机型，禁止写入"; return; }
                if (!TryBeginBusy()) { UiLog.Action("上一操作还在进行中，请稍候…"); return; }
                int v = slider.Value;
                UiLog.Action("正在设置手动风扇 " + v + "%…");
                Task.Run(() => Config.Run(Config.NbfcExe, "set -s " + v)).ContinueWith(t =>
                {
                    manualEngaged = true;
                    BeginInvoke((Action)(() =>
                    {
                        EndBusy();
                        string msg = "手动模式 " + v + "%（温度≥80℃ 会自动交还）";
                        lblHint.Text = msg;
                        UiLog.Done(msg);
                    }));
                });
            };
            btnManualAuto.Click += (s, e) =>
            {
                if (!TryBeginBusy()) { UiLog.Action("上一操作还在进行中，请稍候…"); return; }
                UiLog.Action("正在回到自动曲线…");
                Task.Run(() => Config.Run(Config.NbfcExe, "set -a")).ContinueWith(t =>
                {
                    manualEngaged = false;
                    BeginInvoke((Action)(() =>
                    {
                        EndBusy();
                        lblHint.Text = "已回到自动曲线";
                        UiLog.Done("手动风扇已交还自动曲线");
                    }));
                });
            };
            manualGroup.Controls.Add(slider); manualGroup.Controls.Add(lblManVal);
            manualGroup.Controls.Add(btnManualApply); manualGroup.Controls.Add(btnManualAuto);

            page.Controls.Add(info);
            page.Controls.Add(manualGroup);
            page.Controls.Add(top);
            ApplyProfileGates();
            return page;
        }

        void ApplyProfileGates()
        {
            lblModel.Text = "机型: " + Profile.Model + (Profile.Supported ? "（已适配）" : "（未适配，只读）");
            lblModel.ForeColor = Profile.Supported ? Color.Black : Color.Firebrick;
            if (Profile.Supported) return;
            foreach (var b in modeButtons) { b.Enabled = false; b.ForeColor = Color.Gray; }
            btnRestore.Enabled = false;
            btnManualApply.Enabled = false;
            btnManualAuto.Enabled = false;
            if (btnCurveSave != null) btnCurveSave.Enabled = false;
            if (btnCurveReset != null) btnCurveReset.Enabled = false;
            if (btnCurveAdd != null) btnCurveAdd.Enabled = false;
            if (btnCurveDel != null) btnCurveDel.Enabled = false;
            lblHint.Text = "未适配机型：风扇写入已禁用，仅显示状态与诊断";
        }

        Label MkLabel(FlowLayoutPanel p, string text)
        {
            var l = new Label { Text = text, AutoSize = true, Font = new Font("Microsoft YaHei UI", 9.5F) };
            p.Controls.Add(l); return l;
        }

        TabPage BuildMonitorTab()
        {
            var page = new TabPage("监控");
            var top = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 64, Padding = new Padding(8) };
            lblMonPl = new Label { Text = "实时功耗: -", AutoSize = true, Padding = new Padding(0, 8, 20, 0), Font = new Font("Microsoft YaHei UI", 9.5F) };
            var btnClear = new Button { Text = "清除历史", Width = 90 };
            btnClear.Click += (s, e) => { Telemetry.Clear(); chart.Invalidate(); };
            var btnCsv = new Button { Text = "导出 CSV", Width = 90 };
            btnCsv.Click += (s, e) =>
            {
                try
                {
                    string path = Telemetry.ExportCsv(Config.LogDir + "\\history-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".csv");
                    UiLog.Done("历史数据已导出: " + path);
                    if (!App.Headless) Process.Start("explorer.exe", "/select,\"" + path + "\"");
                }
                catch (Exception ex) { UiLog.Fail("导出 CSV 失败: " + ex.Message); }
            };
            top.Controls.Add(lblMonPl);
            top.Controls.Add(btnClear);
            top.Controls.Add(btnCsv);
            chart = new TrendChart { Dock = DockStyle.Fill };
            lblMonStats = new Label
            {
                Dock = DockStyle.Bottom,
                Height = 26,
                Padding = new Padding(10, 4, 0, 0),
                Text = "10 分钟统计: 采样中…"
            };
            lblMonHint = new Label
            {
                Dock = DockStyle.Bottom,
                Height = 30,
                Padding = new Padding(10, 6, 0, 0),
                Text = "红=温度 蓝=风扇% 绿=频率 虚线=当前温度；每 5 秒采样，保留 10 分钟"
            };
            page.Controls.Add(chart);
            page.Controls.Add(lblMonHint);
            page.Controls.Add(lblMonStats);
            page.Controls.Add(top);
            return page;
        }

        void UpdateMonitorStats()
        {
            if (lblMonStats == null) return;
            var data = Telemetry.Copy();
            if (data.Count < 2) { lblMonStats.Text = "10 分钟统计: 采样中…"; return; }
            int tMin = int.MaxValue, tMax = int.MinValue; long tSum = 0;
            int dMin = int.MaxValue, dMax = int.MinValue;
            double wMin = double.MaxValue, wMax = double.MinValue;
            int n = 0;
            foreach (var s in data)
            {
                if (s.Temp > 0) { tMin = Math.Min(tMin, s.Temp); tMax = Math.Max(tMax, s.Temp); tSum += s.Temp; n++; }
                if (s.Duty >= 0) { dMin = Math.Min(dMin, s.Duty); dMax = Math.Max(dMax, s.Duty); }
                if (s.Watts >= 0) { wMin = Math.Min(wMin, s.Watts); wMax = Math.Max(wMax, s.Watts); }
            }
            if (n == 0) { lblMonStats.Text = "10 分钟统计: 无有效样本"; return; }
            lblMonStats.Text = string.Format("10 分钟统计: 温度 {0}-{1}℃(均值 {2:0.#})   风扇 {3}-{4}%   功耗 {5}-{6} W   样本 {7}",
                tMin, tMax, (double)tSum / n,
                dMin == int.MaxValue ? "-" : dMin.ToString(), dMax == int.MinValue ? "-" : dMax.ToString(),
                wMin == double.MaxValue ? "-" : wMin.ToString("0.#"), wMax == double.MinValue ? "-" : wMax.ToString("0.#"),
                data.Count);
        }

        TabPage BuildCurveTab()
        {
            var page = new TabPage("风扇曲线");
            var top = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 40, Padding = new Padding(8) };
            top.Controls.Add(new Label { Text = "模式:", AutoSize = true, Padding = new Padding(0, 8, 0, 0) });
            cmbCurveMode = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 130 };
            cmbCurveMode.Items.AddRange(new object[] { "静音模式", "均衡模式", "极限性能", "勿扰全静" });
            cmbCurveMode.SelectedIndex = 0;
            top.Controls.Add(cmbCurveMode);
            var btnLoad = new Button { Text = "读取当前", Width = 80 };
            top.Controls.Add(btnLoad);
            btnCurveReset = new Button { Text = "恢复默认曲线", Width = 100 };
            top.Controls.Add(btnCurveReset);
            btnCurveSave = new Button { Text = "保存并生效", Width = 100 };
            top.Controls.Add(btnCurveSave);

            var editBar = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 36, Padding = new Padding(8, 4, 8, 4) };
            btnCurveAdd = new Button { Text = "＋ 增加一档", Width = 96 };
            btnCurveDel = new Button { Text = "－ 删除末档", Width = 96 };
            editBar.Controls.Add(btnCurveAdd);
            editBar.Controls.Add(btnCurveDel);

            curveGrid = new TableLayoutPanel { Dock = DockStyle.Top, ColumnCount = 3, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(8) };
            curveGrid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            curvePreview = new CurvePreview { Dock = DockStyle.Fill };
            lblCurveStatus = new Label { Dock = DockStyle.Bottom, Height = 26, Padding = new Padding(8, 4, 0, 0), Text = "回差自动 = 触发温度 - 7℃；最多 8 档" };

            LoadCurve();
            cmbCurveMode.SelectedIndexChanged += (s, e) => LoadCurve();
            btnLoad.Click += (s, e) => LoadCurve();
            btnCurveAdd.Click += (s, e) =>
            {
                if (!Profile.Supported) { lblCurveStatus.Text = "未适配机型，禁止写入"; return; }
                AddCurveRow();
            };
            btnCurveDel.Click += (s, e) =>
            {
                if (!Profile.Supported) { lblCurveStatus.Text = "未适配机型，禁止写入"; return; }
                RemoveCurveRow();
            };
            btnCurveReset.Click += (s, e) =>
            {
                if (!Profile.Supported) { lblCurveStatus.Text = "未适配机型，禁止写入"; return; }
                if (!TryBeginBusy()) { UiLog.Action("上一操作还在进行中，请稍候…"); return; }
                int idx = cmbCurveMode.SelectedIndex;
                UiLog.Action("正在恢复" + Modes.Names[idx] + "默认曲线…");
                Task.Run(() => Curves.ResetDefaults(idx)).ContinueWith(t =>
                {
                    string r = t.IsFaulted ? "error: " + t.Exception.GetBaseException().Message : t.Result;
                    try { BeginInvoke((Action)(() => { lblCurveStatus.Text = r; UiLog.Done("恢复默认曲线 — " + r); LoadCurve(); EndBusy(); })); } catch { }
                });
            };
            btnCurveSave.Click += (s, e) =>
            {
                if (!Profile.Supported) { lblCurveStatus.Text = "未适配机型，禁止写入"; return; }
                for (int i = 2; i < curveRows.Count; i++)
                {
                    if (curveRows[i][0] <= curveRows[i - 1][0]) { lblCurveStatus.Text = "触发温度必须逐档递增"; return; }
                }
                if (!TryBeginBusy()) { UiLog.Action("上一操作还在进行中，请稍候…"); return; }
                int idx = cmbCurveMode.SelectedIndex;
                int[][] rows = curveRows.ToArray();
                UiLog.Action("正在保存" + Modes.Names[idx] + "风扇曲线…");
                Task.Run(() => Curves.Save(idx, rows)).ContinueWith(t =>
                {
                    string r = t.IsFaulted ? "error: " + t.Exception.GetBaseException().Message : t.Result;
                    try { BeginInvoke((Action)(() => { lblCurveStatus.Text = r; UiLog.Done("保存曲线 — " + r); EndBusy(); })); } catch { }
                });
            };

            page.Controls.Add(curvePreview);
            page.Controls.Add(lblCurveStatus);
            page.Controls.Add(curveGrid);
            page.Controls.Add(editBar);
            page.Controls.Add(top);
            return page;
        }

        void LoadCurve()
        {
            int[][] steps = Curves.Load(cmbCurveMode.SelectedIndex);
            curveRows.Clear();
            curveRows.Add(new[] { 0, Clamp(steps[0][2], 0, 100) });
            for (int i = 1; i < steps.Length; i++)
                curveRows.Add(new[] { Clamp(steps[i][0], 1, 95), Clamp(steps[i][2], 0, 100) });
            RebuildCurveGrid();
        }

        void RebuildCurveGrid()
        {
            curveGrid.SuspendLayout();
            curveGrid.Controls.Clear();
            curveGrid.RowCount = curveRows.Count + 1;
            curveGrid.RowStyles.Clear();
            curveGrid.Controls.Add(new Label { Text = "档位", AutoSize = true }, 0, 0);
            curveGrid.Controls.Add(new Label { Text = "触发温度 ℃", AutoSize = true }, 1, 0);
            curveGrid.Controls.Add(new Label { Text = "转速 %", AutoSize = true }, 2, 0);
            for (int i = 0; i < curveRows.Count; i++)
            {
                int[] item = curveRows[i];
                curveGrid.Controls.Add(new Label { Text = i == 0 ? "常驻(低于起转点)" : "第" + i + "档阈值", AutoSize = true }, 0, i + 1);
                if (i > 0)
                {
                    var up = new NumericUpDown { Minimum = 1, Maximum = 95, Value = Clamp(item[0], 1, 95), Width = 70 };
                    up.ValueChanged += (s, e) => { item[0] = (int)up.Value; UpdateCurvePreview(); };
                    curveGrid.Controls.Add(up, 1, i + 1);
                }
                var sp = new NumericUpDown { Minimum = 0, Maximum = 100, Value = Clamp(item[1], 0, 100), Width = 70 };
                sp.ValueChanged += (s, e) => { item[1] = (int)sp.Value; UpdateCurvePreview(); };
                curveGrid.Controls.Add(sp, 2, i + 1);
            }
            curveGrid.ResumeLayout();
            UpdateCurvePreview();
        }

        void AddCurveRow()
        {
            if (curveRows.Count >= 9) { lblCurveStatus.Text = "最多 8 档（不含常驻）"; return; }
            int[] last = curveRows[curveRows.Count - 1];
            int up = Math.Min(95, last[0] + 6);
            if (up <= last[0]) { lblCurveStatus.Text = "已经是最高温度档"; return; }
            curveRows.Add(new[] { up, last[1] });
            RebuildCurveGrid();
        }

        void RemoveCurveRow()
        {
            if (curveRows.Count <= 2) { lblCurveStatus.Text = "至少保留一档"; return; }
            curveRows.RemoveAt(curveRows.Count - 1);
            RebuildCurveGrid();
        }

        void UpdateCurvePreview()
        {
            if (curvePreview == null) return;
            curvePreview.Rows = curveRows;
            curvePreview.CurrentTemp = lastTemp;
            curvePreview.Invalidate();
        }

        int Clamp(int v, int lo, int hi) { return v < lo ? lo : v > hi ? hi : v; }
        public void SelectTab(int i) { if (tabs != null && i >= 0 && i < tabs.TabPages.Count) tabs.SelectedIndex = i; }

        TabPage BuildToolTab()
        {
            var page = new TabPage("工具");
            // 5 sections no longer fit the viewport once the activity bar exists -> allow scrolling
            var fl = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, Padding = new Padding(10), AutoScroll = true, WrapContents = false };

            var g1 = new GroupBox { Text = "智能自动（温度驱动的静音↔均衡自动切换）", Width = 510, Height = 92 };
            var bOn = new Button { Text = "开启", Width = 90, Location = new Point(12, 24) };
            var bOff = new Button { Text = "关闭", Width = 90, Location = new Point(110, 24) };
            var lblAutoState = new Label { Text = "", AutoSize = true, Location = new Point(210, 28) };
            Action<bool> runAuto = enable =>
            {
                if (!TryBeginBusy()) { UiLog.Action("上一操作还在进行中，请稍候…"); return; }
                UiLog.Action(enable ? "正在开启智能自动（创建/启用计划任务）…" : "正在关闭智能自动…");
                Task.Run(() => Config.Run("powershell", "-NoProfile -ExecutionPolicy Bypass -File \"" + Config.ScriptsDir + "\\lib\\install-auto.ps1\" " + (enable ? "enable" : "disable"), 30000))
                    .ContinueWith(t =>
                    {
                        string r = t.Result == null ? "" : t.Result.Trim();
                        BeginInvoke((Action)(() =>
                        {
                            lblAutoState.Text = r;
                            Extensions.InvalidateTaskState();
                            StatusEngine.Kick();
                            EndBusy();
                            UiLog.Done("智能自动已" + (enable ? "开启" : "关闭") + (r == "" ? "" : " — " + r));
                        }));
                    });
            };
            bOn.Click += (s, e) => runAuto(true);
            bOff.Click += (s, e) => runAuto(false);
            g1.Controls.Add(bOn); g1.Controls.Add(bOff); g1.Controls.Add(lblAutoState);
            fl.Controls.Add(g1);

            var g2 = new GroupBox { Text = "全局频率上限 (MHz，作用于全部 Z-fan 方案；0=不限)", Width = 510, Height = 92 };
            cmbFreq = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 100, Location = new Point(12, 24) };
            cmbFreq.Items.AddRange(new object[] { "不限", "1800", "2000", "2200", "2500", "2800" });
            cmbFreq.SelectedIndex = 0;
            var bFreq = new Button { Text = "应用", Width = 80, Location = new Point(120, 22) };
            lblToolStatus = new Label { Text = "", AutoSize = true, Location = new Point(210, 28) };
            bFreq.Click += (s, e) =>
            {
                if (!TryBeginBusy()) { UiLog.Action("上一操作还在进行中，请稍候…"); return; }
                int mhz = cmbFreq.SelectedIndex == 0 ? 0 : int.Parse((string)cmbFreq.SelectedItem);
                UiLog.Action("正在设置全局频率上限（8 个电源方案值）…");
                Task.Run(() =>
                {
                    foreach (var g in new[] { Config.SilentGuid, Config.BalancedGuid, Config.PerfGuid, Config.DndGuid })
                    {
                        Config.Run("powercfg", "/setacvalueindex " + g + " " + Config.SubProcessor + " " + Config.FreqGuid + " " + mhz);
                        Config.Run("powercfg", "/setdcvalueindex " + g + " " + Config.SubProcessor + " " + Config.FreqGuid + " " + mhz);
                    }
                    string active = Config.ActivePlanGuid();
                    if (active.StartsWith("9a10e4a1")) Config.Run("powercfg", "/setactive " + active);
                    Config.Log("freq cap set to " + mhz + " MHz");
                }).ContinueWith(t => BeginInvoke((Action)(() =>
                {
                    EndBusy();
                    lblToolStatus.Text = "频率上限已应用: " + (mhz == 0 ? "不限" : mhz + " MHz");
                    UiLog.Done("频率上限: " + (mhz == 0 ? "不限" : mhz + " MHz"));
                })));
            };
            g2.Controls.Add(cmbFreq); g2.Controls.Add(bFreq); g2.Controls.Add(lblToolStatus);
            fl.Controls.Add(g2);

            var g5 = new GroupBox { Text = "风扇转速估算（本机 EC 无转速计数器，按占空比折算；实测满载转速后可校准）", Width = 510, Height = 92 };
            var numMaxRpm = new NumericUpDown { Minimum = 2000, Maximum = 15000, Increment = 100, Value = Prefs.MaxFanRpm, Location = new Point(12, 26), Width = 90 };
            var lblRpmHint = new Label
            {
                Text = "「≈N RPM」= NBFC 风扇% × 满载转速。\n用 BIOS/其它工具读到真实满载转速后改这个值即可校准。",
                AutoSize = true, Location = new Point(115, 18)
            };
            numMaxRpm.ValueChanged += (s, e) => { Prefs.MaxFanRpm = (int)numMaxRpm.Value; Prefs.Save(); };
            g5.Controls.Add(numMaxRpm); g5.Controls.Add(lblRpmHint);
            fl.Controls.Add(g5);

            var g3 = new GroupBox { Text = "Intel DPTF (esifsvc) —— 极限模式可选解锁项；停止后睿频更高，热策略交由固件", Width = 510, Height = 92 };
            btnDptfStop = new Button { Text = "停止 DPTF", Width = 100, Location = new Point(12, 24) };
            btnDptfStart = new Button { Text = "启动 DPTF", Width = 100, Location = new Point(120, 24) };
            lblDptf = new Label { Text = DptfStatus(), AutoSize = true, Location = new Point(230, 28) };
            Action<bool> runDptf = stop =>
            {
                if (!TryBeginBusy()) { UiLog.Action("上一操作还在进行中，请稍候…"); return; }
                UiLog.Action(stop ? "正在停止 Intel DPTF 服务（最长 30 秒）…" : "正在启动 Intel DPTF 服务…");
                Task.Run(() => Config.Run("net", stop ? "stop esifsvc" : "start esifsvc", 30000))
                    .ContinueWith(t =>
                    {
                        BeginInvoke((Action)(() =>
                        {
                            lblDptf.Text = DptfStatus();
                            EndBusy();
                            Config.Log(stop ? "DPTF stopped" : "DPTF started");
                            UiLog.Done("DPTF 已" + (stop ? "停止" : "启动") + "，" + lblDptf.Text);
                        }));
                    });
            };
            btnDptfStop.Click += (s, e) => runDptf(true);
            btnDptfStart.Click += (s, e) => runDptf(false);
            g3.Controls.Add(btnDptfStop); g3.Controls.Add(btnDptfStart); g3.Controls.Add(lblDptf);
            fl.Controls.Add(g3);

            var g4 = new GroupBox { Text = "诊断", Width = 510, Height = 92 };
            btnDump = new Button { Text = "转储 EC 到日志", Width = 130, Location = new Point(12, 24) };
            btnDump.Click += (s, e) =>
            {
                if (!TryBeginBusy()) { UiLog.Action("上一操作还在进行中，请稍候…"); return; }
                UiLog.Action("正在转储 EC 全部寄存器…");
                Task.Run(() =>
                {
                    string outp = Config.Run(Config.EcProbe, "dump", 60000);
                    File.WriteAllText(Config.LogDir + "\\ec-dump-app-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".txt", outp, Encoding.UTF8);
                }).ContinueWith(t => BeginInvoke((Action)(() =>
                {
                    EndBusy();
                    lblToolStatus.Text = "EC 转储已保存到 logs 文件夹";
                    UiLog.Done("EC 转储已保存到 logs 文件夹");
                })));
            };
            var btnFolder = new Button { Text = "打开日志文件夹", Width = 130, Location = new Point(150, 24) };
            btnFolder.Click += (s, e) => Process.Start(Config.LogDir);
            var btnDiag = new Button { Text = "导出诊断包", Width = 110, Location = new Point(288, 24) };
            btnDiag.Click += (s, e) =>
            {
                if (!TryBeginBusy()) { UiLog.Action("上一操作还在进行中，请稍候…"); return; }
                btnDiag.Enabled = false;
                lblToolStatus.Text = "正在打包…";
                UiLog.Action("正在导出诊断包（日志+EC转储+自测+环境信息）…");
                Task.Run(() => Diagnostics.Export()).ContinueWith(t =>
                {
                    string r = t.IsFaulted ? "失败: " + t.Exception.GetBaseException().Message : t.Result;
                    try
                    {
                        BeginInvoke((Action)(() =>
                        {
                            btnDiag.Enabled = true;
                            EndBusy();
                            lblToolStatus.Text = File.Exists(r) ? "诊断包: " + r : r;
                            UiLog.Done(File.Exists(r) ? "诊断包已生成: " + r : "诊断包导出失败: " + r);
                            if (File.Exists(r)) Process.Start("explorer.exe", "/select,\"" + r + "\"");
                        }));
                    }
                    catch { }
                });
            };
            g4.Controls.Add(btnDump); g4.Controls.Add(btnFolder); g4.Controls.Add(btnDiag);
            fl.Controls.Add(g4);

            page.Controls.Add(fl);
            return page;
        }

        string DptfStatus()
        {
            try { var s = new ServiceController("esifsvc"); return "状态: " + s.Status; }
            catch { return "未安装"; }
        }

        TabPage BuildTestTab()
        {
            var page = new TabPage("自测");
            var top = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 44, Padding = new Padding(8) };
            btnTest = new Button { Text = "运行完整自测", Width = 140 };
            top.Controls.Add(btnTest);
            lvTest = new ListView { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true };
            lvTest.Columns.Add("项目", 160); lvTest.Columns.Add("结果", 90); lvTest.Columns.Add("详情", 260);
            txtLog = new TextBox { Dock = DockStyle.Bottom, Multiline = true, Height = 120, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Font = new Font("Consolas", 8.5F) };
            btnTest.Click += (s, e) =>
            {
                lvTest.Items.Clear();
                btnTest.Enabled = false;
                UiLog.Action("正在运行完整自测（10 项）…");
                Task.Run(() => SelfTest.RunAll()).ContinueWith(t =>
                {
                    BeginInvoke((Action)(() =>
                    {
                        btnTest.Enabled = true;
                        foreach (var r in t.Result)
                        {
                            var it = lvTest.Items.Add(r[0]);
                            it.SubItems.Add(r[1]); it.SubItems.Add(r[2]);
                            it.ForeColor = r[1].StartsWith("OK") || r[1].StartsWith("ON") ? Color.Green : r[1] == "WARN" || r[1].StartsWith("OFF") ? Color.DarkOrange : Color.Red;
                        }
                        string summary = DateTime.Now + "\r\n" + string.Join("\r\n", t.Result.Select(r => string.Format("[{0}] {1}: {2}", r[1], r[0], r[2])));
                        File.WriteAllText(Config.LogDir + "\\app-selftest.txt", summary, Encoding.UTF8);
                        txtLog.Text = summary;
                        int bad = t.Result.Count(r => r[1] == "FAIL");
                        UiLog.Done(bad == 0 ? "自测通过（" + t.Result.Count + " 项，无 FAIL）" : "自测完成，" + bad + " 项 FAIL");
                    }));
                });
            };
            page.Controls.Add(lvTest);
            page.Controls.Add(txtLog);
            page.Controls.Add(top);
            // show the previous run's results immediately (fresh run overwrites on demand)
            try { if (File.Exists(Config.LogDir + "\\app-selftest.txt")) txtLog.Text = File.ReadAllText(Config.LogDir + "\\app-selftest.txt", Encoding.UTF8); } catch { }
            return page;
        }

        // paint the latest engine snapshot onto the controls; must stay free of any blocking call
        void PaintStatus()
        {
            StatusSnapshot s = StatusEngine.Latest;
            try
            {
                if (s.Stamp == DateTime.MinValue)
                {
                    lblMode.Text = "当前模式: 读取中…";
                    return;
                }
                lblMode.Text = "当前模式: " + s.PlanName + "   (" + s.Cfg + ")";
                lblTemp.Text = "温度: EC " + (s.Temp < 0 ? "读取失败" : s.Temp + "℃") + " / NBFC " + s.NbfcTemp + "℃";
                // thermal glanceability: black < 60C, orange 60-74C, red >= 75C
                lblTemp.ForeColor = s.Temp >= 75 ? Color.Firebrick : s.Temp >= 60 ? Color.DarkOrange : Color.Black;
                lblFan.Text = string.Format("风扇: 当前 {0}% / 目标 {1}%", s.FanCur, s.FanTgt);
                lblFanRpm.Text = s.EstRpm >= 0
                    ? string.Format("风扇转速: ≈{0} RPM（估算 · {1}）", s.EstRpm, s.FanState)
                    : "风扇转速: " + s.FanState;
                lblDuty.Text = s.Duty < 0 || s.EcMode < 0 ? "EC 占空比: 读取失败"
                    : string.Format("EC 占空比: 0x{0:X2}{3}  模式: 0x{1:X2}{2}", s.Duty, s.EcMode,
                        s.EcMode == 4 ? " (EC自动)" : s.EcMode == 20 ? " (NBFC手动)" : " (直接手动)",
                        s.Feedback >= 0 ? " (回读 0x" + s.Feedback.ToString("X2") + ")" : "");
                lblPl.Text = s.Pl > 0
                    ? "功耗墙 PL1: " + s.Pl.ToString("0.#") + "W" + (s.PlLocked ? " (已锁)" : "")
                    : "功耗墙 PL1: 读取失败";
                lblSvc.Text = "NBFC 服务: " + s.SvcText;
                lblAuto.Text = "智能自动: " + s.AutoText;

                // service banner (admin banner takes priority and is never auto-hidden).
                // Only touch it when the state actually changed, otherwise the 1s repaint would
                // re-open a banner the user just dismissed.
                if (!bannerAdmin && banner != null)
                {
                    if (s.SvcText != "Running")
                    {
                        string want = "NBFC 服务未运行（状态: " + s.SvcText + "）——风扇由 EC 固件自动控制，模式切换不可用";
                        if (!bannerDismissed && (!banner.Visible || lblBanner.Text != want)) ShowBanner(want, false);
                    }
                    else
                    {
                        bannerDismissed = false;
                        if (banner.Visible && lblBanner.Text.StartsWith("NBFC")) banner.Visible = false;
                    }
                }

                lastTemp = s.Temp;
                if (chart != null) chart.Invalidate();
                UpdateMonitorStats();
                UpdateCurvePreview();
                if (lblMonPl != null)
                    lblMonPl.Text = string.Format("实时功耗: {0}   功耗墙: {1}   CPU频率: {2}",
                        s.Watts > 0 ? s.Watts.ToString("0.0") + " W" : "读取失败",
                        s.Pl > 0 ? s.Pl.ToString("0.#") + " W" : "-",
                        s.CpuMhz > 0 ? (s.CpuMhz / 1000.0).ToString("0.00") + " GHz" : "-");

                Color idle = Profile.Supported ? Color.Black : Color.Gray;
                modeButtons[0].ForeColor = s.PlanGuid == Config.SilentGuid ? Color.Green : idle;
                modeButtons[1].ForeColor = s.PlanGuid == Config.BalancedGuid ? Color.Green : idle;
                modeButtons[2].ForeColor = s.PlanGuid == Config.PerfGuid ? Color.Green : idle;
                modeButtons[3].ForeColor = s.PlanGuid == Config.DndGuid ? Color.Green : idle;

                if (tray != null && tray.Visible)
                {
                    string t = s.Temp < 0 ? "-" : s.Temp.ToString();
                    string p = s.FanPct < 0 ? "-" : Math.Round(s.FanPct).ToString();
                    try { tray.Text = "风扇管家  " + t + "℃  风扇 " + p + "%  " + s.PlanName; } catch { }
                }
            }
            catch (Exception ex) { UiLog.Log("界面刷新失败: " + ex.Message); }
        }
    }

    // tiny helper: scheduled task state via schtasks CSV (GBK); cached for 30s (6 polls at 5s)
    public static class Extensions
    {
        static string autoState;
        static int autoStateAge = 6;

        public static void InvalidateTaskState() { autoStateAge = 6; }

        public static string TaskSchedulerFallback()
        {
            if (autoStateAge < 6) { autoStateAge++; return autoState; }
            autoStateAge = 0;
            string q = Config.Run("schtasks", "/Query /TN " + Config.TaskName + " /FO CSV /NH", 8000, 1);
            if (q == null || q.Contains("ERR") || q.Trim() == "") autoState = "未创建";
            else if (q.Contains("禁用")) autoState = "OFF";
            else autoState = "ON";
            return autoState;
        }
    }

    public static class App
    {
        [DllImport("user32.dll")] static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);
        [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr hwnd, int nCmdShow);
        public static bool Headless = false;
        public static int ShotTab = 0;
        public static void SetShotTab(int i) { ShotTab = i; Headless = true; }

        static Icon appIcon;
        // ui\app.ico ships with the repo; fall back to the stock icon if it is missing or unreadable
        public static Icon Icon
        {
            get
            {
                if (appIcon == null)
                {
                    try
                    {
                        string p = Config.BaseDir + @"\ui\app.ico";
                        if (File.Exists(p)) appIcon = new Icon(p);
                    }
                    catch { }
                    if (appIcon == null) appIcon = SystemIcons.Application;
                }
                return appIcon;
            }
        }

        // last-resort handlers: a crash should leave evidence in logs instead of vanishing silently
        public static void InstallErrorHandlers()
        {
            // NOTE: SetUnhandledExceptionMode() throws once ANY control exists on the thread
            // (the startup splash creates one first), so rely on the default Automatic mode:
            // with a ThreadException handler registered, UI-thread exceptions are routed here.
            Application.ThreadException += (s, e) =>
            {
                Config.Log("UI exception: " + e.Exception);
                UiLog.Fail("程序异常: " + e.Exception.Message);
            };
            AppDomain.CurrentDomain.UnhandledException += (s, e) =>
            {
                Config.Log("FATAL: " + e.ExceptionObject);
                try { File.WriteAllText(Config.LogDir + "\\app-error.txt", DateTime.Now + "\r\n" + e.ExceptionObject, Encoding.UTF8); } catch { }
            };
        }

        public static void Run()
        {
            InstallErrorHandlers();
            const string mutexName = "FanManager_TM1901_SingleInstance";
            bool created;
            using (var mutex = new System.Threading.Mutex(true, mutexName, out created))
            {
                if (!created) { MessageBox.Show("风扇管家已在运行。"); return; }
                try { Application.EnableVisualStyles(); } catch { }
                try { Application.SetCompatibleTextRenderingDefault(false); } catch { }
                // no-ops when FanManager.ps1 already set them before the startup splash;
                // these APIs throw once a control exists on the thread, hence the guards
                var form = new MainForm();
                // VBS/提权用的是 SW_HIDE 启动，STARTUPINFO 会吞掉首次 ShowWindow，这里再显式显示一次
                form.Shown += (s, e) => ShowWindow(form.Handle, 5);
                Application.Run(form);
            }
        }
        public static void RunScreenshot()
        {
            InstallErrorHandlers();
            var form = new MainForm();
            form.Load += (s, e) =>
            {
                var t = new Timer { Interval = 11000 };   // two engine cycles so the RAPL delta (watts) is primed
                t.Tick += (s2, e2) =>
                {
                    t.Stop();
                    try
                    {
                        form.SelectTab(ShotTab);
                        Application.DoEvents();
                        System.Threading.Thread.Sleep(600);
                        var bmp = new Bitmap(form.Width, form.Height);
                        form.DrawToBitmap(bmp, new Rectangle(0, 0, form.Width, form.Height));
                        bmp.Save(Config.LogDir + "\\app-window-" + ShotTab + ".png", System.Drawing.Imaging.ImageFormat.Png);
                    }
                    catch (Exception ex) { Config.Log("screenshot failed: " + ex.Message); }
                    form.Close();
                };
                t.Start();
            };
            Application.Run(form);
        }
    }
}
