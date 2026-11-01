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
        private const uint MOD_ALT = 0x0001;
        private const uint MOD_CONTROL = 0x0002;
        private const uint MOD_NOREPEAT = 0x4000;

        private bool boosting;
        private bool rebuildQueued;

        // ---------------- 页面可用 ----------------

        public void Toast(string text)
        {
            if (toast != null) toast.Show(text);
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

        /// <summary>语言或主题变化后重建所有页面:页面缓存了本地化文案与配色,重建最省心。</summary>
        private void RebuildUiCore()
        {
            if (IsDisposed) return;
            I18n.Lang = St.Language == "en" ? "en" : "zh";
            Theme.Set(St.Dark);
            Theme.ResetFonts();
            BackColor = Theme.Bg;
            ApplyWindowFrame();

            int idx = activePage;
            if (pages != null)
            {
                for (int i = 0; i < pages.Length; i++)
                {
                    // 先隐藏再销毁:可见的子窗口被销毁时,系统会在它原来的位置留下一块
                    // 没重绘的洞,叠在标题栏上就是半截图标或一条旧底色。
                    pages[i].Visible = false;
                    Controls.Remove(pages[i]);
                    pages[i].Dispose();
                }
                pages = null;
            }
            boostPage = null;
            setPage = null;

            BuildPages();
            ShowPage(idx);
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

            string name;
            int pid = BoostEngine.DetectGamePid(out name);
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
                    Toast(I18n.T("toast.boosted"));
                });
            });
        }

        public void RestoreBoost(bool notify)
        {
            try { if (Boost != null) Boost.Restore(); }
            catch (Exception ex) { Log.Write("restore: " + ex.Message); }

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
            if (Boost == null || !Boost.Active) return;
            if (Boost.GameAlive()) return;
            RestoreBoost(false);
            Toast(I18n.T("toast.gameexit"));
        }

        private string ComposeResult(BoostResult r)
        {
            var parts = new List<string>();
            if (r.PriorityOk > 0) parts.Add(I18n.T("boost.item.priority"));
            if (r.PowerOk > 0) parts.Add(I18n.T("boost.item.power"));
            if (r.TimerOk > 0) parts.Add(I18n.T("boost.item.timer"));
            if (r.DvrOk > 0) parts.Add(I18n.T("boost.item.dvr"));
            if (r.MemReleased > 0) parts.Add(I18n.T("boost.item.mem", Fmt.Bytes((ulong)r.MemReleased)));
            if (r.Suspended > 0) parts.Add(I18n.T("boost.item.bg", r.Suspended));

            string text = parts.Count > 0
                ? string.Join("   ·   ", parts.ToArray())
                : I18n.T("boost.item.failed");

            string names = Boost.SuspendedNames();
            if (names.Length > 0) text += "\r\n" + I18n.T("boost.item.bg.names", names);
            return text;
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
            bool show = St.OverlayOn && (lastFullscreen || !St.OverlayLocked);
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
            try { Sys.Filter.ApplySettings(St, isGame); }
            catch (Exception ex) { Log.Write("filter: " + ex.Message); }
        }

        // ---------------- 内存 ----------------

        public void RunTrim()
        {
            if (trimming) return;
            trimming = true;
            if (boostPage != null) boostPage.SetTrimBusy(true);

            int gamePid = -1;
            SysSample s = Mon.Current;
            if (s != null && s.Fullscreen) gamePid = s.ForegroundPid;

            ThreadPool.QueueUserWorkItem(delegate
            {
                OptimizeResult r = null;
                try { r = MemEngine.Optimize(OptimizeLevel.Standard, gamePid, null); }
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
            if (s.RamPct < St.MemThreshold) return;
            if ((DateTime.UtcNow - lastAutoTrim).TotalSeconds < 120) return;
            lastAutoTrim = DateTime.UtcNow;
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
            // Ctrl+Alt+B 一键加速/还原,Ctrl+Alt+O 切换叠加层
            Native.RegisterHotKey(Handle, HotkeyBoost, MOD_CONTROL | MOD_ALT | MOD_NOREPEAT, 0x42);
            Native.RegisterHotKey(Handle, HotkeyOverlay, MOD_CONTROL | MOD_ALT | MOD_NOREPEAT, 0x4F);
        }

        private void UnregisterHotkeys()
        {
            if (!IsHandleCreated) return;
            try
            {
                Native.UnregisterHotKey(Handle, HotkeyBoost);
                Native.UnregisterHotKey(Handle, HotkeyOverlay);
            }
            catch { }
        }

        private void HandleHotkey(int id)
        {
            if (id == HotkeyBoost) ApplyBoost();
            else if (id == HotkeyOverlay) ToggleOverlay();
        }
    }
}