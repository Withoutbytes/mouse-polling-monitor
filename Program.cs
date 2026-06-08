using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using MousePollingMonitor;

internal readonly record struct MousePacket(long Ticks, int Dx, int Dy, bool HasMovement);

internal enum Verdict { Aguardando, Natural, Suspeito, Injecao }

internal sealed class MouseMonitor
{
    // ── Detection thresholds (empirically calibrated) ─────────────────────────
    private const double BurstThreshMs     = 0.5;   // below this = burst (USB physically impossible)
    private const double StdDevInjecao     = 0.015; // ms — inhuman precision
    private const double StdDevSuspeito    = 0.060; // ms — suspiciously low jitter
    private const double BurstRateInjecao  = 0.20;  // 20% — impossible on real hardware
    private const double BurstRateSuspeito = 0.10;  // 10% — aggressive but possible
    private const int    MinSamples        = 30;    // minimum samples before issuing verdict

    // ── Rolling window (100 packets) ──────────────────────────────────────────
    private const int WindowSize = 100;
    private readonly double[] _intervals   = new double[WindowSize];
    private readonly bool[]   _bursts      = new bool[WindowSize];
    private int    _head         = 0;
    private int    _filled       = 0;
    private int    _windowBursts = 0;
    private double _windowSum    = 0;

    // ── Sparkline buffer (last 60 packets, insertion order) ───────────────────
    private const int SparkLen = 60;
    private readonly double[] _spark      = new double[SparkLen];
    private readonly bool[]   _sparkBurst = new bool[SparkLen];
    private int _sparkCount = 0;

    // ── Histogram buckets ─────────────────────────────────────────────────────
    // < 0.5ms | 0.5–1.5ms | 1.5–3ms | 3–6ms | 6–12ms | > 12ms
    private readonly int[] _hist = new int[6];

    // ── Pipeline ──────────────────────────────────────────────────────────────
    private readonly ConcurrentQueue<MousePacket> _queue = new();
    private readonly Stopwatch _sw = Stopwatch.StartNew();
    private volatile bool _alive = true;
    private readonly Win32.WndProcDelegate _wndProcDelegate;

    private long _totalMovPkts = 0;
    private long _totalBursts  = 0;
    private long _prevTicks    = 0;
    private int  _dashRow      = -1;

    // ─────────────────────────────────────────────────────────────────────────

    public static void Main()
    {
        Console.OutputEncoding = Encoding.UTF8;
        new MouseMonitor().Run();
    }

    private MouseMonitor() { _wndProcDelegate = WndProc; }

    private void Run()
    {
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; _alive = false; Win32.PostQuitMessage(0); };
        PrintHeader();

        new Thread(ProcessorLoop)
        {
            IsBackground = true,
            Name = "PacketProcessor",
            Priority = ThreadPriority.BelowNormal
        }.Start();

        SetupAndRunMessageLoop();
        _alive = false;
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

        if (hwnd == IntPtr.Zero) { Console.WriteLine($"  [ERRO] CreateWindowEx ({Marshal.GetLastWin32Error()})"); return; }

        var rid = new[] { new Win32.RAWINPUTDEVICE
        {
            usUsagePage = 0x01, usUsage = 0x02,
            dwFlags = Win32.RIDEV_INPUTSINK, hwndTarget = hwnd
        }};

        if (!Win32.RegisterRawInputDevices(rid, 1, (uint)Marshal.SizeOf<Win32.RAWINPUTDEVICE>()))
        { Console.WriteLine($"  [ERRO] RegisterRawInputDevices ({Marshal.GetLastWin32Error()})"); return; }

