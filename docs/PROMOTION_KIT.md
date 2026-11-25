# FrameForge 开源推广与涨星文案包

> 以下文案均已针对各平台风格定制，可直接复制发布。图片可直接选用仓库内的 `docs/social-preview.png` 与 `docs/screenshots/v1.6.5/overlay-hud.png`。

---

## 一、GitHub 仓库后台一键设置（还差 1 步）

- **Topics 与 Description**：已通过 GitHub API 自动写入完成。
- **Social Preview（社交分享封面）**：打开 `https://github.com/caijun-1/frameforge/settings` → 找到 **Social preview** → 点击 **Edit → Upload an image**，上传仓库中的 `docs/social-preview.png`（1280×640）。

---

## 二、V2EX「分享创造」发帖模板

**标题**：
`[开源] 写了款 7MB 单文件 FPS 游戏增效工具 FrameForge：水墨武侠 UI + 现代 HUD 叠加层 + 100% 可回滚加速`

**正文**：
```markdown
自己平时打 FPS 游戏，一直觉得微星小飞机 + 游戏加加 + 各种加速工具装一堆太重了，有些还会往游戏里注入 DLL 或者偷偷改完系统设置不还原。

于是用纯 C#（.NET Framework 4.8 + 原生 GDI+，直接 `csc.exe` 单文件编译，不引第三方 UI 库）写了个轻量级开源工具 **FrameForge**，目前刚发到 **v1.6.5**。

### 核心特点
1. **极轻单文件**：整个程序只有 ~7 MB，免安装双击即用，零广告零遥测。
2. **默认零注入（反作弊友好）**：帧率通过 Intel PresentMon ETW 采集，叠加层默认用分层透明窗口绘制；独占全屏下支持无缝交给 RTSS 接管（同步多色主题与字号层级）。
3. **v1.6.5 全新现代 HUD 叠加层**：毛玻璃渐变暗底 + 主题色竖条 + 放大主视觉 FPS + 等宽不抖动列对齐，支持 5 套配色主题（朱砂 / 翠绿 / 粉紫 / 青蓝 / 极简）和 3 档底板自由切换。
4. **所有优化 100% 事务级回滚**：基于自研 SCTL v2 事务预写日志（带 HMAC 校验），哪怕游戏崩溃或被强杀，重启/看门狗也会把电源计划、计时器、核心亲和性、挂起的后台进程全部还原。
5. **硬件级画面滤镜 & 系统体检**：支持 Gamma LUT + NVAPI 数字振动、DDC/CI 硬件背光调节、ICC 校色导入，以及 VBS / HAGS / MPO / MSI 中断一键体检切换。

仓库地址（MIT 开源）：https://github.com/caijun-1/frameforge
Release 下载：https://github.com/caijun-1/frameforge/releases/latest

欢迎各位提 Issue 或 PR，觉得有用也求个 Star 支持！
```

---

## 三、HelloGitHub 自荐模板

前往 `https://github.com/521xueweihan/HelloGitHub/issues/new/choose` 选择「推荐开源项目」：

- **项目名称**：FrameForge
- **项目地址**：`https://github.com/caijun-1/frameforge`
- **开源协议**：MIT
- **推荐理由 / 简介**：
  > 一款仅 7 MB 的单文件 Windows FPS 游戏增效与性能监控工具。采用 C# 与纯原生 GDI+ 实现水墨武侠双主题界面，默认不注入游戏进程（基于 PresentMon ETW 采集帧率与 1% Low），支持现代毛玻璃 HUD 叠加层与 RTSS 双后端、硬件级画面滤镜/ICC 校色、赛后帧率战绩报告，以及基于事务日志的可崩溃恢复系统加速。

---

## 四、Reddit (`r/OptimizedGaming` / `r/opensource`) 发帖模板

**Title**:
`[Open Source] FrameForge v1.6.5 — 7 MB portable Windows FPS toolkit (PresentMon/RTSS modern HUD, 100% reversible boost, hardware filters, zero DLL injection)`

