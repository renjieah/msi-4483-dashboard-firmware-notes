namespace PanelRuntime.Models;

public sealed record NetworkAdapterValue(
    string Key,
    string Alias,
    string Name,
    string Description,
    string Status,
    long LinkSpeedBps,
    string IpAddress,
    double RxBytesPerSec,
    double TxBytesPerSec,
    string RxText,
    string TxText);

public sealed record NetworkSnapshot(
    long UpdatedAt,
    Dictionary<string, NetworkAdapterValue> Adapters);
