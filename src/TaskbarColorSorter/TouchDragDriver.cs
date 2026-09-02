using System.Drawing;
using System.Runtime.InteropServices;

namespace TaskbarColorSorter;

/// <summary>
/// 首选驱动：用合成指针设备（<c>CreateSyntheticPointerDevice</c> / <c>InjectSyntheticPointerInput</c>，
/// Win10 1809+ 公开 API）注入触摸拖拽。
/// 相比 SendInput 的决定性优势：<b>不会移动用户的真实鼠标指针</b>，排序期间用户可以继续正常操作电脑。
/// </summary>
internal sealed class TouchDragDriver : IDragDriver
{
    public string Name => "合成触摸指针";

    private const uint PT_TOUCH = 2;
    private const uint POINTER_FEEDBACK_NONE = 3;

    private const uint POINTER_FLAG_INRANGE = 0x00000002;
    private const uint POINTER_FLAG_INCONTACT = 0x00000004;
    private const uint POINTER_FLAG_DOWN = 0x00010000;
    private const uint POINTER_FLAG_UPDATE = 0x00020000;
    private const uint POINTER_FLAG_UP = 0x00040000;

    private const uint TOUCH_MASK_CONTACTAREA = 0x00000001;
    private const uint TOUCH_MASK_PRESSURE = 0x00000004;

    private const uint ContactPressure = 32000;
    private const int ContactRadius = 4;

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

    private IntPtr _device;

    private TouchDragDriver(IntPtr device) => _device = device;

    /// <summary>创建驱动；系统不支持合成指针设备时返回 null，由调用方回退到鼠标驱动。</summary>
    public static TouchDragDriver? TryCreate()
    {
        try
        {
            IntPtr device = CreateSyntheticPointerDevice(PT_TOUCH, 1, POINTER_FEEDBACK_NONE);
            return device == IntPtr.Zero ? null : new TouchDragDriver(device);
        }
        catch (EntryPointNotFoundException)
        {
            return null;   // < Win10 1809
        }
        catch (DllNotFoundException)
        {
            return null;
        }
    }

    public void Drag(Rectangle source, int targetX, Rectangle taskbar, CancellationToken ct)
    {
        int y = DragGeometry.CenterY(taskbar);
        int startX = DragGeometry.ClampX(source.Left + source.Width / 2, taskbar);
        int endX = DragGeometry.ClampX(targetX, taskbar);

        bool inContact = false;
        try
        {
            Inject(startX, y, POINTER_FLAG_DOWN | POINTER_FLAG_INRANGE | POINTER_FLAG_INCONTACT);
            inContact = true;
            Thread.Sleep(DragTiming.AfterPress);

            // 先做几步小位移越过系统的拖拽启动阈值，否则会被识别成单击
            int dir = endX >= startX ? 1 : -1;
            for (int i = 1; i <= DragTiming.ThresholdSteps; i++)
            {
                Update(DragGeometry.ClampX(startX + dir * i * DragTiming.ThresholdStepPixels, taskbar), y);
                Thread.Sleep(DragTiming.ThresholdStepDelay);
            }

            int steps = DragTiming.StepCount(Math.Abs(endX - startX));
            for (int i = 1; i <= steps; i++)
            {
                DragGeometry.ThrowIfAborted(ct);
                int x = startX + (int)Math.Round((endX - startX) * (double)i / steps);
                Update(DragGeometry.ClampX(x, taskbar), y);
                Thread.Sleep(DragTiming.MoveStepDelay);
            }

            Thread.Sleep(DragTiming.BeforeRelease);
            Inject(endX, y, POINTER_FLAG_UP);
            inContact = false;
            Thread.Sleep(DragTiming.AfterRelease);
        }
        finally
        {
            if (inContact)
            {
                // 无论如何都要抬起来，否则系统会一直认为有一根手指按在任务栏上
                try { Inject(endX, y, POINTER_FLAG_UP); } catch { }
            }
        }
    }

    private void Update(int x, int y)
        => Inject(x, y, POINTER_FLAG_UPDATE | POINTER_FLAG_INRANGE | POINTER_FLAG_INCONTACT);

    private void Inject(int x, int y, uint flags)
    {
        var info = new[]
        {
            new POINTER_TYPE_INFO
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
                    touchMask = TOUCH_MASK_CONTACTAREA | TOUCH_MASK_PRESSURE,
                    rcContact = new RECT
                    {
                        Left = x - ContactRadius,
                        Top = y - ContactRadius,
                        Right = x + ContactRadius,
                        Bottom = y + ContactRadius,
                    },
                    pressure = ContactPressure,
                }
            }
        };

        if (!InjectSyntheticPointerInput(_device, info, 1))
            throw new TaskbarException($"注入触摸事件失败, Win32Error={Marshal.GetLastWin32Error()}");
    }

    public void Dispose()
    {
        if (_device != IntPtr.Zero)
        {
            DestroySyntheticPointerDevice(_device);
            _device = IntPtr.Zero;
        }
    }
}
