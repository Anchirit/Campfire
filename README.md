# Campfire Linux CLI

适用于 **Ubuntu 22.04 x86_64** 的 Minecraft Java 联机命令行客户端，基于 [Xocio/Campfire](https://github.com/Xocio/Campfire) `1.1.31` 移植。

**Linux 建房 → Windows 原版 Campfire 加入：已通过用户实机互通验证。** Windows 建房 → Linux 加入尚未收到实测结果。

沿用原版 Campfire 的房间协议、邀请码、认证边界与联机方式，固定使用 **EasyTier 2.6.4**、原版九个公共节点和原网络参数。原版 Java Agent、Minecraft TCP 代理与 LAN 公告代码直接复用。本仓库提供 Linux CLI，Windows 端继续使用原版 Campfire。

## 快速开始

### 从源码运行

准备 .NET 8 SDK；自动开放 LAN 需要带 `jdk.attach` 模块的 Java 9+，已在 OpenJDK 17 上验证。Minecraft 由你照常启动，不通过此 CLI 启动。

```bash
git clone https://github.com/Anchirit/Campfire.git
cd Campfire
dotnet build src/Campfire/Campfire.Cli.csproj -c Release
./campfire --help
```

工程没有外部 NuGet 业务包。源码运行需要 .NET 8；下面的自包含构建完成后，运行产物不需要预装 .NET。

### 构建自包含版本

需要 .NET 8 SDK、`curl`、`sha256sum` 和互联网连接：

```bash
bash scripts/build.sh
./campfire --version
```

产物位于 `artifacts/linux-x64/`，包含 .NET 运行时以及经过固定 SHA-256 校验的原项目 EasyTier Linux 2.6.4 归档。可以将整个目录复制到另一台 Ubuntu 22.04 x86_64 机器并执行其中的 `campfire`。

Git 仓库只保存源码、原 Java JAR 和构建脚本；`artifacts/`、`bin/`、`obj/` 不提交到 Git。

### 初始化和检查

```bash
./campfire install
./campfire doctor
./campfire nodes
```

`install` 优先使用自包含目录附带的官方 2.6.4 ZIP；未附带时沿用原安装机制尝试原项目下载源。也支持离线安装同一个固定版本的原始归档：

```bash
./campfire install --archive /path/to/easytier-linux-x86_64-v2.6.4.zip
```

下载失败不会改用其他 EasyTier 版本或替换节点。

## Linux 建房，Windows 加入

此方向已通过用户实际互通验证。

1. 在 Linux 启动 Minecraft Java 并进入自己的世界。
2. 启动房间：

   ```bash
   ./campfire host --name LinuxHost
   ```

3. CLI 通过原 Java Agent 请求开放 LAN，并扫描 Java 监听端口，用原 Minecraft ping 确认游戏端口。
4. 看到 `[ready] host` 后，将输出的 `U/....-....-....-....` 邀请码交给 Windows 玩家。
5. Windows 玩家在原版 Campfire 输入邀请码，再从 Minecraft 的局域网世界列表进入。

如果当前 JVM 不支持附加，可在游戏菜单手动“对局域网开放”，再指定游戏显示的真实端口：

```bash
./campfire host --name LinuxHost --port 51234
```

`51234` 是示例端口。多个游戏并行时可用 `--pid PID` 选择目标游戏。游戏 LAN 端口与 Campfire 网关端口可能不同，CLI 按原版流程处理，不需要手工设置网关端口。

## Windows 建房，Linux 加入

此方向已经实现 CLI 加入流程，但尚未收到真实游戏互通的用户验收结果。

Windows 玩家用原版 Campfire 建房，Linux 玩家拿到真实邀请码后运行：

```bash
./campfire join 'U/XXXX-XXXX-XXXX-XXXX' --name LinuxGuest
```

上面是格式占位符，须替换为实际有效的邀请码。随后从 Linux Minecraft 的**局域网世界列表**进入；CLI 也会显示本机转发地址。

LAN 列表与手动输入地址在部分账号条件下有不同的游戏登录语义。请优先使用 LAN 列表，保持原版行为。游戏版本、模组和账号条件仍由 Minecraft 自身处理，CLI 不绕过认证。

保持 CLI 运行以维持联机。按 `Ctrl+C` 或发送 `SIGTERM` 退出/关闭房间；Minecraft 进程与已开放的世界保持运行。

## 常用参数

```bash
# 房间名、公告、人数上限
./campfire host --name LinuxHost --room-name '朋友的世界' --notice '欢迎加入' --max-players 4

# 指定本地转发端口，仅允许本机游戏访问
./campfire join 'U/真实邀请码' --name LinuxGuest --local-port 25565 --no-lan-share

# 结构化状态输出
./campfire host --name LinuxHost --json

# 检查邀请码格式和网络名，不连接房间
./campfire inspect-code 'U/真实邀请码'
```

| 适用命令 | 参数 | 用途 |
| --- | --- | --- |
| 全局 | `--data-dir PATH`、`--lang en\|zh` | 数据目录和语言 |
| host / join / install / nodes / doctor | `--json` | 结构化状态或结果输出 |
| host / join | `--name NAME`、`--pid PID` | 房间显示名、目标游戏进程 |
| host / join | `--allow-relay` / `--direct-only` | 原版允许中继或仅使用直连的开关 |
| host / join | `--show-address` / `--hide-address` | 原版地址隐私开关；隐藏地址优先禁用 P2P 并使用中继 |
| host | `--port PORT`、`--reuse-code CODE` | 手动 LAN 端口、复用历史邀请码 |
| host | `--room-name TEXT`、`--notice TEXT`、`--max-players N` | 房间信息；人数上限为 2–100，0 表示不限 |
| host | `--auto-lan` / `--no-auto-lan` | 自动开放 LAN |
| host | `--backup` / `--no-backup`、`--voice` / `--no-voice` | 原版备份与语音开关；真实游戏完整矩阵尚未验证 |
| join | `--local-port PORT`、`--search-timeout 30\|60\|120` | 本地转发端口、搜索超时；端口 0 表示自动 |
| join | `--lan-share` / `--no-lan-share` | 是否将加入的房间分享给本机局域网设备 |

完整参数以 `./campfire --help` 为准。`--allow-invalid-game-name` 对应原版游戏名异常时的继续确认。

## 数据与默认配置

默认数据目录为 `$XDG_DATA_HOME/campfire`，未设置时使用 `~/.local/share/campfire`。也可使用 `CAMPFIRE_DATA_DIR` 环境变量或 `--data-dir PATH`。每个数据目录同时运行一个 CLI 会话。

沿用原版配置键与默认值：九个内置节点全部启用、允许中继、地址可见，自动开放 LAN、LAN 共享和备份开启，语音关闭。参数会保存到 `settings.ini`；例如设置过 `--no-auto-lan` 后，可用 `--auto-lan` 再开启。

节点选择和网络开关不写入邀请码，也不会在两端自动同步。CLI 不接收自定义节点或服务端点，即使迁移的设置含 `link.nodes` 自定义 URL，也不会使用。CLI 不采集或发送遥测。当前不提供大厅发布、GUI、启动器修复或模组下载向导。

日志位于 `<数据目录>/Link/link.log`。日志沿用原邀请码遮码；主机 Ready 状态和 JSON 输出会显示完整邀请码，供你分享给其他玩家。退出时清理网络进程及包含网络密钥的 TOML；父 CLI 被强制结束时，Linux supervisor 也会清理它管理的 core。Java Agent 按原版状态文件机制停止工作，不卸载 JVM 中的 Hook，也不结束游戏。

数据目录不能含 `|`，因为原 Java Agent 将该字符用作参数分隔符。自动 LAN 的 Attach JVM 可通过 `JAVA_HOME` 指定。

## 验证状态

| 验证项 | 状态 | 来源与范围 |
| --- | --- | --- |
| Linux 建房 → Windows 原版加入 | **已通过** | 用户于 2026-11-06 反馈实际互通成功；具体游戏、Java 和 Windows Campfire 版本未记录 |
| Windows 建房 → Linux 加入 | 待实机验收 | CLI 流程已实现，尚无该方向的用户实测结果 |
| Release 编译、房间协议和游戏代理 | 已通过 | 本地自动化验证，涵盖原握手、心跳、人数限制、移除响应和加密透传 |
| Linux JVM Attach 与 Hook | 已通过 | 原版 JAR + OpenJDK 17 受控 JVM；覆盖进程/端口归属、映射路径及 publishServer 调用 |
| 真实 EasyTier 创建 / 加入 / TCP 转发 | 已通过 | EasyTier 2.6.4、原节点、同宿主两个独立实例；游戏端使用受控 JVM |
| SIGTERM / SIGKILL 清理 | 已通过 | 验证本 CLI 所属 core 结束和网络密钥配置清理 |
| Java 8、版本/加载器、不同 NAT、IPv6、语音/模组/备份矩阵 | 未完整验证 | 不能从一次互通测试或本地夹具推定所有组合均可用 |

## 测试与来源校验

本地测试需要 .NET 8 SDK、JDK 17+ 和 Python 3：

```bash
bash tests/run-tests.sh
python3 tools/verify-upstream.py
```

测试覆盖原邀请码、精确房间帧、RoomClient/RoomServer、GameProxy、Linux 进程与端口发现、原 Java Agent。来源校验检查房间协议、邀请码、节点、网络配置、Java 源码/JAR 与基线的逐字节一致性。

需要显式验证实际隧道时，可运行以下测试；它会连接原公共节点：

```bash
python3 tests/run-network-smoke.py artifacts/linux-x64/campfire
```

该测试使用真实 EasyTier 和受控 JVM，不新增节点，也不能替代真实游戏或所有网络拓扑的验收。

原三个 JAR 已保留并嵌入程序，无需重建。Java 源码改动后可用 `bash src/java-agent/build.sh` 重建（沿用原构建要求：JDK 21+），随后重新构建 CLI。

## 来源、文档与许可证

- 原项目：[Xocio/Campfire](https://github.com/Xocio/Campfire)，基线提交 [`20bd056`](https://github.com/Xocio/Campfire/commit/20bd056f648c858161cbe7b4d881f402fb4544b0)。感谢原作者 Xocio；本仓库为修改后的 Linux CLI 移植。
- EasyTier：原项目 [2.6.4 发布](https://github.com/EasyTier/EasyTier/releases/tag/v2.6.4)，程序归档与文件哈希固定在安装器中。
- [移植实施与验证记录](docs/LINUX_PORT_IMPLEMENTATION.md)
- [移植前源码审计归档](docs/LINUX_PORTING_AUDIT.md)
- [GPL-3.0 许可证](LICENSE) · [第三方声明](THIRD-PARTY-NOTICES.md) · [上游来源哈希清单](upstream-manifest.json)
