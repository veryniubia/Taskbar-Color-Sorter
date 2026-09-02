using System.Drawing;

namespace TaskbarColorSorter;

/// <summary>把任务栏按钮拖到指定横坐标的执行器。</summary>
internal interface IDragDriver : IDisposable
{
    string Name { get; }

    /// <summary>
    /// 把 <paramref name="source"/> 按钮拖到任务栏上的目标 X 位置。
    /// 实现必须把 Y 坐标钳制在 <paramref name="taskbar"/> 内，并保证异常退出时释放"按下"状态。
    /// </summary>
    void Drag(Rectangle source, int targetX, Rectangle taskbar, CancellationToken ct);
}

/// <summary>用户按 ESC 或点了取消。</summary>
internal sealed class SortAbortedException() : Exception("已中止。");

internal static class DragTiming
{
    public const int AfterPress = 130;
    public const int ThresholdSteps = 4;
    public const int ThresholdStepPixels = 4;
    public const int ThresholdStepDelay = 40;
    public const int MoveStepDelay = 14;
    public const int BeforeRelease = 200;
    public const int AfterRelease = 320;

    public static int StepCount(int distance) => Math.Clamp(distance / 8, 12, 60);
}

internal static class DragGeometry
{
    /// <summary>拖拽路径必须完全落在任务栏内，否则可能触发"取消固定"等副作用。</summary>
    public static int ClampX(int x, Rectangle taskbar)
        => Math.Clamp(x, taskbar.Left + 4, taskbar.Right - 5);

    public static int CenterY(Rectangle taskbar) => taskbar.Top + taskbar.Height / 2;

    public static void ThrowIfAborted(CancellationToken ct)
    {
        if (ct.IsCancellationRequested || Native.EscapePressed())
            throw new SortAbortedException();
    }
}
