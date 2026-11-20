// 实时硬件监控（单文件独立程序）
// 采集 / HTTP / 独立窗口全部内置，不依赖 Node.js、PowerShell 脚本或 DSH。
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Management;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

[assembly: AssemblyTitle("实时硬件监控")]
[assembly: AssemblyDescription("实时 CPU/GPU/内存/磁盘/网络监控，单文件独立程序")]
[assembly: AssemblyProduct("SysMonitor")]
[assembly: AssemblyVersion("1.0.0.0")]
[assembly: AssemblyFileVersion("1.0.0.0")]
[assembly: ComVisible(false)]

namespace SysMonitor
{
    /* ------------------------------------------------------------------ 原生 */
    internal static class Native
    {
        [StructLayout(LayoutKind.Sequential)]
        public struct MEMORYSTATUSEX
        {
            public uint dwLength;
            public uint dwMemoryLoad;
            public ulong ullTotalPhys;
            public ulong ullAvailPhys;
            public ulong ullTotalPageFile;
            public ulong ullAvailPageFile;
            public ulong ullTotalVirtual;
            public ulong ullAvailVirtual;
            public ulong ullAvailExtendedVirtual;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

        [DllImport("kernel32.dll")]
        public static extern ulong GetTickCount64();

        [DllImport("user32.dll")]
        public static extern uint GetDpiForSystem();

        [DllImport("user32.dll")]
        public static extern bool SetProcessDpiAwarenessContext(IntPtr value);

        public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll")]
        public static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

        [DllImport("user32.dll")]
        public static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern bool PostMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

        public static MEMORYSTATUSEX ReadMemory()
        {
            MEMORYSTATUSEX m = new MEMORYSTATUSEX();
            m.dwLength = (uint)Marshal.SizeOf(typeof(MEMORYSTATUSEX));
            GlobalMemoryStatusEx(ref m);
            return m;
        }
    }

    /* ------------------------------------------------------------------ JSON */
    internal static class J
    {
        public static string Esc(string s)
        {
            if (s == null) return "";
            StringBuilder sb = new StringBuilder(s.Length + 8);
            foreach (char ch in s)
            {
                switch (ch)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (ch < ' ') sb.Append("\\u").Append(((int)ch).ToString("x4"));
                        else sb.Append(ch);
                        break;
                }
            }
            return sb.ToString();
        }

        public static string Str(string s)
        {
            return s == null ? "null" : "\"" + Esc(s) + "\"";
        }

        public static string Num(double? v, int dec)
        {
            if (!v.HasValue) return "null";
            double d = v.Value;
            if (double.IsNaN(d) || double.IsInfinity(d)) return "null";
            return Math.Round(d, dec).ToString("F" + dec.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
        }

        public static string Int(double v)
        {
            return Num(v, 0);
        }

        public static double? Parse(string s)
        {
            if (s == null) return null;
            string t = s.Replace("[N/A]", "").Replace("N/A", "").Trim();
            if (t.Length == 0) return null;
            double d;
            if (double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out d)) return d;
            return null;
        }
    }

    /* -------------------------------------------------------------- 采集器 */
    internal sealed class Collector
    {
        private sealed class CoreCounter { public int Index; public PerformanceCounter Pc; }
        private sealed class Nic { public string Name; public PerformanceCounter Rx; public PerformanceCounter Tx; }
        private sealed class Zone { public string N; public double C; }
        private sealed class LhmSensor { public string Name; public double Value; }
        private sealed class ProcRow { public string N; public double P; public double M; public int Pid; }
        private sealed class TempInfo
        {
            public double? C;
            public string Src = "NONE";
            public string Label = "未发现温度传感器";
            public List<Zone> Zones = new List<Zone>();
            public List<double> CoreTemps = new List<double>();
        }

        private readonly object gate = new object();
        private readonly List<string> history = new List<string>();
        private string latest;
        private long version;
        private volatile bool running = true;
        private Thread worker;

        private readonly List<CoreCounter> cores = new List<CoreCounter>();
        private readonly List<KeyValuePair<string, PerformanceCounter>> thermals = new List<KeyValuePair<string, PerformanceCounter>>();
        private readonly List<Nic> nics = new List<Nic>();
        private PerformanceCounter cpuTotal, cpuFreq, cpuFreqPct, diskRead, diskWrite;

        private string smiPath;
        private string hostJson = "{}";
        private LhmSensor[] lhmCache = new LhmSensor[0];
        private string procsJson = "[]";

        private Dictionary<int, double> procPrev = new Dictionary<int, double>();
        private DateTime procPrevAt = DateTime.MinValue;

        public string Latest { get { lock (gate) { return latest; } } }
        public long Version { get { lock (gate) { return version; } } }
        public int CoreCount { get { return cores.Count; } }

        public string HistoryJson()
        {
            lock (gate) { return "[" + string.Join(",", history.ToArray()) + "]"; }
        }

        public void Start()
        {
            InitCounters();
            hostJson = BuildHostJson();
            worker = new Thread(Run);
            worker.IsBackground = true;
            worker.Name = "sensors";
            worker.Start();
        }

