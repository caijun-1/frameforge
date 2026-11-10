using System;
using System.Collections.Generic;
using System.Drawing;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;
using FrameForge.Sys;

namespace FrameForge.UI
{
    /// <summary>主窗口的业务动作:加速、叠加层、内存整理、下载、语言/主题、热键与托盘。</summary>
    public sealed partial class MainForm
    {
        private const int HotkeyBoost = 1;
        private const int HotkeyOverlay = 2;
        private const int HotkeyFilter = 3;
        private const uint MOD_ALT = 0x0001;
        private const uint MOD_CONTROL = 0x0002;
        private const uint MOD_NOREPEAT = 0x4000;

        private bool boosting;
        private bool rebuildQueued;
        private int lastAutoPid = -1;
        private int suppressAutoPid = -1;
        private int lastFilterMode = 2;
        private DateTime lastAutoTry = DateTime.MinValue;
        // 进程失联宽限:反作弊/权限导致的瞬时读取失败不能立刻还原加速。
        private int lostPid = -1;
        private DateTime lostAt = DateTime.MinValue;

        // ---------------- 页面可用 ----------------

        public void Toast(string text)
        {
            if (toast != null) toast.Show(text);
        }

        /// <summary>检查更新。manual=true 表示用户主动点的,没更新时也给个提示。</summary>
        public void CheckUpdates(bool manual)
        {
            if (!manual && !St.CheckUpdates) return;
            UpdateCheck.Start(delegate(bool newer)
            {
                if (IsDisposed || !IsHandleCreated) return;
                try
                {
                    BeginInvoke((MethodInvoker)delegate
                    {
                        if (IsDisposed) return;
                        if (newer) ShowUpdateDialog();
                        else if (manual) Toast(I18n.T("upd.latest"));
                    });
                }
                catch (Exception ex)
                {
                    Log.Write("update invoke failed: " + ex.Message);
                }
            });
        }

        private void ShowUpdateDialog()
        {
            using (UpdateDialog d = new UpdateDialog(UpdateCheck.CurrentVersion, "v" + UpdateCheck.LatestTag))
            {
                if (d.ShowDialog(this) != DialogResult.OK) return;
                if (d.InstallRequested) Close();
                else UpdateCheck.OpenDownloadPage();
            }
        }

        /// <summary>编辑「暂停后台进程」的保护名单。</summary>
        public void EditWhitelist()
        {
            using (var d = new WhitelistDialog(St.WhitelistNames(), St.ExcludeNames()))
            {
                if (d.ShowDialog(this) != DialogResult.OK) return;
                St.BoostWhitelist = d.Result;
                St.BoostExclude = d.ExcludeResult;
                St.Save();
                BoostEngine.SetWhitelist(St.WhitelistNames());
                BoostEngine.SetExclude(St.ExcludeNames());
                Toast(I18n.T("toast.whitelist"));
            }
        }

        /// <summary>还原全部改动,包括上次异常退出留下的日志。</summary>
        public void RestoreAll()
        {
            if (!DialogBox.Ask(this, I18n.T("set.restoreall"), I18n.T("set.restoreall.body"),
                    I18n.T("set.restoreall.ok"), I18n.T("dialog.cancel"))) return;

            // 先按日志还原:日志里记的是原始值,而且会顺带清掉日志;
            // 再走一次内存里的还原,把还挂起着的后台进程恢复回来。
            int n = 0;
            try { n = BoostJournal.RestoreAll(); }
            catch (Exception ex) { Log.Write("restore all: " + ex.Message); }
            RestoreBoost(false);

            if (setPage != null) setPage.SyncState();
            Toast(n > 0 ? I18n.T("toast.restoreall", n) : I18n.T("toast.restored"));
        }

        public void SaveSettingsSoon()
        {
            if (saveTimer == null) return;
            saveTimer.Stop();
            saveTimer.Start();
        }

        public void ApplyTheme()
        {
            Theme.Set(St.Dark);
            RequestRebuild();
        }

