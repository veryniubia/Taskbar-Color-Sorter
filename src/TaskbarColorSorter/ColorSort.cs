namespace TaskbarColorSorter;

internal static class ColorSort
{
    /// <summary>
    /// 有彩色项按色相升序排成彩虹（同色相按饱和度降序，让颜色更"实"的排前面）；
    /// 无彩色项没有色相，按明度由深到浅单独成段接在末尾。
    /// </summary>
    /// <remarks>
    /// 曾试过"多色图标参考左右邻居选插入位"，实测有害：代价函数只奖励与邻居配色接近，
    /// 没有把图标拉回自身主色区的约束，导致带红色角标的 Teams 被插进红色区、
    /// 绿色的 Git Extensions 被插进 FileZilla 和 Acrobat 之间。
    /// 而且"次色/主色面积比"区分不出真多色图标和带角标的图标（Teams 0.63 反而高于 Chrome 0.57）。
    /// 严格按主色相排序能保证彩虹单调，任何图标都不会掉进错误的色区。
    /// </remarks>
    public static List<T> Order<T>(IEnumerable<T> items, Func<T, IconColor> colorOf)
    {
        var list = items.ToList();

        var colored = list.Where(x => !colorOf(x).IsGray)
                          .OrderBy(x => colorOf(x).Hue)
                          .ThenByDescending(x => colorOf(x).Saturation);

        var gray = list.Where(x => colorOf(x).IsGray)
                       .OrderBy(x => colorOf(x).Value);

        return colored.Concat(gray).ToList();
    }
}