        public void Stop() { running = false; }

        /* ---------------------------------------------------------- 初始化 */
        private static PerformanceCounter NewPc(string category, string counter, string instance)
        {
            try
            {
                PerformanceCounter pc = new PerformanceCounter(category, counter, instance);
                pc.NextValue();
                return pc;
            }
            catch { return null; }
        }

        private static int CoreIndex(string instance)
        {
            int i = instance.IndexOf(',');
            int v;
            if (i >= 0 && int.TryParse(instance.Substring(i + 1), out v)) return v;
            return 0;
        }

        private void InitCounters()
        {
            cpuTotal = NewPc("Processor Information", "% Processor Time", "_Total");
            cpuFreq = NewPc("Processor Information", "Processor Frequency", "_Total");
            cpuFreqPct = NewPc("Processor Information", "% of Maximum Frequency", "_Total");

            try
            {
                PerformanceCounterCategory cat = new PerformanceCounterCategory("Processor Information");
                List<string> list = new List<string>();
                foreach (string n in cat.GetInstanceNames())
                {
                    // 只要 "0,0" 这种物理核实例，排除 "0,_Total"、"_Total" 等
                    if (!Regex.IsMatch(n, "^\\d+,\\d+$")) continue;
                    list.Add(n);
                }
                list.Sort(delegate(string a, string b) { return CoreIndex(a).CompareTo(CoreIndex(b)); });
                foreach (string n in list)
                {
                    PerformanceCounter pc = NewPc("Processor Information", "% Processor Time", n);
                    if (pc != null)
                    {
                        CoreCounter c = new CoreCounter();
                        c.Index = CoreIndex(n);
                        c.Pc = pc;
                        cores.Add(c);
                    }
                }
            }
            catch { }

            try
            {
                PerformanceCounterCategory cat = new PerformanceCounterCategory("Thermal Zone Information");
                foreach (string n in cat.GetInstanceNames())
                {
                    PerformanceCounter pc = NewPc("Thermal Zone Information", "Temperature", n);
                    if (pc == null) continue;
                    string name = n.StartsWith("\\_") ? n.Substring(2) : n;
                    thermals.Add(new KeyValuePair<string, PerformanceCounter>(name, pc));
                }
            }
            catch { }

            diskRead = NewPc("LogicalDisk", "Disk Read Bytes/sec", "_Total");
            diskWrite = NewPc("LogicalDisk", "Disk Write Bytes/sec", "_Total");

            try
            {
                PerformanceCounterCategory cat = new PerformanceCounterCategory("Network Interface");
                foreach (string n in cat.GetInstanceNames())
                {
                    if (n.Contains("Loopback") || n.Contains("isatap") || n.Contains("Teredo")
                        || n.Contains("Pseudo") || n.Contains("WAN Miniport") || n.Contains("vEthernet")
                        || n.Contains("Bluetooth")) continue;
                    PerformanceCounter rx = NewPc("Network Interface", "Bytes Received/sec", n);
                    PerformanceCounter tx = NewPc("Network Interface", "Bytes Sent/sec", n);
                    if (rx == null || tx == null) continue;
                    Nic x = new Nic();
                    x.Name = n;
                    x.Rx = rx;
                    x.Tx = tx;
                    nics.Add(x);
                }
            }
            catch { }

            string[] smiCandidates = new string[]
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "NVIDIA Corporation\\NVSMI\\nvidia-smi.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32\\nvidia-smi.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "NVIDIA Corporation\\NVSMI\\nvidia-smi.exe"),
            };
            foreach (string p in smiCandidates) { if (File.Exists(p)) { smiPath = p; break; } }
            if (smiPath == null)
            {
                string path = Environment.GetEnvironmentVariable("PATH") ?? "";
                foreach (string dir in path.Split(';'))
                {
                    if (dir.Trim().Length == 0) continue;
                    try
                    {
                        string cand = Path.Combine(dir.Trim(), "nvidia-smi.exe");
                        if (File.Exists(cand)) { smiPath = cand; break; }
                    }
                    catch { }
                }
            }
        }

