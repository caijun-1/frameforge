using System;
using System.Collections.Generic;

namespace FrameForge.Sys
{
    /// <summary>
    /// 加速前后帧率对比:加速前先攒一段基线,加速稳定后再取中位数比较。
    /// 只读数据,不做任何系统修改;差异太小时直接说"基本持平",不甩误导性的负数。
    /// </summary>
    public sealed class PerfCompare
    {
        private const double BaseWindow = 10;   // 加速前保留多长的基线
        private const double SettleDelay = 12;  // 加速后多久出结论
        private const double PostWindow = 4;    // 结论只取最后几秒,避开刚加速的抖动
        private const double FlatPct = 3;       // 差值小于这个百分比视为持平

        private struct Sample
        {
            public double T;
            public double Fps;
            public double Low;
        }

        private readonly List<Sample> pre = new List<Sample>();
        private readonly List<Sample> post = new List<Sample>();
        private bool active;
        private bool invalid;      // 没有可信的加速前基线,这一轮不比较
        private DateTime start;
        private double baseFps;
        private double baseLow;
        private bool hasLow;
        private string result = "";

        public bool Armed { get { return active && result.Length == 0; } }

        /// <summary>每跳调用一次。boosting 为 false 时积累加速前基线,为 true 时开始计时。</summary>
        public void Feed(bool boosting, double fps, double low)
        {
            if (fps <= 1) return;
            DateTime now = DateTime.UtcNow;
            double t = now.Ticks / (double)TimeSpan.TicksPerSecond;

            if (!boosting)
            {
                active = false;
                invalid = false;
                start = DateTime.MinValue;
                result = "";
                post.Clear();
                pre.Add(new Sample { T = t, Fps = fps, Low = low });
                Trim(pre, t, BaseWindow);
                return;
            }

            if (!active)
            {
                active = true;
                start = now;
                result = "";
                post.Clear();

                // 基线必须是"游戏真在出帧"的一段连续数据。以前基线不够时会拿
                // 加速后的第一帧顶替,而那一帧往往还是加载画面/菜单(正好卡在
                // 刷新率上限),于是 15 分钟一局会被判成 -25%。宁可不出结论。
                invalid = pre.Count < 15 || (pre[pre.Count - 1].T - pre[0].T) < 5.0;
                if (invalid)
                {
                    Log.Write("perf: baseline too short (" + pre.Count + " samples), skip compare");
                    return;
                }

                // 基线取加速前最后 10 秒的中位数,场景抖动能被摊平。
                baseFps = Median(pre, true);
                baseLow = Median(pre, false);
                hasLow = baseLow > 0;
            }
            if (invalid) return;

            post.Add(new Sample { T = t, Fps = fps, Low = low });
            Trim(post, t, SettleDelay);

            if (result.Length == 0 && (now - start).TotalSeconds >= SettleDelay)
                result = Build(t);
        }

        public string Text() { return result; }

        public void Reset()
        {
            active = false;
            invalid = false;
            start = DateTime.MinValue;
            result = "";
            baseFps = 0;
            baseLow = 0;
            hasLow = false;
            pre.Clear();
            post.Clear();
        }

        /// <summary>用最近 4 秒的中位数下结论,单个卡顿尖峰不会把数字带偏。</summary>
        private string Build(double now)
        {
            var win = new List<Sample>();
            for (int i = 0; i < post.Count; i++)
                if (post[i].T >= now - PostWindow) win.Add(post[i]);
            if (win.Count < 3) win = post;
            if (win.Count == 0 || baseFps <= 0) return "";

            double nowFps = Median(win, true);
            double nowLow = Median(win, false);
            if (nowFps <= 0) return "";

            double pct = (nowFps - baseFps) / baseFps * 100.0;
            if (Math.Abs(pct) < FlatPct)
                return I18n.T("perf.flat", baseFps, nowFps);

            string delta = (pct >= 0 ? "+" : "") + pct.ToString("0") + "%";
            string s = I18n.T("perf.line", baseFps, nowFps, delta);
            if (hasLow && baseLow > 0 && nowLow > 0)
                s += " · " + I18n.T("perf.low", baseLow, nowLow);
            return s;
        }

        private static void Trim(List<Sample> list, double now, double span)
        {
            int i = 0;
            while (i < list.Count && list[i].T < now - span) i++;
            if (i > 0) list.RemoveRange(0, i);
            // 长时间挂着也不让列表无限长。
            if (list.Count > 600) list.RemoveRange(0, list.Count - 600);
        }

        private static double Median(List<Sample> list, bool fps)
        {
            if (list.Count == 0) return 0;
            var v = new List<double>(list.Count);
            for (int i = 0; i < list.Count; i++)
            {
                double x = fps ? list[i].Fps : list[i].Low;
                if (x > 0) v.Add(x);
            }
            if (v.Count == 0) return 0;
            v.Sort();
            return v[v.Count / 2];
        }
    }
}