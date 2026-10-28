// FanManager.exe - RedmiBook 14 2020 (TM1901) fan & power manager
// Single-file WinForms app, .NET Framework 4.8 (in-box), admin manifest.
// Build: csc /codepage:65001 /target:winexe /out:FanManager.exe /r:System.dll /r:System.Core.dll
//        /r:System.Drawing.dll /r:System.Windows.Forms.dll /r:System.ServiceProcess.dll
//        /win32manifest:app.manifest Program.cs
// Modes: quiet/balanced/performance/dnd/restore. EC 0x93/0x94, MSR 0x610 via WinRing0_1_2_0.
using System;
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
            const string mutexName = "FanManager_TM1901_SingleInstance";
            bool created;
            using (var mutex = new System.Threading.Mutex(true, mutexName, out created))
            {
                if (!created) { MessageBox.Show("风扇管家已在运行。"); return; }
                string arg = args.Length > 0 ? args[0].ToLower() : "";
                if (arg == "--selftest") { SelfTest.CliRun(); return; }
                if (arg == "--screenshot") { App.RunScreenshot(); return; }
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new MainForm());
            }
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
                    string so = p.StandardOutput.ReadToEnd();
                    p.WaitForExit(timeoutMs);
                    return so;
                }
            }
            catch (Exception ex) { return "ERR: " + ex.Message; }
        }

        public static int EcRead(int reg)
        {
            string outp = Run(EcProbe, "read " + reg, 8000);
            Match m = Regex.Match(outp, @"\((0x[0-9A-Fa-f]+)\)");
            if (m.Success) return Convert.ToInt32(m.Groups[1].Value, 16);
            return -1;
        }

        public static string ActivePlanGuid()
        {
            string s = Run("powercfg", "/getactivescheme", 8000, 1);
            Match m = Regex.Match(s, @"([0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12})");
            return m.Success ? m.Groups[1].Value.ToLower() : "";
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

    public static class Modes
    {
        public static string[] Names = { "静音模式", "均衡模式", "极限性能", "勿扰全静" };
        public static string[][] Meta = { Config.SilentCfg, Config.BalancedCfg, Config.PerfCfg, Config.DndCfg };

        public static string Apply(string mode)
        {
            string log = "";
            if (mode == "restore")
            {
                Config.Run(Config.NbfcExe, "stop");
                log += "NBFC stopped (EC auto). ";
                ulong orig;
                if (File.Exists(Config.MsrBackup) &&
                    ulong.TryParse(Regex.Match(File.ReadAllText(Config.MsrBackup), @"0x([0-9A-Fa-f]{16})").Groups[1].Value,
                    System.Globalization.NumberStyles.HexNumber, null, out orig))
                {
                    if (Msr.Write(0x610, orig)) log += "PL restored. ";
                }
                Config.Run("powercfg", "/setactive " + Config.SystemGuid);
                return log + "plan=system default";
            }
            string[] meta = null;
            if (mode == "quiet") meta = Config.SilentCfg;
            else if (mode == "balanced") meta = Config.BalancedCfg;
            else if (mode == "performance") meta = Config.PerfCfg;
            else if (mode == "dnd") meta = Config.DndCfg;
            if (meta == null) return "unknown mode";
            if (mode != "performance")
            {
                // leaving performance: restore original PL if overridden
                ulong cur, orig;
                if (Msr.Read(0x610, out cur) && File.Exists(Config.MsrBackup) &&
                    ulong.TryParse(Regex.Match(File.ReadAllText(Config.MsrBackup), @"0x([0-9A-Fa-f]{16})").Groups[1].Value,
                    System.Globalization.NumberStyles.HexNumber, null, out orig) && cur != orig)
                { Msr.Write(0x610, orig); log += "PL restored. "; }
            }
            Config.Run("powercfg", "/setactive " + meta[1]);
            Config.Run(Config.NbfcExe, "config -s \"" + meta[0] + "\"");
            System.Threading.Thread.Sleep(300);
            Config.Run(Config.NbfcExe, "start");
            if (mode == "performance")
            {
                ulong cur;
                if (Msr.Read(0x610, out cur))
                {
                    if (!File.Exists(Config.MsrBackup))
                        File.WriteAllText(Config.MsrBackup, "0x" + cur.ToString("X16"));
                    Msr.SetPl1Watts(28);
                    log += "PL1=28W. ";
                }
            }
            Config.Log("mode switch: " + mode);
            return log + "plan=" + meta[0];
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
                File.Copy(path, Config.NbfcConfigs + "\\" + Modes.Meta[modeIdx][0] + ".xml", true);
                Config.Run(Config.NbfcExe, "config -s \"" + Modes.Meta[modeIdx][0] + "\"");
                System.Threading.Thread.Sleep(300);
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
                XmlDocument doc = new XmlDocument(); doc.Load(path);
                XmlNode tths = doc.SelectSingleNode("//TemperatureThresholds");
                tths.RemoveAll();
                var d = Defaults[modeIdx];
                AddTh(tths, doc, 0, 0, d[0][2]);
                for (int i = 1; i < d.Length; i++) AddTh(tths, doc, d[i][0], d[i][1], d[i][2]);
                doc.Save(path);
                File.Copy(path, Config.NbfcConfigs + "\\" + Modes.Meta[modeIdx][0] + ".xml", true);
                Config.Run(Config.NbfcExe, "config -s \"" + Modes.Meta[modeIdx][0] + "\"");
                System.Threading.Thread.Sleep(300);
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
            bool isAdmin; try { new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator); isAdmin = true; } catch { isAdmin = false; }
            try { isAdmin = new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator); } catch { }
            results.Add(new[] { "管理员权限", isAdmin ? "OK" : "FAIL", isAdmin ? "已提权" : "未提权（功能受限）" });

            try { var svc = new ServiceController("NbfcService"); results.Add(new[] { "NBFC 服务", svc.Status == ServiceControllerStatus.Running ? "OK" : "FAIL", svc.Status.ToString() }); }
            catch (Exception ex) { results.Add(new[] { "NBFC 服务", "FAIL", ex.Message }); }

            bool drvOpen = false; ulong v;
            if (Msr.Read(0x610, out v)) drvOpen = true;
            results.Add(new[] { "WinRing0 驱动/MSR", drvOpen ? "OK" : "FAIL", drvOpen ? ("PL1=" + Msr.Pl1W().ToString("0.#") + "W, locked=" + Msr.Locked()) : "无法打开驱动设备" });

            int m = Config.EcRead(147);
            results.Add(new[] { "EC 访问", (m == 4 || m == 20) ? "OK" : "FAIL", "mode(0x93)=0x" + m.ToString("X2") + (m == 4 ? " (自动)" : m == 20 ? " (手动)" : " (异常)") });

            int t = Config.EcRead(168);
            results.Add(new[] { "温度传感器", (t >= 20 && t <= 95) ? "OK" : "FAIL", "EC 0xA8=" + t + " C" });

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

    public class MainForm : Form
    {
        Timer timer;
        Label lblMode, lblTemp, lblFan, lblDuty, lblPl, lblSvc, lblAuto, lblHint;
        Button[] modeButtons;
        bool manualEngaged = false;
        TabControl tabs;

        ComboBox cmbCurveMode; NumericUpDown[] curveUp; NumericUpDown[] curveSp; Label lblCurveStatus;
        ComboBox cmbFreq; Button btnDptfStop, btnDptfStart; Label lblDptf, lblToolStatus;
        ListView lvTest; Button btnTest, btnDump;
        TextBox txtLog;

        public MainForm()
        {
            Text = "风扇管家 - RedmiBook 14 增强版 2020";
            Size = new Size(560, 640); MinimumSize = new Size(560, 640);
            StartPosition = FormStartPosition.CenterScreen;
            Font = new Font("Microsoft YaHei UI", 9F);
            tabs = new TabControl { Dock = DockStyle.Fill };
            tabs.TabPages.Add(BuildMainTab());
            tabs.TabPages.Add(BuildCurveTab());
            tabs.TabPages.Add(BuildToolTab());
            tabs.TabPages.Add(BuildTestTab());
            Controls.Add(tabs);
            timer = new Timer { Interval = 3000 };
            timer.Tick += (s, e) => RefreshStatus();
            Load += (s, e) => { RefreshStatus(); if (!App.Headless) timer.Start(); SelfTest.RunAll(); };
            FormClosing += (s, e) => { if (manualEngaged) { Config.Run(Config.NbfcExe, "set -a"); } };
        }

        TabPage BuildMainTab()
        {
            var page = new TabPage("主面板");
            var top = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 100, Padding = new Padding(8) };
            modeButtons = new Button[4];
            string[] labels = { "静音模式", "均衡模式", "极限性能", "勿扰全静" };
            for (int i = 0; i < 4; i++)
            {
                var b = new Button { Text = labels[i], Size = new Size(118, 36), Tag = new[] { "quiet", "balanced", "performance", "dnd" }[i] };
                string captured = (string)b.Tag;
                b.Click += (s, e) => Task.Run(() => Modes.Apply(captured)).ContinueWith(t => { Config.Log("apply " + captured + ": " + t.Result); BeginInvoke((Action)(() => RefreshStatus())); });
                modeButtons[i] = b; top.Controls.Add(b);
            }
            var btnRestore = new Button { Text = "恢复默认", Size = new Size(118, 36) };
            btnRestore.Click += (s, e) => Task.Run(() => Modes.Apply("restore")).ContinueWith(t => BeginInvoke((Action)(() => RefreshStatus())));
            top.Controls.Add(btnRestore);

            var info = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, Padding = new Padding(10) };
            lblMode = MkLabel(info, "当前模式: -");
            lblTemp = MkLabel(info, "温度: -");
            lblFan = MkLabel(info, "风扇: -");
            lblDuty = MkLabel(info, "EC 占空比: -");
            lblPl = MkLabel(info, "功耗墙 PL1: -");
            lblSvc = MkLabel(info, "NBFC 服务: -");
            lblAuto = MkLabel(info, "智能自动: -");
            lblHint = MkLabel(info, "");

            var manualGroup = new GroupBox { Text = "手动风扇（测试/特殊用途，温度≥80℃ 自动交还自动控制）", Dock = DockStyle.Top, Height = 100 };
            var slider = new TrackBar { Minimum = 0, Maximum = 100, TickFrequency = 10, Value = 35, Location = new Point(140, 22), Width = 260 };
            var lblManVal = new Label { Text = "35%", Location = new Point(410, 28), AutoSize = true };
            var btnApply = new Button { Text = "应用手动转速", Width = 120, Location = new Point(12, 60) };
            var btnAuto = new Button { Text = "回到自动曲线", Width = 120, Location = new Point(140, 60) };
            slider.ValueChanged += (s, e) => lblManVal.Text = slider.Value + "%";
            btnApply.Click += (s, e) =>
            {
                int v = slider.Value;
                Task.Run(() => Config.Run(Config.NbfcExe, "set -s " + v)).ContinueWith(t => { manualEngaged = true; BeginInvoke((Action)(() => lblHint.Text = "手动模式 " + v + "%（温度≥80℃ 会自动交还）")); });
            };
            btnAuto.Click += (s, e) =>
            {
                Task.Run(() => Config.Run(Config.NbfcExe, "set -a")).ContinueWith(t => { manualEngaged = false; BeginInvoke((Action)(() => lblHint.Text = "已回到自动曲线")); });
            };
            manualGroup.Controls.Add(slider); manualGroup.Controls.Add(lblManVal);
            manualGroup.Controls.Add(btnApply); manualGroup.Controls.Add(btnAuto);

            page.Controls.Add(info);
            page.Controls.Add(manualGroup);
            page.Controls.Add(top);
            return page;
        }

        Label MkLabel(FlowLayoutPanel p, string text)
        {
            var l = new Label { Text = text, AutoSize = true, Font = new Font("Microsoft YaHei UI", 9.5F) };
            p.Controls.Add(l); return l;
        }

        TabPage BuildCurveTab()
        {
            var page = new TabPage("风扇曲线");
            var top = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 40, Padding = new Padding(8) };
            top.Controls.Add(new Label { Text = "模式:", AutoSize = true, Padding = new Padding(0, 8, 0, 0) });
            cmbCurveMode = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 150 };
            cmbCurveMode.Items.AddRange(new object[] { "静音模式", "均衡模式", "勿扰全静" });
            cmbCurveMode.SelectedIndex = 0;
            top.Controls.Add(cmbCurveMode);
            var btnLoad = new Button { Text = "读取当前", Width = 90 };
            top.Controls.Add(btnLoad);
            var btnReset = new Button { Text = "恢复默认曲线", Width = 110 };
            top.Controls.Add(btnReset);
            var btnSave = new Button { Text = "保存并生效", Width = 110 };
            top.Controls.Add(btnSave);

            var grid = new TableLayoutPanel { Dock = DockStyle.Top, Height = 240, ColumnCount = 3, RowCount = 7, Padding = new Padding(8) };
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            grid.Controls.Add(new Label { Text = "档位", AutoSize = true }, 0, 0);
            grid.Controls.Add(new Label { Text = "触发温度 ℃", AutoSize = true }, 1, 0);
            grid.Controls.Add(new Label { Text = "转速 %", AutoSize = true }, 2, 0);
            curveUp = new NumericUpDown[4]; curveSp = new NumericUpDown[5];
            // row 1 = base (speed only); rows 2-5 = thresholds (up + speed)
            curveSp[0] = new NumericUpDown { Minimum = 0, Maximum = 100, Value = 0, Width = 70 };
            grid.Controls.Add(new Label { Text = "常驻(低于起转点)", AutoSize = true }, 0, 1);
            grid.Controls.Add(curveSp[0], 2, 1);
            for (int i = 1; i <= 4; i++)
            {
                grid.Controls.Add(new Label { Text = "第" + i + "档阈值", AutoSize = true }, 0, i + 1);
                curveUp[i - 1] = new NumericUpDown { Minimum = 0, Maximum = 95, Value = 0, Width = 70, Enabled = false };
                curveSp[i] = new NumericUpDown { Minimum = 0, Maximum = 100, Value = 0, Width = 70, Enabled = false };
                grid.Controls.Add(curveUp[i - 1], 1, i + 1);
                grid.Controls.Add(curveSp[i], 2, i + 1);
            }
            lblCurveStatus = new Label { Dock = DockStyle.Top, Height = 30, Text = "提示: 第1档=常驻转速(温度低于起转点时)，其余档在达到触发温度后生效；回差自动=触发温度-7℃" };
            LoadCurve();
            cmbCurveMode.SelectedIndexChanged += (s, e) => LoadCurve();
            btnLoad.Click += (s, e) => LoadCurve();
            btnReset.Click += (s, e) => { string r = Curves.ResetDefaults(cmbCurveMode.SelectedIndex); lblCurveStatus.Text = r; LoadCurve(); };
            btnSave.Click += (s, e) =>
            {
                var rows = new System.Collections.Generic.List<int[]>();
                rows.Add(new[] { 0, (int)curveSp[0].Value });
                for (int i = 1; i <= 4; i++)
                {
                    if (!curveUp[i - 1].Enabled) continue;
                    rows.Add(new[] { (int)curveUp[i - 1].Value, (int)curveSp[i].Value });
                }
                int idx = cmbCurveMode.SelectedIndex;
                Task.Run(() => Curves.Save(idx, rows.ToArray())).ContinueWith(t => BeginInvoke((Action)(() => lblCurveStatus.Text = t.Result)));
            };            page.Controls.Add(lblCurveStatus);
            page.Controls.Add(grid);
            page.Controls.Add(top);
            return page;
        }

        void LoadCurve()
        {
            int idx = cmbCurveMode.SelectedIndex;
            var steps = Curves.Load(idx);
            curveSp[0].Value = Clamp(steps[0][2], 0, 100);
            for (int i = 1; i <= 4; i++)
            {
                bool has = i < steps.Length;
                curveUp[i - 1].Enabled = has;
                curveSp[i].Enabled = true;
                if (has)
                {
                    curveUp[i - 1].Value = Clamp(steps[i][0], 0, 95);
                    curveSp[i].Value = Clamp(steps[i][2], 0, 100);
                }
                else
                {
                    curveSp[i].Value = Clamp(steps[steps.Length - 1][2], 0, 100);
                }
            }
        }

        int Clamp(int v, int lo, int hi) { return v < lo ? lo : v > hi ? hi : v; }
        public void SelectTab(int i) { if (tabs != null && i >= 0 && i < tabs.TabPages.Count) tabs.SelectedIndex = i; }

        TabPage BuildToolTab()
        {
            var page = new TabPage("工具");
            var fl = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, Padding = new Padding(10) };

            var g1 = new GroupBox { Text = "智能自动（温度驱动的静音↔均衡自动切换）", Width = 510, Height = 92 };
            var bOn = new Button { Text = "开启", Width = 90, Location = new Point(12, 24) };
            var bOff = new Button { Text = "关闭", Width = 90, Location = new Point(110, 24) };
            var lblAutoState = new Label { Text = "", AutoSize = true, Location = new Point(210, 28) };
            bOn.Click += (s, e) => { string r = Config.Run("powershell", "-NoProfile -ExecutionPolicy Bypass -File \"" + Config.ScriptsDir + "\\lib\\install-auto.ps1\" enable", 30000); lblAutoState.Text = r.Trim(); RefreshStatus(); };
            bOff.Click += (s, e) => { string r = Config.Run("powershell", "-NoProfile -ExecutionPolicy Bypass -File \"" + Config.ScriptsDir + "\\lib\\install-auto.ps1\" disable", 30000); lblAutoState.Text = r.Trim(); RefreshStatus(); };
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
                int mhz = cmbFreq.SelectedIndex == 0 ? 0 : int.Parse((string)cmbFreq.SelectedItem);
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
                }).ContinueWith(t => BeginInvoke((Action)(() => lblToolStatus.Text = "频率上限已应用: " + (mhz == 0 ? "不限" : mhz + " MHz"))));
            };
            g2.Controls.Add(cmbFreq); g2.Controls.Add(bFreq); g2.Controls.Add(lblToolStatus);
            fl.Controls.Add(g2);

            var g3 = new GroupBox { Text = "Intel DPTF (esifsvc) —— 极限模式可选解锁项；停止后睿频更高，热策略交由固件", Width = 510, Height = 92 };
            btnDptfStop = new Button { Text = "停止 DPTF", Width = 100, Location = new Point(12, 24) };
            btnDptfStart = new Button { Text = "启动 DPTF", Width = 100, Location = new Point(120, 24) };
            lblDptf = new Label { Text = DptfStatus(), AutoSize = true, Location = new Point(230, 28) };
            btnDptfStop.Click += (s, e) => { Config.Run("net", "stop esifsvc", 30000); lblDptf.Text = DptfStatus(); Config.Log("DPTF stopped"); };
            btnDptfStart.Click += (s, e) => { Config.Run("net", "start esifsvc", 30000); lblDptf.Text = DptfStatus(); Config.Log("DPTF started"); };
            g3.Controls.Add(btnDptfStop); g3.Controls.Add(btnDptfStart); g3.Controls.Add(lblDptf);
            fl.Controls.Add(g3);

            var g4 = new GroupBox { Text = "诊断", Width = 510, Height = 92 };
            btnDump = new Button { Text = "转储 EC 到日志", Width = 130, Location = new Point(12, 24) };
            btnDump.Click += (s, e) =>
            {
                Task.Run(() =>
                {
                    string outp = Config.Run(Config.EcProbe, "dump", 60000);
                    File.WriteAllText(Config.LogDir + "\\ec-dump-app-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".txt", outp, Encoding.UTF8);
                }).ContinueWith(t => BeginInvoke((Action)(() => lblToolStatus.Text = "EC 转储已保存到 logs 文件夹")));
            };
            var btnFolder = new Button { Text = "打开日志文件夹", Width = 130, Location = new Point(150, 24) };
            btnFolder.Click += (s, e) => Process.Start(Config.LogDir);
            g4.Controls.Add(btnDump); g4.Controls.Add(btnFolder);
            fl.Controls.Add(g4);

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
                Task.Run(() => SelfTest.RunAll()).ContinueWith(t =>
                {
                    BeginInvoke((Action)(() =>
                    {
                        foreach (var r in t.Result)
                        {
                            var it = lvTest.Items.Add(r[0]);
                            it.SubItems.Add(r[1]); it.SubItems.Add(r[2]);
                            it.ForeColor = r[1].StartsWith("OK") || r[1].StartsWith("ON") ? Color.Green : r[1] == "WARN" || r[1].StartsWith("OFF") ? Color.DarkOrange : Color.Red;
                        }
                        File.WriteAllText(Config.LogDir + "\\app-selftest.txt",
                            DateTime.Now + "\r\n" + string.Join("\r\n", t.Result.Select(r => string.Format("[{0}] {1}: {2}", r[1], r[0], r[2]))), Encoding.UTF8);
                    }));
                });
            };
            page.Controls.Add(lvTest);
            page.Controls.Add(txtLog);
            page.Controls.Add(top);
            return page;
        }

        void RefreshStatus()
        {
            try
            {
                string st = Config.Run(Config.NbfcExe, "status", 8000);
                int temp = Config.EcRead(168);
                int duty = Config.EcRead(148);
                int mode = Config.EcRead(147);
                double pl = Msr.Pl1W();
                string guid = Config.ActivePlanGuid();
                string modeName = "未知";
                if (guid == Config.SilentGuid) modeName = "静音模式"; else if (guid == Config.BalancedGuid) modeName = "均衡模式";
                else if (guid == Config.PerfGuid) modeName = "极限性能"; else if (guid == Config.DndGuid) modeName = "勿扰全静";
                else if (guid == Config.SystemGuid) modeName = "系统默认(节能)";

                string nbfcTemp = "-", cur = "-", tgt = "-", cfg = "-";
                Match m;
                if ((m = Regex.Match(st, @"Selected config name\s*:\s*(.+)")).Success) cfg = m.Groups[1].Value.Trim();
                if ((m = Regex.Match(st, @"Temperature\s*:\s*([\d.\-]+)")).Success) nbfcTemp = m.Groups[1].Value;
                if ((m = Regex.Match(st, @"Current fan speed\s*:\s*([\d.\-]+)")).Success) cur = m.Groups[1].Value;
                if ((m = Regex.Match(st, @"Target fan speed\s*:\s*([\d.\-]+)")).Success) tgt = m.Groups[1].Value;

                lblMode.Text = "当前模式: " + modeName + "   (" + cfg + ")";
                lblTemp.Text = string.Format("温度: EC {0}℃ / NBFC {1}℃", temp, nbfcTemp);
                lblFan.Text = string.Format("风扇: 当前 {0}% / 目标 {1}%", cur, tgt);
                lblDuty.Text = string.Format("EC 占空比: 0x{0:X2}  模式: 0x{1:X2}{2}", duty, mode, mode == 4 ? " (EC自动)" : mode == 20 ? " (手动受控)" : "");
                lblPl.Text = "功耗墙 PL1: " + (pl > 0 ? pl.ToString("0.#") + "W" : "读取失败") + (Msr.Locked() ? " (已锁)" : "");
                try { lblSvc.Text = "NBFC 服务: " + new ServiceController("NbfcService").Status; } catch { lblSvc.Text = "NBFC 服务: 未安装"; }
                string auto = Extensions.TaskSchedulerFallback();
                lblAuto.Text = "智能自动: " + auto;
                modeButtons[0].ForeColor = guid == Config.SilentGuid ? Color.Green : Color.Black;
                modeButtons[1].ForeColor = guid == Config.BalancedGuid ? Color.Green : Color.Black;
                modeButtons[2].ForeColor = guid == Config.PerfGuid ? Color.Green : Color.Black;
                modeButtons[3].ForeColor = guid == Config.DndGuid ? Color.Green : Color.Black;

                if (manualEngaged && temp >= 80)
                {
                    Config.Run(Config.NbfcExe, "set -a");
                    manualEngaged = false;
                    lblHint.Text = "温度≥80℃，手动模式已自动交还！";
                    Config.Log("manual fan guard triggered at " + temp + "C");
                }
            }
            catch (Exception ex) { lblTemp.Text = "刷新失败: " + ex.Message; }
        }
    }

    // tiny helper: scheduled task state via schtasks CSV (GBK)
    public static class Extensions
    {
        public static string TaskSchedulerFallback()
        {
            string q = Config.Run("schtasks", "/Query /TN " + Config.TaskName + " /FO CSV /NH", 8000, 1);
            if (q == null || q.Contains("ERR") || q.Trim() == "") return "未创建";
            if (q.Contains("禁用")) return "OFF";
            return "ON";
        }
    }

    public static class App
    {
        [DllImport("user32.dll")] static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);
        public static bool Headless = false;
        public static int ShotTab = 0;
        public static void SetShotTab(int i) { ShotTab = i; Headless = true; }
        public static void RunScreenshot()
        {
            var form = new MainForm();
            form.Load += (s, e) =>
            {
                var t = new Timer { Interval = 2500 };
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
