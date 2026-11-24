# Rifflo

Rifflo 是 Windows 11 x64 上的透明歌曲悬浮窗。播放音乐时，歌名、歌手和跟随音乐变化的频谱会出现在桌面上。暂停约两秒后，悬浮窗自动收起，继续播放时再出现。

<img src="docs/images/overlay-preview.png" width="478" alt="Rifflo 外观预览，上方为蓝色频谱，下方为歌名和歌手">

## 快速开始

当前版本 **0.1.2** 从源码构建。在 Windows 11 x64 上解压源码，进入包含 `global.json` 的目录并打开 PowerShell。构建需要 **.NET SDK 10.0.404**，可用 `dotnet --version` 核对。[下载源码 ZIP](https://github.com/huwany1/Rifflo/archive/refs/heads/main.zip)

<details>
<summary>安装项目指定的 .NET SDK</summary>

以下命令把 SDK 安装到项目的 `.tools` 目录，并在当前 PowerShell 窗口中启用它。[微软安装脚本文档](https://learn.microsoft.com/zh-cn/dotnet/core/tools/dotnet-install-script)

```powershell
New-Item -ItemType Directory -Force .tools | Out-Null
Invoke-WebRequest 'https://dot.net/v1/dotnet-install.ps1' -OutFile '.tools\dotnet-install.ps1'
powershell -NoProfile -ExecutionPolicy Bypass -File '.tools\dotnet-install.ps1' -Version 10.0.404 -Architecture x64 -InstallDir '.tools\dotnet' -NoPath
$env:Path = "$PWD\.tools\dotnet;$env:Path"
dotnet --version
```

</details>

**构建应用。** 在同一个 PowerShell 窗口运行以下命令。

```powershell
$project = '.\src\Rifflo\Rifflo.csproj'
dotnet restore $project -r win-x64 -p:SelfContained=true --locked-mode
dotnet publish $project -c Release -r win-x64 --self-contained true --no-restore -o .\artifacts\publish\win-x64
```

**开始播放。** 双击 `artifacts\publish\win-x64\Rifflo.exe`，然后在播放器里播放一首歌。悬浮窗默认出现在主屏左下角。双击系统托盘里的 Rifflo 图标可打开设置，即使没有播放音乐，也能用样例预览外观。发布目录已包含运行时，复制到其他 Windows 11 x64 电脑时请保留整个目录。

## 调整悬浮窗

| 想做什么 | 在哪里操作 |
| --- | --- |
| 移动和固定位置 | 拖动歌名或频谱，松手即保存。右键托盘图标，勾选**锁定位置并点击穿透**，鼠标即可操作下方窗口。 |
| 调整歌名和频谱的排列 | 在**设置 → 布局与文字**中选择上下或左右排列，也可交换歌名与频谱的顺序。长歌名支持**单向循环**和**左右往返**，短歌名保持静止。 |
| 只看歌名，或使用预制动画 | 在**设置 → 频谱**中关闭**显示频谱**即可只看歌曲信息。保留显示、关闭**采集实时频谱**则会使用预制动画。 |
| 修改外观和帧率 | 设置中可调字号、文字与频谱颜色、不透明度、柱数和灵敏度。托盘菜单的**悬浮窗动画帧率**可从默认的 30 帧切换到 60 帧。 |
| 临时隐藏或找回窗口 | 右键托盘图标选择**隐藏悬浮窗**或**恢复悬浮窗**。要重置位置，打开**设置 → 位置与播放器 → 回到左下角**。 |

## 播放器与频谱

Rifflo 从 Windows 系统媒体会话读取歌曲信息。播放器需要向系统提供歌名和播放状态。多个播放器同时播放时，可在**设置 → 位置与播放器 → 优先播放器**中指定来源。酷狗音乐支持自动识别，也支持自定义安装目录。

**有歌名，频谱却在重复同一段动画？** 打开设置，顶部会显示当前的采集状态。Rifflo 通过 WASAPI 采集选中播放器及其子进程的音频。采集暂时不可用，或多个活动媒体会话共享音频进程、无法区分来源时，会自动切换到预制动画，恢复后再切回实时频谱。浏览器的多个标签页也可能共用一个音频进程。同一进程里未登记媒体会话的标签页声音可能混入频谱。

## 在抖音直播伴侣中显示歌名

1. 播放一首歌，确认桌面上已出现悬浮窗，在 Rifflo 托盘菜单中勾选**直播捕获模式**。
2. 在直播伴侣中打开**添加素材 → 窗口捕获**，选择 **Rifflo 桌面悬浮窗**。如果列表已打开，刷新或重开列表。**Rifflo 外观预览**是设置中的样例窗口。
3. 如果还用 OBS 捕获游戏，将 Rifflo 素材放在 OBS 画面上方，即可单独调整歌名的位置和大小。开播前在直播伴侣预览中检查透明背景，以及暂停和恢复播放后的画面。

## 开发与测试

应用使用 C#、WPF 和 NAudio。[应用源码](src/Rifflo) · [测试项目](tests) · [桌面验证脚本](scripts)

在仓库根目录运行以下检查，分别验证播放器选择与音频处理、窗口定位、悬浮窗布局与滚动。

```powershell
dotnet restore .\tests\Rifflo.CoreChecks\Rifflo.CoreChecks.csproj --locked-mode
dotnet run --project .\tests\Rifflo.CoreChecks -c Release --no-restore
dotnet run --project .\tests\Rifflo.PlacementChecks -c Release
dotnet run --project .\tests\Rifflo.OverlayChecks -c Release
```

<details>
<summary>桌面验收与诊断</summary>

完成上方构建后，可在 Windows 11 的交互式桌面用 PowerShell 7.4 或更高版本运行验收。窗口检查会移动鼠标，请在桌面空闲时执行。以下示例检查直播捕获窗口，结果保存在 `artifacts/verification/broadcast/`。

```powershell
pwsh -NoProfile -File .\scripts\Verify-Rifflo.ps1 -Mode Broadcast -PublishDirectory .\artifacts\publish\win-x64
```

日常设置保存在 `%LOCALAPPDATA%\Rifflo\settings.json`。测试时可通过以下环境变量指定独立的设置和诊断文件。

| 变量 | 用途 |
| --- | --- |
| `RIFFLO_SETTINGS_PATH` | 指向测试用设置文件。每个设置路径对应一个应用实例。 |
| `RIFFLO_DIAGNOSTICS` | 指定诊断快照文件，查看播放器来源、频谱模式和采集状态。 |
| `RIFFLO_FRAME_TRACE` | 已启用诊断时设为 `1`，在快照中记录频谱和滚动文字的帧时间。 |

</details>

## 许可证

Rifflo 采用 [GNU Affero General Public License v3.0 only](LICENSE)（`AGPL-3.0-only`）。
