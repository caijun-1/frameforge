using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace FrameForge.Sys
{
    /// <summary>
    /// ICC 配置文件 → 5×5 颜色矩阵。
    ///
    /// 支持两种写法:
    /// 1) 矩阵型:rXYZ/gXYZ/bXYZ 三组色度标签 + rTRC/gTRC/bTRC 传递曲线,显示器 profile 最常见;
    /// 2) LUT 型:mAB/mBA 查找表(曲线 + 3×3 矩阵 + CLUT),sRGB v4 偏好这类文件就是这种。
    ///
    /// 没走系统的 mscms.dll:OpenColorProfile 的文件名模式在本机始终返回
    /// ERROR_INVALID_PARAMETER,内存缓冲模式能建出句柄但 TranslateColors 结果错乱还会崩进程。
    /// WIC 的 ColorContext 也试过:它按感知意图做色域映射,纯色会被改得面目全非。
    ///
    /// 做法是在 9×9×9 的 sRGB 网格上把每个颜色按 profile 定义映射一遍
    /// (sRGB 编码 → 线性 → XYZ → profile 线性 → profile 编码),再用最小二乘拟合 3×3 + 偏移。
    /// 放大镜只接受矩阵,曲线部分只能这样近似。
    /// </summary>
    public static class Icc
    {
        // D50 适配后的 sRGB → XYZ 矩阵,列向量和等于 D50 白点。
        private static readonly double[] SrgbToXyz =
        {
            0.4360747, 0.3850649, 0.1430804,
            0.2225045, 0.7168786, 0.0606169,
            0.0139322, 0.0971045, 0.7141733
        };

        private static readonly int[] Levels = { 0, 32, 64, 96, 128, 160, 192, 224, 255 };

        /// <summary>缓存:同一个 profile 每帧都要用,解析一次要好几百次采样。</summary>
        private sealed class Entry
        {
            public long Stamp;
            public float[] Matrix;
            public string Error;
        }

        private static readonly Dictionary<string, Entry> cache =
            new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);

        /// <summary>取 profile 的近似颜色矩阵;失败返回 null(原因记在日志里)。</summary>
        public static float[] For(string path)
        {
            string err;
            return Load(path, out err);
        }

        /// <summary>读取并解析 profile。失败时 err 给出原因,不抛异常。</summary>
        public static float[] Load(string path, out string err)
        {
            err = "";
            if (string.IsNullOrEmpty(path)) { err = "no profile"; return null; }
            try
            {
                var fi = new FileInfo(path);
                if (!fi.Exists) { err = "file not found"; return null; }
                long stamp = fi.LastWriteTimeUtc.Ticks;

                Entry hit;
                if (cache.TryGetValue(path, out hit) && hit.Stamp == stamp)
                {
                    // 失败结果也缓存:界面每 200ms 算一次预览,不能每次都去读一遍坏文件。
                    if (hit.Matrix == null) err = hit.Error;
                    return hit.Matrix;
                }

                float[] m = Parse(File.ReadAllBytes(path), out err);
                if (cache.Count > 16) cache.Clear();
                var e = new Entry();
                e.Stamp = stamp;
                e.Matrix = m;
                e.Error = err;
                cache[path] = e;
                if (m == null) Log.Write("icc " + path + ": " + err);
                return m;
            }
            catch (Exception ex)
            {
                err = ex.Message;
                Log.Write("icc: " + ex.Message);
                return null;
            }
        }

        public static void ClearCache() { cache.Clear(); }

        /// <summary>解析 profile 字节。失败时 err 给出原因。</summary>
        public static float[] Parse(byte[] d, out string err)
        {
            err = "";
            if (d == null || d.Length < 132) { err = "truncated profile"; return null; }
            if (Sig(d, 36) != "acsp") { err = "not an ICC profile"; return null; }
            if (Sig(d, 16) != "RGB ") { err = "not an RGB profile"; return null; }

            int n = U32(d, 128);
            if (n <= 0 || n > 256 || 132 + n * 12 > d.Length) { err = "bad tag table"; return null; }

            int ro = 0, go = 0, bo = 0, rt = 0, gt = 0, bt = 0;
            int a2b0 = 0, a2b1 = 0, b2a0 = 0, b2a1 = 0;
            for (int i = 0; i < n; i++)
            {
                int e = 132 + i * 12;
                string sig = Sig(d, e);
                int off = U32(d, e + 4);
                int size = U32(d, e + 8);
                if (off <= 0 || size <= 0 || off + size > d.Length) continue;
                if (sig == "rXYZ") ro = off;
                else if (sig == "gXYZ") go = off;
                else if (sig == "bXYZ") bo = off;
                else if (sig == "A2B0") a2b0 = off;
                else if (sig == "A2B1") a2b1 = off;
                else if (sig == "B2A0") b2a0 = off;
                else if (sig == "B2A1") b2a1 = off;
                else if (sig == "rTRC") rt = off;
                else if (sig == "gTRC") gt = off;
                else if (sig == "bTRC") bt = off;
            }
            if (ro == 0 || go == 0 || bo == 0)
            {
                // 没有矩阵标签就走 LUT。屏幕要的是“PCS → 设备值”,即 B2A 方向;
                // 优先相对比色(B2A1):感知意图会做黑点补偿和色域压缩,sRGB 文件会平白偏色。
                float[] lut = null;
                if (b2a1 != 0) lut = LutFit(d, b2a1, out err);
                if (lut == null && b2a0 != 0) lut = LutFit(d, b2a0, out err);
                if (lut != null) return lut;
                // 只有 A2B(设备 → PCS)的文件没法直接拿来当输出 profile。
                if (a2b0 != 0 || a2b1 != 0) { err = "lut profile"; return null; }
                err = "missing matrix tags";
                return null;
            }

            double[] r = Xyz(d, ro), g = Xyz(d, go), b = Xyz(d, bo);
            double[] mp = { r[0], g[0], b[0], r[1], g[1], b[1], r[2], g[2], b[2] };
            double[] inv = Inv3(mp);
            if (inv == null) { err = "degenerate matrix"; return null; }

            // profile 的编码 → 线性表;缺标签的通道按 sRGB 处理,至少不会整条通道失效。
            var trcR = new double[1024];
            var trcG = new double[1024];
            var trcB = new double[1024];
            for (int i = 0; i < 1024; i++)
            {
                double x = i / 1023.0;
                trcR[i] = SrgbDecode(x);
                trcG[i] = trcR[i];
                trcB[i] = trcR[i];
            }
            double[] t;
            if (rt != 0) { t = CurvTable(d, rt); if (t != null) trcR = t; }
            if (gt != 0) { t = CurvTable(d, gt); if (t != null) trcG = t; }
            if (bt != 0) { t = CurvTable(d, bt); if (t != null) trcB = t; }

            var samples = new double[729, 3];
            var target = new double[729, 3];
            int idx = 0;
            double[] xyz = new double[3], lin = new double[3];
            for (int rr = 0; rr < 9; rr++)
                for (int gg = 0; gg < 9; gg++)
                    for (int bb = 0; bb < 9; bb++)
                    {
                        double er = Levels[rr] / 255.0;
                        double eg = Levels[gg] / 255.0;
                        double eb = Levels[bb] / 255.0;
                        double[] srgbLin = { SrgbDecode(er), SrgbDecode(eg), SrgbDecode(eb) };
                        Mul(SrgbToXyz, srgbLin, xyz);
                        Mul(inv, xyz, lin);
                        samples[idx, 0] = er;
                        samples[idx, 1] = eg;
                        samples[idx, 2] = eb;
                        target[idx, 0] = Inv(trcR, Clamp01(lin[0]));
                        target[idx, 1] = Inv(trcG, Clamp01(lin[1]));
                        target[idx, 2] = Inv(trcB, Clamp01(lin[2]));
                        idx++;
                    }

            var res = Fit(samples, target, out err);
            if (res == null) return null;
            return res;
        }

        /// <summary>
        /// 在 9×9×9 的 sRGB 网格上最小二乘拟合 3×3 + 偏移;发散时返回 null。
        /// </summary>
        private static float[] Fit(double[,] samples, double[,] target, out string err)
        {
            return Fit(samples, target, samples.GetLength(0), false, out err);
        }

        /// <summary>
        /// LUT 专用:带剔除的最小二乘。LUT 在暗端常把值外插到 [0,1] 之外,那几个点不是
        /// 可信的设备值,留着会把 4 个数一起拽偏;先按残差剔几轮再收尾。
        /// 矩阵型 profile 不走剔除:它的曲线本来就拟合不进线性矩阵,剔了反而只剩两端。
        /// </summary>
        private static float[] Fit(double[,] samples, double[,] target, int count, out string err)
        {
            return Fit(samples, target, count, true, out err);
        }

        private static float[] Fit(double[,] samples, double[,] target, int count, bool trim, out string err)
        {
            err = "";
            var res = new float[25];
            var keep = new bool[count];
            for (int i = 0; i < count; i++) keep[i] = true;
            double[][] x = new double[3][];
            int pass = 0;
            while (true)
            {
                int kept = 0;
                for (int i = 0; i < count; i++) if (keep[i]) kept++;
                if (kept < (trim ? 64 : 4)) { err = "fit failed"; return null; }
                for (int ch = 0; ch < 3; ch++) x[ch] = Solve(samples, target, ch, keep, count);
                if (!trim || pass >= 3) break;
                pass++;
                var drop = new bool[count];
                int dropped = 0;
                for (int i = 0; i < count; i++)
                {
                    if (!keep[i]) continue;
                    double worst = 0;
                    for (int ch = 0; ch < 3; ch++)
                    {
                        double[] s = x[ch];
                        double e = Math.Abs(s[0] * samples[i, 0] + s[1] * samples[i, 1] + s[2] * samples[i, 2] + s[3] - target[i, ch]);
                        if (e > worst) worst = e;
                    }
                    if (worst > 0.06) { drop[i] = true; dropped++; }
                }
                // 一次最多剔三成,避免某一轮把样本清空。
                if (dropped == 0 || dropped > kept / 3) break;
                for (int i = 0; i < count; i++) if (drop[i]) keep[i] = false;
            }
            for (int ch = 0; ch < 3; ch++)
            {
                double[] s = x[ch];
                for (int c = 0; c < 4; c++)
                {
                    // 拟合发散(NaN / 超大值)宁可报错,也不要推给放大镜把桌面刷花。
                    if (double.IsNaN(s[c]) || double.IsInfinity(s[c]) || Math.Abs(s[c]) > 8.0)
                    {
                        err = "fit failed";
                        return null;
                    }
                    res[ch * 5 + c] = (float)s[c];
                }
            }
            res[18] = 1;
            res[24] = 1;
            return res;
        }

        /// <summary>
        /// LUT 型 profile:mAB/mBA 是「曲线组 → 3×3 矩阵 → 曲线组 → CLUT → 曲线组」的多段管线。
        /// 在 sRGB 网格上取 729 个颜色,先算成 PCS(Lab),过一遍 B2A 管线得到设备 RGB,
        /// 再拿同一套最小二乘拟合 3×3 + 偏移。相机/显示器 profile 用它做“按 profile 显示”。
        /// </summary>
        private static float[] LutFit(byte[] d, int off, out string err)
        {
            err = "";
            var lut = ReadLut(d, off);
            if (lut == null) { err = "bad lut"; return null; }
            // mBA 才是 PCS → 设备值;mAB 是反方向,倒推不了。
            if (lut.Type != "mBA ") { err = "lut direction"; return null; }
            if (lut.OutCh < 3) { err = "bad lut"; return null; }

            // PCS 大多数是 Lab(v4 也允许 XYZ):编码方式不同,这里按头部字段分辨。
            bool pcsLab = Sig(d, 20) == "Lab ";
            double xn = 0.9642, yn = 1.0, zn = 0.8249;
            int wt = TagOffset(d, "wtpt");
            if (wt != 0)
            {
                double[] wp = Xyz(d, wt);
                if (wp[1] > 0.01) { xn = wp[0]; yn = wp[1]; zn = wp[2]; }
            }

            var samples = new double[729, 3];
            var target = new double[729, 3];
            int k = 0;
            double[] pcs = new double[3], outv = new double[3], xyz = new double[3];
            for (int r = 0; r < 9; r++)
                for (int g = 0; g < 9; g++)
                    for (int b = 0; b < 9; b++)
                    {
                        double er = Levels[r] / 255.0;
                        double eg = Levels[g] / 255.0;
                        double eb = Levels[b] / 255.0;
                        double[] lin = { SrgbDecode(er), SrgbDecode(eg), SrgbDecode(eb) };
                        Mul(SrgbToXyz, lin, xyz);
                        if (pcsLab)
                        {
                            double fx = LabF(xyz[0] / xn), fy = LabF(xyz[1] / yn), fz = LabF(xyz[2] / zn);
                            pcs[0] = (116 * fy - 16) / 100.0;
                            pcs[1] = (500 * (fx - fy) + 128.0) / 255.0;
                            pcs[2] = (200 * (fy - fz) + 128.0) / 255.0;
                        }
                        else
                        {
                            pcs[0] = xyz[0] / xn;
                            pcs[1] = xyz[1] / yn;
                            pcs[2] = xyz[2] / zn;
                        }
                        RunB2A(lut, pcs, outv);
                        // 曲线里 pow 遇到负底数会出 NaN;这种点直接丢掉,否则整份拟合全废。
                        if (double.IsNaN(outv[0]) || double.IsNaN(outv[1]) || double.IsNaN(outv[2])) continue;
                        samples[k, 0] = er;
                        samples[k, 1] = eg;
                        samples[k, 2] = eb;
                        target[k, 0] = Clamp01(outv[0]);
                        target[k, 1] = Clamp01(outv[1]);
                        target[k, 2] = Clamp01(outv[2]);
                        k++;
                    }
            return Fit(samples, target, k, out err);
        }

        private static double LabF(double t)
        {
            return t > 0.008856451679035631 ? Math.Pow(t, 1.0 / 3.0) : (7.787037037037035 * t + 16.0 / 116.0);
        }

        /// <summary>mAB/mBA 标签:三组各 3 条曲线、可选的 3×4 矩阵、可选的 CLUT。</summary>
        private sealed class Lut
        {
            public byte[] D;
            public string Type;
            public int OutCh;
            public double[][] A, B, M;
            public double[] Mtx;
            public int[] G = new int[3];
            public int Clut;
        }

        private static Lut ReadLut(byte[] d, int off)
        {
            if (off <= 0 || off + 32 > d.Length) return null;
            string type = Sig(d, off);
            if (type != "mAB " && type != "mBA ") return null;
            var l = new Lut();
            l.D = d;
            l.Type = type;
            l.OutCh = d[off + 9];
            if (l.OutCh < 1 || l.OutCh > 3) return null;

            int bO = U32(d, off + 12), mO = U32(d, off + 16), moO = U32(d, off + 20);
            int cO = U32(d, off + 24), aO = U32(d, off + 28);
            if (bO != 0) { l.B = ReadCurves(d, off + bO); if (l.B == null) return null; }
            if (moO != 0) { l.M = ReadCurves(d, off + moO); if (l.M == null) return null; }
            if (aO != 0) { l.A = ReadCurves(d, off + aO); if (l.A == null) return null; }
            if (mO != 0)
            {
                if (off + mO + 48 > d.Length) return null;
                l.Mtx = new double[12];
                for (int i = 0; i < 12; i++) l.Mtx[i] = S15(d, off + mO + i * 4);
            }
            if (cO != 0)
            {
                int p = off + cO;
                if (p + 20 > d.Length) return null;
                l.G[0] = d[p];
                l.G[1] = d[p + 1];
                l.G[2] = d[p + 2];
                if (l.G[0] < 2 || l.G[1] < 2 || l.G[2] < 2) return null;
                // mAB/mBA 的 CLUT 固定 16 位格点,头部 20 字节(grid×3 + 精度 + 16 字节保留)。
                int need = 20 + l.G[0] * l.G[1] * l.G[2] * l.OutCh * 2;
                if (p + need > d.Length) return null;
                l.Clut = p + 20;
            }
            return l;
        }

        /// <summary>连续读 3 条曲线,每条都摊平成 1024 点表。</summary>
        private static double[][] ReadCurves(byte[] d, int p)
        {
            var res = new double[3][];
            for (int i = 0; i < 3; i++)
            {
                int next;
                res[i] = ReadCurve(d, p, out next);
                if (res[i] == null) return null;
                p = next;
            }
            return res;
        }

        private static double[] ReadCurve(byte[] d, int p, out int next)
        {
            next = p;
            if (p + 12 > d.Length) return null;
            string type = Sig(d, p);
            var t = new double[1024];
            if (type == "curv")
            {
                int n = U32(d, p + 8);
                if (n < 0 || p + 12 + n * 2 > d.Length) return null;
                next = p + 12 + n * 2;
                if (n == 0)
                {
                    for (int i = 0; i < 1024; i++) t[i] = i / 1023.0;
                    return t;
                }
                if (n == 1)
                {
                    double gam = ((d[p + 12] << 8) | d[p + 13]) / 256.0;
                    if (gam <= 0.001) gam = 1.0;
                    for (int i = 0; i < 1024; i++) t[i] = Math.Pow(i / 1023.0, gam);
                    return t;
                }
                var tb = new double[n];
                for (int i = 0; i < n; i++) tb[i] = ((d[p + 12 + i * 2] << 8) | d[p + 13 + i * 2]) / 65535.0;
                for (int i = 0; i < 1024; i++)
                {
                    double x = i / 1023.0 * (n - 1);
                    int i0 = (int)x;
                    if (i0 >= n - 1) { t[i] = tb[n - 1]; continue; }
                    double f = x - i0;
                    t[i] = tb[i0] + (tb[i0 + 1] - tb[i0]) * f;
                }
                return t;
            }
            if (type == "para")
            {
                int fn = (d[p + 8] << 8) | d[p + 9];
                int nP = fn == 0 ? 1 : fn == 1 ? 3 : fn == 2 ? 4 : fn == 3 ? 5 : fn == 4 ? 7 : -1;
                if (nP < 0 || p + 12 + nP * 4 > d.Length) return null;
                next = p + 12 + nP * 4;
                double g = S15(d, p + 12);
                // 缺省参数:只有 g 时 a 取 1,其余 0。
                double a = 1, b = 0, c = 0, dd = 0, e = 0, ff = 0;
                if (fn >= 1) { a = S15(d, p + 16); b = S15(d, p + 20); }
                if (fn >= 2) c = S15(d, p + 24);
                if (fn >= 3) dd = S15(d, p + 28);
                if (fn == 4) { e = S15(d, p + 32); ff = S15(d, p + 36); }
                for (int i = 0; i < 1024; i++)
                {
                    double x = i / 1023.0, y;
                    if (fn == 0) y = Math.Pow(x, g);
                    else if (fn == 1) y = (a != 0 && x >= -b / a) ? Math.Pow(a * x + b, g) : 0;
                    else if (fn == 2) y = Math.Pow(a * x + b, g) + c;
                    else if (fn == 3) y = x >= dd ? Math.Pow(a * x + b, g) + c : c;
                    else y = x >= dd ? Math.Pow(a * x + b, g) + e : c * x + ff;
                    t[i] = y;
                }
                return t;
            }
            return null;
        }

        private static double Tab(double[][] set, int ch, double x)
        {
            if (set == null || set[ch] == null) return Clamp01(x);
            if (x <= 0) return set[ch][0];
            if (x >= 1) return set[ch][1023];
            double pos = x * 1023.0;
            int i = (int)pos;
            double f = pos - i;
            return set[ch][i] + (set[ch][i + 1] - set[ch][i]) * f;
        }

        /// <summary>mBA 管线:B 曲线 → 矩阵(+偏移) → M 曲线 → CLUT → A 曲线。</summary>
        private static void RunB2A(Lut l, double[] pcs, double[] outv)
        {
            double v0 = Tab(l.B, 0, pcs[0]);
            double v1 = Tab(l.B, 1, pcs[1]);
            double v2 = Tab(l.B, 2, pcs[2]);
            if (l.Mtx != null)
            {
                double x = l.Mtx[0] * v0 + l.Mtx[1] * v1 + l.Mtx[2] * v2 + l.Mtx[9];
                double y = l.Mtx[3] * v0 + l.Mtx[4] * v1 + l.Mtx[5] * v2 + l.Mtx[10];
                double z = l.Mtx[6] * v0 + l.Mtx[7] * v1 + l.Mtx[8] * v2 + l.Mtx[11];
                v0 = x; v1 = y; v2 = z;
            }
            v0 = Tab(l.M, 0, v0);
            v1 = Tab(l.M, 1, v1);
            v2 = Tab(l.M, 2, v2);
            if (l.Clut != 0)
            {
                double[] o = new double[3];
                ClutEval(l, v0, v1, v2, o);
                v0 = o[0]; v1 = o[1]; v2 = o[2];
            }
            outv[0] = Tab(l.A, 0, v0);
            outv[1] = Tab(l.A, 1, v1);
            outv[2] = Tab(l.A, 2, v2);
        }

        /// <summary>CLUT 三线性插值。索引约定:通道 0 变化最慢(与本 profile 实测一致)。</summary>
        private static void ClutEval(Lut l, double v0, double v1, double v2, double[] o)
        {
            o[0] = 0; o[1] = 0; o[2] = 0;
            double f0 = Clamp01(v0) * (l.G[0] - 1);
            double f1 = Clamp01(v1) * (l.G[1] - 1);
            double f2 = Clamp01(v2) * (l.G[2] - 1);
            int i0 = (int)f0, i1 = (int)f1, i2 = (int)f2;
            if (i0 >= l.G[0] - 1) i0 = l.G[0] - 2;
            if (i1 >= l.G[1] - 1) i1 = l.G[1] - 2;
            if (i2 >= l.G[2] - 1) i2 = l.G[2] - 2;
            if (i0 < 0) i0 = 0;
            if (i1 < 0) i1 = 0;
            if (i2 < 0) i2 = 0;
            double t0 = f0 - i0, t1 = f1 - i1, t2 = f2 - i2;
            for (int a = 0; a < 2; a++)
                for (int b = 0; b < 2; b++)
                    for (int c = 0; c < 2; c++)
                    {
                        double w = (a == 0 ? 1 - t0 : t0) * (b == 0 ? 1 - t1 : t1) * (c == 0 ? 1 - t2 : t2);
                        if (w == 0) continue;
                        int id = ((i0 + a) * l.G[1] + (i1 + b)) * l.G[2] + (i2 + c);
                        int at = l.Clut + id * l.OutCh * 2;
                        for (int ch = 0; ch < 3; ch++)
                        {
                            int q = at + ch * 2;
                            o[ch] += w * (((l.D[q] << 8) | l.D[q + 1]) / 65535.0);
                        }
                    }
        }

        private static int TagOffset(byte[] d, string sig)
        {
            int n = U32(d, 128);
            for (int i = 0; i < n; i++)
            {
                int e = 132 + i * 12;
                if (Sig(d, e) == sig) return U32(d, e + 4);
            }
            return 0;
        }

        // ---------------- 标签解析 ----------------

        private static int U32(byte[] d, int o)
        {
            return (d[o] << 24) | (d[o + 1] << 16) | (d[o + 2] << 8) | d[o + 3];
        }

        private static double S15(byte[] d, int o)
        {
            return U32(d, o) / 65536.0;
        }

        private static string Sig(byte[] d, int o)
        {
            return "" + (char)d[o] + (char)d[o + 1] + (char)d[o + 2] + (char)d[o + 3];
        }

        private static double[] Xyz(byte[] d, int off)
        {
            return new double[] { S15(d, off + 8), S15(d, off + 12), S15(d, off + 16) };
        }

        /// <summary>传递曲线:编码值 → 线性值,统一插值成 1024 点。未知类型返回 null。</summary>
        private static double[] CurvTable(byte[] d, int off)
        {
            if (off + 12 > d.Length) return null;
            string type = Sig(d, off);
            if (type == "curv")
            {
                int n = U32(d, off + 8);
                if (n <= 0) return Identity();          // 恒等曲线
                var res = new double[1024];
                if (n == 1)
                {
                    // 单 gamma:u8Fixed8 在 off+12,注意不是 count 字段的低半字节。
                    if (off + 14 > d.Length) return null;
                    double gamma = ((d[off + 12] << 8) | d[off + 13]) / 256.0;
                    if (gamma <= 0.001) gamma = 1.0;
                    for (int i = 0; i < 1024; i++) res[i] = Math.Pow(i / 1023.0, gamma);
                    return res;
                }
                if (n > 4096 || off + 12 + n * 2 > d.Length) return null;
                var tbl = new double[n];
                for (int i = 0; i < n; i++)
                    tbl[i] = ((d[off + 12 + i * 2] << 8) | d[off + 13 + i * 2]) / 65535.0;
                for (int i = 0; i < 1024; i++)
                {
                    double x = i / 1023.0 * (n - 1);
                    int i0 = (int)x;
                    if (i0 >= n - 1) { res[i] = tbl[n - 1]; continue; }
                    double f = x - i0;
                    res[i] = tbl[i0] + (tbl[i0 + 1] - tbl[i0]) * f;
                }
                return res;
            }
            if (type == "para")
            {
                if (off + 12 > d.Length) return null;
                int fn = (d[off + 8] << 8) | d[off + 9];
                int need = 12 + (fn >= 2 ? 4 : 0) + (fn >= 3 ? 4 : 0) + (fn == 4 ? 8 : 0);
                if (off + need > d.Length) return null;
                int p = off + 12;
                double g = S15(d, p), a = S15(d, p + 4), b = S15(d, p + 8);
                double c = 0, dd = 0, e = 0, f = 0;
                if (fn >= 2) c = S15(d, p + 12);
                if (fn >= 3) dd = S15(d, p + 16);
                if (fn == 4) { e = S15(d, p + 20); f = S15(d, p + 24); }
                var res = new double[1024];
                for (int i = 0; i < 1024; i++)
                {
                    double x = i / 1023.0, y;
                    if (fn == 0) y = Math.Pow(x, g);
                    else if (fn == 1) y = (a != 0 && x >= -b / a) ? Math.Pow(a * x + b, g) : 0;
                    else if (fn == 2) y = Math.Pow(a * x + b, g) + c;
                    else if (fn == 3) y = x >= dd ? Math.Pow(a * x + b, g) + c : c;
                    else y = x >= dd ? Math.Pow(a * x + b, g) + e : c * x + f;
                    res[i] = y;
                }
                return res;
            }
            return null;
        }

        private static double[] Identity()
        {
            var t = new double[1024];
            for (int i = 0; i < 1024; i++) t[i] = i / 1023.0;
            return t;
        }

        /// <summary>在单调递增的查找表上反查线性值对应的编码值。</summary>
        private static double Inv(double[] fwd, double y)
        {
            if (y <= fwd[0]) return 0;
            if (y >= fwd[1023]) return 1;
            int lo = 0, hi = 1023;
            while (hi - lo > 1)
            {
                int mid = (lo + hi) / 2;
                if (fwd[mid] <= y) lo = mid; else hi = mid;
            }
            double span = fwd[hi] - fwd[lo];
            if (span <= 1e-9) return lo / 1023.0;
            double t = lo + (y - fwd[lo]) / span;
            return t / 1023.0;
        }

        private static double SrgbDecode(double x)
        {
            return x <= 0.04045 ? x / 12.92 : Math.Pow((x + 0.055) / 1.055, 2.4);
        }

        private static double Clamp01(double v)
        {
            if (v < 0) return 0;
            if (v > 1) return 1;
            return v;
        }

        // ---------------- 矩阵与拟合 ----------------

        private static double[] Inv3(double[] m)
        {
            double a = m[0], b = m[1], c = m[2];
            double d = m[3], e = m[4], f = m[5];
            double g = m[6], h = m[7], i = m[8];
            double det = a * (e * i - f * h) - b * (d * i - f * g) + c * (d * h - e * g);
            if (Math.Abs(det) < 1e-12) return null;
            double id = 1 / det;
            return new double[]
            {
                (e * i - f * h) * id, (c * h - b * i) * id, (b * f - c * e) * id,
                (f * g - d * i) * id, (a * i - c * g) * id, (c * d - a * f) * id,
                (d * h - e * g) * id, (b * g - a * h) * id, (a * e - b * d) * id
            };
        }

        private static void Mul(double[] m, double[] v, double[] out3)
        {
            double x = v[0], y = v[1], z = v[2];
            out3[0] = m[0] * x + m[1] * y + m[2] * z;
            out3[1] = m[3] * x + m[4] * y + m[5] * z;
            out3[2] = m[6] * x + m[7] * y + m[8] * z;
        }

        /// <summary>最小二乘拟合 3 输入 + 偏移;正规方程 4×4,部分主元高斯消元。</summary>
        private static double[] Solve(double[,] samples, double[,] target, int ch)
        {
            return Solve(samples, target, ch, null, samples.GetLength(0));
        }

        private static double[] Solve(double[,] samples, double[,] target, int ch, bool[] keep, int count)
        {
            var m = new double[4, 5];
            for (int i = 0; i < count; i++)
            {
                if (keep != null && !keep[i]) continue;
                double[] a = { samples[i, 0], samples[i, 1], samples[i, 2], 1.0 };
                for (int r = 0; r < 4; r++)
                {
                    for (int c = 0; c < 4; c++) m[r, c] += a[r] * a[c];
                    m[r, 4] += a[r] * target[i, ch];
                }
            }
            for (int col = 0; col < 4; col++)
            {
                int pivot = col;
                for (int r = col + 1; r < 4; r++)
                    if (Math.Abs(m[r, col]) > Math.Abs(m[pivot, col])) pivot = r;
                if (Math.Abs(m[pivot, col]) < 1e-12) return new double[4];
                if (pivot != col)
                    for (int c = col; c <= 4; c++)
                    {
                        double t = m[col, c];
                        m[col, c] = m[pivot, c];
                        m[pivot, c] = t;
                    }
                for (int r = col + 1; r < 4; r++)
                {
                    double f = m[r, col] / m[col, col];
                    if (f == 0) continue;
                    for (int c = col; c <= 4; c++) m[r, c] -= f * m[col, c];
                }
            }
            var x = new double[4];
            for (int r = 3; r >= 0; r--)
            {
                double s = m[r, 4];
                for (int c = r + 1; c < 4; c++) s -= m[r, c] * x[c];
                x[r] = s / m[r, r];
            }
            return x;
        }

        // ---------------- 自检 ----------------

        /// <summary>
        /// 用一份现造的 sRGB profile(矩阵标签 + gamma 2.2)验证解析链路:
        /// sRGB 进、sRGB 出,拟合矩阵必须接近恒等。
        /// </summary>
        public static bool SelfTest(out string detail)
        {
            detail = "";
            try
            {
                string err;
                float[] m = Parse(SyntheticSrgb(), out err);
                if (m == null) { detail = "synthetic profile: " + err; return false; }
                double worst = 0;
                for (int row = 0; row < 3; row++)
                {
                    for (int col = 0; col < 3; col++)
                    {
                        double want = row == col ? 1.0 : 0.0;
                        double diff = Math.Abs(m[row * 5 + col] - want);
                        if (diff > worst) worst = diff;
                    }
                    double off = Math.Abs(m[row * 5 + 4]);
                    if (off > worst) worst = off;
                }
                detail = "sRGB identity, max deviation " + worst.ToString("0.000", CultureInfo.InvariantCulture);
                return worst < 0.05;
            }
            catch (Exception ex) { detail = ex.Message; return false; }
        }

        /// <summary>造一份最小的显示类 profile:6 个标签,数据区从 204 开始。</summary>
        private static byte[] SyntheticSrgb()
        {
            int total = 280;
            var d = new byte[total];
            PutU32(d, 0, total);
            PutSig(d, 4, "none");
            PutU32(d, 8, 0x02100000);
            PutSig(d, 12, "mntr");
            PutSig(d, 16, "RGB ");
            PutSig(d, 20, "XYZ ");
            PutSig(d, 36, "acsp");
            // D50 白点
            PutS15(d, 68, 0.9642);
            PutS15(d, 72, 1.0);
            PutS15(d, 76, 0.8249);
            PutU32(d, 128, 6);

            Tag(d, 0, "rXYZ", 204, 20);
            Tag(d, 1, "gXYZ", 224, 20);
            Tag(d, 2, "bXYZ", 244, 20);
            Tag(d, 3, "rTRC", 264, 14);
            Tag(d, 4, "gTRC", 264, 14);
            Tag(d, 5, "bTRC", 264, 14);

            XyzBlock(d, 204, SrgbToXyz[0], SrgbToXyz[3], SrgbToXyz[6]);
            XyzBlock(d, 224, SrgbToXyz[1], SrgbToXyz[4], SrgbToXyz[7]);
            XyzBlock(d, 244, SrgbToXyz[2], SrgbToXyz[5], SrgbToXyz[8]);

            PutSig(d, 264, "curv");
            PutU32(d, 268, 1);
            d[272] = 0x02;
            d[273] = 0x33;   // 2.2
            return d;
        }

        private static void Tag(byte[] d, int i, string sig, int off, int size)
        {
            int e = 132 + i * 12;
            PutSig(d, e, sig);
            PutU32(d, e + 4, off);
            PutU32(d, e + 8, size);
        }

        private static void XyzBlock(byte[] d, int off, double x, double y, double z)
        {
            PutSig(d, off, "XYZ ");
            PutS15(d, off + 8, x);
            PutS15(d, off + 12, y);
            PutS15(d, off + 16, z);
        }

        private static void PutU32(byte[] d, int o, int v)
        {
            d[o] = (byte)(v >> 24);
            d[o + 1] = (byte)(v >> 16);
            d[o + 2] = (byte)(v >> 8);
            d[o + 3] = (byte)v;
        }

        private static void PutSig(byte[] d, int o, string s)
        {
            for (int i = 0; i < 4; i++) d[o + i] = (byte)s[i];
        }

        private static void PutS15(byte[] d, int o, double v)
        {
            PutU32(d, o, (int)Math.Round(v * 65536.0));
        }
    }
}