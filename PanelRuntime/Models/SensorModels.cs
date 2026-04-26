namespace PanelRuntime.Models;

public sealed record SensorValue(
    string Id,
    string Kind,
    string Label,
    string Raw,
    double? Value);

public sealed record SensorSnapshot(
    long UpdatedAt,
    bool Online,
    string? Error,
    Dictionary<string, SensorValue> Sensors);

public sealed record RuntimeStats(
    long Frames,
    double LastCaptureMs,
    double LastConvertMs,
    double LastSendMs,
    string TransportState,
    string? LastError);
