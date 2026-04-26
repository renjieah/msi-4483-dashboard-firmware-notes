using System.Diagnostics;
using System.IO;
using System.Text.Json;
using PanelRuntime.Models;

namespace PanelRuntime.Services;

public sealed class WindowsStorageProvider
{
    private readonly TimeSpan _diskInfoRefreshInterval;
    private readonly object _cacheLock = new();
    private List<DiskInfo> _diskInfo = [];
    private DateTimeOffset _diskInfoUpdatedAt = DateTimeOffset.MinValue;
    private string _diskInfoError = "";

    public WindowsStorageProvider(TimeSpan? diskInfoRefreshInterval = null)
    {
        _diskInfoRefreshInterval = diskInfoRefreshInterval ?? TimeSpan.FromMinutes(1);
    }

    public StorageSnapshot ReadSnapshot()
    {
        var diskInfo = GetCachedDiskInfo();
        var diskByLetter = diskInfo
            .SelectMany(disk => disk.Volumes.Select(volume => new { Volume = volume, Disk = disk }))
            .GroupBy(item => item.Volume, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First().Disk, StringComparer.OrdinalIgnoreCase);

        var volumes = new List<StorageVolumeValue>();
        foreach (var drive in DriveInfo.GetDrives())
        {
            if (!drive.IsReady || drive.DriveType is not DriveType.Fixed)
            {
                continue;
            }

            var name = drive.Name.TrimEnd('\\');
            diskByLetter.TryGetValue(name, out var disk);
            var total = Math.Max(0L, drive.TotalSize);
            var free = Math.Max(0L, drive.AvailableFreeSpace);
            var used = Math.Max(0L, total - free);
            var usedPercent = total > 0 ? used * 100.0 / total : 0.0;
            volumes.Add(new StorageVolumeValue(
                name,
                drive.VolumeLabel,
                drive.DriveType.ToString(),
                disk?.DiskNumber ?? "",
                disk?.Model ?? "",
                total,
                free,
                used,
                usedPercent,
                FormatBytes(total),
                FormatBytes(free),
                FormatBytes(used)));
        }

        var disks = BuildDiskValues(diskInfo, volumes);
        return new StorageSnapshot(
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            _diskInfoUpdatedAt == DateTimeOffset.MinValue ? 0 : _diskInfoUpdatedAt.ToUnixTimeMilliseconds(),
            _diskInfoError,
            volumes,
            disks);
    }

