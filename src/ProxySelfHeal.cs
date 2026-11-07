using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

[assembly: AssemblyTitle("ProxySelfHeal")]
[assembly: AssemblyDescription("Windows system proxy residue self-heal patch")]
[assembly: AssemblyProduct("ProxySelfHeal Patch")]
[assembly: AssemblyCompany("")]
[assembly: AssemblyVersion("2.0.0.0")]
[assembly: AssemblyFileVersion("2.0.0.0")]

namespace ProxySelfHeal
{
    internal sealed class EndpointInfo
    {
        public string Scheme;
        public string Host;
        public int Port;
        public bool Reachable;

        public string Display()
        {
            string head = string.Empty;
            if (!string.IsNullOrEmpty(Scheme)) head = Scheme + "=";
            return head + Host + ":" + Port.ToString(CultureInfo.InvariantCulture);
        }
    }

    internal sealed class ProxyState
    {
        public int ProxyEnable;
        public string ProxyServer = string.Empty;
        public string ProxyOverride = string.Empty;
        public string AutoConfigURL = string.Empty;
    }

    internal sealed class Diagnosis
    {
        public bool ProxyDead;
        public bool PacDead;
        public bool ProxyStaleDisabled;
        public bool ProxyUnusable;
        public string UnusableDetail = string.Empty;
        public List<EndpointInfo> Endpoints = new List<EndpointInfo>();
        public EndpointInfo PacEndpoint;

        public bool NeedFix()
        {
            return ProxyDead || PacDead || ProxyStaleDisabled || ProxyUnusable;
        }
    }

    internal static class Program
    {
        private const string InetKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Internet Settings";
        private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string RunValueName = "ProxySelfHeal";
        private const string GuardRunValueName = "ProxySelfHealGuard";
        private const string GuardMutexName = "Local\\ProxySelfHeal.Guard.Singleton";
        private const int INTERNET_OPTION_REFRESH = 37;
        private const int INTERNET_OPTION_SETTINGS_CHANGED = 39;
        private const int SW_HIDE = 0;
        private const string Version = "2.0.0";

        // 受管的代理客户端进程名（精确匹配，绝不包含系统服务）
        private static readonly string[] ProxyProcessNames = new string[]
        {
            "Hiddify", "HiddifyNext", "HiddifyCli", "HiddifyCore",
            "clash-verge", "clash-meta", "clash-win64", "Clash for Windows", "mihomo",
            "v2rayN", "v2ray", "xray",
            "Shadowsocks", "ShadowsocksR", "ss-local", "ss-local-win",
            "sing-box", "NekoRay", "NekoBox", "Qv2ray"
        };

        // 探测点：国内稳定 HTTP 端点，避免走 TLS 与出海链路
        private static readonly string[] ProbeUrls = new string[]
        {
            "http://connect.rom.miui.com/generate_204",
            "http://www.baidu.com/",
            "http://wifi.vivo.com.cn/generate_204"
        };

        private static Mutex _guardMutex;

        [DllImport("wininet.dll", SetLastError = true, CharSet = CharSet.Auto)]
        private static extern bool InternetSetOption(IntPtr hInternet, int dwOption, IntPtr lpBuffer, int dwBufferLength);

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetConsoleWindow();

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        private static string DataDir
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ProxySelfHeal"); }
        }

        private static string InstalledExe
        {
            get { return Path.Combine(DataDir, "ProxySelfHeal.exe"); }
        }

        private static string LogPath
        {
            get { return Path.Combine(DataDir, "selfheal.log"); }
        }

        [STAThread]
        private static int Main(string[] args)
        {
            bool hadArgs = args != null && args.Length > 0;
            string mode = "install";
            int delay = 40;
            bool quiet = false;
            bool dryRun = false;
            bool assist = true;

            if (args != null)
            {
                for (int i = 0; i < args.Length; i++)
                {
                    string a = args[i];
                    if (a == null) continue;
                    a = a.Trim().TrimStart('-', '/').ToLowerInvariant();
                    if (a == "install" || a == "i") mode = "install";
                    else if (a == "uninstall" || a == "u" || a == "remove") mode = "uninstall";
                    else if (a == "heal" || a == "h" || a == "run") mode = "heal";
                    else if (a == "status" || a == "s" || a == "check") mode = "status";
                    else if (a == "guard" || a == "g" || a == "watch") mode = "guard";
                    else if (a == "cleanup" || a == "close") mode = "cleanup";
                    else if (a == "simulate-shutdown" || a == "test-shutdown") mode = "shutdown";
                    else if (a == "help" || a == "?" || a == "version" || a == "v") mode = "help";
                    else if (a == "dry-run" || a == "dryrun" || a == "dry") dryRun = true;
                    else if (a == "quiet" || a == "q" || a == "silent") quiet = true;
                    else if (a == "no-assist" || a == "noprocess") assist = false;
                    else if (a == "delay")
                    {
                        if (i + 1 < args.Length)
                        {
                            int d;
                            if (int.TryParse(args[i + 1], NumberStyles.Integer, CultureInfo.InvariantCulture, out d))
                            {
                                if (d < 0) d = 0;
                                if (d > 600) d = 600;
                                delay = d;
                            }
                            i++;
                        }
                    }
                }
            }

            if (mode != "heal" && mode != "status" && mode != "guard") TryAttachConsoleTitle();

            int rc;
            try
            {
                switch (mode)
                {
                    case "help":
                        PrintHelp();
                        rc = 0;
                        break;
                    case "status":
                        rc = RunStatus(dryRun);
                        break;
                    case "heal":
                        rc = RunHeal(delay, dryRun, quiet, assist);
                        break;
                    case "guard":
                        rc = RunGuard();
                        break;
                    case "cleanup":
                        rc = RunCleanup(dryRun, quiet, assist);
                        break;
                    case "shutdown":
                        rc = RunShutdownSimulation(quiet);
                        break;
                    case "uninstall":
                        rc = RunUninstall(quiet);
                        break;
                    default:
                        rc = RunInstall(quiet);
                        break;
                }
            }
            catch (Exception ex)
            {
                if (mode != "guard")
                {
                    Console.WriteLine("发生未预期异常: " + ex.Message);
                }
                Log("FATAL " + ex.ToString());
                rc = 1;
            }

            if (!hadArgs)
            {
                Console.WriteLine();
                Console.WriteLine("按任意键退出...");
                try { Console.ReadKey(true); } catch { }
            }
            return rc;
        }

