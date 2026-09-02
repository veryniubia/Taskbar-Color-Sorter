using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace TaskbarColorSorter;

internal sealed class TrayApp : ApplicationContext
{
    private const string AppTitle = "Taskbar Color Sorter";

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr handle);

    private readonly NotifyIcon _tray;
    private readonly ToolStripMenuItem _sortItem;
    private readonly ToolStripMenuItem _abortItem;
    private readonly IntPtr _iconHandle;

    private CancellationTokenSource? _cts;
    private bool _busy;
    private SorterForm? _panel;

    public TrayApp()
    {
        (Icon icon, _iconHandle) = CreateTrayIcon();

        _sortItem = new ToolStripMenuItem("按颜色排序", null, (_, _) => StartSort())
        {
            Font = new Font(SystemFonts.MenuFont!, FontStyle.Bold),
        };
        _abortItem = new ToolStripMenuItem("中止", null, (_, _) => _cts?.Cancel()) { Enabled = false };

        var menu = new ContextMenuStrip();
        menu.Items.Add(_sortItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_abortItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("打开它", null, (_, _) => ShowPanel()));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("退出", null, (_, _) => ExitApp()));

        _tray = new NotifyIcon
        {
            Icon = icon,
            Text = AppTitle,
            Visible = true,
            ContextMenuStrip = menu,
        };
        _tray.DoubleClick += (_, _) => StartSort();

        _tray.ShowBalloonTip(3000, AppTitle, "双击托盘图标即可按颜色排序任务栏。", ToolTipIcon.Info);
    }

    private void StartSort()
    {
        if (_busy) return;
        SetBusy(true);

        _cts = new CancellationTokenSource();
        var token = _cts.Token;

        Task.Run(() =>
        {
            using IDragDriver driver = SortEngine.CreateDriver();
            var engine = new SortEngine(driver);
            engine.Progress += (step, total, name) => SetStatus($"排序中 {step}/{total}：{Shorten(name, 24)}");
            return engine.Run(token);
        }).ContinueWith(t =>
        {
            SortResult result = t.IsFaulted
                ? new SortResult(SortOutcome.Failed, 0, Describe(t.Exception))
                : t.Result;

            SetBusy(false);
            SetStatus(AppTitle);
            ShowResult(result);

            _cts?.Dispose();
            _cts = null;
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    private void ShowPanel()
    {
        if (_panel is { IsDisposed: false })
        {
            _panel.Activate();
            return;
        }

        _panel = new SorterForm();
        _panel.Show();
    }

    private void ShowResult(SortResult result)
    {
        var iconKind = result.Outcome switch
        {
            SortOutcome.Sorted or SortOutcome.AlreadySorted or SortOutcome.NothingToDo => ToolTipIcon.Info,
            SortOutcome.Aborted => ToolTipIcon.Warning,
            _ => ToolTipIcon.Error,
        };
        _tray.ShowBalloonTip(4000, AppTitle, result.Message, iconKind);
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        _sortItem.Enabled = !busy;
        _abortItem.Enabled = busy;
    }

    private void SetStatus(string text)
    {
        // NotifyIcon.Text 上限 63 个字符，超了会抛异常
        _tray.Text = Shorten(text, 63);
    }

    private static string Shorten(string s, int max)
        => s.Length <= max ? s : s[..(max - 1)] + "…";

    private static string Describe(AggregateException? ex)
    {
        var inner = ex?.Flatten().InnerExceptions.FirstOrDefault();
        return inner == null ? "未知错误。" : $"{inner.GetType().Name}: {inner.Message}";
    }

    private void ExitApp()
    {
        _cts?.Cancel();
        _panel?.Close();
        _tray.Visible = false;
        _tray.Dispose();
        if (_iconHandle != IntPtr.Zero) DestroyIcon(_iconHandle);
        ExitThread();
    }

    /// <summary>画一个"按色相排好序的柱状图"作为托盘图标，避免额外携带资源文件。</summary>
    private static (Icon Icon, IntPtr Handle) CreateTrayIcon()
    {
        using var bmp = new Bitmap(32, 32, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(Color.Transparent);
            g.SmoothingMode = SmoothingMode.AntiAlias;

            const int bars = 6;
            for (int i = 0; i < bars; i++)
            {
                using var brush = new SolidBrush(FromHsv(i * 360.0 / bars, 0.85, 0.95));
                int height = 8 + i * 4;
                g.FillRectangle(brush, 2 + i * 5, 30 - height, 4, height);
            }
        }

        IntPtr handle = bmp.GetHicon();
        return (Icon.FromHandle(handle), handle);
    }

    private static Color FromHsv(double h, double s, double v)
    {
        int hi = (int)(h / 60) % 6;
        double f = h / 60 - Math.Floor(h / 60);
        int p = (int)(v * (1 - s) * 255);
        int q = (int)(v * (1 - f * s) * 255);
        int t = (int)(v * (1 - (1 - f) * s) * 255);
        int val = (int)(v * 255);

        return hi switch
        {
            0 => Color.FromArgb(val, t, p),
            1 => Color.FromArgb(q, val, p),
            2 => Color.FromArgb(p, val, t),
            3 => Color.FromArgb(p, q, val),
            4 => Color.FromArgb(t, p, val),
            _ => Color.FromArgb(val, p, q),
        };
    }
}
