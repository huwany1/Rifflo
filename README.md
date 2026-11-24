# Rifflo

Rifflo 是 Windows 11 x64 的桌面歌曲悬浮窗。默认上方显示频谱，下方以单行显示当前歌曲信息，两者左对齐。播放时优先使用当前播放器的真实音频，采集暂不可用时会自动切换为预制动画。

## 双击运行

在 Windows 11 x64 上按[开发与发布](#开发与发布)中的命令构建 Rifflo 0.1.2，然后双击发布目录中的 `Rifflo.exe`。发布目录自带 .NET Desktop Runtime，移动或共享时请保留整个目录。应用以普通用户权限运行。如果旧版正在运行，请先从旧版的托盘菜单退出。

- 有歌曲正在播放时，悬浮窗自动显示。没有可显示的歌曲时，Rifflo 留在系统托盘。托盘图标可能收纳在任务栏的 `^` 菜单里。
- 右键托盘图标可打开**设置**、切换**锁定位置并点击穿透**、**隐藏悬浮窗 / 恢复悬浮窗**或**退出**。双击图标或再次运行程序也会打开设置。
- 右键托盘图标，打开**悬浮窗动画帧率**，选择**30 帧（默认）**或**60 帧**。帧率即时应用于悬浮窗和外观预览，并在退出后保留。**设置 → 布局与文字 → 启用 60 帧动画**与托盘选项同步。恢复默认设置会回到 30 帧。
- 60 帧让动态频谱和滚动歌名获得更密集的画面更新。两档使用相同的歌名滚动速度、预制频谱节奏和音频分析。静态短歌名无需动画，隐藏悬浮窗并关闭预览后停止动画。
- 默认悬浮窗位于主屏幕工作区左下角，左侧和底部各留 24 DIP。按住文字、频谱或组件内部空白处即可拖动，松手保存位置。悬浮窗默认置顶，拖动时保留当前应用的键盘焦点。可从设置或托盘开启**锁定位置并点击穿透**，使鼠标点击落到底层窗口。
- 设置中的**回到左下角**仅重置位置。调整布局或加宽后，窗口会在当前工作区内按需平移。保存位置所在的显示器断开时，悬浮窗回到主屏工作区左下角。
- 暂停播放时，频谱先回落，悬浮窗约两秒后收起。恢复播放时重新出现。手动隐藏会跨歌曲切换维持，直到选择恢复。
- 在设置的**布局与文字**中可选择“上频谱、下歌名”“上歌名、下频谱”“左歌名、右频谱”或“左频谱、右歌名”。竖排左对齐，横排底部对齐。文字显示宽度可调范围为 160～640 DIP，默认 260 DIP。工作区较窄时会临时收窄文字视口。
- 文字超出可见宽度时会滚动。**单向循环**在末尾停留后回到开头，**左右往返**则从末尾平滑滚回开头。短文本保持静止，关闭频谱时长文本仍可滚动。
- 从托盘打开**设置**，在**频谱**区域可独立调整**显示频谱**和**采集实时频谱**。两项默认开启。关闭采集时，正在播放的歌曲会立即使用预制动画。关闭显示时只显示歌曲信息，采集和频谱动画停止，重新显示时沿用上次的采集偏好。两个开关均即时生效并跨重启保存。
- 设置窗口会显示当前是正在连接、实时频谱、手动预制、自动降级、静音或暂停。无播放时打开设置可用样例预览查看样式、滚动和拖动效果。样例无需启动音频采集。
- 歌手、字号、两种颜色、不透明度、柱数、灵敏度、置顶和优先播放器也可即时调整。预制动画适用 8～32 根柱条及全部四种布局。

设置保存在 `%LOCALAPPDATA%\Rifflo\settings.json`。旧配置保留已有位置和外观。缺少**采集实时频谱**字段的配置默认允许采集，原有**显示频谱**偏好继续生效。缺少 `animationFrameRate` 或其值无效时使用 30 帧。测试环境可设置 `RIFFLO_SETTINGS_PATH` 指定独立配置文件。同一个配置路径只运行一个实例。显式设置 `RIFFLO_DIAGNOSTICS` 为文件路径时，程序每秒记录一份快照，包含帧率档位、展示模式、降级原因、采集状态、音频包和 FFT 计数、实际柱高、频带和悬浮窗位置。另设 `RIFFLO_FRAME_TRACE=1` 可采集最近的柱高与文字位移提交时间，用于帧间隔验收。

## 直播捕获模式

1. 启动 Rifflo 并播放有歌名的音乐，等桌面悬浮窗出现。
2. 在 Rifflo 设置顶部或托盘右键菜单勾选**直播捕获模式**。两个入口同步，切换即时生效，重启保留。可以按需开启**悬浮窗置顶**和**锁定位置并点击穿透**。
3. 在 OBS 的录制场景只添加针对游戏窗口的**游戏捕获**，用现有方式把 OBS 画面送入抖音直播伴侣。
4. 在直播伴侣打开**添加素材 → 窗口捕获**，刷新或重开列表后选择 **Rifflo 桌面悬浮窗**。把它放在 OBS 素材上方，在直播伴侣画布中单独移动和缩放。设置里的样例窗口名为 **Rifflo 外观预览**。

新安装和缺少 `broadcastCaptureEnabled` 的旧配置默认关闭直播捕获。**采集实时频谱**可独立开关，关闭它时仍可使用预制频谱。

暂停播放后，Rifflo 的桌面频谱先回落，悬浮窗约两秒后收起，恢复音乐时重新显示原窗口。通过托盘手动隐藏会立即收起，并保持到选择**恢复悬浮窗**。恢复时有歌曲正在播放，原窗口会重新显示。直播预期是收起期间只显示底层 OBS 画面，恢复后原素材继续显示歌名和频谱。窗口列表、透明画面及隐藏后的实际表现请在你的直播伴侣预览中确认，可在开播前检查。

## 播放器兼容性

- 播放器需发布 Windows 系统媒体会话和有效歌名。Rifflo 从正在播放的会话中优先选择设置指定的播放器，其次参考 Windows 当前会话。首选播放器暂停后，其他正在播放的会话可接替。列表显示当前系统媒体会话中出现的播放器。[Microsoft GSMTC 文档](https://learn.microsoft.com/en-us/uwp/api/windows.media.control.globalsystemmediatransportcontrolssession)
- 支持自动识别酷狗音乐。酷狗可装在自定义目录，Rifflo 通过 Windows 注册信息和活动音频会话识别播放器进程。
- 实时频谱使用 WASAPI 进程回环，采集范围限于确认的播放器进程及其子进程。同一来源有多个活动的 Windows 媒体会话，或者不同的活动媒体来源共享一棵音频进程树时，Rifflo 会播放预制动画。设置会说明当前的来源歧义或采集故障，恢复可采集后自动切回。浏览器同一进程树内若有未向 Windows 登记媒体会话的发声标签页，进程回环无法按标签页识别这些音频。[Microsoft 进程回环示例](https://learn.microsoft.com/en-us/samples/microsoft/windows-classic-samples/applicationloopbackaudio-sample/)
- 首次匹配、打开采集及等待首个音频包各有约 3 秒的等待上限。采集中持续 5 秒无数据包会触发重新连接。健康连接收到静音包或播放器明确静音时，柱条自然回落。一般的同播放器切歌会复用健康连接，输出设备变化时会重新匹配。自动降级后按 3、5、7、9 秒的间隔逐步重试，媒体或设备事件可提前触发检查。
- 显示器布局或 DPI 变化后会重新核对工作区。

## 开发与发布

用 `.NET SDK 10.0.404` 构建，版本由 `global.json` 固定。应用目标为 `net10.0-windows10.0.22621.0`，使用 WPF、Win32 和 WinForms 托盘图标。`NAudio.Core` 和 `NAudio.Wasapi` 均固定为 `3.3.0`，依赖锁文件位于 `src/Rifflo/packages.lock.json`。

在仓库根目录运行以下 PowerShell 命令可把 SDK 装进项目的 `.tools/`。如果本机已安装相同版本，可在后续命令中使用系统 `dotnet`。[Microsoft 安装脚本文档](https://learn.microsoft.com/en-us/dotnet/core/tools/dotnet-install-script)

```powershell
New-Item -ItemType Directory -Force .tools | Out-Null
Invoke-WebRequest 'https://dot.net/v1/dotnet-install.ps1' -OutFile '.tools\dotnet-install.ps1'
powershell -NoProfile -ExecutionPolicy Bypass -File '.tools\dotnet-install.ps1' -Version 10.0.404 -Architecture x64 -InstallDir '.tools\dotnet' -NoPath
.\.tools\dotnet\dotnet.exe --version
```

从仓库根目录还原、构建、运行核心检查并发布 Windows x64 自包含包。

```powershell
$sdk = '.\.tools\dotnet\dotnet.exe'
& $sdk restore .\src\Rifflo\Rifflo.csproj -r win-x64 -p:SelfContained=true --locked-mode
& $sdk build .\src\Rifflo\Rifflo.csproj -c Release -r win-x64 --self-contained -warnaserror --no-restore
& $sdk restore .\tests\Rifflo.CoreChecks\Rifflo.CoreChecks.csproj --locked-mode
& $sdk run --project .\tests\Rifflo.CoreChecks\Rifflo.CoreChecks.csproj -c Release --no-restore
& $sdk run --project .\tests\Rifflo.PlacementChecks\Rifflo.PlacementChecks.csproj -c Release -- --desktop-layout
& $sdk run --project .\tests\Rifflo.OverlayChecks\Rifflo.OverlayChecks.csproj -c Release
$release = '.\artifacts\publish\win-x64-broadcast-capture'
& $sdk publish .\src\Rifflo\Rifflo.csproj -c Release -r win-x64 --self-contained true --no-build --no-restore -o $release
```

| 路径 | 内容 |
| --- | --- |
| `src/Rifflo/` | 应用生命周期、托盘、设置、媒体会话、音频和悬浮窗 |
| `src/Rifflo/Assets/` | 黑胶声浪原图与 EXE、托盘、设置窗口共用的多尺寸图标 |
| `tests/Rifflo.CoreChecks/` | 会话选择、音频进程匹配和频谱、配置保存与容错检查 |
| `tests/Rifflo.IdentityChecks/` | 本机酷狗 Shell 注册、运行镜像和签名验证，以及受控文件位置和身份变更 |
| `tests/Rifflo.PlacementChecks/` | 24 DIP 坐标换算、屏幕变更与无效位置、透明度恢复检查。`--desktop-layout` 只读显示实际屏幕工作区与 DPI |
| `tests/Rifflo.OverlayChecks/` | WPF 实际文字测量、端点字形、布局、144 Hz 帧率调度、卡顿恢复和可控时间滚动检查。`--rendering-smoke default` 或 `--rendering-smoke software` 会在交互桌面短暂打开测试悬浮窗，分别验证默认渲染与进程独立的软件渲染 |
| `tests/Rifflo.PlaybackFixture/` | 系统媒体测试播放器、已知频率 PCM、静音及断流控制、Edge 测试页面 |
| `tests/Rifflo.BrowserTreeProbe/` | 同一专用 Edge 进程树中两种已知频率的独立进程回环验收探针 |
| `scripts/Verify-Rifflo.ps1` | 已发布 EXE 的直播捕获窗口、频谱、Windows 桌面、托盘、播放和资源占用验收 |
| `scripts/Verify-Rifflo-Animation.ps1` | 真实托盘切档、保存与恢复，以及对比两档的有效更新、帧间隔和资源占用 |

## 运行验收

在 Windows 11 的交互式桌面上使用 PowerShell 7.4 或更高版本，从项目根目录依次运行验收脚本。窗口检查会控制鼠标，需要在桌面空闲时运行。连接两个显示器时可为 `UI` 模式添加 `-CrossMonitor` 来验证真实跨屏拖动。脚本使用独立配置路径和托盘图标，夹具模式只选择测试播放器的 GSMTC 会话。`Edge` 模式使用独立的临时浏览器配置，`Performance` 模式默认对无歌、播放可见和播放隐藏分别取样至少 60 秒。证据默认保存到 `artifacts/verification/<模式小写>/`，可用 `-PublishDirectory` 和 `-EvidenceDirectory` 指定目录。

```powershell
$release = '.\artifacts\publish\win-x64-broadcast-capture'
pwsh -NoProfile -File .\scripts\Verify-Rifflo.ps1 -Mode Broadcast -PublishDirectory $release
pwsh -NoProfile -File .\scripts\Verify-Rifflo-Animation.ps1 -PublishDirectory $release
pwsh -NoProfile -File .\scripts\Verify-Rifflo.ps1 -Mode Spectrum -PublishDirectory $release
pwsh -NoProfile -File .\scripts\Verify-Rifflo.ps1 -Mode Fixtures -PublishDirectory $release
pwsh -NoProfile -File .\scripts\Verify-Rifflo.ps1 -Mode Functional -PublishDirectory $release
pwsh -NoProfile -File .\scripts\Verify-Rifflo.ps1 -Mode Edge -PublishDirectory $release
pwsh -NoProfile -File .\scripts\Verify-Rifflo.ps1 -Mode EdgeSharedTree -PublishDirectory $release
pwsh -NoProfile -File .\scripts\Verify-Rifflo.ps1 -Mode UI -PublishDirectory $release
pwsh -NoProfile -File .\scripts\Verify-Rifflo.ps1 -Mode Overlay -PublishDirectory $release
pwsh -NoProfile -File .\scripts\Verify-Rifflo.ps1 -Mode Performance -PublishDirectory $release
```

## 许可证

Rifflo 采用 GNU Affero General Public License 第 3 版（`AGPL-3.0-only`）。[许可证全文](LICENSE)