        public void RebuildUi()
        {
            RequestRebuild();
        }

        /// <summary>
        /// 重建请求合并:连点语言/主题按钮时只排一次重建。
        /// 以前每点一次就排一个 BeginInvoke,连点会连续重建,窗口有半帧停在"旧控件已销毁"
        /// 的状态,标题栏上就会露出一条没重绘的底色和半截图标。
        /// </summary>
        private void RequestRebuild()
        {
            if (rebuildQueued) return;
            rebuildQueued = true;
            Defer(delegate
            {
                rebuildQueued = false;
                RebuildUiCore();
            });
        }

        private void Defer(MethodInvoker action)
        {
            if (IsHandleCreated && !IsDisposed) BeginInvoke(action);
            else action();
        }

        /// <summary>
        /// 语言或主题变化后刷新界面:页面缓存了本地化文案与配色,
        /// 所以只重建当前可见页,其余页标记为待建,切过去时再建。
        /// </summary>
        private void RebuildUiCore()
        {
            if (IsDisposed) return;
            I18n.Lang = St.Language == "en" ? "en" : "zh";
            Theme.Set(St.Dark);
            Theme.ResetFonts();
            BackColor = Theme.Bg;
            ApplyWindowFrame();

            int idx = activePage;
            // 只重建当前看得见的那一页:另外 5 页没人看,等真正切过去再建。
            // 切语言/主题时因此不会整窗空白闪一下,也省掉一次 6 页全建的开销。
            if (pages == null)
            {
                BuildPages();
                if (idx != 0) ShowPage(idx);
            }
            else
            {
                MarkPagesDirtyExcept(idx);
                RebuildPage(idx);
                ShowPage(idx);
            }
            LayoutAll();

            nav.Items = NavItems();
            nav.SetSilent(idx);
            btnLang.LangText = I18n.IsEn ? "中" : "EN";
            tips.SetToolTip(btnLang, I18n.T("title.lang"));
            tips.SetToolTip(btnTheme, I18n.T("title.theme"));
            tips.SetToolTip(btnMin, I18n.T("title.min"));
            tips.SetToolTip(btnClose, I18n.T("title.close"));
            toast.BackColor = Theme.Bg;

            // 页面销毁重建时,被盖住的区域不会自己消失:先把旧页面藏起来再重绘,
            // 然后整窗(含所有子控件)立即刷新。这里只重绘客户区——
            // 带上 RDW_FRAME 会让系统去画非客户区(标题栏/边框),正是那条蓝灰条的来源。
            if (IsHandleCreated)
                Native.RedrawWindow(Handle, IntPtr.Zero, IntPtr.Zero,
                    Native.RDW_INVALIDATE | Native.RDW_ERASE |
                    Native.RDW_ALLCHILDREN | Native.RDW_UPDATENOW);
            else
                Invalidate(true);
        }

        // ---------------- 加速 ----------------

