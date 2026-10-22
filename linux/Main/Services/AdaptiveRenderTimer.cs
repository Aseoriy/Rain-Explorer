using System.Diagnostics;
using Avalonia.Rendering;

namespace RainExplorer.Services;

/// <summary>A background render clock whose cadence can change without replacing the compositor.</summary>
internal sealed class AdaptiveRenderTimer : IRenderTimer, IDisposable
{
    private readonly object _gate = new();
    private readonly AutoResetEvent _wake = new(false);
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private Action<TimeSpan>? _tick;
    private Thread? _thread;
    private int _framesPerSecond;
    private bool _disposed;

    public AdaptiveRenderTimer(int framesPerSecond) => FramesPerSecond = framesPerSecond;

    public int FramesPerSecond
    {
        get => Volatile.Read(ref _framesPerSecond);
        set
        {
            if (value is < 1 or > 360) throw new ArgumentOutOfRangeException(nameof(value));
            lock (_gate)
            {
                if (_disposed) return;
                Volatile.Write(ref _framesPerSecond, value);
                _wake.Set();
            }
        }
    }

    public bool RunsInBackground => true;

    public event Action<TimeSpan> Tick
    {
        add
        {
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                _tick += value;
                if (_thread is null)
                {
                    _thread = new Thread(Run) { IsBackground = true, Name = "Rain render clock" };
                    _thread.Start();
                }
                _wake.Set();
            }
        }
        remove
        {
            lock (_gate)
            {
                _tick -= value;
                if (!_disposed) _wake.Set();
            }
        }
    }

    private void Run()
    {
        var next = _clock.Elapsed;
        int previousRate = 0;
        try
        {
            while (true)
            {
                Action<TimeSpan>? tick;
                lock (_gate)
                {
                    if (_disposed) return;
                    tick = _tick;
                }
                if (tick is null)
                {
                    _wake.WaitOne();
                    previousRate = 0;
                    continue;
                }

                int rate = FramesPerSecond;
                var interval = TimeSpan.FromSeconds(1d / rate);
                var now = _clock.Elapsed;
                if (rate != previousRate)
                {
                    next = now + interval;
                    previousRate = rate;
                }
                var remaining = next - now;
                if (remaining > TimeSpan.Zero)
                {
                    // Sleep to an absolute deadline: integer-millisecond sleeps otherwise
                    // turn e.g. 280 Hz into a slower fixed 4 ms cadence. Never busy-spin.
                    _wake.WaitOne(Math.Max(1, (int)Math.Ceiling(remaining.TotalMilliseconds)));
                    continue;
                }

                tick(now);
                next += interval;
                now = _clock.Elapsed;
                // A slow frame must not create a burst of catch-up frames.
                if (next < now - interval) next = now + interval;
            }
        }
        finally { _wake.Dispose(); }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _tick = null;
            if (_thread is null) _wake.Dispose();
            else _wake.Set();
        }
    }
}
