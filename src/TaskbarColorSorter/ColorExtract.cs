using System.Drawing;
using System.Drawing.Imaging;

namespace TaskbarColorSorter;

/// <summary>图标里的一簇颜色。<paramref name="Share"/> 是该簇占整个图标的**面积**比例。</summary>
internal readonly record struct HueCluster(double Hue, double Share, Color Representative);

internal readonly record struct IconColor(
    double Hue,          // 0..360，IsGray 时无意义
    double Saturation,   // 0..1
    double Value,        // 0..1
    bool IsGray,
    Color Representative,
    double AchromaticShare,  // 无彩色（黑/白/灰）区域占图标面积比例
    HueCluster[] Clusters);

internal static class ColorExtract
{
    /// <summary>参与色相统计的最低饱和度/明度。低于此值的像素视为无彩色（黑/白/灰或抗锯齿边缘）。</summary>
    private const double MinSat = 0.25;
    private const double MinVal = 0.18;

    /// <summary>与背景色的 RGB 距离小于此值的像素视为背景，不算图标。</summary>
    private const int BgDistanceThreshold = 52;

    /// <summary>彩色簇要被当回事至少得占这么大面积，低于此值视为噪声。</summary>
    private const double ClusterNoiseFloor = 0.05;

    /// <summary>次要色簇被认定为"显著"所需的面积，相对主色簇的比例。达到即视为多色图标。</summary>
    private const double MinClusterRatio = 0.55;

    private const int MaxClusters = 4;

    /// <summary>主色搜索窗口的半宽（度）。</summary>
    private const int HueWindow = 25;

    /// <summary>空间权重高斯核的 sigma，相对分析区域边长的比例。</summary>
    private const double CenterSigmaRatio = 0.30;

    /// <summary>分析区域相对按钮矩形的比例（取中心正方形，只覆盖图标本身）。</summary>
    private const double CoreRatio = 0.62;

    public static Bitmap CaptureScreen(Rectangle area)
    {
        var bmp = new Bitmap(area.Width, area.Height, PixelFormat.Format32bppArgb);
        try
        {
            using var g = Graphics.FromImage(bmp);
            g.CopyFromScreen(area.Left, area.Top, 0, 0, area.Size, CopyPixelOperation.SourceCopy);
            return bmp;
        }
        catch
        {
            bmp.Dispose();
            throw;
        }
    }

    /// <summary>用出现频率最高的量化颜色估计任务栏背景色（背景面积远大于图标）。</summary>
    public static Color EstimateBackground(Bitmap shot)
    {
        var hist = new Dictionary<int, int>();
        for (int y = 0; y < shot.Height; y += 2)
        {
            for (int x = 0; x < shot.Width; x += 2)
            {
                var p = shot.GetPixel(x, y);
                int key = (p.R >> 3 << 10) | (p.G >> 3 << 5) | (p.B >> 3);
                hist[key] = hist.GetValueOrDefault(key) + 1;
            }
        }

        if (hist.Count == 0) return Color.Black;

        int best = hist.MaxBy(kv => kv.Value).Key;
        return Color.FromArgb(
            ((best >> 10) & 0x1F) << 3,
            ((best >> 5) & 0x1F) << 3,
            (best & 0x1F) << 3);
    }

    /// <summary>
    /// 分析一个按钮矩形内的图标主色。<paramref name="buttonInShot"/> 是相对 <paramref name="shot"/> 的坐标。
    /// </summary>
    /// <remarks>
    /// 三个关键设计（都是实测踩坑后定下来的）：
    /// 1) 色相用 ±<see cref="HueWindow"/>° 的循环滑动窗口找累积权重最大处，而不是取直方图单个峰值分箱。
    ///    渐变类图标（如 Edge）的色相散布在几十度范围内，分箱取峰会被小面积杂色反超，判成完全错误的颜色。
    /// 2) 每个像素额外乘一个以图标中心为峰的高斯空间权重。未读消息角标位于图标角落，
    ///    否则会把整个图标的主色带偏（如 Teams 的紫色图标被右上角红色角标判成红色）。
    /// 3) 有彩/无彩把**无彩色区域也当成一个簇，跟各彩色簇比面积**。
    ///    黑底 + 一小块高饱和色的图标（MobaXterm）平均彩度并不低，但黑色面积才是主体；
    ///    而 Chrome 这种中心白圆 + 四块彩色的图标，白色占比达不到多数，仍归为彩色。
    /// </remarks>
    public static IconColor Analyze(Bitmap shot, Rectangle buttonInShot, Color background)
    {
        Rectangle core = CenterSquare(buttonInShot, CoreRatio);
        core.Intersect(new Rectangle(0, 0, shot.Width, shot.Height));
        if (core.Width <= 1 || core.Height <= 1)
            return Gray(0, background, 1);

        var area = new double[360];     // 面积（空间加权），决定主体占比
        var chroma = new double[360];   // 彩度质量，决定色相窗口落在哪
        var accR = new double[360];
        var accG = new double[360];
        var accB = new double[360];

        double cx = core.Left + core.Width / 2.0;
        double cy = core.Top + core.Height / 2.0;
        double sigma = Math.Max(core.Width, core.Height) * CenterSigmaRatio;
        double twoSigmaSq = 2 * sigma * sigma;

        double fgWeight = 0, achromaticWeight = 0, achromaticLuma = 0;

        for (int y = core.Top; y < core.Bottom; y++)
        {
            for (int x = core.Left; x < core.Right; x++)
            {
                var p = shot.GetPixel(x, y);
                if (ColorDistance(p, background) < BgDistanceThreshold) continue;

                double dx = x - cx, dy = y - cy;
                double spatial = Math.Exp(-(dx * dx + dy * dy) / twoSigmaSq);

                RgbToHsv(p, out double h, out double s, out double v);
                fgWeight += spatial;

                if (s < MinSat || v < MinVal)
                {
                    achromaticWeight += spatial;
                    achromaticLuma += v * spatial;
                    continue;
                }

                int deg = Math.Clamp((int)h, 0, 359);
                double w = s * v * spatial;
                area[deg] += spatial;
                chroma[deg] += w;
                accR[deg] += p.R * w;
                accG[deg] += p.G * w;
                accB[deg] += p.B * w;
            }
        }

        if (fgWeight <= 0) return Gray(0, background, 1);

        double achromatic = achromaticWeight / fgWeight;
        double grayLuma = achromaticWeight > 0 ? achromaticLuma / achromaticWeight : 0;

        var clusters = ExtractClusters(area, chroma, accR, accG, accB, fgWeight);

        // 主体色看面积：无彩色区域比最大的彩色簇还大，就说明图标主体是黑/白/灰。
        if (clusters.Length == 0 || achromatic > clusters[0].Share)
            return Gray(grayLuma, background, achromatic);

        var primary = clusters[0];
        RgbToHsv(primary.Representative, out double ph, out double ps, out double pv);
        return new IconColor(ph, ps, pv, false, primary.Representative, achromatic, clusters);
    }

