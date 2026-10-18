# Rolithax Launcher 扩展插件

由 **WLudy1012** 开发的 MSLX Daemon 第三方插件，为 **Rolithax Launcher** 提供统一的服务端扩展能力，集中承载客户端所需的整合与增强功能。
当前包含扫码配对、设备授权管理、服务端图标和资源中心远端直装，以**单个 DLL** 分发；后续增强功能可继续集成在此插件中。
统一入口为 `RolithaxPlugin`，ID 为 `mslx-plugin-rolithax`，前端包名与此一致。
插件版本从 **1.0.0** 起，发布 tag 构建时自动同步 tag 版本；声明的最低 **MSLX Daemon 版本为 1.5.10.2**。
插件图标为内嵌的硫磺史莱姆 PNG，由 `/plugins/{id}/{version}/icon.png` 下发。

> Daemon 的 PluginManager 对每个程序集只识别第一个 `IPlugin` 实现，因此合并采用单一入口，
> 规范端点前缀为 `/api/plugin/mslx-plugin-rolithax/pair`、`/api/plugin/mslx-plugin-rolithax/icon` 和 `/api/plugin/mslx-plugin-rolithax/resources`。

## 安装

1. 运行 `pack.ps1`（需 .NET 10 SDK；`MSLX.SDK` 自动探测：仓库内 `MSLX-dev`、
   平级 `MSLX-dev`、平级 `RolithaxLauncher/MSLX-dev`；兼容本地旧目录 `MSLX-Android/MSLX-dev`，也可用 `-SdkDir` 或
   `-p:MSLX_SDK_DIR=<SDK 目录>` 指定）得到 `dist/Rolithax.Plugin.DaemonExtensions.dll`；
