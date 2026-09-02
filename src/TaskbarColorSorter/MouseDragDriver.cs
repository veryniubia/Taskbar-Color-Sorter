using System.Drawing;

namespace TaskbarColorSorter;

/// <summary>
/// 兜底驱动：用 SendInput 模拟鼠标拖拽。
/// 只在系统不支持合成指针设备时使用——它会<b>接管真实鼠标指针</b>，期间用户不能操作电脑。
/// </summary>
internal sealed class MouseDragDriver : IDragDriver
{
    public string Name => "SendInput 鼠标";

    public void Drag(Rectangle source, int targetX, Rectangle taskbar, CancellationToken ct)
    {
        int y = DragGeometry.CenterY(taskbar);
        int startX = DragGeometry.ClampX(source.Left + source.Width / 2, taskbar);
        int endX = DragGeometry.ClampX(targetX, taskbar);

        Point origin = Native.GetCursorPos(out var p) ? new Point(p.X, p.Y) : Point.Empty;
        bool buttonDown = false;

        try
        {
            Native.MoveMouseTo(startX, y);
            Thread.Sleep(80);

            Native.LeftDown();
            buttonDown = true;
            Thread.Sleep(DragTiming.AfterPress);

            int dir = endX >= startX ? 1 : -1;
            for (int i = 1; i <= DragTiming.ThresholdSteps; i++)
            {
                Native.MoveMouseTo(DragGeometry.ClampX(startX + dir * i * DragTiming.ThresholdStepPixels, taskbar), y);
                Thread.Sleep(DragTiming.ThresholdStepDelay);
            }

            int steps = DragTiming.StepCount(Math.Abs(endX - startX));
            for (int i = 1; i <= steps; i++)
            {
                DragGeometry.ThrowIfAborted(ct);
                int x = startX + (int)Math.Round((endX - startX) * (double)i / steps);
                Native.MoveMouseTo(DragGeometry.ClampX(x, taskbar), y);
                Thread.Sleep(DragTiming.MoveStepDelay);
            }

            Thread.Sleep(DragTiming.BeforeRelease);
            Native.LeftUp();
            buttonDown = false;
            Thread.Sleep(DragTiming.AfterRelease);
        }
        finally
        {
            if (buttonDown)
            {
                // 兜底：绝不能让鼠标左键停在按下状态
                try { Native.LeftUp(); } catch { }
            }
            if (origin != Point.Empty)
            {
                try { Native.MoveMouseTo(origin.X, origin.Y); } catch { }
            }
        }
    }

    public void Dispose() { }
}
