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
        public HealthLevel Level = HealthLevel.Info;
        public string Value = "";    // 已经本地化的值
        public string Note = "";     // 已经本地化的补充说明
        public string Action = "";   // "" / "admin" / "power" / "url:<协议地址>"
        public string ActKey = "";   // 按钮文字 i18n 键
    }

    /// <summary>
    /// 只读系统体检:检查会直接影响帧率、但用户通常不知道在哪里看的几项设置。
    /// 这里只探测,不改任何东西;需要动作的项由界面提供按钮。
    /// </summary>
    public static class Health
    {
        private const string HvciKey = @"SYSTEM\CurrentControlSet\Control\DeviceGuard\Scenarios\HypervisorEnforcedCodeIntegrity";
        private const string VbsKey = @"SYSTEM\CurrentControlSet\Control\DeviceGuard";
        private const string GfxKey = @"SYSTEM\CurrentControlSet\Control\GraphicsDrivers";

        public static List<HealthItem> Check(SysSample s)
        {
            var list = new List<HealthItem>();

            list.Add(Admin());
            list.Add(MemoryIntegrity());
            list.Add(Hags());
            list.Add(Power());
            list.Add(Msi());
            list.Add(Gpu(s));

            return list;
        }

        private static HealthItem Admin()
        {
            var it = new HealthItem();
            it.Key = "health.admin";
            it.Level = MemEngine.IsAdmin ? HealthLevel.Ok : HealthLevel.Warn;
            it.Value = MemEngine.IsAdmin ? I18n.T("health.val.on") : I18n.T("health.val.off");
            it.Note = I18n.T(MemEngine.IsAdmin ? "health.admin.on" : "health.admin.off");
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
            if (hvciOn || vbsOn)
            {
                // 开着更安全,但虚拟化层会吃掉一点游戏性能,所以标成「注意」而不是「好」。
                it.Level = HealthLevel.Warn;
                it.Value = I18n.T("health.val.on");
                it.Note = I18n.T("health.vbs.on.tip");
                it.Action = "url:windowsdefender://coreisolation";
                it.ActKey = "health.act.open";
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
                it.Action = "url:ms-settings:display-advancedgraphics";
                it.ActKey = "health.act.open";
            }
            else
            {
                it.Level = HealthLevel.Info;
                it.Value = I18n.T("health.val.unknown");
                it.Note = I18n.T("health.hags.unknown");
            }
            return it;
        }

        private static HealthItem Power()
        {
            string guid = BoostEngine.ActiveSchemeGuid();
            bool high = string.Equals(guid, BoostEngine.SchemeHigh, StringComparison.OrdinalIgnoreCase);

            var it = new HealthItem();
            it.Key = "health.power";
            it.Level = high ? HealthLevel.Ok : HealthLevel.Warn;
            it.Value = high ? I18n.T("health.power.high") : I18n.T("health.power.other");
            it.Note = I18n.T(high ? "health.power.high.tip" : "health.power.other.tip");
            if (!high)
            {
                it.Action = "power";
                it.ActKey = "health.act.power";
            }
            return it;
        }

        private static HealthItem Msi()
        {
            var it = new HealthItem();
            it.Key = "health.msi";
            int n = 0;
            try { n = InterruptEngine.Enumerate().Length; }
            catch { n = 0; }

            if (n > 0)
            {
                it.Level = HealthLevel.Ok;
                it.Value = n.ToString(CultureInfo.InvariantCulture);
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
            if (s == null || s.GpuName.Length == 0)
            {
                it.Level = HealthLevel.Info;
                it.Value = I18n.T("health.val.unknown");
                it.Note = I18n.T("health.gpu.none");
                return it;
            }

            it.Level = s.GpuFromNvml ? HealthLevel.Ok : HealthLevel.Info;
            it.Value = s.GpuName;
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
    }
}