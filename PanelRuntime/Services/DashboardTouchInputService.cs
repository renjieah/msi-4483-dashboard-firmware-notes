using Microsoft.Win32.SafeHandles;
using PanelRuntime.Models;

namespace PanelRuntime.Services;

public sealed record DashboardTouchEvent(string Type, double X, double Y);

public sealed class DashboardTouchInputService : IAsyncDisposable
{
    private const int ReportSize = 64;
    private readonly RuntimeConfig _config;
    private readonly Func<DashboardTouchEvent, Task> _dispatch;
    private readonly Action<string> _log;
    private CancellationTokenSource? _cts;
    private Task? _task;
    private SafeFileHandle? _handle;
    private bool _lastDown;
    private int _lastX;
    private int _lastY;
    private int _failureLogCount;

    public DashboardTouchInputService(
        RuntimeConfig config,
        Func<DashboardTouchEvent, Task> dispatch,
        Action<string> log)
    {
        _config = config;
        _dispatch = dispatch;
        _log = log;
    }

    public void Start(CancellationToken parentToken)
    {
        if (_task is not null)
        {
            return;
        }

        _cts = CancellationTokenSource.CreateLinkedTokenSource(parentToken);
        _task = Task.Run(() => ReadLoopAsync(_cts.Token));
    }

    public async Task StopAsync()
    {
        var cts = _cts;
        if (cts is null)
        {
            return;
        }

        cts.Cancel();
        CloseHandle();
        if (_task is not null)
        {
            try
            {
                await _task.WaitAsync(TimeSpan.FromSeconds(2));
            }
            catch
            {
            }
        }

        cts.Dispose();
        _cts = null;
        _task = null;
        _lastDown = false;
    }

    private async Task ReadLoopAsync(CancellationToken token)
    {
        var missingLogged = false;
        while (!token.IsCancellationRequested)
        {
            try
            {
                var path = NativeHid.FindDevicePath(NativeHid.VidMsi, NativeHid.PidDashboard);
                if (path is null)
                {
                    if (!missingLogged)
                    {
                        _log("Dashboard touch input waiting for device.");
                        missingLogged = true;
                    }
                    await Task.Delay(1000, token);
                    continue;
                }

                missingLogged = false;
                _handle = NativeHid.OpenReadWrite(path);
                _failureLogCount = 0;
                _log("Dashboard touch input opened.");

                var buffer = new byte[ReportSize];
                while (!token.IsCancellationRequested && _handle is { IsInvalid: false })
                {
                    Array.Clear(buffer);
                    var read = NativeHid.ReadFileBlocking(_handle, buffer);
                    if (read > 0)
                    {
                        await ProcessReportAsync(buffer.AsMemory(0, Math.Min(read, buffer.Length)), token);
                    }
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                break;
            }
            catch (ObjectDisposedException) when (token.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                if (!token.IsCancellationRequested)
                {
                    _failureLogCount++;
                    if (_failureLogCount <= 3 || _failureLogCount % 10 == 0)
                    {
                        _log($"Dashboard touch input failed: {ex.Message}");
                    }
                    await Task.Delay(1000, token);
                }
            }
            finally
            {
                CloseHandle();
            }
        }
    }

    private async Task ProcessReportAsync(ReadOnlyMemory<byte> report, CancellationToken token)
    {
        var data = report.ToArray();
        if (data.Length < 8 || IsDashboardCommandResponse(data))
        {
            return;
        }

        var down = data[3] != 0;
        if (!down && !_lastDown)
        {
            return;
        }

        if (down)
        {
            var rawX = ReadInt16(data[4], data[5]);
            var rawY = ReadInt16(data[6], data[7]);
            var (x, y) = MapPoint(rawX, rawY);
            var ix = (int)Math.Round(x);
            var iy = (int)Math.Round(y);

            if (!_lastDown)
            {
                _lastDown = true;
                _lastX = ix;
                _lastY = iy;
                _log($"Touch down x={ix} y={iy}");
                await _dispatch(new DashboardTouchEvent("mousePressed", x, y));
                return;
            }

            if (Math.Abs(ix - _lastX) >= 2 || Math.Abs(iy - _lastY) >= 2)
            {
                _lastX = ix;
                _lastY = iy;
            }
            return;
        }

        token.ThrowIfCancellationRequested();
        _lastDown = false;
        _log($"Touch up x={_lastX} y={_lastY}");
        await _dispatch(new DashboardTouchEvent("mouseReleased", _lastX, _lastY));
    }

    private (double X, double Y) MapPoint(int rawX, int rawY)
    {
        var x = rawX;
        var y = rawY;
        if (_config.TouchAutoSwapXY && x > _config.Width && x <= _config.Height && y <= _config.Width)
        {
            (x, y) = (y, x);
        }

        return (
            Math.Clamp(x, 0, Math.Max(_config.Width - 1, 0)),
            Math.Clamp(y, 0, Math.Max(_config.Height - 1, 0)));
    }

    private void CloseHandle()
    {
        _handle?.Dispose();
        _handle = null;
    }

    private static bool IsDashboardCommandResponse(ReadOnlySpan<byte> data)
    {
        return (data.Length >= 2 && data[0] == 0x6B && data[1] == 0x5A)
            || (data.Length >= 3 && data[1] == 0x6B && data[2] == 0x5A);
    }

    private static int ReadInt16(byte lo, byte hi)
    {
        var value = lo | (hi << 8);
        return value >= 0x8000 ? value - 0x10000 : value;
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
    }
}
