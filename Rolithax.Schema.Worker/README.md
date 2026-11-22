# Rolithax Schema 宿主机任务

Worker 在 Daemon 所在主机上用独立低权限账户运行。Rolithax 中心的「宿主机任务」开关默认关闭。只有开关打开且 Worker 通过本机通道验证后，客户端才会收到该能力。

客户端只提交预设任务 ID。Worker 根据 MSLX 用户 ID、实例编号和本机配置筛选任务，执行固定的二进制文件和参数。执行程序、工作目录与环境变量都由主机管理员控制。每个任务最长 30 秒，输出最多 4096 字符；插件和 Worker 记录身份、实例、任务和结果，不把输出、密钥或命令参数写进审计日志。

## 本机配置

在 Windows 或 Linux 安装 .NET 10 Runtime，从 GitHub Release 下载对应平台的 Worker 压缩包。需要从源码构建时可执行以下命令。

~~~powershell
dotnet publish ./Rolithax.Schema.Worker -c Release -r win-x64 --self-contained false
dotnet publish ./Rolithax.Schema.Worker -c Release -r linux-x64 --self-contained false
~~~

将程序和下方两个配置文件分别放到操作系统保护的本机目录。生成同一份 32 字节随机密钥并以 Base64 填入两个配置的 `secret`。密钥不得进入仓库、Daemon WebPanel 或实例目录。

Worker 的配置支持以下字段。示例里的账户、路径、ID、密钥都需要替换为该 Daemon 对应的值。

~~~json
{
  "secret": "<base64-32-byte-key>",
  "executableDirectory": "/usr/local/libexec/rolithax-tasks",
  "workDirectory": "/var/lib/rolithax-schema-worker",
  "socketPath": "/run/rolithax-schema-worker/control.sock",
  "daemonUid": 1001,
  "tasks": [
    {
      "id": "check-runtime",
      "title": "查询运行时版本",
      "description": "返回管理员配置的运行时版本。",
      "program": "runtime-version",
      "arguments": ["--version"],
      "users": ["<MSLX 用户 ID>"],
      "instances": [1],
      "timeoutSeconds": 5
    }
  ]
}
~~~

用 Daemon 用户管理页里的稳定用户 ID 填写 `users`。配对设备对应独立用户，给原账号的授权不会自动转给配对设备。系统管理员也需要在对应任务内登记。使用专用工具或诊断程序，将二进制和配置目录设为管理员可写，Worker 账户只读。工作目录仅供 Worker 账户使用。Daemon 或 Worker 账户对方的密钥配置只需各自能读其中之一。

Daemon 进程设置环境变量 `ROLITHAX_SCHEMA_WORKER_LINK` 为本机连接配置的绝对路径。连接配置只保留 `secret` 和 Linux 的 `socketPath`。

## 版本预检

需要补充元数据探测时，可在预设上登记 `"versionProbe": { "kind": "mcdrPlugin", "target": "my_plugin" }`。`kind` 也可使用 `minecraftPlugin` 或 `mod`，`target` 与 Schema 清单的预检 ID 一致。每条预设只对应一个实例。用户和实例权限仍由 `users` 与 `instances` 限定。

把预设指向管理员审核的只读版本工具，并按需授予 Worker 账户对目标元数据的读取权限。工具须在退出码为 0 时向标准输出写出一行版本，例如 `2.1.0`。客户端在元数据无法确认版本时才显示登记的探测，并在用户确认后请求执行。Worker 重启后需要重新确认。该预设只用于版本预检，普通任务列表不会显示它。

## Linux

建立独立系统账户 `rolithax-worker` 和共享通道组 `rolithax-ipc`，让 Daemon 账户加入该组。Worker 的 UID 须与 Daemon 账户不同，不能使用 root。配置填写 Daemon 的实际 UID。管理员安装 Worker、任务二进制和配置，并把服务单元保存到 systemd。

~~~ini
[Unit]
Description=Rolithax Schema Host Worker

[Service]
User=rolithax-worker
Group=rolithax-ipc
ExecStart=/opt/rolithax-schema-worker/Rolithax.Schema.Worker --config /etc/rolithax-schema-worker/worker.json
RuntimeDirectory=rolithax-schema-worker
RuntimeDirectoryMode=0750
StateDirectory=rolithax-schema-worker
StateDirectoryMode=0700
UMask=0077
NoNewPrivileges=yes
CapabilityBoundingSet=
AmbientCapabilities=
ProtectSystem=strict
ProtectHome=yes
PrivateTmp=yes
PrivateDevices=yes
RestrictAddressFamilies=AF_UNIX
KillMode=control-group
Restart=on-failure

[Install]
WantedBy=multi-user.target
~~~

Worker 检查当前 UID、Linux capability 与 `NoNewPrivileges`。连接经 Unix socket，校验对端 UID 和每次握手的认证信息。重启 Daemon 使环境变量和组资格生效，再开启 Rolithax 中心的「宿主机任务」。

## Windows

建立一个没有管理员权限的专用本机账户，用任务计划程序将 Worker 配置为开机启动，以该账户运行，关闭「使用最高权限运行」。程序参数为 `--config <Worker 配置绝对路径>`。启动程序时使用完整路径，并让该账户保有运行、读取本机配置与写入专用工作目录所需的最小权限。

Worker 配置使用 Windows 的绝对目录路径，填写运行 Daemon 的 Windows 账户 SID（`daemonSid`），移除 `daemonUid` 与 `socketPath`。Daemon 连接配置仅需 `secret`。Worker 通过具有限制 ACL 的本机命名管道接收请求，启动时检查 Worker 与 Daemon 账户不是同一身份，且 Worker 不拥有管理员身份。将 Daemon 安装目录、凭据和不相关的实例目录留在 Worker 账户权限之外。

## 验证

用配置中列出的用户连接 Daemon，查询实例可用任务。先选择仅查询版本的预设，核对客户端显示的退出状态、Worker 日志以及未授权账户的任务列表。修改预设配置后重启 Worker。关闭「宿主机任务」后，插件拒绝后续任务请求。
