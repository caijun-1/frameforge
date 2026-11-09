using System;
using System.Collections.Generic;
using System.Globalization;
using Microsoft.Win32;

namespace FrameForge.Sys
{
    public enum HealthLevel
    {
        Ok,
        Warn,
        Bad,
        Info
    }

    /// <summary>体检里的一行。</summary>
    public class HealthItem
    {
        public string Key = "";      // 标题 i18n 键
        public string Id = "";       // 可调节项的标识,交给 Tune 执行
        public HealthLevel Level = HealthLevel.Info;
        public string Value = "";    // 已经本地化的值
        public string Note = "";     // 已经本地化的补充说明
        public string Rec = "";      // 已经本地化的游戏推荐,如「游戏时推荐关闭」
        public string Action = "";   // "" / "admin" / "power" / "url:<协议地址>"
        public string ActKey = "";   // 按钮文字 i18n 键
        public string Path = "";     // 按游戏调节时目标 exe 的完整路径

        public bool CanToggle;       // 右侧给开关
        public bool On;              // 开关当前状态
        public bool CanChoose;       // 右侧给分段选择
        public string[] Choices = new string[0];
        public int Selected;
        public bool NeedAdmin;       // 改这项需要管理员
        public bool NeedReboot;      // 改完要重启
        public bool Confirm;         // 关闭前需要二次确认
    }

    /// <summary>
    /// 系统检查:逐项读出现状并说明影响;能调节的项由界面给开关,写入交给 Tune。
    /// 这里只探测,不改任何东西。
    /// </summary>
    public static class Health
    {
        private const string HvciKey = @"SYSTEM\CurrentControlSet\Control\DeviceGuard\Scenarios\HypervisorEnforcedCodeIntegrity";
        private const string VbsKey = @"SYSTEM\CurrentControlSet\Control\DeviceGuard";
        private const string GfxKey = @"SYSTEM\CurrentControlSet\Control\GraphicsDrivers";
        private const string DvrStore = @"System\GameConfigStore";
        private const string DvrKey = @"Software\Microsoft\Windows\CurrentVersion\GameDVR";

        public static List<HealthItem> Check(SysSample s)
        {
            var list = new List<HealthItem>();

            list.Add(Admin());
            list.Add(MemoryIntegrity());
            list.Add(Hags());
            list.Add(Mpo());
            list.Add(Dvr());
            list.Add(FullscreenOpt(s));
            list.Add(WindowedOpt());
            list.Add(Power());
            list.Add(Msi());
            list.Add(Gpu(s));

            return list;
        }

        private static HealthItem Admin()
        {
            var it = new HealthItem();
            it.Key = "health.admin";
            it.Id = "admin";
            it.Level = MemEngine.IsAdmin ? HealthLevel.Ok : HealthLevel.Warn;
            it.Value = MemEngine.IsAdmin ? I18n.T("health.val.on") : I18n.T("health.val.off");
            it.Note = I18n.T(MemEngine.IsAdmin ? "health.admin.on" : "health.admin.off");
            it.Rec = I18n.T("health.rec.admin");
            if (!MemEngine.IsAdmin)
            {
                it.Action = "admin";
                it.ActKey = "health.act.admin";
            }
            return it;
        }

        private static HealthItem MemoryIntegrity()
        {
            object hvci = Read(HvciKey, "Enabled");
            object vbs = Read(VbsKey, "EnableVirtualizationBasedSecurity");
            bool hvciOn = ToInt(hvci, 0) == 1;
            bool vbsOn = ToInt(vbs, 0) == 1;

            var it = new HealthItem();
            it.Key = "health.vbs";
            it.Id = "vbs";
            it.CanToggle = true;
            it.On = hvciOn || vbsOn;
            it.NeedAdmin = true;
            it.NeedReboot = true;
            it.Confirm = true;
            it.Rec = I18n.T("health.rec.off");
            if (hvciOn || vbsOn)
            {
                // 开着更安全,但虚拟化层会吃掉一点游戏性能,所以标成「注意」而不是「好」。
                it.Level = HealthLevel.Warn;
                it.Value = I18n.T("health.val.on");
                it.Note = I18n.T("health.vbs.on.tip");
            }
            else
            {
                it.Level = HealthLevel.Ok;
                it.Value = I18n.T("health.val.off");
                it.Note = I18n.T("health.vbs.off");
            }
            return it;
        }

