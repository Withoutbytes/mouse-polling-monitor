using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace MousePollingMonitor;

internal readonly record struct MousePacket(long Ticks, int Dx, int Dy, bool HasMovement);

public enum Verdict { Waiting, Natural, Suspicious, Injection }

public sealed record EngineStats(
    int      InstantHz,
    double   AvgHz,
    double   StdDev,
    double   BurstRate,
    long     TotalPackets,
    long     TotalBursts,
    int      FilledSamples,
    Verdict  Verdict,
    double[] Spark,
    bool[]   SparkBurst,
    int      SparkCount
);

internal sealed class MouseEngine
{
    private const double BurstThreshMs     = 0.5;
    private const double StdDevInjecao     = 0.015;
    private const double StdDevSuspeito    = 0.060;
    private const double BurstRateInjecao  = 0.20;
    private const double BurstRateSuspeito = 0.10;
    private const int    MinSamples        = 30;
    private const int    WindowSize        = 100;
    public  const int    SparkLen          = 60;

    private readonly double[] _intervals   = new double[WindowSize];
    private readonly bool[]   _bursts      = new bool[WindowSize];
    private int    _head         = 0;
    private int    _filled       = 0;
    private int    _windowBursts = 0;
    private double _windowSum    = 0;

    private readonly double[] _spark      = new double[SparkLen];
    private readonly bool[]   _sparkBurst = new bool[SparkLen];
    private int _sparkCount = 0;

    // Instantaneous Hz: count events that arrived in the last 1 second
    private readonly Queue<long> _instantTicks = new();

    private readonly ConcurrentQueue<MousePacket> _queue = new();
    private readonly Stopwatch _sw = Stopwatch.StartNew();
    private volatile bool _alive = true;
    private Win32.WndProcDelegate? _wndProcDelegate;

    private long _totalMovPkts = 0;
    private long _totalBursts  = 0;
    private long _prevTicks    = 0;

    private readonly Stopwatch _renderThrottle = Stopwatch.StartNew();
    private const long RenderIntervalMs = 50;

    public event Action<EngineStats>? StatsUpdated;

    public void Start()
    {
        new Thread(ProcessorLoop)
        {
            IsBackground = true,
            Name         = "PacketProcessor",
            Priority     = ThreadPriority.BelowNormal
        }.Start();

        new Thread(MessageLoopThread)
        {
            IsBackground = true,
            Name         = "Win32MsgLoop"
        }.Start();
    }

    public void Stop() => _alive = false;

    private void MessageLoopThread()
    {
        _wndProcDelegate = WndProc;
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
        if (hwnd == IntPtr.Zero) return;

        Win32.RegisterRawInputDevices(new[] { new Win32.RAWINPUTDEVICE
        {
            usUsagePage = 0x01,
            usUsage     = 0x02,
            dwFlags     = Win32.RIDEV_INPUTSINK,
            hwndTarget  = hwnd
        }}, 1, (uint)Marshal.SizeOf<Win32.RAWINPUTDEVICE>());

        Win32.MSG msg;
        while (_alive && Win32.GetMessageW(out msg, IntPtr.Zero, 0, 0) > 0)
        {
            Win32.TranslateMessage(ref msg);
            Win32.DispatchMessageW(ref msg);
        }
    }

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

        byte* m = buf + hdrSz;
        int dx = *(int*)(m + 12);
        int dy = *(int*)(m + 16);
        _queue.Enqueue(new MousePacket(_sw.ElapsedTicks, dx, dy, dx != 0 || dy != 0));
    }

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
        // Track all packets for Hz measurement (hardware polls even without movement)
        long nowTicks = p.Ticks;
        long cutoff   = nowTicks - Stopwatch.Frequency; // 1 second ago
        _instantTicks.Enqueue(nowTicks);
        while (_instantTicks.Count > 0 && _instantTicks.Peek() < cutoff)
            _instantTicks.Dequeue();

        if (_prevTicks == 0) { _prevTicks = p.Ticks; return; }

        double dt = (p.Ticks - _prevTicks) * 1000.0 / Stopwatch.Frequency;
        _prevTicks = p.Ticks;
        if (dt <= 0) return;

        // Burst/injection analysis only applies to movement packets
        if (!p.HasMovement)
        {
            if (_renderThrottle.ElapsedMilliseconds >= RenderIntervalMs)
            {
                _renderThrottle.Restart();
                FireStats();
            }
            return;
        }
        _totalMovPkts++;

        bool burst = dt < BurstThreshMs;
        if (burst) _totalBursts++;

        int slot = _head;
        if (_filled == WindowSize) { _windowSum -= _intervals[slot]; if (_bursts[slot]) _windowBursts--; }
        else _filled++;
        _intervals[slot] = dt; _bursts[slot] = burst;
        _windowSum += dt; if (burst) _windowBursts++;
        _head = (slot + 1) % WindowSize;

        if (_sparkCount < SparkLen)
            { _spark[_sparkCount] = dt; _sparkBurst[_sparkCount] = burst; _sparkCount++; }
        else
        {
            Array.Copy(_spark,      1, _spark,      0, SparkLen - 1);
            Array.Copy(_sparkBurst, 1, _sparkBurst, 0, SparkLen - 1);
            _spark[SparkLen - 1] = dt; _sparkBurst[SparkLen - 1] = burst;
        }

        if (_renderThrottle.ElapsedMilliseconds >= RenderIntervalMs)
        {
            _renderThrottle.Restart();
            FireStats();
        }
    }

    private void FireStats()
    {
        var (_, stdDev, burstRate, avgHz) = ComputeStats();
        var verdict = EvaluateVerdict(stdDev, burstRate);

        StatsUpdated?.Invoke(new EngineStats(
            _instantTicks.Count, avgHz, stdDev, burstRate,
            _totalMovPkts, _totalBursts, _filled, verdict,
            (double[])_spark.Clone(), (bool[])_sparkBurst.Clone(), _sparkCount
        ));
    }

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
        if (_filled < MinSamples)                                         return Verdict.Waiting;
        if (burstRate >= BurstRateInjecao  || stdDev <= StdDevInjecao)   return Verdict.Injection;
        if (burstRate >= BurstRateSuspeito || stdDev <= StdDevSuspeito)  return Verdict.Suspicious;
        return Verdict.Natural;
    }
}
