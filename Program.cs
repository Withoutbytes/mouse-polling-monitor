using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using MousePollingMonitor;

// ── ANSI helpers — all color/cursor ops go through here for single-write frames

internal static class A
{
    public const string R       = "\x1b[0m";     // reset
    public const string K       = "\x1b[K";      // erase to end of line
    public const string Hide    = "\x1b[?25l";   // hide cursor
    public const string Show    = "\x1b[?25h";   // show cursor

    public const string Cyan    = "\x1b[96m";
    public const string DkCyan  = "\x1b[36m";
    public const string White   = "\x1b[97m";
    public const string Gray    = "\x1b[37m";
    public const string DkGray  = "\x1b[90m";
    public const string Green   = "\x1b[92m";
    public const string DkGreen = "\x1b[32m";
    public const string Yellow  = "\x1b[93m";
    public const string Red     = "\x1b[91m";

    // Move cursor to absolute row (1-based) column 1
    public static string Pos(int row) => $"\x1b[{row};1H";
}

// ── Packet ────────────────────────────────────────────────────────────────────

internal readonly record struct MousePacket(long Ticks, int Dx, int Dy, bool HasMovement);
internal enum Verdict { Aguardando, Natural, Suspeito, Injecao }

// ── Main application ──────────────────────────────────────────────────────────

internal sealed class MouseMonitor
{
    // ── Detection thresholds (empirically calibrated) ─────────────────────────
    private const double BurstThreshMs     = 0.5;
    private const double StdDevInjecao     = 0.015;
    private const double StdDevSuspeito    = 0.060;
    private const double BurstRateInjecao  = 0.20;
    private const double BurstRateSuspeito = 0.10;
    private const int    MinSamples        = 30;

    // ── Rolling window ────────────────────────────────────────────────────────
    private const int WindowSize = 100;
    private readonly double[] _intervals   = new double[WindowSize];
    private readonly bool[]   _bursts      = new bool[WindowSize];
    private int    _head         = 0;
    private int    _filled       = 0;
    private int    _windowBursts = 0;
    private double _windowSum    = 0;

    // ── Sparkline (60 packets, insertion order) ───────────────────────────────
    private const int SparkLen = 60;
    private readonly double[] _spark      = new double[SparkLen];
    private readonly bool[]   _sparkBurst = new bool[SparkLen];
    private int _sparkCount = 0;

    // ── Pipeline ──────────────────────────────────────────────────────────────
    private readonly ConcurrentQueue<MousePacket> _queue = new();
    private readonly Stopwatch _sw = Stopwatch.StartNew();
    private volatile bool _alive = true;
    private readonly Win32.WndProcDelegate _wndProcDelegate;

    private long _totalMovPkts = 0;
    private long _totalBursts  = 0;
    private long _prevTicks    = 0;

    // ── Render state ──────────────────────────────────────────────────────────
    private int  _dashRow      = -1;   // console row where dashboard starts (0-based)
    private const int DashLines = 27;  // total lines reserved for dashboard

    // Rate limiter: cap redraws at ~20 fps to avoid hammering the console
    private readonly Stopwatch _renderThrottle = Stopwatch.StartNew();
    private const long RenderIntervalMs = 50;

    // ── Reusable frame buffer ─────────────────────────────────────────────────
    private readonly StringBuilder _frame = new(4096);

    // ─────────────────────────────────────────────────────────────────────────

    public static void Main()
    {
        Console.OutputEncoding = Encoding.UTF8;
        Win32.EnableAnsiConsole();
        Console.Write(A.Hide); // hide cursor for the entire session
        new MouseMonitor().Run();
    }

    private MouseMonitor() { _wndProcDelegate = WndProc; }

    private void Run()
    {
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            _alive = false;
            Console.Write(A.Show); // restore cursor on exit
            Win32.PostQuitMessage(0);
        };

        PrintHeader();

        new Thread(ProcessorLoop)
        {
            IsBackground = true,
            Name = "PacketProcessor",
            Priority = ThreadPriority.BelowNormal
        }.Start();

