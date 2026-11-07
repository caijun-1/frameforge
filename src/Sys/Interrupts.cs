using System;
using System.Collections.Generic;
using System.Globalization;
using Microsoft.Win32;

namespace FrameForge.Sys
{
    public class IrqDevice
    {
        public string Name = "";
        public string Key = "";       // Enum\PCI 下的相对路径
        public string ClassGuid = "";
    }

    public class IrqResult
    {
        public bool Ok;
        public bool NeedAdmin;
        public bool NeedReboot = true;
        public int Devices;
        public int Set;
        public string Error = "";
    }

    /// <summary>
    /// 设备中断避让:把网卡的 MSI 中断亲和性挪到游戏不用的核心上,
    /// 减少游戏核上的 DPC/ISR 抖动。写入的是设备的中断亲和策略,
    /// 由内核在下次启动时应用,所以必须重启才生效。
    /// 只处理已经启用 MSI 的网卡,且记录原值以便还原。
    /// 显卡不参与:MSI 亲和策略会把显卡中断钉死在少数核心上,
    /// 驱动侧没有为此做保证,是明确的掉驱动/设备挂起风险。
    /// </summary>
    public sealed class InterruptEngine
    {
        private const string EnumRoot = @"SYSTEM\CurrentControlSet\Enum\PCI";
        private const string NetClass = "4d36e972-e325-11ce-bfc1-08002be10318";
        private const int PolicySpecifiedProcessors = 4;

        private sealed class IrqSaved
        {
            public string Path = "";
            public bool CreatedKey;
            public bool HadPolicy;
            public object Policy;
            public bool HadMask;
            public object Mask;
        }

        private readonly List<IrqSaved> saved = new List<IrqSaved>();

        public static IrqDevice[] Enumerate()
        {
            var list = new List<IrqDevice>();
            try
            {
                using (RegistryKey root = Registry.LocalMachine.OpenSubKey(EnumRoot))
                {
                    if (root == null) return list.ToArray();
                    string[] hwIds = root.GetSubKeyNames();
                    for (int i = 0; i < hwIds.Length; i++)
                    {
                        using (RegistryKey hw = root.OpenSubKey(hwIds[i]))
                        {
                            if (hw == null) continue;
                            string[] insts = hw.GetSubKeyNames();
                            for (int k = 0; k < insts.Length; k++)
                            {
                                string rel = hwIds[i] + "\\" + insts[k];
                                using (RegistryKey dev = root.OpenSubKey(rel))
                                {
                                    if (dev == null) continue;
                                    string cls = Str(dev.GetValue("ClassGUID")).Trim().ToLowerInvariant();
                                    // 注册表里是带花括号的 {4d36e972-...},统一去掉再比。
                                    cls = cls.Trim('{', '}');
                                    if (cls != NetClass) continue;

                                    using (RegistryKey im = dev.OpenSubKey(@"Device Parameters\Interrupt Management"))
                                    {
                                        if (im == null) continue;
                                        if (!MsiEnabled(im)) continue;
                                        var d = new IrqDevice();
                                        d.Key = rel;
                                        d.ClassGuid = cls;
                                        d.Name = Friendly(Str(dev.GetValue("DeviceDesc")));
                                        if (d.Name.Length == 0) d.Name = rel;
                                        list.Add(d);
                                    }
                                }
                            }
                        }
                    }
                }
            }
            catch (Exception ex) { Log.Write("irq enum: " + ex.Message); }
            return list.ToArray();
        }

        private static bool MsiEnabled(RegistryKey im)
        {
            using (RegistryKey ms = im.OpenSubKey("MessageSignaledInterruptProperties"))
            {
                if (ms == null) return false;
                object v = ms.GetValue("MSISupported");
                if (v == null) return true;
                try { return Convert.ToInt32(v, CultureInfo.InvariantCulture) != 0; }
                catch { return false; }
            }
        }

