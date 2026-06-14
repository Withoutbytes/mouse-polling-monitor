using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace MousePollingMonitor;

public partial class MainWindow : Window
{
    private readonly MouseEngine _engine = new();
    private EngineStats?         _lastStats;
    private const int            MaxLogEntries = 500;
    private readonly Stopwatch   _logThrottle  = Stopwatch.StartNew();

    // Pre-allocated spark bar rectangles — updated in-place each frame
    private readonly Rectangle[] _sparkRects = new Rectangle[MouseEngine.SparkLen];

    private static readonly SolidColorBrush BrushBurst = Frozen(Color.FromRgb(220, 50, 50));
    private static readonly SolidColorBrush BrushBar   = Frozen(Color.FromRgb(0, 200, 100));
    private static readonly SolidColorBrush BrushEmpty = Frozen(Color.FromRgb(25, 25, 45));

    private static SolidColorBrush Frozen(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }

    public MainWindow()
    {
        InitializeComponent();

        txtTimerInfo.Text = $"Timer: {Stopwatch.Frequency:N0} Hz  |  {(IntPtr.Size == 8 ? "x64" : "x86")}  |  Close window to exit";

        for (int i = 0; i < MouseEngine.SparkLen; i++)
        {
            var r = new Rectangle { Height = 2, Fill = BrushEmpty };
            sparkCanvas.Children.Add(r);
            _sparkRects[i] = r;
        }

        _engine.StatsUpdated += stats => Dispatcher.InvokeAsync(() => ApplyStats(stats));
        _engine.Start();

        Closing += (_, _) => _engine.Stop();
    }

    private void Topmost_Changed(object sender, RoutedEventArgs e)
    {
        Topmost = chkTopmost.IsChecked == true;
    }

    // ── Stats update (UI thread) ─────────────────────────────────────────────

    private void ApplyStats(EngineStats s)
    {
        _lastStats = s;

        txtInstantHz.Text  = s.InstantHz > 0 ? s.InstantHz.ToString() : "- - -";
        txtSamples.Text    = s.FilledSamples.ToString();

        if (s.FilledSamples >= 2)
        {
            txtAvgHz.Text     = s.AvgHz.ToString("F0", CultureInfo.InvariantCulture);
            txtStdDev.Text    = s.StdDev.ToString("F3", CultureInfo.InvariantCulture);
            txtBurstRate.Text = (s.BurstRate * 100).ToString("F1", CultureInfo.InvariantCulture) + "%";
            txtStdDev.Foreground    = StdDevBrush(s.StdDev);
            txtBurstRate.Foreground = BurstBrush(s.BurstRate);
        }
        else
        {
            txtAvgHz.Text     = "---";
            txtStdDev.Text    = "---";
            txtBurstRate.Text = "---";
        }

        ApplyVerdict(s);
        DrawSparkline(s);

        if (_logThrottle.ElapsedMilliseconds >= 1000)
        {
            _logThrottle.Restart();
            AddLog(s);
        }
    }

    // ── Verdict ──────────────────────────────────────────────────────────────

    private void ApplyVerdict(EngineStats s)
    {
        var (title, subtitle, fgColor, bgColor, borderColor) = s.Verdict switch
        {
            Verdict.Natural    => ("✓  NATURAL SIGNAL",
                                   "Physical jitter detected — real hardware input",
                                   Color.FromRgb(0, 230, 120),
                                   Color.FromRgb(0, 20, 10),
                                   Color.FromRgb(0, 80, 40)),

            Verdict.Suspicious => ("?  SUSPICIOUS SIGNAL",
                                   "Abnormally low jitter or elevated burst rate",
                                   Color.FromRgb(255, 215, 0),
                                   Color.FromRgb(25, 18, 0),
                                   Color.FromRgb(100, 75, 0)),

            Verdict.Injection  => ("!  INJECTION DETECTED",
                                   "Pattern physically impossible on real USB hardware",
                                   Color.FromRgb(255, 70, 70),
                                   Color.FromRgb(28, 0, 5),
                                   Color.FromRgb(120, 20, 30)),

            _                  => ("…  WAITING FOR DATA",
                                   $"Move the mouse — {s.FilledSamples}/30 samples collected",
                                   Color.FromRgb(85, 85, 119),
                                   Color.FromRgb(13, 13, 30),
                                   Color.FromRgb(51, 51, 85)),
        };

        txtVerdictTitle.Text       = title;
        txtVerdictTitle.Foreground = new SolidColorBrush(fgColor);
        txtVerdictSub.Text         = subtitle;
        verdictBorder.Background   = new SolidColorBrush(bgColor);
        verdictBorder.BorderBrush  = new SolidColorBrush(borderColor);
    }

