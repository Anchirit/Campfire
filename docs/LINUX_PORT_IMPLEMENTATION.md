# Linux CLI 实施与验证记录

目标：Ubuntu 22.04 x86_64。基线：Xocio/Campfire `20bd056f648c858161cbe7b4d881f402fb4544b0`（1.1.31）。此目录是独立 Linux 移植，原 Xocio/Campfire Windows 业务源码没有改动。

## 保留的协议与机制

下列关键源码与基线逐字节相同，可用 `upstream-manifest.json` 校验：

- `src/Campfire/Link/RoomCode.cs`；整个 `src/Campfire/Link/Room/`（RoomServer、RoomClient、RoomProtocol、RoomFraming、RoomInfo、PlayerProfile）。
- `src/Campfire/Link/PublicNodes.cs`、`Runtime/EasyTierConfig.cs`、`Runtime/EasyTierPeer.cs`。
- `src/Campfire/Link/Minecraft/GameProxy.cs`、`GameProtocol.cs`、`GameOverlay.cs`、`MinecraftProbe.cs`、`LanWorldAnnouncer.cs`、`LanOpener.cs`。
- `src/java-agent/dist/` 三个 JAR、`src/java-agent/src/` Java 源码和原 mappings。

没有更改邀请码/校验算法、网络名和密钥派生、消息帧、固定 Fingerprint、玩家资料、认证边界、Minecraft 加密透传或原版节点。`EasyTierConfig.ToToml()` 原样保留；Linux 的 `IsWindows7=false` 使其使用原非 Win7 的 QUIC 配置。原有语音、mods 和备份的底层处理仍在核心源码中，未新增联机功能。

## 平台适配

| 文件/模块 | Linux 实现 |
| --- | --- |
| `Program.cs`、`Campfire.Cli.csproj` | 独立 net8.0 CLI，显式输入代替 WPF、可读状态或 JSON、Ctrl+C/SIGTERM、数据目录排他锁。版本和原 wire format不变 |
| `Platform/SerialContext.cs` | 用单线程队列保持原 LinkSession 状态更新顺序，替代 DispatcherSynchronizationContext |
| `Link/LinkSession.cs` | 仅将 StartHost/StartJoin 的 async void 改为 Task 以便 CLI 等待；去掉两处本地遥测计数调用；其余创建/加入/维护/释放流程保留 |
| `Platform/LinuxProcesses.cs` | 读取 `/proc/<pid>/cmdline` 的 NUL 分隔 argv、exe symlink、`/proc/net/{tcp,tcp6,udp,udp6}` inode 与 fd 归属；仅扫描选定或 Java 进程，不记录账号 token |
| `GameAccount.cs`、`LocalWorldFinder.cs`、`LauncherProcess.cs` | 用上述能力替代 Win32/WMI/IP Helper；保留账号字段提取、Java端口发现、原游戏 ping 与轮询语义 |
| `Worlds/RunningGame.cs`、`GameMappings.cs` | 改用 argv、Linux 路径和冒号 classpath，支持带空格游戏目录；原映射 URL、SHA-1、内置名称表和继承查找保留 |
| `Minecraft/JavaAgent.cs` | 搜索游戏 JVM、JAVA_HOME、`/usr/lib/jvm`、PATH；保留模块检查、提取缓存、标准 Attach 桥与原 JAR；用 ArgumentList 传参并清理取消的桥进程 |
| `Runtime/EasyTierPackage.cs` | 仍为2.6.4；改用原项目 Linux x86_64包与固定哈希、Linux架构判断、Unix可执行位；支持随分发附带的同一原始归档，不接受任意替换二进制 |
| `Runtime/EasyTierInstance.cs`、`Platform/ProcessSupervisor.cs`、`ChildProcessJob.cs` | 原 core/CLI 参数和RPC不变；参数改ArgumentList；管道EOF supervisor替代Windows Job，在父进程死亡时结束自己的core、删除TOML/owner；遗留清理只认自身owner记录及受控命令，不泛杀EasyTier进程 |
| `AppSettings.cs` | 原INI键/格式；Linux XDG或显式数据目录，私有目录权限；不迁移Windows PCLPlus目录 |
| `LinkSettings.cs` | 保留原默认值和内置节点启停；GetNodes只消费原PublicNodes，禁止使用旧配置中的自定义URL |
| `Worlds/GameFolders.cs`、`Link/Diagnostics.cs` | 去掉Windows注册表目录发现；诊断系统版本改为当前OS版本 |
| `Online/OnlineService.cs` | 去掉WPF Color类型及GUI颜色解析；保留原ServiceUrl元数据读取器，本CLI工程不注入ServiceUrl，当前不连接大厅服务 |

没有 GUI/托盘/主题/头像渲染/启动器修复和自动更新。没有新增大厅服务器。CLI 当前不提供大厅发布、模组下载向导或踢人管理命令；原 RoomServer 的准入、名单和扩展响应仍保留，基础邀请码双向联机使用同一原机制。

## EasyTier 制品来源

原项目发布元数据：`https://api.github.com/repos/EasyTier/EasyTier/releases/tags/v2.6.4`。