        Win32.MSG msg;
        while (Win32.GetMessageW(out msg, IntPtr.Zero, 0, 0) > 0)
        {
            Win32.TranslateMessage(ref msg);
            Win32.DispatchMessageW(ref msg);
        }
    }

    // ── WndProc — HOT PATH: zero alloc, zero I/O ─────────────────────────────

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

        // RAWMOUSE — MSVC 4-byte aligned layout (sizeof = 24):
        //   +0 usFlags(2) +2 pad(2) +4 ulButtons(4) +8 ulRawButtons(4)
        //   +12 lLastX(4)  +16 lLastY(4)  +20 ulExtraInfo(4)
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

        // ── Rolling window ────────────────────────────────────────────────────
        int slot = _head;
        if (_filled == WindowSize) { _windowSum -= _intervals[slot]; if (_bursts[slot]) _windowBursts--; }
        else _filled++;

        _intervals[slot] = dt;
        _bursts[slot]    = burst;
        _windowSum      += dt;
        if (burst) _windowBursts++;
        _head = (slot + 1) % WindowSize;

        // ── Sparkline (insertion order) ───────────────────────────────────────
        if (_sparkCount < SparkLen)
        {
            _spark[_sparkCount] = dt;
            _sparkBurst[_sparkCount] = burst;
            _sparkCount++;
        }
        else
        {
            Array.Copy(_spark,      1, _spark,      0, SparkLen - 1);
            Array.Copy(_sparkBurst, 1, _sparkBurst, 0, SparkLen - 1);
            _spark[SparkLen - 1]      = dt;
            _sparkBurst[SparkLen - 1] = burst;
        }

        // ── Histogram bucket ──────────────────────────────────────────────────
        // Recompute from scratch each refresh (simple, window is only 100 items)

        if (_totalMovPkts % 10 == 0)
            RefreshDashboard();
    }

    // ── Dashboard ─────────────────────────────────────────────────────────────

    private void RefreshDashboard()
    {
        var (avgDt, stdDev, burstRate, avgHz) = ComputeStats();
        Verdict verdict = EvaluateVerdict(stdDev, burstRate);

        if (_dashRow < 0)
        {
            _dashRow = Console.CursorTop;
            for (int i = 0; i < 27; i++) Console.WriteLine();
        }
        try { Console.SetCursorPosition(0, _dashRow); } catch { return; }

        // ── Metrics ───────────────────────────────────────────────────────────
        SectionLine("ANÁLISE — JANELA DE 100 PACOTES");
        MetricLine("  Frequência     :",
            _filled >= 2 ? $"{avgHz,7:F1} Hz  (Δt médio: {avgDt:F3} ms)" : "aguardando...",
            ConsoleColor.White);
        MetricLine("  Desvio padrão  :",
            _filled >= 2 ? $"{stdDev,7:F3} ms  ({DescribeStdDev(stdDev)})" : "aguardando...",
            StdDevColor(stdDev));
        MetricLine("  Bursts / janela:",
            _filled >= 2 ? $"{_windowBursts,4} / {_filled,-4}  ({burstRate * 100:F1}%)" : "aguardando...",
            BurstColor(burstRate));
        MetricLine("  Total capturado:",
            $"{_totalMovPkts} pacotes  ({_totalBursts} bursts acumulados)",
            ConsoleColor.DarkGray);

        Console.WriteLine();

        // ── Sparkline ─────────────────────────────────────────────────────────
        SectionLine("HISTÓRICO — últimos 60 pacotes  (barra curta vermelha = burst)");
        Console.Write("  ┌");
        Console.Write(new string('─', SparkLen));
        Console.WriteLine("┐");
        Console.Write("  │");
        RenderSparkline(avgDt);
        Console.WriteLine("│");
        Console.Write("  └");
        Console.Write(new string('─', SparkLen));
        Console.WriteLine("┘");
        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.WriteLine($"  escala: 0 ms (baixo/burst) → {avgDt * 2.5:F1} ms (alto)                          ");
        Console.ResetColor();

        Console.WriteLine();

        // ── Histogram ─────────────────────────────────────────────────────────
        SectionLine("DISTRIBUIÇÃO DE INTERVALOS");
        RenderHistogram();

        Console.WriteLine();

        // ── Verdict ───────────────────────────────────────────────────────────
        (string icon, string title, string subtitle, ConsoleColor color) = verdict switch
        {
            Verdict.Natural  => ("✓", "SINAL NATURAL",    "Jitter físico detectado — hardware real",               ConsoleColor.Green),
            Verdict.Suspeito => ("?", "SINAL SUSPEITO",   "Jitter baixo ou bursts elevados — investigar",          ConsoleColor.Yellow),
            Verdict.Injecao  => ("!", "INJEÇÃO DETECTADA","Padrão fisicamente impossível para USB real",            ConsoleColor.Red),
            _                => ("…", "AGUARDANDO DADOS", $"Mova o mouse — {_filled}/{MinSamples} amostras",       ConsoleColor.DarkGray),
        };

        Console.ForegroundColor = color;
        Console.WriteLine("  ┌────────────────────────────────────────────────────────────────┐");
        Console.WriteLine("  │                                                                │");
        Console.WriteLine($"  │    {icon}  {title,-58}│");
        Console.WriteLine($"  │       {subtitle,-57}│");
        Console.WriteLine("  │                                                                │");
        Console.WriteLine("  └────────────────────────────────────────────────────────────────┘");
        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.WriteLine(
            $"  Natural: bursts<10% e σ≥0.060ms  " +
            $"│ Suspeito: 10–20% ou σ<0.060ms  " +
            $"│ Injeção: >20% ou σ≤0.015ms     ");
        Console.ResetColor();
    }

    private void RenderSparkline(double avgDt)
    {
        double scaleMax = Math.Max(avgDt * 2.5, 1.0);
        char[] blocks = { ' ', '▁', '▂', '▃', '▄', '▅', '▆', '▇', '█' };

        for (int i = 0; i < _sparkCount; i++)
        {
            if (_sparkBurst[i])
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.Write('▁');
            }
            else
            {
                int level = (int)Math.Clamp(Math.Ceiling(_spark[i] / scaleMax * 8), 1, 8);
                Console.ForegroundColor = ConsoleColor.Green;
                Console.Write(blocks[level]);
            }
        }
        for (int i = _sparkCount; i < SparkLen; i++)
        {
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.Write('·');
        }
        Console.ResetColor();
    }

    private void RenderHistogram()
    {
        // Recompute buckets from rolling window
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

        string[] labels = { "< 0.5 ms  [BURST]", "0.5–1.5 ms ~1000Hz", "1.5–3 ms   ~500Hz", "3–6 ms     ~250Hz", "6–12 ms    ~125Hz", "> 12 ms    lento " };
        ConsoleColor[] colors = { ConsoleColor.Red, ConsoleColor.Green, ConsoleColor.Green, ConsoleColor.DarkGreen, ConsoleColor.DarkGray, ConsoleColor.DarkGray };

        int maxCount = Math.Max(1, counts.Max());
        const int barWidth = 28;

        for (int b = 0; b < 6; b++)
        {
            double pct    = _filled > 0 ? counts[b] * 100.0 / _filled : 0;
            int    filled = (int)Math.Round((double)counts[b] / maxCount * barWidth);

            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.Write($"  {labels[b]}  ");
            Console.ForegroundColor = colors[b];
            Console.Write(new string('█', filled));
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.Write(new string('░', barWidth - filled));
            Console.ForegroundColor = ConsoleColor.Gray;
            Console.WriteLine($"  {counts[b],4} ({pct,5:F1}%)");
        }
        Console.ResetColor();
    }

    // ── Statistics helpers ────────────────────────────────────────────────────

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

    private static string DescribeStdDev(double sd) =>
        sd <= StdDevInjecao  ? "precisão inumana — injeção" :
        sd <= StdDevSuspeito ? "jitter suspeitosamente baixo" :
                               "jitter natural";

    // ── Color helpers ─────────────────────────────────────────────────────────

    private static ConsoleColor StdDevColor(double sd) =>
        sd <= StdDevInjecao  ? ConsoleColor.Red :
        sd <= StdDevSuspeito ? ConsoleColor.Yellow :
                               ConsoleColor.Green;

    private static ConsoleColor BurstColor(double rate) =>
        rate >= BurstRateInjecao  ? ConsoleColor.Red :
        rate >= BurstRateSuspeito ? ConsoleColor.Yellow :
                                    ConsoleColor.Green;

    // ── Layout helpers ────────────────────────────────────────────────────────

    private static void SectionLine(string title)
    {
        Console.ForegroundColor = ConsoleColor.DarkCyan;
        Console.WriteLine($"  ── {title} ──".PadRight(70));
        Console.ResetColor();
    }

    private static void MetricLine(string label, string value, ConsoleColor valueColor)
    {
        Console.ForegroundColor = ConsoleColor.Gray;
        Console.Write($"{label,-22}");
        Console.ForegroundColor = valueColor;
        Console.WriteLine($" {value,-52}");
        Console.ResetColor();
    }

    // ── Initial header ────────────────────────────────────────────────────────

    private static void PrintHeader()
    {
        try { Console.Clear(); } catch { }
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine("╔══════════════════════════════════════════════════════════════════╗");
        Console.WriteLine("║        MOUSE POLLING RATE MONITOR  v1.0                         ║");
        Console.WriteLine("║        Raw Input Diagnostic Tool — Detecção de Injeção          ║");
        Console.WriteLine("╚══════════════════════════════════════════════════════════════════╝");
        Console.ResetColor();
        double tickUs = 1_000_000.0 / Stopwatch.Frequency;
        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.WriteLine($"  Timer: {Stopwatch.Frequency:N0} Hz ({tickUs:F4} µs/tick)  |  {(IntPtr.Size == 8 ? "x64" : "x86")}  |  Ctrl+C para encerrar");
        Console.ResetColor();
        Console.WriteLine();
    }
}
