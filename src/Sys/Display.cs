using System;
using System.Management;
using System.Runtime.InteropServices;

namespace FrameForge.Sys
{
    /// <summary>
    /// 显示器硬件亮度。外接显示器走 DDC/CI 的 VCP 0x10;
    /// 笔记本内置屏普遍不实现 DDC/CI,改走 WMI(WmiMonitorBrightness)调背光。
    /// 直接调硬件亮度,不改画面颜色,也不进录屏/截图;不支持时明确返回失败。
    /// </summary>
    public static class Display
    {
        private const uint MonitorDefaultToNearest = 2;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct PhysicalMonitor
        {
            public IntPtr Handle;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
            public string Description;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct Point
        {
            public int X;
            public int Y;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct MonitorInfoEx
        {
            public int Size;
            public int MonitorLeft, MonitorTop, MonitorRight, MonitorBottom;
            public int WorkLeft, WorkTop, WorkRight, WorkBottom;
            public uint Flags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
            public string Device;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct DisplayDevice
        {
            public int Size;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
            public string DeviceName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
            public string DeviceString;
            public uint StateFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
            public string DeviceID;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
            public string DeviceKey;
        }

        [DllImport("user32.dll")]
        private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

        [DllImport("user32.dll")]
        private static extern IntPtr MonitorFromPoint(Point pt, uint flags);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MonitorInfoEx info);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern bool EnumDisplayDevices(string device, uint devNum, ref DisplayDevice dev, uint flags);

        [DllImport("dxva2.dll", SetLastError = true)]
        private static extern bool GetNumberOfPhysicalMonitorsFromHMONITOR(IntPtr hMonitor, out uint count);

        [DllImport("dxva2.dll", SetLastError = true)]
        private static extern bool GetPhysicalMonitorsFromHMONITOR(IntPtr hMonitor, uint count,
            [Out] PhysicalMonitor[] monitors);

        [DllImport("dxva2.dll", SetLastError = true)]
        private static extern bool DestroyPhysicalMonitors(uint count, [In] PhysicalMonitor[] monitors);

        [DllImport("dxva2.dll", SetLastError = true)]
        private static extern bool GetMonitorBrightness(IntPtr monitor, out uint min, out uint cur, out uint max);

        [DllImport("dxva2.dll", SetLastError = true)]
        private static extern bool SetMonitorBrightness(IntPtr monitor, uint value);

        private static IntPtr window;
        private static IntPtr target;
        private static PhysicalMonitor[] monitors = new PhysicalMonitor[0];
        private static bool probed;
        private static bool ready;
        private static bool wmiReady;
        private static string wmiInstance = "";
        private static int index = -1;
        private static int lo, hi, cur;
        private static string name = "";

        /// <summary>当前显示器名称,给界面做提示用。</summary>
        public static string MonitorName
        {
            get { Ensure(); return name; }
        }

        public static bool Supported
        {
            get { Ensure(); return ready; }
        }

        /// <summary>告诉硬件亮度要跟着哪个窗口所在的显示器走。</summary>
        public static void Attach(IntPtr hwnd)
        {
            if (hwnd != IntPtr.Zero) window = hwnd;
            Ensure();
        }

        /// <summary>读当前亮度与范围;不支持时返回 false。</summary>
        public static bool TryGet(out int value, out int min, out int max)
        {
            Ensure();
            value = cur;
            min = lo;
            max = hi;
            if (!ready) return false;

            if (wmiReady)
            {
                int v = WmiGet();
                if (v < 0) return false;
                cur = v;
                lo = 0;
                hi = 100;
                value = cur;
                min = lo;
                max = hi;
                return true;
            }

            uint l, c, h;
            if (!GetMonitorBrightness(monitors[index].Handle, out l, out c, out h)) return false;
            lo = (int)l;
            hi = (int)h;
            cur = (int)c;
            value = cur;
            min = lo;
            max = hi;
            return true;
        }

        /// <summary>写硬件亮度;失败返回 false,由界面提示不支持。</summary>
        public static bool TrySet(int value)
        {
            Ensure();
            if (!ready) return false;

            if (value < lo) value = lo;
            if (value > hi) value = hi;

            if (wmiReady) return WmiSet(value);

            try
            {
                if (!SetMonitorBrightness(monitors[index].Handle, (uint)value))
                {
                    Log.Write("ddc set brightness failed: " + Marshal.GetLastWin32Error());
                    return false;
                }
            }
            catch (Exception ex)
            {
                Log.Write("ddc set brightness: " + ex.Message);
                return false;
            }
            cur = value;
            return true;
        }

        /// <summary>退出时释放物理显示器句柄。</summary>
        public static void Shutdown()
        {
            Release();
            probed = false;
            window = IntPtr.Zero;
        }

        private static void Ensure()
        {
            IntPtr hmon = IntPtr.Zero;
            try
            {
                if (window != IntPtr.Zero) hmon = MonitorFromWindow(window, MonitorDefaultToNearest);
                if (hmon == IntPtr.Zero)
                {
                    var pt = new Point();
                    pt.X = 0;
                    pt.Y = 0;
                    hmon = MonitorFromPoint(pt, MonitorDefaultToNearest);
                }
            }
            catch (Exception ex)
            {
                Log.Write("ddc monitor: " + ex.Message);
            }

            if (probed && hmon == target) return;
            Release();
            target = hmon;
            probed = true;
            Probe();
        }

        private static void Probe()
        {
            ready = false;
            wmiReady = false;
            wmiInstance = "";
            index = -1;
            name = "";
            try
            {
                if (target == IntPtr.Zero) return;

                uint count;
                if (GetNumberOfPhysicalMonitorsFromHMONITOR(target, out count) && count > 0)
                {
                    var list = new PhysicalMonitor[count];
                    if (GetPhysicalMonitorsFromHMONITOR(target, count, list))
                    {
                        monitors = list;
                        if (list.Length > 0) name = list[0].Description == null ? "" : list[0].Description;

                        for (int i = 0; i < list.Length; i++)
                        {
                            uint l, c, h;
                            if (GetMonitorBrightness(list[i].Handle, out l, out c, out h) && h > 0)
                            {
                                index = i;
                                lo = (int)l;
                                hi = (int)h;
                                cur = (int)c;
                                ready = true;
                                break;
                            }
                        }
                    }
                }

                // 内置屏:DDC/CI 通常读不到亮度,改用 WMI 的背光接口。
                if (!ready && ProbeWmi())
                {
                    wmiReady = true;
                    ready = true;
                }
            }
            catch (Exception ex)
            {
                Log.Write("ddc probe: " + ex.Message);
            }
        }

        /// <summary>当前 HMONITOR 对应显示器的 PnP 设备 ID,用来把 WMI 条目和屏幕对上。</summary>
        private static string TargetDeviceId()
        {
            try
            {
                var mi = new MonitorInfoEx();
                mi.Size = Marshal.SizeOf(typeof(MonitorInfoEx));
                if (!GetMonitorInfo(target, ref mi)) return "";

                var dd = new DisplayDevice();
                dd.Size = Marshal.SizeOf(typeof(DisplayDevice));
                if (!EnumDisplayDevices(mi.Device, 0, ref dd, 0)) return "";
                return dd.DeviceID == null ? "" : dd.DeviceID;
            }
            catch (Exception ex)
            {
                Log.Write("ddc device id: " + ex.Message);
                return "";
            }
        }

        /// <summary>
        /// WMI 的 InstanceName 是 DISPLAY\CSO161B\5&...,
        /// EnumDisplayDevices 的 DeviceID 是 MONITOR\CSO161B\{...}\0004,
        /// 前缀和实例后缀都不同,第二段的面板型号相同才是同一块屏。
        /// </summary>
        private static bool SamePanel(string wmiInstance, string deviceId)
        {
            string a = PanelKey(wmiInstance);
            string b = PanelKey(deviceId);
            return a.Length > 0 && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        }

        private static string PanelKey(string id)
        {
            if (id == null || id.Length == 0) return "";
            string[] parts = id.Split('\\');
            return parts.Length < 2 ? "" : parts[1];
        }

        private static bool ProbeWmi()
        {
            string device = TargetDeviceId();
            bool know = device.Length > 0;
            int fallbackCur = -1;
            string fallbackInst = "";
            try
            {
                using (var s = new ManagementObjectSearcher(@"root\wmi",
                    "SELECT InstanceName, CurrentBrightness FROM WmiMonitorBrightness"))
                {
                    foreach (ManagementBaseObject o in s.Get())
                    {
                        string inst = Convert.ToString(o["InstanceName"]);
                        object v = o["CurrentBrightness"];
                        if (v == null) continue;
                        int level = Convert.ToInt32(v);

                        if (know && !SamePanel(inst, device))
                        {
                            if (fallbackCur < 0) { fallbackCur = level; fallbackInst = inst == null ? "" : inst; }
                            continue;
                        }

                        cur = level;
                        lo = 0;
                        hi = 100;
                        wmiInstance = inst == null ? "" : inst;
                        name = "Internal display";
                        return true;
                    }
                }

                // 拿不到 PnP ID 时退一步:只有一个内置屏也认。
                if (!know && fallbackCur >= 0)
                {
                    cur = fallbackCur;
                    lo = 0;
                    hi = 100;
                    wmiInstance = fallbackInst;
                    name = "Internal display";
                    return true;
                }
            }
            catch (Exception ex)
            {
                Log.Write("wmi brightness probe: " + ex.Message);
            }
            return false;
        }

        private static int WmiGet()
        {
            try
            {
                using (var s = new ManagementObjectSearcher(@"root\wmi",
                    "SELECT InstanceName, CurrentBrightness FROM WmiMonitorBrightness"))
                {
                    foreach (ManagementBaseObject o in s.Get())
                    {
                        string inst = Convert.ToString(o["InstanceName"]);
                        if (wmiInstance.Length > 0 && !string.Equals(inst, wmiInstance, StringComparison.OrdinalIgnoreCase))
                            continue;
                        object v = o["CurrentBrightness"];
                        if (v != null) return Convert.ToInt32(v);
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Write("wmi brightness read: " + ex.Message);
            }
            return -1;
        }

        private static bool WmiSet(int value)
        {
            try
            {
                using (var s = new ManagementObjectSearcher(@"root\wmi",
                    "SELECT InstanceName FROM WmiMonitorBrightnessMethods"))
                {
                    foreach (ManagementBaseObject o in s.Get())
                    {
                        string inst = Convert.ToString(o["InstanceName"]);
                        if (wmiInstance.Length > 0 && !string.Equals(inst, wmiInstance, StringComparison.OrdinalIgnoreCase))
                            continue;

                        using (var mo = (ManagementObject)o)
                        {
                            object[] args = new object[] { (uint)0, (byte)value };
                            mo.InvokeMethod("WmiSetBrightness", args);
                        }
                        cur = value;
                        return true;
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Write("wmi brightness set: " + ex.Message);
            }
            return false;
        }

        private static void Release()
        {
            wmiReady = false;
            wmiInstance = "";
            if (monitors.Length == 0) return;
            try { DestroyPhysicalMonitors((uint)monitors.Length, monitors); }
            catch (Exception ex) { Log.Write("ddc release: " + ex.Message); }
            monitors = new PhysicalMonitor[0];
            ready = false;
            index = -1;
        }
    }
}