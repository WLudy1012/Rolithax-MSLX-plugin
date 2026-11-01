# Rolithax Launcher Daemon 扩展

为 **Rolithax Launcher** 提供配套服务端能力的 MSLX Daemon 插件。当前包含设备扫码配对、实例图标和资源中心远端直装；后续客户端增强功能统一扩展在此插件中。

| 项目 | 值 |
| --- | --- |
| 插件 ID | `mslx-plugin-rolithax` |
| 程序集与目录 | `Rolithax.Plugin.DaemonExtensions` |
| 当前版本 | `1.0.0`（发布 tag 构建时使用 tag 版本） |
| 最低 MSLX Daemon 版本 | `1.7.3` |
| Android 包名 | `com.wludy.rolithax.launcher` |

插件按单个 DLL 分发，WebPanel 前端构建物嵌入 DLL。插件 ID 与前端包名一致；API 路由遵循 MSLX Daemon 插件规范，保留 `mslx-plugin-rolithax` 前缀。

## 功能

- **扫码配对**：生成一次性二维码，为设备签发独立、可过期和可撤销的受限 API Key。
- **实例图标**：读取实例图标并按需查询公网状态服务，使用本地磁盘缓存。
- **资源中心远端直装**：Daemon 所在机器直接从 Modrinth 下载资源到目标实例，不经 Android 客户端中转；支持模组、插件、数据包与整合包，检查实例权限、兼容性、依赖和摘要，并在暂存后提交文件。
- **Rolithax 中心**：在独立菜单查看和配置扩展 API 的启用状态。

详细路由、权限和迁移说明见 [`Rolithax.Plugin.DaemonExtensions/README.md`](Rolithax.Plugin.DaemonExtensions/README.md)。

## 构建

需要 .NET 10 SDK、Node.js 与 pnpm。MSLX SDK 可通过 `-SdkDir` 指定；未指定时脚本会从当前工作区的 `MSLX-dev` 目录自动探测。

```powershell
./Rolithax.Plugin.DaemonExtensions/pack.ps1 -BuildFrontend -SdkDir D:/MSLX/MSLX-dev/MSLX.SDK
```

产物位于 `Rolithax.Plugin.DaemonExtensions/dist/`：

- `Rolithax.Plugin.DaemonExtensions.dll`：Daemon 插件本体。
- `Rolithax.Plugin.DaemonExtensions.zip`：DLL 和安装说明。

单独检查前端：

```powershell
cd Rolithax.Plugin.DaemonExtensions/Frontend
pnpm install --frozen-lockfile
pnpm typecheck
pnpm build
```

前端包名为 `mslx-plugin-rolithax`，输出入口为 `Frontend/dist/mslx-plugin-entry.js`。该固定入口随 DLL 内嵌；插件图标使用指定的 CDN 链接，CNB 构建不需要安装 Node.js。

## 安装与升级

把 `Rolithax.Plugin.DaemonExtensions.dll` 放到 Daemon 数据目录下的 `Plugins/`，随后重启 Daemon 或从插件管理页热加载。Daemon 运行版本低于 `1.7.3` 时不满足本插件要求。

更换旧插件时先停掉 Daemon，再移除旧版程序集，避免重复注册路由。请保留 `PluginsData/mslx-pair/`、`PluginsData/mslx-icon/` 和 Daemon 用户数据；新版仍读取现有配对配置与图标缓存。

## 自动构建

- GitHub Actions：`main` 推送构建校验并将完整源码同步到 CNB；推送 `v*` tag 时构建 DLL/ZIP、发布 GitHub Release，并同步 `main` 源码与 tag。
- CNB：`main` 推送执行 Release 构建校验；`v*` tag 构建 DLL/ZIP 并发布 CNB Release。
- 两边构建均从上游检出 MSLX SDK，并验证 DLL 内含 `mslx-plugin-entry.js` 与前端资源。

## 致谢

本项目基于 MSLX Daemon SDK 开发，接口设计遵循 [MSLX 插件开发规范](https://mslx.mslmc.cn/plugin-dev/init/start/)。
