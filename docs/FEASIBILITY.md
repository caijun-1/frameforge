# 技术选型与可行性

这份文档说明 FrameForge 在每个关键节点上为什么这样做、放弃了哪些方案，以及由此带来的限制。

## 1. 体积：为什么是 .NET Framework 4.8 + 系统自带 csc

目标是最小体积的单文件 exe。可选方案对比：

| 方案 | 空壳体积 | 依赖 | 结论 |
| --- | --- | --- | --- |
| C++ / Win32 | ~30 KB | 需 MSVC 工具链 | 体积最优，但本机无 MSVC，且手写控件成本高 |
| .NET 8 自包含 | 60–70 MB | 无 | 与「体积极小」目标冲突 |
| .NET Framework 4.8 框架依赖 | **~145 KB** | Windows 10 1809+ 自带 | ✅ 采用 |
| Electron / WebView | 80 MB+ | 自带 Chromium | 直接排除 |

代价是只能用 C# 5：系统目录里的 `csc.exe`（`v4.0.30319`）不支持插值字符串、null 条件运算符、
表达式体成员等语法。项目里所有代码都按这条约束书写，`build.ps1` 也不接受 `/langversion` 参数。

界面不引入任何 UI 框架，全部用 `Control` 派生自绘（`src/UI/Controls.cs`）：圆角卡片、环形表、
开关、分段控件、滑杆、迷你折线图都是 `OnPaint` 手绘，既省体积也保证深浅主题一致。

## 2. 加速：只做可回滚的系统设置

不做 DLL 注入、不改游戏内存、不装驱动——这类做法会触发反作弊，也与「不修改游戏」的原则冲突。

实际采用的都是公开、可回滚的系统调整：

- 进程优先级 `SetPriorityClass(HIGH_PRIORITY_CLASS)`；
- 电源计划切换（`PowerSetActiveScheme`），记住原 GUID 以便还原；
- 计时器精度 `timeBeginPeriod(1)` / `timeEndPeriod(1)`；
- Game DVR 与全屏优化注册表项，写入前保存原值；
- 工作集整理 `EmptyWorkingSet` / `SetSystemFileCacheSize`；
- 进阶项：`NtSuspendProcess` 挂起后台无窗口进程，退出时恢复。

项目启动时把原状态序列化进配置，还原时逐项比对写回；进程异常退出时下一次启动会提示并清理残留。

## 3. 帧率：PresentMon 的 ETW 会话

自建 ETW 消费者需要解析 DXGI/D3D 呈现事件，工作量大且随驱动变化。Intel PresentMon 已经把这层
封装好并且是 MIT 协议，因此：

- 不在仓库中分发二进制，首次使用时按需下载到 `%LOCALAPPDATA%\FrameForge\`；
- 以子进程方式运行，读取它的 CSV 输出并计算平均 FPS 与 1% Low；
- 需要管理员权限（ETW 内核会话），未授权时页面明确提示而不是显示假数据。

无法读取时统一显示 `—`，宁可留空也不猜测。

## 4. 滤镜：Windows 放大镜颜色矩阵

注入式后处理（ReShade 一类）兼容性差且会被反作弊拦截。这里改用系统自带的全屏放大镜颜色效果
（`MagSetFullscreenColorEffect`）：

- 5×5 颜色矩阵，可组合亮度、对比度、饱和度、色温与灰度；
- 作用于整个桌面合成结果，不改游戏进程，随时可一键还原为恒等矩阵；
- 「仅游戏」模式下由前台全屏检测决定何时挂载。

限制：远程桌面会话不支持该 API；HDR 输出下矩阵作用在 SDR 通道，观感与 SDR 屏幕略有差异。

## 5. 叠加层：分层窗口而不是钩子

叠加层是一个 `WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW` 的置顶窗口：

- 不注入、不接管输入，游戏崩溃不会连带崩溃；
- 自带三种布局与缩放，位置可拖动并锁定；
- 独占全屏（Exclusive Fullscreen）下会被游戏抢走显示，这是无注入方案的固有代价，文档中已说明。

## 6. DPI 与布局

高分屏上常见的错位来自「文字按 DPI 放大、布局仍按 1× 计算」。处理方式：

- 窗口创建后立刻用 `GetDpiForWindow(Handle)` 取得真实 DPI（`Form.DeviceDpi` 在部分系统上恒为 96）；
- 所有布局数值统一经 `Ui.Px()` 换算成设备像素；
- `app.manifest` 声明 Per-Monitor V2 DPI 感知，避免系统二次缩放导致模糊。

## 7. 内存与线程

- 一个 1 秒定时器驱动所有采样与刷新，无独立采样线程；
- GPU 通过 NVML 的轻量查询按需读取，驱动不存在时一次性降级，不做重试；
- PresentMon 只在检测到全屏游戏时启动，离开全屏即结束子进程；
- 常驻工作集约 20 MB，空载 CPU 占用接近 0。

## 8. 自检

`FrameForge.exe --selftest` 不需要打开界面就能验证：传感器采样、显卡识别、游戏检测、
电源方案读写、后台进程候选、设置到加速计划的映射、PresentMon 安装状态、叠加层窗口初始化。
日志写入 `%TEMP%\frameforge_selftest.log`，便于在 CI 或用户机器上快速定位环境问题。