        private static void TryAttachConsoleTitle()
        {
            try { Console.Title = "ProxySelfHeal v" + Version + " - Windows 系统代理残留自愈补丁"; } catch { }
        }

        private static void HideConsole()
        {
            try { ShowWindow(GetConsoleWindow(), SW_HIDE); } catch { }
        }

        private static void PrintHelp()
        {
            Console.WriteLine("ProxySelfHeal v" + Version + " - Windows 系统代理残留自愈补丁");
            Console.WriteLine();
            Console.WriteLine("用法:");
            Console.WriteLine("  ProxySelfHeal.exe                  一键部署（等同于 --install，双击即可）");
            Console.WriteLine("  ProxySelfHeal.exe --install        部署：复制程序 + 注册登录自检与关机守护 + 立即自检");
            Console.WriteLine("  ProxySelfHeal.exe --uninstall      卸载：移除自启项、结束守护进程并清理程序目录");
            Console.WriteLine("  ProxySelfHeal.exe --status         诊断：只读查看代理现状、端口连通性与出网可用性");
            Console.WriteLine("  ProxySelfHeal.exe --heal           执行一次自愈（登录自启项调用此模式）");
            Console.WriteLine("  ProxySelfHeal.exe --heal --delay 0 立即自愈，不等待代理程序自启");
            Console.WriteLine("  ProxySelfHeal.exe --guard          常驻守护：监听关机/重启/注销，先清理代理再放行");
            Console.WriteLine("  ProxySelfHeal.exe --cleanup        立即清理：强制结束代理软件并关闭系统代理");
            Console.WriteLine("  ProxySelfHeal.exe --dry-run        与 --status/--heal/--cleanup 配合，只判定不修改");
            Console.WriteLine("  ProxySelfHeal.exe --no-assist      自愈时不主动结束代理进程（默认会结束）");
            Console.WriteLine("  ProxySelfHeal.exe --quiet          静默模式，不输出到控制台");
            Console.WriteLine();
            Console.WriteLine("v2.0 新增:");
            Console.WriteLine("  1) 关机/重启/注销前自动强制结束代理软件并关闭系统代理，确保下次开机代理处于关闭状态；");
            Console.WriteLine("  2) 代理端口存活但实际无法出网时，同样判定为故障并复位（修复“端口活着却上不了网”）；");
            Console.WriteLine("  3) 精确进程白名单，只处理代理客户端，不触碰任何系统服务。");
            Console.WriteLine();
            Console.WriteLine("安全边界: 只处理指向本机回环地址(127.0.0.1/localhost/::1)的代理配置；");
            Console.WriteLine("          关机清理只在用户会话结束时执行；全部为当前用户级操作，无需管理员权限。");
        }

        // ---------------- 核心：状态读取 / 判定 / 修复 ----------------

