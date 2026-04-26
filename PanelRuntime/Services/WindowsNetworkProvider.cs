using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using PanelRuntime.Models;

namespace PanelRuntime.Services;

public sealed class WindowsNetworkProvider
{
    private readonly Dictionary<string, string> _aliases;
    private readonly Dictionary<string, CounterSample> _previous = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new();

    public WindowsNetworkProvider(IReadOnlyDictionary<string, string>? aliases)
    {
        _aliases = aliases is { Count: > 0 }
            ? new Dictionary<string, string>(aliases, StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["wifi"] = "WLAN",
                ["ethernet10g"] = "以太网",
                ["ethernet25g"] = "以太网 2"
            };
    }

    public NetworkSnapshot ReadSnapshot()
    {
        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var adapters = NetworkInterface.GetAllNetworkInterfaces();
        var values = new Dictionary<string, NetworkAdapterValue>(StringComparer.OrdinalIgnoreCase);

        lock (_lock)
        {
            foreach (var (key, alias) in _aliases)
            {
                var adapter = FindAdapter(adapters, alias);
                values[key] = adapter is null
                    ? MissingValue(key, alias)
                    : ReadAdapter(key, alias, adapter, nowMs);
            }
        }

        return new NetworkSnapshot(nowMs, values);
    }

    private NetworkAdapterValue ReadAdapter(string key, string alias, NetworkInterface adapter, long nowMs)
    {
        try
        {
            var stats = adapter.GetIPv4Statistics();
            var rx = Math.Max(0L, stats.BytesReceived);
            var tx = Math.Max(0L, stats.BytesSent);
            var sample = new CounterSample(rx, tx, nowMs);

            var rxPerSec = 0.0;
            var txPerSec = 0.0;
            if (_previous.TryGetValue(key, out var previous))
            {
                var elapsed = Math.Max((nowMs - previous.TimestampMs) / 1000.0, 0.001);
                rxPerSec = Math.Max(0.0, (rx - previous.RxBytes) / elapsed);
                txPerSec = Math.Max(0.0, (tx - previous.TxBytes) / elapsed);
            }
            _previous[key] = sample;

            return new NetworkAdapterValue(
                key,
                alias,
                adapter.Name,
                adapter.Description,
                adapter.OperationalStatus.ToString(),
                SafeSpeed(adapter),
                FirstIpv4(adapter),
                rxPerSec,
                txPerSec,
                FormatRate(rxPerSec),
                FormatRate(txPerSec));
        }
        catch
        {
            return new NetworkAdapterValue(
                key,
                alias,
                adapter.Name,
                adapter.Description,
                "Error",
                0,
                "",
                0,
                0,
                FormatRate(0),
                FormatRate(0));
        }
    }

    private static NetworkAdapterValue MissingValue(string key, string alias)
    {
        return new NetworkAdapterValue(
            key,
            alias,
            "",
            "",
            "Missing",
            0,
            "",
            0,
            0,
            FormatRate(0),
            FormatRate(0));
    }

    private static NetworkInterface? FindAdapter(NetworkInterface[] adapters, string alias)
    {
        return adapters.FirstOrDefault(adapter => string.Equals(adapter.Name, alias, StringComparison.OrdinalIgnoreCase))
            ?? adapters.FirstOrDefault(adapter => string.Equals(adapter.Description, alias, StringComparison.OrdinalIgnoreCase))
            ?? adapters.FirstOrDefault(adapter => string.Equals(adapter.Id, alias, StringComparison.OrdinalIgnoreCase))
            ?? adapters.FirstOrDefault(adapter => adapter.Name.Contains(alias, StringComparison.OrdinalIgnoreCase))
            ?? adapters.FirstOrDefault(adapter => adapter.Description.Contains(alias, StringComparison.OrdinalIgnoreCase));
    }

    private static long SafeSpeed(NetworkInterface adapter)
    {
        try
        {
            return Math.Max(0L, adapter.Speed);
        }
        catch
        {
            return 0;
        }
    }

    private static string FirstIpv4(NetworkInterface adapter)
    {
        try
        {
            return adapter.GetIPProperties().UnicastAddresses
                .Where(address => address.Address.AddressFamily == AddressFamily.InterNetwork)
                .Select(address => address.Address)
                .FirstOrDefault(address => !IPAddress.IsLoopback(address) && !address.ToString().StartsWith("169.254.", StringComparison.Ordinal))?
                .ToString() ?? "";
        }
        catch
        {
            return "";
        }
    }

    private static string FormatRate(double bytesPerSec)
    {
        string[] units = ["B/s", "K/s", "M/s", "G/s"];
        var value = Math.Max(0.0, bytesPerSec);
        var unit = 0;
        while (value >= 1024.0 && unit < units.Length - 1)
        {
            value /= 1024.0;
            unit++;
        }

        return unit == 0
            ? $"{value:0}{units[unit]}"
            : $"{value:0.0}{units[unit]}";
    }

    private sealed record CounterSample(long RxBytes, long TxBytes, long TimestampMs);
}
