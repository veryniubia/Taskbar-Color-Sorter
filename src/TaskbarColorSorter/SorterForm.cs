using System.Drawing.Drawing2D;
using System.Text;

namespace TaskbarColorSorter;

/// <summary>
/// 一个微恐怖主题的"控制面板"：中间一个按钮，点一下开始排序，再点一下停止。
/// 背景是缓慢明暗的暗红色调，文字偶尔会被叠加组合符号弄得"不太对劲"（文字恐怖谷效果）。
/// </summary>
internal sealed class SorterForm : Form
{
    private static readonly string[] Titles =
    {
        "别回头",
        "我一直都在看着",
        "你上次关掉我是什么时候",
        "任务栏色彩净化装置",
        "这是第几次了",
        "你不是第一个打开我的人",
        "安静点，它们在听",
        "继续，我在等",
    };

    private static readonly string[] Quotes =
    {
        "别担心，图标只是暂时被移动而已。",
        "有些顺序，换了就回不去了。",
        "我数过你的图标，一个都不少。",
        "关掉这个窗口，不会有事的。",
        "刚才那声音，不是我发出的。",
        "它们本来就应该这样排列，一直都是。",
        "你没有别的选择，其实。",
        "别眨眼，一下就好。",
        "我们已经这样很久了。",
        "安静，快好了。",
    };

    private static readonly string[] IdleButtonTexts =
        { "点我", "开始吧", "你知道你会点的", "别犹豫了", "就这一下" };

    private static readonly string[] BusyButtonTexts =
        { "停下来", "别看它们移动", "已经开始了", "太迟了", "嘘……" };

    private static readonly string[] DonePrefixes =
        { "它们乖乖听话了。", "结束了，看，多整齐。", "全部就位，如你所愿。", "都排好了，别谢我。" };

    private static readonly string[] AbortedPrefixes =
        { "你叫停了它，它不太高兴。", "停下了，但它记住了。", "半途而废，它会记仇的。" };

    private static readonly string[] FailedPrefixes =
        { "出错了，也许它不想被打断。", "失败了，别再试第二次。", "它挣扎了一下，没成功。" };

    /// <summary>叠加在字符后面的组合符号：不改变字形本身，只让文字看起来"哪里不对"。</summary>
    private static readonly char[] GlitchMarks = { '\u0336', '\u0335', '\u0334', '\u0347', '\u0359' };

    private readonly Label _titleLabel;
    private readonly Label _quoteLabel;
    private readonly Button _btn;
    private readonly Label _statusLabel;
    private readonly System.Windows.Forms.Timer _vibeTimer;
    private readonly Random _rng = new();

    private Point _btnHome;
    private double _pulsePhase;

    private CancellationTokenSource? _cts;
    private bool _busy;

    public SorterForm()
    {
        DoubleBuffered = true;
        ClientSize = new Size(420, 320);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = true;
        StartPosition = FormStartPosition.CenterScreen;
        Text = Glitch(Titles[_rng.Next(Titles.Length)], 0.03);

        _titleLabel = new Label
        {
            Dock = DockStyle.Top,
            Height = 44,
            TextAlign = ContentAlignment.MiddleCenter,
            Font = new Font("Segoe UI", 14F, FontStyle.Bold),
            ForeColor = Color.Gainsboro,
            BackColor = Color.Transparent,
            Text = Text,
        };

        _quoteLabel = new Label
        {
            Dock = DockStyle.Top,
            Height = 34,
            TextAlign = ContentAlignment.MiddleCenter,
            Font = new Font("Segoe UI", 9.5F, FontStyle.Italic),
            ForeColor = Color.Silver,
            BackColor = Color.Transparent,
            Text = Glitch(Quotes[_rng.Next(Quotes.Length)], 0.03),
        };

        _btn = new Button
        {
            Size = new Size(220, 96),
            Font = new Font("Segoe UI", 16F, FontStyle.Bold),
            FlatStyle = FlatStyle.Flat,
            Cursor = Cursors.Hand,
            ForeColor = Color.Gainsboro,
            Text = IdleButtonTexts[_rng.Next(IdleButtonTexts.Length)],
        };
        _btn.FlatAppearance.BorderSize = 3;
        _btn.Click += (_, _) => ToggleSort();
        _btnHome = new Point((ClientSize.Width - _btn.Width) / 2, 130);
        _btn.Location = _btnHome;

        _statusLabel = new Label
        {
            Dock = DockStyle.Bottom,
            Height = 54,
            TextAlign = ContentAlignment.MiddleCenter,
            Font = new Font("Consolas", 9F),
            ForeColor = Color.Gainsboro,
            BackColor = Color.FromArgb(160, 0, 0, 0),
            Text = "状态：它在等你点下去。",
        };

        Controls.Add(_btn);
        Controls.Add(_statusLabel);
        Controls.Add(_quoteLabel);
        Controls.Add(_titleLabel);

        _vibeTimer = new System.Windows.Forms.Timer { Interval = 120 };
        _vibeTimer.Tick += OnVibeTick;
        _vibeTimer.Start();

        FormClosing += (_, _) =>
        {
            _cts?.Cancel();
            _vibeTimer.Stop();
        };
    }