**Body**:
```markdown
Hi everyone, I've been building **FrameForge**, an open-source (MIT), single-file (~7 MB) Windows toolkit for FPS gamers and hardware enthusiasts.

Instead of installing multiple heavy background suites or injecting hooks into anti-cheat games, FrameForge runs as a single portable `.exe` compiled cleanly against .NET Framework 4.8.

### What's inside (v1.6.5):
- **Modern HUD & RTSS Overlay**: Translucent glass HUD with enlarged hero FPS, monospaced fixed-width columns (no jitter), 5 colour themes (Cinnabar, Emerald, Neon, Cyber, Mono), and 3 backdrop tiers (`HUD` / `Subtle` / `None`). Works via layered window by default (zero injection) or hands off seamlessly to RTSS with matching colour tags for exclusive fullscreen.
- **ETW Frame-Time & Hardware Telemetry**: Powered by Intel PresentMon for FPS & 1% Low, NVML/ADL for GPU temp/clock/power/VRAM, plus automatic post-match performance reports (last 300 sessions with FPS graphs & CSV export).
- **100% Reversible System Boost (WAL Journal)**: Priority class, 1 ms timer, high-perf power plan, core topology steering (P-cores / 3D V-Cache CCD), and optional background process suspend (strictly skips ACE/EAC/BattlEye/Vanguard). Every change is recorded in an HMAC-signed Write-Ahead Log (`BoostJournal`) with an out-of-process watchdog so even a crash or force-kill rolls everything back cleanly.
- **Non-invasive Screen Filters & ICC**: 23 presets + 6-axis tuning via Magnification / Gamma LUT + NVAPI Digital Vibrance, hardware DDC/CI backlight control, and `.icc`/`.icm` profile support.
- **System Check-up**: One-click audit & toggle for VBS/HVCI, HAGS, MPO, Game DVR, FSO, and MSI interrupt mode.

GitHub: https://github.com/caijun-1/frameforge
Release v1.6.5 (with SHA-256): https://github.com/caijun-1/frameforge/releases/tag/v1.6.5

Would love feedback from the optimization community — especially on the new HUD readability and core scheduler behavior!
```

---

## 五、B站 / 小红书 / 抖音短视频标题与脚本大纲

**爆款备选标题**：
1. 《别再装一堆臃肿软件了！7MB 开源单文件搞定游戏帧率 HUD + 画面滤镜 + 一键优化》
2. 《反作弊游戏不让显示帧率？这个 7MB 开源神器不注入游戏，崩溃还能自动还原系统》
3. 《用 C# 手搓水墨风游戏工具箱，单文件 7MB 居然塞进了这么多硬核功能》

**45 秒脚本节奏**：
- `0-5s`：展示游戏内新版朱砂/赛博青毛玻璃 HUD 实拍对比（“不装小飞机也能有这么帅的帧率监控？”）。
- `5-15s`：展示文件夹里仅一个 `FrameForge.exe`（7 MB），打开水墨双主题界面，演示切换 5 套 HUD 配色。
- `15-30s`：演示滤镜 16:9 拖动对比（雪地/雾天瞬间清晰）+ 一键加速与事务日志自动还原（强调“反作弊不拦截、退出不留垃圾”）。
- `30-45s`：展示赛后战绩帧率曲线 + GitHub 免费开源链接（引导三连/Star）。

---

## 六、Winget 官方仓库提交命令（可选）

已为你生成好 `packaging/winget/manifests/j/JunCai/FrameForge/1.6.5/` 下的三份清单文件（含精确 SHA-256）。
只需 Fork `microsoft/winget-pkgs`，把该 `1.6.5` 文件夹拷贝到对应目录提 PR，通过审核后全球用户即可直接：
```powershell
winget install JunCai.FrameForge
```


---

## 七、GitHub 站内 Awesome 列表与 Scoop PR 模板

以下为 GitHub 站内高星聚合仓库的提交素材，直接 Fork 对应仓库并提交 PR 即可：

### 1. Awesome 列表单行条目（英文）

适用于 `Awesome-Windows`、`awesome-dotnet`、`awesome-csharp`、`awesome-gaming` 等仓库的 `Utilities` / `Gaming` 分类：

```markdown
- [FrameForge](https://github.com/caijun-1/frameforge) - Lightweight (~7 MB) portable Windows FPS toolkit with transactional reversible system boost, PresentMon/RTSS modern HUD overlay, and non-invasive hardware screen filters. (`MIT`)
```

**PR 标题**：`Add FrameForge (Windows FPS toolkit & modern HUD overlay)`

**PR 描述**：
```markdown
Adds [FrameForge](https://github.com/caijun-1/frameforge), a lightweight (~7 MB) MIT-licensed portable Windows FPS toolkit written in C# (.NET Framework 4.8 / native GDI+):

- One-click reversible system boost backed by an HMAC-signed write-ahead journal and crash-recovery watchdog
- Zero-DLL-injection FPS & 1% Low monitoring via Intel PresentMon ETW with a modern translucent HUD (plus optional RTSS sync for exclusive fullscreen)
- Non-invasive screen filters (Magnification / Gamma LUT + NVAPI Digital Vibrance), DDC/CI backlight control, and ICC profile support
```

### 2. Scoop 清单提交（`ScoopInstaller/Extras`）

已在仓库内生成标准清单文件 `packaging/scoop/frameforge.json`（含 `v1.6.5` 精确 SHA-256 及 `checkver`/`autoupdate` 自动更新配置）。
将该文件复制到 `ScoopInstaller/Extras` 仓库的 `bucket/frameforge.json` 提交 PR；合入后全球用户可通过以下命令直接安装：

```powershell
scoop bucket add extras
scoop install frameforge
```
