using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Microsoft.Win32;

[assembly: AssemblyTitle("ProxySelfHeal")]
[assembly: AssemblyDescription("Windows system proxy residue self-heal patch")]
[assembly: AssemblyProduct("ProxySelfHeal Patch")]
[assembly: AssemblyCompany("")]
[assembly: AssemblyVersion("1.0.0.0")]
[assembly: AssemblyFileVersion("1.0.0.0")]

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
        public List<EndpointInfo> Endpoints = new List<EndpointInfo>();
        public EndpointInfo PacEndpoint;

        public bool NeedFix()
        {
            return ProxyDead || PacDead || ProxyStaleDisabled;
        }
    }

    internal static class Program
    {
        private const string InetKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Internet Settings";
        private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string RunValueName = "ProxySelfHeal";
        private const int INTERNET_OPTION_REFRESH = 37;
        private const int INTERNET_OPTION_SETTINGS_CHANGED = 39;
        private const string Version = "1.0.0";

        [DllImport("wininet.dll", SetLastError = true, CharSet = CharSet.Auto)]
        private static extern bool InternetSetOption(IntPtr hInternet, int dwOption, IntPtr lpBuffer, int dwBufferLength);

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

        private static int Main(string[] args)
        {
            bool hadArgs = args != null && args.Length > 0;
            string mode = "install";
            int delay = 40;
            bool quiet = false;
            bool dryRun = false;

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
                    else if (a == "help" || a == "?" || a == "version" || a == "v") mode = "help";
                    else if (a == "dry-run" || a == "dryrun" || a == "dry") dryRun = true;
                    else if (a == "quiet" || a == "q" || a == "silent") quiet = true;
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

            if (mode != "heal" && mode != "status") TryAttachConsoleTitle();

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
                        rc = RunHeal(delay, dryRun, quiet);
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
                Console.WriteLine("发生未预期异常: " + ex.Message);
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

        private static void PrintHelp()
        {
            Console.WriteLine("ProxySelfHeal v" + Version + " - Windows 系统代理残留自愈补丁");
            Console.WriteLine();
            Console.WriteLine("用法:");
            Console.WriteLine("  ProxySelfHeal.exe                  一键部署（等同于 --install，双击即可）");
            Console.WriteLine("  ProxySelfHeal.exe --install        部署：复制程序 + 注册登录自启 + 立即自检");
            Console.WriteLine("  ProxySelfHeal.exe --uninstall      卸载：移除自启项并清理程序目录");
            Console.WriteLine("  ProxySelfHeal.exe --status         诊断：只读查看代理现状与判定结果");
            Console.WriteLine("  ProxySelfHeal.exe --heal           执行一次自愈（自启项调用此模式）");
            Console.WriteLine("  ProxySelfHeal.exe --heal --delay 0 立即自愈，不等待代理程序自启");
            Console.WriteLine("  ProxySelfHeal.exe --dry-run        与 --status/--heal 配合，只判定不修改");
            Console.WriteLine("  ProxySelfHeal.exe --quiet          静默模式，不输出到控制台");
            Console.WriteLine();
            Console.WriteLine("说明: 仅处理指向本机回环地址(127.0.0.1/localhost/::1)且端口已不可达的代理残留，");
            Console.WriteLine("      不会触碰远程代理、公司代理或正常工作的代理配置。全部为当前用户级操作，无需管理员权限。");
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

        private static Diagnosis Evaluate(ProxyState st)
        {
            Diagnosis d = new Diagnosis();
            d.Endpoints = ParseLoopbackEndpoints(st.ProxyServer);

            bool anyReachable = false;
            for (int i = 0; i < d.Endpoints.Count; i++)
            {
                if (d.Endpoints[i].Reachable) { anyReachable = true; break; }
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
            return d;
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

        private static bool ApplyFix(ProxyState st, bool fixProxy, bool fixPac, bool quiet)
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
                Log("修复完成 | 原 ProxyEnable=" + st.ProxyEnable + " | 原 ProxyServer=" + st.ProxyServer + " | 原 AutoConfigURL=" + st.AutoConfigURL);
                return true;
            }
            catch (Exception ex)
            {
                Report(quiet, "  [失败] 修复过程异常: " + ex.Message);
                Log("ApplyFix 异常: " + ex.ToString());
                return false;
            }
        }

        private static void NotifyProxyChanged()
        {
            try { InternetSetOption(IntPtr.Zero, INTERNET_OPTION_SETTINGS_CHANGED, IntPtr.Zero, 0); } catch { }
            try { InternetSetOption(IntPtr.Zero, INTERNET_OPTION_REFRESH, IntPtr.Zero, 0); } catch { }
        }

        // ---------------- 模式实现 ----------------

        private static int RunStatus(bool dryRun)
        {
            ProxyState st = ReadState();
            Diagnosis d = Evaluate(st);

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

            Console.WriteLine("=== 判定结果 ===");
            Console.WriteLine("  " + DescribeDiagnosis(d));
            Console.WriteLine("  开机自愈补丁: " + (IsAutoStartInstalled() ? "已安装 (登录时自动执行自检)" : "未安装"));
            Console.WriteLine();

            if (dryRun && d.NeedFix())
            {
                Console.WriteLine("=== dry-run: 将要执行的动作 ===");
                if (d.ProxyDead) Console.WriteLine("  1) 置 ProxyEnable=0 并删除 ProxyServer (" + st.ProxyServer + ")");
                else if (d.ProxyStaleDisabled) Console.WriteLine("  1) 删除残留 ProxyServer (" + st.ProxyServer + ")，ProxyEnable 已为 0 保持不变");
                if (d.PacDead) Console.WriteLine("  2) 删除失效 AutoConfigURL (" + st.AutoConfigURL + ")");
                Console.WriteLine("  3) 通知系统重载代理设置");
            }

            return 0;
        }

        private static string DescribeDiagnosis(Diagnosis d)
        {
            if (d.ProxyDead) return "故障态: 系统代理已启用，但其指向的本机端口无人监听 —— 当前浏览器将无法上网";
            if (d.PacDead) return "故障态: 自动配置脚本(PAC)指向的本机端口无人监听 —— 当前浏览器可能无法上网";
            if (d.ProxyStaleDisabled) return "残留态: 系统代理未启用，但 ProxyServer 留有指向死端口的脏值（不影响上网，建议清理）";
            return "正常: 未发现代理残留或死端口指向";
        }

        private static string EmptyHint(string s)
        {
            if (string.IsNullOrEmpty(s)) return "(空)";
            return s;
        }

        private static int RunHeal(int delaySeconds, bool dryRun, bool quiet)
        {
            ProxyState st0 = ReadState();
            Log("HEAL 触发 | ProxyEnable=" + st0.ProxyEnable + " | ProxyServer=" + st0.ProxyServer + " | AutoConfigURL=" + st0.AutoConfigURL);

            Diagnosis d0 = Evaluate(st0);
            if (!d0.NeedFix())
            {
                Log("无需处理");
                Report(quiet, "无需处理: 未发现代理残留或死端口指向。");
                return 0;
            }

            // 故障态时先等待代理程序完成开机自启，避免误清一台正在恢复中的配置
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
                    Diagnosis d1 = Evaluate(s1);
                    if (!d1.ProxyDead && !d1.PacDead)
                    {
                        Log("代理程序已恢复监听，取消修复");
                        Report(quiet, "代理程序已恢复监听，无需修复。");
                        return 0;
                    }
                    if (attempt < 2) Thread.Sleep(12000);
                }
            }

            ProxyState st = ReadState();
            Diagnosis d = Evaluate(st);
            if (!d.NeedFix())
            {
                Log("复查后无需处理");
                Report(quiet, "复查后确认无需处理。");
                return 0;
            }

            bool fixProxy = d.ProxyDead || d.ProxyStaleDisabled;
            bool fixPac = d.PacDead;

            if (dryRun)
            {
                Report(quiet, "[dry-run] 将修复: fixProxy=" + fixProxy + " fixPac=" + fixPac);
                Console.WriteLine("  ProxyEnable=" + st.ProxyEnable + " ProxyServer=" + EmptyHint(st.ProxyServer));
                return 0;
            }

            Report(quiet, "开始修复: " + DescribeDiagnosis(d));
            bool ok = ApplyFix(st, fixProxy, fixPac, quiet);

            if (ok)
            {
                ProxyState after = ReadState();
                Report(quiet, "修复后: ProxyEnable=" + after.ProxyEnable.ToString(CultureInfo.InvariantCulture)
                    + " ProxyServer=" + EmptyHint(after.ProxyServer)
                    + " AutoConfigURL=" + EmptyHint(after.AutoConfigURL));
                return 0;
            }

            Report(quiet, "修复未完成，详见日志: " + LogPath);
            return 2;
        }

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
                        run.SetValue(RunValueName, "\"" + InstalledExe + "\" --heal --delay 40 --quiet", RegistryValueKind.String);
                        runOk = true;
                    }
                }
                if (runOk) Report(quiet, "  [完成] 已注册登录自启动项: HKCU\\" + RunKeyPath + "\\" + RunValueName);
                else Report(quiet, "  [失败] 无法写入登录自启动项");

                string uninst = Path.Combine(DataDir, "uninstall.cmd");
                try
                {
                    File.WriteAllText(uninst, "@echo off\r\n\"" + InstalledExe + "\" --uninstall\r\n", Encoding.Default);
                    Report(quiet, "  [完成] 已生成卸载入口: " + uninst);
                }
                catch { }

                Report(quiet, "  [信息] 日志文件: " + LogPath);
                Log("安装完成: " + InstalledExe);

                Console.WriteLine();
                Console.WriteLine("=== 立即执行首次自检 ===");
                int rc = RunHeal(0, false, quiet);

                Console.WriteLine();
                Console.WriteLine("部署完成。此后每次登录 Windows 都会自动执行一次代理残留自检，");
                Console.WriteLine("若发现代理指向已被关闭的程序端口，会自动复位，无需人工干预。");
                return rc;
            }
            catch (Exception ex)
            {
                Console.WriteLine("  [失败] 部署异常: " + ex.Message);
                Log("安装失败: " + ex.ToString());
                return 1;
            }
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
                    if (run != null && run.GetValue(RunValueName) != null)
                    {
                        run.DeleteValue(RunValueName, false);
                        removed = true;
                    }
                }
                Report(quiet, removed ? "  [完成] 已移除登录自启动项" : "  [跳过] 未发现登录自启动项");
                Log(removed ? "卸载: 已移除自启动项" : "卸载: 无自启动项");

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
                    Console.WriteLine("卸载完成。注意: 本补丁不会改动你当前的代理设置，仅解除开机自检。");
                }
                else
                {
                    Console.WriteLine();
                    Console.WriteLine("已解除开机自检。程序文件可能因正在运行而未能删除，");
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