        /// <summary>一键加速:未加速时应用,已加速时还原。</summary>
        public void ApplyBoost()
        {
            if (boosting) return;
            if (Boost != null && Boost.Active) { RestoreBoost(true); return; }

            string name = "";
            int pid = -1;
            // 锁定游戏优先:即使当前前台是别的程序,也加速用户指定的那个游戏。
            if (St.BoostPin.Length > 0)
            {
                pid = FrameForge.Sys.Monitor.FindProcessByName(St.BoostPin);
                if (pid > 0) name = St.BoostPin;
            }
            if (pid <= 0) pid = BoostEngine.DetectGamePid(out name);
            SysSample s = Mon.Current;
            if (pid <= 0 && s != null && s.GameRecent && s.GamePid > 0)
            {
                pid = s.GamePid;
                name = s.GameName.Length > 0 ? s.GameName : I18n.T("mon.game.generic");
            }
            if (pid <= 0)
            {
                Toast(I18n.T("toast.nogame"));
                return;
            }
            // 排除名单里的程序不加速:自动和手动一视同仁,避免把壁纸/直播工具当游戏。
            if (BoostEngine.IsExcluded(name))
            {
                Toast(I18n.T("toast.excluded", name));
                return;
            }

            BoostPlan plan = St.Plan();
            if (plan.Background)
            {
                BackgroundProc[] cands = BoostEngine.Candidates(pid, 6);
                if (cands.Length > 0)
                {
                    var sb = new StringBuilder();
                    for (int i = 0; i < cands.Length; i++)
                        sb.AppendLine("· " + cands[i].Name + "  (" + Fmt.Bytes(cands[i].WorkingSet) + ")");
                    bool go = DialogBox.Ask(this, I18n.T("confirm.title"),
                        I18n.T("confirm.body", sb.ToString().TrimEnd()),
                        I18n.T("confirm.ok"), I18n.T("dialog.cancel"));
                    if (!go) return;
                }
            }

            boosting = true;
            if (boostPage != null) boostPage.SetBusy(true);

            BoostResult[] box = new BoostResult[1];
            string gameName = name;
            int gamePid = pid;
            ThreadPool.QueueUserWorkItem(delegate
            {
                try { box[0] = Boost.Apply(plan, gamePid, gameName); }
                catch (Exception ex) { Log.Write("boost: " + ex.Message); }

                Post(delegate
                {
                    boosting = false;
                    BoostResult r = box[0];
                    if (boostPage != null)
                    {
                        boostPage.SetBusy(false);
                        boostPage.SyncState();
                    }
                    if (r == null || !r.Ok)
                    {
                        Toast(I18n.T("toast.nogame"));
                        return;
                    }
                    if (boostPage != null) boostPage.SetResult(ComposeResult(r));
                    if (St.BoostProfile) SaveProfile(gameName, plan);
                    lastAutoPid = gamePid;
                    Toast(I18n.T("toast.boosted"));
                });
            });
        }

        /// <summary>
        /// 锁定 / 解除锁定当前游戏。锁定后自动加速和手动加速都只认这个进程,
        /// 切屏到桌面或其他程序时不会再跟着前台窗口换目标。
        /// </summary>
        public void ToggleBoostPin()
        {
            string current = St.BoostPin;
            string target = "";
            if (Boost != null && Boost.Active && Boost.GameName.Length > 0) target = Boost.GameName;
            else if (Mon.Current != null && Mon.Current.GameName.Length > 0) target = Mon.Current.GameName;

            if (target.Length == 0)
            {
                if (current.Length == 0)
                {
                    Toast(I18n.T("boost.pin.none"));
                    return;
                }
                target = current;   // 游戏没在跑时,再点一次解除
            }

            if (current.Length > 0 && string.Equals(target, current, StringComparison.OrdinalIgnoreCase))
            {
                St.BoostPin = "";
                SaveSettingsSoon();
                Toast(I18n.T("toast.pin.off"));
            }
            else
            {
                St.BoostPin = target.ToLowerInvariant();
                SaveSettingsSoon();
                int pid = FrameForge.Sys.Monitor.FindProcessByName(St.BoostPin);
                string title = pid > 0 ? GameDb.Title(pid, St.BoostPin) : St.BoostPin;
                if (title.Length == 0) title = St.BoostPin;
                Toast(I18n.T("toast.pin.on", title));
            }
            if (boostPage != null) boostPage.SyncPin();
        }

        public void RestoreBoost(bool notify)
        {
            // 手动还原后,同一个游戏不再自动套用,否则下一次采样又会把它加速回去。
            suppressAutoPid = Boost != null ? Boost.GamePid : -1;
            try { if (Boost != null) Boost.Restore(); }
            catch (Exception ex) { Log.Write("restore: " + ex.Message); }
            Perf.Reset();

            if (boostPage != null)
            {
                boostPage.SetBusy(false);
                boostPage.SyncState();
                boostPage.SetResult(I18n.T("boost.result.empty"));
            }
            if (notify) Toast(I18n.T("toast.restored"));
        }

