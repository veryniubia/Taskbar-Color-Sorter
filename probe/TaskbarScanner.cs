using System.Drawing;
using System.Text;
using System.Windows.Automation;

namespace TaskbarProbe;

/// <summary>任务栏上一个可排序的应用按钮。</summary>
internal sealed record TaskbarButton(
    string Name,
    string AutomationId,
    string ClassName,
    Rectangle Bounds,
    AutomationElement Element)
{
    public Point Center => new(Bounds.Left + Bounds.Width / 2, Bounds.Top + Bounds.Height / 2);
    public override string ToString() => $"{Name} @[{Bounds.Left},{Bounds.Top} {Bounds.Width}x{Bounds.Height}]";
}

internal static class TaskbarScanner
{
    /// <summary>已知的、长得像应用按钮但不该参与排序的项（按 Name 匹配，中英双语）。</summary>
    private static readonly string[] ExcludedNames =
    [
        "任务视图", "Task View",
        "小组件", "Widgets",
        "搜索", "Search",
        "开始", "Start",
        "Copilot",
        "聊天", "Chat",
        "显示桌面", "Show desktop",
    ];

    public static IntPtr FindPrimaryTaskbarHandle()
    {
        IntPtr h = Native.FindWindow("Shell_TrayWnd", null);
        if (h == IntPtr.Zero)
            throw new InvalidOperationException("找不到主任务栏窗口 (Shell_TrayWnd)。");
        return h;
    }

    public static Rectangle GetTaskbarBounds()
    {
        IntPtr h = FindPrimaryTaskbarHandle();
        if (!Native.GetWindowRect(h, out var r))
            throw new InvalidOperationException("GetWindowRect(Shell_TrayWnd) 失败。");
        return Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom);
    }

    public static AutomationElement GetTaskbarElement()
        => AutomationElement.FromHandle(FindPrimaryTaskbarHandle())
           ?? throw new InvalidOperationException("无法从 Shell_TrayWnd 取得 AutomationElement。");

    /// <summary>把整棵 UIA 原始树 dump 成文本，用于人工核对结构。</summary>
    public static string DumpTree(AutomationElement root, int maxDepth = 10)
    {
        var sb = new StringBuilder();
        var walker = TreeWalker.RawViewWalker;

        void Visit(AutomationElement el, int depth)
        {
            if (depth > maxDepth) return;

            string name, cls, ctrl, autoId, rect;
            try
            {
                var c = el.Current;
                name = c.Name ?? "";
                cls = c.ClassName ?? "";
                ctrl = c.ControlType?.ProgrammaticName?.Replace("ControlType.", "") ?? "?";
                autoId = c.AutomationId ?? "";
                var b = c.BoundingRectangle;
                rect = b.IsEmpty ? "-" : $"{(int)b.Left},{(int)b.Top} {(int)b.Width}x{(int)b.Height}";
            }
            catch (ElementNotAvailableException) { return; }

            sb.Append(new string(' ', depth * 2))
              .Append($"[{ctrl}] cls=\"{cls}\" id=\"{autoId}\" rect={rect} name=\"{Truncate(name, 60)}\"")
              .AppendLine();

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
        return sb.ToString();
    }

    /// <summary>枚举主屏任务栏上可排序的应用按钮，按屏幕 X 坐标从左到右排序。</summary>
    public static List<TaskbarButton> GetAppButtons()
    {
        var taskbar = GetTaskbarElement();
        var all = CollectButtons(taskbar);

        // Win11: 应用按钮的 peer 类名统一是 Taskbar.TaskListButtonAutomationPeer
        var win11 = all.Where(b => b.ClassName.Contains("TaskListButton", StringComparison.Ordinal)).ToList();
        var candidates = win11.Count > 0 ? win11 : CollectWin10Buttons();

        return candidates
            .Where(b => b.Bounds.Width > 0 && b.Bounds.Height > 0)
            .Where(b => !ExcludedNames.Any(x => b.Name.Equals(x, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(b => b.Bounds.Left)
            .ToList();
    }

    private static List<TaskbarButton> CollectWin10Buttons()
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

    private static List<TaskbarButton> CollectButtons(AutomationElement root)
    {
        var result = new List<TaskbarButton>();
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
                    result.Add(new TaskbarButton(
                        c.Name ?? "",
                        c.AutomationId ?? "",
                        c.ClassName ?? "",
                        Rectangle.FromLTRB((int)b.Left, (int)b.Top, (int)b.Right, (int)b.Bottom),
                        el));
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

    private static string Truncate(string s, int max)
        => s.Length <= max ? s : s[..max] + "…";
}