        SetupAndRunMessageLoop();
        _alive = false;
        Console.Write(A.Show);
    }

    // ── Message loop ──────────────────────────────────────────────────────────

    private void SetupAndRunMessageLoop()
    {
        IntPtr hInstance = Win32.GetModuleHandleW(null);
        string cls = "MPM_" + Environment.ProcessId;

        var wc = new Win32.WNDCLASSW
        {
            lpfnWndProc   = Marshal.GetFunctionPointerForDelegate(_wndProcDelegate),
            hInstance     = hInstance,
            lpszClassName = cls
        };
        Win32.RegisterClassW(ref wc);

        IntPtr hwnd = Win32.CreateWindowExW(0, cls, "MPM", 0, 0, 0, 0, 0,
            Win32.HWND_MESSAGE, IntPtr.Zero, hInstance, IntPtr.Zero);

        if (hwnd == IntPtr.Zero)
        {
            Console.WriteLine($"  [ERRO] CreateWindowEx ({Marshal.GetLastWin32Error()})");
            return;
        }

        var rid = new[] { new Win32.RAWINPUTDEVICE
        {
            usUsagePage = 0x01, usUsage = 0x02,
            dwFlags = Win32.RIDEV_INPUTSINK, hwndTarget = hwnd
        }};

        if (!Win32.RegisterRawInputDevices(rid, 1, (uint)Marshal.SizeOf<Win32.RAWINPUTDEVICE>()))
        {
            Console.WriteLine($"  [ERRO] RegisterRawInputDevices ({Marshal.GetLastWin32Error()})");
            return;
        }

        Win32.MSG msg;
        while (Win32.GetMessageW(out msg, IntPtr.Zero, 0, 0) > 0)
        {
            Win32.TranslateMessage(ref msg);
            Win32.DispatchMessageW(ref msg);
        }
    }

    // ── WndProc — HOT PATH ────────────────────────────────────────────────────

    private IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == Win32.WM_INPUT)   { CapturePacket(lParam); return IntPtr.Zero; }
        if (msg == Win32.WM_DESTROY) { Win32.PostQuitMessage(0); return IntPtr.Zero; }
        return Win32.DefWindowProcW(hWnd, msg, wParam, lParam);
    }

    private unsafe void CapturePacket(IntPtr hRaw)
    {
        uint sz = 0, hdrSz = Win32.RawInputHeaderSize;
        Win32.GetRawInputData(hRaw, Win32.RID_INPUT, IntPtr.Zero, ref sz, hdrSz);
        if (sz == 0 || sz > 256) return;

        byte* buf = stackalloc byte[(int)sz];
        Win32.GetRawInputData(hRaw, Win32.RID_INPUT, (IntPtr)buf, ref sz, hdrSz);
        if (*(uint*)buf != Win32.RIM_TYPEMOUSE) return;

        // RAWMOUSE — MSVC 4-byte aligned (sizeof=24):
        //   +0 usFlags +2 pad +4 ulButtons +8 ulRawButtons +12 lLastX +16 lLastY
        byte* m = buf + hdrSz;
        int dx = *(int*)(m + 12);
        int dy = *(int*)(m + 16);
        long ticks = _sw.ElapsedTicks;

        _queue.Enqueue(new MousePacket(ticks, dx, dy, dx != 0 || dy != 0));
    }

    // ── Processor thread ──────────────────────────────────────────────────────

    private void ProcessorLoop()
    {
        int idle = 0;
        while (_alive || !_queue.IsEmpty)
        {
            if (_queue.TryDequeue(out var pkt)) { idle = 0; Analyze(pkt); }
            else if (++idle > 600) { Thread.Sleep(1); idle = 0; }
        }
    }

    private void Analyze(MousePacket p)
    {
        if (!p.HasMovement) return;
        _totalMovPkts++;
        if (_prevTicks == 0) { _prevTicks = p.Ticks; return; }

        double dt = (p.Ticks - _prevTicks) * 1000.0 / Stopwatch.Frequency;
        _prevTicks = p.Ticks;
        if (dt <= 0) return;

        bool burst = dt < BurstThreshMs;
        if (burst) _totalBursts++;

        // Rolling window
        int slot = _head;
        if (_filled == WindowSize) { _windowSum -= _intervals[slot]; if (_bursts[slot]) _windowBursts--; }
        else _filled++;
        _intervals[slot] = dt; _bursts[slot] = burst;
        _windowSum += dt; if (burst) _windowBursts++;
        _head = (slot + 1) % WindowSize;

        // Sparkline
        if (_sparkCount < SparkLen)
        {
            _spark[_sparkCount] = dt; _sparkBurst[_sparkCount] = burst; _sparkCount++;
        }
        else
        {
            Array.Copy(_spark,      1, _spark,      0, SparkLen - 1);
            Array.Copy(_sparkBurst, 1, _sparkBurst, 0, SparkLen - 1);
            _spark[SparkLen - 1] = dt; _sparkBurst[SparkLen - 1] = burst;
        }

        // Rate-limited render
        if (_renderThrottle.ElapsedMilliseconds >= RenderIntervalMs)
        {
            _renderThrottle.Restart();
            RefreshDashboard();
        }
    }

    // ── Dashboard — single-write frame ────────────────────────────────────────

    private void RefreshDashboard()
    {
        var (avgDt, stdDev, burstRate, avgHz) = ComputeStats();
        Verdict verdict = EvaluateVerdict(stdDev, burstRate);

        // Reserve lines on first call
        if (_dashRow < 0)
        {
            _dashRow = Console.CursorTop;
            for (int i = 0; i < DashLines; i++) Console.WriteLine();
        }

        _frame.Clear();

        // Move cursor to top of dashboard area (ANSI rows are 1-based)
        _frame.Append(A.Pos(_dashRow + 1));

        // ── Metrics ───────────────────────────────────────────────────────────
        Section(_frame, "ANÁLISE — JANELA DE 100 PACOTES");

        string waiting = _filled < MinSamples ? $" (aguardando {MinSamples - _filled}...)" : $" (amostras: {_filled})";

        Metric(_frame, "  Frequência     :",
            _filled >= 2 ? $"{avgHz,7:F1} Hz  (Δt médio: {avgDt:F3} ms)" : "aguardando...",
            A.White);

        Metric(_frame, "  Desvio padrão  :",
            _filled >= 2 ? $"{stdDev,7:F3} ms  ({DescribeStd(stdDev)}){waiting}" : "aguardando...",
            StdDevColor(stdDev));

        Metric(_frame, "  Bursts / janela:",
            _filled >= 2 ? $"{_windowBursts,4} / {_filled,-4}  ({burstRate * 100:F1}%)" : "aguardando...",
            BurstColor(burstRate));

        Metric(_frame, "  Total capturado:",
            $"{_totalMovPkts} pacotes  ({_totalBursts} bursts acumulados)",
            A.DkGray);

        Line(_frame);

        // ── Sparkline ─────────────────────────────────────────────────────────
        Section(_frame, "HISTÓRICO — últimos 60 pacotes  (vermelho = burst)");

        _frame.Append("  ┌").Append('─', SparkLen).Append("┐").Append(A.K).Append('\n');
        _frame.Append("  │");
        AppendSparkline(_frame, avgDt);
        _frame.Append("│").Append(A.K).Append('\n');
        _frame.Append("  └").Append('─', SparkLen).Append("┘").Append(A.K).Append('\n');

        _frame.Append(A.DkGray)
              .Append($"  escala: 0 ms (burst) → {avgDt * 2.5:F1} ms (alto)".PadRight(70))
              .Append(A.R).Append(A.K).Append('\n');

        Line(_frame);

        // ── Histogram ─────────────────────────────────────────────────────────
        Section(_frame, "DISTRIBUIÇÃO DE INTERVALOS");
        AppendHistogram(_frame);

        Line(_frame);

        // ── Verdict ───────────────────────────────────────────────────────────
        (string icon, string title, string subtitle, string color) = verdict switch
        {
            Verdict.Natural  => ("✓", "SINAL NATURAL",    "Jitter físico detectado — hardware real",          A.Green),
            Verdict.Suspeito => ("?", "SINAL SUSPEITO",   "Jitter baixo ou bursts elevados — investigar",     A.Yellow),
            Verdict.Injecao  => ("!", "INJEÇÃO DETECTADA","Padrão fisicamente impossível para USB real",       A.Red),
            _                => ("…", "AGUARDANDO DADOS", $"Mova o mouse — {_filled}/{MinSamples} amostras",  A.DkGray),
        };

        _frame.Append(color);
        _frame.Append("  ┌────────────────────────────────────────────────────────────────┐").Append(A.K).Append('\n');
        _frame.Append("  │                                                                │").Append(A.K).Append('\n');
        _frame.Append($"  │    {icon}  {title,-58}│").Append(A.K).Append('\n');
        _frame.Append($"  │       {subtitle,-57}│").Append(A.K).Append('\n');
        _frame.Append("  │                                                                │").Append(A.K).Append('\n');
        _frame.Append("  └────────────────────────────────────────────────────────────────┘").Append(A.K).Append('\n');

        _frame.Append(A.DkGray)
              .Append("  Natural:<10% σ≥0.060ms  │ Suspeito:10-20% σ<0.060ms  │ Injeção:>20% σ≤0.015ms  ")
              .Append(A.R).Append(A.K).Append('\n');

        // Single write — no flicker
        Console.Write(_frame);
    }

    // ── Frame builders ────────────────────────────────────────────────────────

    private static void Section(StringBuilder sb, string title)
    {
        sb.Append(A.DkCyan)
          .Append($"  ── {title} ──".PadRight(70))
          .Append(A.R).Append(A.K).Append('\n');
    }

    private static void Metric(StringBuilder sb, string label, string value, string color)
    {
        sb.Append(A.Gray).Append(label.PadRight(22))
          .Append(color).Append(' ').Append(value.PadRight(52))
          .Append(A.R).Append(A.K).Append('\n');
    }

    private static void Line(StringBuilder sb)
    {
        sb.Append(A.K).Append('\n');
    }

    private void AppendSparkline(StringBuilder sb, double avgDt)
    {
        double scaleMax = Math.Max(avgDt * 2.5, 1.0);
        char[] blocks = { ' ', '▁', '▂', '▃', '▄', '▅', '▆', '▇', '█' };

        for (int i = 0; i < _sparkCount; i++)
        {
            if (_sparkBurst[i])
            {
                sb.Append(A.Red).Append('▁');
            }
            else
            {
                int level = (int)Math.Clamp(Math.Ceiling(_spark[i] / scaleMax * 8), 1, 8);
                sb.Append(A.Green).Append(blocks[level]);
            }
        }
        for (int i = _sparkCount; i < SparkLen; i++)
            sb.Append(A.DkGray).Append('·');

        sb.Append(A.R);
    }

    private void AppendHistogram(StringBuilder sb)
    {
        var counts = new int[6];
        for (int i = 0; i < _filled; i++)
        {
            double v = _intervals[i];
            if      (v < 0.5)  counts[0]++;
            else if (v < 1.5)  counts[1]++;
            else if (v < 3.0)  counts[2]++;
            else if (v < 6.0)  counts[3]++;
            else if (v < 12.0) counts[4]++;
            else               counts[5]++;
        }

        string[] labels = { "< 0.5 ms  [BURST]  ", "0.5–1.5 ms ~1000Hz", "1.5–3 ms   ~500Hz ", "3–6 ms     ~250Hz ", "6–12 ms    ~125Hz ", "> 12 ms    lento  " };
        string[] colors  = { A.Red, A.Green, A.Green, A.DkGreen, A.DkGray, A.DkGray };

        int maxCount = Math.Max(1, counts.Max());
        const int barW = 28;

        for (int b = 0; b < 6; b++)
        {
            double pct    = _filled > 0 ? counts[b] * 100.0 / _filled : 0;
            int    filled = (int)Math.Round((double)counts[b] / maxCount * barW);

            sb.Append(A.DkGray).Append("  ").Append(labels[b]).Append("  ")
              .Append(colors[b]).Append(new string('█', filled))
              .Append(A.DkGray).Append(new string('░', barW - filled))
              .Append(A.Gray).Append($"  {counts[b],4} ({pct,5:F1}%)")
              .Append(A.R).Append(A.K).Append('\n');
        }
    }

    // ── Stats / verdict ───────────────────────────────────────────────────────

    private (double avgDt, double stdDev, double burstRate, double avgHz) ComputeStats()
    {
        if (_filled < 2) return (0, 0, 0, 0);
        double avg = _windowSum / _filled;
        double variance = 0;
        for (int i = 0; i < _filled; i++)
            variance += (_intervals[i] - avg) * (_intervals[i] - avg);
        return (avg, Math.Sqrt(variance / _filled), (double)_windowBursts / _filled, 1000.0 / avg);
    }

    private Verdict EvaluateVerdict(double stdDev, double burstRate)
    {
        if (_filled < MinSamples)                                         return Verdict.Aguardando;
        if (burstRate >= BurstRateInjecao  || stdDev <= StdDevInjecao)   return Verdict.Injecao;
        if (burstRate >= BurstRateSuspeito || stdDev <= StdDevSuspeito)  return Verdict.Suspeito;
        return Verdict.Natural;
    }

    private static string DescribeStd(double sd) =>
        sd <= StdDevInjecao  ? "precisão inumana" :
        sd <= StdDevSuspeito ? "jitter suspeito"  : "jitter natural";

    private static string StdDevColor(double sd) =>
        sd <= StdDevInjecao  ? A.Red :
        sd <= StdDevSuspeito ? A.Yellow : A.Green;

    private static string BurstColor(double rate) =>
        rate >= BurstRateInjecao  ? A.Red :
        rate >= BurstRateSuspeito ? A.Yellow : A.Green;

    // ── Static header (printed once) ─────────────────────────────────────────

    private static void PrintHeader()
    {
        try { Console.Clear(); } catch { }
        Console.Write(
            $"{A.Cyan}" +
            "╔══════════════════════════════════════════════════════════════════╗\n" +
            "║        MOUSE POLLING RATE MONITOR  v1.0                         ║\n" +
            "║        Raw Input Diagnostic Tool — Detecção de Injeção          ║\n" +
            "╚══════════════════════════════════════════════════════════════════╝\n" +
            $"{A.DkGray}");

        double tickUs = 1_000_000.0 / Stopwatch.Frequency;
        Console.WriteLine($"  Timer: {Stopwatch.Frequency:N0} Hz ({tickUs:F4} µs/tick)  |  {(IntPtr.Size == 8 ? "x64" : "x86")}  |  Ctrl+C para encerrar");
        Console.Write(A.R);
        Console.WriteLine();
    }
}