        /// <summary>游戏退出后自动还原,避免高性能电源计划一直挂着耗电。</summary>
        private void CheckBoostAlive()
        {
            if (Boost == null || !Boost.Active) { lostPid = -1; return; }
            if (Boost.GameAlive()) { lostPid = -1; return; }

            // 连续 5 秒都读不到才认定退出:切换全屏、游戏切后台、反作弊扫描的
            // 瞬间都可能让进程查询失败,以前每次失败都会立刻还原并弹窗。
            int pid = Boost.GamePid;
            if (lostPid != pid)
            {
                lostPid = pid;
                lostAt = DateTime.UtcNow;
                return;
            }
            if ((DateTime.UtcNow - lostAt).TotalSeconds < 5) return;

            lostPid = -1;
            RestoreBoost(false);
            Toast(I18n.T("toast.gameexit"));
        }

        /// <summary>
        /// 检测到游戏后自动套用加速。有该游戏的策略就用策略,没有就用当前加速项。
        /// 只做安全项:暂停后台进程需要用户确认,中断避让要重启才生效,两项都不在自动范围内。
        /// </summary>
        private void MaybeAutoBoost(SysSample s)
        {
            if (!St.AutoBoost || s == null) return;
            if (Boost == null || Boost.Active || boosting) return;
            if ((DateTime.UtcNow - lastAutoTry).TotalSeconds < 3) return;
            lastAutoTry = DateTime.UtcNow;

            int gamePid;
            string gameName;
            if (St.BoostPin.Length > 0)
            {
                // 锁定后只加速指定的游戏:它在后台、窗口不可见也照样认,
                // 桌面上的触摸键盘/其他全屏程序不会被顺手加速。
                gamePid = FrameForge.Sys.Monitor.FindProcessByName(St.BoostPin);
                if (gamePid <= 0) return;
                gameName = St.BoostPin;
            }
            else
            {
                if (s.GamePid <= 0 || s.GameName.Length == 0 || !s.GameRecent) return;
                gamePid = s.GamePid;
                gameName = s.GameName;
            }
            if (gamePid == lastAutoPid || gamePid == suppressAutoPid) return;
            if (BoostEngine.IsExcluded(gameName)) return;

            GameProfile prof = Profiles != null ? Profiles.Find(gameName) : null;
            BoostPlan plan = prof != null ? prof.Plan() : St.Plan();
            // 有游戏策略时也沿用全局的优先级档位;理想处理器是实验项,不进自动加速。
            if (prof != null) plan.PriorityLevel = St.BoostPriorityLevel;
            plan.Background = false;
            plan.Irq = false;
            plan.IdealCpu = false;
            if (!plan.Any) return;

            lastAutoPid = gamePid;
            boosting = true;
            if (boostPage != null) boostPage.SetBusy(true);

            BoostResult[] box = new BoostResult[1];
            ThreadPool.QueueUserWorkItem(delegate
            {
                try { box[0] = Boost.Apply(plan, gamePid, gameName); }
                catch (Exception ex) { Log.Write("auto boost: " + ex.Message); }

                Post(delegate
                {
                    boosting = false;
                    BoostResult r = box[0];
                    if (boostPage != null)
                    {
                        boostPage.SetBusy(false);
                        boostPage.SyncState();
                        if (r != null) boostPage.SetResult(ComposeResult(r));
                    }
                    if (r != null && r.Ok)
                    {
                        Toast(I18n.T("toast.auto", gameName));
                    }
                });
            });
        }

        /// <summary>把本次加速项记到该游戏名下,下次启动自动套用。</summary>
        private void SaveProfile(string game, BoostPlan plan)
        {
            if (Profiles == null || plan == null || game.Length == 0) return;
            GameProfile p = Profiles.Find(game);
            if (p == null) p = new GameProfile { Name = game };
            p.Priority = plan.Priority;
            p.PowerPlan = plan.PowerPlan;
            p.Timer = plan.Timer;
            p.Dvr = plan.Dvr;
            p.Memory = plan.Memory;
            p.Background = plan.Background;
            p.Affinity = plan.Affinity;
            p.Irq = plan.Irq;
            p.Cores = plan.Cores;
            Profiles.Save(p);
        }

