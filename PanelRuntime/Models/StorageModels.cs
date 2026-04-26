namespace PanelRuntime.Models;

public sealed record StorageVolumeValue(
    string Name,
    string Label,
    string DriveType,
    string DiskNumber,
    string DiskModel,
    long TotalBytes,
    long FreeBytes,
    long UsedBytes,
    double UsedPercent,
    string TotalText,
    string FreeText,
    string UsedText);

public sealed record StorageDiskValue(
    string DiskNumber,
    string Model,
    string SerialNumber,
    string Health,
    string OperationalStatus,
    string BusType,
    string MediaType,
    long SizeBytes,
    string SizeText,
    List<string> Volumes);

public sealed record StorageSnapshot(
    long UpdatedAt,
    long DiskInfoUpdatedAt,
    string DiskInfoError,
    List<StorageVolumeValue> Volumes,
    List<StorageDiskValue> Disks);
