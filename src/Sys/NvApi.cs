using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace FrameForge.Sys
{
    /// <summary>
    /// NVIDIA 驱动里的数字振动(饱和度)。
    /// 独占全屏时 DWM 不参与合成、gamma LUT 又做不了跨通道,唯一还能改颜色的
    /// 就是驱动自己的显示设置;这里通过 NVAPI 直接改 DVC 等级,不注入、不挂 hook。
    /// 没有 NVIDIA 驱动(或函数编号对不上)时静默跳过,不影响其它滤镜路径。
    /// </summary>
    internal static class NvApi
    {
        // NVAPI 只导出 nvapi_QueryInterface,其它函数都要用编号去换地址。
        // 这些编号在 NVIDIA 驱动里多年没变过,属于事实上的稳定 ABI。
        private const uint IdInitialize = 0x0150E828;
        private const uint IdEnumDisplay = 0x9ABDD40D;
        private const uint IdEnumPhysicalGpu = 0xE5AC921F;
        private const uint IdGetDvcInfo = 0x4085DE45;
        private const uint IdSetDvcLevel = 0x172409B4;
        private const uint IdSetDvcLevelEx = 0x4A54C2DE;

        private const int Ok = 0;
        // NV_DVC_INFO 实际是 16 字节(version/current/min/max),传 8 字节版本会被判为
        // INCOMPATIBLE_STRUCT_VERSION(-9),这个在真机上验证过。
        private const uint DvcInfoVer1 = 0x00010010;

        [StructLayout(LayoutKind.Sequential)]
        private struct DvcInfo
        {
            public uint version;
            public int current;
            public int min;
            public int max;
        }

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate IntPtr QueryFn(uint id);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int InitializeFn();
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int EnumDisplayFn(uint index, out IntPtr handle);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int EnumGpuFn(IntPtr[] handles, out uint count);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int GetDvcFn(IntPtr handle, uint outputId, ref DvcInfo info);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int SetDvcFn(IntPtr handle, uint outputId, int level);

        private static bool probed;
        private static bool available;
        private static IntPtr lib;
        private static GetDvcFn getDvc;
        private static SetDvcFn setDvc;

        // 能读能写的输出句柄(优先显示句柄,退而用物理 GPU 句柄),
        // 以及每个输出当前的 DVC 值和上限,还原与按比例抬升都靠它们。
        private static IntPtr[] targets = new IntPtr[0];
        private static int[] saved = new int[0];
        private static int[] maxes = new int[0];

        private static bool applied;
        private static double fraction;

        /// <summary>当前是否正由我们接管数字振动。</summary>
        public static bool Applied { get { return applied; } }

        /// <summary>
        /// 按驱动量程的百分比抬升数字振动(fraction 0-1)。
        /// 用「当前值 + 剩余量程 × fraction」而不是绝对等级:不同驱动的 DVC 量程不一样
        /// (常见 0-100,也有 0-63),并且这样只会往上抬,不会把用户自己调的值压回去。
        /// 返回 true 表示驱动已经接管了饱和度,调用方就不用再叠 S 曲线近似。
        /// </summary>
        public static bool Boost(double fraction)
        {
            Probe();
            if (!available) return false;
            if (fraction < 0) fraction = 0;
            if (fraction > 1) fraction = 1;
            // 每次接管前重新读一遍用户当前值,免得把探测之后的改动还原回去。
            if (!applied) Capture();

            bool ok = false;
            for (int i = 0; i < targets.Length; i++)
            {
                int v = Target(i, fraction);
                if (v < 0) continue;
                try { if (setDvc(targets[i], 0, v) == Ok) ok = true; }
                catch { }
            }
            if (!ok) return false;
            if (!applied) Log.Write("nvapi dvc frac=" + fraction.ToString("0.00"));
            applied = true;
            NvApi.fraction = fraction;
            return true;
        }

        /// <summary>
        /// 按上次的比例补写。NVIDIA App 会在游戏启动 / Alt+Tab 时按配置重放显示设置,
        /// 不补写的话用户会发现滤镜"过一会儿自己没了"。
        /// </summary>
        public static void Reapply()
        {
            if (!applied) return;
            for (int i = 0; i < targets.Length; i++)
            {
                int v = Target(i, fraction);
                if (v < 0) continue;
                try { setDvc(targets[i], 0, v); }
                catch { }
            }
        }

        /// <summary>把数字振动还原成接管前的值。</summary>
        public static void Restore()
        {
            if (!applied) return;
            applied = false;
            for (int i = 0; i < targets.Length; i++)
            {
                try { setDvc(targets[i], 0, saved[i]); }
                catch { }
            }
            Log.Write("nvapi dvc restore");
        }

        /// <summary>退出时调用:还原并卸载 nvapi 模块。</summary>
        public static void Release()
        {
            try { Restore(); } catch { }
            if (lib != IntPtr.Zero)
            {
                try { Native.FreeLibrary(lib); } catch { }
                lib = IntPtr.Zero;
            }
            getDvc = null;
            setDvc = null;
            targets = new IntPtr[0];
            saved = new int[0];
            maxes = new int[0];
            probed = false;
            available = false;
        }

        private static int Target(int i, double fraction)
        {
            int span = maxes[i] - saved[i];
            if (span <= 0) return -1;   // 已经顶到驱动上限,驱动这边帮不上忙
            return saved[i] + (int)Math.Round(span * fraction);
        }

        private static void Probe()
        {
            if (probed) return;
            probed = true;
            try
            {
                lib = Native.LoadLibrary(IntPtr.Size == 8 ? "nvapi64.dll" : "nvapi.dll");
                if (lib == IntPtr.Zero) return;
                IntPtr addr = Native.GetProcAddress(lib, "nvapi_QueryInterface");
                if (addr == IntPtr.Zero) return;
                var query = (QueryFn)Marshal.GetDelegateForFunctionPointer(addr, typeof(QueryFn));

                IntPtr pInit = query(IdInitialize);
                if (pInit == IntPtr.Zero) return;
                if (((InitializeFn)Marshal.GetDelegateForFunctionPointer(pInit, typeof(InitializeFn)))() != Ok) return;

                IntPtr pGet = query(IdGetDvcInfo);
                IntPtr pSet = query(IdSetDvcLevelEx);
                if (pSet == IntPtr.Zero) pSet = query(IdSetDvcLevel);
                if (pGet == IntPtr.Zero || pSet == IntPtr.Zero) return;
                getDvc = (GetDvcFn)Marshal.GetDelegateForFunctionPointer(pGet, typeof(GetDvcFn));
                setDvc = (SetDvcFn)Marshal.GetDelegateForFunctionPointer(pSet, typeof(SetDvcFn));

                // 先按显示句柄找:绝大多数单屏/多屏桌面都能直接读到。
                var list = new List<IntPtr>();
                IntPtr pEnum = query(IdEnumDisplay);
                if (pEnum != IntPtr.Zero)
                {
                    var en = (EnumDisplayFn)Marshal.GetDelegateForFunctionPointer(pEnum, typeof(EnumDisplayFn));
                    for (uint i = 0; i < 8; i++)
                    {
                        IntPtr h;
                        if (en(i, out h) != Ok || h == IntPtr.Zero) break;
                        list.Add(h);
                    }
                }
                if (list.Count == 0)
                {
                    // 少数驱动/笔记本内屏上显示句柄枚举为空,退回物理 GPU 句柄。
                    IntPtr pGpu = query(IdEnumPhysicalGpu);
                    if (pGpu != IntPtr.Zero)
                    {
                        var arr = new IntPtr[16];
                        uint count;
                        var eg = (EnumGpuFn)Marshal.GetDelegateForFunctionPointer(pGpu, typeof(EnumGpuFn));
                        if (eg(arr, out count) == Ok)
                        {
                            for (uint i = 0; i < count && i < arr.Length; i++)
                                if (arr[i] != IntPtr.Zero) list.Add(arr[i]);
                        }
                    }
                }

                var keepH = new List<IntPtr>();
                var keepV = new List<int>();
                var keepMax = new List<int>();
                for (int i = 0; i < list.Count; i++)
                {
                    var info = new DvcInfo();
                    info.version = DvcInfoVer1;
                    int st;
                    try { st = getDvc(list[i], 0, ref info); }
                    catch { continue; }
                    if (st != Ok || info.max <= info.min) continue;
                    keepH.Add(list[i]);
                    keepV.Add(info.current);
                    keepMax.Add(info.max);
                }
                if (keepH.Count == 0) return;

                targets = keepH.ToArray();
                saved = keepV.ToArray();
                maxes = keepMax.ToArray();
                available = true;
                Log.Write("nvapi dvc ok targets=" + targets.Length + " cur=" + saved[0] + " max=" + maxes[0]);
            }
            catch (DllNotFoundException) { }
            catch (EntryPointNotFoundException) { }
            catch (Exception ex) { Log.Write("nvapi: " + ex.Message); }
        }

        /// <summary>接管前读一遍用户当前的 DVC,退出时按这个值还原。</summary>
        private static void Capture()
        {
            for (int i = 0; i < targets.Length; i++)
            {
                var info = new DvcInfo();
                info.version = DvcInfoVer1;
                try
                {
                    if (getDvc(targets[i], 0, ref info) == Ok)
                    {
                        saved[i] = info.current;
                        maxes[i] = info.max;
                    }
                }
                catch { }
            }
        }
    }
}