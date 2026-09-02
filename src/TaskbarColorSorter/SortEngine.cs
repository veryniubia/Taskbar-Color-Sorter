using System.Drawing;

namespace TaskbarColorSorter;

internal enum SortOutcome { Sorted, AlreadySorted, NothingToDo, Aborted, Failed }

internal sealed record SortResult(SortOutcome Outcome, int Moves, string Message);

internal readonly record struct ColoredButton(TaskbarButton Button, IconColor Color);

/// <summary>
/// 排序流程编排：枚举 → 提色 → 算目标顺序 → 逐个拖到位。
/// </summary>
internal sealed class SortEngine
{
    /// <summary>每一步拖拽失败后的重试次数。</summary>
    private const int RetriesPerStep = 1;

    /// <summary>拖拽后等任务栏动画稳定的时间。</summary>
    private const int SettleAfterDrag = 120;

    public event Action<int, int, string>? Progress;

    public SortResult Run(CancellationToken ct)
    {
        try
        {
            return RunCore(ct);
        }
        catch (SortAbortedException)
        {
            return new SortResult(SortOutcome.Aborted, 0, "已中止，任务栏可能停在中间状态。");
        }
        catch (TaskbarException ex)
        {
            return new SortResult(SortOutcome.Failed, 0, ex.Message);
        }
    }

    private SortResult RunCore(CancellationToken ct)
    {
        if (Native.IsTaskbarAutoHide())
            return new SortResult(SortOutcome.Failed, 0,
                "任务栏设置了自动隐藏，无法定位图标。请先关闭自动隐藏。");

        Rectangle taskbar = TaskbarScanner.GetTaskbarBounds();
        var initial = TaskbarScanner.GetAppButtons();

        if (initial.Count < 2)
            return new SortResult(SortOutcome.NothingToDo, 0, "任务栏上不足两个图标，无需排序。");

        List<TaskbarButton> target = ComputeTargetOrder(initial, taskbar);

        if (initial.Select(b => b.Key).SequenceEqual(target.Select(b => b.Key)))
            return new SortResult(SortOutcome.AlreadySorted, 0, $"{initial.Count} 个图标已经是按颜色排好的。");

        int moves = 0;

        // 选择排序：从左往右逐个把"该在第 i 位"的图标拖过来。
        // 因为前 i 项已经就位，要拖的项一定在 i 右侧，所以永远是向左拖 —— 与实测通过的拖拽方向一致。
        for (int i = 0; i < target.Count - 1; i++)
        {
            DragGeometry.ThrowIfAborted(ct);

            var current = TaskbarScanner.GetAppButtons();
            if (current.Count != initial.Count)
                return new SortResult(SortOutcome.Aborted, moves,
                    $"排序中途任务栏图标数量变了（有程序启动或退出），已停在第 {i} 步。");

            int from = IndexOf(current, target[i]);
            if (from < 0)
                return new SortResult(SortOutcome.Aborted, moves,
                    $"排序中途有图标消失了，已停在第 {i} 步。");

            if (from == i) continue;

            Progress?.Invoke(i + 1, target.Count, target[i].Name);

            if (!MoveInto(current, target[i], i, taskbar, ct))
                return new SortResult(SortOutcome.Failed, moves,
                    $"第 {i + 1} 步拖拽没有生效，已停止。任务栏可能停在中间状态。");

            moves++;
        }

        return new SortResult(SortOutcome.Sorted, moves, $"{initial.Count} 个图标已按颜色排好，共移动 {moves} 次。");
    }

    /// <summary>把 <paramref name="item"/> 拖到第 <paramref name="to"/> 个位置，并验证结果。</summary>
    private bool MoveInto(
        List<TaskbarButton> current, TaskbarButton item, int to,
        Rectangle taskbar, CancellationToken ct)
    {
        for (int attempt = 0; attempt <= RetriesPerStep; attempt++)
        {
            DragGeometry.ThrowIfAborted(ct);

            // 每次尝试都重新采样位置：上一次失败的拖拽可能已经把布局搓乱了。
            var snapshot = attempt == 0 ? current : TaskbarScanner.GetAppButtons();
            int from = IndexOf(snapshot, item);
            if (from < 0) return false;
            if (from == to) return true;
            if (to >= snapshot.Count) return false;

            // 落点取第 to 个槽位的左边缘：向左拖时落在该槽位之前，正好成为第 to 项。
            _driver.Drag(snapshot[from].Bounds, snapshot[to].Bounds.Left, taskbar, ct);
            Thread.Sleep(SettleAfterDrag);

            if (IndexOf(TaskbarScanner.GetAppButtons(), item) == to)
                return true;
        }

        return false;
    }

    private List<TaskbarButton> ComputeTargetOrder(List<TaskbarButton> buttons, Rectangle taskbar)
    {
        // 每次排序都重新截屏取色，不做缓存：图标可能带角标、可能随系统主题变化。
        using var shot = ColorExtract.CaptureScreen(taskbar);
        Color background = ColorExtract.EstimateBackground(shot);

        var colored = buttons.Select(b => new ColoredButton(
            b,
            ColorExtract.Analyze(shot, ToShotCoordinates(b.Bounds, taskbar), background)));

        return ColorSort.Order(colored, x => x.Color).Select(x => x.Button).ToList();
    }

    private static Rectangle ToShotCoordinates(Rectangle screenRect, Rectangle taskbar)
        => screenRect with { X = screenRect.X - taskbar.Left, Y = screenRect.Y - taskbar.Top };

    /// <summary>
    /// 定位按钮：优先用 UIA RuntimeId；万一任务栏重建了 XAML peer 导致 RuntimeId 变化，
    /// 则回退到按名称匹配——但只在名称唯一时才用，避免在"从不合并"模式下认错同名按钮。
    /// </summary>
    private static int IndexOf(List<TaskbarButton> buttons, TaskbarButton item)
    {
        int byKey = buttons.FindIndex(b => b.Key == item.Key);
        if (byKey >= 0) return byKey;

        var sameName = buttons.Where(b => b.Name == item.Name).ToList();
        return sameName.Count == 1 ? buttons.IndexOf(sameName[0]) : -1;
    }

    private readonly IDragDriver _driver;

    public SortEngine(IDragDriver driver) => _driver = driver;

    public static IDragDriver CreateDriver()
        => (IDragDriver?)TouchDragDriver.TryCreate() ?? new MouseDragDriver();
}