    /// <summary>
    /// 反复取"±HueWindow 窗口内彩度和最大"的位置作为一簇，取完清零再找下一簇。
    /// 窗口落点用彩度（避免被大片淡色带跑），但簇的强弱用面积衡量，
    /// 所以最后要按面积重新排序——鲜艳但面积小的一小块不该当主体色。
    /// </summary>
    private static HueCluster[] ExtractClusters(
        double[] area, double[] chroma, double[] accR, double[] accG, double[] accB, double fgWeight)
    {
        var found = new List<HueCluster>();

        for (int n = 0; n < MaxClusters; n++)
        {
            int best = -1;
            double bestChroma = 0;
            for (int c = 0; c < 360; c++)
            {
                double sum = 0;
                for (int d = -HueWindow; d <= HueWindow; d++)
                    sum += chroma[(c + d + 360) % 360];
                if (sum > bestChroma) { bestChroma = sum; best = c; }
            }

            if (best < 0) break;

            double wSum = 0, aSum = 0, rSum = 0, gSum = 0, bSum = 0;
            for (int d = -HueWindow; d <= HueWindow; d++)
            {
                int i = (best + d + 360) % 360;
                aSum += area[i];
                wSum += chroma[i]; rSum += accR[i]; gSum += accG[i]; bSum += accB[i];
                area[i] = chroma[i] = accR[i] = accG[i] = accB[i] = 0;   // 清零，避免下一簇重复命中
            }

            if (wSum <= 0) break;

            double share = aSum / fgWeight;
            if (share < ClusterNoiseFloor) continue;

            var rep = Color.FromArgb(
                (int)Math.Clamp(rSum / wSum, 0, 255),
                (int)Math.Clamp(gSum / wSum, 0, 255),
                (int)Math.Clamp(bSum / wSum, 0, 255));

            RgbToHsv(rep, out double hue, out _, out _);
            found.Add(new HueCluster(hue, share, rep));
        }

        if (found.Count == 0) return [];

        found.Sort((a, b) => b.Share.CompareTo(a.Share));
        double top = found[0].Share;
        return [.. found.Where(c => c.Share >= top * MinClusterRatio)];
    }

    private static IconColor Gray(double luma, Color fallback, double achromatic)
    {
        int g8 = (int)Math.Clamp(luma * 255, 0, 255);
        var rep = luma > 0 ? Color.FromArgb(g8, g8, g8) : fallback;
        return new IconColor(0, 0, luma, true, rep, achromatic, []);
    }

    private static Rectangle CenterSquare(Rectangle r, double ratio)
    {
        int side = (int)(Math.Min(r.Width, r.Height) * ratio);
        if (side < 2) side = Math.Min(r.Width, r.Height);
        return new Rectangle(
            r.Left + (r.Width - side) / 2,
            r.Top + (r.Height - side) / 2,
            side, side);
    }

    private static double ColorDistance(Color a, Color b)
    {
        double dr = a.R - b.R, dg = a.G - b.G, db = a.B - b.B;
        return Math.Sqrt(dr * dr + dg * dg + db * db);
    }

    public static void RgbToHsv(Color c, out double h, out double s, out double v)
    {
        double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
        double max = Math.Max(r, Math.Max(g, b));
        double min = Math.Min(r, Math.Min(g, b));
        double delta = max - min;

        v = max;
        s = max <= 0 ? 0 : delta / max;

        if (delta <= 0) { h = 0; return; }

        if (max == r) h = 60 * (((g - b) / delta) % 6);
        else if (max == g) h = 60 * (((b - r) / delta) + 2);
        else h = 60 * (((r - g) / delta) + 4);

        if (h < 0) h += 360;
    }
}
