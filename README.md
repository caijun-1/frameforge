# FrameForge

**一个绿色免安装的 FPS 游戏增效工具：一键加速、实时帧率监测、游戏内叠加层与画面滤镜。**
单文件 exe 约 145 KB，常驻内存约 20 MB，不注入游戏进程、不安装驱动。

**A portable FPS toolkit for Windows: one-click boost, live frame-time monitoring, in-game overlay and screen filters.**
Single 145 KB executable, ~20 MB resident, no game injection, no kernel driver.

---

## 目录

- [功能](#功能)
- [截图](#截图)
- [系统要求](#系统要求)
- [快速开始](#快速开始)
- [加速做了什么](#加速做了什么)
- [帧率从哪来](#帧率从哪来)
- [画面滤镜](#画面滤镜)
- [快捷键](#快捷键)
- [数据与隐私](#数据与隐私)
- [从源码构建](#从源码构建)
- [项目结构](#项目结构)
- [已知限制](#已知限制)
- [English](#english)

---

## 功能

| 模块 | 能力 |
| --- | --- |
| 一键加速 | 进程优先级、高性能电源计划、系统计时器 1 ms、关闭 Game DVR 与全屏优化、内存整理、进阶：暂停后台无窗口占用大户（二次确认） |
| 实时监测 | CPU / GPU 占用环形表、GPU 温度 / 频率 / 功耗 / 显存、内存水位、FPS 与 1% Low 折线 |
| 画面滤镜 | 9 组预设 + 强度 / 亮度 / 对比度 / 饱和度 / 色温 五维微调，仅游戏或始终生效 |
| 游戏内叠加层 | 置顶半透明小窗，横向 / 纵向 / 简洁三种样式，可缩放、可锁定位置 |
| 其他 | 中英双语、深浅双主题、托盘常驻、开机自启、内存阈值自动整理 |

所有加速项都是**可回滚**的系统设置，点「还原设置」或关闭游戏后逐项恢复原状。

## 截图

| 加速 | 监测 |
| --- | --- |
| ![加速](docs/screenshots/boost.png) | ![监测](docs/screenshots/monitor.png) |

| 滤镜 | 叠加层 |
| --- | --- |
| ![滤镜](docs/screenshots/filter.png) | ![叠加层](docs/screenshots/overlay.png) |

![设置](docs/screenshots/settings.png)

## 系统要求

- Windows 10 1809 及以上 / Windows 11（x64）
- .NET Framework 4.8（Windows 10 1809 起为系统自带）
- 帧率功能需要管理员权限并首次在「设置 → 帧率引擎」下载 Intel PresentMon（约 1 MB）
- 显卡传感器依赖 NVIDIA NVML；其余厂商的传感器读不到时显示 `—`

## 快速开始

1. 下载 `FrameForge.exe`，双击运行，无需安装。
2. 启动游戏并切到游戏窗口，加速页会自动识别铺满屏幕的前台程序。
3. 点 **一键加速**；退出游戏或点 **还原设置** 即可回滚。
4. 想要帧率数字：设置 → 帧率引擎 → 下载 / 修复，然后以管理员身份重启本程序。

## 加速做了什么

| 项目 | 具体动作 | 如何还原 |
| --- | --- | --- |
| 进程优先级 | 游戏进程设为 `HIGH_PRIORITY_CLASS` | 恢复原优先级 |
| 高性能电源计划 | 切换到系统「高性能」方案 | 切回原方案 GUID |
| 系统计时器 1 ms | `timeBeginPeriod(1)` | `timeEndPeriod(1)` |
| 关闭 Game DVR | 写入 `GameDVR_Enabled=0`、关闭全屏优化 | 恢复原注册表值 |
| 内存整理 | 对空闲进程 `EmptyWorkingSet` + 清系统工作集 | 无需还原（缓存会自行重建） |
| 暂停后台进程 | 挂起无窗口且占用靠前的后台进程 | 自动恢复线程 |

> 进阶项「暂停后台进程」会先列出候选进程并要求二次确认，只挂起、不结束。

## 帧率从哪来

通过 **Intel PresentMon** 的 ETW 会话读取前台游戏进程的呈现事件，计算平均 FPS 与 1% Low。
PresentMon 是在运行时按需下载的独立 exe（MIT 协议），不随本项目分发，也不注入游戏。

## 画面滤镜

滤镜走 Windows 放大镜 API 的全屏颜色矩阵（`Magnification.dll`），作用于整个桌面输出：

- 不注入游戏、不安装驱动、不修改游戏文件；
- 「仅游戏」模式下只在检测到全屏游戏时生效；
- 远程桌面等不支持的环境会给出提示并保持原色。

## 快捷键

| 按键 | 作用 |
| --- | --- |
| `Ctrl + Alt + O` | 显示 / 隐藏叠加层 |
| `Ctrl + Alt + B` | 一键加速 / 还原 |

## 数据与隐私

- 配置：`%LOCALAPPDATA%\FrameForge\settings.json`
- 日志：`%LOCALAPPDATA%\FrameForge\log.txt`（超过 1 MB 轮转一份）
- 不联网、不上传任何数据；唯一的网络请求是你主动触发的 PresentMon 下载。

## 从源码构建

只需要 Windows 自带的 .NET Framework 编译器，无需 Visual Studio / .NET SDK：

```powershell
powershell -ExecutionPolicy Bypass -File .\build.ps1
```

输出 `FrameForge.exe`。自检（不需要开界面）：

```powershell
.\FrameForge.exe --selftest   # 结果写入 %TEMP%\frameforge_selftest.log
```

自检覆盖：传感器采样、显卡识别、游戏检测、电源方案、后台进程候选、设置映射、PresentMon 状态、叠加层初始化。

> 代码保持 C# 5 语法（随系统分发的 `csc.exe` 只认 C# 5），请勿使用插值字符串、`?.`、表达式体成员等新语法。

## 项目结构

```
frameforge/
├── build.ps1              # 一键编译（csc + app.manifest）
├── app.manifest           # DPI 感知、UAC 清单
├── src/
│   ├── Program.cs         # 入口、单实例、--selftest
│   ├── Settings.cs        # 配置读写
│   ├── Theme.cs / I18n.cs # 配色与中英文案
│   ├── Util.cs / Native.cs# 日志、P/Invoke 声明
│   ├── Overlay.cs         # 分层窗口叠加层
│   ├── Sys/
│   │   ├── Monitor.cs     # CPU / 内存 / GPU 采样
│   │   ├── Pdh.cs / Nvml.cs   # 性能计数器与显卡传感器
│   │   ├── Fps.cs         # PresentMon 下载与帧率读取
│   │   ├── Boost.cs       # 加速项与回滚
│   │   ├── MemEngine.cs   # 内存整理与提权
│   │   └── Filter.cs      # 全屏颜色矩阵滤镜
│   └── UI/
│       ├── MainForm.cs        # 外壳：标题栏、导航、托盘
│       ├── MainForm.Actions.cs# 业务动作：加速、叠加层、热键
│       ├── Pages.cs           # 五个页面
│       ├── Controls.cs / Widgets.cs # 自绘控件库
└── docs/
    ├── FEASIBILITY.md     # 技术选型与取舍
    └── screenshots/
```

## 已知限制

- 独占全屏（Exclusive Fullscreen）游戏会遮挡叠加层，建议改用无边框窗口；
- 帧率 / 电源计划 / 系统内存整理需要管理员权限，普通权限下只优化本进程；
- 滤镜为桌面级后处理，HDR 与广色域显示器上的观感会与 SDR 略有差异；
- 仅支持 Windows；Linux / macOS 不在计划内。

## 许可

[MIT](LICENSE)。第三方组件声明见 [NOTICE](NOTICE)。

---

## English

**FrameForge** is a portable Windows toolkit for FPS games: one-click boost, live frame-time monitoring, an in-game overlay and desktop-wide screen filters. It ships as a single ~145 KB executable with about 20 MB of resident memory, and it never injects into games or installs a driver.

### Highlights

- **One-click boost** — process priority, high-performance power plan, 1 ms timer resolution, Game DVR / fullscreen optimization off, working-set trim, plus an opt-in "suspend background hogs" step that asks for confirmation first. Every change is reversible.
- **Live monitoring** — CPU/GPU rings, GPU temperature, clock, power and VRAM, memory pressure, FPS and 1% low chart.
- **Screen filters** — nine presets plus intensity, brightness, contrast, saturation and warmth sliders, applied through the Windows Magnification color matrix (game-only or always).
- **In-game overlay** — top-most layered window with three layouts, scaling and position lock.
- Bilingual UI (Chinese / English), dark and light themes, tray mode, autostart.

### Requirements

Windows 10 1809+ / Windows 11 (x64) with .NET Framework 4.8. FPS readings require administrator rights and a one-time download of Intel PresentMon from inside the app.

### Build

```powershell
powershell -ExecutionPolicy Bypass -File .\build.ps1
.\FrameForge.exe --selftest
```

No Visual Studio or .NET SDK needed — the in-box `csc.exe` compiles the sources directly. Sources intentionally stay on C# 5 syntax.

### Data

Settings and logs live in `%LOCALAPPDATA%\FrameForge\`. The app makes no network calls except the PresentMon download you start yourself.

### License

[MIT](LICENSE). Third-party notices in [NOTICE](NOTICE).