        private string ComposeResult(BoostResult r)
        {
            // 只报确认生效的数量,跳过项用原因短语挂在后面,不列一堆看起来已经成功的条目。
            int applied = 0;
            if (r.PriorityOk > 0) applied++;
            if (r.AffinityOk > 0) applied++;
            if (r.IrqOk > 0) applied++;
            if (r.PowerOk > 0) applied++;
            if (r.TimerOk > 0) applied++;
            if (r.DvrOk > 0) applied++;
            if (r.MemoryOk > 0) applied++;
            if (r.IdealOk > 0) applied++;
            if (r.Suspended > 0) applied++;

            var reasons = new List<string>();
            for (int i = 0; i < r.Skipped.Count; i++)
            {
                string t = I18n.T(r.Skipped[i]);
                if (t.Length == 0 || t == r.Skipped[i] || reasons.Contains(t)) continue;
                reasons.Add(t);
            }

            if (applied == 0 && reasons.Count == 0) return I18n.T("boost.item.failed");

            string text = I18n.T("boost.result.count", applied, r.Skipped.Count);
            if (reasons.Count > 0)
                text += I18n.T("boost.result.reasons",
                    string.Join(I18n.IsEn ? ", " : "、", reasons.ToArray()));
            return text;
        }

        // ---------------- 战绩 ----------------

        /// <summary>一局游戏结束:刷新战绩页,并按设置在游戏加加式弹窗里汇报本局表现。</summary>
        private void OnSessionDone(SessionRecord r)
        {
            if (r == null) return;
            Toast(I18n.T("toast.report"));
            if (reportPage != null) reportPage.Reload();
            if (!St.ReportAuto) return;
            try
            {
                var f = new ReportForm(r, delegate { RestoreFromTray(); ShowPage(3); });
                f.Show();
            }
            catch (Exception ex) { Log.Write("report form: " + ex.Message); }
        }

        // ---------------- 叠加层 ----------------

        public void ToggleOverlay()
        {
            St.OverlayOn = !St.OverlayOn;
            St.Save();
            SyncOverlay();
            Toast(St.OverlayOn ? I18n.T("toast.overlay.on") : I18n.T("toast.overlay.off"));
        }

        public void SyncOverlay()
        {
            if (Ov == null) return;
            // 截图进行时先收起来:置顶窗口每 200ms 重画一次,既会拖慢截图的取色层,
            // 也会把帧率文字一起截进图里。
            bool show = !captureGuard && St.OverlayOn && (lastFullscreen || !St.OverlayLocked);
            if (show)
            {
                Ov.ApplySettings();
                if (!ovShown) { ovShown = true; Ov.Start(); }
            }
            else if (ovShown)
            {
                ovShown = false;
                Ov.Stop();
            }
        }

        // ---------------- 滤镜 ----------------

        /// <summary>把当前滤镜设置推给系统;「仅游戏」模式下切出游戏会自动还原。</summary>
        public void ApplyFilter()
        {
            bool isGame = Mon != null && Mon.Current != null && Mon.Current.Fullscreen;
            try
            {
                // 截图工具在抓屏:只推恒等矩阵不够,放大镜的整条效果链还挂在 DWM 上,
                // Win+Shift+S 的取色层会和它抢同一帧,整屏就卡住。这里直接把放大镜卸掉,
                // 工具栏收起后由守卫重新装回来。
                if (captureGuard) Sys.Filter.Suspend();
                else Sys.Filter.ApplySettings(St, isGame);
            }
            catch (Exception ex) { Log.Write("filter: " + ex.Message); }
        }