        public IrqResult Apply(int coresWanted)
        {
            var r = new IrqResult();
            Restore();

            IrqDevice[] devs = Enumerate();
            r.Devices = devs.Length;
            if (devs.Length == 0) { r.Error = "nodevice"; return r; }

            int cores;
            long game = CpuTopology.GameMask(coresWanted, out cores);
            long avoid = CpuTopology.AllMask() & ~game;
            if (avoid == 0) { r.Error = "nomask"; return r; }

            if (!MemEngine.IsAdmin) { r.NeedAdmin = true; r.Error = "admin"; return r; }

            byte[] bytes = MaskBytes(avoid);
            for (int i = 0; i < devs.Length; i++)
            {
                try
                {
                    string apPath = EnumRoot + "\\" + devs[i].Key + @"\Device Parameters\Interrupt Management\Affinity Policy";
                    bool existed = true;
                    using (RegistryKey ap = Registry.LocalMachine.OpenSubKey(apPath, true))
                    {
                        if (ap == null) existed = false;
                    }
                    using (RegistryKey ap = Registry.LocalMachine.OpenSubKey(apPath, true)
                        ?? Registry.LocalMachine.CreateSubKey(apPath))
                    {
                        if (ap == null) continue;
                        object oldPolicy = ap.GetValue("DevicePolicy");
                        object oldMask = ap.GetValue("AssignmentSetOverride");
                        saved.Add(new IrqSaved
                        {
                            Path = apPath,
                            CreatedKey = !existed,
                            HadPolicy = oldPolicy != null,
                            Policy = oldPolicy,
                            HadMask = oldMask != null,
                            Mask = oldMask
                        });
                        ap.SetValue("DevicePolicy", PolicySpecifiedProcessors, RegistryValueKind.DWord);
                        ap.SetValue("AssignmentSetOverride", bytes, RegistryValueKind.Binary);
                        r.Set++;
                    }
                }
                catch (Exception ex) { Log.Write("irq set " + devs[i].Name + ": " + ex.Message); }
            }

            r.Ok = r.Set > 0;
            Log.Write("irq apply devices=" + r.Devices + " set=" + r.Set + " mask=0x"
                + avoid.ToString("X", CultureInfo.InvariantCulture));
            return r;
        }

        public void Restore()
        {
            for (int i = saved.Count - 1; i >= 0; i--)
            {
                IrqSaved s = saved[i];
                try
                {
                    if (s.CreatedKey)
                    {
                        Registry.LocalMachine.DeleteSubKeyTree(s.Path, false);
                        continue;
                    }
                    using (RegistryKey ap = Registry.LocalMachine.OpenSubKey(s.Path, true))
                    {
                        if (ap == null) continue;
                        if (s.HadPolicy) ap.SetValue("DevicePolicy", s.Policy);
                        else ap.DeleteValue("DevicePolicy", false);
                        if (s.HadMask) ap.SetValue("AssignmentSetOverride", s.Mask);
                        else ap.DeleteValue("AssignmentSetOverride", false);
                    }
                }
                catch (Exception ex) { Log.Write("irq restore: " + ex.Message); }
            }
            saved.Clear();
        }

        private static byte[] MaskBytes(long mask)
        {
            int n = Environment.Is64BitOperatingSystem ? 8 : 4;
            byte[] b = new byte[n];
            ulong u = (ulong)mask;
            for (int i = 0; i < n; i++) b[i] = (byte)(u >> (i * 8));
            return b;
        }

        private static string Str(object v)
        {
            return v == null ? "" : v.ToString();
        }

        /// <summary>设备描述形如 "@oem12.inf,%name%;Realtek ...",只保留分号后的可读名。</summary>
        private static string Friendly(string desc)
        {
            if (desc.Length == 0) return "";
            int cut = desc.LastIndexOf(';');
            return cut >= 0 && cut + 1 < desc.Length ? desc.Substring(cut + 1) : desc;
        }
    }
}