using System.Drawing;
using System.IO;
using System.Text;
using TaskbarColorSorter;

namespace TaskbarProbe;

internal static class Program
{
    private static readonly string OutDir =
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "out");

    [STAThread]
    private static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        Native.EnablePerMonitorDpiAwareness();
        Directory.CreateDirectory(OutDir);

        string cmd = args.Length > 0 ? args[0].ToLowerInvariant() : "all";

        try
        {
            switch (cmd)
            {
                case "dump": CmdDump(); break;
                case "colors": CmdColors(); break;
                case "patterns": CmdPatterns(); break;
                case "dragtest": CmdDragTest("SendInput 鼠标", Drag.DragButton); break;
                case "touchdrag": CmdDragTest("合成触摸指针", TouchInject.DragButton); break;
                case "all": CmdDump(); Console.WriteLine(); CmdColors(); break;
                default:
                    Console.WriteLine("用法: TaskbarProbe [dump|colors|patterns|dragtest|touchdrag|all]");
                    return 2;
            }
            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine();
            Console.WriteLine($"!! 失败: {ex.GetType().Name}: {ex.Message}");
            Console.WriteLine(ex.StackTrace);
            return 1;
        }
    }

    // ---------- 步骤 1：UIA 枚举 ----------
    private static void CmdDump()
    {
        Header("步骤 1 / UIA 枚举");

        var bounds = TaskbarScanner.GetTaskbarBounds();
        Console.WriteLine($"任务栏矩形 : {bounds.Left},{bounds.Top} {bounds.Width}x{bounds.Height}");

        var root = TaskbarScanner.GetTaskbarElement();
        string tree = TaskbarScanner.DumpTree(root);
        string treePath = Path.GetFullPath(Path.Combine(OutDir, "uia-tree.txt"));
        File.WriteAllText(treePath, tree, Encoding.UTF8);
        Console.WriteLine($"完整 UIA 树 : {treePath}  ({tree.Split('\n').Length} 行)");

        var buttons = TaskbarScanner.GetAppButtons();
        Console.WriteLine();
        Console.WriteLine($"识别到 {buttons.Count} 个可排序应用按钮（左 → 右）:");
        Console.WriteLine("  #   X     W    ClassName                                  Name");
        for (int i = 0; i < buttons.Count; i++)
        {
            var b = buttons[i];
            Console.WriteLine($"  {i,-3} {b.Bounds.Left,-5} {b.Bounds.Width,-4} {Pad(b.ClassName, 42)} {b.Name}");
        }

        if (buttons.Count == 0)
            Console.WriteLine("  (空 —— 枚举方案在这台机器上不成立，需要换思路)");
    }

    // ---------- 步骤 2：截屏提色 ----------
    private static void CmdColors()
    {
        Header("步骤 2 / 截屏提色");

        var taskbar = TaskbarScanner.GetTaskbarBounds();
        var buttons = TaskbarScanner.GetAppButtons();
        if (buttons.Count == 0) { Console.WriteLine("没有按钮可分析。"); return; }

        using var shot = ColorExtract.CaptureScreen(taskbar);
        var bg = ColorExtract.EstimateBackground(shot);
        Console.WriteLine($"任务栏背景色估计 : RGB({bg.R},{bg.G},{bg.B})");
        Console.WriteLine();

        var results = new List<(TaskbarButton Btn, IconColor Col)>();
        foreach (var b in buttons)
        {
            var local = b.Bounds with { X = b.Bounds.X - taskbar.Left, Y = b.Bounds.Y - taskbar.Top };
            results.Add((b, ColorExtract.Analyze(shot, local, bg)));
        }

        Console.WriteLine("  #   主色        色相    无彩占比  色簇(色相:面积占比)              Name");
        for (int i = 0; i < results.Count; i++)
        {
            var (b, c) = results[i];
            string hue = c.IsGray ? "  灰度" : $"{c.Hue,6:F1}";
            string clusters = c.Clusters.Length == 0
                ? "-"
                : string.Join(" ", c.Clusters.Select(k => $"{k.Hue,3:F0}:{k.Share:F2}"));
            Console.WriteLine(
                $"  {i,-3} #{c.Representative.R:X2}{c.Representative.G:X2}{c.Representative.B:X2}     " +
                $"{hue}     {c.AchromaticShare,6:F2}  {Pad(clusters, 32)} {b.Name}");
        }

        Console.WriteLine();
        Console.WriteLine("按颜色排序后的目标顺序（彩虹段 + 灰度段）:");
        var sorted = ColorSort.Order(results, x => x.Col);
        for (int i = 0; i < sorted.Count; i++)
            Console.WriteLine($"  {i,-3} {sorted[i].Btn.Name}");

        string png = Path.GetFullPath(Path.Combine(OutDir, "colors.png"));
        SaveAnnotated(shot, taskbar, results, png);
        Console.WriteLine();
        Console.WriteLine($"对比图 : {png}");
    }

    private static void SaveAnnotated(
        Bitmap shot, Rectangle taskbar,
        List<(TaskbarButton Btn, IconColor Col)> results, string path)
    {
        const int swatch = 44;
        using var canvas = new Bitmap(shot.Width, shot.Height + swatch);
        using var g = Graphics.FromImage(canvas);
        g.Clear(Color.FromArgb(24, 24, 24));
        g.DrawImage(shot, 0, 0);

        using var font = new Font("Segoe UI", 7f);
        foreach (var (b, c) in results)
        {
            int x = b.Bounds.Left - taskbar.Left;
            var rect = new Rectangle(x + 2, shot.Height + 2, Math.Max(b.Bounds.Width - 4, 6), swatch - 18);
            using var brush = new SolidBrush(c.Representative);
            g.FillRectangle(brush, rect);
            g.DrawRectangle(Pens.DimGray, rect);
            string label = c.IsGray ? "gray" : $"{c.Hue:F0}";
            g.DrawString(label, font, Brushes.White, x + 2, shot.Height + swatch - 15);
        }

        canvas.Save(path, System.Drawing.Imaging.ImageFormat.Png);
    }

    // ---------- 步骤 3：拖拽可行性 ----------
    private static void CmdDragTest(string driverName, Action<Rectangle, int, Rectangle> drag)
    {
        Header($"步骤 3 / 拖拽可行性 —— 驱动: {driverName}");

        var taskbar = TaskbarScanner.GetTaskbarBounds();
        var before = TaskbarScanner.GetAppButtons();
        if (before.Count < 2) { Console.WriteLine("按钮少于 2 个，无法测试。"); return; }

        Console.WriteLine("当前顺序:");
        PrintOrder(before);

        var last = before[^1];
        var first = before[0];
        Console.WriteLine();
        Console.WriteLine($"实验: 把最右边的「{last.Name}」拖到最左边（「{first.Name}」之前）。");
        Console.WriteLine("执行期间请勿移动鼠标；按 ESC 可中止。");
        Console.Write("按 Enter 开始，或 Ctrl+C 放弃... ");
        Console.ReadLine();

        Native.GetCursorPos(out var cursorBefore);
        drag(last.Bounds, first.Bounds.Left, taskbar);
        Thread.Sleep(500);
        Native.GetCursorPos(out var cursorAfter);

        var after = TaskbarScanner.GetAppButtons();
        Console.WriteLine();
        Console.WriteLine("拖拽后顺序:");
        PrintOrder(after);

        bool moved = after.Count > 0 && after[0].Name == last.Name;
        Console.WriteLine();
        Console.WriteLine(moved
            ? ">>> PASS: 拖拽重排生效，最右项已经跑到最左边。"
            : ">>> FAIL: 顺序没有按预期改变。");
        Console.WriteLine(
            $"    鼠标指针: ({cursorBefore.X},{cursorBefore.Y}) -> ({cursorAfter.X},{cursorAfter.Y})" +
            (cursorBefore.X == cursorAfter.X && cursorBefore.Y == cursorAfter.Y
                ? "  [未被移动]" : "  [被移动过]"));

        if (!moved) return;

        Console.Write("按 Enter 把它拖回最右边... ");
        Console.ReadLine();
        drag(after[0].Bounds, after[^1].Bounds.Right, taskbar);
        Thread.Sleep(500);
        Console.WriteLine("还原后顺序:");
        PrintOrder(TaskbarScanner.GetAppButtons());
    }

    // ---------- 附加实验：UIA 是否提供官方重排接口 ----------
    private static void CmdPatterns()
    {
        Header("附加实验 / 任务栏按钮支持哪些 UIA 控件模式");

        var buttons = TaskbarScanner.GetAppButtons();
        if (buttons.Count == 0) { Console.WriteLine("没有按钮可检查。"); return; }

        foreach (var b in buttons.Take(3))
        {
            var names = b.Element.GetSupportedPatterns()
                .Select(p => p.ProgrammaticName.Replace("PatternIdentifiers.Pattern", ""))
                .OrderBy(x => x)
                .ToArray();
            Console.WriteLine($"  {b.Name}");
            Console.WriteLine($"    {(names.Length == 0 ? "(无)" : string.Join(", ", names))}");
        }

        var all = buttons.SelectMany(b => b.Element.GetSupportedPatterns())
                         .Select(p => p.ProgrammaticName)
                         .Distinct()
                         .ToList();
        bool hasDrag = all.Any(n => n.Contains("Drag", StringComparison.OrdinalIgnoreCase));
        bool hasTransform = all.Any(n => n.Contains("Transform", StringComparison.OrdinalIgnoreCase));
        Console.WriteLine();
        Console.WriteLine($"  DragPattern      : {(hasDrag ? "支持" : "不支持")}");
        Console.WriteLine($"  TransformPattern : {(hasTransform ? "支持" : "不支持")}");
        Console.WriteLine(hasDrag || hasTransform
            ? "  >>> 存在可用的官方接口，值得改用。"
            : "  >>> 没有官方重排接口，只能靠输入注入。");
    }

    private static void PrintOrder(List<TaskbarButton> buttons)
    {
        for (int i = 0; i < buttons.Count; i++)
            Console.WriteLine($"  {i,-3} x={buttons[i].Bounds.Left,-5} {buttons[i].Name}");
    }

    private static void Header(string title)
    {
        Console.WriteLine(new string('=', 70));
        Console.WriteLine(title);
        Console.WriteLine(new string('=', 70));
    }

    private static string Pad(string s, int width)
        => s.Length >= width ? s[..width] : s.PadRight(width);
}