    private void ToggleSort()
    {
        if (_busy)
        {
            _cts?.Cancel();
            SetStatus("状态：你叫停了它……它停下了，很缓慢。");
            return;
        }

        StartSort();
    }

    private void StartSort()
    {
        SetBusy(true);
        SetStatus("状态：它睁开了眼睛……开始了。");

        _cts = new CancellationTokenSource();
        CancellationToken token = _cts.Token;

        Task.Run(() =>
        {
            using IDragDriver driver = SortEngine.CreateDriver();
            var engine = new SortEngine(driver);
            engine.Progress += (step, total, name) =>
                RunOnUi(() => SetStatus($"状态：正在移动它们 {step}/{total}：{Shorten(name, 20)}"));
            return engine.Run(token);
        }).ContinueWith(t =>
        {
            SortResult result = t.IsFaulted
                ? new SortResult(SortOutcome.Failed, 0, DescribeError(t.Exception))
                : t.Result;

            SetBusy(false);
            SetStatus("状态：" + DescribePrefix(result.Outcome) + " " + result.Message);

            _cts?.Dispose();
            _cts = null;
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        _btn.Text = (busy ? BusyButtonTexts : IdleButtonTexts)[_rng.Next(busy ? BusyButtonTexts.Length : IdleButtonTexts.Length)];
    }

    private void SetStatus(string text) => _statusLabel.Text = Glitch(text, _busy ? 0.02 : 0.01);

    private void RunOnUi(Action action)
    {
        if (IsDisposed) return;
        if (InvokeRequired) BeginInvoke(action);
        else action();
    }

    private string DescribePrefix(SortOutcome outcome)
    {
        string[] pool = outcome switch
        {
            SortOutcome.Sorted or SortOutcome.AlreadySorted or SortOutcome.NothingToDo => DonePrefixes,
            SortOutcome.Aborted => AbortedPrefixes,
            _ => FailedPrefixes,
        };
        return pool[_rng.Next(pool.Length)];
    }

    private static string DescribeError(AggregateException? ex)
    {
        var inner = ex?.Flatten().InnerExceptions.FirstOrDefault();
        return inner == null ? "未知错误，它不愿多说。" : $"{inner.GetType().Name}: {inner.Message}";
    }

    private static string Shorten(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";

    /// <summary>给文字随机叠加组合符号，制造"哪里不对劲"的文字恐怖谷效果，但不影响内容本身可读。</summary>
    private string Glitch(string s, double chancePerChar)
    {
        if (chancePerChar <= 0) return s;
        var sb = new StringBuilder(s.Length + 4);
        foreach (char c in s)
        {
            sb.Append(c);
            if (!char.IsWhiteSpace(c) && _rng.NextDouble() < chancePerChar)
                sb.Append(GlitchMarks[_rng.Next(GlitchMarks.Length)]);
        }
        return sb.ToString();
    }

    private void OnVibeTick(object? sender, EventArgs e)
    {
        _pulsePhase += _busy ? 0.18 : 0.05;
        Invalidate();

        // 偶尔换标题 / 换废话，营造"它一直在自言自语"的感觉。
        if (_rng.NextDouble() < (_busy ? 0.04 : 0.012))
        {
            Text = Glitch(Titles[_rng.Next(Titles.Length)], 0.04);
            _titleLabel.Text = Text;
        }
        if (_rng.NextDouble() < (_busy ? 0.05 : 0.018))
        {
            _quoteLabel.Text = Glitch(Quotes[_rng.Next(Quotes.Length)], 0.04);
        }

        double glow = (Math.Sin(_pulsePhase) + 1) / 2; // 0..1 缓慢明暗
        Color borderColor = Color.FromArgb(255,
            (int)(120 + glow * 100),
            (int)(20 + glow * 20),
            (int)(20 + glow * 20));
        _btn.BackColor = Color.FromArgb(255, (int)(30 + glow * 20), 8, 8);
        _btn.FlatAppearance.BorderColor = borderColor;

        // 排序进行中，按钮跟着轻微手抖；空闲时纹丝不动。
        if (_busy)
        {
            int dx = _rng.Next(-3, 4);
            int dy = _rng.Next(-3, 4);
            _btn.Location = new Point(_btnHome.X + dx, _btnHome.Y + dy);
        }
        else if (_btn.Location != _btnHome)
        {
            _btn.Location = _btnHome;
        }
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        double glow = (Math.Sin(_pulsePhase) + 1) / 2;

        // 偶尔一次极短的猩红闪烁，制造"它眨了下眼"的观感。
        bool flicker = _rng.NextDouble() < (_busy ? 0.02 : 0.006);

        Color c1 = flicker
            ? Color.FromArgb(255, 60, 4, 4)
            : Color.FromArgb(255, (int)(10 + glow * 8), (int)(8 + glow * 4), (int)(10 + glow * 6));
        Color c2 = Color.FromArgb(255, 4, 2, 4);

        using var brush = new LinearGradientBrush(ClientRectangle, c1, c2, LinearGradientMode.ForwardDiagonal);
        e.Graphics.FillRectangle(brush, ClientRectangle);
    }
}
