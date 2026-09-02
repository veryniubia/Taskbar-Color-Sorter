using System.Drawing;

namespace TaskbarProbe;

internal static class Drag
{
    /// <summary>用户按下 ESC 时抛出，调用方负责恢复鼠标状态。</summary>
    public sealed class AbortedException : Exception
    {
        public AbortedException() : base("用户按 ESC 中止。") { }
    }

    /// <summary>
    /// 把 <paramref name="source"/> 按钮拖到任务栏上的目标 X 位置。
    /// Y 坐标全程钳制在任务栏矩形内，避免拖出任务栏触发"取消固定"等副作用。
    /// </summary>
    public static void DragButton(Rectangle source, int targetCenterX, Rectangle taskbarBounds)
    {
        int y = taskbarBounds.Top + taskbarBounds.Height / 2;
        int startX = source.Left + source.Width / 2;
        int endX = Math.Clamp(targetCenterX, taskbarBounds.Left + 4, taskbarBounds.Right - 5);

        var origin = GetCursor();
        bool buttonDown = false;

        try
        {
            Native.MoveMouseTo(startX, y);
            Sleep(80);

            Native.LeftDown();
            buttonDown = true;
            Sleep(140);

            // 先做几步小位移越过系统的拖拽启动阈值
            int dir = endX >= startX ? 1 : -1;
            for (int i = 1; i <= 3; i++)
            {
                Native.MoveMouseTo(startX + dir * i * 5, y);
                Sleep(40);
            }

            int distance = Math.Abs(endX - startX);
            int steps = Math.Clamp(distance / 8, 12, 60);
            for (int i = 1; i <= steps; i++)
            {
                ThrowIfAborted();
                int x = startX + (int)Math.Round((endX - startX) * (double)i / steps);
                Native.MoveMouseTo(Math.Clamp(x, taskbarBounds.Left + 4, taskbarBounds.Right - 5), y);
                Sleep(14);
            }

            Sleep(220);   // 等任务栏的重排动画跟上
            Native.LeftUp();
            buttonDown = false;
            Sleep(380);
        }
        finally
        {
            if (buttonDown)
            {
                try { Native.LeftUp(); } catch { /* 兜底：无论如何都要松开鼠标键 */ }
            }
            try { Native.MoveMouseTo(origin.X, origin.Y); } catch { }
        }
    }

    private static void ThrowIfAborted()
    {
        if (Native.EscapePressed()) throw new AbortedException();
    }

    private static Point GetCursor()
        => Native.GetCursorPos(out var p) ? new Point(p.X, p.Y) : Point.Empty;

    private static void Sleep(int ms) => Thread.Sleep(ms);
}
