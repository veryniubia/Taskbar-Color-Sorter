using System.Drawing;
using System.Windows.Automation;

namespace TaskbarColorSorter;

/// <summary>任务栏上一个可排序的应用按钮。</summary>
internal sealed class TaskbarButton
{
    public required string Name { get; init; }
    public required Rectangle Bounds { get; init; }
    public required AutomationElement Element { get; init; }

    /// <summary>
    /// UIA RuntimeId 拼成的稳定标识。重排过程中元素本身不变，所以它比 Name 可靠
    /// （"从不合并"模式下多个窗口按钮可能重名）。
    /// </summary>
    public required string Key { get; init; }

    public override string ToString() => Name;
}

internal static class TaskbarScanner
{
    public static IntPtr FindPrimaryTaskbarHandle()
    {
        IntPtr h = Native.FindWindow("Shell_TrayWnd", null);
        if (h == IntPtr.Zero)
            throw new TaskbarException("找不到主任务栏窗口 (Shell_TrayWnd)。");
        return h;
    }

    public static Rectangle GetTaskbarBounds()
    {
        if (!Native.GetWindowRect(FindPrimaryTaskbarHandle(), out var r))
            throw new TaskbarException("读取任务栏位置失败。");

        var bounds = Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom);
        if (bounds.Width < 8 || bounds.Height < 8)
            throw new TaskbarException("任务栏尺寸异常，无法操作。");
        return bounds;
    }

    /// <summary>枚举主屏任务栏上可排序的应用按钮，按屏幕 X 坐标从左到右排序。</summary>
    public static List<TaskbarButton> GetAppButtons()
    {
        var taskbar = AutomationElement.FromHandle(FindPrimaryTaskbarHandle())
            ?? throw new TaskbarException("无法访问任务栏的 UI Automation 树。");

        var all = CollectButtons(taskbar);

        // Win11：应用按钮的 peer 类名统一是 Taskbar.TaskListButtonAutomationPeer。
        // 开始/搜索/小组件/输入法/系统托盘用的是别的 peer，天然被排除，不需要维护黑名单。
        var win11 = all.Where(x => x.ClassName.Contains("TaskListButton", StringComparison.Ordinal)).ToList();
        var candidates = win11.Count > 0 ? win11 : CollectWin10Buttons();

        return candidates
            .Where(x => x.Button.Bounds.Width > 0 && x.Button.Bounds.Height > 0)
            .Select(x => x.Button)
            .OrderBy(b => b.Bounds.Left)
            .ToList();
    }

    private static List<(TaskbarButton Button, string ClassName)> CollectWin10Buttons()
    {
        // Win10 布局：Shell_TrayWnd > ReBarWindow32 > MSTaskSwWClass > MSTaskListWClass
        IntPtr tray = FindPrimaryTaskbarHandle();
        IntPtr rebar = Native.FindWindowEx(tray, IntPtr.Zero, "ReBarWindow32", null);
        IntPtr taskSw = rebar != IntPtr.Zero ? Native.FindWindowEx(rebar, IntPtr.Zero, "MSTaskSwWClass", null) : IntPtr.Zero;
        IntPtr taskList = taskSw != IntPtr.Zero ? Native.FindWindowEx(taskSw, IntPtr.Zero, "MSTaskListWClass", null) : IntPtr.Zero;
        if (taskList == IntPtr.Zero) return [];

        var el = AutomationElement.FromHandle(taskList);
        return el == null ? [] : CollectButtons(el);
    }

    private static List<(TaskbarButton Button, string ClassName)> CollectButtons(AutomationElement root)
    {
        var result = new List<(TaskbarButton, string)>();
        var walker = TreeWalker.RawViewWalker;

        void Visit(AutomationElement el, int depth)
        {
            if (depth > 12) return;

            try
            {
                var c = el.Current;
                if (Equals(c.ControlType, ControlType.Button))
                {
                    var b = c.BoundingRectangle;
                    result.Add((new TaskbarButton
                    {
                        Name = c.Name ?? "",
                        Bounds = Rectangle.FromLTRB((int)b.Left, (int)b.Top, (int)b.Right, (int)b.Bottom),
                        Element = el,
                        Key = MakeKey(el, c.Name ?? ""),
                    }, c.ClassName ?? ""));
                }
            }
            catch (ElementNotAvailableException) { return; }

            AutomationElement? child;
            try { child = walker.GetFirstChild(el); }
            catch (ElementNotAvailableException) { return; }

            while (child != null)
            {
                Visit(child, depth + 1);
                try { child = walker.GetNextSibling(child); }
                catch (ElementNotAvailableException) { break; }
            }
        }

        Visit(root, 0);
        return result;
    }

    private static string MakeKey(AutomationElement el, string fallbackName)
    {
        try
        {
            int[] id = el.GetRuntimeId();
            if (id.Length > 0) return "rid:" + string.Join('.', id);
        }
        catch (ElementNotAvailableException) { }
        return "name:" + fallbackName;
    }
}

internal sealed class TaskbarException(string message) : Exception(message);
