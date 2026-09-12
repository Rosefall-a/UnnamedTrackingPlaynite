using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace UnnamedTrackingPlaynite;

internal sealed class AfkTracker : IDisposable
{
    private const uint AfkThresholdSeconds = 300;
    private const int SampleIntervalMilliseconds = 1000;
    private const int MaxSampleSeconds = 5;

    private readonly object sync = new();
    private Timer? timer;
    private DateTime lastSampleUtc;
    private long afkSeconds;
    private bool running;

    public void Start()
    {
        lock (sync)
        {
            StopLocked();
            afkSeconds = 0;
            lastSampleUtc = DateTime.UtcNow;
            running = true;
            timer = new Timer(Sample, null, SampleIntervalMilliseconds, SampleIntervalMilliseconds);
        }
    }

    public long Stop()
    {
        lock (sync)
        {
            if (!running) return 0;
            SampleLocked(DateTime.UtcNow);
            running = false;
            timer?.Dispose();
            timer = null;
            return afkSeconds;
        }
    }

    public void Dispose()
    {
        lock (sync)
        {
            StopLocked();
        }
    }

    private void Sample(object? state)
    {
        lock (sync)
        {
            if (running) SampleLocked(DateTime.UtcNow);
        }
    }

    private void SampleLocked(DateTime nowUtc)
    {
        var elapsedSeconds = Math.Max(0, Math.Min(MaxSampleSeconds, (nowUtc - lastSampleUtc).TotalSeconds));
        lastSampleUtc = nowUtc;

        if (elapsedSeconds <= 0 || GetIdleSeconds() < AfkThresholdSeconds) return;
        afkSeconds += (long)Math.Round(elapsedSeconds, MidpointRounding.ToEven);
    }

    private void StopLocked()
    {
        running = false;
        timer?.Dispose();
        timer = null;
    }

    private static uint GetIdleSeconds()
    {
        var info = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf(typeof(LASTINPUTINFO)) };
        if (!GetLastInputInfo(ref info)) return 0;
        return (GetTickCount() - info.dwTime) / 1000;
    }

    [DllImport("user32.dll")]
    private static extern bool GetLastInputInfo(ref LASTINPUTINFO plii);

    [DllImport("kernel32.dll")]
    private static extern uint GetTickCount();

    [StructLayout(LayoutKind.Sequential)]
    private struct LASTINPUTINFO
    {
        public uint cbSize;
        public uint dwTime;
    }
}