        private string BuildHostJson()
        {
            string os = "Windows", build = "", cpuName = "CPU", board = "", model = "";
            try
            {
                using (RegistryKey root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
                using (RegistryKey k = root.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion"))
                {
                    if (k != null)
                    {
                        os = Convert.ToString(k.GetValue("ProductName"));
                        string dv = Convert.ToString(k.GetValue("DisplayVersion"));
                        if (!string.IsNullOrEmpty(dv)) os = os + " " + dv;
                        build = Convert.ToString(k.GetValue("CurrentBuild")) + "." + Convert.ToString(k.GetValue("UBR"));
                    }
                }
                using (RegistryKey root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
                using (RegistryKey k = root.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0"))
                {
                    if (k != null) cpuName = Convert.ToString(k.GetValue("ProcessorNameString"));
                }
                using (RegistryKey root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
                using (RegistryKey k = root.OpenSubKey(@"HARDWARE\DESCRIPTION\System\BIOS"))
                {
                    if (k != null)
                    {
                        board = (Convert.ToString(k.GetValue("BaseBoardManufacturer")) + " " + Convert.ToString(k.GetValue("BaseBoardProduct"))).Trim();
                        model = Convert.ToString(k.GetValue("SystemProductName")).Trim();
                    }
                }
            }
            catch { }
            cpuName = Regex.Replace(cpuName ?? "CPU", "\\s+", " ").Trim();
            return "{\"name\":" + J.Str(Environment.MachineName)
                 + ",\"user\":" + J.Str(Environment.UserName)
                 + ",\"os\":" + J.Str(os)
                 + ",\"build\":" + J.Str(build)
                 + ",\"cpu\":" + J.Str(cpuName)
                 + ",\"board\":" + J.Str(board)
                 + ",\"model\":" + J.Str(model)
                 + ",\"cores\":" + cores.Count
                 + ",\"boot\":" + Native.GetTickCount64() + "}";
        }

        /* ---------------------------------------------------------- 主循环 */
        private void Run()
        {
            Thread.Sleep(600);   // 速率型计数器预热
            int tick = 0;
            while (running)
            {
                Stopwatch sw = Stopwatch.StartNew();
                try
                {
                    if ((tick % 3) == 0) UpdateProcs();
                    if ((tick % 6) == 0) RefreshLhm();
                    string json = BuildSample();
                    lock (gate)
                    {
                        latest = json;
                        history.Add(json);
                        if (history.Count > 300) history.RemoveAt(0);
                        version++;
                    }
                }
                catch { }
                tick++;
                int sleep = 1000 - (int)sw.ElapsedMilliseconds;
                if (sleep < 50) sleep = 50;
                Thread.Sleep(sleep);
            }
        }

        private static double? ReadPc(PerformanceCounter pc)
        {
            if (pc == null) return null;
            try { return pc.NextValue(); } catch { return null; }
        }

        /* ---------------------------------------------------------- 采样 */
        private string BuildSample()
        {
            Native.MEMORYSTATUSEX m = Native.ReadMemory();
            StringBuilder sb = new StringBuilder(4096);
            sb.Append("{\"t\":").Append(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            sb.Append(",\"up\":").Append(J.Int(Native.GetTickCount64() / 1000.0));
            sb.Append(",\"h\":").Append(hostJson);

            // CPU
            double? cpuPct = ReadPc(cpuTotal);
            double? freq = ReadPc(cpuFreq);
            double? freqPct = ReadPc(cpuFreqPct);
            double? maxFreq = null;
            if (freq.HasValue && freqPct.HasValue && freqPct.Value > 1)
                maxFreq = Math.Round(freq.Value / (freqPct.Value / 100.0), 0);

            StringBuilder cv = new StringBuilder();
            foreach (CoreCounter c in cores)
            {
                double? v = ReadPc(c.Pc);
                if (cv.Length > 0) cv.Append(",");
                cv.Append(v.HasValue ? J.Int(Math.Round(v.Value, 0)) : "null");
            }
            TempInfo temp = ReadThermal();

            sb.Append(",\"cpu\":{\"u\":").Append(J.Num(cpuPct, 1))
              .Append(",\"cores\":[").Append(cv).Append("]")
              .Append(",\"f\":").Append(J.Num(freq, 0))
              .Append(",\"mf\":").Append(J.Num(maxFreq, 0))
              .Append(",\"temp\":").Append(TempJson(temp))
              .Append("}");

            // 内存
            sb.Append(",\"mem\":{\"total\":").Append(J.Num(m.ullTotalPhys / 1073741824.0, 2))
              .Append(",\"used\":").Append(J.Num((m.ullTotalPhys - m.ullAvailPhys) / 1073741824.0, 2))
              .Append(",\"pct\":").Append(J.Num(m.dwMemoryLoad, 0))
              .Append(",\"page\":").Append(J.Num((m.ullTotalPageFile - m.ullAvailPageFile) / 1073741824.0, 2))
              .Append(",\"pageT\":").Append(J.Num(m.ullTotalPageFile / 1073741824.0, 2))
              .Append("}");

            // GPU
            sb.Append(",\"gpu\":").Append(GpuJson());

            // 磁盘
            double? dr = ReadPc(diskRead);
            double? dw = ReadPc(diskWrite);
            sb.Append(",\"disk\":{\"r\":").Append(J.Num(dr.HasValue ? dr.Value / 1048576.0 : (double?)null, 2))
              .Append(",\"w\":").Append(J.Num(dw.HasValue ? dw.Value / 1048576.0 : (double?)null, 2))
              .Append(",\"vols\":").Append(VolumesJson())
              .Append("}");

            // 网络
            double rx = 0, tx = 0;
            StringBuilder na = new StringBuilder("[");
            bool firstNic = true;
            foreach (Nic n in nics)
            {
                double? r = ReadPc(n.Rx);
                double? t = ReadPc(n.Tx);
                if (r.HasValue) rx += r.Value;
                if (t.HasValue) tx += t.Value;
                if (r.HasValue && t.HasValue && (r.Value + t.Value) > 0)
                {
                    if (!firstNic) na.Append(",");
                    firstNic = false;
                    na.Append("{\"n\":").Append(J.Str(n.Name))
                      .Append(",\"rx\":").Append(J.Int(Math.Round(r.Value / 1024.0, 0)))
                      .Append(",\"tx\":").Append(J.Int(Math.Round(t.Value / 1024.0, 0)))
                      .Append("}");
                }
            }
            na.Append("]");
            sb.Append(",\"net\":{\"rx\":").Append(J.Int(Math.Round(rx / 1024.0, 0)))
              .Append(",\"tx\":").Append(J.Int(Math.Round(tx / 1024.0, 0)))
              .Append(",\"a\":").Append(na).Append("}");

            sb.Append(",\"procs\":").Append(procsJson).Append("}");
            return sb.ToString();
        }

        private static string TempJson(TempInfo t)
        {
            StringBuilder sb = new StringBuilder();
            sb.Append("{\"c\":").Append(J.Num(t.C, 1));
            sb.Append(",\"src\":").Append(J.Str(t.Src));
            sb.Append(",\"label\":").Append(J.Str(t.Label));
            sb.Append(",\"zones\":[");
            for (int i = 0; i < t.Zones.Count; i++)
            {
                if (i > 0) sb.Append(",");
                sb.Append("{\"n\":").Append(J.Str(t.Zones[i].N)).Append(",\"c\":").Append(J.Num(t.Zones[i].C, 1)).Append("}");
            }
            sb.Append("],\"coreTemps\":[");
            for (int i = 0; i < t.CoreTemps.Count; i++)
            {
                if (i > 0) sb.Append(",");
                sb.Append(J.Num(t.CoreTemps[i], 1));
            }
            sb.Append("]}");
            return sb.ToString();
        }

        private TempInfo ReadThermal()
        {
            TempInfo ti = new TempInfo();
            double? best = null;
            string bestName = null;
            foreach (KeyValuePair<string, PerformanceCounter> kv in thermals)
            {
                double? k = ReadPc(kv.Value);
                if (!k.HasValue || k.Value <= 250) continue;
                Zone z = new Zone();
                z.N = kv.Key;
                z.C = Math.Round(k.Value - 273.15, 1);
                ti.Zones.Add(z);
                if (!best.HasValue || z.C > best.Value) { best = z.C; bestName = kv.Key; }
            }

            LhmSensor[] lhm = lhmCache;
            double? pkg = null;
            if (lhm != null && lhm.Length > 0)
            {
                foreach (LhmSensor s in lhm)
                {
                    if (IsPkgName(s.Name))
                    {
                        double v = Math.Round(s.Value, 1);
                        if (!pkg.HasValue || v > pkg.Value) pkg = v;
                    }
                    if (IsCoreName(s.Name)) ti.CoreTemps.Add(Math.Round(s.Value, 1));
                }
            }
            if (pkg.HasValue)
            {
                ti.C = pkg;
                ti.Src = "LHM";
                ti.Label = "CPU Package (LibreHardwareMonitor)";
                return ti;
            }
            if (best.HasValue)
            {
                ti.C = best;
                ti.Src = "ACPI";
                ti.Label = "ACPI 热区 " + bestName;
                return ti;
            }
            return ti;
        }

        private static bool IsPkgName(string n)
        {
            if (n == null) return false;
            return Regex.IsMatch(n, "^CPU Package$|^Core \\(Tctl/Tdie\\)$|^Core \\(Tdie\\)$|^CPU Core$|^Core Average$");
        }

        private static bool IsCoreName(string n)
        {
            if (n == null) return false;
            return Regex.IsMatch(n, "^CPU Core #\\d+$|^Core #\\d+$");
        }

        private void RefreshLhm()
        {
            string[] scopes = new string[] { "root\\LibreHardwareMonitor", "root\\OpenHardwareMonitor" };
            foreach (string ns in scopes)
            {
                try
                {
                    ManagementScope scope = new ManagementScope(ns);
                    scope.Connect();
                    ObjectQuery query = new ObjectQuery("SELECT Name, Value FROM Sensor WHERE SensorType='Temperature'");
                    using (ManagementObjectSearcher searcher = new ManagementObjectSearcher(scope, query))
                    {
                        List<LhmSensor> list = new List<LhmSensor>();
                        foreach (ManagementBaseObject mo in searcher.Get())
                        {
                            try
                            {
                                LhmSensor s = new LhmSensor();
                                s.Name = Convert.ToString(mo["Name"]);
                                object v = mo["Value"];
                                s.Value = Convert.ToDouble(v, CultureInfo.InvariantCulture);
                                list.Add(s);
                            }
                            catch { }
                            finally { mo.Dispose(); }
                        }
                        if (list.Count > 0) { lhmCache = list.ToArray(); return; }
                    }
                }
                catch (ManagementException) { }
                catch (COMException) { }
                catch (UnauthorizedAccessException) { }
                catch (InvalidOperationException) { }
            }
            lhmCache = new LhmSensor[0];
        }

        private string RunSmi()
        {
            if (smiPath == null) return null;
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo(smiPath,
                    "--query-gpu=name,temperature.gpu,utilization.gpu,memory.used,memory.total,clocks.sm,clocks.mem,fan.speed,power.draw,power.limit --format=csv,noheader,nounits");
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                psi.StandardOutputEncoding = Encoding.UTF8;
                using (Process p = Process.Start(psi))
                {
                    if (p == null) return null;
                    string outp = p.StandardOutput.ReadToEnd();
                    if (!p.WaitForExit(3000)) { try { p.Kill(); } catch { } return null; }
                    if (outp == null) return null;
                    int nl = outp.IndexOf('\n');
                    string line = nl >= 0 ? outp.Substring(0, nl) : outp;
                    line = line.Trim();
                    return line.Length == 0 ? null : line;
                }
            }
            catch { return null; }
        }

        private string GpuJson()
        {
            string raw = RunSmi();
            if (raw == null) return "null";
            string[] f = raw.Split(',');
            for (int i = 0; i < f.Length; i++) f[i] = f[i].Trim();
            if (f.Length < 10) return "null";
            StringBuilder sb = new StringBuilder();
            sb.Append("{\"name\":").Append(J.Str(f[0]));
            sb.Append(",\"temp\":").Append(J.Num(J.Parse(f[1]), 0));
            sb.Append(",\"util\":").Append(J.Num(J.Parse(f[2]), 0));
            sb.Append(",\"memUsed\":").Append(J.Num(J.Parse(f[3]), 0));
            sb.Append(",\"memTotal\":").Append(J.Num(J.Parse(f[4]), 0));
            sb.Append(",\"clock\":").Append(J.Num(J.Parse(f[5]), 0));
            sb.Append(",\"memClock\":").Append(J.Num(J.Parse(f[6]), 0));
            sb.Append(",\"fan\":").Append(J.Num(J.Parse(f[7]), 0));
            sb.Append(",\"power\":").Append(J.Num(J.Parse(f[8]), 0));
            sb.Append(",\"powerMax\":").Append(J.Num(J.Parse(f[9]), 0));
            sb.Append("}");
            return sb.ToString();
        }

        private static string VolumesJson()
        {
            StringBuilder sb = new StringBuilder("[");
            bool first = true;
            foreach (DriveInfo d in DriveInfo.GetDrives())
            {
                try
                {
                    if (!d.IsReady) continue;
                    if (d.DriveType != DriveType.Fixed) continue;
                    if (d.TotalSize <= 0) continue;
                    long used = d.TotalSize - d.AvailableFreeSpace;
                    if (!first) sb.Append(",");
                    first = false;
                    sb.Append("{\"d\":").Append(J.Str(d.Name.Substring(0, 2)))
                      .Append(",\"total\":").Append(J.Int(Math.Round(d.TotalSize / 1073741824.0, 0)))
                      .Append(",\"free\":").Append(J.Int(Math.Round(d.AvailableFreeSpace / 1073741824.0, 0)))
                      .Append(",\"pct\":").Append(J.Int(Math.Round(used / (double)d.TotalSize * 100.0, 0)))
                      .Append("}");
                }
                catch { }
            }
            sb.Append("]");
            return sb.ToString();
        }

        private void UpdateProcs()
        {
            DateTime now = DateTime.UtcNow;
            double dt = procPrevAt == DateTime.MinValue ? 1.0 : (now - procPrevAt).TotalSeconds;
            if (dt <= 0.2) dt = 1.0;
            int nc = cores.Count < 1 ? Environment.ProcessorCount : cores.Count;

            Dictionary<int, double> cur = new Dictionary<int, double>();
            List<ProcRow> rows = new List<ProcRow>();
            Process[] ps = Process.GetProcesses();
            foreach (Process p in ps)
            {
                try
                {
                    int id = p.Id;
                    double ct = p.TotalProcessorTime.TotalSeconds;
                    cur[id] = ct;
                    double pct = 0;
                    double old;
                    if (procPrev.TryGetValue(id, out old)) pct = ((ct - old) / dt) / nc * 100.0;
                    if (pct < 0) pct = 0;
                    ProcRow r = new ProcRow();
                    r.N = p.ProcessName;
                    r.P = pct;
                    r.M = p.WorkingSet64 / 1048576.0;
                    r.Pid = id;
                    rows.Add(r);
                }
                catch { }
            }
            foreach (Process p in ps) { try { p.Dispose(); } catch { } }

            rows.Sort(delegate(ProcRow a, ProcRow b) { return b.P.CompareTo(a.P); });

            StringBuilder sb = new StringBuilder("[");
            int n = Math.Min(6, rows.Count);
            for (int i = 0; i < n; i++)
            {
                if (i > 0) sb.Append(",");
                sb.Append("{\"n\":").Append(J.Str(rows[i].N))
                  .Append(",\"p\":").Append(J.Num(rows[i].P, 1))
                  .Append(",\"m\":").Append(J.Int(Math.Round(rows[i].M, 0)))
                  .Append(",\"pid\":").Append(rows[i].Pid.ToString(CultureInfo.InvariantCulture))
                  .Append("}");
            }
            sb.Append("]");
            procsJson = sb.ToString();
            procPrev = cur;
            procPrevAt = now;
        }
    }

    /* -------------------------------------------------------------- HTTP 服务 */
    internal sealed class HttpServer
    {
        private readonly Collector col;
        private readonly byte[] indexHtml;
        private TcpListener listener;
        private volatile bool running;

        public int Port { get; private set; }
        public Action OnQuit { get; set; }

        public HttpServer(Collector collector)
        {
            col = collector;
            indexHtml = LoadIndex();
        }

        private static byte[] LoadIndex()
        {
            try
            {
                Assembly asm = Assembly.GetExecutingAssembly();
                using (Stream s = asm.GetManifestResourceStream("SysMonitor.index.html"))
                {
                    if (s != null)
                    {
                        using (MemoryStream ms = new MemoryStream())
                        {
                            byte[] buf = new byte[8192];
                            int n;
                            while ((n = s.Read(buf, 0, buf.Length)) > 0) ms.Write(buf, 0, n);
                            return ms.ToArray();
                        }
                    }
                }
            }
            catch { }
            return Encoding.UTF8.GetBytes("<html><body style='background:#0b1220;color:#8ab4f8;font-family:segoe ui'>界面资源缺失</body></html>");
        }

        public int Listen(int startPort)
        {
            for (int p = startPort; p < startPort + 5; p++)
            {
                try
                {
                    TcpListener l = new TcpListener(IPAddress.Loopback, p);
                    l.Start();
                    listener = l;
                    Port = p;
                    running = true;
                    Thread t = new Thread(AcceptLoop);
                    t.IsBackground = true;
                    t.Name = "http";
                    t.Start();
                    return p;
                }
                catch (SocketException) { }
            }
            return 0;
        }

        public void Stop()
        {
            running = false;
            try { if (listener != null) listener.Stop(); } catch { }
        }

        private void AcceptLoop()
        {
            while (running)
            {
                TcpClient client;
                try { client = listener.AcceptTcpClient(); }
                catch { break; }
                ThreadPool.QueueUserWorkItem(Handle, client);
            }
        }

        private void Handle(object state)
        {
            TcpClient client = (TcpClient)state;
            try
            {
                client.ReceiveTimeout = 5000;
                client.SendTimeout = 5000;
                NetworkStream ns = client.GetStream();
                string req = ReadHeaders(ns);
                if (req == null) { client.Close(); return; }
                int eol = req.IndexOf('\n');
                string firstLine = (eol > 0 ? req.Substring(0, eol) : req).Trim();
                string[] parts = firstLine.Split(' ');
                string path = parts.Length > 1 ? parts[1] : "/";
                int q = path.IndexOf('?');
                if (q >= 0) path = path.Substring(0, q);

                if (path == "/stream") { ServeStream(client, ns); return; }

                if (path == "/api/snapshot")
                {
                    string body = "{\"latest\":" + (col.Latest ?? "null") + ",\"history\":" + col.HistoryJson() + "}";
                    Send(ns, 200, "application/json; charset=utf-8", Encoding.UTF8.GetBytes(body), client);
                    return;
                }

                if (path == "/api/quit")
                {
                    Send(ns, 200, "application/json; charset=utf-8", Encoding.UTF8.GetBytes("{\"ok\":true}"), client);
                    ThreadPool.QueueUserWorkItem(delegate
                    {
                        Thread.Sleep(150);
                        if (OnQuit != null) OnQuit();
                    });
                    return;
                }

                if (path == "/favicon.ico")
                {
                    client.Close();
                    return;
                }

                if (path == "/" || path == "/index.html")
                {
                    Send(ns, 200, "text/html; charset=utf-8", indexHtml, client);
                    return;
                }

                Send(ns, 404, "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("404"), client);
            }
            catch
            {
                try { client.Close(); } catch { }
            }
        }

        private static string ReadHeaders(NetworkStream ns)
        {
            byte[] buf = new byte[4096];
            StringBuilder sb = new StringBuilder();
            int total = 0;
            while (total < 32768)
            {
                int n = ns.Read(buf, 0, buf.Length);
                if (n <= 0) return null;
                total += n;
                sb.Append(Encoding.ASCII.GetString(buf, 0, n));
                if (sb.ToString().IndexOf("\r\n\r\n") >= 0) return sb.ToString();
            }
            return null;
        }

        private static void Send(NetworkStream ns, int code, string contentType, byte[] body, TcpClient client)
        {
            string reason = code == 200 ? "OK" : (code == 404 ? "Not Found" : "No Content");
            string head = "HTTP/1.1 " + code.ToString(CultureInfo.InvariantCulture) + " " + reason + "\r\n"
                        + "Content-Type: " + contentType + "\r\n"
                        + "Content-Length: " + body.Length.ToString(CultureInfo.InvariantCulture) + "\r\n"
                        + "Cache-Control: no-store\r\n"
                        + "Connection: close\r\n\r\n";
            byte[] h = Encoding.ASCII.GetBytes(head);
            ns.Write(h, 0, h.Length);
            if (body.Length > 0) ns.Write(body, 0, body.Length);
            ns.Flush();
            client.Close();
        }

        private void ServeStream(TcpClient client, NetworkStream ns)
        {
            try
            {
                string head = "HTTP/1.1 200 OK\r\n"
                            + "Content-Type: text/event-stream; charset=utf-8\r\n"
                            + "Cache-Control: no-cache, no-transform\r\n"
                            + "Connection: keep-alive\r\n"
                            + "X-Accel-Buffering: no\r\n\r\n";
                WriteText(ns, head);
                WriteText(ns, "retry: 1000\n\n");
                WriteText(ns, "event: history\ndata: " + col.HistoryJson() + "\n\n");
                long v = col.Version;
                DateTime ping = DateTime.UtcNow;
                while (running)
                {
                    Thread.Sleep(200);
                    if (col.Version != v)
                    {
                        v = col.Version;
                        string latest = col.Latest;
                        if (latest != null) WriteText(ns, "event: sample\ndata: " + latest + "\n\n");
                    }
                    if ((DateTime.UtcNow - ping).TotalSeconds >= 15)
                    {
                        WriteText(ns, ": ping\n\n");
                        ping = DateTime.UtcNow;
                    }
                }
            }
            catch { }
            finally
            {
                try { client.Close(); } catch { }
            }
        }

        private static void WriteText(NetworkStream ns, string s)
        {
            byte[] b = Encoding.UTF8.GetBytes(s);
            ns.Write(b, 0, b.Length);
            ns.Flush();
        }
    }

    /* -------------------------------------------------------------- 主程序 */
    internal static class Program
    {
        private static Collector collector;
        private static HttpServer server;
        private static NotifyIcon tray;
        private static Form hidden;
        private static Process uiProcess;
        private static IntPtr dashHandle = IntPtr.Zero;
        private static bool windowWatcherStarted;
        private static DateTime uiStarted = DateTime.MinValue;
        private static bool noWindow;
        private static bool exiting;

        [STAThread]
        private static void Main(string[] args)
        {
            foreach (string a in args)
            {
                if (a == "--no-window" || a == "/no-window") noWindow = true;
            }
            try { Native.SetProcessDpiAwarenessContext(new IntPtr(-4)); } catch { }

            int runningPort = FindRunning();
            if (runningPort > 0)
            {
                if (!noWindow) OpenWindow(runningPort, false);
                return;
            }

            collector = new Collector();
            collector.Start();

            server = new HttpServer(collector);
            server.OnQuit = RequestExit;
            int port = server.Listen(8787);
            if (port <= 0)
            {
                MessageBox.Show("端口 8787-8791 都被占用，无法启动。", "实时硬件监控",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            CreateTray();
            if (!noWindow) OpenWindow(port, true);

            hidden = new Form();
            hidden.Text = "SysMonitor";
            hidden.ShowInTaskbar = false;
            hidden.FormBorderStyle = FormBorderStyle.None;
            hidden.StartPosition = FormStartPosition.Manual;
            hidden.Location = new Point(-4000, -4000);
            hidden.Size = new Size(1, 1);
            hidden.Show();
            Application.Run(hidden);

            Shutdown();
        }

        private static void CreateTray()
        {
            tray = new NotifyIcon();
            try
            {
                Icon ico = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
                tray.Icon = ico != null ? ico : SystemIcons.Application;
            }
            catch { tray.Icon = SystemIcons.Application; }
            tray.Text = "实时硬件监控（双击打开窗口）";
            ContextMenuStrip menu = new ContextMenuStrip();
            menu.Items.Add("打开监控窗口", null, delegate { OpenWindow(server.Port, true); });
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("退出", null, delegate { RequestExit(); });
            tray.ContextMenuStrip = menu;
            tray.Visible = true;
            tray.DoubleClick += delegate { OpenWindow(server.Port, true); };
        }

        private static int FindRunning()
        {
            for (int p = 8787; p <= 8791; p++)
            {
                try
                {
                    using (TcpClient c = new TcpClient())
                    {
                        IAsyncResult ar = c.BeginConnect(IPAddress.Loopback, p, null, null);
                        if (!ar.AsyncWaitHandle.WaitOne(250)) continue;
                        c.EndConnect(ar);
                        NetworkStream ns = c.GetStream();
                        byte[] req = Encoding.ASCII.GetBytes("GET /api/snapshot HTTP/1.1\r\nHost: localhost\r\nConnection: close\r\n\r\n");
                        ns.Write(req, 0, req.Length);
                        c.ReceiveTimeout = 800;
                        byte[] buf = new byte[64];
                        int n = ns.Read(buf, 0, buf.Length);
                        if (n > 0 && Encoding.ASCII.GetString(buf, 0, n).StartsWith("HTTP/1.1 200")) return p;
                    }
                }
                catch { }
            }
            return 0;
        }

        private static string FindBrowser()
        {
            string[] candidates = new string[]
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Microsoft\\Edge\\Application\\msedge.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Microsoft\\Edge\\Application\\msedge.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Google\\Chrome\\Application\\chrome.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Google\\Chrome\\Application\\chrome.exe"),
            };
            foreach (string p in candidates) { if (File.Exists(p)) return p; }
            return null;
        }

        private static Size WindowSize()
        {
            Rectangle r = Screen.PrimaryScreen.WorkingArea;
            double scale = 1.0;
            try
            {
                uint dpi = Native.GetDpiForSystem();
                if (dpi >= 96) scale = dpi / 96.0;
            }
            catch { }
            int w = (int)Math.Round(r.Width / scale) - 40;
            int h = (int)Math.Round(r.Height / scale) - 60;
            if (w < 860) w = 860;
            if (w > 1300) w = 1300;
            if (h < 600) h = 600;
            if (h > 900) h = 900;
            return new Size(w, h);
        }

        private static void OpenWindow(int port, bool track)
        {
            string url = "http://127.0.0.1:" + port.ToString(CultureInfo.InvariantCulture);
            string exe = FindBrowser();
            if (exe == null)
            {
                try { Process.Start(url); } catch { }
                return;
            }
            try
            {
                Size size = WindowSize();
                string profile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SysMonitor", "ui");
                Directory.CreateDirectory(profile);
                string args = "--app=" + url
                            + " --window-size=" + size.Width.ToString(CultureInfo.InvariantCulture) + "," + size.Height.ToString(CultureInfo.InvariantCulture)
                            + " --no-first-run --no-default-browser-check --disable-features=Translate"
                            + " --user-data-dir=\"" + profile + "\"";
                ProcessStartInfo psi = new ProcessStartInfo(exe, args);
                psi.UseShellExecute = false;
                Process p = Process.Start(psi);
                uiProcess = p;
                uiStarted = DateTime.UtcNow;
                if (track && !windowWatcherStarted)
                {
                    windowWatcherStarted = true;
                    Thread t = new Thread(WatchWindow);
                    t.IsBackground = true;
                    t.Name = "ui-watch";
                    t.Start();
                }
            }
            catch
            {
                try { Process.Start(url); } catch { }
            }
        }

        /// <summary>找到标题为「实时硬件监控」的可见窗口（Edge --app 窗口）</summary>
        private static bool FindDashboard(out IntPtr handle)
        {
            IntPtr found = IntPtr.Zero;
            Native.EnumWindowsProc cb = delegate(IntPtr h, IntPtr l)
            {
                if (!Native.IsWindowVisible(h)) return true;
                StringBuilder sb = new StringBuilder(300);
                Native.GetWindowText(h, sb, sb.Capacity);
                if (sb.ToString().IndexOf("实时硬件监控", StringComparison.Ordinal) >= 0)
                {
                    found = h;
                    return false;
                }
                return true;
            };
            Native.EnumWindows(cb, IntPtr.Zero);
            handle = found;
            return found != IntPtr.Zero;
        }

        /// <summary>窗口关掉超过 4 秒，就退出程序（关窗口 = 结束监控）</summary>
        private static void WatchWindow()
        {
            bool sawWindow = false;
            DateTime lastSeen = DateTime.MinValue;
            while (!exiting)
            {
                Thread.Sleep(1000);
                IntPtr h;
                bool visible = FindDashboard(out h);
                if (visible)
                {
                    sawWindow = true;
                    lastSeen = DateTime.UtcNow;
                    dashHandle = h;
                }
                else if (sawWindow && (DateTime.UtcNow - lastSeen).TotalSeconds >= 4)
                {
                    RequestExit();
                    return;
                }
                else if (!sawWindow && (DateTime.UtcNow - uiStarted).TotalSeconds > 45)
                {
                    // 窗口一直没出现（例如没有 Edge），保持托盘常驻，不自动退出
                    return;
                }
            }
        }

        private static void RequestExit()
        {
            if (exiting) return;
            try
            {
                if (hidden != null && hidden.IsHandleCreated)
                    hidden.BeginInvoke(new Action(delegate { hidden.Close(); }));
                else
                    Shutdown();
            }
            catch { }
        }

        private static void Shutdown()
        {
            if (exiting) return;
            exiting = true;
            try { if (collector != null) collector.Stop(); } catch { }
            try { if (server != null) server.Stop(); } catch { }
            try { if (dashHandle != IntPtr.Zero) Native.PostMessage(dashHandle, 0x0010, IntPtr.Zero, IntPtr.Zero); } catch { }
            try { if (uiProcess != null && !uiProcess.HasExited) uiProcess.Kill(); } catch { }
            try
            {
                if (tray != null)
                {
                    tray.Visible = false;
                    tray.Dispose();
                }
            }
            catch { }
        }
    }
}
