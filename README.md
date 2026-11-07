# ProxySelfHeal · Windows 系统代理残留自愈补丁

> 一键修复 **Hiddify / Clash / v2rayN / Shadowsocks / sing-box** 等代理软件**未正常退出就关机**后，导致 Windows 下次开机出现 **「网络已连接，但所有网页打不开」** 的问题。

```
Windows 10 / 11 · .NET Framework 4.x · 单文件 exe · 无需管理员权限 · 无网络行为
```

---

## 一、问题：Hiddify 关机后，开机就断网

这是代理软件用户的高频故障，典型描述是这样的：

> 用 Hiddify 上着网，直接关机没退出程序。第二天开机，Wi-Fi 显示已连接，微信提示网络异常，浏览器所有网页都打不开。必须重新启动一次 Hiddify 才能恢复上网。

如果你遇到的是这个现象，本工具就是为你准备的。

**受影响的软件**（只要是"系统代理"模式的桌面客户端都可能中招）：

| 软件 | 是否常见 |
|---|---|
| Hiddify / Hiddify Next | 非常常见，尤其是 MSIX 商店版本 |
| Clash for Windows / Clash Verge | 常见 |
| v2rayN / v2rayNG(桌面) | 常见 |
| Shadowsocks / ShadowsocksR | 常见（老问题，教程一堆） |
| sing-box / NekoRay / NekoBox | 常见 |

---

## 二、根因：不是 Windows 的锅，但 Windows 也没帮上忙

这类客户端通过修改注册表接管系统流量：

```
HKCU\Software\Microsoft\Windows\CurrentVersion\Internet Settings
    ProxyEnable = 1
    ProxyServer = 127.0.0.1:12334        ← 客户端自己的本地监听端口
```

问题出在三个环节的叠加：

1. **注册表是持久化的**。进程退出后这两项**不会自动消失**。
2. **回滚靠客户端自己**。正常退出时，客户端会主动把 `ProxyEnable` 改回 `0`；但这一步是**业务代码里的逻辑**，不是系统兜底。
3. **关机不给它时间**。关机时 Windows 向程序发送 `WM_QUERYENDSESSION`，若在 `HungAppTimeout`（默认 **5 秒**）内没有响应完，直接 `TerminateProcess` 强制结束。此时 `ProxyEnable=1` 已经落盘，而回滚代码根本没执行到。

于是下次开机：

- Windows **按设计原样读取**注册表 —— 它从不主动重置代理配置（代理本来就是用户的持久设置，系统没有理由清空）；
- 代理开关仍然是开的，指向 `127.0.0.1:12334`；
- 而 Hiddify 没有启动，**这个端口无人监听**，所有请求连接被直接拒绝；
- 结果就是「网络正常但网页全打不开」。而重启一次 Hiddify，它重新监听端口并重写配置，立刻恢复。

> 为什么有的软件还能联网？因为 `ProxyEnable` 只影响 **WinINET** 这一层（浏览器、Office、多数桌面客户端）。走 `WinHTTP` 或原始 socket 的程序不受影响，所以表现为"部分软件能上、部分不能"，很容易被误判成 DNS 或网卡故障。

**结论**：这不是 Windows 的 bug，也不是用户操作错误，而是「用户态代理客户端 + 无服务托管 + 关机强制终止」三者叠加的**结构性缺口**，责任在软件侧。上游短期内不会修，所以需要一层外部兜底。

---

## 三、解决：登录时自动体检，发现残留就复位

`ProxySelfHeal` 只做一件事：**在用户登录时检查系统代理是否指向一个已经死掉的本地端口，如果是，就自动复位。**

效果等价于你手动重启一次代理软件，但全自动、无感知、开机即恢复。

### 判定规则

| 检测到的状态 | 动作 |
|---|---|
| `ProxyEnable=1` + 指向本机回环端口 + 该端口**不可达** | **故障态** → 置 `ProxyEnable=0`，删除 `ProxyServer` |
| `ProxyEnable=0` + `ProxyServer` 仍留有回环死地址 | **残留态** → 仅删除脏值（不影响上网，属卫生清理） |
| `AutoConfigURL`（PAC）指向回环死端口 | 删除失效 PAC |
| 代理端口**仍在正常监听** | 判定为正常，**不做任何修改** |
| 远程代理 / 公司内网代理 / 公网 PAC | **完全不碰** |
| `ProxyEnable=1` 但 `ProxyServer` 为空 | 判定为正常（系统会直连），不动 |

修复后调用 WinINET 的 `InternetSetOption`（`INTERNET_OPTION_SETTINGS_CHANGED` + `INTERNET_OPTION_REFRESH`）通知系统重载配置，**无需重启浏览器、无需重启资源管理器、无需重启电脑**。

---

## 四、快速开始

### 方式一：下载即用（推荐）