        private static ProxyState ReadState()
        {
            ProxyState s = new ProxyState();
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(InetKeyPath, false))
                {
                    if (k == null) return s;
                    object v = k.GetValue("ProxyEnable", null);
                    if (v != null)
                    {
                        try { s.ProxyEnable = Convert.ToInt32(v, CultureInfo.InvariantCulture); }
                        catch { s.ProxyEnable = 0; }
                    }
                    s.ProxyServer = AsString(k.GetValue("ProxyServer", string.Empty));
                    s.ProxyOverride = AsString(k.GetValue("ProxyOverride", string.Empty));
                    s.AutoConfigURL = AsString(k.GetValue("AutoConfigURL", string.Empty));
                }
            }
            catch (Exception ex)
            {
                Log("读取注册表失败: " + ex.Message);
            }
            return s;
        }

        private static string AsString(object o)
        {
            if (o == null) return string.Empty;
            string s = o as string;
            if (s != null) return s;
            return Convert.ToString(o, CultureInfo.InvariantCulture);
        }

        private static Diagnosis Evaluate(ProxyState st, bool deepCheck)
        {
            Diagnosis d = new Diagnosis();
            d.Endpoints = ParseLoopbackEndpoints(st.ProxyServer);

            bool anyReachable = false;
            EndpointInfo live = null;
            for (int i = 0; i < d.Endpoints.Count; i++)
            {
                if (d.Endpoints[i].Reachable)
                {
                    anyReachable = true;
                    if (live == null) live = d.Endpoints[i];
                }
            }

            if (d.Endpoints.Count > 0 && !anyReachable)
            {
                if (st.ProxyEnable == 1) d.ProxyDead = true;
                else d.ProxyStaleDisabled = true;
            }

            if (!string.IsNullOrEmpty(st.AutoConfigURL))
            {
                EndpointInfo pac = ParseProxyTarget(st.AutoConfigURL);
                if (pac != null)
                {
                    pac.Reachable = IsTcpReachable(pac.Host, pac.Port, 800);
                    d.PacEndpoint = pac;
                    if (!pac.Reachable) d.PacDead = true;
                }
            }

            // v2.0 新增：端口活着不等于代理能用
            if (deepCheck && st.ProxyEnable == 1 && live != null && !d.ProxyDead)
            {
                CheckProxyUsable(live, d);
            }

            return d;
        }

        // 代理端口在监听，但实际请求出不去 —— 典型场景：Hiddify 已自启但未连上节点
        private static void CheckProxyUsable(EndpointInfo live, Diagnosis d)
        {
            try
            {
                if (!DirectInternetReachable())
                {
                    // 整机都没有网，不能归咎于代理配置，保持不动
                    return;
                }

                if (ProxyPathWorks(live.Host, live.Port))
                {
                    return;
                }

                Thread.Sleep(1200);
                if (ProxyPathWorks(live.Host, live.Port))
                {
                    return;
                }

                d.ProxyUnusable = true;
                d.UnusableDetail = live.Display() + " 端口在监听，但经该代理无法访问外网";
            }
            catch (Exception ex)
            {
                Log("代理可用性探测异常: " + ex.Message);
            }
        }

        private static bool DirectInternetReachable()
        {
            for (int i = 0; i < ProbeUrls.Length; i++)
            {
                if (ProbeUrl(ProbeUrls[i], null, 2500)) return true;
            }
            return false;
        }

        private static bool ProxyPathWorks(string host, int port)
        {
            string addr = "http://" + host + ":" + port.ToString(CultureInfo.InvariantCulture);
            for (int i = 0; i < ProbeUrls.Length; i++)
            {
                if (ProbeUrl(ProbeUrls[i], addr, 3500)) return true;
            }
            return false;
        }

        private static bool ProbeUrl(string url, string proxyAddress, int timeoutMs)
        {
            HttpWebRequest req = null;
            try
            {
                req = (HttpWebRequest)WebRequest.Create(url);
                req.Timeout = timeoutMs;
                req.ReadWriteTimeout = timeoutMs;
                req.UserAgent = "ProxySelfHeal/" + Version;
                req.AllowAutoRedirect = false;
                req.KeepAlive = false;
                if (proxyAddress == null)
                {
                    req.Proxy = null;
                }
                else
                {
                    req.Proxy = new WebProxy(proxyAddress, false);
                }

                using (HttpWebResponse resp = (HttpWebResponse)req.GetResponse())
                {
                    int code = (int)resp.StatusCode;
                    try { resp.Close(); } catch { }
                    return code >= 200 && code < 400;
                }
            }
            catch (WebException wex)
            {
                HttpWebResponse r = wex.Response as HttpWebResponse;
                if (r != null)
                {
                    int code = (int)r.StatusCode;
                    try { r.Close(); } catch { }
                    return code > 0;
                }
                return false;
            }
            catch
            {
                return false;
            }
            finally
            {
                if (req != null) { try { req.Abort(); } catch { } }
            }
        }

        private static List<EndpointInfo> ParseLoopbackEndpoints(string proxyServer)
        {
            List<EndpointInfo> list = new List<EndpointInfo>();
            if (string.IsNullOrEmpty(proxyServer)) return list;

            string[] parts = proxyServer.Split(new char[] { ';' }, StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < parts.Length; i++)
            {
                string item = parts[i].Trim();
                if (item.Length == 0) continue;

                string scheme = string.Empty;
                int eq = item.IndexOf('=');
                if (eq > 0)
                {
                    scheme = item.Substring(0, eq).Trim();
                    item = item.Substring(eq + 1).Trim();
                }

                // 剥离 http:// https:// socks5:// 等协议前缀
                int schemeSep = item.IndexOf("://", StringComparison.Ordinal);
                if (schemeSep > 0)
                {
                    if (scheme.Length == 0) scheme = item.Substring(0, schemeSep).Trim();
                    item = item.Substring(schemeSep + 3).Trim();
                }

                // 剥离路径部分，仅保留 host:port
                int pathSep = item.IndexOf('/');
                if (pathSep > 0) item = item.Substring(0, pathSep).Trim();

                string host;
                int port;
                if (!SplitHostPort(item, 80, out host, out port)) continue;
                if (!IsLoopbackHost(host)) continue;

                EndpointInfo e = new EndpointInfo();
                e.Scheme = scheme;
                e.Host = host;
                e.Port = port;
                e.Reachable = IsTcpReachable(e.Host, e.Port, 800);
                list.Add(e);
            }
            return list;
        }

        private static EndpointInfo ParseProxyTarget(string url)
        {
            if (string.IsNullOrEmpty(url)) return null;
            string s = url.Trim();
            int sc = s.IndexOf("://", StringComparison.Ordinal);
            if (sc > 0) s = s.Substring(sc + 3);
            int slash = s.IndexOf('/');
            if (slash > 0) s = s.Substring(0, slash);
            int q = s.IndexOf('?');
            if (q > 0) s = s.Substring(0, q);

            string host;
            int port;
            if (!SplitHostPort(s, 80, out host, out port)) return null;
            if (!IsLoopbackHost(host)) return null;

            EndpointInfo e = new EndpointInfo();
            e.Scheme = string.Empty;
            e.Host = host;
            e.Port = port;
            return e;
        }

        private static bool SplitHostPort(string s, int defaultPort, out string host, out int port)
        {
            host = string.Empty;
            port = 0;
            if (string.IsNullOrEmpty(s)) return false;
            s = s.Trim();

            string portStr = string.Empty;
            if (s.StartsWith("[", StringComparison.Ordinal))
            {
                int close = s.IndexOf(']');
                if (close < 0) return false;
                host = s.Substring(1, close - 1);
                string rest = s.Substring(close + 1).Trim();
                rest = rest.TrimStart(':').Trim();
                portStr = rest;
            }
            else
            {
                int colon = s.LastIndexOf(':');
                if (colon <= 0)
                {
                    host = s;
                    portStr = string.Empty;
                }
                else
                {
                    host = s.Substring(0, colon);
                    portStr = s.Substring(colon + 1);
                }
            }

            host = host.Trim();
            if (host.Length == 0) return false;

            if (portStr.Length == 0)
            {
                port = defaultPort;
                return port > 0;
            }

            int p;
            if (!int.TryParse(portStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out p)) return false;
            if (p <= 0 || p > 65535) return false;
            port = p;
            return true;
        }

        private static bool IsLoopbackHost(string host)
        {
            if (string.IsNullOrEmpty(host)) return false;
            string h = host.Trim().Trim('[', ']').ToLowerInvariant();
            if (h == "localhost") return true;
            if (h == "::1") return true;
            if (h == "0:0:0:0:0:0:0:1") return true;
            if (h == "127.0.0.1") return true;
            if (h.StartsWith("127.", StringComparison.Ordinal)) return true;
            return false;
        }

        private static bool IsLoopbackPac(string url)
        {
            EndpointInfo e = ParseProxyTarget(url);
            return e != null;
        }

        private static bool IsTcpReachable(string host, int port, int timeoutMs)
        {
            TcpClient client = null;
            try
            {
                client = new TcpClient();
                IAsyncResult ar = client.BeginConnect(host, port, null, null);
                bool signaled = ar.AsyncWaitHandle.WaitOne(timeoutMs, false);
                if (!signaled) return false;
                client.EndConnect(ar);
                return client.Connected;
            }
            catch
            {
                return false;
            }
            finally
            {
                if (client != null)
                {
                    try { client.Close(); } catch { }
                }
            }
        }

        private static bool ApplyFix(ProxyState st, bool fixProxy, bool fixPac, bool quiet, string reason)
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(InetKeyPath, true))
                {
                    if (k == null)
                    {
                        Report(quiet, "  无法以写权限打开 Internet Settings 注册表键");
                        Log("ApplyFix 失败：无法打开注册表键");
                        return false;
                    }

                    if (fixProxy)
                    {
                        if (st.ProxyEnable != 0)
                        {
                            k.SetValue("ProxyEnable", 0, RegistryValueKind.DWord);
                            Report(quiet, "  [完成] 已关闭系统代理开关 (ProxyEnable: 1 -> 0)");
                        }
                        if (!string.IsNullOrEmpty(st.ProxyServer))
                        {
                            try
                            {
                                k.DeleteValue("ProxyServer", false);
                                Report(quiet, "  [完成] 已删除残留代理地址 ProxyServer: " + st.ProxyServer);
                            }
                            catch (Exception ex)
                            {
                                Report(quiet, "  [失败] 删除 ProxyServer 出错: " + ex.Message);
                            }
                        }
                    }

                    if (fixPac && !string.IsNullOrEmpty(st.AutoConfigURL))
                    {
                        try
                        {
                            k.DeleteValue("AutoConfigURL", false);
                            Report(quiet, "  [完成] 已删除失效自动配置脚本: " + st.AutoConfigURL);
                        }
                        catch (Exception ex)
                        {
                            Report(quiet, "  [失败] 删除 AutoConfigURL 出错: " + ex.Message);
                        }
                    }
                }

                NotifyProxyChanged();
                Report(quiet, "  [完成] 已通知系统重载代理配置 (WinINET settings changed)");
                Log("修复完成 | 原因=" + reason + " | 原 ProxyEnable=" + st.ProxyEnable
                    + " | 原 ProxyServer=" + st.ProxyServer + " | 原 AutoConfigURL=" + st.AutoConfigURL);
                return true;
            }
            catch (Exception ex)
            {
                Report(quiet, "  [失败] 修复过程异常: " + ex.Message);
                Log("ApplyFix 异常: " + ex.ToString());
                return false;
            }
        }

        // 关机场景使用：无论当前处于什么状态，都强制落到“代理关闭”
        private static bool ForceDisableProxy(ProxyState st, bool quiet, string reason)
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(InetKeyPath, true))
                {
                    if (k == null) return false;

                    if (st.ProxyEnable != 0)
                    {
                        k.SetValue("ProxyEnable", 0, RegistryValueKind.DWord);
                    }
                    if (!string.IsNullOrEmpty(st.ProxyServer))
                    {
                        try { k.DeleteValue("ProxyServer", false); } catch { }
                    }
                    if (IsLoopbackPac(st.AutoConfigURL))
                    {
                        try { k.DeleteValue("AutoConfigURL", false); } catch { }
                    }
                }

                NotifyProxyChanged();
                Report(quiet, "  [完成] 已关闭系统代理并清除代理地址");
                Log("强制关闭系统代理 | 原因=" + reason + " | 原 ProxyEnable=" + st.ProxyEnable
                    + " | 原 ProxyServer=" + st.ProxyServer + " | 原 AutoConfigURL=" + st.AutoConfigURL);
                return true;
            }
            catch (Exception ex)
            {
                Log("ForceDisableProxy 异常: " + ex.Message);
                return false;
            }
        }

        private static void NotifyProxyChanged()
        {
            try { InternetSetOption(IntPtr.Zero, INTERNET_OPTION_SETTINGS_CHANGED, IntPtr.Zero, 0); } catch { }
            try { InternetSetOption(IntPtr.Zero, INTERNET_OPTION_REFRESH, IntPtr.Zero, 0); } catch { }
        }

        // ---------------- 代理进程处置 ----------------

        // 先礼后兵：CloseMainWindow 让客户端有机会自己回滚代理，超时后再强制结束
        private static int TerminateProxyClients(bool quiet, out string report)
        {
            List<Process> pending = new List<Process>();
            StringBuilder sb = new StringBuilder();
            int selfPid = Process.GetCurrentProcess().Id;

            for (int i = 0; i < ProxyProcessNames.Length; i++)
            {
                Process[] ps = null;
                try { ps = Process.GetProcessesByName(ProxyProcessNames[i]); }
                catch { continue; }

                for (int j = 0; j < ps.Length; j++)
                {
                    Process p = ps[j];
                    try
                    {
                        if (p.Id == selfPid) { p.Dispose(); continue; }
                        string nm = p.ProcessName.ToLowerInvariant();
                        if (nm.IndexOf("service", StringComparison.Ordinal) >= 0) { p.Dispose(); continue; }

                        sb.Append(p.ProcessName + "(" + p.Id.ToString(CultureInfo.InvariantCulture) + ") ");
                        try { p.CloseMainWindow(); } catch { }
                        pending.Add(p);
                    }
                    catch { try { p.Dispose(); } catch { } }
                }
            }

            if (pending.Count == 0)
            {
                report = string.Empty;
                return 0;
            }

            Thread.Sleep(900);

            int killed = 0;
            for (int i = 0; i < pending.Count; i++)
            {
                Process p = pending[i];
                try
                {
                    if (!p.HasExited)
                    {
                        try { p.Kill(); } catch { }
                        try { p.WaitForExit(1500); } catch { }
                    }
                    killed++;
                }
                catch { }
                finally
                {
                    try { p.Dispose(); } catch { }
                }
            }

            report = sb.ToString().Trim();
            Report(quiet, "  [完成] 已结束代理进程: " + report);
            Log("结束代理进程 | 目标=" + report + " | 数量=" + killed.ToString(CultureInfo.InvariantCulture));
            return killed;
        }

        // ---------------- 模式实现 ----------------

        private static int RunStatus(bool dryRun)
        {
            ProxyState st = ReadState();
            Diagnosis d = Evaluate(st, true);

            Console.WriteLine("=== 系统代理现状 (HKCU\\" + InetKeyPath + ") ===");
            Console.WriteLine("  ProxyEnable   : " + st.ProxyEnable.ToString(CultureInfo.InvariantCulture));
            Console.WriteLine("  ProxyServer   : " + EmptyHint(st.ProxyServer));
            Console.WriteLine("  ProxyOverride : " + EmptyHint(st.ProxyOverride));
            Console.WriteLine("  AutoConfigURL : " + EmptyHint(st.AutoConfigURL));
            Console.WriteLine();

            Console.WriteLine("=== 本机回环代理端口连通性 ===");
            if (d.Endpoints.Count == 0)
            {
                Console.WriteLine("  未发现指向本机回环地址的代理端口");
            }
            else
            {
                for (int i = 0; i < d.Endpoints.Count; i++)
                {
                    EndpointInfo e = d.Endpoints[i];
                    Console.WriteLine("  " + e.Display() + "  ->  " + (e.Reachable ? "可达 (有程序在监听)" : "不可达 (连接被拒绝)"));
                }
            }
            if (d.PacEndpoint != null)
            {
                Console.WriteLine("  PAC " + d.PacEndpoint.Display() + "  ->  " + (d.PacEndpoint.Reachable ? "可达" : "不可达"));
            }
            Console.WriteLine();

            Console.WriteLine("=== 出网链路探测 ===");
            Console.WriteLine("  直连探测: " + (DirectInternetReachable() ? "可用" : "不可用"));
            if (d.ProxyUnusable)
            {
                Console.WriteLine("  经代理探测: 不可用 (" + d.UnusableDetail + ")");
            }
            else if (d.Endpoints.Count > 0)
            {
                Console.WriteLine("  经代理探测: 可用或未启用代理");
            }
            Console.WriteLine();

            Console.WriteLine("=== 判定结果 ===");
            Console.WriteLine("  " + DescribeDiagnosis(d));
            Console.WriteLine("  登录自愈      : " + (IsAutoStartInstalled() ? "已安装 (登录时自动自检)" : "未安装"));
            Console.WriteLine("  关机清理守护  : " + (IsGuardInstalled() ? "已安装 (关机/重启前清理代理)" : "未安装"));
            Console.WriteLine("  守护进程状态  : " + (IsGuardRunning() ? "运行中" : "未运行"));
            Console.WriteLine();

            if (dryRun && d.NeedFix())
            {
                Console.WriteLine("=== dry-run: 将要执行的动作 ===");
                if (d.ProxyDead) Console.WriteLine("  1) 置 ProxyEnable=0 并删除 ProxyServer (" + st.ProxyServer + ")");
                else if (d.ProxyStaleDisabled) Console.WriteLine("  1) 删除残留 ProxyServer (" + st.ProxyServer + ")，ProxyEnable 已为 0 保持不变");
                else if (d.ProxyUnusable) Console.WriteLine("  1) 代理端口存活但无法出网 -> 置 ProxyEnable=0 并删除 ProxyServer");
                if (d.PacDead) Console.WriteLine("  2) 删除失效 AutoConfigURL (" + st.AutoConfigURL + ")");
                Console.WriteLine("  3) 通知系统重载代理设置");
            }

            return 0;
        }

        private static string DescribeDiagnosis(Diagnosis d)
        {
            if (d.ProxyDead) return "故障态: 系统代理已启用，但其指向的本机端口无人监听 —— 当前浏览器将无法上网";
            if (d.PacDead) return "故障态: 自动配置脚本(PAC)指向的本机端口无人监听 —— 当前浏览器可能无法上网";
            if (d.ProxyUnusable) return "故障态: 代理端口在监听但不能出网（代理软件已启动但未连通节点）—— 当前浏览器将无法上网";
            if (d.ProxyStaleDisabled) return "残留态: 系统代理未启用，但 ProxyServer 留有指向死端口的脏值（不影响上网，建议清理）";
            return "正常: 未发现代理残留、死端口或失效代理";
        }

        private static string EmptyHint(string s)
        {
            if (string.IsNullOrEmpty(s)) return "(空)";
            return s;
        }

        private static int RunHeal(int delaySeconds, bool dryRun, bool quiet, bool assist)
        {
            ProxyState st0 = ReadState();
            Log("HEAL 触发 | ProxyEnable=" + st0.ProxyEnable + " | ProxyServer=" + st0.ProxyServer + " | AutoConfigURL=" + st0.AutoConfigURL);

            Diagnosis d0 = Evaluate(st0, true);
            if (!d0.NeedFix())
            {
                Log("无需处理");
                Report(quiet, "无需处理: 未发现代理残留、死端口或失效代理。");
                return 0;
            }

            // 端口不可达时先等代理程序完成开机自启，避免误清正在恢复中的配置
            if (d0.ProxyDead || d0.PacDead)
            {
                if (delaySeconds > 0)
                {
                    Report(quiet, "检测到代理残留，等待 " + delaySeconds.ToString(CultureInfo.InvariantCulture) + " 秒以确认代理程序是否随开机自启...");
                    Thread.Sleep(delaySeconds * 1000);
                }

                for (int attempt = 0; attempt < 3; attempt++)
                {
                    ProxyState s1 = ReadState();
                    Diagnosis d1 = Evaluate(s1, true);
                    if (!d1.NeedFix())
                    {
                        Log("代理程序已恢复且可出网，取消修复");
                        Report(quiet, "代理程序已恢复监听并可出网，无需修复。");
                        return 0;
                    }
                    if (attempt < 2) Thread.Sleep(12000);
                }
            }

            ProxyState st = ReadState();
            Diagnosis d = Evaluate(st, true);
            if (!d.NeedFix())
            {
                Log("复查后无需处理");
                Report(quiet, "复查后确认无需处理。");
                return 0;
            }

            if (dryRun)
            {
                Report(quiet, "[dry-run] 将修复: " + DescribeDiagnosis(d));
                Console.WriteLine("  ProxyEnable=" + st.ProxyEnable + " ProxyServer=" + EmptyHint(st.ProxyServer));
                return 0;
            }

            // 代理软件失效场景：先结束进程，再复位（避免它退出时又把代理写回来）
            if (d.ProxyUnusable && assist)
            {
                string rep;
                TerminateProxyClients(quiet, out rep);
                Thread.Sleep(500);
            }

            bool fixProxy = d.ProxyDead || d.ProxyStaleDisabled || d.ProxyUnusable;
            bool fixPac = d.PacDead;

            Report(quiet, "开始修复: " + DescribeDiagnosis(d));
            bool ok = ApplyFix(st, fixProxy, fixPac, quiet, DescribeDiagnosis(d));

            if (ok)
            {
                ProxyState after = ReadState();
                Report(quiet, "修复后: ProxyEnable=" + after.ProxyEnable.ToString(CultureInfo.InvariantCulture)
                    + " ProxyServer=" + EmptyHint(after.ProxyServer)
                    + " AutoConfigURL=" + EmptyHint(after.AutoConfigURL));
                string note = d.ProxyUnusable
                    ? "已切换为直连，你现在可以直接上网；代理软件需要重新连接后才能恢复代理上网。"
                    : "代理残留已清除，当前为直连状态。";
                Report(quiet, "  [说明] " + note);
                return 0;
            }

            Report(quiet, "修复未完成，详见日志: " + LogPath);
            return 2;
        }

        private static int RunCleanup(bool dryRun, bool quiet, bool assist)
        {
            ProxyState before = ReadState();
            Log("CLEANUP 触发 | ProxyEnable=" + before.ProxyEnable + " | ProxyServer=" + before.ProxyServer);

            if (dryRun)
            {
                Console.WriteLine("[dry-run] 将要执行：");
                Console.WriteLine("  1) 结束代理客户端进程（白名单：" + string.Join(", ", ProxyProcessNames) + "）");
                Console.WriteLine("  2) 置 ProxyEnable=0 并删除 ProxyServer (" + EmptyHint(before.ProxyServer) + ")");
                Console.WriteLine("  3) 通知系统重载代理设置");
                return 0;
            }

            Console.WriteLine("=== 立即清理代理 ===");
            if (assist)
            {
                string rep;
                TerminateProxyClients(quiet, out rep);
            }
            Thread.Sleep(400);

            ProxyState st = ReadState();
            if (st.ProxyEnable != 0 || !string.IsNullOrEmpty(st.ProxyServer))
            {
                ForceDisableProxy(st, quiet, "手动 --cleanup");
            }
            else
            {
                Report(quiet, "  系统代理已处于关闭状态，无需修改");
                Log("CLEANUP 无需处理 | ProxyEnable=" + st.ProxyEnable + " | ProxyServer=" + st.ProxyServer);
            }

            ProxyState after = ReadState();
            Console.WriteLine("  当前状态: ProxyEnable=" + after.ProxyEnable.ToString(CultureInfo.InvariantCulture)
                + " ProxyServer=" + EmptyHint(after.ProxyServer));
            Console.WriteLine("  直连探测: " + (DirectInternetReachable() ? "可用" : "不可用"));
            return 0;
        }

        // 人工触发一次完整的关机清理流程，用于验证而无需真的重启
        private static int RunShutdownSimulation(bool quiet)
        {
            Log("SHUTDOWN-SIM 触发（人工模拟关机流程）");
            Console.WriteLine("=== 模拟关机清理（不真正关机）===");
            ShutdownCleanup();
            ProxyState after = ReadState();
            Console.WriteLine("  清理后: ProxyEnable=" + after.ProxyEnable.ToString(CultureInfo.InvariantCulture)
                + "  ProxyServer=" + EmptyHint(after.ProxyServer));
            Console.WriteLine("  直连探测: " + (DirectInternetReachable() ? "可用" : "不可用"));
            return 0;
        }

        // ---------------- 常驻守护：关机/重启/注销前清理 ----------------

        private static int RunGuard()
        {
            bool created;
            try
            {
                _guardMutex = new Mutex(true, GuardMutexName, out created);
            }
            catch
            {
                created = false;
            }

            if (!created)
            {
                Log("GUARD 已存在实例，本次启动退出");
                return 0;
            }

            HideConsole();
            Log("GUARD 启动 | PID=" + Process.GetCurrentProcess().Id.ToString(CultureInfo.InvariantCulture)
                + " | v" + Version);

            try
            {
                SystemEvents.SessionEnding += new SessionEndingEventHandler(OnSessionEnding);
                SystemEvents.SessionSwitch += new SessionSwitchEventHandler(OnSessionSwitch);
                Application.Run(new ApplicationContext());
            }
            catch (Exception ex)
            {
                Log("GUARD 主循环异常: " + ex.Message);
            }
            finally
            {
                try { SystemEvents.SessionEnding -= new SessionEndingEventHandler(OnSessionEnding); } catch { }
                Log("GUARD 退出");
            }
            return 0;
        }

        private static void OnSessionSwitch(object sender, SessionSwitchEventArgs e)
        {
            // 会话锁定/解锁不处理，仅记录，便于排查
            try
            {
                if (e.Reason == SessionSwitchReason.SessionLogoff)
                {
                    Log("GUARD 会话注销");
                }
            }
            catch { }
        }

        private static void OnSessionEnding(object sender, SessionEndingEventArgs e)
        {
            e.Cancel = false;
            try
            {
                Log("GUARD 收到会话结束事件 | Reason=" + e.Reason.ToString());
                ShutdownCleanup();
                Log("GUARD 关机清理完成");
            }
            catch (Exception ex)
            {
                Log("GUARD 关机清理异常: " + ex.Message);
            }
        }

        // 关机时序：先让代理客户端退出（它会自己回滚代理），再强制确保代理处于关闭
        private static void ShutdownCleanup()
        {
            ProxyState before = ReadState();
            Log("SHUTDOWN 清理前 | ProxyEnable=" + before.ProxyEnable + " | ProxyServer=" + before.ProxyServer);

            string rep;
            TerminateProxyClients(true, out rep);

            Thread.Sleep(350);
            ProxyState s1 = ReadState();
            if (s1.ProxyEnable != 0 || !string.IsNullOrEmpty(s1.ProxyServer))
            {
                ForceDisableProxy(s1, true, "关机/重启会话结束");
            }

            // 复查：防止客户端退出过程中把配置写回
            Thread.Sleep(250);
            ProxyState s2 = ReadState();
            if (s2.ProxyEnable != 0 || !string.IsNullOrEmpty(s2.ProxyServer))
            {
                Log("SHUTDOWN 检测到回写，二次复位");
                ForceDisableProxy(s2, true, "关机复查二次复位");
            }

            ProxyState after = ReadState();
            Log("SHUTDOWN 清理后 | ProxyEnable=" + after.ProxyEnable + " | ProxyServer=" + after.ProxyServer);
        }

        // ---------------- 安装 / 卸载 ----------------

        private static int RunInstall(bool quiet)
        {
            Console.WriteLine("ProxySelfHeal v" + Version + " - 一键部署");
            Console.WriteLine();

            try
            {
                if (!Directory.Exists(DataDir)) Directory.CreateDirectory(DataDir);

                string self = GetSelfPath();
                bool needCopy = true;
                try
                {
                    needCopy = !string.Equals(Path.GetFullPath(self), Path.GetFullPath(InstalledExe), StringComparison.OrdinalIgnoreCase);
                }
                catch
                {
                    needCopy = true;
                }

                if (needCopy)
                {
                    // 覆盖前先结束旧守护进程，否则文件被占用
                    StopGuardProcess(quiet);
                    File.Copy(self, InstalledExe, true);
                    Report(quiet, "  [完成] 已部署程序: " + InstalledExe);
                }
                else
                {
                    Report(quiet, "  [跳过] 程序已在部署目录中运行");
                }

                bool runOk = false;
                using (RegistryKey run = Registry.CurrentUser.CreateSubKey(RunKeyPath))
                {
                    if (run != null)
                    {
                        run.SetValue(RunValueName, "\"" + InstalledExe + "\" --heal --delay 25 --quiet", RegistryValueKind.String);
                        run.SetValue(GuardRunValueName, "\"" + InstalledExe + "\" --guard", RegistryValueKind.String);
                        runOk = true;
                    }
                }
                if (runOk)
                {
                    Report(quiet, "  [完成] 已注册登录自检项: HKCU\\" + RunKeyPath + "\\" + RunValueName);
                    Report(quiet, "  [完成] 已注册关机清理守护: HKCU\\" + RunKeyPath + "\\" + GuardRunValueName);
                }
                else
                {
                    Report(quiet, "  [失败] 无法写入登录自启动项");
                }

                string uninst = Path.Combine(DataDir, "uninstall.cmd");
                try
                {
                    File.WriteAllText(uninst, "@echo off\r\n\"" + InstalledExe + "\" --uninstall\r\n", Encoding.Default);
                    Report(quiet, "  [完成] 已生成卸载入口: " + uninst);
                }
                catch { }

                // 立即拉起守护进程，无需重新登录即可生效
                bool guardStarted = StartGuard(quiet);

                Report(quiet, "  [信息] 日志文件: " + LogPath);
                Log("安装完成: " + InstalledExe + " | 守护启动=" + guardStarted);

                Console.WriteLine();
                Console.WriteLine("=== 立即执行首次自检 ===");
                int rc = RunHeal(0, false, quiet, true);

                Console.WriteLine();
                Console.WriteLine("部署完成。此后:");
                Console.WriteLine("  - 登录 Windows 时自动执行一次代理残留自检；");
                Console.WriteLine("  - 正常关机/重启/注销前，自动结束代理软件并关闭系统代理，");
                Console.WriteLine("    确保下次开机时系统代理处于关闭状态，不会再出现开机断网。");
                return rc;
            }
            catch (Exception ex)
            {
                Console.WriteLine("  [失败] 部署异常: " + ex.Message);
                Log("安装失败: " + ex.ToString());
                return 1;
            }
        }

        private static bool StartGuard(bool quiet)
        {
            try
            {
                if (IsGuardRunning())
                {
                    Report(quiet, "  [跳过] 守护进程已在运行");
                    return true;
                }

                ProcessStartInfo psi = new ProcessStartInfo();
                psi.FileName = InstalledExe;
                psi.Arguments = "--guard";
                // 必须用 ShellExecute 方式启动：它不继承调用进程的句柄表。
                // 若改用 UseShellExecute=false，常驻的守护进程会继承父进程（安装器、cmd、脚本）
                // 的控制台/管道句柄，调用方会一直等待句柄关闭而卡死。
                psi.UseShellExecute = true;
                psi.WindowStyle = ProcessWindowStyle.Hidden;
                psi.WorkingDirectory = DataDir;
                Process p = Process.Start(psi);
                if (p != null)
                {
                    try { p.Dispose(); } catch { }
                }
                Thread.Sleep(600);
                bool ok = IsGuardRunning();
                Report(quiet, ok ? "  [完成] 关机清理守护已启动" : "  [提示] 守护进程启动较慢，将在下次登录时自动运行");
                return ok;
            }
            catch (Exception ex)
            {
                Report(quiet, "  [提示] 守护进程启动失败: " + ex.Message);
                Log("启动守护失败: " + ex.Message);
                return false;
            }
        }

        private static bool IsGuardRunning()
        {
            try
            {
                Process[] ps = Process.GetProcessesByName("ProxySelfHeal");
                int selfPid = Process.GetCurrentProcess().Id;
                for (int i = 0; i < ps.Length; i++)
                {
                    try
                    {
                        if (ps[i].Id == selfPid) { ps[i].Dispose(); continue; }
                        string cmd = GetCommandLine(ps[i]);
                        bool isGuard = cmd != null && cmd.IndexOf("--guard", StringComparison.OrdinalIgnoreCase) >= 0;
                        ps[i].Dispose();
                        if (isGuard) return true;
                    }
                    catch { try { ps[i].Dispose(); } catch { } }
                }
                return false;
            }
            catch { return false; }
        }

        private static void StopGuardProcess(bool quiet)
        {
            try
            {
                Process[] ps = Process.GetProcessesByName("ProxySelfHeal");
                int selfPid = Process.GetCurrentProcess().Id;
                for (int i = 0; i < ps.Length; i++)
                {
                    try
                    {
                        if (ps[i].Id == selfPid) { ps[i].Dispose(); continue; }
                        string cmd = GetCommandLine(ps[i]);
                        bool isGuard = cmd != null && cmd.IndexOf("--guard", StringComparison.OrdinalIgnoreCase) >= 0;
                        if (isGuard)
                        {
                            try { ps[i].Kill(); ps[i].WaitForExit(1500); } catch { }
                            Report(quiet, "  [完成] 已结束旧的守护进程 (PID " + ps[i].Id.ToString(CultureInfo.InvariantCulture) + ")");
                        }
                        ps[i].Dispose();
                    }
                    catch { }
                }
            }
            catch { }
        }

        // 通过 WMI 读取命令行，用于区分 --guard 与一次性模式
        private static string GetCommandLine(Process p)
        {
            try
            {
                using (System.Management.ManagementObject mo = new System.Management.ManagementObject("Win32_Process.Handle=\"" + p.Id.ToString(CultureInfo.InvariantCulture) + "\""))
                {
                    object o = mo["CommandLine"];
                    return o == null ? null : o.ToString();
                }
            }
            catch { return null; }
        }

        private static int RunUninstall(bool quiet)
        {
            Console.WriteLine("ProxySelfHeal v" + Version + " - 卸载");
            Console.WriteLine();

            try
            {
                bool removed = false;
                using (RegistryKey run = Registry.CurrentUser.OpenSubKey(RunKeyPath, true))
                {
                    if (run != null)
                    {
                        if (run.GetValue(RunValueName) != null)
                        {
                            run.DeleteValue(RunValueName, false);
                            removed = true;
                        }
                        if (run.GetValue(GuardRunValueName) != null)
                        {
                            run.DeleteValue(GuardRunValueName, false);
                            removed = true;
                        }
                    }
                }
                Report(quiet, removed ? "  [完成] 已移除登录自检项与关机清理守护项" : "  [跳过] 未发现自启动项");
                Log(removed ? "卸载: 已移除自启动项" : "卸载: 无自启动项");

                StopGuardProcess(quiet);

                try
                {
                    string uninst = Path.Combine(DataDir, "uninstall.cmd");
                    if (File.Exists(uninst)) File.Delete(uninst);
                    if (File.Exists(LogPath)) File.Delete(LogPath);
                }
                catch { }

                bool copiedExe = false;
                try
                {
                    if (File.Exists(InstalledExe))
                    {
                        File.Delete(InstalledExe);
                        copiedExe = true;
                    }
                }
                catch { }

                if (copiedExe)
                {
                    Report(quiet, "  [完成] 已删除部署的程序文件");
                    try
                    {
                        if (Directory.Exists(DataDir) && Directory.GetFileSystemEntries(DataDir).Length == 0)
                        {
                            Directory.Delete(DataDir);
                            Report(quiet, "  [完成] 已删除部署目录");
                        }
                    }
                    catch { }
                    Console.WriteLine();
                    Console.WriteLine("卸载完成。注意: 本补丁不会改动你当前的代理设置，仅解除登录自检与关机清理。");
                }
                else
                {
                    Console.WriteLine();
                    Console.WriteLine("已解除登录自检与关机清理。程序文件可能因正在运行而未能删除，");
                    Console.WriteLine("可在关闭本窗口后手动删除目录: " + DataDir);
                }
                return 0;
            }
            catch (Exception ex)
            {
                Console.WriteLine("  [失败] 卸载异常: " + ex.Message);
                Log("卸载失败: " + ex.ToString());
                return 1;
            }
        }

        private static bool IsAutoStartInstalled()
        {
            try
            {
                using (RegistryKey run = Registry.CurrentUser.OpenSubKey(RunKeyPath, false))
                {
                    if (run == null) return false;
                    return run.GetValue(RunValueName) != null;
                }
            }
            catch { return false; }
        }

        private static bool IsGuardInstalled()
        {
            try
            {
                using (RegistryKey run = Registry.CurrentUser.OpenSubKey(RunKeyPath, false))
                {
                    if (run == null) return false;
                    return run.GetValue(GuardRunValueName) != null;
                }
            }
            catch { return false; }
        }

        private static string GetSelfPath()
        {
            try
            {
                string p = Assembly.GetExecutingAssembly().Location;
                if (!string.IsNullOrEmpty(p) && File.Exists(p)) return p;
            }
            catch { }
            return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ProxySelfHeal.exe");
        }

        private static void Report(bool quiet, string msg)
        {
            if (!quiet) Console.WriteLine(msg);
        }

        private static void Log(string msg)
        {
            try
            {
                if (!Directory.Exists(DataDir)) Directory.CreateDirectory(DataDir);
                string line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + "  " + msg + "\r\n";
                File.AppendAllText(LogPath, line, new UTF8Encoding(true));

                try
                {
                    FileInfo fi = new FileInfo(LogPath);
                    if (fi.Exists && fi.Length > 256 * 1024) TrimLog(fi);
                }
                catch { }
            }
            catch { }
        }

        private static void TrimLog(FileInfo fi)
        {
            try
            {
                string[] lines = File.ReadAllLines(LogPath, Encoding.UTF8);
                int keep = lines.Length > 300 ? 300 : lines.Length;
                StringBuilder sb = new StringBuilder();
                for (int i = lines.Length - keep; i < lines.Length; i++)
                {
                    sb.AppendLine(lines[i]);
                }
                File.WriteAllText(LogPath, sb.ToString(), new UTF8Encoding(false));
                GC.KeepAlive(fi);
            }
            catch { }
        }
    }
}
