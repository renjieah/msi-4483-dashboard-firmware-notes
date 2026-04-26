using Microsoft.Win32.SafeHandles;

namespace PanelRuntime.Services;

public sealed class Nuc126KeepAlive : IDisposable
{
    private readonly Action<string> _log;
    private readonly object _sync = new();
    private SafeFileHandle? _handle;
    private int _reportLength = 64;
    private CancellationTokenSource? _cts;
    private Task? _loopTask;

    public int Sent { get; private set; }
    public int Failed { get; private set; }
    public string State { get; private set; } = "stopped";

    public Nuc126KeepAlive(Action<string> log)
    {
        _log = log;
    }

    public bool Open()
    {
        lock (_sync)
        {
            CloseHandleLocked();
            foreach (var pid in NativeHid.PidNuc126List)
            {
                var path = NativeHid.FindDevicePath(NativeHid.VidMsi, pid);
                if (path is null) continue;

                _handle = NativeHid.OpenReadWrite(path);
                _reportLength = Math.Max(8, NativeHid.GetOutputReportLength(_handle));
                State = $"open PID=0x{pid:X4}";
                _log($"NUC126 opened: PID=0x{pid:X4} report={_reportLength}");
                return true;
            }
        }

        State = "not found";
        _log("NUC126 not found");
        return false;
    }

    public void Wake()
    {
        if (_handle is null || _handle.IsInvalid)
        {
            if (!Open()) return;
        }

        Send(0xE3, 0);
        Thread.Sleep(500);
        Send(0xE5, 0);
        Thread.Sleep(1000);
    }

    public void Start(TimeSpan interval, CancellationToken externalToken)
    {
        if (_loopTask is not null) return;
        if (_handle is null || _handle.IsInvalid)
        {
            Open();
        }

        _cts = CancellationTokenSource.CreateLinkedTokenSource(externalToken);
        _loopTask = Task.Run(async () =>
        {
            var consecutiveFailures = 0;
            while (!_cts.IsCancellationRequested)
            {
                try
                {
                    if (Send(0xFF, 0))
                    {
                        Sent++;
                        consecutiveFailures = 0;
                        State = "running";
                    }
                    else
                    {
                        consecutiveFailures++;
                        Failed++;
                    }

                    if (consecutiveFailures == 3)
                    {
                        _log("NUC heartbeat failed 3 times; reopening NUC126");
                        Open();
                    }
                    else if (consecutiveFailures >= 10)
                    {
                        State = "degraded";
                    }
                }
                catch (Exception ex)
                {
                    Failed++;
                    consecutiveFailures++;
                    State = $"error: {ex.Message}";
                }

                try
                {
                    await Task.Delay(interval, _cts.Token);
                }
                catch (TaskCanceledException)
                {
                    break;
                }
            }
        }, _cts.Token);
    }

    public async Task StopAsync()
    {
        if (_cts is not null)
        {
            _cts.Cancel();
        }
        if (_loopTask is not null)
        {
            try { await _loopTask; } catch { }
        }
        _loopTask = null;
        _cts?.Dispose();
        _cts = null;
        State = "stopped";
    }

    private bool Send(byte command, byte parameter)
    {
        lock (_sync)
        {
            if (_handle is null || _handle.IsInvalid) return false;
            var raw = Enumerable.Repeat((byte)0xCC, _reportLength).ToArray();
            raw[0] = 1;
            raw[1] = command;
            raw[6] = parameter;
            NativeHid.WriteFileExact(_handle, raw);
        }
        return true;
    }

    private void CloseHandleLocked()
    {
        _handle?.Dispose();
        _handle = null;
    }

    public void Dispose()
    {
        _cts?.Cancel();
        try { _loopTask?.Wait(1000); } catch { }
        _cts?.Dispose();
        lock (_sync)
        {
            CloseHandleLocked();
        }
    }
}
