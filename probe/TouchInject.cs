using System.Runtime.InteropServices;
using System.Drawing;

namespace TaskbarProbe;

/// <summary>
/// 用合成指针设备（CreateSyntheticPointerDevice / InjectSyntheticPointerInput，Win10 1809+）
/// 注入触摸拖拽。相比 SendInput 的优势：<b>不会移动用户的真实鼠标指针</b>。
/// </summary>
internal static class TouchInject
{
    private const uint PT_TOUCH = 2;
    private const uint POINTER_FEEDBACK_NONE = 3;

    private const uint POINTER_FLAG_INRANGE = 0x00000002;
    private const uint POINTER_FLAG_INCONTACT = 0x00000004;
    private const uint POINTER_FLAG_DOWN = 0x00010000;
    private const uint POINTER_FLAG_UPDATE = 0x00020000;
    private const uint POINTER_FLAG_UP = 0x00040000;

    private const uint TOUCH_MASK_CONTACTAREA = 0x00000001;
    private const uint TOUCH_MASK_PRESSURE = 0x00000004;

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINTER_INFO
    {
        public uint pointerType;
        public uint pointerId;
        public uint frameId;
        public uint pointerFlags;
        public IntPtr sourceDevice;
        public IntPtr hwndTarget;
        public POINT ptPixelLocation;
        public POINT ptHimetricLocation;
        public POINT ptPixelLocationRaw;
        public POINT ptHimetricLocationRaw;
        public uint dwTime;
        public uint historyCount;
        public int InputData;
        public uint dwKeyStates;
        public ulong PerformanceCount;
        public int ButtonChangeType;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINTER_TOUCH_INFO
    {
        public POINTER_INFO pointerInfo;
        public uint touchFlags;
        public uint touchMask;
        public RECT rcContact;
        public RECT rcContactRaw;
        public uint orientation;
        public uint pressure;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct POINTER_TYPE_INFO
    {
        [FieldOffset(0)] public uint type;
        [FieldOffset(8)] public POINTER_TOUCH_INFO touchInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr CreateSyntheticPointerDevice(uint pointerType, uint maxCount, uint mode);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool InjectSyntheticPointerInput(IntPtr device, POINTER_TYPE_INFO[] info, uint count);

    [DllImport("user32.dll")]
    private static extern void DestroySyntheticPointerDevice(IntPtr device);

    private static POINTER_TYPE_INFO MakeContact(int x, int y, uint flags)
    {
        const int r = 4;
        return new POINTER_TYPE_INFO
        {
            type = PT_TOUCH,
            touchInfo = new POINTER_TOUCH_INFO
            {
                pointerInfo = new POINTER_INFO
                {
                    pointerType = PT_TOUCH,
                    pointerId = 0,
                    pointerFlags = flags,
                    ptPixelLocation = new POINT { X = x, Y = y },
                },
                touchFlags = 0,
                touchMask = TOUCH_MASK_CONTACTAREA | TOUCH_MASK_PRESSURE,
                rcContact = new RECT { Left = x - r, Top = y - r, Right = x + r, Bottom = y + r },
                orientation = 0,
                pressure = 32000,
            }
        };
    }

    /// <summary>用合成触摸把 <paramref name="source"/> 拖到目标 X。不触碰真实鼠标指针。</summary>
    public static void DragButton(Rectangle source, int targetCenterX, Rectangle taskbarBounds)
    {
        IntPtr device = CreateSyntheticPointerDevice(PT_TOUCH, 1, POINTER_FEEDBACK_NONE);
        if (device == IntPtr.Zero)
            throw new InvalidOperationException(
                $"CreateSyntheticPointerDevice 失败, Win32Error={Marshal.GetLastWin32Error()}");

        int y = taskbarBounds.Top + taskbarBounds.Height / 2;
        int startX = source.Left + source.Width / 2;
        int endX = Math.Clamp(targetCenterX, taskbarBounds.Left + 4, taskbarBounds.Right - 5);

        bool down = false;
        try
        {
            Inject(device, startX, y, POINTER_FLAG_DOWN | POINTER_FLAG_INRANGE | POINTER_FLAG_INCONTACT);
            down = true;
            Thread.Sleep(160);

            // 长按一小会儿并做微小位移，越过系统的拖拽启动阈值
            int dir = endX >= startX ? 1 : -1;
            for (int i = 1; i <= 4; i++)
            {
                Update(device, startX + dir * i * 4, y);
                Thread.Sleep(50);
            }

            int steps = Math.Clamp(Math.Abs(endX - startX) / 8, 12, 60);
            for (int i = 1; i <= steps; i++)
            {
                if (Native.EscapePressed()) throw new Drag.AbortedException();
                int x = startX + (int)Math.Round((endX - startX) * (double)i / steps);
                Update(device, Math.Clamp(x, taskbarBounds.Left + 4, taskbarBounds.Right - 5), y);
                Thread.Sleep(16);
            }

            Thread.Sleep(220);
            Inject(device, endX, y, POINTER_FLAG_UP);
            down = false;
            Thread.Sleep(380);
        }
        finally
        {
            if (down)
            {
                try { Inject(device, endX, y, POINTER_FLAG_UP); } catch { }
            }
            DestroySyntheticPointerDevice(device);
        }
    }

    private static void Update(IntPtr device, int x, int y)
        => Inject(device, x, y, POINTER_FLAG_UPDATE | POINTER_FLAG_INRANGE | POINTER_FLAG_INCONTACT);

    private static void Inject(IntPtr device, int x, int y, uint flags)
    {
        var info = new[] { MakeContact(x, y, flags) };
        if (!InjectSyntheticPointerInput(device, info, 1))
            throw new InvalidOperationException(
                $"InjectSyntheticPointerInput 失败, Win32Error={Marshal.GetLastWin32Error()}");
    }
}