核实的原始制品：[easytier-linux-x86_64-v2.6.4.zip](https://github.com/EasyTier/EasyTier/releases/download/v2.6.4/easytier-linux-x86_64-v2.6.4.zip)，发布API的digest与下载归档SHA-256一致。单文件哈希从该已核验归档计算，并固定在安装器中。

```text
archive       61b659eaedba658fa66fe47d17e1426cdd77e5d02fa15fed447bb4357c09dfd6
easytier-core 35b854e0c5853c02733870488c977200f6aa06d09c08d5dc7f9f9e150e7be8d5
easytier-cli  8da66644ec50dcf618fccf76780991f52dfbe2939797abc0efe0ef4d4c4d3383
```

依旧使用原项目Gitee/GitHub及原镜像域名作程序下载源，仅更换为已确认存在的Linux asset。程序下载源不是联机节点。未更新EasyTier版本，未配置任何新的STUN/中继/公共节点。

## 已完成的验证

测试宿主为 Ubuntu 22.04 x86_64（6.8内核），临时工具链 .NET SDK 8.0.427 / runtime 8.0.33、OpenJDK 17.0.20.1。自包含发布未启用裁剪，避免反射/资源行为变化。

| 验证 | 结果与实际边界 |
| --- | --- |
| Release源码构建、测试构建 | 0 warnings、0 errors；未构建或修改Windows工程 |
| 原邀请码 | 已知派生、非法校验、128个随机原邀请码往返通过 |
| 房间消息 | 精确请求字节、读写与长度上限、实际loopback RoomClient/RoomServer固定回显、两字节端口、RoomInfo JSON通过 |
| 成员准入 | 心跳/名单、人数上限拒绝、移除回复通过；使用原代码，无额外账号认证 |
| Minecraft代理 | legacy ping经原GameProxy通过；加密请求之后任意字节透传；原压缩帧编解码通过 |
| 原节点与参数 | 默认9节点、原非Win7参数、DirectOnly/HideAddress优先级和禁止消费自定义URL通过 |
| Linux mappings路径 | 带空格目录、冒号classpath和Forge式libraries路径定位通过 |
| Linux进程/端口 | Java进程发现、argv与token脱敏、TCP PID和UDP归属、带空格gameDir通过 |
| 原Agent | 原JAR经Linux jdk.attach加载成功；受控JVM的MinecraftServer命名夹具被原transformer注入并由Hook调用publishServer，LAN结果文件返回正端口。不是实际Minecraft世界 |
| 固定EasyTier包 | 原发布归档/文件哈希校验通过，可执行位设置后版本自检成功 |
| 真实EasyTier链路 | 两个Linux CLI、原9节点配置、真实2.6.4；两次测试房主Ready时分别连接5个和7个原节点；加入者以原邀请码发现房主、取得gate端口、完成原RoomClient握手和本地TCP转发 |
| 真实TCP数据路径 | 本地客户端→加入者port-forward→真实EasyTier→房主原GameProxy→受控JVM的0xFE/0xFF probe成功；不是游戏登录测试 |
| 退出/崩溃 | SIGTERM结束加入者后其core消失；SIGKILL房主CLI后supervisor结束其core并删除网络密钥TOML；未终止JVM |
| 自包含产物 | 包含.NET运行时，直接执行版本/帮助成功；无需用户预装.NET |

常规验证：`bash tests/run-tests.sh`。实际节点冒烟：`python3 tests/run-network-smoke.py artifacts/linux-x64/campfire`。后者显式使用原公共节点；没有用测试节点、额外中继或服务端替换原联机方式。

## 用户实机互通反馈（2026-11-06）

用户确认 **Linux 建房、Windows 原版 Campfire 加入** 已在实际 Minecraft 中互通成功。该结果来自用户实测，与前述受控 JVM 和自动化测试分开记录；具体 Minecraft、Java、Windows Campfire 版本及是否自动开放 LAN 未提供。Windows 建房、Linux 加入尚未反馈实际结果。

## 尚未确认，不能当作已通过

1. **真实 Minecraft：Windows 原版建房→Linux 入服。** 此方向尚无用户实机验收结果；Linux 建房→Windows 原版加入已由用户确认通过，但未记录具体版本组合。
2. Java8目标游戏配合Java9+附加桥；各混淆版本/加载器与模组组合；真实世界自动开放LAN和Overlay显示。夹具只验证了Linux Attach和实际Hook调用链。
3. 不同主机、不同NAT、强制P2P/原节点中继、HideAddress、IPv6、LinuxMinecraft LAN列表和正版/离线账号矩阵。真实EasyTier冒烟使用同一宿主的两个独立实例，不能覆盖全部公网拓扑。
4. 语音与模组/备份在真实游戏目录上的端到端结果；原代码保留不代表每种平台目录/版本均已验证。
5. 原官方ServiceUrl没有随源码给出，当前不启用大厅服务；没有猜测或替换地址。

## 用户验收步骤

Linux 建房、Windows 加入已获用户反馈通过；后续应反向测试Windows建房、Linux加入，再分别验证手动端口与自动LAN/Agent。记录游戏版本、Java版本、原版Campfire版本、两个方向的结果与`Link/link.log`。若失败，按日志定位原流程；不要添加备用节点、换EasyTier版本或绕过认证。