    // ── Sparkline ────────────────────────────────────────────────────────────

    private void SparkCanvas_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_lastStats != null) DrawSparkline(_lastStats);
    }

    private void DrawSparkline(EngineStats s)
    {
        double w = sparkCanvas.ActualWidth;
        double h = sparkCanvas.ActualHeight;
        if (w <= 0 || h <= 0) return;

        double barWidth = w / MouseEngine.SparkLen;
        double avgDt    = s.AvgHz > 0 ? 1000.0 / s.AvgHz : 8.0;
        double scaleMax = Math.Max(avgDt * 2.5, 1.0);

        txtSparkScale.Text = $"scale: 0 ms (burst) → {(avgDt * 2.5).ToString("F1", CultureInfo.InvariantCulture)} ms (tall)";

        for (int i = 0; i < MouseEngine.SparkLen; i++)
        {
            var r = _sparkRects[i];
            r.Width = Math.Max(barWidth - 1, 1);

            if (i < s.SparkCount)
            {
                if (s.SparkBurst[i])
                {
                    r.Height = 6;
                    r.Fill   = BrushBurst;
                }
                else
                {
                    r.Height = Math.Clamp(s.Spark[i] / scaleMax * h, 2, h);
                    r.Fill   = BrushBar;
                }
            }
            else
            {
                r.Height = 2;
                r.Fill   = BrushEmpty;
            }

            Canvas.SetLeft(r, i * barWidth);
            Canvas.SetTop(r, h - r.Height);
        }
    }

    // ── Log ──────────────────────────────────────────────────────────────────

    private void AddLog(EngineStats s)
    {
        var ic = CultureInfo.InvariantCulture;
        string entry = s.FilledSamples >= 2
            ? $"[{DateTime.Now:HH:mm:ss}]  {s.InstantHz,4} Hz  avg:{s.AvgHz.ToString("F1", ic),7}  σ:{s.StdDev.ToString("F3", ic)}ms  bursts:{(s.BurstRate * 100).ToString("F1", ic)}%  {VerdictTag(s.Verdict)}"
            : $"[{DateTime.Now:HH:mm:ss}]  collecting... ({s.FilledSamples}/30 samples)";

        logList.Items.Add(entry);
        if (logList.Items.Count > MaxLogEntries)
            logList.Items.RemoveAt(0);
        if (logList.Items.Count > 0)
            logList.ScrollIntoView(logList.Items[logList.Items.Count - 1]);
    }

    private static string VerdictTag(Verdict v) => v switch
    {
        Verdict.Natural    => "[NATURAL]",
        Verdict.Suspicious => "[SUSPICIOUS]",
        Verdict.Injection  => "[INJECTION]",
        _                  => "[waiting]"
    };

    // ── Color helpers ─────────────────────────────────────────────────────────

    private static Brush StdDevBrush(double sd) =>
        sd <= 0.015 ? new SolidColorBrush(Color.FromRgb(255, 70, 70)) :
        sd <= 0.060 ? new SolidColorBrush(Color.FromRgb(255, 215, 0)) :
                      new SolidColorBrush(Color.FromRgb(0, 230, 120));

    private static Brush BurstBrush(double rate) =>
        rate >= 0.20 ? new SolidColorBrush(Color.FromRgb(255, 70, 70)) :
        rate >= 0.10 ? new SolidColorBrush(Color.FromRgb(255, 215, 0)) :
                       new SolidColorBrush(Color.FromRgb(0, 230, 120));
}