        /// <summary>Ctrl+Alt+F:在游戏里直接开关画面滤镜,不用切回主界面。</summary>
        public void ToggleFilter()
        {
            if (!Sys.Filter.Supported)
            {
                Toast(I18n.T("flt.unsupported"));
                return;
            }
            if (St.FilterMode == 0)
            {
                St.FilterMode = lastFilterMode > 0 ? lastFilterMode : 2;
            }
            else
            {
                lastFilterMode = St.FilterMode;
                St.FilterMode = 0;
            }
            St.Save();
            ApplyFilter();
            Toast(St.FilterMode != 0 ? I18n.T("toast.filter.on") : I18n.T("toast.filter.off"));
        }

        // ---------------- 内存 ----------------

        public void RunTrim()
        {
            if (trimming) return;
            trimming = true;
            if (boostPage != null) boostPage.SetTrimBusy(true);

            int gamePid = -1;
            SysSample s = Mon.Current;
            if (s != null && s.GameRecent) gamePid = s.GamePid;

            ThreadPool.QueueUserWorkItem(delegate
            {
                OptimizeResult r = null;
                try { r = MemEngine.Optimize(OptimizeLevel.Standard, gamePid, null, gamePid <= 0); }
                catch (Exception ex) { Log.Write("trim: " + ex.Message); }

                string text;
                if (r != null && r.Released > 0)
                    text = I18n.T("boost.item.mem", Fmt.Bytes((ulong)r.Released));
                else
                    text = I18n.T("boost.result.empty");

                Post(delegate
                {
                    trimming = false;
                    if (boostPage != null)
                    {
                        boostPage.SetTrimBusy(false);
                        if (r != null && r.Released > 0) boostPage.SetResult(text);
                    }
                    if (r != null && r.Released > 0) Toast(I18n.T("toast.mem", Fmt.Bytes((ulong)r.Released)));
                });
            });
        }

        private void AutoTrim(SysSample s)
        {
            if (!St.MemAuto || trimming || s.RamPct < 0) return;
            // 游戏在跑时不清缓存、不清待命列表:刚加载的资源被赶出去后要重新读盘,
            // 表现就是每隔两分钟卡一下。等退出游戏后再维护。
            if (s.GameRecent || (Boost != null && Boost.Active)) return;

            DateTime now = DateTime.UtcNow;
            if (St.MemAutoMode == "interval")
            {
                // 按时间:不看占用,到点就整理。启动 / 刚开启时先起计时,
                // 等满一个间隔再动手,免得刚开软件就清一次。
                int minutes = St.MemIntervalMin;
                if (minutes < 5) minutes = 5;
                if (lastAutoTrim == DateTime.MinValue) { lastAutoTrim = now; return; }
                if ((now - lastAutoTrim).TotalMinutes < minutes) return;
            }
            else
            {
                // 按阈值:占用达到设定值才整理;两分钟只是防抖,避免连续采样反复清。
                if (s.RamPct < St.MemThreshold) return;
                if ((now - lastAutoTrim).TotalSeconds < 120) return;
            }
            lastAutoTrim = now;
            ThreadPool.QueueUserWorkItem(delegate
            {
                try { MemEngine.SmartClean(null); }
                catch (Exception ex) { Log.Write("autotrim: " + ex.Message); }
            });
        }

        // ---------------- 帧率组件 ----------------

        public void DownloadPresentMon()
        {
            if (downloading) return;
            downloading = true;
            if (setPage != null) setPage.SetDownloadState(true, 0);

            ThreadPool.QueueUserWorkItem(delegate
            {
                int lastPct = -1;
                string err = FpsEngine.Download(delegate(int pct)
                {
                    if (pct == lastPct) return;
                    lastPct = pct;
                    Post(delegate { if (setPage != null) setPage.SetDownloadState(true, pct); });
                });

                Post(delegate
                {
                    downloading = false;
                    if (setPage != null)
                    {
                        setPage.SetDownloadState(false, 0);
                        setPage.UpdateStatus();
                    }
                    Toast(err.Length == 0 ? I18n.T("toast.fpsok") : I18n.T("err.download", err));
                });
            });
        }

        private void Post(MethodInvoker action)
        {
            try
            {
                if (IsHandleCreated && !IsDisposed) BeginInvoke(action);
            }
            catch { }
        }