1. 到 [Releases](https://github.com/jaja1620/win-proxy-selfheal/releases) 下载 `ProxySelfHeal.exe`
2. **双击运行**，看到「部署完成」即可
3. 结束

支持单文件绿色运行，不写系统目录，不装服务，不改 PATH。

### 方式二：命令行

```cmd
ProxySelfHeal.exe --install
```

### 部署后发生了什么

只有三处改动，全部在当前用户范围内，**不需要管理员权限**：

| 动作 | 位置 |
|---|---|
| 复制程序 | `%LOCALAPPDATA%\ProxySelfHeal\ProxySelfHeal.exe` |
| 注册登录自启 | `HKCU\Software\Microsoft\Windows\CurrentVersion\Run\ProxySelfHeal` |
| 生成卸载入口 | `%LOCALAPPDATA%\ProxySelfHeal\uninstall.cmd` |

部署时会立即执行一次自检并打印当前代理状态。

### 验证是否生效

```cmd
ProxySelfHeal.exe --status
```

正常态输出：

```
=== 系统代理现状 ===
  ProxyEnable   : 0
  ProxyServer   : (空)
  AutoConfigURL : (空)

=== 本机回环代理端口连通性 ===
  未发现指向本机回环地址的代理端口

=== 判定结果 ===
  正常: 未发现代理残留或死端口指向
  开机自愈补丁: 已安装 (登录时自动执行自检)
```

故障态会明确指出问题：

```
  http=127.0.0.1:12334  ->  不可达 (连接被拒绝)
  故障态: 系统代理已启用，但其指向的本机端口无人监听 —— 当前浏览器将无法上网
```

### 已经断网了，想立刻恢复

```cmd
ProxySelfHeal.exe --heal --delay 0
```

---

## 五、命令行参考

| 参数 | 作用 |
|---|---|
| 无参数 | 等同于 `--install`；双击场景使用，结束前等待按键 |
| `--install` | 部署：复制程序 + 注册登录自启 + 立即自检 |
| `--uninstall` | 卸载：移除自启项并清理程序目录 |
| `--status` | 只读诊断，不修改任何配置 |
| `--heal` | 执行一次自愈（自启项调用此模式） |
| `--heal --delay 0` | 立即自愈，不等待代理程序自启 |
| `--dry-run` | 与 `--status` / `--heal` 配合，只判定不修改 |
| `--quiet` | 静默模式，不输出到控制台 |
| `--help` | 显示帮助 |

---

## 六、安全性设计

本工具刻意把权限和影响面压到最小，以下都是硬约束：

1. **只处理本机回环地址**（`127.0.0.1` / `localhost` / `::1`）。远程代理、公司内网代理、PAC 服务器一律不碰。
2. **只处理死端口**。代理端口若仍在正常监听，判定为正常，完全不动。
3. **不误伤正在启动的代理程序**。登录后默认等待 **40 秒** 再判定，最多重试 **3 次**（间隔 12 秒），共约 64 秒恢复窗口——足以覆盖代理客户端开机自启的耗时。
4. **只写 HKCU**。不触碰 HKLM、系统服务、内核驱动、路由表、防火墙。
5. **不需要管理员权限**，不提权、不弹 UAC。
6. **无网络行为**。不联网、不上报、不下载、无遥测。
7. **可审计**。每次动作（含被清理的原值）都写日志：

```
%LOCALAPPDATA%\ProxySelfHeal\selfheal.log
```

（超过 256 KB 自动截断保留最近 300 行）

---

## 七、卸载

任选其一：

```cmd
:: 方式一
%LOCALAPPDATA%\ProxySelfHeal\uninstall.cmd

:: 方式二
ProxySelfHeal.exe --uninstall
```

卸载只解除开机自检，**不会改动你当前的代理设置**。

---

## 八、从源码构建

无需 Visual Studio、无需 .NET SDK，只需要 Windows 自带的 .NET Framework 编译器：

```cmd
build.cmd
```

或手动：

```cmd
%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe ^
  /target:exe /platform:anycpu /optimize+ /codepage:65001 ^
  /out:ProxySelfHeal.exe src\ProxySelfHeal.cs
```

产物约 20 KB。

---

## 九、适用范围与限制

**支持**：Windows 10 / 11（含 Server 2019+），x64 / x86，.NET Framework 4.x（Win10 1809 起系统自带）。

**不覆盖**：

1. 仅处理 **WinINET 层**（浏览器、Office、多数桌面客户端）。`netsh winhttp` 层与环境变量 `http_proxy` 不在处理范围（这两层通常不会被代理客户端写入）。
2. 仅作用于**当前用户**。多用户共用机器需各自部署一次。
3. 这是**故障后自愈**，不是预防。

---

## 十、想从根源避免？

比"残留后修复"更彻底的做法是让代理软件改用 **TUN / 虚拟网卡模式**：

- 通过虚拟网卡 + 路由表接管流量，**完全不写注册表代理**；
- 进程消失后路由自动撤销，内核驱动随之卸载；
- 不存在"指向死端口"的持久化状态，天然免疫本类故障。

代价是需要管理员权限、需要安装虚拟网卡驱动（如 wintun），且**多款代理软件不要同时开启 TUN**（会抢路由）。

如果因为某些原因必须使用系统代理模式，那就留着本工具做兜底。

---

## 十一、FAQ

**Q：会不会把我正常使用的代理关掉？**
不会。判定前提是「端口不可达」。代理程序在运行时端口是活的，工具会直接判定为正常，不做任何修改。

**Q：会不会修改我的公司代理设置？**
不会。只处理指向 `127.0.0.1` / `localhost` / `::1` 的地址，远程代理一律跳过。

**Q：为什么不用计划任务，而用 Run 键？**
Run 键在当前用户登录时触发，无需管理员权限，部署体验更轻。计划任务（尤其 `ONLOGON`）通常需要提权，对分发不友好。

**Q：会不会和 Clash 之类的软件冲突？**
不会。它只读取并复位"指向死端口"的残留，不驻留、不接管流量、不监听任何端口。

**Q：日志里的"无需处理"是正常的吗？**
正常。说明当前系统代理指向的端口是活的，或原本就没有残留。

---

## 十二、License

[MIT](LICENSE)
