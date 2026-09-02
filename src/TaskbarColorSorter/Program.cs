namespace TaskbarColorSorter;

internal static class Program
{
    private const string SingleInstanceMutexName = @"Local\TaskbarColorSorter.SingleInstance";

    [STAThread]
    private static int Main(string[] args)
    {
        // 必须在创建任何窗口、访问任何 UIA 元素之前设置，
        // 否则 UIA 返回的坐标会被系统按 DPI 缩放，与截屏坐标系对不上。
        Native.EnablePerMonitorDpiAwareness();

        // 一次性模式：排完就退出，不驻留托盘。方便绑快捷方式，也用于自动化验证。
        if (args.Any(a => a.Equals("--sort", StringComparison.OrdinalIgnoreCase)))
            return RunOnce();

        using var mutex = new Mutex(initiallyOwned: true, SingleInstanceMutexName, out bool isFirstInstance);
        if (!isFirstInstance) return 0;

        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        // --ui：直接打开无用控制面板，不驻留托盘，方便单独调试这个窗口。
        if (args.Any(a => a.Equals("--ui", StringComparison.OrdinalIgnoreCase)))
        {
            Application.Run(new SorterForm());
            return 0;
        }

        Application.Run(new TrayApp());
        return 0;
    }

    private static int RunOnce()
    {
        using IDragDriver driver = SortEngine.CreateDriver();
        SortResult result = new SortEngine(driver).Run(CancellationToken.None);

        return result.Outcome switch
        {
            SortOutcome.Sorted or SortOutcome.AlreadySorted or SortOutcome.NothingToDo => 0,
            SortOutcome.Aborted => 2,
            _ => 1,
        };
    }
}