        // ---------------- 常规 ----------------

        public void SetAutostart(bool on)
        {
            // 程序要求管理员权限,注册表 Run 项在登录时提不起权会被系统直接跳过,
            // 所以自启改用计划任务(登录触发 + 最高权限);建不起来再退回 Run 项。
            try
            {
                if (on)
                {
                    bool made = RunSchtasks("/Create /F /TN \"FrameForge\" /TR \"\\\""
                        + Application.ExecutablePath + "\\\" --minimized\" /SC ONLOGON /RL HIGHEST");
                    if (!made) WriteRunKey(true);
                    return;
                }
                RunSchtasks("/Delete /F /TN \"FrameForge\"");
                WriteRunKey(false);
                return;
            }
            catch (Exception ex) { Log.Write("autostart task: " + ex.Message); }
        }

        private static void WriteRunKey(bool on)
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(
                    @"Software\Microsoft\Windows\CurrentVersion\Run", true))
                {
                    if (k == null) return;
                    if (on) k.SetValue("FrameForge", "\"" + Application.ExecutablePath + "\" --minimized");
                    else k.DeleteValue("FrameForge", false);
                }
            }
            catch (Exception ex) { Log.Write("autostart: " + ex.Message); }
        }

        private static bool RunSchtasks(string args)
        {
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo("schtasks.exe", args);
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                using (System.Diagnostics.Process p = System.Diagnostics.Process.Start(psi))
                {
                    if (!p.WaitForExit(5000)) return false;
                    return p.ExitCode == 0;
                }
            }
            catch { return false; }
        }

        public void OpenLogFolder()
        {
            try
            {
                if (!System.IO.Directory.Exists(Settings.Dir)) System.IO.Directory.CreateDirectory(Settings.Dir);
                System.Diagnostics.Process.Start("explorer.exe", "\"" + Settings.Dir + "\"");
            }
            catch (Exception ex) { Log.Write("open log: " + ex.Message); }
        }

        private void ToggleLanguage()
        {
            St.Language = I18n.IsEn ? "zh" : "en";
            St.Save();
            RebuildUi();
        }

        private void ToggleTheme()
        {
            St.Dark = !St.Dark;
            St.Save();
            ApplyTheme();
        }

        private void AskElevationOnce()
        {
            St.AskedElevation = true;
            St.Save();
            bool yes = DialogBox.Ask(this, I18n.T("dialog.admin.title"), I18n.T("dialog.admin.body"),
                I18n.T("dialog.admin.ok"), I18n.T("dialog.admin.skip"));
            if (yes) MemEngine.RestartElevated(startMinimized ? "--minimized" : "");
        }

        // ---------------- 热键 ----------------

        private void RegisterHotkeys()
        {
            // Ctrl+Alt+B 一键加速/还原,Ctrl+Alt+O 切换叠加层,Ctrl+Alt+F 开关滤镜
            Native.RegisterHotKey(Handle, HotkeyBoost, MOD_CONTROL | MOD_ALT | MOD_NOREPEAT, 0x42);
            Native.RegisterHotKey(Handle, HotkeyOverlay, MOD_CONTROL | MOD_ALT | MOD_NOREPEAT, 0x4F);
            Native.RegisterHotKey(Handle, HotkeyFilter, MOD_CONTROL | MOD_ALT | MOD_NOREPEAT, 0x46);
        }

        private void UnregisterHotkeys()
        {
            if (!IsHandleCreated) return;
            try
            {
                Native.UnregisterHotKey(Handle, HotkeyBoost);
                Native.UnregisterHotKey(Handle, HotkeyOverlay);
                Native.UnregisterHotKey(Handle, HotkeyFilter);
            }
            catch { }
        }

        private void HandleHotkey(int id)
        {
            if (id == HotkeyBoost) ApplyBoost();
            else if (id == HotkeyOverlay) ToggleOverlay();
            else if (id == HotkeyFilter) ToggleFilter();
        }
    }
}