        private static HealthItem Hags()
        {
            object mode = Read(GfxKey, "HwSchMode");
            int v = ToInt(mode, 0);

            var it = new HealthItem();
            it.Key = "health.hags";
            it.Id = "hags";
            it.CanToggle = true;
            it.On = v == 2;
            it.NeedAdmin = true;
            it.NeedReboot = true;
            it.Rec = I18n.T("health.rec.on");
            if (v == 2)
            {
                it.Level = HealthLevel.Ok;
                it.Value = I18n.T("health.val.on");
                it.Note = I18n.T("health.hags.on");
            }
            else if (v == 1)
            {
                it.Level = HealthLevel.Info;
                it.Value = I18n.T("health.val.off");
                it.Note = I18n.T("health.hags.off");
            }
            else
            {
                it.Level = HealthLevel.Info;
                it.Value = I18n.T("health.val.unknown");
                it.Note = I18n.T("health.hags.unknown");
            }
            return it;
        }

        /// <summary>Game DVR 与全屏优化:不需要管理员,改了立刻生效。</summary>
        private static HealthItem Dvr()
        {
            object enabled = ReadUser(DvrStore, "GameDVR_Enabled");
            object capture = ReadUser(DvrKey, "AppCaptureEnabled");
            bool on = ToInt(enabled, 1) != 0 || ToInt(capture, 1) != 0;

            var it = new HealthItem();
            it.Key = "health.dvr";
            it.Id = "dvr";
            it.CanToggle = true;
            it.On = on;
            it.Rec = I18n.T("health.rec.off");
            it.Level = on ? HealthLevel.Info : HealthLevel.Ok;
            it.Value = I18n.T(on ? "health.val.on" : "health.val.off");
            it.Note = I18n.T(on ? "health.dvr.on" : "health.dvr.off");
            return it;
        }

        /// <summary>MPO 多平面叠加:少数显卡上会导致窗口化卡顿,禁用需重启;只作为实验项。</summary>
        private static HealthItem Mpo()
        {
            bool on = Compat.MpoEnabled();

            var it = new HealthItem();
            it.Key = "health.mpo";
            it.Id = "mpo";
            it.CanToggle = true;
            it.On = on;
            it.NeedAdmin = true;
            it.NeedReboot = true;
            it.Rec = I18n.T("health.rec.try");
            it.Level = HealthLevel.Info;
            it.Value = I18n.T(on ? "health.val.on" : "health.val.off");
            it.Note = I18n.T(on ? "health.mpo.on" : "health.mpo.off");
            return it;
        }

        /// <summary>按当前游戏关闭全屏优化:写的是该 exe 的兼容性标记,不影响其他程序。</summary>
        private static HealthItem FullscreenOpt(SysSample s)
        {
            var it = new HealthItem();
            it.Key = "health.fse";
            it.Id = "fse";
            // 关掉全屏优化会把游戏的全屏变成真独占全屏,叠加层就再也盖不上去,
            // 所以这里不能再标成「推荐关闭」,要把代价写在推荐位之前。
            it.Rec = I18n.T("health.rec.fse");

            string exe = "";
            if (s != null && s.GamePid > 0) exe = Compat.GameExePath(s.GamePid);
            if (exe.Length == 0)
            {
                it.Level = HealthLevel.Info;
                it.Value = I18n.T("health.val.none");
                it.Note = I18n.T("health.fse.nogame");
                return it;
            }

            bool off = Compat.FullscreenOptOff(exe);
            it.Path = exe;
            it.CanToggle = true;
            it.On = !off;   // 开关表示「全屏优化」本身是否开启
            it.Level = HealthLevel.Info;
            it.Value = I18n.T(off ? "health.val.off" : "health.val.on");
            it.Note = I18n.T(off ? "health.fse.off" : "health.fse.on");
            return it;
        }