    private List<StorageDiskValue> BuildDiskValues(IReadOnlyList<DiskInfo> diskInfo, IReadOnlyList<StorageVolumeValue> volumes)
    {
        var disks = new List<StorageDiskValue>();
        foreach (var disk in diskInfo.OrderBy(disk => ParseDiskNumber(disk.DiskNumber)))
        {
            var diskVolumes = volumes
                .Where(volume => string.Equals(volume.DiskNumber, disk.DiskNumber, StringComparison.OrdinalIgnoreCase))
                .Select(volume => volume.Name)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(volume => volume, StringComparer.OrdinalIgnoreCase)
                .ToList();

            disks.Add(new StorageDiskValue(
                disk.DiskNumber,
                disk.Model,
                disk.SerialNumber,
                disk.Health,
                disk.OperationalStatus,
                disk.BusType,
                disk.MediaType,
                disk.SizeBytes,
                FormatBytes(disk.SizeBytes),
                diskVolumes));
        }

        var unmatched = volumes
            .Where(volume => string.IsNullOrWhiteSpace(volume.DiskNumber))
            .OrderBy(volume => volume.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (disks.Count == 0 && unmatched.Count > 0)
        {
            disks.Add(new StorageDiskValue(
                "",
                "Windows Storage",
                "",
                "",
                "",
                "",
                "",
                unmatched.Sum(volume => volume.TotalBytes),
                FormatBytes(unmatched.Sum(volume => volume.TotalBytes)),
                unmatched.Select(volume => volume.Name).ToList()));
        }

        return disks;
    }

    private List<DiskInfo> GetCachedDiskInfo()
    {
        lock (_cacheLock)
        {
            if (_diskInfoUpdatedAt != DateTimeOffset.MinValue &&
                DateTimeOffset.UtcNow - _diskInfoUpdatedAt < _diskInfoRefreshInterval)
            {
                return _diskInfo;
            }
        }

        var (items, error) = ProbeDiskInfo();
        lock (_cacheLock)
        {
            _diskInfoUpdatedAt = DateTimeOffset.UtcNow;
            _diskInfoError = error;
            if (items.Count > 0 || string.IsNullOrWhiteSpace(error))
            {
                _diskInfo = items;
            }
            return _diskInfo;
        }
    }

    private static (List<DiskInfo> Items, string Error) ProbeDiskInfo()
    {
        const string script = """
            $ErrorActionPreference='Stop'
            $items = foreach ($volume in (Get-Volume | Where-Object { $_.DriveLetter })) {
              $partition = Get-Partition -DriveLetter $volume.DriveLetter -ErrorAction SilentlyContinue | Select-Object -First 1
              if ($null -eq $partition) { continue }
              $disk = Get-Disk -Number $partition.DiskNumber -ErrorAction SilentlyContinue
              if ($null -eq $disk) { continue }
              [pscustomobject]@{
                DiskNumber = [string]$disk.Number
                Model = [string]$disk.FriendlyName
                SerialNumber = [string]$disk.SerialNumber
                Health = [string]$disk.HealthStatus
                OperationalStatus = [string]($disk.OperationalStatus -join ',')
                BusType = [string]$disk.BusType
                MediaType = [string]$disk.MediaType
                SizeBytes = [int64]$disk.Size
                DriveLetter = ([string]$volume.DriveLetter) + ':'
              }
            }
            $items | ConvertTo-Json -Compress
            """;

        try
        {
            using var process = new Process();
            process.StartInfo.FileName = "powershell.exe";
            process.StartInfo.ArgumentList.Add("-NoProfile");
            process.StartInfo.ArgumentList.Add("-ExecutionPolicy");
            process.StartInfo.ArgumentList.Add("Bypass");
            process.StartInfo.ArgumentList.Add("-Command");
            process.StartInfo.ArgumentList.Add(script);
            process.StartInfo.UseShellExecute = false;
            process.StartInfo.RedirectStandardOutput = true;
            process.StartInfo.RedirectStandardError = true;
            process.StartInfo.CreateNoWindow = true;

            process.Start();
            var outputTask = process.StandardOutput.ReadToEndAsync();
            var errorTask = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(10000))
            {
                process.Kill(entireProcessTree: true);
                return ([], "storage probe timeout");
            }

            var output = outputTask.GetAwaiter().GetResult();
            var error = errorTask.GetAwaiter().GetResult();
            if (process.ExitCode != 0)
            {
                return ([], string.IsNullOrWhiteSpace(error) ? $"storage probe exit {process.ExitCode}" : error.Trim());
            }

            return (ParseDiskInfo(output), "");
        }
        catch (Exception ex)
        {
            return ([], ex.Message);
        }
    }

    private static List<DiskInfo> ParseDiskInfo(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        using var document = JsonDocument.Parse(json);
        var elements = document.RootElement.ValueKind == JsonValueKind.Array
            ? document.RootElement.EnumerateArray().ToArray()
            : [document.RootElement];
        var disks = new Dictionary<string, DiskInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (var element in elements)
        {
            var diskNumber = ReadString(element, "DiskNumber");
            var driveLetter = ReadString(element, "DriveLetter");
            if (string.IsNullOrWhiteSpace(diskNumber) || string.IsNullOrWhiteSpace(driveLetter))
            {
                continue;
            }

            if (!disks.TryGetValue(diskNumber, out var disk))
            {
                disk = new DiskInfo(
                    diskNumber,
                    ReadString(element, "Model"),
                    ReadString(element, "SerialNumber"),
                    ReadString(element, "Health"),
                    ReadString(element, "OperationalStatus"),
                    ReadString(element, "BusType"),
                    ReadString(element, "MediaType"),
                    ReadLong(element, "SizeBytes"),
                    []);
                disks[diskNumber] = disk;
            }

            if (!disk.Volumes.Contains(driveLetter, StringComparer.OrdinalIgnoreCase))
            {
                disk.Volumes.Add(driveLetter);
            }
        }

        return disks.Values.ToList();
    }

    private static string ReadString(JsonElement element, string name)
    {
        return element.TryGetProperty(name, out var property) && property.ValueKind != JsonValueKind.Null
            ? property.ToString()
            : "";
    }

    private static long ReadLong(JsonElement element, string name)
    {
        return element.TryGetProperty(name, out var property) && property.TryGetInt64(out var value)
            ? value
            : 0L;
    }

    private static int ParseDiskNumber(string text)
    {
        return int.TryParse(text, out var value) ? value : int.MaxValue;
    }

    private static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        var value = Math.Max(0.0, bytes);
        var unit = 0;
        while (value >= 1024.0 && unit < units.Length - 1)
        {
            value /= 1024.0;
            unit++;
        }

        return unit < 3
            ? $"{value:0}{units[unit]}"
            : $"{value:0.0}{units[unit]}";
    }

    private sealed record DiskInfo(
        string DiskNumber,
        string Model,
        string SerialNumber,
        string Health,
        string OperationalStatus,
        string BusType,
        string MediaType,
        long SizeBytes,
        List<string> Volumes);
}