2. 将 `Rolithax.Plugin.DaemonExtensions.dll` 复制到 Daemon 数据目录的 `Plugins/` 子目录：
   - Windows：`%APPDATA%\MSLX\MSLXData\DaemonData\Plugins\`
   - macOS：`~/Library/Application Support/MSLX/MSLXData/DaemonData/Plugins/`
3. 重启 Daemon，或在面板插件管理中热加载；
4. 日志出现 `[mslx-plugin-rolithax] Rolithax 扩展已挂载配对、图标和资源安装接口`，再确认面板出现「设置 → 扫码配对」。

### 从旧版本升级

1. 停止 Daemon，移除旧 `MSLX.Plugin.AndroidThirdpartyAddons.dll`（旧 ID `mslx-plugin-android-thirdparty-addons`）、`MSLX.Plugin.AndroidExtensions.dll`（旧 ID `mslx-plugin-android-extensions`）、`MSLX.Plugin.Extras.dll` 或 `MSLX.Plugin.PairingServerIcon.dll`，再放入本版 `Rolithax.Plugin.DaemonExtensions.dll`，避免端点重复注册。
2. 保留 `PluginsData/mslx-pair/`、`PluginsData/mslx-icon/` 和 Daemon 用户数据；改名继续读取原数据位置，不要求重新配对。
3. 放入新的 `Rolithax.Plugin.DaemonExtensions.dll`，启动 Daemon 并刷新面板。Android 客户端需要使用新的规范接口前缀。

---

## 一、扫码配对（`/api/plugin/mslx-plugin-rolithax/pair`）

在已授权的客户端（Rolithax Launcher / 面板 / curl）生成**一次性配对二维码**，手机扫码后自动换取一枚
**独立受限**的 API Key，免去手输长 Key。每台配对设备对应一个独立的 Daemon 用户：可撤销、可过期
（默认 30 天，可选 1–365 天）、可审计，服务端只保存 API Key 前缀，完整 Key 仅返回给扫码设备一次。

`redeem` 允许匿名兑换；配置地址和设备管理要求 **admin** 角色，生成二维码要求登录用户。

### 授权期限与撤销

- **设备记录原子更新**：设备记录的读取、修改、写回在同一把锁内完成（兑换、撤销、过期清理互不覆盖）；兑换时先落设备记录、再创建用户，任何时刻都不会出现「Key 有效但管理列表里没有」的设备。撤销只有在确认对应用户已删除后才会把记录标成已撤销。
- **配对设备签发的新设备受上级约束**：配对设备也可以生成配对码，但新设备的到期时间不会晚于签发设备，签发设备被撤销、到期或删除时，它签发的下级设备一并失效（状态 `issuer-revoked` / `issuer-removed`）。
- **请求期校验**：本插件的全部接口在每次请求时核对配对设备的撤销、到期和签发链，失效的凭据当场删除并返回 401，不依赖定时清理。定时清理每分钟运行一次，并在插件加载时立即执行，用来收回停用期间到期的设备、已删除签发者名下的设备，以及没有设备记录的孤儿配对账号。
- **插件卸载即撤销**：Daemon 自身的 API Key 鉴权不识别设备到期时间，所以插件停用、移除或在面板中覆盖更新（调用 `OnUnload`）时会撤销全部配对设备（状态 `plugin-unloaded`），需要重新扫码。按下文「从旧版本升级」先停止 Daemon 再替换 DLL 不触发此行为。直接删除 DLL 且 Daemon 未走卸载流程时，插件没有机会收回凭据，请同时在用户管理中删除 `pair_` 开头的配对用户。
- **签发者失效即作废**：兑换时重新核对签发者仍然存在、`scope=full` 的签发者仍是管理员，并把权限收敛到签发者当前拥有的实例；签发者删除后，未兑换的配对码立即不可兑换。
- **替换旧设备需要出示旧 Key**：`redeem` 请求可带 `previousApiKey`。只有它对应一台未撤销、未到期、指纹与本次提交一致的已配对设备时，才会撤销该旧设备（其下级设备改挂到新设备）。仅凭 `deviceFingerprint` 不会撤销任何设备；不带或带错时新设备与现有设备并存，由管理员在设备列表中撤销。

首次安装时需要管理员在面板中保存 Daemon 对外地址；未配置地址时，`/codes` 会返回 400，避免二维码携带不可访问的请求地址。

| 方法 | 路径 | 说明 |
| --- | --- | --- |
| GET | `/config` | 读取已保存的 Daemon 地址和当前用户可执行的操作 |
| PUT | `/config` | 管理员保存 Daemon 对外地址 |
| POST | `/codes` | 生成一次性配对码（TTL 120s，仅内存保存）。管理员可选择 `scope=full\|limited` 和 `resources[]`；普通用户强制使用自己的资源权限 |
| POST | `/redeem` | 兑换配对码（匿名端点，插件内部强校验：HMAC 签名 / 时效 / 一次性 / IP 限速与失败锁定 / 签发者仍有效）。可选 `previousApiKey` 用于换发同一设备的旧凭据 |
| GET | `/devices` | 已配对设备列表（脱敏：Key 前缀、指纹前缀、状态、有效期） |
| POST | `/devices/{deviceId}/revoke` | 撤销设备（删除配对用户，Key 立即失效） |

二维码载荷为 `mslxp1:` + Base64Url(JSON)，`payload` 字段即二维码原文；面板与 App 只负责显示/回传，
**不自行拼装或验签**（客户端无安装密钥）。配对码 120 秒过期、单次使用、仅内存保存；同 IP 每分钟
最多 10 次兑换、连续失败 5 次临时锁定 15 分钟。

## 二、服务端图标（`/api/plugin/mslx-plugin-rolithax/icon`）

为实例统一提供图标，供 App / 面板实例卡片展示。**来源优先级**（结果带 24 小时磁盘缓存）：
实例目录 `server-icon.png` → 插件磁盘缓存 → 第三方状态 API（`mcsrvstat.us` → `mcstatus.io`，
仅当 `server.properties` 显式配置了**公网** `server-ip` 才外发）。无可用来源返回 `404 + message`。

逐实例校验 `server:{id}` 资源权限（admin / system-admin 直通）。

| 方法 | 路径 | 说明 |
| --- | --- | --- |
| GET | `/server/{id}` | 返回图标 PNG 字节流（`image/png`）；无图标时返回 `{code,message}` JSON |
| POST | `/server/{id}/refresh` | 清除该实例的第三方图标磁盘缓存，下次请求重新拉取 |

## 三、资源中心远端直装（`/api/plugin/mslx-plugin-rolithax/resources`）

客户端提交已校验的 Modrinth v3 项目与版本清单后，插件在 Daemon 所在机直接下载到实例目录，资源包不会经过客户端中转。每次任务都会重新读取 Modrinth 元数据并检查项目与版本对应关系、游戏版本、加载器、服务端兼容性、必需依赖和冲突。写入前按实例加锁，检查摘要、文件大小和安全路径，在实例目录内暂存后原子替换；失败时回滚并保留无法恢复的备份。

插件按当前 Daemon 用户逐个检查 `server:{id}` 实例权限。任务查询和取消仅允许发起任务的用户操作。

| 方法 | 路径 | 说明 |
| --- | --- | --- |
| POST | `/install` | 启动远端安装任务。请求包含 `instanceId`、`gameVersion`、`loader` 和 `items[]`（`provider`、`projectId`、`versionId`、`type`）；当前只接受 `modrinth` 来源 |
| GET | `/install/{id}` | 查询任务进度与安装文件清单 |
| POST | `/install/{id}/cancel` | 请求取消尚未结束的任务 |

支持 `mod`、`plugin`、`datapack`、`modpack` 四种类型。整合包只安装服务端可用文件以及 `server-overrides/`（没有时使用 `overrides/`）；拒绝超出大小上限、没有可信摘要、穿越实例目录或经过符号链接的安装目标。

安装限额（每个任务）：

- 暂存区总量上限 4 GB，下载的资源文件、整合包内清单列出的下载文件、解压出的覆盖文件共用同一份配额；以实际写入的字节计量，压缩条目或清单里声明的大小只用于提前拒绝，整合包本体展开后立即释放配额。
- 单个文件 1 GB；任务内文件数量上限 20000，整合包条目数上限 40000，清单 4 MB。
- 暂存期间持续检查实例所在磁盘，剩余空间低于 1 GB 时中止并回滚。

---

普通用户生成二维码时，服务端忽略请求中的 `scope` 和 `resources`，直接复制当前登录用户的 `Resources`，新设备角色固定为 `user`。管理员可生成完整权限设备，也可生成指定实例的受限设备。

## 数据位置

- 配对配置：`<AppData>/PluginsData/mslx-pair/Config.json`（安装密钥 + Daemon 地址 + 设备记录，设备记录含签发者 `issuedBy`）
- 图标缓存：`<AppData>/PluginsData/mslx-icon/IconCache/<实例 id>.png`（TTL 24 小时）
- 配对设备对应的用户：Daemon `UserList.json` 中用户名形如 `pair_dxxxxxxxxxx`

## 回归测试

```powershell
dotnet test ./Rolithax.Plugin.DaemonExtensions.Tests -p:MSLX_SDK_DIR=<MSLX.SDK 目录>
```

测试用内存实现替身替换 Daemon 的用户与插件配置，覆盖并发更新、派生凭据、设备替换、期限与卸载、签发者失效和安装限额。