        /// <summary>Windows 11 的窗口化游戏优化:关掉后窗口化游戏会退回桌面合成器。</summary>
        private static HealthItem WindowedOpt()
        {
            bool on = Compat.WindowedOptOn();

            var it = new HealthItem();
            it.Key = "health.windowed";
            it.Id = "windowed";
            it.CanToggle = true;
            it.On = on;
            it.Rec = I18n.T("health.rec.on");
            it.Level = on ? HealthLevel.Ok : HealthLevel.Warn;
            it.Value = I18n.T(on ? "health.val.on" : "health.val.off");
            it.Note = I18n.T(on ? "health.windowed.on" : "health.windowed.off");
            return it;
        }

        private static HealthItem Power()
        {
            string guid = BoostEngine.ActiveSchemeGuid();
            bool high = string.Equals(guid, BoostEngine.SchemeHigh, StringComparison.OrdinalIgnoreCase);

            var it = new HealthItem();
            it.Key = "health.power";
            it.Id = "power";
            it.CanChoose = true;
            it.Choices = new string[] { I18n.T("health.power.high"), I18n.T("health.power.balanced") };
            it.Selected = high ? 0 : 1;
            it.Rec = I18n.T("health.rec.power");
            it.Level = high ? HealthLevel.Ok : HealthLevel.Warn;
            it.Value = high ? I18n.T("health.power.high") : I18n.T("health.power.other");
            it.Note = I18n.T(high ? "health.power.high.tip" : "health.power.other.tip");
            return it;
        }

        private static HealthItem Msi()
        {
            var it = new HealthItem();
            it.Key = "health.msi";
            it.Id = "msi";
            it.Rec = I18n.T("health.rec.on");
            int n = 0;
            try { n = InterruptEngine.Enumerate().Length; }
            catch { n = 0; }

            if (n > 0)
            {
                it.Level = HealthLevel.Ok;
                it.Value = string.Format(CultureInfo.InvariantCulture, I18n.T("health.msi.count"), n);
                it.Note = I18n.T("health.msi.on");
            }
            else
            {
                it.Level = HealthLevel.Info;
                it.Value = I18n.T("health.val.none");
                it.Note = I18n.T("health.msi.off");
            }
            return it;
        }

        private static HealthItem Gpu(SysSample s)
        {
            var it = new HealthItem();
            it.Key = "health.gpu";
            it.Id = "gpu";
            it.Rec = I18n.T("health.rec.gpu");
            if (s == null || s.GpuName.Length == 0)
            {
                it.Level = HealthLevel.Info;
                it.Value = I18n.T("health.val.unknown");
                it.Note = I18n.T("health.gpu.none");
                return it;
            }

            it.Level = s.GpuFromNvml ? HealthLevel.Ok : HealthLevel.Info;
            // NVML 的名字去掉了厂商前缀,这里补回来,显示完整的显卡型号。
            it.Value = s.GpuFromNvml ? "NVIDIA " + s.GpuName : s.GpuName;
            it.Note = I18n.T(s.GpuFromNvml ? "health.gpu.ready" : "health.gpu.pdh");
            return it;
        }

        private static object Read(string path, string name)
        {
            try
            {
                using (RegistryKey baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
                {
                    using (RegistryKey k = baseKey.OpenSubKey(path))
                    {
                        if (k == null) return null;
                        return k.GetValue(name, null);
                    }
                }
            }
            catch { return null; }
        }

        private static int ToInt(object v, int fallback)
        {
            if (v == null) return fallback;
            try { return Convert.ToInt32(v, CultureInfo.InvariantCulture); }
            catch { return fallback; }
        }

        private static object ReadUser(string path, string name)
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(path))
                {
                    if (k == null) return null;
                    return k.GetValue(name, null);
                }
            }
            catch { return null; }
        }
    }
}